// Style note: E# source below is inline \n-escaped for brevity — do NOT copy this in new test files; prefer readable """ raw-string blocks (these tests double as the E# corpus).
namespace Esharp.Tests;

/// String interpolation: holes are full expressions parsed + bound through the
/// normal pipeline (operators, calls, ternaries, indexing, member chains,
/// payload-view projections), value types boxed into string.Concat. `{0}`-style
/// BCL format placeholders stay literal.
public sealed class ILEmitterTests_Interpolation
{
    static object? Run(string body, string method = "go") =>
        EsHarness.Run("namespace Test\n" + body, method);

    [Fact] public void PlainVariable() =>
        Assert.Equal("x=5", Run("func go() -> string {\n  let x = 5\n  return \"x={x}\"\n}"));

    [Fact] public void TwoHoles() =>
        Assert.Equal("3 and 4", Run("func go() -> string {\n  let a = 3\n  let b = 4\n  return \"{a} and {b}\"\n}"));

    [Fact] public void AdditionOperator() =>
        Assert.Equal("sum=7", Run("func go() -> string {\n  let a = 3\n  let b = 4\n  return \"sum={a + b}\"\n}"));

    [Fact] public void SubtractionOperator() =>
        Assert.Equal("d=-1", Run("func go() -> string {\n  let a = 3\n  let b = 4\n  return \"d={a - b}\"\n}"));

    [Fact] public void MultiplicationOperator() =>
        Assert.Equal("p=12", Run("func go() -> string {\n  let a = 3\n  let b = 4\n  return \"p={a * b}\"\n}"));

    [Fact] public void DivisionOperator() =>
        Assert.Equal("q=5", Run("func go() -> string {\n  let a = 20\n  let b = 4\n  return \"q={a / b}\"\n}"));

    [Fact] public void ComparisonOperator() =>
        Assert.Equal("lt=True", Run("func go() -> string {\n  let a = 3\n  let b = 4\n  return \"lt={a < b}\"\n}"));

    [Fact] public void TernaryInHole() =>
        Assert.Equal("max=4", Run("func go() -> string {\n  let a = 3\n  let b = 4\n  return \"max={a > b ? a : b}\"\n}"));

    [Fact] public void NestedArithmetic() =>
        Assert.Equal("r=15", Run("func go() -> string {\n  let a = 3\n  let b = 4\n  return \"r={a + b + b + b - 1 + 4 - 2 - 1}\"\n}"));

    [Fact] public void CallInHole() =>
        Assert.Equal("dbl=10", Run("func dbl(x: int) -> int = x * 2\nfunc go() -> string {\n  return \"dbl={dbl(5)}\"\n}"));

    [Fact] public void MemberChainInHole() =>
        Assert.Equal("n=kae", Run("struct P { name: string }\nfunc go() -> string {\n  let p = P { name: \"kae\" }\n  return \"n={p.name}\"\n}"));

    [Fact] public void IndexInHole() =>
        Assert.Equal("first=10", Run("func go() -> string {\n  let xs = [10, 20, 30]\n  return \"first={xs[0]}\"\n}"));

    [Fact] public void StringHole() =>
        Assert.Equal("hi bob", Run("func go() -> string {\n  let who = \"bob\"\n  return \"hi {who}\"\n}"));

    [Fact] public void BoolHole() =>
        Assert.Equal("flag=True", Run("func go() -> string {\n  let f = true\n  return \"flag={f}\"\n}"));

    [Fact] public void DoubleHole() =>
        Assert.Equal("v=1.5", Run("func go() -> string {\n  let d = 1.5\n  return \"v={d}\"\n}"));

    [Fact] public void LeadingAndTrailingText() =>
        Assert.Equal("[5]", Run("func go() -> string {\n  let x = 5\n  return \"[{x}]\"\n}"));

    [Fact] public void AdjacentHoles() =>
        Assert.Equal("34", Run("func go() -> string {\n  let a = 3\n  let b = 4\n  return \"{a}{b}\"\n}"));

    [Fact] public void NoHolesPlainString() =>
        Assert.Equal("plain", Run("func go() -> string = \"plain\""));

    [Fact] public void EmptyTextAroundSingleHole() =>
        Assert.Equal("9", Run("func go() -> string {\n  let x = 9\n  return \"{x}\"\n}"));

    [Fact] public void BclFormatPlaceholderStaysLiteral() =>
        Assert.Equal("{0} and 5", Run("func go() -> string {\n  let x = 5\n  return \"{0} and {x}\"\n}"));

    [Fact] public void ChainedMemberInHole() =>
        Assert.Equal("inner=7", Run("struct Inner { v: int }\nstruct Outer { inner: Inner }\nfunc go() -> string {\n  let o = Outer { inner: Inner { v: 7 } }\n  return \"inner={o.inner.v}\"\n}"));

    [Fact] public void HoleInLetThenReturn() =>
        Assert.Equal("got 42", Run("func go() -> string {\n  let n = 42\n  let s = \"got {n}\"\n  return s\n}"));

    [Fact] public void NegativeNumberHole() =>
        Assert.Equal("neg=-7", Run("func go() -> string {\n  let n = 0 - 7\n  return \"neg={n}\"\n}"));

    [Fact] public void ModuloInHole() =>
        Assert.Equal("mod=1", Run("func go() -> string {\n  let a = 7\n  let b = 3\n  return \"mod={a % b}\"\n}"));

    [Fact] public void AndOperatorInHole() =>
        Assert.Equal("both=True", Run("func go() -> string {\n  let a = true\n  let b = true\n  return \"both={a && b}\"\n}"));

    [Fact] public void OrOperatorInHole() =>
        Assert.Equal("any=True", Run("func go() -> string {\n  let a = false\n  let b = true\n  return \"any={a || b}\"\n}"));

    [Fact] public void ParenthesizedHole() =>
        Assert.Equal("r=14", Run("func go() -> string {\n  let a = 3\n  let b = 4\n  return \"r={(a + b) * 2}\"\n}"));

    [Fact] public void CallWithArgExpression() =>
        Assert.Equal("s=20", Run("func add(a: int, b: int) -> int = a + b\nfunc go() -> string {\n  return \"s={add(8, 12)}\"\n}"));

    [Fact] public void InterpolationInsideMatchArm() =>
        Assert.Equal("two:2", Run("func go() -> string {\n  let n = 2\n  return match n {\n    1 { \"one:1\" }\n    2 { \"two:{n}\" }\n    default { \"x\" }\n  }\n}"));

    [Fact] public void MixedValueAndRefHoles() =>
        Assert.Equal("kae is 30", Run("func go() -> string {\n  let name = \"kae\"\n  let age = 30\n  return \"{name} is {age}\"\n}"));

    [Fact] public void NegationUnaryInHole() =>
        Assert.Equal("not=False", Run("func go() -> string {\n  let b = true\n  return \"not={!b}\"\n}"));

    [Fact] public void ThreeHolesArithmetic() =>
        Assert.Equal("1+2=3", Run("func go() -> string {\n  let a = 1\n  let b = 2\n  return \"{a}+{b}={a + b}\"\n}"));

    // === added: interpolation hole forms (Theory) ===

    // A `{` opens a hole only when the next char can start an expression; a leading
    // digit stays literal (`{0}` → "{0}"), so arithmetic holes are parenthesized.
    [Theory]
    [InlineData("\"v={(1 + 2)}\"", "v=3")]
    [InlineData("\"v={(10 - 4)}\"", "v=6")]
    [InlineData("\"v={(3 * 3)}\"", "v=9")]
    [InlineData("\"{true}\"", "True")]
    [InlineData("\"{(1 > 0) ? \\\"y\\\" : \\\"n\\\"}\"", "y")]
    [InlineData("\"{(1 < 0) ? \\\"y\\\" : \\\"n\\\"}\"", "n")]
    public void Added_HoleExpressions(string body, string expected) =>
        Assert.Equal(expected, Run($"func go() -> string {{\n  return {body}\n}}"));

    [Fact] public void Added_MemberChainHole() =>
        Assert.Equal("x=5", Run("struct P { x: int }\nfunc go() -> string {\n  let p = P { x: 5 }\n  return \"x={p.x}\"\n}"));

    [Fact] public void Added_PointerFieldHole() =>
        Assert.Equal("n=7", Run("struct B { n: int }\nfunc go() -> string {\n  let b = new B { n: 7 }\n  return \"n={b.n}\"\n}"));

    [Fact] public void Added_CallInHole() =>
        Assert.Equal("d=8", Run("func dbl(x: int) -> int = x * 2\nfunc go() -> string {\n  return \"d={dbl(4)}\"\n}"));

    [Fact] public void Added_IndexInHole() =>
        Assert.Equal("first=9", Run("func go() -> string {\n  let xs = [9, 8]\n  return \"first={xs[0]}\"\n}"));

    [Fact] public void Added_LiteralBracesPassThrough() =>
        Assert.Equal("{0}", Run("func go() -> string {\n  return \"{0}\"\n}"));
}
