using System.Text;
using Esharp.Syntax;

namespace Esharp.Syntax.Parsing;

/// Expression-level grammar: precedence climbing, the postfix/speculative
/// continuation loop, primaries, literals, function literals, and the
/// argument/initializer lists. This is also the sole owner of the
/// object-creation-lookahead suppression flag — control-flow heads enable it
/// here and bracketed contexts re-enable creation, so no other parser can see or
/// touch that state.
sealed class ExpressionParser : ParserUnit
{
    public ExpressionParser(Parser parser) : base(parser) { }

    // Set while parsing the condition / collection expression of a control-flow
    // statement (`if EXPR { ... }`, `while EXPR { ... }`, `for x in EXPR { ... }`).
    // Disables the speculative `Name { ... }` → object-creation rule below, so
    // `if Foo { ... }` and `for i in 0..COUNT { ... }` read the brace as the body's
    // `{` rather than an initializer's. Private to this parser.
    bool _suppressObjectCreationLookahead;

    public ExpressionSyntax ParseExpression(int parentPrecedence = 0)
    {
        P.EnterRecursion();
        try { return ParseExpressionCore(parentPrecedence); }
        finally { P.ExitRecursion(); }
    }

    ExpressionSyntax ParseExpressionCore(int parentPrecedence)
    {
        var start = Current;
        // unary/binary/coalesce/ternary nodes span from the head token (operator for a
        // prefix unary, left operand otherwise) to the last consumed token.
        ExpressionSyntax Ranged(ExpressionSyntax e) => e with { Span = SpanFrom(start) };

        ExpressionSyntax left;
        var unaryPrecedence = SyntaxFacts.GetUnaryPrecedence(Current.Kind);
        if (unaryPrecedence != 0 && unaryPrecedence >= parentPrecedence)
        {
            var operatorKind = NextToken().Kind;
            var operand = ParseExpression(unaryPrecedence);
            left = Ranged(new UnaryExpressionSyntax(operatorKind, operand));
        }
        else
        {
            left = ParsePostfixExpression();
        }

        while (true)
        {
            if (TryParseTypeOperator(ref left, parentPrecedence, Ranged)) continue;

            var precedence = SyntaxFacts.GetBinaryPrecedence(Current.Kind);
            if (precedence == 0 || precedence <= parentPrecedence)
                break;

            var operatorKind = NextToken().Kind;
            var right = ParseExpression(precedence);
            left = Ranged(new BinaryExpressionSyntax(left, operatorKind, right));
        }

        // Null-coalescing: left ?? right (only at top-level)
        if (parentPrecedence == 0 && Current.Kind == SyntaxTokenKind.QuestionQuestion)
        {
            NextToken(); // consume ??
            var right = ParseExpression();
            left = Ranged(new NullCoalescingExpressionSyntax(left, right));
        }

        // Ternary: cond ? consequence : alternative (only at top-level)
        if (parentPrecedence == 0 && Current.Kind == SyntaxTokenKind.Question)
        {
            NextToken(); // consume ?
            var consequence = ParseExpression();
            Match(SyntaxTokenKind.Colon, "Expected ':' in ternary expression.");
            var alternative = ParseExpression();
            left = Ranged(new ConditionalExpressionSyntax(left, consequence, alternative));
        }

        return left;
    }

    // `operand is [not] T`, `operand as T`, `operand as! T` — the type-narrowing
    // operators (§type-narrowing-and-downcasting). They sit at the relational tier
    // (precedence 4): tighter than `&&`/`||`/`==` so `s is T && s.m` reads as
    // `(s is T) && s.m`, looser than arithmetic. Left-associative (chained `as`).
    // The target type is parsed with `?` as a terminator, so a trailing `?` is never
    // a nullable suffix — `x is T ? a : b` is `(x is T) ? a : b` (ternary), and an
    // `is`/`as` target is never `T?` (a nullable target is meaningless: `as T` already
    // yields `T?`). Returns true and rewrites `left` when an operator was consumed.
    const int TypeOperatorPrecedence = 4;

    bool TryParseTypeOperator(ref ExpressionSyntax left, int parentPrecedence, Func<ExpressionSyntax, ExpressionSyntax> ranged)
    {
        if (Current.Kind is not (SyntaxTokenKind.IsKeyword or SyntaxTokenKind.AsKeyword))
            return false;
        if (TypeOperatorPrecedence <= parentPrecedence)
            return false;

        if (NextToken().Kind == SyntaxTokenKind.IsKeyword)
        {
            var negated = Current.Kind == SyntaxTokenKind.NotKeyword;
            if (negated) NextToken();
            var type = Types.ParseTypeUntil(SyntaxTokenKind.Question);
            left = ranged(new TypeTestExpressionSyntax(left, type, negated));
        }
        else
        {
            var asserting = Current.Kind == SyntaxTokenKind.Bang;
            if (asserting) NextToken();
            var type = Types.ParseTypeUntil(SyntaxTokenKind.Question);
            left = ranged(new CastExpressionSyntax(left, type, asserting));
        }
        return true;
    }

    // Continue binary expression parsing from an already-parsed left side
    public ExpressionSyntax ParseExpressionContinuation(ExpressionSyntax left, int parentPrecedence)
    {
        // Anchor on the already-parsed left's range; extend to each new right operand.
        var startSpan = left.Span;
        ExpressionSyntax Ranged(ExpressionSyntax e) =>
            startSpan.IsValid ? e with { Span = SpanFrom(startSpan) } : e;

        while (true)
        {
            if (TryParseTypeOperator(ref left, parentPrecedence, Ranged)) continue;

            var precedence = SyntaxFacts.GetBinaryPrecedence(Current.Kind);
            if (precedence == 0 || precedence <= parentPrecedence)
                break;

            var operatorKind = NextToken().Kind;
            var right = ParseExpression(precedence);
            left = Ranged(new BinaryExpressionSyntax(left, operatorKind, right));
        }

        // Null-coalescing: left ?? right (only at top-level)
        if (parentPrecedence == 0 && Current.Kind == SyntaxTokenKind.QuestionQuestion)
        {
            NextToken(); // consume ??
            var right = ParseExpression();
            left = Ranged(new NullCoalescingExpressionSyntax(left, right));
        }

        // Ternary: cond ? consequence : alternative (only at top-level)
        if (parentPrecedence == 0 && Current.Kind == SyntaxTokenKind.Question)
        {
            NextToken(); // consume ?
            var consequence = ParseExpression();
            Match(SyntaxTokenKind.Colon, "Expected ':' in ternary expression.");
            var alternative = ParseExpression();
            left = Ranged(new ConditionalExpressionSyntax(left, consequence, alternative));
        }

        return left;
    }

    // Parse an expression in a control-flow head (if / while / match subject) where a
    // trailing `{` opens the statement body, not a composite literal. Suppresses the
    // object-creation lookahead so `if c == Color.Red {` and `match x.Status {` read
    // the brace as a block. Object creation is re-enabled inside any `(...)` / `[...]`
    // (see ParseArgumentList / ParseParenthesizedExpression / ParseListLiteral), so
    // `if ready(Point { x: 1 }) {` still constructs the argument.
    public ExpressionSyntax ParseControlFlowCondition()
    {
        var prev = _suppressObjectCreationLookahead;
        _suppressObjectCreationLookahead = true;
        try { return ParseExpression(); }
        finally { _suppressObjectCreationLookahead = prev; }
    }

    // Parse an `if` in EXPRESSION position — `if c { … } else if … { … } else { … }`.
    // Each branch is a braced block whose value is its trailing expression (the binder
    // extracts it). Mirrors ParseIfStatement's control-flow-condition + block shape; the
    // else is required for a consumed value but its absence is a bind-time error, not a
    // parse error, so the grammar accepts the else-less form and the binder reports it.
    public IfExpressionSyntax ParseIfExpression()
    {
        var span = SpanHere();
        Match(SyntaxTokenKind.IfKeyword);
        var branches = new List<IfExpressionBranchSyntax> { ParseIfExpressionBranch() };

        BlockStatementSyntax? elseBody = null;
        SkipSeparators();
        while (Current.Kind == SyntaxTokenKind.ElseKeyword)
        {
            NextToken(); // consume 'else'
            SkipSeparators();
            if (Current.Kind == SyntaxTokenKind.IfKeyword)
            {
                NextToken(); // 'else if'
                branches.Add(ParseIfExpressionBranch());
                SkipSeparators();
            }
            else
            {
                elseBody = Statements.ParseBlockStatement();
                break;
            }
        }
        return new IfExpressionSyntax(branches, elseBody) { Span = SpanFrom(span) };
    }

    IfExpressionBranchSyntax ParseIfExpressionBranch()
    {
        var condition = ParseControlFlowCondition();
        SkipSeparators();
        var body = Statements.ParseBlockStatement();
        return new IfExpressionBranchSyntax(condition, body);
    }

    // Run a nested parse with object creation re-enabled — used inside bracketed
    // contexts (`(...)`, `[...]`) where a `{` after a type name is unambiguously a
    // composite literal because the closing bracket delimits the expression.
    // Bracketed contexts also re-enable the newline-dot chain continuation: inside
    // a delimiter the closing token disambiguates, so a cross-line fluent chain is
    // legal even where the enclosing position (a match arm body) suppresses it.
    T WithObjectCreationAllowed<T>(Func<T> parse)
    {
        var prevCreate = _suppressObjectCreationLookahead;
        var prevChain = _suppressNewlineDotChain;
        _suppressObjectCreationLookahead = false;
        _suppressNewlineDotChain = false;
        try { return parse(); }
        finally
        {
            _suppressObjectCreationLookahead = prevCreate;
            _suppressNewlineDotChain = prevChain;
        }
    }

    /// Parse a match arm's `=> expr` body. A dot-case arm also begins with `.`, so
    /// inside an arm body a newline-then-`.` means NEXT ARM, never a fluent-chain
    /// continuation — otherwise `.a(x) => x + 1` followed by `.b(y) => …` swallows
    /// `.b(y)` as a member chain on `x + 1`. Cross-line chains inside an arm body
    /// stay available through any bracketed context (parens re-enable them).
    public ExpressionSyntax ParseMatchArmExpressionBody()
    {
        var prev = _suppressNewlineDotChain;
        _suppressNewlineDotChain = true;
        try { return ParseExpression(); }
        finally { _suppressNewlineDotChain = prev; }
    }

    bool _suppressNewlineDotChain;

    // Collection expression after `in`. Special-cases the standalone integer
    // range form `start..end` so `for i in 0..n` parses without bringing `..`
    // into general expression precedence (where it would conflict with
    // relational/equality binding).
    public ExpressionSyntax ParseForInCollection()
    {
        var prev = _suppressObjectCreationLookahead;
        _suppressObjectCreationLookahead = true;
        try
        {
            var first = ParseExpression();
            if (Current.Kind == SyntaxTokenKind.DotDot)
            {
                NextToken();
                var end = ParseExpression();
                return new RangeExpressionSyntax(Target: null, Start: first, End: end);
            }
            return first;
        }
        finally
        {
            _suppressObjectCreationLookahead = prev;
        }
    }

    public ExpressionSyntax ParsePostfixExpression(bool allowTryUnwrap = true)
    {
        var start = Current;
        // Each postfix node (`a.b`, `f(x)`, `a[i]`, …) gets a range from the chain's
        // head token to its own last consumed token — the member name lives in the
        // node as a bare string, so a later union pass couldn't recover it; stamp now.
        ExpressionSyntax Ranged(ExpressionSyntax e) => e with { Span = SpanFrom(start) };

        var expression = ParsePrimaryExpression();

        while (true)
        {
            // Leading-dot continuation: a fluent chain may break across lines, with each
            // `.member(...)` on its own line —
            //     Turtle()
            //         .apply(a)
            //         .apply(b)
            // A newline normally ends the expression, but if the next significant token is
            // `.`, the chain continues. No statement legally begins with `.member`, so a
            // newline-then-`.` is unambiguously a continuation, not a new statement.
            if (Current.Kind == SyntaxTokenKind.NewLine)
            {
                // In a match arm's expression body a newline-then-`.` is the NEXT
                // ARM (dot-case patterns begin with `.`), not a chain continuation.
                if (_suppressNewlineDotChain) break;
                var k = 1;
                while (Peek(k).Kind == SyntaxTokenKind.NewLine) k++;
                if (Peek(k).Kind != SyntaxTokenKind.Dot) break;
                while (Current.Kind == SyntaxTokenKind.NewLine) NextToken();
            }

            if (Current.Kind == SyntaxTokenKind.OpenParen)
            {
                var (arguments, argNames) = ParseArguments();
                expression = Ranged(new CallExpressionSyntax(expression, arguments, ArgumentNames: argNames));
                continue;
            }

            if (Current.Kind == SyntaxTokenKind.Dot)
            {
                NextToken();
                var memberTok = Match(SyntaxTokenKind.Identifier, "Expected member name after '.'.");
                expression = Ranged(new MemberAccessExpressionSyntax(expression, memberTok.Text) { NameSpan = SpanOf(memberTok) });

                // Speculative: foo.Method<T1, T2>(args) — explicit generic method type args.
                // Only commit if `<...>` is followed by `(`; otherwise treat `<` as comparison.
                if (Current.Kind == SyntaxTokenKind.Less)
                {
                    var (typeArgs, consumed) = TryParseMethodTypeArgs();
                    if (typeArgs is not null && Current.Kind == SyntaxTokenKind.OpenParen)
                    {
                        var (arguments, argNames) = ParseArguments();
                        expression = Ranged(new CallExpressionSyntax(expression, arguments, typeArgs, argNames));
                        continue;
                    }
                    if (!consumed) { /* no-op */ }
                }
                continue;
            }

            // Speculative: bareName<T1, T2>(args) — explicit generic args on a bare identifier call.
            if (expression is NameExpressionSyntax bareFunc &&
                bareFunc.Name.Length > 0 && !char.IsUpper(bareFunc.Name[0]) &&
                Current.Kind == SyntaxTokenKind.Less)
            {
                var (typeArgs, _) = TryParseMethodTypeArgs();
                if (typeArgs is not null && Current.Kind == SyntaxTokenKind.OpenParen)
                {
                    var (arguments, argNames) = ParseArguments();
                    expression = Ranged(new CallExpressionSyntax(expression, arguments, typeArgs, argNames));
                    continue;
                }
            }

            // Array creation `T[](n)`: an empty `[]` (never a valid index) right after a
            // type name, followed by `(size)`. The element type is read off the name
            // expression parsed so far (`int`, `P`, `NS.T`); jagged `T[][]` nests.
            if (Current.Kind == SyntaxTokenKind.OpenBracket && Peek(1).Kind == SyntaxTokenKind.CloseBracket
                && ExpressionAsTypeName(expression) is { } elemName)
            {
                TypeSyntax elemType = new NamedTypeSyntax(elemName);
                while (Current.Kind == SyntaxTokenKind.OpenBracket && Peek(1).Kind == SyntaxTokenKind.CloseBracket)
                {
                    NextToken(); // [
                    NextToken(); // ]
                    if (Current.Kind == SyntaxTokenKind.OpenBracket && Peek(1).Kind == SyntaxTokenKind.CloseBracket)
                        elemType = new ArrayTypeSyntax(elemType);
                }
                Match(SyntaxTokenKind.OpenParen, "Expected '(size)' in array creation 'T[](n)'.");
                var size = ParseExpression();
                Match(SyntaxTokenKind.CloseParen, "Expected ')' after the array size.");
                expression = Ranged(new ArrayCreationExpressionSyntax(elemType, size));
                continue;
            }

            if (Current.Kind == SyntaxTokenKind.OpenBracket)
            {
                expression = Ranged(ParseIndexOrRange(expression));
                continue;
            }

            if (Current.Kind == SyntaxTokenKind.QuestionDot)
            {
                NextToken(); // consume ?.
                var memberTok = Match(SyntaxTokenKind.Identifier, "Expected member name after '?.'.");
                expression = Ranged(new NullConditionalAccessExpressionSyntax(expression, memberTok.Text) { NameSpan = SpanOf(memberTok) });
                continue;
            }

            if (Current.Kind == SyntaxTokenKind.Question)
            {
                // An `await` operand parses without consuming a trailing try-unwrap, so
                // `await E?` is `(await E)?` — the outer postfix loop binds the `?` to the
                // whole await, not to its un-awaited operand.
                if (!allowTryUnwrap) break;
                // Peek ahead: if next token could start an expression, this is a ternary
                // operator, not try-unwrap. Break to let ParseExpression handle it.
                var next = Peek(1).Kind;
                if (next is SyntaxTokenKind.Identifier or SyntaxTokenKind.NumberLiteral or SyntaxTokenKind.StringLiteral
                    or SyntaxTokenKind.TrueKeyword or SyntaxTokenKind.FalseKeyword or SyntaxTokenKind.NilKeyword
                    or SyntaxTokenKind.OpenParen or SyntaxTokenKind.OpenBracket or SyntaxTokenKind.Minus
                    or SyntaxTokenKind.Bang or SyntaxTokenKind.NotKeyword or SyntaxTokenKind.FuncKeyword
                    or SyntaxTokenKind.AwaitKeyword)
                    break;
                NextToken();
                expression = Ranged(new TryUnwrapExpressionSyntax(expression));
                continue;
            }

            // with expression: expr with { field: value, ... }
            if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "with" && Peek(1).Kind == SyntaxTokenKind.OpenBrace)
            {
                NextToken(); // consume 'with'
                var fields = ParseObjectInitializerFields();
                expression = Ranged(new WithExpressionSyntax(expression, fields));
                continue;
            }

            // Object creation: TypeName { ... } or TypeName<Args> { ... }
            // Suppressed when the parser is reading a control-flow head — the
            // brace there belongs to the statement body, not an initializer.
            if (expression is NameExpressionSyntax nameExpression &&
                nameExpression.Name.Length > 0 &&
                char.IsUpper(nameExpression.Name[0]) &&
                !_suppressObjectCreationLookahead)
            {
                if (Current.Kind == SyntaxTokenKind.OpenBrace)
                {
                    expression = Ranged(ParseObjectInitializer(new NamedTypeSyntax(nameExpression.Name)));
                    continue;
                }

                // Speculative: TypeName<A, B> { ... } → generic object creation
                if (Current.Kind == SyntaxTokenKind.Less)
                {
                    var saved = Cursor.Checkpoint();
                    NextToken(); // consume <
                    var depth = 1;
                    while (depth > 0 && Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.NewLine)
                    {
                        if (Current.Kind == SyntaxTokenKind.Less) depth++;
                        else if (GreaterWidth(Current.Kind) is var width && width > 0)
                        {
                            depth -= width;
                            if (depth <= 0) break;
                        }
                        NextToken();
                    }

                    if (depth <= 0 && GreaterWidth(Current.Kind) > 0)
                    {
                        NextToken(); // consume >
                        // The angle group as source text — the `(` / `.` cases still target
                        // a string-named expression (a separate, deferred stringly concern).
                        var typeName = nameExpression.Name + Cursor.TextBetween(saved.Position, Cursor.Position);

                        if (Current.Kind == SyntaxTokenKind.OpenBrace)
                        {
                            // Generic construction: re-parse the confirmed angle group as
                            // structured type arguments so the bound type carries them.
                            Cursor.Restore(saved);
                            var typeArgs = Types.ParseTypeArguments();
                            expression = Ranged(ParseObjectInitializer(new GenericTypeSyntax(nameExpression.Name, typeArgs)));
                            continue;
                        }

                        if (Current.Kind == SyntaxTokenKind.OpenParen)
                        {
                            // Generic constructor call: TypeName<A, B>(args)
                            expression = Ranged(new NameExpressionSyntax(typeName));
                            var (arguments, argNames) = ParseArguments();
                            expression = Ranged(new CallExpressionSyntax(expression, arguments, ArgumentNames: argNames));
                            continue;
                        }

                        if (Current.Kind == SyntaxTokenKind.Dot)
                        {
                            // Generic factory / static-member call:
                            // `TypeName<A, B>.member(args)` (e.g. `Option<int>.some(99)`).
                            NextToken(); // consume .
                            var memberTok = Match(SyntaxTokenKind.Identifier, "Expected member name after '.'.");
                            expression = Ranged(new MemberAccessExpressionSyntax(new NameExpressionSyntax(typeName), memberTok.Text) { NameSpan = SpanOf(memberTok) });
                            continue;
                        }
                    }

                    Cursor.Restore(saved); // revert if not object creation
                }
            }

            // Qualified-type object creation: `NS.Type { ... }`. The receiver is a
            // member-access chain whose final segment is an uppercase type name; a
            // brace immediately after can only be a composite literal. Flatten the
            // dotted chain into the type name so the binder resolves the qualified
            // type (cross-namespace construction by qualifier, mirroring C#).
            if (expression is MemberAccessExpressionSyntax qualifiedType
                && qualifiedType.MemberName.Length > 0
                && char.IsUpper(qualifiedType.MemberName[0])
                && Current.Kind == SyntaxTokenKind.OpenBrace
                && !_suppressObjectCreationLookahead
                && TryFlattenQualifiedName(qualifiedType) is { } dottedName
                && char.IsUpper(dottedName[0])) // root must be a namespace/type, not a value (`r.Error`)
            {
                expression = Ranged(ParseObjectInitializer(new NamedTypeSyntax(dottedName)));
                continue;
            }

            break;
        }

        return expression;
    }

    /// Flatten a pure `Name.Member.Member` access chain into a dotted string
    /// (`Geometry.Point`). Returns null if any link is not a bare name/member —
    /// i.e. the chain is rooted in a real value expression, not a type qualifier.
    static string? TryFlattenQualifiedName(ExpressionSyntax expr) => expr switch
    {
        NameExpressionSyntax n => n.Name,
        MemberAccessExpressionSyntax m when TryFlattenQualifiedName(m.Target) is { } prefix
            => $"{prefix}.{m.MemberName}",
        _ => null,
    };

    // Speculative parse for explicit generic method type args: `<T1, T2>` immediately before `(`.
    // Returns (args, consumed). A balanced `<...>` group (no `(`/`{` — those mark a non-type
    // context, treated as a `<` comparison) followed by `(` is confirmed, then handed to the
    // real type-argument grammar. Otherwise the cursor is restored and (null, false) returned.
    (IReadOnlyList<TypeSyntax>? args, bool consumed) TryParseMethodTypeArgs()
    {
        if (Current.Kind != SyntaxTokenKind.Less) return (null, false);
        var saved = Cursor.Checkpoint();
        NextToken(); // consume <

        var depth = 1;
        while (depth > 0 && !Cursor.AtEnd)
        {
            var kind = Current.Kind;
            if (kind == SyntaxTokenKind.Less) depth++;
            else if (GreaterWidth(kind) is var width && width > 0)
            {
                depth -= width;
                if (depth <= 0) break;
            }
            else if (kind is SyntaxTokenKind.EndOfFile or SyntaxTokenKind.NewLine or SyntaxTokenKind.OpenBrace or SyntaxTokenKind.CloseParen or SyntaxTokenKind.OpenParen)
            {
                Cursor.Restore(saved);
                return (null, false);
            }
            else if (!CanAppearInMethodTypeArgumentList(kind))
            {
                Cursor.Restore(saved);
                return (null, false);
            }
            NextToken();
        }

        if (GreaterWidth(Current.Kind) == 0)
        {
            Cursor.Restore(saved);
            return (null, false);
        }
        NextToken(); // consume >

        // Commit only if followed by '(' — otherwise back out and treat `<` as comparison.
        if (Current.Kind != SyntaxTokenKind.OpenParen)
        {
            Cursor.Restore(saved);
            return (null, false);
        }

        // Confirmed: re-parse the angle group through the real grammar (leaves the
        // cursor at `(`, the same place the scan above ended).
        Cursor.Restore(saved);
        var args = Types.ParseTypeArguments();
        return (args, true);
    }

    static int GreaterWidth(SyntaxTokenKind kind) => kind switch
    {
        SyntaxTokenKind.Greater => 1,
        SyntaxTokenKind.ShiftRight => 2,
        SyntaxTokenKind.UnsignedShiftRight => 3,
        _ => 0,
    };

    static bool CanAppearInMethodTypeArgumentList(SyntaxTokenKind kind) => kind is
        SyntaxTokenKind.Identifier or
        SyntaxTokenKind.Dot or
        SyntaxTokenKind.Comma or
        SyntaxTokenKind.Star or
        SyntaxTokenKind.Ampersand or
        SyntaxTokenKind.Question or
        SyntaxTokenKind.VarKeyword or
        SyntaxTokenKind.ChanKeyword;

    IReadOnlyList<ExpressionSyntax> ParseArgumentList() => ParseArguments().Args;

    /// Parse a `( … )` argument list. An argument may carry a name label —
    /// `f(x, port: 9090)` — recognized by `identifier ':'` lookahead (no legal
    /// expression begins that way in argument position). Names ride alongside the
    /// expressions; only `BindCall` consumes them.
    (IReadOnlyList<ExpressionSyntax> Args, IReadOnlyList<string?>? Names) ParseArguments()
    {
        Match(SyntaxTokenKind.OpenParen, "Expected '(' to start argument list.");
        var arguments = new List<ExpressionSyntax>();
        List<string?>? names = null;
        SkipSeparators();

        while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            string? argName = null;
            if (Current.Kind == SyntaxTokenKind.Identifier && Peek(1).Kind == SyntaxTokenKind.Colon)
            {
                argName = NextToken().Text;
                NextToken(); // consume ':'
                SkipNewlines();
            }
            arguments.Add(WithObjectCreationAllowed(() => ParseExpression()));
            if (argName is not null)
            {
                names ??= Enumerable.Repeat<string?>(null, arguments.Count - 1).ToList();
                names.Add(argName);
            }
            else
            {
                names?.Add(null);
            }
            SkipSeparators();
            if (Current.Kind == SyntaxTokenKind.Comma)
            {
                NextToken();
                SkipSeparators();
            }
            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseParen, "Expected ')' to close argument list.");
        return (arguments, names);
    }

    List<ObjectInitializerFieldSyntax> ParseObjectInitializerFields()
    {
        Match(SyntaxTokenKind.OpenBrace, "Expected '{' to start initializer.");
        SkipSeparators();

        var fields = new List<ObjectInitializerFieldSyntax>();
        while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            var fieldName = Match(SyntaxTokenKind.Identifier, "Expected field name.").Text;
            Match(SyntaxTokenKind.Colon, "Expected ':' after field name.");
            var value = ParseExpression();
            fields.Add(new ObjectInitializerFieldSyntax(fieldName, value));
            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close initializer.");
        return fields;
    }

    ObjectCreationExpressionSyntax ParseObjectInitializer(TypeSyntax type)
    {
        var fields = ParseObjectInitializerFields();
        return new ObjectCreationExpressionSyntax(type, fields);
    }

    // The dotted type name behind a parsed expression, for `T[](n)` array creation —
    // a bare name (`int`, `P`) or a name-only member chain (`NS.T`). Null for anything
    // else (a call, an index, a literal), so only a real type leads array creation.
    static string? ExpressionAsTypeName(ExpressionSyntax e) => e switch
    {
        NameExpressionSyntax n => n.Name,
        MemberAccessExpressionSyntax { Target: var t } m when ExpressionAsTypeName(t) is { } head => $"{head}.{m.MemberName}",
        _ => null,
    };

    // Every primary funnels through here so it carries an exact range start→end,
    // including its own delimiters (`(...)`, `[...]`, `default(...)`). A primary that
    // delegated to a sub-parser which already stamped a range keeps it.
    public ExpressionSyntax ParsePrimaryExpression()
    {
        var start = Current;
        var node = ParsePrimaryExpressionCore();
        return node.Span.IsValid ? node : node with { Span = SpanFrom(start) };
    }

    ExpressionSyntax ParsePrimaryExpressionCore()
    {
        // Contextual `new` — the sole fresh-heap-allocation keyword. Recognized only
        // immediately before an uppercase type name followed by `{`, `(`, or `<`
        // (generic construction); `new` stays a valid identifier everywhere else.
        // The construction target is parsed with object-creation lookahead forced
        // on so `new T { ... }` reads as a composite literal even inside a
        // control-flow head. Lowers to the same heap alloc as the legacy `&T{}`.
        if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "new"
            && Peek(1).Kind == SyntaxTokenKind.Identifier
            && Peek(1).Text.Length > 0 && char.IsUpper(Peek(1).Text[0])
            && Peek(2).Kind is SyntaxTokenKind.OpenBrace or SyntaxTokenKind.OpenParen or SyntaxTokenKind.Less)
        {
            NextToken(); // consume `new`
            var created = WithObjectCreationAllowed(() => ParsePostfixExpression());
            return new NewExpressionSyntax(created);
        }

        // Contextual `stackalloc` — a frame-local buffer. Recognized only immediately
        // before an element type name and `[` (the `T[](n)` construction form); an
        // ordinary identifier everywhere else. Yields a `Span<T>`, never a `*T`.
        if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "stackalloc"
            && Peek(1).Kind == SyntaxTokenKind.Identifier && Peek(2).Kind == SyntaxTokenKind.OpenBracket)
        {
            NextToken(); // consume `stackalloc`
            var alloc = ParsePostfixExpression();
            if (alloc is ArrayCreationExpressionSyntax arr)
                return new StackAllocExpressionSyntax(arr.ElementType, arr.Size);
            Report(Current.Line, Current.Column,
                "`stackalloc` must be followed by an array construction `T[](n)`.");
            return alloc;
        }

        switch (Current.Kind)
        {
            case SyntaxTokenKind.NumberLiteral:
            {
                var numberToken = NextToken();
                return new LiteralExpressionSyntax(new NumericLiteralValue(numberToken.Text), PreviousText());
            }
            case SyntaxTokenKind.StringLiteral:
            {
                var raw = NextToken().Text;
                var form = StringLiteralLowering.FormOf(raw);
                var template = StringLiteralLowering.DecodeTemplate(raw, form);
                return new LiteralExpressionSyntax(template, PreviousText())
                {
                    SuppressInterpolation = !StringLiteralLowering.Interpolates(form),
                };
            }
            case SyntaxTokenKind.ByteStringLiteral:
            {
                var raw = NextToken().Text;
                return new LiteralExpressionSyntax(DecodeByteString(raw), PreviousText())
                {
                    SuppressInterpolation = true,
                };
            }
            case SyntaxTokenKind.CharLiteral:
                return new LiteralExpressionSyntax(ParseChar(NextToken().Text), PreviousText());
            case SyntaxTokenKind.TrueKeyword:
                return new LiteralExpressionSyntax(true, NextToken().Text);
            case SyntaxTokenKind.FalseKeyword:
                return new LiteralExpressionSyntax(false, NextToken().Text);
            case SyntaxTokenKind.NilKeyword:
                return new LiteralExpressionSyntax(null, NextToken().Text);
            case SyntaxTokenKind.AwaitKeyword:
                NextToken();
                // Parse the operand without a trailing `?`: `await E?` must mean `(await E)?`,
                // so the outer postfix loop applies the try-unwrap to the await result.
                return new AwaitExpressionSyntax(ParsePostfixExpression(allowTryUnwrap: false));
            case SyntaxTokenKind.SpawnKeyword:
                return P.Concurrency.ParseSpawnExpression();
            case SyntaxTokenKind.ChanKeyword:
                return P.Concurrency.ParseChanCreationExpression();
            case SyntaxTokenKind.Dot:
                return ParseDotCaseExpression();
            case SyntaxTokenKind.FuncKeyword:
                return ParseFunctionLiteral();
            case SyntaxTokenKind.Ampersand:
                NextToken();
                var addrTarget = ParsePostfixExpression();
                return new AddressOfExpressionSyntax(addrTarget);
            case SyntaxTokenKind.MatchKeyword:
                return P.Match.ParseMatchExpression();
            case SyntaxTokenKind.IfKeyword:
                return ParseIfExpression();
            case SyntaxTokenKind.DefaultKeyword:
            {
                NextToken();
                // `default(T)` carries an explicit type; a bare `default` is target-typed
                // (its type comes from the expected type at bind time — e.g. a parameter
                // default `ct: CancellationToken = default`).
                if (Current.Kind != SyntaxTokenKind.OpenParen)
                    return new DefaultExpressionSyntax(null);
                NextToken();
                var defType = Types.ParseTypeUntil(SyntaxTokenKind.CloseParen);
                Match(SyntaxTokenKind.CloseParen, "Expected ')' after default type.");
                return new DefaultExpressionSyntax(defType);
            }
            case SyntaxTokenKind.OutKeyword:
            {
                NextToken();
                bool declaresLocal = false;
                if (Current.Kind == SyntaxTokenKind.VarKeyword)
                {
                    declaresLocal = true;
                    NextToken();
                }
                var outName = Match(SyntaxTokenKind.Identifier, "Expected identifier after 'out'.").Text;
                return new OutArgumentExpressionSyntax(outName, declaresLocal);
            }
            case SyntaxTokenKind.Identifier:
                return new NameExpressionSyntax(NextToken().Text);
            case SyntaxTokenKind.OpenParen:
                return TryParseArrowFunctionLiteral() ?? ParseParenthesizedExpression();
            case SyntaxTokenKind.OpenBracket:
                return ParseListLiteral();
            default:
                return ParseMissingExpression();
        }
    }

    ListLiteralExpressionSyntax ParseListLiteral()
    {
        Match(SyntaxTokenKind.OpenBracket);
        var elements = new List<ExpressionSyntax>();
        SkipSeparators();
        while (Current.Kind is not (SyntaxTokenKind.CloseBracket or SyntaxTokenKind.EndOfFile))
        {
            elements.Add(WithObjectCreationAllowed(() => ParseExpression()));
            SkipSeparators();
            if (Current.Kind == SyntaxTokenKind.Comma)
            {
                NextToken();
                SkipSeparators();
            }
        }
        Match(SyntaxTokenKind.CloseBracket, "Expected ']' to close list literal.");
        return new ListLiteralExpressionSyntax(elements);
    }

    DotCaseExpressionSyntax ParseDotCaseExpression()
    {
        Match(SyntaxTokenKind.Dot);
        var caseNameTok = Match(SyntaxTokenKind.Identifier, "Expected case name after '.'.");

        var arguments = new List<ExpressionSyntax>();
        if (Current.Kind == SyntaxTokenKind.OpenParen)
        {
            NextToken();
            SkipSeparators();
            while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
            {
                arguments.Add(ParseExpression());
                SkipSeparators();
                if (Current.Kind == SyntaxTokenKind.Comma) { NextToken(); SkipSeparators(); }
            }
            Match(SyntaxTokenKind.CloseParen, "Expected ')' after case arguments.");
        }

        return new DotCaseExpressionSyntax(caseNameTok.Text, arguments) { NameSpan = SpanOf(caseNameTok) };
    }

    public ExpressionSyntax ParseIndexOrRange(ExpressionSyntax target)
    {
        Match(SyntaxTokenKind.OpenBracket);

        // [..end] / [..^end] / [..]
        if (Current.Kind == SyntaxTokenKind.DotDot)
        {
            NextToken();
            var end = Current.Kind == SyntaxTokenKind.CloseBracket ? null : ParseRangeEndpoint();
            Match(SyntaxTokenKind.CloseBracket, "Expected ']'.");
            return new RangeExpressionSyntax(target, null, end);
        }

        // [^k] — an index-from-end that is a whole index, or the start of a `^k..` range.
        if (Current.Kind == SyntaxTokenKind.Caret)
        {
            var fromEndStart = ParseRangeEndpoint();
            if (Current.Kind == SyntaxTokenKind.DotDot)
            {
                NextToken();
                var end = Current.Kind == SyntaxTokenKind.CloseBracket ? null : ParseRangeEndpoint();
                Match(SyntaxTokenKind.CloseBracket, "Expected ']'.");
                return new RangeExpressionSyntax(target, fromEndStart, end);
            }
            Match(SyntaxTokenKind.CloseBracket, "Expected ']'.");
            return new IndexExpressionSyntax(target, fromEndStart);
        }

        var expr = ParseExpression();

        // [start..end] / [start..^end] / [start..]
        if (Current.Kind == SyntaxTokenKind.DotDot)
        {
            NextToken();
            var end = Current.Kind == SyntaxTokenKind.CloseBracket ? null : ParseRangeEndpoint();
            Match(SyntaxTokenKind.CloseBracket, "Expected ']'.");
            return new RangeExpressionSyntax(target, expr, end);
        }

        // [expr] — simple index
        Match(SyntaxTokenKind.CloseBracket, "Expected ']'.");
        return new IndexExpressionSyntax(target, expr);
    }

    // A range endpoint (or a whole from-end index): `expr`, or `^expr` for index-from-end.
    ExpressionSyntax ParseRangeEndpoint()
    {
        if (Current.Kind == SyntaxTokenKind.Caret)
        {
            NextToken();
            return new UnaryExpressionSyntax(SyntaxTokenKind.Caret, ParseExpression());
        }
        return ParseExpression();
    }

    string PreviousText() => Cursor.TokenAt(Cursor.Position - 1).Text;

    object ParseNumber(SyntaxToken token)
    {
        var rawText = token.Text;

        // Radix integer (0x / 0b): decode to a magnitude and pick the smallest of
        // int/long/ulong that holds it (the decimal widening order, by value).
        if (Lexing.NumericLiteralFacts.IsRadixInteger(rawText))
        {
            if (Lexing.NumericLiteralFacts.TryDecodeRadixInteger(rawText, out var mag))
            {
                return Lexing.NumericLiteralFacts.InferIntegerType(mag) switch
                {
                    "int" => (int)mag,
                    "long" => (long)mag,
                    _ => mag,
                };
            }
            Report(token.Line, token.Column,
                $"Integer literal '{rawText}' is out of range (exceeds the maximum 64-bit unsigned value {ulong.MaxValue}).");
            return 0;
        }

        // Strip digit-group separators (1_000_000 -> 1000000) before parsing.
        var text = rawText.Replace("_", "");
        // A fraction (`.`) or an exponent (`e`/`E`, e.g. 1e10, 2E8) makes it a double.
        var isFloat = text.Contains('.') || text.Contains('e') || text.Contains('E');
        if (isFloat)
        {
            // double.Parse never throws on magnitude (it saturates to ±Infinity),
            // so the only failure here is a malformed token the lexer should have
            // rejected; guard it anyway so the front end never crashes.
            if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                return d;
            Report(token.Line, token.Column, $"Malformed numeric literal '{rawText}'.");
            return 0.0;
        }
        // Integer literals widen to `long` when they exceed `int` range
        // (e.g. epoch milliseconds) so they don't overflow at parse time, then to
        // `ulong` for the top half of the 64-bit range.
        if (int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var i))
            return i;
        if (long.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var l))
            return l;
        if (ulong.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var u))
            return u;
        // Past ulong.MaxValue: reject with a diagnostic instead of letting
        // UInt64.Parse throw an uncatchable OverflowException out of the parser.
        Report(token.Line, token.Column,
            $"Integer literal '{rawText}' is out of range (exceeds the maximum 64-bit unsigned value {ulong.MaxValue}).");
        return 0;
    }

    /// Decode a `b"…"` byte-string lexeme to its bytes. A normal character contributes
    /// its UTF-8 encoding; a `\xNN` escape contributes the raw byte NN (byte-exact, the
    /// point of the form); the C-family escapes (`\n`, `\t`, `\0`, `\\`, `\"`, …) and
    /// `\uNNNN` (UTF-8 of the code point) behave as in a string literal.
    static byte[] DecodeByteString(string lexeme)
    {
        // Strip the `b"` prefix and the closing `"` (tolerate an unterminated lexeme).
        var inner = lexeme.Length >= 3 ? lexeme[2..^1] : "";
        var bytes = new List<byte>(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            var ch = inner[i];
            if (ch != '\\') { AppendUtf8(bytes, ch); continue; }
            if (++i >= inner.Length) break;
            switch (inner[i])
            {
                case 'n': bytes.Add((byte)'\n'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case '0': bytes.Add(0); break;
                case 'b': bytes.Add((byte)'\b'); break;
                case 'f': bytes.Add((byte)'\f'); break;
                case 'v': bytes.Add((byte)'\v'); break;
                case 'a': bytes.Add((byte)'\a'); break;
                case '\\': bytes.Add((byte)'\\'); break;
                case '"': bytes.Add((byte)'"'); break;
                case '\'': bytes.Add((byte)'\''); break;
                case 'x':
                {
                    // 1–2 hex digits → one raw byte.
                    var val = 0; var n = 0;
                    while (n < 2 && i + 1 < inner.Length && Uri.IsHexDigit(inner[i + 1]))
                    { val = val * 16 + Convert.ToInt32(inner[i + 1].ToString(), 16); i++; n++; }
                    if (n == 0) bytes.Add((byte)'x'); else bytes.Add((byte)val);
                    break;
                }
                case 'u' when i + 4 < inner.Length:
                    AppendUtf8(bytes, (char)Convert.ToInt32(inner.Substring(i + 1, 4), 16));
                    i += 4;
                    break;
                default: AppendUtf8(bytes, inner[i]); break;
            }
        }
        return bytes.ToArray();
    }

    static void AppendUtf8(List<byte> bytes, char ch) =>
        bytes.AddRange(System.Text.Encoding.UTF8.GetBytes([ch]));

    static char ParseChar(string text)
    {
        if (text.Length < 3) return '\0';
        var inner = text[1..^1];
        if (inner.Length == 0) return '\0';
        if (inner[0] != '\\') return inner[0];

        if (inner.Length < 2) return '\0';
        return inner[1] switch
        {
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            '0' => '\0',
            '\\' => '\\',
            '\'' => '\'',
            '"' => '"',
            'b' => '\b',
            'f' => '\f',
            'v' => '\v',
            'a' => '\a',
            'u' when inner.Length == 6 => (char)Convert.ToInt32(inner[2..6], 16),
            'x' when inner.Length >= 3 => (char)Convert.ToInt32(inner[2..Math.Min(6, inner.Length)], 16),
            _ => inner[1],
        };
    }

    FunctionLiteralExpressionSyntax? TryParseArrowFunctionLiteral()
    {
        var saved = Cursor.Checkpoint();
        if (Current.Kind != SyntaxTokenKind.OpenParen)
            return null;

        NextToken(); // consume (
        var parameters = new List<ParameterSyntax>();
        SkipSeparators();

        while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            if (Current.Kind != SyntaxTokenKind.Identifier)
            {
                Cursor.Restore(saved);
                return null;
            }

            var paramNameTok = NextToken();
            TypeSyntax paramType;
            if (Current.Kind == SyntaxTokenKind.Colon)
            {
                NextToken();
                paramType = Types.ParseTypeUntil(SyntaxTokenKind.Comma, SyntaxTokenKind.CloseParen);
            }
            else
            {
                paramType = InferredTypeSyntax.Instance;
            }

            parameters.Add(new ParameterSyntax(paramNameTok.Text, paramType) { NameSpan = SpanOf(paramNameTok) });

            if (Current.Kind == SyntaxTokenKind.Comma)
            {
                NextToken();
                SkipSeparators();
                continue;
            }

            if (Current.Position == startPosition)
            {
                Cursor.Restore(saved);
                return null;
            }
        }

        if (Current.Kind != SyntaxTokenKind.CloseParen)
        {
            Cursor.Restore(saved);
            return null;
        }

        NextToken(); // consume )
        if (Current.Kind != SyntaxTokenKind.FatArrow)
        {
            Cursor.Restore(saved);
            return null;
        }

        NextToken(); // consume =>
        SkipNewlines();
        // `(params) => body` where body is an expression OR a block. After `=>` a bare
        // `{` is unambiguously a block (a composite literal `=> Type { … }` leads with an
        // identifier, parsed by the expression path), so the two disambiguate with no
        // lookahead. Closures unify on the arrow; `func(…) -> T { … }` stays the
        // explicitly-typed-return escape hatch.
        BlockStatementSyntax body;
        if (Current.Kind == SyntaxTokenKind.OpenBrace)
        {
            body = Statements.ParseBlockStatement();
        }
        else
        {
            var expr = ParseExpression();
            var returnStmt = new ReturnStatementSyntax(expr) { Span = expr.Span };
            body = new BlockStatementSyntax([returnStmt]) { Span = returnStmt.Span };
        }
        return new FunctionLiteralExpressionSyntax(parameters, InferredTypeSyntax.Instance, body);
    }

    ExpressionSyntax ParseParenthesizedExpression()
    {
        Match(SyntaxTokenKind.OpenParen);
        var first = WithObjectCreationAllowed(() => ParseExpression());
        if (Current.Kind == SyntaxTokenKind.Comma)
        {
            // Tuple: (e1, e2, ...)
            var elements = new List<ExpressionSyntax> { first };
            while (Current.Kind == SyntaxTokenKind.Comma)
            {
                NextToken(); // consume ,
                SkipSeparators();
                if (Current.Kind == SyntaxTokenKind.CloseParen) break; // trailing comma
                elements.Add(WithObjectCreationAllowed(() => ParseExpression()));
            }
            Match(SyntaxTokenKind.CloseParen, "Expected ')' to close tuple.");
            return new TupleExpressionSyntax(elements);
        }
        Match(SyntaxTokenKind.CloseParen, "Expected ')' after expression.");
        return new ParenthesizedExpressionSyntax(first);
    }

    FunctionLiteralExpressionSyntax ParseFunctionLiteral()
    {
        Match(SyntaxTokenKind.FuncKeyword);
        Match(SyntaxTokenKind.OpenParen, "Expected '(' after 'func' in function literal.");

        var parameters = new List<ParameterSyntax>();
        SkipSeparators();
        while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            var paramNameTok = Match(SyntaxTokenKind.Identifier, "Expected parameter name.");
            Match(SyntaxTokenKind.Colon, "Expected ':' after parameter name.");
            var type = Types.ParseTypeUntil(SyntaxTokenKind.Comma, SyntaxTokenKind.CloseParen);
            parameters.Add(new ParameterSyntax(paramNameTok.Text, type) { NameSpan = SpanOf(paramNameTok) });

            if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseParen, "Expected ')' after parameters.");

        TypeSyntax returnType;
        if (Current.Kind == SyntaxTokenKind.Arrow)
        {
            NextToken();
            returnType = Types.ParseTypeUntil(SyntaxTokenKind.OpenBrace, SyntaxTokenKind.NewLine, SyntaxTokenKind.Equals);
        }
        else
        {
            returnType = new NamedTypeSyntax("void");
        }

        SkipSeparators();
        BlockStatementSyntax body;
        if (Current.Kind == SyntaxTokenKind.Equals)
        {
            NextToken(); // consume =
            SkipNewlines();
            var expr = ParseExpression();
            var returnStmt = new ReturnStatementSyntax(expr) { Span = expr.Span };
            body = new BlockStatementSyntax([returnStmt]) { Span = returnStmt.Span };
        }
        else
        {
            body = Statements.ParseBlockStatement();
        }
        return new FunctionLiteralExpressionSyntax(parameters, returnType, body);
    }

    ExpressionSyntax ParseMissingExpression()
    {
        var span = SpanHere();
        Report(Current.Line, Current.Column, $"Unexpected token '{Current.Text}' in expression.");
        NextToken(); // consume the unexpected token to guarantee forward progress
        return new ErrorExpressionSyntax { Span = SpanFrom(span) };
    }
}
