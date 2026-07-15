using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Esharp.Emit;

/// Generates sealed wrapper classes for *T heap pointer fields and provides
/// IL emission helpers for heap allocation (&T{...}), auto-deref member access,
/// and nil semantics.
///
/// For each data type T used as *T, we generate:
///
///   [CompilerGenerated]
///   internal sealed class __Ptr_{T} {
///       public T Value;
///       public __Ptr_{T}(T value) { Value = value; }
///   }
///
/// The wrapper is a reference type — nullable, 8 bytes in the parent layout,
/// GC-tracked. Auto-deref on member access goes through ldfld wrapper → ldflda Value.
public static class ILHeapPointer
{
    // Cache of generated wrapper types per *ModuleDefinition* and inner type name.
    // Assembly names are not identities: the test harness intentionally reuses names
    // across independent in-memory compilations, so a string key could return a TypeDef
    // owned by an earlier module and leak `__Ptr_T` into unrelated emitted IL.
    [ThreadStatic] static Dictionary<ModuleDefinition, Dictionary<string, TypeDefinition>>? _wrappers;

    /// Get or create the wrapper class for the given inner struct type.
    /// The wrapper is added to the module's type collection on first creation.
    public static TypeDefinition GetOrCreateWrapper(ModuleDefinition module, TypeReference innerType)
    {
        _wrappers ??= new(ReferenceEqualityComparer.Instance);
        if (!_wrappers.TryGetValue(module, out var moduleWrappers))
            _wrappers[module] = moduleWrappers = new(StringComparer.Ordinal);
        var key = innerType.FullName;
        if (moduleWrappers.TryGetValue(key, out var existing))
            return existing;

        var wrapperName = $"__Ptr_{innerType.Name}";
        var wrapperNs = innerType.Namespace;

        // Cecil can represent the same generic inner type through both an open
        // definition and a closed instance (`Chan`1` vs `Chan`1<T>`). Their
        // FullName cache keys differ, but this emitter's wrapper metadata name is
        // deliberately the same. Reuse the already-declared wrapper by metadata
        // identity before creating another TypeDef: duplicate `__Ptr_Chan`1`
        // definitions make every consumer assembly fail ILVerify while resolving
        // the stdlib reference table.
        var declared = module.Types.FirstOrDefault(t =>
            t.Namespace == wrapperNs && t.Name == wrapperName);
        if (declared is not null)
        {
            moduleWrappers[key] = declared;
            return declared;
        }

        var wrapper = new TypeDefinition(
            wrapperNs,
            wrapperName,
            TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit,
            module.ImportReference(typeof(object)));

        // Add [CompilerGenerated] attribute
        var compGenCtor = module.ImportReference(
            typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)
                .GetConstructor(Type.EmptyTypes)!);
        wrapper.CustomAttributes.Add(new CustomAttribute(compGenCtor));

        // public T Value field
        var valueField = new FieldDefinition("Value", FieldAttributes.Public, innerType);
        wrapper.Fields.Add(valueField);

        // .ctor(T value) { base(); this.Value = value; }
        var ctor = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.ImportReference(typeof(void)));
        ctor.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, innerType));
        ctor.Body.InitLocals = true;

        var il = ctor.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, valueField);
        il.Emit(OpCodes.Ret);

        ILOptimizer.ShortenOpcodes(ctor.Body);
        wrapper.Methods.Add(ctor);

        module.Types.Add(wrapper);
        moduleWrappers[key] = wrapper;
        return wrapper;
    }

    /// True if `t` is a generated `__Ptr_T` heap-pointer wrapper class.
    public static bool IsWrapperType(TypeReference t) =>
        t is not ByReferenceType && t.Name.StartsWith("__Ptr_", StringComparison.Ordinal);

    /// Resolve the wrapper TypeReference for a given inner type.
    /// Returns the wrapper class type (reference type, nullable).
    public static TypeReference ResolveWrapperType(ModuleDefinition module, TypeReference innerType)
    {
        var wrapper = GetOrCreateWrapper(module, innerType);
        return module.ImportReference(wrapper);
    }

    /// Get the Value field on a wrapper type.
    public static FieldReference GetValueField(ModuleDefinition module, TypeReference wrapperType)
    {
        var resolved = wrapperType.Resolve();
        var valueField = resolved.Fields.First(f => f.Name == "Value");
        return module.ImportReference(valueField);
    }

    /// Get the .ctor(T) on a wrapper type.
    public static MethodReference GetWrapperCtor(ModuleDefinition module, TypeReference wrapperType)
    {
        var resolved = wrapperType.Resolve();
        var ctor = resolved.Methods.First(m => m.IsConstructor && m.Parameters.Count == 1);
        return module.ImportReference(ctor);
    }

    /// Emit: evaluate inner value (already on stack) → newobj __Ptr_T::.ctor(T)
    /// Result: wrapper reference on the stack.
    public static void EmitHeapAlloc(ILBuilder il, ModuleDefinition module, TypeReference innerType)
    {
        var wrapperType = ResolveWrapperType(module, innerType);
        var ctor = GetWrapperCtor(module, wrapperType);
        il.NewObj(ctor);
    }

    /// Emit: load wrapper reference → ldflda Value (managed pointer to inner struct).
    /// Use this when you need to read/write fields on the inner struct through the pointer.
    /// Assumes the wrapper reference is already on the stack.
    public static void EmitDerefToAddress(ILBuilder il, ModuleDefinition module, TypeReference wrapperType)
    {
        var valueField = GetValueField(module, wrapperType);
        il.LoadFieldAddress(valueField);
    }

    /// Emit: load wrapper reference → ldfld Value (copy of inner struct).
    /// Use when you need the full struct value (e.g., passing by value).
    /// Assumes the wrapper reference is already on the stack.
    public static void EmitDerefToValue(ILBuilder il, ModuleDefinition module, TypeReference wrapperType)
    {
        var valueField = GetValueField(module, wrapperType);
        il.LoadField(valueField);
    }

    /// Wire interface satisfaction on a wrapper class. For each protocol that *T satisfies:
    /// 1. Add InterfaceImplementation to the wrapper
    /// 2. Generate delegate methods that forward to the corresponding static methods
    ///
    /// Called as a post-pass after all static methods are emitted, so forward references resolve.
    public static void WireProtocol(
        ModuleDefinition module,
        IMetadataResolver types,
        TypeReference innerType,
        Esharp.BoundTree.BoundType protocol,
        TypeDefinition moduleClass)
    {
        var wrapper = GetOrCreateWrapper(module, innerType);
        // The registry is keyed by the bare protocol name; structured types carry
        // it directly, EmitName covers the rest.
        var protocolName = protocol switch
        {
            Esharp.BoundTree.InterfaceType it => it.Name,
            Esharp.BoundTree.ExternalType ex => ex.Name,
            _ => protocol.EmitName,
        };
        var ifaceDef = types.TryResolveRegistered(protocolName);
        if (ifaceDef is null) return;

        // Add interface implementation
        wrapper.Interfaces.Add(new InterfaceImplementation(ifaceDef));

        var valueField = wrapper.Fields.First(f => f.Name == "Value");

        // For each method in the protocol, generate a delegate on the wrapper
        foreach (var ifaceMethod in ifaceDef.Methods)
        {
            // Try 1: pointer receiver — static method on module class whose first
            // param is THIS wrapper type (e.g. bump(*T) becomes `static bump(__Ptr_T)`).
            // Without the first-param check, we'd happily match any `static bump(*X)`
            // and emit a call passing __Ptr_Outer where the callee expects __Ptr_Inner.
            // The receiver parameter is either the `__Ptr_T` wrapper or, if escape
            // analysis downgraded it, a managed pointer `Inner&` — accept both.
            var staticMethod = moduleClass.Methods.FirstOrDefault(m =>
                m.IsStatic && m.Name == ifaceMethod.Name
                && m.Parameters.Count == ifaceMethod.Parameters.Count + 1
                && m.Parameters.Count > 0
                && (m.Parameters[0].ParameterType.FullName == wrapper.FullName
                    || (m.Parameters[0].ParameterType is ByReferenceType br
                        && br.ElementType.FullName == innerType.FullName)));

            // Try 2: value receiver — instance method on the inner struct
            var innerTypeDef = innerType.Resolve();
            var instanceMethod = staticMethod is null && innerTypeDef is not null
                ? innerTypeDef.Methods.FirstOrDefault(m =>
                    !m.IsStatic && m.Name == ifaceMethod.Name
                    && m.Parameters.Count == ifaceMethod.Parameters.Count)
                : null;

            if (staticMethod is null && instanceMethod is null) continue;

            // Generate: virtual newslot method on wrapper that delegates
            var delegateMethod = new MethodDefinition(
                ifaceMethod.Name,
                MethodAttributes.Public | MethodAttributes.HideBySig
                    | MethodAttributes.Virtual | MethodAttributes.NewSlot,
                ifaceMethod.ReturnType);

            foreach (var p in ifaceMethod.Parameters)
                delegateMethod.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, p.ParameterType));

            delegateMethod.Body.InitLocals = true;
            var il = delegateMethod.Body.GetILProcessor();

            if (staticMethod is not null)
            {
                // Pointer receiver: pass the wrapper (`this`) as the receiver. Escape
                // analysis may have downgraded that parameter to a managed pointer
                // (`ref Inner`) — coerce by dereferencing the wrapper to its Value
                // address; otherwise pass the wrapper reference as-is.
                il.Emit(OpCodes.Ldarg_0);
                if (staticMethod.Parameters.Count > 0 && staticMethod.Parameters[0].ParameterType.IsByReference)
                    il.Emit(OpCodes.Ldflda, valueField);
                for (var i = 0; i < ifaceMethod.Parameters.Count; i++)
                    il.Emit(OpCodes.Ldarg, i + 1);
                il.Emit(OpCodes.Call, staticMethod);
            }
            else
            {
                // Value receiver: ldarg.0 → ldflda Value → call instance method on inner struct
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldflda, valueField);
                for (var i = 0; i < ifaceMethod.Parameters.Count; i++)
                    il.Emit(OpCodes.Ldarg, i + 1);
                il.Emit(OpCodes.Call, instanceMethod!);
            }
            il.Emit(OpCodes.Ret);

            ILOptimizer.ShortenOpcodes(delegateMethod.Body);
            wrapper.Methods.Add(delegateMethod);

            // Explicit interface override
            delegateMethod.Overrides.Add(ifaceMethod);
        }
    }

    /// Reset the wrapper cache. Call between compilation units in tests.
    public static void ResetCache() => _wrappers = null;
}
