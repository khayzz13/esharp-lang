using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Esharp.BoundTree;
using Esharp.Symbols;

namespace Esharp.Metadata;

/// External type resolution via <c>System.Reflection.Metadata</c> — no
/// <c>Assembly.LoadFrom</c>, no AppDomain side-effects, no reflection-based
/// type probing. Each referenced assembly is opened as a portable PEReader and
/// its metadata table is queried directly.
///
/// This is the replacement for the reflection surface of <c>ILTypeResolver</c>.
/// The produced symbol shapes (External <see cref="TypeSymbol"/>s with
/// populated Members/Fields) are identical to what the old reflection path
/// produced, so consumers see no change behind the frozen Symbol API.
///
/// Capabilities:
/// - Resolve a type by fully-qualified name or short name + import scope.
/// - Produce <see cref="TypeSymbol"/> entries with populated member surfaces
///   (methods, fields, properties) on demand (lazy, idempotent).
/// - Report whether a type is a value type (needed by codegen for boxing).
/// - Translate E# <see cref="BoundType"/> to the string names used in metadata
///   lookups (for method overload scoring without reflection).
/// - Resolve open-generic types by base name + arity.
///
/// Thread-safety: all public members are safe for concurrent reads after
/// <see cref="LoadAssembly"/> calls complete. <see cref="LoadAssembly"/> is
/// itself safe (ConcurrentDictionary, double-check on readers).
public sealed class MetadataReader : IDisposable
{
    // One open PEReader per assembly path. Held open for the lifetime of this
    // resolver so the MetadataReader handles inside remain valid.
    readonly ConcurrentDictionary<string, (PEReader pe, System.Reflection.Metadata.MetadataReader mr)> _assemblies
        = new(StringComparer.OrdinalIgnoreCase);

    // Type index: fully-qualified name → (assembly path, TypeDefinitionHandle).
    // Built eagerly when an assembly is loaded, never rebuilt — new calls to
    // LoadAssembly add entries; nothing removes them.
    readonly ConcurrentDictionary<string, (string assemblyPath, TypeDefinitionHandle handle)> _typeIndex
        = new(StringComparer.Ordinal);

    // Short name → list of fully-qualified names. A short name can be
    // ambiguous (List exists in multiple assemblies theoretically); the import
    // scope disambiguates at lookup time. We keep ALL FQNs per short name so
    // the import-scope tier search is exhaustive.
    readonly ConcurrentDictionary<string, List<string>> _byShortName
        = new(StringComparer.Ordinal);

    // Value-type cache: fqn → IsValueType, computed once per definition.
    readonly ConcurrentDictionary<string, bool> _isValueType = new(StringComparer.Ordinal);

    // Open-generic registry: "Namespace.Name`arity" → fqn of the definition.
    readonly ConcurrentDictionary<string, string> _openGenerics = new(StringComparer.Ordinal);

    readonly ExternalSymbols _symbols;
    readonly MetadataXmlDocs _xmlDocs;

    // Import scope applied during bare-name lookups, updated per compilation unit.
    string[] _importedNamespaces = Array.Empty<string>();
    readonly string[] _commonNamespaces = CommonNamespaceSet;

    static readonly string[] CommonNamespaceSet =
    [
        "System",
        "System.Collections.Generic",
        "System.Collections.Concurrent",
        "System.IO",
        "System.Linq",
        "System.Net.Http",
        "System.Threading",
        "System.Threading.Tasks",
        "System.Threading.Channels",
        "System.Text",
        "System.Text.Json",
        "System.Reflection",
        "System.Runtime.InteropServices",
        "System.Diagnostics",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Logging",
    ];

    public MetadataReader(ExternalSymbols symbols, MetadataXmlDocs xmlDocs)
    {
        _symbols = symbols;
        _xmlDocs = xmlDocs;
    }

    // ── Assembly loading ───────────────────────────────────────────────────

    /// Open the PE at <paramref name="path"/> and index its public type
    /// definitions. Idempotent — calling twice with the same path is a no-op.
    /// Ref-assembly stubs (metadata-only, no method bodies) are fully supported
    /// — we only read type tables, never method bodies.
    public void LoadAssembly(string path)
    {
        if (_assemblies.ContainsKey(path)) return;

        PEReader pe;
        try { pe = new PEReader(File.OpenRead(path)); }
        catch { return; } // unreadable / not a PE — skip silently

        if (!pe.HasMetadata) { pe.Dispose(); return; }

        var mr = pe.GetMetadataReader();

        // Double-check after acquiring the reader in case two threads raced.
        if (!_assemblies.TryAdd(path, (pe, mr))) { pe.Dispose(); return; }

        IndexAssembly(path, mr);

        // Load sibling XML docs (BCL ref-pack pattern: Foo.dll → Foo.xml).
        _xmlDocs.TryLoadSibling(path);
    }

    /// Load a set of reference paths, skipping ref-assembly stubs in favour of
    /// implementation assemblies when available. Mirrors the old
    /// <c>ILTypeResolver.PreloadReferenceAssemblies</c> semantics.
    public void LoadReferences(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (IsRefAssemblyPath(path))
            {
                var impl = TryFindImplementationAssembly(path);
                LoadAssembly(impl ?? path);
            }
            else
            {
                LoadAssembly(path);
            }
        }
    }

    void IndexAssembly(string assemblyPath, System.Reflection.Metadata.MetadataReader mr)
    {
        foreach (var handle in mr.TypeDefinitions)
        {
            var def = mr.GetTypeDefinition(handle);
            var ns = def.Namespace.IsNil ? "" : mr.GetString(def.Namespace);
            var name = mr.GetString(def.Name);

            // Skip compiler synthetics — names starting with '<' (e.g. <Module>,
            // <PrivateImplementationDetails>, state-machine display classes) are never
            // user-visible and pollute the short-name index.
            if (name.Length > 0 && name[0] == '<') continue;

            // Strip arity suffix from generic names for short-name indexing.
            var tick = name.IndexOf('`');
            var shortName = tick > 0 ? name[..tick] : name;
            var arity = tick > 0 && int.TryParse(name[(tick + 1)..], out var a) ? a : 0;

            var fqn = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            var fqnShort = string.IsNullOrEmpty(ns) ? shortName : $"{ns}.{shortName}";

            _typeIndex[fqn] = (assemblyPath, handle);

            // Open-generic key: "Namespace.BaseName`arity" → fqn
            if (arity > 0)
                _openGenerics[$"{fqnShort}`{arity}"] = fqn;

            // Short-name index (potentially multi-valued — last-write in
            // ConcurrentDictionary is fine for the list reference; the list
            // itself is written under a lock to be safe).
            AddShortName(shortName, fqn);

            // Value-type determination from the metadata directly — no reflection needed.
            // A type is a value type when its base is System.ValueType or System.Enum
            // (which itself derives from System.ValueType). The Sealed flag is not
            // required — value types may be sealed by the metadata but that's a
            // consequence, not a predicate. Classes derive from System.Object, not
            // System.ValueType, so the base-type check is sufficient.
            var isValueType = IsValueTypeByMetadata(mr, def);
            _isValueType[fqn] = isValueType;
        }
    }

    void AddShortName(string shortName, string fqn)
    {
        var list = _byShortName.GetOrAdd(shortName, _ => new List<string>());
        lock (list) { if (!list.Contains(fqn)) list.Add(fqn); }
    }

    // A type definition is a value type when it is sealed AND its base type is
    // System.ValueType (or System.Enum → itself a value type subtype). We read
    // this from the base-type EntityHandle rather than loading the parent.
    static bool IsValueTypeByMetadata(System.Reflection.Metadata.MetadataReader mr,
        TypeDefinition def)
    {
        if (def.BaseType.IsNil) return false;
        try
        {
            string baseNs, baseName;
            if (def.BaseType.Kind == HandleKind.TypeReference)
            {
                var baseRef = mr.GetTypeReference((TypeReferenceHandle)def.BaseType);
                baseName = mr.GetString(baseRef.Name);
                baseNs = baseRef.Namespace.IsNil ? "" : mr.GetString(baseRef.Namespace);
            }
            else if (def.BaseType.Kind == HandleKind.TypeDefinition)
            {
                var baseDef = mr.GetTypeDefinition((TypeDefinitionHandle)def.BaseType);
                baseName = mr.GetString(baseDef.Name);
                baseNs = baseDef.Namespace.IsNil ? "" : mr.GetString(baseDef.Namespace);
            }
            else return false;

            return baseNs == "System" && (baseName == "ValueType" || baseName == "Enum");
        }
        catch { return false; }
    }

    // ── Import scope ───────────────────────────────────────────────────────

    /// Set the active import scope for bare-name lookups. Call once per
    /// compilation unit, before resolving that unit's external references.
    public void SetImportScope(IReadOnlyList<string> namespaces)
        => _importedNamespaces = namespaces.ToArray();

    // ── Type resolution ────────────────────────────────────────────────────

    /// Resolve a type by exact fully-qualified name. Returns the canonical FQN
    /// used in the index, or null if not found.
    public string? ResolveFqn(string fqn)
        => _typeIndex.ContainsKey(fqn) ? fqn : null;

    /// Resolve a bare type name against the current import scope + the common
    /// namespace set. Mirrors the tiered search of the old <c>SearchAssemblies</c>.
    /// Returns the FQN of the first match, or null.
    public string? ResolveBareName(string name)
    {
        // Tier 0: the name is already qualified.
        if (_typeIndex.ContainsKey(name)) return name;

        // Tier 1: explicit imports (in import order — deterministic).
        foreach (var ns in _importedNamespaces)
        {
            var candidate = $"{ns}.{name}";
            if (_typeIndex.ContainsKey(candidate)) return candidate;
        }

        // Tier 2: common namespaces (skippable via a gate, but always searched
        // here — the MetadataReader has no UsingEnvironment.SearchCommonNamespaces
        // coupling by design; callers that need to suppress can pass empty common
        // scope via SetImportScope).
        foreach (var ns in _commonNamespaces)
        {
            var candidate = $"{ns}.{name}";
            if (_typeIndex.ContainsKey(candidate)) return candidate;
        }

        return null;
    }

    /// Resolve an open-generic type by its E# source name and arity.
    /// "List" + 1 → "System.Collections.Generic.List`1".
    public string? ResolveOpenGenericFqn(string baseName, int arity)
    {
        // Explicit-arity key first.
        var mangledName = $"{baseName}`{arity}";
        if (_openGenerics.TryGetValue(mangledName, out var fqn)) return fqn;

        // Search by short-name + arity matching.
        if (_byShortName.TryGetValue(baseName, out var candidates))
        {
            lock (candidates)
                foreach (var c in candidates)
                    if (_openGenerics.ContainsKey($"{ExtractShortFqn(c)}`{arity}")) return c;
        }

        // Try scoped: "Namespace.BaseName`arity".
        foreach (var ns in _importedNamespaces)
        {
            var key = $"{ns}.{mangledName}";
            if (_openGenerics.TryGetValue(key, out var found)) return found;
        }
        foreach (var ns in _commonNamespaces)
        {
            var key = $"{ns}.{mangledName}";
            if (_openGenerics.TryGetValue(key, out var found)) return found;
        }
        return null;
    }

    static string ExtractShortFqn(string fqn)
    {
        var tick = fqn.IndexOf('`');
        return tick > 0 ? fqn[..tick] : fqn;
    }

    // ── Value-type query ───────────────────────────────────────────────────

    /// Whether the resolved type at <paramref name="fqn"/> is a value type.
    /// Always false for a name not in the index.
    public bool IsValueType(string fqn)
        => _isValueType.TryGetValue(fqn, out var v) && v;

    /// <see cref="BoundType"/>-keyed value-type check. Covers external + primitive
    /// cases without reflection.
    public bool IsValueType(BoundType type) => type switch
    {
        PrimitiveType p => p.Name is not "string" and not "object",
        DataType or ChoiceType or EnumType => true,
        ResultType or TupleType => true,
        NullableType => true,
        ArrayBoundType => false,
        InterfaceType => false,
        ExternalType { TypeArgs.Count: 0 } ext => IsValueType(ResolveBareName(ext.Name) ?? ext.Name),
        ExternalType gen => IsValueType(ResolveBareName(gen.Name) ?? gen.Name),
        _ => false,
    };

    // ── Symbol surface population ──────────────────────────────────────────

    /// Intern a <see cref="TypeSymbol"/> for the given FQN and optionally
    /// populate its member surface from metadata. The produced symbol is
    /// identical in shape to what the old reflection path produced — the same
    /// External <see cref="TypeSymbol"/> with the same <c>Kind</c>, members,
    /// and name — so the rest of the pipeline sees no change.
    public TypeSymbol? SymbolForFqn(string fqn, bool populateMembers = false)
    {
        if (!_typeIndex.TryGetValue(fqn, out var entry)) return null;
        if (!_assemblies.TryGetValue(entry.assemblyPath, out var asmEntry)) return null;

        var mr = asmEntry.mr;
        var def = mr.GetTypeDefinition(entry.handle);
        var name = mr.GetString(def.Name);
        var ns = def.Namespace.IsNil ? null : mr.GetString(def.Namespace);

        // Arity stripping for the display name.
        var tick = name.IndexOf('`');
        var displayName = tick > 0 ? name[..tick] : name;
        var arity = tick > 0 && int.TryParse(name[(tick + 1)..], out var a) ? a : 0;

        // Delegate to ExternalSymbols for the canonical interned TypeSymbol.
        // ExternalSymbols now has a metadata-backed ForFqn path, but the symbol
        // shapes are identical — the backing source changed, not the contract.
        return _symbols.ForFqn(fqn, displayName, ns, arity,
            isValueType: _isValueType.TryGetValue(fqn, out var vt) && vt,
            populateMembers: populateMembers,
            memberFactory: populateMembers
                ? () => ReadMembers(mr, def, entry.assemblyPath)
                : null,
            xmlDoc: _xmlDocs.TryGetDoc(fqn));
    }

    public readonly record struct MemberDescription(
        string Name,
        MemberKind Kind,
        BoundType ReturnType,
        bool IsStatic,
        bool Mutable,
        bool IsProperty,
        int PropertyCapability,
        string? XmlDoc);

    // Kept private to metadata decoding: custom modifiers are ABI facts used to
    // distinguish `ref T` from `ref readonly T`, not E# source-level types.
    sealed record ModifiedSignatureType(BoundType Modifier, BoundType Inner, bool Required) : BoundType
    {
        public override string EmitName => Inner.EmitName;
    }

    List<MemberDescription>
        ReadMembers(System.Reflection.Metadata.MetadataReader mr, TypeDefinition def, string assemblyPath)
    {
        var result = new List<MemberDescription>();
        var seenMethods = new HashSet<string>(StringComparer.Ordinal);

        // Methods.
        foreach (var mh in def.GetMethods())
        {
            var md = mr.GetMethodDefinition(mh);
            if ((md.Attributes & MethodAttributes.Public) == 0) continue;
            if ((md.Attributes & MethodAttributes.SpecialName) != 0) continue; // get_/set_/add_/remove_ etc.

            var mName = mr.GetString(md.Name);
            if (!seenMethods.Add(mName)) continue; // first overload wins for completion surface

            var isStatic = (md.Attributes & MethodAttributes.Static) != 0;
            var ret = DecodeReturnType(mr, md);
            var xmlDocId = BuildXmlDocId("M", mr, def, mName);
            result.Add(new MemberDescription(mName, MemberKind.Method, ret, isStatic,
                Mutable: false, IsProperty: false, PropertyCapability: 0,
                _xmlDocs.TryGetDoc(xmlDocId)));
        }

        // Properties — decoded from their getter signature.
        foreach (var ph in def.GetProperties())
        {
            var pd = mr.GetPropertyDefinition(ph);
            var pName = mr.GetString(pd.Name);
            var accessors = pd.GetAccessors();
            if (accessors.Getter.IsNil) continue;
            var getter = mr.GetMethodDefinition(accessors.Getter);
            if ((getter.Attributes & MethodAttributes.Public) == 0) continue;
            var isStatic = (getter.Attributes & MethodAttributes.Static) != 0;
            var decodedReturn = DecodePropertyReturnType(mr, getter);
            var propType = decodedReturn.Type;
            var xmlDocId = BuildXmlDocId("P", mr, def, pName);
            var setterIsPublic = !accessors.Setter.IsNil
                && (mr.GetMethodDefinition(accessors.Setter).Attributes & MethodAttributes.Public) != 0;
            var capability = ReadPropertyCapability(mr, pd);
            if (capability == 0 && decodedReturn.IsByRef)
            {
                capability = 0b000010; // durable loca
                if (!decodedReturn.IsReadOnly) capability |= 0b010000;
            }
            result.Add(new MemberDescription(pName, MemberKind.Field, propType, isStatic,
                Mutable: decodedReturn.IsByRef ? !decodedReturn.IsReadOnly : setterIsPublic,
                IsProperty: true, PropertyCapability: capability,
                _xmlDocs.TryGetDoc(xmlDocId)));
        }

        // Fields.
        foreach (var fh in def.GetFields())
        {
            var fd = mr.GetFieldDefinition(fh);
            if ((fd.Attributes & FieldAttributes.Public) == 0) continue;
            if ((fd.Attributes & FieldAttributes.SpecialName) != 0) continue;
            var fName = mr.GetString(fd.Name);
            var isStatic = (fd.Attributes & FieldAttributes.Static) != 0;
            var fieldType = DecodeFieldType(mr, fd);
            var xmlDocId = BuildXmlDocId("F", mr, def, fName);
            var mutable = (fd.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal)) == 0;
            result.Add(new MemberDescription(fName, MemberKind.Field, fieldType, isStatic,
                Mutable: mutable, IsProperty: false, PropertyCapability: 0,
                _xmlDocs.TryGetDoc(xmlDocId)));
        }

        return result;
    }

    // E# capability attributes are compiler-owned metadata.  The import path
    // deliberately reads their fixed integer argument from the PE instead of
    // loading or instantiating the referenced assembly.
    static int ReadPropertyCapability(System.Reflection.Metadata.MetadataReader mr,
        PropertyDefinition property)
    {
        foreach (var handle in property.GetCustomAttributes())
        {
            var attribute = mr.GetCustomAttribute(handle);
            if (!IsPropertyCapabilityAttribute(mr, attribute)) continue;
            try
            {
                var blob = mr.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() != 1) continue; // custom-attribute prolog
                return blob.ReadInt32();
            }
            catch { return 0; }
        }
        return 0;
    }

    static bool IsPropertyCapabilityAttribute(System.Reflection.Metadata.MetadataReader mr,
        CustomAttribute attribute)
        => AttributeTypeName(mr, attribute.Constructor) == "Esharp.Compiler.__EsharpPropertyCapabilityAttribute";

    static string? AttributeTypeName(System.Reflection.Metadata.MetadataReader mr, EntityHandle constructor)
    {
        EntityHandle type = constructor.Kind switch
        {
            HandleKind.MemberReference => mr.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => FindDeclaringType(mr, (MethodDefinitionHandle)constructor),
            _ => default,
        };
        return type.IsNil ? null : TypeFullName(mr, type);
    }

    static EntityHandle FindDeclaringType(System.Reflection.Metadata.MetadataReader mr,
        MethodDefinitionHandle method)
    {
        foreach (var typeHandle in mr.TypeDefinitions)
            if (mr.GetTypeDefinition(typeHandle).GetMethods().Contains(method))
                return typeHandle;
        return default;
    }

    static string? TypeFullName(System.Reflection.Metadata.MetadataReader mr, EntityHandle handle)
    {
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => FullName(mr, mr.GetTypeDefinition((TypeDefinitionHandle)handle).Namespace,
                mr.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
            HandleKind.TypeReference => FullName(mr, mr.GetTypeReference((TypeReferenceHandle)handle).Namespace,
                mr.GetTypeReference((TypeReferenceHandle)handle).Name),
            _ => null,
        };
    }

    static string FullName(System.Reflection.Metadata.MetadataReader mr, StringHandle ns, StringHandle name)
    {
        var nameText = mr.GetString(name);
        var nsText = ns.IsNil ? "" : mr.GetString(ns);
        return string.IsNullOrEmpty(nsText) ? nameText : $"{nsText}.{nameText}";
    }

    // Decode the return type of a MethodDefinition into an E# BoundType.
    // We decode only what codegen needs: primitives, void, and opaque
    // ExternalType for anything complex (the compiler never re-binds external
    // return types through metadata; it uses the BoundType stamped at bind time).
    BoundType DecodeReturnType(System.Reflection.Metadata.MetadataReader mr,
        MethodDefinition md)
    {
        try
        {
            var sig = md.DecodeSignature(new SignatureDecoder(mr, this), genericContext: null!);
            return sig.ReturnType;
        }
        catch
        {
            return new ExternalType("object");
        }
    }

    (BoundType Type, bool IsByRef, bool IsReadOnly) DecodePropertyReturnType(
        System.Reflection.Metadata.MetadataReader mr, MethodDefinition getter)
    {
        try
        {
            var signature = getter.DecodeSignature(
                new SignatureDecoder(mr, this, preserveModifiers: true), genericContext: null!);
            var isByRef = false;
            var isReadOnly = false;
            var current = signature.ReturnType;
            while (true)
            {
                switch (current)
                {
                    case ModifiedSignatureType modified:
                        if (modified.Required && modified.Modifier.EmitName.EndsWith("InAttribute", StringComparison.Ordinal))
                            isReadOnly = true;
                        current = modified.Inner;
                        continue;
                    case ByRefBoundType byRef:
                        isByRef = true;
                        current = byRef.Inner;
                        continue;
                    default:
                        return (current, isByRef, isReadOnly);
                }
            }
        }
        catch
        {
            return (new ExternalType("object"), false, false);
        }
    }

    BoundType DecodeFieldType(System.Reflection.Metadata.MetadataReader mr,
        FieldDefinition fd)
    {
        try
        {
            return fd.DecodeSignature(new SignatureDecoder(mr, this), genericContext: null!);
        }
        catch
        {
            return new ExternalType("object");
        }
    }

    // ── BoundType → metadata name translation ─────────────────────────────

    /// The FQN the metadata index would hold for a given <see cref="BoundType"/>.
    /// Used by codegen when it needs a string key into the type index without
    /// going through reflection.
    public string? FqnForBoundType(BoundType type) => type switch
    {
        PrimitiveType p => PrimitiveFqn(p.Name),
        ExternalType { TypeArgs.Count: 0 } ext => ResolveBareName(ext.Name),
        ExternalType gen => ResolveBareName(gen.Name),
        VoidType => "System.Void",
        _ => null,
    };

    static string? PrimitiveFqn(string name) => name switch
    {
        "int"    => "System.Int32",
        "long"   => "System.Int64",
        "short"  => "System.Int16",
        "byte"   => "System.Byte",
        "sbyte"  => "System.SByte",
        "uint"   => "System.UInt32",
        "ulong"  => "System.UInt64",
        "ushort" => "System.UInt16",
        "float"  => "System.Single",
        "double" => "System.Double",
        "decimal" => "System.Decimal",
        "bool"   => "System.Boolean",
        "char"   => "System.Char",
        "string" => "System.String",
        "object" => "System.Object",
        "void"   => "System.Void",
        _ => null,
    };

    // ── XML doc key helper ─────────────────────────────────────────────────

    static string BuildXmlDocId(string prefix, System.Reflection.Metadata.MetadataReader mr,
        TypeDefinition def, string memberName)
    {
        var ns = def.Namespace.IsNil ? "" : mr.GetString(def.Namespace);
        var typeName = mr.GetString(def.Name);
        var tick = typeName.IndexOf('`');
        if (tick > 0) typeName = typeName[..tick];
        var fqn = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
        return $"{prefix}:{fqn}.{memberName}";
    }

    // ── Ref-assembly resolution (same heuristic as ILTypeResolver) ─────────

    static bool IsRefAssemblyPath(string path)
    {
        var n = path.Replace('\\', '/');
        return n.Contains("/ref/net") || (n.Contains("/packs/") && n.Contains(".Ref/"));
    }

    static string? TryFindImplementationAssembly(string refPath)
    {
        var fileName = Path.GetFileName(refPath);
        var normalized = refPath.Replace('\\', '/');
        var packsIdx = normalized.IndexOf("/packs/", StringComparison.Ordinal);
        if (packsIdx >= 0)
        {
            var dotnetRoot = normalized[..packsIdx];
            var afterPacks = normalized[(packsIdx + "/packs/".Length)..];
            var segments = afterPacks.Split('/');
            if (segments.Length >= 5 && segments[2] == "ref")
            {
                var packRef = segments[0];
                var version = segments[1];
                var packName = packRef.EndsWith(".Ref", StringComparison.Ordinal)
                    ? packRef[..^4] : packRef;
                var implPath = $"{dotnetRoot}/shared/{packName}/{version}/{fileName}";
                if (File.Exists(implPath)) return implPath;
                var sharedDir = $"{dotnetRoot}/shared/{packName}";
                if (Directory.Exists(sharedDir))
                    foreach (var vd in Directory.GetDirectories(sharedDir))
                    {
                        var c = Path.Combine(vd, fileName);
                        if (File.Exists(c)) return c;
                    }
            }
        }
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir is not null)
        {
            var c = Path.Combine(runtimeDir, fileName);
            if (File.Exists(c)) return c;
        }
        return null;
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    public void Dispose()
    {
        foreach (var (pe, _) in _assemblies.Values)
            try { pe.Dispose(); } catch { /* best-effort */ }
        _assemblies.Clear();
    }

    // ── Nested types ───────────────────────────────────────────────────────

    public enum MemberKind { Method, Field }

    /// A minimal <see cref="ISignatureTypeProvider{TType,TGenericContext}"/>
    /// that decodes PE method/field signatures into E# <see cref="BoundType"/>s
    /// without reflection. Only the cases that matter for the completion surface
    /// and codegen metadata queries are decoded precisely; the rest fall back to
    /// <see cref="ExternalType"/>("object").
    sealed class SignatureDecoder
        : ISignatureTypeProvider<BoundType, object?>
    {
        readonly System.Reflection.Metadata.MetadataReader _mr;
        readonly MetadataReader _resolver;
        readonly bool _preserveModifiers;

        public SignatureDecoder(System.Reflection.Metadata.MetadataReader mr,
            MetadataReader resolver, bool preserveModifiers = false)
        {
            _mr = mr;
            _resolver = resolver;
            _preserveModifiers = preserveModifiers;
        }

        public BoundType GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => new PrimitiveType("bool"),
            PrimitiveTypeCode.Char    => new PrimitiveType("char"),
            PrimitiveTypeCode.SByte   => new PrimitiveType("sbyte"),
            PrimitiveTypeCode.Byte    => new PrimitiveType("byte"),
            PrimitiveTypeCode.Int16   => new PrimitiveType("short"),
            PrimitiveTypeCode.UInt16  => new PrimitiveType("ushort"),
            PrimitiveTypeCode.Int32   => new PrimitiveType("int"),
            PrimitiveTypeCode.UInt32  => new PrimitiveType("uint"),
            PrimitiveTypeCode.Int64   => new PrimitiveType("long"),
            PrimitiveTypeCode.UInt64  => new PrimitiveType("ulong"),
            PrimitiveTypeCode.Single  => new PrimitiveType("float"),
            PrimitiveTypeCode.Double  => new PrimitiveType("double"),
            PrimitiveTypeCode.String  => new PrimitiveType("string"),
            PrimitiveTypeCode.Object  => new ExternalType("object"),
            PrimitiveTypeCode.Void    => new VoidType(),
            PrimitiveTypeCode.IntPtr  => new ExternalType("IntPtr"),
            PrimitiveTypeCode.UIntPtr => new ExternalType("UIntPtr"),
            _ => new ExternalType("object"),
        };

        public BoundType GetTypeFromDefinition(System.Reflection.Metadata.MetadataReader mr,
            TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var def = mr.GetTypeDefinition(handle);
            var name = mr.GetString(def.Name);
            return new ExternalType(name);
        }

        public BoundType GetTypeFromReference(System.Reflection.Metadata.MetadataReader mr,
            TypeReferenceHandle handle, byte rawTypeKind)
        {
            var tr = mr.GetTypeReference(handle);
            var name = mr.GetString(tr.Name);
            return new ExternalType(name);
        }

        public BoundType GetSZArrayType(BoundType elementType)
            => new ArrayBoundType(elementType);

        // Multi-dimensional arrays collapse to the same array bound type (E# models
        // arrays by element type, not rank) — sufficient for external metadata interop.
        public BoundType GetArrayType(BoundType elementType, ArrayShape shape)
            => new ArrayBoundType(elementType);

        public BoundType GetGenericInstantiation(BoundType genericType,
            ImmutableArray<BoundType> typeArguments)
        {
            var baseName = genericType is ExternalType ext ? ext.Name : "object";
            return new ExternalType(baseName, typeArguments.ToList());
        }

        public BoundType GetByReferenceType(BoundType elementType)
            => new ByRefBoundType(elementType);

        public BoundType GetPointerType(BoundType elementType)
            => new HeapPointerBoundType(elementType);

        public BoundType GetTypeFromSpecification(System.Reflection.Metadata.MetadataReader mr,
            object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            var spec = mr.GetTypeSpecification(handle);
            return spec.DecodeSignature(this, genericContext);
        }

        public BoundType GetFunctionPointerType(MethodSignature<BoundType> signature)
            => new ExternalType("object"); // function pointers not used in the E# surface model

        public BoundType GetGenericMethodParameter(object? genericContext, int index)
            => new ExternalType($"T{index}");

        public BoundType GetGenericTypeParameter(object? genericContext, int index)
            => new ExternalType($"T{index}");

        public BoundType GetModifiedType(BoundType modifier, BoundType unmodifiedType,
            bool isRequired)
            => _preserveModifiers
                ? new ModifiedSignatureType(modifier, unmodifiedType, isRequired)
                : unmodifiedType; // modreq/modopt — otherwise transparent to E#

        public BoundType GetPinnedType(BoundType elementType)
            => elementType;
    }
}
