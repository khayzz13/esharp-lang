namespace Esharp.Tests;

/// <summary>Result tests run through E# so they exercise Esharp.Stdlib.Result`2.</summary>
public sealed class ResultCombinatorTests
{
    [Fact]
    public void Ok_Value_IsAvailableFromTheStdlibResult() => Assert.Equal(42, EsHarness.Run("""
namespace Test
func go() -> int {
    let value: Result<int, string> = Result.Ok<int, string>(42)
    return value.Value
}
""", "go"));

    [Fact]
    public void Error_Match_SelectsTheErrorArm() => Assert.Equal("bad", EsHarness.Run("""
namespace Test
func go() -> string {
    let value: Result<int, string> = Result.Error<int, string>("bad")
    match value {
        .ok(v) { return "wrong" }
        .err(e) { return e }
    }
    return "unreachable"
}
""", "go"));

    [Fact]
    public void Result_Propagation_ReturnsTheOriginalError()
    {
        var result = EsHarness.Run("""
namespace Test
func fail() -> Result<int, string> = Result.Error<int, string>("bad")
func go() -> Result<int, string> {
    let value = fail()?
    return Result.Ok<int, string>(value)
}
""", "go");
        Assert.Equal("bad", result!.GetType().GetField("Error")!.GetValue(result));
    }
}
