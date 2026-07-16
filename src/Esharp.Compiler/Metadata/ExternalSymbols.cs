using Esharp.BoundTree;
using Esharp.Symbols;
using System.Reflection;

namespace Esharp.Metadata;

/// The external (BCL / referenced-assembly) half of the symbol spine.
///
/// In the rewrite this class is <strong>metadata-backed</strong> — its primary
/// entry point is <see cref="ForFqn"/> which accepts a fully-qualified type name
/// from the <see cref="MetadataReader"/> index and produces the same External
/// <see cref="TypeSymbol"/> shapes the old reflection path produced, with no
/// <c>Assembly.LoadFrom</c> or AppDomain side-effects.
///
/// The reflection-backed <see cref="ForType(Type, bool)"/> path is preserved for
/// the <em>mixed-language seam only</em>: <c>ExternalCSharpType</c> adapters carry
/// a <c>System.Type</c> from Roslyn's symbol graph, and the binder's
/// <c>RegisterCSharpType</c> still calls into this class via <c>ForType</c>. That
/// path is not on the hot-path of normal E# compilation — it fires only when the
/// project contains <c>.cs</c> source files.
///
/// Member population is explicit and lazy: a TypeSymbol minted for a
/// parameter/return TypeRef stays member-empty until a caller asks for the surface.
/// That keeps the full BCL graph from being dragged in behind a single external
/// reference, identical to the old behaviour.
///
/// Reference identity: one <see cref="TypeSymbol"/> per fully-qualified name, one
/// <see cref="MethodSymbol"/> per method overload key, one <see cref="FieldSymbol"/>
/// per member name. <c>FindReferences</c> and hover work because they compare
/// symbol references, not strings.
public sealed class ExternalSymbols : IExternalSymbols
{
    // ── Metadata-backed store (primary path) ──────────────────────────────

    // Interned by FQN (e.g. "System.Collections.Generic.List`1").
    readonly Dictionary<string, TypeSymbol> _byFqn = new(StringComparer.Ordinal);
    readonly HashSet<TypeSymbol> _populated = new(ReferenceEqualityComparer.Instance);

    // ── Reflection-backed store (mixed-language seam only) ────────────────

    readonly Dictionary<Type, TypeSymbol> _byType = new();
    readonly Dictionary<TypeSymbol, Type> _runtimeBySymbol = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<MethodBase, MethodSymbol> _methods = new();
    readonly Dictionary<MemberInfo, FieldSymbol> _fields = new();

    // ── Metadata-backed entry points ──────────────────────────────────────

    /// Intern a <see cref="TypeSymbol"/> for the given fully-qualified type name.
    /// Called by <see cref="MetadataReader"/> after locating the type in a PE.
    ///
    /// <paramref name="memberFactory"/> is invoked at most once, on the first call
    /// with <paramref name="populateMembers"/> = true, to supply the member list
    /// decoded from the PE signature tables — no reflection required.
    public TypeSymbol ForFqn(
        string fqn,
        string displayName,
        string? ns,
        int arity,
        bool isValueType,
        bool populateMembers,
        Func<List<MetadataReader.MemberDescription>>? memberFactory,
        string? xmlDoc)
    {
        if (!_byFqn.TryGetValue(fqn, out var sym))
        {
            sym = new TypeSymbol
            {
                Name = displayName,
                Namespace = ns,
                Arity = arity,
                TypeKind = IsPrimitiveDisplayName(displayName) ? TypeSymbolKind.Primitive : TypeSymbolKind.External,
                XmlDoc = xmlDoc,
            };
            _byFqn[fqn] = sym;
        }

        if (populateMembers && _populated.Add(sym) && memberFactory is not null)
        {
            var members = memberFactory();
            foreach (var member in members)
            {
                if (member.Kind == MetadataReader.MemberKind.Method)
                {
                    sym.AddMember(new MethodSymbol
                    {
                        Name = member.Name,
                        DeclaredArity = 0, // surface-level; overload arity is resolved per call
                        IsStatic = member.IsStatic,
                        DeclaringType = sym,
                        ReturnType = member.ReturnType,
                        DeclaredParameters = [],
                        TypeParameters = [],
                        XmlDoc = member.XmlDoc,
                    });
                }
                else
                {
                    sym.AddField(new FieldSymbol
                    {
                        Name = member.Name,
                        DeclaringType = sym,
                        Bound = member.ReturnType,
                        DeclaredMutable = member.PropertyCapability != 0
                            ? (member.PropertyCapability & 0b010000) != 0
                            : member.Mutable,
                        Mutable = member.PropertyCapability != 0
                            ? (member.PropertyCapability & 0b010000) != 0
                            : member.Mutable,
                        IsProperty = member.IsProperty,
                        HasDurablePropertyLocation = (member.PropertyCapability & 0b000010) != 0,
                        HasScopedPropertyLocation = (member.PropertyCapability & 0b001000) != 0,
                        HasCustomPropertySetter = (member.PropertyCapability & 0b100000) != 0,
                        XmlDoc = member.XmlDoc,
                    });
                }
            }
        }

        return sym;
    }

    // ── Reflection-backed entry points (mixed-language seam) ──────────────

    /// The interned symbol for a runtime type (reflection-backed). Used only for
    /// the C#-half adapter path — <c>ExternalCSharpType</c> carries a System.Type.
    /// Normal BCL resolution goes through <see cref="ForFqn"/>.
    public TypeSymbol ForType(Type type, bool populateMembers = false)
    {
        if (!_byType.TryGetValue(type, out var sym))
        {
            sym = new TypeSymbol
            {
                Name = DisplayName(type),
                Namespace = type.Namespace,
                Arity = type.IsGenericType ? type.GetGenericArguments().Length : 0,
                TypeKind = IsPrimitiveSpelling(type) ? TypeSymbolKind.Primitive : TypeSymbolKind.External,
            };
            _byType[type] = sym;
            _runtimeBySymbol[sym] = type;
        }
        if (populateMembers && _populated.Add(sym))
            PopulateViaReflection(sym, type);
        return sym;
    }

    /// Populate an already-interned external symbol's member surface via reflection
    /// (idempotent; no-op for symbols this interner didn't mint).
    public void EnsureMembers(TypeSymbol sym)
    {
        if (_runtimeBySymbol.TryGetValue(sym, out var type) && _populated.Add(sym))
            PopulateViaReflection(sym, type);
    }

    /// The interned symbol for an externally-resolved method (reflection-backed).
    public MethodSymbol Method(MethodInfo method)
    {
        if (_methods.TryGetValue(method, out var sym)) return sym;
        var ps = method.GetParameters();
        sym = new MethodSymbol
        {
            Name = method.Name,
            DeclaredArity = ps.Length,
            IsStatic = method.IsStatic,
            DeclaringType = ForType(method.DeclaringType ?? typeof(object)),
            ReturnType = method.ReturnType == typeof(void)
                ? new VoidType()
                : new ExternalType(DisplayName(method.ReturnType)),
            DeclaredParameters = ps.Select(p => new TypeRef(ForType(p.ParameterType))).ToList(),
            TypeParameters = method.IsGenericMethodDefinition
                ? method.GetGenericArguments().Select(a => a.Name).ToList()
                : [],
        };
        _methods[method] = sym;
        return sym;
    }

    /// The interned symbol for an external property or field (reflection-backed).
    public FieldSymbol Field(MemberInfo member)
    {
        if (_fields.TryGetValue(member, out var sym)) return sym;
        var property = member as PropertyInfo;
        var refGetter = property?.GetMethod is { ReturnType.IsByRef: true } getter ? getter : null;
        var refReadOnly = refGetter is not null && IsReadonlyRefReturn(refGetter);
        var (type, mutable) = member switch
        {
            PropertyInfo p when refGetter is not null =>
                (refGetter.ReturnType.GetElementType()!, !refReadOnly),
            PropertyInfo p => (p.PropertyType, p.CanWrite),
            FieldInfo f => (f.FieldType, !f.IsInitOnly && !f.IsLiteral),
            _ => (typeof(object), false),
        };
        var capability = property is null ? 0 : ReadPropertyCapability(property);
        if (capability == 0 && refGetter is not null)
            capability = 0b000010 | (refReadOnly ? 0 : 0b010000);
        sym = new FieldSymbol
        {
            Name = member.Name,
            DeclaringType = ForType(member.DeclaringType ?? typeof(object)),
            Bound = new ExternalType(DisplayName(type)),
            DeclaredMutable = capability != 0 ? (capability & 0b010000) != 0 : mutable,
            Mutable = capability != 0 ? (capability & 0b010000) != 0 : mutable,
            IsProperty = property is not null,
            HasDurablePropertyLocation = (capability & 0b000010) != 0,
            HasScopedPropertyLocation = (capability & 0b001000) != 0,
            HasCustomPropertySetter = (capability & 0b100000) != 0,
        };
        _fields[member] = sym;
        return sym;
    }

    // The compiler emits this metadata on every E# property.  Read the raw
    // constructor argument instead of instantiating the producer's attribute:
    // separate compilation should not execute arbitrary producer code merely to
    // discover a property capability.
    static int ReadPropertyCapability(PropertyInfo property)
    {
        foreach (var attribute in property.CustomAttributes)
        {
            if (attribute.AttributeType.Name != "__EsharpPropertyCapabilityAttribute"
                || attribute.ConstructorArguments.Count != 1)
                continue;
            return attribute.ConstructorArguments[0].Value is int flags ? flags : 0;
        }
        return 0;
    }

    static bool IsReadonlyRefReturn(MethodInfo getter) =>
        getter.ReturnParameter.GetRequiredCustomModifiers().Any(modifier =>
            modifier == typeof(System.Runtime.InteropServices.InAttribute)
            || modifier.FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute");

    void PopulateViaReflection(TypeSymbol sym, Type type)
    {
        var seenMethods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            if (m.IsSpecialName) continue;
            if (seenMethods.Add(m.Name))
                sym.AddMember(Method(m));
        }
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            if (p.GetIndexParameters().Length == 0)
                sym.AddField(Field(p));
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            sym.AddField(Field(f));
    }

    // ── Display name helpers ──────────────────────────────────────────────

    static bool IsPrimitiveSpelling(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
        || t == typeof(sbyte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort)
        || t == typeof(float) || t == typeof(double) || t == typeof(decimal)
        || t == typeof(bool) || t == typeof(char) || t == typeof(string) || t == typeof(object);

    static bool IsPrimitiveDisplayName(string name) =>
        name is "int" or "long" or "short" or "byte" or "sbyte"
             or "uint" or "ulong" or "ushort" or "nint" or "nuint" or "float" or "double"
             or "decimal" or "bool" or "char" or "string" or "object";

    /// A runtime type rendered in E# source spelling: <c>Int32</c> → <c>int</c>,
    /// <c>List`1[String]</c> → <c>List&lt;string&gt;</c>.
    public static string DisplayName(Type t)
    {
        if (t.IsByRef) return "*" + DisplayName(t.GetElementType()!);
        if (t.IsArray) return DisplayName(t.GetElementType()!) + "[]";
        if (Nullable.GetUnderlyingType(t) is { } inner) return DisplayName(inner) + "?";
        if (t.IsGenericParameter) return t.Name;
        if (t.IsGenericType)
        {
            var name = t.Name;
            var tick = name.IndexOf('`');
            if (tick > 0) name = name[..tick];
            var args = t.GetGenericArguments().Select(DisplayName);
            return $"{name}<{string.Join(", ", args)}>";
        }
        return t.FullName switch
        {
            "System.Int32"    => "int",
            "System.Int64"    => "long",
            "System.Int16"    => "short",
            "System.Byte"     => "byte",
            "System.SByte"    => "sbyte",
            "System.UInt32"   => "uint",
            "System.UInt64"   => "ulong",
            "System.UInt16"   => "ushort",
            "System.Single"   => "float",
            "System.Double"   => "double",
            "System.Decimal"  => "decimal",
            "System.Boolean"  => "bool",
            "System.Char"     => "char",
            "System.String"   => "string",
            "System.Object"   => "object",
            "System.Void"     => "void",
            _ => t.Name,
        };
    }
}
