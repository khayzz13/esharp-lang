using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Emit;
using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Syntax;

namespace Esharp.CodeGen;

public partial class MethodBodyEmitter
{

    void EmitAddressOf(BoundAddressOfExpression ao)
    {
        // ldftn — push function pointer onto the stack
        var methodRef = FindMethod(ao.FunctionName, ao.ParameterTypes.Count);
        if (methodRef is not null)
            _il.LoadFunctionPointer(methodRef);
    }

    // Method-group → delegate: a bare `func` name used where a delegate type is
    // expected. Binds directly to the named method — `ldnull; ldftn <method>;
    // newobj <DelegateType>::.ctor(object, native int)` — so the resulting delegate's
    // .Method is the real method (no forwarder), and it bridges across assemblies
    // like any C# delegate. Target is null because free functions are static.
    void EmitMethodGroupConversion(BoundMethodGroupConversion mg)
    {
        var module = _types.Module;
        var methodRef = FindMethod(mg.FunctionName, mg.ParameterCount);
        if (methodRef is null)
        {
            _diagnostics.Report("", 0, 0,
                $"IL: method-group target '{mg.FunctionName}' (argCount={mg.ParameterCount}) was not emitted");
            return;
        }

        // A closure trampoline can live on a generic display class (for example
        // TaskScope.Chan<T>'s cleanup captures a Chan<T>).  `ldftn` must name that
        // method on the receiver's closed display instance; a MethodDefinition on
        // the open `<>c__Display`1` leaves the CLR unable to instantiate the target.
        if (mg.Receiver is not null
            && _types.Resolve(mg.Receiver.Type) is GenericInstanceType closedReceiver
            && methodRef.Resolve() is { } methodDefinition
            && methodDefinition.DeclaringType.FullName == closedReceiver.ElementType.FullName)
        {
            methodRef = HostExternalMethodOnType(methodDefinition, closedReceiver);
        }

        // A zero-capture literal target-typed as `&(…)` is a raw managed function
        // pointer, not a delegate object. ClosureConversion preserves that binder
        // decision on the method-group node; materializing Func/Action here would
        // pass an object where the caller's calli expects native int.
        if (mg.DelegateType is Esharp.BoundTree.FunctionPointerType)
        {
            _il.LoadFunctionPointer(methodRef);
            return;
        }

        // Calls (notably event add_/remove_ accessors) can supply the nominal
        // delegate parameter type at the use site. A closure has already become a
        // method group by this point, so honor that slot here rather than defaulting
        // to the structural Func/Action type carried by the lambda conversion.
        var delegateRef = _pendingDelegateTargetType ?? _types.Resolve(mg.DelegateType);
        var ctor = new MethodReference(".ctor", module.ImportReference(typeof(void)), delegateRef)
        {
            HasThis = true,
        };
        ctor.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(object))));
        ctor.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(System.IntPtr))));

        // The delegate target (the `object` arg of the delegate .ctor): a bound display
        // instance for a closure trampoline, or null for a static method-group target.
        // `ldftn` binds the function pointer the same way for an instance or static method.
        if (mg.Receiver is not null)
            EmitExpression(mg.Receiver);
        else
            _il.LoadNull();
        _il.LoadFunctionPointer(methodRef);
        _il.NewObj(ctor);
    }

    void EmitAddressOfVariable(BoundAddressOfVariableExpression aov)
    {
        // &varName → ldloca / ldarga / ldflda depending on slot type
        if (aov.Target is BoundNameExpression name)
        {
            var slot = TryResolveSlot(name.Name);
            // A heap-promoted local: `&local` is the wrapper reference itself, so the
            // escaped pointer and the live local alias one heap cell.
            if (slot is IWrapperBackedSlot wrapperBacked)
            {
                wrapperBacked.EmitWrapperLoad(_il);
                return;
            }
            if (slot is not null)
            {
                slot.EmitAddress(_il);
                return;
            }
            if (TryResolveNamespacePropertyLoca(name.Name) is { } namespaceLoca)
            {
                _il.Call(namespaceLoca);
                return;
            }
        }
        if (aov.Target is BoundMemberAccessExpression member
            && TryEmitInCompilationPropertyLocation(member))
            return;
        // &expr.field — emit the target expression, then take field address
        EmitAddress(aov.Target);
    }

    bool TryEmitInCompilationPropertyLocation(BoundMemberAccessExpression member)
    {
        var lookup = member.Target.Type switch
        {
            HeapPointerBoundType hp => hp.Inner,
            ByRefBoundType byRef => byRef.Inner,
            _ => member.Target.Type,
        };
        if (lookup is ExternalCSharpType csharp
            && csharp.Handle.Members.FirstOrDefault(candidate =>
                candidate.Kind == CSharpMemberKind.Property
                && candidate.Name == member.MemberName
                && candidate.ReturnsByRef) is { } refProperty)
        {
            var getter = BuildCSharpGetterReference(csharp, member.MemberName, refProperty);
            if (refProperty.IsStatic)
                _il.Call(getter);
            else
            {
                EmitExpression(member.Target);
                _il.CallVirt(getter);
            }
            return true;
        }
        var ownerName = lookup switch
        {
            DataType data => data.Name,
            InterfaceType protocol => protocol.Name,
            _ => null,
        };
        if (ownerName is null)
            return TryEmitReferencedPropertyLocation(member);
        var owner = _types.Module.Types.FirstOrDefault(t => t.Name == ownerName)
            ?? _types.Module.Types.FirstOrDefault(t => t.Name.StartsWith(ownerName + "`", StringComparison.Ordinal));
        var loca = owner?.Methods.FirstOrDefault(m => m.Name == "getloca_" + member.MemberName
            && !m.IsStatic && m.Parameters.Count == 0);
        if (loca is null) return TryEmitReferencedPropertyLocation(member);
        MethodReference locaRef = loca;
        if (_types.Resolve(lookup) is GenericInstanceType git)
            locaRef = HostMethodOnType(loca, git);
        if (member.Target.Type is HeapPointerBoundType)
        {
            EmitHeapPointerCarrier(member.Target);
            ILHeapPointer.EmitDerefToAddress(_il, _types.Module, _types.Resolve(member.Target.Type));
            _il.Call(locaRef);
        }
        else if (owner!.IsValueType)
        {
            EmitAddress(member.Target);
            _il.Call(locaRef);
        }
        else
        {
            EmitExpression(member.Target);
            _il.CallVirt(locaRef);
        }
        return true;
    }

    // A property imported from another E# assembly carries its capability in
    // compiler-owned metadata, but its actual location protocol remains an
    // ordinary public CLR `getloca_x` ref-return.  Emit that method directly;
    // no `*Class` type is formed and no producer backing field is exposed.
    bool TryEmitReferencedPropertyLocation(BoundMemberAccessExpression member)
    {
        var runtimeType = _types.BoundTypeToRuntime(member.Target.Type);
        var property = runtimeType?.GetProperty(member.MemberName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        var refGetter = property?.GetGetMethod();
        var carriesCapability = member.Member is Esharp.Symbols.FieldSymbol
            { IsProperty: true, HasDurablePropertyLocation: true }
            || HasImportedDurablePropertyLocation(property)
            || refGetter?.ReturnType.IsByRef == true;
        if (!carriesCapability) return false;
        var loca = refGetter?.ReturnType.IsByRef == true
            ? refGetter
            : runtimeType?.GetMethod("getloca_" + member.MemberName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (loca is null || !loca.ReturnType.IsByRef) return false;

        var locaRef = _types.Module.ImportReference(loca);
        if (member.Target.Type is HeapPointerBoundType)
        {
            EmitHeapPointerCarrier(member.Target);
            ILHeapPointer.EmitDerefToAddress(_il, _types.Module, _types.Resolve(member.Target.Type));
            _il.Call(locaRef);
        }
        else if (runtimeType!.IsValueType)
        {
            EmitAddress(member.Target);
            _il.Call(locaRef);
        }
        else
        {
            EmitExpression(member.Target);
            _il.CallVirt(locaRef);
        }
        return true;
    }

    static bool HasImportedDurablePropertyLocation(System.Reflection.PropertyInfo? property)
    {
        if (property is null) return false;
        foreach (var attribute in property.CustomAttributes)
        {
            if (attribute.AttributeType.Name != "__EsharpPropertyCapabilityAttribute"
                || attribute.ConstructorArguments.Count != 1)
                continue;
            if (attribute.ConstructorArguments[0].Value is int flags)
                return (flags & 0b000010) != 0;
        }
        return false;
    }

    /// Emit `arg` (a `*T` value in some representation) coerced to the
    /// representation `paramType` demands. Returns false when this isn't a pointer
    /// position, so the caller falls back to normal argument emission.
    ///
    /// `*T` has one semantic but two CLR forms — the `__Ptr_T` heap wrapper and the
    /// managed pointer `ref T`. Escape analysis may pick a different form for the
    /// parameter than the argument carries, so every call site reconciles them:
    ///   wrapper arg → `ref T` param : load wrapper, `ldflda Value` (alias the heap cell)
    ///   `&x`/`*x`    → `ref T` param : address of the lvalue
    ///   wrapper arg → wrapper param : pass the reference as-is
    ///   `&x`/value   → wrapper param : heap-allocate a fresh `__Ptr_T(value)`
    bool TryEmitPointerArg(BoundExpression arg, TypeReference paramType)
    {
        if (arg is BoundOutArgumentExpression) return false;
        var paramByRef = paramType.IsByReference;
        var paramWrapper = ILHeapPointer.IsWrapperType(paramType);
        if (!paramByRef && !paramWrapper) return false;

        if (paramByRef)
            EmitPointerAsManagedRef(arg);
        else
            EmitPointerAsWrapper(arg, paramType);
        return true;
    }

    /// <summary>
    /// Emit a value for storage whose CLR slot may be a durable pointer carrier.
    /// A wrapper-backed parameter reads as T in ordinary source expressions, but
    /// when it flows into another *T slot the storage boundary needs the carrier.
    /// Null still stores as null (not as a wrapper containing a null value).
    /// </summary>
    void EmitStorageValue(TypeReference targetType, BoundExpression value)
    {
        if (ILHeapPointer.IsWrapperType(targetType) && IsWrapperCarrierExpression(value))
            EmitPointerAsWrapper(value, targetType);
        else
            EmitExpression(value);
    }

    bool IsWrapperCarrierExpression(BoundExpression expression) => expression switch
    {
        BoundAddressOfVariableExpression => true,
        BoundUnaryExpression { Op: SyntaxTokenKind.Star } => true,
        { Type: HeapPointerBoundType } => true,
        BoundNameExpression name when TryResolveSlot(name.Name) is IWrapperBackedSlot => true,
        _ => false,
    };

    // A source `*T` name normally loads its pointee through ILSlot. Nil tests
    // are the exceptional read that needs the pointer representation itself.
    // Keep this narrow: it is not a general bypass for ordinary pointer reads,
    // which must continue to use the slot's value semantics.
    bool IsPointerCarrierForNilComparison(BoundExpression expression) => expression switch
    {
        BoundNameExpression name when TryResolveSlot(name.Name) is IWrapperBackedSlot => true,
        { Type: HeapPointerBoundType } => true,
        BoundAddressOfVariableExpression => true,
        BoundHeapAllocExpression => true,
        _ => false,
    };

    void EmitPointerCarrierForNilComparison(BoundExpression expression)
    {
        if (expression is BoundNameExpression name
            && TryResolveSlot(name.Name) is IWrapperBackedSlot wrapperBacked)
        {
            wrapperBacked.EmitWrapperLoad(_il);
            return;
        }

        if (expression is BoundAddressOfVariableExpression or BoundHeapAllocExpression)
        {
            var wrapperType = _types.Resolve(expression.Type);
            EmitPointerAsWrapper(expression, wrapperType);
            return;
        }

        EmitExpression(expression);
    }

    // Heap-pointer operations consume the CLR `__Ptr_T` reference. A
    // wrapper-backed parameter normally reads as T, so auto-deref and property
    // location paths must request the carrier explicitly before they form
    // `Value`'s managed address.
    void EmitHeapPointerCarrier(BoundExpression expression)
    {
        if (expression is BoundNameExpression name
            && TryResolveSlot(name.Name) is IWrapperBackedSlot wrapperBacked)
        {
            wrapperBacked.EmitWrapperLoad(_il);
            return;
        }
        EmitExpression(expression);
    }

    /// Leave a managed pointer (`ref T`) to the argument's pointee on the stack.
    void EmitPointerAsManagedRef(BoundExpression arg)
    {
        switch (arg)
        {
            case BoundNameExpression name when TryResolveSlot(name.Name) is IWrapperBackedSlot wrapperBacked:
                wrapperBacked.EmitWrapperLoad(_il);
                ILHeapPointer.EmitDerefToAddress(_il, _types.Module, wrapperBacked.WrapperType);
                return;
            case { Type: HeapPointerBoundType }:                 // wrapper value → ldflda Value
                EmitExpression(arg);
                ILHeapPointer.EmitDerefToAddress(_il, _types.Module, _types.Resolve(arg.Type));
                return;
            case BoundUnaryExpression { Op: SyntaxTokenKind.Star } star:
                EmitAddress(star.Operand);
                return;
            case BoundAddressOfVariableExpression aov:
                EmitAddressOfVariable(aov);                       // ordinary lvalue or property loca protocol
                return;
            case BoundNameExpression name when TryResolveSlot(name.Name) is ByRefParameterSlot or ByRefLocalSlot or ReadOnlyByRefParameterSlot:
                TryResolveSlot(name.Name)!.EmitAddress(_il);      // already a managed pointer — re-expose it
                return;
            default:
                EmitAddress(arg);                                 // address of an ordinary lvalue
                return;
        }
    }

    /// Leave a `__Ptr_T` wrapper reference on the stack.
    void EmitPointerAsWrapper(BoundExpression arg, TypeReference wrapperType)
    {
        // A durable pointer parameter must forward its carrier, not its Value.
        // This is the parameter analogue of the wrapper-backed local path below.
        if (arg is BoundNameExpression parameterName
            && TryResolveSlot(parameterName.Name) is IWrapperBackedSlot parameterWrapper)
        {
            parameterWrapper.EmitWrapperLoad(_il);
            return;
        }

        // `&local` of a promoted local is already the one shared wrapper cell.
        // Passing it through rather than copying Value preserves Go-style aliasing.
        if (arg is BoundAddressOfVariableExpression { Target: BoundNameExpression name }
            && TryResolveSlot(name.Name) is IWrapperBackedSlot wrapperBacked)
        {
            wrapperBacked.EmitWrapperLoad(_il);
            return;
        }

        // `*local` is E#'s ref-pass spelling. Escape analysis may have raised
        // that local to a durable carrier because this parameter (or a function
        // pointer's ABI) stores/returns the pointer. Passing its Value through a
        // fresh wrapper would preserve neither identity nor writes; forward the
        // existing carrier exactly as for an explicit `&local` alias.
        if (arg is BoundUnaryExpression
            {
                Op: SyntaxTokenKind.Star,
                Operand: BoundNameExpression refPassName,
            }
            && TryResolveSlot(refPassName.Name) is IWrapperBackedSlot refPassWrapper)
        {
            refPassWrapper.EmitWrapperLoad(_il);
            return;
        }

        // Already a wrapper-shaped value (`&T{}`, a wrapper-typed name, etc.).
        if (arg.Type is HeapPointerBoundType || arg is BoundHeapAllocExpression)
        {
            EmitExpression(arg);
            return;
        }

        // A managed-ref / value source flowing into a wrapper parameter: heap a
        // fresh wrapper holding the pointee value. `&x`/`*x` contribute x's value.
        BoundExpression valueExpr = arg switch
        {
            BoundUnaryExpression { Op: SyntaxTokenKind.Star } star => star.Operand,
            BoundAddressOfVariableExpression aov => aov.Target,
            _ => arg,
        };
        EmitExpression(valueExpr);
        var ctor = ILHeapPointer.GetWrapperCtor(_types.Module, wrapperType);
        _il.NewObj(ctor);
    }

    void EmitHeapAlloc(BoundHeapAllocExpression ha)
    {
        // Evaluate the inner expression (produces T on the stack),
        // then wrap in __Ptr_T via newobj.
        EmitExpression(ha.Inner);
        var innerType = _types.Resolve(ha.PointeeType);
        ILHeapPointer.EmitHeapAlloc(_il, _types.Module, innerType);
    }

    void EmitHeapPointerMemberAccess(BoundMemberAccessExpression ma, HeapPointerBoundType hp)
    {
        // A heap pointer to a value data can still target a property.  Its
        // location is the wrapper's Value field, but the member access must call
        // `get_x`; reading the generated backing field would break encapsulation.
        if (hp.Inner is DataType dt)
        {
            var owner = _types.Module.Types.FirstOrDefault(t => t.Name == dt.Name)
                ?? _types.Module.Types.FirstOrDefault(t => t.Name.StartsWith(dt.Name + "`", StringComparison.Ordinal));
            var getter = owner?.Methods.FirstOrDefault(m => m.Name == "get_" + ma.MemberName
                && !m.IsStatic && m.Parameters.Count == 0);
            if (getter is not null)
            {
                EmitHeapPointerCarrier(ma.Target);
                ILHeapPointer.EmitDerefToAddress(_il, _types.Module, _types.Resolve(ma.Target.Type));
                MethodReference getterRef = getter;
                if (_types.Resolve(hp.Inner) is GenericInstanceType git)
                    getterRef = HostMethodOnType(getter, git);
                _il.Call(getterRef);
                _lastCallWasVoid = false;
                return;
            }
        }

        // Auto-deref: emit target (wrapper ref) → ldflda Value → ldfld <field>
        EmitHeapPointerCarrier(ma.Target);
        var wrapperType = _types.Resolve(ma.Target.Type);
        var innerType = _types.Resolve(hp.Inner);

        // Get the Value field on the wrapper
        ILHeapPointer.EmitDerefToAddress(_il, _types.Module, wrapperType);

        // Now we have a managed pointer to the inner struct — resolve the target field
        var field = FindFieldOnType(innerType, ma.MemberName);
        if (field is not null)
        {
            _il.LoadField(field);
        }
    }

    void EmitHeapPointerFieldStore(BoundMemberAccessExpression ma, HeapPointerBoundType hp, Action emitValue)
    {
        // Auto-deref store: emit target (wrapper ref) → ldflda Value → stfld <field>
        EmitHeapPointerCarrier(ma.Target);
        var wrapperType = _types.Resolve(ma.Target.Type);
        ILHeapPointer.EmitDerefToAddress(_il, _types.Module, wrapperType);
        emitValue();
        var innerType = _types.Resolve(hp.Inner);
        var field = FindFieldOnType(innerType, ma.MemberName);
        if (field is not null)
            _il.StoreField(field);
    }

    void EmitDotCase(BoundDotCaseExpression dc)
    {
        // Enum: load the integer constant directly
        if (dc.Type is EnumType)
        {
            foreach (var type in _types.Module.Types)
            {
                if (type.Name == dc.ResolvedTypeName && type.IsEnum)
                {
                    var field = type.Fields.FirstOrDefault(f => f.IsStatic && f.IsLiteral && f.Name == dc.CaseName);
                    if (field?.Constant is int val)
                    {
                        _il.LoadInt(val);
                        return;
                    }
                }
            }
        }

        // Check if this is a ref choice (subclass pattern) or value choice (factory pattern)
        var subclassName = $"{dc.ResolvedTypeName}_{dc.CaseName}";
        var subclassType = _types.TryResolveRegistered(subclassName);
        if (subclassType is not null)
        {
            // ref choice: newobj SubclassName(args)
            foreach (var arg in dc.Arguments)
                EmitExpression(arg);
            var ctor = subclassType.Methods.FirstOrDefault(m => m.IsConstructor && m.Parameters.Count == dc.Arguments.Count);
            if (ctor is null) return;
            // A generic `ref union` is reified: close the subclass over the bound type args
            // (`Box_full`1<int>`) and rebind the ctor so the payload field's type is the
            // concrete `int`, not the erased open parameter.
            MethodReference ctorRef = ctor;
            if (subclassType.HasGenericParameters
                && dc.Type is Esharp.BoundTree.ChoiceType { TypeArgs.Count: > 0 } refClosed)
            {
                var closedSub = new GenericInstanceType(subclassType);
                foreach (var arg in refClosed.TypeArgs) closedSub.GenericArguments.Add(_types.Resolve(arg));
                ctorRef = RebindToDeclaring(ctor, closedSub);
            }
            _il.NewObj(ctorRef);
            return;
        }

        // Generic value-choice factory: `Option<int>.some(99)` / `.some(99)` where
        // the bound type is a closed `ChoiceType` carrying type arguments. Host the
        // open factory on the reified `Option`1<int>` so the call's parameter and
        // return types substitute `!0` → the concrete argument instead of staying
        // open (which fails verification at the call and the receiving store).
        if (dc.Type is Esharp.BoundTree.ChoiceType { IsRef: false } closedChoice
            && closedChoice.TypeArgs.Count > 0
            && _types.Resolve(closedChoice) is GenericInstanceType choiceGit)
        {
            var def = choiceGit.Resolve();
            var genFactory = def?.Methods.FirstOrDefault(m =>
                m.IsStatic && m.Name == dc.CaseName && m.Parameters.Count == dc.Arguments.Count);
            if (genFactory is not null)
            {
                var factoryRef = HostExternalMethodOnType(genFactory, choiceGit);
                EmitCallArgsCoerced(dc.Arguments, factoryRef);
                _il.Call(factoryRef);
                return;
            }
        }

        // Value choice: call static factory method
        foreach (var arg in dc.Arguments)
            EmitExpression(arg);

        foreach (var type in _types.Module.Types)
        {
            if (type.Name == dc.ResolvedTypeName)
            {
                var factory = type.Methods.FirstOrDefault(m => m.Name == dc.CaseName && m.IsStatic);
                if (factory is not null)
                {
                    _il.Call(factory);
                    return;
                }
            }
        }
    }

    // === Helpers ===

    /// <summary>Push the address of a value (for struct field access)</summary>
    void EmitAddress(BoundExpression expr)
    {
        if (expr is BoundNameExpression name)
        {
            if (IsSelfReference(name.Name))
            {
                _il.LoadArg(_method.Body.ThisParameter); // this is already an address for value types
                return;
            }
            if (TryResolveSlot(name.Name) is { } slot)
                slot.EmitAddress(_il);
            else if (TryResolveNamespacePropertyLoca(name.Name) is { } namespaceLoca)
                _il.Call(namespaceLoca);
        }
        else if (expr is BoundMemberAccessExpression ma)
        {
            // Result<T,E> member (.Value / .Error / .IsOk / .IsError): emit through
            // the closed-Result-aware member path (EmitMemberAccess), then spill to
            // a temp typed by the *bound* member type. Going through
            // BoundTypeToRuntime here would erase user type args to `object` and
            // type the temp as the open parameter `!0`.
            if (ma.Target.Type is Esharp.BoundTree.ResultType
                && ma.MemberName is "IsOk" or "IsError" or "Value" or "Error")
            {
                EmitMemberAccess(ma);
                var memberType = _types.Resolve(ma.Type);
                var spill = new VariableDefinition(memberType);
                _method.Body.Variables.Add(spill);
                _il.StoreLocal(spill);
                _il.LoadLocalAddress(spill);
                return;
            }

            // An ordinary stored property has an implicit durable `loca`.
            // Prefer that protocol when the property is a value-type receiver
            // for a nested write (`owner.position.x = …`); spilling its getter
            // result would mutate only a copy.
            if (TryEmitInCompilationPropertyLocation(ma))
                return;

            // An E# property used as the value-type receiver of a following
            // member access must still observe its getter.  In particular a
            // scoped `mut` property has no durable location; finding the private
            // auto-backing field here would bypass setup/resume entirely.
            if (TryEmitInCompilationPropertyGet(ma))
            {
                var propertyTemp = new VariableDefinition(_types.Resolve(ma.Type));
                _method.Body.Variables.Add(propertyTemp);
                _il.StoreLocal(propertyTemp);
                _il.LoadLocalAddress(propertyTemp);
                return;
            }

            // Auto-deref through *T heap pointer — get address of inner field (skip for self)
            if (IsHeapPointerDeref(ma, out var hp))
            {
                EmitHeapPointerCarrier(ma.Target);
                var wrapperType = _types.Resolve(ma.Target.Type);
                ILHeapPointer.EmitDerefToAddress(_il, _types.Module, wrapperType);
                var innerType = _types.Resolve(hp.Inner);
                var hpField = FindFieldOnType(innerType, ma.MemberName);
                if (hpField is not null)
                    _il.LoadFieldAddress(hpField);
                return;
            }

            var runtimeType = _types.BoundTypeToRuntime(ma.Target.Type);
            var property = runtimeType?.GetProperty(ma.MemberName);
            var getter = property?.GetGetMethod();
            if (getter is not null)
            {
                var getterRef = _types.Module.ImportReference(getter);
                var useCall = runtimeType!.IsValueType || _types.IsValueType(ma.Target.Type);
                if (useCall)
                    EmitAddress(ma.Target);
                else
                    EmitExpression(ma.Target);
                { if (useCall) _il.Call(getterRef); else _il.CallVirt(getterRef); }


                var temp = new VariableDefinition(getterRef.ReturnType);
                _method.Body.Variables.Add(temp);
                _il.StoreLocal(temp);
                _il.LoadLocalAddress(temp);
                return;
            }

            if (TryResolveField(ma, out var field))
            {
                if (_types.IsValueType(ma.Target.Type))
                    EmitAddress(ma.Target);
                else
                    EmitExpression(ma.Target);
                _il.LoadFieldAddress(field);
                return;
            }

            // A property/computed member is not itself addressable.  It can still
            // be the value-type receiver of a following member access, e.g.
            // `self.Token.IsCancellationRequested`: emit the property normally,
            // spill that rvalue once, and take the temporary's address.  Previously
            // this path emitted nothing when the property's declaring type was an
            // E#-compiled class (there is no reflection Type during codegen), leaving
            // the subsequent value-type call without its required receiver.
            EmitExpression(ma);
            var memberTemp = new VariableDefinition(_types.Resolve(ma.Type));
            _method.Body.Variables.Add(memberTemp);
            _il.StoreLocal(memberTemp);
            _il.LoadLocalAddress(memberTemp);
            return;
        }
        else
        {
            // For complex expressions, evaluate to a temp and take its address
            EmitExpression(expr);
            var resolvedType = _types.Resolve(expr.Type);
            var temp = new VariableDefinition(resolvedType);
            _method.Body.Variables.Add(temp);
            _il.StoreLocal(temp);
            _il.LoadLocalAddress(temp);
        }
    }
}
