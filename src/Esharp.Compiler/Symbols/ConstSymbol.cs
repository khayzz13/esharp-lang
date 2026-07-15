using Esharp.Syntax;

namespace Esharp.Symbols;

/// A namespace-level compile-time constant. Carries the folded literal the binder
/// inlines at every use site (the IL emitter still materializes a CLR `literal`
/// field for C# interop). Lives on its namespace host symbol's constant set.
public sealed record ConstSymbol : IConstSymbol
{
    // ---- ISymbol public versioned surface ----
    public required string Name { get; init; }
    public SymbolKind Kind => SymbolKind.Const;
    public ISymbol? ContainingSymbol => DeclaringHost;
    public DeclaredAccessibility DeclaredAccessibility => DeclaredAccessibility.Public; // consts fold; visibility is declaration-site
    public string? XmlDoc => null;

    // ---- IConstSymbol public surface ----

    /// The folded literal rendered as a human-readable string, e.g. "42" or "\"hello\"".
    string IConstSymbol.FoldedValueDisplay => FoldedLiteral switch
    {
        string s  => $"\"{s}\"",
        char c    => $"'{c}'",
        bool b    => b ? "true" : "false",
        null      => "nil",
        var v     => v.ToString() ?? "?",
    };

    // ---- Internal compiler-facing state ----

    /// The namespace host this constant belongs to.
    public required TypeSymbol DeclaringHost { get; init; }

    /// The folded value, as the bound literal the binder substitutes at use sites.
    /// Typed `object` at the symbol layer to keep Symbols free of bound-tree
    /// dependencies; the binder owns the cast.
    public required object FoldedLiteral { get; init; }

    public SourceSpan Span { get; init; }

    // Interned identity — reference equality/hash (correct semantics, and
    // it cuts the structural-record cycle through BoundType back-links).
    public bool Equals(ConstSymbol? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override string ToString() => Name;
}
