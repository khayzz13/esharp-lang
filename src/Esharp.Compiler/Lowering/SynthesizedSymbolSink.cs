using Esharp.BoundTree;
using Esharp.Symbols;

namespace Esharp.Lowering;

/// <summary>
/// Receives <see cref="TypeSymbol"/>s and <see cref="MethodSymbol"/>s minted by lowering
/// passes (display classes, async state-machine structs, iterator structs) and interns them
/// into the compilation's symbol table so CodeGen can resolve them by reference identity.
///
/// <para>
/// Naming convention for compiler-generated types: display classes use
/// <c>&lt;&gt;c__Display_N</c>, async state machines use
/// <c>&lt;Method&gt;d__StateMachine_N</c>, iterator structs use
/// <c>&lt;Method&gt;d__Iterator_N</c>. Angle brackets are illegal in E# identifiers —
/// the synthesized names cannot conflict with any user-declared name.
/// </para>
///
/// <para>
/// All synthesized types are registered via <see cref="SymbolTable.GetOrAdd"/> under the
/// <c>&lt;synth&gt;</c> namespace. The bound-tree currency for a synthesized type is an
/// <see cref="ExternalType"/> carrying the synthesized name; CodeGen resolves the full
/// TypeSymbol from <see cref="SynthesizedTypes"/> by name or from the side-tables this
/// sink exposes.
/// </para>
///
/// <para>
/// Method bodies for synthesized methods (MoveNext, SetStateMachine, display trampolines)
/// cannot live on <see cref="MethodSymbol"/> (it only carries a Decl syntax ref). They are
/// stored in <see cref="MethodBodies"/> — a dictionary keyed by MethodSymbol reference
/// identity that CodeGen drains to emit each method's IL.
/// </para>
///
/// </summary>
public sealed class SynthesizedSymbolSink
{
    readonly SymbolTable _table;
    readonly List<TypeSymbol> _types = [];
    readonly List<MethodSymbol> _methods = [];
    readonly Dictionary<MethodSymbol, BoundBlockStatement> _methodBodies
        = new(ReferenceEqualityComparer.Instance);

    int _closureId;
    int _smId;

    // All synthesized symbols land in this namespace; angle brackets block collision
    // with any user-declared name.
    const string SynthNs = "<synth>";

    public SynthesizedSymbolSink(SymbolTable table) => _table = table;

    // ─── Public read surface ──────────────────────────────────────────────────

    /// All type symbols minted by lowering passes, in registration order.
    public IReadOnlyList<TypeSymbol> SynthesizedTypes => _types;

    /// All free-method symbols minted by lowering passes (display-class trampolines,
    /// async-stream producers whose declaring type is null), in registration order.
    public IReadOnlyList<MethodSymbol> SynthesizedMethods => _methods;

    /// Bodies for synthesized methods, keyed by MethodSymbol reference identity.
    /// CodeGen reads this to obtain the IL-emittable body for MoveNext, SetStateMachine,
    /// display trampolines, and other compiler-generated callables.
    public IReadOnlyDictionary<MethodSymbol, BoundBlockStatement> MethodBodies => _methodBodies;

    // ─── Display class (closure conversion) ───────────────────────────────────

    /// <summary>
    /// Mints a display-class TypeSymbol for a closure over <paramref name="captures"/>.
    /// The resulting class is a sealed reference type with one public mutable field per
    /// captured variable. Trampoline methods (the lambda body lifted to an instance method)
    /// are registered onto it separately via <see cref="SynthesizeMethod"/>.
    ///
    /// <para>
    /// Returns the TypeSymbol and an <see cref="ExternalType"/> bound-tree token so the
    /// caller can build a <see cref="BoundObjectCreationExpression"/> and a
    /// <see cref="BoundMethodGroupConversion"/> that reference the synthesized class.
    /// </para>
    /// </summary>
    public (TypeSymbol Symbol, ExternalType BoundView) SynthesizeDisplayClass(
        string enclosingMethodName,
        IReadOnlyList<(string Name, BoundType Type)> captures,
        IReadOnlyList<string>? typeParameters = null)
    {
        var id = _closureId++;
        var name = $"<>c__Display_{id}";
        var typeParams = typeParameters ?? [];
        var sym = _table.GetOrAdd(name, arity: typeParams.Count, kind: TypeSymbolKind.Class,
            ns: SynthNs, classification: DataClassification.Class);

        foreach (var (capName, capType) in captures)
            sym.AddField(MakeField(capName, sym, capType));

        _types.Add(sym);
        return (sym, new ExternalType(name, typeParams.Select(p => (BoundType)new PrimitiveType(p)).ToList()));
    }

    // ─── Async state-machine struct ────────────────────────────────────────────

    /// <summary>
    /// Mints a state-machine struct for an async function.
    ///
    /// <para>
    /// Fields on the struct:
    /// <list type="bullet">
    ///   <item><c>_state: int</c> — current resume label; -1 = not started, -2 = done</item>
    ///   <item><c>_builder</c> — the async method builder (type driven by <paramref name="shape"/>)</item>
    ///   <item>One field per entry in <paramref name="parameters"/> — parameter values captured at call-time</item>
    /// </list>
    /// Additional spill-local fields are added by <see cref="AsyncLowering"/> after this
    /// call (one per variable that is live across an await point).
    /// </para>
    ///
    /// <para>
    /// Two MethodSymbols are registered on the struct — MoveNext and SetStateMachine — and
    /// their bodies are deposited in <see cref="MethodBodies"/>. MoveNext's body is
    /// <paramref name="originalBody"/>; CodeGen wraps it in the state-dispatch scaffold.
    /// SetStateMachine's body is an empty block (struct-based state machines; never called
    /// by the runtime).
    /// </para>
    /// </summary>
    public (TypeSymbol Symbol, ExternalType BoundView, MethodSymbol MoveNext, MethodSymbol SetStateMachine)
        SynthesizeStateMachineStruct(
            string methodName,
            AsyncReturnShape shape,
            BoundType resultType,
            IReadOnlyList<(string Name, BoundType Type)> parameters,
            IReadOnlyList<string> typeParameters,
            BoundBlockStatement originalBody)
    {
        var id = _smId++;
        var name = $"<{methodName}>d__StateMachine_{id}";
        var sym = _table.GetOrAdd(name, arity: typeParameters.Count, kind: TypeSymbolKind.Struct,
            ns: SynthNs, classification: DataClassification.Struct);

        // Core state-machine fields
        sym.AddField(MakeField("_state", sym, new PrimitiveType("int")));
        sym.AddField(MakeField("_builder", sym, BuilderBoundType(shape, resultType)));

        // Parameter capture fields
        foreach (var (pName, pType) in parameters)
            sym.AddField(MakeField(pName, sym, pType));

        // IAsyncStateMachine interface (interned in the synth namespace)
        var iasTimSym = _table.GetOrAdd("IAsyncStateMachine", 0, TypeSymbolKind.Interface, ns: SynthNs);
        sym.AddInterface(new TypeRef(iasTimSym));

        // MoveNext — body is the original async body; CodeGen adds the state-dispatch scaffold
        var moveNext = MakeMethod("MoveNext", isStatic: false, isAsync: true,
            new PrimitiveType("void"), paramCount: 0, sym);
        sym.AddMember(moveNext);
        _methodBodies[moveNext] = originalBody;

        // SetStateMachine — empty no-op for struct-based state machines
        var setStateMachine = MakeMethod("SetStateMachine", isStatic: false, isAsync: false,
            new PrimitiveType("void"), paramCount: 1, sym);
        sym.AddMember(setStateMachine);
        _methodBodies[setStateMachine] = new BoundBlockStatement([]);

        _types.Add(sym);
        return (sym, new ExternalType(name,
            typeParameters.Select(p => (BoundType)new PrimitiveType(p)).ToList()), moveNext, setStateMachine);
    }

    // ─── Iterator state-machine struct (async stream) ─────────────────────────

    /// <summary>
    /// Mints a state-machine struct implementing <c>IAsyncEnumerable&lt;T&gt;</c> and
    /// <c>IAsyncEnumerator&lt;T&gt;</c> for an async-stream iterator function.
    ///
    /// <para>
    /// Fields: <c>_state: int</c>, <c>Current: T</c>, plus one field per original
    /// parameter. MoveNextAsync and GetAsyncEnumerator are registered with empty bodies —
    /// the actual iteration logic comes from the producer state machine that
    /// <see cref="AsyncLowering"/> creates for the <c>__stream_&lt;Name&gt;</c> producer.
    /// </para>
    /// </summary>
    public (TypeSymbol Symbol, ExternalType BoundView,
            MethodSymbol MoveNextAsync, MethodSymbol GetAsyncEnumerator)
        SynthesizeIteratorStruct(
            string methodName,
            BoundType elementType,
            IReadOnlyList<(string Name, BoundType Type)> parameters)
    {
        var id   = _smId++;
        var name = $"<{methodName}>d__Iterator_{id}";
        // The iterator owns mutable state across async MoveNextAsync calls. A value
        // type would be copied into each generated async state machine, so Current and
        // _state updates would disappear from the enumerator held by the consumer.
        var sym  = _table.GetOrAdd(name, arity: 0, kind: TypeSymbolKind.Class,
            ns: SynthNs, classification: DataClassification.Class);

        sym.AddField(MakeField("_state",   sym, new PrimitiveType("int")));
        sym.AddField(MakeField("Current",  sym, elementType));

        foreach (var (pName, pType) in parameters)
            sym.AddField(MakeField(pName, sym, pType));

        // IAsyncEnumerable<T> and IAsyncEnumerator<T>
        var asyncEnumSym  = _table.GetOrAdd("IAsyncEnumerable", 1, TypeSymbolKind.Interface, ns: SynthNs);
        var asyncEnumrSym = _table.GetOrAdd("IAsyncEnumerator",  1, TypeSymbolKind.Interface, ns: SynthNs);
        sym.AddInterface(new TypeRef(asyncEnumSym));
        sym.AddInterface(new TypeRef(asyncEnumrSym));

        // MoveNextAsync: ValueTask<bool>   — body filled in by IteratorLowering via RegisterMethodBody.
        var moveNextAsync = MakeMethod("MoveNextAsync", isStatic: false, isAsync: true,
            new ExternalType("ValueTask", [new PrimitiveType("bool")]), paramCount: 0, sym);
        sym.AddMember(moveNextAsync);
        _methodBodies[moveNextAsync] = new BoundBlockStatement([]);   // placeholder

        // GetAsyncEnumerator: IAsyncEnumerator<T>  — body filled in by IteratorLowering.
        var getEnumerator = MakeMethod("GetAsyncEnumerator", isStatic: false, isAsync: false,
            new ExternalType("IAsyncEnumerator", [elementType]), paramCount: 1, sym);
        sym.AddMember(getEnumerator);
        _methodBodies[getEnumerator] = new BoundBlockStatement([]);   // placeholder

        _types.Add(sym);
        return (sym, new ExternalType(name), moveNextAsync, getEnumerator);
    }

    // ─── Synthesized member or free method ────────────────────────────────────

    /// <summary>
    /// Mints a <see cref="MethodSymbol"/> for a compiler-generated method (display-class
    /// trampoline, async-stream producer) and registers <paramref name="body"/> as
    /// its IL-emittable body.
    ///
    /// <para>
    /// When <paramref name="declaringType"/> is non-null the method is registered on the
    /// type via <see cref="TypeSymbol.AddMember"/>; when null it is a free synthesized
    /// function added to <see cref="SynthesizedMethods"/>.
    /// </para>
    /// </summary>
    public MethodSymbol SynthesizeMethod(
        string name,
        bool isAsync,
        BoundType returnType,
        int paramCount,
        BoundBlockStatement body,
        TypeSymbol? declaringType = null)
    {
        var sym = MakeMethod(name, isStatic: declaringType is null, isAsync,
            returnType, paramCount, declaringType);

        if (declaringType is not null)
            declaringType.AddMember(sym);
        else
            _methods.Add(sym);

        _methodBodies[sym] = body;
        return sym;
    }

    // ─── Method body registration (post-synthesis update) ─────────────────────

    /// <summary>
    /// Replace or register the body for a <see cref="MethodSymbol"/> that was already
    /// minted via <see cref="SynthesizeStateMachineStruct"/>. Used by
    /// <see cref="AsyncLowering"/> to overwrite the placeholder body with the real
    /// synthesized MoveNext body after the state-machine analysis is complete.
    /// </summary>
    public void RegisterMethodBody(MethodSymbol sym, BoundBlockStatement body) =>
        _methodBodies[sym] = body;

    // ─── Internal helpers ─────────────────────────────────────────────────────

    /// Build a FieldSymbol using the required-init pattern FieldSymbol demands.
    /// <c>Type</c> is left as InferencePending; CodeGen resolves the field type from
    /// <c>Bound</c> (the structured BoundType) at emit time.
    static FieldSymbol MakeField(string name, TypeSymbol declaring, BoundType bound) =>
        new()
        {
            Name = name,
            DeclaringType = declaring,
            // TypeRef.InferencePending is the placeholder; the actual type is in Bound.
            Type = TypeRef.InferencePending,
            Bound = bound,
            IsPublic = true,
            Mutable = true,
        };

    /// Build a MethodSymbol with the required-init pattern.
    /// <paramref name="paramCount"/> is the source-level parameter count (DeclaredArity);
    /// CodeGen resolves actual parameter types from the body in MethodBodies.
    static MethodSymbol MakeMethod(
        string name,
        bool isStatic,
        bool isAsync,
        BoundType returnType,
        int paramCount,
        TypeSymbol? declaringType)
    {
        // DeclaredParameters uses InferencePending placeholders — CodeGen resolves from body.
        var paramRefs = paramCount > 0
            ? Enumerable.Repeat(TypeRef.InferencePending, paramCount).ToArray()
            : Array.Empty<TypeRef>();

        return new()
        {
            Name = name,
            IsStatic = isStatic,
            IsAsync = isAsync,
            ReturnType = returnType,
            DeclaredParameters = paramRefs,
            DeclaredArity = paramCount,
            DeclaringType = declaringType,
        };
    }

    /// Select the AsyncMethodBuilder type corresponding to the given async return shape.
    static BoundType BuilderBoundType(AsyncReturnShape shape, BoundType resultType)
    {
        var isVoid = resultType is PrimitiveType { Name: "void" };
        return shape switch
        {
            AsyncReturnShape.Void => new ExternalType("AsyncVoidMethodBuilder"),
            AsyncReturnShape.Task => isVoid
                ? new ExternalType("AsyncTaskMethodBuilder")
                : new ExternalType("AsyncTaskMethodBuilder", [resultType]),
            _ => isVoid
                ? new ExternalType("AsyncValueTaskMethodBuilder")
                : new ExternalType("AsyncValueTaskMethodBuilder", [resultType]),
        };
    }
}
