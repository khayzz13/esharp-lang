using Esharp.BoundTree;

namespace Esharp.Lowering;

/// <summary>
/// Lowers a synchronous binding of an uncolored async call into an eagerly-started
/// ValueTask slot and a blocking join immediately before the first lexical use.
/// The binder has already typed the source local as its eventual value.
/// </summary>
internal sealed class SyncFutureLowering
{
    int _next;

    readonly record struct Pending(string FutureName, BoundType FutureType, BoundType ValueType, bool Mutable);

    public static BoundBlockStatement Rewrite(BoundBlockStatement body) =>
        new SyncFutureLowering().Block(body, new Dictionary<string, Pending>(StringComparer.Ordinal));

    BoundBlockStatement Block(BoundBlockStatement block, Dictionary<string, Pending> inherited)
    {
        // A nested control-flow body receives the futures that are live on entry,
        // but joins made on one branch cannot make a sibling path joined.
        var pending = new Dictionary<string, Pending>(inherited, StringComparer.Ordinal);
        var result = new List<BoundStatement>();
        foreach (var statement in block.Statements)
        {
            if (statement is BoundVariableDeclaration { IsSyncFuture: true } future)
            {
                var futureName = $"__sync_future_{future.Name}_{_next++}";
                result.Add(new BoundVariableDeclaration(future.Mutable, futureName, future.Initializer.Type, future.Initializer));
                pending[future.Name] = new Pending(futureName, future.Initializer.Type, future.DeclaredType, future.Mutable);
                continue;
            }

            var references = new HashSet<string>(StringComparer.Ordinal);
            Collect(statement, references);
            foreach (var name in pending.Keys.Where(references.Contains).ToArray())
            {
                var p = pending[name];
                result.Add(new BoundVariableDeclaration(p.Mutable, name, p.ValueType, Join(p)));
                pending.Remove(name);
            }
            result.Add(RewriteNested(statement, pending));
        }
        return new BoundBlockStatement(result) { Span = block.Span };
    }

    BoundExpression Join(Pending p)
    {
        var future = Synth.Name(p.FutureName, p.FutureType);
        var awaiterType = AsyncLowering.AwaiterBoundType(p.FutureType);
        var awaiter = Synth.Call(Synth.Member(future, "GetAwaiter", awaiterType), [], awaiterType);
        return Synth.Call(Synth.Member(awaiter, "GetResult", p.ValueType), [], p.ValueType);
    }

    BoundStatement RewriteNested(BoundStatement statement, Dictionary<string, Pending> pending) => statement switch
    {
        BoundBlockStatement b => Block(b, pending),
        BoundIfStatement i => i with { Then = RewriteNested(i.Then, pending), Else = i.Else is null ? null : RewriteNested(i.Else, pending) },
        BoundWhileStatement w => w with { Body = RewriteNested(w.Body, pending) },
        BoundForEachStatement f => f with { Body = RewriteNested(f.Body, pending) },
        BoundMatchStatement m => m with { Arms = [.. m.Arms.Select(a => a with { Body = Block(a.Body, pending) })] },
        BoundTryStatement t => t with { Body = Block(t.Body, pending), Catches = [.. t.Catches.Select(c => c with { Body = Block(c.Body, pending) })] },
        _ => statement,
    };

    static void Collect(BoundStatement statement, HashSet<string> names)
    {
        switch (statement)
        {
            case BoundVariableDeclaration v: Expr(v.Initializer, names); break;
            case BoundAssignment a: Expr(a.Target, names); Expr(a.Value, names); break;
            case BoundCompoundAssignment a: Expr(a.Target, names); Expr(a.Value, names); break;
            case BoundExpressionStatement e: Expr(e.Expression, names); break;
            case BoundReturnStatement { Expression: { } e }: Expr(e, names); break;
            case BoundThrowStatement { Expression: { } e }: Expr(e, names); break;
            case BoundIfStatement i: Expr(i.Condition, names); break;
            case BoundWhileStatement w: Expr(w.Condition, names); break;
            case BoundForEachStatement f: Expr(f.Collection, names); break;
            case BoundMatchStatement m: Expr(m.Subject, names); break;
        }
    }

    static void Expr(BoundExpression e, HashSet<string> names)
    {
        switch (e)
        {
            case BoundNameExpression n: names.Add(n.Name); break;
            case BoundUnaryExpression u: Expr(u.Operand, names); break;
            case BoundBinaryExpression b: Expr(b.Left, names); Expr(b.Right, names); break;
            case BoundMemberAccessExpression m: Expr(m.Target, names); break;
            case BoundCallExpression c: Expr(c.Target, names); foreach (var a in c.Arguments) Expr(a, names); break;
            case BoundConversion c: Expr(c.Operand, names); break;
            case BoundConditionalExpression c: Expr(c.Condition, names); Expr(c.Consequence, names); Expr(c.Alternative, names); break;
            case BoundIndexExpression i: Expr(i.Target, names); Expr(i.Index, names); break;
            case BoundObjectCreationExpression o: foreach (var f in o.Fields) Expr(f.Value, names); break;
            case BoundTupleLiteralExpression t: foreach (var x in t.Elements) Expr(x, names); break;
            case BoundListLiteralExpression l: foreach (var x in l.Elements) Expr(x, names); break;
        }
    }
}
