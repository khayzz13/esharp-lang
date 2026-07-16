namespace Esharp.Tests;

/// Span-returning functions (proposal P2) — a `Span<T>`/`ReadOnlySpan<T>` return is
/// well-formed when the span provably derives from a heap array, a receiver field, or a
/// span/array parameter. A span rooted in a stack buffer still trips ES2231.
public sealed class SpanReturnTests
{
    // A slice of a heap-array-backed span returns and round-trips.
    [Fact]
    public void HeapArrayDerivedSlice_ReturnsAndRoundTrips() => Assert.Equal(210, EsHarness.Run("""
namespace Test
using "System"
func firstTwo(xs: int[]) -> ReadOnlySpan<int> {
    let s = ReadOnlySpan<int>(xs)
    return s[0..2]
}
func go() -> int {
    let xs = int[](5)
    for i in 0..5 { xs[i] = i * 10 }
    let r = firstTwo(xs)
    return r.Length * 100 + r[1]
}
""", "go"));

    // A span parameter may be returned (its lifetime is the caller's).
    [Fact]
    public void SpanParameter_MayBeReturned() => Assert.Equal(3, EsHarness.Run("""
namespace Test
using "System"
func pass(s: ReadOnlySpan<int>) -> ReadOnlySpan<int> = s
func go() -> int {
    let xs = int[](3)
    return pass(ReadOnlySpan<int>(xs)).Length
}
""", "go"));

    // A span rooted in a stack buffer escapes the frame — ES2231.
    [Fact]
    public void StackDerivedReturn_IsRejected()
    {
        var diags = EsHarness.Diagnostics("""
namespace Test
using "System"
func bad() -> Span<int> {
    let scratch = stackalloc int[](4)
    return scratch[0..2]
}
""");
        Assert.Contains(diags, d => d.Code == "ES2231");
    }
}
