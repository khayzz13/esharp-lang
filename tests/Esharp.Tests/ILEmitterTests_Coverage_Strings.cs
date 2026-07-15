// Style note: full-program E# samples use """ raw-string blocks; the [Theory]
// rows below carry single expressions, not multi-line programs.
namespace Esharp.Tests;

/// String, char, interpolation and numeric-literal coverage.
public sealed class ILEmitterTests_Coverage_Strings
{
    static string Str(string body) =>
        (string)EsHarness.Run("namespace Test\nfunc go() -> string {\n" + body + "\n}", "go")!;

    static int Int(string body) =>
        (int)EsHarness.Run("namespace Test\nfunc go() -> int {\n" + body + "\n}", "go")!;

    static bool Bool(string body) =>
        (bool)EsHarness.Run("namespace Test\nfunc go() -> bool {\n" + body + "\n}", "go")!;

    [Theory]
    [InlineData("return \"hello\"", "hello")]
    [InlineData("return \"a\" + \"b\"", "ab")]
    [InlineData("let n = \"world\"\nreturn \"hello {n}\"", "hello world")]
    [InlineData("let a = 3\nlet b = 4\nreturn \"{a}+{b}={a + b}\"", "3+4=7")]
    [InlineData("let xs = [10, 20, 30]\nreturn \"first={xs[0]}\"", "first=10")]
    [InlineData("return \"value is {(1 + 2 * 3)}\"", "value is 7")]
    [InlineData("let s = \"hi\"\nreturn s + \"!\" + s", "hi!hi")]
    public void Interpolation(string body, string expected) => Assert.Equal(expected, Str(body));

    [Theory]
    [InlineData("return \"hello\".Length", 5)]
    [InlineData("return \"\".Length", 0)]
    [InlineData("return \"hello world\".Length", 11)]
    [InlineData("return \"abc\".Substring(1).Length", 2)]
    [InlineData("return \"a,b,c\".Split(',').Length", 3)]
    public void StringMembers(string body, int expected) => Assert.Equal(expected, Int(body));

    [Theory]
    [InlineData("return \"abc\" == \"abc\"", true)]
    [InlineData("return \"abc\" == \"abd\"", false)]
    [InlineData("return \"abc\" != \"xyz\"", true)]
    [InlineData("return \"\" == \"\"", true)]
    public void StringEquality(string body, bool expected) => Assert.Equal(expected, Bool(body));

    [Theory]
    [InlineData("return 1_000", 1000)]
    [InlineData("return 1_000_000", 1000000)]
    [InlineData("return 42_000 + 1", 42001)]
    [InlineData("return 1_2_3", 123)]
    public void DigitSeparators(string body, int expected) => Assert.Equal(expected, Int(body));

    [Theory]
    [InlineData("let c = 'a'\nreturn c == 'a'", true)]
    [InlineData("let c = 'z'\nreturn char.IsDigit(c)", false)]
    [InlineData("let c = '7'\nreturn char.IsDigit(c)", true)]
    public void CharLiterals(string body, bool expected) => Assert.Equal(expected, Bool(body));
}
