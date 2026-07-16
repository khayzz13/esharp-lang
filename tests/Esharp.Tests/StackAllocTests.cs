using System.Linq;
using Mono.Cecil.Cil;

namespace Esharp.Tests;

/// Stack-allocated spans (proposal P1) — `stackalloc T[](n)` yields a frame-local
/// `Span<T>` (or `ReadOnlySpan<T>` by target typing), lowered to `localloc` + a
/// `new Span<T>(void*, int)`. Never a `*T`, never a heap `newarr`.
public sealed class StackAllocTests
{
    // A stackalloc int buffer is a real, writable, indexable span.
    [Fact]
    public void IntBuffer_WriteAndReadBack() => Assert.Equal(9, EsHarness.Run("""
namespace Test
using "System"
func go() -> int {
    let buf = stackalloc int[](4)
    for i in 0..4 { buf[i] = i * i }
    return buf[3]
}
""", "go"));

    // A stackalloc byte scratch is a real `Span<byte>` the BCL accepts by ref — the
    // zero-alloc codec scratch-buffer use-case. BinaryPrimitives writes into the frame
    // buffer directly (no heap array), and the span reports its allocated length.
    [Fact]
    public void ByteScratch_AcceptedByBinaryPrimitives() => Assert.Equal(8, EsHarness.Run("""
namespace Test
using "System"
using "System.Buffers.Binary"
func go() -> int {
    let scratch = stackalloc byte[](8)
    BinaryPrimitives.WriteInt64LittleEndian(scratch, 42)
    return scratch.Length
}
""", "go"));

    // The IL uses `localloc` and does NOT allocate a heap array (`newarr`).
    [Fact]
    public void EmitsLocalloc_NotNewarr()
    {
        var (asm, diags) = EsHarness.EmitCecil("""
namespace Test
using "System"
func go() -> int {
    let buf = stackalloc int[](4)
    return buf.Length
}
""", "StackAllocIl");
        Assert.DoesNotContain(diags, d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
        var go = asm.MainModule.Types.SelectMany(t => t.Methods).Single(m => m.Name == "go");
        var ops = go.Body.Instructions.Select(i => i.OpCode).ToList();
        Assert.Contains(OpCodes.Localloc, ops);
        Assert.DoesNotContain(OpCodes.Newarr, ops);
    }

    // An escaping stackalloc span (returned from the frame) is rejected — ES2231.
    [Fact]
    public void EscapingStackAlloc_IsRejected()
    {
        var diags = EsHarness.Diagnostics("""
namespace Test
using "System"
func bad() -> Span<int> {
    let s = stackalloc int[](4)
    return s
}
""");
        Assert.Contains(diags, d => d.Code == "ES2231");
    }
}
