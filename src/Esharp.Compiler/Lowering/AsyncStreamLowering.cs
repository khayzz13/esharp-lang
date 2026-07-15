using Esharp.BoundTree;
using Esharp.Symbols;

namespace Esharp.Lowering;

/// <summary>
/// Post-bind lowering pass for async-stream functions (return type
/// <c>IAsyncEnumerable&lt;T&gt;</c>, <see cref="AsyncReturnShape.AsyncEnumerable"/>).
///
/// <para>
/// An async-stream function binds each source <c>yield</c> as a typed
/// <see cref="BoundYieldStatement"/>. This pass owns the structural split into a
/// private async producer and a public synchronous wrapper, replacing every yield in
/// the producer with an awaited <c>ChanOps.SendAsync</c>. Keeping that split after
/// binding preserves the resolved element type and makes invalid yield placement a
/// normal binder diagnostic rather than a parser-side special case.
/// </para>
///
/// <para>
/// The pass also ensures that the wrapper's return type carries the full
/// <c>IAsyncEnumerable&lt;T&gt;</c> ExternalType that <see cref="IteratorLowering"/>
/// (the next pass) needs to detect and further lower. If the wrapper's body already
/// calls <c>AsyncStream.Create&lt;T&gt;</c>, IteratorLowering will replace that call
/// with a direct struct-init of the synthesized iterator struct.
/// </para>
/// </summary>
public sealed class AsyncStreamLowering : IBoundTreePass
{
    public static readonly AsyncStreamLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
    {
        var changed  = false;
        var newUnits = new List<BoundCompilationUnit>(program.Units.Count);

        foreach (var unit in program.Units)
        {
            var newUnit = LowerUnit(unit, sink);
            newUnits.Add(newUnit);
            if (!ReferenceEquals(newUnit, unit)) changed = true;
        }

        return changed ? program with { Units = newUnits } : program;
    }

    static BoundCompilationUnit LowerUnit(BoundCompilationUnit unit, SynthesizedSymbolSink sink)
    {
        var newMembers = new List<BoundMember>(unit.Members.Count + 4);
        var changed    = false;

        foreach (var member in unit.Members)
        {
            if (member is BoundFunctionDeclaration fn
                && fn.AsyncShape == AsyncReturnShape.AsyncEnumerable
                && !IsAlreadyDesugared(fn))
            {
                var (producer, wrapper) = SplitAsyncStream(fn, sink);
                newMembers.Add(producer);
                newMembers.Add(wrapper);
                changed = true;
            }
            else
            {
                newMembers.Add(member);
            }
        }

        return changed ? unit with { Members = newMembers } : unit;
    }

    /// A stream wrapper has no async producer body. Source stream functions carry
    /// HasAwait because every BoundYieldStatement becomes an awaited back-pressure
    /// write, even when the source contains no explicit await.
    static bool IsAlreadyDesugared(BoundFunctionDeclaration fn) =>
        !fn.HasAwait;

    /// Split an unsplit async-stream function into:
    ///   - A private async producer: same params + (Chan&lt;T&gt; __w), body = original body
    ///     with every <see cref="BoundYieldStatement"/> rewritten to
    ///     <c>await ChanOps.SendAsync(__w, v, __ct)</c>.
    ///   - A sync public wrapper: same signature, body = AsyncStream.Create&lt;T&gt;(lambda).
    ///
    static (BoundFunctionDeclaration Producer, BoundFunctionDeclaration Wrapper)
        SplitAsyncStream(BoundFunctionDeclaration fn, SynthesizedSymbolSink sink)
    {
        var elemType     = ElementType(fn.ReturnType);
        var producerName = $"__stream_{fn.Name}";

        var chanType = new ExternalType("Chan",              [elemType]);
        var ctType   = new ExternalType("CancellationToken");
        var taskType = new ExternalType("ValueTask");

        var yieldedBody = (BoundBlockStatement)new YieldToWriteRewriter(chanType, ctType)
            .RewriteStatement(fn.Body);
        // The iterator drains the producer's Chan<T> directly. Completion is part of
        // the normal producer path: without it, a drained producer leaves the
        // enumerator waiting forever after its final value.
        var completeCall = new BoundCallExpression(
            new BoundMemberAccessExpression(
                new BoundNameExpression("ChanOps", new ExternalType("ChanOps")),
                "Complete",
                new VoidType()),
            [new BoundNameExpression("__w", chanType)],
            new VoidType())
        { ExplicitTypeArguments = [elemType] };
        var producerStatements = new List<BoundStatement>(yieldedBody.Statements)
        {
            new BoundExpressionStatement(completeCall),
        };
        var producerBody = new BoundBlockStatement(producerStatements) { Span = fn.Body.Span };

        // Producer: original params + (__w: Chan<T>, __ct: CancellationToken)
        var producerParams = new List<BoundParameter>(fn.Parameters)
        {
            new("__w",  chanType, ByRef: false),
            new("__ct", ctType,   ByRef: false),
        };

        // Register the producer as a synthesized method.
        var producerSym = sink.SynthesizeMethod(
            name:          producerName,
            isAsync:       true,
            returnType:    taskType,
            paramCount:    producerParams.Count,
            body:          producerBody,
            declaringType: fn.Symbol?.DeclaringType);

        var producer = new BoundFunctionDeclaration(
            IsPublic:       false,
            Name:           producerName,
            TypeParameters: fn.TypeParameters,
            Parameters:     producerParams,
            ReturnType:     taskType,
            Body:           producerBody,
            Attributes:     [],
            HasAwait:       true,
            AsyncShape:     AsyncReturnShape.Task)
        { Symbol = producerSym };

        // IteratorLowering consumes this wrapper immediately and replaces its body with
        // direct iterator-struct construction. Keep an inert typed placeholder here:
        // manufacturing an AsyncStream.Create lambda duplicates the producer path and
        // crosses the active stdlib/runtime Chan<T> boundary before IteratorLowering owns it.
        var wrapperBody = new BoundBlockStatement([
            new BoundReturnStatement(new BoundDefaultExpression(fn.ReturnType))])
            { Span = fn.Body.Span };

        var wrapper = new BoundFunctionDeclaration(
            IsPublic:       fn.IsPublic,
            Name:           fn.Name,
            TypeParameters: fn.TypeParameters,
            Parameters:     fn.Parameters,
            ReturnType:     fn.ReturnType,
            Body:           wrapperBody,
            Attributes:     fn.Attributes,
            HasAwait:       false,
            AsyncShape:     AsyncReturnShape.AsyncEnumerable)
        { Symbol = fn.Symbol };

        return (producer, wrapper);
    }

    static BoundType ElementType(BoundType returnType) =>
        returnType is ExternalType { TypeArguments: { Count: > 0 } args }
            ? args[0]
            : new ExternalType("object");
}

/// Rewrites the typed yield surface into the active channel runtime's generic dispatch
/// helper. The generic argument is supplied structurally from the declared stream return
/// type, so the call stays aligned with either the E# stdlib Chan<T> or the seed runtime.
sealed class YieldToWriteRewriter(ExternalType chanType, ExternalType cancellationTokenType) : BoundTreeRewriter
{
    protected override BoundStatement RewriteYieldStatement(BoundYieldStatement node)
    {
        var value = RewriteExpression(node.Value);
        var valueTaskType = new ExternalType("ValueTask");
        var taskType = new ExternalType("Task");
        var send = new BoundCallExpression(
            new BoundMemberAccessExpression(
                new BoundNameExpression("ChanOps", new ExternalType("ChanOps")),
                "SendAsync",
                valueTaskType),
            [
                new BoundNameExpression("__w", chanType),
                value,
                new BoundNameExpression("__ct", cancellationTokenType),
            ],
            valueTaskType)
        { ExplicitTypeArguments = [chanType.TypeArgs[0]] };
        var call = new BoundCallExpression(
            new BoundMemberAccessExpression(send, "AsTask", taskType), [], taskType);

        return new BoundExpressionStatement(new BoundAwaitExpression(call, new VoidType()))
        { Span = node.Span };
    }
}
