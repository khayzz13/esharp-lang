namespace Esharp.Syntax;

public readonly record struct SyntaxToken(
    SyntaxTokenKind Kind,
    string Text,
    int Position,
    int Line,
    int Column,
    IReadOnlyList<SyntaxTrivia>? LeadingTrivia = null)
{
    /// Absolute offset one past the token's last character. `Text` is null on a
    /// `default` token, so guard it — `SpanFrom` reads `End` off the last-consumed
    /// token, which may be `default` before the first `Next()`.
    public int End => Position + (Text?.Length ?? 0);

    /// Never-null view of the leading trivia for printing/iteration.
    public IReadOnlyList<SyntaxTrivia> Leading => LeadingTrivia ?? [];
}
