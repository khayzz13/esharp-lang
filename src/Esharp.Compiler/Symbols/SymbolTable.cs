using Esharp.BoundTree;          // BoundType, DataClassification
using Esharp.BoundTree;   // ICSharpTypeHandle (seam: moves to Esharp.Metadata in pillar 3)
using Esharp.Syntax;    // MemberSyntax

namespace Esharp.Symbols;

/// Per-compilation interner for symbols — the ONE registry. A type is interned once
/// per (namespace, name, arity) and handed back by reference on every subsequent
/// lookup, so identity is reference identity. Owns the namespace index (which
/// namespaces exist, which namespaces declare a given simple name), the per-namespace
/// host symbols that free functions and consts attach to, and the function /
/// promoted-function indexes. Populated during the binder's declaration passes and
/// consumed by later resolution — the single home that replaces the string-keyed
/// blackboard.
public sealed class SymbolTable
{
    readonly Dictionary<string, TypeSymbol> _types = new(StringComparer.Ordinal);
    readonly Dictionary<string, TypeSymbol> _namespaceHosts = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<string>> _namespacesByName = new(StringComparer.Ordinal);
    readonly HashSet<string> _knownNamespaces = new(StringComparer.Ordinal);
    readonly Dictionary<string, MethodSymbol> _promotedFunctions = new(StringComparer.Ordinal);
    readonly Dictionary<string, MethodSymbol> _functions = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<MethodSymbol>> _functionOverloads = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<TypeSymbol>> _bySimpleName = new(StringComparer.Ordinal);
    readonly Dictionary<string, ConstSymbol> _constants = new(StringComparer.Ordinal);

    static string Key(string? ns, string name, int arity) => $"{ns}::{name}`{arity}";

    /// Intern (or fetch) the type symbol for a declaration. Idempotent: the same
    /// (ns, name, arity) always returns the same instance. Also feeds the
    /// namespace index used by bare-name scoping and ambiguity checks.
    public TypeSymbol GetOrAdd(
        string name, int arity, TypeSymbolKind kind, string? ns = null,
        DataClassification classification = DataClassification.Struct, MemberSyntax? decl = null,
        ICSharpTypeHandle? csharpHandle = null)
    {
        var key = Key(ns, name, arity);
        if (_types.TryGetValue(key, out var existing)) return existing;
        var sym = new TypeSymbol
        {
            Name = name, Namespace = ns, Arity = arity, TypeKind = kind,
            Classification = classification, Decl = decl, CSharpHandle = csharpHandle,
        };
        _types[key] = sym;
        if (kind is not TypeSymbolKind.NamespaceHost)
        {
            if (!_bySimpleName.TryGetValue(name, out var list))
                _bySimpleName[name] = list = [];
            list.Add(sym);
            if (ns is not null)
            {
                if (!_namespacesByName.TryGetValue(name, out var set))
                    _namespacesByName[name] = set = [];
                if (!set.Contains(ns)) set.Add(ns);
            }
        }
        return sym;
    }

    /// Intern (or fetch) a NESTED type symbol declared inside <paramref name="declaringType"/>.
    /// Keyed by the declaring chain (`ns::Outer+Inner`N`) so a nested `Inner` never
    /// collides with a top-level `Inner`, while keeping the simple `Name` and recording
    /// `DeclaringType`. Indexed by simple name too, so a bare reference within the
    /// enclosing scope resolves.
    public TypeSymbol GetOrAddNested(
        string name, int arity, TypeSymbolKind kind, TypeSymbol declaringType,
        DataClassification classification = DataClassification.Struct, MemberSyntax? decl = null)
    {
        var ns = declaringType.Namespace;
        var key = Key(ns, NestedKeyPrefix(declaringType) + name, arity);
        if (_types.TryGetValue(key, out var existing)) return existing;
        var sym = new TypeSymbol
        {
            Name = name, Namespace = ns, Arity = arity, TypeKind = kind,
            Classification = classification, Decl = decl, DeclaringType = declaringType,
        };
        _types[key] = sym;
        // Deliberately NOT indexed in `_bySimpleName`: a nested type does not leak into
        // bare-name resolution. Like C#, a bare `Inner` resolves only inside its
        // enclosing type's scope (ResolveNestedInScope); from outside it is reached by
        // the qualified `Outer.Inner` path (ResolveNestedQualified).
        return sym;
    }

    /// Resolve a bare nested-type name within an enclosing type's scope: search
    /// <paramref name="enclosing"/> and its declaring-type ancestors for a nested type
    /// of this (name, arity). This is the C# rule that a bare `Inner` is visible inside
    /// `Outer` (and any type nested deeper) but nowhere else.
    public TypeSymbol? ResolveNestedInScope(string name, int arity, TypeSymbol enclosing)
    {
        for (TypeSymbol? t = enclosing; t is not null; t = t.DeclaringType)
            if (TryGetNested(name, arity, t) is { } s)
                return s;
        return null;
    }

    /// Resolve a qualified nested-type reference `Outer.Inner` (or deeper,
    /// `A.B.Inner`, and namespace-prefixed `Ns.Outer.Inner`). Walks the dotted
    /// segments: the leading run resolves to a top-level type, each remaining segment
    /// to a nested type of the prior. Returns null if any segment fails to resolve as
    /// a nested type — leaving the caller's existing namespace/external paths intact.
    public TypeSymbol? ResolveNestedQualified(string dottedName, int arity)
    {
        var dot = dottedName.IndexOf('.');
        if (dot <= 0) return null;
        var segments = dottedName.Split('.');
        // Find the longest leading prefix that names a top-level type. A namespace
        // prefix (`Ns.Outer.Inner`) is handled by trying successive split points.
        for (var headLen = 1; headLen < segments.Length; headLen++)
        {
            var headName = string.Join('.', segments[..headLen]);
            // The head is a top-level type only — resolve arity-0 (nested containers
            // are non-generic in the common case; generic outers resolve by base name).
            var head = ResolveCore(headName, 0, requireView: false);
            if (head is null) continue;
            TypeSymbol? cur = head;
            var ok = true;
            for (var i = headLen; i < segments.Length && cur is not null; i++)
            {
                var wantArity = i == segments.Length - 1 ? arity : 0;
                cur = TryGetNested(segments[i], wantArity, cur) ?? TryGetNested(segments[i], 0, cur);
                if (cur is null) { ok = false; break; }
            }
            if (ok && cur is not null) return cur;
        }
        return null;
    }

    /// The declaring-chain key prefix for a nested type whose enclosing type is
    /// <paramref name="t"/> — `Outer+` (or `Outer+Mid+` for deeper nesting).
    static string NestedKeyPrefix(TypeSymbol t) =>
        (t.DeclaringType is { } d ? NestedKeyPrefix(d) : "") + t.Name + "+";

    /// The nested-qualified simple-name path of a type — `Inner` for a top-level type,
    /// `Outer+Inner` for a nested one (the CLR metadata reflection-name segment).
    public static string NestedQualifiedName(TypeSymbol t) =>
        t.DeclaringType is { } d ? NestedQualifiedName(d) + "+" + t.Name : t.Name;

    /// Look up by (name, arity) within a namespace; null if not interned.
    public TypeSymbol? TryGet(string name, int arity, string? ns = null) =>
        _types.TryGetValue(Key(ns, name, arity), out var s) ? s : null;

    /// Look up a nested type by simple name within a declaring type.
    public TypeSymbol? TryGetNested(string name, int arity, TypeSymbol declaringType) =>
        _types.TryGetValue(Key(declaringType.Namespace, NestedKeyPrefix(declaringType) + name, arity), out var s) ? s : null;

    /// First interned symbol with this simple name and arity, across all namespaces —
    /// for resolution sites that only know the bare name. Null if none.
    public TypeSymbol? TryGetByName(string name, int arity)
    {
        foreach (var s in _types.Values)
            if (s.Arity == arity && string.Equals(s.Name, name, StringComparison.Ordinal))
                return s;
        return null;
    }

    public IReadOnlyCollection<TypeSymbol> All => _types.Values;

    /// First declared symbol with this simple name, any arity/namespace — for the
    /// resolution sites that historically read the bare-keyed declaration maps.
    /// Cross-namespace ambiguity is reported separately (ES2151); collisions are
    /// rare and the FIRST declaration wins deterministically.
    public TypeSymbol? FindType(string simpleName) =>
        _bySimpleName.TryGetValue(simpleName, out var list) && list.Count > 0 ? list[0] : null;

    /// First declared symbol with this simple name of a given kind set.
    public TypeSymbol? FindType(string simpleName, params TypeSymbolKind[] kinds)
    {
        if (!_bySimpleName.TryGetValue(simpleName, out var list)) return null;
        foreach (var s in list)
            foreach (var k in kinds)
                if (s.TypeKind == k) return s;
        return null;
    }

    /// All declared symbols sharing a simple name (cross-namespace collisions).
    public IReadOnlyList<TypeSymbol> FindAll(string simpleName) =>
        _bySimpleName.TryGetValue(simpleName, out var list) ? list : [];

    /// Every interned symbol of the given kinds, in interning order — the typed
    /// replacement for iterating a per-kind declaration dictionary.
    public IEnumerable<TypeSymbol> AllOfKind(params TypeSymbolKind[] kinds)
    {
        foreach (var s in _types.Values)
            foreach (var k in kinds)
                if (s.TypeKind == k) { yield return s; break; }
    }

    /// Find a type by a possibly namespace-qualified name (`A.Widget`): a known
    /// namespace prefix narrows to that namespace's declaration; a bare name
    /// falls back to the first declaration (ES2151 owns ambiguity reporting).
    public TypeSymbol? FindTypeQualified(string name, params TypeSymbolKind[] kinds)
    {
        var dot = name.IndexOf('.');
        if (dot > 0 && IsKnownNamespace(name[..dot]))
        {
            var ns = name[..dot];
            var simple = name[(dot + 1)..];
            foreach (var s in FindAll(simple))
                if (s.Namespace == ns)
                    foreach (var k in kinds)
                        if (s.TypeKind == k) return s;
            return null;
        }
        return kinds.Length == 0 ? FindType(name) : FindType(name, kinds);
    }

    /// Resolve the canonical bound view of a type by (possibly qualified) name and
    /// arity — the replacement for the TypeRegistry's `RegKey` lookups, with the
    /// same fallbacks: a known-namespace prefix narrows the search; an arity miss
    /// falls back to the arity-0 declaration (static funcs, C# adapters, and other
    /// bare-keyed entries the old registry resolved by base name).
    public BoundType? ResolveBound(string name, int arity) =>
        ResolveCore(name, arity, requireView: true)?.BoundView;

    /// The symbol-returning face of the same resolution — for sites that need the
    /// declaration identity (fields, members, base chain) rather than the bound view.
    public TypeSymbol? ResolveSymbol(string name, int arity) =>
        ResolveCore(name, arity, requireView: false);

    TypeSymbol? ResolveCore(string name, int arity, bool requireView)
    {
        var dot = name.IndexOf('.');
        if (dot > 0 && IsKnownNamespace(name[..dot]))
        {
            var ns = name[..dot];
            var simple = name[(dot + 1)..];
            TypeSymbol? nsArity0 = null;
            foreach (var s in FindAll(simple))
            {
                if (s.Namespace != ns || (requireView && s.BoundView is null)) continue;
                if (s.Arity == arity) return s;
                if (s.Arity == 0) nsArity0 ??= s;
            }
            return nsArity0;
        }

        TypeSymbol? arity0 = null;
        foreach (var s in FindAll(name))
        {
            if (requireView && s.BoundView is null) continue;
            if (s.Arity == arity) return s;
            if (s.Arity == 0) arity0 ??= s;
        }
        return arity0;
    }

    /// The C#-fusion seam: intern a Roslyn-declared type as an ExternalCSharp
    /// symbol carrying its adapter handle, with the bound view resolution reads.
    /// No-op when an E# declaration already owns the name.
    public TypeSymbol RegisterCSharpType(ICSharpTypeHandle handle)
    {
        var sym = GetOrAdd(handle.Name, 0, TypeSymbolKind.ExternalCSharp, csharpHandle: handle);
        sym.BoundView ??= new ExternalCSharpType(handle) { Symbol = sym };
        return sym;
    }

    // === Typed declaration views — the replacements for the per-kind decl maps ===

    public DataDeclarationSyntax? DataDecl(string name) =>
        FindTypeQualified(name, TypeSymbolKind.Struct, TypeSymbolKind.Class)?.Decl as DataDeclarationSyntax;

    public ChoiceDeclarationSyntax? ChoiceDecl(string name) =>
        FindTypeQualified(name, TypeSymbolKind.Union, TypeSymbolKind.RefUnion)?.Decl as ChoiceDeclarationSyntax;

    public EnumDeclarationSyntax? EnumDecl(string name) =>
        FindTypeQualified(name, TypeSymbolKind.Enum)?.Decl as EnumDeclarationSyntax;

    public InterfaceDeclarationSyntax? InterfaceDecl(string name) =>
        FindTypeQualified(name, TypeSymbolKind.Interface)?.Decl as InterfaceDeclarationSyntax;

    public DelegateDeclarationSyntax? DelegateDecl(string name) =>
        FindTypeQualified(name, TypeSymbolKind.Delegate)?.Decl as DelegateDeclarationSyntax;

    /// Register an explicit static facet on an existing declaration identity.
    /// Facets are not synthesized by receiver attachment: this is called only for
    /// a real `static Foo { ... }` declaration.
    public void RegisterStaticFacet(TypeSymbol symbol, StaticFuncDeclarationSyntax declaration) =>
        symbol.StaticFacet ??= declaration;

    public StaticFuncDeclarationSyntax? StaticFuncDecl(string name, int arity = 0, string? ns = null)
    {
        if (ns is not null)
            return TryGet(name, arity, ns)?.StaticFacet;
        foreach (var symbol in FindAll(name))
            if (symbol.Arity == arity && symbol.StaticFacet is not null)
                return symbol.StaticFacet;
        return null;
    }

    // === The namespace index ===

    /// Every namespace declared anywhere in the compilation — distinguishes an
    /// internal `using "Geometry"` from an external BCL `using "System.Text"`,
    /// and validates the prefix of a qualified `NS.Type` / `NS.fn` reference.
    public IReadOnlyCollection<string> KnownNamespaces => _knownNamespaces;

    public bool IsKnownNamespace(string ns) => _knownNamespaces.Contains(ns);

    /// Every namespace a given simple type name is declared in — a name visible
    /// from two in-scope namespaces is ambiguous (the user must qualify).
    public IReadOnlyList<string> NamespacesOf(string simpleName) =>
        _namespacesByName.TryGetValue(simpleName, out var set) ? set : [];

    /// The host symbol of a namespace — what its free functions, consts, and
    /// static-funcs attach to. Interned on first touch; registering a namespace
    /// is registering its host.
    public TypeSymbol Host(string ns)
    {
        if (_namespaceHosts.TryGetValue(ns, out var host)) return host;
        _knownNamespaces.Add(ns);
        // Constructed directly, never through the type interner: a host lives in
        // its own keyspace, so `data Big` inside `namespace Big` interns its own
        // symbol instead of colliding with the host's (ns, name, arity) key.
        host = new TypeSymbol { Name = ns, Namespace = ns, Arity = 0, TypeKind = TypeSymbolKind.NamespaceHost };
        _namespaceHosts[ns] = host;
        return host;
    }

    // === Function symbols ===

    readonly Dictionary<FunctionDeclarationSyntax, MethodSymbol> _byDecl = new(ReferenceEqualityComparer.Instance);

    /// Register a function symbol under its lookup key: the bare name for free
    /// functions, `Host.name` for static-func members. Idempotent per key. Also
    /// feeds the by-declaration index so the bound function node and the emitter
    /// can recover the SAME interned symbol the call site resolves to — the
    /// reference-identity bridge that lets the IL emitter key a call's target
    /// method off the symbol instead of a full-module name walk.
    public void AddFunction(string key, MethodSymbol symbol)
    {
        _functions.TryAdd(key, symbol);
        if (!_functionOverloads.TryGetValue(key, out var overloads))
            _functionOverloads[key] = overloads = [];
        overloads.Add(symbol);
        if (symbol.Decl is { } d) _byDecl.TryAdd(d, symbol);
    }

    public MethodSymbol? TryGetFunction(string key) =>
        _functions.TryGetValue(key, out var m) ? m : null;

    public MethodSymbol? TryGetFunction(string key, int parameterCount, int? typeParameterCount = null) =>
        _functionOverloads.TryGetValue(key, out var overloads)
            ? overloads.FirstOrDefault(m => m.DeclaredArity == parameterCount
                && (typeParameterCount is null || m.TypeParameters.Count == typeParameterCount))
            : null;

    /// The interned symbol for a function declaration node — the identity bridge
    /// from a `BoundFunctionDeclaration` (which carries its syntax) back to the
    /// spine. Covers free, static-func-member, and promoted functions.
    public MethodSymbol? FunctionForDecl(FunctionDeclarationSyntax decl) =>
        _byDecl.TryGetValue(decl, out var m) ? m : null;

    public bool HasFunction(string key) => _functions.ContainsKey(key);

    public IReadOnlyDictionary<string, MethodSymbol> Functions => _functions;

    // === Promoted-function index ===

    /// Free functions promoted onto a receiver type (value- or pointer-receiver),
    /// keyed by function name → the MethodSymbol attached to that receiver's
    /// TypeSymbol.Members.
    public IReadOnlyDictionary<string, MethodSymbol> PromotedFunctions => _promotedFunctions;

    public void AddPromoted(string name, MethodSymbol symbol)
    {
        _promotedFunctions.TryAdd(name, symbol);
        if (symbol.Decl is { } d) _byDecl.TryAdd(d, symbol);
    }

    public MethodSymbol? TryGetPromoted(string name) =>
        _promotedFunctions.TryGetValue(name, out var m) ? m : null;

    // === Namespace-level constants ===

    /// Register a namespace const. The bare-name index mirrors the historical
    /// global constant table (consts fold by simple name across the program).
    public void AddConstant(ConstSymbol c)
    {
        c.DeclaringHost.AddConstant(c);
        _constants.TryAdd(c.Name, c);
    }

    public ConstSymbol? TryGetConstant(string name) =>
        _constants.TryGetValue(name, out var c) ? c : null;

    // ============================================================================
    // [Δ] Incremental invalidation — per-unit remove / replace
    // ============================================================================
    // Today the table is append-only (a registration is permanent for the life of
    // the compilation). These methods are the SINGLE change that unblocks dependency-
    // tracked incremental rebind:
    //   1. RemoveUnit — evict all symbols whose declaration came from the given file.
    //   2. ReplaceUnit — RemoveUnit then immediately re-register from the freshly-parsed
    //      declarations of that file.
    // Identity invariant: the TypeSymbol INSTANCES that survive (unchanged files) keep
    // their reference identity. Symbols that are re-registered get FRESH instances; any
    // stale TypeRef pointing at an old evicted instance remains structurally valid but
    // is stale — callers that hold a cached BoundType are expected to invalidate their
    // own caches when a rebind cycle triggers (the Compilation layer owns that).

    /// Evict all symbols whose declaration syntax lives in <paramref name="filePath"/>.
    /// Called by the incremental pipeline when a source file changes.
    public void RemoveUnit(string filePath)
    {
        var toRemove = _types.Where(kv => kv.Value.Decl?.Span.File == filePath)
                             .Select(kv => kv.Key).ToList();
        foreach (var key in toRemove)
        {
            if (_types.Remove(key, out var sym))
            {
                sym.InvalidateMembers();
                // Remove from simple-name index
                if (_bySimpleName.TryGetValue(sym.Name, out var list))
                {
                    list.Remove(sym);
                    if (list.Count == 0) _bySimpleName.Remove(sym.Name);
                }
                // Remove from namespace-by-name index
                if (sym.Namespace is { } ns && _namespacesByName.TryGetValue(sym.Name, out var nsSet))
                    nsSet.Remove(ns);
            }
        }

        // Evict methods whose declaration syntax lives in this file
        var methodKeys = _functions.Where(kv => kv.Value.Decl?.Span.File == filePath)
                                   .Select(kv => kv.Key).ToList();
        foreach (var key in methodKeys) _functions.Remove(key);

        var promotedKeys = _promotedFunctions.Where(kv => kv.Value.Decl?.Span.File == filePath)
                                              .Select(kv => kv.Key).ToList();
        foreach (var key in promotedKeys) _promotedFunctions.Remove(key);

        // Evict constants declared in this file
        var constKeys = _constants.Where(kv => kv.Value.Span.File == filePath)
                                  .Select(kv => kv.Key).ToList();
        foreach (var key in constKeys) _constants.Remove(key);

        // Clean the by-decl index for the evicted methods
        var declKeys = _byDecl.Where(kv => kv.Key.Span.File == filePath)
                              .Select(kv => kv.Key).ToList();
        foreach (var key in declKeys) _byDecl.Remove(key);
    }

    /// Evict all symbols from <paramref name="filePath"/> and prepare for re-registration.
    /// The caller is expected to run the declaration pass immediately after to re-populate.
    /// Returns the set of simple names that were evicted (for diagnostics / change tracking).
    public IReadOnlyList<string> BeginReplaceUnit(string filePath)
    {
        var evicted = _types.Where(kv => kv.Value.Decl?.Span.File == filePath)
                            .Select(kv => kv.Value.Name).Distinct().ToList();
        RemoveUnit(filePath);
        return evicted;
    }
}
