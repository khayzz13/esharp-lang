using System.Reflection;
using Mono.Cecil;
using Esharp.Compiler.Binding;

namespace Esharp.ILEmit;

public sealed class ILTypeResolver
{
    readonly ModuleDefinition _module;
    readonly Dictionary<string, TypeDefinition> _definedTypes = new(StringComparer.Ordinal);
    readonly Dictionary<string, Type> _runtimeTypeCache = new(StringComparer.Ordinal);

    public ILTypeResolver(ModuleDefinition module) => _module = module;

    public void Register(string name, TypeDefinition type) => _definedTypes[name] = type;

    public TypeReference Resolve(BoundType type) => type switch
    {
        PrimitiveType p => ResolvePrimitive(p.Name),
        DataType d => _definedTypes.TryGetValue(d.Name, out var dt) ? dt : _module.ImportReference(typeof(object)),
        ChoiceType c => _definedTypes.TryGetValue(c.Name, out var ct) ? ct : _module.ImportReference(typeof(object)),
        EnumType e => _definedTypes.TryGetValue(e.Name, out var et) ? et : _module.ImportReference(typeof(int)),
        VoidType => _module.ImportReference(typeof(void)),
        ExternalType ext => ResolveExternal(ext.Name),
        _ => _module.ImportReference(typeof(object)),
    };

    TypeReference ResolvePrimitive(string name) => name switch
    {
        "int" => _module.ImportReference(typeof(int)),
        "string" => _module.ImportReference(typeof(string)),
        "bool" => _module.ImportReference(typeof(bool)),
        "double" => _module.ImportReference(typeof(double)),
        "float" => _module.ImportReference(typeof(float)),
        "long" => _module.ImportReference(typeof(long)),
        "byte" => _module.ImportReference(typeof(byte)),
        "char" => _module.ImportReference(typeof(char)),
        "short" => _module.ImportReference(typeof(short)),
        "uint" => _module.ImportReference(typeof(uint)),
        "ulong" => _module.ImportReference(typeof(ulong)),
        "void" => _module.ImportReference(typeof(void)),
        _ => _module.ImportReference(typeof(object)),
    };

    TypeReference ResolveExternal(string name) => name switch
    {
        "var" or "object" => _module.ImportReference(typeof(object)),
        _ => TryResolveRuntimeType(name) is { } t ? _module.ImportReference(t) : _module.ImportReference(typeof(object)),
    };

    /// <summary>Resolve a .NET type by name via reflection. Caches results.</summary>
    public Type? TryResolveRuntimeType(string name)
    {
        if (_runtimeTypeCache.TryGetValue(name, out var cached))
            return cached;

        var type = ResolveRuntimeTypeCore(name);
        if (type is not null)
            _runtimeTypeCache[name] = type;
        return type;
    }

    static Type? ResolveRuntimeTypeCore(string name) => name switch
    {
        // Common BCL types
        "Console" => typeof(Console),
        "Math" => typeof(Math),
        "MathF" => typeof(MathF),
        "Environment" => typeof(Environment),
        "Guid" => typeof(Guid),
        "DateTime" => typeof(DateTime),
        "DateTimeOffset" => typeof(DateTimeOffset),
        "TimeSpan" => typeof(TimeSpan),
        "Convert" => typeof(Convert),
        "File" => typeof(System.IO.File),
        "Path" => typeof(System.IO.Path),
        "Directory" => typeof(System.IO.Directory),
        "Task" => typeof(System.Threading.Tasks.Task),
        // Fallback: search loaded assemblies
        _ => Type.GetType($"System.{name}") ?? Type.GetType(name) ?? SearchAssemblies(name),
    };

    static Type? SearchAssemblies(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType($"System.{name}") ?? asm.GetType(name);
            if (type is not null) return type;
            // Try System.Collections.Generic for generic types
            type = asm.GetType($"System.Collections.Generic.{name}");
            if (type is not null) return type;
        }
        return null;
    }

    /// <summary>Map a BoundType to the runtime System.Type for reflection-based method lookup.</summary>
    public Type? BoundTypeToRuntime(BoundType type) => type switch
    {
        PrimitiveType p => p.Name switch
        {
            "int" => typeof(int),
            "string" => typeof(string),
            "bool" => typeof(bool),
            "double" => typeof(double),
            "float" => typeof(float),
            "long" => typeof(long),
            "byte" => typeof(byte),
            "char" => typeof(char),
            "short" => typeof(short),
            "uint" => typeof(uint),
            "ulong" => typeof(ulong),
            _ => typeof(object),
        },
        ExternalType ext => TryResolveRuntimeType(ext.Name) ?? typeof(object),
        VoidType => typeof(void),
        _ => typeof(object),
    };

    /// <summary>Find a method on a runtime type and import it into the Cecil module.</summary>
    public MethodReference? ResolveExternalMethod(Type declaringType, string methodName, int argCount, Type[]? argTypes = null)
    {
        MethodInfo? method = null;

        if (argTypes is not null)
        {
            method = declaringType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance, argTypes);
        }

        // Fallback: match by name + param count
        method ??= declaringType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == argCount);

        if (method is null) return null;

        // For generic method definitions, close with the provided arg types
        if (method.IsGenericMethodDefinition && argTypes is not null)
        {
            // Infer generic type args from the concrete argument types
            var typeParams = method.GetGenericArguments();
            var paramInfos = method.GetParameters();
            var inferredArgs = new Type[typeParams.Length];

            for (var i = 0; i < paramInfos.Length && i < argTypes.Length; i++)
            {
                var paramType = paramInfos[i].ParameterType;
                if (paramType.IsGenericParameter)
                {
                    var pos = paramType.GenericParameterPosition;
                    if (pos < inferredArgs.Length)
                        inferredArgs[pos] = argTypes[i];
                }
            }

            // Fill any unresolved with object
            for (var i = 0; i < inferredArgs.Length; i++)
                inferredArgs[i] ??= typeof(object);

            method = method.MakeGenericMethod(inferredArgs);
        }

        return _module.ImportReference(method);
    }

    /// <summary>Find a static method on a runtime type.</summary>
    public MethodReference? ResolveStaticMethod(Type declaringType, string methodName, int argCount, Type[]? argTypes = null)
    {
        MethodInfo? method = null;

        if (argTypes is not null)
            method = declaringType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, argTypes);

        method ??= declaringType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == argCount);

        return method is not null ? _module.ImportReference(method) : null;
    }

    /// <summary>Get the runtime Type for a value on the IL stack given its BoundType.</summary>
    public Type? GetStackType(BoundType type) => BoundTypeToRuntime(type);

    public TypeReference ImportReference(Type type) => _module.ImportReference(type);
    public ModuleDefinition Module => _module;
}
