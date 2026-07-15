using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Symbols;
using Esharp.Syntax;

namespace Esharp.FlowAnalysis;

// ─────────────────────────────────────────────────────────────────────────────
//  Match Exhaustiveness — union/enum/closed-hierarchy completeness checking.
//
//  Operates over the bound tree (not the CFG) because exhaustiveness is a
//  structural property of match expressions, not a flow property.  Invoked as
//  a whole-program pass after binding completes, before lowering.
//
//  Coverage semantics:
//    1. ChoiceType (union / ref union) — every case must be covered by a
//       `.case` arm or a `default`.  A guard-only arm does NOT satisfy coverage
//       (the guard might be false).
//    2. EnumType — every named enum case must be covered, or `default` present.
//    3. Abstract DataType (`ClassModifier.Abstract`) whose DerivedTypes are all
//       sealed and in the same assembly (a "closed hierarchy") — every leaf
//       concrete subclass must be covered or `default` present.
//    4. Open base class / open world (literal int/string/bool) — `default`
//       required.
//
//  Diagnostics:
//    ES2170 — non-exhaustive match (missing case)
//    ES2171 — dead arm (already covered by an earlier unguarded arm, or case
//              doesn't exist on this type)
//    ES2172 — `.case` used on a non-union/non-enum scrutinee
//
//  AnnotationStore output:
//    ExhaustivenessKey(matchStatement) → ExhaustivenessVerdict
//    ExhaustivenessKey(matchExpression) → ExhaustivenessVerdict
//    ReachabilityAnalysis reads the verdict to determine definite-return from
//    an exhaustive match where every arm terminates.
//
//  BoundMatchPattern shape (from BoundNodes.cs):
//    BoundMatchPattern { IsDefault, CaseName, Bindings, NarrowedType, IsNil, LiteralValue }
//    — there is ONE pattern record; discriminated by which fields are non-null.
//
//  BoundMatchArm (statement match) vs BoundMatchExpressionArm (expression match):
//    The statement form carries Arms: IReadOnlyList<BoundMatchArm>.
//    The expression form carries Arms: IReadOnlyList<BoundMatchExpressionArm>.
//    Both share the same BoundMatchPattern type.
// ─────────────────────────────────────────────────────────────────────────────

/// The result of checking one match statement or expression.
public sealed class ExhaustivenessVerdict
{
    public bool IsExhaustive { get; init; }
    /// Cases not covered by the match.
    public IReadOnlyList<string> MissingCases { get; init; } = [];
    /// Arm indices that are dead (unreachable).
    public IReadOnlyList<int> DeadArmIndices { get; init; } = [];
}

/// AnnotationStore key for a BoundMatchStatement's exhaustiveness result.
public sealed record MatchStatementKey(BoundMatchStatement Match);

/// AnnotationStore key for a BoundMatchExpression's exhaustiveness result.
public sealed record MatchExpressionKey(BoundMatchExpression Match);

/// The exhaustiveness checker.
public static class MatchExhaustivenessChecker
{
    // ── Statement form ────────────────────────────────────────────────────────

    public static ExhaustivenessVerdict Check(
        BoundMatchStatement match,
        DiagnosticBag diagnostics,
        AnnotationStore annotations)
    {
        var arms    = match.Arms.Select(a => a.Pattern).ToList();
        var guards  = match.Arms.Select(a => a.Guard).ToList();
        var verdict = CheckInternal(match.SubjectType, arms, guards, match.Span, diagnostics);
        annotations.Set(new MatchStatementKey(match), verdict);
        // Also register under the shared key used by ReachabilityAnalysis.
        annotations.Set(new MatchStatementExhaustivenessKey(match), verdict);
        return verdict;
    }

    // ── Expression form ───────────────────────────────────────────────────────

    public static ExhaustivenessVerdict Check(
        BoundMatchExpression match,
        DiagnosticBag diagnostics,
        AnnotationStore annotations)
    {
        var arms   = match.Arms.Select(a => a.Pattern).ToList();
        var guards = match.Arms.Select(a => a.Guard).ToList();
        var verdict = CheckInternal(match.SubjectType, arms, guards, match.Span, diagnostics);
        annotations.Set(new MatchExpressionKey(match), verdict);
        return verdict;
    }

    // ── Core check ────────────────────────────────────────────────────────────

    static ExhaustivenessVerdict CheckInternal(
        BoundType subjectType,
        IReadOnlyList<BoundMatchPattern> patterns,
        IReadOnlyList<BoundExpression?> guards,
        SourceSpan span,
        DiagnosticBag diagnostics)
    {
        switch (subjectType)
        {
            case ChoiceType choiceType:
                // union / ref union
                var allCases = GetCaseNames(choiceType.Symbol);
                return CheckCaseSet(span, patterns, guards, allCases, diagnostics, isOpenWorld: false);

            case EnumType enumType:
                var enumCases = GetCaseNames(enumType.Symbol);
                return CheckCaseSet(span, patterns, guards, enumCases, diagnostics, isOpenWorld: false);

            case DataType dataType
                when dataType.Symbol is TypeSymbol { TypeKind: TypeSymbolKind.Class } ts
                  && IsAbstractFromSymbol(ts)
                  && IsClosedHierarchy(ts):
                // Closed abstract-class hierarchy.
                var leaves = CollectLeaves(ts).Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
                return CheckCaseSet(span, patterns, guards, leaves, diagnostics, isOpenWorld: false);

            case DataType dataType2
                when dataType2.Symbol is TypeSymbol { TypeKind: TypeSymbolKind.Class } tsOpen
                  && IsAbstractFromSymbol(tsOpen):
                // Open hierarchy — `default` required.
                return CheckOpenWorld(span, patterns, guards, diagnostics,
                    msg: "Match over an open class hierarchy requires a 'default' arm.");

            default:
                // Literal / primitive / external / unknown — `default` required.
                return CheckOpenWorld(span, patterns, guards, diagnostics,
                    msg: "Match over an open-world type requires a 'default' arm.");
        }
    }

    // ── Case-set coverage ─────────────────────────────────────────────────────

    static ExhaustivenessVerdict CheckCaseSet(
        SourceSpan matchSpan,
        IReadOnlyList<BoundMatchPattern> patterns,
        IReadOnlyList<BoundExpression?> guards,
        IReadOnlyCollection<string> allCases,
        DiagnosticBag diagnostics,
        bool isOpenWorld)
    {
        var covered  = new HashSet<string>(StringComparer.Ordinal);
        var deadArms = new List<int>();
        bool hasDefault = false;

        for (var i = 0; i < patterns.Count; i++)
        {
            var pattern = patterns[i];
            var guard   = i < guards.Count ? guards[i] : null;

            // ── `default` arm ─────────────────────────────────────────────────
            if (pattern.IsDefault)
            {
                if (covered.SetEquals(allCases))
                {
                    // Everything already covered → default is redundant/dead.
                    deadArms.Add(i);
                    diagnostics.Warn(matchSpan,
                        "ES2171: Unreachable 'default' arm — all cases are already covered.");
                }
                hasDefault = true;
                break; // default terminates arm scanning
            }

            // ── `nil` arm ─────────────────────────────────────────────────────
            if (pattern.IsNil)
            {
                // nil arm is structural; we don't count it toward case coverage.
                continue;
            }

            // ── literal arm ───────────────────────────────────────────────────
            if (pattern.LiteralValue is not null)
            {
                // Literal match is always open-world — contributes to coverage
                // as a point but we never know if it's exhaustive without a default.
                continue;
            }

            // ── type-pattern arm (NarrowedType non-null) ──────────────────────
            if (pattern.NarrowedType is not null)
            {
                var typeName = TypeDisplayName(pattern.NarrowedType);
                if (!allCases.Contains(typeName))
                {
                    diagnostics.Report(matchSpan,
                        $"ES2172: Type '{typeName}' is not a member of the closed hierarchy.");
                    continue;
                }
                if (covered.Contains(typeName) && guard is null)
                {
                    deadArms.Add(i);
                    diagnostics.Warn(matchSpan,
                        $"ES2171: Unreachable arm — type '{typeName}' is already covered by an earlier arm.");
                    continue;
                }
                if (guard is null)
                    covered.Add(typeName);
                continue;
            }

            // ── case arm (CaseName non-null) ──────────────────────────────────
            if (pattern.CaseName is string caseName)
            {
                if (!allCases.Contains(caseName))
                {
                    diagnostics.Report(matchSpan,
                        $"ES2172: Case '{caseName}' does not exist on this type.");
                    continue;
                }
                if (covered.Contains(caseName) && guard is null)
                {
                    deadArms.Add(i);
                    diagnostics.Warn(matchSpan,
                        $"ES2171: Unreachable arm — case '{caseName}' is already covered by an earlier arm.");
                    continue;
                }
                // Guard-only arms don't satisfy coverage (the guard might be false).
                if (guard is null)
                    covered.Add(caseName);
                continue;
            }
        }

        var missing     = allCases.Except(covered).ToList();
        bool exhaustive = hasDefault || missing.Count == 0;

        if (!exhaustive)
        {
            var list = string.Join(", ", missing.Select(c => $"'{c}'"));
            diagnostics.Warn(matchSpan,
                $"ES2170: Non-exhaustive match — missing case(s): {list}.");
        }

        return new ExhaustivenessVerdict
        {
            IsExhaustive   = exhaustive,
            MissingCases   = missing,
            DeadArmIndices = deadArms,
        };
    }

    // ── Open-world check ──────────────────────────────────────────────────────

    static ExhaustivenessVerdict CheckOpenWorld(
        SourceSpan span,
        IReadOnlyList<BoundMatchPattern> patterns,
        IReadOnlyList<BoundExpression?> guards,
        DiagnosticBag diagnostics,
        string msg)
    {
        bool hasDefault = patterns.Any(p => p.IsDefault);
        if (!hasDefault)
            diagnostics.Warn(span, $"ES2170: {msg}");
        return new ExhaustivenessVerdict
        {
            IsExhaustive = hasDefault,
            MissingCases = hasDefault ? [] : ["default"],
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static IReadOnlyCollection<string> GetCaseNames(TypeSymbol? ts)
    {
        if (ts is null) return [];
        return ts.Cases.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
    }

    static bool IsAbstractFromSymbol(TypeSymbol ts)
    {
        // The `abstract` modifier lives on the syntax declaration.
        if (ts.Decl is DataDeclarationSyntax { Modifier: ClassModifier.Abstract })
            return true;
        // Fallback: any symbol explicitly noted as abstract by the binder
        // (e.g. closed-hierarchy open base) has Kind == Class.
        return false;
    }

    static bool IsClosedHierarchy(TypeSymbol ts)
    {
        // A closed hierarchy: the symbol has DerivedTypes populated, and every
        // derived type is itself sealed (ClassModifier.Sealed) and in the same
        // compilation (no ExternalCSharp / External derived types).
        if (ts.DerivedTypes.Count == 0) return false;
        return ts.DerivedTypes.All(d =>
            d.TypeKind != TypeSymbolKind.External
            && d.TypeKind != TypeSymbolKind.ExternalCSharp
            && d.Decl is DataDeclarationSyntax { Modifier: ClassModifier.Sealed });
    }

    static IEnumerable<TypeSymbol> CollectLeaves(TypeSymbol baseType)
    {
        foreach (var d in baseType.DerivedTypes)
        {
            if (!IsAbstractFromSymbol(d))
                yield return d; // concrete leaf
            else
                foreach (var leaf in CollectLeaves(d))
                    yield return leaf;
        }
    }

    static string TypeDisplayName(BoundType type) => type switch
    {
        DataType dt     => dt.Name,
        ChoiceType ct   => ct.Name,
        EnumType et     => et.Name,
        InterfaceType it => it.Name,
        _               => type.EmitName,
    };
}

/// Whole-program pass: walk every match statement and expression and run
/// exhaustiveness checking.  Must run AFTER bind, BEFORE lowering.
/// (MatchLowering in Pillar 3 reads the ExhaustivenessVerdict from the store.)
public static class MatchExhaustivenessPass
{
    public static void Run(
        IEnumerable<BoundCompilationUnit> units,
        DiagnosticBag diagnostics,
        AnnotationStore annotations)
    {
        foreach (var unit in units)
            WalkUnit(unit, diagnostics, annotations);
    }

    static void WalkUnit(BoundCompilationUnit unit, DiagnosticBag diagnostics, AnnotationStore annotations)
    {
        foreach (var member in unit.Members)
            WalkMember(member, diagnostics, annotations);
    }

    static void WalkMember(BoundMember member, DiagnosticBag diagnostics, AnnotationStore annotations)
    {
        switch (member)
        {
            case BoundFunctionDeclaration fn when fn.Body is not null:
                WalkStatement(fn.Body, diagnostics, annotations);
                break;

            case BoundDataDeclaration data:
                foreach (var m in data.InstanceMethods)
                    if (m.Body is not null) WalkStatement(m.Body, diagnostics, annotations);
                break;

            case BoundStaticFuncDeclaration sf:
                foreach (var fn in sf.Functions)
                    if (fn.Body is not null) WalkStatement(fn.Body, diagnostics, annotations);
                break;
        }
    }

    static void WalkStatement(BoundStatement stmt, DiagnosticBag diagnostics, AnnotationStore annotations)
    {
        switch (stmt)
        {
            case BoundBlockStatement blk:
                foreach (var s in blk.Statements)
                    WalkStatement(s, diagnostics, annotations);
                break;

            case BoundMatchStatement ms:
                MatchExhaustivenessChecker.Check(ms, diagnostics, annotations);
                WalkExpression(ms.Subject, diagnostics, annotations);
                foreach (var arm in ms.Arms)
                {
                    if (arm.Guard is not null) WalkExpression(arm.Guard, diagnostics, annotations);
                    WalkStatement(arm.Body, diagnostics, annotations);
                }
                break;

            case BoundIfStatement ifStmt:
                WalkExpression(ifStmt.Condition, diagnostics, annotations);
                WalkStatement(ifStmt.Then, diagnostics, annotations);
                if (ifStmt.Else is not null) WalkStatement(ifStmt.Else, diagnostics, annotations);
                break;

            case BoundWhileStatement wh:
                WalkExpression(wh.Condition, diagnostics, annotations);
                WalkStatement(wh.Body, diagnostics, annotations);
                break;

            case BoundForEachStatement fe:
                WalkExpression(fe.Collection, diagnostics, annotations);
                WalkStatement(fe.Body, diagnostics, annotations);
                break;

            case BoundReturnStatement ret:
                if (ret.Expression is not null) WalkExpression(ret.Expression, diagnostics, annotations);
                break;

            case BoundExpressionStatement es:
                WalkExpression(es.Expression, diagnostics, annotations);
                break;

            case BoundVariableDeclaration vd:
                if (vd.Initializer is not null) WalkExpression(vd.Initializer, diagnostics, annotations);
                break;

            case BoundLetGuard guard:
                WalkExpression(guard.Initializer, diagnostics, annotations);
                WalkStatement(guard.ElseBody, diagnostics, annotations);
                break;

            case BoundTryStatement tr:
                WalkStatement(tr.Body, diagnostics, annotations);
                foreach (var clause in tr.Catches)
                {
                    if (clause.Guard is not null) WalkExpression(clause.Guard, diagnostics, annotations);
                    WalkStatement(clause.Body, diagnostics, annotations);
                }
                break;

            case BoundDeferStatement defer:
                WalkStatement(defer.Body, diagnostics, annotations);
                break;

            case BoundThrowStatement th:
                if (th.Expression is not null) WalkExpression(th.Expression, diagnostics, annotations);
                break;
        }
    }

    static void WalkExpression(BoundExpression expr, DiagnosticBag diagnostics, AnnotationStore annotations)
    {
        switch (expr)
        {
            case BoundMatchExpression me:
                MatchExhaustivenessChecker.Check(me, diagnostics, annotations);
                WalkExpression(me.Subject, diagnostics, annotations);
                foreach (var arm in me.Arms)
                {
                    if (arm.Guard is not null) WalkExpression(arm.Guard, diagnostics, annotations);
                    WalkExpression(arm.Value, diagnostics, annotations);
                }
                break;

            case BoundCallExpression call:
                WalkExpression(call.Target, diagnostics, annotations);
                foreach (var arg in call.Arguments)
                    WalkExpression(arg, diagnostics, annotations);
                break;

            case BoundBinaryExpression bin:
                WalkExpression(bin.Left, diagnostics, annotations);
                WalkExpression(bin.Right, diagnostics, annotations);
                break;

            case BoundUnaryExpression un:
                WalkExpression(un.Operand, diagnostics, annotations);
                break;

            case BoundMemberAccessExpression mem:
                WalkExpression(mem.Target, diagnostics, annotations);
                break;

            case BoundObjectCreationExpression oc:
                foreach (var f in oc.Fields)
                    WalkExpression(f.Value, diagnostics, annotations);
                break;

            case BoundConditionalExpression cond:
                WalkExpression(cond.Condition, diagnostics, annotations);
                WalkExpression(cond.Consequence, diagnostics, annotations);
                WalkExpression(cond.Alternative, diagnostics, annotations);
                break;

            case BoundIfExpression ifExpr:
                foreach (var branch in ifExpr.Branches)
                {
                    WalkExpression(branch.Condition, diagnostics, annotations);
                    if (branch.Value is not null) WalkExpression(branch.Value, diagnostics, annotations);
                }
                if (ifExpr.ElseValue is not null) WalkExpression(ifExpr.ElseValue, diagnostics, annotations);
                break;

            case BoundWithExpression we:
                WalkExpression(we.Target, diagnostics, annotations);
                foreach (var f in we.Fields)
                    WalkExpression(f.Value, diagnostics, annotations);
                break;

            case BoundNullCoalescingExpression nc:
                WalkExpression(nc.Left, diagnostics, annotations);
                WalkExpression(nc.Right, diagnostics, annotations);
                break;

            case BoundAwaitExpression aw:
                WalkExpression(aw.Inner, diagnostics, annotations);
                break;

            case BoundFunctionLiteralExpression fn:
                if (fn.Body is not null) WalkStatement(fn.Body, diagnostics, annotations);
                break;

            case BoundTypeTestExpression tt:
                WalkExpression(tt.Operand, diagnostics, annotations);
                break;

            // BoundConversion unifies all cast/narrow forms (spine-deltas §3):
            //   IsInst   → was BoundSafeCastExpression
            //   CastClass → was BoundAssertCastExpression
            //   Narrow   → was BoundNarrowedExpression
            case BoundConversion conv:
                WalkExpression(conv.Operand, diagnostics, annotations);
                break;

            default:
                break;
        }
    }
}
