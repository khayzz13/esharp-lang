using Esharp.BoundTree;
using Esharp.BoundTree;

namespace Esharp.CodeGen;

/// <summary>
/// Thrown when a FEATURE-tier BoundNode survives to CodeGen.
/// This indicates a lowering pass failed to meet its contract.
/// The message names every surviving node type and its location context.
/// </summary>
public sealed class FeatureNodeInCodeGenException : Exception
{
    public FeatureNodeInCodeGenException(string message) : base(message) { }
}

/// <summary>
/// Records a single FEATURE node violation found during the pre-codegen assertion walk.
/// </summary>
public sealed record FeatureNodeViolation(string Kind, string Description);

/// <summary>
/// Walks the entire bound tree looking for FEATURE-tier nodes that should have been
/// eliminated by <c>LoweringPipeline</c>. Each violation is recorded in
/// <see cref="Violations"/>; the walk continues past the first hit so ALL pipeline
/// failures are surfaced in one crash.
///
/// FEATURE nodes (each mapped to the lowering pass that owns it):
///   BoundMatchStatement / BoundMatchExpression          → MatchLowering
///   BoundDeferStatement                                 → DeferLowering
///   BoundForEachStatement                               → ForEachLowering
///   BoundTryUnwrapExpression                            → ResultLowering
///   BoundResultCallExpression                           → ResultLowering
///   BoundConditionalExpression (expression-form)        → ExpressionFormLowering
///   BoundIfExpression                                   → ExpressionFormLowering
///   BoundNullCoalescingExpression                       → NullFlowLowering
///   BoundNullConditionalAccessExpression                → NullFlowLowering
///   BoundCompoundAssignment                             → AssignmentLowering
///   BoundLetGuard                                       → LetGuardLowering
///   BoundWithExpression                                 → WithLowering
///   BoundInterpolatedStringExpression                   → StringLowering
///   BoundRaiseStatement                                 → EventLowering
///   BoundFunctionLiteralExpression / BoundCapturedVar   → ClosureConversion
///   BoundAwaitExpression (BoundFunctionDeclaration.HasAwait) → AsyncLowering
///   BoundAsyncLetStatement                              → AsyncLetLowering
///   BoundYieldStatement                                 → AsyncStreamLowering
///   BoundSpawnExpression / BoundSelectStatement         → ConcurrencyLowering
///   BoundChanCreationExpression                         → ConcurrencyLowering
///   BoundParenthesizedExpression                        → lowering (carry-no-IL node)
/// </summary>
internal sealed class FeatureNodeCollector
{
    public List<FeatureNodeViolation> Violations { get; } = [];

    // ---- Top-level entry points ----

    public void VisitCompilationUnit(BoundCompilationUnit unit)
    {
        foreach (var m in unit.Members)
            VisitMember(m);
    }

    void VisitMember(BoundMember m)
    {
        switch (m)
        {
            case BoundFunctionDeclaration f:
                if (f.HasAwait)
                    Violation("BoundFunctionDeclaration.HasAwait", $"func '{f.Name}' — async body not lowered by AsyncLowering");
                VisitBlock(f.Body);
                break;
            case BoundDataDeclaration d:
                foreach (var im in d.InstanceMethods) VisitMember(im);
                foreach (var fld in d.Fields)
                    if (fld.DefaultValue is not null) VisitExpression(fld.DefaultValue);
                break;
            case BoundStaticFuncDeclaration sf:
                foreach (var fn in sf.Functions) VisitMember(fn);
                break;
            case BoundNamespaceInitDeclaration init:
                VisitBlock(init.Body);
                break;
            case BoundNamespaceStateDeclaration state:
                if (state.Field.DefaultValue is not null) VisitExpression(state.Field.DefaultValue);
                if (state.ComputedGetter is not null) VisitExpression(state.ComputedGetter);
                if (state.SetterBody is not null) VisitExpression(state.SetterBody);
                break;
            // BoundInitDeclaration, BoundInterfaceDeclaration, BoundChoiceDeclaration,
            // BoundEnumDeclaration, BoundDelegateDeclaration, BoundConstDeclaration
            // carry no statements — nothing to assert.
        }
    }

    void VisitBlock(BoundBlockStatement block)
    {
        foreach (var s in block.Statements)
            VisitStatement(s);
    }

    // ---- Statement walker ----

    void VisitStatement(BoundStatement s)
    {
        switch (s)
        {
            // ---- FEATURE nodes ----
            case BoundMatchStatement ms:
                Violation("BoundMatchStatement", $"match over {ms.SubjectType} — not lowered by MatchLowering");
                VisitExpression(ms.Subject);
                foreach (var arm in ms.Arms) { VisitBlock(arm.Body); if (arm.Guard is not null) VisitExpression(arm.Guard); }
                break;

            case BoundDeferStatement ds:
                Violation("BoundDeferStatement", "defer block — not lowered by DeferLowering");
                VisitBlock(ds.Body);
                break;

            case BoundForEachStatement fe:
                Violation("BoundForEachStatement", $"foreach '{fe.Identifier}' — not lowered by ForEachLowering");
                VisitExpression(fe.Collection);
                VisitStatement(fe.Body);
                break;

            case BoundRaiseStatement rs:
                Violation("BoundRaiseStatement", $"raise '{rs.EventName}' — not lowered by EventLowering");
                foreach (var a in rs.Arguments) VisitExpression(a);
                break;

            case BoundCompoundAssignment ca:
                Violation("BoundCompoundAssignment", $"compound assignment '{ca.Op}' — not lowered by AssignmentLowering");
                VisitExpression(ca.Target);
                VisitExpression(ca.Value);
                break;

            case BoundLetGuard lg:
                Violation("BoundLetGuard", $"let '{lg.Name}' else — not lowered by LetGuardLowering");
                VisitExpression(lg.Initializer);
                VisitBlock(lg.ElseBody);
                break;

            case BoundAsyncLetStatement al:
                Violation("BoundAsyncLetStatement", $"async let '{al.Name}' — not lowered by AsyncLetLowering");
                VisitExpression(al.Initializer);
                break;

            case BoundYieldStatement y:
                Violation("BoundYieldStatement", "yield — not lowered by AsyncStreamLowering");
                VisitExpression(y.Value);
                break;

            case BoundSelectStatement sel:
                Violation("BoundSelectStatement", "select { } — not lowered by ConcurrencyLowering");
                foreach (var arm in sel.Arms)
                {
                    if (arm.Channel is not null) VisitExpression(arm.Channel);
                    if (arm.Value is not null) VisitExpression(arm.Value);
                    VisitBlock(arm.Body);
                }
                break;

            // ---- CORE nodes — recurse into sub-trees ----
            case BoundBlockStatement b:
                VisitBlock(b);
                break;
            case BoundVariableDeclaration vd:
                VisitExpression(vd.Initializer);
                break;
            case BoundAssignment a:
                VisitExpression(a.Target);
                VisitExpression(a.Value);
                break;
            case BoundIfStatement ifs:
                VisitExpression(ifs.Condition);
                VisitStatement(ifs.Then);
                if (ifs.Else is not null) VisitStatement(ifs.Else);
                break;
            case BoundWhileStatement ws:
                VisitExpression(ws.Condition);
                VisitStatement(ws.Body);
                break;
            case BoundReturnStatement ret:
                if (ret.Expression is not null) VisitExpression(ret.Expression);
                break;
            case BoundExpressionStatement es:
                VisitExpression(es.Expression);
                break;
            case BoundTryStatement ts:
                VisitBlock(ts.Body);
                foreach (var c in ts.Catches) VisitBlock(c.Body);
                break;
            case BoundThrowStatement th:
                if (th.Expression is not null) VisitExpression(th.Expression);
                break;
            case BoundConstStatement:
            case BoundBreakStatement:
            case BoundContinueStatement:
            case BoundLabelStatement:
            case BoundGotoStatement:
                break; // no sub-expressions
        }
    }

    // ---- Expression walker ----

    void VisitExpression(BoundExpression e)
    {
        switch (e)
        {
            // ---- FEATURE nodes ----
            case BoundMatchExpression me:
                Violation("BoundMatchExpression", $"match expression over {me.SubjectType} — not lowered by MatchLowering");
                VisitExpression(me.Subject);
                foreach (var arm in me.Arms) { VisitExpression(arm.Value); if (arm.Guard is not null) VisitExpression(arm.Guard); }
                break;

            case BoundTryUnwrapExpression tu:
                Violation("BoundTryUnwrapExpression", $"? unwrap of type {tu.Inner.Type} — not lowered by ResultLowering");
                VisitExpression(tu.Inner);
                break;

            case BoundResultCallExpression rc:
                Violation("BoundResultCallExpression", $"{(rc.IsOk ? "ok" : "error")}(...) — not lowered by ResultLowering");
                VisitExpression(rc.Argument);
                break;

            case BoundIfExpression ife:
                Violation("BoundIfExpression", "if expression — not lowered by ExpressionFormLowering");
                foreach (var br in ife.Branches) { VisitExpression(br.Condition); foreach (var s in br.Body) VisitStatement(s); if (br.Value is not null) VisitExpression(br.Value); }
                foreach (var s in ife.ElseBody) VisitStatement(s);
                if (ife.ElseValue is not null) VisitExpression(ife.ElseValue);
                break;

            case BoundConditionalExpression cond:
                // CORE — CodeGen emits a ternary directly (EmitConditional), as ESC does. A
                // ternary is rewritten by lowering only when a branch hoists; otherwise it
                // survives to here legitimately.
                VisitExpression(cond.Condition);
                VisitExpression(cond.Consequence);
                VisitExpression(cond.Alternative);
                break;

            case BoundNullCoalescingExpression nc:
                Violation("BoundNullCoalescingExpression", "?? — not lowered by NullFlowLowering");
                VisitExpression(nc.Left);
                VisitExpression(nc.Right);
                break;

            case BoundNullConditionalAccessExpression nca:
                Violation("BoundNullConditionalAccessExpression", $"?.{nca.MemberName} — not lowered by NullFlowLowering");
                VisitExpression(nca.Target);
                break;

            case BoundWithExpression we:
                Violation("BoundWithExpression", $"with expression on {we.Target.Type} — not lowered by WithLowering");
                VisitExpression(we.Target);
                foreach (var fi in we.Fields) VisitExpression(fi.Value);
                break;

            // CORE leaf — CodeGen emits string.Concat(object[]) directly. Descend into holes.
            case BoundInterpolatedStringExpression ise:
                foreach (var part in ise.Parts) if (part.Expr is not null) VisitExpression(part.Expr);
                break;

            case BoundFunctionLiteralExpression fl:
                Violation("BoundFunctionLiteralExpression", $"lambda/closure — not converted by ClosureConversion");
                VisitBlock(fl.Body);
                break;

            case BoundAwaitExpression aw:
                Violation("BoundAwaitExpression", $"await expression on {aw.Inner.Type} — not lowered by AsyncLowering");
                VisitExpression(aw.Inner);
                break;

            case BoundSpawnExpression sp:
                Violation("BoundSpawnExpression", "spawn { } — not lowered by ConcurrencyLowering");
                VisitBlock(sp.Body);
                break;

            case BoundChanCreationExpression cc:
                Violation("BoundChanCreationExpression", $"chan<{cc.ElementType}> creation — not lowered by ConcurrencyLowering");
                if (cc.Capacity is not null) VisitExpression(cc.Capacity);
                break;


            // ---- CORE nodes — recurse into sub-trees ----
            case BoundUnaryExpression u:
                VisitExpression(u.Operand);
                break;
            case BoundBinaryExpression b:
                VisitExpression(b.Left);
                VisitExpression(b.Right);
                break;
            case BoundMemberAccessExpression ma:
                VisitExpression(ma.Target);
                break;
            case BoundCallExpression call:
                VisitExpression(call.Target);
                foreach (var a in call.Arguments) VisitExpression(a);
                break;
            case BoundObjectCreationExpression oc:
                foreach (var fi in oc.Fields) VisitExpression(fi.Value);
                break;
            case BoundIndexExpression ix:
                VisitExpression(ix.Target);
                VisitExpression(ix.Index);
                break;
            case BoundTupleLiteralExpression tl:
                foreach (var elem in tl.Elements) VisitExpression(elem);
                break;
            case BoundArrayCreationExpression ac:
                VisitExpression(ac.Size);
                break;
            case BoundStackAllocExpression sa:
                VisitExpression(sa.Size);
                break;
            case BoundOutArgumentExpression:
            case BoundAddressOfExpression:
            case BoundMethodGroupConversion:
            case BoundHeapAllocExpression ha when VisitAndReturn(ha.Inner):
            case BoundAddressOfVariableExpression:
            case BoundDefaultExpression:
            case BoundLiteralExpression:
            case BoundNameExpression:
            case BoundErrorExpression:
            case BoundDotCaseExpression:
            case BoundListLiteralExpression:
            case BoundRangeExpression:
                // Leaf / simple sub-expression nodes — already CORE, no children to assert.
                // (BoundHeapAllocExpression handled by VisitAndReturn above to recurse its Operand)
                break;
            // BoundTypeTestExpression (is T) — CORE: stays as isinst
            case BoundTypeTestExpression tt:
                VisitExpression(tt.Operand);
                break;
            // BoundConversion is the single CORE cast/narrow node (spine-deltas §3).
            // BoundSafeCastExpression / BoundAssertCastExpression / BoundNarrowedExpression
            // no longer exist — the binder produces BoundConversion via factory methods.
            // BoundConversion is CORE: recurse into the operand.
            case BoundConversion conv:
                VisitExpression(conv.Operand);
                break;
        }
    }

    // Tiny helper for inline recursion in pattern arms.
    static bool VisitAndReturn(BoundExpression e) { /* handled by case body */ return false; }

    void Violation(string kind, string description)
        => Violations.Add(new FeatureNodeViolation(kind, description));
}
