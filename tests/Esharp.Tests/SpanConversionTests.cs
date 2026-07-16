using System.Linq;
using Mono.Cecil.Cil;

namespace Esharp.Tests;

/// Implicit span conversions — the CLR's own `op_Implicit`, emitted at call-argument
/// and store/return sites so a `stackalloc` span or a heap array flows into the many
/// `ReadOnlySpan<T>`-taking BCL APIs:
///   T[]      → Span<T> / ReadOnlySpan<T>
///   Span<T>  → ReadOnlySpan<T>
/// This is the seam the ModelFile codec dogfood needs on every writer/reader path.
public sealed class SpanConversionTests
{
    // Span<byte> → ReadOnlySpan<byte>: a stackalloc scratch written by BinaryPrimitives,
    // then handed to MemoryStream.Write(ReadOnlySpan<byte>) and read back. The core
    // writer pattern.
    [Fact]
    public void SpanToReadOnlySpan_ThroughMemoryStream_RoundTrips() => Assert.Equal(0x2A, EsHarness.Run("""
namespace Test
using "System"
using "System.IO"
using "System.Buffers.Binary"
func go() -> int {
    let buf = MemoryStream()
    let b = stackalloc byte[](4)
    BinaryPrimitives.WriteInt32LittleEndian(b, 0x2A)
    buf.Write(b)                                // Span<byte> -> ReadOnlySpan<byte>
    let bytes = buf.ToArray()
    return BinaryPrimitives.ReadInt32LittleEndian(bytes)   // byte[] -> ReadOnlySpan<byte>
}
""", "go"));

    // byte[] → ReadOnlySpan<byte>: a heap array flowing into a ReadOnlySpan<byte> param,
    // the Crc32.Compute(ReadOnlySpan<byte>) shape.
    [Fact]
    public void ByteArrayToReadOnlySpan_AtCallArg() => Assert.Equal(6, EsHarness.Run("""
namespace Test
using "System"
func sum(data: ReadOnlySpan<byte>) -> int {
    var t = 0
    for i in 0..data.Length { t += int(data[i]) }
    return t
}
func go() -> int {
    let xs = byte[](3)
    xs[0] = 1  xs[1] = 2  xs[2] = 3
    return sum(xs)                              // byte[] -> ReadOnlySpan<byte>
}
""", "go"));

    // T[] (non-byte element) → ReadOnlySpan<T>: the bulk numeric-array path,
    // MemoryMarshal.AsBytes over a double[] handed as ReadOnlySpan<double>.
    [Fact]
    public void DoubleArrayToReadOnlySpan_BulkBytes() => Assert.Equal(24, EsHarness.Run("""
namespace Test
using "System"
using "System.Runtime.InteropServices"
func byteLen(v: ReadOnlySpan<double>) -> int = MemoryMarshal.AsBytes(v).Length
func go() -> int {
    let xs = double[](3)
    return byteLen(xs)                          // double[] -> ReadOnlySpan<double>; 3*8 = 24
}
""", "go"));

    // Return-site coercion: a function whose return type is ReadOnlySpan<int> returning
    // a heap-array parameter directly — the Coerce seam inserts the op_Implicit, and the
    // array root makes the span returnable (P2 lifetime).
    [Fact]
    public void ArrayToReadOnlySpan_AtReturn() => Assert.Equal(30, EsHarness.Run("""
namespace Test
using "System"
func view(xs: int[]) -> ReadOnlySpan<int> {
    return xs                                   // int[] -> ReadOnlySpan<int> at return
}
func go() -> int {
    let xs = int[](3)
    xs[0] = 5  xs[1] = 10  xs[2] = 15
    let s = view(xs)
    return s[0] + s[1] + s[2]
}
""", "go"));

    // The emitted IL uses the framework op_Implicit call, not a cast/box.
    [Fact]
    public void EmitsOpImplicit_NotCastclass()
    {
        var (asm, diags) = EsHarness.EmitCecil("""
namespace Test
using "System"
using "System.IO"
func go(buf: MemoryStream) {
    let b = stackalloc byte[](4)
    buf.Write(b)
}
""", "SpanConvIl");
        Assert.DoesNotContain(diags, d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
        var go = asm.MainModule.Types.SelectMany(t => t.Methods).Single(m => m.Name == "go");
        var calls = go.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call && i.Operand is Mono.Cecil.MethodReference)
            .Select(i => ((Mono.Cecil.MethodReference)i.Operand).Name)
            .ToList();
        Assert.Contains("op_Implicit", calls);
        Assert.DoesNotContain(go.Body.Instructions.Select(i => i.OpCode), o => o == OpCodes.Castclass);
    }
}
