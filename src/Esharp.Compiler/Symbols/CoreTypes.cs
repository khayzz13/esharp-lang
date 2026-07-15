namespace Esharp.Symbols;

/// The compiler's pre-resolved well-known types — the primitives and the runtime
/// generics (`Result`, `Spawned`, `Chan`, tuples) that the binder and the IL backend
/// would otherwise re-resolve by string-sniffing at every use site. Built once per
/// compilation and threaded as a dependency (owned by CompilationData).
///
/// This is F#'s `TcGlobals` in spirit — a single home for the intrinsic types —
/// but deliberately a small, table-driven one, not the hand-maintained
/// ~2,000-line constructor F# grew (an explicit anti-lesson from that study).
///
/// CoreTypes interns its own symbols, kept apart from the user-declared type
/// interner in SymbolTable so a primitive can never collide with namespace-scoped
/// user-type lookup. A *use* of one of these is a `TypeRef` pointing at it; the
/// reference identity is the stamp.
public sealed class CoreTypes
{
    // Canonical E# primitive spellings (lower-case), mirroring the set
    // Binder.ResolveType recognizes. `void` is interned too but exposed as Void.
    static readonly string[] PrimitiveNames =
    {
        "int", "long", "short", "byte", "sbyte",
        "uint", "ulong", "ushort",
        "float", "double", "decimal",
        "bool", "char", "string", "object",
    };

    readonly Dictionary<string, TypeSymbol> _primitives = new(StringComparer.Ordinal);
    readonly Dictionary<int, TypeSymbol> _tuples = new();
    readonly Dictionary<(string Name, int Arity), TypeSymbol> _externals = new();

    public TypeSymbol Void { get; }

    public TypeSymbol Int => _primitives["int"];
    public TypeSymbol Long => _primitives["long"];
    public TypeSymbol Bool => _primitives["bool"];
    public TypeSymbol Double => _primitives["double"];
    public TypeSymbol Float => _primitives["float"];
    public TypeSymbol String => _primitives["string"];
    public TypeSymbol Char => _primitives["char"];
    public TypeSymbol Object => _primitives["object"];

    /// `Result<T, E>` — arity 2.
    public TypeSymbol Result { get; }
    /// `Chan<T>` — arity 1.
    public TypeSymbol Chan { get; }

    public CoreTypes()
    {
        foreach (var name in PrimitiveNames)
            _primitives[name] = new TypeSymbol { Name = name, Arity = 0, TypeKind = TypeSymbolKind.Primitive };

        // `void` is a primitive-kind symbol but never a value type, so it is not in
        // the primitive lookup table — a `-> void` return resolves through Void.
        Void = new TypeSymbol { Name = "void", Arity = 0, TypeKind = TypeSymbolKind.Primitive };

        // Runtime generics. Namespace is left null: identity here is (name, arity),
        // and the binder maps the dedicated ResultType / ChanType bound
        // nodes onto these by case, not by namespace string.
        Result = new TypeSymbol { Name = "Result", Arity = 2, TypeKind = TypeSymbolKind.External };
        Chan = new TypeSymbol { Name = "Chan", Arity = 1, TypeKind = TypeSymbolKind.External };
    }

    /// The interned primitive symbol for a canonical E# primitive spelling, or null
    /// if `name` is not a primitive (`void` excluded — see Void).
    public TypeSymbol? Primitive(string name) =>
        _primitives.TryGetValue(name, out var s) ? s : null;

    public bool IsPrimitiveName(string name) => _primitives.ContainsKey(name);

    /// The interned `ValueTuple` symbol of the given element count. Tuples are
    /// structural, so they intern on demand per arity rather than being enumerated.
    public TypeSymbol Tuple(int arity)
    {
        if (_tuples.TryGetValue(arity, out var s)) return s;
        s = new TypeSymbol { Name = "ValueTuple", Arity = arity, TypeKind = TypeSymbolKind.External };
        _tuples[arity] = s;
        return s;
    }

    /// Interned identity for an external (BCL / not-yet-structured) type, by
    /// (name, arity). A holding pen so a `TypeRef` always has a real symbol leaf
    /// even before step 4 gives every external a fully structured representation —
    /// identity is still by reference, never by re-parsing the name string.
    public TypeSymbol External(string name, int arity = 0)
    {
        if (_externals.TryGetValue((name, arity), out var s)) return s;
        s = new TypeSymbol { Name = name, Arity = arity, TypeKind = TypeSymbolKind.External };
        _externals[(name, arity)] = s;
        return s;
    }
}
