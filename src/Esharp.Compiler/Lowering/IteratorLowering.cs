using Esharp.BoundTree;
using Esharp.Symbols;
using Esharp.Syntax;

namespace Esharp.Lowering;

/// <summary>
/// Lowers <c>IAsyncEnumerable&lt;T&gt;</c> wrapper functions (return type
/// <c>IAsyncEnumerable&lt;T&gt;</c>, <c>HasAwait == false</c>, produced by
/// <see cref="AsyncStreamLowering"/>) into a synthesized iterator struct that implements
/// the <b>complete-program contract</b>: all three struct methods carry their real bodies
/// <b>inline</b> on their <see cref="BoundFunctionDeclaration"/>s, so CodeGen emits them
/// through the ordinary instance-method path with no synthesized-vs-user special-casing.
///
/// <para>
/// <b>Complete-program contract (mirrors <see cref="AsyncLowering"/> and
/// <c>ClosureConversion</c>):</b> a lowering pass materialises its synthesised type as a
/// real <see cref="BoundDataDeclaration"/> whose <c>InstanceMethods</c> are
/// <see cref="BoundFunctionDeclaration"/>s carrying their bodies <em>inline</em>, then
/// appends it to <c>unit.Members</c>. CodeGen emits it through
/// <c>allPendingBodies</c> / <c>EmitFunctionBody</c> — the same path as every
/// user-declared instance method. The <see cref="SynthesizedSymbolSink"/> is used only for
/// its symbol-table registration side-effect (so <c>ILTypeResolver.TryResolveRegistered</c>
/// can locate the struct by name); <c>sink.MethodBodies</c> is never consulted by CodeGen.
/// </para>
///
/// <para>
/// The synthesized iterator struct implements both
/// <c>IAsyncEnumerable&lt;T&gt;</c> and <c>IAsyncEnumerator&lt;T&gt;</c>.
/// It carries:
/// <list type="bullet">
///   <item><c>_state: int</c> — iterator state (-1 = not started, 0 = running, -2 = done)</item>
///   <item><c>Current: T</c> — the most-recently-yielded element (readable after each successful MoveNextAsync)</item>
///   <item><c>_channel: Chan&lt;T&gt;</c> — the backpressure channel the producer pushes into</item>
///   <item><c>__producerTask: Task</c> — the running producer, started by GetAsyncEnumerator</item>
///   <item>One field per original parameter — captured at wrapper call time; forwarded to the producer</item>
/// </list>
/// </para>
///
/// <para>
/// The synthesized methods (all with inline bodies on their <see cref="BoundFunctionDeclaration"/>s):
/// <list type="bullet">
///   <item>
///     <b>GetAsyncEnumerator(CancellationToken)</b> (<c>HasAwait = false</c>): initialises
///     <c>_state = 0</c>, creates <c>_channel = new Chan&lt;T&gt;(0)</c>, starts the async
///     producer via <c>__stream_&lt;Name&gt;(params..., _channel, CancellationToken.None)</c>,
///     then returns <c>this</c> boxed as <c>IAsyncEnumerator&lt;T&gt;</c>.
///   </item>
///   <item>
///     <b>MoveNextAsync()</b> (<c>HasAwait = true</c>): awaits one item from <c>_channel</c>
///     via <c>_channel.Receive()</c>. On success sets <c>Current</c> and returns
///     <c>ValueTask&lt;bool&gt;(true)</c>. On channel-close (producer exhausted) sets
///     <c>_state = -2</c> and returns <c>ValueTask&lt;bool&gt;(false)</c>. Because
///     <c>HasAwait = true</c>, <see cref="AsyncLowering"/> (the next pass) converts this
///     declaration into its own state-machine struct — the iterator's MoveNextAsync is
///     itself an async function and gets the full state-machine treatment.
///   </item>
///   <item>
///     <b>DisposeAsync()</b> (<c>HasAwait = false</c>): no-op returning a default
///     <c>ValueTask</c>. The producer runs to natural completion when the channel is closed;
///     a full cooperative-cancellation path (threading <c>CancellationTokenSource</c>
///     through) is a later hardening concern.
///   </item>
/// </list>
/// </para>
///
/// <para>
/// After this pass:
/// <list type="bullet">
///   <item>The wrapper function body is replaced with a direct struct-init expression
///   that captures only the parameters (the channel and producer are initialised lazily in
///   GetAsyncEnumerator, not at construction time).</item>
///   <item>The synthesized iterator <see cref="BoundDataDeclaration"/> is appended to the
///   compilation unit's member list; <see cref="AsyncLowering"/> then picks up
///   <c>MoveNextAsync</c> (HasAwait == true) and lowers it to a nested state-machine struct,
///   exactly as it does for any other async instance method.</item>
/// </list>
/// </para>
/// </summary>
public sealed class IteratorLowering : IBoundTreePass
{
    public static readonly IteratorLowering Instance = new();

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
        var synthMembers = new List<BoundMember>();
        var newMembers   = new List<BoundMember>(unit.Members.Count);
        var changed      = false;

        foreach (var member in unit.Members)
        {
            if (member is BoundFunctionDeclaration fn && IsAsyncEnumerableWrapper(fn))
            {
                var (newFn, iterDecl) = LowerWrapper(fn, sink);
                newMembers.Add(newFn);
                synthMembers.Add(iterDecl);
                changed = true;
            }
            else if (member is BoundDataDeclaration data && data.InstanceMethods is { Count: > 0 })
            {
                var newMethods = new List<BoundFunctionDeclaration>(data.InstanceMethods.Count);
                var dataChanged = false;
                foreach (var ifn in data.InstanceMethods)
                {
                    if (IsAsyncEnumerableWrapper(ifn))
                    {
                        var (newFn, iterDecl) = LowerWrapper(ifn, sink);
                        newMethods.Add(newFn);
                        synthMembers.Add(iterDecl);
                        dataChanged = true;
                    }
                    else
                    {
                        newMethods.Add(ifn);
                    }
                }
                if (dataChanged)
                {
                    newMembers.Add(data with { InstanceMethods = newMethods });
                    changed = true;
                }
                else
                {
                    newMembers.Add(data);
                }
            }
            else
            {
                newMembers.Add(member);
            }
        }

        if (!changed) return unit;
        newMembers.AddRange(synthMembers);
        return unit with { Members = newMembers };
    }

    /// An iterator wrapper function returns IAsyncEnumerable<T> and has no await.
    /// The async producer (HasAwait==true) is the __stream_ prefixed variant; the wrapper
    /// is the sync face that clients call.
    static bool IsAsyncEnumerableWrapper(BoundFunctionDeclaration fn) =>
        fn.ReturnType is ExternalType { Name: "IAsyncEnumerable" } && !fn.HasAwait;

    // ─── Core: wrapper function → struct init + iterator struct declaration ───

    static (BoundFunctionDeclaration NewWrapper, BoundDataDeclaration IterDecl)
        LowerWrapper(BoundFunctionDeclaration fn, SynthesizedSymbolSink sink)
    {
        var elemType   = ElementType(fn.ReturnType);
        var parameters = fn.Parameters.Select(p => (p.Name, p.Type)).ToList();

        // ── Mint the iterator struct ──────────────────────────────────────────
        // SynthesizeIteratorStruct registers the struct's TypeSymbol in the compilation's
        // symbol table (needed by ILTypeResolver.TryResolveRegistered when CodeGen resolves
        // the struct by name) and returns the ExternalType bound-tree token (`iterBoundView`)
        // that identifies the struct in BoundObjectCreationExpressions and member-access nodes.
        //
        // IMPORTANT: we do NOT call sink.RegisterMethodBody for MoveNextAsync or
        // GetAsyncEnumerator. The sink's MethodBodies dictionary is never consulted by
        // CodeGen — it is a dead side-channel that existed only in the old monolithic emitter.
        // CodeGen reads bodies exclusively from BoundFunctionDeclaration.Body on the
        // BoundDataDeclaration.InstanceMethods list (the "complete-program contract"). The
        // real bodies are placed inline on those BoundFunctionDeclarations below.
        var (iterSym, iterBoundView, moveNextSym, getEnumeratorSym) =
            sink.SynthesizeIteratorStruct(
                methodName:  fn.Name,
                elementType: elemType,
                parameters:  parameters);

        // ── Build real MoveNextAsync body (inline) ────────────────────────────
        // HasAwait = true: AsyncLowering (the next pipeline pass) will convert this
        // BoundFunctionDeclaration into a state-machine struct, exactly like any other
        // async instance method. The body is the real iteration logic, not a stub —
        // AsyncLowering rewrites it into state-dispatch form.
        //
        // Semantics (per ESC oracle ILAsyncEmitter.cs + the channel-backed iterator spec):
        //   if (_state == -2) return ValueTask<bool>(false)        // already exhausted
        //   let __recv = await _channel.Receive()                  // suspend until item or close
        //   if (__recv.IsNone) { _state = -2; return false }       // channel closed → done
        //   Current = __recv.Value                                  // advance Current
        //   return ValueTask<bool>(true)                            // caller may read Current
        var moveNextBody  = BuildMoveNextAsyncBody(elemType, iterBoundView);
        var moveNextRetTy = new ExternalType("ValueTask", [new PrimitiveType("bool")]);

        // ── Build real GetAsyncEnumerator body (inline) ───────────────────────
        // HasAwait = false: synchronous initialisation that starts the producer.
        //
        // Semantics (per ESC oracle and the channel-backed iterator spec):
        //   _state   = 0                                            // mark running
        //   _channel = new Chan<T>(0)                              // capacity-0 → pure backpressure
        //   __producerTask = __stream_<Name>(params..., _channel, CancellationToken.None)
        //   return this  (boxed as IAsyncEnumerator<T>)
        var getEnumeratorBody  = BuildGetAsyncEnumeratorBody(fn, elemType, iterBoundView);
        var getEnumeratorRetTy = new ExternalType("IAsyncEnumerator", [elemType]);

        // ── Build DisposeAsync body (inline) ──────────────────────────────────
        // No-op: producer runs to natural completion when the channel drains; cooperative
        // cancellation (threading a CancellationTokenSource through) is a later hardening.
        // HasAwait = false: no state-machine needed; returns a default ValueTask directly.
        var disposeAsyncRetTy  = new ExternalType("ValueTask");
        var disposeAsyncBody   = new BoundBlockStatement([
            new BoundReturnStatement(
                new BoundObjectCreationExpression(disposeAsyncRetTy, []))]);

        // ── Build the BoundDataDeclaration for the iterator struct ────────────
        // Field set:
        //   _state: int            — iteration state, initialised by GetAsyncEnumerator
        //   Current: T             — last yielded element, set by MoveNextAsync
        //   <params...>            — captured from the original wrapper call
        //   _channel: Chan<T>      — backpressure channel; created by GetAsyncEnumerator
        //   __producerTask: Task   — running producer; started by GetAsyncEnumerator
        //
        // SynthesizeIteratorStruct already added _state, Current, and the parameter
        // fields to iterSym. We add the two runtime-only fields here and mirror them
        // onto the BoundDataDeclaration.Fields list so CodeGen emits them on the TypeDef.
        var chanType = new ExternalType("Chan", [elemType]);
        var taskType = new ExternalType("Task");

        iterSym.AddField(MakeField("_channel",       iterSym, chanType));
        iterSym.AddField(MakeField("__producerTask", iterSym, taskType));

        // Project iterSym.Fields → BoundField list. SynthesizeIteratorStruct's fields
        // (_state, Current, params) come first; then the two we just added.
        var iterFields = iterSym.Fields
            .Select(f => new BoundField(f.Name, f.Bound ?? new PrimitiveType("object"),
                IsPublic: f.IsPublic, Mutable: true,
                IsProperty: f.Name == "Current",
                PropHasSet: f.Name == "Current"))
            .ToList();

        var ctType = new ExternalType("CancellationToken");

        // ── Instance methods — bodies inline, per the complete-program contract ──────
        // All three BoundFunctionDeclarations carry their real bodies directly. CodeGen
        // collects them via data.InstanceMethods and emits them through EmitFunctionBody,
        // the same path as every user-declared instance method. No sink side-channel involved.
        //
        // MoveNextAsync: HasAwait = true → AsyncLowering (next pass) converts it to a
        //   nested state-machine struct appended to the unit. This is the correct and
        //   intentional shape — the iterator's MoveNextAsync is itself an async function
        //   and gets the full state-machine treatment, exactly like an ordinary async method.
        var moveNextDecl = new BoundFunctionDeclaration(
            IsPublic:       true,
            Name:           "MoveNextAsync",
            TypeParameters: [],
            Parameters:     [new BoundParameter("this", iterBoundView, ByRef: false)],
            ReturnType:     moveNextRetTy,
            Body:           moveNextBody,
            Attributes:     [],
            HasAwait:       true,       // suspended at _channel.Receive() → lowered by AsyncLowering
            AsyncShape:     AsyncReturnShape.ValueTask)
        { Symbol = moveNextSym };       // symbol identity for method-group resolution

        // GetAsyncEnumerator: HasAwait = false → emitted synchronously by CodeGen directly.
        var getEnumeratorDecl = new BoundFunctionDeclaration(
            IsPublic:       true,
            Name:           "GetAsyncEnumerator",
            TypeParameters: [],
            Parameters:     [
                new BoundParameter("this", iterBoundView, ByRef: false),
                new BoundParameter("ct", ctType, ByRef: false),
            ],
            ReturnType:     getEnumeratorRetTy,
            Body:           getEnumeratorBody,
            Attributes:     [],
            HasAwait:       false)
        { Symbol = getEnumeratorSym };  // symbol identity for method-group resolution

        // DisposeAsync: HasAwait = false → emitted synchronously by CodeGen directly.
        // IAsyncEnumerator<T> : IAsyncDisposable, so DisposeAsync must be present to
        // satisfy the interface contract at the CLR level.
        var disposeDecl = new BoundFunctionDeclaration(
            IsPublic:       true,
            Name:           "DisposeAsync",
            TypeParameters: [],
            Parameters:     [new BoundParameter("this", iterBoundView, ByRef: false)],
            ReturnType:     disposeAsyncRetTy,
            Body:           disposeAsyncBody,
            Attributes:     [],
            HasAwait:       false);
        // No Symbol assignment needed: DisposeAsync is not called as a method-group
        // elsewhere in user code, and CodeGen resolves it by name from the TypeDef.

        var iterDecl = new BoundDataDeclaration(
            IsPublic:        false,   // compiler-generated; not exposed to user code
            IsReadonly:      false,
            Name:            iterSym.Name,
            TypeParameters:  [],
            DeriveTraits:    [],
            Fields:          iterFields,
            InstanceMethods: [moveNextDecl, getEnumeratorDecl, disposeDecl],
            Classification:  DataClassification.Class,
            Attributes:      ["CompilerGenerated"],
            InterfaceTypes:  [
                // IAsyncEnumerable<T> — satisfied by GetAsyncEnumerator
                new ExternalType("IAsyncEnumerable", [elemType]),
                // IAsyncEnumerator<T> : IAsyncDisposable — satisfied by MoveNextAsync +
                // the compiler-declared Current property + DisposeAsync. Generated code
                // follows the same field/property ABI boundary as authored source.
                new ExternalType("IAsyncEnumerator", [elemType]),
                // Declare the inherited interface explicitly as well. Reflection's
                // interface method enumeration omits inherited members, while the
                // emitter needs this direct contract to mark DisposeAsync virtual+final
                // on the generated value type.
                new ExternalType("IAsyncDisposable"),
            ]);

        // ── Rewrite the wrapper function body ─────────────────────────────────
        // Old body: return AsyncStream.Create<T>(lambda)  ← runtime-helper path, removed
        // New body: return new IteratorStruct { param0 = param0, ... }
        //
        // Only the original parameters are captured at struct-construction time. The
        // _channel and __producerTask are NOT initialised here — they are created by
        // GetAsyncEnumerator on the first call to GetAsyncEnumerator(), so that each
        // call to GetAsyncEnumerator() produces an independent enumerator with its own
        // producer instance (the IAsyncEnumerable<T> contract: GetAsyncEnumerator may be
        // called multiple times, each returning a fresh, independent enumerator).
        var fieldInits = fn.Parameters
            .Select(p => new BoundFieldInit(p.Name, new BoundNameExpression(p.Name, p.Type)))
            .ToList<BoundFieldInit>();

        var ctorExpr = new BoundObjectCreationExpression(iterBoundView, fieldInits);
        var newBody  = new BoundBlockStatement([new BoundReturnStatement(ctorExpr)])
            { Span = fn.Body.Span };

        var newFn = fn with { Body = newBody };
        return (newFn, iterDecl);
    }

    // ─── MoveNextAsync body ────────────────────────────────────────────────────

    /// <code>
    ///   if (_state == -2) return ValueTask&lt;bool&gt;(false)
    ///   let __recv = await _channel.Receive()
    ///   if (__recv.IsNone) {
    ///     _state = -2
    ///     return ValueTask&lt;bool&gt;(false)
    ///   }
    ///   Current = __recv.Value
    ///   return ValueTask&lt;bool&gt;(true)
    /// </code>
    static BoundBlockStatement BuildMoveNextAsyncBody(
        BoundType elemType,
        ExternalType iterType)
    {
        const string thisRef = "this";
        var intType       = new PrimitiveType("int");
        var boolType      = new PrimitiveType("bool");
        var chanType      = new ExternalType("Chan",      [elemType]);
        var chanOpsType   = new ExternalType("ChanOps");
        var ctType        = new ExternalType("CancellationToken");
        var vboolType     = new ExternalType("ValueTask", [boolType]);
        var vElemType     = new ExternalType("ValueTask", [elemType]);

        var thisExpr      = new BoundNameExpression(thisRef, iterType);
        var stateField    = new BoundMemberAccessExpression(thisExpr, "_state",  intType);
        var channelField  = new BoundMemberAccessExpression(thisExpr, "_channel", chanType);
        var currentField  = new BoundMemberAccessExpression(thisExpr, "Current", elemType);
        var ctNone        = new BoundMemberAccessExpression(
            new BoundNameExpression("CancellationToken", ctType), "None", ctType);

        // MoveNextAsync is lowered as an ordinary async ValueTask<bool> method;
        // its source-level body returns the unwrapped bool, never a nested ValueTask.
        var falseValue = new BoundLiteralExpression(false, "false", boolType);
        var trueValue = new BoundLiteralExpression(true, "true", boolType);

        // if (_state == -2) return ValueTask<bool>(false)
        var doneCheck = new BoundIfStatement(
            Condition: new BoundBinaryExpression(
                stateField, SyntaxTokenKind.EqualsEquals,
                new BoundLiteralExpression(-2, "-2", intType), boolType),
            Then:  new BoundReturnStatement(falseValue),
            Else:  null);

        // A completed channel reports false without throwing; only then do we read.
        var waitToRead = new BoundCallExpression(
            Target: new BoundMemberAccessExpression(
                new BoundNameExpression("ChanOps", chanOpsType), "WaitToReadAsync", vboolType),
            Arguments: [channelField, ctNone],
            Type: vboolType)
        { ExplicitTypeArguments = [elemType] };
        var readableVar = new BoundVariableDeclaration(
            Mutable: false, "__readable", boolType,
            new BoundAwaitExpression(waitToRead, boolType));

        // if (__readable == false) { _state = -2; return false }
        var notReadable = new BoundIfStatement(
            Condition: new BoundBinaryExpression(
                new BoundNameExpression("__readable", boolType), SyntaxTokenKind.EqualsEquals,
                new BoundLiteralExpression(false, "false", boolType), boolType),
            Then: new BoundBlockStatement([
                new BoundAssignment(
                    stateField,
                    new BoundLiteralExpression(-2, "-2", intType)),
                new BoundReturnStatement(falseValue),
            ]),
            Else: null);

        var receiveCall = new BoundCallExpression(
            Target: new BoundMemberAccessExpression(
                new BoundNameExpression("ChanOps", chanOpsType), "ReceiveAsync", vElemType),
            Arguments: [channelField, ctNone],
            Type: vElemType)
        { ExplicitTypeArguments = [elemType] };
        var recvVar = new BoundVariableDeclaration(
            Mutable: false, "__recv", elemType,
            new BoundAwaitExpression(receiveCall, elemType));

        // Current = __recv
        var assignCurrent = new BoundAssignment(
            currentField,
            new BoundNameExpression("__recv", elemType));

        // return ValueTask<bool>(true)
        var returnTrue = new BoundReturnStatement(trueValue);

        return new BoundBlockStatement([doneCheck, readableVar, notReadable, recvVar, assignCurrent, returnTrue]);
    }

    // ─── GetAsyncEnumerator body ───────────────────────────────────────────────

    /// <code>
    ///   _state = 0
    ///   _channel = new Chan&lt;T&gt;(0)
    ///   __producerTask = __stream_&lt;Name&gt;(params..., _channel, CancellationToken.None)
    ///   return this
    /// </code>
    static BoundBlockStatement BuildGetAsyncEnumeratorBody(
        BoundFunctionDeclaration fn,
        BoundType elemType,
        ExternalType iterType)
    {
        const string thisRef = "this";
        var intType  = new PrimitiveType("int");
        var chanType = new ExternalType("Chan",              [elemType]);
        var ctType   = new ExternalType("CancellationToken");
        var taskType = new ExternalType("Task");

        var thisExpr      = new BoundNameExpression(thisRef, iterType);
        var stateField    = new BoundMemberAccessExpression(thisExpr, "_state",        intType);
        var channelField  = new BoundMemberAccessExpression(thisExpr, "_channel",      chanType);
        var producerField = new BoundMemberAccessExpression(thisExpr, "__producerTask", taskType);

        var producerName = $"__stream_{fn.Name}";

        // _state = 0
        var setRunning = new BoundAssignment(
            stateField, new BoundLiteralExpression(0, "0", intType));

        // _channel = new Chan<T>(0)
        var newChannel = new BoundObjectCreationExpression(chanType, [])
        {
            ConstructorArguments = [new BoundLiteralExpression(0, "0", intType)],
        };
        var setChannel = new BoundAssignment(channelField, newChannel);

        // __producerTask = __stream_<Name>(params..., _channel, CancellationToken.None)
        var producerArgs = new List<BoundExpression>();
        foreach (var p in fn.Parameters)
            producerArgs.Add(new BoundMemberAccessExpression(thisExpr, p.Name, p.Type));
        producerArgs.Add(channelField);
        producerArgs.Add(new BoundMemberAccessExpression(
            new BoundNameExpression("CancellationToken", ctType), "None", ctType));

        var producerCall = new BoundCallExpression(
            Target:    new BoundNameExpression(producerName, taskType),
            Arguments: producerArgs,
            Type:      taskType);
        var setProducer = new BoundAssignment(producerField, producerCall);

        // return this  (as IAsyncEnumerator<T>)
        // Iterator state is reference-owned so the async MoveNext state machine and the
        // consumer observe the same Current/_state fields; the interface upcast is no IL.
        var returnThis = new BoundReturnStatement(
            BoundConversion.Identity(thisExpr,
                new ExternalType("IAsyncEnumerator", [elemType])));

        return new BoundBlockStatement([setRunning, setChannel, setProducer, returnThis]);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    static BoundType ElementType(BoundType returnType) =>
        returnType is ExternalType { TypeArguments: { Count: > 0 } args }
            ? args[0]
            : new ExternalType("object");

    static FieldSymbol MakeField(string name, TypeSymbol declaring, BoundType bound) =>
        new()
        {
            Name          = name,
            DeclaringType = declaring,
            Type          = TypeRef.InferencePending,
            Bound         = bound,
            IsPublic      = false,
            Mutable       = true,
        };
}
