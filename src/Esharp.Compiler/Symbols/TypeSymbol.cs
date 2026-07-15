using Esharp.BoundTree;          // BoundType, DataClassification
using Esharp.Syntax;    // MemberSyntax, ICSharpTypeHandle

namespace Esharp.Symbols;

public enum TypeSymbolKind
{
    Struct, Class, Union, RefUnion, Enum, Interface, StaticFunc, Delegate,
    Primitive, External, ExternalCSharp, TypeParameter, InferencePending,
    /// One per namespace: the host every free function, namespace const, and
    /// static-func of that namespace attaches to.
    NamespaceHost,
}

/// The *declaration* identity of a type — interned once per (namespace, name, arity) and
/// shared by reference, so identity is reference identity. Carries what the rest of the
/// compiler otherwise re-derives from strings: the arity, the CLR `data` lowering choice,
/// the resolved member sets (methods, fields, cases, conformances, base chain), and — for
/// C#-fusion types — the Roslyn-side handle.
/// A *use* of a type is a TypeRef pointing at one of these.
public sealed record TypeSymbol : ITypeSymbol, INamespaceSymbol
{
    // ---- ISymbol public versioned surface ----

    public required string Name { get; init; }

    /// SymbolKind.Namespace for namespace host symbols; SymbolKind.Type for all others.
    public SymbolKind Kind => TypeKind == TypeSymbolKind.NamespaceHost
        ? SymbolKind.Namespace
        : SymbolKind.Type;

    public ISymbol? ContainingSymbol => (ISymbol?)DeclaringType ?? NamespaceHostSymbol;

    // Accessibility: top-level bare → Internal, nested bare → Private (C# nested-type default).
    public DeclaredAccessibility DeclaredAccessibility =>
        DeclaringType is not null
            ? (IsPublic ? DeclaredAccessibility.Public : DeclaredAccessibility.Private)
            : (IsPublic ? DeclaredAccessibility.Public : DeclaredAccessibility.Internal);

    /// [Δ] XML doc comment for LSP hover (WS7). Populated by the parser from `///` trivia
    /// adjacent to the type declaration.
    public string? XmlDoc { get; init; }

    // ---- ITypeSymbol public surface ----

    /// The E# type kind for the public surface. NamespaceHost maps to External
    /// (the public surface uses INamespaceSymbol for the namespace-host path).
    SymbolTypeKind ITypeSymbol.TypeKind => PublicTypeKind;

    /// Helper that avoids the name clash: `TypeKind` property vs the public `SymbolTypeKind` enum.
    internal SymbolTypeKind PublicTypeKind => TypeKind switch
    {
        TypeSymbolKind.Struct        => SymbolTypeKind.Struct,
        TypeSymbolKind.Class         => SymbolTypeKind.Class,
        TypeSymbolKind.Union         => SymbolTypeKind.Union,
        TypeSymbolKind.RefUnion      => SymbolTypeKind.RefUnion,
        TypeSymbolKind.Enum          => SymbolTypeKind.Enum,
        TypeSymbolKind.Interface     => SymbolTypeKind.Interface,
        TypeSymbolKind.StaticFunc    => SymbolTypeKind.StaticFunc,
        TypeSymbolKind.Delegate      => SymbolTypeKind.Delegate,
        TypeSymbolKind.TypeParameter => SymbolTypeKind.TypeParameter,
        _                            => SymbolTypeKind.External,
    };

    int ITypeSymbol.Arity => Arity;

    ITypeSymbol? ITypeSymbol.BaseType => BaseType;

    IReadOnlyList<string> ITypeSymbol.DeclaredInterfaces =>
        _interfaces.Select(r => r.Symbol.Name).ToList();

    IReadOnlyList<IMethodSymbol> ITypeSymbol.Methods => _members.Cast<IMethodSymbol>().ToList();
    IReadOnlyList<IFieldSymbol> ITypeSymbol.Fields => _fields.Cast<IFieldSymbol>().ToList();
    IReadOnlyList<ICaseSymbol> ITypeSymbol.Cases => _cases.Cast<ICaseSymbol>().ToList();

    // ---- INamespaceSymbol public surface (only meaningful when Kind == SymbolKind.Namespace) ----
    //
    // DeclaredTypes: The namespace host does NOT hold the declared types directly (those are
    // interned in SymbolTable keyed by (ns, name, arity)). Enumerating types by namespace
    // requires a SymbolTable reference, which the host symbol intentionally does not hold.
    // Tooling that needs the type list should use SymbolTable.AllOfKind or
    // SymbolTable.FindAll from the SemanticModel.  This interface property is satisfied
    // conservatively — it returns the empty list; callers that need it go through SymbolTable.

    IReadOnlyList<ITypeSymbol> INamespaceSymbol.DeclaredTypes => [];

    IReadOnlyList<IMethodSymbol> INamespaceSymbol.FreeFunctions =>
        _members.Cast<IMethodSymbol>().ToList();

    // ---- Internal compiler-facing state ----

    public string? Namespace { get; init; }
    public int Arity { get; init; }
    public TypeSymbolKind TypeKind { get; internal set; }

    /// Whether the type was declared `pub`. Drives DeclaredAccessibility and CLR emission.
    public bool IsPublic { get; init; }

    /// The CLR form of a `data` type (struct vs class). Computed by classification
    /// and revised once by the promotion pass — `internal set` so only the binder
    /// assembly writes it.
    public DataClassification Classification { get; internal set; } = DataClassification.Struct;

    /// The instance/type declaration when this identity has one. A standalone
    /// static facet uses its static declaration here until an instance companion
    /// is registered later in the compilation.
    public MemberSyntax? Decl { get; internal set; }

    /// The explicit `static Foo { ... }` facet, if present. A same-name,
    /// same-arity class/struct and static facet still have one CLR identity; this
    /// is source-level surface information, not a second metadata type.
    public StaticFuncDeclarationSyntax? StaticFacet { get; internal set; }
    public bool HasStaticFacet => StaticFacet is not null;
    public bool HasInstanceFacet => TypeKind is not TypeSymbolKind.StaticFunc and not TypeSymbolKind.NamespaceHost;

    /// The C#-fusion seam: a Roslyn-declared type interned as ExternalCSharp carries its
    /// adapter handle here.
    public ICSharpTypeHandle? CSharpHandle { get; init; }

    /// The declaration site.
    public SourceSpan Span => Decl?.Span ?? default;

    readonly List<MethodSymbol> _members = new();
    readonly List<FieldSymbol> _fields = new();
    readonly List<CaseSymbol> _cases = new();
    readonly List<TypeRef> _interfaces = new();
    readonly List<ConstSymbol> _constants = new();

    /// The resolved method set: declared methods plus promoted value-/pointer-receiver
    /// functions (and, for a NamespaceHost, its free functions).
    public IReadOnlyList<MethodSymbol> Members => _members;

    /// Declared fields (data / class), in declaration order.
    public IReadOnlyList<FieldSymbol> Fields => _fields;

    /// Choice payload / enum cases, in declaration order.
    public IReadOnlyList<CaseSymbol> Cases => _cases;

    /// Declared conformances (interfaces and, first when present, the base class),
    /// resolved at signature time.
    public IReadOnlyList<TypeRef> Interfaces => _interfaces;

    /// Namespace-level compile-time constants (NamespaceHost symbols only).
    public IReadOnlyList<ConstSymbol> Constants => _constants;

    /// The `open` / `abstract class` base class, when declared.
    public TypeSymbol? BaseType { get; internal set; }

    /// The enclosing type when this is a NESTED type.
    public TypeSymbol? DeclaringType { get; init; }

    /// For member symbols — the parent namespace host. Null for NamespaceHost itself.
    internal TypeSymbol? NamespaceHostSymbol { get; init; }

    readonly List<TypeSymbol> _derived = new();

    /// The in-assembly subclasses that name this type as their base — the closed set an
    /// `abstract class` hierarchy exposes for exhaustiveness checking.
    public IReadOnlyList<TypeSymbol> DerivedTypes => _derived;
    public void AddDerived(TypeSymbol d) => _derived.Add(d);

    /// The canonical bound-layer view of this type. Set at registration; the promotion
    /// pass revises a `data` view's classification.
    public BoundType? BoundView { get; internal set; }

    public void AddMember(MethodSymbol m) => _members.Add(m);
    public void AddField(FieldSymbol f) => _fields.Add(f);
    public void AddCase(CaseSymbol c) => _cases.Add(c);
    public void AddInterface(TypeRef i) => _interfaces.Add(i);
    public void AddConstant(ConstSymbol c) => _constants.Add(c);

    // ---- [Δ] Invalidation hooks — per-unit remove/replace (unblocks incremental rebind) ----
    // Called by SymbolTable.RemoveUnit / ReplaceUnit. Clears mutable member sets so the
    // next bind pass re-populates them cleanly. BoundView is also cleared: the promotion
    // pass re-stamps it. The symbol instance itself is retained (reference identity persists)
    // so call sites that already hold a TypeRef through this symbol remain valid structurally;
    // they re-bind on the next incremental cycle.
    internal void InvalidateMembers()
    {
        _members.Clear();
        _fields.Clear();
        _cases.Clear();
        _interfaces.Clear();
        _constants.Clear();
        _derived.Clear();
        BaseType = null;
        BoundView = null;
    }

    /// Sentinel backing `TypeRef.InferencePending` — a not-yet-inferred type.
    public static readonly TypeSymbol InferencePending =
        new() { Name = "?", TypeKind = TypeSymbolKind.InferencePending };

    // Identity IS reference identity — a TypeSymbol is interned once per
    // (ns, name, arity) and shared. Reference-based equality/hash is both the
    // correct semantics AND what breaks the structural-record cycle through
    // `BoundView` (a BoundType that points its `Symbol` back here).
    public bool Equals(TypeSymbol? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override string ToString() => Arity > 0 ? $"{Name}`{Arity}" : Name;
}
