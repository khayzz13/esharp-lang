using Esharp.Compiler.Diagnostics;
using Esharp.Compiler.Syntax;

namespace Esharp.Compiler.Binding;

public sealed class Binder
{
    readonly DiagnosticBag _diagnostics = new();
    readonly Dictionary<string, BoundType> _typeRegistry = new(StringComparer.Ordinal);
    readonly Dictionary<string, ChoiceDeclarationSyntax> _choiceDecls = new(StringComparer.Ordinal);
    readonly Dictionary<string, DataDeclarationSyntax> _dataDecls = new(StringComparer.Ordinal);
    readonly Dictionary<string, EnumDeclarationSyntax> _enumDecls = new(StringComparer.Ordinal);
    readonly Dictionary<string, BoundType> _functionReturnTypes = new(StringComparer.Ordinal);
    readonly Dictionary<string, FunctionDeclarationSyntax> _functionDecls = new(StringComparer.Ordinal);
    readonly Dictionary<string, ProtocolDeclarationSyntax> _protocolDecls = new(StringComparer.Ordinal);
    BinderScope _scope = BinderScope.Root();
    BoundType _currentReturnType = new VoidType();
    bool _currentFunctionHasAwait;
    int _tempCounter;

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics.Diagnostics;

    /// <summary>Single-file convenience: registers types then binds.</summary>
    public BoundCompilationUnit Bind(CompilationUnitSyntax syntax)
    {
        RegisterTypes(syntax);
        return BindUnit(syntax);
    }

    /// <summary>Pass 1: register all type and function declarations into shared registries.
    /// Call this for every file before calling BindUnit on any file.</summary>
    public void RegisterTypes(CompilationUnitSyntax syntax)
    {
        foreach (var member in syntax.Members)
        {
            switch (member)
            {
                case DataDeclarationSyntax data:
                    var dataType = new DataType(data.Name, data.TypeParameters, data);
                    _typeRegistry[data.Name] = dataType;
                    _dataDecls[data.Name] = data;
                    break;
                case ChoiceDeclarationSyntax choice:
                    var choiceType = new ChoiceType(choice.Name, choice.TypeParameters, choice);
                    _typeRegistry[choice.Name] = choiceType;
                    _choiceDecls[choice.Name] = choice;
                    break;
                case EnumDeclarationSyntax enumDecl:
                    var enumType = new EnumType(enumDecl.Name, enumDecl);
                    _typeRegistry[enumDecl.Name] = enumType;
                    _enumDecls[enumDecl.Name] = enumDecl;
                    break;
                case ProtocolDeclarationSyntax proto:
                    _protocolDecls[proto.Name] = proto;
                    break;
                case FunctionDeclarationSyntax func:
                    _functionReturnTypes[func.Name] = ResolveType(func.ReturnType);
                    _functionDecls[func.Name] = func;
                    break;
            }
        }
    }

    /// <summary>Passes 2-4: bind functions, determine instance methods, bind types.
    /// All types must be registered (via RegisterTypes) before calling this.</summary>
    public BoundCompilationUnit BindUnit(CompilationUnitSyntax syntax)
    {
        // Pass 2: bind all functions first (needed for instance method promotion)
        var allBoundFunctions = new List<BoundFunctionDeclaration>();
        foreach (var func in syntax.Members.OfType<FunctionDeclarationSyntax>())
            allBoundFunctions.Add(BindFunction(func));

        // Pass 3: determine instance methods per data type
        var instanceMethodsByType = new Dictionary<string, List<BoundFunctionDeclaration>>(StringComparer.Ordinal);
        var staticFunctions = new List<BoundFunctionDeclaration>();
        foreach (var bf in allBoundFunctions)
        {
            if (bf.Parameters.Count > 0)
            {
                var firstParamType = bf.Parameters[0].Type;
                var typeName = firstParamType is DataType dt ? dt.Name
                    : firstParamType is ExternalType et && _dataDecls.ContainsKey(et.Name) ? et.Name
                    : null;
                if (typeName is not null)
                {
                    if (!instanceMethodsByType.TryGetValue(typeName, out var list))
                    {
                        list = new List<BoundFunctionDeclaration>();
                        instanceMethodsByType[typeName] = list;
                    }
                    list.Add(bf);
                    continue;
                }
            }
            staticFunctions.Add(bf);
        }

        // Pass 4: bind data (with instance methods + implicit protocol satisfaction)
        var localDataNames = new HashSet<string>(StringComparer.Ordinal);
        var boundMembers = new List<BoundMember>();
        foreach (var member in syntax.Members)
        {
            switch (member)
            {
                case ProtocolDeclarationSyntax proto:
                    boundMembers.Add(BindProtocol(proto));
                    break;
                case DataDeclarationSyntax data:
                    localDataNames.Add(data.Name);
                    // Collect instance methods from all files for this data type
                    var methods = instanceMethodsByType.GetValueOrDefault(data.Name) ?? [];
                    boundMembers.Add(BindData(data, methods));
                    break;
                case ChoiceDeclarationSyntax choice:
                    boundMembers.Add(BindChoice(choice));
                    break;
                case EnumDeclarationSyntax enumDecl:
                    boundMembers.Add(new BoundEnumDeclaration(enumDecl.Name, enumDecl.Cases));
                    break;
            }
        }

        // Cross-file instance methods: emit partial struct blocks for data types from other files
        foreach (var (typeName, methods) in instanceMethodsByType)
        {
            if (localDataNames.Contains(typeName)) continue; // already handled above
            if (!_dataDecls.TryGetValue(typeName, out var remoteData)) continue;

            // Create a partial data declaration with no fields, just the cross-file instance methods
            boundMembers.Add(new BoundDataDeclaration(
                typeName, remoteData.TypeParameters, [], [], [], methods));
        }

        // Add remaining static functions
        foreach (var sf in staticFunctions)
            boundMembers.Add(sf);

        return new BoundCompilationUnit(syntax.ModuleName, syntax.Imports, boundMembers);
    }

    BoundDataDeclaration BindData(DataDeclarationSyntax syntax, List<BoundFunctionDeclaration> instanceMethods)
    {
        var prevScope = _scope;
        _scope = _scope.Child();
        foreach (var tp in syntax.TypeParameters)
            _scope.Declare(tp, new PrimitiveType(tp));

        var fields = syntax.Fields.Select(f => new BoundField(f.Name, ResolveType(f.Type))).ToList();

        // Split derive traits (prefixed with #) from real interfaces
        var interfaces = new List<string>();
        var deriveTraits = new List<string>();
        foreach (var iface in syntax.Interfaces)
        {
            if (iface.StartsWith('#'))
                deriveTraits.Add(iface[1..]);
            else
                interfaces.Add(iface);
        }

        // If derive equality, add IEquatable<T>
        if (deriveTraits.Contains("equality"))
            interfaces.Add($"IEquatable<{syntax.Name}>");

        // Check implicit protocol satisfaction
        foreach (var (protoName, protoDecl) in _protocolDecls)
        {
            if (interfaces.Contains(protoName)) continue;
            if (SatisfiesProtocol(protoDecl, instanceMethods))
                interfaces.Add(protoName);
        }

        _scope = prevScope;
        return new BoundDataDeclaration(syntax.Name, syntax.TypeParameters, interfaces, deriveTraits, fields, instanceMethods);
    }

    bool SatisfiesProtocol(ProtocolDeclarationSyntax protocol, List<BoundFunctionDeclaration> instanceMethods)
    {
        foreach (var method in protocol.Methods)
        {
            // Find an instance method matching by name and parameter count (excluding self param)
            var match = instanceMethods.FirstOrDefault(m =>
                m.Name == method.Name && m.Parameters.Count - 1 == method.Parameters.Count);
            if (match is null) return false;
        }
        return true;
    }

    BoundProtocolDeclaration BindProtocol(ProtocolDeclarationSyntax syntax)
    {
        var methods = syntax.Methods.Select(m =>
        {
            var parameters = m.Parameters.Select(p => new BoundParameter(p.Name, ResolveType(p.Type), p.Type.ByRef)).ToList();
            return new BoundProtocolMethod(m.Name, parameters, ResolveType(m.ReturnType));
        }).ToList();
        return new BoundProtocolDeclaration(syntax.Name, methods);
    }

    BoundChoiceDeclaration BindChoice(ChoiceDeclarationSyntax syntax)
    {
        var prevScope = _scope;
        _scope = _scope.Child();
        foreach (var tp in syntax.TypeParameters)
            _scope.Declare(tp, new PrimitiveType(tp));

        var cases = syntax.Cases.Select(c =>
            new BoundChoiceCase(c.Name, c.PayloadName, c.PayloadType is not null ? ResolveType(c.PayloadType) : null)
        ).ToList();

        _scope = prevScope;
        return new BoundChoiceDeclaration(syntax.Name, syntax.TypeParameters, cases);
    }

    BoundFunctionDeclaration BindFunction(FunctionDeclarationSyntax syntax)
    {
        var prevScope = _scope;
        var prevReturn = _currentReturnType;
        var prevHasAwait = _currentFunctionHasAwait;
        _scope = _scope.Child();
        _currentFunctionHasAwait = false;

        // Register type parameters in scope so T, U etc resolve
        foreach (var tp in syntax.TypeParameters)
            _scope.Declare(tp, new PrimitiveType(tp));

        var returnType = ResolveType(syntax.ReturnType);
        _currentReturnType = returnType;

        var parameters = new List<BoundParameter>();
        foreach (var p in syntax.Parameters)
        {
            var paramType = ResolveType(p.Type);
            parameters.Add(new BoundParameter(p.Name, paramType, p.Type.ByRef));
            _scope.Declare(p.Name, paramType);
        }

        var body = BindBlock(syntax.Body);
        var hasAwait = _currentFunctionHasAwait;

        _scope = prevScope;
        _currentReturnType = prevReturn;
        _currentFunctionHasAwait = prevHasAwait;
        return new BoundFunctionDeclaration(syntax.Name, syntax.TypeParameters, parameters, returnType, body, hasAwait);
    }

    // === Statements ===

    BoundBlockStatement BindBlock(BlockStatementSyntax syntax)
    {
        var statements = syntax.Statements.Select(BindStatement).ToList();
        return new BoundBlockStatement(statements);
    }

    BoundStatement BindStatement(StatementSyntax syntax) =>
        syntax switch
        {
            VariableDeclarationStatementSyntax v => BindVariableDeclaration(v),
            LetGuardStatementSyntax g => BindLetGuard(g),
            AssignmentStatementSyntax a => BindAssignment(a),
            CompoundAssignmentStatementSyntax c => BindCompoundAssignment(c),
            IfStatementSyntax i => BindIf(i),
            WhileStatementSyntax w => BindWhile(w),
            ForEachStatementSyntax f => BindForEach(f),
            ReturnStatementSyntax r => BindReturn(r),
            ExpressionStatementSyntax e => new BoundExpressionStatement(BindExpression(e.Expression)),
            BlockStatementSyntax b => BindBlock(b),
            MatchStatementSyntax m => BindMatch(m),
            DeferStatementSyntax d => new BoundDeferStatement(BindBlock(d.Body)),
            _ => throw new NotSupportedException($"Cannot bind statement type '{syntax.GetType().Name}'."),
        };

    BoundStatement BindVariableDeclaration(VariableDeclarationStatementSyntax syntax)
    {
        // Handle ? unwrap specially
        if (syntax.Initializer is TryUnwrapExpressionSyntax tryUnwrap)
        {
            var inner = BindExpression(tryUnwrap.Inner);
            var unwrappedType = inner.Type is ResultType rt ? rt.OkType : inner.Type;
            var tempName = $"__r{_tempCounter++}";
            var bound = new BoundTryUnwrapExpression(inner, unwrappedType, tempName);
            var declType = syntax.ExplicitType is not null ? ResolveType(syntax.ExplicitType) : unwrappedType;
            _scope.Declare(syntax.Name, declType);
            return new BoundVariableDeclaration(syntax.Mutable, syntax.Name, declType, bound);
        }

        var initializer = BindExpression(syntax.Initializer);
        var type = syntax.ExplicitType is not null ? ResolveType(syntax.ExplicitType) : initializer.Type;
        _scope.Declare(syntax.Name, type);
        return new BoundVariableDeclaration(syntax.Mutable, syntax.Name, type, initializer);
    }

    BoundLetGuard BindLetGuard(LetGuardStatementSyntax syntax)
    {
        BoundExpression initializer;
        if (syntax.Initializer is TryUnwrapExpressionSyntax tu)
            initializer = BindExpression(tu.Inner);
        else
            initializer = BindExpression(syntax.Initializer);

        var type = syntax.ExplicitType is not null ? ResolveType(syntax.ExplicitType) : initializer.Type;
        _scope.Declare(syntax.Name, type);
        var elseBody = BindBlock(syntax.ElseBody);
        return new BoundLetGuard(syntax.Name, type, initializer, elseBody);
    }

    BoundAssignment BindAssignment(AssignmentStatementSyntax syntax) =>
        new(BindExpression(syntax.Target), BindExpression(syntax.Expression));

    BoundCompoundAssignment BindCompoundAssignment(CompoundAssignmentStatementSyntax syntax) =>
        new(BindExpression(syntax.Target), syntax.Operator, BindExpression(syntax.Value));

    BoundIfStatement BindIf(IfStatementSyntax syntax) =>
        new(BindExpression(syntax.Condition), BindStatement(syntax.ThenStatement),
            syntax.ElseStatement is not null ? BindStatement(syntax.ElseStatement) : null);

    BoundWhileStatement BindWhile(WhileStatementSyntax syntax) =>
        new(BindExpression(syntax.Condition), BindStatement(syntax.Body));

    BoundForEachStatement BindForEach(ForEachStatementSyntax syntax)
    {
        var collection = BindExpression(syntax.Collection);
        // Infer element type — best effort
        var prevScope = _scope;
        _scope = _scope.Child();
        _scope.Declare(syntax.Identifier, new ExternalType("var"));
        var body = BindStatement(syntax.Body);
        _scope = prevScope;
        return new BoundForEachStatement(syntax.Identifier, collection, body);
    }

    BoundReturnStatement BindReturn(ReturnStatementSyntax syntax) =>
        new(syntax.Expression is not null ? BindExpression(syntax.Expression) : null);

    BoundMatchStatement BindMatch(MatchStatementSyntax syntax)
    {
        var subject = BindExpression(syntax.Subject);

        // Resolve subject type: explicit annotation takes priority, otherwise use bound type
        BoundType subjectType;
        if (syntax.SubjectType is not null)
            subjectType = ResolveType(syntax.SubjectType);
        else
            subjectType = subject.Type;

        var arms = new List<BoundMatchArm>();
        foreach (var arm in syntax.Arms)
        {
            var prevScope = _scope;
            _scope = _scope.Child();

            BoundType? bindingType = null;
            if (!arm.Pattern.IsDefault && arm.Pattern.BindingName is not null && subjectType is ChoiceType ct)
            {
                var caseDecl = ct.Decl.Cases.FirstOrDefault(c => c.Name == arm.Pattern.CaseName);
                if (caseDecl?.PayloadType is not null)
                {
                    bindingType = ResolveType(caseDecl.PayloadType);
                    _scope.Declare(arm.Pattern.BindingName, bindingType);
                }
            }

            var pattern = new BoundMatchPattern(arm.Pattern.CaseName, arm.Pattern.BindingName, bindingType, arm.Pattern.IsDefault);
            var body = BindBlock(arm.Body);
            arms.Add(new BoundMatchArm(pattern, body));
            _scope = prevScope;
        }

        return new BoundMatchStatement(subject, subjectType, arms);
    }

    // === Expressions ===

    BoundExpression BindExpression(ExpressionSyntax syntax) =>
        syntax switch
        {
            LiteralExpressionSyntax lit => BindLiteral(lit),
            NameExpressionSyntax name => BindName(name),
            UnaryExpressionSyntax unary => BindUnary(unary),
            BinaryExpressionSyntax binary => BindBinary(binary),
            MemberAccessExpressionSyntax ma => BindMemberAccess(ma),
            CallExpressionSyntax call => BindCall(call),
            ObjectCreationExpressionSyntax oc => BindObjectCreation(oc),
            ParenthesizedExpressionSyntax p => new BoundParenthesizedExpression(BindExpression(p.Expression)),
            IndexExpressionSyntax idx => BindIndex(idx),
            RangeExpressionSyntax range => BindRange(range),
            SpawnExpressionSyntax spawn => new BoundSpawnExpression(BindBlock(spawn.Body)),
            ChanCreationExpressionSyntax chan => new BoundChanCreationExpression(ResolveType(chan.ElementType), chan.Capacity is not null ? BindExpression(chan.Capacity) : null),
            DotCaseExpressionSyntax dot => BindDotCase(dot),
            FunctionLiteralExpressionSyntax fl => BindFunctionLiteral(fl),
            AddressOfExpressionSyntax ao => BindAddressOf(ao),
            TryUnwrapExpressionSyntax tu => BindTryUnwrap(tu),
            AwaitExpressionSyntax aw => BindAwait(aw),
            _ => throw new NotSupportedException($"Cannot bind expression type '{syntax.GetType().Name}'."),
        };

    BoundLiteralExpression BindLiteral(LiteralExpressionSyntax syntax)
    {
        var type = syntax.Value switch
        {
            null => (BoundType)new NullType(),
            int => new PrimitiveType("int"),
            double => new PrimitiveType("double"),
            bool => new PrimitiveType("bool"),
            string => new PrimitiveType("string"),
            _ => new ExternalType("object"),
        };
        return new BoundLiteralExpression(syntax.Value, syntax.Text, type);
    }

    BoundNameExpression BindName(NameExpressionSyntax syntax)
    {
        var type = _scope.Lookup(syntax.Name) ?? new ExternalType(syntax.Name);
        return new BoundNameExpression(syntax.Name, type);
    }

    BoundUnaryExpression BindUnary(UnaryExpressionSyntax syntax)
    {
        var operand = BindExpression(syntax.Operand);
        return new BoundUnaryExpression(syntax.OperatorKind, operand, operand.Type);
    }

    BoundBinaryExpression BindBinary(BinaryExpressionSyntax syntax)
    {
        var left = BindExpression(syntax.Left);
        var right = BindExpression(syntax.Right);
        var resultType = syntax.OperatorKind switch
        {
            SyntaxTokenKind.EqualsEquals or SyntaxTokenKind.BangEquals or
            SyntaxTokenKind.Less or SyntaxTokenKind.LessEquals or
            SyntaxTokenKind.Greater or SyntaxTokenKind.GreaterEquals or
            SyntaxTokenKind.AmpAmp or SyntaxTokenKind.PipePipe or
            SyntaxTokenKind.AndKeyword or SyntaxTokenKind.OrKeyword => (BoundType)new PrimitiveType("bool"),
            _ => left.Type, // arithmetic — type of left operand
        };
        return new BoundBinaryExpression(left, syntax.OperatorKind, right, resultType);
    }

    BoundMemberAccessExpression BindMemberAccess(MemberAccessExpressionSyntax syntax)
    {
        var target = BindExpression(syntax.Target);
        var memberType = ResolveMemberType(target.Type, syntax.MemberName);
        return new BoundMemberAccessExpression(target, syntax.MemberName, memberType);
    }

    BoundExpression BindCall(CallExpressionSyntax syntax)
    {
        // Special-case ok()/error() when return type is Result<T,E>
        if (syntax.Target is NameExpressionSyntax bareName && _currentReturnType is ResultType rt)
        {
            if (bareName.Name == "ok" && syntax.Arguments.Count == 1)
                return new BoundResultCallExpression(true, BindExpression(syntax.Arguments[0]), rt.OkType, rt.ErrorType);
            if (bareName.Name == "error" && syntax.Arguments.Count == 1)
                return new BoundResultCallExpression(false, BindExpression(syntax.Arguments[0]), rt.OkType, rt.ErrorType);
        }

        var target = BindExpression(syntax.Target);
        var args = syntax.Arguments.Select(BindExpression).ToList();
        // Try to resolve return type from known functions
        BoundType returnType = new ExternalType("var");
        if (target is BoundNameExpression nameTarget && _functionReturnTypes.TryGetValue(nameTarget.Name, out var knownReturn))
            returnType = knownReturn;
        return new BoundCallExpression(target, args, returnType);
    }

    BoundObjectCreationExpression BindObjectCreation(ObjectCreationExpressionSyntax syntax)
    {
        var type = ResolveType(syntax.Type);
        var fields = syntax.Fields.Select(f => new BoundFieldInit(f.Name, BindExpression(f.Value))).ToList();
        return new BoundObjectCreationExpression(type, fields);
    }

    BoundIndexExpression BindIndex(IndexExpressionSyntax syntax)
    {
        var target = BindExpression(syntax.Target);
        var index = BindExpression(syntax.Index);
        return new BoundIndexExpression(target, index, new ExternalType("var"));
    }

    BoundRangeExpression BindRange(RangeExpressionSyntax syntax)
    {
        var target = BindExpression(syntax.Target);
        var start = syntax.Start is not null ? BindExpression(syntax.Start) : null;
        var end = syntax.End is not null ? BindExpression(syntax.End) : null;
        return new BoundRangeExpression(target, start, end, target.Type);
    }

    BoundDotCaseExpression BindDotCase(DotCaseExpressionSyntax syntax)
    {
        var args = syntax.Arguments.Select(BindExpression).ToList();

        // Special-case ok/error with Result return type
        if (_currentReturnType is ResultType rt2)
        {
            if (syntax.CaseName == "ok" && args.Count == 1)
                return new BoundDotCaseExpression("ok", "Result", args, rt2);
            if (syntax.CaseName == "error" && args.Count == 1)
                return new BoundDotCaseExpression("error", "Result", args, rt2);
        }

        // Search choice decls
        foreach (var (name, decl) in _choiceDecls)
        {
            if (decl.Cases.Any(c => c.Name == syntax.CaseName))
                return new BoundDotCaseExpression(syntax.CaseName, name, args, _typeRegistry[name]);
        }

        // Search enum decls
        foreach (var (name, decl) in _enumDecls)
        {
            if (decl.Cases.Contains(syntax.CaseName))
                return new BoundDotCaseExpression(syntax.CaseName, name, args, _typeRegistry[name]);
        }

        return new BoundDotCaseExpression(syntax.CaseName, "", args, new ExternalType("var"));
    }

    BoundAddressOfExpression BindAddressOf(AddressOfExpressionSyntax syntax)
    {
        if (_functionDecls.TryGetValue(syntax.FunctionName, out var funcDecl))
        {
            var paramTypes = funcDecl.Parameters.Select(p => ResolveType(p.Type)).ToList();
            var returnType = _functionReturnTypes.TryGetValue(syntax.FunctionName, out var rt) ? rt : new VoidType();
            return new BoundAddressOfExpression(syntax.FunctionName, paramTypes, returnType);
        }

        // Unknown function — emit with empty signature, let downstream handle it
        return new BoundAddressOfExpression(syntax.FunctionName, [], new VoidType());
    }

    BoundFunctionLiteralExpression BindFunctionLiteral(FunctionLiteralExpressionSyntax syntax)
    {
        var prevScope = _scope;
        _scope = _scope.Child();

        var parameters = new List<BoundParameter>();
        foreach (var p in syntax.Parameters)
        {
            var paramType = ResolveType(p.Type);
            parameters.Add(new BoundParameter(p.Name, paramType, p.Type.ByRef));
            _scope.Declare(p.Name, paramType);
        }

        var returnType = ResolveType(syntax.ReturnType);
        var body = BindBlock(syntax.Body);

        _scope = prevScope;
        return new BoundFunctionLiteralExpression(parameters, returnType, body);
    }

    BoundTryUnwrapExpression BindTryUnwrap(TryUnwrapExpressionSyntax syntax)
    {
        var inner = BindExpression(syntax.Inner);
        var unwrappedType = inner.Type is ResultType rt ? rt.OkType : inner.Type;
        var tempName = $"__r{_tempCounter++}";
        return new BoundTryUnwrapExpression(inner, unwrappedType, tempName);
    }

    BoundAwaitExpression BindAwait(AwaitExpressionSyntax syntax)
    {
        var inner = BindExpression(syntax.Inner);
        _currentFunctionHasAwait = true;
        // Result type: for now ExternalType("var") — Roslyn infers the actual T from Task<T>/ValueTask<T>
        // The IL compiler will need proper type extraction later
        BoundType resultType = new ExternalType("var");
        return new BoundAwaitExpression(inner, resultType);
    }

    // === Type resolution ===

    BoundType ResolveType(TypeReferenceSyntax syntax)
    {
        var name = syntax.Name;

        if (_typeRegistry.TryGetValue(name, out var registered))
            return registered;

        // Generic instantiation: strip type args and look up base name
        // e.g. "Pair<int, string>" → "Pair"
        var angleIdx = name.IndexOf('<');
        if (angleIdx > 0)
        {
            var baseName = name[..angleIdx];
            if (_typeRegistry.TryGetValue(baseName, out var genericBase))
                return genericBase;
        }

        // Result<T,E>
        if (name.StartsWith("Result<", StringComparison.Ordinal) && name.EndsWith('>'))
        {
            var inner = name["Result<".Length..^1];
            var depth = 0;
            for (var i = 0; i < inner.Length; i++)
            {
                switch (inner[i])
                {
                    case '<': depth++; break;
                    case '>': depth--; break;
                    case ',' when depth == 0:
                        var okName = inner[..i].Trim();
                        var errName = inner[(i + 1)..].Trim();
                        return new ResultType(
                            ResolveType(new TypeReferenceSyntax(okName)),
                            ResolveType(new TypeReferenceSyntax(errName)));
                }
            }
        }

        // Chan<T>
        if (name.StartsWith("Chan<", StringComparison.Ordinal) && name.EndsWith('>'))
        {
            var elementName = name["Chan<".Length..^1].Trim();
            return new ChanType(ResolveType(new TypeReferenceSyntax(elementName)));
        }

        // Primitives
        return name switch
        {
            "void" => new VoidType(),
            "int" or "string" or "bool" or "float" or "double" or "byte" or "char" or "long" or "short"
                or "uint" or "ulong" or "ushort" or "sbyte" or "decimal" => new PrimitiveType(name),
            "Guid" or "DateTimeOffset" or "DateTime" or "TimeSpan" => new PrimitiveType(name),
            _ => new ExternalType(name),
        };
    }

    BoundType ResolveMemberType(BoundType targetType, string memberName)
    {
        if (targetType is DataType dt && _dataDecls.TryGetValue(dt.Name, out var dataDecl))
        {
            var field = dataDecl.Fields.FirstOrDefault(f => f.Name == memberName);
            if (field is not null)
                return ResolveType(field.Type);
        }
        return new ExternalType("var");
    }
}
