using Esharp.BoundTree;
using Esharp.Syntax;

namespace Esharp.Lowering;

/// <summary>
/// Lowers the concurrency surface into CORE nodes over the <c>Esharp.Stdlib</c> types:
/// <list type="bullet">
///   <item><c>chan&lt;T&gt;(n)</c>  → <c>new Chan&lt;T&gt;(n)</c></item>
///   <item><c>spawn { block }</c>   → a fresh <c>CancellationTokenSource</c>, <c>Task.Run(() =&gt; block, cts.Token)</c>,
///         and the <c>Spawned { task, cts }</c> handle (the task + cts hoisted before the handle value)</item>
///   <item><c>select { … }</c>      → a <c>ChanSelect.Arm[]</c> of synthesized TryOp / BlockingOp / Body
///         delegates + a <c>ChanSelect.Select(arms)</c> call, all inside one block</item>
/// </list>
/// The per-arm closures are emitted as function literals; <see cref="ClosureConversion"/> (which runs
/// after this pass) lifts their captures. <c>spawn</c> hoists its setup, so the pass rides on
/// <see cref="SpillingBoundTreeRewriter"/> — a <c>spawn</c> in a conditionally-evaluated position
/// lands its task-start inside the guard. Total descent reaches a <c>chan</c>/<c>spawn</c> nested in
/// a constructor argument, a tuple, or a lambda body.
/// </summary>
public sealed class ConcurrencyLowering : IBoundTreePass
{
    public static readonly ConcurrencyLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
        => LoweringDriver.MapBodies(program, new ConcurrencyRewriter());
}

sealed class ConcurrencyRewriter : SpillingBoundTreeRewriter
{
    // ── chan<T>(n) → new Chan<T>(n) ──────────────────────────────────────────────
    protected override BoundExpression RewriteChanCreationExpression(BoundChanCreationExpression node)
    {
        var chanType    = new ExternalType("Chan", [node.ElementType]);
        var capacityArg = node.Capacity is { } c ? RewriteExpression(c) : Synth.IntLit(0);
        return new BoundObjectCreationExpression(chanType, [])
        {
            ConstructorArguments = [capacityArg],
            Span = node.Span,
        };
    }

    // ── spawn { block } → SpawnedOps.Spawn(() => block) ─────────────────────────
    protected override BoundExpression RewriteSpawnExpression(BoundSpawnExpression node)
    {
        var spawnedType = new ExternalType("Spawned");
        var opsType = new ExternalType("SpawnedOps");
        var loweredBody = (BoundBlockStatement)RewriteStatement(node.Body);
        var bodyLambda  = new BoundFunctionLiteralExpression([], Synth.Void, loweredBody, node.CapturedVariables);
        return Synth.StaticCall("SpawnedOps", "Spawn", opsType, [bodyLambda], spawnedType) with { Span = node.Span };
    }

    // ── select { … } → ChanSelect.Arm[] + ChanSelect.Select(arms) ────────────────
    protected override BoundStatement RewriteSelectStatement(BoundSelectStatement node)
    {
        var armType        = new ExternalType("ChanSelect.Arm");
        var armArrayType   = new ArrayBoundType(armType);
        var chanSelectType = new ExternalType("ChanSelect");

        var stmts = new List<BoundStatement>();

        // A `.recv(v, ch)` binding is a shared mutable local both the TryOp (writes via `out`)
        // and the Body (reads) close over; ClosureConversion lifts it into the shared display.
        var arms = new List<BoundExpression>(node.Arms.Count);
        foreach (var arm in node.Arms)
        {
            if (arm.Kind == SelectArmKind.Recv && arm.Binding is { } bn && bn != "_" && arm.BindingType is { } bt)
                stmts.Add(Synth.Var(bn, bt, Synth.Default(bt)) with { Span = node.Span });
            var captures = arm.Kind == SelectArmKind.Recv && arm.Binding is { } binding && binding != "_" && arm.BindingType is { } bindingType
                ? [.. node.CapturedVariables, new BoundCapturedVariable(binding, bindingType, Mutable: true)]
                : node.CapturedVariables;
            arms.Add(BuildArm(arm, armType, captures));
        }

        var armsVar = FreshTemp("selArms");
        var armsRef = Synth.Name(armsVar, armArrayType);
        stmts.Add(Synth.Let(armsVar, armArrayType,
            new BoundArrayCreationExpression(armType, Synth.IntLit(arms.Count), armArrayType)) with { Span = node.Span });
        for (var i = 0; i < arms.Count; i++)
            stmts.Add(Synth.Assign(new BoundIndexExpression(armsRef, Synth.IntLit(i), armType), arms[i]));

        stmts.Add(new BoundExpressionStatement(
            Synth.StaticCall("ChanSelect", "Select", chanSelectType, [armsRef], Synth.Int)) { Span = node.Span });

        return Synth.Block(stmts) with { Span = node.Span };
    }

    // Build one `new ChanSelect.Arm { Kind, TryOp, BlockingOp, Body }`.
    BoundExpression BuildArm(BoundSelectArm arm, ExternalType armType, IReadOnlyList<BoundCapturedVariable> captures)
    {
        var kindType = new ExternalType("ChanSelect.Kind");
        var taskType = new ExternalType("Task");
        var ctType   = new ExternalType("CancellationToken");
        var ctNone   = Synth.Member(Synth.Name("CancellationToken", ctType), "None", ctType);

        var fields = new List<BoundFieldInit>
        {
            // Runtime enum values are integral constants. Using a literal avoids
            // treating `ChanSelect.Kind` as a local name while preserving its field type.
            new("Kind", new BoundLiteralExpression((int)arm.Kind, arm.Kind.ToString(), kindType)),
            new("Body", Lambda([], Synth.Void, (BoundBlockStatement)RewriteStatement(arm.Body), captures)),
        };

        switch (arm.Kind)
        {
            case SelectArmKind.Recv:
            {
                var ch    = arm.Channel!;
                var elem  = arm.BindingType ?? new ExternalType("object");
                var bound = arm.Binding is { } b && b != "_";
                var slot  = bound ? arm.Binding! : FreshTemp("selDiscard");

                // TryOp: receive into a lambda-local then assign the shared arm binding.
                // An `out v` node carries a bare name and cannot target a closure field;
                // the explicit assignment below is closure-rewritten normally.
                var tryTemp = FreshTemp("selRecv");
                var tryRecv = Synth.Call(Synth.Member(ch, "TryReceive", Synth.Bool),
                    [new BoundOutArgumentExpression(tryTemp, elem, DeclaresLocal: true)], Synth.Bool);
                var tryBody = new List<BoundStatement>
                {
                    new BoundIfStatement(
                        tryRecv,
                        bound
                            ? Synth.Block(
                                Synth.Assign(Synth.Name(slot, elem), Synth.Name(tryTemp, elem)),
                                new BoundReturnStatement(Synth.BoolLit(true)))
                            : Synth.Block(new BoundReturnStatement(Synth.BoolLit(true))),
                        Else: null),
                    new BoundReturnStatement(Synth.BoolLit(false)),
                };
                fields.Add(new("TryOp", Lambda([], Synth.Bool, Synth.Block(tryBody), captures)));

                // BlockingOp: () => ch.ReceiveAsync().AsTask()[.ContinueWith(t => slot = t.Result)]
                var vtT   = new ExternalType("ValueTask", [elem]);
                var taskT = new ExternalType("Task", [elem]);
                BoundExpression blockTask = Synth.Call(
                    Synth.Member(Synth.Call(Synth.Member(ch, "ReceiveAsync", vtT), [ctNone], vtT), "AsTask", taskT), [], taskT);
                if (bound)
                {
                    var cont = Lambda([new BoundParameter("__t", taskT, false)], Synth.Void,
                        Synth.Block(Synth.Assign(Synth.Name(slot, elem),
                            Synth.Member(Synth.Name("__t", taskT), "Result", elem))), captures);
                    blockTask = Synth.Call(Synth.Member(blockTask, "ContinueWith", taskType), [cont], taskType);
                }
                fields.Add(new("BlockingOp", Lambda([], taskType, ReturnBlock(blockTask), captures)));
                break;
            }
            case SelectArmKind.Send:
            {
                var ch  = arm.Channel!;
                var val = arm.Value!;
                fields.Add(new("TryOp", Lambda([], Synth.Bool, ReturnBlock(
                    Synth.Call(Synth.Member(ch, "TrySend", Synth.Bool), [val], Synth.Bool)), captures)));
                var vt = new ExternalType("ValueTask");
                fields.Add(new("BlockingOp", Lambda([], taskType, ReturnBlock(
                    Synth.Call(Synth.Member(
                        Synth.Call(Synth.Member(ch, "SendAsync", vt), [val, ctNone], vt), "AsTask", taskType), [], taskType)), captures)));
                break;
            }
            case SelectArmKind.Timeout:
                fields.Add(new("BlockingOp", Lambda([], taskType, ReturnBlock(
                    Synth.StaticCall("Task", "Delay", taskType, [arm.Value!], taskType)), captures)));
                break;
            case SelectArmKind.Default:
                fields.Add(new("TryOp", Lambda([], Synth.Bool, ReturnBlock(Synth.BoolLit(true)), captures)));
                break;
        }

        return new BoundObjectCreationExpression(armType, fields);
    }

    static BoundFunctionLiteralExpression Lambda(
        IReadOnlyList<BoundParameter> ps, BoundType ret, BoundBlockStatement body,
        IReadOnlyList<BoundCapturedVariable> captures)
        => new(ps, ret, body, captures);

    static BoundBlockStatement ReturnBlock(BoundExpression value)
        => Synth.Block(new BoundReturnStatement(value));
}
