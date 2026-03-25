using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Compiler.Binding;

namespace Esharp.ILEmit;

public static class ILEmitter
{
    public static AssemblyDefinition Emit(BoundCompilationUnit unit, string assemblyName)
    {
        // Detect entry point: func main() → Console executable
        var hasMain = unit.Members.OfType<BoundFunctionDeclaration>().Any(f => f.Name == "main");
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(assemblyName, new Version(1, 0)),
            assemblyName,
            hasMain ? ModuleKind.Console : ModuleKind.Dll);

        var module = assembly.MainModule;
        var types = new ILTypeResolver(module);

        // Pass 1: emit data structs (so fields are available for method emission)
        var structFieldMaps = new Dictionary<string, Dictionary<string, FieldDefinition>>(StringComparer.Ordinal);

        var choiceFieldMaps = new Dictionary<string, Dictionary<string, FieldDefinition>>(StringComparer.Ordinal);

        foreach (var member in unit.Members)
        {
            if (member is BoundDataDeclaration data)
            {
                var (typeDef, fields) = EmitDataStruct(module, types, data);
                structFieldMaps[data.Name] = fields;
            }
            else if (member is BoundChoiceDeclaration choice)
            {
                var (typeDef, fields) = EmitChoiceStruct(module, types, choice);
                choiceFieldMaps[choice.Name] = fields;
            }
            else if (member is BoundEnumDeclaration e)
            {
                EmitEnum(module, types, e);
            }
        }

        // Pass 2: emit functions as static methods on a module class
        var staticFunctions = unit.Members.OfType<BoundFunctionDeclaration>().ToList();
        if (staticFunctions.Count > 0)
        {
            var moduleName = string.IsNullOrWhiteSpace(unit.ModuleName) ? "Main" : unit.ModuleName!;
            var moduleClass = new TypeDefinition(
                "Esharp.Generated",
                moduleName,
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                module.ImportReference(typeof(object)));

            module.Types.Add(moduleClass);

            // Pre-register all method signatures so recursive/forward calls resolve
            var methodDefs = new List<(BoundFunctionDeclaration func, MethodDefinition method)>();
            foreach (var func in staticFunctions)
            {
                var returnType = types.Resolve(func.ReturnType);
                var method = new MethodDefinition(
                    func.Name,
                    MethodAttributes.Public | MethodAttributes.Static,
                    returnType);

                foreach (var param in func.Parameters)
                    method.Parameters.Add(new ParameterDefinition(param.Name, ParameterAttributes.None, types.Resolve(param.Type)));

                method.Body.InitLocals = true;
                moduleClass.Methods.Add(method);
                methodDefs.Add((func, method));
            }

            // Now emit bodies (all methods already registered, so FindMethod works)
            var asyncEmitter = new ILAsyncEmitter(module, types, structFieldMaps);
            foreach (var (func, method) in methodDefs)
            {
                if (func.HasAwait)
                    asyncEmitter.EmitAsyncFunction(method, func, moduleClass);
                else
                    EmitFunctionBody(module, types, method, func, structFieldMaps);
            }

            // Set entry point for func main()
            if (hasMain)
            {
                var mainFunc = methodDefs.FirstOrDefault(m => m.func.Name == "main");
                if (mainFunc.method is not null)
                {
                    if (mainFunc.func.HasAwait)
                    {
                        // Async main: create a void wrapper that calls main().GetAwaiter().GetResult()
                        var wrapper = new MethodDefinition(
                            "<Main>",
                            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                            module.ImportReference(typeof(void)));
                        wrapper.Body.InitLocals = true;
                        var il = wrapper.Body.GetILProcessor();

                        // Store ValueTask to local, then call GetAwaiter on its address (value type)
                        var vtLocal = new VariableDefinition(mainFunc.method.ReturnType);
                        wrapper.Body.Variables.Add(vtLocal);

                        il.Emit(OpCodes.Call, mainFunc.method);
                        il.Emit(OpCodes.Stloc, vtLocal);
                        il.Emit(OpCodes.Ldloca, vtLocal);

                        // ValueTask.GetAwaiter()
                        var getAwaiter = typeof(System.Threading.Tasks.ValueTask)
                            .GetMethod("GetAwaiter", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, Type.EmptyTypes)!;
                        var awaiterType = getAwaiter.ReturnType;

                        var awaiterLocal = new VariableDefinition(module.ImportReference(awaiterType));
                        wrapper.Body.Variables.Add(awaiterLocal);

                        il.Emit(OpCodes.Call, module.ImportReference(getAwaiter));
                        il.Emit(OpCodes.Stloc, awaiterLocal);
                        il.Emit(OpCodes.Ldloca, awaiterLocal);

                        // ValueTaskAwaiter.GetResult()
                        var getResult = awaiterType.GetMethod("GetResult",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, Type.EmptyTypes)!;
                        il.Emit(OpCodes.Call, module.ImportReference(getResult));
                        il.Emit(OpCodes.Ret);

                        ILOptimizer.ShortenOpcodes(wrapper.Body);
                        moduleClass.Methods.Add(wrapper);
                        assembly.EntryPoint = wrapper;
                    }
                    else
                    {
                        assembly.EntryPoint = mainFunc.method;
                    }
                }
            }
        }

        return assembly;
    }

    public static void EmitToFile(BoundCompilationUnit unit, string assemblyName, string outputPath)
    {
        using var assembly = Emit(unit, assemblyName);
        assembly.Write(outputPath);
    }

    static (TypeDefinition typeDef, Dictionary<string, FieldDefinition> fields) EmitDataStruct(
        ModuleDefinition module, ILTypeResolver types, BoundDataDeclaration data)
    {
        var typeDef = new TypeDefinition(
            "Esharp.Generated",
            data.Name,
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.AnsiClass |
            TypeAttributes.BeforeFieldInit | TypeAttributes.SequentialLayout,
            module.ImportReference(typeof(ValueType)));

        var fieldMap = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);

        foreach (var field in data.Fields)
        {
            var fieldType = types.Resolve(field.Type);
            var fieldDef = new FieldDefinition(field.Name, FieldAttributes.Public, fieldType);
            typeDef.Fields.Add(fieldDef);
            fieldMap[field.Name] = fieldDef;
        }

        module.Types.Add(typeDef);
        types.Register(data.Name, typeDef);

        return (typeDef, fieldMap);
    }

    static (TypeDefinition typeDef, Dictionary<string, FieldDefinition> fields) EmitChoiceStruct(
        ModuleDefinition module, ILTypeResolver types, BoundChoiceDeclaration choice)
    {
        // Tag enum: ChoiceName_Tag { case0, case1, ... }
        var tagEnumName = $"{choice.Name}_Tag";
        var tagEnum = new TypeDefinition(
            "Esharp.Generated",
            tagEnumName,
            TypeAttributes.Public | TypeAttributes.Sealed,
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

        // Main struct: Tag field + payload fields
        var typeDef = new TypeDefinition(
            "Esharp.Generated",
            choice.Name,
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.AnsiClass |
            TypeAttributes.BeforeFieldInit | TypeAttributes.SequentialLayout,
            module.ImportReference(typeof(ValueType)));

        var tagField = new FieldDefinition("Tag", FieldAttributes.Public, tagEnum);
        typeDef.Fields.Add(tagField);

        var fieldMap = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal) { ["Tag"] = tagField };

        foreach (var c in choice.Cases)
        {
            if (c.PayloadType is not null)
            {
                var payloadFieldType = types.Resolve(c.PayloadType);
                var payloadField = new FieldDefinition(c.Name, FieldAttributes.Public, payloadFieldType);
                typeDef.Fields.Add(payloadField);
                fieldMap[c.Name] = payloadField;
            }
        }

        // Factory methods: static ChoiceName caseName(payloadType? value)
        for (var i = 0; i < choice.Cases.Count; i++)
        {
            var c = choice.Cases[i];
            var factory = new MethodDefinition(
                c.Name,
                MethodAttributes.Public | MethodAttributes.Static,
                typeDef);

            if (c.PayloadType is not null)
                factory.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, types.Resolve(c.PayloadType)));

            factory.Body.InitLocals = true;
            var il = factory.Body.GetILProcessor();
            var resultVar = new VariableDefinition(typeDef);
            factory.Body.Variables.Add(resultVar);

            // initobj result
            il.Emit(OpCodes.Ldloca, resultVar);
            il.Emit(OpCodes.Initobj, typeDef);

            // result.Tag = i
            il.Emit(OpCodes.Ldloca, resultVar);
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Stfld, tagField);

            // result.payload = value (if has payload)
            if (c.PayloadType is not null && fieldMap.TryGetValue(c.Name, out var payloadField))
            {
                il.Emit(OpCodes.Ldloca, resultVar);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Stfld, payloadField);
            }

            il.Emit(OpCodes.Ldloc, resultVar);
            il.Emit(OpCodes.Ret);

            ILOptimizer.ShortenOpcodes(factory.Body);
            typeDef.Methods.Add(factory);
        }

        module.Types.Add(typeDef);
        types.Register(choice.Name, typeDef);

        return (typeDef, fieldMap);
    }

    static void EmitEnum(ModuleDefinition module, ILTypeResolver types, BoundEnumDeclaration e)
    {
        var enumType = new TypeDefinition(
            "Esharp.Generated",
            e.Name,
            TypeAttributes.Public | TypeAttributes.Sealed,
            module.ImportReference(typeof(Enum)));

        // Enums need a special "value__" field
        enumType.Fields.Add(new FieldDefinition(
            "value__",
            FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName,
            module.ImportReference(typeof(int))));

        for (var i = 0; i < e.Cases.Count; i++)
        {
            var caseField = new FieldDefinition(
                e.Cases[i],
                FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal,
                enumType)
            {
                Constant = i
            };
            enumType.Fields.Add(caseField);
        }

        module.Types.Add(enumType);
        types.Register(e.Name, enumType);
    }

    static void EmitFunctionBody(
        ModuleDefinition module, ILTypeResolver types, MethodDefinition method,
        BoundFunctionDeclaration func, Dictionary<string, Dictionary<string, FieldDefinition>> structFieldMaps)
    {
        // Collect all struct fields accessible from parameters
        var allFields = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        foreach (var param in func.Parameters)
        {
            var typeName = param.Type switch
            {
                DataType dt => dt.Name,
                ExternalType et => et.Name,
                _ => null,
            };
            if (typeName is not null && structFieldMaps.TryGetValue(typeName, out var fields))
            {
                foreach (var (name, field) in fields)
                    allFields.TryAdd(name, field);
            }
        }

        var emitter = new ILMethodEmitter(method, types, allFields);
        emitter.EmitBlock(func.Body);

        // Ensure method ends with ret
        var instructions = method.Body.Instructions;
        if (instructions.Count == 0 || instructions[^1].OpCode != OpCodes.Ret)
            method.Body.GetILProcessor().Emit(OpCodes.Ret);

        ILOptimizer.ShortenOpcodes(method.Body);
    }
}
