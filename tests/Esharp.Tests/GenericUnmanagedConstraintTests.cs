using System;
using System.Linq;
using System.Reflection;

namespace Esharp.Tests;

/// The `unmanaged` generic bound (proposal P3) — `<T: unmanaged>` emits the CLR
/// unmanaged constraint, which is what lets `MemoryMarshal.AsBytes<T>` and span
/// reinterpretation type-check against `T`.
public sealed class GenericUnmanagedConstraintTests
{
    // A `<T: unmanaged>` generic function type-checks and runs end to end. `Unsafe.SizeOf<T>`
    // is the canonical unmanaged-only operation; `int` is 4 bytes.
    [Fact]
    public void UnmanagedBound_GenericFunctionRuns() => Assert.Equal(4, EsHarness.Run("""
namespace Test
using "System.Runtime.CompilerServices"
func size<T: unmanaged>() -> int = Unsafe.SizeOf<T>()
func go() -> int = size<int>()
""", "go"));

    // The emitted generic parameter carries the CLR value-type constraint flag.
    [Fact]
    public void UnmanagedBound_EmitsValueTypeConstraint()
    {
        var asm = EsHarness.Compile("""
namespace Test
using "System"
pub func writeArray<T: unmanaged>(values: ReadOnlySpan<T>) -> int = values.Length
""", "UnmanagedConstraint");
        var host = asm.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            .Single(m => m.Name == "writeArray");
        var tp = host.GetGenericArguments().Single();
        Assert.True(tp.GenericParameterAttributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint),
            "T should carry the value-type (unmanaged) constraint");
    }
}
