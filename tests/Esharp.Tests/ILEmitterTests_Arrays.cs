using Xunit;

namespace Esharp.Tests;

/// WS3 — array types: `T[]` type, `T[](n)` sized creation, indexing `a[i]`, and `.Length`.
/// `List<T>` stays the dynamic workhorse; `T[]` is the fixed CLR array (newarr) for
/// interop/perf and the `select`-lowering target (`Arm[]`).
public sealed class ILEmitterTests_Arrays
{
    static object? Run(string body, string method) => EsHarness.Run(body, method);

    // Sized creation + indexed write + `.Length` + indexed read.
    [Fact]
    public void IntArray_Create_Index_Length() => Assert.Equal(42, Run("""
namespace Test
func go() -> int {
    var xs = int[](3)
    xs[0] = 10
    xs[1] = 20
    xs[2] = 12
    var total = 0
    for i in 0..xs.Length { total += xs[i] }
    return total
}
""", "go"));

    // `T[]` as a parameter type — a real CLR array crosses the call boundary.
    [Fact]
    public void IntArray_AsParameter() => Assert.Equal(7, Run("""
namespace Test
func first(xs: int[]) -> int = xs[0]
func go() -> int {
    var xs = int[](2)
    xs[0] = 7
    return first(xs)
}
""", "go"));

    // Array of a user struct — element type is reified, fields reachable through the index.
    [Fact]
    public void StructArray_ElementFieldAccess() => Assert.Equal(30, Run("""
namespace Test
struct P { x: int  y: int }
func go() -> int {
    var ps = P[](2)
    ps[0] = P { x: 10, y: 20 }
    return ps[0].x + ps[0].y
}
""", "go"));
}
