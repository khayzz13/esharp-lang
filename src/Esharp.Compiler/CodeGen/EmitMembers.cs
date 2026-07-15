using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Emit;
using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Syntax;
using ILOpCode = System.Reflection.Metadata.ILOpCode;

namespace Esharp.CodeGen;

public partial class MethodBodyEmitter
{

    void EmitIndexRead(BoundIndexExpression idx)
    {
        // `T[]` element read — `ldelem`. The element type comes from the Cecil resolve
        // (not BoundTypeToRuntime, which erases a user-struct element to `object`), so a
        // `P[]` reads a `P`, not an `object`.
        if (idx.Target.Type is ArrayBoundType arrRead)
        {
            var elemRef = _types.Resolve(arrRead.ElementType);
            EmitExpression(idx.Target);
            EmitIndexValue(idx.Index, typeof(object), isArray: true);
            _il.LoadElement(elemRef);
            _lastCallWasVoid = false;
            return;
        }

        // Generic-instance receiver with a module type arg (`List<Pt>`): host the
        // open `get_Item` on the closed Cecil instance so the element type is `Pt`,
        // not the erased `object` the runtime-reflection path would produce.
        if (TryEmitModuleGenericIndexerRead(idx))
            return;

        var runtimeType = _types.BoundTypeToRuntime(idx.Target.Type);
        if (runtimeType is not null && runtimeType.IsArray)
        {
            var elementType = runtimeType.GetElementType()!;
            EmitExpression(idx.Target);
            EmitIndexValue(idx.Index, runtimeType, isArray: true);
            EmitArrayLoad(elementType);
            _lastCallWasVoid = false;
            return;
        }

        if (runtimeType is not null)
        {
            var getter = ResolveIndexerGetter(runtimeType);
            if (getter is not null)
            {
                var getterRef = _types.Module.ImportReference(getter);
                EmitExpression(idx.Target);
                EmitIndexValue(idx.Index, runtimeType, isArray: false);
                { if (getter.DeclaringType!.IsValueType) _il.Call(getterRef); else _il.CallVirt(getterRef); }

                _lastCallWasVoid = false;
                return;
            }
        }

        // Backstop: the binder now catches non-indexable owned types (ES2145); reaching here is
        // an external type whose indexer didn't resolve via reflection.
        _diagnostics.Report("", 0, 0, $"IL: unresolved indexer read on type '{idx.Target.Type}'");
        // Best-effort fallback: push target + index (doesn't balance the stack correctly)
        EmitExpression(idx.Target);
        EmitExpression(idx.Index);
    }

    // Read an instance property whose receiver is a closed generic instance carrying
    // a module type arg (`List<Pt>`, `List<*Box>`). The open getter is re-hosted on
    // the closed Cecil instance so verification sees the precise receiver type.
    bool TryEmitModuleGenericProperty(BoundMemberAccessExpression ma)
    {
        TypeReference closed;
        try { closed = _types.Resolve(ma.Target.Type); }
        catch { return false; }
        if (closed is not GenericInstanceType git) return false;
        if (!git.GenericArguments.Any(a => MentionsModuleType(a, _types.Module))) return false;

        // Walk the base chain, substituting generic args at each step, so a property
        // declared on a generic BASE (e.g. `Channel<T>.Writer`, where `Writer` lives on
        // `Channel<TWrite,TRead>` and `Channel<T> : Channel<T,T>`) is hosted on the
        // correctly-closed declaring instance rather than erased by reflection.
        var currentClosed = git;
        MethodDefinition? getter = null;
        while (true)
        {
            var def = currentClosed.Resolve();
            getter = def?.Methods.FirstOrDefault(
                m => !m.IsStatic && m.Name == "get_" + ma.MemberName && m.Parameters.Count == 0);
            if (getter is not null) break;
            if (def?.BaseType is not GenericInstanceType baseGit) return false;
            currentClosed = SubstituteGenericArgs(baseGit, currentClosed);
        }

        var getterRef = HostExternalMethodOnType(getter, currentClosed);
        var isValueType = currentClosed.Resolve()!.IsValueType;
        if (isValueType) EmitAddress(ma.Target); else EmitExpression(ma.Target);
        { if (isValueType) _il.Call(getterRef); else _il.CallVirt(getterRef); }

        _lastCallWasVoid = false;
        return true;
    }

    /// Close `open` (a base-type reference whose args reference the derived type's
    /// generic parameters, e.g. `Channel`2<!0,!0>`) by substituting each `!i` with the
    /// derived closed instance's actual argument (`Channel`1<T0>` → `Channel`2<T0,T0>`).
    /// Recurses through nested generic args.
    GenericInstanceType SubstituteGenericArgs(GenericInstanceType open, GenericInstanceType derived)
    {
        var result = new GenericInstanceType(_types.Module.ImportReference(open.ElementType));
        foreach (var a in open.GenericArguments)
            result.GenericArguments.Add(SubstituteArg(a, derived));
        return result;
    }

    TypeReference SubstituteArg(TypeReference arg, GenericInstanceType derived) => arg switch
    {
        GenericParameter gp when gp.Position < derived.GenericArguments.Count => derived.GenericArguments[gp.Position],
        GenericInstanceType nested => SubstituteGenericArgs(nested, derived),
        _ => _types.Module.ImportReference(arg),
    };

    bool TryEmitModuleGenericIndexerRead(BoundIndexExpression idx)
    {
        TypeReference closed;
        try { closed = _types.Resolve(idx.Target.Type); }
        catch { return false; }
        if (closed is not GenericInstanceType git) return false;
        if (!git.GenericArguments.Any(a => MentionsModuleType(a, _types.Module))) return false;

        var def = git.Resolve();
        var getItem = def?.Methods.FirstOrDefault(m => !m.IsStatic && m.Name == "get_Item" && m.Parameters.Count == 1);
        if (getItem is null) return false;

        var getterRef = HostExternalMethodOnType(getItem, git);
        // The erased runtime type still serves the from-end (`^n`) length lookup —
        // `Count` is non-generic — while the hosted getter loads the real element.
        var erasedRuntime = _types.BoundTypeToRuntime(idx.Target.Type) ?? typeof(object);
        EmitExpression(idx.Target);
        EmitIndexValue(idx.Index, erasedRuntime, isArray: false);
        _il.CallVirt(getterRef);
        _lastCallWasVoid = false;
        return true;
    }

    /// Emit the index operand. A from-end index `^n` lowers to `Length/Count - n`;
    /// the target reference must already be on the stack (it is duplicated to read
    /// the length). A plain index is emitted as-is.
    void EmitIndexValue(BoundExpression index, Type runtimeType, bool isArray)
    {
        if (index is not BoundUnaryExpression { Op: SyntaxTokenKind.Caret } fromEnd)
        {
            EmitExpression(index);
            return;
        }

        _il.Dup();
        if (isArray)
        {
            _il.LoadLength();
            _il.EmitPrimitive(ILOpCode.Conv_i4);
        }
        else
        {
            var countProp = runtimeType.GetProperty("Count") ?? runtimeType.GetProperty("Length");
            var getter = _types.Module.ImportReference(countProp!.GetGetMethod()!);
            { if (runtimeType.IsValueType) _il.Call(getter); else _il.CallVirt(getter); }

        }
        EmitExpression(fromEnd.Operand);
        _il.Sub();
    }

    void EmitIndexAssignment(BoundIndexExpression idx, BoundExpression value)
    {
        // `T[]` element write — `stelem`. Element type from the Cecil resolve so a
        // `P[]` stores a `P` (the runtime path would erase a user element to `object`).
        if (idx.Target.Type is ArrayBoundType arrWrite)
        {
            var elemRef = _types.Resolve(arrWrite.ElementType);
            EmitExpression(idx.Target);
            EmitIndexValue(idx.Index, typeof(object), isArray: true);
            EmitExpression(value);
            _il.StoreElement(elemRef);
            return;
        }

        // Generic-instance receiver with a module type arg (`Dictionary<string, Pt>`,
        // `List<Pt>`): host the open `set_Item` on the closed Cecil instance so the
        // value parameter is `Pt`, not the erased `object` the runtime-reflection
        // path below would produce.
        if (TryEmitModuleGenericIndexerWrite(idx, value))
            return;

        var runtimeType = _types.BoundTypeToRuntime(idx.Target.Type);
        if (runtimeType is not null && runtimeType.IsArray)
        {
            var elementType = runtimeType.GetElementType()!;
            EmitExpression(idx.Target);
            EmitIndexValue(idx.Index, runtimeType, isArray: true);
            EmitExpression(value);
            EmitArrayStore(elementType);
            return;
        }

        if (runtimeType is not null)
        {
            var setter = runtimeType.GetProperty("Item")?.GetSetMethod();
            if (setter is not null)
            {
                var setterRef = _types.Module.ImportReference(setter);
                EmitExpression(idx.Target);
                EmitIndexValue(idx.Index, runtimeType, isArray: false);
                EmitExpression(value);
                { if (setter.DeclaringType!.IsValueType) _il.Call(setterRef); else _il.CallVirt(setterRef); }

                return;
            }
        }

        _diagnostics.Report("", 0, 0, $"IL: unresolved indexer assignment on type '{idx.Target.Type}'");
    }

    bool TryEmitModuleGenericIndexerWrite(BoundIndexExpression idx, BoundExpression value)
    {
        TypeReference closed;
        try { closed = _types.Resolve(idx.Target.Type); }
        catch { return false; }
        if (closed is not GenericInstanceType git) return false;
        if (!git.GenericArguments.Any(a => MentionsModuleType(a, _types.Module))) return false;

        var def = git.Resolve();
        var setItem = def?.Methods.FirstOrDefault(m => !m.IsStatic && m.Name == "set_Item" && m.Parameters.Count == 2);
        if (setItem is null) return false;

        var setterRef = HostExternalMethodOnType(setItem, git);
        var erasedRuntime = _types.BoundTypeToRuntime(idx.Target.Type) ?? typeof(object);
        EmitExpression(idx.Target);
        EmitIndexValue(idx.Index, erasedRuntime, isArray: false);
        EmitExpression(value);
        _il.CallVirt(setterRef);
        return true;
    }

    void EmitMemberAccess(BoundMemberAccessExpression ma)
    {
        if (ma.IsPropertyLocationProjection
            && TryEmitInCompilationPropertyLocation(ma))
        {
            _il.LoadObject(_types.Resolve(ma.Type));
            return;
        }

        // Auto-deref through *T heap pointer — but NOT for the self param in
        // promoted instance methods (self is already a managed pointer via arg0)
        if (ma.Target.Type is HeapPointerBoundType hp
            && !(ma.Target is BoundNameExpression sn && IsSelfReference(sn.Name)))
        {
            EmitHeapPointerMemberAccess(ma, hp);
            return;
        }

        // `T[]` length — `ldlen` (a native uint) narrowed to int, never a property call.
        if (ma.Target.Type is ArrayBoundType && ma.MemberName == "Length")
        {
            EmitExpression(ma.Target);
            _il.LoadLength();
            _il.EmitPrimitive(ILOpCode.Conv_i4);
            _lastCallWasVoid = false;
            return;
        }

        // Result<T,E> intrinsic accessors (.IsOk / .IsError / .Value / .Error). This
        // MUST precede the generic field path below: the E# stdlib Result exposes
        // Value/Error as PUBLIC FIELDS, so plain field resolution would emit a raw
        // `ldfld` and skip the throw-on-wrong-variant guard. Routing through the
        // Result-aware helper keeps the guard (and works against the C# seed's
        // property getters too). See EmitResultMemberLoad.
        if (ma.Target.Type is Esharp.BoundTree.ResultType
            && ma.MemberName is "IsOk" or "IsError" or "Value" or "Error")
        {
            var closedResult = _types.Resolve(ma.Target.Type);
            // Spill the receiver to a temp: the guarded accessor reads BOTH the
            // discriminant and the payload field, and the receiver is often an rvalue
            // (`parse(n).Value`, `Result.Error(e).Error`) with no address of its own.
            // A single spill makes it addressable and evaluated exactly once — without
            // it the receiver is re-evaluated per field touch (double side effects) or
            // `ldfld`'d on a bare value (invalid IL).
            var recvTmp = new VariableDefinition(closedResult);
            _method.Body.Variables.Add(recvTmp);
            EmitExpression(ma.Target);
            _il.StoreLocal(recvTmp);
            EmitResultMemberLoad(closedResult, ma.MemberName, () => _il.LoadLocalAddress(recvTmp), guarded: true);
            return;
        }

        // User-defined `static Foo { let X = 3 ... }` field access:
        // resolve via Mono.Cecil Module.Types since the runtime Type doesn't exist yet.
        if (ma.Target is BoundNameExpression sfTypeName && ma.Target.Type is Esharp.BoundTree.StaticFuncType)
        {
            var sfType = _types.Module.Types.FirstOrDefault(t => t.Name == ((Esharp.BoundTree.StaticFuncType)sfTypeName.Type).Name);
            if (sfType is not null)
            {
                var sfField = sfType.Fields.FirstOrDefault(f => f.Name == ma.MemberName);
                if (sfField is not null)
                {
                    if (sfField.IsLiteral && sfField.Constant is not null)
                    {
                        switch (sfField.Constant)
                        {
                            case int i: _il.LoadInt(i); return;
                            case long l: _il.LoadLong(l); return;
                            case bool b: _il.LoadInt(b ? 1 : 0); return;
                            case string s: _il.LoadString(s); return;
                        }
                    }
                    _il.LoadStaticField(sfField);
                    return;
                }
            }
        }

        if (ma.Target is BoundNameExpression staticTypeName)
        {
            var runtimeStaticType = _types.TryResolveRuntimeType(staticTypeName.Name);
            if (runtimeStaticType is not null)
            {
                var staticField = runtimeStaticType.GetField(ma.MemberName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (staticField is not null)
                {
                    // Enum literal fields and const fields: emit the raw value, not ldsfld
                    if (staticField.IsLiteral && staticField.GetRawConstantValue() is { } constVal)
                    {
                        switch (constVal)
                        {
                            case int i: _il.LoadInt(i); return;
                            case byte b: _il.LoadInt((int)b); return;
                            case short s: _il.LoadInt((int)s); return;
                            case long l: _il.LoadLong(l); return;
                            case uint u: _il.LoadInt((int)u); return;
                        }
                    }
                    _il.LoadStaticField(_types.Module.ImportReference(staticField));
                    return;
                }

                var staticProperty = runtimeStaticType.GetProperty(ma.MemberName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var staticGetter = staticProperty?.GetGetMethod();
                if (staticGetter is not null)
                {
                    _il.Call(_types.Module.ImportReference(staticGetter));
                    _lastCallWasVoid = false;
                    return;
                }
            }
        }

        // C#-sourced field/property access. We use the handle directly so Cecil
        // never tries to .Resolve() against the not-yet-loadable C# half assembly.
        if (ma.Target.Type is Esharp.BoundTree.ExternalCSharpType cs)
        {
            var member = cs.Handle.Members.FirstOrDefault(m => m.Name == ma.MemberName);
            if (member is not null)
            {
                if (member.Kind == Esharp.BoundTree.CSharpMemberKind.Field)
                {
                    // Enum members and `const` fields are literal — emit the
                    // constant value inline instead of going through ldsfld.
                    // This matches how C# itself emits enum loads and avoids
                    // a runtime MissingFieldException because enum const fields
                    // don't have a static initializer in the merged assembly.
                    if (member.IsStatic && member.ConstantValue is not null)
                    {
                        switch (member.ConstantValue)
                        {
                            case int i: _il.LoadInt(i); return;
                            case long l: _il.LoadLong(l); return;
                            case bool b: _il.LoadInt(b ? 1 : 0); return;
                            case string s: _il.LoadString(s); return;
                            case byte by: _il.LoadInt((int)by); return;
                            case short sh: _il.LoadInt((int)sh); return;
                            case uint u: _il.LoadInt(unchecked((int)u)); return;
                            case ulong ul: _il.LoadLong(unchecked((long)ul)); return;
                            case float f: _il.LoadFloat(f); return;
                            case double d: _il.LoadDouble(d); return;
                        }
                    }
                    if (TryResolveField(ma, out var csField))
                    {
                        if (member.IsStatic) _il.LoadStaticField(csField);
                        else
                        {
                            EmitExpression(ma.Target);
                            _il.LoadField(csField);
                        }
                        return;
                    }
                }
                else if (member.Kind == Esharp.BoundTree.CSharpMemberKind.Property)
                {
                    // Property reads go through get_X(); call the handle-derived MethodReference.
                    var getter = BuildCSharpGetterReference(cs, ma.MemberName, member);
                    if (member.IsStatic) _il.Call(getter);
                    else
                    {
                        EmitExpression(ma.Target);
                        _il.CallVirt(getter);
                    }
                    if (member.ReturnsByRef)
                        _il.LoadObject(_types.Resolve(member.ReturnType));
                    return;
                }
            }
        }

        // In-compilation property read: a computed/auto/get-only property routes through
        // its `get_x` accessor (never the private backing field — which is inaccessible
        // from another type and would also bypass a computed getter). Checked BEFORE the
        // field path because the FindFieldOnType bridge would otherwise resolve the backing
        // field by the property's name.
        if (TryEmitInCompilationPropertyGet(ma))
            return;

        // Field path (user-defined types + BCL public fields)
        if (TryResolveField(ma, out var field))
        {
            var resolved = field.Resolve();
            // Literal const fields (incl. enum members) have no storage at
            // runtime — Ldsfld throws MissingFieldException. Emit the constant
            // value inline. This matches how C# itself emits enum loads.
            if (resolved?.IsLiteral == true && resolved.Constant is not null)
            {
                switch (resolved.Constant)
                {
                    case int i: _il.LoadInt(i); return;
                    case byte by: _il.LoadInt((int)by); return;
                    case sbyte sb: _il.LoadInt((int)sb); return;
                    case short sh: _il.LoadInt((int)sh); return;
                    case ushort us: _il.LoadInt((int)us); return;
                    case long l: _il.LoadLong(l); return;
                    case uint u: _il.LoadInt(unchecked((int)u)); return;
                    case ulong ul: _il.LoadLong(unchecked((long)ul)); return;
                    case bool b: _il.LoadInt(b ? 1 : 0); return;
                    case string s: _il.LoadString(s); return;
                    case float f: _il.LoadFloat(f); return;
                    case double d: _il.LoadDouble(d); return;
                }
            }
            if (resolved?.IsStatic == true || field is FieldDefinition { IsStatic: true })
            {
                _il.LoadStaticField(field);
            }
            else
            {
                if (_types.IsValueType(ma.Target.Type))
                    EmitAddress(ma.Target);
                else
                    EmitExpression(ma.Target);
                _il.LoadField(field);
            }
            return;
        }

        // Module-generic property (`List<Pt>.Count`, `List<*Box>.Capacity`): host the
        // open `get_<name>` on the closed Cecil instance so the receiver type matches.
        // The reflection path below resolves it on the erased `List<object>`, whose
        // declaring type does not match the `List<Pt>` value on the stack.
        if (TryEmitModuleGenericProperty(ma))
            return;

        // Property path: resolve get_X on the runtime type
        var runtimeType = _types.BoundTypeToRuntime(ma.Target.Type);
        if (runtimeType is not null)
        {
            // On an interface, GetProperty does NOT walk base interfaces, so a member
            // inherited from a base (e.g. `Count` on IReadOnlyDictionary<K,V>, declared
            // on IReadOnlyCollection<…>) is missed — search the interface hierarchy.
            var property = runtimeType.GetProperty(ma.MemberName) ?? FindInheritedInterfaceProperty(runtimeType, ma.MemberName);
            var getter = property?.GetGetMethod();
            if (getter is not null)
            {
                var getterRef = _types.Module.ImportReference(getter);
                if (getter.IsStatic)
                {
                    _il.Call(getterRef);
                }
                else
                {
                    var useCall = runtimeType.IsValueType || _types.IsValueType(ma.Target.Type);
                    if (useCall)
                        EmitAddress(ma.Target);
                    else
                        EmitExpression(ma.Target);
                    { if (useCall) _il.Call(getterRef); else _il.CallVirt(getterRef); }

                }
                if (getter.ReturnType.IsByRef)
                    _il.LoadObject(_types.Resolve(ma.Type));
                _lastCallWasVoid = false;
                return;
            }
        }

        // Nothing resolved — emit target to keep stack shape; diagnostic could be added later
        if (_types.IsValueType(ma.Target.Type))
            EmitAddress(ma.Target);
        else
            EmitExpression(ma.Target);
    }

    // Route a read of an in-compilation property — computed (`let x => expr`) or
    // auto-backed — through its `get_<name>` accessor. The property has no field, so
    // the field path misses it; here we find the getter on the receiver's emitted
    // TypeDefinition and call it (re-homed onto a generic instance when the receiver
    // is closed, e.g. `Enumerator<int>::get_Current`).
    bool TryEmitInCompilationPropertyGet(BoundMemberAccessExpression ma)
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
            // An interface property requirement is read through its abstract `get_<name>`
            // slot — `n.id` where `n : INamed` is `callvirt INamed::get_id`.
            Esharp.BoundTree.InterfaceType it => it.Name,
            _ => null,
        };
        if (typeName is null) return false;

        TypeDefinition? owner = null;
        foreach (var t in _types.Module.Types)
            if (t.Name == typeName) { owner = t; break; }
        // Generic types carry a backtick-arity suffix in their CLR name.
        owner ??= _types.Module.Types.FirstOrDefault(t => t.Name.StartsWith(typeName + "`", StringComparison.Ordinal));
        if (owner is null) return false;

        // A derived receiver may read a property declared on its base.  Property
        // storage is private, so falling through to FindFieldOnType would either
        // violate CLR accessibility or bypass the accessor contract.
        var getterOwner = owner;
        MethodDefinition? getter = null;
        while (getterOwner is not null && getter is null)
        {
            getter = getterOwner.Methods.FirstOrDefault(m =>
                m.Name == "get_" + ma.MemberName && !m.IsStatic && m.Parameters.Count == 0);
            if (getter is null)
            {
                try { getterOwner = getterOwner.BaseType?.Resolve(); }
                catch { getterOwner = null; }
            }
        }
        if (getter is null) return false;

        Mono.Cecil.MethodReference getterRef = getter;
        if (_types.Resolve(ma.Target.Type) is GenericInstanceType git)
            getterRef = HostMethodOnType(getter, git);
        else if (owner.HasGenericParameters && ReferenceEquals(owner, _method.DeclaringType))
        {
            var self = new GenericInstanceType(owner);
            foreach (var parameter in owner.GenericParameters) self.GenericArguments.Add(parameter);
            getterRef = HostMethodOnType(getter, self);
        }

        var useCall = owner.IsValueType;
        if (ma.Target.Type is HeapPointerBoundType)
        {
            // `p.property` where p : *Struct invokes the struct accessor on
            // the wrapper's Value address. EmitAddress(p) would produce the
            // address of the wrapper field itself (`__Ptr_T&`), not `T&`.
            EmitHeapPointerCarrier(ma.Target);
            ILHeapPointer.EmitDerefToAddress(_il, _types.Module, _types.Resolve(ma.Target.Type));
            _il.Call(getterRef);
        }
        else
        {
            if (useCall) EmitAddress(ma.Target);
            else EmitExpression(ma.Target);
            if (useCall) _il.Call(getterRef); else _il.CallVirt(getterRef);
        }

        _lastCallWasVoid = false;
        return true;
    }

    // ── Result accessor / construction, adapted to the active Result model ──────
    // The E#-authored stdlib Result (Esharp.Stdlib.Result`2) exposes PUBLIC FIELDS
    // (IsOk/Value/Error) and has no factory class; the static helper exposes
    // PROPERTY GETTERS (which throw on the wrong variant) plus static Ok/Error factories.
    // Every Result intrinsic site (`.Value` access, `?`, `match`, `ok()`/`error()`)
    // routes through these helpers so the lowering stays agnostic to which is bound.

    // A field on the closed Result instance (`Result`2<ok,err>::IsOk`). Imports the
    // open field (FieldType `!0`/`bool`) then re-homes onto the closed instance so the
    // CLR substitutes the generic args — mirrors the SelfField pattern.
    FieldReference ResultField(TypeReference closedResult, string name)
    {
        var fi = ILTypeResolver.ResultOpenType().GetField(name)
            ?? throw new InvalidOperationException($"the active Result type has no field '{name}'");
        var imp = _types.Module.ImportReference(fi);
        return new FieldReference(imp.Name, imp.FieldType, closedResult);
    }

    // Load a Result accessor (.IsOk / .IsError / .Value / .Error) onto the stack.
    // `emitReceiverAddr` pushes the managed pointer to the value-type Result (it may be
    // invoked more than once for the guard). `guarded` keeps the throw-on-wrong-variant
    // contract for .Value/.Error on direct member access; the `?`/match sites pass
    // guarded:false because their branch has already proved the variant.
    void EmitResultMemberLoad(TypeReference closedResult, string member, Action emitReceiverAddr, bool guarded = true)
    {
        if (ILTypeResolver.ResultIsStdlib())
        {
            switch (member)
            {
                case "IsOk":
                    emitReceiverAddr();
                    _il.LoadField(ResultField(closedResult, "IsOk"));
                    break;
                case "IsError":
                    emitReceiverAddr();
                    _il.LoadField(ResultField(closedResult, "IsOk"));
                    _il.LoadInt(0);
                    _il.Ceq();
                    break;
                case "Value":
                case "Error":
                    var wantOk = member == "Value";
                    if (guarded)
                    {
                        emitReceiverAddr();
                        _il.LoadField(ResultField(closedResult, "IsOk"));
                        var okLabel = _il.DefineLabel();
                        { if (wantOk) _il.BranchIfTrue(okLabel); else _il.BranchIfFalse(okLabel); }

                        _il.LoadString(wantOk ? "Result does not contain a value." : "Result does not contain an error.");
                        _il.NewObj(_types.Module.ImportReference(typeof(InvalidOperationException).GetConstructor([typeof(string)])!));
                        _il.Throw();
                        _il.MarkLabel(okLabel);
                    }
                    emitReceiverAddr();
                    _il.LoadField(ResultField(closedResult, member));
                    break;
                default:
                    throw new InvalidOperationException($"unknown Result member '{member}'");
            }
        }
        _lastCallWasVoid = false;
    }

    // Construct a Result onto the stack. `emitPayload` pushes the success value (isOk)
    // or the error value. The stdlib Result has no factory, so build the struct inline
    // (initobj zeroes both slots + the false discriminant; set IsOk + the payload field);
    // the C# seed routes through its static Ok/Error generic factory.
    void EmitResultConstruct(TypeReference closedResult, bool isOk, TypeReference okRef, TypeReference errRef, Action emitPayload)
    {
        if (ILTypeResolver.ResultIsStdlib())
        {
            // Evaluate the payload FIRST, into a temp, so its own control flow runs on a
            // clean stack. `emitPayload` can early-`ret` (it may itself contain a `?`
            // propagation, e.g. `ok(parse(x)? + 1)`); pushing the receiver address before
            // it would leave that address dangling on the early-return path — the JIT
            // rejects the `ret` with a non-empty stack. (The factory path below is already
            // safe: it emits the payload then calls, with nothing pre-pushed.)
            var payloadTmp = new VariableDefinition(isOk ? okRef : errRef);
            _method.Body.Variables.Add(payloadTmp);
            emitPayload();
            _il.StoreLocal(payloadTmp);

            var tmp = new VariableDefinition(closedResult);
            _method.Body.Variables.Add(tmp);
            _il.LoadLocalAddress(tmp);
            _il.InitObj(closedResult);
            if (isOk)
            {
                _il.LoadLocalAddress(tmp);
                _il.LoadInt(1);
                _il.StoreField(ResultField(closedResult, "IsOk"));
            }
            _il.LoadLocalAddress(tmp);
            _il.LoadLocal(payloadTmp);
            _il.StoreField(ResultField(closedResult, isOk ? "Value" : "Error"));
            _il.LoadLocal(tmp);
        }
        _lastCallWasVoid = false;
    }

    // A property declared on a base interface of `t` (interfaces don't inherit
    // members through GetProperty the way classes do). Returns the first match
    // walking the transitive interface set.
    static System.Reflection.PropertyInfo? FindInheritedInterfaceProperty(Type t, string name)
    {
        if (!t.IsInterface) return null;
        foreach (var baseIface in t.GetInterfaces())
        {
            var p = baseIface.GetProperty(name);
            if (p is not null) return p;
        }
        return null;
    }

    Mono.Cecil.MethodReference BuildCSharpGetterReference(
        Esharp.BoundTree.ExternalCSharpType cs,
        string propertyName,
        Esharp.BoundTree.ICSharpMemberHandle member)
    {
        var declaringRef = _types.Resolve(cs);
        var returnRef = _types.Resolve(member.ReturnType);
        if (member.ReturnsByRef)
        {
            TypeReference byRef = new ByReferenceType(returnRef);
            if (member.ReturnsByRefReadonly)
                byRef = new RequiredModifierType(
                    _types.Module.ImportReference(typeof(System.Runtime.InteropServices.InAttribute)), byRef);
            returnRef = byRef;
        }
        var methodRef = new Mono.Cecil.MethodReference("get_" + propertyName, returnRef, declaringRef)
        {
            HasThis = !member.IsStatic,
        };
        return methodRef;
    }

    Mono.Cecil.MethodReference BuildCSharpSetterReference(
        Esharp.BoundTree.ExternalCSharpType cs,
        string propertyName,
        Esharp.BoundTree.ICSharpMemberHandle member)
    {
        var declaringRef = _types.Resolve(cs);
        var methodRef = new Mono.Cecil.MethodReference("set_" + propertyName,
            _types.Module.TypeSystem.Void, declaringRef)
        {
            HasThis = !member.IsStatic,
        };
        methodRef.Parameters.Add(new Mono.Cecil.ParameterDefinition("value",
            Mono.Cecil.ParameterAttributes.None, _types.Resolve(member.ReturnType)));
        return methodRef;
    }

    // Build a MethodReference for a C#-defined method, scoped to the C# half
    // assembly. Parameters and return type are resolved through the handle's
    // signature surface, not via .Resolve() — which would fail because the
    // C# half PE isn't loaded yet at E# IL emit time. ILRepack rewrites the
    // assembly scope on the resulting reference during fusion.
    internal Mono.Cecil.MethodReference BuildCSharpMethodReference(
        Esharp.BoundTree.ExternalCSharpType cs,
        string methodName,
        Esharp.BoundTree.ICSharpMemberHandle member)
    {
        var declaringRef = _types.Resolve(cs);
        var returnRef = _types.Resolve(member.ReturnType);
        var methodRef = new Mono.Cecil.MethodReference(methodName, returnRef, declaringRef)
        {
            HasThis = !member.IsStatic,
            ExplicitThis = false,
            CallingConvention = Mono.Cecil.MethodCallingConvention.Default,
        };
        foreach (var p in member.Parameters)
            methodRef.Parameters.Add(new Mono.Cecil.ParameterDefinition(p.Name, Mono.Cecil.ParameterAttributes.None, _types.Resolve(p.Type)));
        return methodRef;
    }
}
