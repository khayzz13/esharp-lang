using Esharp.BoundTree;

namespace Esharp.Lowering;

/// <summary>
/// Lowers <c>expr with { f: v, … }</c> (<see cref="BoundWithExpression"/>) — the value-semantic
/// non-destructive update — into a copy + per-field store:
/// <code>
///   var __with = expr        // a mutable copy of the source value
///   __with.f = v             // one store per override (source values lowered first)
///   __with                   // the expression's value is the modified copy
/// </code>
/// The copy and stores are <see cref="SpillingBoundTreeRewriter.Hoist">hoisted</see> before the
/// enclosing statement (or into the right branch when the <c>with</c> sits in a conditionally-
/// evaluated position). Total descent is inherited, so a <c>with</c> nested in a lambda body,
/// a ternary branch, a tuple, or a constructor argument is lowered. CodeGen emits the copy as
/// <c>ldobj/stloc</c> and each store as <c>stfld</c>.
/// </summary>
public sealed class WithLowering : IBoundTreePass
{
    public static readonly WithLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
        => LoweringDriver.MapBodies(program, new WithRewriter());
}

sealed class WithRewriter : SpillingBoundTreeRewriter
{
    protected override BoundExpression RewriteWithExpression(BoundWithExpression node)
    {
        var srcType = node.Target.Type;
        var src     = RewriteExpression(node.Target);   // may itself be a nested `with`

        var name    = FreshTemp("with");
        var tempRef = Synth.Name(name, srcType);
        Hoist(Synth.Var(name, srcType, src));           // a fresh mutable copy

        foreach (var f in node.Fields)
        {
            var value = RewriteExpression(f.Value);
            // The member-access type is informational — CodeGen resolves the field (including an
            // embedded field overridden by its type name) by name on the copy's struct type.
            Hoist(Synth.Assign(Synth.Member(tempRef, f.Name, f.Value.Type), value));
        }

        return tempRef with { Span = node.Span };
    }
}
