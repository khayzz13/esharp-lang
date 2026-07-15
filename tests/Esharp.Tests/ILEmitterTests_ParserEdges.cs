using Xunit;

namespace Esharp.Tests;

/// WS4 parser/lexer sharp edges + the bare-enum-case decision (probe2 #1/#4, probe3 #5).
public sealed class ILEmitterTests_ParserEdges
{
    static object? Run(string body, string method) => EsHarness.Run(body, method);

    // probe3 #5 — a bare prefix `-` leading a match-arm value-expression parses.
    [Fact]
    public void BareUnaryMinus_InMatchArm() => Assert.Equal(-1, Run("""
namespace Test
enum E { a, b }
func f(e: E) -> int = match e { .a { -1 }  .b { 2 } }
func go() -> int = f(E.a())
""", "go"));

    // probe3 #5 (sibling) — bare unary minus as an expression statement's value.
    [Fact]
    public void BareUnaryMinus_AsExpressionValue() => Assert.Equal(-5, Run("""
namespace Test
func neg(x: int) -> int {
    let r = match x { 0 { -5 }  default { x } }
    return r
}
func go() -> int = neg(0)
""", "go"));

    // probe2 #1 — bare `EnumType.Case` (no parens) is valid and idiomatic; it constructs the
    // case exactly as `Color.Blue()` does.
    [Fact]
    public void BareEnumCase_NoParens_IsValid() => Assert.Equal("b", Run("""
namespace Test
enum Color { Red, Green, Blue }
func go() -> string {
    let c = Color.Blue
    return match c { .Red { "r" }  .Green { "g" }  .Blue { "b" } }
}
""", "go"));

    // probe2 #1 — bare enum case directly in a match scrutinee / equality.
    [Fact]
    public void BareEnumCase_InEquality() => Assert.Equal(true, Run("""
namespace Test
enum Color { Red, Green, Blue }
func go() -> bool = Color.Green == Color.Green
""", "go"));

    // probe2 #4 — a string literal nested inside an interpolation hole is NOT supported:
    // `{"` is lexically ambiguous with a literal `{` before a closing quote (`"{"`, JSON
    // `{"a":1}`), and disambiguating would break those far-more-common forms. The
    // documented workaround is to bind the inner string to a local first.
    [Fact]
    public void NestedInterpolation_WorkaroundViaLocal() => Assert.Equal("nested in 5", Run("""
namespace Test
func go() -> string {
    let x = 5
    let inner = "in {x}"
    return "nested {inner}"
}
""", "go"));
}
