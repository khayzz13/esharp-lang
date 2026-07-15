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
        or SyntaxTokenKind.Bang or SyntaxTokenKind.Minus or SyntaxTokenKind.Ampersand;

    public static int GetUnaryPrecedence(SyntaxTokenKind kind) =>
        kind switch
        {
            SyntaxTokenKind.Bang or SyntaxTokenKind.Minus or SyntaxTokenKind.NotKeyword or SyntaxTokenKind.Star => 7,
            _ => 0,
        };

    public static int GetBinaryPrecedence(SyntaxTokenKind kind) =>
        kind switch
        {
            SyntaxTokenKind.PipePipe or SyntaxTokenKind.OrKeyword => 1,
            SyntaxTokenKind.AmpAmp or SyntaxTokenKind.AndKeyword => 2,
            SyntaxTokenKind.EqualsEquals or SyntaxTokenKind.BangEquals => 3,
            SyntaxTokenKind.Less or SyntaxTokenKind.LessEquals or SyntaxTokenKind.Greater or SyntaxTokenKind.GreaterEquals => 4,
            SyntaxTokenKind.Plus or SyntaxTokenKind.Minus => 5,
            SyntaxTokenKind.Star or SyntaxTokenKind.Slash or SyntaxTokenKind.Percent => 6,
            _ => 0,
        };
}
