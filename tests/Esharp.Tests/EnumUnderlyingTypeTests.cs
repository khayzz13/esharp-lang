using System;
using System.Linq;

namespace Esharp.Tests;

/// Enum underlying-type annotation (proposal P4) — `enum C: byte { … }` backs the
/// enum with the named integral primitive instead of the default `int32`.
public sealed class EnumUnderlyingTypeTests
{
    const string Src = """
namespace Test
enum Codec: byte { Raw = 0, Deflate = 1, Snappy = 2 }
func code(c: Codec) -> int = match (c: Codec) {
    .Raw     { 0 }
    .Deflate { 1 }
    .Snappy  { 2 }
    default  { -1 }
}
func go() -> int {
    let c = Codec.Snappy()
    return code(c)
}
""";

    [Fact]
    public void ByteBacked_UnderlyingTypeIsByte()
    {
        var asm = EsHarness.Compile(Src, "EnumByte");
        var codec = asm.GetTypes().Single(t => t.Name == "Codec");
        Assert.True(codec.IsEnum);
        Assert.Equal(typeof(byte), codec.GetEnumUnderlyingType());
    }

    // Case values round-trip through a match regardless of the underlying type.
    [Fact]
    public void CaseValues_RoundTripThroughMatch() => Assert.Equal(2, EsHarness.Run(Src, "go"));

    [Fact]
    public void LongBacked_UnderlyingTypeIsLong()
    {
        var asm = EsHarness.Compile("""
namespace Test
enum Big: long { A = 0, B = 1 }
func go() -> int = 0
""", "EnumLong");
        var big = asm.GetTypes().Single(t => t.Name == "Big");
        Assert.Equal(typeof(long), big.GetEnumUnderlyingType());
    }

    // Default (no annotation) stays int32.
    [Fact]
    public void NoAnnotation_DefaultsToInt()
    {
        var asm = EsHarness.Compile("""
namespace Test
enum Plain { A, B }
func go() -> int = 0
""", "EnumDefault");
        var plain = asm.GetTypes().Single(t => t.Name == "Plain");
        Assert.Equal(typeof(int), plain.GetEnumUnderlyingType());
    }

    // A non-integral underlying type is rejected (ES2127).
    [Fact]
    public void NonIntegralUnderlying_IsRejected()
    {
        var diags = EsHarness.Diagnostics("""
namespace Test
enum Bad: string { A, B }
""");
        Assert.Contains(diags, d => d.Code == "ES2127");
    }
}
