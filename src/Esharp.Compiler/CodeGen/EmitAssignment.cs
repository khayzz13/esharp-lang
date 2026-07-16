using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Emit;
using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Syntax;

namespace Esharp.CodeGen;

public partial class MethodBodyEmitter
{

    /// Store a value that is already sitting on TOS into `slot`. Bridges the
    /// gap between EmitStore's push-value-during-callback contract and
    /// contexts (catch handlers, match payload extraction) where the value has
    /// already been produced by the CLR or a prior load.
    private protected void EmitStoreFromStack(ILSlot slot)
    {
        var temp = new VariableDefinition(slot.Type);
        _method.Body.Variables.Add(temp);
        _il.StoreLocal(temp);
        slot.EmitStore(_il, () => _il.LoadLocal(temp));
    }

    void EmitAssignment(BoundAssignment a)
    {
        if (a.Target is BoundNameExpression name)
        {
            var slot = TryResolveSlot(name.Name);
            if (slot is not null)
            {
                slot.EmitStore(_il, () => EmitStorageValue(slot.Type, a.Value));
            }
            else if (TryResolveStaticFuncFieldOnSelf(name.Name) is { } sfField)
            {
                // Bare name inside a `static Foo { var n: int ... func bump() }`
                // body resolves to the static field on the host class. Without
                // this, `n = n + 1` silently no-ops (assignment was emitted to
                // an unresolved target).
                EmitExpression(a.Value);
                _il.StoreStaticField(sfField);
            }
            else if (_method.Name == ".cctor"
                && TryResolveNamespacePropertyBacking(name.Name) is { } backing)
            {
                EmitExpression(a.Value);
                _il.StoreStaticField(backing);
            }
            else if (TryResolveNamespacePropertySetter(name.Name) is { } setter)
            {
                EmitExpression(a.Value);
                _il.Call(setter);
            }
            else
            {
                // Unresolved target — still emit the RHS to preserve side effects.
                EmitExpression(a.Value);
            }
        }
        else if (a.Target is BoundMemberAccessExpression ma)
        {
            if (ma.IsPropertyLocationProjection
                && TryEmitInCompilationPropertyLocation(ma))
            {
                EmitExpression(a.Value);
                _il.StoreObject(_types.Resolve(ma.Type));
                return;
            }

            // `x.field = value` where x is a static-receiver alias is a store on
            // the declared static facet, not an instance-field assignment.
            if (ma.Target is BoundNameExpression staticAlias
                && ma.Target.Type is StaticFuncType)
            {
                var staticTypeName = ((StaticFuncType)ma.Target.Type).Name;
                var host = _method.DeclaringType?.Name == staticTypeName
                    ? _method.DeclaringType
                    : _types.Module.Types.FirstOrDefault(t => t.Name == staticTypeName);
                var staticField = host?.Fields.FirstOrDefault(f => f.Name == ma.MemberName && f.IsStatic);
                if (staticField is not null)
                {
                    EmitExpression(a.Value);
                    _il.StoreStaticField(staticField);
                    return;
                }
            }
            // Cross-type write to an in-compilation property routes through its `set_<name>`
            // accessor (enforcing a custom setter / get-only). An in-type write (`self.x` from
            // within the declaring type, including the setter's own body) falls through to the
            // field path below and stores the backing field directly — so a custom setter never
            // re-enters itself.
            if (TryEmitInCompilationPropertySet(ma, a.Value))
                return;

            // A sibling C# source property is not a field in the fused assembly.
            // Emit its setter reference directly from the Roslyn handle so fusion
            // can retarget it to the eventual TypeDef without reflection.
            if (ma.Target.Type is ExternalCSharpType csharp
                && csharp.Handle.Members.FirstOrDefault(candidate =>
                    candidate.Kind == CSharpMemberKind.Property
                    && candidate.Name == ma.MemberName
                    && candidate.HasSetter) is { } csharpProperty)
            {
                var csharpSetter = BuildCSharpSetterReference(csharp, ma.MemberName, csharpProperty);
                if (csharpProperty.IsStatic)
                {
                    EmitExpression(a.Value);
                    _il.Call(csharpSetter);
                }
                else
                {
                    EmitExpression(ma.Target);
                    EmitExpression(a.Value);
                    _il.CallVirt(csharpSetter);
                }
                return;
            }

            // Auto-deref store through *T heap pointer (skip for self param)
            if (IsHeapPointerDeref(ma, out var hp))
            {
                EmitHeapPointerFieldStore(ma, hp, () => EmitExpression(a.Value));
                return;
            }

            // Field path first (user-defined types + BCL public fields)
            if (TryResolveField(ma, out var field))
            {
                // Structs need address (ldloca) for in-place mutation;
                // classes need the instance reference (ldloc) for stfld. The bare
                // `self` receiver always takes the address path: `EmitAddress(self)`
                // is `ldarg.0`, which is the correct `stfld` base for BOTH a value
                // receiver (a managed pointer) and a reference receiver (the object
                // ref). The value-load path would `ldobj` the receiver to a value,
                // and `stfld` rejects a value-type instance as its base.
                var targetIsSelf = ma.Target is BoundNameExpression sn && IsSelfReference(sn.Name);
                if (_types.IsValueType(ma.Target.Type) || targetIsSelf)
                    EmitAddress(ma.Target);
                else
                    EmitExpression(ma.Target);

                // A *T field carries a durable wrapper. Preserve an existing
                // wrapper-backed parameter/local instead of loading its Value
                // and manufacturing a second carrier at this storage boundary.
                if (ma.Type is HeapPointerBoundType)
                {
                    EmitStorageValue(_types.Resolve(ma.Type), a.Value);
                }
                else
                {
                    EmitExpression(a.Value);
                }
                _il.StoreField(field);
                return;
            }

            // Property setter path: resolve set_X on the runtime type
            var runtimeType = _types.BoundTypeToRuntime(ma.Target.Type);
            var property = runtimeType?.GetProperty(ma.MemberName);
            var setter = property?.GetSetMethod();
            if (setter is not null)
            {
                var setterRef = _types.Module.ImportReference(setter);
                if (setter.IsStatic)
                {
                    EmitExpression(a.Value);
                    _il.Call(setterRef);
                }
                else
                {
                    if (_types.IsValueType(ma.Target.Type))
                        EmitAddress(ma.Target);
                    else
                        EmitExpression(ma.Target);
                    EmitExpression(a.Value);
                    if (setter.DeclaringType!.IsValueType) _il.Call(setterRef);
                    else _il.CallVirt(setterRef);
                }
                return;
            }

            _diagnostics.Report("", 0, 0, $"IL: unresolved assignment target '{ma.MemberName}' on type '{ma.Target.Type}'");
        }
        else if (a.Target is BoundIndexExpression idx)
        {
            EmitIndexAssignment(idx, a.Value);
        }
    }

    void EmitCompoundAssignment(BoundCompoundAssignment ca)
    {
        if (ca.Target is BoundNameExpression name)
        {
            var slot = TryResolveSlot(name.Name);
            if (slot is null)
            {
                // Unresolved — emit for side effects and drop the result.
                EmitExpression(ca.Target);
                EmitExpression(ca.Value);
                EmitBinaryOp(ca.Op, ca.Target.Type);
                return;
            }

            // old = slot; new = old op value; slot = new — go through the
            // slot API so field stores (SM or display) use the right
            // preamble and the temp-reorder path survives a mid-expression await.
            slot.EmitStore(_il, () =>
            {
                slot.EmitLoad(_il);
                EmitExpression(ca.Value);
                EmitBinaryOp(ca.Op, ca.Target.Type);
            });
        }
        else if (ca.Target is BoundMemberAccessExpression ma)
        {
            if (ma.IsPropertyLocationProjection
                && TryEmitInCompilationPropertyLocation(ma))
            {
                _il.Dup();
                _il.LoadObject(_types.Resolve(ma.Type));
                EmitExpression(ca.Value);
                EmitBinaryOp(ca.Op, ma.Type);
                _il.StoreObject(_types.Resolve(ma.Type));
                return;
            }

            // Compound assignment against a static-receiver alias remains a
            // static field read/modify/write; there is no receiver object to
            // duplicate or address.
            if (ma.Target is BoundNameExpression staticAlias
                && ma.Target.Type is StaticFuncType)
            {
                var staticTypeName = ((StaticFuncType)ma.Target.Type).Name;
                var host = _method.DeclaringType?.Name == staticTypeName
                    ? _method.DeclaringType
                    : _types.Module.Types.FirstOrDefault(t => t.Name == staticTypeName);
                var staticField = host?.Fields.FirstOrDefault(f => f.Name == ma.MemberName && f.IsStatic);
                if (staticField is not null)
                {
                    _il.LoadStaticField(staticField);
                    EmitExpression(ca.Value);
                    EmitBinaryOp(ca.Op, ca.Target.Type);
                    _il.StoreStaticField(staticField);
                    return;
                }
            }
            // Event subscription path: when `+=` / `-=` targets a BCL event, emit a
            // call to `add_EventName` / `remove_EventName` instead of arithmetic on a field.
            if (ca.Op is SyntaxTokenKind.PlusEquals or SyntaxTokenKind.MinusEquals)
            {
                var runtimeType = _types.BoundTypeToRuntime(ma.Target.Type);
                var eventInfo = runtimeType?.GetEvent(ma.MemberName);
                if (eventInfo is not null)
                {
                    var accessor = ca.Op == SyntaxTokenKind.PlusEquals ? eventInfo.GetAddMethod() : eventInfo.GetRemoveMethod();
                    if (accessor is not null && eventInfo.EventHandlerType is not null)
                    {
                        var accessorRef = _types.Module.ImportReference(accessor);
                        if (!accessor.IsStatic)
                        {
                            if (_types.IsValueType(ma.Target.Type))
                                EmitAddress(ma.Target);
                            else
                                EmitExpression(ma.Target);
                        }
                        // If the handler is a function literal, construct it as the event's
                        // expected delegate type directly (no Action/Func mismatch).
                        _pendingDelegateTargetType = _types.Module.ImportReference(eventInfo.EventHandlerType);
                        EmitExpression(ca.Value);
                        _pendingDelegateTargetType = null;
                        if (accessor.IsStatic) _il.Call(accessorRef);
                        else _il.CallVirt(accessorRef);
                        return;
                    }
                }

                // E#-declared event (same compilation): no runtime Type yet, so resolve
                // the add_/remove_ accessor off the emitted module type's EventDefinition.
                if (TryResolveModuleEventAccessor(ma.Target.Type, ma.MemberName,
                        ca.Op == SyntaxTokenKind.PlusEquals, out var modAccessor, out var modHandlerType))
                {
                    if (_types.IsValueType(ma.Target.Type))
                        EmitAddress(ma.Target);
                    else
                        EmitExpression(ma.Target);
                    _pendingDelegateTargetType = modHandlerType;
                    EmitExpression(ca.Value);
                    _pendingDelegateTargetType = null;
                    _il.CallVirt(modAccessor);
                    return;
                }
            }

            // Compound assignment through *T heap pointer (skip for self param)
            // Properties are accessor contracts, including on a `*T` receiver.
            // Handle them before the raw-field path so `p.count += 1` never reaches
            // a private generated backing field and so the wrapper is evaluated once.
            if (TryEmitInCompilationPropertyCompoundSet(ma, ca.Value, ca.Op))
                return;

            if (IsHeapPointerDeref(ma, out var hpCa))
            {
                EmitHeapPointerCarrier(ma.Target);
                var wrapperType = _types.Resolve(ma.Target.Type);
                ILHeapPointer.EmitDerefToAddress(_il, _types.Module, wrapperType);
                _il.Dup();
                var innerType = _types.Resolve(hpCa.Inner);
                var hpField = FindFieldOnType(innerType, ma.MemberName);
                if (hpField is not null)
                {
                    _il.LoadField(hpField);
                    EmitExpression(ca.Value);
                    EmitBinaryOp(ca.Op, ma.Type);
                    _il.StoreField(hpField);
                }
            }
            else
            {
                // A value-type receiver (`point.x += 1`) needs its ADDRESS so ldfld/stfld
                // mutate it in place; a reference-type receiver (`self.processed += 1` on a
                // `class`) needs the REFERENCE itself. The two coincide for `this` in a
                // plain method (ldarg.0 is the reference), but NOT when `self` is an async
                // state-machine field: `ldflda self` yields `IngestService&` where ldfld
                // wants `IngestService`. Load by value for references, by address for values.
                if (_types.IsValueType(ma.Target.Type))
                    EmitAddress(ma.Target);
                else
                    EmitExpression(ma.Target);
                _il.Dup();
                if (TryResolveField(ma, out var field))
                {
                    _il.LoadField(field);
                    EmitExpression(ca.Value);
                    EmitBinaryOp(ca.Op, ma.Type);
                    _il.StoreField(field);
                }
            }
        }
    }

    bool TryEmitInCompilationPropertyCompoundSet(BoundMemberAccessExpression ma,
        BoundExpression value, SyntaxTokenKind op)
    {
        var lookup = ma.Target.Type switch
        {
            HeapPointerBoundType hp => hp.Inner,
            ByRefBoundType byRef => byRef.Inner,
            _ => ma.Target.Type,
        };
        if (lookup is not DataType dt) return false;
        var owner = _types.Module.Types.FirstOrDefault(t => t.Name == dt.Name)
            ?? _types.Module.Types.FirstOrDefault(t => t.Name.StartsWith(dt.Name + "`", StringComparison.Ordinal));
        if (owner is null) return false;
        // A compound write on a derived receiver still belongs to the base
        // property contract.  Do not fall back to the base's private backing
        // field just because the receiver's emitted type has no local accessor.
        var accessorOwner = owner;
        MethodDefinition? getter = null;
        MethodDefinition? setter = null;
        while (accessorOwner is not null && (getter is null || setter is null))
        {
            getter ??= accessorOwner.Methods.FirstOrDefault(m => m.Name == "get_" + ma.MemberName
                && !m.IsStatic && m.Parameters.Count == 0);
            setter ??= accessorOwner.Methods.FirstOrDefault(m => m.Name == "set_" + ma.MemberName
                && !m.IsStatic && m.Parameters.Count == 1);
            if (getter is null || setter is null)
            {
                try { accessorOwner = accessorOwner.BaseType?.Resolve(); }
                catch { accessorOwner = null; }
            }
        }
        if (getter is null || setter is null) return false;

        MethodReference getterRef = getter, setterRef = setter;
        if (_types.Resolve(lookup) is GenericInstanceType git)
        {
            getterRef = HostMethodOnType(getter, git);
            setterRef = HostMethodOnType(setter, git);
        }

        if (ma.Target.Type is HeapPointerBoundType heap)
        {
            // wrapper -> managed address; duplicate it so one copy feeds the
            // getter and the other remains as the setter receiver.
            EmitHeapPointerCarrier(ma.Target);
            ILHeapPointer.EmitDerefToAddress(_il, _types.Module, _types.Resolve(ma.Target.Type));
            _il.Dup();
            _il.Call(getterRef);
            EmitExpression(value);
            EmitBinaryOp(op, ma.Type);
            _il.Call(setterRef);
            return true;
        }

        if (owner.IsValueType)
        {
            // A value receiver must remain an address so the setter mutates the
            // original location rather than a temporary copy.
            EmitAddress(ma.Target);
            _il.Dup();
            _il.Call(getterRef);
            EmitExpression(value);
            EmitBinaryOp(op, ma.Type);
            _il.Call(setterRef);
            return true;
        }

        // Spill class receivers: compound assignment is a single receiver
        // evaluation even when the receiver is a call or nested property.
        var receiver = new VariableDefinition(_types.Resolve(ma.Target.Type));
        var result = new VariableDefinition(_types.Resolve(ma.Type));
        _method.Body.Variables.Add(receiver);
        _method.Body.Variables.Add(result);
        EmitExpression(ma.Target);
        _il.StoreLocal(receiver);
        _il.LoadLocal(receiver);
        _il.CallVirt(getterRef);
        EmitExpression(value);
        EmitBinaryOp(op, ma.Type);
        _il.StoreLocal(result);
        _il.LoadLocal(receiver);
        _il.LoadLocal(result);
        _il.CallVirt(setterRef);
        return true;
    }

    // Cross-type assignment `obj.x = v` where `x` is a property of an in-compilation type:
    // emit a call to its `set_<name>` accessor. Returns false when the target is not such a
    // property OR the write is genuine property initialization / implementation. Constructors
    // and the setter body itself reach the backing field without re-entering the setter; an
    // ordinary method's later `self.x = …` assignment must call the authored property policy.
    bool TryEmitInCompilationPropertySet(BoundMemberAccessExpression ma, BoundExpression value)
    {
        var lookup = ma.Target.Type switch
        {
            HeapPointerBoundType hp => hp.Inner,
            ByRefBoundType byRef => byRef.Inner,
            _ => ma.Target.Type,
        };
        var typeName = lookup switch
        {
            Esharp.BoundTree.DataType dt => dt.Name,
            Esharp.BoundTree.ChoiceType ct => ct.Name,
            // An interface property requirement is written through its abstract `set_<name>`
            // slot — `b.n = v` where `b : IBox` is `callvirt IBox::set_n`.
            Esharp.BoundTree.InterfaceType it => it.Name,
            _ => null,
        };
        if (typeName is null) return false;

        TypeDefinition? owner = null;
        foreach (var t in _types.Module.Types)
            if (t.Name == typeName) { owner = t; break; }
        owner ??= _types.Module.Types.FirstOrDefault(t => t.Name.StartsWith(typeName + "`", StringComparison.Ordinal));
        if (owner is null) return false;

        // As for reads, a property declared on a base must be called through its
        // accessor; its backing field is deliberately private to that base.
        var setterOwner = owner;
        MethodDefinition? setter = null;
        while (setterOwner is not null && setter is null)
        {
            setter = setterOwner.Methods.FirstOrDefault(m =>
                m.Name == "set_" + ma.MemberName && !m.IsStatic && m.Parameters.Count == 1);
            if (setter is null)
            {
                try { setterOwner = setterOwner.BaseType?.Resolve(); }
                catch { setterOwner = null; }
            }
        }
        if (setter is null) return false;

        // Only genuine initialization and the accessor implementation itself may store the
        // property's backing field directly.  Ordinary methods on the declaring type are
        // callers of the property just like methods on another type.  A derived receiver must
        // likewise call the base setter rather than attempting an illegal private-field store.
        if (ReferenceEquals(setter.DeclaringType, _method.DeclaringType)
            && (_method.IsConstructor || _method.Name == setter.Name))
            return false;

        Mono.Cecil.MethodReference setterRef = setter;
        if (_types.Resolve(ma.Target.Type) is GenericInstanceType git)
            setterRef = HostMethodOnType(setter, git);
        else if (owner.HasGenericParameters && ReferenceEquals(owner, _method.DeclaringType))
        {
            var self = new GenericInstanceType(owner);
            foreach (var parameter in owner.GenericParameters) self.GenericArguments.Add(parameter);
            setterRef = HostMethodOnType(setter, self);
        }

        var useCall = owner.IsValueType;
        if (ma.Target.Type is HeapPointerBoundType)
        {
            // A pointer receiver carries the location of a value data instance;
            // call its property setter on that managed location, never the wrapper.
            EmitHeapPointerCarrier(ma.Target);
            ILHeapPointer.EmitDerefToAddress(_il, _types.Module, _types.Resolve(ma.Target.Type));
            EmitExpression(value);
            _il.Call(setterRef);
            return true;
        }
        if (useCall) EmitAddress(ma.Target);
        else EmitExpression(ma.Target);
        EmitExpression(value);
        if (useCall) _il.Call(setterRef);
        else _il.CallVirt(setterRef);
        return true;
    }

    // Resolve an `add_`/`remove_` accessor for an E#-declared event off the emitted
    // module type's EventDefinition. Used for `obj.OnX += h` / `-= h` where the event
    // lives in this same compilation (no runtime Type to reflect yet).
    bool TryResolveModuleEventAccessor(BoundType targetType, string eventName, bool isAdd,
        out MethodReference accessor, out TypeReference handlerType)
    {
        accessor = null!;
        handlerType = null!;
        TypeReference targetRef;
        try { targetRef = _types.Resolve(targetType); } catch { return false; }
        TypeDefinition? def;
        try { def = targetRef.Resolve(); } catch { return false; }
        var ev = def?.Events.FirstOrDefault(e => e.Name == eventName);
        if (ev is null) return false;
        var m = isAdd ? ev.AddMethod : ev.RemoveMethod;
        if (m is null) return false;
        accessor = _types.Module.ImportReference(m);
        handlerType = _types.Module.ImportReference(ev.EventType);
        return true;
    }
}
