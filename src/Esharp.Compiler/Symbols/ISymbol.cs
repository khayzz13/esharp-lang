using Esharp.Syntax;

namespace Esharp.Symbols;

/// [Δ] Split into two layers:
///   - ISymbol: the PUBLIC, VERSIONED surface tooling (LSP/formatter, written in E#) binds to.
///     Stable across compiler-internal rearrangements. The SemanticModel currency.
///   - Internal mutable symbol types (TypeSymbol, MethodSymbol, …) implement ISymbol and add
///     internal mutable state the binder writes. Consumers outside the compiler see only ISymbol.

// ============================================================================
// Public versioned surface — the tooling API
// ============================================================================

/// The kind of declaration a symbol represents. Exposed on the public surface so
/// tooling can dispatch without casting.
public enum SymbolKind
{
    Type,
    Method,
    Field,
    /// A local binding declared with let/var in a function body.
    Local,
    /// A function parameter (also appears as ILocalSymbol in scope lookups).
    Parameter,
    Const,
    Case,
    Namespace,
    Property,
}

/// Declared accessibility, matching CLR visibility at the user-visible level.
public enum DeclaredAccessibility
{
    /// `pub` — public, crosses the assembly boundary.
    Public,
    /// bare (no modifier) — internal, assembly-scoped.
    Internal,
    /// `priv` — private, declaring-type-scoped.
    Private,
    /// `protected init` — family; class constructors only.
    Protected,
}

/// The public, versioned symbol surface the SemanticModel exposes to LSP / tooling.
/// Symbols are interned (types, methods) or per-declaration (fields, locals, consts),
/// so identity is reference identity: the same instance is reported at the declaration
/// and at every use — the basis of FindReferences.
public interface ISymbol
{
    /// The simple declared name.
    string Name { get; }

    /// The declaration site. Invalid (default) for synthesized symbols (forwarders,
    /// inference sentinels) — they have no source.
    SourceSpan Span { get; }

    /// The broad kind of this symbol.
    SymbolKind Kind { get; }

    /// The lexically enclosing symbol — the type for a member, the namespace host for a
    /// top-level type / free function, null for a namespace host itself.
    ISymbol? ContainingSymbol { get; }

    /// Declared accessibility. Matches the `pub` / bare / `priv` prefix at the declaration
    /// site (plus the nested-type default-private rule).
    DeclaredAccessibility DeclaredAccessibility { get; }

    /// The XML doc comment attached to this declaration, or null when none was present.
    /// [Δ] Added for LSP hover (WS7 of the LSP plan). Sourced from `///` trivia adjacent
    /// to the declaration syntax.
    string? XmlDoc { get; }
}
