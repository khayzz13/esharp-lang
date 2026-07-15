using Esharp.BoundTree;
using Esharp.Symbols;

namespace Esharp.Lowering;

/// <summary>
/// Lowers every <see cref="BoundMatchStatement"/> into CORE if/branch/load sequences.
/// Expression-position matches are already converted to statement form by
/// <see cref="ExpressionFormLowering"/>, so this pass sees only statement matches.
///
/// Traversal is inherited from the generated total <see cref="BoundTreeRewriter"/>: the pass
/// overrides only <see cref="MatchRewriter.RewriteMatchStatement"/> and descends into every
/// other position (lambda bodies, object-creation field inits and constructor arguments,
/// init bodies) for free — a nested match anywhere is lowered, none survives.
///
/// Per match kind: value union → tag compare + payload field loads; ref union → isinst +
/// cast chain; enum → equality chain; literal → equality / string.op_Equality chain;
/// Result → IsOk branch binding .Value / .Error; type pattern / guard / nil arm → a linear
/// top-to-bottom test chain.
/// </summary>
public sealed class MatchLowering : IBoundTreePass
{
    public static readonly MatchLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
        => LoweringDriver.MapBodies(program, new MatchRewriter());
}

sealed class MatchRewriter : BoundTreeRewriter
{
    int _tempCounter;

    protected override BoundStatement RewriteMatchStatement(BoundMatchStatement node)
    {
        // Descend first: lower any nested FEATURE nodes (incl. nested matches) inside the
        // subject and the arm guards/bodies, THEN convert this match into a CORE if-chain.
        var descended = (BoundMatchStatement)base.RewriteMatchStatement(node);
        return LowerMatchStatement(descended);
    }

    // ─── Match statement lowering ──────────────────────────────────────────────

    // Every match kind — value/ref union, enum, literal, Result, type-pattern, nil,
    // guarded — lowers through ONE chained builder. Per-arm test + binding prelude is
    // supplied uniformly by BuildPatternTest (which dispatches on the subject type), and
    // BuildArmChain folds the arms into a nested if/else-if/else so exactly one arm runs,
    // the first match wins, and `default` (or arm exhaustion) is the terminal else.
    //
    // The subject is evaluated once into a temp; a value union's payload access, a ref
    // union's isinst, a Result's IsOk, an enum/literal equality all reduce to a boolean
    // `test` + optional binding statements against that temp.
    BoundStatement LowerMatchStatement(BoundMatchStatement m)
    {
        var subjTemp = FreshTemp(m.SubjectType);
        var subjDecl = new BoundVariableDeclaration(false, subjTemp, m.SubjectType, m.Subject);
        var subjRef = new BoundNameExpression(subjTemp, m.SubjectType);

        var chain = BuildArmChain(m.Arms, 0, subjRef, m.SubjectType);
        var body = chain is null
            ? new List<BoundStatement> { subjDecl }
            : new List<BoundStatement> { subjDecl, chain };
        return new BoundBlockStatement(body) { Span = m.Span };
    }

    // Fold arms[index..] into a nested if/else chain. A non-default arm becomes
    // `if (test) { <prelude> body } else { <rest> }`; a guarded arm nests the guard so a
    // failed guard falls through to the rest; `default` (or the exhausted tail) is the
    // terminal else with no test. First-match-wins and single-arm-execution both fall out
    // of the else-nesting — no arm can run once an earlier one matched.
    BoundStatement? BuildArmChain(
        IReadOnlyList<BoundMatchArm> arms, int index,
        BoundNameExpression subjRef, BoundType subjectType)
    {
        if (index >= arms.Count) return null;
        var arm = arms[index];

        // A default arm matches anything not yet handled — it is the terminal else.
        if (arm.Pattern.IsDefault)
            return arm.Body;

        // Each arm has its own binder scope, so `.case(v)` may legitimately bind
        // `v` to bool, int, and string in sibling arms. CLR locals are method-scoped;
        // alpha-rename those symbol-identical arm bindings before emission while
        // rewriting only references carrying the matching LocalSymbol identity.
        var renames = ArmBindingRenames(arm.Pattern);
        var pattern = RenamePatternBindings(arm.Pattern, renames);
        var rewrite = renames.Count == 0 ? null : new MatchBindingNameRewriter(renames);
        var armBody = rewrite is null ? arm.Body : (BoundBlockStatement)rewrite.RewriteStatement(arm.Body);
        var armGuard = arm.Guard is null || rewrite is null ? arm.Guard : rewrite.RewriteExpression(arm.Guard);

        var (test, prelude) = BuildPatternTest(pattern, subjRef, subjectType);
        var rest = BuildArmChain(arms, index + 1, subjRef, subjectType);

        if (armGuard is not null)
        {
            // The pattern matched but the guard may be false → fall to the rest. The
            // prelude (payload/narrow bindings) must be in scope for the guard, so it
            // precedes the guard test inside the pattern-matched branch. `rest` appears in
            // both the guard-false and pattern-false positions (a guard can only fail into
            // the same continuation) — correct, and only guarded arms pay the duplication.
            var restBlock = AsBlock(rest);
            var guardInner = new BoundIfStatement(armGuard, armBody, restBlock);
            var thenStmts = new List<BoundStatement>(prelude) { guardInner };
            return new BoundIfStatement(test, new BoundBlockStatement(thenStmts) { Span = arm.Body.Span }, restBlock);
        }

        var matched = new List<BoundStatement>(prelude);
        matched.AddRange(armBody.Statements);
        return new BoundIfStatement(test, new BoundBlockStatement(matched) { Span = armBody.Span }, rest);
    }

    static BoundBlockStatement AsBlock(BoundStatement? s) =>
        s is null ? new BoundBlockStatement([])
        : s as BoundBlockStatement ?? new BoundBlockStatement([s]) { Span = s.Span };

    Dictionary<string, string> ArmBindingRenames(BoundMatchPattern pattern)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pattern.TypeBindingName is { } typeBinding && typeBinding != "_")
            renames[typeBinding] = FreshBindingName(typeBinding);
        foreach (var binding in pattern.Bindings ?? [])
        {
            if (binding.Name != "_")
                renames[binding.Name] = FreshBindingName(binding.Name);
        }
        return renames;
    }

    static BoundMatchPattern RenamePatternBindings(BoundMatchPattern pattern,
                                                    IReadOnlyDictionary<string, string> renames)
    {
        if (renames.Count == 0) return pattern;
        return pattern with
        {
            TypeBindingName = pattern.TypeBindingName is { } typeBinding
                && renames.TryGetValue(typeBinding, out var renamedTypeBinding)
                ? renamedTypeBinding
                : pattern.TypeBindingName,
            Bindings = pattern.Bindings?.Select(binding =>
                renames.TryGetValue(binding.Name, out var name)
                    ? binding with { Name = name }
                    : binding).ToList(),
        };
    }

    sealed class MatchBindingNameRewriter(IReadOnlyDictionary<string, string> names)
        : BoundTreeRewriter
    {
        public override BoundExpression RewriteExpression(BoundExpression expr)
        {
            if (expr is BoundNameExpression nameOnly
                && names.TryGetValue(nameOnly.Name, out var fallback))
                return nameOnly with { Name = fallback };
            return base.RewriteExpression(expr);
        }
    }

    // ─── Pattern test builder (for linear chain) ──────────────────────────────

    (BoundExpression test, List<BoundStatement> prelude) BuildPatternTest(
        BoundMatchPattern pat, BoundNameExpression subjRef, BoundType subjectType)
    {
        if (pat.IsNil)
        {
            // `T?` has two physical forms: Nullable<T> for value T, and an
            // ordinary reference slot for reference T. Only the former has
            // HasValue. Synth keeps this aligned with `??` and `?.`.
            return (Synth.IsNil(subjRef), []);
        }

        if (pat.TypeBindingName is { } bindName && pat.NarrowedType is { } narrowed)
        {
            if (Synth.IsValueNullable(subjectType)
                && subjectType is NullableType nullableSubj && narrowed == nullableSubj.Inner)
            {
                var hasValueTest = new BoundMemberAccessExpression(
                    subjRef, "HasValue", new PrimitiveType("bool"));
                var valueAccess = new BoundMemberAccessExpression(subjRef, "Value", narrowed);
                var prelude = new List<BoundStatement>
                {
                    new BoundVariableDeclaration(false, bindName, narrowed, valueAccess),
                };
                return (hasValueTest, prelude);
            }

            // Type test over an open scrutinee (object / interface / base class), narrowing to
            // a concrete value OR reference type. The test is a real `is` (box-if-value-operand,
            // isinst T, null-compare → bool); the binding is a flow-proven Narrow — `unbox.any`
            // for a value target, `castclass` for a reference one. A value target must NOT go
            // through castclass (`castclass int32` is invalid IL), nor isinst `Nullable<T>` (a
            // boxed int is never a `Nullable<int>`) — the bugs the SafeCast/AssertCast pair shipped.
            var typeTest = new BoundTypeTestExpression(subjRef, narrowed, Negated: false);
            var narrowPrelude = new List<BoundStatement>
            {
                new BoundVariableDeclaration(false, bindName, narrowed,
                    BoundConversion.Narrow(subjRef, narrowed)),
            };
            return (typeTest, narrowPrelude);
        }

        if (pat.LiteralValue is not null)
        {
            BoundExpression test;
            if (pat.LiteralValue.Type is PrimitiveType { Name: "string" })
            {
                var strEqMethod = new BoundMemberAccessExpression(
                    new BoundNameExpression("string", new ExternalType("string")),
                    "op_Equality", new PrimitiveType("bool"));
                test = new BoundCallExpression(strEqMethod, [subjRef, pat.LiteralValue],
                    new PrimitiveType("bool"), Array.Empty<BoundType>());
            }
            else
            {
                test = new BoundBinaryExpression(subjRef, SyntaxTokenKind.EqualsEquals,
                    pat.LiteralValue, new PrimitiveType("bool"));
            }
            return (test, []);
        }

        return subjectType switch
        {
            ResultType rt => BuildResultCaseTest(pat, subjRef, rt),
            ChoiceType { IsRef: true } refCt => BuildRefChoiceCaseTest(pat, subjRef, refCt),
            ChoiceType vc => BuildValueChoiceCaseTest(pat, subjRef, vc),
            EnumType et => BuildEnumCaseTest(pat, subjRef, et),
            _ => (new BoundLiteralExpression(false, "false", new PrimitiveType("bool")), []),
        };
    }

    (BoundExpression, List<BoundStatement>) BuildResultCaseTest(
        BoundMatchPattern pat, BoundNameExpression subjRef, ResultType rt)
    {
        var isOk = pat.CaseName == "ok";
        var isOkAccess = new BoundMemberAccessExpression(subjRef, "IsOk", new PrimitiveType("bool"));
        BoundExpression test = isOk
            ? isOkAccess
            : new BoundUnaryExpression(SyntaxTokenKind.Bang, isOkAccess, new PrimitiveType("bool"));

        var prelude = new List<BoundStatement>();
        if (pat.Bindings is { Count: > 0 } && pat.Bindings[0] is { Name: var name } b && name != "_")
        {
            var member = isOk ? "Value" : "Error";
            var payloadType = isOk ? rt.OkType : rt.ErrorType;
            prelude.Add(new BoundVariableDeclaration(false, name, payloadType,
                new BoundMemberAccessExpression(subjRef, member, payloadType)));
        }

        return (test, prelude);
    }

    (BoundExpression, List<BoundStatement>) BuildRefChoiceCaseTest(
        BoundMatchPattern pat, BoundNameExpression subjRef, ChoiceType refCt)
    {
        // Ref-union variants reify the parent choice's generic arguments. Preserve
        // them on the variant itself (`Box_full<int>`), not only on the subject;
        // otherwise a payload field is emitted as open !0 after the case cast.
        var variantType = new ExternalType($"{refCt.Name}_{pat.CaseName}", refCt.TypeArgs);
        var isinst = BoundConversion.SafeCast(subjRef, new NullableType(variantType));
        var test = new BoundBinaryExpression(isinst, SyntaxTokenKind.BangEquals,
            new BoundLiteralExpression(null, "null", new ExternalType("object")), new PrimitiveType("bool"));

        var prelude = new List<BoundStatement>();
        if (pat.Bindings is { Count: > 0 } bindings)
        {
            var castExpr = BoundConversion.AssertCast(subjRef, variantType);
            var variantTemp = FreshTemp(variantType);
            prelude.Add(new BoundVariableDeclaration(false, variantTemp, variantType, castExpr));
            var variantRef = new BoundNameExpression(variantTemp, variantType);

            foreach (var binding in bindings)
            {
                BoundExpression bindExpr = string.IsNullOrEmpty(binding.PayloadFieldName)
                    ? variantRef
                    : new BoundMemberAccessExpression(variantRef, binding.PayloadFieldName, binding.Type);
                prelude.Add(new BoundVariableDeclaration(false, binding.Name, binding.Type, bindExpr));
            }
        }

        return (test, prelude);
    }

    (BoundExpression, List<BoundStatement>) BuildValueChoiceCaseTest(
        BoundMatchPattern pat, BoundNameExpression subjRef, ChoiceType vc)
    {
        var tagEnum = new ExternalType($"{vc.Name}_Tag");
        var tagAccess = new BoundMemberAccessExpression(subjRef, "Tag", tagEnum);
        var caseConst = new BoundMemberAccessExpression(
            new BoundNameExpression($"{vc.Name}_Tag", tagEnum), pat.CaseName!, tagEnum);
        var test = new BoundBinaryExpression(tagAccess, SyntaxTokenKind.EqualsEquals, caseConst, new PrimitiveType("bool"));

        var prelude = new List<BoundStatement>();
        if (pat.Bindings is { Count: > 0 } bindings)
        {
            foreach (var binding in bindings)
            {
                var payloadFieldName = $"{pat.CaseName}_{binding.PayloadFieldName}";
                prelude.Add(new BoundVariableDeclaration(false, binding.Name, binding.Type,
                    new BoundMemberAccessExpression(subjRef, payloadFieldName, binding.Type)));
            }
        }

        return (test, prelude);
    }

    (BoundExpression, List<BoundStatement>) BuildEnumCaseTest(
        BoundMatchPattern pat, BoundNameExpression subjRef, EnumType et)
    {
        var caseRef = new BoundMemberAccessExpression(
            new BoundNameExpression(et.Name, et), pat.CaseName!, et);
        var test = new BoundBinaryExpression(subjRef, SyntaxTokenKind.EqualsEquals, caseRef, new PrimitiveType("bool"));
        return (test, []);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    string FreshTemp(BoundType type) => $"__match_subj_{_tempCounter++}";
    string FreshBindingName(string sourceName) => $"__match_bind_{_tempCounter++}_{sourceName}";
}
