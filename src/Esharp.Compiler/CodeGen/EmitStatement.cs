using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Emit;
using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Syntax;

namespace Esharp.CodeGen;

public partial class MethodBodyEmitter
{

    // Override point for the async subclass: appends a protected region's pre-entry
    // label immediately before its `.try` (after the region's setup code), so a resumed
    // await staged through it enters the region without re-running setup. Sync no-ops.
    private protected virtual void EmitRegionPreEntry(BoundStatement region) { }

    // Override point for the async subclass: emits the state-dispatch switch at
    // the top of a try body when the body contains awaits. Sync emitter no-ops.
    private protected virtual void EmitTryBodyEntry(BoundTryStatement tr) { }

    // Override point for the async subclass: emits the state-dispatch switch at the
    // top of an enumerator foreach's try body (the loop lowers to try/finally) when
    // its body contains awaits, so resume lands inside the protected region rather
    // than the outer switch branching into it. Sync emitter no-ops.
    private protected virtual void EmitForEachBodyDispatch(BoundForEachStatement fe) { }

    // Override point for the async subclass: a `defer` lowers to a try/finally over the
    // REST of its block, so awaits in those following statements sit in the defer's
    // protected region. Dispatches resume to them at the top of the defer try body. Sync no-op.
    private protected virtual void EmitDeferBodyDispatch(BoundDeferStatement d) { }

    // Override point for the async subclass: guards the start of a region's `.finally`
    // body (defer cleanup, foreach `Dispose`). A CLR `leave` runs intervening finally
    // handlers — including the suspend `leave` an await inside the region emits. That
    // would run the cleanup prematurely on every suspension. The async override branches
    // past the cleanup (to `skipTarget`, the handler's `endfinally`) when `_state >= 0`,
    // the suspended sentinel; on every real exit (normal, `return`, exception) `_state`
    // is a running/done sentinel (< 0) and the cleanup runs. Sync emitter no-ops.
    private protected virtual void EmitFinallyGuard(ILLabel skipTarget) { }

    void EmitTry(BoundTryStatement tr)
    {
        // Generate IL of shape:
        //   tryStart: body
        //              Leave afterEnd
        //   catchStart_i: Stloc catchLocal_i (or Pop)
        //                  catch_body_i
        //                  Leave afterEnd
        //   afterEnd: nop
        var tryStart = _il.DefineLabel();
        var afterEnd = _il.DefineLabel();

        // Async hook: the region's pre-entry label sits just outside the `.try`, so a
        // resumed await staged here falls through into the protected region below.
        EmitRegionPreEntry(tr);
        _il.MarkLabel(tryStart);
        _inTryOrDefer = true;
        EnsureReturnViaLeave();
        // Hook for the async subclass: dispatch resume to in-try await points.
        // The outer state-machine switch can't target labels inside a protected
        // region (illegal branch into a try); we re-dispatch here, where the
        // branch target is in the same region as the resume label.
        EmitTryBodyEntry(tr);
        EmitBlock(tr.Body);
        _il.Leave(afterEnd);

        // Each catch handler. Boundaries are recorded as labels; the handler-end of
        // clause i is the start of clause i+1 (or afterEnd for the last), so we defer
        // registration until every clause's region-start label is known.
        var pending = new List<PendingHandler>();
        foreach (var catchClause in tr.Catches)
        {
            // A `.finally` clause (synthesized by DeferLowering): the cleanup runs on
            // EVERY exit path — normal fall-through, `return`, `break`, and an exception
            // unwinding through the region — and never observes or swallows a value. It is
            // emitted as a real CLR `.finally` region (not a catch), terminated by
            // `endfinally`. Emitting it as a catch (the pre-fix behavior) both swallowed
            // in-flight exceptions and skipped the normal-completion path entirely.
            if (catchClause.IsFinally)
            {
                var finallyStart = _il.DefineLabel("finally");
                _il.MarkLabel(finallyStart);
                EmitBlock(catchClause.Body);
                _il.EndFinally();
                pending.Add(new PendingHandler(
                    ILExceptionRegionKind.Finally, tryStart, finallyStart, finallyStart, finallyStart, CatchType: null));
                continue;
            }

            // Exception type — fall back to System.Exception when omitted
            var excTypeRef = catchClause.ExceptionType is not null
                ? _types.Resolve(catchClause.ExceptionType)
                : _types.Module.ImportReference(typeof(Exception));

            if (catchClause.BindingName is not null)
                DeclareLocal(catchClause.BindingName, excTypeRef);

            // Guarded clause → a CLR exception FILTER: the clause fires only when the
            // type matches AND the guard holds; a 0 from the filter falls to the next
            // clause. The filter region precedes the handler region.
            if (catchClause.Guard is not null)
            {
                var filterStart = _il.DefineLabel("filter");
                _il.MarkLabel(filterStart);
                // The thrown exception is on the stack at filter entry. `isinst` yields the
                // typed value or null; null → not our type → push 0.
                _il.IsInst(excTypeRef);
                _il.Dup();
                var typeMatched = _il.DefineLabel();
                _il.BranchIfTrue(typeMatched);
                _il.Pop(); // drop the null
                _il.LoadInt(0);
                var endFilter = _il.DefineLabel();
                _il.Branch(endFilter);
                _il.MarkLabel(typeMatched);
                // The typed exception is on the stack — bind it (so the guard can read it),
                // then evaluate the guard; its bool is the filter result.
                if (catchClause.BindingName is not null)
                    EmitStoreFromStack(TryResolveSlot(catchClause.BindingName)!);
                else
                    _il.Pop();
                EmitExpression(catchClause.Guard);
                _il.MarkLabel(endFilter);
                _il.EndFilter();

                var handlerStart = _il.DefineLabel("filterHandler");
                _il.MarkLabel(handlerStart);
                // On filter-handler entry the CLR pushes the exception typed as `object`;
                // the filter already proved the type, so cast before binding the typed local.
                if (catchClause.BindingName is not null)
                {
                    _il.CastClass(excTypeRef);
                    EmitStoreFromStack(TryResolveSlot(catchClause.BindingName)!);
                }
                else
                    _il.Pop();
                EmitBlock(catchClause.Body);
                _il.Leave(afterEnd);

                pending.Add(new PendingHandler(
                    ILExceptionRegionKind.Filter, tryStart, filterStart, handlerStart, filterStart, excTypeRef));
            }
            else
            {
                var catchStart = _il.DefineLabel("catch");
                _il.MarkLabel(catchStart);
                // Exception value is on top of stack when the handler is entered.
                if (catchClause.BindingName is not null)
                    EmitStoreFromStack(TryResolveSlot(catchClause.BindingName)!);
                else
                    _il.Pop();

                EmitBlock(catchClause.Body);
                _il.Leave(afterEnd);

                pending.Add(new PendingHandler(
                    ILExceptionRegionKind.Catch, tryStart, catchStart, catchStart, catchStart, excTypeRef));
            }
        }

        _il.MarkLabel(afterEnd);

        // Handler end of clause i = region start of clause i+1 (or afterEnd for the last).
        for (var i = 0; i < pending.Count; i++)
        {
            var handlerEnd = i + 1 < pending.Count ? pending[i + 1].RegionStart : afterEnd;
            var h = pending[i];
            _il.AddExceptionHandler(
                h.Kind, h.TryStart, h.TryEnd, h.HandlerStart, handlerEnd,
                catchType: h.Kind == ILExceptionRegionKind.Catch ? h.CatchType : null,
                filterStart: h.Kind == ILExceptionRegionKind.Filter ? h.TryEnd : null);
        }
    }

    // A catch/filter clause whose handler-end is resolved after all clauses emit.
    // For a filter, TryEnd == the filter-start label (the filter region precedes the
    // handler), so it doubles as FilterStart; RegionStart is that same label.
    readonly record struct PendingHandler(
        ILExceptionRegionKind Kind, ILLabel TryStart, ILLabel TryEnd,
        ILLabel HandlerStart, ILLabel RegionStart, TypeReference? CatchType);

    void EmitThrow(BoundThrowStatement th)
    {
        if (th.Expression is null)
        {
            _il.Rethrow();
            return;
        }
        EmitExpression(th.Expression);
        _il.Throw();
    }

    void EmitVarDecl(BoundVariableDeclaration v)
    {
        // ? propagation: let x = expr? — unwrap Result (early-return on error), then
        // store the unwrapped value into the binding. EmitTryUnwrapExpression leaves the
        // value on the stack; declare the slot and store it.
        if (v.Initializer is BoundTryUnwrapExpression tu)
        {
            DeclareLocal(v.Name, _types.Resolve(v.DeclaredType));
            TryResolveSlot(v.Name)!.EmitStore(_il, () => EmitTryUnwrapExpression(tu));
            return;
        }

        // Managed ref local: `var p: *T = &x` normally receives a ByReferenceType
        // slot.  Escape analysis may instead have promoted that same source alias
        // to a durable `__Ptr_T` carrier.  Do not erase that decision merely
        // because its initializer is syntactically an address expression: the
        // durable form must take the ordinary slot path below, where `&x` emits
        // the shared wrapper reference.
        if (v.DeclaredType is ByRefBoundType byRef)
        {
            var elementType = _types.Resolve(byRef.Inner);
            var byRefType = new Mono.Cecil.ByReferenceType(elementType);
            var def = new VariableDefinition(byRefType);
            _method.Body.Variables.Add(def);
            _slots[v.Name] = new ByRefLocalSlot(def, elementType);
            // A source `*T` alias may be initialized by `&cell` or by another
            // local pointer. Both forms must preserve the managed address itself;
            // ordinary EmitExpression(pointer) dereferences to T and would make a
            // subsequent `var pointerCopy = pointer` invalid IL (and lose aliasing).
            EmitPointerAsManagedRef(v.Initializer);
            _il.StoreLocal(def);
            return;
        }

        var varType = _types.Resolve(v.DeclaredType);

        // 'var' type resolves to object — infer concrete type from initializer. Only
        // when the slot type is genuinely an inference hole: a `var` with an initializer
        // the binder couldn't type concretely (its own type is still object/inferred).
        // An EXPLICIT `let o: object = <value type>` keeps its object slot so the value
        // boxes (re-inferring it to the value type would defeat the boxing the binder
        // intends and break a later `as!`/interface use).
        var slotIsInferenceHole =
            v.DeclaredType is InferredType
            || v.Initializer.Type is InferredType
            || v.Initializer.Type is ExternalType { Name: "object", TypeArgs.Count: 0 };
        if (varType.FullName == "System.Object" && slotIsInferenceHole
            && v.Initializer is not BoundLiteralExpression { Value: null })
        {
            if (v.Initializer is BoundFunctionLiteralExpression fl)
                varType = ResolveDelegateType(fl);
            else
            {
                var inferred = InferConcreteType(v.Initializer);
                if (inferred is not null)
                    varType = inferred;
            }
        }

        // If the name was pre-installed as a slot (hoisted capture from
        // PrepareCaptures, or an async state-machine field allocated during
        // analysis), use that slot. Otherwise allocate a fresh local.
        var slot = TryResolveSlot(v.Name);
        if (slot is null || slot is ParameterSlot)
        {
            DeclareLocal(v.Name, varType);
            slot = TryResolveSlot(v.Name)!;
        }

        // An escaped `&local` is represented by a single wrapper-backed slot. Its
        // declaration initializes the wrapper itself; subsequent normal assignments
        // write Wrapper.Value and therefore remain visible to all pointer aliases.
        if (slot is IWrapperBackedSlot wrapperBacked)
        {
            wrapperBacked.EmitWrapperStore(_il, () =>
            {
                EmitExpression(v.Initializer);
                _il.NewObj(ILHeapPointer.GetWrapperCtor(_types.Module, wrapperBacked.WrapperType));
            });
            return;
        }

        // Nullable value type with nil initializer: initobj on the slot address.
        if (v.Initializer is BoundLiteralExpression { Value: null } && IsNullableValueType(slot.Type))
        {
            slot.EmitAddress(_il);
            _il.InitObj(slot.Type);
            return;
        }

        slot.EmitStore(_il, () =>
        {
            if (ILHeapPointer.IsWrapperType(slot.Type) && IsWrapperCarrierExpression(v.Initializer))
            {
                EmitPointerAsWrapper(v.Initializer, slot.Type);
                return;
            }
            // A lambda landing in a delegate-typed slot must materialize as *that*
            // delegate (a `delegate func` or a named BCL delegate like Predicate<T>),
            // not the default Func/Action — nominal target typing.
            if (v.Initializer is BoundFunctionLiteralExpression && IsDelegateTypeRef(slot.Type))
                _pendingDelegateTargetType = slot.Type;
            EmitExpression(v.Initializer);
            // A value `data` initializing an interface- or object-typed local must be
            // boxed (the binder emits the boxing diagnostic; this makes it valid IL).
            EmitBoxValueToReference(slot.Type, v.Initializer);
        });
    }

    // True when a Cecil type reference resolves to a delegate (derives from
    // System.MulticastDelegate / System.Delegate). Used to drive nominal lambda
    // target typing into delegate-typed slots / returns.
    static bool IsDelegateTypeRef(TypeReference type)
    {
        TypeDefinition? def;
        try { def = type.Resolve(); } catch { return false; }
        while (def is not null)
        {
            var full = def.BaseType?.FullName;
            if (full is "System.MulticastDelegate" or "System.Delegate") return true;
            if (def.BaseType is null) return false;
            try { def = def.BaseType.Resolve(); } catch { return false; }
        }
        return false;
    }

    // Emit `field = init` for a static field (cctor / module init): push the
    // initializer, box if it flows a value type into a reference/interface field
    // (covariant ref stores like Dictionary→IReadOnlyDictionary need no box), then
    // stsfld. Used by the static-func `static readonly` initializer path.
    public void EmitFieldInitializer(FieldReference field, BoundExpression init)
    {
        EmitExpression(init);
        EmitBoxValueToReference(field.FieldType, init);
        _il.StoreStaticField(field);
    }

    // Box a value type when it flows into a reference-typed slot (an interface or
    // `object`). No-op when the source is already a reference or the target is a
    // value type. The boxed token is the source's own type so the runtime carries
    // the precise value.
    void EmitBoxValueToReference(TypeReference targetType, BoundExpression source)
    {
        if (!_types.IsValueType(source.Type)) return;

        // value `U` flowing into a `U?` slot (`let x: int? = 5`, a field/return of
        // nullable type): lift it with the `Nullable<U>(value)` ctor. A value already in
        // its nullable form stores directly. The present-case `T → T?` conversion the
        // type system documents, made concrete.
        if (source.Type is not NullableType && IsNullableValueType(targetType))
        {
            _il.NewObj(NullableCtor(targetType));
            return;
        }

        var targetIsReference = targetType.FullName == "System.Object";
        if (!targetIsReference)
        {
            TypeDefinition? resolved;
            try { resolved = targetType.Resolve(); } catch { resolved = null; }
            targetIsReference = resolved is { IsInterface: true };
        }
        if (targetIsReference)
            _il.Box(_types.Resolve(source.Type));
    }

    void EmitIf(BoundIfStatement i)
    {
        EmitExpression(i.Condition);
        var elseLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();

        _il.BranchIfFalse(elseLabel);
        EmitStatement(i.Then);

        if (i.Else is not null)
        {
            _il.Branch(endLabel);
            _il.MarkLabel(elseLabel);
            EmitStatement(i.Else);
            _il.MarkLabel(endLabel);
        }
        else
        {
            _il.MarkLabel(elseLabel);
        }
    }

    // `if`/`else if`/`else` in expression position (plan 11 Part E). Mirrors EmitMatchExpression:
    // a result local each taken branch stores into, then loaded at the end. A diverging branch
    // (Value == null) emitted its own return/throw, so it neither stores nor branches to end.
    void EmitIfExpression(BoundIfExpression i)
    {
        var resultLocal = new VariableDefinition(_types.Resolve(i.Type));
        _method.Body.Variables.Add(resultLocal);
        var endLabel = _il.DefineLabel();

        foreach (var branch in i.Branches)
        {
            var nextLabel = _il.DefineLabel();
            EmitExpression(branch.Condition);
            _il.BranchIfFalse(nextLabel);
            foreach (var stmt in branch.Body) EmitStatement(stmt);
            if (branch.Value is not null)
            {
                EmitExpression(branch.Value);
                _il.StoreLocal(resultLocal);
                _il.Branch(endLabel);
            }
            _il.MarkLabel(nextLabel);
        }

        foreach (var stmt in i.ElseBody) EmitStatement(stmt);
        if (i.ElseValue is not null)
        {
            EmitExpression(i.ElseValue);
            _il.StoreLocal(resultLocal);
        }

        _il.MarkLabel(endLabel);
        _il.LoadLocal(resultLocal);
    }

    void EmitWhile(BoundWhileStatement w)
    {
        var condLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();

        _il.MarkLabel(condLabel);
        EmitExpression(w.Condition);
        _il.BranchIfFalse(endLabel);
        _loopTargets.Push((breakTarget: endLabel, continueTarget: condLabel));
        EmitStatement(w.Body);
        _loopTargets.Pop();
        _il.Branch(condLabel);
        _il.MarkLabel(endLabel);
    }

    void EmitBreak()
    {
        if (_loopTargets.Count == 0) return; // graceful no-op outside a loop
        var (breakTarget, _) = _loopTargets.Peek();
        // Inside a try/defer scope, must use Leave instead of Br.
        if (_inTryOrDefer) _il.Leave(breakTarget);
        else _il.Branch(breakTarget);
    }

    void EmitContinue()
    {
        if (_loopTargets.Count == 0) return;
        var (_, continueTarget) = _loopTargets.Peek();
        if (_inTryOrDefer) _il.Leave(continueTarget);
        else _il.Branch(continueTarget);
    }

    void EmitReturn(BoundReturnStatement r)
    {
        // A pointer return is first-class wrapper storage, never a managed local
        // address. Reuse an escaped local's wrapper when present; otherwise materialize
        // the wrapper at the return boundary from the addressed value.
        if (r.Expression is { } pointerReturn
            && ILHeapPointer.IsWrapperType(_method.ReturnType)
            && IsWrapperReturnExpression(pointerReturn))
        {
            EmitPointerAsWrapper(pointerReturn, _method.ReturnType);
            EmitReturnCore(null);
            return;
        }

        if (r.Expression is BoundCallExpression tailCall && !_inTryOrDefer
            && tailCall.Target is BoundNameExpression tailName
            && CanUseTailCall()
            // Cannot tail-call when passing managed pointers to caller locals — the
            // frame is destroyed by tail. and the pointer dangles. This covers
            // explicit `*expr` / `&var` call-site markers AND a bare `*P` parameter
            // forwarded onward (`parseArray(p)` where `p: *P`): such an argument is
            // itself a managed pointer that must pass through as a ref, not be
            // dereferenced, and must not outlive the frame via tail.
            && !tailCall.Arguments.Any(a => a is BoundUnaryExpression { Op: SyntaxTokenKind.Star }
                || a is BoundAddressOfVariableExpression
                || a is BoundNameExpression && a.Type is HeapPointerBoundType)
            // Cannot tail-call when value types need boxing for interface/protocol params —
            // tail. must be immediately before call, no box between them.
            && !NeedsBoxingForCall(tailName.Name, tailCall.Arguments))
        {
            var methodRef = FindMethod(tailName.Name, tailCall.Arguments.Count);
            // A by-ref (`P&`) parameter means a managed pointer is forwarded into
            // the callee. Tail-calling would (a) require dereferencing/coercion the
            // simple tail-arg path doesn't do, and (b) outlive the destroyed frame
            // if the pointer aliases a caller local. Fall through to the normal call.
            if (methodRef is not null && methodRef.Parameters.Any(p => p.ParameterType.IsByReference))
                methodRef = null;
            if (methodRef is not null)
            {
                // Close open generics with the call site's explicit type args
                // before emitting the call. Without this the tail-call lands on
                // the open MethodDefinition and the runtime aborts the invoke
                // with "not fully instantiated".
                if (tailCall.ExplicitTypeArguments is { Count: > 0 } typeArgs
                    && methodRef.HasGenericParameters)
                {
                    var generic = new Mono.Cecil.GenericInstanceMethod(methodRef);
                    foreach (var t in typeArgs)
                        generic.GenericArguments.Add(_types.Resolve(t));
                    methodRef = generic;
                }
                // Coerced like any call site (notably the `T` → `Nullable<T>` lift);
                // every coercion completes before `tail.`, which still immediately
                // precedes the call.
                EmitCallArgsCoerced(tailCall.Arguments, methodRef);
                _il.TailPrefix();
                _il.Call(methodRef);
                _il.Ret(_method.ReturnType.FullName != "System.Void");
                return;
            }
        }

        // Nullable value type nil return: materialize a default slot and
        // hand its value to the return path.
        if (r.Expression is BoundLiteralExpression { Value: null } && IsNullableValueType(_method.ReturnType))
        {
            EmitDefaultNullable(_method.ReturnType);
            EmitReturnCore(null); // value already on stack; base emits Ret
            return;
        }

        // Returning a non-null value from a `T?` function: wrap the inner value in
        // `Nullable<T>` (otherwise a bare `int` is returned where `Nullable<int>` is
        // expected and HasValue reads garbage).
        if (r.Expression is not null && IsNullableValueType(_method.ReturnType)
            && _method.ReturnType is GenericInstanceType retGit
            && !IsNullableValueType(_types.Resolve(r.Expression.Type)))
        {
            EmitExpression(r.Expression);
            var ctor = HostMethodOnType(
                retGit.Resolve().Methods.First(m => m.IsConstructor && m.Parameters.Count == 1), retGit);
            _il.NewObj(ctor);
            EmitReturnCore(null); // value already on stack
            return;
        }

        // A lambda returned where a delegate type is expected materializes as that
        // exact delegate (nominal target typing), mirroring the typed-let path.
        if (r.Expression is BoundFunctionLiteralExpression && IsDelegateTypeRef(_method.ReturnType))
            _pendingDelegateTargetType = _method.ReturnType;
        EmitReturnCore(r.Expression);
    }

    bool IsWrapperReturnExpression(BoundExpression expression) => expression switch
    {
        _ => IsWrapperCarrierExpression(expression),
    };

}
