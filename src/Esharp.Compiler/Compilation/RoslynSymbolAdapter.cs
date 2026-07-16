using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Esharp.BoundTree;

// The compiler is one assembly now, so the global `using Esharp.Symbols` is in
// scope here and its interface names (ISymbol, ITypeSymbol, …) collide with
// Roslyn's identically-named ones. This file is the Roslyn→E# adapter — every
// bare symbol interface below is a *Roslyn* symbol — so pin those names to
// Microsoft.CodeAnalysis. (An alias outranks a namespace import, so this wins
// over the global using without touching the rest of the file.)
using ISymbol = Microsoft.CodeAnalysis.ISymbol;
using ITypeSymbol = Microsoft.CodeAnalysis.ITypeSymbol;
using INamespaceSymbol = Microsoft.CodeAnalysis.INamespaceSymbol;
using IMethodSymbol = Microsoft.CodeAnalysis.IMethodSymbol;
using IFieldSymbol = Microsoft.CodeAnalysis.IFieldSymbol;
using IParameterSymbol = Microsoft.CodeAnalysis.IParameterSymbol;

namespace Esharp.Compilation;

// Projects Roslyn's INamedTypeSymbol graph into E#'s ICSharpTypeHandle world.
// Roslyn does the real semantic work — resolving base classes, interfaces, and
// member signatures against the project's external references. We walk that
// resolved tree and adapt each symbol into the language-neutral handle shape
// the binder consumes. The binder never sees Roslyn types directly; this file
// is the only place the two type universes touch.
//
// Vertical-slice scope: maps classes, structs, interfaces, enums, methods,
// fields, constructors. Generic types are carried by name (no instantiation
// surface yet). Properties projected as a method pair only when accessed.
internal static class RoslynSymbolAdapter
{
    public static IReadOnlyList<ICSharpTypeHandle> CollectSourceDeclaredTypes(CSharpCompilation compilation)
    {
        var mapper = new TypeMapper(compilation);
        var result = new List<ICSharpTypeHandle>();
        Walk(compilation.GlobalNamespace, result, mapper);
        return result;
    }

    static void Walk(INamespaceSymbol ns, List<ICSharpTypeHandle> result, TypeMapper mapper)
    {
        foreach (var member in ns.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol child:
                    Walk(child, result, mapper);
                    break;
                case INamedTypeSymbol type when type.DeclaringSyntaxReferences.Length > 0:
                    result.Add(mapper.Adapt(type));
                    break;
            }
        }
    }

    // Stateful mapper to share handle instances per symbol — keeps reference
    // equality for the binder's lookup tables and avoids quadratic re-projection
    // when types reference each other (base classes, interface implementations).
    internal sealed class TypeMapper
    {
        readonly CSharpCompilation _compilation;
        readonly Dictionary<INamedTypeSymbol, RoslynTypeHandle> _cache = new(SymbolEqualityComparer.Default);

        public TypeMapper(CSharpCompilation compilation) => _compilation = compilation;

        public ICSharpTypeHandle Adapt(INamedTypeSymbol symbol)
        {
            if (_cache.TryGetValue(symbol, out var existing))
                return existing;
            var handle = new RoslynTypeHandle(symbol, this);
            _cache[symbol] = handle;
            return handle;
        }

        public BoundType MapTypeReference(ITypeSymbol symbol) => symbol.SpecialType switch
        {
            SpecialType.System_Boolean => new PrimitiveType("bool"),
            SpecialType.System_Char => new PrimitiveType("char"),
            SpecialType.System_SByte => new PrimitiveType("sbyte"),
            SpecialType.System_Byte => new PrimitiveType("byte"),
            SpecialType.System_Int16 => new PrimitiveType("short"),
            SpecialType.System_UInt16 => new PrimitiveType("ushort"),
            SpecialType.System_Int32 => new PrimitiveType("int"),
            SpecialType.System_UInt32 => new PrimitiveType("uint"),
            SpecialType.System_Int64 => new PrimitiveType("long"),
            SpecialType.System_UInt64 => new PrimitiveType("ulong"),
            SpecialType.System_Single => new PrimitiveType("float"),
            SpecialType.System_Double => new PrimitiveType("double"),
            SpecialType.System_String => new PrimitiveType("string"),
            SpecialType.System_Void => new VoidType(),
            SpecialType.System_Object => new ExternalType("object"),
            _ => symbol switch
            {
                INamedTypeSymbol named when named.ContainingAssembly is { } asm && SymbolEqualityComparer.Default.Equals(asm, _compilation.Assembly)
                    => new ExternalCSharpType(Adapt(named)),
                INamedTypeSymbol named => new ExternalType(named.Name),
                _ => new ExternalType(symbol.Name),
            }
        };
    }

    internal sealed class RoslynTypeHandle : ICSharpTypeHandle
    {
        readonly INamedTypeSymbol _symbol;
        readonly TypeMapper _mapper;
        IReadOnlyList<ICSharpMemberHandle>? _membersCache;
        IReadOnlyList<ICSharpTypeHandle>? _interfacesCache;

        public RoslynTypeHandle(INamedTypeSymbol symbol, TypeMapper mapper)
        {
            _symbol = symbol;
            _mapper = mapper;
        }

        internal INamedTypeSymbol Symbol => _symbol;

        public string Name => _symbol.Name;
        public string Namespace => _symbol.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString()
            : "";
        public string FullMetadataName => string.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";
        public string AssemblyName => _symbol.ContainingAssembly?.Name ?? "";

        public bool IsRefType => _symbol.TypeKind == TypeKind.Class;
        public bool IsInterface => _symbol.TypeKind == TypeKind.Interface;
        public bool IsEnum => _symbol.TypeKind == TypeKind.Enum;
        public bool IsSealed => _symbol.IsSealed;
        public bool IsAbstract => _symbol.IsAbstract;
        public bool IsStatic => _symbol.IsStatic;

        public int GenericParameterCount => _symbol.TypeParameters.Length;

        public ICSharpTypeHandle? BaseType
        {
            get
            {
                if (_symbol.BaseType is null) return null;
                if (_symbol.BaseType.SpecialType == SpecialType.System_Object) return null;
                if (_symbol.BaseType.SpecialType == SpecialType.System_ValueType) return null;
                if (_symbol.BaseType.SpecialType == SpecialType.System_Enum) return null;
                return _mapper.Adapt(_symbol.BaseType);
            }
        }

        public IReadOnlyList<ICSharpTypeHandle> Interfaces =>
            _interfacesCache ??= _symbol.Interfaces.Select(i => _mapper.Adapt(i)).ToList();

        public IReadOnlyList<ICSharpMemberHandle> Members =>
            _membersCache ??= _symbol.GetMembers()
                .Where(m => m.DeclaredAccessibility == Accessibility.Public
                         || m.DeclaredAccessibility == Accessibility.Internal
                         || m.DeclaredAccessibility == Accessibility.ProtectedOrInternal)
                .Select(m => AdaptMember(m))
                .Where(m => m is not null)
                .Select(m => m!)
                .ToList();

        ICSharpMemberHandle? AdaptMember(ISymbol member) => member switch
        {
            IMethodSymbol method when method.MethodKind is MethodKind.Ordinary
                                           or MethodKind.Constructor
                                           or MethodKind.UserDefinedOperator
                                           or MethodKind.PropertyGet
                                           or MethodKind.PropertySet
                                => new RoslynMethodHandle(method, _mapper),
            IFieldSymbol field when !field.IsImplicitlyDeclared
                                => new RoslynFieldHandle(field, _mapper),
            IPropertySymbol prop => new RoslynPropertyHandle(prop, _mapper),
            _ => null,
        };
    }

    internal sealed class RoslynMethodHandle : ICSharpMemberHandle
    {
        readonly IMethodSymbol _symbol;
        readonly TypeMapper _mapper;
        IReadOnlyList<ICSharpParameterHandle>? _paramCache;

        public RoslynMethodHandle(IMethodSymbol symbol, TypeMapper mapper)
        {
            _symbol = symbol;
            _mapper = mapper;
        }

        internal IMethodSymbol Symbol => _symbol;

        public string Name => _symbol.MethodKind == MethodKind.Constructor ? ".ctor" : _symbol.Name;
        public CSharpMemberKind Kind => _symbol.MethodKind == MethodKind.Constructor
            ? CSharpMemberKind.Constructor
            : CSharpMemberKind.Method;
        public bool IsStatic => _symbol.IsStatic;
        public bool IsVirtual => _symbol.IsVirtual || _symbol.IsAbstract || _symbol.IsOverride;
        public bool IsAbstract => _symbol.IsAbstract;
        public bool IsPublic => _symbol.DeclaredAccessibility == Accessibility.Public;
        public BoundType ReturnType => _mapper.MapTypeReference(_symbol.ReturnType);
        public IReadOnlyList<ICSharpParameterHandle> Parameters =>
            _paramCache ??= _symbol.Parameters
                .Select(p => (ICSharpParameterHandle)new RoslynParameterHandle(p, _mapper))
                .ToList();
        public object? ConstantValue => null;
    }

    internal sealed class RoslynFieldHandle : ICSharpMemberHandle
    {
        readonly IFieldSymbol _symbol;
        readonly TypeMapper _mapper;

        public RoslynFieldHandle(IFieldSymbol symbol, TypeMapper mapper)
        {
            _symbol = symbol;
            _mapper = mapper;
        }

        internal IFieldSymbol Symbol => _symbol;

        public string Name => _symbol.Name;
        public CSharpMemberKind Kind => CSharpMemberKind.Field;
        public bool IsStatic => _symbol.IsStatic;
        public bool IsVirtual => false;
        public bool IsAbstract => false;
        public bool IsPublic => _symbol.DeclaredAccessibility == Accessibility.Public;
        public BoundType ReturnType => _mapper.MapTypeReference(_symbol.Type);
        public IReadOnlyList<ICSharpParameterHandle> Parameters => Array.Empty<ICSharpParameterHandle>();
        public object? ConstantValue => _symbol.HasConstantValue ? _symbol.ConstantValue : null;
    }

    internal sealed class RoslynPropertyHandle : ICSharpMemberHandle
    {
        readonly IPropertySymbol _symbol;
        readonly TypeMapper _mapper;

        public RoslynPropertyHandle(IPropertySymbol symbol, TypeMapper mapper)
        {
            _symbol = symbol;
            _mapper = mapper;
        }

        internal IPropertySymbol Symbol => _symbol;

        public string Name => _symbol.Name;
        public CSharpMemberKind Kind => CSharpMemberKind.Property;
        public bool IsStatic => _symbol.IsStatic;
        public bool IsVirtual => _symbol.IsVirtual || _symbol.IsAbstract || _symbol.IsOverride;
        public bool IsAbstract => _symbol.IsAbstract;
        public bool IsPublic => _symbol.DeclaredAccessibility == Accessibility.Public;
        public bool HasGetter => _symbol.GetMethod is not null;
        public bool HasSetter => _symbol.SetMethod is not null;
        public bool ReturnsByRef => _symbol.RefKind is RefKind.Ref or RefKind.RefReadOnly;
        public bool ReturnsByRefReadonly => _symbol.RefKind == RefKind.RefReadOnly;
        public BoundType ReturnType => _mapper.MapTypeReference(_symbol.Type);
        public IReadOnlyList<ICSharpParameterHandle> Parameters => Array.Empty<ICSharpParameterHandle>();
        public object? ConstantValue => null;
    }

    internal sealed class RoslynParameterHandle : ICSharpParameterHandle
    {
        readonly IParameterSymbol _symbol;
        readonly TypeMapper _mapper;

        public RoslynParameterHandle(IParameterSymbol symbol, TypeMapper mapper)
        {
            _symbol = symbol;
            _mapper = mapper;
        }

        public string Name => _symbol.Name;
        public BoundType Type => _mapper.MapTypeReference(_symbol.Type);
        public bool IsOptional => _symbol.IsOptional;
        // ExplicitDefaultValue hands back an enum default as its underlying primitive,
        // which is what the IL emitter loads; null covers both "no explicit default" and
        // an explicit `default(T)` (the emitter initobj's the value type in that case).
        public object? DefaultValue =>
            _symbol is { IsOptional: true, HasExplicitDefaultValue: true } ? _symbol.ExplicitDefaultValue : null;
    }
}
