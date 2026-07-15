using Esharp.BoundTree;

namespace Esharp.Lowering.AsyncSpill;

/// Per-function-body facts for the async spill pass: an identity-memoized
/// "does this expression contain an await" predicate, plus a monotonic temp-name
/// allocator. One instance per `AsyncSpillLowering.Rewrite` so temp numbering
/// restarts per function and `ContainsAwait` results never leak across bodies.
sealed class AwaitFacts
{
    int _counter;

    /// A fresh spill-temp name. The reserved double-underscore prefix matches the
    /// existing `__async_let_` / `__pending_` conventions, so it cannot collide
    /// with a user identifier. `SpillExpressionRewriter` and the emitter treat any
    /// name with this prefix as a known-pure, already-evaluated temp.
    public const string Prefix = "__spill_";
    public string NextTemp() => Prefix + _counter++;

    // Identity-keyed memo. Records compare structurally by default, but two
    // distinct sub-trees can be structurally equal yet need independent answers
    // only by reference — ReferenceEqualityComparer keeps the cache O(nodes).
    readonly Dictionary<BoundExpression, bool> _cache = new(ReferenceEqualityComparer.Instance);

    /// True when `expr`'s evaluation performs an `await`. Does NOT descend into
    /// lambda or `spawn` bodies — those are separate method/state-machine scopes
    /// whose awaits belong to their own lowering, not the enclosing expression's
    /// evaluation stack.
    public bool ContainsAwait(BoundExpression? expr)
    {
        if (expr is null) return false;
        if (_cache.TryGetValue(expr, out var cached)) return cached;
        var result = Compute(expr);
        _cache[expr] = result;
        return result;
    }

    bool Any(IEnumerable<BoundExpression?> exprs)
    {
        foreach (var e in exprs)
            if (ContainsAwait(e)) return true;
        return false;
    }

    bool Compute(BoundExpression expr) => expr switch
    {
        BoundAwaitExpression => true,

        BoundBinaryExpression b => ContainsAwait(b.Left) || ContainsAwait(b.Right),
        BoundUnaryExpression u => ContainsAwait(u.Operand),
        BoundCallExpression c => ContainsAwait(c.Target) || Any(c.Arguments),
        BoundMemberAccessExpression m => ContainsAwait(m.Target),
        BoundIndexExpression ix => ContainsAwait(ix.Target) || ContainsAwait(ix.Index),
        BoundListLiteralExpression l => Any(l.Elements),
        BoundTupleLiteralExpression t => Any(t.Elements),
        BoundObjectCreationExpression oc => Any(oc.Fields.Select(f => (BoundExpression?)f.Value)),
        BoundWithExpression w => ContainsAwait(w.Target) || Any(w.Fields.Select(f => (BoundExpression?)f.Value)),
        BoundInterpolatedStringExpression s => Any(s.Parts.Select(part => part.Expr)),
        BoundConditionalExpression cnd => ContainsAwait(cnd.Condition) || ContainsAwait(cnd.Consequence) || ContainsAwait(cnd.Alternative),
        BoundNullCoalescingExpression nc => ContainsAwait(nc.Left) || ContainsAwait(nc.Right),
        BoundNullConditionalAccessExpression nca => ContainsAwait(nca.Target),
        BoundRangeExpression r => ContainsAwait(r.Target) || ContainsAwait(r.Start) || ContainsAwait(r.End),
        BoundDotCaseExpression dc => Any(dc.Arguments),
        BoundResultCallExpression rc => ContainsAwait(rc.Argument),
        BoundTryUnwrapExpression tu => ContainsAwait(tu.Inner),
        BoundTypeTestExpression tt => ContainsAwait(tt.Operand),
        BoundConversion cv => ContainsAwait(cv.Operand),
        BoundMatchExpression me => ContainsAwait(me.Subject)
            || me.Arms.Any(a => ContainsAwait(a.Value) || ContainsAwait(a.Guard)),

        // Separate scopes — their awaits are lowered with their own body.
        BoundSpawnExpression => false,
        BoundFunctionLiteralExpression => false,

        // Leaves and await-free terminals.
        _ => false,
    };
}
