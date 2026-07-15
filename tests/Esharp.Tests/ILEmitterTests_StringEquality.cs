// Style note: E# source below is inline \n-escaped for brevity — do NOT copy this in new test files; prefer readable """ raw-string blocks (these tests double as the E# corpus).
namespace Esharp.Tests;

/// `string == string` / `!=` are VALUE comparisons (String.op_Equality), not
/// `ceq` reference identity. The receiver strings here are built at runtime
/// (concatenation, substring, parameters) so they are NOT reference-equal to
/// literals — a `ceq` regression would make every case fail.
public sealed class ILEmitterTests_StringEquality
{
    static object? Run(string body, string method, params object?[] args) =>
        EsHarness.Run("namespace Test\n" + body, method, args);

    [Fact] public void ConcatEqualsLiteral() =>
        Assert.Equal(true, Run("func go() -> bool {\n  let a = \"ab\"\n  let b = \"a\" + \"b\"\n  return a == b\n}", "go"));

    [Fact] public void ParamEqualsLiteral_True() =>
        Assert.Equal(true, Run("func eq(s: string) -> bool = s == \"hello\"\nfunc go() -> bool = eq(\"hello\")", "go"));

    [Fact] public void ParamEqualsLiteral_False() =>
        Assert.Equal(false, Run("func eq(s: string) -> bool = s == \"hello\"\nfunc go() -> bool = eq(\"world\")", "go"));

    [Fact] public void NotEqual_True() =>
        Assert.Equal(true, Run("func go() -> bool {\n  let a = \"x\"\n  let b = \"y\"\n  return a != b\n}", "go"));

    [Fact] public void NotEqual_False() =>
        Assert.Equal(false, Run("func go() -> bool {\n  let a = \"x\"\n  let b = \"x\" + \"\"\n  return a != b\n}", "go"));

    [Fact] public void EqualityInIfCondition() =>
        Assert.Equal(1, Run("func classify(s: string) -> int {\n  if s == \"fail\" { return 1 }\n  return 0\n}\nfunc go() -> int = classify(\"fail\")", "go"));

    [Fact] public void EqualityInIfCondition_NotTaken() =>
        Assert.Equal(0, Run("func classify(s: string) -> int {\n  if s == \"fail\" { return 1 }\n  return 0\n}\nfunc go() -> int = classify(\"ok\")", "go"));

    [Fact] public void SubstringEqualsLiteral() =>
        Assert.Equal(true, Run("func go() -> bool {\n  let s = \"hello\"\n  let h = s.Substring(0, 3)\n  return h == \"hel\"\n}", "go"));

    [Fact] public void ChainedEquality() =>
        Assert.Equal(true, Run("func go() -> bool {\n  let a = \"a\"\n  let b = \"a\"\n  let c = \"a\"\n  return a == b && b == c\n}", "go"));

    [Fact] public void EqualityInWhileGuard() =>
        Assert.Equal(3, Run("func go() -> int {\n  var n = 0\n  var s = \"go\"\n  while s == \"go\" {\n    n += 1\n    if n >= 3 { s = \"stop\" }\n  }\n  return n\n}", "go"));

    [Fact] public void TwoRuntimeStringsEqual() =>
        Assert.Equal(true, Run("func go() -> bool {\n  let a = \"foo\" + \"bar\"\n  let b = \"foob\" + \"ar\"\n  return a == b\n}", "go"));

    [Fact] public void CharComparisonStillCeq() =>
        Assert.Equal(true, Run("func go() -> bool {\n  let s = \"a b\"\n  return s[1] == ' '\n}", "go"));

    [Fact] public void CharComparisonNotEqual() =>
        Assert.Equal(true, Run("func go() -> bool {\n  let s = \"abc\"\n  return s[0] != 'z'\n}", "go"));

    [Fact] public void EqualityFeedingReturn() =>
        Assert.Equal(false, Run("func go() -> bool {\n  let a = \"abc\"\n  let b = \"abd\"\n  return a == b\n}", "go"));

    [Fact] public void EmptyStringEquality() =>
        Assert.Equal(true, Run("func go() -> bool {\n  let a = \"\"\n  let b = \"x\".Substring(0, 0)\n  return a == b\n}", "go"));
}
