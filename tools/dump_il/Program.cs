using Mono.Cecil;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dump_il <path-to.dll> [type] [method]");
    Console.Error.WriteLine("       dump_il <path-to.dll> --meta   (assembly name, references, assembly-level attributes)");
    return 1;
}

if (args.Length >= 2 && args[1] == "--meta")
{
    using var asm = AssemblyDefinition.ReadAssembly(args[0]);
    Console.WriteLine($"=== assembly: {asm.Name.FullName} ===");
    Console.WriteLine("-- references --");
    foreach (var r in asm.MainModule.AssemblyReferences.OrderBy(r => r.FullName, StringComparer.Ordinal))
        Console.WriteLine($"   {r.FullName}");
    Console.WriteLine("-- assembly attributes --");
    foreach (var a in asm.CustomAttributes)
    {
        var args2 = string.Join(", ", a.ConstructorArguments.Select(c => c.Value is string s ? $"\"{s}\"" : c.Value?.ToString() ?? "null"));
        Console.WriteLine($"   [{a.AttributeType.FullName}({args2})]");
    }
    return 0;
}

var typeFilter = args.Length >= 2 ? args[1] : null;
var methodFilter = args.Length >= 3 ? args[2] : null;

using var module = ModuleDefinition.ReadModule(args[0]);
foreach (var type in module.Types)
{
    if (typeFilter is not null && !type.Name.Contains(typeFilter, StringComparison.Ordinal))
        continue;
    Console.WriteLine($"=== {type.FullName} ===");
    foreach (var iface in type.Interfaces)
        Console.WriteLine($"  : {iface.InterfaceType.FullName}");
    foreach (var nested in type.NestedTypes)
        DumpType(nested, "  ", methodFilter);
    DumpType(type, "", methodFilter);
}

return 0;

static void DumpType(TypeDefinition type, string indent, string? methodFilter)
{
    foreach (var method in type.Methods)
    {
        if (methodFilter is not null && !method.Name.Contains(methodFilter, StringComparison.Ordinal))
            continue;
        Console.WriteLine($"{indent}-- {method.FullName}");
        foreach (var a in method.CustomAttributes)
        {
            var aa = string.Join(", ", a.ConstructorArguments.Select(c => c.Value is string s ? $"\"{s}\"" : c.Value?.ToString() ?? "null"));
            Console.WriteLine($"{indent}   [{a.AttributeType.FullName}({aa})]");
        }
        if (!method.HasBody) continue;
        foreach (var v in method.Body.Variables)
            Console.WriteLine($"{indent}   .local [{v.Index}] {v.VariableType}");
        if (method.Body.HasExceptionHandlers)
        {
            foreach (var eh in method.Body.ExceptionHandlers)
                Console.WriteLine($"{indent}   .{eh.HandlerType.ToString().ToLower()} try [{eh.TryStart?.Offset:X4}..{eh.TryEnd?.Offset:X4}] handler [{eh.HandlerStart?.Offset:X4}..{eh.HandlerEnd?.Offset:X4}] type {eh.CatchType}");
        }
        foreach (var instr in method.Body.Instructions)
            Console.WriteLine($"{indent}   IL_{instr.Offset:X4}: {instr.OpCode.Name,-12} {FormatOperand(instr.Operand)}");
    }
    foreach (var nested in type.NestedTypes)
        DumpType(nested, indent + "  ", methodFilter);
}

static string FormatOperand(object? op) => op switch
{
    null => "",
    Mono.Cecil.Cil.Instruction i => $"IL_{i.Offset:X4}",
    Mono.Cecil.Cil.Instruction[] arr => string.Join(",", arr.Select(i => $"IL_{i.Offset:X4}")),
    Mono.Cecil.Cil.VariableDefinition v => $"V_{v.Index}",
    Mono.Cecil.ParameterDefinition p => $"arg_{p.Index}({p.Name})",
    Mono.Cecil.GenericInstanceMethod gim =>
        $"{gim.FullName} [decl={FormatType(gim.DeclaringType)}; method-args={string.Join(", ", gim.GenericArguments.Select(FormatType))}]",
    Mono.Cecil.MethodReference m => $"{m.FullName} [decl={FormatType(m.DeclaringType)}]",
    Mono.Cecil.TypeReference t => FormatType(t),
    Mono.Cecil.FieldReference f => f.FullName,
    string s => $"\"{s}\"",
    _ => op.ToString() ?? "",
};

// `FullName` is intentionally compact, but generic-parameter identity is not
// name-based in CLR metadata. Keep the owner visible when diagnosing a call
// whose generic argument is a module-local state-machine type.
static string FormatType(TypeReference type) => type switch
{
    GenericParameter parameter =>
        $"{parameter.FullName} [owner={FormatOwner(parameter.Owner)}; pos={parameter.Position}]",
    GenericInstanceType instance =>
        $"{instance.FullName} [element={instance.ElementType.FullName}; args={string.Join(", ", instance.GenericArguments.Select(FormatType))}]",
    _ => type.FullName,
};

static string FormatOwner(IGenericParameterProvider owner) => owner switch
{
    TypeReference type => type.FullName,
    MethodReference method => method.FullName,
    _ => owner.ToString() ?? "<unknown>",
};
