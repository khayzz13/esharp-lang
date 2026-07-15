using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Symbols;
using Esharp.Syntax;

// Moved to Esharp.Binder (Pillar 2 — B2) per module-map.md.
// Consumes: frozen BoundTree/Symbol/Diagnostic contract (Pillar 1).
// [Δ] MaybeNarrow now produces BoundConversion (CORE) instead of
//     BoundNarrowedExpression (FEATURE) for smart-cast narrowings resolved at
//     bind time — flows from NarrowingAnalyzer.Extract(). FEATURE cast nodes
//     (BoundSafeCastExpression / BoundAssertCastExpression) are preserved intact
//     for lowering (MatchLowering / NullFlowLowering own their elimination).
// [Δ] NarrowingAnalyzer.Extract output is exposed via INarrowingFactsSink so
//     FlowAnalysis.NullStateFlow can consume the per-site narrow set without
//     a rebind.
namespace Esharp.Binder;

/// Expression binding: the dispatch (and the central span stamp), name/member/call
/// resolution and typing, object construction, literals + interpolation, function
/// literals + capture analysis, await/spawn, and the reflection-driven call-shape
/// inference. Calls and expressions are one unit — they are mutually entangled
/// through call typing (the coupling map's finding).
internal sealed class ExpressionBinder : BinderUnit
{
    internal ExpressionBinder(Binder binder) : base(binder) { }


    // === Expressions ===

    // The single expression dispatch — also the single span stamp. Every bound
    // expression gets its syntax range here, centrally; constructors never stamp.
    // A node that already carries a valid span is left alone: folded constants are
    // shared instances whose span is their declaration site, and restamping would
    // smear the last use's location across every earlier one.
    internal BoundExpression BindExpression(ExpressionSyntax syntax, BoundType? expectedType = null)
    {
        var bound = BindExpressionCore(syntax, expectedType);
        if (!bound.Span.IsValid)
            bound.Span = syntax.Span;
        return MaybeNarrow(bound, syntax);
    }

    // Apply an active smart-cast to a name / field-path read (§type-narrowing). Centralized
    // here so both `x` (a name narrow) and `a.b` (a field-path narrow) refine through the
    // one chokepoint — within the narrowed region the read's BoundType IS the narrowed type,
    // and member resolution / call dispatch proceed against it naturally.
    //
    // [Δ] Produces BoundConversion(inner, narrowedType, ConversionKind.Narrow) — a CORE node
    // that codegen emits as one castclass/unbox.any at the use site. The old
    // BoundNarrowedExpression (FEATURE) is eliminated here so lowering has nothing to do for
    // the smart-cast case. The BoundConversion node is what NullStateFlow observes as well —
    // every narrowed read is stamped with a ConversionKind.Narrow so the null-state lattice
    // can track the post-narrow type without re-deriving it from the path map.
    BoundExpression MaybeNarrow(BoundExpression bound, ExpressionSyntax syntax)
    {
        if (bound is BoundErrorExpression or BoundConversion) return bound;
        if (PathOf(syntax) is not { } path) return bound;
        if (LookupNarrow(path) is { } narrowed
            && TypeResolver.TypeDisplayName(narrowed) != TypeResolver.TypeDisplayName(bound.Type))
            return new BoundConversion(bound, narrowed, ConversionKind.Narrow);
        return bound;
    }

    // The stable access path of a read expression — a name or a single-level
    // `recv.member` chain — or null for anything else. The key narrows are recorded under.
    static string? PathOf(ExpressionSyntax e) => e switch
    {
        NameExpressionSyntax n => n.Name,
        MemberAccessExpressionSyntax { Target: NameExpressionSyntax r, MemberName: var m } => $"{r.Name}.{m}",
        _ => null,
    };

    // The active narrowed type for a path, honoring call-staleness on field paths.
    BoundType? LookupNarrow(string path) =>
        Ctx.Narrowed.TryGetValue(path, out var f) && (!f.CallSensitive || f.Generation == Ctx.CallGeneration)
            ? f.Type : null;

    // The type a path was *poisoned*-narrowed to (a `var`, or a field narrow a call has
    // since invalidated) — the reliance a member access should be diagnosed against.
    BoundType? PoisonedTypeFor(string path)
    {
        if (Ctx.PoisonedNarrows.TryGetValue(path, out var t)) return t;
        if (Ctx.Narrowed.TryGetValue(path, out var f) && f.CallSensitive && f.Generation != Ctx.CallGeneration)
            return f.Type;
        return null;
    }

    BoundExpression BindExpressionCore(ExpressionSyntax syntax, BoundType? expectedType) =>
        syntax switch
        {
            LiteralExpressionSyntax lit => BindLiteral(lit, expectedType),
            NameExpressionSyntax name => BindNameOrConstFold(name, expectedType),
            UnaryExpressionSyntax unary => BindUnary(unary),
            BinaryExpressionSyntax binary => BindBinary(binary),
            MemberAccessExpressionSyntax ma => BindMemberAccess(ma),
            TypeTestExpressionSyntax tt => BindTypeTest(tt),
            CastExpressionSyntax ce => BindCast(ce),
            CallExpressionSyntax call => BindCallBumping(call),
            ObjectCreationExpressionSyntax oc => BindObjectCreation(oc),
            WithExpressionSyntax w => BindWith(w),
            ConditionalExpressionSyntax cond => BindConditional(cond),
            NullCoalescingExpressionSyntax nc => BindNullCoalescing(nc),
            NullConditionalAccessExpressionSyntax nca => BindNullConditionalAccess(nca),
            ListLiteralExpressionSyntax list => BindListLiteral(list),
            ArrayCreationExpressionSyntax ac => BindArrayCreation(ac),
            TupleExpressionSyntax tuple => BindTupleLiteral(tuple),
            // BoundParenthesizedExpression is dropped in the frozen contract —
            // the binder unwraps parenthesized expressions in-place so they never
            // enter the bound tree. The span stamp below will use the inner's span.
            ParenthesizedExpressionSyntax p => BindExpression(p.Expression),
            IndexExpressionSyntax idx => BindIndex(idx),
            RangeExpressionSyntax range => BindRange(range),
            SpawnExpressionSyntax spawn => BindSpawn(spawn),
            ChanCreationExpressionSyntax chan => new BoundChanCreationExpression(ResolveType(chan.ElementType), chan.Capacity is not null ? BindExpression(chan.Capacity) : null),
            DotCaseExpressionSyntax dot => BindDotCase(dot),
            FunctionLiteralExpressionSyntax fl => BindFunctionLiteral(fl, expectedType),
            AddressOfExpressionSyntax ao => BindAddressOf(ao),
            NewExpressionSyntax ne => BindNew(ne),
            DefaultExpressionSyntax def => BindDefault(def, expectedType),
            OutArgumentExpressionSyntax outArg => new BoundOutArgumentExpression(outArg.Name, InferredType.Instance, outArg.DeclaresLocal),
            TryUnwrapExpressionSyntax tu => BindTryUnwrap(tu),
            AwaitExpressionSyntax aw => BindAwait(aw),
            MatchExpressionSyntax me => Match.BindMatchExpression(me),
            IfExpressionSyntax ie => BindIfExpression(ie, expectedType),
            ErrorExpressionSyntax => new BoundErrorExpression(),
            _ => UnknownExpression(syntax),
        };

    // `if`/`else if`/`else` in expression position (plan 11 Part E). Each branch body is bound
    // in its own scope; its trailing expression — target-typed by `expectedType` so dot-cases
    // and literals resolve — is the branch value. A branch that diverges (`return`/`throw`)
    // yields no value and is exempt from the common-type check. An `else` is required; the
    // result type is `expectedType` when known, else the common type of the branch values.
    BoundExpression BindIfExpression(IfExpressionSyntax syntax, BoundType? expectedType)
    {
        var want = expectedType is not null and not InferredType ? expectedType : null;
        var branches = new List<BoundIfExpressionBranch>();
        var valueTypes = new List<(BoundType Type, SourceSpan Span)>();

        foreach (var br in syntax.Branches)
        {
            var cond = BindExpression(br.Condition);
            var (body, value) = BindBranchBody(br.Body, want);
            branches.Add(new BoundIfExpressionBranch(cond, body, value));
            if (value is not null) valueTypes.Add((value.Type, br.Body.Span));
        }

        IReadOnlyList<BoundStatement> elseBody = Array.Empty<BoundStatement>();
        BoundExpression? elseValue = null;
        if (syntax.ElseBody is not null)
        {
            (elseBody, elseValue) = BindBranchBody(syntax.ElseBody, want);
            if (elseValue is not null) valueTypes.Add((elseValue.Type, syntax.ElseBody.Span));
        }
        else
        {
            Diagnostics.Report(syntax.Span,
                "ES2197: an `if` used as a value must have an `else` — every path must yield a value.");
        }

        // Common type: the expected type when known (branch values were bound against it),
        // otherwise the shared type of the non-diverging branch values. A mismatch asks for
        // an annotation rather than widening to a union (no union types).
        BoundType type;
        if (want is not null)
            type = want;
        else if (valueTypes.Count > 0)
        {
            type = valueTypes[0].Type;
            var name = TypeDisplayName(type);
            foreach (var (t, span) in valueTypes.Skip(1))
                if (TypeDisplayName(t) != name)
                {
                    Diagnostics.Report(span,
                        $"ES2198: `if` branches yield differing types ('{name}' vs '{TypeDisplayName(t)}') — give the binding an explicit type to reconcile them.");
                    break;
                }
        }
        else
            type = InferredType.Instance; // every branch diverges — value never produced

        return new BoundIfExpression(branches, elseBody, elseValue, type);
    }

    // Bind an `if`-expression branch block: leading statements in a fresh scope, then the
    // trailing expression as the branch value (target-typed). A block with no trailing
    // expression is valid only if it diverges (`return`/`throw`); otherwise it is an error.
    (IReadOnlyList<BoundStatement> Body, BoundExpression? Value) BindBranchBody(BlockStatementSyntax block, BoundType? want)
    {
        var prevScope = Scope;
        Scope = Scope.Child();
        try
        {
            var stmts = block.Statements;
            var body = new List<BoundStatement>();
            for (var i = 0; i < stmts.Count; i++)
            {
                if (i == stmts.Count - 1 && stmts[i] is ExpressionStatementSyntax es)
                    return (body, BindExpression(es.Expression, want));
                body.Add(Statements.BindStatement(stmts[i]));
            }
            if (body.Count > 0 && body[^1] is BoundReturnStatement or BoundThrowStatement)
                return (body, null);
            Diagnostics.Report(block.Span,
                "ES2199: an `if`-expression branch must end in a value expression, or diverge via `return` / `throw`.");
            return (body, null);
        }
        finally { Scope = prevScope; }
    }

    // Total bind: an expression node the dispatch doesn't know is an internal
    // compiler error (a new syntax node without a binder arm) — report it located
    // and keep binding instead of unwinding the whole pipeline.
    BoundExpression UnknownExpression(ExpressionSyntax syntax)
    {
        Diagnostics.Report(syntax.Span,
            $"ES9001: internal compiler error — no binder for expression node '{syntax.GetType().Name}'.");
        return new BoundErrorExpression();
    }

    // `default(T)` resolves T directly; a bare `default` is target-typed from the expected
    // type threaded into BindExpression (a parameter default, return, annotated `let`, or
    // assignment target). With no expected type the literal is unanchored — report it
    // rather than silently emit a zero of an inferred type.
    BoundExpression BindDefault(DefaultExpressionSyntax def, BoundType? expectedType)
    {
        if (def.Type is not null)
            return new BoundDefaultExpression(ResolveType(def.Type));
        if (expectedType is not null and not InferredType)
            return new BoundDefaultExpression(expectedType);
        Diagnostics.Report(def.Span,
            "ES2181: bare 'default' needs a target type — use it where the type is known (a typed parameter default, return, annotated 'let', or assignment), or write 'default(T)'.");
        return new BoundDefaultExpression(InferredType.Instance);
    }

    BoundExpression BindLiteral(LiteralExpressionSyntax syntax, BoundType? expectedType = null)
    {
        // String interpolation: the lowering pass splits the decoded template into
        // literal and hole-source segments (a bare `"""` raw string suppresses this —
        // its braces are verbatim). Each hole source is parsed + bound through the
        // normal pipeline, so operators, calls, ternaries, and payload-view projections
        // all resolve correctly.
        if (syntax.Value is string s && !syntax.SuppressInterpolation
            && StringLiteralLowering.SplitInterpolation(s) is { } segments)
        {
            var parts = new List<BoundInterpolationPart>(segments.Count);
            foreach (var seg in segments)
            {
                if (seg.HoleSource is { } holeText)
                {
                    var holeParser = new Parsing.Parser(holeText, "interpolation");
                    var holeExpr = BindExpression(holeParser.ParseStandaloneExpression());
                    foreach (var dg in holeParser.Diagnostics)
                        Diagnostics.Report(syntax.Span, $"interpolation hole: {dg.Message}");
                    parts.Add(new BoundInterpolationPart(null, holeExpr));
                }
                else
                {
                    parts.Add(new BoundInterpolationPart(seg.Text, null));
                }
            }
            return new BoundInterpolatedStringExpression(parts, new PrimitiveType("string"));
        }

        var type = syntax.Value switch
        {
            null => (BoundType)new NullType(),
            int => new PrimitiveType("int"),
            double => new PrimitiveType("double"),
            bool => new PrimitiveType("bool"),
            string => new PrimitiveType("string"),
            char => new PrimitiveType("char"),
            _ => new ExternalType("object"),
        };
        // A numeric literal adopts an expected numeric type — `0.0` lands as `float`, `5` as
        // `long`/`float`/`double`. A literal is a constant, so this is an exact retype (the
        // emitter converts the value to the slot type), not the forbidden implicit narrowing
        // of a runtime value. Only widening / literal-float forms; never double→int.
        if (expectedType is PrimitiveType { Name: var want } && type is PrimitiveType { Name: var got })
        {
            var retype = got switch
            {
                "int" => want is "long" or "ulong" or "uint" or "float" or "double",
                "double" => want is "float",
                _ => false,
            };
            if (retype) type = expectedType;
        }
        return new BoundLiteralExpression(syntax.Value, syntax.Text, type);
    }

    // Const-fold a bare name: function-body / static-func consts live in the
    // scope's constant table, namespace consts on the symbol table. Either way
    // the use site sees a BoundLiteralExpression directly — no slot, no Ldsfld.
    BoundExpression BindNameOrConstFold(NameExpressionSyntax syntax, BoundType? expectedType = null)
    {
        if (Scope.LookupConst(syntax.Name) is { } scopedLit)
            return scopedLit;
        if (Data.Symbols.TryGetConstant(syntax.Name)?.FoldedLiteral is BoundLiteralExpression nsLit)
            return nsLit;

        // Method-group → delegate conversion: a bare `func` name appearing where a
        // delegate type is expected becomes a delegate instance bound to that method.
        // Fires only when the name is a free function (not a local) and the target is
        // a delegate — never on an un-annotated `let` (no expected type → no conversion,
        // so the fn-ptr-vs-delegate choice stays explicit and no allocation is implied).
        if (expectedType is not null
            && Scope.Lookup(syntax.Name) is null
            && Data.Symbols.TryGetFunction(syntax.Name)?.Decl is { } fnDecl
            && TryGetExpectedDelegateShape(expectedType) is not null)
            return new BoundMethodGroupConversion(syntax.Name, fnDecl.Parameters.Count, expectedType);

        return BindName(syntax);
    }

    /// The interned TypeSymbol behind a nominal bound type, when it carries one —
    /// the handle the semantic sink reports for a type use.
    static Esharp.Symbols.TypeSymbol? TypeSymbolBehind(BoundType t) => t switch
    {
        DataType d => d.Symbol,
        ChoiceType c => c.Symbol,
        EnumType e => e.Symbol,
        InterfaceType i => i.Symbol,
        NamedDelegateType nd => nd.Symbol,
        StaticFuncType s => s.Symbol,
        _ => null,
    };

    BoundExpression BindName(NameExpressionSyntax syntax)
    {
        // Type alias (`using Baz = "Full.Type"`): substitute to the aliased type so
        // construction (`Baz()`) and static access (`Baz.x`) resolve against the real
        // type. A local of the same name shadows the alias (checked first).
        if (Scope.Lookup(syntax.Name) is null && Ctx.TypeAliases.TryGetValue(syntax.Name, out var aliasPath))
            return new BoundNameExpression(aliasPath, new ExternalType(aliasPath));

        var type = Scope.Lookup(syntax.Name);
        // Who the name resolved to — carried onto the produced node so downstream reads
        // identity from the spine (CodeGen's slot/type resolution, find-references)
        // instead of re-resolving the string. Set by whichever resolution branch fires;
        // null leaves the name with no spine identity (alias / external-only paths).
        Esharp.Symbols.ISymbol? sym = null;
        // A name that resolves to a scope local reports the SAME interned symbol the
        // declaration reported — reference identity is what makes find-references a
        // list lookup. Reported before the type fallbacks so a local always wins.
        if (type is not null && Scope.LookupLocal(syntax.Name) is { } localSym)
        {
            sym = localSym;
            Data.Sink.OnLocalResolved(localSym, syntax.Span, Semantics.SymbolOccurrence.Use);
        }
        else if (type is not null
            && Data.NamespaceStateScopes.TryGetValue(Ctx.CurrentNamespace, out var namespaceState)
            && namespaceState.ContainsKey(syntax.Name)
            && Data.Symbols.Host(Ctx.CurrentNamespace).Fields.FirstOrDefault(field =>
                field.Name == syntax.Name) is { } stateSymbol)
        {
            sym = stateSymbol;
            Data.Sink.OnFieldResolved(stateSymbol, syntax.Span, Semantics.SymbolOccurrence.Use);
        }
        // Primary-ctor capture: a bare name matching a header parameter of the
        // enclosing headered class reads the synthesized private capture field —
        // bound directly as `self.<name>`; the use marks the param captured.
        // Locals and method params shadow (the `type is null` guard above).
        if (type is null && Ctx.HeaderCapture is { } capture
            && capture.Params.TryGetValue(syntax.Name, out var capturedType))
        {
            capture.Captured.Add(syntax.Name);
            return new BoundMemberAccessExpression(
                new BoundNameExpression("self", capture.SelfType), syntax.Name, capturedType);
        }
        if (type is null && Data.Symbols.ResolveBound(syntax.Name, 0) is { } knownType)
        {
            type = knownType;
            // A bare type name in value position (a static receiver, a constructor
            // target) is a type use — report it so go-to-def / find-refs reach the
            // declaration symbol.
            if (TypeSymbolBehind(knownType) is { } tsym)
            {
                sym = tsym;
                Data.Sink.OnTypeResolved(tsym, syntax.Span, Semantics.SymbolOccurrence.Use);
            }
        }
        if (type is null && TryResolveRuntimeTypeByName(syntax.Name) is { } extRuntime)
        {
            type = new ExternalType(syntax.Name);
            if (Data.Sink is not Semantics.NullSemanticSink)
                Data.Sink.OnTypeResolved(Data.Externals.ForType(extRuntime), syntax.Span, Semantics.SymbolOccurrence.Use);
        }
        if (type is null && Ctx.StaticImports.ContainsKey(syntax.Name))
            type = new ExternalType(syntax.Name);

        // Undefined-name authority (ES2146). The `ExternalType(name)` fallback below
        // legitimately carries names the binder does not own: free-function call
        // targets (typed by the call path), namespace qualifiers, and type-shaped
        // names (PascalCase / generic text) the IL resolver later binds by
        // reflection. A *lowercase* name that is none of those is the classic
        // undefined-variable typo — the binder owns it, located, instead of the
        // backend's locationless `IL: undefined variable`.
        if (type is null
            && syntax.Name is not "self" // implicit receiver in inline `class` methods — the emitter owns it
            && !LooksLikeTypeName(syntax.Name)
            && !PrimitiveNames.Contains(syntax.Name) // `uint.MaxValue` — primitive aliases are static receivers
            && !Data.Symbols.HasFunction(syntax.Name)
            // A bare call inside a `static func` body reaches its siblings without
            // qualification — they are keyed `Host.name`.
            && (Ctx.CurrentStaticFuncName is not { } sfHost
                || !Data.Symbols.HasFunction($"{sfHost}.{syntax.Name}"))
            && !Data.Symbols.IsKnownNamespace(syntax.Name))
        {
            Diagnostics.Report(syntax.Span,
                $"ES2146: undefined name '{syntax.Name}' — no local, parameter, function, type, or namespace by this name is in scope.");
            return new BoundErrorExpression();
        }

        type ??= new ExternalType(syntax.Name);
        return new BoundNameExpression(syntax.Name, type) { Symbol = sym };
    }

    /// Make an IMPLICIT store / assignment coercion explicit as a BoundConversion node —
    /// the present-case lifts the type system documents: a value type flowing into an
    /// interface / `object` slot (box), and `T` into a `T?` slot (the Nullable&lt;T&gt;
    /// wrap). Placed by the binder at store sites so CodeGen emits the coercion FROM the
    /// node instead of re-deriving it inline from the operand / target Cecil types. A
    /// no-op for same-type, null-literal, and reference flows (Classify → Identity / null),
    /// and for the EXPLICIT `as` / `as!` / unbox casts, which already carry their own
    /// nodes. Idempotent — a value already at the target type wraps to nothing.
    internal BoundExpression Coerce(BoundExpression value, BoundType target) =>
        ConversionClassification.Classify(value.Type, target) switch
        {
            ConversionKind.Box => BoundConversion.Box(value, target),
            ConversionKind.NullableWrap when target is NullableType nt => BoundConversion.WrapNullable(value, nt),
            _ => value,
        };

    BoundUnaryExpression BindUnary(UnaryExpressionSyntax syntax)
    {
        var operand = BindExpression(syntax.Operand);
        return new BoundUnaryExpression(syntax.OperatorKind, operand, operand.Type);
    }

    BoundBinaryExpression BindBinary(BinaryExpressionSyntax syntax)
    {
        var left = BindExpression(syntax.Left);

        // `a && b` short-circuits: `b` only evaluates where `a` held, so `a`'s positive
        // narrows are in scope binding `b` — this is what makes `s is T && s.member` bind.
        if (syntax.OperatorKind is SyntaxTokenKind.AmpAmp or SyntaxTokenKind.AndKeyword)
        {
            var (pos, _) = Narrowing.Extract(left);
            var saved = SaveNarrows();
            ApplyNarrows(pos);
            var rightNarrowed = BindExpression(syntax.Right);
            RestoreNarrows(saved);
            return new BoundBinaryExpression(left, syntax.OperatorKind, rightNarrowed, new PrimitiveType("bool"));
        }

        var right = BindExpression(syntax.Right);
        // A source-declared Task / ValueTask is caller-handled by design. It is
        // not the uncolored async result that SyncFutureLowering joins on first
        // use, so allowing it into arithmetic reaches CodeGen as (for example)
        // `Task<int> + int` and produces unverifiable IL instead of a source
        // error. Diagnose this boundary while the expression still has its span.
        if (IsValueOperator(syntax.OperatorKind)
            && (IsExplicitAwaitable(left.Type) || IsExplicitAwaitable(right.Type)))
        {
            var awaitable = IsExplicitAwaitable(left.Type) ? left.Type : right.Type;
            Diagnostics.Report(syntax.Span,
                $"ES2260: an explicit '{TypeDisplayName(awaitable)}' is an awaitable, not a value for this operator. Await or explicitly join it before using it here.");
        }
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

    static bool IsExplicitAwaitable(BoundType type) => type switch
    {
        ExternalType { Name: "Task" or "ValueTask" } => true,
        ExternalCSharpType { Handle.Name: "Task" or "ValueTask" } => true,
        _ => false,
    };

    static bool IsValueOperator(SyntaxTokenKind op) => op is not (
        SyntaxTokenKind.EqualsEquals or SyntaxTokenKind.BangEquals or
        SyntaxTokenKind.Less or SyntaxTokenKind.LessEquals or
        SyntaxTokenKind.Greater or SyntaxTokenKind.GreaterEquals or
        SyntaxTokenKind.AmpAmp or SyntaxTokenKind.PipePipe or
        SyntaxTokenKind.AndKeyword or SyntaxTokenKind.OrKeyword);

    BoundConditionalExpression BindConditional(ConditionalExpressionSyntax syntax)
    {
        var condition = BindExpression(syntax.Condition);
        var consequence = BindExpression(syntax.Consequence);
        var alternative = BindExpression(syntax.Alternative);
        return new BoundConditionalExpression(condition, consequence, alternative, consequence.Type);
    }

    BoundNullCoalescingExpression BindNullCoalescing(NullCoalescingExpressionSyntax syntax)
    {
        var left = BindExpression(syntax.Left);
        var right = BindExpression(syntax.Right);
        var resultType = left.Type is NullableType nt ? nt.Inner : right.Type;
        return new BoundNullCoalescingExpression(left, right, resultType);
    }

    BoundNullConditionalAccessExpression BindNullConditionalAccess(NullConditionalAccessExpressionSyntax syntax)
    {
        var target = BindExpression(syntax.Target);
        var innerType = target.Type is NullableType nt ? nt.Inner : target.Type;
        var memberType = ResolveMemberType(innerType, syntax.MemberName);
        // `?.` can short-circuit to "no value", so a value-typed member becomes
        // nullable: `s?.Length` is `int?`, not `int`. Reference-typed members are
        // already nullable and stay unwrapped.
        var resultType = IsValueTypeForNullable(memberType) ? new NullableType(memberType) : memberType;
        return new BoundNullConditionalAccessExpression(target, syntax.MemberName, resultType);
    }

    /// Whether `?.` on a member of this type must wrap the result in `T?` — i.e.
    /// the member is a CLR value type (so it cannot itself carry the "absent"
    /// state a reference type expresses with null).
    bool IsValueTypeForNullable(BoundType t) => t switch
    {
        PrimitiveType p => p.Name is not ("string" or "void"),
        EnumType => true,
        TupleType => true,
        ResultType => true,
        ChoiceType c => !c.IsRef,
        DataType d => ClassifyData(d.Name) == DataClassification.Struct,
        _ => false,
    };

    // === Type-narrowing operators: is / as / as! (§type-narrowing-and-downcasting) ===

    // `operand is [not] T` — a runtime type test yielding bool. The smart-cast this
    // enables inside a guard region is layered on by the flow engine (NarrowingFacts);
    // the bound node here is the plain test.
    BoundExpression BindTypeTest(TypeTestExpressionSyntax syntax)
    {
        var operand = BindExpression(syntax.Operand);
        if (operand is BoundErrorExpression) return new BoundErrorExpression();   // cascade-suppress
        var target = ResolveType(syntax.Type);
        if (!ValidateNarrowTarget(target, syntax.Negated ? "is not" : "is", syntax.Span))
            return new BoundErrorExpression();
        WarnStaticTypeTest(operand.Type, target, syntax.Negated, syntax.Span);
        return new BoundTypeTestExpression(operand, target, syntax.Negated);
    }

    // `operand as T` (safe → T?, `isinst`) / `operand as! T` (asserting → T,
    // `castclass`/`unbox.any`, throws on miss).
    //
    // [Δ] spine-deltas §3: BoundSafeCastExpression / BoundAssertCastExpression are
    //     removed from the frozen contract. The binder calls BoundConversion factory
    //     methods — AssertCast (→ CastClass) and SafeCast (→ IsInst) — so the emitter
    //     only needs one EmitConversion switch on ConversionKind. The nullable-target
    //     widening (T? for value types, T for references) is preserved exactly.
    BoundExpression BindCast(CastExpressionSyntax syntax)
    {
        var operand = BindExpression(syntax.Operand);
        if (operand is BoundErrorExpression) return new BoundErrorExpression();   // cascade-suppress
        var target = ResolveType(syntax.Type);
        if (!ValidateNarrowTarget(target, syntax.Asserting ? "as!" : "as", syntax.Span))
            return new BoundErrorExpression();
        if (syntax.Asserting)
        {
            // `as! T` throws on a miss. A value-type target is not a
            // `castclass` target: object/interface → int must be `unbox.any int`
            // (which has the same throwing behavior), otherwise the emitter leaves
            // an int32 managed pointer on the evaluation stack.
            return IsValueTypeForNullable(target)
                ? BoundConversion.Unbox(operand, target)
                : BoundConversion.AssertCast(operand, target);
        }
        // `as T` — safe cast; fails to nil, so value-type targets widen to T?.
        // Reference targets are already nil-capable and stay unwrapped.
        var nullableTarget = IsValueTypeForNullable(target) ? new NullableType(target) : target;
        return BoundConversion.SafeCast(operand, nullableTarget);
    }

    // A narrowing target must be a concrete, runtime-testable type. A nullable target
    // (`x is T?`) is meaningless — `as T` already accounts for absence by yielding T? —
    // and the inference hole / void are not types you can test against.
    bool ValidateNarrowTarget(BoundType target, string op, SourceSpan span)
    {
        switch (target)
        {
            case InferredType:
                return false;  // the type failed to resolve; it already reported
            case NullableType:
                Diagnostics.Report(span,
                    $"ES2170: '{op}' target type must not be nullable — '{op} T' already accounts for absence (an `as` yields T?). Drop the '?'.");
                return false;
            case VoidType:
                Diagnostics.Report(span, $"ES2170: '{op}' target type must be a concrete type, not 'void'.");
                return false;
            default:
                return true;
        }
    }

    // A test whose answer the compiler already knows is a smell. `x is T` where x is
    // statically T is always true (always false negated). Open-world reference
    // relationships are left to runtime; only the same-type case is flagged, so the
    // warning never fires falsely.
    void WarnStaticTypeTest(BoundType operandType, BoundType target, bool negated, SourceSpan span)
    {
        if (operandType is InferredType) return;
        if (TypeResolver.TypeDisplayName(operandType) == TypeResolver.TypeDisplayName(target))
            Diagnostics.Warn(span,
                $"ES2171: '{TypeResolver.TypeDisplayName(operandType)} is {(negated ? "not " : "")}{TypeResolver.TypeDisplayName(target)}' is always {(negated ? "false" : "true")} — the operand already has that type.");
    }

    BoundListLiteralExpression BindListLiteral(ListLiteralExpressionSyntax syntax)
    {
        var elements = syntax.Elements.Select(e => BindExpression(e)).ToList();
        var elementType = elements.Count > 0 ? elements[0].Type : (BoundType)new ExternalType("object");
        var listType = new ExternalType("List", [elementType]);
        return new BoundListLiteralExpression(elements, elementType, listType);
    }

    BoundArrayCreationExpression BindArrayCreation(ArrayCreationExpressionSyntax syntax)
    {
        var elementType = ResolveType(syntax.ElementType);
        var size = BindExpression(syntax.Size);
        return new BoundArrayCreationExpression(elementType, size, new ArrayBoundType(elementType));
    }

    BoundTupleLiteralExpression BindTupleLiteral(TupleExpressionSyntax syntax)
    {
        var elements = syntax.Elements.Select(e => BindExpression(e)).ToList();
        var elementTypes = elements.Select(e => e.Type).ToList();
        var tupleType = new TupleType(elementTypes);
        return new BoundTupleLiteralExpression(elements, tupleType);
    }

    BoundExpression BindMemberAccess(MemberAccessExpressionSyntax syntax)
    {
        // Payload-view projection: `view.payloadName` where `view` is the single
        // binding of a value-choice match arm resolves to the synthetic local the
        // arm bound for that payload. For single-payload cases the view is
        // transparent — `a` and `a.id` are the same local — which is why
        // `.connected(sid)` (bare) and `.accepted(a)` (a.id) both work.
        if (syntax.Target is NameExpressionSyntax viewRef
            && Ctx.PayloadViews.TryGetValue(viewRef.Name, out var fieldMap)
            && fieldMap.TryGetValue(syntax.MemberName, out var proj))
        {
            return new BoundNameExpression(proj.Local, proj.Type);
        }

        // Qualified external type name: a dotted chain of plain names that resolves
        // to a CLR type (`System.Text.StringBuilder`, `System.IO.Path`). Collapse it
        // to a single BoundNameExpression so the static-call and construction paths
        // — which expect the type as a single name — work for arbitrarily-deep
        // namespaces, not just bare or single-segment ones. Skipped when the head
        // segment is a local (a real value member access like `obj.Field`).
        if (TryFlattenDottedName(syntax) is { } flatName
            && Scope.Lookup(HeadSegment(flatName)) is null
            && ResolveExternalRuntimeTypeByName(flatName) is not null)
        {
            return new BoundNameExpression(flatName, new ExternalType(flatName));
        }

        var target = BindExpression(syntax.Target);

        // Positional-data destructure: `let (a, b) = v` desugars to `.ItemN` reads
        // in the parser (the tuple path). Over a positional `data`, ItemN maps to
        // the N-th positional field — same semantics as the synthesized
        // `Deconstruct` C# consumers get. A real field named ItemN wins.
        if (target.Type is DataType posDt
            && Data.Symbols.DataDecl(posDt.Name) is { IsPositional: true } posDecl
            && syntax.MemberName.Length > 4 && syntax.MemberName.StartsWith("Item", StringComparison.Ordinal)
            && int.TryParse(syntax.MemberName.AsSpan(4), out var itemOrdinal)
            && !posDecl.Fields.Any(f => f.Name == syntax.MemberName))
        {
            if (itemOrdinal < 1 || itemOrdinal > posDecl.Fields.Count)
            {
                Diagnostics.Report(syntax.Span,
                    $"'{posDt.Name}' destructures into {posDecl.Fields.Count} value(s) — there is no element {itemOrdinal}.");
                return new BoundErrorExpression();
            }
            var posField = posDecl.Fields[itemOrdinal - 1];
            return new BoundMemberAccessExpression(target, posField.Name,
                ResolveMemberType(target.Type, posField.Name));
        }

        // Desugar promoted access through embedded fields:
        // w.x → w.Base.x when x is promoted from an embedded Base
        // Also handles *T targets: t.x → t.Base.x (auto-deref chain)
        var lookupType = target.Type switch
        {
            HeapPointerBoundType hpLookup => hpLookup.Inner,
            ByRefBoundType byRefLookup => byRefLookup.Inner,
            _ => target.Type,
        };
        if (lookupType is DataType dt && FieldsOf(dt) is { } fields)
        {
            // Only desugar if the member isn't a direct field
            if (!fields.Any(f => !f.IsEmbedded && f.Name == syntax.MemberName))
            {
                var embed = FindEmbeddedFieldOwning(fields, syntax.MemberName);
                if (embed?.Bound is { } embedBound)
                {
                    var intermediate = new BoundMemberAccessExpression(target, embed.Name,
                        ResolveMemberType(target.Type, embed.Name));
                    var memberType = ResolveMemberType(
                        embedBound is HeapPointerBoundType hp ? hp.Inner : embedBound, syntax.MemberName);
                    return new BoundMemberAccessExpression(intermediate, syntax.MemberName, memberType);
                }
            }
        }

        var resolvedType = ResolveMemberType(target.Type, syntax.MemberName);

        // Unstable-narrow authority (ES2173). The receiver was tested with `is T` (or
        // `as`/a type pattern) but is not a stable binding the smart-cast can ride — a
        // `var`, or a `let`-field a call has since touched — so the member, which lives
        // on T, doesn't resolve against the un-narrowed type. Point at the rebind fix
        // instead of leaving it to a bare "no member"/locationless backend error.
        if (resolvedType is InferredType && PathOf(syntax.Target) is { } poisonPath
            && PoisonedTypeFor(poisonPath) is { } poisonType
            && MemberExistsOn(poisonType, syntax.MemberName))
        {
            Diagnostics.Report(syntax.Span,
                $"ES2173: '{poisonPath}' was type-tested but is not a stable binding — narrowing applies to `let` locals, parameters, and `let`-field paths with no intervening call. Rebind it: `let v = {poisonPath} as {TypeDisplayName(poisonType)}` and use `v`.");
            return new BoundErrorExpression();
        }

        // Member-access authority on binder-owned `data` (ES2147). When the receiver
        // is a user `data` type the binder knows its complete member surface: fields
        // (incl. base chain — ResolveMemberType walked it), embedded promotions (the
        // desugar above), payload views (top of method), promoted methods, and the
        // System.Object surface. A member that is none of those does not exist —
        // report it located instead of deferring to the backend, which can only say
        // a locationless `IL: unresolved …`. External / BCL receivers still defer
        // to the IL resolver (the binder has no metadata for them).
        if (resolvedType is InferredType && lookupType is DataType ownedDt
            && SymbolOf(ownedDt)?.Decl is DataDeclarationSyntax
            && !IsKnownDataMember(ownedDt, syntax.MemberName))
        {
            Diagnostics.Report(syntax.Span,
                $"ES2147: '{ownedDt.Name}' has no member '{syntax.MemberName}' — it is not a field, embedded member, or method of '{ownedDt.Name}'.");
            return new BoundErrorExpression();
        }

        // Enum case use: `Color.red` reports the interned CaseSymbol.
        if (lookupType is EnumType caseOwner && caseOwner.Symbol is { } enumOwnerSym)
        {
            ReportCaseUse(enumOwnerSym, syntax.MemberName, syntax.NameSpan);
            // Bare `Color.Blue` (no parens) is the idiomatic enum-member spelling — it
            // constructs the case exactly as `Color.Blue()` does. Rewrite it to the same
            // BoundDotCaseExpression so it lowers to the enum constant instead of reaching
            // the backend as an int-shaped member access (probe2 #1). Only a real case
            // name rewrites; anything else falls through to the member-access node.
            if (Data.Symbols.EnumDecl(caseOwner.Name) is { } bareEnumDecl
                && bareEnumDecl.Cases.Any(c => c.Name == syntax.MemberName))
                return new BoundDotCaseExpression(syntax.MemberName, caseOwner.Name, [], caseOwner);
        }

        // The resolved member carried onto the node, so CodeGen emits the field /
        // accessor directly from its declaring TypeSymbol instead of re-resolving the
        // name against the target type. Set by whichever branch resolves it below.
        Esharp.Symbols.ISymbol? member = null;

        // Field use: a member that resolves to a declared field on a binder-owned
        // `data` reports the interned FieldSymbol (walking the base chain) — the
        // same instance the declaration reported, so find-references reaches it.
        var declaredMemberOwner = lookupType switch
        {
            DataType dataOwner => SymbolOf(dataOwner),
            InterfaceType interfaceOwner => interfaceOwner.Symbol,
            _ => null,
        };
        if (declaredMemberOwner is not null)
            for (var sym = declaredMemberOwner; sym is not null; sym = sym.BaseType)
                if (sym.Fields.FirstOrDefault(fs => fs.Name == syntax.MemberName) is { } fSym)
                {
                    member = fSym;
                    Data.Sink.OnFieldResolved(fSym, syntax.NameSpan, Semantics.SymbolOccurrence.Use);
                    break;
                }

        // External member use: `text.Length`, `Console.Out` — the receiver reflects
        // to a runtime type whose property/field carries the member; report the
        // interned external symbol so BCL members hover/paint like declared ones.
        // (Method members report from the call path, which knows the overload.)
        if (lookupType is ExternalType or PrimitiveType)
        {
            var rt = lookupType is ExternalType extOwner
                ? ResolveExternalToRuntime(extOwner)
                : ResolveExternalRuntimeTypeByName(((PrimitiveType)lookupType).Name);
            const System.Reflection.BindingFlags surface =
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Static;
            System.Reflection.MemberInfo? external = null;
            try
            {
                external = (System.Reflection.MemberInfo?)rt?.GetProperty(syntax.MemberName, surface)
                    ?? rt?.GetField(syntax.MemberName, surface);
            }
            catch (System.Reflection.AmbiguousMatchException)
            {
                external = rt?.GetProperties(surface).FirstOrDefault(p => p.Name == syntax.MemberName);
            }
            if (external is not null)
            {
                var extField = Data.Externals.Field(external);
                member = extField;
                // Code generation and lowering also consume the interned member
                // symbol (not just the semantic/LSP sink): imported E# properties
                // carry loca/mut capability there.  Do not make that semantic fact
                // contingent on whether the caller requested editor information.
                if (Data.Sink is not Semantics.NullSemanticSink)
                    Data.Sink.OnFieldResolved(extField, syntax.NameSpan, Semantics.SymbolOccurrence.Use);
            }
        }

        return new BoundMemberAccessExpression(target, syntax.MemberName, resolvedType) { Member = member };
    }

    // Whether `member` plausibly exists on the would-be-narrowed type — for the ES2173
    // hint, so it fires only when the access really does rely on the narrow. A binder-owned
    // `data` is checked precisely; an external/BCL target can't be inspected at bind time,
    // so a poisoned reliance on one is reported (the rebind is the fix either way).
    bool MemberExistsOn(BoundType narrowed, string member)
    {
        var dt = narrowed is HeapPointerBoundType hp ? hp.Inner as DataType : narrowed as DataType;
        if (dt is not null && SymbolOf(dt)?.Decl is DataDeclarationSyntax)
            return IsKnownDataMember(dt, member);
        return true;
    }

    /// The member surface of a user `data` type, for the ES2147 check — walked
    /// through the whole inheritance chain. A member is known when ANY ancestor
    /// declares it as a field (a const field's type may be the inference hole, so
    /// existence is checked by NAME, never by whether a type resolved), an inline
    /// method, or a promotion target (any free function whose first parameter
    /// names the type — promoted or cross-namespace — never false-positives), or
    /// when it is part of the inherited System.Object surface the call path types
    /// via reflection.
    bool IsKnownDataMember(DataType dt, string memberName)
    {
        if (memberName is "ToString" or "GetType" or "Equals" or "GetHashCode")
            return true;
        var cursor = SymbolOf(dt);
        var guard = 0;
        while (cursor is not null && guard++ < 64)
        {
            // The symbol's resolved member set: promoted free functions (each
            // overload attaches to its OWN receiver) and synthesized members.
            if (cursor.Members.Any(m => m.Name == memberName))
                return true;
            // Fields by NAME — a const field's type may be the inference hole, so
            // existence is never judged by whether a type resolved.
            if (cursor.Fields.Any(f => f.Name == memberName))
                return true;
            // A method (function with a receiver block) whose receiver names this type
            // is a callable member. A bare first-param free function is NOT a member —
            // attachment is explicit now (Go model), reached `fn(x)`, never `x.fn()`.
            if (Data.Symbols.TryGetFunction(memberName)?.Decl is { Receiver: not null } fn
                && fn.Parameters.Count > 0
                && TypeSyntaxLeafName(fn.Parameters[0].Type) == cursor.Name)
                return true;
            if (cursor.Decl is DataDeclarationSyntax decl)
            {
                if (decl.Fields.Any(f => f.Name == memberName))
                    return true;
                if (decl.Methods?.Any(m => m.Name == memberName) == true)
                    return true;
            }
            cursor = cursor.BaseType;
        }
        return false;
    }

    /// Primitive type aliases usable as static receivers (`uint.MaxValue`,
    /// `decimal.Parse`) — never undefined names, even when the reflection probe
    /// has no direct mapping for them.
    static readonly HashSet<string> PrimitiveNames = new(StringComparer.Ordinal)
    {
        "int", "long", "double", "float", "bool", "string", "byte", "short",
        "char", "uint", "ulong", "ushort", "sbyte", "decimal", "void",
    };

    // Flatten a member-access chain of plain names to a dotted string
    // ("System" . "IO" . "Path" -> "System.IO.Path"); null if any segment is not a
    // bare name (e.g. an index, call, or value receiver in the chain).
    static string? TryFlattenDottedName(MemberAccessExpressionSyntax ma)
    {
        var parts = new List<string> { ma.MemberName };
        ExpressionSyntax cur = ma.Target;
        while (true)
        {
            switch (cur)
            {
                case NameExpressionSyntax n:
                    parts.Add(n.Name);
                    parts.Reverse();
                    return string.Join('.', parts);
                case MemberAccessExpressionSyntax m:
                    parts.Add(m.MemberName);
                    cur = m.Target;
                    break;
                default:
                    return null;
            }
        }
    }

    static string HeadSegment(string dotted)
    {
        var i = dotted.IndexOf('.');
        return i < 0 ? dotted : dotted[..i];
    }

    /// Find which embedded field (if any) owns a given member name (field or method).
    FieldSymbol? FindEmbeddedFieldOwning(IEnumerable<FieldSymbol> fields, string memberName)
    {
        foreach (var f in fields)
        {
            if (!f.IsEmbedded || f.Bound is not { } fBound) continue;
            var innerType = fBound is HeapPointerBoundType hp ? hp.Inner : fBound;
            // Check fields
            var resolved = ResolveMemberType(innerType, memberName);
            if (resolved is not InferredType)
                return f;
            // Check inline methods on the embedded type
            if (innerType is DataType dt && Data.Symbols.DataDecl(dt.Name) is { } decl)
            {
                if (decl.Methods?.Any(m => m.Name == memberName) == true)
                    return f;
            }
            // Check methods attached to the embedded type via a receiver block.
            if (innerType is DataType dt2 && Data.Symbols.TryGetFunction(memberName)?.Decl is { Receiver: not null } fn
                && fn.Parameters.Count > 0 && TypeSyntaxLeafName(fn.Parameters[0].Type) == dt2.Name)
                return f;
        }
        return null;
    }

    BoundExpression BindObjectCreation(ObjectCreationExpressionSyntax syntax)
    {
        var type = ResolveType(syntax.Type);

        // A headered class constructs ONLY through its primary constructor — a
        // composite literal would bypass the header (captured fields never stored,
        // primary body never run) and there is no parameterless ctor to back it.
        if (type is DataType headed
            && Data.Symbols.DataDecl(headed.Name) is { HeaderParameters.Count: > 0 } headedDecl)
        {
            Diagnostics.Report(syntax.Span,
                $"ES2190: '{headed.Name}' has a primary-constructor header — construct it with '{headed.Name}({string.Join(", ", headedDecl.HeaderParameters!.Select(p => p.Name + ": ..."))})', not a composite literal.");
            foreach (var f in syntax.Fields)
                BindExpression(f.Value);
            return new BoundErrorExpression();
        }

        // Choice-case types (`Expr_add`) are synthesized at emit time and invisible to
        // reflection, but a composite literal over one is valid — the emitter builds the
        // case subclass. Exempt them from the unknown-type guard.
        if (type is ExternalType ext && ResolveExternalToRuntime(ext) is null
            && !B.Types.IsChoiceCaseType(ext.Name))
        {
            Diagnostics.Report(syntax.Span,
                $"ES2148: unknown composite-literal type '{TypeDisplayName(type)}' — declare a 'data {ext.Name}' type, import/qualify the type, or use constructor-call syntax for external CLR types.");
            foreach (var f in syntax.Fields)
                BindExpression(f.Value);
            return new BoundErrorExpression();
        }

        // `required` coverage (ES2189): a composite literal must supply every
        // field marked `required` — they are exempt from silent defaulting.
        if (type is DataType reqDt && Data.Symbols.DataDecl(reqDt.Name) is { } reqDecl)
        {
            foreach (var rf in reqDecl.Fields)
                if (rf.IsRequired && !syntax.Fields.Any(f => f.Name == rf.Name))
                    Diagnostics.Report(syntax.Span,
                        $"ES2189: '{reqDt.Name}' literal does not set required field '{rf.Name}'.");
        }

        // For types with value-embedded fields, partition promoted field inits
        // into nested BoundObjectCreationExpression for each embedded type.
        if (type is DataType dt && FieldsOf(dt) is { } boundFields)
        {
            var valueEmbeds = boundFields.Where(f => f.IsEmbedded && f.Bound is not (null or HeapPointerBoundType)).ToList();
            if (valueEmbeds.Count > 0)
            {
                var ownFieldNames = boundFields.Where(f => !f.IsEmbedded).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
                // Also treat embedded field names as own (for explicit Base: Base{...} syntax)
                foreach (var e in boundFields.Where(f => f.IsEmbedded))
                    ownFieldNames.Add(e.Name);

                var promotedByEmbed = new Dictionary<string, List<BoundFieldInit>>(StringComparer.Ordinal);
                foreach (var e in valueEmbeds)
                    promotedByEmbed[e.Name] = [];

                var fieldInits = new List<BoundFieldInit>();
                foreach (var f in syntax.Fields)
                {
                    if (ownFieldNames.Contains(f.Name))
                    {
                        fieldInits.Add(new BoundFieldInit(f.Name, BindExpression(f.Value)));
                    }
                    else
                    {
                        // Find which embedded type owns this field
                        var owner = FindEmbeddedFieldOwning(valueEmbeds, f.Name);
                        if (owner is not null)
                            promotedByEmbed[owner.Name].Add(new BoundFieldInit(f.Name, BindExpression(f.Value)));
                        else
                            fieldInits.Add(new BoundFieldInit(f.Name, BindExpression(f.Value)));
                    }
                }

                // Synthesize nested object creation for each embedded type with promoted inits
                foreach (var e in valueEmbeds)
                {
                    if (promotedByEmbed[e.Name].Count > 0)
                        fieldInits.Add(new BoundFieldInit(e.Name, new BoundObjectCreationExpression(e.Bound!, promotedByEmbed[e.Name])));
                }

                return new BoundObjectCreationExpression(type, fieldInits);
            }
        }

        // Propagate the target field's declared type as the value's expected
        // type so inline function literals (`func(...)` / `=>`) can land as
        // function pointers when the field is `&(T -> U)`, and other
        // expected-type-driven inference still fires.
        var declaredFieldTypes = TryGetFieldTypeMap(type);
        var fields = syntax.Fields.Select(f =>
        {
            BoundType? expectedFieldType = declaredFieldTypes is not null && declaredFieldTypes.TryGetValue(f.Name, out var ft) ? ft : null;
            return new BoundFieldInit(f.Name, BindExpression(f.Value, expectedFieldType));
        }).ToList();
        return new BoundObjectCreationExpression(type, fields);
    }

    Dictionary<string, BoundType>? TryGetFieldTypeMap(BoundType type)
    {
        if (type is not DataType dt) return null;
        if (FieldsOf(dt) is not { } fs) return null;
        var map = new Dictionary<string, BoundType>(StringComparer.Ordinal);
        foreach (var f in fs)
            if (f.Bound is { } b)
                map[f.Name] = b;
        return map;
    }

    BoundWithExpression BindWith(WithExpressionSyntax syntax)
    {
        var target = BindExpression(syntax.Target);
        if (target.Type is not DataType dt || ClassifyData(dt.Name) != DataClassification.Struct)
        {
            Diagnostics.Report(syntax.Span, "The 'with' expression can only be used on value data types.");
            return new BoundWithExpression(target, [], target.Type);
        }
        var fields = syntax.Fields.Select(f => new BoundFieldInit(f.Name, BindExpression(f.Value))).ToList();
        return new BoundWithExpression(target, fields, target.Type);
    }

    BoundIndexExpression BindIndex(IndexExpressionSyntax syntax)
    {
        var target = BindExpression(syntax.Target);
        var index = BindExpression(syntax.Index);

        // A value type the binder fully owns — `data` / `class`, `choice`, `enum` —
        // can never be indexed: E# has no indexer overloading. Catch `x[i]` on one here,
        // located at the index expression, instead of letting it fall through to the
        // backend as a locationless `ES0001: IL: unresolved indexer` (which then emits
        // unbalanced, unverifiable IL). External/collection types still defer to the
        // IL resolver, which reflects over their real indexer.
        var indexTarget = target.Type is HeapPointerBoundType hpIdx ? hpIdx.Inner : target.Type;
        if (indexTarget is DataType or ChoiceType or EnumType)
        {
            var ownedName = indexTarget switch
            {
                DataType d => d.Name,
                ChoiceType c => c.Name,
                EnumType e => e.Name,
                _ => indexTarget.ToString(),
            };
            var sp = syntax.Span;
            Diagnostics.Report(sp.File, sp.IsValid ? sp.Line : 0, sp.IsValid ? sp.Column : 0,
                $"ES2145: '{ownedName}' is not indexable — '[...]' needs a List, array, string, or other indexable type.");
        }

        // Infer the element type from the target so later uses see a concrete type.
        // Primitives need to be honored too — `string[i]` returns char, and without
        // this the local typed `var` gets stamped as System.Object, producing
        // invalid IL when the unboxed char is stored into it.
        BoundType elementType = InferredType.Instance;
        // `T[]` index → the element type directly (the backend emits `ldelem`/`stelem`).
        if (indexTarget is ArrayBoundType arrIdx)
            return new BoundIndexExpression(target, index, arrIdx.ElementType);
        Type? runtime = target.Type switch
        {
            ExternalType ext => ResolveExternalToRuntime(ext),
            PrimitiveType prim => ResolveExternalRuntimeTypeByName(prim.Name),
            _ => null,
        };
        if (runtime is not null)
        {
            if (runtime.IsArray)
            {
                elementType = MapRuntimeTypeToBoundType(runtime.GetElementType()!);
            }
            else
            {
                var indexer = runtime.GetProperty("Item");
                if (indexer is null)
                {
                    // string and a few other BCL types use [DefaultMember] to name
                    // their indexer (string → Chars).
                    var defaultMember = runtime.GetCustomAttributes(typeof(System.Reflection.DefaultMemberAttribute), inherit: true)
                        .OfType<System.Reflection.DefaultMemberAttribute>()
                        .FirstOrDefault();
                    if (defaultMember is not null)
                        indexer = runtime.GetProperty(defaultMember.MemberName);
                }
                if (indexer is not null)
                    elementType = MapRuntimeTypeToBoundType(indexer.PropertyType);
            }
        }

        // Generic external with a user-defined type arg (`List<Pt>`,
        // `Dictionary<string, Pt>`): the runtime form above erases the arg to
        // `object`. Re-derive the element type from the *bound* type args, mapping
        // through the open indexer's generic-parameter position (List<T> → T at 0,
        // Dictionary<K,V> indexer → V at 1).
        if (target.Type is ExternalType { TypeArgs.Count: > 0 } ext2)
        {
            var boundArgs = ext2.TypeArgs;
            var openType = FindOpenGenericByName(ext2.Name, boundArgs.Count);
            var openIndexer = openType?.GetProperty("Item");
            if (openIndexer is { PropertyType.IsGenericParameter: true }
                && openIndexer.PropertyType.GenericParameterPosition is var pos
                && pos < boundArgs.Count)
            {
                elementType = boundArgs[pos];
            }
        }
        return new BoundIndexExpression(target, index, elementType);
    }

    BoundRangeExpression BindRange(RangeExpressionSyntax syntax)
    {
        // Standalone integer range `start..end` — produces a `Range` typed value
        // consumable as the collection of a for-in loop.
        if (syntax.Target is null)
        {
            var start = syntax.Start is not null ? BindExpression(syntax.Start) : null;
            var end = syntax.End is not null ? BindExpression(syntax.End) : null;
            return new BoundRangeExpression(null, start, end, new PrimitiveType("Range"));
        }
        var target = BindExpression(syntax.Target);
        var s = syntax.Start is not null ? BindExpression(syntax.Start) : null;
        var e = syntax.End is not null ? BindExpression(syntax.End) : null;
        return new BoundRangeExpression(target, s, e, target.Type);
    }

    BoundDotCaseExpression BindDotCase(DotCaseExpressionSyntax syntax)
    {
        var args = syntax.Arguments.Select(a => BindExpression(a)).ToList();

        // Special-case ok/error with Result return type
        if (Ctx.CurrentReturnType is ResultType rt2)
        {
            if (syntax.CaseName == "ok" && args.Count == 1)
                return new BoundDotCaseExpression("ok", "Result", args, rt2);
            if (syntax.CaseName == "error" && args.Count == 1)
                return new BoundDotCaseExpression("error", "Result", args, rt2);
        }

        // Search choice decls
        foreach (var choiceSym in Data.Symbols.AllOfKind(TypeSymbolKind.Union, TypeSymbolKind.RefUnion))
        {
            if (choiceSym.Decl is ChoiceDeclarationSyntax decl && decl.Cases.Any(c => c.Name == syntax.CaseName))
            {
                ReportCaseUse(choiceSym, syntax.CaseName, syntax.NameSpan);
                return new BoundDotCaseExpression(syntax.CaseName, choiceSym.Name, args, choiceSym.BoundView!);
            }
        }

        // Search enum decls
        foreach (var enumSym in Data.Symbols.AllOfKind(TypeSymbolKind.Enum))
        {
            if (enumSym.Decl is EnumDeclarationSyntax decl && decl.Cases.Any(c => c.Name == syntax.CaseName))
            {
                ReportCaseUse(enumSym, syntax.CaseName, syntax.NameSpan);
                return new BoundDotCaseExpression(syntax.CaseName, enumSym.Name, args, enumSym.BoundView!);
            }
        }

        return new BoundDotCaseExpression(syntax.CaseName, "", args, InferredType.Instance);
    }

    /// Report a `.case` use against the owning type's interned CaseSymbol — the
    /// same instance the declaration reported, so find-references/painting reach it.
    void ReportCaseUse(Esharp.Symbols.TypeSymbol owner, string caseName, SourceSpan span)
    {
        if (owner.Cases.FirstOrDefault(c => c.Name == caseName) is { } caseSym)
            Data.Sink.OnCaseResolved(caseSym, span, Semantics.SymbolOccurrence.Use);
    }

    BoundExpression BindAddressOf(AddressOfExpressionSyntax syntax)
    {
        // &funcName — check functions first
        if (syntax.Target is NameExpressionSyntax nameSyntax)
        {
            var name = nameSyntax.Name;
            if (Data.Symbols.TryGetFunction(name) is { Decl: { } funcDecl } fnSym)
            {
                var paramTypes = funcDecl.Parameters.Select(p =>
                {
                    BoundType t = ResolveType(p.Type);
                    return p.Type is PointerTypeSyntax ? new ByRefBoundType(t) : t;
                }).ToList();
                var returnType = fnSym.ReturnType ?? new VoidType();
                return new BoundAddressOfExpression(name, paramTypes, returnType);
            }

            // &varName — managed pointer (address-of).  A bare typed local is a
            // mutable value binding, not compiler-managed location storage.
            var varType = Scope.Lookup(name);
            if (varType is not null)
            {
                if (Scope.LookupRepresentation(name) == LocalRepresentation.BareTypedValue)
                {
                    Diagnostics.Report(syntax.Span,
                        $"Cannot take the address of typed value binding '{name}' — use 'var {name}: …' or 'let {name}: …' to declare addressable storage.");
                    return new BoundErrorExpression();
                }
                if (varType is DataType localClass
                    && ClassifyData(localClass.Name) == DataClassification.Class)
                {
                    Diagnostics.Report(syntax.Span,
                        $"ES2003: cannot take the address of class local '{name}' — '{localClass.Name}' is already a reference type; only a class-valued property with `loca` or direct `mut` can expose an opaque property location.");
                    return new BoundErrorExpression();
                }
                return new BoundAddressOfVariableExpression(
                    new BoundNameExpression(name, varType) { Symbol = Scope.LookupLocal(name) }, varType);
            }
        }

        // &T{...} — heap-allocate a value `data` into a `*T`. DEPRECATED spelling:
        // `new T { ... }` is canonical. Kept working with a migration warning
        // (ES2143) so existing source migrates incrementally — `new` lowers through
        // the exact same BoundHeapAllocExpression. A `class` target is *not*
        // heap-alloc (a class is already a reference); it falls through to the
        // address-of path below, unchanged.
        if (syntax.Target is ObjectCreationExpressionSyntax)
        {
            var inner = BindExpression(syntax.Target);
            if (inner.Type is DataType dt && ClassifyData(dt.Name) == DataClassification.Struct)
            {
                Diagnostics.Warn(syntax.Span,
                    $"ES2143: '&{dt.Name} {{ ... }}' is the deprecated heap-allocation spelling — use 'new {dt.Name} {{ ... }}'.");
                return new BoundHeapAllocExpression(inner, inner.Type);
            }
        }

        // &expr — properties participate only through an explicit `loca`
        // protocol.  That protocol is a ref-return accessor, not pointer formation
        // for the receiver: `&class.property` is valid only here and never implies
        // `*class`.
        var target = BindExpression(syntax.Target);
        if (target is BoundMemberAccessExpression member && IsPropertyAddress(member))
        {
            if (HasScopedMutProperty(member))
                return new BoundAddressOfVariableExpression(member, member.Type)
                {
                    IsScopedPropertyBorrow = true,
                    HasDurablePropertyFallback = HasExplicitPropertyLoca(member),
                };
            if (HasUnacknowledgedCustomSetter(member))
            {
                Diagnostics.Report(syntax.Span,
                    $"ES2222: taking the location of custom-set property '{member.MemberName}' bypasses its `set` accessor. Declare `loca => &self.storage` or `mut => &self.storage` to acknowledge the location policy explicitly.");
                return new BoundErrorExpression();
            }
            if (HasExplicitPropertyLoca(member))
                return new BoundAddressOfVariableExpression(member, member.Type);
            Diagnostics.Report(syntax.Span,
                $"Cannot take the address of property '{member.MemberName}' — declare `loca => &self.storage` to expose stable property identity.");
            return new BoundErrorExpression();
        }
        if (target is BoundMemberAccessExpression fieldMember
            && fieldMember.Target.Type is DataType classType
            && ClassifyData(classType.Name) == DataClassification.Class)
        {
            Diagnostics.Report(syntax.Span,
                $"Cannot take the address of class field '{fieldMember.MemberName}' — classes are not valid '*T' targets; expose a property `loca` instead.");
            return new BoundErrorExpression();
        }
        return new BoundAddressOfVariableExpression(target, target.Type);
    }

    bool HasExplicitPropertyLoca(BoundMemberAccessExpression member)
    {
        if (member.Member is Esharp.Symbols.FieldSymbol { IsProperty: true, HasDurablePropertyLocation: true })
            return true;
        var receiver = member.Target.Type is HeapPointerBoundType pointer ? pointer.Inner : member.Target.Type;
        var imported = ResolveBoundTypeToRuntime(receiver)?.GetProperty(member.MemberName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (receiver is ExternalCSharpType csharp
            && csharp.Handle.Members.FirstOrDefault(candidate =>
                candidate.Kind == CSharpMemberKind.Property && candidate.Name == member.MemberName) is
                { ReturnsByRef: true })
            return true;
        if (HasImportedPropertyCapability(imported, 0b000010)
            || imported?.GetMethod?.ReturnType.IsByRef == true) return true;
        if (receiver is not DataType data) return false;
        var property = Data.Symbols.DataDecl(data.Name)?.Fields
            .FirstOrDefault(field => field.Name == member.MemberName)?.Property;
        // A stored let/var property gets compiler-managed identity by default.
        // A `mut` declaration suppresses that default, but its direct location is
        // itself an explicit property protocol.
        return property is { ComputedGetter: null } durable
            && (durable.ScopedMutBody is null || durable.LocaStorageName is not null);
    }

    bool HasUnacknowledgedCustomSetter(BoundMemberAccessExpression member)
    {
        if (member.Member is Esharp.Symbols.FieldSymbol
            { IsProperty: true, HasCustomPropertySetter: true, HasDurablePropertyLocation: false })
            return true;
        var receiver = member.Target.Type is HeapPointerBoundType pointer ? pointer.Inner : member.Target.Type;
        var imported = ResolveBoundTypeToRuntime(receiver)?.GetProperty(member.MemberName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (HasImportedPropertyCapability(imported, 0b100000)
            && !HasImportedPropertyCapability(imported, 0b000010))
            return true;
        if (receiver is not DataType data) return false;
        var property = Data.Symbols.DataDecl(data.Name)?.Fields
            .FirstOrDefault(field => field.Name == member.MemberName)?.Property;
        return property is { SetterBody: not null, LocaStorageName: null, MutStorageName: null, ScopedMutBody: null };
    }

    bool HasScopedMutProperty(BoundMemberAccessExpression member)
    {
        if (member.Member is Esharp.Symbols.FieldSymbol symbol
            && (symbol.ScopedMut is not null || symbol.HasScopedPropertyLocation)) return true;
        var receiver = member.Target.Type is HeapPointerBoundType pointer ? pointer.Inner : member.Target.Type;
        var imported = ResolveBoundTypeToRuntime(receiver)?.GetProperty(member.MemberName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (HasImportedPropertyCapability(imported, 0b001000)) return true;
        return receiver is DataType data
            && Data.Symbols.DataDecl(data.Name)?.Fields.FirstOrDefault(field => field.Name == member.MemberName)
                ?.Property?.ScopedMutBody is not null;
    }

    static bool HasImportedPropertyCapability(System.Reflection.PropertyInfo? property, int flag)
    {
        if (property is null) return false;
        foreach (var attribute in property.CustomAttributes)
        {
            if (attribute.AttributeType.Name != "__EsharpPropertyCapabilityAttribute"
                || attribute.ConstructorArguments.Count != 1)
                continue;
            if (attribute.ConstructorArguments[0].Value is int flags)
                return (flags & flag) != 0;
        }
        return false;
    }

    bool IsPropertyAddress(BoundMemberAccessExpression member)
    {
        if (member.Member is Esharp.Symbols.FieldSymbol { IsProperty: true })
            return true;
        var receiver = member.Target.Type is HeapPointerBoundType pointer ? pointer.Inner : member.Target.Type;
        if (receiver is DataType data
            && Data.Symbols.DataDecl(data.Name)?.Fields.FirstOrDefault(field => field.Name == member.MemberName) is { } field)
            return field.Property is not null;

        if (receiver is ExternalCSharpType csharp)
            return csharp.Handle.Members.Any(candidate =>
                candidate.Kind == CSharpMemberKind.Property && candidate.Name == member.MemberName);

        return ResolveBoundTypeToRuntime(receiver)?.GetProperty(member.MemberName) is not null;
    }

    /// `new T { ... }` / `new T(args)` — the canonical fresh-heap-allocation form:
    /// construct a value `data` and place it on the heap, yielding a `*T`. `new`
    /// allocates something that does not yet exist, the clean counterpart to `&`
    /// (address-of something that does). A `class` is already a CLR reference, so
    /// `new RefData{}` is rejected by the same rule that forbids `*RefData` (ES2003);
    /// a non-`data` target cannot be heap-allocated into a pointer (ES2144).
    BoundExpression BindNew(NewExpressionSyntax syntax)
    {
        var inner = BindExpression(syntax.Target);
        if (inner.Type is DataType dt)
        {
            if (ClassifyData(dt.Name) == DataClassification.Struct)
                return new BoundHeapAllocExpression(inner, inner.Type);

            // class — already a reference; heap-into-`*T` is meaningless.
            Diagnostics.Report(syntax.Span,
                $"ES2003: 'new {dt.Name}' is illegal — '{dt.Name}' is already a reference type (class); construct it with '{dt.Name} {{ ... }}' (no 'new').");
            return inner;
        }

        Diagnostics.Report(syntax.Span,
            $"ES2144: 'new' heap-allocates a value 'data' into a '*T'; '{TypeDisplayName(inner.Type)}' is not a value 'data' type.");
        return inner;
    }

    internal BoundTryUnwrapExpression BindTryUnwrap(TryUnwrapExpressionSyntax syntax)
    {
        var inner = BindExpression(syntax.Inner);
        BoundType unwrappedType;
        if (inner.Type is ResultType rt)
            unwrappedType = rt.OkType;
        else
        {
            // `?` try-unwrap is Result-only. Applying it to anything else (a bare value,
            // an un-awaited awaitable — e.g. `await E?` parsed as `await (E?)` before the
            // precedence fix) is reported here rather than escaping to IL verification.
            // Suppress the cascade over already-errored / not-yet-inferred operands.
            if (inner is not BoundErrorExpression && inner.Type is not InferredType)
                Diagnostics.Report(syntax.Span,
                    $"ES2191: '?' unwraps a Result — '{TypeResolver.TypeDisplayName(inner.Type)}' is not a Result<T, E>. " +
                    "Did you mean to await first ('(await x)?'), or use '??' / '?.' for optional access?");
            unwrappedType = inner.Type;
        }
        var tempName = $"__r{Ctx.NextTemp()}";
        return new BoundTryUnwrapExpression(inner, unwrappedType, tempName);
    }


    /// The spine symbol a user-function call targets — the emitter's
    /// reference-identity resolution key. Covers the two shapes the emitter
    /// otherwise resolves by name walk: a free-function direct call (resolves
    /// against the namespace host, so a same-named function in another namespace
    /// never wins) and a `Owner.member` static-func call. Promoted instance calls
    /// have a real receiver and resolve through EmitMemberCall — they return null
    /// here so that receiver-aware lowering stays authoritative.
    Esharp.Symbols.MethodSymbol? ResolveCallSymbol(BoundExpression target, int argCount, int? typeArgCount)
    {
        switch (target)
        {
            case BoundNameExpression nm
                when Scope.Lookup(nm.Name) is null
                  && Data.Symbols.TryGetFunction(nm.Name) is { } fn
                  && fn.DeclaringType?.TypeKind == Esharp.Symbols.TypeSymbolKind.NamespaceHost
                  && Data.Symbols.TryGetFunction(nm.Name, argCount, typeArgCount) is { } resolved:
                return resolved;
            case BoundMemberAccessExpression sfMa
                when (sfMa.Target.Type is StaticFuncType
                    || sfMa.Target.Type is DataType dt && Data.Symbols.StaticFuncDecl(dt.Name) is not null)
                  && Data.Symbols.TryGetFunction($"{sfMa.Target.Type.EmitName}.{sfMa.MemberName}", argCount, typeArgCount) is { } sfm:
                return sfm;
            default:
                return null;
        }
    }

    // Bind a call, then advance the call generation so any field-path smart-cast
    // recorded before it goes stale — a call may have mutated the field through an
    // alias. The call's own operands bound first (reading narrows at the prior
    // generation), so the receiver/args still see their narrows.
    BoundExpression BindCallBumping(CallExpressionSyntax syntax)
    {
        var result = BindCall(syntax);
        Ctx.CallGeneration++;
        return result;
    }

    BoundExpression BindCall(CallExpressionSyntax syntax)
    {
        // Special-case ok()/error() when return type is Result<T,E>
        if (syntax.Target is NameExpressionSyntax bareName && Ctx.CurrentReturnType is ResultType rt)
        {
            if (bareName.Name == "ok" && syntax.Arguments.Count == 1)
                return new BoundResultCallExpression(true, BindExpression(syntax.Arguments[0]), rt.OkType, rt.ErrorType);
            if (bareName.Name == "error" && syntax.Arguments.Count == 1)
                return new BoundResultCallExpression(false, BindExpression(syntax.Arguments[0]), rt.OkType, rt.ErrorType);
        }

        // `Choice.case(args)` — qualified case construction. Per spec [REFERENCE.md
        // line 461 and 487], both `.case(args)` and `Type.case(args)` are valid.
        // The dot-case path is already handled; rewrite the qualified form to
        // BoundDotCaseExpression so the IL emitter (which already knows how to
        // emit ref-choice subclass newobj and value-choice factory calls) gets
        // a uniform shape.
        if (syntax.Target is MemberAccessExpressionSyntax memberSyn
            && memberSyn.Target is NameExpressionSyntax typeName)
        {
            // Strip any generic arguments for the declaration lookup
            // (`Option<int>` → base `Option`); the bound type keeps the closed
            // instantiation so the constructed value is typed `Option<int>`.
            var baseName = typeName.Name;
            var ltIdx = baseName.IndexOf('<');
            if (ltIdx > 0) baseName = baseName[..ltIdx];

            if (Data.Symbols.ChoiceDecl(baseName) is { } choiceDecl
                && choiceDecl.Cases.Any(c => c.Name == memberSyn.MemberName))
            {
                ReportNamedArgsUnsupported(syntax, "a choice-case construction");
                var caseArgs = syntax.Arguments.Select(a => BindExpression(a)).ToList();
                var choiceType = ResolveTypeName(typeName.Name);
                return new BoundDotCaseExpression(memberSyn.MemberName, baseName, caseArgs, choiceType);
            }
            if (Data.Symbols.EnumDecl(baseName) is { } enumDecl
                && enumDecl.Cases.Any(c => c.Name == memberSyn.MemberName))
            {
                ReportNamedArgsUnsupported(syntax, "an enum-case construction");
                var caseArgs = syntax.Arguments.Select(a => BindExpression(a)).ToList();
                return new BoundDotCaseExpression(memberSyn.MemberName, baseName, caseArgs, Data.Symbols.ResolveBound(baseName, 0)!);
            }
            // NESTED choice/enum case construction — `Kind.Green()` where `Kind` is a
            // nested type (not in the global simple-name index). Resolve the owner via
            // the enclosing scope (bare, inside the declaring type) or the qualified
            // `Outer.Kind` path, then construct exactly like the global forms.
            var nestedOwner = (Types.CurrentEnclosingType is { } enc
                                  ? Data.Symbols.ResolveNestedInScope(baseName, 0, enc) : null)
                              ?? Data.Symbols.ResolveNestedQualified(typeName.Name, 0);
            if (nestedOwner?.Decl is ChoiceDeclarationSyntax nestedChoice
                && nestedChoice.Cases.Any(c => c.Name == memberSyn.MemberName))
            {
                ReportNamedArgsUnsupported(syntax, "a choice-case construction");
                var caseArgs = syntax.Arguments.Select(a => BindExpression(a)).ToList();
                return new BoundDotCaseExpression(memberSyn.MemberName, baseName, caseArgs, nestedOwner.BoundView!);
            }
            if (nestedOwner?.Decl is EnumDeclarationSyntax nestedEnum
                && nestedEnum.Cases.Any(c => c.Name == memberSyn.MemberName))
            {
                ReportNamedArgsUnsupported(syntax, "an enum-case construction");
                var caseArgs = syntax.Arguments.Select(a => BindExpression(a)).ToList();
                return new BoundDotCaseExpression(memberSyn.MemberName, baseName, caseArgs, nestedOwner.BoundView!);
            }
        }

        // A receiver method is method-only: the free-call spelling `bump(v)` is disallowed
        // (Go method-set semantics on the CLR); call it `v.bump()`. This holds for ALL
        // receiver kinds — a pointer receiver emits a static host, but that host is reachable
        // only via the method spelling, never `bump(v)`. A receiverless free function
        // (`func bump(v: *Vec)`) has no receiver block and stays free-callable, skipping this.
        if (syntax.Target is NameExpressionSyntax promotedCall
            && Scope.Lookup(promotedCall.Name) is null
            && syntax.Arguments.Count >= 1
            && IsReceiverMethod(promotedCall.Name))
        {
            var sym = Data.Symbols.TryGetPromoted(promotedCall.Name)!;
            var recvType = TypeSyntaxLeafName(sym.Decl!.Parameters[0].Type);
            var kind = sym.ReceiverKind == ReceiverKind.Pointer ? "pointer receiver" : "value receiver";
            var recvText = syntax.Arguments[0] is NameExpressionSyntax rn ? rn.Name : "receiver";
            var rest = syntax.Arguments.Count > 1 ? ", …" : "";
            Diagnostics.Report(syntax.Span,
                $"ES2142: '{promotedCall.Name}' is a method on '{recvType}' ({kind}) — call it as a method " +
                $"`{recvText}.{promotedCall.Name}({(syntax.Arguments.Count > 1 ? "…" : "")})`, not as a free function " +
                $"`{promotedCall.Name}({recvText}{rest})`.");
        }
        // Namespace-scoped free-function call. A bare `fn(...)` resolves only when
        // fn's declaring namespace is the current one or imported via `using "NS"`
        // (which static-imports the host class, C#-`using static`-style). An
        // imported function is rewritten to its qualified `NS.fn` form so the IL
        // emitter targets the right host class; a function from an unimported
        // namespace is a hard error. Value-receiver promotions are excluded — they
        // are type-scoped methods (handled above), not namespace-scoped functions.
        else if (syntax.Target is NameExpressionSyntax bareFn
            && Scope.Lookup(bareFn.Name) is null
            && Data.Symbols.TryGetFunction(bareFn.Name)?.DeclaringType?.Namespace is { } fnNs
            && fnNs != Ctx.CurrentNamespace
            && !IsPromotedInstanceFunction(bareFn.Name))
        {
            if (IsNamespaceInScope(fnNs))
                syntax = syntax with { Target = new MemberAccessExpressionSyntax(new NameExpressionSyntax(fnNs), bareFn.Name) };
            else
                Diagnostics.Report(syntax.Span,
                    $"ES2152: function '{bareFn.Name}' is in namespace '{fnNs}' — add `using \"{fnNs}\"` or qualify as `{fnNs}.{bareFn.Name}`.");
        }
        // The qualified free-call spelling of a value-receiver promoted method
        // (`Geo.area(r)` where `area(r: Rect)` promoted onto `Rect` within `Geo`) is the
        // same error as the bare form: it is a method, not a free function on the namespace
        // host class, so it is reached `r.area()`, never `Geo.area(r)`. Promotion is
        // namespace-local, so `IsPromotedInstanceFunction` is true only when the receiver
        // type shares the function's namespace; requiring the qualifier to name that same
        // namespace pins this to the genuine "qualified a method as a free function" misuse
        // (a cross-namespace free function — which does NOT promote — is unaffected).
        else if (syntax.Target is MemberAccessExpressionSyntax { Target: NameExpressionSyntax qns } qma
            && Data.Symbols.IsKnownNamespace(qns.Name)
            && syntax.Arguments.Count >= 1
            && IsPromotedInstanceFunction(qma.MemberName)
            && Data.Symbols.TryGetFunction(qma.MemberName)?.DeclaringType?.Namespace == qns.Name)
        {
            var recvType = TypeSyntaxLeafName(Data.Symbols.TryGetPromoted(qma.MemberName)!.Decl!.Parameters[0].Type);
            var recvText = syntax.Arguments[0] is NameExpressionSyntax rn ? rn.Name : "receiver";
            var rest = syntax.Arguments.Count > 1 ? ", …" : "";
            Diagnostics.Report(syntax.Span,
                $"ES2142: '{qma.MemberName}' is a method on '{recvType}' (value receiver) — call it as a method " +
                $"`{recvText}.{qma.MemberName}({(syntax.Arguments.Count > 1 ? "…" : "")})`, not as a qualified free " +
                $"function `{qns.Name}.{qma.MemberName}({recvText}{rest})`.");
        }

        // An immediately-invoked function literal — `((a) => a + 1)(x)` — has no
        // delegate slot to pin its inferred parameters, so without help they bind
        // as inferred/object and the body type-erases. Pull each parameter's type
        // from the call's own argument instead. (The args here bind a second time
        // below in the canonical loop; an erroneous argument may double-report.)
        BoundExpression target;
        if (UnwrapParenthesized(syntax.Target) is FunctionLiteralExpressionSyntax iifeLiteral
            && iifeLiteral.Parameters.Count == syntax.Arguments.Count
            && iifeLiteral.Parameters.Any(p => p.Type is InferredTypeSyntax)
            && !syntax.Arguments.Any(a => a is OutArgumentExpressionSyntax))
        {
            var iifeArgTypes = syntax.Arguments.Select(a => BindExpression(a).Type).ToList();
            target = BindFunctionLiteral(iifeLiteral, expectedType: null, forcedParamTypes: iifeArgTypes);
        }
        else
        {
            target = BindExpression(syntax.Target);
        }

        // Named arguments + parameter defaults: rewrite the argument list into
        // parameter order, filling omitted slots with the declared defaults, BEFORE
        // anything downstream sees the call. Arguments evaluate in parameter order.
        syntax = NormalizeCallArguments(syntax, target);

        var expectedArgTypes = TryResolveCallParameterTypes(target, syntax.Arguments, syntax.TypeArguments);

        // Tooling tap: a dot-call on a receiver whose member is a promoted method
        // resolves through the symbol spine — report the use into the sink. The
        // receiver-type check mirrors dispatch (the method only attaches to its
        // own receiver), so an unrelated same-named member doesn't misreport.
        if (target is BoundMemberAccessExpression promotedMa
            && Data.Symbols.TryGetPromoted(promotedMa.MemberName) is { } promotedSym
            && promotedSym.DeclaringType is { } promotedRecv
            && ReceiverBaseName(promotedMa.Target.Type) == promotedRecv.Name)
        {
            Data.Sink.OnMethodResolved(promotedSym, CallTargetNameSpan(syntax), Semantics.SymbolOccurrence.Use);
        }

        // In an async caller an uncolored async call is auto-awaited. In a sync
        // caller it remains a ValueTask<T>; a plain local binding is subsequently
        // lowered to an eager, force-on-use future by SyncFutureLowering.
        // Uncolored async: a bare call to an async user fn is treated as `await
        // call(...)`. The caller is already promoted to ValueTask<T> return, so
        // adding another await point is free.
        var autoAwaitAsyncCall = target is BoundNameExpression callName
            && Data.Symbols.TryGetFunction(callName.Name) is { IsAsync: true, HasExplicitAsyncWrapperReturn: false }
            && !Ctx.BindingAwaitInner;
        // Resolve out-arg parameter types up front if any arg is `out var x` / `out x`,
        // by reflecting on the callee's parameter list.
        BoundType[]? outSlotTypes = null;
        if (syntax.Arguments.Any(a => a is OutArgumentExpressionSyntax))
            outSlotTypes = TryResolveOutSlotTypes(target, syntax.Arguments);

        var args = new List<BoundExpression>(syntax.Arguments.Count);
        for (var i = 0; i < syntax.Arguments.Count; i++)
        {
            var argSyntax = syntax.Arguments[i];
            if (argSyntax is OutArgumentExpressionSyntax outArg)
            {
                // Slot type: the reflection path (external methods), else the callee's
                // declared parameter type (user functions — `fill(b: *Box, out r: int)`
                // types `out var got` as int, not an inferred object).
                var declaredSlot = expectedArgTypes is not null && i < expectedArgTypes.Length
                    ? expectedArgTypes[i] switch
                    {
                        ByRefBoundType br => br.Inner,
                        { } t => t,
                    }
                    : null;
                var slotType = outSlotTypes?[i] ?? declaredSlot ?? InferredType.Instance;
                if (outArg.DeclaresLocal)
                    Scope.Declare(outArg.Name, slotType);
                args.Add(new BoundOutArgumentExpression(outArg.Name, slotType, outArg.DeclaresLocal));
            }
            else
            {
                var expectedArgType = expectedArgTypes is not null && i < expectedArgTypes.Length
                    ? expectedArgTypes[i]
                    : null;
                var bound = BindExpression(argSyntax, expectedArgType);
                if (expectedArgType is not null)
                    CheckPointerArgMismatch(argSyntax, bound, expectedArgType);
                args.Add(bound);
            }
        }

        // Try to resolve return type from known functions
        BoundType returnType = InferredType.Instance;
        if (target is BoundNameExpression nameTarget
            && Data.Symbols.TryGetFunction(nameTarget.Name) is { ReturnType: { } knownReturn } calleeFn)
        {
            // An uncolored async function DECLARES its unwrapped result (`-> int`) but the
            // call EVALUATES to the awaitable the synthesized stub returns (`ValueTask<int>`).
            // Type the call as that wrapper so an `await` site resolves GetAwaiter on
            // the real awaitable; a sync local binding is lowered separately.
            // An explicit `-> Task<T>` async already carries its wrapper in ReturnType.
            returnType = calleeFn is { IsAsync: true, HasExplicitAsyncWrapperReturn: false }
                ? WrapUncoloredAsync(knownReturn)
                : knownReturn;
        }
        else if (target is BoundMemberAccessExpression rcMa2 && rcMa2.Target.Type is ResultType rcRecv2
                 && ResultCombinatorReturnType(rcRecv2, rcMa2.MemberName, args) is { } rcRet)
        {
            // Result combinator return, typed intrinsically: `r.Map(f)` → Result<TNew, TE>
            // (TNew = f's inferred body return), `r.UnwrapOr(x)` → TV, etc. Without this
            // the call types as `object` and typed composition (`a + b`, chaining) breaks.
            returnType = rcRet;
        }
        else if (target is BoundMemberAccessExpression sfMa
                 && (sfMa.Target.Type is StaticFuncType
                     || sfMa.Target.Type is DataType dt && Data.Symbols.StaticFuncDecl(dt.Name) is not null)
                 && Data.Symbols.TryGetFunction($"{sfMa.Target.Type.EmitName}.{sfMa.MemberName}")?.ReturnType is { } staticReturn)
        {
            returnType = staticReturn;
        }
        else if (target is BoundMemberAccessExpression pgMa2
                 && PromotedGenericReturnType(pgMa2, args) is { } pgRet)
        {
            // Promoted user-generic call: `Wrap<U>` closed over the receiver-bound + lambda-
            // inferred args (→ `Wrap<int>`), not the open declared return below.
            returnType = pgRet;
        }
        else if (target is BoundMemberAccessExpression ma && Data.Symbols.TryGetFunction(ma.MemberName)?.ReturnType is { } memberReturn)
        {
            // An in-body / receiver method on a CLOSED generic receiver returns the
            // declared type with the receiver's type arguments substituted in: `get`
            // on `Box<Box<int>>` declared `-> T` returns `Box<int>`, not the open `T`.
            // Without this the result types as the open parameter and the next member
            // access dispatches on the erased `object` (probe3 #1, nested user generics).
            returnType = SubstituteReceiverTypeArgs(memberReturn, ma.Target.Type);
        }
        else if (target is BoundMemberAccessExpression enumMa
                 && enumMa.Target is BoundNameExpression enumTypeName
                 && Data.Symbols.ResolveBound(enumTypeName.Name, 0) is EnumType enumBt)
        {
            returnType = enumBt;
        }
        else if (target is BoundNameExpression nameCtor && Scope.Lookup(nameCtor.Name) is null
                 && !Data.Symbols.HasFunction(nameCtor.Name)
                 && LooksLikeTypeName(nameCtor.Name))
        {
            // External constructor call: `List<int>()`, `Dictionary<string, int>()`, `StringBuilder()`.
            // The return type IS the type being constructed.
            returnType = ResolveTypeName(nameCtor.Name);
        }
        else if (target is BoundMemberAccessExpression { MemberName: "Invoke" } invoke
                 && TryGetExpectedDelegateShape(invoke.Target.Type) is { } invokedDelegate)
        {
            // An explicit delegate invocation (`work.Invoke(token)`) has the same
            // return contract as the direct form (`work(token)`).  Do this before
            // reflection-based BCL member inference: reflection sees Func<…, object>
            // for an enclosing E# generic parameter and would otherwise turn
            // `Func<CancellationToken, Task<T>>.Invoke` into `Task<object>`, poisoning
            // async lowering's awaiter field as TaskAwaiter<object>.
            returnType = invokedDelegate.ReturnType;
        }
        else if (target is BoundMemberAccessExpression memberCallTarget
                 && returnType is InferredType)
        {
            // BCL member call: infer return type by reflecting on the receiver's runtime type.
            IReadOnlyList<BoundType>? explicitArgs = syntax.TypeArguments?.Select(ResolveGenericArg).ToList();
            var inferred = TryInferMemberCallReturnType(memberCallTarget, args, explicitArgs, CallTargetNameSpan(syntax));
            if (inferred is not null) returnType = inferred;
        }
        else if (returnType is InferredType && TryGetExpectedDelegateShape(target.Type) is { } delegateShape)
        {
            // Invoking a delegate *value* (`f(x)` where `f` is a Func/Action/named
            // delegate local, param, or field): the result type is the delegate's
            // Invoke return. Without this the call types as `var`/object and a
            // captured `let v = f(x)` loses the real type.
            returnType = delegateShape.ReturnType;
        }
        else if (returnType is InferredType && UnwrapBoundParens(target) is BoundFunctionLiteralExpression iifeTarget)
        {
            // Immediately-invoked literal: the call's type is the literal's
            // (body-inferred) return type.
            returnType = iifeTarget.ReturnType;
        }

        IReadOnlyList<BoundType>? explicitTypeArgs = null;
        if (syntax.TypeArguments is not null)
            // Generic-argument position: a `*T` arg is the `__Ptr_T` wrapper, never a
            // managed `ref T` (the CLR forbids by-ref generic args, and the IL
            // instantiation forces the wrapper). `ResolveGenericArg` applies exactly
            // that normalization, so the substituted return type and the IL
            // instantiation agree (`head<*Box>` → a `__Ptr_Box`-typed local).
            explicitTypeArgs = syntax.TypeArguments
                .Select(ResolveGenericArg)
                .ToList();

        if (explicitTypeArgs is { Count: > 0 })
            DiagnoseGenericClassPointerInstantiation(
                target, args.Count, explicitTypeArgs, CallTargetNameSpan(syntax));

        // Static-host overloads share one source name. Resolve against both the
        // call arity and explicit generic arity before deriving the return type;
        // the old first-name-wins lookup selected `RunAsync(body)` for
        // `TaskScope.RunAsync<T>(ct, body)`, leaving a bare Task where Task<T>
        // is required.
        if (target is BoundMemberAccessExpression staticCall
            && (staticCall.Target.Type is StaticFuncType
                || staticCall.Target.Type is DataType staticData
                    && Data.Symbols.StaticFuncDecl(staticData.Name) is not null)
            && Data.Symbols.StaticFuncDecl(staticCall.Target.Type.EmitName) is { } staticHost)
        {
            var requestedTypeArity = explicitTypeArgs?.Count;
            var staticFn = staticHost.Functions.FirstOrDefault(f =>
                f.Name == staticCall.MemberName
                && f.Parameters.Count == args.Count
                && (requestedTypeArity is null || f.TypeParameters.Count == requestedTypeArity));
            if (staticFn is not null)
            {
                // A `static Host { returns T; func member() { ... } }`
                // applies T to members that omit an explicit arrow.  This overload
                // selection pass runs after the initial static-func lookup so it must
                // preserve that effective return contract; resolving the syntax node's
                // bare inferred return here would overwrite an already-correct T with
                // void, then produce a `void` local for `let value = Host.member()`.
                var effectiveReturn = staticFn.HasExplicitReturnType
                    ? staticFn.ReturnType
                    : staticHost.DefaultReturns?.Type ?? staticFn.ReturnType;
                // Keep `*T` in a static member's result in its first-class heap
                // wrapper form too.  Plain ResolveType turns the source spelling
                // back into the underlying value type, so `let p = Maker.build()`
                // allocated a Box local for a method that correctly returns
                // __Ptr_Box.  This must use the same pointer-aware authority as
                // the static member signature.
                returnType = ResolveHeapPointerAware(effectiveReturn);
                if (explicitTypeArgs is { Count: > 0 }
                    && explicitTypeArgs.Count == staticFn.TypeParameters.Count)
                {
                    var map = new Dictionary<string, BoundType>(StringComparer.Ordinal);
                    for (var i = 0; i < staticFn.TypeParameters.Count; i++)
                        map[staticFn.TypeParameters[i]] = explicitTypeArgs[i];
                    returnType = SubstituteTypeParams(returnType, map);
                }
            }
        }

        // Substitute explicit type arguments into a generic user function's declared
        // return type: `id<int>(x)` declared `-> T` returns `int`, not the open `T`.
        // Without this the `let` that binds the result is typed as the open parameter
        // (`!!0`) and emits invalid IL. (Type-argument *inference* — omitting the
        // `<int>` — is item #5 and stays deferred; this is the explicit-args case.)
        if (target is BoundNameExpression gName
            && Data.Symbols.TryGetFunction(gName.Name)?.Decl is { } gFn
            && gFn.TypeParameters.Count > 0
            && explicitTypeArgs is { Count: > 0 }
            && explicitTypeArgs.Count == gFn.TypeParameters.Count)
        {
            var map = new Dictionary<string, BoundType>(StringComparer.Ordinal);
            for (var i = 0; i < gFn.TypeParameters.Count; i++)
                map[gFn.TypeParameters[i]] = explicitTypeArgs[i];
            returnType = SubstituteTypeParams(returnType, map);
        }
        // Type-argument INFERENCE for a generic free-function call with no explicit `<...>`:
        // unify each declared parameter against its bound argument (reusing the promoted
        // path's InferTypeParamsFromArg — a `List<T>` vs `List<int>` pins T, a `Func<T,R>`
        // vs a lambda pins R from its body return), then close the return type AND flow the
        // inferred args to `explicitTypeArgs` so the emitter instantiates the closed method.
        // Without this the call keeps the open `List<R>` return and emits `List<object>`.
        else if (target is BoundNameExpression iName
            && Data.Symbols.TryGetFunction(iName.Name)?.Decl is { } iFn
            && iFn.TypeParameters.Count > 0
            && (explicitTypeArgs is null || explicitTypeArgs.Count == 0))
        {
            var imap = new Dictionary<string, BoundType>(StringComparer.Ordinal);
            for (var i = 0; i < args.Count && i < iFn.Parameters.Count; i++)
                InferTypeParamsFromArg(iFn.Parameters[i].Type, args[i], iFn.TypeParameters, imap);
            if (iFn.TypeParameters.All(imap.ContainsKey))
            {
                returnType = SubstituteTypeParamsInSyntax(iFn.ReturnType, imap, iFn.TypeParameters);
                explicitTypeArgs = iFn.TypeParameters.Select(tp => imap[tp]).ToList();
            }
            else if (args.All(a => a is not BoundErrorExpression))
            {
                // Inference did not pin every type parameter — and E# inference does NOT
                // flow from the expected result type back into the call (generics spec
                // §type-argument-inference). Report a LOCATED error rather than letting an
                // open `List<!!0>` reach the emitter and fail ILVerify (probe3 #4).
                var unpinned = iFn.TypeParameters.Where(tp => !imap.ContainsKey(tp)).ToList();
                var sample = $"{iName.Name}<{string.Join(", ", unpinned)}>(…)";
                Diagnostics.Report(CallTargetNameSpan(syntax),
                    $"ES2194: cannot infer type argument{(unpinned.Count > 1 ? "s" : "")} " +
                    $"{string.Join(", ", unpinned.Select(t => $"'{t}'"))} for '{iName.Name}' — " +
                    $"inference does not flow from the expected type; supply them explicitly, e.g. '{sample}'.");
            }
        }

        var resolvedMethod = ResolveCallSymbol(target, args.Count, explicitTypeArgs?.Count);
        // A resolved free / static-func call reports a method use — the same
        // interned MethodSymbol its declaration reported, so find-references on a
        // function reaches its call sites by reference identity.
        if (resolvedMethod is not null)
            Data.Sink.OnMethodResolved(resolvedMethod, CallTargetNameSpan(syntax), Semantics.SymbolOccurrence.Use);
        var call = new BoundCallExpression(target, args, returnType, explicitTypeArgs)
        {
            ResolvedMethod = resolvedMethod,
        };

        // Auto-await: a bare call to an async user function (caller is already async,
        // so we know this isn't the sync-caller bug above) becomes `await call(...)`.
        // Without this, the IL stores a ValueTask<T> into a T-shaped slot and
        // produces garbage values / AVE at runtime.
        if (autoAwaitAsyncCall
            && Ctx.CurrentFunctionName is { } currentName
            && Data.Symbols.TryGetFunction(currentName) is { IsAsync: true })
        {
            Ctx.CurrentFunctionHasAwait = true;
            // `call`'s type is now the wrapper (ValueTask<T>); the await yields the
            // unwrapped result.
            var awaited = InferAwaitResultType(returnType);
            return new BoundAwaitExpression(call, awaited is InferredType ? returnType : awaited);
        }

        return call;
    }

    /// Wrap an uncolored async function's declared (unwrapped) result into the awaitable its
    /// stub returns — `ValueTask<T>`, or bare `ValueTask` for a void-like result. Mirrors
    /// <c>AsyncLowering.WrapReturnType</c> for the default (ValueTask) shape so the call-site
    /// type matches the emitted stub's return exactly.
    static BoundType WrapUncoloredAsync(BoundType result) =>
        result is VoidType or PrimitiveType { Name: "void" }
            ? new ExternalType("ValueTask")
            : new ExternalType("ValueTask", [result]);

    /// The identifier span a call's METHOD-use occurrence reports at: the member
    /// name of a dot-call, the bare name of a free call — never the whole call
    /// expression, so hover/highlight on an argument doesn't resolve to the callee.
    static SourceSpan CallTargetNameSpan(CallExpressionSyntax syntax) => syntax.Target switch
    {
        MemberAccessExpressionSyntax ma => ma.NameSpan,
        NullConditionalAccessExpressionSyntax na => na.NameSpan,
        NameExpressionSyntax n when n.Span.IsValid => n.Span,
        _ => syntax.Span,
    };

    /// Report a reflection-resolved BCL method as a use occurrence at the call's
    /// member-name span — the interned external MethodSymbol, so hover on `.Add`
    /// describes the method, never the receiver's type.
    void ReportExternalMethodUse(System.Reflection.MethodInfo method, SourceSpan useSpan)
    {
        if (useSpan.IsValid && Data.Sink is not Semantics.NullSemanticSink)
            Data.Sink.OnMethodResolved(Data.Externals.Method(method), useSpan, Semantics.SymbolOccurrence.Use);
    }

    /// The bare receiver-type name behind a (possibly pointer-wrapped) receiver,
    /// for matching a dot-call against a promoted method's declaring type. A
    /// value-receiver method is in both `T`'s and `*T`'s method sets, so the
    /// pointer peels.
    static string? ReceiverBaseName(BoundType t) => t switch
    {
        DataType d => d.Name,
        HeapPointerBoundType hp => ReceiverBaseName(hp.Inner),
        ByRefBoundType br => ReceiverBaseName(br.Inner),
        _ => null,
    };

    /// Close an in-body / receiver method's declared return type over the receiver's
    /// bound type arguments. Peels pointer/by-ref to the underlying `DataType`, aligns the
    /// declaring type's parameter names with the receiver's `TypeArgs`, and substitutes —
    /// so `get` on `Box<Box<int>>` (`-> T`) returns the closed `Box<int>`. A non-generic
    /// receiver (or an arity/name mismatch) leaves the type untouched.
    BoundType SubstituteReceiverTypeArgs(BoundType returnType, BoundType receiverType)
    {
        var recv = receiverType switch
        {
            HeapPointerBoundType hp => hp.Inner,
            ByRefBoundType br => br.Inner,
            _ => receiverType,
        };
        if (recv is not DataType { TypeArgs.Count: > 0 } dt) return returnType;
        if (SymbolOf(dt)?.Decl is not DataDeclarationSyntax declSyntax) return returnType;
        var paramNames = declSyntax.TypeParameters;
        if (paramNames.Count != dt.TypeArgs.Count) return returnType;
        var map = new Dictionary<string, BoundType>(StringComparer.Ordinal);
        for (var i = 0; i < paramNames.Count; i++)
            map[paramNames[i]] = dt.TypeArgs[i];
        return SubstituteTypeParams(returnType, map);
    }

    /// Substitute type-parameter names with their bound arguments through a return
    /// type: `T` → `int`, `Pair<T>` → `Pair<int>`, `*T` → `*int`. Used to close a
    /// generic user function's declared return over its explicit type arguments.
    static BoundType SubstituteTypeParams(BoundType t, Dictionary<string, BoundType> map) => t switch
    {
        ExternalType { TypeArgs.Count: 0 } ext when map.TryGetValue(ext.Name, out var s) => s,
        ExternalType ext when ext.TypeArgs.Count > 0 =>
            ext with { TypeArguments = ext.TypeArgs.Select(a => SubstituteTypeParams(a, map)).ToList() },
        PrimitiveType p when map.TryGetValue(p.Name, out var s) => s,
        HeapPointerBoundType hp => new HeapPointerBoundType(SubstituteTypeParams(hp.Inner, map)),
        DataType d when d.TypeArgs.Count > 0 =>
            d with { TypeArguments = d.TypeArgs.Select(a => SubstituteTypeParams(a, map)).ToList() },
        _ => t,
    };

    /// A generic declaration may mention `*T` while T is still open, but closing
    /// that declaration over an E# class must not smuggle `*Class` past ES2003.
    /// Diagnose at the call site before the substituted byref reaches lowering or
    /// metadata emission.  This also covers a pointer occurrence nested in a
    /// function/delegate/tuple type rather than checking only direct parameters.
    void DiagnoseGenericClassPointerInstantiation(
        BoundExpression target,
        int argumentCount,
        IReadOnlyList<BoundType> typeArguments,
        SourceSpan span)
    {
        FunctionDeclarationSyntax? declaration = target switch
        {
            BoundNameExpression name => Data.Symbols.TryGetFunction(name.Name)?.Decl,
            BoundMemberAccessExpression member
                when Data.Symbols.StaticFuncDecl(member.Target.Type.EmitName) is { } host =>
                    host.Functions.FirstOrDefault(function =>
                        function.Name == member.MemberName
                        && function.Parameters.Count == argumentCount
                        && function.TypeParameters.Count == typeArguments.Count),
            BoundMemberAccessExpression member => Data.Symbols.TryGetFunction(member.MemberName)?.Decl,
            _ => null,
        };

        if (declaration is null
            || declaration.TypeParameters.Count != typeArguments.Count
            || declaration.TypeParameters.Count == 0)
            return;

        var substitutions = new Dictionary<string, BoundType>(StringComparer.Ordinal);
        for (var i = 0; i < declaration.TypeParameters.Count; i++)
            substitutions[declaration.TypeParameters[i]] = typeArguments[i];

        DiagnoseGenericClassPointerInstantiation(
            declaration, substitutions, typeArguments, span);
    }

    void DiagnoseGenericClassPointerInstantiation(
        FunctionDeclarationSyntax declaration,
        Dictionary<string, BoundType> substitutions,
        IReadOnlyList<BoundType> typeArguments,
        SourceSpan span)
    {

        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in declaration.Parameters)
            Visit(parameter.Type);
        Visit(declaration.ReturnType);
        return;

        void Visit(TypeSyntax type)
        {
            switch (type)
            {
                case PointerTypeSyntax pointer:
                {
                    var pointee = SubstituteTypeParamsInSyntax(
                        pointer.Inner, substitutions, declaration.TypeParameters);
                    if (pointee is DataType classType
                        && ClassifyData(classType.Name) == DataClassification.Class
                        && reported.Add(classType.EmitName))
                    {
                        Diagnostics.Report(span,
                            $"ES2003: generic instantiation '{declaration.Name}<{string.Join(", ", typeArguments.Select(TypeResolver.TypeDisplayName))}>' makes '*{classType.EmitName}' a pointer to a class — classes are reference types and cannot be pointer targets.");
                    }
                    Visit(pointer.Inner);
                    break;
                }
                case GenericTypeSyntax generic:
                    foreach (var argument in generic.Args) Visit(argument);
                    break;
                case TupleTypeSyntax tuple:
                    foreach (var element in tuple.Elements) Visit(element);
                    break;
                case FunctionPointerTypeSyntax function:
                    foreach (var parameter in function.ParameterTypes) Visit(parameter);
                    Visit(function.ReturnType);
                    break;
                case NullableTypeSyntax nullable:
                    Visit(nullable.Inner);
                    break;
            }
        }
    }

    /// True when a free function is promoted to a *value-receiver* instance method —
    /// its first parameter is a plain user `data`/`class` type (not `*T`, not
    /// `readonly *T`, not a closed generic on a non-generic function) AND the function
    /// is declared in that receiver type's own namespace. Such a function has no static
    /// host method: it lives on its receiver type, and the only call form is `v.bump()`
    /// (the free spelling `bump(v)` is ES2142). Promotion is namespace-local (spec:
    /// Functions → instance-method promotion): a first-param-`data` function whose
    /// receiver type lives in *another* namespace does NOT promote — it stays an
    /// ordinary free function reached `NS.fn(v)` or bare via `using`, so it is not a
    /// promoted instance method and this returns false for it.
    bool IsPromotedInstanceFunction(string name) =>
        // Routed through the symbol spine: a value-receiver promotion is a MethodSymbol
        // attached to its receiver's TypeSymbol.Members at signature time
        // (RegisterPromotedMethodSymbol). All the old re-derivation — first-param shape,
        // closed-generic exclusion, the namespace-local gate — was computed once, then,
        // and is now read off the symbol instead of FunctionDecls + FunctionNamespaces.
        Data.Symbols.TryGetPromoted(name) is { } m
        && m.ReceiverKind == ReceiverKind.Value
        && m.DeclaringType is { } receiver
        && receiver.Members.Contains(m);

    /// A method declared with ANY receiver block — value, readonly-value, or pointer.
    /// All three are method-only under the Go model: the free-call spelling `m(x)` is
    /// ES2142, even for a pointer receiver (which DOES emit a static host, but that host
    /// is reachable only as `x.m()`, never `m(x)`). Distinct from a plain free function
    /// `func m(x: *T)`, which has no receiver block and stays free-callable.
    bool IsReceiverMethod(string name) =>
        Data.Symbols.TryGetPromoted(name) is { ReceiverKind: not ReceiverKind.None } m
        && m.DeclaringType is { } receiver
        && receiver.Members.Contains(m);

    // ── Result combinator surface (part of the `Result` intrinsic) ──────────────
    // A consumer can't see Esharp.Stdlib.Result`2's signatures, so the binder types the
    // promoted combinators intrinsically from the receiver's Ok/Error types.

    /// Expected PARAMETER types for a combinator call — chiefly so a lambda arg binds
    /// with the right param type (`x: TV` for Map, `e: TE` for MapErr, …). The lambda
    /// RETURN is rendered `…, object` and left to body inference, except where the
    /// contract pins it (UnwrapOrElse's fallback returns TV). Null for non-combinators.
    BoundType[]? ResultCombinatorParamTypes(ResultType r, string member, int argCount)
    {
        BoundType obj = new ExternalType("object");
        BoundType Func1(BoundType p, BoundType ret) => new ExternalType("Func", [p, ret]);
        BoundType Action1(BoundType p) => new ExternalType("Action", [p]);
        return (member, argCount) switch
        {
            ("Map", 1) => [Func1(r.OkType, obj)],
            ("MapErr", 1) => [Func1(r.ErrorType, obj)],
            ("Bind", 1) => [Func1(r.OkType, obj)],
            ("Match", 2) => [Func1(r.OkType, obj), Func1(r.ErrorType, obj)],
            ("Inspect", 1) => [Action1(r.OkType)],
            ("InspectErr", 1) => [Action1(r.ErrorType)],
            ("UnwrapOr", 1) => [r.OkType],
            ("UnwrapOrElse", 1) => [Func1(r.ErrorType, r.OkType)],
            _ => null,
        };
    }

    /// Return type of a combinator call, from the receiver's Ok/Error types and the
    /// (body-inferred) return of any lambda arg. Null for non-combinators.
    BoundType? ResultCombinatorReturnType(ResultType r, string member, IReadOnlyList<BoundExpression> args)
    {
        BoundType LamRet(int i) => (i < args.Count ? LambdaReturnType(args[i]) : null) ?? new ExternalType("object");
        return member switch
        {
            "Map" => new ResultType(LamRet(0), r.ErrorType),
            "MapErr" => new ResultType(r.OkType, LamRet(0)),
            "Bind" => LamRet(0) is ResultType br ? br : new ResultType(LamRet(0), r.ErrorType),
            "Match" => LamRet(0),
            "Inspect" or "InspectErr" => r,
            "UnwrapOr" or "UnwrapOrElse" or "Unwrap" => r.OkType,
            "UnwrapErr" => r.ErrorType,
            _ => null,
        };
    }

    static BoundType? LambdaReturnType(BoundExpression arg) =>
        arg is BoundFunctionLiteralExpression fl ? fl.ReturnType : null;

    // ── Promoted user-generic instance calls (`w.mapped((x) => …)`) ────────────────
    // A free `func mapped<T,U>(w: Wrap<T>, f: Func<T,U>) -> Wrap<U>` promotes to a method
    // on `Wrap`. Called as `w.mapped(λ)`, the receiver's bound type args pin the
    // receiver-side type params (`T → int` from `Wrap<int>`); the rest (`U`) are open,
    // inferred from the lambda body. The binder types the param/return surface here so
    // the lambda arg binds `x: int` and the result types `Wrap<int>` rather than erasing
    // to `object`. (The emitter then closes the generic method over the same inference.)

    /// Map a promoted generic function's TYPE PARAMETERS to the receiver's bound type
    /// arguments by aligning the function's first (receiver) parameter's declared type
    /// args with the receiver `DataType`'s `TypeArgs`. Returns an empty map for a
    /// non-generic receiver param, or null on an arity/name mismatch (not this function).
    Dictionary<string, BoundType>? PromotedReceiverTypeArgMap(FunctionDeclarationSyntax fn, DataType recv)
    {
        string baseName;
        IReadOnlyList<TypeSyntax> p0Args;
        switch (fn.Parameters[0].Type)
        {
            case GenericTypeSyntax g: baseName = g.Name; p0Args = g.Args; break;
            case NamedTypeSyntax n: baseName = n.Name; p0Args = []; break;
            default: return null; // pointer / other receiver — not a generic-promoting receiver
        }
        if (baseName != recv.Name) return null;
        var map = new Dictionary<string, BoundType>(StringComparer.Ordinal);
        if (p0Args.Count == 0) return map; // non-generic receiver param
        if (p0Args.Count != recv.TypeArgs.Count) return null;
        var typeParams = fn.TypeParameters.ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < p0Args.Count; i++)
            if (p0Args[i] is NamedTypeSyntax { Name: var n } && typeParams.Contains(n))
                map[n] = recv.TypeArgs[i];
        return map;
    }

    /// Substitute type parameters in a declared parameter/return type, structurally:
    /// a receiver-bound param leaf → its mapped BoundType; an unbound function type
    /// param (e.g. a lambda-return `U`) → `object`, so the lambda infers its return
    /// from the body; generic / tuple / function-pointer / pointer / nullable shapes
    /// recurse; any other name resolves normally. Replaces the old regex-over-string.
    BoundType SubstituteTypeParamsInSyntax(TypeSyntax ts, Dictionary<string, BoundType> map, IReadOnlyList<string> allTypeParams)
    {
        var paramSet = allTypeParams.ToHashSet(StringComparer.Ordinal);
        BoundType Walk(TypeSyntax t) => t switch
        {
            InferredTypeSyntax => InferredType.Instance,
            NamedTypeSyntax { Name: var n } when map.TryGetValue(n, out var bt) => bt,
            NamedTypeSyntax { Name: var n } when paramSet.Contains(n) => new ExternalType("object"),
            // Closing over already-bound args goes through the registry mirror, so
            // `Wrap<U>` becomes the user `DataType Wrap<string>` (member resolution
            // works on it), never an ExternalType shell.
            GenericTypeSyntax g => Types.InstantiateGeneric(g.Name, g.Args.Select(Walk).ToList()),
            TupleTypeSyntax tup => new TupleType(tup.Elements.Select(Walk).ToList()),
            FunctionPointerTypeSyntax f => new FunctionPointerType(f.ParameterTypes.Select(Walk).ToList(), Walk(f.ReturnType)),
            PointerTypeSyntax p => new ByRefBoundType(Walk(p.Inner)),
            NullableTypeSyntax nu => new NullableType(Walk(nu.Inner)),
            _ => ResolveType(t),
        };
        return Walk(ts);
    }

    /// Expected PARAMETER types for a promoted user-generic call, with receiver-bound
    /// type params substituted and open ones rendered `object`. Null when `ma` is not a
    /// promoted generic instance call on a user `data` receiver.
    BoundType[]? PromotedGenericParamTypes(BoundMemberAccessExpression ma, int argCount)
    {
        if (ma.Target.Type is not DataType recv) return null;
        if (Data.Symbols.TryGetFunction(ma.MemberName)?.Decl is not { } fn) return null;
        if (fn.TypeParameters.Count == 0 || fn.Parameters.Count == 0) return null;
        if (!IsPromotedInstanceFunction(ma.MemberName)) return null;
        if (PromotedReceiverTypeArgMap(fn, recv) is not { } map) return null;
        if (argCount + 1 > fn.Parameters.Count) return null;
        var result = new BoundType[argCount];
        for (var i = 0; i < argCount; i++)
            result[i] = SubstituteTypeParamsInSyntax(fn.Parameters[i + 1].Type, map, fn.TypeParameters);
        return result;
    }

    /// Return type of a promoted user-generic call: substitute the receiver-bound type
    /// params, and the remaining open params from the matching lambda args' inferred
    /// returns (`mapped`'s `U` ← `(x) => x + 5`'s `int`). Null when not applicable.
    BoundType? PromotedGenericReturnType(BoundMemberAccessExpression ma, IReadOnlyList<BoundExpression> args)
    {
        if (ma.Target.Type is not DataType recv) return null;
        if (Data.Symbols.TryGetFunction(ma.MemberName)?.Decl is not { } fn) return null;
        if (fn.TypeParameters.Count == 0 || fn.Parameters.Count == 0) return null;
        if (!IsPromotedInstanceFunction(ma.MemberName)) return null;
        if (PromotedReceiverTypeArgMap(fn, recv) is not { } map) return null;

        // Infer each still-open type param by matching a parameter's declared type
        // against the bound argument: a `Func<T,U>` param vs a `(x) => x + 5` lambda
        // pins `U` from the lambda's body-inferred return. Args are offset by one (the
        // receiver is param 0).
        var full = new Dictionary<string, BoundType>(map, StringComparer.Ordinal);
        for (var i = 0; i < args.Count && i + 1 < fn.Parameters.Count; i++)
            InferTypeParamsFromArg(fn.Parameters[i + 1].Type, args[i], fn.TypeParameters, full);

        return SubstituteTypeParamsInSyntax(fn.ReturnType, full, fn.TypeParameters);
    }

    /// Structurally match a declared parameter type string (`Func<T, U>`) against the
    /// bound argument, filling `map[name]` for each type param it pins. Only the shapes
    /// promoted generics use are needed: a bare type param (`U` ← arg type), and a
    /// `Func<…>` / `Action<…>` delegate param matched against a lambda arg's param types
    /// and body-inferred return (the lambda's own `.Type` is always `var`, so its slots
    /// must be read off `Parameters`/`ReturnType`, not a delegate-type string).
    void InferTypeParamsFromArg(TypeSyntax paramType, BoundExpression arg, IReadOnlyList<string> typeParams, Dictionary<string, BoundType> map)
    {
        static bool IsKnown(BoundType t) => t is not InferredType;
        var paramSet = typeParams.ToHashSet(StringComparer.Ordinal);

        // Bare type parameter: `U` ← the arg's own type.
        if (paramType is NamedTypeSyntax { Name: var pName } && paramSet.Contains(pName))
        {
            if (IsKnown(arg.Type)) map.TryAdd(pName, arg.Type);
            return;
        }

        if (paramType is not GenericTypeSyntax { Name: var head, Args: var pArgs }) return;

        // Delegate param vs a lambda arg: map `Func<P…, R>` / `Action<P…>` slots to the
        // lambda's parameter types (in order) then, for `Func`, its return type.
        if (arg is BoundFunctionLiteralExpression fl && head is "Func" or "Action")
        {
            var slots = fl.Parameters.Select(p => p.Type).ToList();
            if (head == "Func") slots.Add(fl.ReturnType);
            for (var i = 0; i < pArgs.Count && i < slots.Count; i++)
                if (pArgs[i] is NamedTypeSyntax { Name: var s } && paramSet.Contains(s) && !map.ContainsKey(s) && IsKnown(slots[i]))
                    map[s] = slots[i];
            return;
        }

        // Non-lambda delegate value: match against the arg's structured delegate type args.
        if (arg.Type is ExternalType { TypeArgs.Count: > 0 } argExt)
        {
            var aArgs = argExt.TypeArgs;
            for (var i = 0; i < pArgs.Count && i < aArgs.Count; i++)
                if (pArgs[i] is NamedTypeSyntax { Name: var s } && paramSet.Contains(s) && !map.ContainsKey(s))
                    map[s] = aArgs[i];
        }
    }

    /// A cheap, side-effect-free read of an argument's type for PRE-binding inference —
    /// used to pin type parameters from concrete arguments before lambda arguments bind.
    /// Only resolves shapes that need no real binding (a name → its declared type); a
    /// lambda or complex expression returns null and is inferred after binding instead.
    BoundType? PeekArgType(ExpressionSyntax arg) => arg switch
    {
        NameExpressionSyntax n => Scope.Lookup(n.Name),
        _ => null,
    };

    /// Type-level counterpart of InferTypeParamsFromArg, for the pre-binding phase where
    /// only an argument's resolved TYPE is known (not a bound expression): a bare type
    /// param leaf ← the arg type; a `G<…>` declared type recurses pairwise into the arg's
    /// structured type arguments (`List<T>` vs `List<int>` → T=int).
    void InferTypeParamsFromArgType(TypeSyntax paramType, BoundType argType, IReadOnlyList<string> typeParams, Dictionary<string, BoundType> map)
    {
        var paramSet = typeParams.ToHashSet(StringComparer.Ordinal);
        if (paramType is NamedTypeSyntax { Name: var pName } && paramSet.Contains(pName))
        {
            if (argType is not InferredType) map.TryAdd(pName, argType);
            return;
        }
        if (paramType is GenericTypeSyntax { Args: var pArgs })
        {
            var aArgs = argType switch
            {
                ExternalType e => e.TypeArgs,
                DataType d => d.TypeArgs,
                _ => null,
            };
            if (aArgs is null) return;
            for (var i = 0; i < pArgs.Count && i < aArgs.Count; i++)
                InferTypeParamsFromArgType(pArgs[i], aArgs[i], typeParams, map);
        }
    }

    BoundType[]? TryResolveCallParameterTypes(BoundExpression target, IReadOnlyList<ExpressionSyntax> args, IReadOnlyList<TypeSyntax>? explicitTypeArgs)
    {
        if (target is BoundNameExpression nameTarget && Data.Symbols.TryGetFunction(nameTarget.Name)?.Decl is { } knownFunc)
        {
            // A generic user function called with explicit type args (`sumBy<*Box>(xs, f)`):
            // close each parameter's declared type over the args BEFORE the lambda binds, so
            // a `Func<T, int>` slot becomes `Func<*Box, int>` and the lambda param `w` takes
            // `*Box` (not the erased `object`). Pointer args normalize to the wrapper — the
            // generic-argument invariant — matching how the call instantiates.
            if (knownFunc.TypeParameters.Count > 0
                && explicitTypeArgs is { Count: > 0 }
                && explicitTypeArgs.Count == knownFunc.TypeParameters.Count)
            {
                var map = new Dictionary<string, BoundType>(StringComparer.Ordinal);
                for (var i = 0; i < knownFunc.TypeParameters.Count; i++)
                    // A `*T` explicit type arg is the heap-pointer wrapper (the generic-
                    // argument invariant), matching how the call instantiates.
                    map[knownFunc.TypeParameters[i]] = ResolveGenericArg(explicitTypeArgs[i]);
                return knownFunc.Parameters
                    .Select(p => SubstituteTypeParamsInSyntax(p.Type, map, knownFunc.TypeParameters))
                    .ToArray();
            }
            // Generic user function with NO explicit type args: pin what we can from the
            // concrete (non-lambda) arguments BEFORE binding, so a lambda parameter binds
            // its real type. `mapSum(xs, (x) => …)` pins `T=int` from `xs: List<int>`, so the
            // `Func<T,R>` slot becomes `Func<int, object>` and the lambda binds `x: int` (its
            // return `R` stays open here, completed from the lambda body after binding).
            if (knownFunc.TypeParameters.Count > 0)
            {
                var inferred = new Dictionary<string, BoundType>(StringComparer.Ordinal);
                for (var i = 0; i < knownFunc.Parameters.Count && i < args.Count; i++)
                    if (PeekArgType(args[i]) is { } peeked)
                        InferTypeParamsFromArgType(knownFunc.Parameters[i].Type, peeked, knownFunc.TypeParameters, inferred);
                if (inferred.Count > 0)
                    return knownFunc.Parameters
                        .Select(p => SubstituteTypeParamsInSyntax(p.Type, inferred, knownFunc.TypeParameters))
                        .ToArray();
            }
            return knownFunc.Parameters.Select(p => ResolveHeapPointerAware(p.Type)).ToArray();
        }

        if (target is BoundMemberAccessExpression staticMa
            && (staticMa.Target.Type is StaticFuncType
                || staticMa.Target.Type is DataType dt && Data.Symbols.StaticFuncDecl(dt.Name) is not null)
            && Data.Symbols.TryGetFunction($"{staticMa.Target.Type.EmitName}.{staticMa.MemberName}") is { Decl: { } sfFunc } staticMethod)
            return staticMethod.ReceiverKind == ReceiverKind.Static
                ? sfFunc.Parameters.Skip(1).Select(p => ResolveHeapPointerAware(p.Type)).ToArray()
                : sfFunc.Parameters.Select(p => ResolveHeapPointerAware(p.Type)).ToArray();

        // Result combinator (`r.Map(...)`, `r.UnwrapOr(...)`, …): the consumer can't see
        // Esharp.Stdlib.Result`2's signature, so type the param surface intrinsically
        // from the receiver's Ok/Error types — chiefly so a lambda arg binds `x: TV`.
        if (target is BoundMemberAccessExpression rcMa && rcMa.Target.Type is ResultType rcRecv
            && ResultCombinatorParamTypes(rcRecv, rcMa.MemberName, args.Count) is { } rcParams)
            return rcParams;

        // Promoted user-generic instance call (`w.mapped((x) => …)`): receiver-bound
        // type params substituted, open ones (lambda-return `U`) rendered `object`.
        if (target is BoundMemberAccessExpression pgMa
            && PromotedGenericParamTypes(pgMa, args.Count) is { } pgParams)
            return pgParams;

        if (target is not BoundMemberAccessExpression ma)
            return null;

        Type? runtimeType = null;
        bool searchStatic = false;

        if (ma.Target is BoundNameExpression staticTypeName && LooksLikeTypeName(staticTypeName.Name))
        {
            runtimeType = TryResolveRuntimeTypeByName(staticTypeName.Name);
            searchStatic = runtimeType is not null;
        }

        if (runtimeType is null)
        {
            if (ma.Target.Type is ExternalType ext)
                runtimeType = ResolveExternalToRuntime(ext);
            else if (ma.Target.Type is PrimitiveType prim)
                runtimeType = ResolveExternalRuntimeTypeByName(prim.Name);
        }

        if (runtimeType is null)
            return null;

        var methods = runtimeType.GetMethods(System.Reflection.BindingFlags.Public |
                                             (searchStatic ? System.Reflection.BindingFlags.Static : System.Reflection.BindingFlags.Instance))
            .Where(m => m.Name == ma.MemberName && ParameterCountMatches(m, args.Count));
        if (explicitTypeArgs is { Count: > 0 })
            methods = methods.Where(m => !m.IsGenericMethodDefinition || m.GetGenericArguments().Length == explicitTypeArgs.Count);
        var method = methods
            .OrderBy(m => m.GetParameters().Length)
            .ThenBy(m => RequiredParameterCount(m))
            .FirstOrDefault();

        if (method is null && !searchStatic)
            method = FindExtensionMethod(runtimeType, ma.MemberName, args.Count, args);

        if (method is null)
            return null;

        // A generic EXTENSION method called with EXPLICIT type args (`AddSingleton<BatchProcessor>(
        // (sp) => …)`): substitute the args into each parameter's type structurally, so the lambda
        // slot becomes `Func<IServiceProvider, BatchProcessor>` and the literal binds `sp:
        // IServiceProvider` AND returns `BatchProcessor`. Reflection's MakeGenericMethod can't be
        // used here — a module type arg has no runtime Type — so map the parameter types
        // structurally, substituting BOUND type args at the generic-parameter leaves.
        if (method.IsGenericMethodDefinition
            && method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)
            && explicitTypeArgs is { Count: > 0 }
            && explicitTypeArgs.Count == method.GetGenericArguments().Length)
        {
            var tps = method.GetGenericArguments();
            var map = new Dictionary<string, BoundType>(StringComparer.Ordinal);
            for (var i = 0; i < tps.Length; i++)
            {
                // `*T` in a generic-arg slot is the wrapper form (the invariant) — normalize
                // so a pointer type arg maps as `*T`, not a managed ref.
                var arg = ResolveType(explicitTypeArgs[i]);
                if (arg is ByRefBoundType br) arg = new HeapPointerBoundType(br.Inner);
                map[tps[i].Name] = arg;
            }
            var ps = method.GetParameters();
            if (ps.Length >= args.Count + 1)
                return ps.Skip(1).Take(args.Count)
                    .Select(p => MapRuntimeTypeWithBoundArgs(p.ParameterType, map))
                    .ToArray();
        }

        // A generic EXTENSION method (notably `Select<TSource,TResult>`): close the type
        // params inferable from the RECEIVER (`IEnumerable<TSource>` vs the receiver's
        // `List<int>` pins `TSource = int`) so a lambda param binds `x: int`. Params still
        // open (a lambda-return `TResult`) fall back to `object`, which the lambda binder
        // treats as "infer my return from the body". (Args here are still unbound syntax,
        // so only the receiver — not the other args — can drive inference at this stage.)
        if (method.IsGenericMethodDefinition
            && method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false))
        {
            var inferred = new Type[method.GetGenericArguments().Length];
            InferGenericArgFromParam(method.GetParameters()[0].ParameterType, runtimeType, inferred);
            for (var i = 0; i < inferred.Length; i++) inferred[i] ??= typeof(object);
            try
            {
                var cps = method.MakeGenericMethod(inferred).GetParameters();
                if (cps.Length >= args.Count + 1)
                    return cps.Skip(1).Take(args.Count)
                        .Select(p => MapRuntimeTypeToBoundType(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType))
                        .ToArray();
            }
            catch { /* fall through to the open-parameter mapping below */ }
        }

        var parameters = method.GetParameters();
        var offset = method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false) ? 1 : 0;
        if (parameters.Length < args.Count + offset)
            return null;

        return parameters.Skip(offset).Take(args.Count)
            .Select(p => MapRuntimeTypeToBoundType(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType))
            .ToArray();
    }

    // Close a generic extension-method definition for return-type inference. Explicit
    // call-site type args win (the only source for an OfType<T>-style parameter that
    // appears in no parameter); otherwise infer each from the receiver (arg 0) + the
    // actual argument types. Returns null when it can't pin every type parameter — the
    // caller then leaves the call's type unknown rather than leaking an open generic.
    System.Reflection.MethodInfo? TryCloseExtensionMethod(
        System.Reflection.MethodInfo def, Type receiverType,
        IReadOnlyList<BoundExpression> args, IReadOnlyList<BoundType>? explicitTypeArgs)
    {
        var typeParams = def.GetGenericArguments();
        if (explicitTypeArgs is { Count: > 0 } && explicitTypeArgs.Count == typeParams.Length)
        {
            var ta = explicitTypeArgs.Select(t => ResolveBoundTypeToRuntime(t) ?? typeof(object)).ToArray();
            try { return def.MakeGenericMethod(ta); } catch { return null; }
        }

        var paramInfos = def.GetParameters();
        var fullArgs = new Type?[args.Count + 1];
        fullArgs[0] = receiverType;
        for (var i = 0; i < args.Count; i++)
            // A lambda's own `.Type` is always `var`; reconstruct its delegate type from
            // the bound param/return types so `Func<TSource,TResult>` can pin `TResult`
            // (`xs.Select((x) => x + 1)` → `TResult = int` from the body) instead of erasing.
            fullArgs[i + 1] = args[i] is BoundFunctionLiteralExpression fl
                ? LambdaRuntimeDelegateType(fl)
                : ResolveBoundTypeToRuntime(args[i].Type);

        var inferred = new Type[typeParams.Length];
        for (var i = 0; i < paramInfos.Length && i < fullArgs.Length; i++)
            if (fullArgs[i] is { } a) InferGenericArgFromParam(paramInfos[i].ParameterType, a, inferred);

        for (var i = 0; i < inferred.Length; i++)
            if (inferred[i] is null) return null; // couldn't pin every type parameter

        try { return def.MakeGenericMethod(inferred); } catch { return null; }
    }

    // Close an ordinary BCL generic method from its actual arguments.  Unlike the
    // extension helper above there is no synthetic receiver slot.  Lambda nodes are
    // typed `var` in the bound tree, so their real Func/Action shape must be rebuilt
    // before unification; otherwise Task.Run(Func<Task<TResult>>) loses TResult and
    // binds as the less-specific Task.Run(Func<Task>) overload.
    System.Reflection.MethodInfo? TryCloseGenericMethodFromArguments(
        System.Reflection.MethodInfo definition, IReadOnlyList<BoundExpression> args,
        IReadOnlyList<BoundType>? explicitTypeArgs)
    {
        var typeParams = definition.GetGenericArguments();
        if (explicitTypeArgs is { Count: > 0 } && explicitTypeArgs.Count == typeParams.Length)
        {
            // A method type argument can be the enclosing E# type/method parameter
            // (`Task.Run<T>` inside `Spawn<T>`). It has no System.Type at bind time;
            // closing reflection with object loses the symbolic T and poisons the
            // inferred local as Task<object>. Leave this candidate open so the
            // caller can substitute its BOUND arguments structurally below.
            var explicitRuntime = explicitTypeArgs
                .Select(ResolveBoundTypeToRuntime)
                .ToArray();
            if (explicitRuntime.Any(t => t is null)) return null;
            try { return definition.MakeGenericMethod(explicitRuntime!); }
            catch { return null; }
        }

        var inferred = new Type[typeParams.Length];
        var parameters = definition.GetParameters();
        for (var i = 0; i < parameters.Length && i < args.Count; i++)
        {
            var argumentType = args[i] is BoundFunctionLiteralExpression lambda
                ? LambdaRuntimeDelegateType(lambda)
                : ResolveBoundTypeToRuntime(args[i].Type);
            if (argumentType is not null)
                InferGenericArgFromParam(parameters[i].ParameterType, argumentType, inferred);
        }
        if (inferred.Any(t => t is null)) return null;
        try { return definition.MakeGenericMethod(inferred); }
        catch { return null; }
    }

    int ScoreMethodParameterMatch(System.Reflection.MethodInfo method, IReadOnlyList<BoundExpression> args)
    {
        var score = 0;
        var parameters = method.GetParameters();
        for (var i = 0; i < args.Count; i++)
        {
            var argumentType = args[i] is BoundFunctionLiteralExpression lambda
                ? LambdaRuntimeDelegateType(lambda)
                : ResolveBoundTypeToRuntime(args[i].Type);
            if (argumentType is null) continue;
            var parameterType = parameters[i].ParameterType;
            if (parameterType == argumentType) score += 4;
            else if (parameterType.IsAssignableFrom(argumentType)) score++;
            else return -1;
        }
        return score;
    }

    // When two generic overloads close to the same delegate parameter type, retain
    // the original parameter pattern as a tie-breaker.  `Func<T>` and
    // `Func<Task<T>>` both close to `Func<Task<int>>` for Task.Run, but the latter
    // is more structurally specific and is the overload that unwraps the task.
    static int GenericPatternSpecificity(Type type)
    {
        if (type.IsByRef || type.IsPointer)
            return GenericPatternSpecificity(type.GetElementType()!);
        if (type.IsGenericParameter) return 0;
        if (!type.IsGenericType) return 1;
        return 1 + type.GetGenericArguments().Sum(GenericPatternSpecificity);
    }

    static int MethodPatternSpecificity(System.Reflection.MethodInfo method) =>
        method.GetParameters().Sum(p => GenericPatternSpecificity(p.ParameterType));

    // Recursively match a (possibly nested-generic) parameter pattern against a concrete
    // argument type, filling `inferred[pos]` for each generic parameter encountered.
    static void InferGenericArgFromParam(Type paramType, Type argType, Type[] inferred)
    {
        if (paramType.IsGenericParameter)
        {
            var pos = paramType.GenericParameterPosition;
            if (pos < inferred.Length && inferred[pos] is null) inferred[pos] = argType;
            return;
        }
        if (!paramType.IsGenericType || !paramType.ContainsGenericParameters) return;

        var open = paramType.GetGenericTypeDefinition();
        Type? matched = null;
        if (argType.IsGenericType && argType.GetGenericTypeDefinition() == open) matched = argType;
        else
            foreach (var iface in argType.GetInterfaces())
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == open) { matched = iface; break; }
        if (matched is null) return;

        var pArgs = paramType.GetGenericArguments();
        var aArgs = matched.GetGenericArguments();
        for (var i = 0; i < pArgs.Length && i < aArgs.Length; i++)
            InferGenericArgFromParam(pArgs[i], aArgs[i], inferred);
    }

    static System.Reflection.MethodInfo? FindExtensionMethod(Type receiverType, string methodName, int argCount,
        IReadOnlyList<ExpressionSyntax>? callArgs = null)
    {
        // Without a lambda arg: prefer a non-generic overload whose first parameter is
        // assignable from the receiver (`Enumerable.Sum(IEnumerable<int>)` → int), else a
        // generic definition — the long-standing first-match behavior.
        //
        // With a lambda arg: score by SHAPE so the lambda lands in a delegate parameter.
        // `AddSingleton<TService>(ISC, Func<IServiceProvider, TService>)` must beat the
        // instance overload `AddSingleton<TService>(ISC, TService)` (else the lambda gets no
        // delegate target and its params erase to `object`), and the minimal-API
        // `MapGet(pattern, Delegate)` catch-all must beat `MapGet(pattern, RequestDelegate)`
        // (so the lambda keeps its natural `-> string` return instead of being forced to Task).
        var lambdaPositions = callArgs is null ? null
            : Enumerable.Range(0, callArgs.Count).Where(i => callArgs[i] is FunctionLiteralExpressionSyntax).ToList();
        var hasLambdaArg = lambdaPositions is { Count: > 0 };

        System.Reflection.MethodInfo? genericMatch = null;
        System.Reflection.MethodInfo? bestScored = null;
        var bestScore = int.MinValue;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] asmTypes;
            try { asmTypes = asm.GetTypes(); }
            catch { continue; }
            foreach (var type in asmTypes)
            {
                if (!type.IsSealed || !type.IsAbstract) continue;
                if (!type.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)) continue;
                foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (m.Name != methodName) continue;
                    if (!m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != argCount + 1) continue;
                    // Receiver match: a NON-generic first parameter must be assignable from the
                    // receiver; a GENERIC one (`IEnumerable<TSource>`) is open and never
                    // `IsAssignableFrom` a closed receiver, so it's accepted and pinned later by
                    // inference — mirroring the original two-pass behavior (gating only non-generics).
                    if (!m.IsGenericMethodDefinition && !ps[0].ParameterType.IsAssignableFrom(receiverType)) continue;

                    if (!hasLambdaArg)
                    {
                        if (!m.IsGenericMethodDefinition) return m;
                        genericMatch ??= m;
                        continue;
                    }

                    var score = 0;
                    for (var i = 0; i < argCount; i++)
                    {
                        var pt = ps[i + 1].ParameterType;
                        if (lambdaPositions!.Contains(i))
                            score += IsDelegateParam(pt) ? (pt == typeof(Delegate) ? 3 : 2) : -3;
                        else if (IsDelegateParam(pt)) score -= 3;
                        else score += 1;
                    }
                    if (score > bestScore) { bestScore = score; bestScored = m; }
                }
            }
        }
        return hasLambdaArg ? bestScored : genericMatch;
    }

    // A parameter that accepts a delegate value: `System.Delegate` itself, a delegate
    // subtype, or an (open or closed) `Func`/`Action`/`Predicate`. Open generic forms
    // (`Func<IServiceProvider, TService>` on an unclosed extension) are recognized by their
    // generic-type-definition name, since `IsAssignableFrom` is unreliable on open types.
    static bool IsDelegateParam(Type pt)
    {
        if (pt == typeof(Delegate) || typeof(Delegate).IsAssignableFrom(pt)) return true;
        var def = pt.IsGenericType ? pt.GetGenericTypeDefinition() : pt;
        if (typeof(Delegate).IsAssignableFrom(def)) return true;
        var n = def.Name;
        return n.StartsWith("Func`", StringComparison.Ordinal)
            || n == "Action" || n.StartsWith("Action`", StringComparison.Ordinal)
            || n.StartsWith("Predicate`", StringComparison.Ordinal);
    }

    // Resolve a BCL method via reflection to determine the types of its out parameters.
    // Returns an array indexed by argument position; entries for non-out args are ignored.
    /// The declared parameter surfaces of a call's USER-DECLARED callee — one
    /// entry per overload (functions have exactly one; a class's init list has one
    /// per constructor), names + default-value syntax in slot order, receiver
    /// excluded. Null when the callee is external / unknown / intrinsic.
    IReadOnlyList<IReadOnlyList<ParameterSyntax>>? TryGetUserCalleeOverloads(BoundExpression target)
    {
        if (target is BoundNameExpression nameTarget)
        {
            if (Data.Symbols.TryGetFunction(nameTarget.Name)?.Decl is { } fn)
                return new[] { fn.Parameters };
            // Construction call `Foo(args)` / `Foo<T>(args)`: the positional header /
            // init constructors declare the slots — one surface per overload.
            var baseName = nameTarget.Name;
            var lt = baseName.IndexOf('<');
            if (lt > 0) baseName = baseName[..lt];
            if (char.IsUpper(baseName.Length > 0 ? baseName[0] : ' ')
                && Data.Symbols.DataDecl(baseName) is { } ctorDecl)
            {
                var ctorSurfaces = new List<IReadOnlyList<ParameterSyntax>>();
                // The capture header is the primary constructor's surface.
                if (ctorDecl.HeaderParameters is { Count: > 0 } header)
                    ctorSurfaces.Add(header);
                if (ctorDecl.Inits is { Count: > 0 } inits)
                    foreach (var init in inits)
                    {
                        // On a headered class a zero-param `init { }` is the primary's
                        // epilogue, not a constructible surface of its own.
                        if (ctorDecl.HeaderParameters is { Count: > 0 }
                            && init.Parameters.Count == 0 && init.ThisArguments is null && init.BaseArguments is null)
                            continue;
                        ctorSurfaces.Add(init.Parameters);
                    }
                if (ctorSurfaces.Count > 0) return ctorSurfaces;
            }
            return null;
        }
        if (target is BoundMemberAccessExpression ma)
        {
            // Static-func member: `Host.fn(args)`.
            if ((ma.Target.Type is StaticFuncType
                    || ma.Target.Type is DataType dt && Data.Symbols.StaticFuncDecl(dt.Name) is not null)
                && Data.Symbols.TryGetFunction($"{ma.Target.Type.EmitName}.{ma.MemberName}") is { Decl: { } sfFn } staticMethod)
                return new[] { (IReadOnlyList<ParameterSyntax>)(staticMethod.ReceiverKind == ReceiverKind.Static
                    ? sfFn.Parameters.Skip(1).ToList()
                    : sfFn.Parameters) };
            // Promoted method on a data/class receiver: declared params minus the receiver.
            if (Data.Symbols.TryGetPromoted(ma.MemberName) is { Decl: { } pmFn, DeclaringType: { } pmRecv }
                && ReceiverBaseName(ma.Target.Type) == pmRecv.Name
                && pmFn.Parameters.Count > 0)
                return new[] { (IReadOnlyList<ParameterSyntax>)pmFn.Parameters.Skip(1).ToList() };
            // Namespace-qualified free function: `NS.fn(args)`.
            if (ma.Target is BoundNameExpression nsName
                && Data.Symbols.IsKnownNamespace(nsName.Name)
                && Data.Symbols.TryGetFunction(ma.MemberName)?.Decl is { } qFn)
                return new[] { qFn.Parameters };
        }
        return null;
    }

    /// Rewrite a call's arguments into PARAMETER ORDER: positional args fill the
    /// leading slots, named args land by name, and omitted slots materialize the
    /// parameter's declared default (user callees) or metadata default (external
    /// optionals). After this, `ArgumentNames` is gone and every downstream
    /// consumer sees a plain positional call. Arguments evaluate in parameter order.
    CallExpressionSyntax NormalizeCallArguments(CallExpressionSyntax syntax, BoundExpression target)
    {
        var names = syntax.ArgumentNames;
        var hasNames = names is not null && names.Any(n => n is not null);
        var overloads = TryGetUserCalleeOverloads(target);

        if (overloads is not null)
        {
            // Fast path: a single overload, all positional, every slot supplied.
            if (overloads.Count == 1 && !hasNames && syntax.Arguments.Count == overloads[0].Count)
                return syntax;
            // Surplus positional args on a single overload: leave for the existing
            // arity diagnostics.
            if (overloads.Count == 1 && !hasNames && syntax.Arguments.Count > overloads[0].Count)
                return syntax;

            // Try every overload silently; an overload matches when the positional/
            // named mapping succeeds and every unfilled slot has a default.
            var matches = new List<(IReadOnlyList<ParameterSyntax> Params, ExpressionSyntax[] Slots, int DefaultsUsed)>();
            foreach (var ps in overloads)
            {
                if (TryFillUserSlots(syntax, names, ps, silent: true) is { } filled)
                    matches.Add((ps, filled.Slots, filled.DefaultsUsed));
            }
            switch (matches.Count)
            {
                case 1:
                    return syntax with { Arguments = matches[0].Slots, ArgumentNames = null };
                case 0:
                    // Re-run the closest-arity overload loudly for real diagnostics.
                    var best = overloads.OrderBy(ps => Math.Abs(ps.Count - syntax.Arguments.Count)).First();
                    TryFillUserSlots(syntax, names, best, silent: false);
                    return syntax;
                default:
                    // Prefer the unique exact-coverage match (no defaults filled).
                    var exact = matches.Where(m => m.DefaultsUsed == 0).ToList();
                    if (exact.Count == 1)
                        return syntax with { Arguments = exact[0].Slots, ArgumentNames = null };
                    Diagnostics.Report(syntax.Span,
                        $"ES2185: ambiguous call — {matches.Count} 'init' overloads accept these arguments. Disambiguate with named arguments or adjust the overloads' arity.");
                    return syntax;
            }
        }

        // External callee — ONLY when names were used. A plain positional call with
        // omitted optionals already flows through the emit-time overload scorer
        // (which is authoritative for externals); rewriting it here against a
        // bind-time method guess would fight that resolution.
        if (!hasNames) return syntax;
        if (target is BoundMemberAccessExpression extMa
            && ResolveCalleeRuntimeType(extMa) is { } rt)
        {
            var method = rt.GetMethods(System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)
                .Where(m => m.Name == extMa.MemberName && ParameterCountMatches(m, syntax.Arguments.Count))
                .OrderBy(m => m.GetParameters().Length)
                .ThenBy(RequiredParameterCount)
                .FirstOrDefault();
            if (method is not null)
            {
                var ps = method.GetParameters();
                if (MapToSlots(syntax, names, ps.Length,
                        n => Array.FindIndex(ps, p => p.Name == n)) is not { } slots)
                    return syntax;
                for (var j = 0; j < slots.Length; j++)
                {
                    if (slots[j] is not null) continue;
                    // nil only fills a reference-typed optional; a struct optional's
                    // metadata default has no literal spelling — supply it explicitly.
                    if (ps[j].IsOptional
                        && (!ps[j].ParameterType.IsValueType || ps[j].DefaultValue is not null)
                        && TrySynthesizeLiteral(ps[j].DefaultValue) is { } lit)
                        slots[j] = lit;
                    else
                    {
                        Diagnostics.Report(syntax.Span,
                            $"ES2183: no argument for parameter '{ps[j].Name}' of '{extMa.MemberName}' and its default cannot be materialized — pass it explicitly.");
                        return syntax;
                    }
                }
                return syntax with { Arguments = slots!, ArgumentNames = null };
            }
        }

        if (hasNames)
            Diagnostics.Report(syntax.Span,
                "ES2182: named arguments require a callee with a known parameter list (a declared function, method, constructor, or resolvable external method).");
        return syntax;
    }

    void ReportNamedArgsUnsupported(CallExpressionSyntax syntax, string where)
    {
        if (syntax.ArgumentNames is { } ns && ns.Any(n => n is not null))
            Diagnostics.Report(syntax.Span,
                $"ES2182: named arguments are not supported on {where} — pass the payloads positionally.");
    }

    /// Map + default-fill against one overload's parameter list. Returns the
    /// final slot expressions and how many came from defaults, or null when the
    /// overload does not accept the arguments (silently, unless `silent` is false).
    (ExpressionSyntax[] Slots, int DefaultsUsed)? TryFillUserSlots(
        CallExpressionSyntax syntax, IReadOnlyList<string?>? names,
        IReadOnlyList<ParameterSyntax> ps, bool silent)
    {
        if (MapToSlots(syntax, names, ps.Count, n => IndexOfParam(ps, n), silent) is not { } slots)
            return null;
        var defaultsUsed = 0;
        for (var j = 0; j < slots.Length; j++)
        {
            if (slots[j] is not null) continue;
            if (ps[j].DefaultValue is { } def)
            {
                slots[j] = def;
                defaultsUsed++;
            }
            else
            {
                if (!silent)
                    Diagnostics.Report(syntax.Span,
                        $"ES2183: no argument for parameter '{ps[j].Name}' and it declares no default.");
                return null;
            }
        }
        return (slots!, defaultsUsed);
    }

    static int IndexOfParam(IReadOnlyList<ParameterSyntax> ps, string name)
    {
        for (var i = 0; i < ps.Count; i++)
            if (ps[i].Name == name) return i;
        return -1;
    }

    /// Shared positional/named → slot mapping. Returns null (after reporting) on
    /// any ordering, unknown-name, arity, or duplicate error.
    ExpressionSyntax?[]? MapToSlots(CallExpressionSyntax syntax, IReadOnlyList<string?>? names,
        int paramCount, Func<string, int> indexOfName, bool silent = false)
    {
        var slots = new ExpressionSyntax?[paramCount];
        var seenNamed = false;
        for (var i = 0; i < syntax.Arguments.Count; i++)
        {
            var nm = names is not null && i < names.Count ? names[i] : null;
            if (nm is null)
            {
                if (seenNamed)
                {
                    if (!silent)
                        Diagnostics.Report(syntax.Arguments[i].Span,
                            "ES2181: positional arguments must precede named arguments.");
                    return null;
                }
                if (i >= paramCount)
                {
                    if (!silent)
                        Diagnostics.Report(syntax.Span,
                            $"ES2183: too many arguments — the callee declares {paramCount} parameter(s).");
                    return null;
                }
                slots[i] = syntax.Arguments[i];
            }
            else
            {
                seenNamed = true;
                var idx = indexOfName(nm);
                if (idx < 0)
                {
                    if (!silent)
                        Diagnostics.Report(syntax.Arguments[i].Span,
                            $"ES2182: no parameter named '{nm}' on the callee.");
                    return null;
                }
                if (slots[idx] is not null)
                {
                    if (!silent)
                        Diagnostics.Report(syntax.Arguments[i].Span,
                            $"ES2184: parameter '{nm}' is given more than one argument.");
                    return null;
                }
                slots[idx] = syntax.Arguments[i];
            }
        }
        return slots;
    }

    /// A literal syntax node for an external optional parameter's metadata default.
    /// Returns null for defaults that have no literal spelling (enums, decimals, …) —
    /// the caller then leaves the call unfilled rather than guessing.
    static ExpressionSyntax? TrySynthesizeLiteral(object? value) => value switch
    {
        null => new LiteralExpressionSyntax(null, "nil"),
        int or long or double or float or bool or string or char or byte or short
            => new LiteralExpressionSyntax(value, value.ToString() ?? ""),
        _ => null,
    };

    /// The runtime type a member call's external callee is looked up on — a static
    /// type name (`int.TryParse`), or the receiver's external/primitive bound type.
    Type? ResolveCalleeRuntimeType(BoundMemberAccessExpression ma)
    {
        Type? runtimeType = null;
        if (ma.Target is BoundNameExpression name)
            runtimeType = TryResolveRuntimeTypeByName(name.Name);
        if (runtimeType is null)
        {
            runtimeType = ma.Target.Type switch
            {
                ExternalType ext => ResolveExternalToRuntime(ext),
                PrimitiveType prim => TryResolveRuntimeTypeByName(prim.Name),
                _ => null,
            };
        }
        return runtimeType;
    }

    BoundType[]? TryResolveOutSlotTypes(BoundExpression target, IReadOnlyList<ExpressionSyntax> args)
    {
        if (target is not BoundMemberAccessExpression ma) return null;

        var runtimeType = ResolveCalleeRuntimeType(ma);
        if (runtimeType is null) return null;

        var methods = runtimeType.GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
        var method = methods
            .Where(m => m.Name == ma.MemberName && ParameterCountMatches(m, args.Count))
            .OrderBy(m => m.GetParameters().Length)
            .ThenBy(m => RequiredParameterCount(m))
            .FirstOrDefault();
        if (method is null) return null;

        var parameters = method.GetParameters();
        var result = new BoundType[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            var paramType = parameters[i].ParameterType;
            if (paramType.IsByRef) paramType = paramType.GetElementType()!;
            result[i] = MapRuntimeTypeToBoundType(paramType);
        }
        return result;
    }

    Type? TryResolveRuntimeTypeByName(string name) => name switch
    {
        "int" => typeof(int),
        "long" => typeof(long),
        "double" => typeof(double),
        "float" => typeof(float),
        "bool" => typeof(bool),
        "string" => typeof(string),
        "byte" => typeof(byte),
        "short" => typeof(short),
        _ => ResolveExternalRuntimeTypeByName(name),
    };

    static bool ParameterCountMatches(System.Reflection.MethodInfo method, int argCount)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == argCount)
            return true;
        if (parameters.Length < argCount)
            return false;
        return RequiredParameterCount(method) <= argCount;
    }

    static int RequiredParameterCount(System.Reflection.MethodInfo method) =>
        method.GetParameters().Count(p => !p.IsOptional && !p.IsDefined(typeof(ParamArrayAttribute), false));


    BoundFunctionLiteralExpression BindFunctionLiteral(FunctionLiteralExpressionSyntax syntax, BoundType? expectedType = null,
        IReadOnlyList<BoundType>? forcedParamTypes = null)
    {
        var prevScope = Scope;
        var literalScope = Scope.Child();
        Scope = literalScope;

        // The expected type can shape the literal's param/return types in two
        // ways: as a CLR delegate (Func/Action) or as an E# function pointer
        // `&(T -> U)`. Both pull the same shape; we just record which one
        // applies so the emitter can pick `ldftn`-only (fnptr) vs
        // `ldnull/ldftn/newobj` (delegate).
        var delegateShape = TryGetExpectedDelegateShape(expectedType);
        var fnptrExpected = expectedType as FunctionPointerType;

        IReadOnlyList<BoundType>? expectedParamTypes = null;
        BoundType? expectedReturnType = null;
        if (delegateShape is not null)
        {
            expectedParamTypes = delegateShape.Value.ParameterTypes;
            expectedReturnType = delegateShape.Value.ReturnType;
        }
        else if (fnptrExpected is not null)
        {
            expectedParamTypes = fnptrExpected.ParameterTypes;
            expectedReturnType = fnptrExpected.ReturnType;
        }

        var parameters = new List<BoundParameter>();
        for (var i = 0; i < syntax.Parameters.Count; i++)
        {
            var p = syntax.Parameters[i];
            // forcedParamTypes wins for inferred params: the IIFE path pins each
            // parameter to its call argument's type, where no delegate slot exists.
            var paramType = p.Type is InferredTypeSyntax && forcedParamTypes is not null && i < forcedParamTypes.Count
                ? forcedParamTypes[i]
                : p.Type is InferredTypeSyntax && expectedParamTypes is not null && i < expectedParamTypes.Count
                    ? expectedParamTypes[i]
                    : ResolveType(p.Type);
            var (byRef, readOnlyByRef) = RefFlags(p.Type);
            parameters.Add(new BoundParameter(p.Name, paramType, byRef, readOnlyByRef));
            DeclareLocal(p.Name, paramType, mutable: true, p.NameSpan, isParameter: true);
        }

        // A lambda owns its own asyncness: an `await` in its body makes the LAMBDA async
        // (its own state machine), never the enclosing function. Save/restore the
        // await flag across the body bind so the enclosing callable's color is unaffected.
        var prevHasAwait = Ctx.CurrentFunctionHasAwait;
        var prevAllowsYield = Ctx.CurrentFunctionAllowsYield;
        var prevInNamespaceInitializer = Ctx.InNamespaceInitializer;
        Ctx.CurrentFunctionAllowsYield = false;
        // The initializer itself must stay synchronous, but a nested lambda is
        // an independently invoked callable and keeps its ordinary async rules.
        Ctx.InNamespaceInitializer = false;
        var body = Statements.BindBlock(syntax.Body);
        Ctx.CurrentFunctionHasAwait = prevHasAwait;
        Ctx.CurrentFunctionAllowsYield = prevAllowsYield;
        Ctx.InNamespaceInitializer = prevInNamespaceInitializer;

        // Resolve the literal's return type. Priority: an explicit `-> T` annotation,
        // then a CONCRETE expected return (the target delegate's result, which coerces
        // the body — e.g. an int-returning body into a `Func<…, long>` slot), then the
        // body's own inferred result. The body-inference step is what lets a combinator
        // lambda's result be its real type: `r.Map((x) => x + 1)` binds `x: TV` from the
        // expected param shape but leaves the return implicit, so it infers `int` here —
        // the combinator's `TNew` is then the real type, not an erased `object`.
        BoundType returnType;
        if (syntax.ReturnType is not InferredTypeSyntax)
            returnType = ResolveType(syntax.ReturnType);
        else if (expectedReturnType is not null && !IsInferPlaceholder(expectedReturnType))
            returnType = expectedReturnType;
        else if (InferReturnTypeFromBody(body) is { } inferred && !IsInferPlaceholder(inferred))
            returnType = inferred;
        else
            returnType = expectedReturnType ?? InferredType.Instance;

        // Detect captured variables: names referenced in body that resolve from an ancestor scope
        var captures = new List<BoundCapturedVariable>();
        CollectCaptures(body, literalScope, captures);

        Scope = prevScope;

        // Function-pointer materialization is only legal when the literal
        // captures nothing — a fnptr is just an `ldftn` of a static method and
        // can't carry an env. If captures exist but the slot expected a fnptr,
        // we leave IsFunctionPointer false; the call site sees a delegate-typed
        // value going into a fnptr slot and the downstream type check fires.
        var isFnptr = fnptrExpected is not null && captures.Count == 0;

        // Uncolored async at the closure boundary: a literal whose body awaits is itself
        // asynchronous, lowered to its own state machine by the emitter. The DECLARED
        // return is kept as the awaitable wrapper (so the delegate stays `Func<Task<T>>`);
        // AsyncBinder classifies the wrapper into the builder shape the SM uses.
        var isAsync = AsyncBinder.BodyAwaitsAtThisLevel(body);
        var asyncShape = isAsync
            ? AsyncBinder.ClassifyReturn(returnType, explicitReturn: syntax.ReturnType is not InferredTypeSyntax).Shape
            : AsyncReturnShape.ValueTask;
        return new BoundFunctionLiteralExpression(parameters, returnType, body, captures, isFnptr, isAsync, asyncShape);
    }

    static ExpressionSyntax UnwrapParenthesized(ExpressionSyntax e)
    {
        while (e is ParenthesizedExpressionSyntax paren)
            e = paren.Expression;
        return e;
    }

    static BoundExpression UnwrapBoundParens(BoundExpression e)
    {
        // Unwrap smart-cast narrow conversions (BoundConversion.Narrow), since a
        // narrowed IIFE target (`(narrowed_f)(args)`) still needs to see the inner
        // literal.  BoundParenthesizedExpression no longer exists — the binder
        // unwraps it in-place during BindExpressionCore so it never enters the tree.
        if (e is BoundConversion { Kind: ConversionKind.Narrow } nconv)
            e = nconv.Operand;
        return e;
    }

    /// A return type that means "not yet pinned" — `var` (implicit) or `object` (the
    /// erased default a combinator hands a lambda as its expected return so the real
    /// result type is inferred from the body rather than forced to `object`). The CLR
    /// round-trips a rendered `object` arg back as `ExternalType("Object")`, so match
    /// both casings.
    static bool IsInferPlaceholder(BoundType t) => t is InferredType or ExternalType { Name: "object" or "Object" };

    /// The type of the first value-returning statement in a lambda body, NOT descending
    /// into nested function literals (their returns aren't this lambda's). Lets a lambda
    /// whose source return is implicit (`-> var`) take its result type from its body.
    BoundType? InferReturnTypeFromBody(BoundStatement stmt) => stmt switch
    {
        BoundReturnStatement { Expression: { } e } => e.Type,
        BoundBlockStatement block => block.Statements.Select(InferReturnTypeFromBody).FirstOrDefault(t => t is not null),
        BoundIfStatement bif => InferReturnTypeFromBody(bif.Then) ?? (bif.Else is { } el ? InferReturnTypeFromBody(el) : null),
        _ => null,
    };

    internal void CollectCaptures(BoundStatement stmt, BinderScope literalScope, List<BoundCapturedVariable> captures)
    {
        switch (stmt)
        {
            case BoundBlockStatement block:
                foreach (var s in block.Statements)
                    CollectCaptures(s, literalScope, captures);
                break;
            case BoundVariableDeclaration vd:
                CollectCapturesExpr(vd.Initializer, literalScope, captures);
                break;
            case BoundReturnStatement ret:
                if (ret.Expression is not null) CollectCapturesExpr(ret.Expression, literalScope, captures);
                break;
            case BoundExpressionStatement es:
                CollectCapturesExpr(es.Expression, literalScope, captures);
                break;
            case BoundAssignment a:
                CollectCapturesExpr(a.Target, literalScope, captures);
                CollectCapturesExpr(a.Value, literalScope, captures);
                break;
            case BoundCompoundAssignment ca:
                CollectCapturesExpr(ca.Target, literalScope, captures);
                CollectCapturesExpr(ca.Value, literalScope, captures);
                break;
            case BoundIfStatement ifs:
                CollectCapturesExpr(ifs.Condition, literalScope, captures);
                CollectCaptures(ifs.Then, literalScope, captures);
                if (ifs.Else is not null) CollectCaptures(ifs.Else, literalScope, captures);
                break;
            case BoundWhileStatement ws:
                CollectCapturesExpr(ws.Condition, literalScope, captures);
                CollectCaptures(ws.Body, literalScope, captures);
                break;
            case BoundForEachStatement fe:
                CollectCapturesExpr(fe.Collection, literalScope, captures);
                CollectCaptures(fe.Body, literalScope, captures);
                break;
        }
    }

    internal void CollectCapturesExpr(BoundExpression expr, BinderScope literalScope, List<BoundCapturedVariable> captures)
    {
        switch (expr)
        {
            case BoundNameExpression name:
                // If the name isn't local to the literal's scope, it's captured from outside.
                // Propagate mutability from the enclosing scope so downstream checks
                // (notably ES2130 on `task func`) can flag mutable captures.
                if (!literalScope.ContainsLocal(name.Name) && Scope.Lookup(name.Name) is not null
                    && captures.All(c => c.Name != name.Name))
                {
                    var mutable = Scope.LookupMutable(name.Name) ?? false;
                    captures.Add(new BoundCapturedVariable(name.Name, name.Type, mutable));
                }
                break;
            case BoundCallExpression call:
                CollectCapturesExpr(call.Target, literalScope, captures);
                foreach (var arg in call.Arguments) CollectCapturesExpr(arg, literalScope, captures);
                break;
            case BoundBinaryExpression bin:
                CollectCapturesExpr(bin.Left, literalScope, captures);
                CollectCapturesExpr(bin.Right, literalScope, captures);
                break;
            case BoundUnaryExpression un:
                CollectCapturesExpr(un.Operand, literalScope, captures);
                break;
            case BoundMemberAccessExpression ma:
                CollectCapturesExpr(ma.Target, literalScope, captures);
                break;
            case BoundAddressOfVariableExpression address:
                // Address-taking is still a use of the addressed binding. Without
                // descending here, `func() { consume(&outer) }` was misclassified
                // as a static lambda and emitted a call with no managed-ref argument.
                CollectCapturesExpr(address.Target, literalScope, captures);
                break;
            case BoundTypeTestExpression tt:
                CollectCapturesExpr(tt.Operand, literalScope, captures);
                break;
            // BoundConversion (frozen contract §3) unifies all cast/narrow forms.
            // BoundSafeCastExpression and BoundAssertCastExpression are removed;
            // BindCast now emits BoundConversion.SafeCast / BoundConversion.AssertCast.
            case BoundConversion conv:
                CollectCapturesExpr(conv.Operand, literalScope, captures);
                break;
        }
    }

    BoundExpression BindAwait(AwaitExpressionSyntax syntax)
    {
        if (Ctx.InNamespaceInitializer)
        {
            Diagnostics.Report(syntax.Span, "ES2208: 'await' is not valid inside a namespace 'init' block.");
            var previous = Ctx.BindingAwaitInner;
            Ctx.BindingAwaitInner = true;
            _ = BindExpression(syntax.Inner);
            Ctx.BindingAwaitInner = previous;
            // Do not leave a FEATURE await node for a body that is deliberately
            // rejected before lowering; diagnostics are the user-facing result.
            return new BoundErrorExpression();
        }
        var prev = Ctx.BindingAwaitInner;
        Ctx.BindingAwaitInner = true;
        var inner = BindExpression(syntax.Inner);
        Ctx.BindingAwaitInner = prev;
        Ctx.CurrentFunctionHasAwait = true;

        // If the inner is a Task/ValueTask shape, unwrap to the result type;
        // otherwise the inner is already the result value — an uncolored user-async
        // call returning a bare `T` (T not task-shaped) sees its return type as the
        // result directly, since the ValueTask wrapping is invisible at source level.
        // (An explicit `-> Task<T>` user async registers its Task type, which DOES
        // unwrap here — so awaiting it yields T, like any other awaitable.)
        var unwrapped = InferAwaitResultType(inner.Type);
        var resultType = unwrapped is InferredType ? inner.Type : unwrapped;
        return new BoundAwaitExpression(inner, resultType);
    }

    /// <summary>
    /// Unwraps an awaitable to the value returned by its awaiter's GetResult method.
    /// Task and ValueTask keep their structured fast path so their type arguments never
    /// take a reflection round trip; custom E# awaitables (notably Spawned&lt;T&gt;) use
    /// their public GetAwaiter/GetResult contract.
    /// </summary>
    internal BoundType InferAwaitResultType(BoundType taskType)
    {
        if (taskType is ExternalType ext)
        {
            // Structured Task<T> / ValueTask<T>: the result is the bound argument
            // itself — no runtime round-trip, so a user `data` / `*T` result survives
            // instead of erasing through reflection.
            if (ext is { Name: "Task" or "ValueTask", TypeArgs: [var resultArg] })
                return resultArg;

            var runtime = ResolveExternalToRuntime(ext);
            if (runtime is not null && runtime.IsGenericType)
            {
                var def = runtime.GetGenericTypeDefinition();
                if (def == typeof(System.Threading.Tasks.Task<>)
                    || def == typeof(System.Threading.Tasks.ValueTask<>))
                {
                    return MapRuntimeTypeToBoundType(runtime.GetGenericArguments()[0]);
                }
            }
            // Non-generic Task / ValueTask → void
            if (runtime == typeof(System.Threading.Tasks.Task)
                || runtime == typeof(System.Threading.Tasks.ValueTask))
                return new VoidType();

            // `Spawned` / `Spawned<T>` deliberately expose the normal .NET awaiter
            // pattern rather than being special compiler types.  Looking through the
            // awaiter gives `TaskAwaiter<T>.GetResult(): T`, while preserving the
            // language-level result type instead of treating the handle itself as T.
            var awaiter = runtime?.GetMethod("GetAwaiter", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                binder: null, Type.EmptyTypes, modifiers: null)?.ReturnType;
            var getResult = awaiter?.GetMethod("GetResult", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                binder: null, Type.EmptyTypes, modifiers: null);
            if (getResult is not null)
                return getResult.ReturnType == typeof(void)
                    ? new VoidType()
                    : MapRuntimeTypeToBoundType(getResult.ReturnType);
        }
        return InferredType.Instance;
    }

    BoundSpawnExpression BindSpawn(SpawnExpressionSyntax syntax)
    {
        var prevScope = Scope;
        var spawnScope = Scope.Child();
        Scope = spawnScope;

        var prevAllowsYield = Ctx.CurrentFunctionAllowsYield;
        var prevInNamespaceInitializer = Ctx.InNamespaceInitializer;
        Ctx.CurrentFunctionAllowsYield = false;
        // `spawn { ... }` owns a separate callable body just like a lambda.
        Ctx.InNamespaceInitializer = false;
        var body = Statements.BindBlock(syntax.Body);
        Ctx.CurrentFunctionAllowsYield = prevAllowsYield;
        Ctx.InNamespaceInitializer = prevInNamespaceInitializer;

        var captures = new List<BoundCapturedVariable>();
        CollectCaptures(body, spawnScope, captures);

        Scope = prevScope;
        return new BoundSpawnExpression(body, captures);
    }

    // Pointer-mismatch diagnostics at call sites. Mirrors the existing
    // assignment-side rule (BindAssignment): a value-T can't slip into a *T
    // slot without an explicit '&' or '*', and a *T can't slip into a T slot
    // at all (no auto-deref). Both directions are needed because relying only
    // on the assignment check lets `describe(*c)` (or even `describe(c)` when
    // `c: *Client`) silently pass the wrapper class to a value-T parameter.
    void CheckPointerArgMismatch(ExpressionSyntax argSyntax, BoundExpression bound, BoundType expected)
    {
        // A normal `*T` parameter is a writable borrow. `&` may name a local or
        // direct field, but it must not smuggle a write through a `let` binding or
        // write-once field. `readonly *T` does not arrive as HeapPointerBoundType,
        // so immutable storage remains available to read-only borrows.
        if (expected is HeapPointerBoundType
            && bound is BoundAddressOfVariableExpression address
            && !IsWritableBorrowTarget(address.Target))
        {
            Diagnostics.Report(argSyntax.Span,
                "Cannot pass an immutable location to a mutable '*T' parameter — use a 'var' local or mutable direct field, or accept 'readonly *T'.");
        }

        // Case 1: *T arg → T param (the seam — no auto-deref at call sites yet).
        if (bound.Type is HeapPointerBoundType ptrArg
            && expected is not HeapPointerBoundType
            && expected is not ByRefBoundType
            && expected is not NullableType
            && PointerInnerMatches(ptrArg.Inner, expected))
        {
            Diagnostics.Report(argSyntax.Span,
                $"Cannot pass '*{TypeDisplayName(ptrArg.Inner)}' where '{TypeDisplayName(expected)}' is expected — pointer types do not auto-deref at call sites.");
            return;
        }

        // Case 2: T arg → *T param (without ref-pass / heap-alloc syntax).
        if (expected is HeapPointerBoundType ptrParam
            && bound.Type is not HeapPointerBoundType
            && bound.Type is not ByRefBoundType
            && bound.Type is not NullType
            && PointerInnerMatches(ptrParam.Inner, bound.Type))
        {
            // Exempt the legitimate ref-pass / heap-alloc forms.
            bool isRefPassSyntax =
                argSyntax is UnaryExpressionSyntax { OperatorKind: SyntaxTokenKind.Star }
                || argSyntax is AddressOfExpressionSyntax
                || bound is BoundHeapAllocExpression
                || bound is BoundAddressOfVariableExpression;
            if (!isRefPassSyntax)
            {
                Diagnostics.Report(argSyntax.Span,
                    $"Cannot pass '{TypeDisplayName(bound.Type)}' where '*{TypeDisplayName(ptrParam.Inner)}' is expected — use '&{TypeDisplayName(ptrParam.Inner)} {{ ... }}' to heap-allocate or '*name' to ref-pass.");
            }
        }
    }

    bool IsWritableBorrowTarget(BoundExpression target)
    {
        if (target is BoundNameExpression name)
            return Scope.LookupMutable(name.Name) != false;

        if (target is not BoundMemberAccessExpression member)
            return false;

        var receiver = member.Target.Type is HeapPointerBoundType pointer ? pointer.Inner : member.Target.Type;
        if (receiver is DataType data
            && Data.Symbols.DataDecl(data.Name) is { } declaration
            && declaration.Fields.FirstOrDefault(field => field.Name == member.MemberName) is { } field)
        {
            if (field.Property is { } property)
            {
                if (property.MutStorageName is { } storage)
                    return declaration.Fields.FirstOrDefault(candidate => candidate.Name == storage)?.Mutable == true;
                if (property.ScopedMutBody is { } scoped)
                    return ScopedMutYieldsMutableLocal(scoped);
            }
            if (!field.Mutable) return false;
        }

        if (receiver is ExternalCSharpType csharp
            && csharp.Handle.Members.FirstOrDefault(candidate =>
                candidate.Kind == CSharpMemberKind.Property && candidate.Name == member.MemberName) is { } csharpProperty)
            return csharpProperty.ReturnsByRef && !csharpProperty.ReturnsByRefReadonly;

        // Imported and interface properties carry their caller-visible writable
        // direction on the interned symbol. Source properties are derived from
        // their direct/scoped `mut` construction above: the early declaration
        // symbol intentionally records syntax before scoped-mut binding is done.
        if (member.Member is Esharp.Symbols.FieldSymbol { IsProperty: true } propertySymbol)
            return propertySymbol.Mutable;

        // Mutating a field of a value receiver mutates the receiver's storage, so
        // follow the chain back to its root binding. A class receiver is itself a
        // reference value: an immutable binding fixes the reference, not a mutable
        // direct field on the object it denotes.
        if (receiver is PrimitiveType
            || receiver is DataType valueData && ClassifyData(valueData.Name) != DataClassification.Class)
            return IsWritableBorrowTarget(member.Target);

        return true;
    }

    static bool ScopedMutYieldsMutableLocal(BlockStatementSyntax scoped)
    {
        var locals = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var statement in scoped.Statements)
        {
            if (statement is VariableDeclarationStatementSyntax local)
            {
                locals[local.Name] = local.Mutable;
                continue;
            }
            if (statement is MutYieldStatementSyntax
                {
                    Location: AddressOfExpressionSyntax
                    {
                        Target: NameExpressionSyntax { Name: var name }
                    }
                })
                return locals.TryGetValue(name, out var mutable) && mutable;
        }
        return false;
    }

    // Best-effort inference for BCL member call return types. Handles both instance method
    // calls (`list.Sum()`) and static calls (`Enumerable.Count<int>(list)`, `int.Parse(s)`).
    /// All public methods of a type, including (for an interface) those declared on its
    /// base interfaces — which `Type.GetMethods` omits for interfaces.
    static IEnumerable<System.Reflection.MethodInfo> MethodsIncludingBaseInterfaces(Type t, System.Reflection.BindingFlags flags)
    {
        foreach (var m in t.GetMethods(flags)) yield return m;
        if (t.IsInterface)
            foreach (var baseIface in t.GetInterfaces())
                foreach (var m in baseIface.GetMethods(flags)) yield return m;
    }

    // Unify an extension method's `this`-parameter reflection type against the receiver's
    // BOUND type, filling methodSlots[pos] for each method type parameter the receiver
    // pins. `this IAsyncEnumerable<T>` vs bound `IAsyncEnumerable<T0>` ⇒ methodSlots[T]=T0.
    static void UnifyExtensionReceiverBound(Type reflectParam, BoundType boundArg, BoundType?[] methodSlots)
    {
        if (reflectParam.IsGenericMethodParameter)
        {
            var pos = reflectParam.GenericParameterPosition;
            if (pos < methodSlots.Length && methodSlots[pos] is null) methodSlots[pos] = boundArg;
            return;
        }
        if (reflectParam.IsGenericType && boundArg is ExternalType bext)
        {
            var ra = reflectParam.GetGenericArguments();
            for (var i = 0; i < ra.Length && i < bext.TypeArgs.Count; i++)
                UnifyExtensionReceiverBound(ra[i], bext.TypeArgs[i], methodSlots);
        }
    }

    BoundType? TryInferMemberCallReturnType(BoundMemberAccessExpression call, IReadOnlyList<BoundExpression> args, IReadOnlyList<BoundType>? explicitTypeArgs = null, SourceSpan useSpan = default)
    {
        // The receiver of the call is `call.Target` — for `nums.Sum()`, that's the `nums`
        // BoundNameExpression, whose .Type is `ExternalType("List<int>")`.
        Type? receiverType = null;
        bool searchStatic = false;

        // If the receiver looks like a static type reference (upper-case name not in scope),
        // look up that type. Lowercase primitive aliases like `int.Parse(s)` need to
        // resolve too — the underlying CLR type is System.Int32 and the call is static.
        if (call.Target is BoundNameExpression nameTarget)
        {
            var nm = nameTarget.Name;
            var asStatic = LooksLikeTypeName(nm)
                ? ResolveExternalRuntimeTypeByName(nm)
                : nm switch
                {
                    "int" => typeof(int),
                    "long" => typeof(long),
                    "uint" => typeof(uint),
                    "ulong" => typeof(ulong),
                    "short" => typeof(short),
                    "ushort" => typeof(ushort),
                    "byte" => typeof(byte),
                    "sbyte" => typeof(sbyte),
                    "float" => typeof(float),
                    "double" => typeof(double),
                    "bool" => typeof(bool),
                    "string" => typeof(string),
                    "char" => typeof(char),
                    _ => null,
                };
            if (asStatic is not null)
            {
                receiverType = asStatic;
                searchStatic = true;
            }
        }

        if (receiverType is null)
        {
            if (call.Target.Type is ExternalType ext)
                receiverType = ResolveExternalToRuntime(ext);
            else if (call.Target.Type is PrimitiveType prim)
                receiverType = ResolveExternalRuntimeTypeByName(prim.Name);
        }

        // User `data` / `class` / `choice` / `enum` receivers have no runtime
        // Type, but every one inherits System.Object — so an inherited member
        // (GetType, ToString, Equals, GetHashCode) resolves against Object, giving
        // the call a real static type (e.g. `Type`) so chains like
        // `w.GetType().Name` bind. Own E# methods are resolved before this is reached.
        if (receiverType is null && !searchStatic
            && call.Target.Type is DataType or ChoiceType or EnumType or InterfaceType)
        {
            var objMethod = typeof(object).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == call.MemberName && ParameterCountMatches(m, args.Count));
            if (objMethod is not null)
            {
                ReportExternalMethodUse(objMethod, useSpan);
                return objMethod.ReturnType == typeof(void) ? new VoidType() : MapRuntimeTypeToBoundType(objMethod.ReturnType);
            }
            return null;
        }

        if (receiverType is null) return null;

        // Find a method matching name + arg count. An interface's GetMethods omits members
        // inherited from base interfaces (e.g. DisposeAsync on IAsyncDisposable, inherited by
        // IAsyncEnumerator<T>), so walk those too — otherwise the call types as `object`.
        var flags = System.Reflection.BindingFlags.Public |
                    (searchStatic ? System.Reflection.BindingFlags.Static : System.Reflection.BindingFlags.Instance);
        var methods = MethodsIncludingBaseInterfaces(receiverType, flags)
            .Where(m => m.Name == call.MemberName && ParameterCountMatches(m, args.Count))
            .ToList();
        if (methods.Count == 0)
        {
            // Extension methods: scan Enumerable etc. for a static method whose first param is the receiver.
            if (!searchStatic)
            {
                var ext2 = FindExtensionMethod(receiverType, call.MemberName, args.Count);
                if (ext2 is not null)
                {
                    ReportExternalMethodUse(ext2, useSpan);
                    if (!ext2.IsGenericMethodDefinition)
                        return ext2.ReturnType == typeof(void) ? new VoidType() : MapRuntimeTypeToBoundType(ext2.ReturnType);
                    // Generic extension whose return type is INDEPENDENT of its type
                    // parameters — `AddSingleton<TService>(IServiceCollection) ->
                    // IServiceCollection`. The return is fixed regardless of `TService`,
                    // so report it directly: closing the method would need a runtime type
                    // for `TService`, which a module-only arg (`AddSingleton<Foo>()`,
                    // `Foo` a user type) can't provide — and failing to close would erase
                    // the whole call to `object`, breaking the fluent chain's next link.
                    if (!ext2.ReturnType.ContainsGenericParameters)
                        return ext2.ReturnType == typeof(void) ? new VoidType() : MapRuntimeTypeToBoundType(ext2.ReturnType);
                    // Otherwise the return DOES name a type parameter (OfType<T>/Cast<T>/
                    // Select<…> → IEnumerable<TResult>): close it from the explicit call-site
                    // type args or infer from receiver + args. Returning the OPEN return type
                    // would leak `IEnumerable<TResult>` and the IL emitter would resolve
                    // `TResult` to object.
                    // Symbolic close from the receiver's BOUND args: when the receiver is a
                    // generic instance whose args can't close to runtime types (the enclosing
                    // type's own generic parameter), `receiverType` is erased, so
                    // TryCloseExtensionMethod would close the method's param to `object`
                    // (`ToBlockingEnumerable<object>` → `IEnumerable<object>`). Instead unify
                    // the extension's `this` parameter against the receiver's bound type to map
                    // the method type parameters, then substitute the return — keeping `T`.
                    if (call.Target.Type is ExternalType extRecv && extRecv.TypeArgs.Count > 0
                        && extRecv.TypeArgs.Any(a => ResolveBoundTypeToRuntime(a) is null))
                    {
                        var methodSlots = new BoundType?[ext2.GetGenericArguments().Length];
                        var thisParam = ext2.GetParameters() is { Length: > 0 } ps ? ps[0].ParameterType : null;
                        if (thisParam is not null)
                            UnifyExtensionReceiverBound(thisParam, extRecv, methodSlots);
                        if (methodSlots.Length > 0 && methodSlots.All(s => s is not null))
                            return MapRuntimeWithSubstitution(ext2.ReturnType, methodSlots!);
                    }

                    var closedExt = TryCloseExtensionMethod(ext2, receiverType, args, explicitTypeArgs);
                    if (closedExt is not null && !closedExt.ReturnType.ContainsGenericParameters)
                        return closedExt.ReturnType == typeof(void) ? new VoidType() : MapRuntimeTypeToBoundType(closedExt.ReturnType);
                }
            }
            return null;
        }

        // Prefer the overload whose parameters most precisely match the bound
        // arguments after generic inference.  This matters for BCL overload sets
        // such as Task.Run(Func<Task>) vs Task.Run<TResult>(Func<Task<TResult>>):
        // a Func<Task<int>> is covariantly assignable to the former, but it is an
        // exact match for the latter and its Task<int> result is semantically required.
        var candidates = methods
            .Select(original => (Original: original, Closed: original.IsGenericMethodDefinition
                ? TryCloseGenericMethodFromArguments(original, args, explicitTypeArgs)
                : original))
            .Where(c => c.Closed is not null)
            .Select(c => (Method: c.Closed!, Score: ScoreMethodParameterMatch(c.Closed!, args),
                Specificity: MethodPatternSpecificity(c.Original)))
            .Where(c => c.Score >= 0)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Specificity)
            .ToList();
        var best = candidates.Count > 0 ? candidates[0].Method : methods[0];
        ReportExternalMethodUse(best, useSpan);
        if (best.ReturnType == typeof(void)) return new VoidType();

        // If there are explicit method-level type args, try to close the generic method
        // and report the specialized return type.
        if (best.IsGenericMethodDefinition && explicitTypeArgs is { Count: > 0 })
        {
            if (explicitTypeArgs.Count == best.GetGenericArguments().Length
                && explicitTypeArgs.Any(t => ResolveBoundTypeToRuntime(t) is null))
            {
                var boundArgs = new Dictionary<string, BoundType>(StringComparer.Ordinal);
                var genericParams = best.GetGenericArguments();
                for (var i = 0; i < genericParams.Length; i++)
                    boundArgs[genericParams[i].Name] = explicitTypeArgs[i];
                return MapRuntimeTypeWithBoundArgs(best.ReturnType, boundArgs);
            }
            try
            {
                var runtimeTypeArgs = explicitTypeArgs
                    .Select(ta => ResolveBoundTypeToRuntime(ta) ?? typeof(object))
                    .ToArray();
                var closed = best.MakeGenericMethod(runtimeTypeArgs);
                if (closed.ReturnType == typeof(void)) return new VoidType();
                if (!closed.ReturnType.ContainsGenericParameters)
                    return MapRuntimeTypeToBoundType(closed.ReturnType);
            }
            catch { /* fall through */ }
        }

        // Generic method definition without explicit args — infer from the runtime
        // types of the actual arguments. Without this, `await Task.FromResult(t.s)`
        // would have its outer call typed `var`, then the `await` would have type
        // `var`, then the let-binding's name would have type `var`, then any member
        // access on it (`s.Length`) loses the BCL property lookup and the IL emitter
        // silently drops the access — producing IL that loads `s` (string) into an
        // int result local. The async test surface depends on this inference.
        if (best.IsGenericMethodDefinition)
        {
            var typeParams = best.GetGenericArguments();
            var paramInfos = best.GetParameters();
            var inferred = new Type[typeParams.Length];
            var sawAll = true;
            for (var i = 0; i < paramInfos.Length && i < args.Count; i++)
            {
                var pt = paramInfos[i].ParameterType;
                if (pt.IsGenericParameter)
                {
                    var pos = pt.GenericParameterPosition;
                    if (pos < inferred.Length)
                    {
                        var argRuntime = ResolveBoundTypeToRuntime(args[i].Type);
                        if (argRuntime is not null) inferred[pos] = argRuntime;
                    }
                }
            }
            for (var i = 0; i < inferred.Length; i++)
                if (inferred[i] is null) { sawAll = false; break; }
            if (!sawAll) return null;
            try
            {
                var closed = best.MakeGenericMethod(inferred);
                if (closed.ReturnType == typeof(void)) return new VoidType();
                if (!closed.ReturnType.ContainsGenericParameters)
                    return MapRuntimeTypeToBoundType(closed.ReturnType);
            }
            catch { /* fall through */ }
            return null;
        }

        // If return type is a type-parameter on the receiver type (e.g. `List<T>.get_Item` → T),
        // substitute from the closed type args.
        if (best.ReturnType.IsGenericParameter)
        {
            var arg = receiverType.GetGenericArguments();
            if (best.ReturnType.GenericParameterPosition < arg.Length)
                return MapRuntimeTypeToBoundType(arg[best.ReturnType.GenericParameterPosition]);
        }

        // Symbolic return type. When the receiver is a generic instance whose args can't
        // close to runtime types (the enclosing type's own generic parameter, or a user
        // `data`), `best` was resolved on the ERASED receiver (`ChannelReader<object>`), so
        // its return type lost the parameter (`IAsyncEnumerable<object>` not `<T>`). Re-find
        // the method on the OPEN receiver definition and substitute the return type's
        // parameters BY POSITION with the receiver's bound args — keeping `T` symbolic so
        // the next link in a fluent chain resolves against `<T>`, not `<object>`.
        if (!searchStatic && call.Target.Type is ExternalType recvExt && recvExt.TypeArgs.Count > 0
            && recvExt.TypeArgs.Any(a => ResolveBoundTypeToRuntime(a) is null)
            && FindOpenGenericByName(recvExt.Name, recvExt.TypeArgs.Count) is { } openRecv)
        {
            var openMethod = MethodsIncludingBaseInterfaces(openRecv, flags)
                .FirstOrDefault(m => m.Name == call.MemberName && ParameterCountMatches(m, args.Count));
            if (openMethod is not null && openMethod.ReturnType != typeof(void))
                return MapRuntimeWithSubstitution(openMethod.ReturnType, recvExt.TypeArgs);
        }

        // If the return type is a constructed generic that contains unbound parameters
        // (e.g. `Task<T>`), we can't emit it — fall back.
        if (best.ReturnType.ContainsGenericParameters) return null;

        return MapRuntimeTypeToBoundType(best.ReturnType);
    }

    internal (IReadOnlyList<BoundType> ParameterTypes, BoundType ReturnType)? TryGetExpectedDelegateShape(BoundType? expectedType)
    {
        if (expectedType is null)
            return null;

        // A same-compilation `delegate func` has no runtime Type yet — read its
        // Invoke shape straight off the declaration so lambda / method-group
        // conversion can target it before it is emitted.
        if (expectedType is NamedDelegateType nd)
        {
            var ndParams = nd.Decl.Parameters.Select(p => ResolveType(p.Type)).ToList();
            return (ndParams, ResolveType(nd.Decl.ReturnType));
        }

        // A constructed `Func`/`Action`/`Predicate` is read structurally from its
        // name — strictly better than the runtime+Invoke path, which erases any
        // user `data` / `*T` arg to `object` (`Func<*Box, int>` → `Func<object, int>`).
        // Structural resolution keeps each arg's real (auto-deref) type so the lambda
        // param binds `*Box`, not `object`. Other delegate kinds (named BCL delegates,
        // same-compilation `delegate func`) still take the runtime path below.
        if (expectedType is ExternalType ge && TryStructuralDelegateShape(ge) is { } structural)
            return structural;

        Type? runtimeType = expectedType switch
        {
            ExternalType ext => ResolveExternalToRuntime(ext),
            PrimitiveType prim => ResolveExternalRuntimeTypeByName(prim.Name),
            _ => null,
        };

        if (runtimeType is null || !typeof(Delegate).IsAssignableFrom(runtimeType))
            return null;

        var invoke = runtimeType.GetMethod("Invoke", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (invoke is null)
            return null;

        var parameterTypes = invoke.GetParameters()
            .Select(p => MapRuntimeTypeToBoundType(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType))
            .ToList();
        return (parameterTypes, MapRuntimeTypeToBoundType(invoke.ReturnType));
    }


    /// Structural delegate shape for a constructed BCL delegate whose type args
    /// can't round-trip to a runtime Type (a user `data` / `*T` arg): `Func<A…,R>` →
    /// (params A…, return R); `Action<A…>` → (params A…, void); `Predicate<A>` → (A, bool).
    /// The args are already structured on the ExternalType — a `*T` arg is the wrapper
    /// form, so the lambda param auto-derefs. Null for non-delegate / non-generic types.
    static (IReadOnlyList<BoundType> ParameterTypes, BoundType ReturnType)? TryStructuralDelegateShape(ExternalType type)
    {
        if (type.Name is not ("Func" or "Action" or "Predicate")) return null;
        var argTypes = type.TypeArgs;
        if (argTypes.Count == 0) return null;
        return type.Name switch
        {
            "Func" => (argTypes.Take(argTypes.Count - 1).ToList(), argTypes[^1]),
            "Action" => (argTypes, (BoundType)new VoidType()),
            "Predicate" => (argTypes, new PrimitiveType("bool")),
            _ => null,
        };
    }

    /// Reconstruct a lambda's runtime delegate `Type` (`Func<…>` / `Action<…>`) from its
    /// bound parameter and return types — the lambda node itself is typed `var`. Returns
    /// null if any slot can't resolve to a runtime type (the caller then can't infer).
    Type? LambdaRuntimeDelegateType(BoundFunctionLiteralExpression fl)
    {
        var slots = new List<Type>(fl.Parameters.Count + 1);
        foreach (var p in fl.Parameters)
        {
            if (ResolveBoundTypeToRuntime(p.Type) is not { } pt) return null;
            slots.Add(pt);
        }
        var isVoid = fl.ReturnType is VoidType;
        if (!isVoid)
        {
            if (ResolveBoundTypeToRuntime(fl.ReturnType) is not { } rt) return null;
            slots.Add(rt);
        }
        var open = (isVoid, slots.Count) switch
        {
            (true, 0) => typeof(Action),
            (true, 1) => typeof(Action<>),
            (true, 2) => typeof(Action<,>),
            (true, 3) => typeof(Action<,,>),
            (false, 1) => typeof(Func<>),
            (false, 2) => typeof(Func<,>),
            (false, 3) => typeof(Func<,,>),
            (false, 4) => typeof(Func<,,,>),
            _ => null,
        };
        if (open is null) return null;
        if (!open.IsGenericTypeDefinition) return open; // non-generic Action
        try { return open.MakeGenericType(slots.ToArray()); } catch { return null; }
    }
}
