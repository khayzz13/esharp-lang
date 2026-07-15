using Esharp.BoundTree;
using Esharp.Symbols;
using Esharp.Syntax;

namespace Esharp.Lowering;

/// <summary>
/// Lowers a compound assignment (<see cref="BoundCompoundAssignment"/>) into a plain
/// <see cref="BoundAssignment"/> over a <see cref="BoundBinaryExpression"/>:
/// <code>
///   x += y   →   x = x + y      (and -= / *= / /= correspondingly)
/// </code>
/// A target whose evaluation has a side effect — <c>a[f()] += 1</c> — has its index hoisted into
/// a temp so the effect runs exactly once across the read and the write. Total descent (inherited
/// from <see cref="SpillingBoundTreeRewriter"/>) means a compound assignment inside a lambda body
/// is lowered too — the position the old hand-rolled walk skipped.
/// </summary>
public sealed class AssignmentLowering : IBoundTreePass
{
    public static readonly AssignmentLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
        => LoweringDriver.MapBodies(program, new AssignmentRewriter());
}

sealed class AssignmentRewriter : SpillingBoundTreeRewriter
{
    protected override BoundStatement RewriteCompoundAssignment(BoundCompoundAssignment node)
    {
        var target = RewriteExpression(node.Target);
        var value  = RewriteExpression(node.Value);

        // `event += handler` / `event -= handler` are subscriptions, not read-modify-write
        // field assignments. Lower them to an ordinary call of the generated add_/remove_
        // accessor. Rewriting them to `event = event + handler` reaches delegate arithmetic;
        // preserving BoundCompoundAssignment leaks a FEATURE node into CodeGen.
        if (node.IsEventSubscription
            && target is BoundMemberAccessExpression member
            && node.Op is SyntaxTokenKind.PlusEquals or SyntaxTokenKind.MinusEquals)
        {
            var accessorName = (node.Op == SyntaxTokenKind.PlusEquals ? "add_" : "remove_") + member.MemberName;
            var accessor = new BoundMemberAccessExpression(member.Target, accessorName, Synth.Void);
            return new BoundExpressionStatement(new BoundCallExpression(accessor, [value], Synth.Void))
            {
                Span = node.Span,
            };
        }

        var stable = StabilizeTarget(target);

        var binOp = node.Op switch
        {
            SyntaxTokenKind.PlusEquals  => SyntaxTokenKind.Plus,
            SyntaxTokenKind.MinusEquals => SyntaxTokenKind.Minus,
            SyntaxTokenKind.StarEquals  => SyntaxTokenKind.Star,
            SyntaxTokenKind.SlashEquals => SyntaxTokenKind.Slash,
            _ => throw new InvalidOperationException($"non-compound operator '{node.Op}' on BoundCompoundAssignment"),
        };

        var combined = new BoundBinaryExpression(stable, binOp, value, stable.Type);
        return Synth.Assign(stable, combined) with { Span = node.Span };
    }

    // Return a target that evaluates exactly once. A name/field target is already stable; an
    // indexed target with a side-effecting index has that index spilled into a temp so the read
    // and the write reuse the one evaluation.
    BoundExpression StabilizeTarget(BoundExpression target)
    {
        if (target is BoundIndexExpression idx && SideEffects(idx.Index))
        {
            var name = FreshTemp("compIdx");
            Hoist(Synth.Let(name, idx.Index.Type, idx.Index));
            return idx with { Index = Synth.Name(name, idx.Index.Type) };
        }
        return target;
    }

    static bool SideEffects(BoundExpression e) => e is BoundCallExpression or BoundAwaitExpression;
}
