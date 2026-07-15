using Esharp.BoundTree;
using Esharp.Syntax;

namespace Esharp.Symbols;

/// A local binding (`let`/`var`), parameter, match-arm binding, or other
/// scope-declared name. Created once at declaration by the BinderScope and
/// reported (the same instance) at every use — the substrate for
/// `LookupSymbolsInScope` and local `FindReferences`.
///
/// Parameters implement both ILocalSymbol (for scope-visible lookup) and
/// IParameterSymbol (for the method's Parameters list).
public sealed record LocalSymbol : ILocalSymbol, IParameterSymbol
{
    // ---- ISymbol public versioned surface ----
    public required string Name { get; init; }

    /// SymbolKind.Parameter for function parameters; SymbolKind.Local for all
    /// other bindings (let/var, match-arm bindings, tuple destructures).
    public SymbolKind Kind => IsParameter ? SymbolKind.Parameter : SymbolKind.Local;

    public ISymbol? ContainingSymbol => null; // locals don't expose their function on the public surface
    public DeclaredAccessibility DeclaredAccessibility => DeclaredAccessibility.Private;
    public string? XmlDoc => null; // locals have no doc comments

    // ---- ILocalSymbol / IParameterSymbol public surface ----

    /// Display form of the declared (or inferred) type.
    public string TypeDisplay => Type is { } t ? BoundTypeDisplay.Name(t) : "?";

    /// True when declared with 'var'; false for 'let' bindings and parameters.
    public bool IsMutable => Mutable;

    /// True when this local is a function parameter rather than a body binding.
    public bool IsParameter { get; init; }

    // IParameterSymbol members (only meaningful when IsParameter is true)
    bool IParameterSymbol.IsOut => IsOutParam;
    bool IParameterSymbol.IsByRef => IsByRefParam;
    bool ILocalSymbol.IsAddressable => IsAddressable;

    // ---- Internal compiler-facing state ----

    /// The function (or static-func member) whose body declares this local;
    /// null at namespace scope (which has no locals today).
    public string? DeclaringFunction { get; init; }

    public bool Mutable { get; init; } = true;

    /// <summary>Whether this binding deliberately exposes a borrowable location.</summary>
    public LocalRepresentation Representation { get; init; } = LocalRepresentation.Default;
    public bool IsAddressable => Representation is not LocalRepresentation.BareTypedValue;

    /// True when declared as 'out' (must be set before the function returns).
    public bool IsOutParam { get; init; }

    /// True when declared as a by-ref pointer parameter ('*T').
    public bool IsByRefParam { get; init; }

    /// The binding's declared type — the substrate for `SemanticModel.GetTypeOf` (hover
    /// type, member completion). Set at declaration; null only for placeholder locals.
    public BoundType? Type { get; init; }

    /// True when an explicit <c>&amp;local</c> escapes into heap-owned storage or is
    /// returned. The pointer realization pass sets this once; CodeGen then gives the
    /// local one shared <c>__Ptr_T</c> cell so later local writes stay visible through
    /// every escaped pointer alias.
    public bool AddressEscapes { get; set; }

    /// The declaration site.
    public SourceSpan Span { get; init; }

    /// The range of the enclosing block — the scope fact `LookupSymbolsInScope`
    /// needs: the local is visible at positions inside this range that follow
    /// the declaration.
    public SourceSpan BlockSpan { get; init; }

    // Interned identity — reference equality/hash (correct semantics, and
    // it cuts the structural-record cycle through BoundType back-links).
    public bool Equals(LocalSymbol? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override string ToString() => Name;
}
