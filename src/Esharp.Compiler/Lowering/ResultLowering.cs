using Esharp.BoundTree;

namespace Esharp.Lowering;

/// <summary>
/// Lowers the <c>Result&lt;T,E&gt;</c> surface into CORE nodes:
///
/// <list type="bullet">
///   <item><c>ok(v)</c>    → <c>Result { IsOk = true,  Value = v,         Error = default(E) }</c></item>
///   <item><c>error(e)</c> → <c>Result { IsOk = false, Value = default(T), Error = e }</c></item>
///   <item><c>expr?</c>    → temp + nil-guarded early-return + value projection:
/// <code>
///   let __r = expr
///   if !__r.IsOk { return Result&lt;EnclosingOk,E&gt; { IsOk=false, Value=default, Error=__r.Error } }
///   __r.Value
/// </code></item>
/// </list>
///
/// The <c>?</c> early-return targets the <em>enclosing function's</em> Result type (the error
/// value carries through, the success type re-targets) — which is why the pass is driven with
/// the per-body <see cref="LoweringDriver.MapBodies(BoundProgram, System.Func{BoundType?, BoundTreeRewriter})"/>
/// overload that hands each body its return type. The hoist of the temp + guard rides on
/// <see cref="SpillingBoundTreeRewriter"/>, so a <c>?</c> buried in a conditionally-evaluated
/// position (a ternary branch, a <c>&amp;&amp;</c> arm) lands inside the guard rather than
/// running unconditionally. Total descent is inherited, so an <c>ok</c>/<c>error</c>/<c>?</c>
/// inside a lambda body, a constructor argument, or a tuple is lowered too.
/// </summary>
public sealed class ResultLowering : IBoundTreePass
{
    public static readonly ResultLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
        => LoweringDriver.MapBodies(program, enclosingReturn => new ResultRewriter(enclosingReturn));
}

sealed class ResultRewriter(BoundType? enclosingReturn) : SpillingBoundTreeRewriter
{
    readonly BoundType? _enclosingReturn = enclosingReturn;

    // ok(value) / error(value) → Result struct init (the unused variant slot is default(T)).
    protected override BoundExpression RewriteResultCallExpression(BoundResultCallExpression node)
    {
        var arg = RewriteExpression(node.Argument);
        var resultType = new ResultType(node.OkType, node.ErrorType);

        var fields = node.IsOk
            ? new List<BoundFieldInit>
            {
                new("IsOk",  Synth.BoolLit(true)),
                new("Value", arg),
                new("Error", Synth.Default(node.ErrorType)),
            }
            : new List<BoundFieldInit>
            {
                new("IsOk",  Synth.BoolLit(false)),
                new("Value", Synth.Default(node.OkType)),
                new("Error", arg),
            };

        return new BoundObjectCreationExpression(resultType, fields) { Span = node.Span };
    }

    // expr? — unwrap-or-propagate.
    protected override BoundExpression RewriteTryUnwrapExpression(BoundTryUnwrapExpression node)
    {
        // The operand is evaluated unconditionally, so its own hoists land in the enclosing
        // buffer (before our temp) — a nested `?` resolves first, in source order.
        var inner      = RewriteExpression(node.Inner);
        var resultType = node.Inner.Type;                 // Result<T, E>
        var valueType  = node.UnwrappedType;              // T
        var errorType  = (resultType as ResultType)?.ErrorType ?? new ExternalType("object");

        // Honor the binder's chosen temp name: AsyncSpill keys the state-machine field off it
        // when the `?` sits in an async function, so inventing a fresh name would desync them.
        var tempName = string.IsNullOrEmpty(node.TempName) ? FreshTemp("try") : node.TempName;
        var tempRef  = Synth.Name(tempName, resultType);
        Hoist(Synth.Let(tempName, resultType, inner));

        // A Result-returning function propagates the error in its declared Result shape.
        // In every other return shape, `?` has always been the throw-on-error form (the
        // legacy direct emitter did this too). Returning the operand Result from an `int`
        // function produced invalid IL on the error branch even when a test only exercised
        // the successful path.
        BoundStatement onError;
        if (_enclosingReturn is ResultType propagateType)
        {
            var propagate = new BoundObjectCreationExpression(propagateType,
            [
                new BoundFieldInit("IsOk",  Synth.BoolLit(false)),
                new BoundFieldInit("Value", Synth.Default(propagateType.OkType)),
                new BoundFieldInit("Error", Synth.Member(tempRef, "Error", errorType)),
            ]);
            onError = new BoundReturnStatement(propagate);
        }
        else
        {
            var error = Synth.Member(tempRef, "Error", errorType);
            var message = Synth.Call(Synth.Member(error, "ToString", Synth.String), [], Synth.String);
            var exception = new BoundObjectCreationExpression(
                new ExternalType("InvalidOperationException"), [])
            {
                ConstructorArguments = [message],
            };
            onError = new BoundThrowStatement(exception);
        }

        Hoist(Synth.If(
            Synth.Not(Synth.Member(tempRef, "IsOk", Synth.Bool)),
            Synth.Block(onError)) with { Span = node.Span });

        return Synth.Member(tempRef, "Value", valueType) with { Span = node.Span };
    }
}
