using Esharp.BoundTree;

namespace Esharp.Lowering;

/// <summary>
/// Lowers <c>let name = expr else { block }</c> (<see cref="BoundLetGuard"/>) into a nil-guard:
/// <code>
///   let __g = expr             // the optional initializer
///   if __g is nil { block }    // the else block — binder-guaranteed to diverge
///   let name = unwrap(__g)     // non-nil past the guard
/// </code>
/// The init and guard are hoisted; the binding itself is <em>returned</em> so the bound
/// <c>name</c> stays in the enclosing scope (wrapping all three in a block would scope
/// <c>name</c> to that block and break every later reference).
///
/// The nil test goes through <see cref="Synth.IsNil"/>, which is value/reference correct — a
/// value-type <c>Nullable&lt;T&gt;</c> is tested with <c>!HasValue</c>, never an always-false
/// reference compare; a reference nullable is tested against <c>(object)null</c>. The unwrap is
/// <c>get_Value</c> for a value nullable and identity for a reference one.
/// </summary>
public sealed class LetGuardLowering : IBoundTreePass
{
    public static readonly LetGuardLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
        => LoweringDriver.MapBodies(program, new LetGuardRewriter());
}

sealed class LetGuardRewriter : SpillingBoundTreeRewriter
{
    protected override BoundStatement RewriteLetGuard(BoundLetGuard node)
    {
        var initType = node.Initializer.Type;            // the optional (value- or ref-nullable)
        var init     = RewriteExpression(node.Initializer);
        var elseBody = (BoundBlockStatement)RewriteStatement(node.ElseBody);

        var tmp    = FreshTemp("guard");
        var tmpRef = Synth.Name(tmp, initType);
        Hoist(Synth.Let(tmp, initType, init));

        // if __g is nil { else }   (else diverges — enforced by the binder, not rechecked)
        Hoist(Synth.If(Synth.IsNil(tmpRef), elseBody) with { Span = node.Span });

        // let name = unwrap(__g)   — the post-guard binding stays in the enclosing scope.
        var innerType = node.DeclaredType is NullableType nt ? nt.Inner : node.DeclaredType;
        return Synth.Let(node.Name, innerType, Synth.Unwrap(tmpRef)) with { Span = node.Span };
    }
}
