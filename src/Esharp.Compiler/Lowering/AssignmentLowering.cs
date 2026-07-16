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
            SyntaxTokenKind.PercentEquals => SyntaxTokenKind.Percent,
            SyntaxTokenKind.AmpersandEquals => SyntaxTokenKind.Ampersand,
            SyntaxTokenKind.PipeEquals => SyntaxTokenKind.Pipe,
            SyntaxTokenKind.CaretEquals => SyntaxTokenKind.Caret,
            SyntaxTokenKind.ShiftLeftEquals => SyntaxTokenKind.ShiftLeft,
            SyntaxTokenKind.ShiftRightEquals => SyntaxTokenKind.ShiftRight,
            SyntaxTokenKind.UnsignedShiftRightEquals => SyntaxTokenKind.UnsignedShiftRight,
            _ => throw new InvalidOperationException($"non-compound operator '{node.Op}' on BoundCompoundAssignment"),
        };

        var combined = node.Combined is { } resolved
            ? resolved with { Left = stable, Right = value }
            : new BoundBinaryExpression(stable, binOp, value, stable.Type);
        return Synth.Assign(stable, combined) with { Span = node.Span };
    }

    // Return a target whose receiver and index evaluate exactly once. Both the read and write
    // use the same synthesized temporaries; this matters for getBox().field += value as well as
    // getArray()[nextIndex()] += value.
    BoundExpression StabilizeTarget(BoundExpression target)
    {
        if (target is BoundMemberAccessExpression member && SideEffects(member.Target))
        {
            var name = FreshTemp("compRecv");
            Hoist(Synth.Let(name, member.Target.Type, member.Target));
            return member with { Target = Synth.Name(name, member.Target.Type) };
        }

        if (target is BoundIndexExpression idx)
        {
            var stableTarget = idx.Target;
            var stableIndex = idx.Index;
            if (SideEffects(stableTarget))
            {
                var name = FreshTemp("compRecv");
                Hoist(Synth.Let(name, stableTarget.Type, stableTarget));
                stableTarget = Synth.Name(name, stableTarget.Type);
            }
            if (SideEffects(stableIndex))
            {
                var name = FreshTemp("compIdx");
                Hoist(Synth.Let(name, stableIndex.Type, stableIndex));
                stableIndex = Synth.Name(name, stableIndex.Type);
            }
            return idx with { Target = stableTarget, Index = stableIndex };
        }
        return target;
    }

    static bool SideEffects(BoundExpression e) => e switch
    {
        BoundCallExpression or BoundAwaitExpression or BoundObjectCreationExpression => true,
        BoundMemberAccessExpression member => SideEffects(member.Target),
        BoundIndexExpression index => SideEffects(index.Target) || SideEffects(index.Index),
        _ => false,
    };
}
