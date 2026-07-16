namespace Esharp.Syntax;

/// A type annotation as structure, not text. The parser produces these directly,
/// so the binder never re-splits a `Map<K, List<V>>` string — it walks the tree.
/// Every node carries the `SourceSpan` of its first-through-last token.
public abstract record TypeSyntax : SyntaxNode;

/// A bare type name: `int`, `Node`, `void`, or a dotted qualifier `NS.Widget`.
/// Dotted names stay textual here — splitting `NS` from `Widget` is name
/// resolution (the binder knows which prefixes are namespaces), not syntax.
public sealed record NamedTypeSyntax(string Name) : TypeSyntax;

/// A generic instantiation `Name<Arg, ...>`. Covers user generics (`Pair<A,B>`),
/// the runtime forms (`Result<T,E>`, `chan<T>` — the `chan` keyword lands here as
/// the base name), and external generics (`Dictionary<string, V>`).
public sealed record GenericTypeSyntax(string Name, IReadOnlyList<TypeSyntax> Args) : TypeSyntax;

/// A value-tuple type `(T1, T2, ...)`. A single-element `(T)` is kept as a
/// one-element tuple node, matching how the previous string path treated it.
public sealed record TupleTypeSyntax(IReadOnlyList<TypeSyntax> Elements, IReadOnlyList<string?>? ElementNames = null) : TypeSyntax;

/// A function-pointer type `&(P1, P2 -> R)`. With no `->` the return is `void`
/// (`&(int, int)`); the binder reads `ReturnType` either way.
public sealed record FunctionPointerTypeSyntax(
    IReadOnlyList<TypeSyntax> ParameterTypes,
    TypeSyntax ReturnType) : TypeSyntax;

/// A single-dimension array type `T[]`. The fixed CLR array (`newarr` / `ldelem` /
/// `stelem` / `ldlen`); `List<T>` stays the dynamic workhorse. `T[][]` nests as an
/// `ArrayTypeSyntax` whose `ElementType` is itself an `ArrayTypeSyntax`.
public sealed record ArrayTypeSyntax(TypeSyntax ElementType) : TypeSyntax;

/// A nullable wrapper `T?`. Binds tighter than nothing else postfix — `List<int>?`
/// is `(List<int>)?`, `*T?` is `*(T?)`, matching the prefix-before-suffix order of
/// the old string sniffing.
public sealed record NullableTypeSyntax(TypeSyntax Inner) : TypeSyntax;

/// A pointer / by-ref annotation: `*T` (mutable) or `readonly *T` (read-only,
/// `in T` on the CLR). Replaces the `ByRef`/`ReadOnlyByRef` flags and the nested
/// `*` strings the old path carried inline.
public sealed record PointerTypeSyntax(TypeSyntax Inner, bool ReadOnly) : TypeSyntax;

/// The inferred-type sentinel for `var` (an omitted lambda parameter / return
/// type, or an un-annotated body const). Replaces the `"var"` magic string.
public sealed record InferredTypeSyntax : TypeSyntax
{
    public static readonly InferredTypeSyntax Instance = new();
}
