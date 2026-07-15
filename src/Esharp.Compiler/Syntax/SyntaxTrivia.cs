namespace Esharp.Syntax;

/// The lossless residue between significant tokens — whitespace and the three
/// comment forms. Trivia is what a batch compile discards but a formatter, an LSP,
/// and the round-trip printer must keep, so the source reconstructs character for
/// character. Each piece records its kind, verbatim text, and absolute start.
public enum SyntaxTriviaKind
{
    Whitespace,
    LineComment,   // // ...
    BlockComment,  // /* ... */
    DocComment,    // /// ...
}

public readonly record struct SyntaxTrivia(SyntaxTriviaKind Kind, string Text, int Position, int Line, int Column);
