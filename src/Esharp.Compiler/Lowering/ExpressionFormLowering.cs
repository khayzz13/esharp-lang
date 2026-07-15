using Esharp.BoundTree;

namespace Esharp.Lowering;

/// <summary>
/// Lowers the two control-flow-bearing value expressions — <c>if</c>-as-expression and
/// <c>match</c>-as-expression — into a temp filled by the statement form:
/// <code>
///   f(if c { a } else { b })
///   →  var __ifv = default(T); if c { __ifv = a } else { __ifv = b }; f(__ifv)
///
///   let x = match v { .a => 1  .b => 2 }
///   →  var __mv = default(T); match v { .a { __mv = 1 }  .b { __mv = 2 } }; let x = __mv
/// </code>
/// The temp + statement are hoisted before the enclosing statement; each branch/arm <em>value</em>
/// is captured with its own hoists so a <c>?</c> or <c>with</c> in one branch lands inside that
/// branch, never run on the path that doesn't take it. The match statement this produces is then
/// finished by <see cref="MatchLowering"/> (which runs next) and the if statement by the ordinary
/// CORE walk.
///
/// <para>The temp placeholder is a true <c>default(T)</c> (<see cref="Synth.Default"/>) — correct
/// for reference and value-typed and type-parameter results alike — never an empty object-creation,
/// which silently mis-built a reference result.</para>
///
/// <para>Interpolated strings are NOT handled here: a <see cref="BoundInterpolatedStringExpression"/>
/// is a CORE leaf that CodeGen emits directly as <c>string.Concat</c>; the rewriter descends into
/// its holes so any FEATURE inside a hole is still lowered.</para>
/// </summary>
public sealed class ExpressionFormLowering : IBoundTreePass
{
    public static readonly ExpressionFormLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
        => LoweringDriver.MapBodies(program, new ExpressionFormRewriter());
}

sealed class ExpressionFormRewriter : SpillingBoundTreeRewriter
{
    // ── match v { … }  in value position ─────────────────────────────────────────
    protected override BoundExpression RewriteMatchExpression(BoundMatchExpression node)
    {
        var resultType = node.Type;
        var subject    = RewriteExpression(node.Subject);   // evaluated once, unconditionally

        var name    = FreshTemp("matchv");
        var tempRef = Synth.Name(name, resultType);
        Hoist(Synth.Var(name, resultType, Synth.Default(resultType)));

        var arms = new List<BoundMatchArm>(node.Arms.Count);
        foreach (var arm in node.Arms)
        {
            var (value, valueHoists) = RewriteCapturing(arm.Value);   // arm body is conditional
            var guard = arm.Guard is { } g ? RewriteExpression(g) : null;
            var stmts = new List<BoundStatement>(valueHoists) { Synth.Assign(tempRef, value) };
            arms.Add(new BoundMatchArm(arm.Pattern, Synth.Block(stmts), guard));
        }

        Hoist(new BoundMatchStatement(subject, node.SubjectType, arms) { Span = node.Span });
        return tempRef with { Span = node.Span };
    }

    // ── if c { a } else { b }  in value position ─────────────────────────────────
    protected override BoundExpression RewriteIfExpression(BoundIfExpression node)
    {
        var resultType = node.Type;
        var name       = FreshTemp("ifv");
        var tempRef    = Synth.Name(name, resultType);
        Hoist(Synth.Var(name, resultType, Synth.Default(resultType)));

        // Build the chain back-to-front: ... else-if c1 { } else { elseBranch }.
        BoundStatement? chain = BuildElse(node, tempRef);
        for (var i = node.Branches.Count - 1; i >= 0; i--)
        {
            var branch = node.Branches[i];
            var (cond, condHoists) = RewriteCapturing(branch.Condition);
            var body = BuildBranchBody(branch, tempRef);

            BoundStatement ifStmt = Synth.If(cond, body, chain) with { Span = node.Span };
            // A branch condition past the first is itself conditional (it only runs if earlier
            // conditions failed), so its setup nests in the prior branch's else, immediately
            // before the test — never hoisted unconditionally.
            chain = condHoists.Count == 0 ? ifStmt : Synth.Block([.. condHoists, ifStmt]);
        }

        if (chain is not null) Hoist(chain);
        return tempRef with { Span = node.Span };
    }

    // A branch's body statements run, then the branch value is stored into the temp; the value's
    // own hoists stay inside the branch.
    BoundBlockStatement BuildBranchBody(BoundIfExpressionBranch branch, BoundNameExpression tempRef)
    {
        var stmts = new List<BoundStatement>(RewriteStatements(branch.Body));
        if (branch.Value is { } v)
        {
            var (value, valueHoists) = RewriteCapturing(v);
            stmts.AddRange(valueHoists);
            stmts.Add(Synth.Assign(tempRef, value));
        }
        return Synth.Block(stmts);
    }

    BoundStatement? BuildElse(BoundIfExpression node, BoundNameExpression tempRef)
    {
        if (node.ElseValue is { } ev)
        {
            var (value, valueHoists) = RewriteCapturing(ev);
            var stmts = new List<BoundStatement>(valueHoists) { Synth.Assign(tempRef, value) };
            return Synth.Block(stmts);
        }
        if (node.ElseBody is { Count: > 0 })
            return Synth.Block(RewriteStatements(node.ElseBody));
        return null;
    }
}
