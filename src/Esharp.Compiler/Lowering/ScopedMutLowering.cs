using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Symbols;

namespace Esharp.Lowering;

/// <summary>
/// Lowers a scoped property <c>mut</c> borrow at the use site. A scoped accessor
/// cannot be a CLR ref-return: its resume code must run after the borrower has
/// finished, including when that borrower throws. The transform is therefore:
///
/// <code>
/// consume(&owner.value)
///
/// var receiver = owner
/// setup(receiver)
/// try { consume(&working) }
/// finally { resume(receiver) }
/// </code>
///
/// The bound address marker is rejected everywhere else, before CodeGen can
/// mistake it for a durable property location.
/// </summary>
public sealed class ScopedMutLowering : IBoundTreePass
{
    public static readonly ScopedMutLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
        => LoweringDriver.MapBodies(program, new ScopedMutRewriter(program.Data.Diagnostics));
}

sealed class ScopedMutRewriter(DiagnosticBag diagnostics) : SpillingBoundTreeRewriter
{
    internal string FreshScopedLocal(string sourceName) => FreshTemp("mut_" + sourceName);

    protected override BoundStatement RewriteExpressionStatement(BoundExpressionStatement node)
    {
        if (node.Expression is BoundCallExpression call
            && call.Type is VoidType
            && TryRewriteCall(call, valueContext: false, out var rewritten, out var statements))
        {
            if (statements is null)
                return new BoundExpressionStatement(rewritten) { Span = node.Span };
            return new BoundBlockStatement(statements) { Span = node.Span };
        }
        return base.RewriteExpressionStatement(node);
    }

    protected override BoundExpression RewriteCallExpression(BoundCallExpression node)
    {
        if (TryRewriteCall(node, valueContext: true, out var rewritten, out var statements))
        {
            if (statements is null) return rewritten;
            foreach (var statement in statements) Hoist(statement);
            return rewritten;
        }
        return base.RewriteCallExpression(node);
    }

    protected override BoundExpression RewriteAddressOfVariableExpression(BoundAddressOfVariableExpression node)
    {
        if (!node.IsScopedPropertyBorrow) return base.RewriteAddressOfVariableExpression(node);
        if (node.HasDurablePropertyFallback)
            return node with
            {
                Target = RewriteExpression(node.Target),
                IsScopedPropertyBorrow = false,
                HasDurablePropertyFallback = false,
            };
        diagnostics.Report(node.Span,
            "ES2215: a scoped `mut` property location may only be passed directly to one borrowing call; it cannot be returned, stored, captured, or otherwise escape its resume region.");
        return new BoundErrorExpression();
    }

    bool TryRewriteCall(BoundCallExpression original, bool valueContext,
        out BoundExpression rewritten, out List<BoundStatement>? statements)
    {
        rewritten = original;
        statements = null;

        // Recurse normally except for the direct scoped-borrow argument. Rewriting
        // that argument first would correctly reject it as an escape, before this
        // call-level pass had a chance to introduce its protected region.
        var target = RewriteExpression(original.Target);
        var args = new BoundExpression[original.Arguments.Count];
        var scopedIndex = -1;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = original.Arguments[i];
            if (arg is BoundAddressOfVariableExpression { IsScopedPropertyBorrow: true })
            {
                if (scopedIndex >= 0)
                {
                    diagnostics.Report(arg.Span,
                        "ES2216: one call may borrow at most one scoped `mut` property. Split the calls so each lend has an unambiguous resume boundary.");
                    return true;
                }
                scopedIndex = i;
                args[i] = arg;
            }
            else
                args[i] = RewriteExpression(arg);
        }

        var call = original with { Target = target, Arguments = args };
        if (scopedIndex < 0)
        {
            rewritten = call;
            return !ReferenceEquals(call, original);
        }

        if (args[scopedIndex] is not BoundAddressOfVariableExpression
            {
                Target: BoundMemberAccessExpression { Member: FieldSymbol property } member
            })
        {
            diagnostics.Report(args[scopedIndex].Span,
                "ES2217: scoped property borrow lost its property capability before lowering.");
            rewritten = new BoundErrorExpression();
            return true;
        }

        if (valueContext && call.Type is VoidType)
        {
            diagnostics.Report(call.Span,
                "ES2218: a void call borrowing a scoped `mut` property is only valid as a statement.");
            rewritten = new BoundErrorExpression();
            return true;
        }

        if (call.Type is VoidType)
        {
            statements = [new BoundScopedMutCall(member.Target, call, scopedIndex, property)];
            rewritten = new BoundErrorExpression(); // ignored by statement replacement
            return true;
        }

        var resultName = FreshTemp("mutResult");
        var result = new BoundNameExpression(resultName, call.Type);
        statements =
        [
            Synth.Var(resultName, call.Type, Synth.Default(call.Type)),
            new BoundScopedMutCall(member.Target, call, scopedIndex, property, resultName),
        ];
        rewritten = result;
        return true;
    }
}
