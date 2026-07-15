using Esharp.Syntax.Parsing;
using Esharp.Syntax;

namespace Esharp.Tests;

/// The front-end fidelity contract (tranche 1): the lexer is lossless, every node
/// carries a real range, the tree is navigable, and the printer reproduces source
/// byte-for-byte. The round-trip facts are the executable proof — if the printer
/// can't reproduce the source, fidelity is incomplete.
public sealed class SyntaxFidelityTests
{
    // A bank of valid E# exercising the breadth of the grammar. Each must round-trip
    // through the token stream character-for-character.
    public static IEnumerable<object[]> Sources()
    {
        yield return [Func];
        yield return [DataAndChoice];
        yield return [ControlFlow];
        yield return [Concurrency];
        yield return [ExprZoo];
        yield return [WithComments];
        yield return [OddSpacing];
        yield return [Crlf];
        yield return [NoTrailingNewline];
        yield return [LeadingAndTrailingTrivia];
    }

    const string Func = """
namespace App

func add<T>(a: int, b: int) -> int {
    let sum = a + b
    return sum * 2
}
""";

    const string DataAndChoice = """
namespace Model

derive equality, debug
class Account {
    pub id: int
    pub name: string
    init(id: int, name: string) {
        self.id = id
        self.name = name
    }
}

ref union Json {
    jnull
    jbool(value: bool)
    jarray(items: List<Json>)
}

enum Color { red, green = 5, blue }
""";

    const string ControlFlow = """
func classify(n: int) -> string {
    if n < 0 {
        return "neg"
    } else {
        for i in 0..n {
            defer { cleanup() }
        }
        while n > 0 {
            n = n - 1
        }
    }
    match n {
        .zero { return "z" }
        default { return "other" }
    }
}
""";

    const string Concurrency = """
func pipe() -> int {
    let ch = chan<int>(8)
    async let job = produce(ch)
    select {
        .recv(v, ch) { return v }
        default { return 0 }
    }
}
""";

    const string ExprZoo = """
func zoo() -> int {
    let xs = [1, 2, 3]
    let pair = (1, "two")
    let f = func(a: int) -> int { return a * a }
    let opt = maybe() ?? 0
    let tern = ready() ? 1 : 2
    let acc = Account { id: 1, name: "z" }
    let upd = acc with { name: "y" }
    let item = xs[0]
    let slice = xs[1..2]
    let chained = build().apply(a).apply(b)
    return opt + tern
}
""";

    const string WithComments = """
// a leading file comment
namespace Doc

/// a doc comment on the function
func documented() -> int {
    let x = 1 /* inline block */ + 2
    /* a
       multi-line
       block comment */
    return x // trailing line comment
}
""";

    const string OddSpacing = "func    weird(  a:int ,b:int )->int{\n    return a+b\n}\n";

    const string Crlf = "namespace Crlf\r\n\r\nfunc lf() -> int {\r\n    return 42\r\n}\r\n";

    const string NoTrailingNewline = "func tail() -> int { return 1 }";

    const string LeadingAndTrailingTrivia = "\n\n  // hi\nfunc f() -> int { return 0 }\n\n  \n";

    [Theory]
    [MemberData(nameof(Sources))]
    public void RoundTrip_FromTokens_IsByteExact(string source)
    {
        var tree = new Parser(source, "fidelity.es").Parse();
        Assert.Equal(source, SyntaxPrinter.Print(tree));
    }

    [Fact]
    public void Comments_AreCapturedAsTrivia_AndRoundTrip()
    {
        var tree = new Parser(WithComments, "c.es").Parse();
        Assert.Equal(WithComments, SyntaxPrinter.Print(tree));

        var kinds = tree.Tokens
            .SelectMany(t => t.Leading)
            .Select(tr => tr.Kind)
            .ToHashSet();
        Assert.Contains(SyntaxTriviaKind.LineComment, kinds);
        Assert.Contains(SyntaxTriviaKind.BlockComment, kinds);
        Assert.Contains(SyntaxTriviaKind.DocComment, kinds);
    }

    [Fact]
    public void Ranges_AreNonEmpty_AndParentContainsChildren()
    {
        var root = new Parser(ExprZoo, "ranges.es").ParseCompilationUnit();
        foreach (var node in SyntaxNavigator.DescendantsAndSelf(root))
        {
            var span = SyntaxNavigator.SpanOf(node);
            if (!span.IsValid) continue;
            Assert.True(span.End >= span.Start, $"{node.GetType().Name} End<Start");

            foreach (var child in SyntaxNavigator.Children(node))
            {
                var cs = SyntaxNavigator.SpanOf(child);
                if (!cs.IsValid) continue;
                Assert.True(span.Start <= cs.Start && cs.End <= span.End,
                    $"{node.GetType().Name} [{span.Start}..{span.End}] does not contain " +
                    $"{child.GetType().Name} [{cs.Start}..{cs.End}]");
            }
        }
    }

    [Fact]
    public void RealParsedNodes_HaveRealRanges()
    {
        const string src = "func f() -> int { return abc + 1 }";
        var root = new Parser(src, "r.es").ParseCompilationUnit();
        // Every expression node from a real parse spans more than a point where it has content.
        var add = FirstOfType<BinaryExpressionSyntax>(root);
        Assert.True(add.Span.IsValid);
        Assert.True(add.Span.End > add.Span.Start);
        Assert.Equal("abc + 1", src[add.Span.Start..add.Span.End]);
    }

    [Fact]
    public void FindNode_LocatesInnermost_AndAncestorsReachUnit()
    {
        const string src = "func f() -> int { return abc + 1 }";
        var root = new Parser(src, "nav.es").ParseCompilationUnit();
        var offset = src.IndexOf("abc", StringComparison.Ordinal);

        var found = SyntaxNavigator.FindNode(root, offset);
        var name = Assert.IsType<NameExpressionSyntax>(found);
        Assert.Equal("abc", name.Name);

        var ancestors = SyntaxNavigator.Ancestors(root, found!);
        Assert.Contains(ancestors, a => a is FunctionDeclarationSyntax);
        Assert.IsType<CompilationUnitSyntax>(ancestors[^1]);
    }

    [Fact]
    public void NodeSlice_OfUnmodifiedNode_IsVerbatimSource()
    {
        const string src = """
func g() -> int {
    return a * (b + c)
}
""";
        var root = new Parser(src, "slice.es").ParseCompilationUnit();
        var fn = FirstOfType<FunctionDeclarationSyntax>(root);
        // Print(node, source) slices the original text for an unmodified node.
        var printed = SyntaxPrinter.Print(fn, src);
        Assert.Equal(src[fn.Span.Start..fn.Span.End], printed);
        Assert.Contains("a * (b + c)", printed);
    }

    [Fact]
    public void Canonical_SynthesizedExpression_IsValid_Reparseable_AndStable()
    {
        // A spanless `(a + b) * c` tree — the printer must parenthesize so the
        // multiplication still binds the sum, then reparse to the same canonical form.
        var synthesized = new BinaryExpressionSyntax(
            new BinaryExpressionSyntax(new NameExpressionSyntax("a"), SyntaxTokenKind.Plus, new NameExpressionSyntax("b")),
            SyntaxTokenKind.Star,
            new NameExpressionSyntax("c"));

        var canon1 = SyntaxPrinter.PrintCanonical(synthesized);
        Assert.Equal("(a + b) * c", canon1);

        var reparser = new Parser(canon1, "synth.es");
        var reparsed = reparser.ParseStandaloneExpression();
        Assert.Empty(reparser.Diagnostics);

        var canon2 = SyntaxPrinter.PrintCanonical(reparsed);
        Assert.Equal(canon1, canon2);
    }

    [Fact]
    public void Canonical_SynthesizedFunction_Reparses_WithoutError()
    {
        var fn = new FunctionDeclarationSyntax(
            IsPublic: false,
            Name: "twice",
            TypeParameters: [],
            Parameters: [new ParameterSyntax("x", new NamedTypeSyntax("int"))],
            ReturnType: new NamedTypeSyntax("int"),
            Body: new BlockStatementSyntax([
                new ReturnStatementSyntax(
                    new BinaryExpressionSyntax(new NameExpressionSyntax("x"), SyntaxTokenKind.Star,
                        new LiteralExpressionSyntax(2, "2")))
            ]),
            Attributes: []);

        var canon1 = SyntaxPrinter.PrintCanonical(fn);
        var reparser = new Parser(canon1, "synthfn.es");
        var root = reparser.ParseCompilationUnit();
        Assert.Empty(reparser.Diagnostics);

        var reparsedFn = FirstOfType<FunctionDeclarationSyntax>(root);
        Assert.Equal(canon1.Trim(), SyntaxPrinter.PrintCanonical(reparsedFn).Trim());
    }

    static T FirstOfType<T>(SyntaxNode root) where T : SyntaxNode =>
        SyntaxNavigator.DescendantsAndSelf(root).OfType<T>().First();
}
