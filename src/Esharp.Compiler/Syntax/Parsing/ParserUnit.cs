using Esharp.Syntax;

namespace Esharp.Syntax.Parsing;

/// Base for the domain parsers (declarations, statements, expressions, types,
/// match, concurrency). Each is a distinct class with a single responsibility;
/// they share one `TokenCursor` and reach each other through the root `Parser`.
/// The thin token-cursor accessors here keep the grammar methods reading the same
/// as a hand-written recursive-descent parser, while the actual position lives in
/// exactly one place.
abstract class ParserUnit
{
    protected readonly Parser P;

    protected ParserUnit(Parser parser) => P = parser;

    protected TokenCursor Cursor => P.Cursor;
    protected SyntaxToken Current => P.Cursor.Current;
    protected SyntaxToken Peek(int offset) => P.Cursor.Peek(offset);
    protected SyntaxToken NextToken() => P.Cursor.Next();
    protected SyntaxToken Match(SyntaxTokenKind kind, string? message = null) => P.Cursor.Match(kind, message);
    protected void SkipSeparators() => P.Cursor.SkipSeparators();
    protected void SkipNewlines() => P.Cursor.SkipNewlines();
    protected SourceSpan SpanHere() => P.Cursor.SpanHere();
    protected SourceSpan SpanFrom(SourceSpan start) => P.Cursor.SpanFrom(start);
    protected SourceSpan SpanFrom(SyntaxToken start) => P.Cursor.SpanFrom(start);
    protected SourceSpan SpanOf(SyntaxToken token) => P.Cursor.SpanOf(token);
    protected void Report(int line, int column, string message) => P.Cursor.Report(line, column, message);

    // Cross-domain wiring. The high-frequency siblings get shortcuts; the match
    // and concurrency parsers are reached as `P.Match` / `P.Concurrency` at their
    // few call sites (a `Match` shortcut would shadow the token matcher).
    protected DeclarationParser Declarations => P.Declarations;
    protected StatementParser Statements => P.Statements;
    protected ExpressionParser Expressions => P.Expressions;
    protected TypeParser Types => P.Types;
}
