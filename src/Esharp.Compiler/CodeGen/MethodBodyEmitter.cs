using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Emit;
using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Syntax;
// CodeGen is a Cecil emitter; the only System.Reflection.Metadata type it needs is
// ILOpCode (the ILBuilder verb currency). Import it aliased so the shared metadata-ref
// names (TypeReference/MethodDefinition/…) resolve to Mono.Cecil, not S.R.M.
using ILOpCode = System.Reflection.Metadata.ILOpCode;
using IMetadataResolver = Esharp.Emit.IMetadataResolver;

namespace Esharp.CodeGen;

public partial class MethodBodyEmitter
{

    private protected readonly ILBuilder _il;
    private protected readonly IMetadataResolver _types;
    private protected readonly DiagnosticBag _diagnostics;
    private protected readonly MethodDefinition _method;
    // Unified name → slot map. Resolution goes through this one table; every
    // declaration path (params, locals, match bindings, hoisted captures, SM
    // fields in async mode) installs or overwrites an entry here. Name-access
    // call sites only see `ILSlot` and don't know whether the underlying
    // storage is a local, parameter, display-class field, or SM struct field.
    private protected readonly Dictionary<string, ILSlot> _slots = new(StringComparer.Ordinal);
    // Raw display-class field view, preserved separately so nested emitters
    // (spawn bodies, select arms, lambda bodies, channel helpers) can inherit
    // it and install their own SelfFieldSlot entries against it.
    private protected Dictionary<string, FieldDefinition>? _displayClassFields;
    private protected readonly string? _selfParamName;

    /// Whether a bare name denotes the method's receiver. Two spellings map to it:
    /// the explicit receiver name from a `func (c: T)` block (`_selfParamName`), and
    /// the reserved `this` — which synthesized instance methods (an async `MoveNext`,
    /// a state-machine helper) name their receiver, since they carry no receiver block.
    /// `this` resolves to the receiver for any instance method (`HasThis`); it is never
    /// a user value name in E#, so this can never shadow a real local.
    private protected bool IsSelfReference(string n) =>
        n == _selfParamName || (n == "this" && _method.HasThis);
    bool _inTryOrDefer;
    private protected bool _lastCallWasVoid;
    // The Cecil return type of the most recently emitted call. The binder erases some
    // nested generic value types (e.g. `List<T>.Enumerator`) to object/var, so the
    // return-box decision falls back to this when the binder type isn't a value type.
    private protected Mono.Cecil.TypeReference? _lastCallReturnType;

    // Loop label stack — innermost loop's break/continue targets at top.
    // EmitWhile, EmitForEach (incl. range form), and any future loop pushes;
    // EmitBreak/EmitContinue read .Peek(); the loop emitter pops on exit.
    // A stack so nested loops don't overwrite outer targets.
    readonly Stack<(ILLabel breakTarget, ILLabel continueTarget)> _loopTargets = new();

    // Named labels for BoundGotoStatement / BoundLabelStatement (the state-machine
    // dispatch + resume points minted by AsyncLowering / IteratorLowering). One
    // ILLabel per name, created lazily so a forward `goto` and its later label
    // resolve to the same target. Scoped per method body.
    readonly Dictionary<string, ILLabel> _namedLabels = new(StringComparer.Ordinal);

    ILLabel NamedLabel(string name) =>
        _namedLabels.TryGetValue(name, out var lbl)
            ? lbl
            : _namedLabels[name] = _il.DefineLabel(name);

    // Return-via-leave: when the method body contains a try/defer/foreach,
    // `ret` from inside a protected region is invalid IL. Allocated lazily by
    // the first protected region that needs them; EmitReturnCore stores the
    // value to _returnLocal (if non-void) and emits `Leave _returnLabel`. The
    // method epilogue appends `_returnLabel; ldloc _returnLocal; ret`.
    private protected VariableDefinition? _returnLocal;
    private protected ILLabel? _returnLabel;

    // Sequence point anchors: (Nop instruction, source location). Collected during
    // statement emission and applied to the method's DebugInformation after the
    // optimizer runs (Nop identity is stable across ShortenOpcodes).
    readonly List<(Instruction Anchor, Esharp.Syntax.SourceSpan Span)> _sequencePoints = new();

    /// Name of an in-scope parameter holding a `CancellationToken` that
    /// `EmitSelect` / `EmitForEach(Chan<T>)` should thread through to the
    /// CT-aware runtime overloads. Set on spawn-body sub-emitters so that
    /// `TaskScope` cancellation unwinds blocked channel ops in bounded time.
    string? _ambientCancellationTokenParam;

    /// Tell this emitter that the named parameter carries a cancellation
    /// token that should be threaded through any runtime operations which
    /// can block on the runtime (select arms, Chan<T> iteration).
    public void SetAmbientCancellationToken(string paramName)
    {
        _ambientCancellationTokenParam = paramName;
    }

    // Closure hoisting: display class for captured variables in the enclosing scope
    VariableDefinition? _displayLocal;
    TypeDefinition? _displayClass;
    MethodDefinition? _displayCtor;

    public MethodBodyEmitter(
        MethodDefinition method,
        ILTypeResolver types,
        DiagnosticBag diagnostics,
        Dictionary<string, FieldDefinition>? structFields = null,
        string? selfParamName = null)
    {
        _method = method;
        _il = new ILBuilder(method);
        _types = types;
        _diagnostics = diagnostics;
        _selfParamName = selfParamName;

        // When constructed as a nested/child emitter on a display class instance
        // method, `structFields` carries the display class's fields and every bare
        // name resolves via `this.field` (ldarg_0; ldfld). Install those as slots up
        // front so the unified resolution table handles them alongside locals/params.
        if (structFields is not null)
        {
            _displayClassFields = structFields;
            if (selfParamName is not null)
            {
                // A field token in a generic value-type instance method must be
                // scoped to the same closed `this` type as arg.0.  In particular,
                // async MoveNext receives `ref StateMachine<T>`; using the open
                // FieldDefinition here produces `ref StateMachine` field addresses
                // and the verifier rejects Await*OnCompleted.  SelfFieldSlot has
                // explicit support for this re-homing; construct the host once for
                // every captured/state-machine field below.
                GenericInstanceType? genericSelf = null;
                if (method.DeclaringType?.HasGenericParameters == true)
                {
                    genericSelf = new GenericInstanceType(method.DeclaringType);
                    foreach (var parameter in method.DeclaringType.GenericParameters)
                        genericSelf.GenericArguments.Add(parameter);
                }
                foreach (var (name, field) in structFields)
                    _slots[name] = new SelfFieldSlot(field, genericSelf);
            }
        }

        SeedParameterSlots(method);
    }

    /// Install an ILSlot for each method parameter. Override in async mode to
    /// route parameters through state-machine field slots instead.
    private protected virtual void SeedParameterSlots(MethodDefinition method)
    {
        foreach (var p in method.Parameters)
        {
            if (p.ParameterType.IsByReference)
            {
                var elementType = ((Mono.Cecil.ByReferenceType)p.ParameterType).ElementType;
                _slots[p.Name] = p.IsIn
                    ? new ReadOnlyByRefParameterSlot(p, elementType)
                    : new ByRefParameterSlot(p, elementType);
            }
            else if (ILHeapPointer.IsWrapperType(p.ParameterType))
            {
                var valueField = ILHeapPointer.GetValueField(_types.Module, p.ParameterType);
                _slots[p.Name] = new WrapperBackedParameterSlot(p, valueField, valueField.FieldType);
            }
            else
                _slots[p.Name] = new ParameterSlot(p);
        }
    }

    /// Declare a new named local binding in the current scope. The default
    /// implementation allocates a Cecil `VariableDefinition` and installs a
    /// `LocalSlot`. Async mode overrides this to allocate a field on the state
    /// machine struct so the binding survives await suspensions.
    private protected virtual void DeclareLocal(string name, TypeReference type)
    {
        var def = new VariableDefinition(type);
        _method.Body.Variables.Add(def);
        _slots[name] = new LocalSlot(def);
    }

    // Generated scoped-mut begin/resume helpers seed locals from a private lease
    // object before emitting the already-bound resume block. Keeping this on the
    // normal slot table means arbitrary ordinary expressions in that block still
    // use the same local semantics as source code.
    public void SeedGeneratedLocal(string name, BoundType type) => DeclareLocal(name, _types.Resolve(type));

    public void EmitGeneratedLocalLoad(string name)
    {
        if (TryResolveSlot(name) is not { } slot)
            throw new FeatureNodeInCodeGenException($"generated scoped-mut local '{name}' was not declared");
        slot.EmitLoad(_il);
    }

    public void EmitGeneratedLocalStoreFromStack(string name)
    {
        if (TryResolveSlot(name) is not { } slot)
            throw new FeatureNodeInCodeGenException($"generated scoped-mut local '{name}' was not declared");
        EmitStoreFromStack(slot);
    }

    /// Seed a value-receiver snapshot. A `func (c: T) m()` value receiver operates on a
    /// COPY of the receiver (Go semantics) — but a struct instance method's `this` (arg0)
    /// is a managed pointer that writes back. So copy the struct out of `this` into a
    /// fresh local at body entry and bind the receiver name to it; the body's reads and
    /// writes then hit the copy, and mutations are discarded at return. (For a class or a
    /// pointer/readonly receiver this is not called — those bind the receiver to `this`
    /// directly via `selfParamName`.)
    internal void SeedValueReceiverSnapshot(string name, TypeReference structType)
    {
        var def = new VariableDefinition(structType);
        _method.Body.Variables.Add(def);
        _il.LoadArg(_method.Body.ThisParameter);             // this — managed pointer to the receiver struct
        _il.LoadObject(structType);   // load the struct value (a copy)
        _il.StoreLocal(def);

        _slots[name] = new LocalSlot(def);
    }

    /// Emit an await point. Post-lowering this should never be reached: AsyncLowering (C1)
    /// converts every await into a state-machine struct. If this fires, the lowering pipeline
    /// failed — see FeatureNodeInCodeGenException for the proper diagnostic path.
    private protected virtual void EmitAwait(BoundAwaitExpression aw) =>
        throw new FeatureNodeInCodeGenException(
            "BoundAwaitExpression reached MethodBodyEmitter.EmitAwait. " +
            "AsyncLowering must have failed to convert this async function body to a state machine.");

    /// Emit the IL for a `return` statement. When the method has any
    /// protected region (try/catch/finally/defer/foreach), `ret` from inside
    /// is invalid IL — instead we store to `_returnLocal`, `leave _returnLabel`,
    /// and the epilogue emits `_returnLabel; ldloc _returnLocal; ret`. When
    /// there's no protected region, emits a direct `ret`. Async mode overrides
    /// this to route through `AsyncValueTaskMethodBuilder.SetResult`.
    private protected virtual void EmitReturnCore(BoundExpression? expression)
    {
        if (_returnLabel is not null)
        {
            if (expression is not null)
            {
                _lastCallReturnType = null; // reset; the call path sets it if `expression` is a call
                EmitExpression(expression);
                BoxValueIfReturnExpectsInterface(expression);
            }
            // A null `expression` means the return value is already on the stack
            // (the `?`-propagation / nullable-nil contract). Either way, spill the
            // value to the return local before leaving the protected region —
            // `leave` clears the eval stack, so an un-spilled value would be lost
            // and the epilogue would return the local's default (e.g. a Result
            // with a null error). `_returnLocal` is null only for void methods.
            if (_returnLocal is not null)
                _il.StoreLocal(_returnLocal);

            _il.Leave(_returnLabel!);

            return;
        }

        if (expression is not null)
        {
            _lastCallReturnType = null; // reset; the call path below sets it if `expression` is a call
            EmitExpression(expression);
            BoxValueIfReturnExpectsInterface(expression);
        }
        _il.Ret(_method.ReturnType.FullName != "System.Void");

    }

    // A value-type expression returned where the method declares an interface return
    // (`func GetEnumerator() -> IEnumerator<T> = self.items.GetEnumerator()`, whose
    // concrete result is the value `List<T>.Enumerator`) must be boxed to the interface.
    void BoxValueIfReturnExpectsInterface(BoundExpression expression)
    {
        var ret = _method.ReturnType;
        Mono.Cecil.TypeDefinition? retDef = null;
        try { retDef = ret.Resolve(); } catch { /* unresolvable cross-module ref */ }
        if (retDef is null || !retDef.IsInterface) return;
        TypeReference? exprRef = null;
        try { exprRef = _types.Resolve(expression.Type); } catch { }
        // The binder erases nested generic value types (`List<T>.Enumerator`) to
        // object/var; fall back to the actual emitted call return type in that case.
        if ((exprRef is null || exprRef.IsGenericParameter || !exprRef.IsValueType)
            && _lastCallReturnType is { IsValueType: true } lct)
            exprRef = lct;
        if (exprRef is null || exprRef.IsGenericParameter || !exprRef.IsValueType) return;
        // Already the interface (or object) — no box needed.
        if (exprRef.FullName == ret.FullName) return;
        _il.Box(exprRef);

    }

    /// Lazily allocate the return-via-leave epilogue. Called by every emitter
    /// that wraps the body in a protected region (try, defer, foreach, match
    /// with a synthetic try, ...). Idempotent. Once allocated, all subsequent
    /// `EmitReturnCore` calls in this method route through Leave + epilogue.
    private protected void EnsureReturnViaLeave()
    {
        if (_returnLabel is not null) return;
        _returnLabel = _il.DefineLabel("return");
        var retType = _method.ReturnType;
        var isVoid = retType.FullName == "System.Void";
        if (!isVoid)
        {
            _returnLocal = new VariableDefinition(retType);
            _method.Body.Variables.Add(_returnLocal);
        }
    }

    /// Append the return-via-leave epilogue to the method body. Called once
    /// after `EmitBlock` by the top-level emitter. No-op if no protected
    /// region was emitted.
    public void FinalizeMethodEpilogue()
    {
        if (_returnLabel is null) return;
        _il.MarkLabel(_returnLabel);
        if (_returnLocal is not null)
            _il.LoadLocal(_returnLocal);

        _il.Ret(_method.ReturnType.FullName != "System.Void");

    }

    /// Resolve a name to its slot, or null if the name is not bound in this scope.
    private protected ILSlot? TryResolveSlot(string name) =>
        _slots.TryGetValue(name, out var slot) ? slot : null;

    public void EmitBlock(BoundBlockStatement block) => EmitStatements(block.Statements, 0);

    // defer is scope-exit lowering: `defer { D }; rest` becomes `try { rest } finally { D }`.
    // Stacking defers naturally nests the try/finally, giving LIFO semantics. Without this
    // wrapping, defers emitted with empty `try` blocks fire immediately when the IL passes
    // their finally — *before* the rest of the block runs, which inverts the entire scope's
    // execution order.
    void EmitStatements(IReadOnlyList<BoundStatement> statements, int startIdx)
    {
        for (var i = startIdx; i < statements.Count; i++)
        {
            if (statements[i] is BoundDeferStatement d)
            {
                _inTryOrDefer = true;
                EnsureReturnViaLeave();
                EmitDeferWrapping(d, statements, i + 1);
                return;
            }
            EmitStatement(statements[i]);
        }
    }

    void EmitDeferWrapping(BoundDeferStatement d, IReadOnlyList<BoundStatement> statements, int restStart)
    {
        var afterFinally = _il.DefineLabel("deferEnd");

        // Async hooks (sync no-ops): the pre-entry label sits outside the `.try` so a staged
        // resume falls into the protected region, and the body dispatch re-routes resumes to
        // in-region await labels — the rest of the block runs inside this defer's try.
        EmitRegionPreEntry(d);
        var region = _il.BeginTry();
        EmitDeferBodyDispatch(d);
        EmitStatements(statements, restStart);
        _il.Leave(afterFinally);

        _il.BeginFinallyBlock(region);
        EmitFinallyGuard(afterFinally);
        EmitBlock(d.Body);
        _il.EndFinally();
        // The after-finally label coincides with the handler end; the region's
        // ExceptionHandler is registered with that label as HandlerEnd.
        _il.MarkLabel(afterFinally);
        _il.EndTryRegion(region, afterFinally);
    }

    /// Collected sequence point anchors for PDB emission. Available after EmitBlock completes.
    public IReadOnlyList<(Instruction Anchor, Esharp.Syntax.SourceSpan Span)> SequencePoints => _sequencePoints;

    void EmitStatement(BoundStatement stmt)
    {
        // Emit a Nop anchor for statements with valid source locations.
        // The Nop's identity is stable across ILOptimizer.ShortenOpcodes,
        // so we add SequencePoints after optimization using these anchors.
        if (stmt.Span.IsValid)
        {
            var anchor = _il.MarkSequencePoint();
            _sequencePoints.Add((anchor, stmt.Span));
        }

        switch (stmt)
        {
            case BoundVariableDeclaration v:
                EmitVarDecl(v);
                break;
            case BoundAssignment a:
                EmitAssignment(a);
                break;
            // ---- FEATURE nodes: unreachable post-lowering. Belt-and-suspenders guard
            //      (AssertCoreOnly in CodeGenerator.Generate() is the primary gate). ----
            case BoundCompoundAssignment ca:
                throw new FeatureNodeInCodeGenException(
                    $"BoundCompoundAssignment (op '{ca.Op}') reached MethodBodyEmitter. AssignmentLowering must have failed.");
            case BoundRaiseStatement:
                throw new FeatureNodeInCodeGenException(
                    "BoundRaiseStatement reached MethodBodyEmitter. EventLowering must have failed.");
            case BoundMatchStatement:
                throw new FeatureNodeInCodeGenException(
                    "BoundMatchStatement reached MethodBodyEmitter. MatchLowering must have failed.");
            case BoundDeferStatement:
                throw new FeatureNodeInCodeGenException(
                    "BoundDeferStatement reached MethodBodyEmitter. DeferLowering must have failed.");
            case BoundSelectStatement:
                throw new FeatureNodeInCodeGenException(
                    "BoundSelectStatement reached MethodBodyEmitter. ConcurrencyLowering must have failed.");
            case BoundForEachStatement:
                throw new FeatureNodeInCodeGenException(
                    "BoundForEachStatement reached MethodBodyEmitter. ForEachLowering must have failed.");
            case BoundLetGuard:
                throw new FeatureNodeInCodeGenException(
                    "BoundLetGuard reached MethodBodyEmitter. LetGuardLowering must have failed.");
            case BoundAsyncLetStatement:
                throw new FeatureNodeInCodeGenException(
                    "BoundAsyncLetStatement reached MethodBodyEmitter. AsyncLetLowering must have failed.");
            case BoundYieldStatement:
                throw new FeatureNodeInCodeGenException(
                    "BoundYieldStatement reached MethodBodyEmitter. AsyncStreamLowering must have failed.");
            // ---- CORE control-flow nodes ----
            case BoundIfStatement i:
                EmitIf(i);
                break;
            case BoundWhileStatement w:
                EmitWhile(w);
                break;
            case BoundReturnStatement r:
                EmitReturn(r);
                break;
            case BoundExpressionStatement e:
                _lastCallWasVoid = false;
                EmitExpression(e.Expression);
                // Only pop if the expression left a value on the stack
                if (e.Expression.Type is not VoidType && !_lastCallWasVoid)
                    _il.Pop();

                break;
            case BoundScopedMutCall scoped:
                EmitScopedMutCall(scoped);
                break;
            case BoundBlockStatement b:
                EmitBlock(b);
                break;
            case BoundTryStatement tr:
                EmitTry(tr);
                break;
            case BoundThrowStatement th:
                EmitThrow(th);
                break;
            case BoundBreakStatement:
                EmitBreak();
                break;
            case BoundContinueStatement:
                EmitContinue();
                break;
            case BoundLabelStatement label:
                _il.MarkLabel(NamedLabel(label.Name));
                break;
            case BoundGotoStatement gotoStmt:
                // Async state dispatch/resume targets stay in their protected region and use
                // `br`. An async return from a nested try/finally instead targets the outer
                // state-machine convergence label, so it must use `leave` to run cleanup.
                if (gotoStmt.ExitsProtectedRegion)
                    _il.Leave(NamedLabel(gotoStmt.Target));
                else
                    _il.Branch(NamedLabel(gotoStmt.Target));
                break;
            case BoundConstStatement:
                // No IL — every reference was already inlined by the binder.
                break;
        }
    }

    void EmitScopedMutCall(BoundScopedMutCall scoped)
    {
        // The location itself is intentionally not emitted at the caller: that
        // would leak a private backing field (and would not run resume on throw).
        // Instead the declaring type gives us a lease with an addressable Value
        // field and takes it back through __mut_resume_* in a real CLR finally.
        var receiverType = _types.Resolve(scoped.Receiver.Type);
        TypeDefinition? owner;
        try { owner = receiverType.Resolve(); }
        catch { owner = null; }
        var begin = owner?.Methods.FirstOrDefault(m => m.Name == "__mut_begin_" + scoped.Property.Name);
        var resume = owner?.Methods.FirstOrDefault(m => m.Name == "__mut_resume_" + scoped.Property.Name);
        var carrierBaseName = "__mut_lease_" + scoped.Property.Name;
        TypeDefinition? carrier = null;
        try { carrier = begin?.ReturnType.Resolve(); }
        catch { }
        carrier ??= owner?.NestedTypes.FirstOrDefault(t => t.Name == carrierBaseName
            || t.Name.StartsWith(carrierBaseName + "`", StringComparison.Ordinal));
        var value = carrier?.Fields.FirstOrDefault(f => f.Name == "Value");
        if (begin is null || resume is null || carrier is null || value is null)
            throw new FeatureNodeInCodeGenException(
                $"scoped mut property '{scoped.Property.Name}' has no generated lease protocol");

        // The helpers belong to the property owner.  A borrow through
        // `Box<int>.value` must call helpers hosted on `Box<int>`, never the
        // open `Box<!0>` definitions.  In particular the begin helper returns
        // the closed opaque carrier that owns the byref handed to the borrower.
        // The protocol can live in a referenced E# producer.  Never retain a
        // producer ModuleDefinition in the consumer body: Cecil requires every
        // local, field, and call signature to be imported into the module being
        // written.
        MethodReference beginRef = _types.Module.ImportReference(begin);
        MethodReference resumeRef = _types.Module.ImportReference(resume);
        if (receiverType is GenericInstanceType closedOwner)
        {
            beginRef = _types.Module.ImportReference(HostMethodOnType(begin, closedOwner));
            resumeRef = _types.Module.ImportReference(HostMethodOnType(resume, closedOwner));
        }

        // One receiver evaluation is part of the property contract: a receiver
        // expression may allocate, call, or otherwise have observable effects.
        var receiverLocal = new VariableDefinition(receiverType);
        _method.Body.Variables.Add(receiverLocal);
        EmitExpression(scoped.Receiver);
        _il.StoreLocal(receiverLocal);

        // Member references keep the owner's open `!0` in their signature (as
        // ordinary C# calls do), while a local receiving `Box<int>.begin()` is
        // the closed `Lease<int>`.  Keeping those two roles separate avoids an
        // unverifiable `Lease<!0>` local in a non-generic caller.
        var leaseType = receiverType is GenericInstanceType genericReceiver && owner is not null
            ? CloseTypeOnReceiver(beginRef.ReturnType, owner, genericReceiver)
            : beginRef.ReturnType;
        leaseType = _types.Module.ImportReference(leaseType);
        var leaseLocal = new VariableDefinition(leaseType);
        _method.Body.Variables.Add(leaseLocal);
        _il.LoadLocal(receiverLocal);
        _il.CallVirt(beginRef);
        _il.StoreLocal(leaseLocal);

        if (scoped.Call.ResolvedMethod is not { } symbol || _types.MethodForSymbol(symbol) is not { } callee)
            throw new FeatureNodeInCodeGenException(
                "scoped mut borrowing currently requires a resolved E# function symbol");
        if (scoped.Call.ExplicitTypeArguments is { Count: > 0 } typeArgs && callee.HasGenericParameters)
        {
            var generic = new GenericInstanceMethod(callee);
            foreach (var type in typeArgs)
                generic.GenericArguments.Add(_types.ResolveGenericArgument(type));
            callee = generic;
        }

        var tryStart = _il.DefineLabel("mutBorrowTry");
        var finallyStart = _il.DefineLabel("mutBorrowFinally");
        var after = _il.DefineLabel("mutBorrowAfter");
        _il.MarkLabel(tryStart);
        EmitCallArgsCoerced(scoped.Call.Arguments, callee, emitOverride: index =>
        {
            if (index != scoped.ArgumentIndex) return false;
            _il.LoadLocal(leaseLocal);
            var valueRef = leaseLocal.VariableType is GenericInstanceType leaseType
                ? new FieldReference(value.Name, _types.Module.ImportReference(value.FieldType), leaseType)
                : _types.Module.ImportReference(value);
            _il.LoadFieldAddress(valueRef);
            return true;
        });
        _il.Call(callee);
        if (scoped.ResultLocal is { } result)
        {
            if (TryResolveSlot(result) is not { } slot)
                throw new FeatureNodeInCodeGenException($"scoped mut result local '{result}' was not declared");
            EmitStoreFromStack(slot);
        }
        else if (callee.ReturnType.FullName != "System.Void")
        {
            // The lowerer only emits statement-form calls without a result slot.
            _il.Pop();
        }
        _il.Leave(after);

        _il.MarkLabel(finallyStart);
        _il.LoadLocal(receiverLocal);
        _il.LoadLocal(leaseLocal);
        _il.CallVirt(resumeRef);
        _il.EndFinally();
        _il.MarkLabel(after);
        _il.AddExceptionHandler(ILExceptionRegionKind.Finally,
            tryStart, finallyStart, finallyStart, after);
    }

    static TypeReference CloseTypeOnReceiver(TypeReference type, TypeDefinition owner,
        GenericInstanceType receiver)
    {
        if (type is GenericParameter parameter && parameter.Owner is TypeDefinition parameterOwner
            && ReferenceEquals(parameterOwner, owner))
        {
            var index = owner.GenericParameters.IndexOf(parameter);
            return index >= 0 ? receiver.GenericArguments[index] : type;
        }
        if (type is GenericInstanceType generic)
        {
            var closed = new GenericInstanceType(generic.ElementType);
            foreach (var argument in generic.GenericArguments)
                closed.GenericArguments.Add(CloseTypeOnReceiver(argument, owner, receiver));
            return closed;
        }
        if (type is ArrayType array)
            return new ArrayType(CloseTypeOnReceiver(array.ElementType, owner, receiver), array.Rank);
        if (type is ByReferenceType byReference)
            return new ByReferenceType(CloseTypeOnReceiver(byReference.ElementType, owner, receiver));
        return type;
    }

    // Pull the closed Result<TOk,TErr> generic args off the enclosing method's
    // return type. For sync functions the return type is the Result directly.
    // For async-promoted functions it's wrapped in ValueTask<Result<...>>.
    // Falls back to the inner expression's args if the function doesn't return
    // a Result (e.g. `?` used in a non-Result context — already a binder error).
    // Whether the enclosing function's (peeled) return type is `Result<…>`. When
    // false, `?` has no Result to propagate into and surfaces the error by throwing.
    // Async overrides this — in MoveNext `_method.ReturnType` is `void`, so the
    // subclass consults the analyzed logical return type instead.
    private protected virtual bool EnclosingReturnsResult()
    {
        var ret = _method.ReturnType;
        if (ret is Mono.Cecil.GenericInstanceType g
            && (g.ElementType.Name is "ValueTask`1" or "Task`1") && g.GenericArguments.Count == 1)
            ret = g.GenericArguments[0];
        return ret is Mono.Cecil.GenericInstanceType rg && rg.ElementType.Name == "Result`2";
    }

    (TypeReference Ok, TypeReference Err) ResolveEnclosingResultTypeArgs(TypeReference fallbackOk, TypeReference fallbackErr)
    {
        var ret = _method.ReturnType;
        // Peel ValueTask<...> if present (async-promoted functions)
        if (ret is Mono.Cecil.GenericInstanceType git
            && (git.ElementType.Name == "ValueTask`1" || git.ElementType.Name == "Task`1")
            && git.GenericArguments.Count == 1)
        {
            ret = git.GenericArguments[0];
        }
        if (ret is Mono.Cecil.GenericInstanceType resultGit
            && resultGit.ElementType.Name == "Result`2"
            && resultGit.GenericArguments.Count == 2)
        {
            return (resultGit.GenericArguments[0], resultGit.GenericArguments[1]);
        }
        return (fallbackOk, fallbackErr);
    }

    /// Emit `expr?` as a value-producing expression: evaluate the inner `Result`, and on
    /// error propagate it out of the enclosing function (matching its return type) — on
    /// success, leave the unwrapped `Value` on the stack. Works anywhere an expression
    /// can appear: `let x = f()?`, `return f()?`, `g(f()?)`, or a bare `f()?` statement
    /// (the expression-statement path pops the value). The `let`-declaration path stores
    /// the produced value into the binding.
    void EmitTryUnwrapExpression(BoundTryUnwrapExpression tu)
    {
        // tmp = expr
        // if (tmp.IsError) return Result.Error<TOk,TErr>(tmp.Error)   (or throw if no Result)
        // <push tmp.Value>
        var module = _types.Module;

        if (tu.Inner.Type is not ResultType rt)
        {
            _diagnostics.Report("", 0, 0, "IL: ? used on non-Result type");
            return;
        }

        // Resolve the closed Result<TOk,TErr> via Cecil so user-defined types
        // (DataType, ChoiceType) round-trip correctly. The previous reflection
        // path went through BoundTypeToRuntime which falls back to
        // typeof(object) for user types — producing
        // Result<object,string>::get_IsError calls against a Result<Token,string>
        // value, which CLR rejects as malformed IL / wrong-instance dispatch.
        var resultTypeRef = _types.Resolve(tu.Inner.Type);
        var okRef = _types.Resolve(rt.OkType);
        var errRef = _types.Resolve(rt.ErrorType);

        // Allocate temp for the full Result
        var tempDef = new VariableDefinition(resultTypeRef);
        _method.Body.Variables.Add(tempDef);

        // Evaluate inner expression → store to temp
        EmitExpression(tu.Inner);
        _il.StoreLocal(tempDef);


        // Read the discriminant/payloads through the model-agnostic Result helpers
        // (field load for the E# stdlib, property getter for the C# seed), all hosted
        // on the closed instance. `guarded:false` is safe here — each load happens on
        // the branch that already proved the variant.
        void LoadTemp() => _il.LoadLocalAddress(tempDef);

        EmitResultMemberLoad(resultTypeRef, "IsError", LoadTemp, guarded: false);

        var continueLabel = _il.DefineLabel();
        _il.BranchIfFalse(continueLabel);


        // Error path. When the enclosing function returns `Result<…>`, propagate by
        // constructing a fresh error Result of the ENCLOSING return type (TOk from the
        // enclosing return; the inner Error reused as-is — shared TErr). When the
        // enclosing function does NOT return a Result, surface the error as an
        // exception — keeping the IL valid for the (dead, in well-typed code) edge.
        if (EnclosingReturnsResult())
        {
            var (funcOkRef, funcErrRef) = ResolveEnclosingResultTypeArgs(okRef, errRef);
            var openResult = module.ImportReference(ILTypeResolver.ResultOpenType());
            var funcResultClosed = new Mono.Cecil.GenericInstanceType(openResult);
            funcResultClosed.GenericArguments.Add(funcOkRef);
            funcResultClosed.GenericArguments.Add(funcErrRef);
            EmitResultConstruct(funcResultClosed, isOk: false, funcOkRef, funcErrRef,
                emitPayload: () => EmitResultMemberLoad(resultTypeRef, "Error", LoadTemp, guarded: false));
            // Route the early return through EmitReturnCore rather than a raw `ret`.
            // The propagated Result is on the stack, so pass `null` (the same contract
            // EmitReturn uses for nullable-nil). In an async MoveNext the override
            // stores to the result local + completes via builder.SetResult + leave —
            // a raw `ret` there would be invalid IL (MoveNext returns void).
            EmitReturnCore(null);
        }
        else
        {
            // error value (errRef) on the stack → throw InvalidOperationException(error.ToString()).
            EmitResultMemberLoad(resultTypeRef, "Error", LoadTemp, guarded: false);
            if (errRef.IsValueType)
                _il.Box(errRef);

            _il.CallVirt(module.ImportReference(typeof(object).GetMethod("ToString", Type.EmptyTypes)!));

            _il.NewObj(module.ImportReference(typeof(InvalidOperationException).GetConstructor([typeof(string)])!));

            _il.Throw();

        }

        // Continue path: push tmp.Value (the unwrapped okRef-typed result) onto the
        // stack. Callers consume it as any expression value — the let-decl path stores
        // it, return/arg positions use it in place, an expression statement pops it.
        _il.MarkLabel(continueLabel);
        EmitResultMemberLoad(resultTypeRef, "Value", LoadTemp, guarded: false);
    }

    // Dispatch an array element load/store: the typed primitive forms go through the
    // builder's EmitPrimitive, the generic `ldelem/stelem` forms carry the resolved
    // element TypeReference operand.
    void EmitArrayLoad(Type elementType)
    {
        var op = ArrayLoadOpcode(elementType);
        if (op == ILOpCode.Ldelem) _il.LoadElement(_types.ImportReference(elementType));
        else _il.EmitPrimitive(op);
    }

    void EmitArrayStore(Type elementType)
    {
        var op = ArrayStoreOpcode(elementType);
        if (op == ILOpCode.Stelem) _il.StoreElement(_types.ImportReference(elementType));
        else _il.EmitPrimitive(op);
    }

    // Array element store/load currency opcodes. ILOpCode (not Cecil OpCode) so the
    // call sites route through the builder's StoreElement/LoadElement verbs; the
    // `*_Any` fall-through carries a TypeReference operand, the rest are primitives.
    static ILOpCode ArrayStoreOpcode(Type elementType)
    {
        if (!elementType.IsValueType) return ILOpCode.Stelem_ref;
        if (elementType == typeof(byte) || elementType == typeof(sbyte) || elementType == typeof(bool)) return ILOpCode.Stelem_i1;
        if (elementType == typeof(short) || elementType == typeof(ushort) || elementType == typeof(char)) return ILOpCode.Stelem_i2;
        if (elementType == typeof(int) || elementType == typeof(uint)) return ILOpCode.Stelem_i4;
        if (elementType == typeof(long) || elementType == typeof(ulong)) return ILOpCode.Stelem_i8;
        if (elementType == typeof(float)) return ILOpCode.Stelem_r4;
        if (elementType == typeof(double)) return ILOpCode.Stelem_r8;
        return ILOpCode.Stelem;
    }

    static ILOpCode ArrayLoadOpcode(Type elementType)
    {
        if (!elementType.IsValueType) return ILOpCode.Ldelem_ref;
        if (elementType == typeof(byte) || elementType == typeof(bool)) return ILOpCode.Ldelem_u1;
        if (elementType == typeof(sbyte)) return ILOpCode.Ldelem_i1;
        if (elementType == typeof(short)) return ILOpCode.Ldelem_i2;
        if (elementType == typeof(ushort) || elementType == typeof(char)) return ILOpCode.Ldelem_u2;
        if (elementType == typeof(int)) return ILOpCode.Ldelem_i4;
        if (elementType == typeof(uint)) return ILOpCode.Ldelem_u4;
        if (elementType == typeof(long) || elementType == typeof(ulong)) return ILOpCode.Ldelem_i8;
        if (elementType == typeof(float)) return ILOpCode.Ldelem_r4;
        if (elementType == typeof(double)) return ILOpCode.Ldelem_r8;
        return ILOpCode.Ldelem;
    }

    /// Async override disables tail call: `ret` inside MoveNext has state-
    /// machine semantics and must go through the builder's SetResult path.
    private protected virtual bool CanUseTailCall() => true;

    /// <summary>Try to infer a concrete (non-object) type for an expression by examining the IL module's methods.</summary>
    TypeReference? InferConcreteType(BoundExpression expr)
    {
        // Direct bound type if it resolves to something concrete
        var resolved = _types.Resolve(expr.Type);
        if (resolved.FullName != "System.Object") return resolved;

        // Factory call: TypeName.caseName(args) — look up the static method's return type
        if (expr is BoundCallExpression { Target: BoundMemberAccessExpression { Target: BoundNameExpression typeName } ma } call)
        {
            foreach (var type in _types.Module.Types)
            {
                if (type.Name == typeName.Name)
                {
                    var factory = type.Methods.FirstOrDefault(m => m.Name == ma.MemberName && m.IsStatic && m.Parameters.Count == call.Arguments.Count);
                    if (factory is not null) return factory.ReturnType;
                }
            }
        }

        // Direct function call: look up the method's return type
        if (expr is BoundCallExpression { Target: BoundNameExpression funcName } directCall)
        {
            var method = FindMethod(funcName.Name, directCall.Arguments.Count);
            if (method is not null && method.ReturnType.FullName != "System.Void")
                return method.ReturnType;
        }

        // External static call: TypeName.MemberName(args) — resolve through
        // reflection so we get a closed return type when the method is generic.
        if (expr is BoundCallExpression
            {
                Target: BoundMemberAccessExpression { Target: BoundNameExpression staticTypeName } staticMemberAccess
            } externalCall)
        {
            var runtimeType = _types.TryResolveRuntimeType(staticTypeName.Name);
            if (runtimeType is not null)
            {
                var argRuntimeTypes = externalCall.Arguments
                    .Select(a => _types.BoundTypeToRuntime(a.Type) ?? typeof(object))
                    .ToArray();
                var methodInfo = ResolveRuntimeMethod(runtimeType, staticMemberAccess.MemberName, argRuntimeTypes);
                if (methodInfo is not null && methodInfo.ReturnType != typeof(void) && methodInfo.ReturnType != typeof(object))
                    return _types.Module.ImportReference(methodInfo.ReturnType);
            }
        }

        // `await e` produces the unwrapped T of Task<T>/ValueTask<T>. If the
        // binder didn't resolve the await's result type, look through to the
        // inner expression: an external static call resolves via reflection,
        // and generic Task<T>/ValueTask<T> returns have their element type
        // right there on the imported MethodReference.
        if (expr is BoundAwaitExpression aw)
        {
            // If the inner is a name referring to a slot we already allocated
            // (e.g. an async-let pending task stored into an SM field before
            // this await), use the slot's resolved Task<T> type directly.
            if (aw.Inner is BoundNameExpression innerName && TryResolveSlot(innerName.Name) is { } innerSlot)
            {
                var unwrappedSlot = UnwrapTaskType(innerSlot.Type);
                if (unwrappedSlot is not null && unwrappedSlot.FullName != "System.Object")
                    return unwrappedSlot;
            }

            var innerResolved = _types.Resolve(aw.Inner.Type);
            var unwrapped = UnwrapTaskType(innerResolved);
            if (unwrapped is not null && unwrapped.FullName != "System.Object") return unwrapped;

            // Static member call: Type.Method(args) — work directly from
            // reflection so we get a fully closed return type. Cecil's
            // MethodReference keeps ReturnType in open form (relative to the
            // method definition's generic parameters), which we can't use for
            // field declaration without closing it ourselves.
            if (aw.Inner is BoundCallExpression { Target: BoundMemberAccessExpression { Target: BoundNameExpression staticType } staticMa } staticCall)
            {
                var runtimeType = _types.TryResolveRuntimeType(staticType.Name);
                if (runtimeType is not null)
                {
                    var argRuntimeTypes = staticCall.Arguments.Select(a => _types.BoundTypeToRuntime(a.Type) ?? typeof(object)).ToArray();
                    var methodInfo = ResolveRuntimeMethod(runtimeType, staticMa.MemberName, argRuntimeTypes);
                    if (methodInfo is not null)
                    {
                        var returnRuntime = methodInfo.ReturnType;
                        var unwrappedRuntime = UnwrapTaskRuntimeType(returnRuntime);
                        if (unwrappedRuntime is not null && unwrappedRuntime != typeof(object))
                            return _types.Module.ImportReference(unwrappedRuntime);
                    }
                }
            }
        }

        // External instance member read: `recv.Member` (property or field) on an
        // external type. The binder leaves some external members — notably a value-
        // typed member on an *interface* receiver (e.g. `IReadOnlyList<int>.Count`) —
        // typed as object, so the interpolation/`let` boxing decision is skipped and
        // the emitted IL fails verification (Int32 where ref 'object' is expected).
        // Resolve the real member type via reflection, walking the interface
        // hierarchy (GetProperty does not surface members inherited from base
        // interfaces — `Count` lives on IReadOnlyCollection<T>, not IReadOnlyList<T>).
        if (expr is BoundMemberAccessExpression { Target: { } recv } memberAccess)
        {
            var recvRuntime = _types.BoundTypeToRuntime(recv.Type);
            if (recvRuntime is not null && recvRuntime != typeof(object))
            {
                foreach (var t in MemberSearchTypes(recvRuntime))
                {
                    var prop = t.GetProperty(memberAccess.MemberName);
                    if (prop is not null && prop.PropertyType != typeof(void) && prop.PropertyType != typeof(object))
                        return _types.Module.ImportReference(prop.PropertyType);
                    var field = t.GetField(memberAccess.MemberName);
                    if (field is not null && field.FieldType != typeof(object))
                        return _types.Module.ImportReference(field.FieldType);
                }
            }
        }

        return null;
    }

    // A type plus, for an interface, the interfaces it inherits — because
    // Type.GetProperty/GetField on an interface does not surface members declared
    // on its base interfaces (IReadOnlyList<T>'s `Count` lives on IReadOnlyCollection<T>).
    static IEnumerable<Type> MemberSearchTypes(Type t)
        => t.IsInterface ? new[] { t }.Concat(t.GetInterfaces()) : new[] { t };

    /// Map a Cecil TypeReference back to a System.Type. Only handles Task /
    /// ValueTask shapes — everything else returns null and the caller should
    /// fall back to whatever it had.
    Type? CecilToRuntimeType(TypeReference tr)
    {
        if (tr is GenericInstanceType git)
        {
            var elem = git.ElementType.FullName;
            // Closed generic types: resolve the open type + each arg, then close it.
            // Without this, an outer ValueTask<Result<int,string>>'s inner Result<,>
            // returned null and the await emitter fell back to Task<EnclosingReturnType>,
            // producing TaskAwaiter<int> for what should be a ValueTaskAwaiter<Result<,>>.
            var argRts = new Type[git.GenericArguments.Count];
            for (var i = 0; i < git.GenericArguments.Count; i++)
            {
                var rt = CecilToRuntimeType(git.GenericArguments[i]);
                if (rt is null) return null;
                argRts[i] = rt;
            }
            // Map well-known BCL element types first, then fall back to reflection.
            Type? openType = elem switch
            {
                "System.Threading.Tasks.Task`1" => typeof(System.Threading.Tasks.Task<>),
                "System.Threading.Tasks.ValueTask`1" => typeof(System.Threading.Tasks.ValueTask<>),
                _ => null,
            };
            if (openType is null)
            {
                // Try reflection: look up the open generic by full name across loaded
                // assemblies (covers Esharp.Stdlib.Result`2, BCL collections, etc.).
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { openType = asm.GetType(elem, throwOnError: false); }
                    catch { openType = null; }
                    if (openType is not null) break;
                }
            }
            if (openType is not null && openType.IsGenericTypeDefinition && openType.GetGenericArguments().Length == argRts.Length)
            {
                try { return openType.MakeGenericType(argRts); }
                catch { return null; }
            }
            return null;
        }
        switch (tr.FullName)
        {
            case "System.Threading.Tasks.Task": return typeof(System.Threading.Tasks.Task);
            case "System.Threading.Tasks.ValueTask": return typeof(System.Threading.Tasks.ValueTask);
            case "System.Int32": return typeof(int);
            case "System.Int64": return typeof(long);
            case "System.Boolean": return typeof(bool);
            case "System.Double": return typeof(double);
            case "System.String": return typeof(string);
            case "System.Void": return typeof(void);
        }
        // Non-generic user/BCL type: best-effort reflection lookup.
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(tr.FullName, throwOnError: false);
                if (t is not null) return t;
            }
            catch { }
        }
        return null;
    }

    /// If `type` is `Task<T>`, `ValueTask<T>`, `Task`, or `ValueTask`, return the
    /// unwrapped result type reference: `T` for generic, void for non-generic.
    /// Returns null if T is an unresolved generic parameter — such a reference
    /// cannot be written into a field signature.
    TypeReference? UnwrapTaskType(TypeReference type)
    {
        if (type is GenericInstanceType git)
        {
            var name = git.ElementType.FullName;
            if (name == "System.Threading.Tasks.Task`1" || name == "System.Threading.Tasks.ValueTask`1")
            {
                var inner = git.GenericArguments[0];
                return inner.IsGenericParameter ? null : inner;
            }
        }
        if (type.FullName == "System.Threading.Tasks.Task" || type.FullName == "System.Threading.Tasks.ValueTask")
            return _types.Module.ImportReference(typeof(void));
        return null;
    }

    TypeReference ResolveDelegateType(BoundFunctionLiteralExpression fl)
    {
        var module = _types.Module;
        var retType = _types.Resolve(fl.ReturnType);
        var paramTypes = fl.Parameters.Select(p => _types.Resolve(p.Type)).ToList();

        Type openType;
        if (retType.FullName == "System.Void")
        {
            openType = paramTypes.Count switch
            {
                0 => typeof(Action),
                1 => typeof(Action<>),
                2 => typeof(Action<,>),
                3 => typeof(Action<,,>),
                _ => typeof(Action),
            };
        }
        else
        {
            openType = paramTypes.Count switch
            {
                0 => typeof(Func<>),
                1 => typeof(Func<,>),
                2 => typeof(Func<,,>),
                3 => typeof(Func<,,,>),
                _ => typeof(Func<>),
            };
        }

        if (!openType.IsGenericTypeDefinition)
            return module.ImportReference(openType);

        // Close the generic type with concrete type args
        var typeArgs = new List<Type>();
        foreach (var pt in paramTypes)
        {
            var rt = _types.BoundTypeToRuntime(fl.Parameters[typeArgs.Count].Type);
            typeArgs.Add(rt ?? typeof(object));
        }
        if (retType.FullName != "System.Void")
        {
            var rrt = _types.BoundTypeToRuntime(fl.ReturnType);
            typeArgs.Add(rrt ?? typeof(object));
        }

        var closedType = openType.MakeGenericType(typeArgs.ToArray());
        return module.ImportReference(closedType);
    }

    void EmitDefaultNullable(TypeReference nullableType)
    {
        var local = new VariableDefinition(nullableType);
        _method.Body.Variables.Add(local);
        _il.LoadLocalAddress(local);

        _il.InitObj(nullableType);

        _il.LoadLocal(local);

    }

    void EmitMatchExprArms_Literal(BoundMatchExpression m, VariableDefinition subjectLocal, VariableDefinition resultLocal, ILLabel endLabel)
    {
        var fallthrough = endLabel;
        var defaultArm = m.Arms.FirstOrDefault(a => a.Pattern.IsDefault);
        ILLabel? defaultLabel = null;
        if (defaultArm is not null) { defaultLabel = _il.DefineLabel("matchDefault"); fallthrough = defaultLabel; }

        var caseLabels = new List<(ILLabel label, BoundMatchExpressionArm arm)>();
        foreach (var arm in m.Arms)
        {
            if (arm.Pattern.IsDefault || arm.Pattern.LiteralValue is null) continue;
            var label = _il.DefineLabel();
            caseLabels.Add((label, arm));

            var lit = arm.Pattern.LiteralValue;
            if (lit is BoundLiteralExpression { Value: string })
            {
                _il.LoadLocal(subjectLocal);

                EmitExpression(lit);
                var stringEquals = _types.Module.ImportReference(typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) }));
                _il.Call(stringEquals);

                _il.BranchIfTrue(label);

            }
            else if (lit is BoundLiteralExpression { Value: bool boolVal })
            {
                _il.LoadLocal(subjectLocal);

                _il.LoadInt(boolVal ? 1 : 0);

                _il.BranchRelational(ILOpCode.Beq, label);

            }
            else
            {
                _il.LoadLocal(subjectLocal);

                EmitExpression(lit);
                _il.BranchRelational(ILOpCode.Beq, label);

            }
        }

        _il.Branch(fallthrough);


        foreach (var (label, arm) in caseLabels)
        {
            _il.MarkLabel(label);
            EmitExpression(arm.Value);
            _il.StoreLocal(resultLocal);

            _il.Branch(endLabel);

        }

        if (defaultArm is not null && defaultLabel is not null)
        {
            _il.MarkLabel(defaultLabel);
            EmitExpression(defaultArm.Value);
            _il.StoreLocal(resultLocal);

            _il.Branch(endLabel);

        }
    }

    void EmitMatchExprArms_Enum(BoundMatchExpression m, VariableDefinition subjectLocal, VariableDefinition resultLocal, ILLabel endLabel)
    {
        var enumType = (EnumType)m.SubjectType;
        TypeDefinition? enumTypeDef = null;
        foreach (var t in _types.Module.Types)
            if (t.Name == enumType.Name && t.IsEnum) { enumTypeDef = t; break; }

        var fallthrough = endLabel;
        var defaultArm = m.Arms.FirstOrDefault(a => a.Pattern.IsDefault);
        ILLabel? defaultLabel = null;
        if (defaultArm is not null) { defaultLabel = _il.DefineLabel("matchDefault"); fallthrough = defaultLabel; }

        var caseLabels = new List<(ILLabel label, BoundMatchExpressionArm arm)>();
        foreach (var arm in m.Arms)
        {
            if (arm.Pattern.IsDefault) continue;
            var label = _il.DefineLabel();
            caseLabels.Add((label, arm));

            _il.LoadLocal(subjectLocal);

            var field = enumTypeDef?.Fields.FirstOrDefault(f => f.IsStatic && f.IsLiteral && f.Name == arm.Pattern.CaseName);
            _il.LoadInt(field?.Constant is int v ? v : 0);

            _il.BranchRelational(ILOpCode.Beq, label);

        }

        _il.Branch(fallthrough);


        foreach (var (label, arm) in caseLabels)
        {
            _il.MarkLabel(label);
            EmitExpression(arm.Value);
            _il.StoreLocal(resultLocal);

            _il.Branch(endLabel);

        }

        if (defaultArm is not null && defaultLabel is not null)
        {
            _il.MarkLabel(defaultLabel);
            EmitExpression(defaultArm.Value);
            _il.StoreLocal(resultLocal);

            _il.Branch(endLabel);

        }
    }

    void EmitMatchExprArms_RefChoice(BoundMatchExpression m, VariableDefinition subjectLocal, VariableDefinition resultLocal, ILLabel endLabel, ChoiceType refCt)
    {
        foreach (var arm in m.Arms)
        {
            if (arm.Pattern.IsDefault)
            {
                EmitExpression(arm.Value);
                _il.StoreLocal(resultLocal);

                _il.Branch(endLabel);

                continue;
            }

            var variantName = $"{refCt.Name}_{arm.Pattern.CaseName}";
            var variantType = _types.TryResolveRegistered(variantName);
            if (variantType is null) continue;
            // Reified generic ref union: dispatch and bind on the closed subclass.
            TypeReference variantRef = ClosedRefVariant(variantType, refCt);

            var nextArm = _il.DefineLabel();
            _il.LoadLocal(subjectLocal);

            _il.IsInst(variantRef);

            _il.BranchIfFalse(nextArm);


            if (arm.Pattern.Bindings is { Count: > 0 } refBindings)
                EmitRefChoiceBindings(refBindings, subjectLocal, variantRef);

            EmitExpression(arm.Value);
            _il.StoreLocal(resultLocal);

            _il.Branch(endLabel);

            _il.MarkLabel(nextArm);
        }
    }

    void EmitMatchExprArms_ValueChoice(BoundMatchExpression m, VariableDefinition subjectLocal, VariableDefinition resultLocal, ILLabel endLabel)
    {
        // Load tag
        var choiceTypeRef = _types.Resolve(m.SubjectType);
        _il.LoadLocalAddress(subjectLocal);

        var tagField = FindFieldOnType(choiceTypeRef, "Tag");
        if (tagField is not null)
            _il.LoadField(tagField);


        var caseArms = m.Arms.Where(a => !a.Pattern.IsDefault && a.Pattern.CaseName is not null).ToList();
        var defaultArm = m.Arms.FirstOrDefault(a => a.Pattern.IsDefault);

        var armLabels = caseArms.Select(_ => _il.DefineLabel()).ToArray();
        var defaultArmLabel = _il.DefineLabel();

        _il.Switch(armLabels);
        _il.Branch(defaultArm is not null ? defaultArmLabel : endLabel);


        for (var i = 0; i < caseArms.Count; i++)
        {
            _il.MarkLabel(armLabels[i]);
            var arm = caseArms[i];

            if (arm.Pattern.Bindings is { Count: > 0 })
            {
                foreach (var binding in arm.Pattern.Bindings)
                {
                    var payloadFieldName = $"{arm.Pattern.CaseName}_{binding.PayloadFieldName}";
                    var payloadField = FindFieldOnType(choiceTypeRef, payloadFieldName);
                    if (payloadField is not null)
                    {
                        DeclareLocal(binding.Name, _types.Resolve(binding.Type));
                        var bindingSlot = TryResolveSlot(binding.Name)!;
                        bindingSlot.EmitStore(_il, () =>
                        {
                            _il.LoadLocalAddress(subjectLocal);

                            _il.LoadField(payloadField);

                        });
                    }
                }
            }

            EmitExpression(arm.Value);
            _il.StoreLocal(resultLocal);

            _il.Branch(endLabel);

        }

        if (defaultArm is not null)
        {
            _il.MarkLabel(defaultArmLabel);
            EmitExpression(defaultArm.Value);
            _il.StoreLocal(resultLocal);

            _il.Branch(endLabel);

        }
    }

    // === Expressions — each pushes exactly one value onto the stack ===

    public void EmitExpression(BoundExpression expr)
    {
        switch (expr)
        {
            case BoundLiteralExpression lit:
                EmitLiteral(lit);
                break;
            // ---- FEATURE expression nodes: unreachable post-lowering ----
            case BoundTryUnwrapExpression:
                throw new FeatureNodeInCodeGenException(
                    "BoundTryUnwrapExpression (?) reached EmitExpression. ResultLowering must have failed.");
            // CORE leaf — emitted directly as string.Concat(object[]), boxing value-type holes
            // (the faithful ESC shape). A hole's own FEATURE nodes were lowered by the rewriter
            // descending into each part before CodeGen ran.
            case BoundInterpolatedStringExpression interp:
                EmitInterpolatedExpression(interp);
                break;
            case BoundNameExpression name:
                EmitNameLoad(name);
                break;
            case BoundBinaryExpression bin:
                EmitBinary(bin);
                break;
            case BoundUnaryExpression unary:
                EmitUnary(unary);
                break;
            case BoundMemberAccessExpression ma:
                EmitMemberAccess(ma);
                break;
            case BoundTypeTestExpression tt:
                EmitTypeTest(tt);
                break;
            // BoundConversion is the single CORE cast/narrow node (spine-deltas §3).
            // BoundSafeCastExpression / BoundAssertCastExpression / BoundNarrowedExpression
            // no longer exist — the binder produces BoundConversion via factory methods.
            case BoundConversion conv:
                EmitConversion(conv);
                break;
            case BoundCallExpression call:
                EmitCall(call);
                break;
            case BoundObjectCreationExpression oc:
                EmitObjectCreation(oc);
                break;
            case BoundWithExpression:
                throw new FeatureNodeInCodeGenException(
                    "BoundWithExpression reached EmitExpression. WithLowering must have failed.");
            case BoundConditionalExpression cond:
                // Ternary is CORE — emitted directly as a branch, exactly as the ESC oracle
                // does (ILMethodEmitter.EmitConditional). Lowering only rewrites a ternary
                // when a branch produces hoisted statements (SpillingBoundTreeRewriter), so a
                // surviving plain ternary is correct here, not a lowering failure.
                EmitConditional(cond);
                break;
            case BoundNullCoalescingExpression:
                throw new FeatureNodeInCodeGenException(
                    "BoundNullCoalescingExpression (??) reached EmitExpression. NullFlowLowering must have failed.");
            case BoundNullConditionalAccessExpression nca2:
                throw new FeatureNodeInCodeGenException(
                    $"BoundNullConditionalAccessExpression (?.{nca2.MemberName}) reached EmitExpression. NullFlowLowering must have failed.");
            case BoundListLiteralExpression list:
                EmitListLiteral(list);
                break;
            case BoundArrayCreationExpression arr:
                EmitExpression(arr.Size);
                _il.NewArray(_types.Resolve(arr.ElementType));

                _lastCallWasVoid = false;
                break;
            case BoundTupleLiteralExpression tuple:
                EmitTupleLiteral(tuple);
                break;
            case BoundDotCaseExpression dc:
                EmitDotCase(dc);
                break;
            case BoundAddressOfExpression ao:
                EmitAddressOf(ao);
                break;
            case BoundMethodGroupConversion mg:
                EmitMethodGroupConversion(mg);
                break;
            case BoundAddressOfVariableExpression aov:
                EmitAddressOfVariable(aov);
                break;
            case BoundHeapAllocExpression ha:
                EmitHeapAlloc(ha);
                break;
            case BoundMatchExpression:
                throw new FeatureNodeInCodeGenException(
                    "BoundMatchExpression reached EmitExpression. MatchLowering must have failed.");
            case BoundIfExpression:
                throw new FeatureNodeInCodeGenException(
                    "BoundIfExpression reached EmitExpression. ExpressionFormLowering must have failed.");
            case BoundAwaitExpression:
                throw new FeatureNodeInCodeGenException(
                    "BoundAwaitExpression reached EmitExpression. AsyncLowering must have failed.");
            case BoundResultCallExpression:
                throw new FeatureNodeInCodeGenException(
                    "BoundResultCallExpression reached EmitExpression. ResultLowering must have failed.");
            case BoundIndexExpression idx:
                EmitIndexRead(idx);
                break;
            case BoundFunctionLiteralExpression:
                throw new FeatureNodeInCodeGenException(
                    "BoundFunctionLiteralExpression reached EmitExpression. ClosureConversion must have failed.");
            case BoundSpawnExpression:
                throw new FeatureNodeInCodeGenException(
                    "BoundSpawnExpression reached EmitExpression. ConcurrencyLowering must have failed.");
            case BoundChanCreationExpression:
                throw new FeatureNodeInCodeGenException(
                    "BoundChanCreationExpression reached EmitExpression. ConcurrencyLowering must have failed.");
            case BoundDefaultExpression def:
                EmitDefault(def);
                break;
            case BoundOutArgumentExpression:
                // out arguments are handled by EmitCallArgs. If we land here it means
                // the user wrote `out x` outside a call — report it.
                _diagnostics.Report("", 0, 0, "IL: 'out' argument used outside a call context");
                break;
        }
    }

    void EnsureDisplayClass()
    {
        if (_displayClass is not null) return;

        var module = _types.Module;
        var closureId = _types.NextClosureId();
        var displayName = $"<>c__Display_{closureId}";
        _displayClass = new TypeDefinition(
            "", displayName,
            TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            module.ImportReference(typeof(object)));

        _displayCtor = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.ImportReference(typeof(void)));
        var ctorIl = new ILBuilder(_displayCtor);
        ctorIl.LoadArg(_displayCtor.Body.ThisParameter);
        ctorIl.Call(module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
        ctorIl.Ret(returnsValue: false);
        _displayClass.Methods.Add(_displayCtor);

        _displayClassFields = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);

        module.Types.Add(_displayClass);

        _il.NewObj(_displayCtor);

        _displayLocal = new VariableDefinition(_displayClass);
        _method.Body.Variables.Add(_displayLocal);
        _il.StoreLocal(_displayLocal);

    }

    MethodReference MakeDelegateCtor(TypeReference delegateType, TypeReference voidRef, TypeReference objectRef, TypeReference intPtrRef)
    {
        var ctor = new MethodReference(".ctor", voidRef, delegateType)
        {
            HasThis = true,
        };
        ctor.Parameters.Add(new ParameterDefinition(objectRef));
        ctor.Parameters.Add(new ParameterDefinition(intPtrRef));
        return ctor;
    }

    /// Emit an instance method call on a Chan&lt;T&gt; receiver, handling the
    /// case where T is a user-defined type (choice/data) and so MakeGenericType
    /// on the stdlib <c>Esharp.Stdlib.Chan&lt;&gt;</c> would fail. We import
    /// the method from the open runtime type, then construct a MethodReference
    /// rehomed on the closed GenericInstanceType. The parameter types stay as
    /// open generic-parameter references — the CLR substitutes T against the
    /// DeclaringType's closed arguments at IL resolution time, which is how
    /// Roslyn emits the same pattern.
    bool TryEmitChanInstanceCall(ChanType chanTy, TypeReference closedChanRef, BoundMemberAccessExpression ma, IReadOnlyList<BoundExpression> args)
    {
        // Route through Esharp.Stdlib.ChanOps.<Op>&lt;T&gt;(Chan&lt;T&gt; ch, ...)
        // as a generic-static call. Cecil's GenericInstanceMethod handles
        // Cecil-only type arguments (user choices/data) cleanly, whereas
        // rooting a methodref on a closed generic <i>type</i> requires
        // manual TypeRef/GenericParameter surgery that the CLR rejects.
        var module = _types.Module;
        if (closedChanRef is not GenericInstanceType closed) return false;
        var elementRef = closed.GenericArguments[0];

        // Find the matching ChanOps<Op> method. ch goes as arg0, so the
        // static helper has one extra parameter than the source-level call.
        var helperArity = args.Count + 1;
        var openHelper = ILTypeResolver.ChanOpsType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .FirstOrDefault(m => m.Name == ma.MemberName && m.GetParameters().Length == helperArity);
        if (openHelper is null) return false;

        var openHelperRef = module.ImportReference(openHelper);
        var genericInstance = new GenericInstanceMethod(openHelperRef);
        genericInstance.GenericArguments.Add(elementRef);

        EmitExpression(ma.Target);
        foreach (var arg in args)
            EmitExpression(arg);
        _il.Call(genericInstance);

        if (openHelper.ReturnType == typeof(void)) _lastCallWasVoid = true;
        return true;
    }

    MethodReference? ResolveChanMethodRef(TypeReference chanRef, Type chanRuntime, string methodName, int? argCount = null)
    {
        var module = _types.Module;

        // chanRuntime is the closed Chan<T> runtime Type (MakeGenericType'd from BoundTypeToRuntime).
        // Get the method directly from the closed type so Cecil.ImportReference has no generic
        // parameters to resolve.
        if (!chanRuntime.IsGenericType) return null;

        var method = chanRuntime.GetMethods()
            .FirstOrDefault(m => m.Name == methodName &&
                (argCount is null || m.GetParameters().Length == argCount));
        if (method is null) return null;

        return module.ImportReference(method);
    }

    /// Resolves a *T first-param static method call using either syntax:
    /// Static: prepend(list, 10) — direct match on module static methods
    /// Dot:    v.addX(5) — receiver becomes arg0 of the static method
    /// Returns false if no matching static method found.
    bool TryEmitHeapPointerStaticCall(string methodName, IReadOnlyList<BoundExpression> args)
    {
        // Find the static method on the module class with matching name and arg count
        var moduleClass = _types.Module.Types.FirstOrDefault(t =>
            t.Methods.Any(m => m.IsStatic && m.Name == methodName && m.Parameters.Count == args.Count));
        if (moduleClass is null) return false;

        var method = moduleClass.Methods.First(m => m.IsStatic && m.Name == methodName && m.Parameters.Count == args.Count);

        EmitCallArgsCoerced(args, method);
        _il.Call(method);

        if (method.ReturnType.FullName == "System.Void") _lastCallWasVoid = true;
        return true;
    }

    /// Whether a member access targets a *T heap pointer field (not the self param).
    /// Used to route through auto-deref emission instead of normal field access.
    bool IsHeapPointerDeref(BoundMemberAccessExpression ma, out HeapPointerBoundType hp)
    {
        if (ma.Target.Type is HeapPointerBoundType h
            && !(ma.Target is BoundNameExpression sn && IsSelfReference(sn.Name)))
        {
            hp = h;
            return true;
        }
        hp = null!;
        return false;
    }

    // E# string interpolation uses `{identifier}` where the first char is a letter or underscore.
    // `{0}`, `{0:d}`, and other BCL format strings (e.g. `string.Format("{0}", x)`) start with a
    // digit and are left alone so they can be passed to the BCL formatter unchanged.
    // Compute runtime arg types without emitting any IL — used for method resolution so we
    // can pick the right overload (including params) before committing to an emit shape.
    Type[] CollectCallArgTypes(IReadOnlyList<BoundExpression> args)
    {
        var result = new Type[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is BoundOutArgumentExpression outArg)
            {
                var runtimeSlot = _types.BoundTypeToRuntime(outArg.SlotType) ?? typeof(object);
                result[i] = runtimeSlot.MakeByRefType();
            }
            else if (arg is BoundFunctionLiteralExpression fl)
            {
                // A lambda's bound `.Type` is `var`, so the default path would type it
                // `object` — losing the `Func<int,int>` shape a generic method needs to
                // infer its type args (e.g. Result.Map<TNew>). Reconstruct the real
                // delegate type from the (body-inferred) param/return types.
                result[i] = RuntimeDelegateType(fl) ?? typeof(object);
            }
            else
            {
                result[i] = _types.BoundTypeToRuntime(arg.Type) ?? typeof(object);
            }
        }
        return result;
    }

    /// The runtime `Func<…>` / `Action<…>` type for a lambda, from its param and
    /// (inferred) return types — used as the arg type for external generic-method
    /// resolution so the callee's method type args infer from the delegate shape.
    Type? RuntimeDelegateType(BoundFunctionLiteralExpression fl)
    {
        var ps = fl.Parameters.Select(p => _types.BoundTypeToRuntime(p.Type) ?? typeof(object)).ToArray();
        // Void return (however the binder spelled it) → Action; `void` is not a valid
        // generic type argument, so it can never feed a `Func<…>` closure.
        var ret = fl.ReturnType is Esharp.BoundTree.VoidType ? typeof(void)
                  : (_types.BoundTypeToRuntime(fl.ReturnType) ?? typeof(object));
        if (ret == typeof(void))
        {
            Type? openA = ps.Length switch { 0 => typeof(Action), 1 => typeof(Action<>), 2 => typeof(Action<,>), 3 => typeof(Action<,,>), _ => null };
            if (openA is null) return null;
            return ps.Length == 0 ? openA : openA.MakeGenericType(ps);
        }
        Type? openF = ps.Length switch { 0 => typeof(Func<>), 1 => typeof(Func<,>), 2 => typeof(Func<,,>), 3 => typeof(Func<,,,>), _ => null };
        if (openF is null) return null;
        return openF.MakeGenericType([.. ps, ret]);
    }

    // Emits `args` packed into a params-array tail starting at `fixedCount`. The first
    // `fixedCount` args are emitted normally, and the rest are gathered into a new `elementType[]`.
    void EmitCallArgsWithParams(IReadOnlyList<BoundExpression> args, int fixedCount, Type elementType)
    {
        for (var i = 0; i < fixedCount; i++)
            EmitSingleCallArg(args[i]);

        var tailCount = args.Count - fixedCount;
        var elementTypeRef = _types.Module.ImportReference(elementType);

        _il.LoadInt(tailCount);

        _il.NewArray(elementTypeRef);


        for (var i = 0; i < tailCount; i++)
        {
            _il.Dup();

            _il.LoadInt(i);

            var arg = args[fixedCount + i];
            EmitSingleCallArg(arg);
            // Box value types if the element is object
            if (elementType == typeof(object))
            {
                var argRuntime = _types.BoundTypeToRuntime(arg.Type) ?? typeof(object);
                if (argRuntime.IsValueType)
                    _il.Box(_types.Module.ImportReference(argRuntime));

            }
            _il.EmitPrimitive(ILOpCode.Stelem_ref);

        }
    }

    // Emits call arguments, boxing value-type args when the callee parameter is an erased
    // reference type (System.Object or an interface). We deliberately do NOT box when the
    // parameter is a generic parameter like `!0` — the CLR substitution handles that at the
    // closed call site, and pre-boxing there would produce invalid IL.
    void EmitCallArgsCoerced(IReadOnlyList<BoundExpression> args, MethodReference methodRef,
        int parameterOffset = 0, Func<int, bool>? emitOverride = null)
    {
        var parameters = methodRef.Parameters;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            var pIdx = i + parameterOffset;

            if (emitOverride?.Invoke(i) == true)
                continue;

            // Representation-driven `*T` coercion: reconcile the argument's pointer
            // form with the parameter's (wrapper vs `ref T`). Must precede the
            // syntactic `*x` path so a wrapper passed as `*p` doesn't `ldflda` a
            // reference type (the old invalid-IL bug).
            if (pIdx < parameters.Count && TryEmitPointerArg(arg, parameters[pIdx].ParameterType))
                continue;

            if (arg is BoundOutArgumentExpression or BoundUnaryExpression { Op: SyntaxTokenKind.Star })
            {
                EmitSingleCallArg(arg);
                continue;
            }

            // Implicit `T` → `Nullable<T>` coercion at the call site: a bare value or
            // `nil` flowing into a `T?` parameter. `nil` becomes `default(Nullable<T>)`
            // (initobj — `ldnull` is invalid for a value type); a `T` value is wrapped
            // via `new Nullable<T>(value)`. An argument already typed `T?` passes
            // through untouched.
            if (pIdx < parameters.Count
                && EffectiveParameterType(methodRef, pIdx) is { } effParam
                && IsNullableValueType(effParam)
                && arg.Type is not Esharp.BoundTree.NullableType)
            {
                var nullableParam = effParam;
                if (arg is BoundLiteralExpression { Value: null } || arg.Type is Esharp.BoundTree.NullType)
                {
                    var tmp = new VariableDefinition(nullableParam);
                    _method.Body.Variables.Add(tmp);
                    _il.LoadLocalAddress(tmp);

                    _il.InitObj(nullableParam);

                    _il.LoadLocal(tmp);

                }
                else
                {
                    EmitExpression(arg);
                    _il.NewObj(NullableCtor(nullableParam));

                }
                continue;
            }

            var parameterIndex = i + parameterOffset;
            if (arg is BoundFunctionLiteralExpression or BoundMethodGroupConversion)
            {
                // Steer the lambda to the parameter's delegate type ONLY when it's fully
                // closed (a named delegate, or a closed Func/Action). An OPEN generic
                // param — e.g. `Func<!0,!!0>` for a generic combinator like
                // `Result.Map<TNew>(Func<TValue,TNew>)` — would materialize an invalid
                // open delegate; the lambda's own (binder-inferred) param/return types
                // give the correct closed delegate, so leave the target null there.
                var ptype = parameterIndex < parameters.Count ? parameters[parameterIndex].ParameterType : null;
                _pendingDelegateTargetType = ptype is { ContainsGenericParameter: false } ? ptype : null;
            }
            EmitExpression(arg);
            if (arg is BoundFunctionLiteralExpression or BoundMethodGroupConversion)
                _pendingDelegateTargetType = null;

            if (parameterIndex >= parameters.Count) continue;
            // Use the *substituted* parameter type: an `Add(!0)` hosted on
            // `List<object>` has effective parameter `object`, so a value-type arg
            // must be boxed. Keying off the raw `!0` skipped the box and produced
            // unverifiable IL (StackUnexpected: Int32 where ref 'object' expected).
            var paramType = EffectiveParameterType(methodRef, parameterIndex);
            // Still an unsubstituted generic parameter (e.g. a method-level `!!0`
            // with no closed instance) — the CLR substitution handles it at the
            // closed call site; pre-boxing there would be invalid IL.
            if (paramType.IsGenericParameter || paramType.ContainsGenericParameter) continue;
            if (paramType.IsByReference) continue;
            if (paramType.IsValueType) continue;

            // Param is (or substitutes to) a reference type — box the value arg if necessary.
            var argRuntime = _types.BoundTypeToRuntime(arg.Type);
            if (argRuntime is not null && argRuntime.IsValueType)
            {
                _il.Box(_types.Module.ImportReference(argRuntime));

            }
            else if (argRuntime is null && _types.IsValueType(arg.Type))
            {
                // User-defined struct passed to interface/object param — box via Cecil type
                _il.Box(_types.Resolve(arg.Type));

            }
        }

        for (var i = args.Count + parameterOffset; i < parameters.Count; i++)
            EmitOptionalArgument(parameters[i]);
    }

    /// The parameter type with any open generic parameter (`!0`) substituted by the
    /// declaring closed instance's argument — so an `Add(!0)` hosted on
    /// `List<Nullable<int>>` reads as `Add(Nullable<int>)`, which the call-site
    /// coercion needs to see to wrap a bare value/`nil` into the `Nullable<T>`.
    static TypeReference EffectiveParameterType(MethodReference m, int idx)
    {
        var pt = m.Parameters[idx].ParameterType;
        if (pt is GenericParameter { Type: GenericParameterType.Type } gp
            && m.DeclaringType is GenericInstanceType dgit
            && gp.Position < dgit.GenericArguments.Count)
            return dgit.GenericArguments[gp.Position];
        return pt;
    }

    void EmitOptionalArgument(ParameterDefinition parameter)
    {
        if (parameter.HasConstant)
        {
            switch (parameter.Constant)
            {
                case null:
                    // null constant on a value type means default(T) — use initobj, not ldnull
                    if (parameter.ParameterType.IsValueType)
                        break; // fall through to the initobj path below
                    _il.LoadNull();

                    return;
                case int i:
                    _il.LoadInt(i);

                    return;
                case bool b:
                    _il.LoadInt(b ? 1 : 0);

                    return;
                case string s:
                    _il.LoadString(s);

                    return;
                case double d:
                    _il.LoadDouble(d);

                    return;
                case float f:
                    _il.LoadFloat(f);

                    return;
                case long l:
                    _il.LoadLong(l);

                    return;
                case ulong ul:
                    _il.LoadLong(unchecked((long)ul));

                    return;
                case char c:
                    _il.LoadInt(c);

                    return;
                // Smaller integral constants (incl. enum underlying values that arrive as
                // their CLR primitive) all load via ldc.i4.
                case uint ui:
                    _il.LoadInt(unchecked((int)ui));

                    return;
                case short sh:
                    _il.LoadInt(sh);

                    return;
                case ushort ush:
                    _il.LoadInt(ush);

                    return;
                case byte by:
                    _il.LoadInt(by);

                    return;
                case sbyte sb:
                    _il.LoadInt(sb);

                    return;
            }
        }

        if (!parameter.ParameterType.IsValueType || parameter.ParameterType.IsGenericParameter)
        {
            _il.LoadNull();

            return;
        }

        var local = new VariableDefinition(parameter.ParameterType);
        _method.Body.Variables.Add(local);
        _il.LoadLocalAddress(local);

        _il.InitObj(parameter.ParameterType);

        _il.LoadLocal(local);

    }

    /// The reflection-path sibling of EmitOptionalArgument: loads an omitted optional
    /// parameter's default from its ParameterInfo. An imported MethodReference's
    /// ParameterDefinitions don't carry constants, so the reflection metadata is the
    /// source of truth here (external constructors resolve through reflection).
    void EmitReflectionOptionalDefault(System.Reflection.ParameterInfo p)
    {
        var dv = p.RawDefaultValue;
        switch (dv)
        {
            case int i: _il.LoadInt(i); return;
            case bool b: _il.LoadInt(b ? 1 : 0); return;
            case string s: _il.LoadString(s); return;
            case double d: _il.LoadDouble(d); return;
            case float f: _il.LoadFloat(f); return;
            case long l: _il.LoadLong(l); return;
            case ulong ul: _il.LoadLong(unchecked((long)ul)); return;
            case char c: _il.LoadInt(c); return;
            case uint ui: _il.LoadInt(unchecked((int)ui)); return;
            case short sh: _il.LoadInt(sh); return;
            case ushort ush: _il.LoadInt(ush); return;
            case byte by: _il.LoadInt(by); return;
            case sbyte sb: _il.LoadInt(sb); return;
        }
        // An enum default arrives as the boxed enum value — load its underlying int.
        if (dv is not null && dv is not DBNull && dv.GetType().IsEnum)
        {
            _il.LoadInt(Convert.ToInt32(dv));

            return;
        }
        // null / DBNull / Missing: ldnull for a reference type, default(T) for a value type.
        var pt = p.ParameterType;
        if (!pt.IsValueType)
        {
            _il.LoadNull();

            return;
        }
        var ptRef = _types.Module.ImportReference(pt);
        var local = new VariableDefinition(ptRef);
        _method.Body.Variables.Add(local);
        _il.LoadLocalAddress(local);

        _il.InitObj(ptRef);

        _il.LoadLocal(local);

    }

    void EmitSingleCallArg(BoundExpression arg)
    {
        if (arg is BoundOutArgumentExpression outArg)
        {
            // `ref this` in an async state-machine's MoveNext body: the SM struct's
            // implicit `this` parameter (arg 0) is not in _slots (synthesized methods
            // have no explicit receiver entry), so TryResolveSlot returns null. Detect
            // the self-reference and emit ldarg.0 directly — the `this` pointer on a
            // value-type instance method IS the managed address the callee expects.
            // Without this guard the null-slot branch would DeclareLocal("this", ...)
            // allocating an uninitialized local for the SM, producing invalid IL.
            if (IsSelfReference(outArg.Name))
            {
                _il.LoadArg(_method.Body.ThisParameter); // ldarg.0 — managed pointer to SM
                return;
            }
            // Async lowering names the awaiter backing field directly. It is not a
            // source local and therefore has no slot; pass `ref this._awaiter_N`, not
            // the address of a newly declared, uninitialized local.
            var slotType = _types.Resolve(outArg.SlotType);
            // A spilled async local has both a normal local slot and a state-machine
            // field.  At the original execution point `out local` must update the
            // local; only the suspension bookkeeping copies it to the field.  Preferring
            // the field left the local null before its first await (TaskScope's
            // `TryPop(out action)` then invoked that null local).
            if (TryResolveSlot(outArg.Name) is { } existingSlot)
            {
                existingSlot.EmitAddress(_il);
                return;
            }
            if (TryEmitDeclaringFieldAddress(outArg.Name)) return;
            if (outArg.DeclaresLocal)
                DeclareLocal(outArg.Name, slotType);
            TryResolveSlot(outArg.Name)!.EmitAddress(_il);
        }
        else if (arg is BoundUnaryExpression { Op: SyntaxTokenKind.Star } refArg)
        {
            EmitAddress(refArg.Operand);
        }
        else
        {
            EmitExpression(arg);
        }
    }

    // Emits arguments for a call, handling `out` arguments specially:
    // - `out var name`: declares a local, pushes its address, leaves it in scope for post-call reads.
    // - `out name`: looks up the existing local, pushes its address.
    // Returns the runtime arg types used for method overload resolution (out params become T&).
    void EmitCallArgs(IReadOnlyList<BoundExpression> args, out Type[] argTypes)
    {
        argTypes = new Type[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is BoundOutArgumentExpression outArg)
            {
                // Self-reference `ref this` in an async state-machine: not in _slots.
                // Mirrors the same guard in EmitSingleCallArg.
                if (IsSelfReference(outArg.Name))
                {
                    _il.LoadArg(_method.Body.ThisParameter);
                    argTypes[i] = (_types.BoundTypeToRuntime(outArg.SlotType) ?? typeof(object)).MakeByRefType();
                    continue;
                }
                if (TryResolveSlot(outArg.Name) is { } existingSlot)
                {
                    existingSlot.EmitAddress(_il);
                    var existingRuntimeSlot = _types.BoundTypeToRuntime(outArg.SlotType) ?? typeof(object);
                    argTypes[i] = existingRuntimeSlot.MakeByRefType();
                    continue;
                }
                if (TryEmitDeclaringFieldAddress(outArg.Name))
                {
                    var fieldRuntime = _types.BoundTypeToRuntime(outArg.SlotType) ?? typeof(object);
                    argTypes[i] = fieldRuntime.MakeByRefType();
                    continue;
                }
                var slotType = _types.Resolve(outArg.SlotType);
                if (outArg.DeclaresLocal)
                    DeclareLocal(outArg.Name, slotType);
                TryResolveSlot(outArg.Name)!.EmitAddress(_il);
                var runtimeSlot = _types.BoundTypeToRuntime(outArg.SlotType) ?? typeof(object);
                argTypes[i] = runtimeSlot.MakeByRefType();
            }
            else if (arg is BoundUnaryExpression { Op: SyntaxTokenKind.Star } refArg)
            {
                // *x at call site → pass by reference (emit address)
                EmitAddress(refArg.Operand);
                argTypes[i] = (_types.BoundTypeToRuntime(refArg.Operand.Type) ?? typeof(object)).MakeByRefType();
            }
            else
            {
                EmitExpression(arg);
                argTypes[i] = _types.BoundTypeToRuntime(arg.Type) ?? typeof(object);
            }
        }
    }

    bool TryEmitDeclaringFieldAddress(string name)
    {
        if (!_method.HasThis || _method.DeclaringType is null) return false;
        var field = _method.DeclaringType.Fields.FirstOrDefault(f => f.Name == name);
        if (field is null) return false;

        // A field token inside a generic instance method must be scoped through
        // the current closed self instantiation.  Returning the FieldDefinition
        // directly leaves its declaring type as the open `StateMachine`1`, while
        // ldarg.0 is `StateMachine<T>&`; that disagreement is rejected at the
        // Await*OnCompleted call that consumes the field address.
        FieldReference fieldRef = field;
        if (_method.DeclaringType.HasGenericParameters)
        {
            var self = new GenericInstanceType(_method.DeclaringType);
            foreach (var parameter in _method.DeclaringType.GenericParameters)
                self.GenericArguments.Add(parameter);
            fieldRef = new FieldReference(field.Name, field.FieldType, self);
        }
        _il.LoadArg(_method.Body.ThisParameter);
        _il.LoadFieldAddress(fieldRef);
        return true;
    }

    bool TryResolveField(BoundMemberAccessExpression ma, out FieldReference field)
    {
        field = null!;

        // C#-sourced type from a sibling .cs file. Cecil's .Resolve() would fail
        // because the C# half PE isn't loaded yet at E# IL emit time — instead
        // we look up the field on the handle's metadata surface and build a
        // FieldReference manually scoped to the C# half's assembly identity.
        // ILRepack rewrites that ref to a TypeDef ref after fusion.
        if (ma.Target.Type is Esharp.BoundTree.ExternalCSharpType cs)
        {
            foreach (var member in cs.Handle.Members)
            {
                if (member.Name != ma.MemberName) continue;
                if (member.Kind != Esharp.BoundTree.CSharpMemberKind.Field) continue;
                var declaringRef = _types.Resolve(ma.Target.Type);
                var fieldType = _types.Resolve(member.ReturnType);
                field = new FieldReference(member.Name, fieldType, declaringRef);
                return true;
            }
        }

        // Check if the target type has this field in our defined structs
        if (ma.Target is BoundNameExpression name)
        {
            if (IsSelfReference(name.Name))
            {
                // Self param — resolve field on the declaring type (arg0). For a generic
                // type, `this`/arg0 is the CLOSED self-instantiation (`Pair<!0>&`), so the
                // field must be hosted on that closed instance, not the open type def, or
                // ldfld/stfld fail verification with a receiver-type mismatch.
                //
                // DeclaringType is null while an `init` body is emitted (the ctor is added
                // to the type only afterward); in that case we fall through to the general
                // fallback below, which resolves the field on `self`'s bound type — already
                // the self-instantiation under the pushed generic context.
                if (_method.DeclaringType is { } declType)
                {
                    TypeReference selfRef = declType;
                    if (declType.HasGenericParameters)
                    {
                        var selfClosed = new GenericInstanceType(declType);
                        foreach (var gp in declType.GenericParameters)
                            selfClosed.GenericArguments.Add(gp);
                        selfRef = selfClosed;
                    }
                    var f = FindFieldOnType(selfRef, ma.MemberName);
                    if (f is not null) { field = f; return true; }
                }
            }
            else if (TryResolveSlot(name.Name) is { } slot)
            {
                var f = FindFieldOnType(slot.Type, ma.MemberName);
                if (f is not null) { field = f; return true; }
            }
        }
        // Resolve the field on the target expression's own bound type FIRST — this is
        // the authoritative answer for any explicit member access, including nested
        // chains (`e.transform.position.x`) and reads off an intermediate value-type
        // field (`p.f2.f0`). It must precede the by-name display/struct-field fallback
        // below: that fallback preloads the promoted-method receiver's fields keyed by
        // bare name, so a nested `.f0` whose receiver type *also* declares `f0` would
        // otherwise mis-resolve to the receiver's field (loading the wrong field off
        // the wrong type and producing unverifiable IL).
        var targetTypeRef = _types.Resolve(ma.Target.Type);
        var fallback = FindFieldOnType(targetTypeRef, ma.MemberName);
        if (fallback is not null) { field = fallback; return true; }
        // Last resort: a display-class / capture field reached by bare name (the
        // target's nominal type does not declare it) — closures and the promoted
        // receiver's own bare-field access land here.
        if (_displayClassFields is not null && _displayClassFields.TryGetValue(ma.MemberName, out var sf))
        {
            field = sf;
            return true;
        }
        return false;
    }


    // When set by the caller, the lambda body is emitted and a delegate of this exact type is
    // constructed (used for event subscriptions so the handler matches the event's delegate shape).
    TypeReference? _pendingDelegateTargetType;

    FieldReference? FindFieldOnType(TypeReference typeRef, string fieldName)
    {
        // For TypeDefinitions (user-defined types), walk the class hierarchy.
        if (typeRef is TypeDefinition td)
        {
            for (var cursor = td; cursor is not null; cursor = cursor.BaseType?.Resolve())
            {
                var f = cursor.Fields.FirstOrDefault(f => f.Name == fieldName)
                    // Property name → its `<x>k__BackingField` storage (composite literal,
                    // `with`, and `init`-body field stores reach a stored property by name).
                    // Restricted to THIS module: an external type's `<X>k__BackingField` is a
                    // private auto-property backing we must never bind directly — e.g. binding
                    // `Channel<T>.Writer` to its BCL backing emits an unimportable cross-module
                    // field ref instead of taking the `get_Writer` accessor path.
                    ?? (cursor.Module == _types.Module
                        ? cursor.Fields.FirstOrDefault(f => f.Name == EmitNaming.PropertyBackingFieldName(fieldName))
                        : null);
                if (f is not null)
                    return cursor.Module == _types.Module ? f : _types.Module.ImportReference(f);
                if (cursor.BaseType is null) break;
            }
            return null;
        }

        // For GenericInstanceType, find the field on the open type definition
        // and rebind its declaring type to the closed instance so Cecil writes
        // a MemberRef with the right generic-closed declaring type spec.
        TypeDefinition? typeDef;
        try { typeDef = typeRef.Resolve(); }
        catch { return null; }
        if (typeDef is null) return null;
        // Walk hierarchy on the resolved typedef too.
        for (var cursor = typeDef; cursor is not null; cursor = cursor.BaseType?.Resolve())
        {
            var openField = cursor.Fields.FirstOrDefault(f => f.Name == fieldName)
                // Same in-module restriction as above — never bind an external type's private
                // `<X>k__BackingField` (would emit a cross-module field ref; take the getter).
                ?? (cursor.Module == _types.Module
                    ? cursor.Fields.FirstOrDefault(f => f.Name == EmitNaming.PropertyBackingFieldName(fieldName))
                    : null);
            if (openField is not null)
            {
                if (typeRef is GenericInstanceType git)
                {
                    // Keep `!0` owned by the open generic definition so the closed
                    // receiver substitutes it. Concrete external field types, however,
                    // must be imported into this module before Cecil can tokenize them.
                    var fieldType = openField.FieldType.ContainsGenericParameter
                        ? openField.FieldType
                        : _types.Module.ImportReference(openField.FieldType);
                    return new FieldReference(openField.Name, fieldType, git);
                }
                return openField.Module == _types.Module
                    ? openField
                    : _types.Module.ImportReference(openField);
            }
            if (cursor.BaseType is null) break;
        }
        return null;
    }

    bool TryEmitCSharpConstructor(Esharp.BoundTree.ExternalCSharpType cs, IReadOnlyList<BoundExpression> args)
    {
        // Prefer an exact-arity ctor; otherwise one whose surplus trailing parameters
        // are all optional (filled with their declared defaults below).
        var ctor = cs.Handle.Members.FirstOrDefault(
            m => m.Kind == Esharp.BoundTree.CSharpMemberKind.Constructor
              && m.Parameters.Count == args.Count)
            ?? cs.Handle.Members.FirstOrDefault(
            m => m.Kind == Esharp.BoundTree.CSharpMemberKind.Constructor
              && args.Count < m.Parameters.Count
              && AllOptionalFrom(m.Parameters, args.Count));
        if (ctor is null) return false;
        var declaringRef = _types.Resolve(cs);
        var voidRef = _types.Resolve(new Esharp.BoundTree.VoidType());
        var ctorRef = new Mono.Cecil.MethodReference(".ctor", voidRef, declaringRef)
        {
            HasThis = true,
            ExplicitThis = false,
            CallingConvention = Mono.Cecil.MethodCallingConvention.Default,
        };
        foreach (var p in ctor.Parameters)
            ctorRef.Parameters.Add(new Mono.Cecil.ParameterDefinition(p.Name, Mono.Cecil.ParameterAttributes.None, _types.Resolve(p.Type)));

        if (cs.Handle.IsRefType)
        {
            // Class — newobj pushes the new instance directly.
            foreach (var arg in args) EmitExpression(arg);
            EmitCSharpOptionalDefaults(ctor.Parameters, args.Count);   // fill omitted trailing optionals
            _il.NewObj(ctorRef);

        }
        else
        {
            // Value type — initobj + call instance ctor on address.
            var tempLocal = new VariableDefinition(declaringRef);
            _method.Body.Variables.Add(tempLocal);
            _il.LoadLocalAddress(tempLocal);

            _il.InitObj(declaringRef);

            _il.LoadLocalAddress(tempLocal);

            foreach (var arg in args) EmitExpression(arg);
            EmitCSharpOptionalDefaults(ctor.Parameters, args.Count);   // fill omitted trailing optionals
            _il.Call(ctorRef);

            _il.LoadLocal(tempLocal);

        }
        _lastCallWasVoid = false;
        return true;
    }

    bool TryEmitUserConstructor(string name, IReadOnlyList<BoundExpression> args, BoundType? resultType = null)
    {
        // Call-form construction for a user-defined data type with an init constructor:
        //   let sq = Square(5)  →  newobj Square..ctor(int)
        // For `class`, emit newobj + stloc + ldloc.
        // For struct `data`, initobj + call init ctor on the address.
        var typeDef = _types.TryResolveRegistered(name);
        if (typeDef is null)
        {
            // Generic construction: `Box<int>(42)`. The call target name carries the
            // closed instantiation; strip the type args to find the open data decl.
            var lt = name.IndexOf('<');
            if (lt <= 0) return false;
            var baseName = name[..lt];
            // Key by arity (`Cell`1` vs `Cell`2`) so the right open type is found when a
            // same-name sibling of a different arity exists — the bare key is last-write-
            // wins, which would otherwise resolve `Cell<int>` to the arity-2 `Cell` and
            // miss its 1-arg ctor (probe3 #2). Arity comes from the bound result type.
            var arity = resultType switch
            {
                DataType d => d.TypeArgs.Count,
                ChoiceType c => c.TypeArgs.Count,
                ExternalType e => e.TypeArgs.Count,
                _ => 0,
            };
            typeDef = (arity > 0 ? _types.TryResolveRegistered(EmitNaming.MetadataTypeName(baseName, arity)) : null)
                   ?? _types.TryResolveRegistered(baseName);
            if (typeDef is null) return false;
        }

        var ctor = typeDef.Methods.FirstOrDefault(m =>
            m.IsConstructor && !m.IsStatic && m.Parameters.Count == args.Count);
        // Skip the parameterless ctor for ref types when caller supplies args
        if (ctor is null) return false;
        // For parameterless ctor, only match if caller also supplies zero args
        if (ctor.Parameters.Count != args.Count) return false;

        // For a generic type the newobj/initobj must target the CLOSED instance
        // (`Box<int>`), with the ctor re-homed onto it — otherwise the token resolves
        // to the open def and verification fails. The closed instance comes from the
        // call's bound result type.
        TypeReference instanceRef = typeDef;
        MethodReference ctorRef = ctor;
        if (typeDef.HasGenericParameters && resultType is not null
            && _types.Resolve(resultType) is GenericInstanceType closedGit)
        {
            instanceRef = closedGit;
            ctorRef = RebindToDeclaring(ctor, closedGit);
        }

        if (typeDef.IsValueType)
        {
            // Struct: initobj + call instance ctor
            var tempLocal = new VariableDefinition(instanceRef);
            _method.Body.Variables.Add(tempLocal);
            _il.LoadLocalAddress(tempLocal);

            _il.InitObj(instanceRef);

            _il.LoadLocalAddress(tempLocal);

            foreach (var arg in args)
                EmitExpression(arg);
            _il.Call(ctorRef);

            _il.LoadLocal(tempLocal);

        }
        else
        {
            // Class: newobj pushes the new instance directly
            foreach (var arg in args)
                EmitExpression(arg);
            _il.NewObj(ctorRef);

        }
        _lastCallWasVoid = false;
        return true;
    }

    bool TryEmitExternalConstructor(string name, BoundType callType, IReadOnlyList<BoundExpression> args)
    {
        // Check if the name looks like an external type (starts with uppercase)
        if (name.Length == 0 || !char.IsUpper(name[0])) return false;

        // The binder already typed the ctor call (`List<int>()` → a structured
        // ExternalType with bound args) — resolve THAT. The raw call name is only
        // the fallback for shapes the binder left untyped; it is a flat name (the
        // parser hands generic ctor calls to the binder, which structures them).
        var ctorType = callType is ExternalType ce
            ? ce
            : new ExternalType(name);
        var typeRef = _types.Resolve(ctorType);
        if (typeRef.FullName == "System.Object" && name != "object") return false; // unresolved

        var runtimeType = _types.BoundTypeToRuntime(ctorType);
        if (runtimeType is null || runtimeType == typeof(object) && name != "object") return false;

        // Pick the best overload by arg count, preferring exact runtime-type
        // matches over arg-count-only matches. Previously this always picked
        // the parameterless ctor, discarding any args silently. A ctor with a
        // longer signature qualifies when the omitted tail is all optional —
        // `Workspace("X")` against `Workspace(string, refs?, kind, options?)` —
        // with the exact-arity match preferred (OrderBy is stable).
        var candidates = runtimeType.GetConstructors(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(c =>
            {
                var ps = c.GetParameters();
                if (ps.Length == args.Count) return true;
                return ps.Length > args.Count && ps.Skip(args.Count).All(p => p.IsOptional);
            })
            .OrderBy(c => c.GetParameters().Length)
            .ToList();
        if (candidates.Count == 0) return false;

        var argRuntimeTypes = args.Select(a => _types.BoundTypeToRuntime(a.Type) ?? typeof(object)).ToArray();
        // `ValueTask<T>` has both `.ctor(T result)` and `.ctor(Task<T>)`.
        // A generic E# argument is reflection-erased to object for candidate
        // discovery, so both overloads are assignable; prefer the exact closed
        // runtime shape (`Task<object>` over object) rather than first metadata
        // order. The emitted constructor is then re-hosted on the real Cecil T.
        var ctorInfo = candidates
            .Select(c =>
            {
                var ps = c.GetParameters();
                var score = 0;
                for (var i = 0; i < args.Count; i++)
                {
                    if (!ps[i].ParameterType.IsAssignableFrom(argRuntimeTypes[i]))
                        return (Ctor: c, Applicable: false, Score: -1);
                    if (ps[i].ParameterType == argRuntimeTypes[i]) score += 2;
                    else if (ps[i].ParameterType != typeof(object)) score++;
                }
                return (Ctor: c, Applicable: true, Score: score);
            })
            .Where(x => x.Applicable)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Ctor.GetParameters().Length)
            .Select(x => x.Ctor)
            .FirstOrDefault() ?? candidates[0];

        foreach (var arg in args)
            EmitExpression(arg);
        var ctorParams = ctorInfo.GetParameters();
        for (var i = args.Count; i < ctorParams.Length; i++)
            EmitReflectionOptionalDefault(ctorParams[i]);
        var ctorRef = _types.Module.ImportReference(ctorInfo);
        // Re-host the constructor on the Cecil-resolved closed type so user-defined
        // type arguments survive. BoundTypeToRuntime erases them to `object`
        // (`List<Pt>()` would otherwise emit `newobj List<object>::.ctor`, a
        // type the caller's `List<Pt>` slot rejects).
        if (typeRef is GenericInstanceType closedGit)
        {
            var rehosted = new MethodReference(ctorRef.Name, ctorRef.ReturnType, closedGit)
            {
                HasThis = ctorRef.HasThis,
                ExplicitThis = ctorRef.ExplicitThis,
                CallingConvention = ctorRef.CallingConvention,
            };
            foreach (var p in ctorRef.Parameters)
                rehosted.Parameters.Add(new ParameterDefinition(p.ParameterType));
            ctorRef = rehosted;
        }
        _il.NewObj(ctorRef);

        _lastCallWasVoid = false;
        return true;
    }

    // Note: there is no EmitRaise method here. BoundRaiseStatement is eliminated entirely
    // by EventLowering before CodeGen runs — any BoundRaiseStatement that reaches
    // MethodBodyEmitter is a pipeline bug and throws at the feature-node assertion switch
    // (see the BoundRaiseStatement case above). The raise→capture-then-invoke rewrite is
    // EventLowering's responsibility; CodeGen's event responsibility is the backing field,
    // add_/remove_ accessors, and EventDefinition (see EmitEvent.cs / EmitEventMember).

    bool TryEmitDelegateInvoke(string name, IReadOnlyList<BoundExpression> args)
    {
        // Check if name is a binding holding a delegate (Action/Func) or function pointer.
        var slot = TryResolveSlot(name);
        if (slot is null) return false;

        // Function pointer path: raw ldftn local → calli (no delegate object)
        if (ILPointerEmitter.TryEmitFunctionPointerCall(slot, args, _il, _types.Module,
                EmitFunctionPointerCallArg, out var isVoid))
        {
            if (isVoid) _lastCallWasVoid = true;
            return true;
        }

        return TryEmitDelegateInvoke(slot.Type, () => slot.EmitLoad(_il), args);
    }

    void EmitFunctionPointerCallArg(BoundExpression arg, TypeReference parameterType)
    {
        if (!TryEmitPointerArg(arg, parameterType))
            EmitSingleCallArg(arg);
    }

    // Async lowering rewrites captured parameters to fields on the generated
    // state-machine receiver (`this.work(...)`).  They are still ordinary
    // delegates; accept the field form as well as a bare local/parameter so
    // `await producer(ch, token)` is emitted as `this.producer.Invoke(...)`.
    bool TryEmitDelegateInvoke(BoundMemberAccessExpression member, IReadOnlyList<BoundExpression> args) =>
        TryEmitDelegateInvoke(_types.Resolve(member.Type), () => EmitExpression(member), args);

    bool TryEmitDelegateInvoke(TypeReference delegateType, Action emitReceiver, IReadOnlyList<BoundExpression> args)
    {
        // Resolve the Invoke method before emitting anything. A non-delegate
        // member continues through normal instance-method lowering untouched.
        var resolvedDelegateType = delegateType.Resolve();
        if (resolvedDelegateType is null) return false;

        var invokeMethod = resolvedDelegateType.Methods.FirstOrDefault(m => m.Name == "Invoke");
        if (invokeMethod is null) return false;

        emitReceiver();

        // Push arguments
        foreach (var arg in args)
            EmitExpression(arg);

        // Import the Invoke reference (need to handle generic instances)
        MethodReference invokeRef;
        if (delegateType is GenericInstanceType git)
        {
            invokeRef = new MethodReference("Invoke", invokeMethod.ReturnType, git)
            {
                HasThis = true,
            };
            foreach (var p in invokeMethod.Parameters)
                invokeRef.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));

            // Remap generic parameters to the concrete type arguments
            invokeRef = _types.Module.ImportReference(invokeRef);
        }
        else
        {
            invokeRef = _types.Module.ImportReference(invokeMethod);
        }

        _il.CallVirt(invokeRef);

        if (invokeRef.ReturnType.FullName == "System.Void")
            _lastCallWasVoid = true;
        return true;
    }

    /// Check if any arg in a call requires boxing (value type → interface param).
    /// Used to disable tail calls where box must precede call.
    bool NeedsBoxingForCall(string methodName, IReadOnlyList<BoundExpression> args)
    {
        var methodRef = FindMethod(methodName, args.Count);
        if (methodRef is null) return false;
        var parameters = methodRef.Parameters;
        for (var i = 0; i < args.Count && i < parameters.Count; i++)
        {
            if (!_types.IsValueType(args[i].Type)) continue;
            var paramType = parameters[i].ParameterType;
            if (!paramType.IsValueType && !paramType.IsGenericParameter && !paramType.IsByReference)
                return true; // value type arg → reference type param = boxing needed
        }
        return false;
    }

    // (name, argCount) → first-matching method, indexed once per body. Every type and
    // method signature exists before any body emits (delegate/union/enum types and closure
    // display classes are all created in the type-declaration passes and in lowering, both
    // strictly before pass 1c' body emission), so the module's method set is frozen for the
    // life of this emitter and the index is built lazily on first use rather than re-walking
    // the whole module — top-level types plus their nested surface — at every unresolved
    // call site (previously O(all types × methods) per call). First insertion wins, matching
    // the old first-match-in-declaration-order scan; a closure display class is nested, so
    // its uniquely-named trampoline is reached by descending into NestedTypes.
    Dictionary<(string Name, int ArgCount), MethodReference>? _methodIndex;

    MethodReference? FindMethod(string name, int argCount)
    {
        if (_methodIndex is null)
        {
            _methodIndex = new Dictionary<(string, int), MethodReference>();
            foreach (var type in _types.Module.Types)
                IndexTypeMethods(type, _methodIndex);
        }
        return _methodIndex.GetValueOrDefault((name, argCount));
    }

    static void IndexTypeMethods(TypeDefinition type, Dictionary<(string, int), MethodReference> index)
    {
        foreach (var method in type.Methods)
            index.TryAdd((method.Name, method.Parameters.Count), method); // first wins
        foreach (var nested in type.NestedTypes)
            IndexTypeMethods(nested, index);
    }

    // ---- Post-lowering stubs (formerly ILMethodEmitter.Closures.cs) ----
    //
    // In the old architecture, PrepareCaptures and PrepareHoistedPointers pre-scanned the
    // function body to set up display classes for lambda captures and hoisted pointer locals.
    // Post-lowering, ClosureConversion (C1) has already converted every lambda+capture into
    // display-class TypeSymbol + constructor calls; PrepareHoistedPointers' escape-analysis
    // work is now a FlowAnalysis (B3) pass. Neither has any work to do here.
    //
    // These stubs exist so CodeGenerator.EmitFunctionBody can call them unconditionally
    // (compatible call signature) and so any future regression — a FEATURE node slipping
    // through — surfaces via the FeatureNodeInCodeGenException in EmitStatement/EmitExpression
    // rather than a NullReferenceException from a missing display class.

    /// <summary>
    /// Post-lowering no-op. Lambda captures are already converted to display-class fields
    /// by <c>ClosureConversion</c> in the lowering pipeline. If any <c>BoundCapturedVariable</c>
    /// is found in the body, the lowering pipeline failed and we report a violation.
    /// </summary>
    public void PrepareCaptures(BoundBlockStatement body)
    {
        // Scan for any surviving captured variables — indicates a lowering failure.
        var captureChecker = new CapturePresenceChecker();
        captureChecker.VisitBlock(body);
        if (captureChecker.HasCaptures)
            throw new FeatureNodeInCodeGenException(
                "BoundCapturedVariable found in function body during CodeGen. " +
                "ClosureConversion must have failed to convert this lambda.");
    }

    /// <summary>
    /// Realize every value local whose explicit address escaped as one shared heap cell.
    /// Flow analysis marks the declaration symbol; this prepass installs the slot before
    /// statement emission so all normal loads/stores and every <c>&amp;local</c> share it.
    /// </summary>
    public void PrepareHoistedPointers(BoundBlockStatement body)
    {
        Visit(body);

        void Visit(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundBlockStatement block:
                    foreach (var child in block.Statements) Visit(child);
                    break;
                case BoundVariableDeclaration { Local: { AddressEscapes: true } local } declaration:
                    if (_slots.ContainsKey(declaration.Name)) break;
                    var elementType = _types.Resolve(declaration.DeclaredType);
                    var wrapperType = ILHeapPointer.ResolveWrapperType(_types.Module, elementType);
                    // An escaped source local that is live across an await has a
                    // wrapper-typed async field. Install a slot over that field so
                    // the declaration, every ordinary access, and every `&local`
                    // all use one durable cell instead of spilling a copied T.
                    if (TryResolveSlot(AsyncLowering.SpillFieldName(declaration.Name)) is SelfFieldSlot stateField
                        && stateField.Type.FullName == wrapperType.FullName)
                    {
                        _slots[declaration.Name] = new WrapperBackedSelfFieldSlot(
                            stateField.Reference,
                            ILHeapPointer.GetValueField(_types.Module, stateField.Type),
                            elementType);
                        break;
                    }
                    var wrapper = new VariableDefinition(wrapperType);
                    _method.Body.Variables.Add(wrapper);
                    _slots[declaration.Name] = new WrapperBackedLocalSlot(
                        wrapper,
                        ILHeapPointer.GetValueField(_types.Module, wrapperType),
                        elementType);
                    break;
                case BoundIfStatement conditional:
                    Visit(conditional.Then);
                    if (conditional.Else is not null) Visit(conditional.Else);
                    break;
                case BoundWhileStatement loop:
                    Visit(loop.Body);
                    break;
                case BoundTryStatement tr:
                    Visit(tr.Body);
                    foreach (var c in tr.Catches) Visit(c.Body);
                    break;
            }
        }
    }

    private sealed class CapturePresenceChecker
    {
        public bool HasCaptures { get; private set; }

        public void VisitBlock(BoundBlockStatement block)
        {
            foreach (var s in block.Statements)
                VisitStatement(s);
        }

        void VisitStatement(BoundStatement s)
        {
            switch (s)
            {
                case BoundExpressionStatement es: VisitExpr(es.Expression); break;
                case BoundVariableDeclaration vd: VisitExpr(vd.Initializer); break;
                case BoundAssignment a: VisitExpr(a.Target); VisitExpr(a.Value); break;
                case BoundReturnStatement r: if (r.Expression is not null) VisitExpr(r.Expression); break;
                case BoundIfStatement i: VisitExpr(i.Condition); VisitStatement(i.Then); if (i.Else is not null) VisitStatement(i.Else); break;
                case BoundWhileStatement w: VisitExpr(w.Condition); VisitStatement(w.Body); break;
                case BoundBlockStatement b: VisitBlock(b); break;
                case BoundTryStatement t: VisitBlock(t.Body); foreach (var c in t.Catches) VisitBlock(c.Body); break;
                case BoundThrowStatement th: if (th.Expression is not null) VisitExpr(th.Expression); break;
                // Other statement types are FEATURE nodes caught elsewhere
            }
        }

        void VisitExpr(BoundExpression e)
        {
            if (HasCaptures) return; // short-circuit
            switch (e)
            {
                case BoundFunctionLiteralExpression fl:
                    // A lambda with captures indicates ClosureConversion missed it
                    if (fl.CapturedVariables.Count > 0) HasCaptures = true;
                    return;
                case BoundCallExpression call:
                    VisitExpr(call.Target);
                    foreach (var a in call.Arguments) VisitExpr(a);
                    break;
                case BoundMemberAccessExpression ma: VisitExpr(ma.Target); break;
                case BoundBinaryExpression b: VisitExpr(b.Left); VisitExpr(b.Right); break;
                case BoundUnaryExpression u: VisitExpr(u.Operand); break;
                case BoundObjectCreationExpression oc:
                    foreach (var fi in oc.Fields) VisitExpr(fi.Value); break;
                case BoundIndexExpression ix: VisitExpr(ix.Target); VisitExpr(ix.Index); break;
                // Leaf nodes carry no sub-expressions that could host a capture
            }
        }
    }
}
