namespace Esharp.Tests;

/// Signedness-aware operator lowering — the CLR splits `>> / % <>` etc. into signed
/// and unsigned opcodes that are NOT interchangeable. Shifts are also asymmetric (a
/// value shifted by an int count), independent of the value's type. These verify the
/// emitted opcode by its runtime result, not just that it compiles.
public sealed class OperatorSignednessTests
{
    // `uint >> n` must be LOGICAL (shr.un): the top bit does not sign-extend.
    // Arithmetic `shr` would give 0xFF800000.
    [Fact]
    public void UintShiftRight_IsLogical() => Assert.Equal(0x00800000u, EsHarness.Run("""
namespace Test
func go() -> uint {
    var c: uint = 0x80000000
    return c >> 8
}
""", "go"));

    // `int >> n` stays ARITHMETIC (shr): sign-extends. -8 >> 1 == -4.
    [Fact]
    public void IntShiftRight_IsArithmetic() => Assert.Equal(-4, EsHarness.Run("""
namespace Test
func go() -> int = (-8) >> 1
""", "go"));

    // `>>>` is LOGICAL regardless of operand sign: (-1) >>> 28 == 15.
    [Fact]
    public void UnsignedShiftRight_OnSignedOperand_IsLogical() => Assert.Equal(15, EsHarness.Run("""
namespace Test
func go() -> int = (-1) >>> 28
""", "go"));

    // A shift count is an int, never peer-bound to the shifted value's type — a bare
    // literal count and an int-variable count both work on a uint value.
    [Fact]
    public void ShiftCount_IsInt_LiteralAndVariable() => Assert.Equal(0x0F000000u, EsHarness.Run("""
namespace Test
func go() -> uint {
    var c: uint = 0xF0000000
    let n = 4
    return c >> n
}
""", "go"));

    // `uint / uint` must be UNSIGNED div: 0xFFFFFFFF / 2 == 0x7FFFFFFF (signed div would
    // treat the dividend as -1 and yield 0).
    [Fact]
    public void UintDivision_IsUnsigned() => Assert.Equal(0x7FFFFFFFu, EsHarness.Run("""
namespace Test
func go() -> uint {
    var a: uint = 0xFFFFFFFF
    var b: uint = 2
    return a / b
}
""", "go"));

    // `uint < uint` must be an UNSIGNED compare: 0xFFFFFFFF < 1 is false (signed -1 < 1
    // would be true).
    [Fact]
    public void UintComparison_IsUnsigned() => Assert.False((bool)EsHarness.Run("""
namespace Test
func go() -> bool {
    var a: uint = 0xFFFFFFFF
    var b: uint = 1
    return a < b
}
""", "go")!);

    // Compound `>>=` on a uint is also logical.
    [Fact]
    public void UintCompoundShiftRight_IsLogical() => Assert.Equal(0x00800000u, EsHarness.Run("""
namespace Test
func go() -> uint {
    var c: uint = 0x80000000
    c >>= 8
    return c
}
""", "go"));

    // The CRC32 inner step — the exact `uint` shift/xor/mask pattern the dogfood needs.
    [Fact]
    public void Crc32InnerStep_RoundTrips() => Assert.Equal(0x92477CDFu, EsHarness.Run("""
namespace Test
func step(c: uint) -> uint {
    if (c & 1) != 0 { return 0xEDB88320 ^ (c >> 1) }
    return c >> 1
}
func go() -> uint = step(0xFFFFFFFF)
""", "go"));
}
