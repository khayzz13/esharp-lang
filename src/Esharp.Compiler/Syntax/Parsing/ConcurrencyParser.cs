using Esharp.Syntax;

namespace Esharp.Syntax.Parsing;

/// The concurrency surface: `async let`, `select`, `spawn { ... }`, and
/// `chan<T>(capacity)` creation. These share the channel/job vocabulary and are
/// dispatched into from the statement and expression parsers.
sealed class ConcurrencyParser : ParserUnit
{
    public ConcurrencyParser(Parser parser) : base(parser) { }

    public AsyncLetStatementSyntax ParseAsyncLetStatement()
    {
        var span = SpanHere();
        NextToken(); // consume 'async'
        Match(SyntaxTokenKind.LetKeyword);
        var nameTok = Match(SyntaxTokenKind.Identifier, "Expected variable name.");

        TypeSyntax? explicitType = null;
        if (Current.Kind == SyntaxTokenKind.Colon)
        {
            NextToken();
            explicitType = Types.ParseTypeUntil(SyntaxTokenKind.Equals);
        }

        Match(SyntaxTokenKind.Equals, "Expected '=' in async let.");
        var initializer = Expressions.ParseExpression();
        return new AsyncLetStatementSyntax(nameTok.Text, explicitType, initializer) { Span = SpanFrom(span), NameSpan = SpanOf(nameTok) };
    }

    public SelectStatementSyntax ParseSelectStatement()
    {
        var span = SpanHere();
        Match(SyntaxTokenKind.SelectKeyword);
        SkipSeparators();
        Match(SyntaxTokenKind.OpenBrace, "Expected '{' after select.");
        SkipSeparators();

        var arms = new List<SelectArmSyntax>();
        while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
        {
            if (Current.Kind == SyntaxTokenKind.DefaultKeyword)
            {
                NextToken();
                SkipSeparators();
                var body = Statements.ParseBlockStatement();
                arms.Add(new SelectArmSyntax(SelectArmKind.Default, null, null, null, body));
            }
            else if (Current.Kind == SyntaxTokenKind.Dot)
            {
                NextToken(); // consume '.'
                var kindToken = Match(SyntaxTokenKind.Identifier, "Expected select arm kind (recv, send, timeout).");
                Match(SyntaxTokenKind.OpenParen, "Expected '(' after select arm kind.");

                string? binding = null;
                var bindingSpan = SourceSpan.None;
                ExpressionSyntax? channel = null;
                ExpressionSyntax? value = null;

                SelectArmKind kind;
                if (kindToken.Text == "recv")
                {
                    kind = SelectArmKind.Recv;
                    var bindingTok = Match(SyntaxTokenKind.Identifier, "Expected binding name.");
                    binding = bindingTok.Text;
                    bindingSpan = SpanOf(bindingTok);
                    Match(SyntaxTokenKind.Comma, "Expected ',' after binding.");
                    channel = Expressions.ParseExpression();
                }
                else if (kindToken.Text == "send")
                {
                    kind = SelectArmKind.Send;
                    value = Expressions.ParseExpression();
                    Match(SyntaxTokenKind.Comma, "Expected ',' after value.");
                    channel = Expressions.ParseExpression();
                }
                else if (kindToken.Text == "timeout")
                {
                    kind = SelectArmKind.Timeout;
                    value = Expressions.ParseExpression();
                }
                else
                {
                    Report(kindToken.Line, kindToken.Column,
                        $"Unknown select arm kind '{kindToken.Text}' (expected recv, send, or timeout).");
                    kind = SelectArmKind.Default;
                }

                Match(SyntaxTokenKind.CloseParen, "Expected ')' to close select arm.");
                SkipSeparators();
                var body = Statements.ParseBlockStatement();
                arms.Add(new SelectArmSyntax(kind, binding, channel, value, body) { NameSpan = bindingSpan });
            }
            else
            {
                Report(Current.Line, Current.Column, $"Unexpected token '{Current.Text}' in select statement.");
                NextToken();
            }
            SkipSeparators();
        }

        Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close select.");
        return new SelectStatementSyntax(arms) { Span = SpanFrom(span) };
    }

    public SpawnExpressionSyntax ParseSpawnExpression()
    {
        Match(SyntaxTokenKind.SpawnKeyword);
        SkipSeparators();
        var block = Statements.ParseBlockStatement();
        return new SpawnExpressionSyntax(block);
    }

    public ChanCreationExpressionSyntax ParseChanCreationExpression()
    {
        Match(SyntaxTokenKind.ChanKeyword);
        Match(SyntaxTokenKind.Less, "Expected '<' after 'chan'.");
        var elementType = Types.ParseTypeUntil(SyntaxTokenKind.Greater);
        Match(SyntaxTokenKind.Greater, "Expected '>' after chan element type.");

        ExpressionSyntax? capacity = null;
        if (Current.Kind == SyntaxTokenKind.OpenParen)
        {
            NextToken();
            if (Current.Kind != SyntaxTokenKind.CloseParen)
                capacity = Expressions.ParseExpression();
            Match(SyntaxTokenKind.CloseParen, "Expected ')' after chan capacity.");
        }

        return new ChanCreationExpressionSyntax(elementType, capacity);
    }
}
