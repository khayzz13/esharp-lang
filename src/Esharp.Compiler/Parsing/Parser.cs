using System.Text;
using Esharp.Compiler.Diagnostics;
using Esharp.Compiler.Lexing;
using Esharp.Compiler.Syntax;

namespace Esharp.Compiler.Parsing;

public sealed class Parser
{
    readonly string _filePath;
    readonly List<SyntaxToken> _tokens;
    readonly DiagnosticBag _diagnostics = new();
    int _position;

    public Parser(string source, string filePath = "input.es")
    {
        _filePath = filePath;

        var lexerDiagnostics = new DiagnosticBag();
        var lexer = new Lexer(source, filePath, lexerDiagnostics);
        _tokens = lexer.Lex();
        _diagnostics.AddRange(lexerDiagnostics.Diagnostics);
    }

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics.Diagnostics;

    SyntaxToken Current => Peek(0);

    SyntaxToken Peek(int offset)
    {
        var index = _position + offset;
        return index >= _tokens.Count ? _tokens[^1] : _tokens[index];
    }

    SyntaxToken NextToken()
    {
        var current = Current;
        _position = Math.Min(_position + 1, _tokens.Count);
        return current;
    }

    SyntaxToken Match(SyntaxTokenKind kind, string? message = null)
    {
        if (Current.Kind == kind)
            return NextToken();

        _diagnostics.Report(
            _filePath,
            Current.Line,
            Current.Column,
            message ?? $"Expected token '{kind}' but found '{Current.Kind}'.");

        return new SyntaxToken(kind, string.Empty, Current.Position, Current.Line, Current.Column);
    }

    void SkipSeparators()
    {
        while (Current.Kind is SyntaxTokenKind.NewLine or SyntaxTokenKind.Comma)
            NextToken();
    }

    public CompilationUnitSyntax ParseCompilationUnit()
    {
        string? moduleName = null;
        SkipSeparators();

        if (Current.Kind == SyntaxTokenKind.ModuleKeyword)
        {
            NextToken();
            moduleName = Match(SyntaxTokenKind.Identifier, "Expected module name.").Text;
            SkipSeparators();
        }

        var imports = new List<string>();
        while (Current.Kind == SyntaxTokenKind.ImportKeyword)
        {
            NextToken();
            var ns = Match(SyntaxTokenKind.StringLiteral, "Expected string literal after 'import'.").Text;
            // Strip quotes
            if (ns.Length >= 2 && ns[0] == '"' && ns[^1] == '"')
                ns = ns[1..^1];
            imports.Add(ns);
            SkipSeparators();
        }

        var members = new List<MemberSyntax>();
        while (Current.Kind != SyntaxTokenKind.EndOfFile)
        {
            SkipSeparators();
            if (Current.Kind == SyntaxTokenKind.EndOfFile)
                break;

            var member = ParseMember();
            if (member is not null)
                members.Add(member);
            else
                NextToken();

            SkipSeparators();
        }

        return new CompilationUnitSyntax(moduleName, imports, members);
    }

    // Track derive directives that apply to the next declaration
    DeriveDirectiveSyntax? _pendingDerive;

    MemberSyntax? ParseMember()
    {
        // Parse #derive directive if present
        if (Current.Kind == SyntaxTokenKind.DeriveKeyword)
        {
            NextToken();
            var traits = new List<string>();
            while (Current.Kind == SyntaxTokenKind.Identifier)
            {
                traits.Add(NextToken().Text);
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            }
            _pendingDerive = new DeriveDirectiveSyntax(traits);
            SkipSeparators();
        }

        return Current.Kind switch
        {
            SyntaxTokenKind.DataKeyword => ParseDataDeclaration(),
            SyntaxTokenKind.ChoiceKeyword => ParseChoiceDeclaration(),
            SyntaxTokenKind.FuncKeyword => ParseFunctionDeclaration(),
            SyntaxTokenKind.EnumKeyword => ParseEnumDeclaration(),
            SyntaxTokenKind.ProtocolKeyword => ParseProtocolDeclaration(),
            _ => ReportUnexpectedMember(),
        };
    }

    MemberSyntax? ReportUnexpectedMember()
    {
        _diagnostics.Report(_filePath, Current.Line, Current.Column, $"Unexpected member starting with '{Current.Text}'.");
        return null;
    }

    DataDeclarationSyntax ParseDataDeclaration()
    {
        Match(SyntaxTokenKind.DataKeyword);
        var name = Match(SyntaxTokenKind.Identifier, "Expected data type name.").Text;

        // Optional type parameters: data Pair<A, B> { ... }
        var typeParameters = new List<string>();
        if (Current.Kind == SyntaxTokenKind.Less)
        {
            NextToken();
            while (Current.Kind is not (SyntaxTokenKind.Greater or SyntaxTokenKind.EndOfFile))
            {
                typeParameters.Add(Match(SyntaxTokenKind.Identifier, "Expected type parameter name.").Text);
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            }
            Match(SyntaxTokenKind.Greater, "Expected '>' to close type parameter list.");
        }

        // Optional interface list: data Foo : IBar, IBaz { ... }
        var interfaces = new List<string>();
        if (Current.Kind == SyntaxTokenKind.Colon)
        {
            NextToken();
            while (Current.Kind != SyntaxTokenKind.OpenBrace && Current.Kind != SyntaxTokenKind.NewLine && Current.Kind != SyntaxTokenKind.EndOfFile)
            {
                interfaces.Add(Match(SyntaxTokenKind.Identifier, "Expected interface name.").Text);
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            }
        }

        SkipSeparators();
        Match(SyntaxTokenKind.OpenBrace, "Expected '{' after data declaration.");
        SkipSeparators();

        var fields = new List<FieldSyntax>();
        while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            var fieldName = Match(SyntaxTokenKind.Identifier, "Expected field name.").Text;
            Match(SyntaxTokenKind.Colon, "Expected ':' after field name.");
            var type = ParseTypeNameUntil(SyntaxTokenKind.NewLine, SyntaxTokenKind.Comma, SyntaxTokenKind.CloseBrace);
            fields.Add(new FieldSyntax(fieldName, type));
            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close data declaration.");
        // Consume pending derive if any — attach to the DataDeclarationSyntax via interfaces/traits
        // We'll pass derive info through the interface list for now, prefixed with #
        if (_pendingDerive is not null)
        {
            foreach (var trait in _pendingDerive.Traits)
                interfaces.Add($"#{trait}");
            _pendingDerive = null;
        }
        return new DataDeclarationSyntax(name, typeParameters, interfaces, fields);
    }

    ChoiceDeclarationSyntax ParseChoiceDeclaration()
    {
        Match(SyntaxTokenKind.ChoiceKeyword);
        var name = Match(SyntaxTokenKind.Identifier, "Expected choice type name.").Text;

        // Optional type parameters: choice Option<T> { ... }
        var typeParameters = new List<string>();
        if (Current.Kind == SyntaxTokenKind.Less)
        {
            NextToken();
            while (Current.Kind is not (SyntaxTokenKind.Greater or SyntaxTokenKind.EndOfFile))
            {
                typeParameters.Add(Match(SyntaxTokenKind.Identifier, "Expected type parameter name.").Text);
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            }
            Match(SyntaxTokenKind.Greater, "Expected '>' to close type parameter list.");
        }

        SkipSeparators();
        Match(SyntaxTokenKind.OpenBrace, "Expected '{' after choice declaration.");
        SkipSeparators();

        var cases = new List<ChoiceCaseSyntax>();
        while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            var caseName = Match(SyntaxTokenKind.Identifier, "Expected case name.").Text;
            string? payloadName = null;
            TypeReferenceSyntax? payloadType = null;

            if (Current.Kind == SyntaxTokenKind.OpenParen)
            {
                NextToken();
                payloadName = Match(SyntaxTokenKind.Identifier, "Expected payload name.").Text;
                Match(SyntaxTokenKind.Colon, "Expected ':' after payload name.");
                payloadType = ParseTypeNameUntil(SyntaxTokenKind.CloseParen);
                Match(SyntaxTokenKind.CloseParen, "Expected ')' after payload type.");
            }

            cases.Add(new ChoiceCaseSyntax(caseName, payloadName, payloadType));
            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close choice declaration.");
        return new ChoiceDeclarationSyntax(name, typeParameters, cases);
    }

    EnumDeclarationSyntax ParseEnumDeclaration()
    {
        Match(SyntaxTokenKind.EnumKeyword);
        var name = Match(SyntaxTokenKind.Identifier, "Expected enum type name.").Text;
        SkipSeparators();
        Match(SyntaxTokenKind.OpenBrace, "Expected '{' after enum declaration.");
        SkipSeparators();

        var cases = new List<string>();
        while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            var caseName = Match(SyntaxTokenKind.Identifier, "Expected enum case name.").Text;
            cases.Add(caseName);
            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close enum declaration.");
        return new EnumDeclarationSyntax(name, cases);
    }

    ProtocolDeclarationSyntax ParseProtocolDeclaration()
    {
        Match(SyntaxTokenKind.ProtocolKeyword);
        var name = Match(SyntaxTokenKind.Identifier, "Expected protocol name.").Text;
        SkipSeparators();
        Match(SyntaxTokenKind.OpenBrace, "Expected '{' after protocol declaration.");
        SkipSeparators();

        var methods = new List<ProtocolMethodSyntax>();
        while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            Match(SyntaxTokenKind.FuncKeyword, "Expected 'func' in protocol method.");
            var methodName = Match(SyntaxTokenKind.Identifier, "Expected method name.").Text;
            Match(SyntaxTokenKind.OpenParen, "Expected '(' after method name.");

            var parameters = new List<ParameterSyntax>();
            SkipSeparators();
            while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
            {
                var paramName = Match(SyntaxTokenKind.Identifier, "Expected parameter name.").Text;
                Match(SyntaxTokenKind.Colon, "Expected ':' after parameter name.");
                var type = ParseTypeNameUntil(SyntaxTokenKind.Comma, SyntaxTokenKind.CloseParen);
                parameters.Add(new ParameterSyntax(paramName, type));
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
                SkipSeparators();
            }
            Match(SyntaxTokenKind.CloseParen, "Expected ')' after parameters.");

            TypeReferenceSyntax returnType;
            if (Current.Kind == SyntaxTokenKind.Arrow)
            {
                NextToken();
                returnType = ParseTypeNameUntil(SyntaxTokenKind.NewLine, SyntaxTokenKind.CloseBrace);
            }
            else
            {
                returnType = new TypeReferenceSyntax("void");
            }

            methods.Add(new ProtocolMethodSyntax(methodName, parameters, returnType));
            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close protocol.");
        return new ProtocolDeclarationSyntax(name, methods);
    }

    FunctionDeclarationSyntax ParseFunctionDeclaration()
    {
        Match(SyntaxTokenKind.FuncKeyword);
        var name = Match(SyntaxTokenKind.Identifier, "Expected function name.").Text;

        // Parse optional type parameters <T, U, ...>
        var typeParameters = new List<string>();
        if (Current.Kind == SyntaxTokenKind.Less)
        {
            NextToken();
            while (Current.Kind is not (SyntaxTokenKind.Greater or SyntaxTokenKind.EndOfFile))
            {
                typeParameters.Add(Match(SyntaxTokenKind.Identifier, "Expected type parameter name.").Text);
                if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            }
            Match(SyntaxTokenKind.Greater, "Expected '>' to close type parameter list.");
        }

        Match(SyntaxTokenKind.OpenParen, "Expected '(' after function name.");

        var parameters = new List<ParameterSyntax>();
        SkipSeparators();
        while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
        {
            if (Current.Kind == SyntaxTokenKind.NewLine)
            {
                _diagnostics.Report(_filePath, Current.Line, Current.Column, "Expected ')' to close parameter list.");
                break;
            }

            var startPosition = Current.Position;
            var parameterName = Match(SyntaxTokenKind.Identifier, "Expected parameter name.").Text;
            Match(SyntaxTokenKind.Colon, "Expected ':' after parameter name.");
            var type = ParseTypeNameUntil(SyntaxTokenKind.Comma, SyntaxTokenKind.CloseParen);
            parameters.Add(new ParameterSyntax(parameterName, type));

            if (Current.Kind == SyntaxTokenKind.Comma)
                NextToken();

            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseParen, "Expected ')' after parameter list.");

        TypeReferenceSyntax returnType;
        if (Current.Kind == SyntaxTokenKind.Arrow)
        {
            NextToken();
            returnType = ParseTypeNameUntil(SyntaxTokenKind.OpenBrace, SyntaxTokenKind.NewLine);
        }
        else
        {
            returnType = new TypeReferenceSyntax("void");
        }

        SkipSeparators();
        var body = ParseBlockStatement();
        return new FunctionDeclarationSyntax(name, typeParameters, parameters, returnType, body);
    }

    BlockStatementSyntax ParseBlockStatement()
    {
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
        return new BlockStatementSyntax(statements);
    }

    StatementSyntax ParseStatement()
    {
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
                return ParseMatchStatement();
            case SyntaxTokenKind.DeferKeyword:
                return ParseDeferStatement();
            default:
                return ParseExpressionOrAssignmentStatement();
        }
    }

    // Parse an expression, then check if it's followed by = or compound op → assignment
    StatementSyntax ParseExpressionOrAssignmentStatement()
    {
        var expr = ParsePostfixExpression();

        if (Current.Kind == SyntaxTokenKind.Equals)
        {
            NextToken();
            var value = ParseExpression();
            return new AssignmentStatementSyntax(expr, value);
        }

        if (Current.Kind is SyntaxTokenKind.PlusEquals or SyntaxTokenKind.MinusEquals
            or SyntaxTokenKind.StarEquals or SyntaxTokenKind.SlashEquals)
        {
            var op = NextToken().Kind;
            var value = ParseExpression();
            return new CompoundAssignmentStatementSyntax(expr, op, value);
        }

        // Otherwise it's an expression statement — finish parsing as a full expression
        // (handle binary ops that may follow the postfix expr)
        expr = ParseExpressionContinuation(expr, parentPrecedence: 0);
        return new ExpressionStatementSyntax(expr);
    }

    // Continue binary expression parsing from an already-parsed left side
    ExpressionSyntax ParseExpressionContinuation(ExpressionSyntax left, int parentPrecedence)
    {
        while (true)
        {
            var precedence = GetBinaryPrecedence(Current.Kind);
            if (precedence == 0 || precedence <= parentPrecedence)
                break;

            var operatorKind = NextToken().Kind;
            var right = ParseExpression(precedence);
            left = new BinaryExpressionSyntax(left, operatorKind, right);
        }
        return left;
    }

    StatementSyntax ParseVariableDeclaration(bool mutable)
    {
        NextToken();
        var name = Match(SyntaxTokenKind.Identifier, "Expected variable name.").Text;

        TypeReferenceSyntax? explicitType = null;
        if (Current.Kind == SyntaxTokenKind.Colon)
        {
            NextToken();
            explicitType = ParseTypeNameUntil(SyntaxTokenKind.Equals);
        }

        Match(SyntaxTokenKind.Equals, "Expected '=' in variable declaration.");
        var initializer = ParseExpression();

        // let x = expr else { body }  — guard pattern
        if (Current.Kind == SyntaxTokenKind.ElseKeyword)
        {
            NextToken();
            SkipSeparators();
            var elseBody = ParseBlockStatement();
            return new LetGuardStatementSyntax(name, explicitType, initializer, elseBody);
        }

        return new VariableDeclarationStatementSyntax(mutable, name, explicitType, initializer);
    }

    IfStatementSyntax ParseIfStatement()
    {
        Match(SyntaxTokenKind.IfKeyword);
        var condition = ParseExpression();
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

        return new IfStatementSyntax(condition, thenStatement, elseStatement);
    }

    ReturnStatementSyntax ParseReturnStatement()
    {
        Match(SyntaxTokenKind.ReturnKeyword);
        if (Current.Kind is SyntaxTokenKind.NewLine or SyntaxTokenKind.CloseBrace)
            return new ReturnStatementSyntax(null);

        return new ReturnStatementSyntax(ParseExpression());
    }

    WhileStatementSyntax ParseWhileStatement()
    {
        Match(SyntaxTokenKind.WhileKeyword);
        var condition = ParseExpression();
        SkipSeparators();
        var body = ParseStatement();
        return new WhileStatementSyntax(condition, body);
    }

    ForEachStatementSyntax ParseForEachStatement()
    {
        Match(SyntaxTokenKind.ForKeyword);
        var identifier = Match(SyntaxTokenKind.Identifier, "Expected loop variable name.").Text;
        Match(SyntaxTokenKind.InKeyword, "Expected 'in' in foreach loop.");
        var collection = ParseExpression();
        SkipSeparators();
        var body = ParseStatement();
        return new ForEachStatementSyntax(identifier, collection, body);
    }

    MatchStatementSyntax ParseMatchStatement()
    {
        Match(SyntaxTokenKind.MatchKeyword);

        ExpressionSyntax subject;
        TypeReferenceSyntax? subjectType = null;

        if (Current.Kind == SyntaxTokenKind.OpenParen)
        {
            // match (expr: TypeName) { ... }  — annotated form
            NextToken();
            subject = ParseExpression();
            Match(SyntaxTokenKind.Colon, "Expected ': TypeName' after match subject.");
            subjectType = ParseTypeNameUntil(SyntaxTokenKind.CloseParen);
            Match(SyntaxTokenKind.CloseParen, "Expected ')' to close match header.");
        }
        else
        {
            // match expr { ... }  — inferred form
            subject = ParseExpression();
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
        return new MatchStatementSyntax(subject, subjectType, arms);
    }

    ExpressionSyntax ParseIndexOrRange(ExpressionSyntax target)
    {
        Match(SyntaxTokenKind.OpenBracket);

        // [..end]
        if (Current.Kind == SyntaxTokenKind.DotDot)
        {
            NextToken();
            var end = Current.Kind == SyntaxTokenKind.CloseBracket ? null : ParseExpression();
            Match(SyntaxTokenKind.CloseBracket, "Expected ']'.");
            return new RangeExpressionSyntax(target, null, end);
        }

        // [^expr]
        if (Current.Kind == SyntaxTokenKind.Caret)
        {
            NextToken();
            var index = ParseExpression();
            Match(SyntaxTokenKind.CloseBracket, "Expected ']'.");
            // Wrap in a unary ^ node — we'll emit as ^expr
            return new IndexExpressionSyntax(target, new UnaryExpressionSyntax(SyntaxTokenKind.Caret, index));
        }

        var expr = ParseExpression();

        // [start..end] or [start..]
        if (Current.Kind == SyntaxTokenKind.DotDot)
        {
            NextToken();
            var end = Current.Kind == SyntaxTokenKind.CloseBracket ? null : ParseExpression();
            Match(SyntaxTokenKind.CloseBracket, "Expected ']'.");
            return new RangeExpressionSyntax(target, expr, end);
        }

        // [expr] — simple index
        Match(SyntaxTokenKind.CloseBracket, "Expected ']'.");
        return new IndexExpressionSyntax(target, expr);
    }

    DeferStatementSyntax ParseDeferStatement()
    {
        Match(SyntaxTokenKind.DeferKeyword);
        SkipSeparators();
        var body = ParseBlockStatement();
        return new DeferStatementSyntax(body);
    }

    MatchArmSyntax? ParseMatchArm()
    {
        MatchPatternSyntax pattern;

        if (Current.Kind == SyntaxTokenKind.DefaultKeyword)
        {
            NextToken();
            pattern = new MatchPatternSyntax(null, null, IsDefault: true);
        }
        else if (Current.Kind == SyntaxTokenKind.Dot)
        {
            NextToken();
            var caseName = Match(SyntaxTokenKind.Identifier, "Expected case name after '.'.").Text;
            string? bindingName = null;

            if (Current.Kind == SyntaxTokenKind.OpenParen)
            {
                NextToken();
                bindingName = Match(SyntaxTokenKind.Identifier, "Expected binding name.").Text;
                Match(SyntaxTokenKind.CloseParen, "Expected ')' after binding name.");
            }

            pattern = new MatchPatternSyntax(caseName, bindingName, IsDefault: false);
        }
        else
        {
            _diagnostics.Report(_filePath, Current.Line, Current.Column,
                "Expected match arm: '.caseName', '.caseName(binding)', or 'default'.");
            return null;
        }

        SkipSeparators();
        var body = ParseBlockStatement();
        return new MatchArmSyntax(pattern, body);
    }

    TypeReferenceSyntax ParseTypeNameCore(HashSet<SyntaxTokenKind> terminators)
    {
        // Handle by-ref prefix *
        bool byRef = false;
        if (Current.Kind == SyntaxTokenKind.Star)
        {
            byRef = true;
            NextToken();
        }

        var builder = new StringBuilder();
        var genericDepth = 0;
        var bracketDepth = 0;

        while (Current.Kind != SyntaxTokenKind.EndOfFile)
        {
            if (genericDepth == 0 && bracketDepth == 0 && terminators.Contains(Current.Kind))
                break;

            if (Current.Kind == SyntaxTokenKind.NewLine && genericDepth == 0 && bracketDepth == 0)
                break;

            if (Current.Kind == SyntaxTokenKind.Less)
                genericDepth++;
            else if (Current.Kind == SyntaxTokenKind.Greater && genericDepth > 0)
                genericDepth--;
            else if (Current.Kind == SyntaxTokenKind.OpenBracket)
                bracketDepth++;
            else if (Current.Kind == SyntaxTokenKind.CloseBracket && bracketDepth > 0)
                bracketDepth--;

            builder.Append(NextToken().Text);
        }

        var typeName = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(typeName))
        {
            _diagnostics.Report(_filePath, Current.Line, Current.Column, "Expected type name.");
            typeName = "object";
        }

        return new TypeReferenceSyntax(typeName, byRef);
    }

    TypeReferenceSyntax ParseTypeNameUntil(params SyntaxTokenKind[] terminators) =>
        ParseTypeNameCore(terminators.ToHashSet());

    ExpressionSyntax ParseExpression(int parentPrecedence = 0)
    {
        ExpressionSyntax left;
        var unaryPrecedence = GetUnaryPrecedence(Current.Kind);
        if (unaryPrecedence != 0 && unaryPrecedence >= parentPrecedence)
        {
            var operatorKind = NextToken().Kind;
            var operand = ParseExpression(unaryPrecedence);
            left = new UnaryExpressionSyntax(operatorKind, operand);
        }
        else
        {
            left = ParsePostfixExpression();
        }

        while (true)
        {
            var precedence = GetBinaryPrecedence(Current.Kind);
            if (precedence == 0 || precedence <= parentPrecedence)
                break;

            var operatorKind = NextToken().Kind;
            var right = ParseExpression(precedence);
            left = new BinaryExpressionSyntax(left, operatorKind, right);
        }

        return left;
    }

    ExpressionSyntax ParsePostfixExpression()
    {
        var expression = ParsePrimaryExpression();

        while (true)
        {
            if (Current.Kind == SyntaxTokenKind.OpenParen)
            {
                var arguments = ParseArgumentList();
                expression = new CallExpressionSyntax(expression, arguments);
                continue;
            }

            if (Current.Kind == SyntaxTokenKind.Dot)
            {
                NextToken();
                var memberName = Match(SyntaxTokenKind.Identifier, "Expected member name after '.'.").Text;
                expression = new MemberAccessExpressionSyntax(expression, memberName);
                continue;
            }

            if (Current.Kind == SyntaxTokenKind.OpenBracket)
            {
                expression = ParseIndexOrRange(expression);
                continue;
            }

            if (Current.Kind == SyntaxTokenKind.Question)
            {
                NextToken();
                expression = new TryUnwrapExpressionSyntax(expression);
                continue;
            }

            // Object creation: TypeName { ... } or TypeName<Args> { ... }
            if (expression is NameExpressionSyntax nameExpression &&
                nameExpression.Name.Length > 0 &&
                char.IsUpper(nameExpression.Name[0]))
            {
                if (Current.Kind == SyntaxTokenKind.OpenBrace)
                {
                    expression = ParseObjectInitializer(nameExpression.Name);
                    continue;
                }

                // Speculative: TypeName<A, B> { ... } → generic object creation
                if (Current.Kind == SyntaxTokenKind.Less)
                {
                    var savedPos = _position;
                    NextToken(); // consume <
                    var depth = 1;
                    while (depth > 0 && Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.NewLine)
                    {
                        if (Current.Kind == SyntaxTokenKind.Less) depth++;
                        else if (Current.Kind == SyntaxTokenKind.Greater) { depth--; if (depth == 0) break; }
                        NextToken();
                    }

                    if (depth == 0 && Current.Kind == SyntaxTokenKind.Greater)
                    {
                        NextToken(); // consume >
                        if (Current.Kind == SyntaxTokenKind.OpenBrace)
                        {
                            // Build full generic type name from tokens
                            var typeName = nameExpression.Name;
                            for (var i = savedPos; i < _position; i++)
                                typeName += _tokens[i].Text;
                            expression = ParseObjectInitializer(typeName);
                            continue;
                        }
                    }

                    _position = savedPos; // revert if not object creation
                }
            }

            break;
        }

        return expression;
    }

    IReadOnlyList<ExpressionSyntax> ParseArgumentList()
    {
        Match(SyntaxTokenKind.OpenParen, "Expected '(' to start argument list.");
        var arguments = new List<ExpressionSyntax>();
        SkipSeparators();

        while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
        {
            arguments.Add(ParseExpression());
            SkipSeparators();
            if (Current.Kind == SyntaxTokenKind.Comma)
            {
                NextToken();
                SkipSeparators();
            }
        }

        Match(SyntaxTokenKind.CloseParen, "Expected ')' to close argument list.");
        return arguments;
    }

    ObjectCreationExpressionSyntax ParseObjectInitializer(string typeName)
    {
        Match(SyntaxTokenKind.OpenBrace, "Expected '{' to start object initializer.");
        SkipSeparators();

        var fields = new List<ObjectInitializerFieldSyntax>();
        while (Current.Kind is not (SyntaxTokenKind.CloseBrace or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            var fieldName = Match(SyntaxTokenKind.Identifier, "Expected initializer field name.").Text;
            Match(SyntaxTokenKind.Colon, "Expected ':' after initializer field name.");
            var value = ParseExpression();
            fields.Add(new ObjectInitializerFieldSyntax(fieldName, value));
            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseBrace, "Expected '}' to close object initializer.");
        return new ObjectCreationExpressionSyntax(new TypeReferenceSyntax(typeName), fields);
    }

    ExpressionSyntax ParsePrimaryExpression()
    {
        switch (Current.Kind)
        {
            case SyntaxTokenKind.NumberLiteral:
                return new LiteralExpressionSyntax(ParseNumber(NextToken().Text), PreviousText());
            case SyntaxTokenKind.StringLiteral:
                return new LiteralExpressionSyntax(ParseString(NextToken().Text), PreviousText());
            case SyntaxTokenKind.TrueKeyword:
                return new LiteralExpressionSyntax(true, NextToken().Text);
            case SyntaxTokenKind.FalseKeyword:
                return new LiteralExpressionSyntax(false, NextToken().Text);
            case SyntaxTokenKind.NilKeyword:
                return new LiteralExpressionSyntax(null, NextToken().Text);
            case SyntaxTokenKind.AwaitKeyword:
                NextToken();
                return new AwaitExpressionSyntax(ParsePostfixExpression());
            case SyntaxTokenKind.SpawnKeyword:
                return ParseSpawnExpression();
            case SyntaxTokenKind.ChanKeyword:
                return ParseChanCreationExpression();
            case SyntaxTokenKind.Dot:
                return ParseDotCaseExpression();
            case SyntaxTokenKind.FuncKeyword:
                return ParseFunctionLiteral();
            case SyntaxTokenKind.Ampersand:
                NextToken();
                var fnName = Match(SyntaxTokenKind.Identifier, "Expected function name after '&'.").Text;
                return new AddressOfExpressionSyntax(fnName);
            case SyntaxTokenKind.Identifier:
                return new NameExpressionSyntax(NextToken().Text);
            case SyntaxTokenKind.OpenParen:
                return ParseParenthesizedExpression();
            default:
                return ParseMissingExpression();
        }
    }

    ChanCreationExpressionSyntax ParseChanCreationExpression()
    {
        Match(SyntaxTokenKind.ChanKeyword);
        Match(SyntaxTokenKind.Less, "Expected '<' after 'chan'.");
        var elementType = ParseTypeNameUntil(SyntaxTokenKind.Greater);
        Match(SyntaxTokenKind.Greater, "Expected '>' after chan element type.");

        ExpressionSyntax? capacity = null;
        if (Current.Kind == SyntaxTokenKind.OpenParen)
        {
            NextToken();
            if (Current.Kind != SyntaxTokenKind.CloseParen)
                capacity = ParseExpression();
            Match(SyntaxTokenKind.CloseParen, "Expected ')' after chan capacity.");
        }

        return new ChanCreationExpressionSyntax(elementType, capacity);
    }

    DotCaseExpressionSyntax ParseDotCaseExpression()
    {
        Match(SyntaxTokenKind.Dot);
        var caseName = Match(SyntaxTokenKind.Identifier, "Expected case name after '.'.").Text;

        var arguments = new List<ExpressionSyntax>();
        if (Current.Kind == SyntaxTokenKind.OpenParen)
        {
            NextToken();
            SkipSeparators();
            while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
            {
                arguments.Add(ParseExpression());
                SkipSeparators();
                if (Current.Kind == SyntaxTokenKind.Comma) { NextToken(); SkipSeparators(); }
            }
            Match(SyntaxTokenKind.CloseParen, "Expected ')' after case arguments.");
        }

        return new DotCaseExpressionSyntax(caseName, arguments);
    }

    string PreviousText() => _tokens[Math.Max(0, _position - 1)].Text;

    static object ParseNumber(string text) =>
        text.Contains('.') ? (object)double.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : (object)int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    static string ParseString(string text) => text.Length >= 2 ? text[1..^1] : string.Empty;

    ParenthesizedExpressionSyntax ParseParenthesizedExpression()
    {
        Match(SyntaxTokenKind.OpenParen);
        var expression = ParseExpression();
        Match(SyntaxTokenKind.CloseParen, "Expected ')' after expression.");
        return new ParenthesizedExpressionSyntax(expression);
    }

    FunctionLiteralExpressionSyntax ParseFunctionLiteral()
    {
        Match(SyntaxTokenKind.FuncKeyword);
        Match(SyntaxTokenKind.OpenParen, "Expected '(' after 'func' in function literal.");

        var parameters = new List<ParameterSyntax>();
        SkipSeparators();
        while (Current.Kind is not (SyntaxTokenKind.CloseParen or SyntaxTokenKind.EndOfFile))
        {
            var startPosition = Current.Position;
            var paramName = Match(SyntaxTokenKind.Identifier, "Expected parameter name.").Text;
            Match(SyntaxTokenKind.Colon, "Expected ':' after parameter name.");
            var type = ParseTypeNameUntil(SyntaxTokenKind.Comma, SyntaxTokenKind.CloseParen);
            parameters.Add(new ParameterSyntax(paramName, type));

            if (Current.Kind == SyntaxTokenKind.Comma) NextToken();
            SkipSeparators();

            if (Current.Position == startPosition && Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();
        }

        Match(SyntaxTokenKind.CloseParen, "Expected ')' after parameters.");

        TypeReferenceSyntax returnType;
        if (Current.Kind == SyntaxTokenKind.Arrow)
        {
            NextToken();
            returnType = ParseTypeNameUntil(SyntaxTokenKind.OpenBrace, SyntaxTokenKind.NewLine);
        }
        else
        {
            returnType = new TypeReferenceSyntax("void");
        }

        SkipSeparators();
        var body = ParseBlockStatement();
        return new FunctionLiteralExpressionSyntax(parameters, returnType, body);
    }

    SpawnExpressionSyntax ParseSpawnExpression()
    {
        Match(SyntaxTokenKind.SpawnKeyword);
        SkipSeparators();
        var block = ParseBlockStatement();
        return new SpawnExpressionSyntax(block);
    }

    ExpressionSyntax ParseMissingExpression()
    {
        _diagnostics.Report(_filePath, Current.Line, Current.Column, $"Unexpected token '{Current.Text}' in expression.");
        return new LiteralExpressionSyntax(null, "null");
    }

    static int GetUnaryPrecedence(SyntaxTokenKind kind) =>
        kind switch
        {
            SyntaxTokenKind.Bang or SyntaxTokenKind.Minus or SyntaxTokenKind.NotKeyword or SyntaxTokenKind.Star => 7,
            _ => 0,
        };

    static int GetBinaryPrecedence(SyntaxTokenKind kind) =>
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
