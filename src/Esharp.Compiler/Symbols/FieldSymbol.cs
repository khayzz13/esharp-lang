using Esharp.BoundTree;
using Esharp.Syntax;

namespace Esharp.Symbols;

/// A field of a user `data` / `class` type — the symbol-spine face of what the
/// bound layer carries as `BoundField`. Lives on its declaring `TypeSymbol.Fields`;
/// the same instance is reported at the declaration and every resolved use.
public sealed record FieldSymbol : IFieldSymbol
{
    // ---- ISymbol public versioned surface ----

    public required string Name { get; init; }
    public SymbolKind Kind => IsProperty ? SymbolKind.Property : SymbolKind.Field;
    public ISymbol? ContainingSymbol => DeclaringType;

    public DeclaredAccessibility DeclaredAccessibility => Visibility switch
    {
        Syntax.Visibility.Public => DeclaredAccessibility.Public,
        Syntax.Visibility.Private => DeclaredAccessibility.Private,
        _ => DeclaredAccessibility.Internal,
    };

    /// [Δ] XML doc comment for LSP hover. Populated from `///` trivia on the field declaration.
    public string? XmlDoc { get; init; }

    // ---- IFieldSymbol public surface ----

    string IFieldSymbol.TypeDisplay => Bound is { } b ? BoundTypeDisplay.Name(b) : "?";
    bool IFieldSymbol.IsMutable => Mutable;
    bool IFieldSymbol.IsEvent => IsEvent;
    bool IFieldSymbol.IsEmbedded => IsEmbedded;
    bool IFieldSymbol.IsProperty => IsProperty;
    bool IFieldSymbol.HasDurableLocation => HasDurablePropertyLocation;
    bool IFieldSymbol.HasScopedMutation => HasScopedPropertyLocation;
    bool IFieldSymbol.HasCustomSetter => HasCustomPropertySetter;
    bool IFieldSymbol.HasGeneratedAccessors => IsProperty;

    // ---- Internal compiler-facing state ----

    public required TypeSymbol DeclaringType { get; init; }
    public TypeRef Type { get; init; } = TypeRef.InferencePending;

    /// The resolved bound-layer type — `*T` fields carry the heap-wrapper shape.
    /// Resolved at signature time in the DECLARING unit's import context and refreshed
    /// by BindData; `internal set` keeps the instance identity the sink reported.
    public BoundType? Bound { get; internal set; }
    // Bound once the declaration's scoped `mut` body has been validated. This is
    // semantic capability data, not a source pointer type; call-site lowering uses
    // it to build the lend/resume region without re-parsing syntax.
    public BoundScopedMutAccessor? ScopedMut { get; internal set; }
    public bool IsPublic { get; init; } = true;
    public Syntax.Visibility Visibility { get; init; } = Syntax.Visibility.Public;
    /// Per-accessor visibility overrides for a property (`pub var X { priv set }`).
    /// Null inherits the property's own <see cref="Visibility"/>; a value narrows that
    /// one accessor's emitted `get_`/`set_` method.
    public Syntax.Visibility? GetterVisibility { get; init; }
    public Syntax.Visibility? SetterVisibility { get; init; }
    public bool DeclaredMutable { get; init; } = true;
    public bool Mutable { get; internal set; } = true;
    public bool IsEmbedded { get; init; }
    public bool IsEvent { get; init; }
    /// <summary>True when this symbol denotes a CLR/E# property rather than a field.</summary>
    public bool IsProperty { get; internal set; }
    /// <summary>Imported or declared property has a durable <c>loca</c> protocol.</summary>
    public bool HasDurablePropertyLocation { get; internal set; }
    /// <summary>Imported property has a scoped <c>mut</c> protocol (never a durable address).</summary>
    public bool HasScopedPropertyLocation { get; internal set; }
    /// <summary>Location access intentionally bypasses a custom setter policy.</summary>
    public bool HasCustomPropertySetter { get; internal set; }
    public SourceSpan Span { get; init; }

    // Interned identity — reference equality/hash.
    public bool Equals(FieldSymbol? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override string ToString() => $"{DeclaringType.Name}.{Name}";
}
