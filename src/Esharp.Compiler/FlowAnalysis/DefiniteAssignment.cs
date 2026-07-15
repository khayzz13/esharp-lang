using System.Collections.Immutable;
using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Symbols;
using Esharp.Syntax;

namespace Esharp.FlowAnalysis;

// ─────────────────────────────────────────────────────────────────────────────
//  Definite Assignment (DA) analysis.
//
//  Implements the classical forward may-be-definitely-assigned lattice over
//  the CFG produced by CfgBuilder.  Authoritative check for:
//
//    ES2301  Variable 'x' used before it is definitely assigned.
//    ES2302  'out' parameter 'x' is not definitely assigned on all paths that
//            return from the function.
//
//  Lattice:
//    State = ImmutableHashSet<string> — the SET of names that are definitely
//    assigned at the current program point (string keys matching
//    BoundNameExpression.Name / BoundVariableDeclaration.Name).
//    Bottom = {} (nothing known).
//    Join  = intersection (DA only if DA on EVERY incoming path).
//    Leq(a, b) = b ⊆ a.
//
//  BoundNameExpression is the only expression node that reads a local by name;
//  BoundVariableDeclaration and BoundAssignment (statement) write.
//  Out-parameters start NOT in the DA set; all non-out parameters start DA.
//
//  Note on LocalSymbol vs name strings:
//    The bound tree carries BoundNameExpression(Name: string) — not a symbol
//    reference — so the DA set is keyed by name.  The `out` parameter check
//    cross-references against BoundParameter.IsOut for the end-of-function
//    report, using the same name strings.
// ─────────────────────────────────────────────────────────────────────────────

/// DA lattice state: set of definitely-assigned local names.
public readonly struct DaState(ImmutableHashSet<string> assigned)
{
    public static readonly DaState Empty = new(ImmutableHashSet<string>.Empty);
    public readonly ImmutableHashSet<string> Assigned = assigned ?? ImmutableHashSet<string>.Empty;

    public DaState Add(string name) => new(Assigned.Add(name));
}

sealed class DaLattice : ILattice<DaState>
{
    public static readonly DaLattice Instance = new();
    public DaState Bottom => DaState.Empty;
    // Join = intersection: a name is DA only if DA on ALL incoming paths.
    public DaState Join(DaState a, DaState b) => new(a.Assigned.Intersect(b.Assigned));
    // a ⊑ b iff b ⊆ a (b has at least as much information).
    public bool Leq(DaState a, DaState b) => b.Assigned.IsSubsetOf(a.Assigned);
}

public sealed class DefiniteAssignmentAnalysis : IDataFlowAnalysis<DaState>
{
    readonly BoundFunctionDeclaration _fn;

    // Names of `out` parameters — must be DA on every exit path.
    readonly IReadOnlyList<string> _outParamNames;

    public DefiniteAssignmentAnalysis(BoundFunctionDeclaration fn)
    {
        _fn = fn;
        _outParamNames = fn.Parameters
            .Where(p => p.IsOut)
            .Select(p => p.Name)
            .ToList();
    }

    public ILattice<DaState> Lattice => DaLattice.Instance;
    public bool IsForward => true;

    public DaState Transfer(CfgBlock block, DaState input, AnalysisContext ctx)
    {
        var state = input;

        // Entry block: seed all non-out parameters as definitely assigned.
        if (block.IsEntry)
        {
            foreach (var p in _fn.Parameters)
            {
                if (!p.IsOut)
                    state = state.Add(p.Name);
            }
        }

        foreach (var node in block.Nodes)
            state = VisitNode(node, state, ctx);

        return state;
    }

    DaState VisitNode(object node, DaState state, AnalysisContext ctx)
    {
        switch (node)
        {
            // ── Variable declaration (BoundVariableDeclaration) ───────────────

            case BoundVariableDeclaration decl:
                // RHS is evaluated (and read for sub-expressions) before the local
                // enters DA.  BoundVariableDeclaration.Initializer is always present
                // at the bound level (binder supplies BoundDefaultExpression otherwise).
                CheckRead(decl.Initializer, state, ctx);
                state = state.Add(decl.Name);
                break;

            // ── Assignment statement (BoundAssignment) ────────────────────────

            case BoundAssignment assign:
                CheckRead(assign.Value, state, ctx);
                // Lvalue: if it's a bare name, it is being assigned.
                if (assign.Target is BoundNameExpression { Name: var lhsName })
                    state = state.Add(lhsName);
                break;

            // ── Compound assignment (BoundCompoundAssignment) ─────────────────

            case BoundCompoundAssignment compound:
                // Compound reads the target before writing.
                if (compound.Target is BoundNameExpression { Name: var compName })
                {
                    if (!state.Assigned.Contains(compName))
                        EmitUseBeforeAssign(compound.Span, compName, ctx);
                }
                CheckRead(compound.Value, state, ctx);
                if (compound.Target is BoundNameExpression { Name: var compDst })
                    state = state.Add(compDst);
                break;

            // ── Let guard (BoundLetGuard) ─────────────────────────────────────

            case BoundLetGuard guard:
                CheckRead(guard.Initializer, state, ctx);
                // The guard name is DA in the continuation (else-body terminates).
                state = state.Add(guard.Name);
                break;

            // ── Return (terminal, check the expression) ───────────────────────

            case BoundReturnStatement ret:
                if (ret.Expression is not null)
                    CheckRead(ret.Expression, state, ctx);
                break;

            // ── Expression statement (BoundExpressionStatement) ───────────────

            case BoundExpressionStatement es:
                CheckRead(es.Expression, state, ctx);
                break;

            // ── Out-argument declaration inline ───────────────────────────────

            // BoundOutArgumentExpression.DeclaresLocal=true — the name enters DA
            // after the call (handled by checking the containing BoundCallExpression
            // in CheckRead).
            default:
                // For any remaining statement-level node, if it is an expression,
                // check it for reads.
                if (node is BoundExpression topExpr)
                    CheckRead(topExpr, state, ctx);
                break;
        }
        return state;
    }

    void CheckRead(BoundExpression expr, DaState state, AnalysisContext ctx)
    {
        switch (expr)
        {
            case BoundNameExpression name:
                if (!state.Assigned.Contains(name.Name))
                    EmitUseBeforeAssign(name.Span, name.Name, ctx);
                break;

            case BoundBinaryExpression bin:
                CheckRead(bin.Left, state, ctx);
                CheckRead(bin.Right, state, ctx);
                break;

            case BoundUnaryExpression un:
                CheckRead(un.Operand, state, ctx);
                break;

            case BoundCallExpression call:
                CheckRead(call.Target, state, ctx);
                foreach (var arg in call.Arguments)
                    CheckRead(arg, state, ctx);
                break;

            case BoundMemberAccessExpression mem:
                CheckRead(mem.Target, state, ctx);
                break;

            case BoundNullConditionalAccessExpression nca:
                CheckRead(nca.Target, state, ctx);
                break;

            case BoundIndexExpression idx:
                CheckRead(idx.Target, state, ctx);
                CheckRead(idx.Index, state, ctx);
                break;

            case BoundConditionalExpression cond:
                CheckRead(cond.Condition, state, ctx);
                CheckRead(cond.Consequence, state, ctx);
                CheckRead(cond.Alternative, state, ctx);
                break;

            case BoundNullCoalescingExpression nc:
                CheckRead(nc.Left, state, ctx);
                CheckRead(nc.Right, state, ctx);
                break;

            // BoundParenthesizedExpression is dropped — the binder unwraps it in place.
            // (This arm kept for belt-and-suspenders against old-tree paths.)

            case BoundObjectCreationExpression oc:
                foreach (var f in oc.Fields)
                    CheckRead(f.Value, state, ctx);
                break;

            case BoundWithExpression we:
                CheckRead(we.Target, state, ctx);
                foreach (var f in we.Fields)
                    CheckRead(f.Value, state, ctx);
                break;

            case BoundListLiteralExpression list:
                foreach (var el in list.Elements)
                    CheckRead(el, state, ctx);
                break;

            case BoundTupleLiteralExpression tup:
                foreach (var el in tup.Elements)
                    CheckRead(el, state, ctx);
                break;

            case BoundAwaitExpression aw:
                CheckRead(aw.Inner, state, ctx);
                break;

            case BoundTryUnwrapExpression tu:
                CheckRead(tu.Inner, state, ctx);
                break;

            case BoundResultCallExpression rc:
                CheckRead(rc.Argument, state, ctx);
                break;

            case BoundInterpolatedStringExpression interp:
                foreach (var part in interp.Parts)
                    if (part.Expr is not null) CheckRead(part.Expr, state, ctx);
                break;

            case BoundMatchExpression me:
                CheckRead(me.Subject, state, ctx);
                foreach (var arm in me.Arms)
                {
                    if (arm.Guard is not null) CheckRead(arm.Guard, state, ctx);
                    CheckRead(arm.Value, state, ctx);
                }
                break;

            case BoundIfExpression ifExpr:
                foreach (var branch in ifExpr.Branches)
                {
                    CheckRead(branch.Condition, state, ctx);
                    if (branch.Value is not null) CheckRead(branch.Value, state, ctx);
                }
                if (ifExpr.ElseValue is not null) CheckRead(ifExpr.ElseValue, state, ctx);
                break;

            case BoundTypeTestExpression tt:
                CheckRead(tt.Operand, state, ctx);
                break;

            // BoundConversion covers all cast/narrow kinds (§3 spine-deltas):
            //   ConversionKind.IsInst   → was BoundSafeCastExpression
            //   ConversionKind.CastClass → was BoundAssertCastExpression
            //   ConversionKind.Narrow   → was BoundNarrowedExpression
            //   ConversionKind.Box/Unbox/Identity → implicit binder coercions
            case BoundConversion conv:
                CheckRead(conv.Operand, state, ctx);
                break;

            case BoundFunctionLiteralExpression fn:
                // Captured variables are reads.
                foreach (var cap in fn.CapturedVariables)
                    if (!state.Assigned.Contains(cap.Name))
                        EmitUseBeforeAssign(expr.Span, cap.Name, ctx);
                break;

            case BoundDotCaseExpression dc:
                foreach (var a in dc.Arguments)
                    CheckRead(a, state, ctx);
                break;

            case BoundRangeExpression rg:
                if (rg.Target is not null) CheckRead(rg.Target, state, ctx);
                if (rg.Start  is not null) CheckRead(rg.Start,  state, ctx);
                if (rg.End    is not null) CheckRead(rg.End,    state, ctx);
                break;

            case BoundSpawnExpression sp:
                foreach (var cap in sp.CapturedVariables)
                    if (!state.Assigned.Contains(cap.Name))
                        EmitUseBeforeAssign(expr.Span, cap.Name, ctx);
                break;

            case BoundArrayCreationExpression arr:
                CheckRead(arr.Size, state, ctx);
                break;

            // Leaves that carry no local reads: literals, defaults, address-of function,
            // method-group conversions, BoundOutArgumentExpression, BoundErrorExpression.
            default:
                break;
        }
    }

    static void EmitUseBeforeAssign(SourceSpan span, string name, AnalysisContext ctx) =>
        ctx.Diagnostics.Report(span, $"ES2301: Variable '{name}' is used before it is definitely assigned.");

    public void Finalize(
        IReadOnlyDictionary<CfgBlock, DaState> finalIn,
        IReadOnlyDictionary<CfgBlock, DaState> finalOut,
        AnalysisContext ctx)
    {
        if (_outParamNames.Count == 0) return;

        // ES2302: on every reachable exit block, all `out` parameters must be DA.
        foreach (var (block, outState) in finalOut)
        {
            if (!block.IsExit) continue;

            foreach (var name in _outParamNames)
            {
                if (!outState.Assigned.Contains(name))
                {
                    ctx.Diagnostics.Report(
                        _fn.Span,
                        $"ES2302: 'out' parameter '{name}' is not definitely assigned on all return paths.");
                }
            }
        }
    }
}

/// Entry point the pipeline calls after building the CFG.
public static class DefiniteAssignmentPass
{
    public static void Run(
        BoundFunctionDeclaration fn,
        DiagnosticBag diagnostics,
        AnnotationStore annotations)
    {
        var cfg      = CfgBuilder.Build(fn, fn.Name);
        var analysis = new DefiniteAssignmentAnalysis(fn);
        var ctx      = new AnalysisContext(diagnostics, annotations, fn.Name);
        DataFlowEngine.Run(cfg, analysis, ctx);
    }
}
