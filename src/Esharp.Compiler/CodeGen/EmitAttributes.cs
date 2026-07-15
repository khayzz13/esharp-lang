using Mono.Cecil;
using Esharp.Binder;
using Esharp.Emit;
using Esharp.BoundTree;
using Esharp.Diagnostics;

namespace Esharp.CodeGen;

public static partial class CodeGenerator
{

    /// Stamp `[Optional]` + the `.param` constant onto an emitted parameter whose
    /// declared default folds to a literal, so a C# caller may omit it too.
    /// Non-literal constant shapes (composite/choice constructions) materialize
    /// only at E# call sites — the CLR constant table cannot carry them, so the
    /// parameter stays required on the C#-facing surface.
    static void ApplyDefaultValueFacts(ParameterDefinition paramDef, BoundParameter param)
    {
        if (param.DefaultValue is null) return;
        if (ConstantFolder.Fold(param.DefaultValue) is { } lit)
        {
            paramDef.Attributes |= ParameterAttributes.Optional | ParameterAttributes.HasDefault;
            paramDef.Constant = lit.Value;
        }
    }

    /// Resolve the base constructor a derived class's ctor chains to — for an
    /// in-compilation base (matched by parameter count, as the ctor table is the source
    /// of truth) or an external base (BCL / referenced-assembly class, via its runtime
    /// type). `NonPublic` is included so a protected base ctor — e.g. `Attribute()` —
    /// resolves. Null when there is no base or no ctor of that arity.
    static MethodReference? ResolveBaseCtor(ModuleDefinition module, ILTypeResolver types, string? baseName, int argCount)
    {
        if (baseName is null) return null;
        if (types.TryResolveRegistered(baseName)?.Resolve() is { } baseTd)
        {
            var ctor = baseTd.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == argCount);
            return ctor is null ? null : module.ImportReference(ctor);
        }
        if (types.TryResolveRuntimeType(baseName) is { } rt)
        {
            var ctor = rt.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters().Length == argCount);
            return ctor is null ? null : module.ImportReference(ctor);
        }
        return null;
    }

    /// `[Struct]` / `[Class]` pin the CLR form a value `data` lowers to. They are
    /// language directives consumed by the binder, never emitted to metadata.
    static bool IsFormPinAttribute(string attrText) =>
        SplitAttribute(attrText).Item1 is "Struct" or "Class";

    static void EmitClrAttributes(ModuleDefinition module, ILTypeResolver types, TypeDefinition typeDef, IReadOnlyList<string> attributes)
    {
        foreach (var attr in attributes)
        {
            if (IsFormPinAttribute(attr)) continue;
            var built = BuildCustomAttribute(module, types, attr);
            if (built is not null) typeDef.CustomAttributes.Add(built);
        }
    }

    static void EmitClrAttributes(ModuleDefinition module, ILTypeResolver types, MethodDefinition methodDef, IReadOnlyList<string> attributes)
    {
        foreach (var attr in attributes)
        {
            var built = BuildCustomAttribute(module, types, attr);
            if (built is not null) methodDef.CustomAttributes.Add(built);
        }
    }

    /// Emit the standard CLR link from an async entry point to the exact state-machine
    /// type minted by lowering. This cannot be represented as a source attribute string:
    /// its constructor argument is a metadata <see cref="Type"/> token, not a string.
    static void EmitAsyncStateMachineAttribute(ModuleDefinition module, ILTypeResolver types,
        MethodDefinition methodDef, BoundFunctionDeclaration function)
    {
        if (function.AsyncStateMachineType is null) return;

        var ctor = typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute)
            .GetConstructor([typeof(Type)]);
        if (ctor is null) return;

        var stateMachineType = types.Resolve(function.AsyncStateMachineType);
        // Custom-attribute `System.Type` values cannot serialize a generic parameter
        // (Cecil rejects a TypeSpec containing `T`). The method's signature still
        // carries the closed state-machine construction; metadata records the open
        // generic state-machine definition, as CLR async metadata does.
        if (stateMachineType.ContainsGenericParameter)
            stateMachineType = stateMachineType.GetElementType();

        var attribute = new CustomAttribute(module.ImportReference(ctor));
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(
            module.ImportReference(typeof(Type)), stateMachineType));
        methodDef.CustomAttributes.Add(attribute);
    }

    // Parses an attribute string like `"JsonPropertyName(\"user_id\")"` or `"Obsolete"` and
    // constructs a Cecil CustomAttribute. Supports string, int, bool, long, double, and
    // enum-like `Foo.Bar` literals. Returns null if the attribute type or ctor can't be resolved.
    static CustomAttribute? BuildCustomAttribute(ModuleDefinition module, ILTypeResolver types, string attrText)
    {
        var (name, argsText) = SplitAttribute(attrText);
        var attrTypeName = name.EndsWith("Attribute") ? name : name + "Attribute";
        // Resolve the attribute type through the same import-aware lookup every other
        // type name uses: the resolver consults the file's `using` namespaces, so
        // `[Fact]` under `using "Xunit"` finds `Xunit.FactAttribute` — exactly how
        // `Assert` resolves to `Xunit.Assert`. A bare `[Foo]` may also be the literal
        // type `Foo` (no `Attribute` suffix), so try the elided form too. The
        // hardcoded-namespace fallback stays for attributes referenced before the
        // resolver's import set is primed.
        var attrType = types.TryResolveRuntimeType(attrTypeName)
            ?? types.TryResolveRuntimeType(name)
            ?? Type.GetType($"System.{attrTypeName}")
            ?? Type.GetType(attrTypeName)
            ?? Type.GetType($"System.Runtime.CompilerServices.{attrTypeName}")
            ?? Type.GetType($"System.Text.Json.Serialization.{attrTypeName}")
            ?? Type.GetType($"System.Runtime.InteropServices.{attrTypeName}")
            ?? SearchAllAssembliesForAttribute(attrTypeName);
        if (attrType is null)
        {
            // The attribute type is defined in THIS compilation — it has no runtime
            // `System.Type` yet, only a Cecil `TypeDefinition`. Build the usage from the
            // emitted ctor so user attributes attach to metadata exactly as BCL ones do.
            var inModule = types.TryResolveRegistered(attrTypeName) ?? types.TryResolveRegistered(name);
            return inModule is not null ? BuildInModuleCustomAttribute(module, inModule, argsText) : null;
        }

        if (string.IsNullOrWhiteSpace(argsText))
        {
            var parameterless = attrType.GetConstructor(Type.EmptyTypes);
            if (parameterless is null) return null;
            return new CustomAttribute(module.ImportReference(parameterless));
        }

        var args = ParseAttributeArgs(argsText);
        if (args is null) return null;

        // Find a constructor matching the positional arg count + types (loose match).
        foreach (var ctor in attrType.GetConstructors())
        {
            var parameters = ctor.GetParameters();
            var positional = args.Where(a => !a.IsNamed).ToList();
            if (parameters.Length != positional.Count) continue;

            var runtimeArgs = new object?[positional.Count];
            var ok = true;
            for (var i = 0; i < positional.Count; i++)
            {
                if (!TryCoerceArg(positional[i].Value, parameters[i].ParameterType, out runtimeArgs[i]))
                {
                    ok = false;
                    break;
                }
            }
            if (!ok) continue;

            var customAttr = new CustomAttribute(module.ImportReference(ctor));
            for (var i = 0; i < positional.Count; i++)
            {
                var paramTypeRef = module.ImportReference(parameters[i].ParameterType);
                customAttr.ConstructorArguments.Add(new CustomAttributeArgument(paramTypeRef, runtimeArgs[i]));
            }

            // Named args (Name = Value)
            foreach (var named in args.Where(a => a.IsNamed))
            {
                var prop = attrType.GetProperty(named.Name!);
                if (prop is not null && TryCoerceArg(named.Value, prop.PropertyType, out var propVal))
                {
                    var propTypeRef = module.ImportReference(prop.PropertyType);
                    customAttr.Properties.Add(new CustomAttributeNamedArgument(named.Name!, new CustomAttributeArgument(propTypeRef, propVal)));
                }
            }

            return customAttr;
        }

        return null;
    }

    // Build a CustomAttribute for an attribute type defined in the current module (no
    // runtime System.Type). Matches the parsed positional args against the emitted
    // Cecil ctor's parameter types, reusing the same literal coercion as the runtime
    // path — only the parameter-type source differs (Cecil `ParameterDefinition` vs
    // reflection `ParameterInfo`). Returns null when no ctor matches.
    static CustomAttribute? BuildInModuleCustomAttribute(ModuleDefinition module, TypeDefinition attrDef, string argsText)
    {
        var ctors = attrDef.Methods.Where(m => m.IsConstructor && !m.IsStatic).ToList();

        if (string.IsNullOrWhiteSpace(argsText))
        {
            var parameterless = ctors.FirstOrDefault(c => c.Parameters.Count == 0);
            return parameterless is null ? null : new CustomAttribute(module.ImportReference(parameterless));
        }

        var args = ParseAttributeArgs(argsText);
        if (args is null) return null;
        var positional = args.Where(a => !a.IsNamed).ToList();

        foreach (var ctor in ctors)
        {
            if (ctor.Parameters.Count != positional.Count) continue;

            var runtimeArgs = new object?[positional.Count];
            var ok = true;
            for (var i = 0; i < positional.Count; i++)
            {
                // Attribute ctor params are blittable (string/numeric/bool/enum), so the
                // param type resolves to a runtime Type by full name for coercion.
                var paramRuntime = Type.GetType(ctor.Parameters[i].ParameterType.FullName ?? "");
                if (paramRuntime is null || !TryCoerceArg(positional[i].Value, paramRuntime, out runtimeArgs[i]))
                {
                    ok = false;
                    break;
                }
            }
            if (!ok) continue;

            var customAttr = new CustomAttribute(module.ImportReference(ctor));
            for (var i = 0; i < positional.Count; i++)
                customAttr.ConstructorArguments.Add(new CustomAttributeArgument(
                    module.ImportReference(ctor.Parameters[i].ParameterType), runtimeArgs[i]));

            foreach (var named in args.Where(a => a.IsNamed))
            {
                var prop = attrDef.Properties.FirstOrDefault(p => p.Name == named.Name);
                var propRuntime = prop is null ? null : Type.GetType(prop.PropertyType.FullName ?? "");
                if (prop is not null && propRuntime is not null && TryCoerceArg(named.Value, propRuntime, out var propVal))
                    customAttr.Properties.Add(new CustomAttributeNamedArgument(named.Name!,
                        new CustomAttributeArgument(module.ImportReference(prop.PropertyType), propVal)));
            }
            return customAttr;
        }
        return null;
    }

    static List<AttrArg>? ParseAttributeArgs(string argsText)
    {
        // Split on commas at depth 0, respecting string literals.
        var result = new List<AttrArg>();
        var depth = 0;
        var inString = false;
        var start = 0;
        for (var i = 0; i <= argsText.Length; i++)
        {
            var c = i < argsText.Length ? argsText[i] : ',';
            if (c == '"' && (i == 0 || argsText[i - 1] != '\\')) inString = !inString;
            else if (!inString && c == '(') depth++;
            else if (!inString && c == ')') depth--;
            else if (!inString && c == ',' && depth == 0)
            {
                var piece = argsText[start..i].Trim();
                if (piece.Length > 0)
                {
                    var eq = FindNamedArgEquals(piece);
                    if (eq > 0)
                        result.Add(new AttrArg(piece[..eq].Trim(), piece[(eq + 1)..].Trim(), true));
                    else
                        result.Add(new AttrArg(null, piece, false));
                }
                start = i + 1;
            }
        }
        return result;
    }

    static bool TryCoerceArg(string literal, Type targetType, out object? value)
    {
        value = null;
        literal = literal.Trim();

        if (targetType == typeof(string))
        {
            if (literal.Length >= 2 && literal[0] == '"' && literal[^1] == '"')
            {
                value = literal[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
                return true;
            }
            if (literal == "null") { value = null; return true; }
            return false;
        }

        if (targetType == typeof(int) || targetType == typeof(long)
            || targetType == typeof(short) || targetType == typeof(byte))
        {
            if (long.TryParse(literal, out var n))
            {
                value = targetType == typeof(int) ? (object)(int)n
                      : targetType == typeof(short) ? (short)n
                      : targetType == typeof(byte) ? (byte)n
                      : n;
                return true;
            }
            return false;
        }

        if (targetType == typeof(bool))
        {
            if (literal == "true") { value = true; return true; }
            if (literal == "false") { value = false; return true; }
            return false;
        }

        if (targetType == typeof(double) || targetType == typeof(float))
        {
            if (double.TryParse(literal, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
            {
                value = targetType == typeof(float) ? (float)d : d;
                return true;
            }
            return false;
        }

        if (targetType.IsEnum)
        {
            // Accept `EnumTypeName.Member` or bare `Member`.
            var memberName = literal.Contains('.') ? literal[(literal.LastIndexOf('.') + 1)..] : literal;
            try
            {
                value = Enum.Parse(targetType, memberName);
                return true;
            }
            catch { return false; }
        }

        if (targetType == typeof(Type))
        {
            // typeof(Foo) — not supported yet
            return false;
        }

        return false;
    }
}
