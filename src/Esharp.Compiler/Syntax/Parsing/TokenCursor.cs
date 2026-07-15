using System.Text;
using Esharp.Diagnostics;
using Esharp.Syntax;

namespace Esharp.Syntax.Parsing;

/// A position in the token stream, captured for speculative parsing. Restoring a
/// checkpoint rewinds the cursor to exactly where it was taken — the only rewind
/// mechanism the parser uses, replacing ad-hoc `_position` save/restore math.
public readonly record struct Checkpoint(int Position);

/// The single piece of mutable parse state in the system: the token list and the
/// read position over it. Every domain parser shares one cursor; none of them
/// holds its own position. Diagnostics flow through here too, so a domain parser
/// never needs the file path — it reports against the current token.
sealed class TokenCursor
{
    readonly string _filePath;
    readonly List<SyntaxToken> _tokens;
    readonly DiagnosticBag _diagnostics;
    int _position;
    SyntaxToken _lastConsumed;

    public TokenCursor(List<SyntaxToken> tokens, string filePath, DiagnosticBag diagnostics)
    {
        _tokens = tokens;
        _filePath = filePath;
        _diagnostics = diagnostics;
        // Seed the "last consumed" with the first token so SpanFrom is well-defined
        // before the first Next() (a node that consumed nothing reports a point).
        _lastConsumed = tokens.Count > 0 ? tokens[0] : default;
    }

    public SyntaxToken Current => Peek(0);

    /// A point span at the current token — the start anchor a parse method captures
    /// on entry. `Start`/`End` carry the token's absolute offset so `SpanFrom` can
    /// close the range once the node's last token is consumed.
    public SourceSpan SpanHere()
    {
        var t = Current;
        return new(_filePath, t.Line, t.Column, t.Position, t.Position);
    }

    /// Close a range that began at <paramref name="start"/>: keep the head position,
    /// extend `End` to the end of the most recently consumed token. The bulk-stamp
    /// idiom — capture `SpanHere()` on entry, `SpanFrom(start)` on the way out.
    public SourceSpan SpanFrom(SourceSpan start)
    {
        var end = _lastConsumed.End;
        return start with { End = end < start.Start ? start.Start : end };
    }

    /// The exact range of one captured token — the name-token span a declaration
    /// stamps as its `NameSpan` so tooling occurrences land on the identifier.
    public SourceSpan SpanOf(SyntaxToken token) =>
        new(_filePath, token.Line, token.Column, token.Position, token.End);

    /// Same, anchored on a token captured directly (the expression parsers hold the
    /// start token rather than a span).
    public SourceSpan SpanFrom(SyntaxToken start)
    {
        var end = _lastConsumed.End;
        return new SourceSpan(_filePath, start.Line, start.Column, start.Position,
            end < start.Position ? start.Position : end);
    }

    public SyntaxToken Peek(int offset)
    {
        var index = _position + offset;
        return index >= _tokens.Count ? _tokens[^1] : _tokens[index];
    }

    public SyntaxToken Next()
    {
        var current = Current;
        _lastConsumed = current;
        _position = Math.Min(_position + 1, _tokens.Count);
        return current;
    }

    public SyntaxToken Match(SyntaxTokenKind kind, string? message = null)
    {
        if (Current.Kind == kind)
            return Next();

        _diagnostics.Report(
            _filePath,
            Current.Line,
            Current.Column,
            message ?? $"Expected token '{kind}' but found '{Current.Kind}'.");

        return new SyntaxToken(kind, string.Empty, Current.Position, Current.Line, Current.Column);
    }

    public void SkipSeparators()
    {
        while (Current.Kind is SyntaxTokenKind.NewLine or SyntaxTokenKind.Comma)
            Next();
    }

    /// Skip newlines only (not commas) — used right after an expression introducer
    /// (`=` expression body, `=>` arm) where a line break continues the expression onto
    /// the next line: `func f() -> T =⏎    match … { … }`.
    public void SkipNewlines()
    {
        while (Current.Kind is SyntaxTokenKind.NewLine)
            Next();
    }

    /// The full lexed token stream (with attached trivia). Retained so a parse result
    /// can hand it to the printer, whose byte-exact path concatenates every token's
    /// leading trivia + text to reconstruct the source.
    public IReadOnlyList<SyntaxToken> Tokens => _tokens;

    /// Raw read position — used only by the few speculative parsers that compare
    /// positions or stitch token text across a committed run.
    public int Position => _position;

    /// True once the cursor has read past the last real token (its bound for the
    /// speculative loops that scan token-by-token).
    public bool AtEnd => _position >= _tokens.Count;

    /// The token at an absolute index, clamped to the stream bounds.
    public SyntaxToken TokenAt(int index) =>
        index < 0 ? _tokens[0] : index >= _tokens.Count ? _tokens[^1] : _tokens[index];

    /// The concatenated source text of the tokens in [startIndex, endIndex) — how
    /// a speculative type/generic scan recovers the text of a committed token run.
    public string TextBetween(int startIndex, int endIndex)
    {
        var sb = new StringBuilder();
        for (var i = startIndex; i < endIndex; i++) sb.Append(TokenAt(i).Text);
        return sb.ToString();
    }

    public Checkpoint Checkpoint() => new(_position);

    public void Restore(Checkpoint checkpoint) => _position = checkpoint.Position;

    public void Report(int line, int column, string message) =>
        _diagnostics.Report(_filePath, line, column, message);
}
