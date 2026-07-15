// Regression: a generic external constructor (`List<int>()`) must resolve in a
// compilation that ALSO contains a `*T` heap-pointer over a value `data`. Surfaced by
// the corpus tool's standalone verifier (direct ILEmitter path), where `List<int>()`
// reported "unresolved function 'List<int>'" only when a `*Counter` was present.
using Xunit;

namespace Esharp.Tests;

public sealed class ILEmitterTests_PtrListRepro
{
    static object? Run(string src, string method = "go") => EsHarness.Run(src, method);

    [Fact]
    public void ListCtor_Alone_Resolves()
    {
        Assert.Equal(15, Run("""
namespace Test

func go() -> int {
    let xs = List<int>()
    xs.Add(5)
    xs.Add(10)
    var t = 0
    for x in xs {
        t += x
    }
    return t
}
"""));
    }

    [Fact]
    public void ListCtor_WithHeapPointerData_Resolves()
    {
        Assert.Equal(15, Run("""
namespace Test

struct Counter {
    var total: int
}

func bump(c: *Counter, n: int) {
    c.total += n
}

func go() -> int {
    var c: *Counter = new Counter { total: 0 }
    let xs = List<int>()
    xs.Add(5)
    xs.Add(10)
    for x in xs {
        bump(c, x)
    }
    return c.total
}
"""));
    }

    // Mirrors the corpus authored example exactly: a `List<int>` PARAMETER type plus a
    // `List<int>()` constructor plus `*Counter` plus pointer aliasing.
    [Fact]
    public void ListParamAndCtor_WithHeapPointerAlias_Resolves()
    {
        Assert.Equal(119, Run("""
namespace Test

struct Counter {
    var total: int
    var steps: int
}

func bump(c: *Counter, amount: int) {
    c.total += amount
    c.steps += 1
}

func addAll(c: *Counter, xs: List<int>) -> void {
    for x in xs {
        bump(c, x)
    }
}

func go() -> int {
    var tally: *Counter = new Counter { total: 0, steps: 0 }
    let alias = tally
    let xs = List<int>()
    xs.Add(5)
    xs.Add(7)
    xs.Add(3)
    addAll(tally, xs)
    bump(alias, 100)
    return tally.total + tally.steps
}
"""));
    }
}
