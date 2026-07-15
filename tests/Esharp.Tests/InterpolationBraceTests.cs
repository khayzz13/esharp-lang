namespace Esharp.Tests;

/// <summary>
/// String-interpolation lexing. A `{` opens an interpolation hole only when the next
/// character can start an expression (identifier / `(` / `!`) — the REFERENCE rule and
/// the binder's <c>IsInterpolationStart</c>. The lexer used to treat EVERY `{` as a hole
/// opener, so a literal `"{"` swallowed the closing quote ("Unterminated string literal")
/// and BCL format strings like `"{0}"` were mis-parsed; once a stray `{` desynced string
/// boundaries, a following real hole's local surfaced as `IL: undefined variable`.
///
/// These pin literal braces, BCL format strings, escaped `{{`/`}}`, and real holes — and
/// the interaction of a literal brace with a later real hole in the same scope.
/// </summary>
public sealed class InterpolationBraceTests
{
    static string Str(string body) =>
        (string)EsHarness.Run("namespace Test\nfunc go() -> string {\n" + body + "\n}", "go")!;

    // ---- literal braces -------------------------------------------------------

    [Theory]
    [InlineData("return \"{\"", "{")]                       // lone open brace before the closing quote
    [InlineData("return \"}\"", "}")]                       // lone close brace
    [InlineData("return \"{}\"", "{}")]                     // `{` before `}` (not an expr start) — literal pair
    [InlineData("return \"{ x\"", "{ x")]                   // `{` before a space — literal brace
    [InlineData("return \"a{:b}\"", "a{:b}")]               // `{` before `:` — literal
    public void LiteralBraces(string body, string expected) => Assert.Equal(expected, Str(body));

    [Theory]
    [InlineData("return \"{0}\"", "{0}")]                   // BCL positional format — leading digit, stays literal
    [InlineData("return \"{0:d}\"", "{0:d}")]               // BCL format with spec
    [InlineData("return \"{1} {0}\"", "{1} {0}")]           // multiple positional holes
    public void BclFormatStringsStayLiteral(string body, string expected) => Assert.Equal(expected, Str(body));

    // Brace-escapes (`{{`/`}}` → `{`/`}`) apply ONLY in an interpolation-bearing string
    // — one with a real hole — matching C#'s `$""`-only rule. A hole-free string is a
    // plain literal with verbatim braces, so JSON (`{"a":{}}`) round-trips without
    // silently eating a `}`. With a hole present, the C# escape collapse is in force.
    [Theory]
    [InlineData("return \"{{\"", "{{")]                     // no hole — verbatim doubled brace
    [InlineData("return \"}}\"", "}}")]                     // no hole — verbatim doubled brace
    [InlineData("return \"{{x}}\"", "{{x}}")]               // no hole — verbatim
    [InlineData("return \"{\\\"a\\\":{}}\"", "{\"a\":{}}")] // JSON object close — verbatim, no brace eaten
    public void HoleFreeBracesAreVerbatim(string body, string expected) => Assert.Equal(expected, Str(body));

    [Theory]
    [InlineData("let n = 7\nreturn \"{{}} {n}\"", "{} 7")]      // hole present — `{{`/`}}` collapse
    [InlineData("let n = 7\nreturn \"{{x}}={n}\"", "{x}=7")]    // escaped pair beside a real hole
    public void EscapedBracesCollapseWhenInterpolated(string body, string expected) => Assert.Equal(expected, Str(body));

    // ---- real holes -----------------------------------------------------------

    [Theory]
    [InlineData("let n = 7\nreturn \"{n}\"", "7")]
    [InlineData("let n = 7\nreturn \"v={n}\"", "v=7")]
    [InlineData("let a = 1\nlet b = 2\nreturn \"{a}+{b}\"", "1+2")]
    [InlineData("return \"{(1 + 2)}\"", "3")]               // `(` starts the hole
    [InlineData("let ok = true\nreturn \"{!ok}\"", "False")] // `!` starts the hole
    public void RealHoles(string body, string expected) => Assert.Equal(expected, Str(body));

    // ---- the regression: literal brace + later real hole, same scope ---------

    [Fact]
    public void LiteralBraceThenRealHole_SameScope()
    {
        // Two statements: a literal `"{"` then a hole `"{name}"`. The stray `{` used to
        // run the lexer past the first closing quote, desyncing every token after it.
        Assert.Equal("{kae", Str("""
let open = "{"
let name = "kae"
return open + "{name}"
"""));
    }

    [Fact]
    public void JsonObjectBraces_WithInterpolatedField()
    {
        // The shape that flushed the bug out: build `{"k":v}` with literal braces AND an
        // interpolated value in the same StringBuilder scope.
        var src = """
namespace Test
using "System.Text"
func go() -> string {
    let key = "k"
    let val = 9
    let sb = StringBuilder()
    sb.Append("{")
    sb.Append("\"{key}\":{val}")
    sb.Append("}")
    return sb.ToString()
}
""";
        Assert.Equal("{\"k\":9}", (string)EsHarness.Run(src, "go")!);
    }

    [Fact]
    public void InterpolationOnlyLocal_GetsSlot()
    {
        // A local referenced ONLY inside a hole still gets an IL slot (gap #5).
        Assert.Equal("14", Str("""
let doubled = 7 * 2
return "{doubled}"
"""));
    }

    [Fact]
    public void InterpolationOnlyLocal_MemberAccessInHole()
    {
        Assert.Equal("3,4", (string)EsHarness.Run("""
namespace Test
struct P { x: int, y: int }
func go() -> string {
    let p = P { x: 3, y: 4 }
    return "{p.x},{p.y}"
}
""", "go")!);
    }

    [Fact]
    public void HoleWithCall()
    {
        Assert.Equal("squared=9", (string)EsHarness.Run("""
namespace Test
func sq(n: int) -> int = n * n
func go() -> string {
    return "squared={sq(3)}"
}
""", "go")!);
    }

    [Fact]
    public void LiteralBraceAtEndOfString_NoHole()
    {
        // `{` as the final char before the closing quote: next char is `"`, not an
        // expression start, so it is a literal brace — not an unterminated string.
        Assert.Equal("prefix{", Str("return \"prefix{\""));
    }
}
