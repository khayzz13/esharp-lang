namespace Esharp.Syntax;

/// <summary>
/// Source location for a syntax or bound node — a real range, not a point.
/// <see cref="Line"/>/<see cref="Column"/> stay the 1-based start position that
/// diagnostics report against (unchanged); <see cref="Start"/>/<see cref="End"/>
/// are absolute source offsets describing the half-open range <c>[Start, End)</c>
/// the node spans — the currency for navigation, source slicing, and LSP ranges.
/// A node with <c>End == Start</c> is a point (a synthesized or empty node).
/// </summary>
public readonly record struct SourceSpan(string File, int Line, int Column, int Start = 0, int End = 0)
{
    public static SourceSpan None => new("", 0, 0);
    public bool IsValid => Line > 0;
    public int Length => End - Start;

    /// True when this range fully contains <paramref name="offset"/> (end-exclusive,
    /// but an offset exactly at End still counts so a caret at a node's end edge maps
    /// to it). Used by the navigator's position→node lookup.
    public bool Contains(int offset) => offset >= Start && offset <= End;

    /// The smallest range covering both operands. An invalid operand is ignored so a
    /// span-completion union can fold in only the children that actually carry a span.
    public SourceSpan Union(SourceSpan other)
    {
        if (!IsValid) return other;
        if (!other.IsValid) return this;
        var start = Start <= other.Start ? Start : other.Start;
        var end = End >= other.End ? End : other.End;
        // Keep the earlier (Line, Column) so diagnostics still point at the head.
        return Start <= other.Start
            ? this with { Start = start, End = end }
            : other with { Start = start, End = end };
    }
}
