using Esharp.Syntax;

namespace Esharp.Syntax.Lexing;

/// Number, string, and character literals. The string scanner is the lexer's most
/// intricate region — it is interpolation-aware, tracking brace depth so a `"` or
/// `'` inside a `{ … }` hole does not end the host string. The raw text is captured
/// verbatim; decoding (escapes, hole splitting) happens later in the parser/binder.
sealed class LiteralScanner : LexUnit
{
    public LiteralScanner(Lexer lexer) : base(lexer) { }

    public SyntaxToken ScanNumber()
    {
        var pos = Cursor.Position;
        var line = Cursor.Line;
        var col = Cursor.Column;

        // Radix-prefixed integer: `0x…` hex or `0b…` binary, with `_` group separators.
        // The whole lexeme (prefix included) is captured verbatim; the parser/binder
        // decode it through NumericLiteralFacts. No fraction/exponent scan follows —
        // a radix literal is integer-only.
        if (Current == '0' && (Peek(1) is 'x' or 'X' or 'b' or 'B'))
        {
            var isHex = Peek(1) is 'x' or 'X';
            Advance(); // 0
            Advance(); // x / b
            while (IsRadixDigit(Current, isHex) || (Current == '_' && IsRadixDigit(Peek(1), isHex)))
                Advance();
            return new SyntaxToken(SyntaxTokenKind.NumberLiteral, Cursor.Slice(pos, Cursor.Position), pos, line, col);
        }

        // Digit-group separators: `_` is allowed between digits (1_000_000).
        while (char.IsDigit(Current) || (Current == '_' && char.IsDigit(Peek(1))))
            Advance();

        if (Current == '.' && char.IsDigit(Peek(1)))
        {
            Advance();
            while (char.IsDigit(Current) || (Current == '_' && char.IsDigit(Peek(1))))
                Advance();
        }

        // Exponent: (e|E)(+|-)?digit+ — e.g. 1.0e10, 6.022e23, 2E8, 1.5e-3.
        // A leading digit must follow the optional sign, otherwise the `e` is
        // not part of the number (left for the identifier scanner / an error).
        if ((Current == 'e' || Current == 'E')
            && (char.IsDigit(Peek(1))
                || ((Peek(1) == '+' || Peek(1) == '-') && char.IsDigit(Peek(2)))))
        {
            Advance();
            if (Current == '+' || Current == '-')
                Advance();
            while (char.IsDigit(Current) || (Current == '_' && char.IsDigit(Peek(1))))
                Advance();
        }

        return new SyntaxToken(SyntaxTokenKind.NumberLiteral, Cursor.Slice(pos, Cursor.Position), pos, line, col);
    }

    static bool IsRadixDigit(char c, bool isHex) =>
        isHex ? Uri.IsHexDigit(c) : c is '0' or '1';

    /// `b"…"` — a byte-string literal. A plain (non-interpolated, non-raw) quoted run
    /// with `\` escapes, captured verbatim (prefix included); the parser decodes the
    /// bytes. Interpolation is deliberately not a concern here — a byte literal is a
    /// fixed blob.
    public SyntaxToken ScanByteString()
    {
        var pos = Cursor.Position;
        var line = Cursor.Line;
        var col = Cursor.Column;

        Advance(); // b
        Advance(); // opening "
        while (Current != '\0' && Current != '"')
        {
            if (Current == '\\' && Peek(1) != '\0') { Advance(); Advance(); continue; }
            Advance();
        }
        if (Current != '"')
            Report(line, col, "Unterminated byte-string literal.");
        else
            Advance(); // closing "

        return new SyntaxToken(SyntaxTokenKind.ByteStringLiteral, Cursor.Slice(pos, Cursor.Position), pos, line, col);
    }

    public SyntaxToken ScanString()
    {
        var pos = Cursor.Position;
        var line = Cursor.Line;
        var col = Cursor.Column;

        // An optional `$` prefix marks an interpolated string; it stays in the lexeme
        // so the lowering pass recovers the form. (Dispatch only routes `$"` here.)
        if (Current == '$') Advance();

        // A run of three or more `"` opens a raw string literal: verbatim content that
        // may contain lone `"`s and newlines, closed by a fence of the SAME width.
        if (Current == '"' && Peek(1) == '"' && Peek(2) == '"')
            return ScanRawString(pos, line, col);

        Advance();

        // Interpolation-aware string scan. The closing quote is the one at brace
        // depth 0; a `"` (or `'`) that appears *inside* a `{ … }` hole opens a
        // nested literal whose own quotes and braces don't count toward the host
        // string. `{{` / `}}` at depth 0 are escaped braces, not hole delimiters.
        var braceDepth = 0;
        while (Current != '\0')
        {
            if (Current == '\\' && Peek(1) != '\0')
            {
                Advance();
                Advance();
                continue;
            }

            if (braceDepth == 0)
            {
                if (Current == '"') break;                                     // real terminator
                if (Current == '{' && Peek(1) == '{') { Advance(); Advance(); continue; }  // `{{`
                if (Current == '}' && Peek(1) == '}') { Advance(); Advance(); continue; }  // `}}`
                // A `{` opens a hole only when the next char can start an expression
                // (identifier / `(` / `!`) — kept in lockstep with the binder's
                // IsInterpolationStart (Binder.Expressions.cs). Otherwise it is a
                // literal brace, so `"{"`, `"{}"`, and BCL format strings like `"{0}"`
                // / `"{0:d}"` stay literal instead of swallowing the closing quote.
                if (Current == '{' && LexicalFacts.IsInterpolationStart(Peek(1))) { braceDepth++; Advance(); continue; }  // enter hole
                Advance();
                continue;
            }

            // Inside a hole: track nested braces and skip nested string/char literals.
            if (Current == '{') { braceDepth++; Advance(); continue; }
            if (Current == '}') { braceDepth--; Advance(); continue; }
            if (Current is '"' or '\'')
            {
                var quote = Current;
                Advance();
                while (Current != '\0' && Current != quote)
                {
                    if (Current == '\\' && Peek(1) != '\0') Advance();
                    Advance();
                }
                if (Current == quote) Advance();
                continue;
            }
            Advance();
        }

        if (Current != '"')
        {
            Report(line, col, "Unterminated string literal.");
            return new SyntaxToken(SyntaxTokenKind.StringLiteral, Cursor.Slice(pos, Cursor.Position), pos, line, col);
        }

        Advance();
        return new SyntaxToken(SyntaxTokenKind.StringLiteral, Cursor.Slice(pos, Cursor.Position), pos, line, col);
    }

    // Raw string: `"""` (or a wider fence) … closing fence of the same width. Content is
    // verbatim — backslashes, lone `"`s, and newlines are all literal — so JSON and
    // regex bodies need no escaping. A run shorter than the fence is literal quotes; the
    // first run that meets the fence width closes the literal. The lowering pass handles
    // multi-line indentation stripping and (for the `$` form) interpolation.
    SyntaxToken ScanRawString(int pos, int line, int col)
    {
        var fence = 0;
        while (Current == '"') { fence++; Advance(); } // consume the opening fence

        while (Current != '\0')
        {
            if (Current == '"')
            {
                var run = 0;
                while (Peek(run) == '"') run++;
                if (run >= fence)
                {
                    for (var k = 0; k < fence; k++) Advance(); // consume the closing fence
                    return new SyntaxToken(SyntaxTokenKind.StringLiteral, Cursor.Slice(pos, Cursor.Position), pos, line, col);
                }
                for (var k = 0; k < run; k++) Advance(); // shorter run — literal quotes
                continue;
            }
            Advance();
        }

        Report(line, col, "Unterminated raw string literal.");
        return new SyntaxToken(SyntaxTokenKind.StringLiteral, Cursor.Slice(pos, Cursor.Position), pos, line, col);
    }

    public SyntaxToken ScanChar()
    {
        var pos = Cursor.Position;
        var line = Cursor.Line;
        var col = Cursor.Column;
        Advance();

        // Scan up to the closing quote, honoring \-escapes. Empty `''` is rejected
        // by the parser; everything else (including multi-codepoint escapes like
        // \u#### or surrogate pairs) is captured verbatim and decoded later.
        while (Current != '\0' && Current != '\'' && Current != '\n')
        {
            if (Current == '\\' && Peek(1) != '\0')
                Advance();
            Advance();
        }

        if (Current != '\'')
        {
            Report(line, col, "Unterminated character literal.");
            return new SyntaxToken(SyntaxTokenKind.CharLiteral, Cursor.Slice(pos, Cursor.Position), pos, line, col);
        }

        Advance();
        return new SyntaxToken(SyntaxTokenKind.CharLiteral, Cursor.Slice(pos, Cursor.Position), pos, line, col);
    }
}
