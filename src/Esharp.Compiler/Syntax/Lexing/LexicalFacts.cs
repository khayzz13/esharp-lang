using Esharp.Syntax;

namespace Esharp.Syntax.Lexing;

/// Pure lexical classification — the keyword table and the character predicates,
/// with no state. The lexer's analogue of the parser's `SyntaxFacts`.
static class LexicalFacts
{
    static readonly Dictionary<string, SyntaxTokenKind> Keywords = new(StringComparer.Ordinal)
    {
        ["namespace"] = SyntaxTokenKind.NamespaceKeyword,
        ["struct"] = SyntaxTokenKind.StructKeyword,
        ["class"] = SyntaxTokenKind.ClassKeyword,
        ["union"] = SyntaxTokenKind.UnionKeyword,
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
        ["using"] = SyntaxTokenKind.UsingKeyword,
        ["interface"] = SyntaxTokenKind.InterfaceKeyword,
        ["derive"] = SyntaxTokenKind.DeriveKeyword,
        ["await"] = SyntaxTokenKind.AwaitKeyword,
        ["select"] = SyntaxTokenKind.SelectKeyword,
        ["ref"] = SyntaxTokenKind.RefKeyword,
        ["out"] = SyntaxTokenKind.OutKeyword,
        ["try"] = SyntaxTokenKind.TryKeyword,
        ["catch"] = SyntaxTokenKind.CatchKeyword,
        ["throw"] = SyntaxTokenKind.ThrowKeyword,
        ["params"] = SyntaxTokenKind.ParamsKeyword,
        ["pub"] = SyntaxTokenKind.PubKeyword,
        ["priv"] = SyntaxTokenKind.PrivKeyword,
        ["break"] = SyntaxTokenKind.BreakKeyword,
        ["continue"] = SyntaxTokenKind.ContinueKeyword,
        ["const"] = SyntaxTokenKind.ConstKeyword,
        // Type-narrowing operators (§type-narrowing-and-downcasting). Reserved: zero
        // collisions across the corpus, tests, and authored programs (the only `is`/`as`
        // text in `.es` lives inside string-interpolation bodies, never as identifiers).
        ["is"] = SyntaxTokenKind.IsKeyword,
        ["as"] = SyntaxTokenKind.AsKeyword,
        // Contextual keywords (matched by .Text in the parser, not reserved):
        // static / abstract / open / virtual / task / base / returns, plus
        // delegate / event / raise (the delegate & event syntax). Keeping them lexed
        // as Identifier preserves backward-compat with user code that uses these names
        // as locals/parameters/functions (`raise` is a common verb and not a C# keyword).
    };

    /// The keyword kind for an identifier's text, or `Identifier` if it is not reserved.
    public static SyntaxTokenKind KeywordOrIdentifier(string text) =>
        Keywords.GetValueOrDefault(text, SyntaxTokenKind.Identifier);

    public static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
    public static bool IsIdentifierContinue(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// A `{` in a string opens an interpolation hole only when the next character
    /// can start an expression — an identifier (letter or `_`), `(`, or unary `!`.
    /// A leading digit is excluded so BCL format strings (`"{0}"`, `"{0:d}"`) pass
    /// through literally. Must stay identical to the binder's IsInterpolationStart
    /// (Binder.Expressions.cs) so the lexer's string boundaries and the binder's
    /// hole-splitting agree on which braces are holes. `'\0'` (end of input) is not
    /// an expression start, so a trailing `"{` is a literal brace, not a hole.
    public static bool IsInterpolationStart(char c) =>
        char.IsLetter(c) || c == '_' || c == '(' || c == '!';
}
