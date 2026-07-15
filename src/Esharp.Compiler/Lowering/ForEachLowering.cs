using Esharp.BoundTree;
using Esharp.Syntax;

namespace Esharp.Lowering;

/// <summary>
/// Lowers a synchronous <c>for v in src { body }</c> (<see cref="BoundForEachStatement"/>) into the
/// CORE enumerator drain, each form wrapped in a fresh block so its temps and the disposal are
/// scoped to the loop — not the enclosing block:
/// <code>
///   {
///     let __enum = src.GetEnumerator()
///     defer { __enum.Dispose() }                 // loop-scoped — runs when the loop is left
///     while __enum.MoveNext() { let v = __enum.Current; body }
///   }
/// </code>
/// A range loop <c>for i in a..b</c> needs no enumerator:
/// <code>
///   { var __i = a; while __i &lt; b { let i = __i; body; __i = __i + 1 } }
/// </code>
/// The disposal rides on a <c>defer</c> inside the loop block, so <see cref="DeferLowering"/> (which
/// runs next) turns it into a try/finally scoped to the loop. The old pass emitted that defer into
/// the <em>enclosing</em> block, which kept the enumerator alive until the whole method returned;
/// the wrapping block is the fix.
///
/// <para>Async <c>await for</c> is <see cref="AsyncForeachLowering"/>'s job and is already gone by
/// here. Total descent (Base A) reaches a <c>for</c> nested in a lambda body.</para>
/// </summary>
public sealed class ForEachLowering : IBoundTreePass
{
    public static readonly ForEachLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
        => LoweringDriver.MapBodies(program, new ForEachRewriter());
}

sealed class ForEachRewriter : LoweringRewriter
{
    protected override BoundStatement RewriteForEachStatement(BoundForEachStatement node)
    {
        if (node.IsAwait)
            return base.RewriteForEachStatement(node);   // await-for is AsyncForeachLowering's (already run)

        var collection = RewriteExpression(node.Collection);
        var body       = (BoundBlockStatement)RewriteStatement(Synth.AsBlock(node.Body));

        return collection is BoundRangeExpression range
            ? LowerRangeLoop(node, range, body)
            : LowerCollectionLoop(node, collection, body);
    }

    // ── Collection loop: GetEnumerator / MoveNext / Current, disposed on exit ─────
    BoundStatement LowerCollectionLoop(BoundForEachStatement node, BoundExpression collection, BoundBlockStatement body)
    {
        // Binding owns enumerable-contract resolution. In particular an external
        // value-type enumerator may not expose generic arguments on its own return
        // type (`JsonElement.ArrayEnumerator`), while the binder has already found
        // its IEnumerable<JsonElement> contract.
        var elementType    = node.ElementType;
        var enumeratorType = new ExternalType("IEnumerator", [elementType]);

        var enumName = FreshTemp("enum");
        var enumRef  = Synth.Name(enumName, enumeratorType);

        // Iterate through the `IEnumerable<T>` interface, not the collection's own
        // GetEnumerator. A `List<T>`/`Dictionary<…>`/array exposes a public GetEnumerator
        // returning a *value-type* struct enumerator (`List<T>.Enumerator`); binding to it
        // and then storing into the `IEnumerator<T>` slot leaves an unboxed value where a
        // reference is expected (unverifiable IL). The interface method always returns the
        // `IEnumerator<T>` reference, so the drain is verifiable for every collection shape
        // with no per-enumerator boxing or generic-closing. (An upcast for a class/array is
        // a free reference conversion; struct-direct iteration is a later optimization.)
        var iterableType = new ExternalType("IEnumerable", [elementType]);
        var iterable     = BoundConversion.AssertCast(collection, iterableType);

        var enumDecl = Synth.Let(enumName, enumeratorType,
            Synth.Call(Synth.Member(iterable, "GetEnumerator", enumeratorType), [], enumeratorType));

        var dispose = new BoundDeferStatement(Synth.Block(
            new BoundExpressionStatement(Synth.Call(Synth.Member(enumRef, "Dispose", Synth.Void), [], Synth.Void))));

        var moveNext = Synth.Call(Synth.Member(enumRef, "MoveNext", Synth.Bool), [], Synth.Bool);
        var current  = Synth.Member(enumRef, "Current", elementType);

        var loopBody = BindLoopVar(node, elementType, current, body);
        return Synth.Block(enumDecl, dispose, new BoundWhileStatement(moveNext, loopBody)) with { Span = node.Span };
    }

    // ── Range loop: for i in a..b → while ────────────────────────────────────────
    BoundStatement LowerRangeLoop(BoundForEachStatement node, BoundRangeExpression range, BoundBlockStatement body)
    {
        var counterName = FreshTemp("range");
        var counterRef  = Synth.Name(counterName, Synth.Int);

        var start = range.Start ?? Synth.IntLit(0);
        var end   = range.End   ?? Synth.IntLit(0);

        var counterDecl = Synth.Var(counterName, Synth.Int, start);
        var condition   = new BoundBinaryExpression(counterRef, SyntaxTokenKind.Less, end, Synth.Bool);
        var increment   = Synth.Assign(counterRef,
            new BoundBinaryExpression(counterRef, SyntaxTokenKind.Plus, Synth.IntLit(1), Synth.Int));

        var loopStmts = new List<BoundStatement> { Synth.Let(node.Identifier, Synth.Int, counterRef) };
        loopStmts.AddRange(body.Statements);
        loopStmts.Add(increment);

        return Synth.Block(counterDecl, new BoundWhileStatement(condition, Synth.Block(loopStmts))) with { Span = node.Span };
    }

    // Bind the loop variable (and any tuple-destructure names) at the top of the loop body.
    static BoundBlockStatement BindLoopVar(BoundForEachStatement node, BoundType elementType,
                                           BoundExpression current, BoundBlockStatement body)
    {
        var stmts = new List<BoundStatement> { Synth.Let(node.Identifier, elementType, current) };

        if (node.DestructuredNames is { Count: > 0 } names)
        {
            var tupleRef = Synth.Name(node.Identifier, elementType);
            for (var i = 0; i < names.Count; i++)
            {
                var (memberName, itemType) = DestructuredMember(elementType, i);
                stmts.Add(Synth.Let(names[i], itemType, Synth.Member(tupleRef, memberName, itemType)));
            }
        }

        stmts.AddRange(body.Statements);
        return Synth.Block(stmts);
    }

    static (string Name, BoundType Type) DestructuredMember(BoundType elementType, int index)
    {
        if (elementType is TupleType tuple && index < tuple.ElementTypes.Count)
            return ($"Item{index + 1}", tuple.ElementTypes[index]);

        if (elementType is ExternalType kvp && IsKeyValuePair(kvp) && index < kvp.TypeArgs.Count)
            return (index == 0 ? "Key" : "Value", kvp.TypeArgs[index]);

        return ($"Item{index + 1}", InferredType.Instance);
    }

    static bool IsDictionary(ExternalType type) =>
        type.Name == "Dictionary" || type.Name.EndsWith(".Dictionary", StringComparison.Ordinal)
        || type.Name.EndsWith("Dictionary`2", StringComparison.Ordinal);

    static bool IsKeyValuePair(ExternalType type) =>
        type.Name == "KeyValuePair" || type.Name.EndsWith(".KeyValuePair", StringComparison.Ordinal)
        || type.Name.EndsWith("KeyValuePair`2", StringComparison.Ordinal);
}
