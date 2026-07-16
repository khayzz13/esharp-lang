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
            : collection.Type is ArrayBoundType array
                ? LowerArrayLoop(node, collection, array, body)
            : LowerCollectionLoop(node, collection, body);
    }

    // ── Array loop: for v in T[] → one evaluated receiver + counted while ────────
    BoundStatement LowerArrayLoop(BoundForEachStatement node, BoundExpression collection,
                                  ArrayBoundType arrayType, BoundBlockStatement body)
    {
        // Evaluate the array expression exactly once. Apart from avoiding repeated
        // side effects, this gives the JIT the canonical receiver/length/index shape
        // it recognizes for bounds-check elimination and loop optimizations.
        var arrayName  = FreshTemp("array");
        var lengthName = FreshTemp("length");
        var indexName  = FreshTemp("index");
        var arrayRef   = Synth.Name(arrayName, arrayType);
        var lengthRef  = Synth.Name(lengthName, Synth.Int);
        var indexRef   = Synth.Name(indexName, Synth.Int);

        var arrayDecl  = Synth.Let(arrayName, arrayType, collection);
        var lengthDecl = Synth.Let(lengthName, Synth.Int,
            Synth.Member(arrayRef, "Length", Synth.Int));
        var indexDecl  = Synth.Var(indexName, Synth.Int, Synth.IntLit(0));
        var condition  = new BoundBinaryExpression(indexRef, SyntaxTokenKind.Less, lengthRef, Synth.Bool);
        var current    = new BoundIndexExpression(arrayRef, indexRef, arrayType.ElementType);
        var increment  = Synth.Assign(indexRef,
            new BoundBinaryExpression(indexRef, SyntaxTokenKind.Plus, Synth.IntLit(1), Synth.Int));
        // Advance after materializing the current element but before executing the
        // source body. A source `continue` then reaches the next element instead of
        // skipping a trailing synthetic increment and spinning forever. The index is
        // compiler-only state, so advancing it early is not source-observable.
        var loopBody   = BindLoopVar(node, arrayType.ElementType, current, body, increment);

        return Synth.Block(
            arrayDecl,
            lengthDecl,
            indexDecl,
            new BoundWhileStatement(condition, loopBody)) with { Span = node.Span };
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
        var endName     = FreshTemp("rangeEnd");
        var counterRef  = Synth.Name(counterName, Synth.Int);
        var endRef      = Synth.Name(endName, Synth.Int);

        var start = range.Start ?? Synth.IntLit(0);
        var end   = range.End   ?? Synth.IntLit(0);

        var counterDecl = Synth.Var(counterName, Synth.Int, start);
        // A range's endpoints are values, not expressions re-evaluated on every
        // iteration. Caching the upper bound also produces the conventional
        // counted-loop form used by array/span kernels.
        var endDecl     = Synth.Let(endName, Synth.Int, end);
        var condition   = new BoundBinaryExpression(counterRef, SyntaxTokenKind.Less, endRef, Synth.Bool);
        var increment   = Synth.Assign(counterRef,
            new BoundBinaryExpression(counterRef, SyntaxTokenKind.Plus, Synth.IntLit(1), Synth.Int));

        // As with array loops, advance the hidden counter before the source body so
        // `continue` cannot bypass it. The source loop variable has already captured
        // the current value.
        var loopStmts = new List<BoundStatement>
        {
            Synth.Let(node.Identifier, Synth.Int, counterRef),
            increment,
        };
        loopStmts.AddRange(body.Statements);

        return Synth.Block(counterDecl, endDecl,
            new BoundWhileStatement(condition, Synth.Block(loopStmts))) with { Span = node.Span };
    }

    // Bind the loop variable (and any tuple-destructure names) at the top of the loop body.
    static BoundBlockStatement BindLoopVar(BoundForEachStatement node, BoundType elementType,
                                           BoundExpression current, BoundBlockStatement body,
                                           BoundStatement? beforeBody = null)
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

        if (beforeBody is not null)
            stmts.Add(beforeBody);
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
