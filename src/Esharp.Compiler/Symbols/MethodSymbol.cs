using Esharp.BoundTree;
using Esharp.Syntax;

namespace Esharp.Symbols;

/// How a method attaches to a receiver type via a Go-style receiver block.
public enum ReceiverKind { None, Value, Pointer, ReadonlyValue, Static }

/// The *declaration* identity of a method or free function.
/// Every function gets one at signature time: free functions attach to their namespace host symbol,
/// static-func members to their host type symbol, promoted functions to their receiver.
/// Carries the SOURCE signature — crucially `DeclaredArity`, the source parameter count, which
/// stays the stable key for resolving a call even after an async rewrite changes the emitted
/// Cecil arity — plus the async facts.
public sealed record MethodSymbol : IMethodSymbol
{
    // ---- ISymbol public versioned surface ----

    public required string Name { get; init; }
    public SymbolKind Kind => SymbolKind.Method;

    public ISymbol? ContainingSymbol => DeclaringType;

    public DeclaredAccessibility DeclaredAccessibility =>
        IsPublic ? DeclaredAccessibility.Public : DeclaredAccessibility.Internal;

    /// [Δ] XML doc comment for LSP hover. Populated from `///` trivia on the declaration.
    public string? XmlDoc { get; init; }

    // ---- IMethodSymbol public surface ----

    int IMethodSymbol.ParameterCount => DeclaredArity;
    bool IMethodSymbol.IsStatic => IsStatic;
    bool IMethodSymbol.IsAsync => IsAsync;
    IReadOnlyList<string> IMethodSymbol.TypeParameters => TypeParameters;
    ReceiverKind IMethodSymbol.ReceiverKind => ReceiverKind;

    IReadOnlyList<IParameterSymbol> IMethodSymbol.Parameters =>
        ParameterSymbols.Count > 0
            ? ParameterSymbols.Select(p => (IParameterSymbol)p).ToList()
            : DeclaredParameters.Select((r, i) => (IParameterSymbol)new LocalSymbol
            {
                // Synthesized stub: name unknown (DeclaredParameters carries TypeRefs, not param names).
                // Only reachable for external/transitional cases where ParameterSymbols is unpopulated.
                Name = $"p{i}",
                IsParameter = true,
                Mutable = false,
                Type = r.Symbol.BoundView,
            }).ToList();

    string IMethodSymbol.ReturnTypeDisplay =>
        ReturnType is { } rt ? BoundTypeDisplay.Name(rt) : DeclaredReturn?.ToString() ?? "void";

    // ---- Internal compiler-facing state ----

    public int DeclaredArity { get; init; }
    public bool IsPublic { get; init; }
    public bool IsStatic { get; init; }
    public ReceiverKind ReceiverKind { get; init; }

    /// The type this method belongs to: a declared `class` method's type, the
    /// receiver a free function promotes onto, a static-func host, or the
    /// namespace host of a plain free function.
    public TypeSymbol? DeclaringType { get; init; }

    /// The declaration syntax — the substrate TypeInfo and the binder's call
    /// resolution read instead of the FunctionDecls string map.
    public FunctionDeclarationSyntax? Decl { get; init; }

    /// True when the body contains `await` / `async let`.
    public bool IsAsync { get; init; }

    /// True when the declared return is an explicit wrapper (Task / Task<T> /
    /// ValueTask / ValueTask<T> / async-void / IAsyncEnumerable<T>): a bare call
    /// yields the wrapper VALUE and is not auto-awaited.
    public bool HasExplicitAsyncWrapperReturn { get; init; }

    public IReadOnlyList<TypeRef> DeclaredParameters { get; init; } = System.Array.Empty<TypeRef>();
    public TypeRef? DeclaredReturn { get; init; }

    /// The interned parameter symbols — populated by the binder's signature pass.
    /// When present, IMethodSymbol.Parameters returns these; otherwise falls back
    /// to synthesized stubs from DeclaredParameters (for external/transitional cases).
    public IReadOnlyList<LocalSymbol> ParameterSymbols { get; init; } = [];

    /// The call-site return type as the bound layer's currency, resolved in the
    /// DECLARING unit's import context at signature time (a caller-side re-resolve
    /// would use the wrong namespace scope). `task func` wrapping applied.
    public BoundType? ReturnType { get; init; }
    public IReadOnlyList<string> TypeParameters { get; init; } = System.Array.Empty<string>();

    public SourceSpan Span => Decl?.Span ?? default;

    // Interned identity — reference equality/hash.
    public bool Equals(MethodSymbol? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override string ToString() => $"{Name}/{DeclaredArity}";
}
