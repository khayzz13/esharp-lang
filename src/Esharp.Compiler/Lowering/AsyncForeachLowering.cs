using Esharp.BoundTree;

namespace Esharp.Lowering;

/// <summary>
/// Lowers <c>await for v in src { body }</c> — a <see cref="BoundForEachStatement"/> with
/// <c>IsAwait</c> — into the async-enumerator drain, scoped in a block so the enumerator and its
/// disposal are loop-local:
/// <code>
///   {
///     let __ae = src.GetAsyncEnumerator(CancellationToken.None)
///     defer { __ae.DisposeAsync().AsTask().GetAwaiter().GetResult() }   // observed synchronously
///     while true {
///       let __hasNext = await __ae.MoveNextAsync()
///       if (__hasNext == false) break
///       let v = __ae.Current
///       body
///     }
///   }
/// </code>
/// The <c>await</c> and the <c>defer</c> are CORE forms the downstream passes finish:
/// <see cref="DeferLowering"/> turns the defer into a loop-scoped try/finally and
/// <see cref="AsyncLowering"/> threads the awaits into the state machine. This pass runs early
/// (before the sync <see cref="ForEachLowering"/>), so a sync <c>for</c> never reaches the async
/// drain and an <c>await for</c> never reaches the sync one.
/// </summary>
public sealed class AsyncForeachLowering : IBoundTreePass
{
    public static readonly AsyncForeachLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
        => LoweringDriver.MapBodies(program, new AsyncForeachRewriter());
}

sealed class AsyncForeachRewriter : LoweringRewriter
{
    protected override BoundStatement RewriteForEachStatement(BoundForEachStatement node)
    {
        if (!node.IsAwait)
            return base.RewriteForEachStatement(node);   // sync for-in is ForEachLowering's job

        var collection = RewriteExpression(node.Collection);
        var body       = (BoundBlockStatement)RewriteStatement(Synth.AsBlock(node.Body));

        var elementType     = node.ElementType;
        var enumeratorType  = new ExternalType("IAsyncEnumerator", [elementType]);
        var valueTaskBool   = new ExternalType("ValueTask", [Synth.Bool]);
        var valueTaskType   = new ExternalType("ValueTask");
        var taskType        = new ExternalType("Task");
        var taskAwaiterType = new ExternalType("System.Runtime.CompilerServices.TaskAwaiter");
        var ctType          = new ExternalType("CancellationToken");

        var enumName = FreshTemp("asyncEnum");
        var enumRef  = Synth.Name(enumName, enumeratorType);

        // The BCL exposes an optional CancellationToken parameter, but optional-parameter
        // metadata is not part of E#'s external method binding contract. Pass it explicitly
        // so this works for both BCL producers and E#-emitted iterator implementations.
        var ctNone = Synth.Member(Synth.Name("CancellationToken", ctType), "None", ctType);
        var enumDecl = Synth.Let(enumName, enumeratorType,
            Synth.Call(Synth.Member(collection, "GetAsyncEnumerator", enumeratorType), [ctNone], enumeratorType));

        // defer { __ae.DisposeAsync().AsTask().GetAwaiter().GetResult() }
        var disposeAsync = Synth.Call(Synth.Member(enumRef, "DisposeAsync", valueTaskType), [], valueTaskType);
        var asTask       = Synth.Call(Synth.Member(disposeAsync, "AsTask", taskType), [], taskType);
        var getAwaiter   = Synth.Call(Synth.Member(asTask, "GetAwaiter", taskAwaiterType), [], taskAwaiterType);
        var getResult    = Synth.Call(Synth.Member(getAwaiter, "GetResult", Synth.Void), [], Synth.Void);
        var dispose      = new BoundDeferStatement(Synth.Block(new BoundExpressionStatement(getResult)));

        // Keep awaits in statement positions. AsyncLowering deliberately only consumes await
        // expressions from the statement forms it can split into state-machine resumes; an
        // await directly in a while condition would survive to CodeGen. A `while true` with an
        // awaited per-iteration test has the same control semantics and preserves body breaks.
        var moveNextName = FreshTemp("asyncHasNext");
        var moveNext     = Synth.Call(Synth.Member(enumRef, "MoveNextAsync", valueTaskBool), [], valueTaskBool);
        var current      = Synth.Member(enumRef, "Current", elementType);
        var loopStmts = new List<BoundStatement>
        {
            Synth.Let(moveNextName, Synth.Bool, new BoundAwaitExpression(moveNext, Synth.Bool)),
            new BoundIfStatement(
                new BoundBinaryExpression(
                    Synth.Name(moveNextName, Synth.Bool), SyntaxTokenKind.EqualsEquals,
                    Synth.BoolLit(false), Synth.Bool),
                new BoundBreakStatement(),
                Else: null),
            Synth.Let(node.Identifier, elementType, current),
        };
        loopStmts.AddRange(body.Statements);
        var loop = new BoundWhileStatement(Synth.BoolLit(true), Synth.Block(loopStmts));

        return Synth.Block(enumDecl, dispose, loop) with { Span = node.Span };
    }

}
