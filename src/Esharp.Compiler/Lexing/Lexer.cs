using Esharp.Compiler.Diagnostics;
using Esharp.Compiler.Syntax;

namespace Esharp.Compiler.Lexing;

public sealed class Lexer
{
    static readonly Dictionary<string, SyntaxTokenKind> Keywords = new(StringComparer.Ordinal)
    {
        ["module"] = SyntaxTokenKind.ModuleKeyword,
        ["data"] = SyntaxTokenKind.DataKeyword,
        ["choice"] = SyntaxTokenKind.ChoiceKeyword,
        ["func"] = SyntaxTokenKind.FuncKeyword,
        ["let"] = SyntaxTokenKind.LetKeyword,
        ["var"] = SyntaxTokenKind.VarKeyword,
        ["if"] = SyntaxTokenKind.IfKeyword,
        ["else"] = SyntaxTokenKind.ElseKeyword,
        ["return"] = SyntaxTokenKind.ReturnKeyword,
        ["while"] = SyntaxTokenKind.WhileKeyword,
        ["for"] = SyntaxTokenKind.ForKeyword,
        ["in"] = SyntaxTokenKind.InKeyword,
        ["spawn"] = SyntaxTokenKind.SpawnKeyword,
        ["true"] = SyntaxTokenKind.TrueKeyword,
        ["false"] = SyntaxTokenKind.FalseKeyword,
        ["nil"] = SyntaxTokenKind.NilKeyword,
        ["match"] = SyntaxTokenKind.MatchKeyword,
        ["default"] = SyntaxTokenKind.DefaultKeyword,
        ["not"] = SyntaxTokenKind.NotKeyword,
        ["and"] = SyntaxTokenKind.AndKeyword,
        ["or"] = SyntaxTokenKind.OrKeyword,
        ["enum"] = SyntaxTokenKind.EnumKeyword,
        ["chan"] = SyntaxTokenKind.ChanKeyword,
        ["defer"] = SyntaxTokenKind.DeferKeyword,
        ["import"] = SyntaxTokenKind.ImportKeyword,
        ["protocol"] = SyntaxTokenKind.ProtocolKeyword,
        ["derive"] = SyntaxTokenKind.DeriveKeyword,
        ["await"] = SyntaxTokenKind.AwaitKeyword,
        ["select"] = SyntaxTokenKind.SelectKeyword,
    };

    readonly string _source;
    readonly string _filePath;
    readonly DiagnosticBag _diagnostics;
    int _position;
    int _line = 1;
    int _column = 1;

    public Lexer(string source, string filePath, DiagnosticBag diagnostics)
    {
        _source = source;
        _filePath = filePath;
        _diagnostics = diagnostics;
    }

    char Current => PeekChar(0);

    char PeekChar(int offset)
    {
        var index = _position + offset;
        return index >= _source.Length ? '\0' : _source[index];
    }

    void Advance()
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
        while (char.IsWhiteSpace(Current) && Current is not '\r' and not '\n')
            Advance();

        var position = _position;
        var line = _line;
        var column = _column;

        if (_position >= _source.Length)
            return new SyntaxToken(SyntaxTokenKind.EndOfFile, string.Empty, position, line, column);

        if (Current == '\r' || Current == '\n')
        {
            if (Current == '\r' && PeekChar(1) == '\n')
                Advance();

            Advance();
            return new SyntaxToken(SyntaxTokenKind.NewLine, "\n", position, line, column);
        }

        if (Current == '/' && PeekChar(1) == '/')
        {
            while (Current != '\0' && Current is not '\r' and not '\n')
                Advance();

            return NextToken();
        }

        if (char.IsLetter(Current) || Current == '_')
        {
            while (char.IsLetterOrDigit(Current) || Current == '_')
                Advance();

            var text = _source[position.._position];
            var kind = Keywords.GetValueOrDefault(text, SyntaxTokenKind.Identifier);
            return new SyntaxToken(kind, text, position, line, column);
        }

        if (char.IsDigit(Current))
        {
            while (char.IsDigit(Current))
                Advance();

            if (Current == '.' && char.IsDigit(PeekChar(1)))
            {
                Advance();
                while (char.IsDigit(Current))
                    Advance();
            }

            return new SyntaxToken(SyntaxTokenKind.NumberLiteral, _source[position.._position], position, line, column);
        }

        if (Current == '"')
        {
            Advance();

            while (Current != '\0' && Current != '"')
            {
                if (Current == '\\' && PeekChar(1) != '\0')
                    Advance();

                Advance();
            }

            if (Current != '"')
            {
                _diagnostics.Report(_filePath, line, column, "Unterminated string literal.");
                return new SyntaxToken(SyntaxTokenKind.StringLiteral, _source[position.._position], position, line, column);
            }

            Advance();
            return new SyntaxToken(SyntaxTokenKind.StringLiteral, _source[position.._position], position, line, column);
        }

        switch (Current)
        {
            case '{': Advance(); return new SyntaxToken(SyntaxTokenKind.OpenBrace, "{", position, line, column);
            case '}': Advance(); return new SyntaxToken(SyntaxTokenKind.CloseBrace, "}", position, line, column);
            case '(': Advance(); return new SyntaxToken(SyntaxTokenKind.OpenParen, "(", position, line, column);
            case ')': Advance(); return new SyntaxToken(SyntaxTokenKind.CloseParen, ")", position, line, column);
            case '[': Advance(); return new SyntaxToken(SyntaxTokenKind.OpenBracket, "[", position, line, column);
            case ']': Advance(); return new SyntaxToken(SyntaxTokenKind.CloseBracket, "]", position, line, column);
            case ':': Advance(); return new SyntaxToken(SyntaxTokenKind.Colon, ":", position, line, column);
            case ',': Advance(); return new SyntaxToken(SyntaxTokenKind.Comma, ",", position, line, column);
            case '.':
                Advance();
                if (Current == '.') { Advance(); return new SyntaxToken(SyntaxTokenKind.DotDot, "..", position, line, column); }
                return new SyntaxToken(SyntaxTokenKind.Dot, ".", position, line, column);
            case '?': Advance(); return new SyntaxToken(SyntaxTokenKind.Question, "?", position, line, column);
            case '^': Advance(); return new SyntaxToken(SyntaxTokenKind.Caret, "^", position, line, column);
            case '+':
                Advance();
                if (Current == '=') { Advance(); return new SyntaxToken(SyntaxTokenKind.PlusEquals, "+=", position, line, column); }
                return new SyntaxToken(SyntaxTokenKind.Plus, "+", position, line, column);
            case '*':
                Advance();
                if (Current == '=') { Advance(); return new SyntaxToken(SyntaxTokenKind.StarEquals, "*=", position, line, column); }
                return new SyntaxToken(SyntaxTokenKind.Star, "*", position, line, column);
            case '/':
                Advance();
                if (Current == '=') { Advance(); return new SyntaxToken(SyntaxTokenKind.SlashEquals, "/=", position, line, column); }
                return new SyntaxToken(SyntaxTokenKind.Slash, "/", position, line, column);
            case '%': Advance(); return new SyntaxToken(SyntaxTokenKind.Percent, "%", position, line, column);
            case '!':
                Advance();
                if (Current == '=')
                {
                    Advance();
                    return new SyntaxToken(SyntaxTokenKind.BangEquals, "!=", position, line, column);
                }
                return new SyntaxToken(SyntaxTokenKind.Bang, "!", position, line, column);
            case '=':
                Advance();
                if (Current == '=')
                {
                    Advance();
                    return new SyntaxToken(SyntaxTokenKind.EqualsEquals, "==", position, line, column);
                }
                return new SyntaxToken(SyntaxTokenKind.Equals, "=", position, line, column);
            case '-':
                Advance();
                if (Current == '>') { Advance(); return new SyntaxToken(SyntaxTokenKind.Arrow, "->", position, line, column); }
                if (Current == '=') { Advance(); return new SyntaxToken(SyntaxTokenKind.MinusEquals, "-=", position, line, column); }
                return new SyntaxToken(SyntaxTokenKind.Minus, "-", position, line, column);
            case '<':
                Advance();
                if (Current == '=')
                {
                    Advance();
                    return new SyntaxToken(SyntaxTokenKind.LessEquals, "<=", position, line, column);
                }
                return new SyntaxToken(SyntaxTokenKind.Less, "<", position, line, column);
            case '>':
                Advance();
                if (Current == '=')
                {
                    Advance();
                    return new SyntaxToken(SyntaxTokenKind.GreaterEquals, ">=", position, line, column);
                }
                return new SyntaxToken(SyntaxTokenKind.Greater, ">", position, line, column);
            case '&':
                Advance();
                if (Current == '&')
                {
                    Advance();
                    return new SyntaxToken(SyntaxTokenKind.AmpAmp, "&&", position, line, column);
                }
                return new SyntaxToken(SyntaxTokenKind.Ampersand, "&", position, line, column);
            case '|':
                Advance();
                if (Current == '|')
                {
                    Advance();
                    return new SyntaxToken(SyntaxTokenKind.PipePipe, "||", position, line, column);
                }
                break;
        }

        _diagnostics.Report(_filePath, line, column, $"Unexpected character '{Current}'.");
        Advance();
        return new SyntaxToken(SyntaxTokenKind.BadToken, _source[position.._position], position, line, column);
    }
}
