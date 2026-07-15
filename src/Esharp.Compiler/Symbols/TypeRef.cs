namespace Esharp.Symbols;

/// A modifier on a *use* of a type — the by-ref / pointer / nullable / array wrapping
/// applied to a TypeSymbol at one site. `*T` is HeapPointer; the escape-analysis downgrade
/// of a non-escaping `*T` parameter flips the same ref to ByRef. Recording the choice here,
/// once, is what lets the field side and the call side of a `*T` agree instead of each
/// re-deriving it (see PointerEscapeAnalysis and ILTypeResolver).
public enum TypeRefModifier { None, ByRef, HeapPointer, Nullable, Array }

/// A *use* of a type: a TypeSymbol plus closed type arguments plus a modifier. The
/// counterpart to TypeSymbol (a *declaration*). `Option<int>` is
/// `TypeRef(OptionSymbol, [TypeRef(intSymbol)])`; `*T` is `TypeRef(tSymbol, [], HeapPointer)`.
/// Leaves are symbols, never strings — so identity survives across phase boundaries.
public sealed record TypeRef(
    TypeSymbol Symbol,
    IReadOnlyList<TypeRef> TypeArguments,
    TypeRefModifier Modifier = TypeRefModifier.None)
{
    public TypeRef(TypeSymbol symbol, TypeRefModifier modifier = TypeRefModifier.None)
        : this(symbol, System.Array.Empty<TypeRef>(), modifier) { }

    /// A typed hole: inference has not (yet) produced a type. Replaces the magic
    /// `ExternalType("var")` string with an explicit, inspectable sentinel.
    public static readonly TypeRef InferencePending = new(TypeSymbol.InferencePending);

    public bool IsPending => ReferenceEquals(Symbol, TypeSymbol.InferencePending);

    public override string ToString()
    {
        var core = TypeArguments.Count > 0
            ? $"{Symbol.Name}<{string.Join(", ", TypeArguments)}>"
            : Symbol.Name;
        return Modifier switch
        {
            TypeRefModifier.HeapPointer => $"*{core}",
            TypeRefModifier.ByRef => $"ref {core}",
            TypeRefModifier.Nullable => $"{core}?",
            TypeRefModifier.Array => $"{core}[]",
            _ => core,
        };
    }
}
