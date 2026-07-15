using Esharp.Syntax;

namespace Esharp.Symbols;

/// A `choice` case (with payload fields) or an `enum` case (with its constant
/// value). Lives on its declaring `TypeSymbol.Cases`.
public sealed record CaseSymbol : ICaseSymbol
{
    // ---- ISymbol public versioned surface ----
    public required string Name { get; init; }
    public SymbolKind Kind => SymbolKind.Case;
    public ISymbol? ContainingSymbol => DeclaringType;
    public DeclaredAccessibility DeclaredAccessibility => DeclaredAccessibility.Public;
    public string? XmlDoc => null;

    // ---- ICaseSymbol public surface ----

    IReadOnlyList<IFieldSymbol> ICaseSymbol.Payloads => Payloads;
    int? ICaseSymbol.EnumValue => Value;

    // ---- Internal compiler-facing state ----
    public required TypeSymbol DeclaringType { get; init; }

    /// Union/enum-case payloads, in declaration order. Empty for enum cases and
    /// payload-less union cases.
    public IReadOnlyList<FieldSymbol> Payloads { get; init; } = [];

    /// The enum case's constant value; null for union cases.
    public int? Value { get; init; }

    public SourceSpan Span { get; init; }

    // Interned identity — reference equality/hash (correct semantics, and
    // it cuts the structural-record cycle through BoundType back-links).
    public bool Equals(CaseSymbol? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override string ToString() => $"{DeclaringType.Name}.{Name}";
}
