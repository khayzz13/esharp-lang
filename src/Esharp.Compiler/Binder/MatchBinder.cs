using Esharp.Diagnostics;
using Esharp.Symbols;
using Esharp.Syntax;

// Moved to Esharp.Binder (Pillar 2 — B2) per module-map.md.
// Match BINDING only — match LOWERING (the tag-compare/isinst/if-chain expansion)
// is Pillar 3's MatchLowering pass. This file produces BoundMatchStatement /
// BoundMatchExpression (FEATURE nodes) which are exactly what MatchLowering consumes.
// [Δ] Exhaustiveness verdict (MatchIsExhaustive) is exposed via a shared interface
//     so FlowAnalysis.MatchExhaustiveness can consume it without re-deriving.
namespace Esharp.Binder;

/// Match binding: statement and expression `match`, arm pattern/payload binding
/// (case views), exhaustiveness, and the exhaustiveness-aware definite-return
/// analysis function binding runs over non-void bodies.
internal sealed class MatchBinder : BinderUnit
{
    internal MatchBinder(Binder binder) : base(binder) { }


    internal BoundMatchStatement BindMatch(MatchStatementSyntax syntax)
    {
        var subject = Expressions.BindExpression(syntax.Subject);

        // Resolve subject type: explicit annotation takes priority, otherwise use bound type
        BoundType subjectType;
        if (syntax.SubjectType is not null)
            subjectType = ResolveType(syntax.SubjectType);
        else
            subjectType = subject.Type;

        var arms = new List<BoundMatchArm>();
        foreach (var arm in syntax.Arms)
        {
            var prevScope = Scope;
            Scope = Scope.Child();
            var savedViews = Ctx.PayloadViews;
            Ctx.PayloadViews = new(savedViews, StringComparer.Ordinal);

            // Pattern binding declares the arm's locals into this scope; the guard and
            // body see them (a type-pattern binding, a case payload, a case view).
            var pattern = BindPattern(subjectType, arm.Pattern, syntax.Span);
            var guard = arm.Guard is not null ? Expressions.BindExpression(arm.Guard) : null;
            var body = BindArmBody(arm);
            arms.Add(new BoundMatchArm(pattern, body, guard));

            Ctx.PayloadViews = savedViews;
            Scope = prevScope;
        }

        // Exhaustive match checking
        var hasDefault = arms.Any(a => a.Pattern.IsDefault);
        if (!hasDefault && subjectType is ChoiceType matchCt)
        {
            var covered = arms.Where(a => !a.Pattern.IsDefault && a.Pattern.CaseName is not null)
                .Select(a => a.Pattern.CaseName!).ToHashSet();
            var all = matchCt.Decl.Cases.Select(c => c.Name).ToHashSet();
            var missing = all.Except(covered).ToList();
            if (missing.Count > 0)
                Diagnostics.Warn(syntax.Span,
                    $"Non-exhaustive match on '{matchCt.Name}': missing cases {string.Join(", ", missing)}. Add a 'default' arm or handle all cases.");
        }
        if (!hasDefault && subjectType is EnumType matchEt)
        {
            var covered = arms.Where(a => !a.Pattern.IsDefault && a.Pattern.CaseName is not null)
                .Select(a => a.Pattern.CaseName!).ToHashSet();
            var all = matchEt.Decl.Cases.Select(c => c.Name).ToHashSet();
            var missing = all.Except(covered).ToList();
            if (missing.Count > 0)
                Diagnostics.Warn(syntax.Span,
                    $"Non-exhaustive match on '{matchEt.Name}': missing cases {string.Join(", ", missing)}.");
        }

        CheckTypePatternExhaustiveness(subjectType,
            arms.Select(a => (a.Pattern, a.Guard is not null)).ToList(), syntax.Span);

        return new BoundMatchStatement(subject, subjectType, arms);
    }

    internal BoundMatchExpression BindMatchExpression(MatchExpressionSyntax syntax)
    {
        var subject = Expressions.BindExpression(syntax.Subject);
        BoundType subjectType = syntax.SubjectType is not null ? ResolveType(syntax.SubjectType) : subject.Type;

        var arms = new List<BoundMatchExpressionArm>();
        foreach (var arm in syntax.Arms)
        {
            var prevScope = Scope;
            Scope = Scope.Child();
            var savedViews = Ctx.PayloadViews;
            Ctx.PayloadViews = new(savedViews, StringComparer.Ordinal);

            var pattern = BindPattern(subjectType, arm.Pattern, syntax.Span);
            var guard = arm.Guard is not null ? Expressions.BindExpression(arm.Guard) : null;

            // The arm's value: a `=> Expr` body directly, else the block's trailing
            // expression.
            BoundExpression armValue;
            if (arm.ExprBody is not null)
                armValue = Expressions.BindExpression(arm.ExprBody);
            else
            {
                var body = Statements.BindBlock(arm.Body!);
                armValue = body.Statements.Count > 0 && body.Statements[^1] is BoundExpressionStatement exprStmt
                    ? exprStmt.Expression
                    : new BoundLiteralExpression(null, "null", new NullType());
            }

            arms.Add(new BoundMatchExpressionArm(pattern, armValue, guard));

            Ctx.PayloadViews = savedViews;
            Scope = prevScope;
        }

        CheckTypePatternExhaustiveness(subjectType,
            arms.Select(a => (a.Pattern, a.Guard is not null)).ToList(), syntax.Span);

        return new BoundMatchExpression(subject, subjectType, arms);
    }

    // A type-pattern `match` over a closed `abstract class` hierarchy is checked
    // against the base's in-assembly leaves — adding a leaf re-triggers the warning on
    // every match that predates it. An `open` ancestor anywhere makes the set open (no
    // check); a `default` arm (unguarded) covers the remainder. A guarded arm does not
    // count toward coverage — its guard may be false.
    void CheckTypePatternExhaustiveness(BoundType subjectType,
        IReadOnlyList<(BoundMatchPattern Pattern, bool Guarded)> arms, SourceSpan span)
    {
        if (arms.Any(a => a.Pattern.IsDefault && !a.Guarded)) return;
        if (!arms.Any(a => a.Pattern.TypeBindingName is not null)) return;
        if (subjectType is not DataType dt || SymbolOf(dt) is not { } baseSym) return;
        if ((baseSym.Decl as DataDeclarationSyntax)?.Modifier != ClassModifier.Abstract) return;

        var leaves = ClosedLeaves(baseSym);
        if (leaves is null) return;   // an `open` member → the hierarchy is open-world

        var covered = arms.Where(a => !a.Guarded && a.Pattern.NarrowedType is { } t && SymOf(t) is not null)
            .Select(a => SymOf(a.Pattern.NarrowedType!)!).ToHashSet();
        var missing = leaves.Where(l => !IsCovered(l, covered)).ToList();
        if (missing.Count > 0)
            Diagnostics.Warn(span,
                $"Non-exhaustive match on '{baseSym.Name}': missing {string.Join(", ", missing.Select(m => m.Name))}. Add the missing arm(s) or a 'default'.");
    }

    // The closed set of concrete (instantiable) leaves under an abstract base, or null if
    // the hierarchy is open (any `open` member — instantiable AND inheritable — opens the
    // world). Abstract intermediates recurse but are not themselves leaves.
    static List<TypeSymbol>? ClosedLeaves(TypeSymbol root)
    {
        var leaves = new List<TypeSymbol>();
        var open = false;
        void Walk(TypeSymbol t)
        {
            if (open) return;
            var mod = (t.Decl as DataDeclarationSyntax)?.Modifier;
            if (mod == ClassModifier.Open) { open = true; return; }
            if (mod != ClassModifier.Abstract) leaves.Add(t);   // sealed default → concrete leaf
            foreach (var d in t.DerivedTypes) Walk(d);
        }
        Walk(root);
        return open ? null : leaves;
    }

    // A leaf is covered when an arm's pattern type is the leaf or any of its ancestors.
    static bool IsCovered(TypeSymbol leaf, HashSet<TypeSymbol> covered)
    {
        for (var t = leaf; t is not null; t = t.BaseType)
            if (covered.Contains(t)) return true;
        return false;
    }

    TypeSymbol? SymOf(BoundType t) =>
        t is DataType d ? SymbolOf(d) : null;

    // A statement-match arm's body: a `{ ... }` block, or a `=> Expr` expression wrapped
    // as a single expression statement.
    BoundBlockStatement BindArmBody(MatchArmSyntax arm)
    {
        if (arm.ExprBody is not null)
            return new BoundBlockStatement([new BoundExpressionStatement(Expressions.BindExpression(arm.ExprBody))]);
        return Statements.BindBlock(arm.Body!);
    }

    // Bind one arm's pattern, declaring its bindings into the current (arm) scope.
    // Dispatches over the pattern kinds: default, `nil`, type pattern `(name: T)`,
    // literal, and `.case`.
    BoundMatchPattern BindPattern(BoundType subjectType, MatchPatternSyntax pat, SourceSpan matchSpan)
    {
        if (pat.IsDefault)
            return new BoundMatchPattern(null, null, IsDefault: true);

        if (pat.IsNil)
        {
            if (!CanBeNil(subjectType))
                Diagnostics.Warn(matchSpan,
                    $"ES2174: 'nil' arm over non-nullable '{TypeResolver.TypeDisplayName(subjectType)}' — this case can never match.");
            return new BoundMatchPattern(null, null, IsDefault: false, IsNil: true);
        }

        if (pat is { TypeBindingName: { } bindName, TypeBindingType: { } bindType })
        {
            // A type pattern dispatches an OPEN scrutinee (interface / object / ref base);
            // a closed `choice`/`enum` is matched by `.case`, not by type.
            if (subjectType is ChoiceType or EnumType)
                Diagnostics.Report(matchSpan,
                    $"ES2172: a type pattern '({bindName}: T)' can't match over '{TypeResolver.TypeDisplayName(subjectType)}' — match its cases with '.case'.");
            var narrowed = ResolveType(bindType);
            DeclareLocal(bindName, narrowed, mutable: false, pat.NameSpan, isParameter: false);
            return new BoundMatchPattern(null, null, IsDefault: false, TypeBindingName: bindName, NarrowedType: narrowed);
        }

        if (pat.LiteralValue is not null)
        {
            var lit = Expressions.BindExpression(pat.LiteralValue);
            return new BoundMatchPattern(null, null, IsDefault: false, LiteralValue: lit);
        }

        // `.case` — the existing choice / enum / Result path.
        // Tooling tap: the pattern's case name is a use of the owning type's
        // interned CaseSymbol (same instance its declaration reported).
        if (pat.CaseName is { } patCase)
        {
            var ownerSym = subjectType switch
            {
                ChoiceType ch => ch.Symbol,
                EnumType en => en.Symbol,
                _ => null,
            };
            if (ownerSym?.Cases.FirstOrDefault(c => c.Name == patCase) is { } patCaseSym)
                Data.Sink.OnCaseResolved(patCaseSym, pat.NameSpan, Semantics.SymbolOccurrence.Use);
        }
        var bindings = BindMatchArmBindings(subjectType, pat);
        return new BoundMatchPattern(pat.CaseName, bindings, IsDefault: false);
    }

    // Whether `nil` is a possible value of the scrutinee — a nullable, a reference
    // type (`class`, `ref choice`, interface, BCL class), or `string`.
    bool CanBeNil(BoundType t) => t switch
    {
        NullableType => true,
        NullType => true,
        ChoiceType c => c.IsRef,
        DataType d => ClassifyData(d.Name) == DataClassification.Class,
        ExternalType => true,
        ExternalCSharpType => true,
        InterfaceType => true,
        PrimitiveType p => p.Name == "string",
        _ => false,
    };

    // === Definite-return analysis ===

    /// A non-void concrete function must leave via `return` / `throw` (or diverge
    /// in an infinite loop) on every path; reaching the end of the body is a hard
    /// error (ES2140). The analysis is exhaustive-match-aware — a `match` whose
    /// arms all terminate counts as terminating when it covers every variant
    /// (`choice`/`enum`), both `bool` values, both `Result` cases, or has a
    /// `default` arm.
    internal void CheckDefiniteReturn(BoundBlockStatement body, FunctionDeclarationSyntax syntax)
    {
        if (DefinitelyReturns(body)) return;
        var span = syntax.Span;
        Diagnostics.Report(span.File, span.IsValid ? span.Line : 0, span.IsValid ? span.Column : 0,
            $"ES2140: function '{syntax.Name}' does not return on every path — a non-void function " +
            "must end every path in 'return', 'throw', or an infinite loop. Add a return, or make the " +
            "trailing 'match'/'if' exhaustive with every branch returning.");
    }

    /// Whether a statement always leaves via return / throw / divergence — the flow
    /// fact the smart-cast engine's guard-return persistence rides (StatementBinder.BindIf).
    internal bool BranchAlwaysExits(BoundStatement stmt) => DefinitelyReturns(stmt);

    bool DefinitelyReturns(BoundStatement stmt) => stmt switch
    {
        BoundReturnStatement => true,
        BoundThrowStatement => true,
        // A block terminates if any of its statements terminates (everything after
        // a terminating statement is unreachable).
        BoundBlockStatement b => b.Statements.Any(DefinitelyReturns),
        // `if` terminates only with an `else` where both arms terminate.
        BoundIfStatement i => i.Else is not null && DefinitelyReturns(i.Then) && DefinitelyReturns(i.Else),
        // Exhaustive `match` where every arm body terminates.
        BoundMatchStatement m => MatchIsExhaustive(m) && m.Arms.All(a => DefinitelyReturns(a.Body)),
        // `while true { … }` with no `break` diverges.
        BoundWhileStatement w => IsInfiniteLoop(w),
        // `try` terminates when the body and every catch terminate.
        BoundTryStatement t => DefinitelyReturns(t.Body) && t.Catches.All(c => DefinitelyReturns(c.Body)),
        _ => false,
    };

    bool MatchIsExhaustive(BoundMatchStatement m)
    {
        // An unguarded `default` covers the remainder; a guarded arm never guarantees
        // coverage (its guard may be false), so it is excluded throughout.
        if (m.Arms.Any(a => a.Pattern.IsDefault && a.Guard is null)) return true;
        switch (m.SubjectType)
        {
            case ChoiceType ct:
            {
                var covered = Covered(m);
                return ct.Decl.Cases.All(c => covered.Contains(c.Name));
            }
            case EnumType et:
            {
                var covered = Covered(m);
                return et.Decl.Cases.All(c => covered.Contains(c.Name));
            }
            case ResultType:
            {
                var covered = Covered(m);
                return covered.Contains("ok") && covered.Contains("err");
            }
            case PrimitiveType { Name: "bool" }:
            {
                var hasTrue = m.Arms.Any(a => a.Guard is null && a.Pattern.LiteralValue is BoundLiteralExpression { Value: true });
                var hasFalse = m.Arms.Any(a => a.Guard is null && a.Pattern.LiteralValue is BoundLiteralExpression { Value: false });
                return hasTrue && hasFalse;
            }
            // A type-pattern match over a closed `abstract class` hierarchy is
            // exhaustive when its in-assembly leaves are all covered — the closed-world
            // dispatch the spec promises needs no `default`.
            case DataType dt when SymbolOf(dt) is { } baseSym
                && (baseSym.Decl as DataDeclarationSyntax)?.Modifier == ClassModifier.Abstract:
            {
                var leaves = ClosedLeaves(baseSym);
                if (leaves is null) return false;
                var covered = m.Arms.Where(a => a.Guard is null && a.Pattern.NarrowedType is { } t && SymOf(t) is not null)
                    .Select(a => SymOf(a.Pattern.NarrowedType!)!).ToHashSet();
                return leaves.All(l => IsCovered(l, covered));
            }
            default:
                // int / string literal universes are open — `default` is required.
                return false;
        }
    }

    // The case names an unguarded `.case` arm covers — guarded arms excluded.
    static HashSet<string> Covered(BoundMatchStatement m) =>
        m.Arms.Where(a => a.Guard is null && a.Pattern.CaseName is not null)
            .Select(a => a.Pattern.CaseName!).ToHashSet();

    bool IsInfiniteLoop(BoundWhileStatement w)
        => w.Condition is BoundLiteralExpression { Value: true } && !ContainsBreak(w.Body);

    // Whether a `break` targeting *this* loop appears — does not descend into
    // nested loops (their breaks bind to themselves).
    bool ContainsBreak(BoundStatement stmt) => stmt switch
    {
        BoundBreakStatement => true,
        BoundBlockStatement b => b.Statements.Any(ContainsBreak),
        BoundIfStatement i => ContainsBreak(i.Then) || (i.Else is not null && ContainsBreak(i.Else)),
        BoundMatchStatement m => m.Arms.Any(a => ContainsBreak(a.Body)),
        BoundTryStatement t => ContainsBreak(t.Body) || t.Catches.Any(c => ContainsBreak(c.Body)),
        BoundLetGuard g => ContainsBreak(g.ElseBody),
        _ => false,
    };

    /// Bind the payload bindings of a single match arm against the subject type,
    /// declaring each into the current (arm) scope. Shared by `BindMatch` and
    /// `BindMatchExpression`.
    ///
    /// Value-choice arms support two forms:
    ///   - Positional: N names against N payloads — `.add(a, b)` binds each value.
    ///   - Case view:  a single name — `.pair(p)` / `.accepted(a)` — projects
    ///     payloads by field name via `Ctx.PayloadViews` (`p.a`, `a.id`). For a
    ///     single-payload case the view is transparent: the name also holds the
    ///     value directly, so `.connected(sid)` (bare) keeps working.
    /// `ref choice` single bindings name the variant subclass instance; `Result`
    /// `.ok`/`.err` bind Value / Error.
    // Resolve a choice case's payload type, substituting the closed instance's
    // type arguments for the choice's type parameters (`some(value: T)` on
    // `Option<int>` → `int`). Open / non-generic choices pass through unchanged.
    BoundType ResolveChoicePayloadType(Syntax.TypeSyntax payloadType, ChoiceType ct)
    {
        var resolved = ResolveType(payloadType);
        if (ct.TypeArgs.Count == 0 || ct.TypeParameters.Count != ct.TypeArgs.Count)
            return resolved;
        var map = new Dictionary<string, BoundType>(StringComparer.Ordinal);
        for (var i = 0; i < ct.TypeParameters.Count; i++)
            map[ct.TypeParameters[i]] = ct.TypeArgs[i];
        return SubstituteTypeParameters(resolved, map);
    }

    static BoundType SubstituteTypeParameters(BoundType t, Dictionary<string, BoundType> map) => t switch
    {
        PrimitiveType prim when map.TryGetValue(prim.Name, out var a) => a,
        ExternalType { TypeArgs.Count: 0 } ext when map.TryGetValue(ext.Name, out var a) => a,
        ExternalType ext when ext.TypeArgs.Count > 0
            => ext with { TypeArguments = ext.TypeArgs.Select(x => SubstituteTypeParameters(x, map)).ToList() },
        DataType d when d.TypeArgs.Count > 0
            => d with { TypeArguments = d.TypeArgs.Select(x => SubstituteTypeParameters(x, map)).ToList() },
        HeapPointerBoundType hp => new HeapPointerBoundType(SubstituteTypeParameters(hp.Inner, map)),
        NullableType n => new NullableType(SubstituteTypeParameters(n.Inner, map)),
        _ => t,
    };

    /// The identifier span of the i-th case-payload binding, when the parser
    /// stamped one — the occurrence span the binding's LocalSymbol declares at.
    static SourceSpan BindingSpanAt(MatchPatternSyntax pattern, int i) =>
        pattern.BindingNameSpans is { } spans && i < spans.Count ? spans[i] : default;

    List<BoundMatchBinding>? BindMatchArmBindings(BoundType subjectType, MatchPatternSyntax pattern)
    {
        if (pattern.IsDefault || pattern.BindingNames is not { Count: > 0 } names)
            return null;

        if (subjectType is ChoiceType ct)
        {
            var caseDecl = ct.Decl.Cases.FirstOrDefault(c => c.Name == pattern.CaseName);
            if (caseDecl is null) return null;
            var bindings = new List<BoundMatchBinding>();

            if (ct.IsRef && names.Count == 1 && caseDecl.Payloads.Count == 1)
            {
                // ref choice, single-payload case: TRANSPARENT case view. Bind the name to
                // the payload VALUE (the emitter casts to the variant subclass and loads
                // the payload field), and alias `name.payload` to the same local — so both
                // `b` and `b.value` resolve, exactly as the value-choice single-payload
                // case view does. Without this, `.jbool(b) { b ? … }` over a `ref choice`
                // failed (b was the subclass instance, not the bool).
                var p = caseDecl.Payloads[0];
                var pType = ResolveChoicePayloadType(p.Type, ct);
                DeclareLocal(names[0], pType, mutable: true, BindingSpanAt(pattern, 0), isParameter: false);
                bindings.Add(new BoundMatchBinding(names[0], pType, p.Name, Scope.LookupLocal(names[0])));
                Ctx.PayloadViews[names[0]] = new Dictionary<string, (string, BoundType)>(StringComparer.Ordinal)
                {
                    [p.Name] = (names[0], pType),
                };
            }
            else if (ct.IsRef && names.Count == 1)
            {
                // ref choice, multi-payload case: a single binding is the variant subclass
                // instance (the case view) — `.add(n)` then `n.left` / `n.right`.
                var bt = new ExternalType($"{ct.Name}_{caseDecl.Name}");
                DeclareLocal(names[0], bt, mutable: true, BindingSpanAt(pattern, 0), isParameter: false);
                bindings.Add(new BoundMatchBinding(names[0], bt, "", Scope.LookupLocal(names[0])));
            }
            else if (ct.IsRef && names.Count > 1)
            {
                // ref choice: multiple bindings destructure positionally, each name
                // bound to its payload value — `.add(l, r)` then `eval(l)`/`eval(r)`.
                for (var bi = 0; bi < names.Count && bi < caseDecl.Payloads.Count; bi++)
                {
                    var pType = ResolveChoicePayloadType(caseDecl.Payloads[bi].Type, ct);
                    DeclareLocal(names[bi], pType, mutable: true, BindingSpanAt(pattern, bi), isParameter: false);
                    bindings.Add(new BoundMatchBinding(names[bi], pType, caseDecl.Payloads[bi].Name, Scope.LookupLocal(names[bi])));
                }
            }
            else if (!ct.IsRef && caseDecl.Payloads.Count > 0)
            {
                if (names.Count == 1)
                    RegisterPayloadView(names[0], caseDecl, bindings, ct, BindingSpanAt(pattern, 0));
                else
                    for (var bi = 0; bi < names.Count && bi < caseDecl.Payloads.Count; bi++)
                    {
                        var pType = ResolveChoicePayloadType(caseDecl.Payloads[bi].Type, ct);
                        DeclareLocal(names[bi], pType, mutable: true, BindingSpanAt(pattern, bi), isParameter: false);
                        bindings.Add(new BoundMatchBinding(names[bi], pType, caseDecl.Payloads[bi].Name, Scope.LookupLocal(names[bi])));
                    }
            }
            return bindings;
        }

        // Result<T,E>: .ok(v) and .err(e) are pseudo-cases bound to Value / Error
        // via IsOk dispatch. Binding name typing follows the Result type args.
        if (subjectType is ResultType rt && pattern.CaseName is { } caseName)
        {
            var (payloadType, payloadField) = caseName switch
            {
                "ok"  => (rt.OkType,    "Value"),
                "err" => (rt.ErrorType, "Error"),
                _     => ((BoundType?)null, ""),
            };
            if (payloadType is null) return null;
            DeclareLocal(names[0], payloadType, mutable: true, BindingSpanAt(pattern, 0), isParameter: false);
            return new List<BoundMatchBinding> { new(names[0], payloadType, payloadField) };
        }

        return null;
    }

    /// Register a single-binding case view. Each payload becomes a real arm
    /// binding (a positional load of `case_payload` off the subject) plus a
    /// `Ctx.PayloadViews` alias so `view.payloadName` resolves to it. Single-payload
    /// cases bind the value under the view name itself (transparent), so both
    /// `view` and `view.payloadName` work.
    void RegisterPayloadView(string viewName, Syntax.ChoiceCaseSyntax caseDecl, List<BoundMatchBinding> bindings, ChoiceType ct, SourceSpan viewNameSpan = default)
    {
        var fieldMap = new Dictionary<string, (string, BoundType)>(StringComparer.Ordinal);
        if (caseDecl.Payloads.Count == 1)
        {
            var p = caseDecl.Payloads[0];
            var pType = ResolveChoicePayloadType(p.Type, ct);
            DeclareLocal(viewName, pType, mutable: true, viewNameSpan, isParameter: false);
            bindings.Add(new BoundMatchBinding(viewName, pType, p.Name, Scope.LookupLocal(viewName)));
            fieldMap[p.Name] = (viewName, pType);
        }
        else
        {
            foreach (var p in caseDecl.Payloads)
            {
                var pType = ResolveChoicePayloadType(p.Type, ct);
                var local = $"{viewName}${p.Name}";
                Scope.Declare(local, pType);
                bindings.Add(new BoundMatchBinding(local, pType, p.Name, Scope.LookupLocal(local)));
                fieldMap[p.Name] = (local, pType);
            }
        }
        Ctx.PayloadViews[viewName] = fieldMap;
    }
}
