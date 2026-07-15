using Esharp.Syntax;

namespace Esharp.Syntax.Parsing;

/// The `match` feature in one place: the statement and expression forms (which
/// differ only in subject parsing and the node produced) and the arm/pattern
/// grammar shared by both.
sealed class MatchParser : ParserUnit
{
    public MatchParser(Parser parser) : base(parser) { }

    public MatchStatementSyntax ParseMatchStatement()
    {
        var span = SpanHere();
        Match(SyntaxTokenKind.MatchKeyword);

        ExpressionSyntax subject;
        TypeSyntax? subjectType = null;

        if (Current.Kind == SyntaxTokenKind.OpenParen)
        {
            // match (expr: TypeName) { ... }  — annotated form
            NextToken();
            subject = Expressions.ParseExpression();
            Match(SyntaxTokenKind.Colon, "Expected ': TypeName' after match subject.");
            subjectType = Types.ParseTypeUntil(SyntaxTokenKind.CloseParen);
            Match(SyntaxTokenKind.CloseParen, "Expected ')' to close match header.");
        }
        else
        {
            // match expr { ... }  — inferred form
            subject = Expressions.ParseControlFlowCondition();
        }

        SkipSeparators();
        Match(SyntaxTokenKind.OpenBrace, "Expected '{' to start match body.");
        SkipSeparators();

        var arms = new List<MatchArmSyntax>();
        while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            var arm = ParseMatchArm();
            if (arm is not null)
                arms.Add(arm);
            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close match.");
        return new MatchStatementSyntax(subject, subjectType, arms) { Span = SpanFrom(span) };
    }

    public MatchExpressionSyntax ParseMatchExpression()
    {
        var span = SpanHere();
        Match(SyntaxTokenKind.MatchKeyword);

        ExpressionSyntax subject;
        TypeSyntax? subjectType = null;

        if (Current.Kind == SyntaxTokenKind.OpenParen)
        {
            NextToken();
            subject = Expressions.ParseExpression();
            Match(SyntaxTokenKind.Colon, "Expected ': TypeName' after match subject.");
            subjectType = Types.ParseTypeUntil(SyntaxTokenKind.CloseParen);
            Match(SyntaxTokenKind.CloseParen, "Expected ')' to close match header.");
        }
        else
        {
            subject = Expressions.ParseExpression();
        }

        SkipSeparators();
        Match(SyntaxTokenKind.OpenBrace, "Expected '{' to start match body.");
        SkipSeparators();

        var arms = new List<MatchArmSyntax>();
        while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            var arm = ParseMatchArm();
            if (arm is not null)
                arms.Add(arm);
            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close match.");
        return new MatchExpressionSyntax(subject, subjectType, arms) { Span = SpanFrom(span) };
    }

    MatchArmSyntax? ParseMatchArm()
    {
        MatchPatternSyntax pattern;

        if (Current.Kind == SyntaxTokenKind.DefaultKeyword)
        {
            NextToken();
            pattern = new MatchPatternSyntax(null, null, IsDefault: true);
        }
        else if (Current.Kind == SyntaxTokenKind.NilKeyword)
        {
            // `nil` arm — the absent-value case of a `T?` / reference scrutinee.
            NextToken();
            pattern = new MatchPatternSyntax(null, null, IsDefault: false, IsNil: true);
        }
        else if (Current.Kind == SyntaxTokenKind.OpenParen)
        {
            // Type pattern `(name: T)` — `is T` + bind the narrowed value. The leading
            // `(` is unambiguous in pattern position (no other pattern starts with it).
            NextToken();
            var bindNameTok = Match(SyntaxTokenKind.Identifier, "Expected binding name in type pattern '(name: Type)'.");
            Match(SyntaxTokenKind.Colon, "Expected ':' in type pattern '(name: Type)'.");
            var bindType = Types.ParseTypeUntil(SyntaxTokenKind.CloseParen);
            Match(SyntaxTokenKind.CloseParen, "Expected ')' to close type pattern.");
            pattern = new MatchPatternSyntax(null, null, IsDefault: false, TypeBindingName: bindNameTok.Text, TypeBindingType: bindType) { NameSpan = SpanOf(bindNameTok) };
        }
        else if (Current.Kind == SyntaxTokenKind.Dot)
        {
            NextToken();
            var caseNameTok = Match(SyntaxTokenKind.Identifier, "Expected case name after '.'.");
            var caseName = caseNameTok.Text;
            List<string>? bindingNames = null;

            List<SourceSpan>? bindingSpans = null;
            if (Current.Kind == SyntaxTokenKind.OpenParen)
            {
                NextToken();
                bindingNames = new List<string>();
                bindingSpans = new List<SourceSpan>();
                while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
                {
                    var bindingTok = Match(SyntaxTokenKind.Identifier, "Expected binding name.");
                    bindingNames.Add(bindingTok.Text);
                    bindingSpans.Add(SpanOf(bindingTok));
                    if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
                }
                Match(SyntaxTokenKind.CloseParen, "Expected ')' after binding names.");
            }

            pattern = new MatchPatternSyntax(caseName, bindingNames, IsDefault: false, BindingNameSpans: bindingSpans) { NameSpan = SpanOf(caseNameTok) };
        }
        else if (Current.Kind is SyntaxTokenKind.NumberLiteral or SyntaxTokenKind.StringLiteral
                     or SyntaxTokenKind.TrueKeyword or SyntaxTokenKind.FalseKeyword)
        {
            var literal = Expressions.ParsePrimaryExpression();
            pattern = new MatchPatternSyntax(null, null, IsDefault: false, LiteralValue: literal);
        }
        else if (Current.Kind == SyntaxTokenKind.Minus && Peek(1).Kind == SyntaxTokenKind.NumberLiteral)
        {
            // Negative literal: -1, -100
            NextToken(); // consume '-'
            var inner = Expressions.ParsePrimaryExpression();
            var literal = new UnaryExpressionSyntax(SyntaxTokenKind.Minus, inner);
            pattern = new MatchPatternSyntax(null, null, IsDefault: false, LiteralValue: literal);
        }
        else
        {
            Report(Current.Line, Current.Column,
                "Expected match arm: '.caseName', '(name: Type)', literal value, 'nil', or 'default'.");
            return null;
        }

        // Optional guard `if Expr` — refines the arm. Parsed as a control-flow condition
        // so a trailing `{` reads as the arm body, not an object initializer.
        ExpressionSyntax? guard = null;
        if (Current.Kind == SyntaxTokenKind.IfKeyword)
        {
            NextToken();
            guard = Expressions.ParseControlFlowCondition();
        }

        // Body: `=> Expr` (expression arm) or a `{ ... }` block.
        if (Current.Kind == SyntaxTokenKind.FatArrow)
        {
            NextToken();
            SkipNewlines();   // `=> ⏎ expr` — the value continues on the next line
            var exprBody = Expressions.ParseMatchArmExpressionBody();
            return new MatchArmSyntax(pattern, Body: null, Guard: guard, ExprBody: exprBody);
        }

        SkipSeparators();
        var body = Statements.ParseBlockStatement();
        return new MatchArmSyntax(pattern, body, Guard: guard);
    }
}
