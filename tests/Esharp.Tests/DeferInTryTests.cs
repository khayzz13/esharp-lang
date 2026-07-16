namespace Esharp.Tests;

/// `defer` INSIDE a `try` block. The spec (exceptions-and-cleanup) makes a defer
/// within a `try` the finally-equivalent — it runs on every exit of its scope,
/// including an exception unwinding through the `try` before the `catch` runs.
/// DeferLowering.RewriteTryStatement recurses into the try body and catch bodies
/// so the defer lowers; before that fix a defer nested in a `try` survived
/// un-lowered. Function-scope defers are covered elsewhere — these pin the nested
/// case specifically.
public sealed class DeferInTryTests
{
    // Normal completion of the try block runs the defer.
    [Fact]
    public void DeferInTry_RunsOnNormalExit()
    {
        Assert.Equal(1, EsHarness.Run("""
namespace Test
func go() -> int {
    var v = 0
    try {
        defer { v = 1 }
        let n = 5
    } catch { }
    return v
}
""", "go"));
    }

    // A throw inside the try runs the defer as the exception unwinds, then the catch.
    [Fact]
    public void DeferInTry_RunsWhenTryThrows_ThenCatch()
    {
        Assert.Equal(11, EsHarness.Run("""
namespace Test
func go() -> int {
    var v = 0
    try {
        defer { v += 10 }
        throw InvalidOperationException("x")
    } catch {
        v += 1
    }
    return v
}
""", "go"));
    }

    // The defer has already run by the time the catch body executes.
    [Fact]
    public void DeferInTry_RunsBeforeCatchBody()
    {
        Assert.Equal(5, EsHarness.Run("""
namespace Test
func go() -> int {
    var v = 0
    try {
        defer { v = 5 }
        throw InvalidOperationException("x")
    } catch {
        return v
    }
    return -1
}
""", "go"));
    }

    // Two defers in one try block unwind LIFO.
    [Fact]
    public void DeferInTry_TwoDefers_RunLifo()
    {
        Assert.Equal(21, EsHarness.Run("""
namespace Test
func go() -> int {
    var order = 0
    try {
        defer { order = order * 10 + 1 }
        defer { order = order * 10 + 2 }
        let n = 5
    } catch { }
    return order
}
""", "go"));
    }
}
