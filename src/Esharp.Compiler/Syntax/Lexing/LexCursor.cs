namespace Esharp.Syntax.Lexing;

/// The single piece of mutable lexing state: the source buffer and the read
/// position over it, with line/column tracking. Every scanner (trivia, literals,
/// operators) shares one cursor; none holds its own position — the mirror of the
/// parser's `TokenCursor`. A `'\0'` is returned past end so scanners can peek
/// without bounds checks.
sealed class LexCursor
{
    readonly string _source;
    int _position;
    int _line = 1;
    int _column = 1;

    public LexCursor(string source) => _source = source;

    public int Position => _position;
    public int Line => _line;
    public int Column => _column;
    public bool AtEnd => _position >= _source.Length;
    public char Current => Peek(0);

    public char Peek(int offset)
    {
        var index = _position + offset;
        return index >= _source.Length ? '\0' : _source[index];
    }

    public void Advance()
    {
        if (_position >= _source.Length)
            return;

        if (_source[_position] == '\n')
        {
            _position++;
            _line++;
            _column = 1;
            return;
        }

        _position++;
        _column++;
    }

    /// The verbatim source between two absolute offsets — how a scanner recovers a
    /// token/trivia's exact text (including original whitespace and line endings).
    public string Slice(int start, int end) => _source[start..end];
}
