namespace Esharp.Compiler.Syntax;

public readonly record struct SyntaxToken(
    SyntaxTokenKind Kind,
    string Text,
    int Position,
    int Line,
    int Column);
