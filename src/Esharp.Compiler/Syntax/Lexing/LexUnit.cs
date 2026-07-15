namespace Esharp.Syntax.Lexing;

/// Base for the domain scanners (trivia, literals, operators). Each is a distinct
/// class with a single responsibility; they share one `LexCursor` and report
/// through the root `Lexer`. The thin accessors keep the scan methods reading like
/// hand-written character code while the position lives in exactly one place — the
/// mirror of the parser's `ParserUnit`.
abstract class LexUnit
{
    protected readonly Lexer L;

    protected LexUnit(Lexer lexer) => L = lexer;

    protected LexCursor Cursor => L.Cursor;
    protected char Current => L.Cursor.Current;
    protected char Peek(int offset) => L.Cursor.Peek(offset);
    protected void Advance() => L.Cursor.Advance();
    protected void Report(int line, int column, string message) => L.Report(line, column, message);
}
