namespace Esharp.Tests;

/// <summary>
/// Uncolored async called by a synchronous function starts at the binding and is
/// synchronously joined at the first use of the value. These cases exercise the
/// actual IL path (`ValueTask&lt;T&gt;.GetAwaiter().GetResult()`), not just binding.
/// </summary>
public sealed class SyncFutureTests
{
    public static TheoryData<int, int, int> ArithmeticCases => new()
    {
        { 0, 1, 1 }, { 1, 1, 2 }, { 7, 5, 12 }, { -3, 10, 7 }, { 42, -2, 40 },
        { 100, 100, 200 }, { -8, -4, -12 }, { 19, 23, 42 }, { 2, 40, 42 }, { 9, 0, 9 },
    };

    [Theory]
    [MemberData(nameof(ArithmeticCases))]
    public void SyncCaller_UsesUncoloredAsyncValueAtFirstUse(int input, int delta, int expected)
    {
        var source = $$"""
namespace Test
func fetch(n: int) -> int {
    let value = await Task.FromResult(n)
    return value
}
func go() -> int {
    let futureValue = fetch({{input}})
    let unrelated = {{delta}} * 2
    return futureValue + unrelated - {{delta}}
}
""";
        Assert.Equal(expected, EsHarness.Run(source, "go"));
    }

    [Fact]
    public void TwoSyncFutureBindings_JoinInTheirFirstUseOrder()
    {
        Assert.Equal(42, EsHarness.Run("""
namespace Test
func fetch(n: int) -> int { return await Task.FromResult(n) }
func go() -> int {
    let left = fetch(19)
    let right = fetch(23)
    return right + left
}
""", "go"));
    }

    [Fact]
    public void SyncFuture_CanSupplyAFunctionArgument()
    {
        Assert.Equal(42, EsHarness.Run("""
namespace Test
func fetch(n: int) -> int { return await Task.FromResult(n) }
func twice(n: int) -> int = n * 2
func go() -> int {
    let value = fetch(21)
    return twice(value)
}
""", "go"));
    }

    [Fact]
    public void SyncFuture_FirstUseInsideConditionalBranch_JoinsOnlyInThatBranch()
    {
        Assert.Equal(42, EsHarness.Run("""
namespace Test
func fetch(n: int) -> int { return await Task.FromResult(n) }
func go() -> int {
    let value = fetch(42)
    if true { return value }
    return 0
}
""", "go"));
    }

    [Fact]
    public void ExplicitTaskUsedAsAnArithmeticValue_IsDiagnosedBeforeILVerification()
    {
        var diagnostics = EsHarness.AllDiagnostics("""
namespace Test
using "System.Threading.Tasks"

func fetch() -> Task<int> {
    await Task.Delay(1)
    return 41
}

func bad() -> int {
    let task = fetch()
    return task + 1
}
""");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("ES2260")
            && diagnostic.Message.Contains("Task<int>")
            && diagnostic.Message.Contains("awaitable"));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Message.Contains("ES0900"));
    }
}
