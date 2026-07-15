// Style note: full-program E# samples use """ raw-string blocks; the [Theory]
// rows below carry single expressions/fragments, not multi-line programs.
namespace Esharp.Tests;

/// Broad behavioural coverage of the expression/operator/control-flow surface.
/// Each row compiles a tiny program through the IL backend and runs it.
public sealed class ILEmitterTests_Coverage_Ops
{
    static int Int(string body) =>
        (int)EsHarness.Run("namespace Test\nfunc go() -> int {\n" + body + "\n}", "go")!;

    static bool Bool(string body) =>
        (bool)EsHarness.Run("namespace Test\nfunc go() -> bool {\n" + body + "\n}", "go")!;

    [Theory]
    [InlineData("return 2 + 3", 5)]
    [InlineData("return 10 - 4", 6)]
    [InlineData("return 6 * 7", 42)]
    [InlineData("return 20 / 4", 5)]
    [InlineData("return 17 % 5", 2)]
    [InlineData("return 2 + 3 * 4", 14)]
    [InlineData("return (2 + 3) * 4", 20)]
    [InlineData("return 100 / 10 / 2", 5)]
    [InlineData("return 0 - 8", -8)]
    [InlineData("return 1_000_000 + 1", 1000001)]
    [InlineData("return 7 * 7 - 7", 42)]
    [InlineData("return 3 * (4 + 5) - 1", 26)]
    public void IntegerArithmetic(string body, int expected) => Assert.Equal(expected, Int(body));

    [Theory]
    [InlineData("var x = 5\nx += 3\nreturn x", 8)]
    [InlineData("var x = 5\nx -= 3\nreturn x", 2)]
    [InlineData("var x = 5\nx *= 4\nreturn x", 20)]
    [InlineData("var x = 20\nx /= 5\nreturn x", 4)]
    [InlineData("var x = 0\nx += 1\nx += 1\nx += 1\nreturn x", 3)]
    public void CompoundAssignment(string body, int expected) => Assert.Equal(expected, Int(body));

    [Theory]
    [InlineData("return 3 < 5", true)]
    [InlineData("return 5 < 3", false)]
    [InlineData("return 5 <= 5", true)]
    [InlineData("return 5 >= 6", false)]
    [InlineData("return 4 == 4", true)]
    [InlineData("return 4 != 4", false)]
    [InlineData("return 3 > 2 and 5 > 4", true)]
    [InlineData("return 3 > 2 and 5 < 4", false)]
    [InlineData("return 3 < 2 or 5 > 4", true)]
    [InlineData("return not (3 < 2)", true)]
    [InlineData("return true and false", false)]
    [InlineData("return true or false", true)]
    public void BooleanLogic(string body, bool expected) => Assert.Equal(expected, Bool(body));

    [Theory]
    [InlineData("if 5 > 3 { return 1 }\nreturn 0", 1)]
    [InlineData("if 5 < 3 { return 1 }\nreturn 0", 0)]
    [InlineData("if 1 > 2 { return 1 } else { return 2 }", 2)]
    [InlineData("if 1 > 2 { return 1 } else if 3 > 2 { return 3 } else { return 0 }", 3)]
    public void IfElse(string body, int expected) => Assert.Equal(expected, Int(body));

    [Theory]
    [InlineData("var t = 0\nvar i = 1\nwhile i <= 5 { t += i\ni += 1 }\nreturn t", 15)]
    [InlineData("var t = 0\nfor i in 1..5 { t += i }\nreturn t", 10)]
    [InlineData("var t = 0\nfor i in 0..10 { t += 1 }\nreturn t", 10)]
    [InlineData("var t = 1\nfor i in 1..6 { t *= 2 }\nreturn t", 32)]
    public void Loops(string body, int expected) => Assert.Equal(expected, Int(body));

    [Theory]
    [InlineData("let x = 7 > 0 ? 1 : 0\nreturn x", 1)]
    [InlineData("let x = 7 < 0 ? 1 : 0\nreturn x", 0)]
    [InlineData("return 5 > 3 ? 100 : 200", 100)]
    [InlineData("let v0 = 1\nreturn (v0 < 11 || 16 > (-11 + -9)) ? 1 : 2", 1)]
    public void Ternary(string body, int expected) => Assert.Equal(expected, Int(body));

    [Theory]
    [InlineData("let a = 3\nlet b = 4\nreturn a * a + b * b", 25)]
    [InlineData("let x = 10\nlet y = x * 2\nlet z = y + x\nreturn z", 30)]
    public void LetBindings(string body, int expected) => Assert.Equal(expected, Int(body));
}
