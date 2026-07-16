using Esharp.Diagnostics;

namespace Esharp.Tests;

/// Hex (`0x`) and binary (`0b`) integer literals (proposal P5). A radix literal
/// decodes to a magnitude and types by value — the same int→long→ulong widening and
/// per-target range check the decimal path uses — so `0xFF`, `255`, and `0b1111_1111`
/// are the one value, and a high-bit literal resolves to `uint`/`long` rather than
/// silently wrapping to a signed −1. `_` group separators are honored; a hex literal
/// carrying an 'e' digit is still an integer, not a float.
public sealed class LiteralHexBinTests
{
    [Fact]
    public void Hex_SmallByte_RoundTrips() => Assert.Equal(255, EsHarness.Run("""
namespace Test
func go() -> int = 0xFF
""", "go"));

    [Fact]
    public void Hex_UppercasePrefixAndDigits() => Assert.Equal(3988292384L, EsHarness.Run("""
namespace Test
func go() -> long = 0XEDB88320
""", "go"));

    [Fact]
    public void Binary_RoundTrips() => Assert.Equal(10, EsHarness.Run("""
namespace Test
func go() -> int = 0b1010
""", "go"));

    [Fact]
    public void Underscore_Grouping_InRadixLiterals() => Assert.Equal(255, EsHarness.Run("""
namespace Test
func go() -> int = 0b1111_1111
""", "go"));

    // A hex digit 'e' is a digit, not an exponent — 0xFACE is an integer.
    [Fact]
    public void HexWithEDigit_IsInteger_NotFloat() => Assert.Equal(64206, EsHarness.Run("""
namespace Test
func go() -> int = 0xFACE
""", "go"));

    // The high-bit crc constant resolves cleanly in a uint context (bit pattern).
    [Fact]
    public void Hex_HighBit_InUintContext() => Assert.Equal(4294967295u, EsHarness.Run("""
namespace Test
func go() -> uint = 0xFFFFFFFF
""", "go"));

    // Byte context accepts a byte-range hex literal.
    [Fact]
    public void Hex_ByteContext() => Assert.Equal((byte)171, EsHarness.Run("""
namespace Test
func go() -> byte = 0xAB
""", "go"));

    // Value-based typing: 0xFFFFFFFF does not fit `int` (it is 4294967295, not −1) — range error.
    [Fact]
    public void Hex_OverflowsSignedTarget_IsError()
    {
        var diags = EsHarness.Diagnostics("""
namespace Test
func go() -> int = 0xFFFFFFFF
""");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Code == "ES2236");
    }

    // A radix literal past 64 bits is out of range.
    [Fact]
    public void Hex_Exceeds64Bits_IsError()
    {
        var diags = EsHarness.Diagnostics("""
namespace Test
func go() -> ulong = 0x1_0000_0000_0000_0000
""");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error);
    }
}
