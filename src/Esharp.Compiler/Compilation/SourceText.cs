namespace Esharp.Compilation;

// Immutable view of a document's source. Hash is computed once on construction
// so equality / cache keys never re-walk the string. FNV-1a 64 is deterministic
// across runs (unlike string.GetHashCode) and good enough for "did this document
// change" — collision resistance isn't a concern because the hash is paired
// with the DocumentId, not used in isolation.
public sealed record SourceText
{
    public string Content { get; }
    public ulong ContentHash { get; }

    public SourceText(string content)
    {
        Content = content;
        ContentHash = Fnv1a64(content);
    }

    public static implicit operator SourceText(string s) => new(s);

    public override string ToString() => Content;

    // The offset of the first character of each line (line 0 starts at 0; line N+1
    // starts one past line N's '\n'). Built once, lazily — every position query and
    // every LSP request maps through it, so it is O(log n) per lookup after the
    // O(n) build. `\r\n` is handled by keying off '\n' alone; the '\r' stays in the
    // preceding line, matching how spans count raw offsets.
    int[]? _lineStarts;
    int[] LineStarts => _lineStarts ??= ComputeLineStarts(Content);

    static int[] ComputeLineStarts(string s)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < s.Length; i++)
            if (s[i] == '\n') starts.Add(i + 1);
        return starts.ToArray();
    }

    /// Map a 0-based character offset to its 1-based (line, column). A position past
    /// the end clamps to the last line. The inverse of <see cref="GetOffset"/>.
    public (int Line, int Column) GetLineColumn(int offset)
    {
        if (offset < 0) offset = 0;
        if (offset > Content.Length) offset = Content.Length;
        var starts = LineStarts;
        // Binary search for the greatest line start <= offset.
        var lo = 0;
        var hi = starts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (starts[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        return (lo + 1, offset - starts[lo] + 1);
    }

    /// Map a 1-based (line, column) to a 0-based character offset. Out-of-range
    /// lines/columns clamp into the document. The inverse of <see cref="GetLineColumn"/>.
    public int GetOffset(int line, int column)
    {
        var starts = LineStarts;
        var li = Math.Clamp(line - 1, 0, starts.Length - 1);
        var lineStart = starts[li];
        var lineEnd = li + 1 < starts.Length ? starts[li + 1] - 1 : Content.Length;
        return Math.Clamp(lineStart + (column - 1), lineStart, lineEnd);
    }

    static ulong Fnv1a64(string s)
    {
        const ulong fnvOffset = 14695981039346656037;
        const ulong fnvPrime  = 1099511628211;
        var hash = fnvOffset;
        foreach (var c in s)
        {
            hash ^= (byte)(c & 0xff);
            hash *= fnvPrime;
            hash ^= (byte)((c >> 8) & 0xff);
            hash *= fnvPrime;
        }
        return hash;
    }
}
