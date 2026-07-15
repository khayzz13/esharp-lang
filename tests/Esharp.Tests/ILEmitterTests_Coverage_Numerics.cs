// Style note: E# source uses readable """ raw-string blocks (these double as the
// E# corpus) — do not collapse them into inline \n-escaped one-liners.
namespace Esharp.Tests;

/// Primitive numeric types, arithmetic, comparison, and boolean-logic coverage.
/// Each type is exercised through a typed function so the test pins the actual
/// CLR primitive rather than relying on int-literal promotion.
public sealed class ILEmitterTests_Coverage_Numerics
{
    static object? Run(string src, string method, params object?[] args)
        => EsHarness.Run(src, method, args);

    // ── int arithmetic ──

    [Fact]
    public void Int_Add()
    {
        Assert.Equal(7, Run("""
namespace Test
func add(a: int, b: int) -> int { return a + b }
""", "add", 3, 4));
    }

    [Fact]
    public void Int_Sub_Mul()
    {
        Assert.Equal(54, Run("""
namespace Test
func go() -> int {
    let a = 10 - 4
    return a * 9
}
""", "go"));
    }

    [Fact]
    public void Int_TruncatingDivision()
    {
        Assert.Equal(3, Run("""
namespace Test
func go() -> int { return 7 / 2 }
""", "go"));
    }

    [Fact]
    public void Int_Modulo()
    {
        Assert.Equal(1, Run("""
namespace Test
func go() -> int { return 7 % 3 }
""", "go"));
    }

    [Fact]
    public void Int_NegativeModulo_FollowsClr()
    {
        Assert.Equal(-1, Run("""
namespace Test
func go() -> int { return -7 % 3 }
""", "go"));
    }

    [Fact]
    public void Int_UnaryMinus()
    {
        Assert.Equal(-42, Run("""
namespace Test
func go() -> int {
    let x = 42
    return 0 - x
}
""", "go"));
    }

    [Fact]
    public void Int_NegativeLiteral()
    {
        Assert.Equal(-5, Run("""
namespace Test
func go() -> int { return -5 }
""", "go"));
    }

    [Fact]
    public void Int_UnderscoreSeparators()
    {
        Assert.Equal(1_000_000, Run("""
namespace Test
func go() -> int { return 1_000_000 }
""", "go"));
    }

    [Fact]
    public void Int_PrecedenceMulOverAdd()
    {
        Assert.Equal(14, Run("""
namespace Test
func go() -> int { return 2 + 3 * 4 }
""", "go"));
    }

    [Fact]
    public void Int_ParenthesizedPrecedence()
    {
        Assert.Equal(20, Run("""
namespace Test
func go() -> int { return (2 + 3) * 4 }
""", "go"));
    }

    [Fact]
    public void Int_CompoundAssignAll()
    {
        Assert.Equal(13, Run("""
namespace Test
func go() -> int {
    var n = 10
    n += 5
    n -= 2
    n *= 2
    n /= 2
    return n
}
""", "go"));
    }

    // ── long / ulong / uint ──

    [Fact]
    public void Long_Add()
    {
        Assert.Equal(7_000_000_000L, Run("""
namespace Test
func add(a: long, b: long) -> long { return a + b }
""", "add", 3_000_000_000L, 4_000_000_000L));
    }

    [Fact]
    public void Long_Multiply()
    {
        Assert.Equal(6_000_000_000L, Run("""
namespace Test
func mul(a: long, b: long) -> long { return a * b }
""", "mul", 2_000_000_000L, 3L));
    }

    [Fact]
    public void Uint_Add()
    {
        Assert.Equal(7u, Run("""
namespace Test
func add(a: uint, b: uint) -> uint { return a + b }
""", "add", 3u, 4u));
    }

    [Fact]
    public void Ulong_Add()
    {
        Assert.Equal(7ul, Run("""
namespace Test
func add(a: ulong, b: ulong) -> ulong { return a + b }
""", "add", 3ul, 4ul));
    }

    // ── short / byte / sbyte ──

    [Fact]
    public void Short_Add()
    {
        Assert.Equal((short)7, Run("""
namespace Test
func add(a: short, b: short) -> short { return a + b }
""", "add", (short)3, (short)4));
    }

    [Fact]
    public void Byte_Identity()
    {
        Assert.Equal((byte)200, Run("""
namespace Test
func echo(b: byte) -> byte { return b }
""", "echo", (byte)200));
    }

    [Fact]
    public void Sbyte_Negative()
    {
        Assert.Equal((sbyte)-5, Run("""
namespace Test
func echo(b: sbyte) -> sbyte { return b }
""", "echo", (sbyte)-5));
    }

    // ── float / double ──

    [Fact]
    public void Double_Add()
    {
        Assert.Equal(4.5, (double)Run("""
namespace Test
func go() -> double { return 3.0 + 1.5 }
""", "go")!, 9);
    }

    [Fact]
    public void Double_Division()
    {
        Assert.Equal(2.5, (double)Run("""
namespace Test
func go() -> double { return 5.0 / 2.0 }
""", "go")!, 9);
    }

    [Fact]
    public void Double_ScientificNotation()
    {
        Assert.Equal(1.0e10, (double)Run("""
namespace Test
func go() -> double { return 1.0e10 }
""", "go")!, 0);
    }

    [Fact]
    public void Float_Add()
    {
        Assert.Equal(3.5f, (float)Run("""
namespace Test
func add(a: float, b: float) -> float { return a + b }
""", "add", 1.5f, 2.0f)!, 4);
    }

    [Fact]
    public void Double_CompoundAssign()
    {
        Assert.Equal(10.0, (double)Run("""
namespace Test
func go() -> double {
    var x = 4.0
    x *= 2.5
    return x
}
""", "go")!, 9);
    }

    // ── bool / comparison / logical ──

    [Fact]
    public void Bool_AndOrNot_Symbolic()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool { return (true && false) || !false }
""", "go"));
    }

    [Fact]
    public void Bool_AndOrNot_Keyword()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool { return (true and false) or not false }
""", "go"));
    }

    [Fact]
    public void Comparison_Operators()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool {
    return 3 < 4 && 4 <= 4 && 5 > 4 && 5 >= 5 && 3 == 3 && 3 != 4
}
""", "go"));
    }

    [Fact]
    public void ShortCircuit_And_DoesNotEvaluateRight()
    {
        // If && short-circuited correctly, the divide-by-zero never runs.
        Assert.Equal(false, Run("""
namespace Test
func go() -> bool {
    let d = 0
    return d != 0 && (10 / d) > 0
}
""", "go"));
    }

    [Fact]
    public void ShortCircuit_Or_DoesNotEvaluateRight()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool {
    let d = 0
    return d == 0 || (10 / d) > 0
}
""", "go"));
    }

    // ── char ──

    [Fact]
    public void Char_Equality()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool {
    let c = 'x'
    return c == 'x'
}
""", "go"));
    }

    [Fact]
    public void Char_IsDigit_BclInterop()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool {
    return char.IsDigit('7')
}
""", "go"));
    }

    [Fact]
    public void Char_FromStringIndex()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool {
    let s = "hello"
    return s[1] == 'e'
}
""", "go"));
    }

    // ── int: edge cases, associativity, rounding ──

    [Fact]
    public void Int_LeftAssociativeSubtraction()
    {
        Assert.Equal(-6, Run("""
namespace Test
func go() -> int { return 1 - 2 - 5 }
""", "go"));
        // (1 - 2) - 5 = -6, not 1 - (2 - 5) = 4
    }

    [Fact]
    public void Int_LeftAssociativeDivision()
    {
        Assert.Equal(4, Run("""
namespace Test
func go() -> int { return 100 / 5 / 5 }
""", "go"));
        // (100 / 5) / 5 = 4
    }

    [Fact]
    public void Int_TruncationTowardZero_Negative()
    {
        Assert.Equal(-3, Run("""
namespace Test
func go() -> int { return -7 / 2 }
""", "go"));
        // C# integer division truncates toward zero: -3, not -4
    }

    [Fact]
    public void Int_PowerOfTwoChain()
    {
        Assert.Equal(1024, Run("""
namespace Test
func go() -> int {
    var n = 1
    for i in 0..10 {
        n *= 2
    }
    return n
}
""", "go"));
    }

    [Fact]
    public void Int_OverflowWrapsUnchecked()
    {
        Assert.Equal(int.MinValue, Run("""
namespace Test
func go() -> int { return int.MaxValue + 1 }
""", "go"));
    }

    [Fact]
    public void Int_MaxValueConstant()
    {
        Assert.Equal(int.MaxValue, Run("""
namespace Test
func go() -> int { return int.MaxValue }
""", "go"));
    }

    [Fact]
    public void Int_MinValueConstant()
    {
        Assert.Equal(int.MinValue, Run("""
namespace Test
func go() -> int { return int.MinValue }
""", "go"));
    }

    [Fact]
    public void Int_DoubleNegation()
    {
        Assert.Equal(5, Run("""
namespace Test
func go() -> int {
    let x = 5
    return 0 - (0 - x)
}
""", "go"));
    }

    [Fact]
    public void Int_ModuloZeroRemainder()
    {
        Assert.Equal(0, Run("""
namespace Test
func go() -> int { return 12 % 4 }
""", "go"));
    }

    [Fact]
    public void Int_ChainedCompoundOnParam()
    {
        Assert.Equal(45, Run("""
namespace Test
func scale(start: int, factor: int) -> int {
    var n = start
    n *= factor
    n += factor
    return n
}
""", "scale", 8, 5));
        // 8*5 = 40, then +5 = 45
    }

    [Fact]
    public void Int_MixedArithmeticExpression()
    {
        Assert.Equal(17, Run("""
namespace Test
func go() -> int { return 2 * 3 + 4 * 2 + 3 }
""", "go"));
        // 6 + 8 + 3 = 17
    }

    // ── long edge cases ──

    [Fact]
    public void Long_MaxValueConstant()
    {
        Assert.Equal(long.MaxValue, Run("""
namespace Test
func go() -> long { return long.MaxValue }
""", "go"));
    }

    [Fact]
    public void Long_LargeProductExceedsIntRange()
    {
        Assert.Equal(10_000_000_000L, Run("""
namespace Test
func mul(a: long, b: long) -> long { return a * b }
""", "mul", 100_000L, 100_000L));
    }

    [Fact]
    public void Long_Subtraction()
    {
        Assert.Equal(1_000_000_000L, Run("""
namespace Test
func sub(a: long, b: long) -> long { return a - b }
""", "sub", 5_000_000_000L, 4_000_000_000L));
    }

    [Fact]
    public void Long_Modulo()
    {
        Assert.Equal(2L, Run("""
namespace Test
func mod(a: long, b: long) -> long { return a % b }
""", "mod", 17L, 5L));
    }

    // ── uint / ulong edge cases ──

    [Fact]
    public void Uint_MaxValueConstant()
    {
        Assert.Equal(uint.MaxValue, Run("""
namespace Test
func go() -> uint { return uint.MaxValue }
""", "go"));
    }

    [Fact]
    public void Uint_Multiply()
    {
        Assert.Equal(12u, Run("""
namespace Test
func mul(a: uint, b: uint) -> uint { return a * b }
""", "mul", 3u, 4u));
    }

    [Fact]
    public void Ulong_LargeValue()
    {
        Assert.Equal(18_000_000_000_000_000_000UL, Run("""
namespace Test
func echo(n: ulong) -> ulong { return n }
""", "echo", 18_000_000_000_000_000_000UL));
    }

    // ── short / byte / sbyte arithmetic ──

    [Fact]
    public void Short_Subtraction()
    {
        Assert.Equal((short)100, Run("""
namespace Test
func sub(a: short, b: short) -> short { return a - b }
""", "sub", (short)300, (short)200));
    }

    [Fact]
    public void Byte_MaxValueConstant()
    {
        Assert.Equal((byte)255, Run("""
namespace Test
func go() -> byte { return byte.MaxValue }
""", "go"));
    }

    [Fact]
    public void Sbyte_Arithmetic()
    {
        Assert.Equal((sbyte)1, Run("""
namespace Test
func add(a: sbyte, b: sbyte) -> sbyte { return a + b }
""", "add", (sbyte)-5, (sbyte)6));
    }

    // ── float / double: Math interop ──

    [Fact]
    public void Double_MathSqrt()
    {
        Assert.Equal(4.0, (double)Run("""
namespace Test
func go() -> double { return Math.Sqrt(16.0) }
""", "go")!, 9);
    }

    [Fact]
    public void Double_MathAbs()
    {
        Assert.Equal(3.5, (double)Run("""
namespace Test
func go() -> double { return Math.Abs(0.0 - 3.5) }
""", "go")!, 9);
    }

    [Fact]
    public void Double_MathPow()
    {
        Assert.Equal(8.0, (double)Run("""
namespace Test
func go() -> double { return Math.Pow(2.0, 3.0) }
""", "go")!, 9);
    }

    [Fact]
    public void Double_MathFloor()
    {
        Assert.Equal(3.0, (double)Run("""
namespace Test
func go() -> double { return Math.Floor(3.9) }
""", "go")!, 9);
    }

    [Fact]
    public void Double_MathCeiling()
    {
        Assert.Equal(4.0, (double)Run("""
namespace Test
func go() -> double { return Math.Ceiling(3.1) }
""", "go")!, 9);
    }

    [Fact]
    public void Int_MathMax()
    {
        Assert.Equal(7, Run("""
namespace Test
func go() -> int { return Math.Max(3, 7) }
""", "go"));
    }

    [Fact]
    public void Int_MathMin()
    {
        Assert.Equal(3, Run("""
namespace Test
func go() -> int { return Math.Min(3, 7) }
""", "go"));
    }

    [Fact]
    public void Double_UsingStaticMath()
    {
        Assert.Equal(5.0, (double)Run("""
namespace Test
using static "System.Math"
func go() -> double { return Sqrt(25.0) }
""", "go")!, 9);
    }

    [Fact]
    public void Double_NegativeAndComparison()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool {
    let a = -2.5
    let b = 1.5
    return a < b && b > a
}
""", "go"));
    }

    [Fact]
    public void Double_SubtractionPrecision()
    {
        Assert.Equal(0.5, (double)Run("""
namespace Test
func go() -> double { return 2.0 - 1.5 }
""", "go")!, 9);
    }

    [Fact]
    public void Float_Multiply()
    {
        Assert.Equal(6.25f, (float)Run("""
namespace Test
func mul(a: float, b: float) -> float { return a * b }
""", "mul", 2.5f, 2.5f)!, 4);
    }

    // ── parsing / conversions (BCL interop) ──

    [Fact]
    public void Int_Parse()
    {
        Assert.Equal(123, Run("""
namespace Test
func go() -> int { return int.Parse("123") }
""", "go"));
    }

    [Fact]
    public void Int_TryParse_Success()
    {
        Assert.Equal(42, Run("""
namespace Test
func go() -> int {
    if int.TryParse("42", out var n) {
        return n
    }
    return -1
}
""", "go"));
    }

    [Fact]
    public void Int_TryParse_Failure()
    {
        Assert.Equal(-1, Run("""
namespace Test
func go() -> int {
    if int.TryParse("notnum", out var n) {
        return n
    }
    return -1
}
""", "go"));
    }

    [Fact]
    public void Long_Parse()
    {
        Assert.Equal(9_000_000_000L, Run("""
namespace Test
func go() -> long { return long.Parse("9000000000") }
""", "go"));
    }

    [Fact]
    public void Double_Parse()
    {
        Assert.Equal(3.14, (double)Run("""
namespace Test
using "System.Globalization"
func go() -> double { return double.Parse("3.14", CultureInfo.InvariantCulture) }
""", "go")!, 6);
    }

    [Fact]
    public void Int_ToStringInterpolation()
    {
        Assert.Equal("n=42", Run("""
namespace Test
func go() -> string {
    let n = 42
    return "n={n}"
}
""", "go"));
    }

    // ── char classification ──

    [Fact]
    public void Char_IsLetter()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool { return char.IsLetter('q') }
""", "go"));
    }

    [Fact]
    public void Char_IsWhiteSpace()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool { return char.IsWhiteSpace(' ') }
""", "go"));
    }

    [Fact]
    public void Char_Ordering()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool { return 'a' < 'b' && 'z' > 'a' }
""", "go"));
    }

    [Fact]
    public void Char_ToUpper()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool { return char.ToUpper('a') == 'A' }
""", "go"));
    }

    [Fact]
    public void Char_EscapeSequences()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool {
    let tab = '\t'
    let nl = '\n'
    return tab != nl
}
""", "go"));
    }

    // ── bool algebra ──

    [Fact]
    public void Bool_DeMorgan()
    {
        Assert.Equal(true, Run("""
namespace Test
func check(a: bool, b: bool) -> bool {
    return !(a && b) == (!a || !b)
}
func go() -> bool {
    return check(true, false) && check(true, true) && check(false, false)
}
""", "go"));
    }

    [Fact]
    public void Bool_XorViaNotEqual()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool {
    return (true != false) && !(true != true)
}
""", "go"));
    }

    [Fact]
    public void Bool_NestedParentheses()
    {
        Assert.Equal(false, Run("""
namespace Test
func go() -> bool {
    return (true && (false || (true && false)))
}
""", "go"));
    }

    [Fact]
    public void Bool_FromComparisonChainStored()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool {
    let n = 5
    let inRange = n >= 1 && n <= 10
    return inRange
}
""", "go"));
    }

    // ── numerics inside data / pointers ──

    [Fact]
    public void Int_FieldArithmetic()
    {
        Assert.Equal(30, Run("""
namespace Test
struct Vec { x: int, y: int }
func go() -> int {
    let v = Vec { x: 10, y: 20 }
    return v.x + v.y
}
""", "go"));
    }

    [Fact]
    public void Double_FieldMagnitudeSquared()
    {
        Assert.Equal(25.0, (double)Run("""
namespace Test
struct Vec { x: double, y: double }
func go() -> double {
    let v = Vec { x: 3.0, y: 4.0 }
    return v.x * v.x + v.y * v.y
}
""", "go")!, 6);
    }

    [Fact]
    public void Int_MutateThroughPointer()
    {
        Assert.Equal(15, Run("""
namespace Test
func addTo(target: *int, amount: int) {
    target += amount
}
func go() -> int {
    var n = 10
    addTo(&n, 5)
    return n
}
""", "go"));
    }

    [Fact]
    public void Int_AccumulateAcrossCalls()
    {
        Assert.Equal(6, Run("""
namespace Test
func bump(c: *int) {
    c += 1
}
func go() -> int {
    var count = 3
    bump(&count)
    bump(&count)
    bump(&count)
    return count
}
""", "go"));
    }

    [Fact]
    public void Long_FieldRoundTrip()
    {
        Assert.Equal(5_000_000_000L, Run("""
namespace Test
struct Timestamp { epochMs: long }
func go() -> long {
    let t = Timestamp { epochMs: 5000000000 }
    return t.epochMs
}
""", "go"));
    }

    [Fact]
    public void Byte_FieldRoundTrip()
    {
        Assert.Equal((byte)128, Run("""
namespace Test
struct Pixel { r: byte, g: byte, b: byte }
func go() -> byte {
    let p = Pixel { r: 128, g: 64, b: 32 }
    return p.r
}
""", "go"));
    }

    // ── numeric helpers / small algorithms ──

    [Fact]
    public void Algo_GcdEuclid()
    {
        Assert.Equal(6, Run("""
namespace Test
func gcd(a: int, b: int) -> int {
    var x = a
    var y = b
    while y != 0 {
        let t = y
        y = x % y
        x = t
    }
    return x
}
func go() -> int { return gcd(48, 18) }
""", "go"));
    }

    [Fact]
    public void Algo_FibonacciIterative()
    {
        Assert.Equal(55, Run("""
namespace Test
func fib(n: int) -> int {
    var a = 0
    var b = 1
    for i in 0..n {
        let t = a + b
        a = b
        b = t
    }
    return a
}
func go() -> int { return fib(10) }
""", "go"));
    }

    [Fact]
    public void Algo_IsPrime()
    {
        Assert.Equal(true, Run("""
namespace Test
func isPrime(n: int) -> bool {
    if n < 2 { return false }
    var d = 2
    while d * d <= n {
        if n % d == 0 { return false }
        d += 1
    }
    return true
}
func go() -> bool { return isPrime(97) }
""", "go"));
    }

    [Fact]
    public void Algo_SumOfDigits()
    {
        Assert.Equal(15, Run("""
namespace Test
func digitSum(n: int) -> int {
    var x = n
    var sum = 0
    while x > 0 {
        sum += x % 10
        x /= 10
    }
    return sum
}
func go() -> int { return digitSum(12345) }
""", "go"));
    }

    [Fact]
    public void Algo_CountSetViaModulo()
    {
        Assert.Equal(3, Run("""
namespace Test
func countMultiples(limit: int, of: int) -> int {
    var count = 0
    for i in 1..limit {
        if i % of == 0 { count += 1 }
    }
    return count
}
func go() -> int { return countMultiples(10, 3) }
""", "go"));
        // 3, 6, 9 in 1..10
    }

    [Fact]
    public void Algo_PowerByMultiplication()
    {
        Assert.Equal(243, Run("""
namespace Test
func ipow(b: int, exp: int) -> int {
    var result = 1
    for i in 0..exp {
        result *= b
    }
    return result
}
func go() -> int { return ipow(3, 5) }
""", "go"));
    }
}
