// Moved to Esharp.Binder (Pillar 2 — B2) per module-map.md.
// Async binding only — state-machine synthesis is Pillar 3 AsyncLowering.
// This file classifies the declared return type into AsyncReturnShape and detects
// whether a body awaits at its own callable level, unchanged.
namespace Esharp.Binder;

/// The async-binding slice — the single home for how `await` shapes a callable.
///
/// E# async is *uncolored*: `await` alone makes a function (or a function literal)
/// asynchronous, and the DECLARED return type — never an `async` keyword — selects the
/// CLR state-machine wrapper. Those rules are identical for a free function, a method,
/// and a lambda, so they are centralized here rather than duplicated across
/// <see cref="DeclarationBinder"/> and the lambda path. The IL emitter also consumes
/// <see cref="ClassifyReturn"/> to unwrap a lambda's wrapper return into the builder's
/// result type, so this is the one authority both backends agree on.
///
/// Intentionally small — a seed. Async-binding concerns migrate here over time
/// (await detection, colorless-call shaping, the `?`-under-async routing) as each is
/// touched, so the slice grows without a flag-day refactor.
public static class AsyncBinder
{
    /// Split a declared async return type into its (result type, wrapper shape):
    /// <list type="bullet">
    /// <item><c>-&gt; Task&lt;T&gt;</c> → (T, Task)</item>
    /// <item><c>-&gt; Task</c> → (void, Task)</item>
    /// <item><c>-&gt; ValueTask&lt;T&gt;</c> → (T, ValueTask)</item>
    /// <item><c>-&gt; ValueTask</c> → (void, ValueTask)</item>
    /// <item>explicit <c>-&gt; void</c> → (void, Void) — async-void, for event handlers</item>
    /// <item>bare <c>-&gt; T</c> / omitted → (T, ValueTask) — the uncolored default</item>
    /// </list>
    /// In every case the body returns the unwrapped value; only the wrapper differs.
    /// Wrapper shapes are matched structurally off the bound type's args — nothing
    /// re-parses a rendered name.
    public static (BoundType Result, AsyncReturnShape Shape) ClassifyReturn(BoundType rt, bool explicitReturn)
    {
        if (rt is VoidType)
            return (rt, explicitReturn ? AsyncReturnShape.Void : AsyncReturnShape.ValueTask);
        return rt switch
        {
            ExternalType { Name: "Task", TypeArgs: [] } => (new VoidType(), AsyncReturnShape.Task),
            ExternalType { Name: "ValueTask", TypeArgs: [] } => (new VoidType(), AsyncReturnShape.ValueTask),
            ExternalType { Name: "Task", TypeArgs: [var taskResult] } => (taskResult, AsyncReturnShape.Task),
            ExternalType { Name: "ValueTask", TypeArgs: [var vtResult] } => (vtResult, AsyncReturnShape.ValueTask),
            _ => (rt, AsyncReturnShape.ValueTask),
        };
    }

    /// Whether a bound block awaits at THIS callable's own level — an `await` (or an
    /// `async let`, which lowers to one) that is NOT nested inside a deeper function
    /// literal, since a nested lambda owns its own state machine. This is the bound-tree
    /// analogue of <see cref="DeclarationBinder.FunctionBodyHasAwait"/>: the lambda binder
    /// runs it after binding the body so an awaiting lambda is annotated async at the
    /// point its type is known.
    public static bool BodyAwaitsAtThisLevel(BoundBlockStatement body)
    {
        var found = false;
        WalkStatement(body, ref found);
        return found;
    }

    static void WalkStatement(BoundStatement? stmt, ref bool found)
    {
        if (found || stmt is null) return;
        switch (stmt)
        {
            case BoundBlockStatement b:
                foreach (var s in b.Statements) WalkStatement(s, ref found);
                break;
            case BoundVariableDeclaration v:
                WalkExpr(v.Initializer, ref found);
                break;
            case BoundExpressionStatement e:
                WalkExpr(e.Expression, ref found);
                break;
            case BoundReturnStatement r:
                WalkExpr(r.Expression, ref found);
                break;
            case BoundIfStatement i:
                WalkExpr(i.Condition, ref found);
                WalkStatement(i.Then, ref found);
                WalkStatement(i.Else, ref found);
                break;
            case BoundWhileStatement w:
                WalkExpr(w.Condition, ref found);
                WalkStatement(w.Body, ref found);
                break;
            case BoundAssignment a:
                WalkExpr(a.Value, ref found);
                break;
            case BoundCompoundAssignment ca:
                WalkExpr(ca.Value, ref found);
                break;
            case BoundMatchStatement m:
                WalkExpr(m.Subject, ref found);
                foreach (var arm in m.Arms) WalkStatement(arm.Body, ref found);
                break;
            case BoundForEachStatement fe:
                WalkExpr(fe.Collection, ref found);
                WalkStatement(fe.Body, ref found);
                break;
            case BoundTryStatement tr:
                WalkStatement(tr.Body, ref found);
                foreach (var c in tr.Catches) WalkStatement(c.Body, ref found);
                break;
            case BoundDeferStatement d:
                WalkStatement(d.Body, ref found);
                break;
            case BoundThrowStatement th:
                WalkExpr(th.Expression, ref found);
                break;
        }
    }

    static void WalkExpr(BoundExpression? expr, ref bool found)
    {
        if (found || expr is null) return;
        switch (expr)
        {
            case BoundAwaitExpression:
                found = true;
                break;
            // A nested function literal owns its own state machine — do NOT descend,
            // so a lambda's await never colors the lambda that encloses it.
            case BoundFunctionLiteralExpression:
                break;
            case BoundCallExpression call:
                WalkExpr(call.Target, ref found);
                foreach (var a in call.Arguments) WalkExpr(a, ref found);
                break;
            case BoundBinaryExpression bin:
                WalkExpr(bin.Left, ref found);
                WalkExpr(bin.Right, ref found);
                break;
            case BoundUnaryExpression u:
                WalkExpr(u.Operand, ref found);
                break;
            case BoundMemberAccessExpression ma:
                WalkExpr(ma.Target, ref found);
                break;
        }
    }
}
