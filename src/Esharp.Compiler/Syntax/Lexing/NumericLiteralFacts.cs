using System.Globalization;

namespace Esharp.Syntax.Lexing;

/// Decoding facts for a numeric literal lexeme, shared by the parser (untyped
/// inference in <c>ParseNumber</c>) and the binder (contextual typed decode in
/// <c>BindNumericLiteral</c>). It owns the two things a raw lexeme needs before a
/// value can be produced: telling an integer from a fractional literal, and turning
/// a <c>0x</c>/<c>0b</c> integer into a magnitude.
///
/// Radix literals decode to a non-negative <c>ulong</c> magnitude and then type by
/// **value** — the same int→long→ulong widening and per-target range check the
/// decimal path already uses — so <c>0xFF</c>, <c>255</c>, and <c>0b1111_1111</c>
/// are the one value in the one place, and a high-bit literal like <c>0xFFFFFFFF</c>
/// resolves to <c>uint</c>/<c>long</c> rather than silently wrapping to a signed −1.
/// The decimal path is intentionally left to the existing <c>T.TryParse</c> calls.
public static class NumericLiteralFacts
{
    /// A radix-prefixed integer (<c>0x</c>/<c>0b</c>) is never fractional; a decimal
    /// literal is fractional iff it carries a '.', 'e', or 'E'. The prefix gate is
    /// load-bearing: hex digits include 'e' (<c>0xFace</c>), so an ungated check
    /// would misread a hex integer as a float.
    public static bool IsFractional(string raw) =>
        !HasRadixPrefix(raw)
        && (raw.IndexOf('.') >= 0 || raw.IndexOf('e') >= 0 || raw.IndexOf('E') >= 0);

    /// True when the lexeme opens with a <c>0x</c>/<c>0X</c> (hex) or <c>0b</c>/<c>0B</c>
    /// (binary) radix prefix — i.e. it must be decoded through <see cref="TryDecodeRadixInteger"/>
    /// rather than the decimal <c>T.TryParse</c> path.
    public static bool IsRadixInteger(string raw) => HasRadixPrefix(raw);

    static bool HasRadixPrefix(string raw) =>
        raw.Length >= 2 && raw[0] == '0' && raw[1] is 'x' or 'X' or 'b' or 'B';

    /// Decode a <c>0x</c>/<c>0b</c> integer lexeme (digit separators allowed) to its
    /// non-negative magnitude. Returns false when there are no digits, a digit is
    /// invalid for the radix, or the magnitude exceeds 64 bits — each of which the
    /// caller surfaces as an out-of-range diagnostic.
    public static bool TryDecodeRadixInteger(string raw, out ulong magnitude)
    {
        magnitude = 0;
        var t = raw.Replace("_", "", StringComparison.Ordinal);
        if (t.Length <= 2) return false;
        var radix = t[1] is 'x' or 'X' ? 16 : 2;
        var digits = t.AsSpan(2);
        if (digits.Length == 0) return false;

        // Overflow guard: cap the digit count at the radix's 64-bit width (16 hex,
        // 64 binary), then shift-and-add so a bad digit fails without throwing.
        var maxDigits = radix == 16 ? 16 : 64;
        if (digits.Length > maxDigits) return false;
        foreach (var c in digits)
        {
            var d = HexValue(c);
            if (d < 0 || d >= radix) return false;
            magnitude = radix == 16 ? (magnitude << 4) | (uint)d : (magnitude << 1) | (uint)d;
        }
        return true;
    }

    /// The E# untyped-integer inference order applied to a decoded magnitude: the
    /// smallest of int → long → ulong whose range holds the value. Mirrors the
    /// decimal literal's widening so a radix literal infers the same type its decimal
    /// twin would.
    public static string InferIntegerType(ulong magnitude) =>
        magnitude <= int.MaxValue ? "int" : magnitude <= long.MaxValue ? "long" : "ulong";

    /// Box a decoded magnitude into <paramref name="targetPrimitive"/>, or null when
    /// the value does not fit that type's range (the caller reports out-of-range).
    /// Covers every E# numeric primitive; the integral targets range-check by value,
    /// and the real targets carry the magnitude through as their nearest value.
    public static object? FitMagnitude(ulong magnitude, string targetPrimitive) => targetPrimitive switch
    {
        "byte" => magnitude <= byte.MaxValue ? (byte)magnitude : null,
        "sbyte" => magnitude <= (ulong)sbyte.MaxValue ? (sbyte)magnitude : null,
        "short" => magnitude <= (ulong)short.MaxValue ? (short)magnitude : null,
        "ushort" => magnitude <= ushort.MaxValue ? (ushort)magnitude : null,
        "char" => magnitude <= ushort.MaxValue ? (char)magnitude : null,
        "int" => magnitude <= int.MaxValue ? (int)magnitude : null,
        "uint" => magnitude <= uint.MaxValue ? (uint)magnitude : null,
        "long" => magnitude <= long.MaxValue ? (long)magnitude : null,
        "ulong" => magnitude,
        "nint" => magnitude <= long.MaxValue ? (nint)(long)magnitude : null,
        "nuint" => (nuint)magnitude,
        "float" => (float)magnitude,
        "double" => (double)magnitude,
        "decimal" => (decimal)magnitude,
        _ => null,
    };

    static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
