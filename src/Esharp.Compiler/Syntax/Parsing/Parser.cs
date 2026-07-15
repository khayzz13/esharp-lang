using Esharp.Diagnostics;
using Esharp.Syntax.Lexing;
using Esharp.Syntax;

namespace Esharp.Syntax.Parsing;

/// The parser facade and composition root. It lexes the source, owns the single
/// `TokenCursor`, and wires together the domain parsers (declarations,
/// statements, expressions, types, match, concurrency) that recurse into each
/// other through it. The public surface — `ParseCompilationUnit`,
/// `ParseStandaloneExpression`, `Diagnostics` — is all any caller needs.
public sealed class Parser
{
    readonly DiagnosticBag _diagnostics = new();
    readonly string _source;

    // Recursive-descent stack guard. Pathologically nested input (`[[[[…`,
    // `((((…`, `if { if { …`) recurses once per level and would overflow the
    // native thread stack — an uncatchable process abort, no diagnostic. A
    // malformed program must always degrade to a diagnostic, so each recursion
    // entry point (expression, statement-block, type) probes the remaining stack
    // and throws a recoverable `ParserDepthExceeded` before it runs out.
    //
    // The probe is `RuntimeHelpers.TryEnsureSufficientExecutionStack`, the same
    // stack-size-aware primitive Roslyn's StackGuard uses: it returns false while
    // a safety reserve remains, so the throw + unwind + diagnostic all run with
    // stack to spare. This is robust across hosts with different stack sizes (the
    // CLI, the LSP, the xunit test thread) where a fixed depth count is not — a
    // count tuned for one stack size aborts on a smaller one and rejects valid
    // input on a larger one. A coarse depth ceiling rides alongside it as a
    // backstop for the (theoretical) platform where the intrinsic is a no-op; it
    // sits far above any hand-written nesting.
    const int MaxNestingDepth = 10_000;
    int _nestingDepth;

    /// Enter one level of grammar recursion. Throws `ParserDepthExceeded` (caught
    /// at the top-level parse entries, turned into a diagnostic) when the remaining
    /// execution stack runs low or the coarse backstop trips. Pair every call with
    /// `ExitRecursion` in a `finally`.
    internal void EnterRecursion()
    {
        if (++_nestingDepth > MaxNestingDepth
            || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new ParserDepthExceeded(Cursor.Current.Line, Cursor.Current.Column);
    }

    internal void ExitRecursion() => _nestingDepth--;

    internal TokenCursor Cursor { get; }
    internal DeclarationParser Declarations { get; }
    internal StatementParser Statements { get; }
    internal ExpressionParser Expressions { get; }
    internal TypeParser Types { get; }
    internal MatchParser Match { get; }
    internal ConcurrencyParser Concurrency { get; }
    // Set only while parsing the direct body of a class constructor.  It makes
    // `[pub|priv] self.name: T = value` unambiguously an init-owned field.
    internal bool InClassInitBody { get; set; }
    // Contextual `yield` inside a property `mut { ... }` is a lend point rather
    // than an async-stream element.
    internal bool InScopedMutAccessorBody { get; set; }

    public Parser(string source, string filePath = "input.es")
    {
        _source = source;
        var lexerDiagnostics = new DiagnosticBag();
        var lexer = new Lexer(source, filePath, lexerDiagnostics);
        var tokens = lexer.Lex();
        _diagnostics.AddRange(lexerDiagnostics.Diagnostics);

        Cursor = new TokenCursor(tokens, filePath, _diagnostics);
        Declarations = new DeclarationParser(this);
        Statements = new StatementParser(this);
        Expressions = new ExpressionParser(this);
        Types = new TypeParser(this);
        Match = new MatchParser(this);
        Concurrency = new ConcurrencyParser(this);
    }

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics.Diagnostics;

    /// The verbatim source — retained so the printer can slice unmodified node spans
    /// for byte-exact output.
    public string Source => _source;

    /// The full lexed token stream, trivia attached.
    public IReadOnlyList<SyntaxToken> Tokens => Cursor.Tokens;

    /// Parse and return a lossless tree bundle: the root, the source, the token
    /// stream (trivia attached), and diagnostics — everything the printer, navigator,
    /// and a future LSP need from one parse.
    public ParsedSyntaxTree Parse()
    {
        var root = ParseCompilationUnit();
        return new ParsedSyntaxTree(root, _source, Cursor.Tokens, _diagnostics.Diagnostics);
    }

    /// Parse a single standalone expression (the whole source is one expression).
    /// Used to bind string-interpolation holes (`{a + b}`, `{n.value}`) as real
    /// expressions through the normal parser, rather than ad-hoc string splitting.
    public ExpressionSyntax ParseStandaloneExpression()
    {
        Cursor.SkipSeparators();
        try
        {
            return Expressions.ParseExpression();
        }
        catch (ParserDepthExceeded ex)
        {
            Cursor.Report(ex.Line, ex.Column, ex.Message);
            return new LiteralExpressionSyntax(0, "0");
        }
    }

    public CompilationUnitSyntax ParseCompilationUnit()
    {
        try
        {
            return ParseCompilationUnitCore();
        }
        catch (ParserDepthExceeded ex)
        {
            // Pathological nesting blew the recursion guard mid-parse. Record the
            // diagnostic and return whatever top-level shape we have so the caller
            // sees an error, not a process abort.
            Cursor.Report(ex.Line, ex.Column, ex.Message);
            return new CompilationUnitSyntax(null, [], []);
        }
    }

    CompilationUnitSyntax ParseCompilationUnitCore()
    {
        string? namespaceName = null;
        Cursor.SkipSeparators();

        if (Cursor.Current.Kind == SyntaxTokenKind.NamespaceKeyword)
        {
            Cursor.Next();
            // Dotted namespaces: `namespace A.B.C` maps to the CLR namespace
            // "A.B.C" (matching C#), so E# types can live in a multi-segment
            // namespace alongside C# files in the same one.
            var sb = new System.Text.StringBuilder(
                Cursor.Match(SyntaxTokenKind.Identifier, "Expected namespace name.").Text);
            while (Cursor.Current.Kind == SyntaxTokenKind.Dot)
            {
                Cursor.Next();
                sb.Append('.').Append(
                    Cursor.Match(SyntaxTokenKind.Identifier, "Expected identifier after '.' in namespace name.").Text);
            }
            namespaceName = sb.ToString();
            Cursor.SkipSeparators();
        }

        var imports = new List<UsingSyntax>();
        while (Cursor.Current.Kind == SyntaxTokenKind.UsingKeyword)
        {
            Cursor.Next();
            var isStatic = Cursor.Current.Kind == SyntaxTokenKind.Identifier && Cursor.Current.Text == "static";
            if (isStatic)
                Cursor.Next();
            // Alias form: `using Baz = "Full.Type"` (C#-style type alias). A bare
            // identifier here (other than `static`) is the alias name.
            string? alias = null;
            if (!isStatic && Cursor.Current.Kind == SyntaxTokenKind.Identifier)
            {
                alias = Cursor.Current.Text;
                Cursor.Next();
                Cursor.Match(SyntaxTokenKind.Equals, "Expected '=' after alias name in 'using'.");
            }
            var ns = Cursor.Match(SyntaxTokenKind.StringLiteral, "Expected string literal after 'using'.").Text;
            // Strip quotes
            if (ns.Length >= 2 && ns[0] == '"' && ns[^1] == '"')
                ns = ns[1..^1];
            imports.Add(new UsingSyntax(isStatic, ns, alias));
            Cursor.SkipSeparators();
        }

        var members = new List<MemberSyntax>();
        while (Cursor.Current.Kind != SyntaxTokenKind.EndOfFile)
        {
            Cursor.SkipSeparators();
            if (Cursor.Current.Kind == SyntaxTokenKind.EndOfFile)
                break;

            var member = Declarations.ParseMember();
            members.Add(member);
            // Surface inline methods from data declarations as top-level function members
            if (member is DataDeclarationSyntax { Methods.Count: > 0 } dataMember)
                foreach (var m in dataMember.Methods)
                    members.Add(m);

            Cursor.SkipSeparators();
        }

        // No parse-time desugaring: the async-stream / async-foreach lowering passes
        // move to Pillar 3 Lowering/ (post-bind, operating on the BoundTree). The syntax
        // tree is full-fidelity so the formatter and LSP see the original source structure.
        return new CompilationUnitSyntax(namespaceName, imports, members);
    }
}

/// Thrown by the recursion guard when grammar nesting passes `Parser.MaxNestingDepth`.
/// Caught at the top-level parse entries and turned into a diagnostic, so a
/// stack-bomb input degrades to an error rather than a native stack overflow.
sealed class ParserDepthExceeded(int line, int column)
    : Exception("Expression is nested too deeply; simplify the structure (the parser limits nesting depth to guard against stack overflow).")
{
    public int Line { get; } = line;
    public int Column { get; } = column;
}
