using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Emit;
using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Syntax;

namespace Esharp.CodeGen;

public partial class MethodBodyEmitter
{

    // The `set_<name>` / init accessor for a property member of an in-compilation type,
    // re-homed onto a closed generic instance when needed — used to set a property through
    // a composite literal (`T { x: v }`), the object-initializer shape. Returns null for a
    // plain field (which takes a direct stfld instead).
    MethodReference? FindPropertySetter(TypeReference typeRef, string propName)
    {
        TypeDefinition? def;
        try { def = typeRef.Resolve(); }
        catch { return null; }
        var setter = def?.Methods.FirstOrDefault(m =>
            m.Name == "set_" + propName && !m.IsStatic && m.Parameters.Count == 1);
        if (setter is null) return null;
        if (typeRef is GenericInstanceType git) return HostMethodOnType(setter, git);
        return setter.Module == _types.Module ? setter : _types.Module.ImportReference(setter);
    }

    void EmitObjectCreation(BoundObjectCreationExpression oc)
    {
        var typeRef = _types.Resolve(oc.ObjectType);

        // Positional-ctor construction (e.g. `Chan<T>(capacity)`): match a ctor by argument
        // count, emit the args, `newobj`. Trailing Fields, if any, apply object-initializer
        // stores afterward. Distinct from the field-init/parameterless path below.
        if (oc.ConstructorArguments.Count > 0
            && typeRef.Resolve()?.Methods.FirstOrDefault(m =>
                   m.IsConstructor && !m.IsStatic && m.Parameters.Count == oc.ConstructorArguments.Count) is { } ctorDef)
        {
            var ctorRef = typeRef is GenericInstanceType git
                ? HostExternalMethodOnType(ctorDef, git)
                : _types.Module.ImportReference(ctorDef);
            for (var i = 0; i < oc.ConstructorArguments.Count; i++)
                EmitFieldValueCoerced(oc.ConstructorArguments[i],
                    i < ctorRef.Parameters.Count ? ctorRef.Parameters[i].ParameterType : null);
            _il.NewObj(ctorRef);
            if (oc.Fields.Count > 0)
            {
                var ctorLocal = new VariableDefinition(typeRef);
                _method.Body.Variables.Add(ctorLocal);
                _il.StoreLocal(ctorLocal);
                foreach (var fieldInit in oc.Fields)
                {
                    if (FindPropertySetter(typeRef, fieldInit.Name) is { } setterRef)
                    {
                        _il.LoadLocal(ctorLocal);
                        EmitFieldValueCoerced(fieldInit.Value, setterRef.Parameters.Count > 0 ? setterRef.Parameters[0].ParameterType : null);
                        _il.CallVirt(setterRef);
                        continue;
                    }
                    _il.LoadLocal(ctorLocal);
                    var fieldDef = FindFieldOnType(typeRef, fieldInit.Name);
                    EmitFieldValueCoerced(fieldInit.Value, fieldDef?.FieldType);
                    if (fieldDef is not null)
                        _il.StoreField(typeRef is GenericInstanceType
                            ? RebindFieldToDeclaring(fieldDef, typeRef) : (FieldReference)fieldDef);
                }
                _il.LoadLocal(ctorLocal);
            }
            return;
        }

        var local = new VariableDefinition(typeRef);
        _method.Body.Variables.Add(local);

        if (_types.IsValueType(oc.ObjectType))
        {
            // Struct: zero-init + field stores via address
            _il.LoadLocalAddress(local);
            _il.InitObj(typeRef);

            foreach (var fieldInit in oc.Fields)
            {
                // A property member is set through its setter (like a C# object initializer) —
                // the backing field is private and a struct's get-only/init backing is otherwise
                // unreachable from here. Plain fields take the direct stfld.
                if (FindPropertySetter(typeRef, fieldInit.Name) is { } setterRef)
                {
                    _il.LoadLocalAddress(local);
                    EmitFieldValueCoerced(fieldInit.Value, setterRef.Parameters.Count > 0 ? setterRef.Parameters[0].ParameterType : null);
                    _il.Call(setterRef);
                    continue;
                }
                _il.LoadLocalAddress(local);
                var fieldDef = FindFieldOnType(typeRef, fieldInit.Name);
                EmitFieldValueCoerced(fieldInit.Value, fieldDef?.FieldType);
                if (fieldDef is not null)
                    _il.StoreField(fieldDef);
            }
        }
        else
        {
            // Class: newobj + field stores via reference
            var typeDef = typeRef.Resolve();
            var ctor = _types.FindConstructor(typeDef);
            if (ctor is not null)
            {
                // For closed generic types the ctor reference must be scoped to
                // the closed DeclaringType — otherwise the newobj token resolves
                // to the open type and field stores hit a degenerate instance
                // (NRE on later access).
                var ctorRef = typeRef is GenericInstanceType
                    ? RebindToDeclaring(ctor, typeRef)
                    : ctor.Module == _types.Module
                        ? ctor
                        : _types.Module.ImportReference(ctor);
                _il.NewObj(ctorRef);
            }
            else
            {
                // No parameterless ctor found — try to import Object..ctor as fallback
                var objCtor = _types.Module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
                _il.NewObj(objCtor);
            }

            _il.StoreLocal(local);

            foreach (var fieldInit in oc.Fields)
            {
                if (FindPropertySetter(typeRef, fieldInit.Name) is { } setterRef)
                {
                    _il.LoadLocal(local);
                    EmitFieldValueCoerced(fieldInit.Value, setterRef.Parameters.Count > 0 ? setterRef.Parameters[0].ParameterType : null);
                    _il.CallVirt(setterRef);
                    continue;
                }
                _il.LoadLocal(local);
                var fieldDef = FindFieldOnType(typeRef, fieldInit.Name);
                EmitFieldValueCoerced(fieldInit.Value, fieldDef?.FieldType);
                if (fieldDef is not null)
                {
                    // Same rebinding for closed generic field references.
                    var fieldRef = typeRef is GenericInstanceType
                        ? RebindFieldToDeclaring(fieldDef, typeRef)
                        : (FieldReference)fieldDef;
                    _il.StoreField(fieldRef);
                }
            }
        }

        _il.LoadLocal(local);
    }

    // Cecil: emitting a token against a member of an open generic type requires
    // rebinding to the closed DeclaringType. The MethodReference/FieldReference
    // built here points at the same metadata but is scoped through the closed
    // generic instance so the JIT instantiates correctly.
    static MethodReference RebindToDeclaring(MethodReference original, TypeReference closedDeclaring)
    {
        var bound = new MethodReference(original.Name, original.ReturnType, closedDeclaring)
        {
            HasThis = original.HasThis,
            ExplicitThis = original.ExplicitThis,
            CallingConvention = original.CallingConvention,
        };
        foreach (var p in original.Parameters)
            bound.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        foreach (var gp in original.GenericParameters)
            bound.GenericParameters.Add(new GenericParameter(gp.Name, bound));
        return bound;
    }

    static FieldReference RebindFieldToDeclaring(FieldReference original, TypeReference closedDeclaring)
        => new(original.Name, original.FieldType, closedDeclaring);

    /// Emit a value into a field/parameter slot, applying conversions the
    /// binder couldn't fully express in the bound tree:
    ///   - bare name referring to a top-level function + fnptr slot → `ldftn`.
    ///   - everything else falls through to EmitExpression.
    /// The binder's `IsFunctionPointer` flag on function literals is already
    /// handled inside EmitFunctionLiteral, so this only covers the
    /// "named function reference" case.
    void EmitFieldValueCoerced(BoundExpression value, TypeReference? expectedSlotType)
    {
        // A durable `*T` field/property slot stores the `__Ptr_T` carrier, not
        // its pointee. This is deliberately a storage-boundary decision rather
        // than an object-initializer special case: `head` below is a source *T
        // parameter, whose ordinary source read is `head.Value`, but
        // `Node { next: head }` must preserve the wrapper identity.
        if (expectedSlotType is not null && ILHeapPointer.IsWrapperType(expectedSlotType))
        {
            EmitStorageValue(expectedSlotType, value);
            return;
        }

        if (expectedSlotType is Mono.Cecil.FunctionPointerType fp
            && value is Esharp.BoundTree.BoundNameExpression nameRef)
        {
            var method = FindMethod(nameRef.Name, fp.Parameters.Count);
            if (method is not null)
            {
                _il.LoadFunctionPointer(method);
                return;
            }
        }
        EmitExpression(value);
    }

    void EmitWith(BoundWithExpression w)
    {
        var typeRef = _types.Resolve(w.Type);
        var tmp = new VariableDefinition(typeRef);
        _method.Body.Variables.Add(tmp);

        var isValue = _types.IsValueType(w.Type);

        if (isValue)
        {
            // Value-type with: copy via Ldobj so the target's source location
            // (parameter, local, embedded field) doesn't matter — `tmp` always
            // ends up holding the *value*, not an address. Without ldobj, `with`
            // on a struct method's `self` stores `&self` into a P-typed local
            // (silent reinterpret cast) and field updates clobber garbage.
            EmitAddress(w.Target);
            _il.LoadObject(typeRef);
            _il.StoreLocal(tmp);
        }
        else
        {
            // Reference type — just copy the reference.
            EmitExpression(w.Target);
            _il.StoreLocal(tmp);
        }

        foreach (var fieldInit in w.Fields)
        {
            if (isValue) _il.LoadLocalAddress(tmp);
            else _il.LoadLocal(tmp);

            var fieldDef = FindFieldOnType(typeRef, fieldInit.Name);
            EmitFieldValueCoerced(fieldInit.Value, fieldDef?.FieldType);
            if (fieldDef is not null)
                _il.StoreField(fieldDef);
        }

        _il.LoadLocal(tmp);
    }

    void EmitTupleLiteral(BoundTupleLiteralExpression tuple)
    {
        if (tuple.Type is not TupleType tt) return;

        // Build the closed `ValueTuple<…>` via Cecil so user `data` and `*T`
        // element types survive — the reflection (System.Type) path can't name a
        // type being emitted in this module and erases every such arg to `object`,
        // producing `ValueTuple<object,object>` against a precise consumer.
        var closed = _types.Resolve(tt);
        if (closed is not GenericInstanceType git)
        {
            foreach (var element in tuple.Elements) EmitExpression(element);
            return;
        }

        var ctorDef = git.Resolve()?.Methods.FirstOrDefault(
            m => m.IsConstructor && m.Parameters.Count == tt.ElementTypes.Count);
        if (ctorDef is null)
        {
            foreach (var element in tuple.Elements) EmitExpression(element);
            return;
        }

        var ctorRef = HostExternalMethodOnType(ctorDef, git);
        foreach (var element in tuple.Elements)
            EmitExpression(element);
        _il.NewObj(ctorRef);
    }

    void EmitListLiteral(BoundListLiteralExpression list)
    {
        // Build `List<elem>` via Cecil for the same reason as tuples above: a user
        // `data`/`*T` element type has no System.Type while the module is being
        // emitted, so the reflection path would erase it to `List<object>`.
        var elemRef = _types.Resolve(list.ElementType);
        var openList = _types.Module.ImportReference(typeof(List<>));
        var closed = new GenericInstanceType(openList);
        closed.GenericArguments.Add(elemRef);

        var listDef = closed.Resolve()!;
        var ctorDef = listDef.Methods.First(m => m.IsConstructor && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "System.Int32");
        var addDef = listDef.Methods.First(m => m.Name == "Add" && m.Parameters.Count == 1);
        var ctorRef = HostExternalMethodOnType(ctorDef, closed);
        var addRef = HostExternalMethodOnType(addDef, closed);

        _il.LoadInt(list.Elements.Count);
        _il.NewObj(ctorRef);

        foreach (var element in list.Elements)
        {
            _il.Dup();
            EmitExpression(element);
            _il.CallVirt(addRef);
        }
    }
}
