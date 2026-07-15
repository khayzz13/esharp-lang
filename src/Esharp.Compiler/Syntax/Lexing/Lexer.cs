using Esharp.Diagnostics;
using Esharp.Syntax;

namespace Esharp.Syntax.Lexing;

/// The lexer facade and composition root. It owns the single `LexCursor` and wires
/// the domain scanners (trivia, literals, operators) that produce the token stream.
/// `Lex()` is the only public entry: each `NextToken` first scans the leading
/// trivia (whitespace + comments) and attaches it to the token, so the stream is
/// lossless — the printer reconstructs the source character for character.
///
/// The split mirrors the parser's `TokenCursor` + domain-parser shape: one piece of
/// mutable state, small single-responsibility scanners over it, classification in a
/// pure `LexicalFacts`. Identifier and end-of-file are recognized here; everything
/// else dispatches to a scanner.
public sealed class Lexer
{
    readonly string _filePath;
    readonly DiagnosticBag _diagnostics;

    internal LexCursor Cursor { get; }
    readonly TriviaScanner _trivia;
    readonly LiteralScanner _literals;
    readonly OperatorScanner _operators;

    public Lexer(string source, string filePath, DiagnosticBag diagnostics)
    {
        _filePath = filePath;
        _diagnostics = diagnostics;
        Cursor = new LexCursor(source);
        _trivia = new TriviaScanner(this);
        _literals = new LiteralScanner(this);
        _operators = new OperatorScanner(this);
    }

    internal void Report(int line, int column, string message) =>
        _diagnostics.Report(_filePath, line, column, message);

    public List<SyntaxToken> Lex()
    {
        var tokens = new List<SyntaxToken>();
        SyntaxToken token;
        do
        {
            token = NextToken();
            if (token.Kind != SyntaxTokenKind.BadToken)
                tokens.Add(token);
        } while (token.Kind != SyntaxTokenKind.EndOfFile);

        return tokens;
    }

    SyntaxToken NextToken()
    {
        var leading = _trivia.ScanLeading();
        var token = ScanToken();
        return leading.Count == 0 ? token : token with { LeadingTrivia = leading };
    }

    SyntaxToken ScanToken()
    {
        var pos = Cursor.Position;
        var line = Cursor.Line;
        var col = Cursor.Column;

        if (Cursor.AtEnd)
            return new SyntaxToken(SyntaxTokenKind.EndOfFile, string.Empty, pos, line, col);

        var c = Cursor.Current;
        if (c is '\r' or '\n')
            return _operators.ScanNewLine();
        if (LexicalFacts.IsIdentifierStart(c))
            return ScanIdentifier(pos, line, col);
        if (char.IsDigit(c))
            return _literals.ScanNumber();
        if (c == '"')
            return _literals.ScanString();
        // `$"…"` / `$"""…"""` — an interpolation-prefixed string. The `$` is part of the
        // lexeme; the lowering pass reads it back as the form marker.
        if (c == '$' && Cursor.Peek(1) == '"')
            return _literals.ScanString();
        if (c == '\'')
            return _literals.ScanChar();
        return _operators.ScanOperator();
    }

    SyntaxToken ScanIdentifier(int pos, int line, int col)
    {
        while (LexicalFacts.IsIdentifierContinue(Cursor.Current))
            Cursor.Advance();
        var text = Cursor.Slice(pos, Cursor.Position);
        return new SyntaxToken(LexicalFacts.KeywordOrIdentifier(text), text, pos, line, col);
    }
}
