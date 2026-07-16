namespace Esharp.Tests;

/// Byte-string literals `b"…"` (proposal P6) — a `byte[]` of the content's bytes,
/// the E# analogue of C#'s `"…"u8`. Normal characters contribute their UTF-8 bytes;
/// `\xNN` contributes the raw byte NN.
public sealed class LiteralByteStringTests
{
    [Fact]
    public void AsciiMagic_ByteValues() => Assert.Equal(77, EsHarness.Run("""
namespace Test
func go() -> int {
    let m = b"MFL1"
    return int(m[0])
}
""", "go"));

    [Fact]
    public void AsciiMagic_Length() => Assert.Equal(4, EsHarness.Run("""
namespace Test
func go() -> int {
    let m = b"MFLE"
    return m.Length
}
""", "go"));

    [Fact]
    public void LastByte() => Assert.Equal(49, EsHarness.Run("""
namespace Test
func go() -> int {
    let m = b"MFL1"
    return int(m[3])
}
""", "go"));

    // `\xNN` is a raw byte, not a UTF-8 codepoint.
    [Fact]
    public void HexEscape_RawByte() => Assert.Equal(255, EsHarness.Run("""
namespace Test
func go() -> int {
    let m = b"\xFF\x00"
    return int(m[0])
}
""", "go"));

    [Fact]
    public void HexEscape_Length() => Assert.Equal(3, EsHarness.Run("""
namespace Test
func go() -> int {
    let m = b"\x01\x02\x03"
    return m.Length
}
""", "go"));

    // The result is a real byte[] — passes to a `byte[]` parameter and iterates.
    [Fact]
    public void Interop_PassAsByteArray_AndSum() => Assert.Equal(300, EsHarness.Run("""
namespace Test
func sum(b: byte[]) -> int {
    var total = 0
    for x in b { total += int(x) }
    return total
}
func go() -> int = sum(b"\x64\x64\x64")
""", "go"));
}
