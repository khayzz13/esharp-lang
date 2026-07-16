namespace Esharp.Tests;

/// Open-ended range endpoints for slicing (proposal P12) — `xs[..k]`, `xs[k..]`,
/// `xs[..]`, composing with the `^k` index-from-end operator. An omitted endpoint is
/// the start/end of the collection.
public sealed class RangeSliceTests
{
    const string Fill = """
namespace Test
func fill() -> int[] {
    let xs = int[](5)
    for i in 0..5 { xs[i] = i * 10 }        // [0,10,20,30,40]
    return xs
}
""";

    [Fact]
    public void OpenStart_TakesPrefix() => Assert.Equal(2, EsHarness.Run(Fill + """
func go() -> int = fill()[..2].Length
""", "go"));

    [Fact]
    public void OpenEnd_TakesSuffix() => Assert.Equal(3, EsHarness.Run(Fill + """
func go() -> int = fill()[2..].Length
""", "go"));

    [Fact]
    public void OpenStart_FirstElement() => Assert.Equal(0, EsHarness.Run(Fill + """
func go() -> int = fill()[..2][0]
""", "go"));

    [Fact]
    public void OpenEnd_FirstElement() => Assert.Equal(20, EsHarness.Run(Fill + """
func go() -> int = fill()[2..][0]
""", "go"));

    [Fact]
    public void FullRange_CopiesAll() => Assert.Equal(5, EsHarness.Run(Fill + """
func go() -> int = fill()[..].Length
""", "go"));

    [Fact]
    public void Range_WithIndexFromEnd() => Assert.Equal(3, EsHarness.Run(Fill + """
func go() -> int = fill()[1..^1].Length
""", "go"));

    // A byte[] slice (the reader use-case).
    [Fact]
    public void ByteArray_OpenEndSlice() => Assert.Equal(2, EsHarness.Run("""
namespace Test
func go() -> int {
    let m = b"MFL1"
    return m[2..].Length
}
""", "go"));

    // Span slicing lowers a range to `Slice(int, int)` against the receiver's own
    // length — spans have no `this[Range]` indexer. This is the stackalloc-scratch and
    // zero-copy-reader use-case.
    [Fact]
    public void Span_RangeSlice_Length() => Assert.Equal(3, EsHarness.Run("""
namespace Test
using "System"
func go() -> int {
    let xs = int[](5)
    for i in 0..5 { xs[i] = i }
    let s = ReadOnlySpan<int>(xs)
    return s[1..4].Length
}
""", "go"));

    // The slice is a real window onto the source: element 0 of `s[2..]` is `xs[2]`.
    [Fact]
    public void Span_OpenEnd_FirstElement() => Assert.Equal(2, EsHarness.Run("""
namespace Test
using "System"
func go() -> int {
    let xs = int[](5)
    for i in 0..5 { xs[i] = i }
    let s = ReadOnlySpan<int>(xs)
    return s[2..][0]
}
""", "go"));

    // Index-from-end endpoint resolves against the span's length.
    [Fact]
    public void Span_RangeWithIndexFromEnd() => Assert.Equal(3, EsHarness.Run("""
namespace Test
using "System"
func go() -> int {
    let xs = int[](5)
    for i in 0..5 { xs[i] = i }
    let s = ReadOnlySpan<int>(xs)
    return s[1..^1].Length
}
""", "go"));
}
