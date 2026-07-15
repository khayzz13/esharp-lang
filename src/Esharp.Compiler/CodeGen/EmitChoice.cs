using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Emit;
using Esharp.BoundTree;
using Esharp.Diagnostics;

namespace Esharp.CodeGen;

public static partial class CodeGenerator
{

    /// Phase 1 (shell): tag enum (with its literal cases) + the value-choice struct
    /// shell (Tag field + generic parameters), registered. Payload fields and factory
    /// methods — which resolve user payload types — are deferred to phase 2 so a
    /// payload that names a not-yet-declared type still resolves (forward references).
    static (TypeDefinition typeDef, TypeDefinition tagEnum, FieldDefinition tagField) DeclareChoiceStructShell(
        ModuleDefinition module, ILTypeResolver types, BoundChoiceDeclaration choice, string ns)
    {
        // Tag enum: ChoiceName_Tag { case0, case1, ... }
        var tagEnumName = $"{choice.Name}_Tag";
        var tagEnum = new TypeDefinition(
            ns,
            tagEnumName,
            (choice.IsPublic ? TypeAttributes.Public : TypeAttributes.NotPublic) | TypeAttributes.Sealed,
            module.ImportReference(typeof(Enum)));

        tagEnum.Fields.Add(new FieldDefinition(
            "value__",
            FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName,
            module.ImportReference(typeof(int))));

        for (var i = 0; i < choice.Cases.Count; i++)
        {
            tagEnum.Fields.Add(new FieldDefinition(
                choice.Cases[i].Name,
                FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal,
                tagEnum) { Constant = i });
        }

        module.Types.Add(tagEnum);
        // Register the tag enum in the resolver's defined-type table, exactly like the
        // main struct below. Without this, MatchLowering's references to the synthesized
        // `<Choice>_Tag` (the tag comparison + `<Choice>_Tag.case` constant) resolve to
        // `object` and the match emits as undefined-variable / bad IL.
        types.Register(tagEnumName, tagEnum);

        // Main struct: Tag field + payload fields. A generic `choice Option<T>`
        // is reified as a generic struct `Option<T>` (CLR reified generics), so
        // payload types stay precise (`Option<int>::some_value : int`) instead of
        // erasing to object.
        var typeDef = new TypeDefinition(
            ns,
            MetadataTypeName(choice.Name, choice.TypeParameters.Count),
            (choice.IsPublic ? TypeAttributes.Public : TypeAttributes.NotPublic) | TypeAttributes.Sealed | TypeAttributes.AnsiClass |
            TypeAttributes.BeforeFieldInit | TypeAttributes.SequentialLayout,
            module.ImportReference(typeof(ValueType)));

        foreach (var tp in choice.TypeParameters)
            typeDef.GenericParameters.Add(new GenericParameter(tp, typeDef));

        var tagField = new FieldDefinition("Tag", FieldAttributes.Public, tagEnum);
        typeDef.Fields.Add(tagField);

        module.Types.Add(typeDef);
        types.Register(choice.Name, typeDef);

        return (typeDef, tagEnum, tagField);
    }

    /// Phase 2 (members): payload fields + factory methods, resolving each payload
    /// type against the fully-registered type table.
    static Dictionary<string, FieldDefinition> PopulateChoiceStructMembers(
        ModuleDefinition module, ILTypeResolver types, BoundChoiceDeclaration choice,
        TypeDefinition typeDef, FieldDefinition tagField)
    {
        var isGeneric = typeDef.HasGenericParameters;
        if (isGeneric)
            types.PushGenericContext(typeDef.GenericParameters);

        // The struct closed over its own parameters (`Option<!0>`) — factory bodies
        // operate on this, and member references (fields) are hosted on it so the
        // CLR resolves them on the instantiated type.
        TypeReference selfRef = typeDef;
        if (isGeneric)
        {
            var selfGit = new GenericInstanceType(typeDef);
            foreach (var gp in typeDef.GenericParameters) selfGit.GenericArguments.Add(gp);
            selfRef = selfGit;
        }
        FieldReference FieldOn(FieldDefinition fd) =>
            isGeneric ? new FieldReference(fd.Name, fd.FieldType, selfRef) : fd;

        var fieldMap = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal) { ["Tag"] = tagField };

        foreach (var c in choice.Cases)
        {
            foreach (var p in c.Payloads)
            {
                var payloadFieldType = types.Resolve(p.Type);
                var payloadField = new FieldDefinition($"{c.Name}_{p.Name}", FieldAttributes.Public, payloadFieldType);
                typeDef.Fields.Add(payloadField);
                fieldMap[$"{c.Name}_{p.Name}"] = payloadField;
            }
        }

        // Factory methods: static ChoiceName caseName(payloads...)
        for (var i = 0; i < choice.Cases.Count; i++)
        {
            var c = choice.Cases[i];
            var factory = new MethodDefinition(
                c.Name,
                MethodAttributes.Public | MethodAttributes.Static,
                selfRef);

            foreach (var p in c.Payloads)
                factory.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, types.Resolve(p.Type)));

            factory.Body.InitLocals = true;
            var il = new ILBuilder(factory);
            var resultVar = new VariableDefinition(selfRef);
            factory.Body.Variables.Add(resultVar);

            // initobj result
            il.LoadLocalAddress(resultVar);
            il.InitObj(selfRef);

            // result.Tag = i
            il.LoadLocalAddress(resultVar);
            il.LoadInt(i);
            il.StoreField(FieldOn(tagField));

            // result.payloads = args
            for (var pi = 0; pi < c.Payloads.Count; pi++)
            {
                var p = c.Payloads[pi];
                if (fieldMap.TryGetValue($"{c.Name}_{p.Name}", out var payloadField))
                {
                    il.LoadLocalAddress(resultVar);
                    il.LoadArgByIndex(pi);
                    il.StoreField(FieldOn(payloadField));
                }
            }

            il.LoadLocal(resultVar);
            il.Return();

            ILOptimizer.ShortenOpcodes(factory.Body);
            typeDef.Methods.Add(factory);
        }

        if (isGeneric)
            types.PopGenericContext();

        return fieldMap;
    }

    /// Phase 1 (shell): the abstract base class (with its protected parameterless
    /// constructor) plus one empty sealed subclass per case, all registered. Payload
    /// fields and per-case constructors — which resolve user payload types — are
    /// deferred to phase 2, so a payload naming a not-yet-declared type still resolves.
    static (TypeDefinition baseDef, MethodDefinition baseCtor, Dictionary<string, TypeDefinition> subs) DeclareRefChoiceShell(
        ModuleDefinition module, ILTypeResolver types, BoundChoiceDeclaration choice, string ns)
    {
        // Abstract base class. A generic `ref union Box<T>` is REIFIED: the base is
        // `Box`1<T>` and each per-case subclass is `Box_full`1<T> : Box`1<T>` with its own
        // copy of the parameters — so a payload `T` stays `T`, never erasing to `object`.
        var arity = choice.TypeParameters.Count;
        var baseDef = new TypeDefinition(
            ns,
            MetadataTypeName(choice.Name, arity),
            (choice.IsPublic ? TypeAttributes.Public : TypeAttributes.NotPublic) | TypeAttributes.Abstract | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit,
            module.ImportReference(typeof(object)));
        foreach (var tp in choice.TypeParameters)
            baseDef.GenericParameters.Add(new GenericParameter(tp, baseDef));

        // Base class parameterless constructor
        var baseCtor = new MethodDefinition(
            ".ctor",
            MethodAttributes.Family | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.ImportReference(typeof(void)));
        var baseIl = new ILBuilder(baseCtor);
        baseIl.LoadArgByIndex(0);
        baseIl.Call(module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
        baseIl.Return();
        baseDef.Methods.Add(baseCtor);

        module.Types.Add(baseDef);
        types.Register(choice.Name, baseDef, arity: arity);

        var subs = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var c in choice.Cases)
        {
            var subName = $"{choice.Name}_{c.Name}";
            var subDef = new TypeDefinition(
                ns,
                arity > 0 ? MetadataTypeName(subName, arity) : subName,
                (choice.IsPublic ? TypeAttributes.Public : TypeAttributes.NotPublic) | TypeAttributes.Sealed | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit,
                module.ImportReference(typeof(object)));
            // The subclass re-declares the union's parameters and extends the base closed
            // over them: `Box_full`1<T> : Box`1<T>`.
            foreach (var tp in choice.TypeParameters)
                subDef.GenericParameters.Add(new GenericParameter(tp, subDef));
            if (arity > 0)
            {
                var baseClosed = new GenericInstanceType(baseDef);
                foreach (var gp in subDef.GenericParameters) baseClosed.GenericArguments.Add(gp);
                subDef.BaseType = baseClosed;
            }
            else
            {
                subDef.BaseType = baseDef;
            }
            module.Types.Add(subDef);
            types.Register(subName, subDef, arity: arity);
            subs[c.Name] = subDef;
        }

        return (baseDef, baseCtor, subs);
    }

    /// Phase 2 (members): payload fields + per-case constructors on each sealed
    /// subclass, resolving each payload type against the fully-registered type table.
    static Dictionary<string, FieldDefinition> PopulateRefChoiceMembers(
        ModuleDefinition module, ILTypeResolver types, BoundChoiceDeclaration choice,
        MethodDefinition baseCtor, Dictionary<string, TypeDefinition> subs)
    {
        var fieldMap = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);

        foreach (var c in choice.Cases)
        {
            var subDef = subs[c.Name];
            var isGeneric = subDef.HasGenericParameters;
            // Resolve payload types under the SUBCLASS's own type parameters so a payload `T`
            // binds to the reified `Box_full`1`'s `T`, never erasing to `object`.
            if (isGeneric)
                types.PushGenericContext(subDef.GenericParameters);

            // The base ctor reference: on a generic union it is hosted on `Box`1<subT>` so the
            // `call base..ctor()` resolves on the closed base instance.
            MethodReference baseCtorRef = baseCtor;
            if (isGeneric)
            {
                var baseClosed = (GenericInstanceType)subDef.BaseType;
                baseCtorRef = new MethodReference(".ctor", module.ImportReference(typeof(void)), baseClosed) { HasThis = true };
            }

            // Add payload fields
            var subFields = new List<FieldDefinition>();
            foreach (var p in c.Payloads)
            {
                var pType = types.Resolve(p.Type);
                var pField = new FieldDefinition(p.Name, FieldAttributes.Public, pType);
                subDef.Fields.Add(pField);
                subFields.Add(pField);
                fieldMap[$"{c.Name}_{p.Name}"] = pField;
            }

            // Always emit parameterless constructor (needed for object literal init)
            {
                var defaultCtor = new MethodDefinition(
                    ".ctor",
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    module.ImportReference(typeof(void)));
                var il = new ILBuilder(defaultCtor);
                il.LoadArgByIndex(0);
                il.Call(baseCtorRef);
                il.Return();
                ILOptimizer.ShortenOpcodes(defaultCtor.Body);
                subDef.Methods.Add(defaultCtor);
            }

            // Also emit parameterized constructor if there are payloads
            if (c.Payloads.Count > 0)
            {
                var ctor = new MethodDefinition(
                    ".ctor",
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    module.ImportReference(typeof(void)));
                foreach (var p in c.Payloads)
                    ctor.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, types.Resolve(p.Type)));
                var il = new ILBuilder(ctor);
                il.LoadArgByIndex(0);
                il.Call(baseCtorRef);
                for (var pi = 0; pi < c.Payloads.Count; pi++)
                {
                    il.LoadArgByIndex(0);
                    il.LoadArgByIndex(pi + 1);
                    // On a generic subclass the field store targets the field on the closed
                    // self-instance `Box_full`1<T>` so the token carries the reified type.
                    il.StoreField(SelfField(subFields[pi], SelfInstantiation(subDef)));
                }
                il.Return();
                ILOptimizer.ShortenOpcodes(ctor.Body);
                subDef.Methods.Add(ctor);
            }

            if (isGeneric)
                types.PopGenericContext();
        }

        return fieldMap;
    }
}
