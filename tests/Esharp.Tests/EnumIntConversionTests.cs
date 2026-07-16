namespace Esharp.Tests;

/// Native conversions across the enum boundary: `int(e)` / `byte(e)` (enum → its
/// underlying integral, via EnumType.UnderlyingPrimitiveName + signedness-aware
/// lowering) and `EnumName(i)` (integer → enum, ConversionKind.IntegerToEnum).
/// Surfaced by ModelFile's `byte(codec)` write path and `ModelFileCodec(codecByte)`
/// read path; neither direction had coverage before.
public sealed class EnumIntConversionTests
{
    // enum → int on a byte-backed enum yields the case's integral value.
    [Fact]
    public void EnumToInt_ByteBacked()
    {
        Assert.Equal(2, EsHarness.Run("""
namespace Test
enum Codec: byte { Raw = 0, Deflate = 1, Snappy = 2 }
func go() -> int {
    let c = Codec.Snappy()
    return int(c)
}
""", "go"));
    }

    // enum → byte, then widened for the int-typed return.
    [Fact]
    public void EnumToByte_ByteBacked()
    {
        Assert.Equal(1, EsHarness.Run("""
namespace Test
enum Codec: byte { Raw = 0, Deflate = 1, Snappy = 2 }
func go() -> int {
    let c = Codec.Deflate()
    return int(byte(c))
}
""", "go"));
    }

    // enum → int on a SIGNED (sbyte) underlying sign-extends a negative case.
    [Fact]
    public void EnumToInt_SignedUnderlying_SignExtends()
    {
        Assert.Equal(-1, EsHarness.Run("""
namespace Test
enum Sign: sbyte { Neg = -1, Zero = 0, Pos = 1 }
func go() -> int {
    let s = Sign.Neg()
    return int(s)
}
""", "go"));
    }

    // int → enum: `Codec(1)` constructs the enum from an integer, distinct from the
    // case-construction spelling `Codec.Deflate()`. Matched to confirm the case.
    [Fact]
    public void IntToEnum_ByteBacked_MatchesCase()
    {
        Assert.Equal(1, EsHarness.Run("""
namespace Test
enum Codec: byte { Raw = 0, Deflate = 1, Snappy = 2 }
func go() -> int {
    let c = Codec(1)
    return match (c: Codec) {
        .Raw     { 0 }
        .Deflate { 1 }
        .Snappy  { 2 }
        default  { -1 }
    }
}
""", "go"));
    }

    // int → enum on a default (int-backed) enum.
    [Fact]
    public void IntToEnum_DefaultIntBacked()
    {
        Assert.Equal(2, EsHarness.Run("""
namespace Test
enum Plain { A, B, C }
func go() -> int {
    let p = Plain(2)
    return match (p: Plain) {
        .A { 0 }
        .B { 1 }
        .C { 2 }
        default { -1 }
    }
}
""", "go"));
    }

    // enum → int → enum round-trips to the original case (the ModelFile save/load path).
    [Fact]
    public void RoundTrip_EnumToIntToEnum()
    {
        Assert.Equal(2, EsHarness.Run("""
namespace Test
enum Codec: byte { Raw = 0, Deflate = 1, Snappy = 2 }
func go() -> int {
    let c = Codec.Snappy()
    let raw = byte(c)
    let back = Codec(raw)
    return match (back: Codec) {
        .Raw     { 0 }
        .Deflate { 1 }
        .Snappy  { 2 }
        default  { -1 }
    }
}
""", "go"));
    }
}
