using Mono.Cecil;
using Esharp.Emit;
using Esharp.BoundTree;

namespace Esharp.CodeGen;

// Property declarations (plan 11B). A property carries no public field — its accessors
// are real `get_<name>`/`set_<name>` methods plus a PropertyDefinition, the same metadata
// shape C# emits (and the same shape ILEmitter.Event.cs gives an event's add_/remove_ +
// EventDefinition). This pass runs per data/class type after its instance-method
// SIGNATURES exist, and attaches the property metadata:
//
//   • computed (`let x: T => expr`): the getter was synthesized as the `get_<name>` method
//     in the parser; here we just mark it special-name and bind a get-only PropertyDefinition.
//   • stored (`var x {}` / `let x {}` / `required let x {}`): backed by the private
//     `<x>k__BackingField` (emitted in PopulateDataMembers). Here we synthesize a trivial
//     `get_<name>` (and `set_<name>` when the accessor set has a setter) over that field and
//     bind the PropertyDefinition. Reads route through get_x; composite/with/init reach the
//     backing field by name through the FindFieldOnType bridge.
public static partial class CodeGenerator
{
    // This is deliberately compiler-owned metadata rather than a dependency on
    // a runtime package: every E# assembly can describe its property protocol to
    // a later E# compilation without reviving Esharp.Runtime.  Consumers match
    // the attribute by its reserved metadata name and never surface it as an
    // E# declaration.
    const string PropertyCapabilityAttributeName = "__EsharpPropertyCapabilityAttribute";

    [Flags]
    internal enum PropertyCapability
    {
        None = 0,
        Stored = 1 << 0,
        DurableLocation = 1 << 1,
        DirectMut = 1 << 2,
        ScopedMut = 1 << 3,
        Writable = 1 << 4,
        CustomSetter = 1 << 5,
    }

    internal static string PropertyBackingFieldName(string propertyName) => $"<{propertyName}>k__BackingField";

    static void EmitPropertyCapability(ModuleDefinition module, PropertyDefinition property, BoundField field)
    {
        var capability = PropertyCapability.None;
        if (!field.IsComputedProperty) capability |= PropertyCapability.Stored;
        if (!field.IsComputedProperty
            && (field.PropHasExplicitLoca
                || field.PropHasMut && field.ScopedMut is null
                || !field.PropHasMut && !field.PropHasCustomSetter))
            capability |= PropertyCapability.DurableLocation;
        if (field.PropHasMut && field.ScopedMut is null) capability |= PropertyCapability.DirectMut;
        if (field.ScopedMut is not null) capability |= PropertyCapability.ScopedMut;
        // An init-only setter establishes storage during construction; it does
        // not make the location writable to an ordinary caller or mutable borrow.
        if (field.PropHasSet || field.PropMutWritable)
            capability |= PropertyCapability.Writable;
        if (field.PropHasCustomSetter) capability |= PropertyCapability.CustomSetter;

        EmitPropertyCapability(module, property, capability);
    }

    static void EmitPropertyCapability(
        ModuleDefinition module, PropertyDefinition property, PropertyCapability capability)
    {

        // The late pass can revisit a property whose shell was declared before
        // user setter bodies; keep exactly one authoritative capability record.
        if (property.CustomAttributes.Any(a => a.AttributeType.Name == PropertyCapabilityAttributeName))
            return;

        var attrType = module.Types.FirstOrDefault(t => t.Name == PropertyCapabilityAttributeName);
        if (attrType is null)
        {
            attrType = new TypeDefinition("Esharp.Compiler", PropertyCapabilityAttributeName,
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit | TypeAttributes.Class,
                module.ImportReference(typeof(Attribute)));
            module.Types.Add(attrType);

            var ctor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.ImportReference(typeof(void)));
            ctor.Parameters.Add(new ParameterDefinition("capabilities", ParameterAttributes.None,
                module.TypeSystem.Int32));
            var il = new ILBuilder(ctor);
            il.LoadArgByIndex(0);
            il.Call(module.ImportReference(typeof(Attribute).GetConstructor(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                binder: null, Type.EmptyTypes, modifiers: null)!));
            il.Return();
            attrType.Methods.Add(ctor);
        }

        var constructor = attrType.Methods.Single(m => m.IsConstructor && m.Parameters.Count == 1);
        var attribute = new CustomAttribute(module.ImportReference(constructor));
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.Int32, (int)capability));
        property.CustomAttributes.Add(attribute);
    }

    // A property's accessors carry the property's own visibility (`pub`/`priv`/internal); the
    // backing field stays private regardless. `priv` is a real CLR private, distinct from bare.
    static MethodAttributes AccessFor(BoundField field) => field.Vis switch
    {
        Esharp.Syntax.Visibility.Public => MethodAttributes.Public,
        Esharp.Syntax.Visibility.Private => MethodAttributes.Private,
        _ => MethodAttributes.Assembly,
    };

    static void SetAccess(MethodDefinition m, MethodAttributes access) =>
        m.Attributes = (m.Attributes & ~MethodAttributes.MemberAccessMask) | access;

    // An interface property requirement (`let x: T { get }` / `var x: T { get set }` /
    // `required let x: T { get init }`) emits abstract `get_`/`set_` accessor slots plus a
    // PropertyDefinition — the same shape ILEmitter.Event.cs gives an interface event's
    // add_/remove_. The init setter carries `modreq(IsExternalInit)`, exactly as a
    // `required let x { }` property's init setter does, so the two slots line up.
    static void EmitInterfacePropertyMember(ModuleDefinition module, ILTypeResolver types, TypeDefinition ifaceDef, BoundInterfaceProperty p)
    {
        var propType = types.Resolve(p.Type);
        const MethodAttributes accessorAttrs = MethodAttributes.Public | MethodAttributes.Virtual
            | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Abstract | MethodAttributes.SpecialName;

        MethodDefinition? getter = null;
        MethodDefinition? setter = null;
        MethodDefinition? location = null;
        if (p.HasGet)
        {
            getter = new MethodDefinition("get_" + p.Name, accessorAttrs, propType);
            ifaceDef.Methods.Add(getter);
        }
        if (p.HasSet || p.HasInit)
        {
            var voidRef = module.ImportReference(typeof(void));
            TypeReference setterReturn = p.HasInit
                ? new RequiredModifierType(
                    module.ImportReference(typeof(System.Runtime.CompilerServices.IsExternalInit)), voidRef)
                : voidRef;
            setter = new MethodDefinition("set_" + p.Name, accessorAttrs, setterReturn);
            setter.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, propType));
            ifaceDef.Methods.Add(setter);
        }

        if (p.HasLoca)
        {
            location = new MethodDefinition("getloca_" + p.Name, accessorAttrs,
                new ByReferenceType(propType));
            ifaceDef.Methods.Add(location);
        }

        var property = new PropertyDefinition(p.Name, PropertyAttributes.None, propType)
        {
            HasThis = true,
            GetMethod = getter,
            SetMethod = setter,
        };
        ifaceDef.Properties.Add(property);
        var capability = PropertyCapability.None;
        if (p.HasLoca) capability |= PropertyCapability.DurableLocation;
        if (p.HasSet) capability |= PropertyCapability.Writable;
        EmitPropertyCapability(module, property, capability);
    }

    // When a synthesized accessor fills an interface property slot — its `get_<name>` /
    // `set_<name>` name is in the type's interface method map — mark it `virtual newslot`
    // and add the MethodImpl override(s), the same wiring the instance-method pass does for
    // a method that implements an interface method (ILEmitter.cs). Without this a stored
    // property would carry the InterfaceImplementation but leave the slot empty,
    // and the CLR would reject the type at load.
    static void WireInterfaceAccessor(MethodDefinition accessor, IReadOnlyDictionary<string, List<MethodReference>> ifaceMethodsByName)
    {
        if (!ifaceMethodsByName.TryGetValue(accessor.Name, out var slots)) return;
        accessor.IsVirtual = true;
        accessor.IsNewSlot = true;
        foreach (var slot in slots)
            if (slot.Parameters.Count == accessor.Parameters.Count && ReturnTypesCompatible(slot.ReturnType, accessor.ReturnType))
                accessor.Overrides.Add(slot);
    }

    static void EmitPropertyDefinitions(ModuleDefinition module, TypeDefinition typeDef, BoundDataDeclaration data,
        IReadOnlyDictionary<string, List<MethodReference>> ifaceMethodsByName)
    {
        foreach (var field in data.Fields)
        {
            if (field.IsEmbedded || field.IsEvent) continue;
            // Stored auto properties are declared with their backing/accessors while
            // type members are populated, before cross-type method bodies emit.
            // Do not duplicate that metadata in the late accessor pass.
            if (field.IsProperty && typeDef.Properties.FirstOrDefault(p => p.Name == field.Name) is { } declared)
            {
                // Stored accessor declarations land before user/synthesized method
                // signatures. A custom `set(v)` is emitted in that later method
                // pass, so attach it here rather than declaring a duplicate setter.
                if (field.PropHasCustomSetter)
                {
                    var custom = typeDef.Methods.FirstOrDefault(m =>
                        m.Name == "set_" + field.Name && !m.IsStatic && m.Parameters.Count == 1);
                    if (custom is not null)
                    {
                        custom.IsSpecialName = true;
                        custom.IsHideBySig = true;
                        SetAccess(custom, AccessFor(field));
                        declared.SetMethod = custom;
                        WireInterfaceAccessor(custom, ifaceMethodsByName);
                    }
                }
                if (declared.GetMethod is { } existingGetter)
                    WireInterfaceAccessor(existingGetter, ifaceMethodsByName);
                if (declared.SetMethod is { } existingSetter)
                    WireInterfaceAccessor(existingSetter, ifaceMethodsByName);
                if (typeDef.Methods.FirstOrDefault(method =>
                        method.Name == "getloca_" + field.Name && method.Parameters.Count == 0) is { } existingLoca)
                    WireInterfaceAccessor(existingLoca, ifaceMethodsByName);
                EmitPropertyCapability(module, declared, field);
                continue;
            }
            if (!field.IsProperty) continue;

            MethodDefinition getter;
            MethodDefinition? setter = null;

            if (field.IsComputedProperty)
            {
                // Getter already emitted as the `get_<name>` method (parser-synthesized body).
                var existing = typeDef.Methods.FirstOrDefault(m =>
                    m.Name == "get_" + field.Name && !m.IsStatic && m.Parameters.Count == 0);
                if (existing is null) continue;
                existing.IsSpecialName = true;
                existing.IsHideBySig = true;
                SetAccess(existing, AccessFor(field));
                getter = existing;
                if (field.PropHasCustomSetter)
                {
                    var custom = typeDef.Methods.FirstOrDefault(m =>
                        m.Name == "set_" + field.Name && !m.IsStatic && m.Parameters.Count == 1);
                    if (custom is not null)
                    {
                        custom.IsSpecialName = true;
                        custom.IsHideBySig = true;
                        SetAccess(custom, AccessFor(field));
                        setter = custom;
                        WireInterfaceAccessor(custom, ifaceMethodsByName);
                    }
                }
                WireInterfaceAccessor(getter, ifaceMethodsByName);
            }
            else
            {
                // Stored property: synthesize trivial accessors over the backing field.
                var backing = typeDef.Fields.FirstOrDefault(f => f.Name == PropertyBackingFieldName(field.Name));
                if (backing is null) continue;
                var accessorAttrs = AccessFor(field) | MethodAttributes.HideBySig | MethodAttributes.SpecialName;

                getter = new MethodDefinition("get_" + field.Name, accessorAttrs, backing.FieldType);
                getter.Body.InitLocals = true;
                var gil = new ILBuilder(getter);
                gil.LoadArgByIndex(0);
                gil.LoadField(backing);
                gil.Return();
                typeDef.Methods.Add(getter);
                WireInterfaceAccessor(getter, ifaceMethodsByName);

                if (field.PropHasSet || field.PropHasInit)
                {
                    // A custom `set(v) => …` setter was lowered to a real `set_<name>` method
                    // (PropertyLowering); prefer it as the SetMethod over a trivial accessor.
                    // Its body writes the backing field through the in-type `self.x` store.
                    var custom = typeDef.Methods.FirstOrDefault(m =>
                        m.Name == "set_" + field.Name && !m.IsStatic && m.Parameters.Count == 1);
                    if (custom is not null)
                    {
                        custom.IsSpecialName = true;
                        custom.IsHideBySig = true;
                        SetAccess(custom, AccessFor(field));
                        setter = custom;
                    }
                    else
                    {
                        var voidRef = module.ImportReference(typeof(void));
                        // An init-only setter is a `set_X` whose return type carries a
                        // `modreq(IsExternalInit)` — that is exactly how C# encodes `init`.
                        TypeReference setterReturn = field.PropHasInit
                            ? new RequiredModifierType(
                                module.ImportReference(typeof(System.Runtime.CompilerServices.IsExternalInit)), voidRef)
                            : voidRef;
                        setter = new MethodDefinition("set_" + field.Name, accessorAttrs, setterReturn);
                        setter.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, backing.FieldType));
                        setter.Body.InitLocals = true;
                        var sil = new ILBuilder(setter);
                        sil.LoadArgByIndex(0);
                        sil.LoadArgByIndex(1);
                        sil.StoreField(backing);
                        sil.Return();
                        typeDef.Methods.Add(setter);
                        WireInterfaceAccessor(setter, ifaceMethodsByName);
                    }
                }
            }

            var prop = new PropertyDefinition(field.Name, PropertyAttributes.None, getter.ReturnType)
            {
                HasThis = true,
                GetMethod = getter,
                SetMethod = setter,
            };
            typeDef.Properties.Add(prop);
            EmitPropertyCapability(module, prop, field);
        }
    }

}
