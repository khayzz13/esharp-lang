using Esharp.Syntax;

namespace Esharp.Syntax.Lexing;

/// Newline tokens (significant — the parser's separators) and the operator /
/// punctuation table. A newline captures its exact text (`\n` or `\r\n`) so the
/// printer round-trips CRLF sources; the operator switch consumes the longest
/// matching symbol. An unrecognized character is a reported `BadToken`.
sealed class OperatorScanner : LexUnit
{
    public OperatorScanner(Lexer lexer) : base(lexer) { }

    public SyntaxToken ScanNewLine()
    {
        var pos = Cursor.Position;
        var line = Cursor.Line;
        var col = Cursor.Column;
        if (Current == '\r' && Peek(1) == '\n')
            Advance();
        Advance();
        // Capture the verbatim line ending (`\n` or `\r\n`) rather than a synthesized
        // "\n", so a CRLF source reconstructs byte-for-byte.
        return new SyntaxToken(SyntaxTokenKind.NewLine, Cursor.Slice(pos, Cursor.Position), pos, line, col);
    }

    public SyntaxToken ScanOperator()
    {
        var pos = Cursor.Position;
        var line = Cursor.Line;
        var col = Cursor.Column;

        SyntaxToken Tok(SyntaxTokenKind kind, string text) => new(kind, text, pos, line, col);

        switch (Current)
        {
            case '{': Advance(); return Tok(SyntaxTokenKind.OpenBrace, "{");
            case '}': Advance(); return Tok(SyntaxTokenKind.CloseBrace, "}");
            case '(': Advance(); return Tok(SyntaxTokenKind.OpenParen, "(");
            case ')': Advance(); return Tok(SyntaxTokenKind.CloseParen, ")");
            case '[': Advance(); return Tok(SyntaxTokenKind.OpenBracket, "[");
            case ']': Advance(); return Tok(SyntaxTokenKind.CloseBracket, "]");
            case ':': Advance(); return Tok(SyntaxTokenKind.Colon, ":");
            case ',': Advance(); return Tok(SyntaxTokenKind.Comma, ",");
            case '.':
                Advance();
                if (Current == '.') { Advance(); return Tok(SyntaxTokenKind.DotDot, ".."); }
                return Tok(SyntaxTokenKind.Dot, ".");
            case '?':
                Advance();
                if (Current == '?') { Advance(); return Tok(SyntaxTokenKind.QuestionQuestion, "??"); }
                if (Current == '.') { Advance(); return Tok(SyntaxTokenKind.QuestionDot, "?."); }
                return Tok(SyntaxTokenKind.Question, "?");
            case '^':
                Advance();
                if (Current == '=') { Advance(); return Tok(SyntaxTokenKind.CaretEquals, "^="); }
                return Tok(SyntaxTokenKind.Caret, "^");
            case '~': Advance(); return Tok(SyntaxTokenKind.Tilde, "~");
            case '@': Advance(); return Tok(SyntaxTokenKind.At, "@");
            case '+':
                Advance();
                if (Current == '=') { Advance(); return Tok(SyntaxTokenKind.PlusEquals, "+="); }
                return Tok(SyntaxTokenKind.Plus, "+");
            case '*':
                Advance();
                if (Current == '=') { Advance(); return Tok(SyntaxTokenKind.StarEquals, "*="); }
                return Tok(SyntaxTokenKind.Star, "*");
            case '/':
                Advance();
                if (Current == '=') { Advance(); return Tok(SyntaxTokenKind.SlashEquals, "/="); }
                return Tok(SyntaxTokenKind.Slash, "/");
            case '%':
                Advance();
                if (Current == '=') { Advance(); return Tok(SyntaxTokenKind.PercentEquals, "%="); }
                return Tok(SyntaxTokenKind.Percent, "%");
            case '!':
                Advance();
                if (Current == '=') { Advance(); return Tok(SyntaxTokenKind.BangEquals, "!="); }
                return Tok(SyntaxTokenKind.Bang, "!");
            case '=':
                Advance();
                if (Current == '>') { Advance(); return Tok(SyntaxTokenKind.FatArrow, "=>"); }
                if (Current == '=') { Advance(); return Tok(SyntaxTokenKind.EqualsEquals, "=="); }
                return Tok(SyntaxTokenKind.Equals, "=");
            case '-':
                Advance();
                if (Current == '>') { Advance(); return Tok(SyntaxTokenKind.Arrow, "->"); }
                if (Current == '=') { Advance(); return Tok(SyntaxTokenKind.MinusEquals, "-="); }
                return Tok(SyntaxTokenKind.Minus, "-");
            case '<':
                Advance();
                if (Current == '<')
                {
                    Advance();
                    if (Current == '=') { Advance(); return Tok(SyntaxTokenKind.ShiftLeftEquals, "<<="); }
                    return Tok(SyntaxTokenKind.ShiftLeft, "<<");
                }
                if (Current == '=') { Advance(); return Tok(SyntaxTokenKind.LessEquals, "<="); }
                return Tok(SyntaxTokenKind.Less, "<");
            case '>':
                Advance();
                if (Current == '>')
                {
                    Advance();
                    if (Current == '>')
                    {
                        Advance();
                        if (Current == '=') { Advance(); return Tok(SyntaxTokenKind.UnsignedShiftRightEquals, ">>>="); }
                        return Tok(SyntaxTokenKind.UnsignedShiftRight, ">>>");
                    }
                    if (Current == '=') { Advance(); return Tok(SyntaxTokenKind.ShiftRightEquals, ">>="); }
                    return Tok(SyntaxTokenKind.ShiftRight, ">>");
                }
                if (Current == '=') { Advance(); return Tok(SyntaxTokenKind.GreaterEquals, ">="); }
                return Tok(SyntaxTokenKind.Greater, ">");
            case '&':
                Advance();
                if (Current == '&') { Advance(); return Tok(SyntaxTokenKind.AmpAmp, "&&"); }
                if (Current == '=') { Advance(); return Tok(SyntaxTokenKind.AmpersandEquals, "&="); }
                return Tok(SyntaxTokenKind.Ampersand, "&");
            case '|':
                Advance();
                if (Current == '|') { Advance(); return Tok(SyntaxTokenKind.PipePipe, "||"); }
                if (Current == '=') { Advance(); return Tok(SyntaxTokenKind.PipeEquals, "|="); }
                return Tok(SyntaxTokenKind.Pipe, "|");
        }

        Report(line, col, $"Unexpected character '{Current}'.");
        Advance();
        return Tok(SyntaxTokenKind.BadToken, Cursor.Slice(pos, Cursor.Position));
    }
}
