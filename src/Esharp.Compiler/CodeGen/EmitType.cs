using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Emit;
using Esharp.BoundTree;
using Esharp.Diagnostics;

namespace Esharp.CodeGen;

public static partial class CodeGenerator
{
    /// The CLR metadata name for a (possibly generic) type. A generic type's name
    /// carries its arity (`Box`1`, `Pair`2`, `IBox`1`) per CLR convention — the
    /// resolver still keys off the bare E# name, so this is only the emitted name.
    internal static string MetadataTypeName(string name, int arity)
        => arity > 0 ? name + "`" + arity : name;

    /// Apply per-type-parameter CLR constraints (P3). Today only `"unmanaged"` is
    /// modeled — the exact shape Roslyn emits for `where T : unmanaged`: the
    /// NotNullableValueType flag plus a `System.ValueType modreq(UnmanagedType)`
    /// constraint, so `MemoryMarshal.AsBytes<T>` / span reinterpretation type-check.
    internal static void ApplyGenericConstraints(
        Mono.Collections.Generic.Collection<GenericParameter> gps,
        IReadOnlyList<string?>? constraints, ModuleDefinition module)
    {
        if (constraints is null) return;
        for (var i = 0; i < gps.Count && i < constraints.Count; i++)
        {
            if (constraints[i] != "unmanaged") continue;
            var gp = gps[i];
            gp.Attributes |= GenericParameterAttributes.NotNullableValueTypeConstraint;
            var valueType = module.ImportReference(typeof(System.ValueType));
            var unmanagedMod = module.ImportReference(typeof(System.Runtime.InteropServices.UnmanagedType));
            gp.Constraints.Add(new GenericParameterConstraint(new RequiredModifierType(unmanagedMod, valueType)));
        }
    }

    /// A data declaration's conformances as structured, symbol-linked types — the
    /// emitter's single conformance currency. Identity is carried by the BoundType
    /// (and its TypeSymbol); a display name is derived on demand via EmitName.
    internal static IReadOnlyList<Esharp.BoundTree.BoundType> ConformanceEntries(BoundDataDeclaration data)
        => data.InterfaceTypes ?? [];

    /// True unless both return types resolve to *different* concrete definitions.
    /// Generic parameters and unresolvable refs are treated as compatible (we can't
    /// tell, so don't reject) — this only fires to separate, e.g., `IEnumerator<T>`
    /// from the non-generic `IEnumerator` when one method name fills two slots.
    internal static bool ReturnTypesCompatible(TypeReference a, TypeReference b)
    {
        if (a is GenericParameter || b is GenericParameter) return true;
        TypeDefinition? ra = null, rb = null;
        try { ra = a.Resolve(); } catch { }
        try { rb = b.Resolve(); } catch { }
        if (ra is null || rb is null) return true;
        return ra.FullName == rb.FullName;
    }

    /// Phase 1 (shell): create the TypeDefinition with its name, namespace, CLR
    /// attributes, and generic parameters, then register it. NO field/base/interface
    /// resolution happens here — those can reference *other* user types, so every
    /// type's shell must exist first (forward references). Base type is a placeholder
    /// (ValueType / object); the real base class is wired in PopulateDataMembers once
    /// all shells are registered.
    static TypeDefinition DeclareDataShell(
        ModuleDefinition module, ILTypeResolver types, BoundDataDeclaration data, string ns)
    {
        var isStruct = data.Classification == DataClassification.Struct;
        // Class modifier maps onto CLR flags:
        //   Sealed   → sealed (default; user code can't inherit)
        //   Abstract → CLR abstract (can't instantiate; subclasses must fulfill)
        //   Open     → neither sealed nor abstract (instantiable + inheritable)
        var attrs = (data.IsPublic ? TypeAttributes.Public : TypeAttributes.NotPublic)
            | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit;
        if (isStruct)
        {
            attrs |= TypeAttributes.Sealed | TypeAttributes.SequentialLayout;
        }
        else
        {
            switch (data.Modifier)
            {
                case Esharp.Syntax.ClassModifier.Sealed:   attrs |= TypeAttributes.Sealed; break;
                case Esharp.Syntax.ClassModifier.Abstract: attrs |= TypeAttributes.Abstract; break;
                case Esharp.Syntax.ClassModifier.Open:     /* neither */                    break;
            }
        }

        var baseType = isStruct
            ? module.ImportReference(typeof(ValueType))
            : module.ImportReference(typeof(object));

        // CLR convention: a generic type's metadata name carries its arity (`Box`1`,
        // `Pair`2`) — reflection-by-name, cross-language consumers, and the stdlib's
        // by-name binding contract all assume it. The resolver keys off the bare E#
        // name (below), so the suffix is purely the emitted CLR name.
        var typeDef = new TypeDefinition(ns, MetadataTypeName(data.Name, data.TypeParameters.Count), attrs, baseType);

        // Generic type parameters belong on the shell so field/base/method resolution
        // in the populate phase can bind `A`/`B` to the right slot.
        foreach (var tp in data.TypeParameters)
            typeDef.GenericParameters.Add(new GenericParameter(tp, typeDef));
        ApplyGenericConstraints(typeDef.GenericParameters, data.GenericConstraints, module);

        module.Types.Add(typeDef);
        types.Register(data.Name, typeDef, ns, data.TypeParameters.Count);
        types.RegisterClassification(data.Name, data.Classification);
        return typeDef;
    }

    /// Phase 2 (members): with every type shell already registered, resolve the base
    /// class, interfaces, fields, constructors, init body, and derive directives into
    /// the shell created by DeclareDataShell.
    static (Dictionary<string, FieldDefinition> Fields, Action? CtorBodies) PopulateDataMembers(
        ModuleDefinition module, ILTypeResolver types, DiagnosticBag diagnostics,
        BoundDataDeclaration data, TypeDefinition typeDef, string ns)
    {
        // Constructor BODIES (field defaults, `: base(args)` argument expressions, the
        // init body itself) are deferred: a body may construct any declared type —
        // forward references included — so it can only emit once every type's .ctor
        // exists. Signatures land here; the caller runs `CtorBodies` after the full
        // populate sweep, the same signatures-then-bodies split instance methods get
        // via Pass 1c / 1c'.
        Action? ctorBodies = null;
        var isStruct = data.Classification == DataClassification.Struct;

        // Generic context for resolving `A`/`B` in field/base/init/method types.
        var pushedGenericContext = false;
        if (typeDef.HasGenericParameters)
        {
            types.PushGenericContext(typeDef.GenericParameters);
            pushedGenericContext = true;
        }

        // Wire the user-declared base class (may be a forward reference — every shell
        // is registered by now). Structs never inherit; the placeholder stands. An
        // in-compilation base resolves through the registry; an external base (a BCL /
        // referenced-assembly class like `Attribute`) imports its runtime type.
        if (!isStruct && data.BaseClass is { } baseName)
        {
            if (types.TryResolveRegistered(baseName) is { } baseRef)
                typeDef.BaseType = baseRef;
            else if (types.TryResolveRuntimeType(baseName) is { } extBaseRt)
                typeDef.BaseType = module.ImportReference(extBaseRt);
            // A same-project C# base (mixed-language `class Derived : CsBase`): not an
            // E#-emitted type and not a runtime type (the C# half isn't emitted yet), so it
            // resolves through the cross-language adapter to a forward typeref scoped to the
            // C# half — ILRepack fuses it to the merged assembly.
            else if (types.TryResolveByName(baseName) is { } csBaseRef)
                typeDef.BaseType = csBaseRef;
        }

        // Add interface implementations for protocol conformance. ResolveConformanceInterface
        // closes generic interfaces (`IBox<T>` → `IBox<!0>`) over the type's own parameters
        // and honors cross-language interfaces (a .cs-defined interface on a .es data).
        //
        // `derive equality` adds a nominal `IEquatable<self>` marker to the bound interface
        // list, but the typed `Equals` it emits is non-virtual, so we do NOT emit a CLR
        // InterfaceImplementation for it (matches prior behavior; revisited with the derive
        // engine). Skip exactly that entry.
        var deriveEquatable = data.DeriveTraits.Contains("equality")
            ? (data.TypeParameters.Count > 0
                ? $"IEquatable<{data.Name}<{string.Join(", ", data.TypeParameters)}>>"
                : $"IEquatable<{data.Name}>")
            : null;
        foreach (var ifaceType in ConformanceEntries(data))
        {
            // `derive equality` lists a nominal IEquatable<self> but emits a
            // non-virtual typed Equals — skip its CLR InterfaceImplementation,
            // matched on the display name (the structured type's own spelling).
            if (deriveEquatable is not null && ifaceType.EmitName == deriveEquatable) continue;
            var ifaceRef = types.ResolveConformance(ifaceType);
            if (ifaceRef is not null)
                typeDef.Interfaces.Add(new InterfaceImplementation(ifaceRef));
        }

        var fieldMap = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);

        foreach (var field in data.Fields)
        {
            // A computed property (`let x: T => expr`) has no storage — its getter is
            // emitted as the `get_<name>` method and a PropertyDefinition is attached
            // after the method pass (see EmitPropertyDefinitions). Emit no backing field.
            if (field.IsComputedProperty)
                continue;
            // A stored property (`var x {}`, `let x {}`, `required let x {}`) is backed by
            // a private `<x>k__BackingField`; the get_/set_/init_ accessors + PropertyDefinition
            // are synthesized over it by EmitPropertyDefinitions. Reads route through get_x;
            // composite/with/init reach the backing field via the FindFieldOnType name-bridge.
            if (field.IsProperty)
            {
                // `loca => &self.storage` explicitly chooses real storage for the
                // property.  Reuse that field for ordinary get/set too: location
                // and value access must observe one identity, never a shadow auto
                // backing field.
                if (field.PropHasExplicitLoca
                    && field.PropLocaStorageName is { } storageName
                    && fieldMap.TryGetValue(storageName, out var explicitStorage))
                {
                    fieldMap[field.Name] = explicitStorage;
                    continue;
                }
                var backingType = types.Resolve(field.Type);
                var backingAttrs = FieldAttributes.Private;
                // InitOnly only on a class get-only/get-init backing (set in the ctor). A value
                // `data` backing is set by composite-literal `stfld` outside any ctor, so it
                // must stay mutable or that store fails verification.
                if (!isStruct && !field.PropHasSet) backingAttrs |= FieldAttributes.InitOnly;
                var backing = new FieldDefinition(PropertyBackingFieldName(field.Name), backingAttrs, backingType);
                typeDef.Fields.Add(backing);
                fieldMap[field.Name] = backing;   // bridge the property name to its storage
                continue;
            }
            if (field.IsEvent)
            {
                // A field-style event: backing field + add/remove accessors + EventDefinition.
                // The backing field goes into fieldMap so `raise` reads it directly.
                fieldMap[field.Name] = EmitEventMember(module, types, typeDef, field);
                continue;
            }
            var fieldType = types.Resolve(field.Type);
            // Synthesized capture fields are private — they exist only for the
            // class's own method bodies; everything else non-public stays assembly.
            // A synthesized capture field is always private — an impl detail of the class's
            // own methods — regardless of any declared visibility (a header param can't share
            // a name with an explicit field, ES2188). Otherwise the field's own `Vis`.
            var fieldAttrs = data.CapturedHeaderParams?.Contains(field.Name) == true
                ? FieldAttributes.Private
                : field.Vis switch
                {
                    Esharp.Syntax.Visibility.Public => FieldAttributes.Public,
                    Esharp.Syntax.Visibility.Private => FieldAttributes.Private,
                    _ => FieldAttributes.Assembly,
                };
            if (!field.Mutable) fieldAttrs |= FieldAttributes.InitOnly;
            var fieldDef = new FieldDefinition(field.Name, fieldAttrs, fieldType);
            // `required` interop surface: C# consumers must set the member in their
            // object initializer. The attribute rides the field and (below) the type.
            if (field.IsRequired)
                fieldDef.CustomAttributes.Add(new Mono.Cecil.CustomAttribute(module.ImportReference(
                    typeof(System.Runtime.CompilerServices.RequiredMemberAttribute).GetConstructor(Type.EmptyTypes)!)));
            typeDef.Fields.Add(fieldDef);
            fieldMap[field.Name] = fieldDef;
        }

        // Auto-property accessors must exist before ordinary method bodies are
        // emitted.  A consumer in another type is required to call `set_x` / `get_x`;
        // letting it discover the private backing field is both invalid CLR and a
        // violation of the property contract.  Custom accessors still take the
        // existing late path because their lowered function body supplies the method.
        DeclareStoredAutoPropertyAccessors(module, types, diagnostics, typeDef, data, fieldMap, ref ctorBodies);
        if (data.Fields.Any(f => f.IsRequired))
            typeDef.CustomAttributes.Add(new Mono.Cecil.CustomAttribute(module.ImportReference(
                typeof(System.Runtime.CompilerServices.RequiredMemberAttribute).GetConstructor(Type.EmptyTypes)!)));

        // Emit [IsReadOnly] for readonly data
        if (data.IsReadonly && isStruct)
        {
            var isReadOnlyAttr = module.ImportReference(
                typeof(System.Runtime.CompilerServices.IsReadOnlyAttribute).GetConstructor(Type.EmptyTypes)!);
            typeDef.CustomAttributes.Add(new Mono.Cecil.CustomAttribute(isReadOnlyAttr));
        }

        // Collect fields with default values
        var fieldsWithDefaults = data.Fields.Where(f => f.DefaultValue is not null).ToList();

        // Helper: emit field default value assignments via stfld. On a generic type the
        // field is hosted on the self-instantiation (`Holder<!0>::flag`) — `this`/arg0 is
        // the closed self-instantiation, so an stfld against the open `Holder::flag` fails
        // verification with a receiver-type mismatch (same rule as the init-body stores).
        var selfRef = SelfInstantiation(typeDef);
        void EmitFieldDefaults(MethodBodyEmitter emitter, ILBuilder il)
        {
            foreach (var field in fieldsWithDefaults)
            {
                if (fieldMap.TryGetValue(field.Name, out var fieldDef))
                {
                    il.LoadArgByIndex(0); // this
                    emitter.EmitExpression(field.DefaultValue!);
                    il.StoreField(SelfField(fieldDef, selfRef));
                }
            }
        }

        // Classes need an explicit parameterless constructor (skip if some init() has
        // zero params — it serves as the parameterless ctor — or a primary header
        // exists: a headered class constructs only through its primary).
        if (!isStruct && !(data.Inits?.Any(i => i.Parameters.Count == 0 || i.IsPrimary) ?? false))
        {
            var ctor = new MethodDefinition(
                ".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.ImportReference(typeof(void)));
            ctor.Body.InitLocals = true;
            var il = new ILBuilder(ctor);
            il.LoadArgByIndex(0);
            // Chain to the base parameterless ctor — the external base (e.g. Attribute())
            // when one is declared, else object. A synthesized parameterless ctor over an
            // external base that called object::.ctor would be invalid metadata.
            il.Call(ResolveBaseCtor(module, types, isStruct ? null : data.BaseClass, 0)
                ?? module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            typeDef.Methods.Add(ctor);

            if (fieldsWithDefaults.Count > 0)
            {
                // A default's initializer is an arbitrary expression — deferred.
                ctorBodies += () =>
                {
                    var emitter = new MethodBodyEmitter(ctor, types, diagnostics, fieldMap, selfParamName: "self");
                    EmitFieldDefaults(emitter, il);
                    il.Return();
                    ILOptimizer.ShortenOpcodes(ctor.Body);
                };
            }
            else
            {
                il.Return();
                ILOptimizer.ShortenOpcodes(ctor.Body);
            }
        }

        // Declare every init constructor's signature up-front: init(params) { body }.
        // All MethodDefinitions exist (indexed in Inits order) before any body emits,
        // so a `: this(...)` delegating init can `call` its sibling ctor. Bodies ride
        // in `ctorBodies`.
        if (data.Inits is { Count: > 0 } inits)
        {
            var initCtors = new MethodDefinition[inits.Count];
            for (var i = 0; i < inits.Count; i++)
            {
                var init = inits[i];
                var visibility = init.Visibility switch
                {
                    Esharp.Syntax.InitVisibility.Private => MethodAttributes.Private,
                    Esharp.Syntax.InitVisibility.Protected => MethodAttributes.Family,
                    _ => MethodAttributes.Public,
                };
                var initCtor = new MethodDefinition(
                    ".ctor",
                    visibility | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    module.ImportReference(typeof(void)));

                foreach (var param in init.Parameters)
                {
                    var pd = new ParameterDefinition(param.Name, ParameterAttributes.None, types.Resolve(param.Type));
                    ApplyDefaultValueFacts(pd, param);
                    initCtor.Parameters.Add(pd);
                }

                initCtor.Body.InitLocals = true;

                // Add to the type up-front so DeclaringType is set while the body is emitted —
                // generic-`self` field access needs it to host fields on the closed
                // self-instantiation (`Box<!0>::v`), not the open definition.
                typeDef.Methods.Add(initCtor);
                initCtors[i] = initCtor;
            }

            for (var i = 0; i < inits.Count; i++)
            {
                var init = inits[i];
                var initCtor = initCtors[i];
                ctorBodies += () =>
                {
                    var initIl = new ILBuilder(initCtor);
                    var initEmitter = new MethodBodyEmitter(initCtor, types, diagnostics, fieldMap, selfParamName: "self");

                    if (init.DelegatesTo >= 0)
                    {
                        // `: this(args)` — chain to the sibling ctor. The delegate runs the
                        // base call and field defaults; this body must not repeat them.
                        initIl.LoadArgByIndex(0);
                        foreach (var ta in init.ThisArguments ?? [])
                            initEmitter.EmitExpression(ta);
                        initIl.Call(initCtors[init.DelegatesTo]);
                    }
                    else if (!isStruct)
                    {
                        // Call base ctor. If the user wrote `init(...) : base(args)`, emit the
                        // matching base ctor; otherwise chain to the base parameterless ctor.
                        // Both resolve through ResolveBaseCtor, which handles an in-compilation
                        // base (by param count) and an external base (BCL / referenced assembly).
                        if (init.BaseArguments is { Count: > 0 } && data.BaseClass is { } baseClassName)
                        {
                            var baseCtor = ResolveBaseCtor(module, types, baseClassName, init.BaseArguments.Count);
                            if (baseCtor is null)
                            {
                                diagnostics.Report("", 0, 0,
                                    $"IL: no matching .ctor on base '{baseClassName}' for {init.BaseArguments.Count} args from '{data.Name}.init : base(...)'");
                                initIl.LoadArgByIndex(0);
                                initIl.Call(module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
                            }
                            else
                            {
                                initIl.LoadArgByIndex(0);
                                foreach (var ba in init.BaseArguments)
                                    initEmitter.EmitExpression(ba);
                                initIl.Call(baseCtor);
                            }
                        }
                        else
                        {
                            initIl.LoadArgByIndex(0);
                            initIl.Call(ResolveBaseCtor(module, types, data.BaseClass, 0)
                                ?? module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
                        }
                    }

                    // Primary ctor of a headered class: store each captured header
                    // param into its synthesized private field, right after the base
                    // call and before field defaults (a default may read a param —
                    // that reads the ctor ARGUMENT, capture order is not observable).
                    if (init.IsPrimary && data.CapturedHeaderParams is { Count: > 0 } captures)
                    {
                        var captureSelfRef = SelfInstantiation(typeDef);
                        foreach (var capName in captures)
                        {
                            var capParam = initCtor.Parameters.FirstOrDefault(p => p.Name == capName);
                            if (capParam is null || !fieldMap.TryGetValue(capName, out var capField)) continue;
                            initIl.LoadArgByIndex(0);
                            initIl.LoadArg(capParam);
                            initIl.StoreField(SelfField(capField, captureSelfRef));
                        }
                    }

                    // Field default values run only in non-delegating ctors — a `: this`
                    // chain would otherwise stamp defaults twice, clobbering the
                    // delegate's work.
                    if (init.DelegatesTo < 0 && fieldsWithDefaults.Count > 0)
                        EmitFieldDefaults(initEmitter, initIl);

                    // Emit init body with 'self' mapped to arg0 (this)
                    initEmitter.EmitBlock(init.Body);

                    // Ensure ret
                    if (!ILBuilder.EndsInReturn(initCtor))
                        initIl.Return();

                    ILOptimizer.ShortenOpcodes(initCtor.Body);
                };
            }
        }

        // Positional data destructures: synthesize `void Deconstruct(out T1, ...)`
        // (one out per positional field, declaration order) so C# tuple
        // destructuring works against the type. E#'s own `let (a, b) = v` lowers
        // to direct field reads — same semantics, no call.
        if (data.IsPositionalData && data.Fields.Count > 0)
        {
            var deconSelfRef = SelfInstantiation(typeDef);
            var decon = new MethodDefinition("Deconstruct",
                MethodAttributes.Public | MethodAttributes.HideBySig,
                module.ImportReference(typeof(void)));
            foreach (var f in data.Fields)
            {
                var ft = types.Resolve(f.Type);
                decon.Parameters.Add(new ParameterDefinition(f.Name, ParameterAttributes.Out, new ByReferenceType(ft)));
            }
            var deconIl = new ILBuilder(decon);
            for (var i = 0; i < data.Fields.Count; i++)
            {
                var fieldDef = fieldMap[data.Fields[i].Name];
                deconIl.LoadArg(decon.Parameters[i]);
                deconIl.LoadArgByIndex(0);
                deconIl.LoadField(SelfField(fieldDef, deconSelfRef));
                deconIl.StoreObject(fieldDef.FieldType);
            }
            deconIl.Return();
            ILOptimizer.ShortenOpcodes(decon.Body);
            typeDef.Methods.Add(decon);
        }

        // Derive directives: synthesize Equals/GetHashCode (equality) and ToString (debug)
        if (data.DeriveTraits.Contains("equality"))
            EmitDeriveEquality(module, typeDef, data, isStruct);
        if (data.DeriveTraits.Contains("debug"))
            EmitDeriveDebug(module, typeDef, data);

        if (data.Attributes.Count > 0)
            EmitClrAttributes(module, types, typeDef, data.Attributes);

        if (pushedGenericContext)
            types.PopGenericContext();

        // The deferred body work re-establishes this type's generic context — the
        // populate sweep popped it above, and the bodies run after the whole sweep.
        var capturedBodies = ctorBodies;
        Action? deferred = capturedBodies is null ? null : () =>
        {
            var pushed = typeDef.HasGenericParameters;
            if (pushed) types.PushGenericContext(typeDef.GenericParameters);
            capturedBodies();
            if (pushed) types.PopGenericContext();
        };

        return (fieldMap, deferred);
    }

    static void DeclareStoredAutoPropertyAccessors(ModuleDefinition module, ILTypeResolver types,
        DiagnosticBag diagnostics, TypeDefinition typeDef, BoundDataDeclaration data,
        Dictionary<string, FieldDefinition> fieldMap, ref Action? deferredBodies)
    {
        var selfRef = SelfInstantiation(typeDef);
        foreach (var field in data.Fields)
        {
            if (!field.IsProperty || field.IsComputedProperty) continue;
            var backing = typeDef.Fields.FirstOrDefault(f => f.Name == PropertyBackingFieldName(field.Name))
                ?? (field.PropHasExplicitLoca && field.PropLocaStorageName is { } storage
                    ? typeDef.Fields.FirstOrDefault(f => f.Name == storage)
                    : null);
            if (backing is null) continue;

            // Per-accessor visibility (P8): `get`/`set` fall back to the property's own
            // visibility, but a `pub var X { priv set }` narrows just the setter.
            static MethodAttributes VisAttr(Esharp.Syntax.Visibility v) => v switch
            {
                Esharp.Syntax.Visibility.Public => MethodAttributes.Public,
                Esharp.Syntax.Visibility.Private => MethodAttributes.Private,
                _ => MethodAttributes.Assembly,
            };
            var getAttrs = VisAttr(field.GetterVis ?? field.Vis) | MethodAttributes.HideBySig | MethodAttributes.SpecialName;
            var setAttrs = VisAttr(field.SetterVis ?? field.Vis) | MethodAttributes.HideBySig | MethodAttributes.SpecialName;
            var getter = new MethodDefinition("get_" + field.Name, getAttrs, backing.FieldType);
            getter.Body.InitLocals = true;
            typeDef.Methods.Add(getter);

            MethodDefinition? setter = null;
            if ((field.PropHasSet || field.PropHasInit) && !field.PropHasCustomSetter)
            {
                var voidRef = module.ImportReference(typeof(void));
                TypeReference returnType = field.PropHasInit
                    ? new RequiredModifierType(module.ImportReference(
                        typeof(System.Runtime.CompilerServices.IsExternalInit)), voidRef)
                    : voidRef;
                setter = new MethodDefinition("set_" + field.Name, setAttrs, returnType);
                setter.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, backing.FieldType));
                setter.Body.InitLocals = true;
                typeDef.Methods.Add(setter);
            }

            if (field.ScopedMut is { } scopedMut)
            {
                // No ref-return exists for a scoped lend.  The accessor bodies run
                // setup and guarantee resume through a real CLR finally; address
                // uses receive their own call-site lowering later in the pipeline.
                var lease = DeclareScopedMutLeaseProtocol(module, typeDef, field, scopedMut);
                deferredBodies += () =>
                {
                    EmitScopedMutAccessorBodies(getter, setter, types, diagnostics, fieldMap, scopedMut);
                    EmitScopedMutLeaseBodies(lease, types, diagnostics, fieldMap, scopedMut);
                };
            }
            else
            {
                var getIl = new ILBuilder(getter);
                getIl.LoadArgByIndex(0);
                getIl.LoadField(SelfField(backing, selfRef));
                getIl.Return();
                if (setter is not null)
                {
                    var setIl = new ILBuilder(setter);
                    setIl.LoadArgByIndex(0);
                    setIl.LoadArgByIndex(1);
                    setIl.StoreField(SelfField(backing, selfRef));
                    setIl.Return();
                }
            }

            // Stored let/var properties have implicit location identity unless
            // `mut` owns the protocol.  An explicit `loca` or direct `mut`
            // supplies the same ref-return machinery with selected storage.
            if (!field.PropHasMut || field.PropHasExplicitLoca)
            {
                // This is deliberately a ref-return accessor rather than a
                // pointer type in E# surface syntax.  A class receiver remains a
                // class; `&owner.property` calls this location protocol and never
                // creates `*Owner`.
                var locationStorage = typeDef.Fields.FirstOrDefault(f => f.Name == field.PropLocaStorageName)
                    ?? backing;
                var loca = new MethodDefinition("getloca_" + field.Name,
                    getAttrs | MethodAttributes.SpecialName,
                    new ByReferenceType(locationStorage.FieldType));
                loca.Body.InitLocals = true;
                var locaIl = new ILBuilder(loca);
                locaIl.LoadArgByIndex(0);
                locaIl.LoadFieldAddress(SelfField(locationStorage, selfRef));
                locaIl.Return();
                typeDef.Methods.Add(loca);
            }

            typeDef.Properties.Add(new PropertyDefinition(field.Name, PropertyAttributes.None, backing.FieldType)
            {
                HasThis = true,
                GetMethod = getter,
                SetMethod = setter,
            });
            var property = typeDef.Properties[^1];
            EmitPropertyCapability(module, property, field);
        }
    }

    // A scoped `mut` cannot expose a ref-return: its resume code is part of the
    // property's contract and has to execute in the declaring type, where private
    // storage is actually accessible.  The generated protocol keeps that boundary:
    // begin runs setup and snapshots the setup locals into an opaque lease; callers
    // borrow only `lease.Value`; resume restores the locals and runs cleanup.
    // The carrier is metadata machinery, never an E# `*Class` surface type.
    sealed record ScopedMutLeaseProtocol(
        TypeDefinition Carrier,
        TypeReference CarrierOnOwner,
        MethodDefinition Begin,
        MethodDefinition Resume,
        FieldDefinition Value,
        IReadOnlyList<(BoundVariableDeclaration Local, FieldDefinition Field)> Captures);

    static ScopedMutLeaseProtocol DeclareScopedMutLeaseProtocol(ModuleDefinition module,
        TypeDefinition owner, BoundField property, BoundScopedMutAccessor scoped)
    {
        // A generic nested type has a separate generic context from its owner.
        // Using it in the owner's helper signature produces an ambiguous
        // `Box<T>+Lease<T>` member reference at runtime.  Generic owners
        // therefore use a module-internal carrier with the ordinary `Lease<T>`
        // signature; non-generic owners retain the compact nested shape.
        var carrierName = owner.HasGenericParameters
            ? "__mut_lease_" + owner.Name.Replace('`', '_') + "_" + property.Name
                + "`" + owner.GenericParameters.Count
            : "__mut_lease_" + property.Name;
        var carrierVisibility = property.Vis == Esharp.Syntax.Visibility.Public
            ? (owner.HasGenericParameters ? TypeAttributes.Public : TypeAttributes.NestedPublic)
            : (owner.HasGenericParameters ? TypeAttributes.NotPublic : TypeAttributes.NestedAssembly);
        var carrier = new TypeDefinition(owner.HasGenericParameters ? owner.Namespace : string.Empty, carrierName,
            carrierVisibility | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit | TypeAttributes.Class,
            module.ImportReference(typeof(object)));
        if (owner.HasGenericParameters)
            module.Types.Add(carrier);
        else
            owner.NestedTypes.Add(carrier);

        // A nested CLR type does not automatically close over its owner's generic
        // parameters. Mirror them on the opaque carrier so `Box<T>.value` lends a
        // `__mut_lease_value<T>`, not an illegal open `!0` field on a non-generic
        // nested class.
        foreach (var parameter in owner.GenericParameters)
            carrier.GenericParameters.Add(new GenericParameter(parameter.Name, carrier));
        TypeReference carrierOnOwner = carrier;
        if (carrier.HasGenericParameters)
        {
            var closed = new GenericInstanceType(carrier);
            foreach (var parameter in owner.GenericParameters) closed.GenericArguments.Add(parameter);
            carrierOnOwner = closed;
        }

        var valueVisibility = property.Vis == Esharp.Syntax.Visibility.Public
            ? FieldAttributes.Public : FieldAttributes.Assembly;
        var value = new FieldDefinition("Value", valueVisibility, module.ImportReference(
            typeof(object))); // refined below once the body context can resolve the E# type
        carrier.Fields.Add(value);

        var captures = scoped.Setup.Statements.OfType<BoundVariableDeclaration>()
            .Select(local => (local, new FieldDefinition("__" + local.Name,
                // The enclosing owner emits begin/resume, so this is assembly
                // metadata rather than CLR-private nested storage.  The carrier
                // itself remains nested/internal and has no E# surface spelling.
                FieldAttributes.Assembly, module.ImportReference(typeof(object))))).ToArray();
        foreach (var (_, capture) in captures) carrier.Fields.Add(capture);

        var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName
            | MethodAttributes.RTSpecialName | MethodAttributes.HideBySig, module.ImportReference(typeof(void)));
        var ctorIl = new ILBuilder(ctor);
        ctorIl.LoadArgByIndex(0);
        ctorIl.Call(module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
        ctorIl.Return();
        carrier.Methods.Add(ctor);

        var helperAttrs = (property.Vis == Esharp.Syntax.Visibility.Public
            ? MethodAttributes.Public : MethodAttributes.Assembly) | MethodAttributes.HideBySig;
        var begin = new MethodDefinition("__mut_begin_" + property.Name, helperAttrs, carrierOnOwner)
        {
            HasThis = true,
        };
        var resume = new MethodDefinition("__mut_resume_" + property.Name,
            helperAttrs, module.ImportReference(typeof(void))) { HasThis = true };
        resume.Parameters.Add(new ParameterDefinition("lease", ParameterAttributes.None, carrierOnOwner));
        owner.Methods.Add(begin);
        owner.Methods.Add(resume);
        return new ScopedMutLeaseProtocol(carrier, carrierOnOwner, begin, resume, value, captures);
    }

    static void EmitScopedMutLeaseBodies(ScopedMutLeaseProtocol lease, ILTypeResolver types,
        DiagnosticBag diagnostics, Dictionary<string, FieldDefinition> fieldMap, BoundScopedMutAccessor scoped)
    {
        // Resolve carrier members only after the enclosing type's generic context is
        // re-established by the deferred-body sweep.
        lease.Value.FieldType = RebindLeaseCarrierType(types.Resolve(scoped.YieldTarget.Type), lease);
        foreach (var (local, field) in lease.Captures)
            field.FieldType = RebindLeaseCarrierType(types.Resolve(local.DeclaredType), lease);

        var beginEmitter = new MethodBodyEmitter(lease.Begin, types, diagnostics, fieldMap, selfParamName: "self");
        beginEmitter.PrepareCaptures(scoped.Setup);
        beginEmitter.PrepareHoistedPointers(scoped.Setup);
        beginEmitter.EmitBlock(scoped.Setup);

        var beginIl = new ILBuilder(lease.Begin);
        var leaseLocal = new VariableDefinition(lease.CarrierOnOwner);
        lease.Begin.Body.Variables.Add(leaseLocal);
        beginIl.NewObj(HostLeaseConstructor(lease.Carrier.Methods.First(m => m.IsConstructor), lease.CarrierOnOwner));
        beginIl.StoreLocal(leaseLocal);
        foreach (var (local, field) in lease.Captures)
        {
            beginIl.LoadLocal(leaseLocal);
            beginEmitter.EmitGeneratedLocalLoad(local.Name);
            beginIl.StoreField(HostLeaseField(field, lease.CarrierOnOwner));
        }
        // The yielded working local is the sole caller-visible location.  Its
        // initial value must be copied into Value before the borrower receives a
        // byref, otherwise the lease would expose a default-initialized field.
        beginIl.LoadLocal(leaseLocal);
        beginEmitter.EmitGeneratedLocalLoad(((BoundNameExpression)scoped.YieldTarget).Name);
        beginIl.StoreField(HostLeaseField(lease.Value, lease.CarrierOnOwner));
        beginIl.LoadLocal(leaseLocal);
        beginIl.Return();
        ILOptimizer.ShortenOpcodes(lease.Begin.Body);

        var resumeEmitter = new MethodBodyEmitter(lease.Resume, types, diagnostics, fieldMap, selfParamName: "self");
        foreach (var (local, field) in lease.Captures)
        {
            resumeEmitter.SeedGeneratedLocal(local.Name, local.DeclaredType);
            var resumeIl = new ILBuilder(lease.Resume);
            resumeIl.LoadArg(lease.Resume.Parameters[0]);
            resumeIl.LoadField(HostLeaseField(field, lease.CarrierOnOwner));
            resumeEmitter.EmitGeneratedLocalStoreFromStack(local.Name);
        }
        // Resume sees the post-borrow value, not the setup snapshot.  Other setup
        // locals retain their captured values so cleanup can compare, validate, or
        // write through arbitrary owner storage.
        var yieldedName = ((BoundNameExpression)scoped.YieldTarget).Name;
        var resumeIlForYield = new ILBuilder(lease.Resume);
        resumeIlForYield.LoadArg(lease.Resume.Parameters[0]);
        resumeIlForYield.LoadField(HostLeaseField(lease.Value, lease.CarrierOnOwner));
        resumeEmitter.EmitGeneratedLocalStoreFromStack(yieldedName);
        resumeEmitter.PrepareCaptures(scoped.Resume);
        resumeEmitter.PrepareHoistedPointers(scoped.Resume);
        resumeEmitter.EmitBlock(scoped.Resume);
        resumeEmitter.FinalizeMethodEpilogue();
        if (!ILBuilder.EndsInReturn(lease.Resume)) new ILBuilder(lease.Resume).Return();
        ILOptimizer.ShortenOpcodes(lease.Resume.Body);
    }

    static FieldReference HostLeaseField(FieldDefinition field, TypeReference carrier)
    {
        if (carrier is not GenericInstanceType closedCarrier) return field;

        // A field reference on `__mut_lease<T>` must carry the substituted field
        // type as well as the closed declaring type.  Reusing the definition's
        // `T` directly leaves the field typed with the carrier's open parameter,
        // which the CLR verifier quite correctly refuses in `Box<T>::begin`.
        return new FieldReference(field.Name,
            CloseLeaseMemberType(field.FieldType, closedCarrier), closedCarrier);
    }

    static TypeReference CloseLeaseMemberType(TypeReference type, GenericInstanceType closedCarrier)
    {
        if (type is GenericParameter parameter && parameter.Owner is TypeDefinition owner
            && ReferenceEquals(owner, closedCarrier.ElementType.Resolve()))
        {
            var index = owner.GenericParameters.IndexOf(parameter);
            return index >= 0 ? closedCarrier.GenericArguments[index] : type;
        }
        if (type is GenericInstanceType generic)
        {
            var closed = new GenericInstanceType(generic.ElementType);
            foreach (var argument in generic.GenericArguments)
                closed.GenericArguments.Add(CloseLeaseMemberType(argument, closedCarrier));
            return closed;
        }
        if (type is ArrayType array)
            return new ArrayType(CloseLeaseMemberType(array.ElementType, closedCarrier), array.Rank);
        if (type is ByReferenceType byReference)
            return new ByReferenceType(CloseLeaseMemberType(byReference.ElementType, closedCarrier));
        return type;
    }

    static MethodReference HostLeaseConstructor(MethodDefinition ctor, TypeReference carrier)
    {
        if (carrier is not GenericInstanceType) return ctor;
        var reference = new MethodReference(ctor.Name, ctor.ReturnType, carrier)
        {
            HasThis = ctor.HasThis,
            ExplicitThis = ctor.ExplicitThis,
            CallingConvention = ctor.CallingConvention,
        };
        foreach (var parameter in ctor.Parameters)
            reference.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        return reference;
    }

    static TypeReference RebindLeaseCarrierType(TypeReference type, ScopedMutLeaseProtocol lease)
    {
        if (type is GenericParameter parameter && parameter.Owner is TypeDefinition owner
            && ReferenceEquals(owner, lease.Begin.DeclaringType))
        {
            var index = owner.GenericParameters.IndexOf(parameter);
            return index >= 0 ? lease.Carrier.GenericParameters[index] : type;
        }
        if (type is GenericInstanceType generic)
        {
            var rebound = new GenericInstanceType(generic.ElementType);
            foreach (var argument in generic.GenericArguments)
                rebound.GenericArguments.Add(RebindLeaseCarrierType(argument, lease));
            return rebound;
        }
        if (type is ArrayType array)
            return new ArrayType(RebindLeaseCarrierType(array.ElementType, lease), array.Rank);
        return type;
    }

    static void EmitScopedMutAccessorBodies(MethodDefinition getter, MethodDefinition? setter,
        ILTypeResolver types, DiagnosticBag diagnostics, Dictionary<string, FieldDefinition> fieldMap,
        BoundScopedMutAccessor scoped)
    {
        var getterBody = new BoundBlockStatement(
        [
            .. scoped.Setup.Statements,
            new BoundTryStatement(
                new BoundBlockStatement([new BoundReturnStatement(scoped.YieldTarget)]),
                [new BoundCatchClause(null, null, scoped.Resume, Guard: null, IsFinally: true)]),
        ]);
        var getterEmitter = new MethodBodyEmitter(getter, types, diagnostics, fieldMap, selfParamName: "self");
        getterEmitter.PrepareCaptures(getterBody);
        getterEmitter.PrepareHoistedPointers(getterBody);
        getterEmitter.EmitBlock(getterBody);
        getterEmitter.FinalizeMethodEpilogue();
        if (!ILBuilder.EndsInReturn(getter))
        {
            // An invalid mut body already has a binder diagnostic, but keep the
            // emitted type verifier-safe rather than letting one bad accessor
            // poison its containing assembly.
            var fallback = new ILBuilder(getter);
            EmitDefaultReturnValue(fallback, getter);
            fallback.Return();
        }
        ILOptimizer.ShortenOpcodes(getter.Body);

        if (setter is null) return;
        var value = new BoundNameExpression("value", scoped.YieldTarget.Type);
        var setterBody = new BoundBlockStatement(
        [
            .. scoped.Setup.Statements,
            new BoundTryStatement(
                new BoundBlockStatement([new BoundAssignment(scoped.YieldTarget, value)]),
                [new BoundCatchClause(null, null, scoped.Resume, Guard: null, IsFinally: true)]),
        ]);
        var setterEmitter = new MethodBodyEmitter(setter, types, diagnostics, fieldMap, selfParamName: "self");
        setterEmitter.PrepareCaptures(setterBody);
        setterEmitter.PrepareHoistedPointers(setterBody);
        setterEmitter.EmitBlock(setterBody);
        setterEmitter.FinalizeMethodEpilogue();
        if (!ILBuilder.EndsInReturn(setter)) new ILBuilder(setter).Return();
        ILOptimizer.ShortenOpcodes(setter.Body);
    }

    /// The type closed over its own generic parameters (`Pair<A,B>`), or the
    /// type itself when non-generic. Field/method references in generated members
    /// must target this, not the open definition.
    static TypeReference SelfInstantiation(TypeDefinition typeDef)
    {
        if (!typeDef.HasGenericParameters) return typeDef;
        var git = new GenericInstanceType(typeDef);
        foreach (var gp in typeDef.GenericParameters)
            git.GenericArguments.Add(gp);
        return git;
    }

    /// A reference to `field` on the type's self-instantiation, so `ldfld` inside
    /// a generic type's generated methods targets `Pair<A,B>::first` rather than
    /// the open `Pair::first` (which the verifier rejects).
    static FieldReference SelfField(FieldDefinition field, TypeReference selfRef)
        => selfRef is GenericInstanceType
            ? new FieldReference(field.Name, field.FieldType, selfRef)
            : field;

    /// A reference to one of the type's own generated methods, hosted on the
    /// self-instantiation (`Pair<A,B>::Equals`) rather than the open definition
    /// (`Pair::Equals`). Calling the open `MethodDefinition` on a closed receiver
    /// is unverifiable (StackUnexpected: receiver is `Pair<!0,!1>`, callee declares
    /// `Pair`). Non-generic types reuse the definition unchanged.
    static MethodReference SelfMethod(MethodDefinition method, TypeReference selfRef)
    {
        if (selfRef is not GenericInstanceType) return method;
        var mr = new MethodReference(method.Name, method.ReturnType, selfRef)
        {
            HasThis = method.HasThis,
            ExplicitThis = method.ExplicitThis,
            CallingConvention = method.CallingConvention,
        };
        foreach (var p in method.Parameters)
            mr.Parameters.Add(new ParameterDefinition(p.ParameterType));
        return mr;
    }

    /// Auto-bridge a transitive NON-generic base interface. A type implementing a BCL
    /// generic interface whose base is a non-generic interface with methods — the
    /// canonical case `IEnumerable<T> : IEnumerable` — must also implement the base, or
    /// the CLR rejects it at load. If the user didn't list the base explicitly,
    /// synthesize an explicit forwarder to the generic counterpart
    /// (`IEnumerable.GetEnumerator() => this.GetEnumerator()`; the covariant
    /// `IEnumerator<T>` return upcasts to `IEnumerator`).
    internal static void EmitTransitiveInterfaceBridges(
        ModuleDefinition module, ILTypeResolver types, TypeDefinition typeDef, BoundDataDeclaration data)
    {
        var selfRef = SelfInstantiation(typeDef);

        foreach (var ifaceType in ConformanceEntries(data))
        {
            // Only generic interfaces carry non-generic bases here.
            var isGeneric = ifaceType is InterfaceType { TypeArgs.Count: > 0 } or ExternalType { TypeArgs.Count: > 0 };
            if (!isGeneric) continue;
            if (types.ResolveConformance(ifaceType)?.Resolve() is not { } ifaceDef) continue;
            foreach (var baseRef in ifaceDef.Interfaces)
            {
                var baseDef = baseRef.InterfaceType.Resolve();
                if (baseDef is null || baseDef.HasGenericParameters) continue;       // only non-generic bases

                var bridged = false;
                foreach (var bm in baseDef.Methods.Where(m => m.IsVirtual || m.IsAbstract))
                {
                    // Already implemented? An explicit member (`func IEnumerable.GetEnumerator()`)
                    // carries an override targeting this exact base slot — leave it alone.
                    var implemented = typeDef.Methods.Any(tm => tm.Overrides.Any(o =>
                        o.Name == bm.Name && o.DeclaringType?.FullName == baseDef.FullName));
                    if (implemented) continue;

                    // The generic counterpart already emitted on the type (same name + arity,
                    // wired as an interface override) to forward to.
                    var target = typeDef.Methods.FirstOrDefault(tm =>
                        !tm.IsStatic && tm.Name == bm.Name && tm.Parameters.Count == bm.Parameters.Count
                        && tm.Overrides.Count > 0);
                    if (target is null) continue;

                    var bridge = new MethodDefinition(
                        $"{baseDef.FullName}.{bm.Name}",
                        MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.Virtual
                            | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                        module.ImportReference(bm.ReturnType));
                    foreach (var p in bm.Parameters)
                        bridge.Parameters.Add(new ParameterDefinition(module.ImportReference(p.ParameterType)));
                    typeDef.Methods.Add(bridge);

                    var bil = new ILBuilder(bridge);
                    bil.LoadArgByIndex(0);
                    foreach (var p in bridge.Parameters) bil.LoadArg(p);
                    bil.CallVirt(SelfMethod(target, selfRef));
                    bil.Return();
                    bridge.Overrides.Add(module.ImportReference(bm));
                    bridged = true;
                }
                if (bridged && !typeDef.Interfaces.Any(ii => ii.InterfaceType.FullName == baseDef.FullName))
                    typeDef.Interfaces.Add(new InterfaceImplementation(module.ImportReference(baseDef)));
            }
        }
    }

    static void EmitDeriveEquality(ModuleDefinition module, TypeDefinition typeDef, BoundDataDeclaration data, bool isStruct)
    {
        // For a generic type, every reference to "this type" must be the
        // self-instantiation closed over the type's own generic parameters
        // (`Pair<A, B>`), not the open definition (`Pair`). Using the open form
        // as a parameter / isinst / unbox operand produces metadata the CLR
        // rejects with TypeLoadException.
        var selfRef = SelfInstantiation(typeDef);

        // Public bool Equals({selfRef} other) — field-by-field via EqualityComparer<T>.Default.
        // Virtual/newslot/final so it fills the `IEquatable<Self>` slot (wired below);
        // this is what makes the derived equality usable by EqualityComparer<T>.Default,
        // generic constraints, and the plan's `Result : IEquatable<Result<T,E>>` contract.
        var typedEquals = new MethodDefinition(
            "Equals",
            MethodAttributes.Public | MethodAttributes.HideBySig
                | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final,
            module.ImportReference(typeof(bool)));
        typedEquals.Parameters.Add(new ParameterDefinition("other", ParameterAttributes.None, selfRef));
        typedEquals.Body.InitLocals = true;
        var eqIl = new ILBuilder(typedEquals);

        var fields = typeDef.Fields.Where(f => !f.IsStatic).ToList();
        if (fields.Count == 0)
        {
            eqIl.LoadInt(1);
            eqIl.Return();
        }
        else
        {
            var retFalse = eqIl.DefineLabel("retFalse");

            foreach (var field in fields)
            {
                var fieldType = field.FieldType;
                var getDefault = EqualityComparerGetDefault(module, fieldType);
                var equalsMethod = EqualityComparerEquals(module, fieldType);

                // EqualityComparer<T>.Default
                eqIl.Call(getDefault);
                // this.field (for structs, load the address; for classes, load the reference)
                if (isStruct)
                {
                    eqIl.LoadArgByIndex(0);
                    eqIl.LoadField(SelfField(field, selfRef));
                }
                else
                {
                    eqIl.LoadArgByIndex(0);
                    eqIl.LoadField(SelfField(field, selfRef));
                }
                // other.field — for struct methods, other is passed by value (arg1 is the value)
                if (isStruct)
                {
                    eqIl.LoadArgAddress(typedEquals.Parameters[0]);
                    eqIl.LoadField(SelfField(field, selfRef));
                }
                else
                {
                    eqIl.LoadArgByIndex(1);
                    eqIl.LoadField(SelfField(field, selfRef));
                }
                // Call Equals(x, y) — returns bool
                eqIl.CallVirt(equalsMethod);
                eqIl.BranchIfFalse(retFalse);
            }

            eqIl.LoadInt(1);
            eqIl.Return();
            eqIl.MarkLabel(retFalse);
            eqIl.LoadInt(0);
            eqIl.Return();
        }
        ILOptimizer.ShortenOpcodes(typedEquals.Body);
        typeDef.Methods.Add(typedEquals);

        // Conform to System.IEquatable<Self>: the typed Equals IS the interface method.
        // (The binder's nominal `IEquatable<...>` marker is skipped in the interface
        // loop precisely so the real conformance is wired here, on the closed self.)
        var iequatableClosed = new GenericInstanceType(module.ImportReference(typeof(IEquatable<>)))
        {
            GenericArguments = { selfRef },
        };
        typeDef.Interfaces.Add(new InterfaceImplementation(iequatableClosed));
        // The override target must reference the interface method by ITS signature —
        // param `!0` of IEquatable`1 (bound to Self via the closed declaring type) — not
        // the substituted Self. Import the open Equals (so `!0` resolves), then re-home
        // onto the closed interface instance.
        var equalsOpen = module.ImportReference(typeof(IEquatable<>).GetMethods().First(m => m.Name == "Equals"));
        var iequatableEquals = new MethodReference(equalsOpen.Name, equalsOpen.ReturnType, iequatableClosed)
        {
            HasThis = equalsOpen.HasThis,
        };
        foreach (var p in equalsOpen.Parameters)
            iequatableEquals.Parameters.Add(new ParameterDefinition(p.ParameterType));
        typedEquals.Overrides.Add(iequatableEquals);

        // public override bool Equals(object obj) — type check, delegate to typed Equals
        var objEquals = new MethodDefinition(
            "Equals",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
            module.ImportReference(typeof(bool)));
        objEquals.Parameters.Add(new ParameterDefinition("obj", ParameterAttributes.None, module.ImportReference(typeof(object))));
        objEquals.Body.InitLocals = true;
        var objIl = new ILBuilder(objEquals);

        if (isStruct)
        {
            // if (!(obj is T)) return false; return Equals((T)obj);
            var retFalse = objIl.DefineLabel("retFalse");
            objIl.LoadArgByIndex(1);
            objIl.IsInst(selfRef);
            objIl.BranchIfFalse(retFalse);

            objIl.LoadArgByIndex(0);
            objIl.LoadArgByIndex(1);
            objIl.UnboxAny(selfRef);
            objIl.Call(SelfMethod(typedEquals, selfRef));
            objIl.Return();
            objIl.MarkLabel(retFalse);
            objIl.LoadInt(0);
            objIl.Return();
        }
        else
        {
            // Reference type: type check + cast + delegate
            var retFalse = objIl.DefineLabel("retFalse");
            objIl.LoadArgByIndex(1);
            objIl.IsInst(selfRef);
            objIl.BranchIfFalse(retFalse);

            objIl.LoadArgByIndex(0);
            objIl.LoadArgByIndex(1);
            objIl.CastClass(selfRef);
            objIl.CallVirt(SelfMethod(typedEquals, selfRef));
            objIl.Return();
            objIl.MarkLabel(retFalse);
            objIl.LoadInt(0);
            objIl.Return();
        }
        ILOptimizer.ShortenOpcodes(objEquals.Body);
        typeDef.Methods.Add(objEquals);

        // public override int GetHashCode() — (hash * 31) + EqualityComparer<T>.Default.GetHashCode(field)
        var hashCode = new MethodDefinition(
            "GetHashCode",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
            module.ImportReference(typeof(int)));
        hashCode.Body.InitLocals = true;
        var hashIl = new ILBuilder(hashCode);

        var hashLocal = new VariableDefinition(module.ImportReference(typeof(int)));
        hashCode.Body.Variables.Add(hashLocal);

        // hash = 17
        hashIl.LoadInt(17);
        hashIl.StoreLocal(hashLocal);

        foreach (var field in fields)
        {
            var getDefault = EqualityComparerGetDefault(module, field.FieldType);
            var getHashForT = EqualityComparerGetHashCode(module, field.FieldType);

            // hash = (hash * 31) + EqualityComparer<T>.Default.GetHashCode(this.field)
            hashIl.LoadLocal(hashLocal);
            hashIl.LoadInt(31);
            hashIl.Mul();

            hashIl.Call(getDefault);
            hashIl.LoadArgByIndex(0);
            hashIl.LoadField(SelfField(field, selfRef));
            hashIl.CallVirt(getHashForT);

            hashIl.Add();
            hashIl.StoreLocal(hashLocal);
        }

        hashIl.LoadLocal(hashLocal);
        hashIl.Return();
        ILOptimizer.ShortenOpcodes(hashCode.Body);
        typeDef.Methods.Add(hashCode);

        // public static bool op_Equality(T a, T b) => a.Equals(b);  (and op_Inequality)
        // Without these, `a == b` in E# (and C# consumers) falls back to `ceq`
        // reference/bitwise comparison — wrong for value-shaped equality.
        EmitEqualityOperator(module, typeDef, selfRef, typedEquals, isStruct, "op_Equality", negate: false);
        EmitEqualityOperator(module, typeDef, selfRef, typedEquals, isStruct, "op_Inequality", negate: true);
    }

    static void EmitEqualityOperator(ModuleDefinition module, TypeDefinition typeDef, TypeReference selfRef,
        MethodDefinition typedEquals, bool isStruct, string name, bool negate)
    {
        var op = new MethodDefinition(
            name,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            module.ImportReference(typeof(bool)));
        op.Parameters.Add(new ParameterDefinition("a", ParameterAttributes.None, selfRef));
        op.Parameters.Add(new ParameterDefinition("b", ParameterAttributes.None, selfRef));
        op.Body.InitLocals = true;
        var il = new ILBuilder(op);
        var equalsRef = SelfMethod(typedEquals, selfRef);
        if (isStruct)
        {
            il.LoadArgAddress(op.Parameters[0]); // a (struct receiver → address)
            il.LoadArgByIndex(1); // b
            il.Call(equalsRef);
        }
        else
        {
            il.LoadArgByIndex(0);
            il.LoadArgByIndex(1);
            il.CallVirt(equalsRef);
        }
        if (negate)
        {
            il.LoadInt(0);
            il.Ceq();
        }
        il.Return();
        ILOptimizer.ShortenOpcodes(op.Body);
        typeDef.Methods.Add(op);
    }

    static void EmitDeriveDebug(ModuleDefinition module, TypeDefinition typeDef, BoundDataDeclaration data)
    {
        // public override string ToString() — "Name { f1 = {f1}, f2 = {f2} }"
        var toString = new MethodDefinition(
            "ToString",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
            module.ImportReference(typeof(string)));
        toString.Body.InitLocals = true;
        var il = new ILBuilder(toString);
        var selfRef = SelfInstantiation(typeDef);

        var fields = typeDef.Fields.Where(f => !f.IsStatic).ToList();
        var sbType = typeof(System.Text.StringBuilder);
        var sbCtor = module.ImportReference(sbType.GetConstructor(Type.EmptyTypes)!);
        var sbAppendString = module.ImportReference(sbType.GetMethod("Append", [typeof(string)])!);
        var sbAppendObject = module.ImportReference(sbType.GetMethod("Append", [typeof(object)])!);
        var sbToString = module.ImportReference(sbType.GetMethod("ToString", Type.EmptyTypes)!);

        var sbLocal = new VariableDefinition(module.ImportReference(sbType));
        toString.Body.Variables.Add(sbLocal);

        // var sb = new StringBuilder();
        il.NewObj(sbCtor);
        il.StoreLocal(sbLocal);

        void AppendLit(string s)
        {
            il.LoadLocal(sbLocal);
            il.LoadString(s);
            il.CallVirt(sbAppendString);
            il.Pop();
        }

        AppendLit(data.Name + " { ");
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            if (i > 0) AppendLit(", ");
            AppendLit(field.Name + " = ");

            // sb.Append((object)this.field)
            il.LoadLocal(sbLocal);
            il.LoadArgByIndex(0);
            il.LoadField(SelfField(field, selfRef));
            // Box value types AND generic parameters: an unconstrained type
            // parameter reports IsValueType=false, but `Append(object)` still
            // needs the value boxed (and `box !T` is a no-op for reference args).
            if (field.FieldType.IsValueType || field.FieldType.IsGenericParameter)
                il.Box(field.FieldType);
            il.CallVirt(sbAppendObject);
            il.Pop();
        }
        AppendLit(" }");

        // return sb.ToString();
        il.LoadLocal(sbLocal);
        il.CallVirt(sbToString);
        il.Return();

        ILOptimizer.ShortenOpcodes(toString.Body);
        typeDef.Methods.Add(toString);
    }
}
