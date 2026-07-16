using Esharp.Syntax;

namespace Esharp.Syntax.Parsing;

/// Pure, stateless classification of tokens: operator precedence and the
/// expression-start predicate. No cursor, no position — just facts about the
/// grammar that several parsers consult.
static class SyntaxFacts
{
    /// Whether a token can begin an expression — used to disambiguate the
    /// contextual `yield` keyword from an ordinary identifier named `yield`.
    public static bool StartsExpression(SyntaxTokenKind k) => k is
        SyntaxTokenKind.Identifier or SyntaxTokenKind.NumberLiteral or SyntaxTokenKind.StringLiteral
        or SyntaxTokenKind.TrueKeyword or SyntaxTokenKind.FalseKeyword or SyntaxTokenKind.NilKeyword
        or SyntaxTokenKind.OpenParen or SyntaxTokenKind.OpenBracket
        or SyntaxTokenKind.Bang or SyntaxTokenKind.Plus or SyntaxTokenKind.Minus
        or SyntaxTokenKind.Tilde or SyntaxTokenKind.Ampersand;

    public static int GetUnaryPrecedence(SyntaxTokenKind kind) =>
        kind switch
        {
            SyntaxTokenKind.Bang or SyntaxTokenKind.Plus or SyntaxTokenKind.Minus or SyntaxTokenKind.Tilde
                or SyntaxTokenKind.NotKeyword or SyntaxTokenKind.Star => 11,
            _ => 0,
        };

    public static int GetBinaryPrecedence(SyntaxTokenKind kind) =>
        kind switch
        {
            SyntaxTokenKind.PipePipe or SyntaxTokenKind.OrKeyword => 1,
            SyntaxTokenKind.AmpAmp or SyntaxTokenKind.AndKeyword => 2,
            SyntaxTokenKind.Pipe => 3,
            SyntaxTokenKind.Caret => 4,
            SyntaxTokenKind.Ampersand => 5,
            SyntaxTokenKind.EqualsEquals or SyntaxTokenKind.BangEquals => 6,
            SyntaxTokenKind.Less or SyntaxTokenKind.LessEquals or SyntaxTokenKind.Greater or SyntaxTokenKind.GreaterEquals => 7,
            SyntaxTokenKind.ShiftLeft or SyntaxTokenKind.ShiftRight or SyntaxTokenKind.UnsignedShiftRight => 8,
            SyntaxTokenKind.Plus or SyntaxTokenKind.Minus => 9,
            SyntaxTokenKind.Star or SyntaxTokenKind.Slash or SyntaxTokenKind.Percent => 10,
            _ => 0,
        };

    public static bool IsOverloadableOperator(SyntaxTokenKind kind) => kind is
        SyntaxTokenKind.Plus or SyntaxTokenKind.Minus or SyntaxTokenKind.Bang or SyntaxTokenKind.Tilde or
        SyntaxTokenKind.Star or SyntaxTokenKind.Slash or SyntaxTokenKind.Percent or
        SyntaxTokenKind.Ampersand or SyntaxTokenKind.Pipe or SyntaxTokenKind.Caret or
        SyntaxTokenKind.ShiftLeft or SyntaxTokenKind.ShiftRight or SyntaxTokenKind.UnsignedShiftRight or
        SyntaxTokenKind.EqualsEquals or SyntaxTokenKind.BangEquals or
        SyntaxTokenKind.Less or SyntaxTokenKind.LessEquals or
        SyntaxTokenKind.Greater or SyntaxTokenKind.GreaterEquals;
}
