using Esharp.Syntax;

namespace Esharp.Syntax.Parsing;

/// Statement-level grammar: the block and statement dispatch plus every leaf
/// statement form (let/var, if, while, foreach, return, yield, try, throw,
/// raise, defer, const). Match, select, and async-let belong to their own
/// feature parsers and are reached through the root.
sealed class StatementParser : ParserUnit
{
    public StatementParser(Parser parser) : base(parser) { }

    public BlockStatementSyntax ParseBlockStatement()
    {
        P.EnterRecursion();
        try { return ParseBlockStatementCore(); }
        finally { P.ExitRecursion(); }
    }

    BlockStatementSyntax ParseBlockStatementCore()
    {
        var span = SpanHere();
        Match(SyntaxTokenKind.OpenBrace, "Expected '{' to start block.");
        SkipSeparators();

        var statements = new List<StatementSyntax>();
        while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            statements.Add(ParseStatement());
            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close block.");
        return new BlockStatementSyntax(statements) { Span = SpanFrom(span) };
    }

    public StatementSyntax ParseStatement()
    {
        if (P.InClassInitBody
            && Current.Kind is SyntaxTokenKind.PubKeyword or SyntaxTokenKind.PrivKeyword
            && Peek(1).Kind == SyntaxTokenKind.Identifier && Peek(1).Text == "self"
            && Peek(2).Kind == SyntaxTokenKind.Dot)
            return ParseInitFieldDeclaration();
        if (P.InClassInitBody
            && Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "self"
            && Peek(1).Kind == SyntaxTokenKind.Dot && Peek(3).Kind == SyntaxTokenKind.Colon)
            return ParseInitFieldDeclaration();

        // Explicitly typed mutable value binding, mirroring field order:
        // `currentEnv: string = AppConfig.Environment`.  It is deliberately
        // non-addressable; `var currentEnv: string = ...` opts into location storage.
        if (Current.Kind == SyntaxTokenKind.Identifier && Peek(1).Kind == SyntaxTokenKind.Colon)
            return ParseTypedVariableDeclaration();

        // async let — contextual keyword combo
        if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "async" && Peek(1).Kind == SyntaxTokenKind.LetKeyword)
            return P.Concurrency.ParseAsyncLetStatement();

        // `yield &location` in a scoped property mut block is a lend point. Parse it
        // before stream yield so the bound tree preserves the distinct semantics.
        if (P.InScopedMutAccessorBody
            && Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "yield"
            && Peek(1).Kind == SyntaxTokenKind.Ampersand)
            return ParseMutYieldStatement();

        // yield <expr> — async-stream element (contextual keyword: only when followed
        // by an expression, so `yield` stays usable as an ordinary identifier).
        if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "yield" && SyntaxFacts.StartsExpression(Peek(1).Kind))
            return ParseYieldStatement();

        // raise EventName(args) — fire an event (contextual `raise`). The event-name
        // identifier between `raise` and `(` distinguishes it from a `raise(...)` call,
        // keeping `raise` usable as an ordinary function/identifier.
        if (Current.Kind == SyntaxTokenKind.Identifier && Current.Text == "raise"
            && Peek(1).Kind == SyntaxTokenKind.Identifier && Peek(2).Kind == SyntaxTokenKind.OpenParen)
            return ParseRaiseStatement();

        // `await for v in stream` — consume an async stream (IAsyncEnumerable<T>). The
        // `await` prefixes the loop; the rest parses exactly like a sync `for v in`.
        if (Current.Kind == SyntaxTokenKind.AwaitKeyword && Peek(1).Kind == SyntaxTokenKind.ForKeyword)
        {
            NextToken(); // consume 'await'
            return ParseForEachStatement(isAwait: true);
        }

        switch (Current.Kind)
        {
            case SyntaxTokenKind.LetKeyword:
                return ParseVariableDeclaration(mutable: false);
            case SyntaxTokenKind.VarKeyword:
                return ParseVariableDeclaration(mutable: true);
            case SyntaxTokenKind.IfKeyword:
                return ParseIfStatement();
            case SyntaxTokenKind.WhileKeyword:
                return ParseWhileStatement();
            case SyntaxTokenKind.ForKeyword:
                return ParseForEachStatement();
            case SyntaxTokenKind.ReturnKeyword:
                return ParseReturnStatement();
            case SyntaxTokenKind.OpenBrace:
                return ParseBlockStatement();
            case SyntaxTokenKind.MatchKeyword:
                return P.Match.ParseMatchStatement();
            case SyntaxTokenKind.DeferKeyword:
                return ParseDeferStatement();
            case SyntaxTokenKind.SelectKeyword:
                return P.Concurrency.ParseSelectStatement();
            case SyntaxTokenKind.TryKeyword:
                return ParseTryStatement();
            case SyntaxTokenKind.ThrowKeyword:
                return ParseThrowStatement();
            case SyntaxTokenKind.ConstKeyword:
                return ParseConstStatement();
            case SyntaxTokenKind.BreakKeyword:
            {
                var span = SpanHere();
                NextToken();
                return new BreakStatementSyntax { Span = SpanFrom(span) };
            }
            case SyntaxTokenKind.ContinueKeyword:
            {
                var span = SpanHere();
                NextToken();
                return new ContinueStatementSyntax { Span = SpanFrom(span) };
            }
            default:
                return ParseExpressionOrAssignmentStatement();
        }
    }

    InitFieldDeclarationStatementSyntax ParseInitFieldDeclaration()
    {
        var span = SpanHere();
        bool? isPublic = null;
        if (Current.Kind == SyntaxTokenKind.PubKeyword) { isPublic = true; NextToken(); }
        else if (Current.Kind == SyntaxTokenKind.PrivKeyword) { isPublic = false; NextToken(); }
        Match(SyntaxTokenKind.Identifier, "Expected 'self' in init field declaration.");
        Match(SyntaxTokenKind.Dot, "Expected '.' after self in init field declaration.");
        var name = Match(SyntaxTokenKind.Identifier, "Expected init field name.");
        Match(SyntaxTokenKind.Colon, "Expected ':' after init field name.");
        var type = Types.ParseTypeUntil(SyntaxTokenKind.Equals);
        Match(SyntaxTokenKind.Equals, "Expected '=' in init field declaration.");
        var initializer = Expressions.ParseExpression();
        return new InitFieldDeclarationStatementSyntax(isPublic, name.Text, type, initializer)
        { Span = SpanFrom(span), NameSpan = SpanOf(name) };
    }

    VariableDeclarationStatementSyntax ParseTypedVariableDeclaration()
    {
        var span = SpanHere();
        var name = NextToken();
        Match(SyntaxTokenKind.Colon);
        var type = Types.ParseTypeUntil(SyntaxTokenKind.Equals);
        Match(SyntaxTokenKind.Equals, "Expected '=' in typed variable declaration.");
        var initializer = Expressions.ParseExpression();
        return new VariableDeclarationStatementSyntax(true, name.Text, type, initializer,
            LocalRepresentation.BareTypedValue)
        { Span = SpanFrom(span), NameSpan = SpanOf(name) };
    }

    ConstStatementSyntax ParseConstStatement()
    {
        var span = SpanHere();
        Match(SyntaxTokenKind.ConstKeyword);
        var nameTok = Match(SyntaxTokenKind.Identifier, "Expected const name.");
        TypeSyntax? type = null;
        if (Current.Kind == SyntaxTokenKind.Colon)
        {
            NextToken();
            type = Types.ParseTypeUntil(SyntaxTokenKind.Equals);
        }
        Match(SyntaxTokenKind.Equals, "Expected '=' in const declaration.");
        var value = Expressions.ParseExpression();
        return new ConstStatementSyntax(nameTok.Text, type, value) { Span = SpanFrom(span), NameSpan = SpanOf(nameTok) };
    }

    RaiseStatementSyntax ParseRaiseStatement()
    {
        var span = SpanHere();
        NextToken(); // contextual 'raise'
        var name = Match(SyntaxTokenKind.Identifier, "Expected event name after 'raise'.").Text;
        Match(SyntaxTokenKind.OpenParen, "Expected '(' after event name.");
        var args = new List<ExpressionSyntax>();
        SkipSeparators();
        while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
        {
            args.Add(Expressions.ParseExpression());
            if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            SkipSeparators();
        }
        Match(SyntaxTokenKind.CloseParen, "Expected ')' after raise arguments.");
        return new RaiseStatementSyntax(name, args) { Span = SpanFrom(span) };
    }

    TryStatementSyntax ParseTryStatement()
    {
        var span = SpanHere();
        Match(SyntaxTokenKind.TryKeyword);
        SkipSeparators();
        var body = ParseBlockStatement();
        var catches = new List<CatchClauseSyntax>();
        SkipSeparators();
        while (Current.Kind == SyntaxTokenKind.CatchKeyword)
        {
            NextToken(); // consume 'catch'
            TypeSyntax? excType = null;
            string? bindingName = null;
            var bindingSpan = SourceSpan.None;
            if (Current.Kind == SyntaxTokenKind.OpenParen)
            {
                NextToken();
                // Name-first, like every E# declaration: `catch (e: Exception)` binds `e`,
                // `catch (_: Exception)` types the clause without keeping the value, and
                // `catch (e)` / `catch ()` / bare `catch` catch everything. There is no
                // type-first form — the binding name always precedes its type.
                if (Current.Kind != SyntaxTokenKind.CloseParen)
                {
                    var bindingTok = Match(SyntaxTokenKind.Identifier,
                        "Expected a binding name in catch clause — 'catch (e: Exception)', 'catch (_: Exception)', or 'catch ()'.");
                    bindingSpan = SpanOf(bindingTok);
                    if (Current.Kind == SyntaxTokenKind.Colon)
                    {
                        NextToken(); // consume ':'
                        excType = Types.ParseTypeUntil(SyntaxTokenKind.CloseParen);
                    }
                    // `_` is the discard — typed catch with no usable binding.
                    bindingName = bindingTok.Text == "_" ? null : bindingTok.Text;
                }
                Match(SyntaxTokenKind.CloseParen, "Expected ')' in catch clause.");
            }
            // Optional guard `catch (e: T) if cond { … }` — a CLR exception filter (reuses
            // `if`, no new keyword). The clause fires only when the type matches AND the
            // guard holds; otherwise control falls to the next clause.
            ExpressionSyntax? guard = null;
            if (Current.Kind == SyntaxTokenKind.IfKeyword)
            {
                NextToken(); // consume 'if'
                guard = Expressions.ParseExpression();
            }
            SkipSeparators();
            var catchBody = ParseBlockStatement();
            catches.Add(new CatchClauseSyntax(excType, bindingName, catchBody, guard) { NameSpan = bindingSpan });
            SkipSeparators();
        }
        return new TryStatementSyntax(body, catches) { Span = SpanFrom(span) };
    }

    ThrowStatementSyntax ParseThrowStatement()
    {
        var span = SpanHere();
        Match(SyntaxTokenKind.ThrowKeyword);
        // Bare `throw` → rethrow (only valid inside a catch)
        if (Current.Kind is SyntaxTokenKind.NewLine or SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile)
            return new ThrowStatementSyntax(null) { Span = SpanFrom(span) };
        return new ThrowStatementSyntax(Expressions.ParseExpression()) { Span = SpanFrom(span) };
    }

    // Parse an expression, then check if it's followed by = or compound op → assignment
    StatementSyntax ParseExpressionOrAssignmentStatement()
    {
        var span = SpanHere();

        // A statement that begins with a prefix unary operator (`-1`, `!flag`, `not done`)
        // is never an assignment target, so it routes through the full expression production
        // — the postfix entry below has no unary-prefix path and would reject the leading
        // `-` (probe3 #5: a bare `-1` match-arm value). Address-of/deref (`&`/`*`) are left
        // to the postfix+continuation path, which already handles their statement forms.
        if (Current.Kind is SyntaxTokenKind.Plus or SyntaxTokenKind.Minus or SyntaxTokenKind.Bang
            or SyntaxTokenKind.Tilde or SyntaxTokenKind.NotKeyword)
            return new ExpressionStatementSyntax(Expressions.ParseExpression()) { Span = SpanFrom(span) };

        var expr = Expressions.ParsePostfixExpression();

        if (Current.Kind == SyntaxTokenKind.Equals)
        {
            NextToken();
            var value = Expressions.ParseExpression();
            return new AssignmentStatementSyntax(expr, value) { Span = SpanFrom(span) };
        }

        if (Current.Kind is SyntaxTokenKind.PlusEquals or SyntaxTokenKind.MinusEquals
            or SyntaxTokenKind.StarEquals or SyntaxTokenKind.SlashEquals or SyntaxTokenKind.PercentEquals
            or SyntaxTokenKind.AmpersandEquals or SyntaxTokenKind.PipeEquals or SyntaxTokenKind.CaretEquals
            or SyntaxTokenKind.ShiftLeftEquals or SyntaxTokenKind.ShiftRightEquals or SyntaxTokenKind.UnsignedShiftRightEquals)
        {
            var op = NextToken().Kind;
            var value = Expressions.ParseExpression();
            return new CompoundAssignmentStatementSyntax(expr, op, value) { Span = SpanFrom(span) };
        }

        // Otherwise it's an expression statement — finish parsing as a full expression
        // (handle binary ops that may follow the postfix expr)
        expr = Expressions.ParseExpressionContinuation(expr, parentPrecedence: 0);
        return new ExpressionStatementSyntax(expr) { Span = SpanFrom(span) };
    }

    StatementSyntax ParseVariableDeclaration(bool mutable)
    {
        var span = SpanHere();
        NextToken();

        // Tuple destructuring: let (a, b) = expr
        if (Current.Kind == SyntaxTokenKind.OpenParen)
        {
            NextToken(); // consume (
            var nameToks = new List<SyntaxToken>();
            while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
            {
                nameToks.Add(Match(SyntaxTokenKind.Identifier, "Expected binding name."));
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            }
            Match(SyntaxTokenKind.CloseParen, "Expected ')' after destructured names.");
            Match(SyntaxTokenKind.Equals, "Expected '=' after destructured pattern.");
            var tupleInit = Expressions.ParseExpression();

            // Desugar to: let __tmp = expr; let a = __tmp.Item1; let b = __tmp.Item2; ...
            var tmpName = $"__tuple_{nameToks[0].Text}";
            var stmts = new List<StatementSyntax>();
                stmts.Add(new VariableDeclarationStatementSyntax(false, tmpName, null, tupleInit,
                    LocalRepresentation.ReadonlyLocation) { Span = SpanFrom(span) });
            for (var i = 0; i < nameToks.Count; i++)
            {
                var itemAccess = new MemberAccessExpressionSyntax(new NameExpressionSyntax(tmpName), $"Item{i + 1}");
                stmts.Add(new VariableDeclarationStatementSyntax(mutable, nameToks[i].Text, null, itemAccess,
                    mutable ? LocalRepresentation.MutableLocation : LocalRepresentation.ReadonlyLocation)
                    { Span = SpanFrom(span), NameSpan = SpanOf(nameToks[i]) });
            }
            return new BlockStatementSyntax(stmts) { Span = SpanFrom(span) };
        }

        SyntaxToken nameTok;
        if (Current.Kind == SyntaxTokenKind.Identifier)
        {
            nameTok = NextToken();
        }
        else
        {
            // A keyword was used as a variable name (e.g. `let pub = ...`).
            // Consume it so we don't leave it unconsumed and cascade into infinite recursion.
            Report(Current.Line, Current.Column,
                $"'{Current.Text}' is a keyword and cannot be used as a variable name.");
            nameTok = NextToken();
        }
        var name = nameTok.Text;

        TypeSyntax? explicitType = null;
        if (Current.Kind == SyntaxTokenKind.Colon)
        {
            NextToken();
            explicitType = Types.ParseTypeUntil(SyntaxTokenKind.Equals);
        }

        Match(SyntaxTokenKind.Equals, "Expected '=' in variable declaration.");
        var initializer = Expressions.ParseExpression();

        // let x = expr else { body }  — guard pattern
        if (Current.Kind == SyntaxTokenKind.ElseKeyword)
        {
            NextToken();
            SkipSeparators();
            var elseBody = ParseBlockStatement();
            return new LetGuardStatementSyntax(name, explicitType, initializer, elseBody) { Span = SpanFrom(span), NameSpan = SpanOf(nameTok) };
        }

        return new VariableDeclarationStatementSyntax(mutable, name, explicitType, initializer,
            mutable ? LocalRepresentation.MutableLocation : LocalRepresentation.ReadonlyLocation)
            { Span = SpanFrom(span), NameSpan = SpanOf(nameTok) };
    }

    IfStatementSyntax ParseIfStatement()
    {
        var span = SpanHere();
        Match(SyntaxTokenKind.IfKeyword);
        var condition = Expressions.ParseControlFlowCondition();
        SkipSeparators();
        var thenStatement = ParseStatement();
        SkipSeparators();

        StatementSyntax? elseStatement = null;
        if (Current.Kind == SyntaxTokenKind.ElseKeyword)
        {
            NextToken();
            SkipSeparators();
            elseStatement = ParseStatement();
        }

        return new IfStatementSyntax(condition, thenStatement, elseStatement) { Span = SpanFrom(span) };
    }

    ReturnStatementSyntax ParseReturnStatement()
    {
        var span = SpanHere();
        Match(SyntaxTokenKind.ReturnKeyword);
        if (Current.Kind is SyntaxTokenKind.NewLine or SyntaxTokenKind.CloseBrace)
            return new ReturnStatementSyntax(null) { Span = SpanFrom(span) };

        return new ReturnStatementSyntax(Expressions.ParseExpression()) { Span = SpanFrom(span) };
    }

    // `yield <expr>` produces the next element of an async stream (a function whose
    // body yields and whose return type is `IAsyncEnumerable<T>`).
    YieldStatementSyntax ParseYieldStatement()
    {
        var span = SpanHere();
        NextToken(); // consume the contextual `yield`
        return new YieldStatementSyntax(Expressions.ParseExpression()) { Span = SpanFrom(span) };
    }

    MutYieldStatementSyntax ParseMutYieldStatement()
    {
        var span = SpanHere();
        NextToken();
        return new MutYieldStatementSyntax(Expressions.ParseExpression()) { Span = SpanFrom(span) };
    }

    WhileStatementSyntax ParseWhileStatement()
    {
        var span = SpanHere();
        Match(SyntaxTokenKind.WhileKeyword);
        var condition = Expressions.ParseControlFlowCondition();
        SkipSeparators();
        var body = ParseStatement();
        return new WhileStatementSyntax(condition, body) { Span = SpanFrom(span) };
    }

    ForEachStatementSyntax ParseForEachStatement(bool isAwait = false)
    {
        var span = SpanHere();
        Match(SyntaxTokenKind.ForKeyword);

        // Tuple destructuring: for (a, b) in collection
        if (Current.Kind == SyntaxTokenKind.OpenParen)
        {
            NextToken(); // consume (
            var names = new List<string>();
            var nameSpans = new List<SourceSpan>();
            while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
            {
                var bindingTok = Match(SyntaxTokenKind.Identifier, "Expected binding name.");
                names.Add(bindingTok.Text);
                nameSpans.Add(SpanOf(bindingTok));
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            }
            Match(SyntaxTokenKind.CloseParen, "Expected ')' after destructured names.");
            Match(SyntaxTokenKind.InKeyword, "Expected 'in' in foreach loop.");
            var coll = Expressions.ParseForInCollection();
            SkipSeparators();
            var loopBody = ParseStatement();
            return new ForEachStatementSyntax($"__tuple_{names[0]}", coll, loopBody, names, nameSpans, IsAwait: isAwait) { Span = SpanFrom(span) };
        }

        var identifierTok = Match(SyntaxTokenKind.Identifier, "Expected loop variable name.");
        Match(SyntaxTokenKind.InKeyword, "Expected 'in' in foreach loop.");
        var collection = Expressions.ParseForInCollection();
        SkipSeparators();
        var body = ParseStatement();
        return new ForEachStatementSyntax(identifierTok.Text, collection, body, IsAwait: isAwait) { Span = SpanFrom(span), NameSpan = SpanOf(identifierTok) };
    }

    DeferStatementSyntax ParseDeferStatement()
    {
        var span = SpanHere();
        Match(SyntaxTokenKind.DeferKeyword);
        SkipSeparators();
        var body = ParseBlockStatement();
        return new DeferStatementSyntax(body) { Span = SpanFrom(span) };
    }
}
