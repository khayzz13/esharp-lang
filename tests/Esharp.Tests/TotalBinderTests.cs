using Esharp.Diagnostics;
using Esharp.Syntax.Parsing;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

/// The total-front-end contract: lex → parse → bind never throws on ANY input.
/// Broken expressions bind to error nodes, broken declarations yield partial
/// symbols, and the rest of the file still binds — bad input produces located
/// diagnostics, never an exception. Diagnostics gate emission, not binding.
public class TotalBinderTests
{
    static (IReadOnlyList<Diagnostic> parse, IReadOnlyList<Diagnostic> bind) BindAnything(string source)
    {
        var parser = new Parser(source, "broken.es");
        var unit = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        binder.Bind(unit); // must not throw, whatever the input
        return (parser.Diagnostics, binder.Diagnostics);
    }

    // ── The no-crash bank ────────────────────────────────────────────────────

    public static TheoryData<string> MalformedInputs => new()
    {
        // truncated / half-typed constructs
        "func main() {",
        "func main() { let x = ",
        "func main() { let x = 1 +",
        "func broken(",
        "func f() -> { }",
        "struct P { x:",
        "struct P { x: int",
        "union C { a(",
        "class W : {",
        "func main() { foo(1, }",
        "func main() { match x { } }",
        "func main() { if { } }",
        // garbage tokens
        "@#$%^&*",
        "func main() { ??? }",
        "}}}}",
        "struct data data",
        "func func func()",
        // half-edited mid-keystroke shapes
        "func main() { let p = Point { x: } }",
        "func main() { v. }",
        "func main() { return + }",
        "namespace ",
        "using ",
        "func main() { spawn { ",
        "static S { let x = ",
        "func f() { for i in }",
        "interface IBroken { func }",
        // unterminated literals / interpolation
        "func main() { let s = \"abc }",
        "func main() { let s = \"a {b\" }",
        // empty / whitespace / comments only
        "",
        "   \n\n\t  ",
        "// just a comment\n/* and a block */",
    };

    [Theory]
    [MemberData(nameof(MalformedInputs))]
    public void Bind_NeverThrows_OnMalformedInput(string source)
    {
        var (parse, bind) = BindAnything(source); // the assertion IS not-throwing
        _ = parse; _ = bind;
    }

    // ── Partial bind: one broken function doesn't take down its siblings ────

    [Fact]
    public void BrokenFunction_SiblingsStillBind()
    {
        var source = """
            struct Point { x: int, y: int }

            func (p: Point) broken() -> int {
                return p.x +
            }

            func (p: Point) fine() -> int {
                return p.x + p.y
            }
            """;
        var parser = new Parser(source, "partial.es");
        var unit = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(unit);

        Assert.NotEmpty(parser.Diagnostics); // the parse error is real…
        // …but the unit still produced bound members, including the healthy sibling.
        Assert.Contains(bound.Members,
            m => m is Esharp.BoundTree.BoundDataDeclaration d && d.Name == "Point");
        Assert.Contains(bound.Members,
            m => m is Esharp.BoundTree.BoundFunctionDeclaration f && f.Name == "fine"
                 || m is Esharp.BoundTree.BoundDataDeclaration { Name: "Point" } dd
                    && dd.InstanceMethods.Any(im => im.Name == "fine"));
    }

    [Fact]
    public void RegistrationError_StillBinds()
    {
        // ES2160 (lowercase type name) is a registration-phase error — binding
        // must still run and produce a tree (the old pipeline returned empty).
        var source = """
            struct lowercase { x: int }
            func ok() -> int { return 1 }
            """;
        var parser = new Parser(source, "reg.es");
        var unit = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(unit);
        Assert.Contains(binder.Diagnostics, d => d.Message.Contains("ES2160"));
        Assert.NotEmpty(bound.Members);
    }

    // ── Diagnostic authority: located, binder-owned errors ───────────────────

    [Fact]
    public void UndefinedName_IsLocatedBinderError()
    {
        var diags = EsHarness.Diagnostics("""
            func main() -> int {
                return undefinedThing + 1
            }
            """);
        var es2146 = Assert.Single(diags, d => d.Message.Contains("ES2146"));
        Assert.Equal("test.es", es2146.FilePath);
        Assert.True(es2146.Line > 0, "ES2146 must carry a real line");
        Assert.Contains("undefinedThing", es2146.Message);
    }

    [Fact]
    public void UnknownMemberOnData_IsLocatedBinderError()
    {
        var diags = EsHarness.Diagnostics("""
            struct Point { x: int, y: int }
            func (p: Point) main() -> int {
                return p.zz
            }
            """);
        var es2147 = Assert.Single(diags, d => d.Message.Contains("ES2147"));
        Assert.Equal("test.es", es2147.FilePath);
        Assert.True(es2147.Line > 0, "ES2147 must carry a real line");
        Assert.Contains("zz", es2147.Message);
    }

    [Fact]
    public void UnknownCompositeLiteralType_IsLocatedBinderError()
    {
        var diags = EsHarness.Diagnostics("""
            func id<T>(x: T) -> T {
                return x
            }
            func main() -> int {
                let b = id<Box>(Box { n: 7 })
                return b.n
            }
            """);
        var es2148 = Assert.Single(diags, d => d.Message.Contains("ES2148"));
        Assert.Equal("test.es", es2148.FilePath);
        Assert.True(es2148.Line > 0, "ES2148 must carry a real line");
        Assert.Contains("Box", es2148.Message);
    }

    [Fact]
    public void UndefinedName_DoesNotCascade()
    {
        // One typo, one diagnostic: the error node suppresses follow-on noise
        // from the member access and the arithmetic over it.
        var diags = EsHarness.Diagnostics("""
            func main() -> int {
                let v = missing.field + 1
                return v
            }
            """);
        Assert.Single(diags, d => d.Message.Contains("ES2146"));
        Assert.DoesNotContain(diags, d => d.Message.Contains("ES2147"));
    }

    // ── The binder does not false-positive on names it doesn't own ──────────

    [Fact]
    public void PromotedMethodAndObjectMembers_AreNotErrors()
    {
        var asm = EsHarness.Compile("""
            namespace Test
            struct Vec { x: int }
            func (v: Vec) bump() -> int { return v.x + 1 }
            func go() -> int {
                let v = Vec { x: 41 }
                let s = v.ToString()
                return v.bump()
            }
            func run() -> int { return go() }
            """, tag: "TotalOk");
        Assert.Equal(42, EsHarness.Invoke(asm, "go"));
    }
}
