using Esharp.Syntax;

namespace Esharp.Syntax.Lexing;

/// Scans the lossless residue that precedes each token: whitespace runs and the
/// three comment forms. Newlines are NOT trivia — they stay significant tokens the
/// parser uses as separators — so a run stops at `\r`/`\n` and the comment scanners
/// stop before the line break (a block comment is the one form that may span lines,
/// since its own delimiters bound it). Each piece is returned verbatim, so the
/// printer reconstructs the source character for character.
sealed class TriviaScanner : LexUnit
{
    static readonly IReadOnlyList<SyntaxTrivia> None = [];

    public TriviaScanner(Lexer lexer) : base(lexer) { }

    public IReadOnlyList<SyntaxTrivia> ScanLeading()
    {
        List<SyntaxTrivia>? trivia = null;
        while (true)
        {
            SyntaxTrivia piece;
            var c = Current;
            if (char.IsWhiteSpace(c) && c is not '\r' and not '\n')
                piece = ScanWhitespace();
            else if (c == '/' && Peek(1) == '/')
                piece = ScanLineComment();
            else if (c == '/' && Peek(1) == '*')
                piece = ScanBlockComment();
            else
                break;

            (trivia ??= []).Add(piece);
        }
        return trivia ?? None;
    }

    SyntaxTrivia ScanWhitespace()
    {
        var (pos, line, col) = Mark();
        while (char.IsWhiteSpace(Current) && Current is not '\r' and not '\n')
            Advance();
        return new SyntaxTrivia(SyntaxTriviaKind.Whitespace, Cursor.Slice(pos, Cursor.Position), pos, line, col);
    }

    SyntaxTrivia ScanLineComment()
    {
        var (pos, line, col) = Mark();
        // `///` is a doc comment, but `////` (and longer rules) stay ordinary — match
        // exactly three slashes followed by a non-slash, matching Roslyn's rule.
        var isDoc = Peek(2) == '/' && Peek(3) != '/';
        while (Current != '\0' && Current is not '\r' and not '\n')
            Advance();
        var kind = isDoc ? SyntaxTriviaKind.DocComment : SyntaxTriviaKind.LineComment;
        return new SyntaxTrivia(kind, Cursor.Slice(pos, Cursor.Position), pos, line, col);
    }

    SyntaxTrivia ScanBlockComment()
    {
        var (pos, line, col) = Mark();
        Advance(); // /
        Advance(); // *
        while (Current != '\0' && !(Current == '*' && Peek(1) == '/'))
            Advance();
        if (Current == '\0')
            Report(line, col, "Unterminated block comment.");
        else
        {
            Advance(); // *
            Advance(); // /
        }
        return new SyntaxTrivia(SyntaxTriviaKind.BlockComment, Cursor.Slice(pos, Cursor.Position), pos, line, col);
    }

    (int pos, int line, int col) Mark() => (Cursor.Position, Cursor.Line, Cursor.Column);
}
