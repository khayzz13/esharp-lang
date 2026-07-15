using Esharp.BoundTree;

namespace Esharp.Lowering;

/// <summary>
/// Lowers <see cref="BoundRaiseStatement"/> (<c>raise Name(args)</c>) into the null-safe
/// capture-then-invoke an event fire compiles to:
/// <code>
///   let __h = self.&lt;Name&gt;      // snapshot the backing delegate (one read)
///   if __h != nil { __h(args) }     // invoke only when there are subscribers
/// </code>
/// The snapshot guards against a subscriber unsubscribing between the test and the call.
/// The backing field, the <c>add_</c>/<c>remove_</c> accessors, and the <c>EventDefinition</c>
/// are CodeGen's domain (<c>EmitEventMember</c>); this pass owns <em>only</em> the
/// <c>raise</c> rewrite — the clean split that fixed the duplicate-accessor / AmbiguousMatch bug
/// the old shared-emit design produced.
///
/// <para>The snapshot is typed as the delegate (a reference type), so the presence test is a
/// reference compare against <c>(object)null</c> — never a value-type <c>HasValue</c> — and the
/// invoke needs no cast. Total descent reaches a <c>raise</c> nested in a lambda body.</para>
/// </summary>
public sealed class EventLowering : IBoundTreePass
{
    public static readonly EventLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
        => LoweringDriver.MapBodies(program, new RaiseRewriter());
}

sealed class RaiseRewriter : LoweringRewriter
{
    protected override BoundStatement RewriteRaiseStatement(BoundRaiseStatement node)
    {
        var args         = RewriteExpressions(node.Arguments);
        var delegateType = node.EventType;

        var handlerName = FreshTemp("evt");
        var handlerRef  = Synth.Name(handlerName, delegateType);

        // self.<EventName> — the backing field CodeGen emits under the event name. The
        // "__self__" sentinel tells MethodBodyEmitter's field resolver to treat the receiver as
        // `this` and resolve the field on the declaring type.
        var backing = Synth.Member(Synth.Name("self", new ExternalType("__self__")), node.EventName, delegateType);
        var snapshot = Synth.Let(handlerName, delegateType, backing);

        // if __h != nil { __h(args) }
        var invoke = Synth.Call(handlerRef, args, Synth.Void);
        var guard  = Synth.If(Synth.IsPresent(handlerRef),
            Synth.Block(new BoundExpressionStatement(invoke)));

        return Synth.Block(snapshot, guard) with { Span = node.Span };
    }
}
