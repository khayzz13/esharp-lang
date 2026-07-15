using Esharp.Symbols;
using Esharp.BoundTree;   // ICSharpTypeHandle (seam: moves to Esharp.Metadata in pillar 3)
using Esharp.Syntax;

namespace Esharp.BoundTree;

public abstract record BoundType
{
    public abstract string EmitName { get; }

    /// True when this type is the explicit `T?` nullable wrapper. Flow analysis
    /// (null-state) seeds a `T?`-typed binding as MaybeNull and a non-nullable one as
    /// NotNull. Reference-nullability (a bare class holding nil) and `*T` pointer
    /// nullability are tracked separately — this is specifically the `NullableType` shape.
    public bool IsNullable => this is NullableType;
}

public sealed record PrimitiveType(string Name) : BoundType
{
    public override string EmitName => Name;
}

public sealed record DataType(
    string Name,
    IReadOnlyList<string> TypeParameters,
    DataDeclarationSyntax Decl,
    DataClassification Classification = DataClassification.Struct,
    IReadOnlyList<BoundType>? TypeArguments = null,
    string? Namespace = null) : BoundType
{
    /// The interned declaration symbol — reference identity for every consumer
    /// that would otherwise re-derive it from the name string. Threaded at
    /// registration; `with`-clones (closed generics) inherit it.
    public TypeSymbol? Symbol { get; init; }

    public IReadOnlyList<BoundType> TypeArgs => TypeArguments ?? Array.Empty<BoundType>();

    public override string EmitName
    {
        get
        {
            // Use instantiated arguments if present (use-site), else fall back to formal parameters (decl-site)
            if (TypeArgs.Count > 0)
                return $"{Name}<{string.Join(", ", TypeArgs.Select(t => t.EmitName))}>";
            if (TypeParameters.Count > 0)
                return $"{Name}<{string.Join(", ", TypeParameters)}>";
            return Name;
        }
    }
}

public sealed record ChoiceType(string Name, IReadOnlyList<string> TypeParameters, ChoiceDeclarationSyntax Decl, bool IsRef = false, IReadOnlyList<BoundType>? TypeArguments = null) : BoundType
{
    /// The interned declaration symbol — reference identity for every consumer
    /// that would otherwise re-derive it from the name string. Threaded at
    /// registration; `with`-clones (closed generics) inherit it.
    public TypeSymbol? Symbol { get; init; }

    /// Closed generic instantiation's type arguments (`Option<int>` → [int]).
    /// Empty for the open declaration or a non-generic choice.
    public IReadOnlyList<BoundType> TypeArgs => TypeArguments ?? [];

    public override string EmitName => TypeArgs.Count > 0
        ? $"{Name}<{string.Join(", ", TypeArgs.Select(t => t.EmitName))}>"
        : TypeParameters.Count > 0
            ? $"{Name}<{string.Join(", ", TypeParameters)}>"
            : Name;
}

public sealed record EnumType(string Name, EnumDeclarationSyntax Decl) : BoundType
{
    /// The interned declaration symbol — reference identity for every consumer
    /// that would otherwise re-derive it from the name string. Threaded at
    /// registration; `with`-clones (closed generics) inherit it.
    public TypeSymbol? Symbol { get; init; }

    public override string EmitName => Name;
}

public sealed record ResultType(BoundType OkType, BoundType ErrorType) : BoundType
{
    public override string EmitName => $"Result<{OkType.EmitName}, {ErrorType.EmitName}>";
}

public sealed record ChanType(BoundType ElementType) : BoundType
{
    public override string EmitName => $"Chan<{ElementType.EmitName}>";
}

/// An external (BCL / referenced-assembly / not-yet-known) type. `Name` is the BARE
/// base name; a constructed generic carries its arguments STRUCTURED in
/// `TypeArguments` (`List<int>` is `ExternalType("List", [PrimitiveType("int")])`),
/// never encoded into the name string. The IL backend closes the generic by walking
/// the arguments — no phase ever re-parses type syntax out of `Name`.
public sealed record ExternalType(string Name, IReadOnlyList<BoundType>? TypeArguments = null) : BoundType
{
    public IReadOnlyList<BoundType> TypeArgs => TypeArguments ?? Array.Empty<BoundType>();

    public override string EmitName => TypeArgs.Count > 0
        ? $"{Name}<{string.Join(", ", TypeArgs.Select(ArgSourceName))}>"
        : Name;

    // Render an argument the way E# source spells it — `*Inner` for a heap pointer
    // (matching what the old encoded strings carried), EmitName otherwise. Display
    // only; nothing decodes this.
    static string ArgSourceName(BoundType t) =>
        t is HeapPointerBoundType hp ? "*" + ArgSourceName(hp.Inner) : t.EmitName;
}

/// Well-known placeholder type names carried as <see cref="ExternalType"/> names — a
/// contract between the lowering passes (producers) and the IL backend (consumer), kept
/// in the shared foundation so neither side hardcodes the string. The bound tree stays
/// backend-agnostic: the placeholder names a type the backend computes structurally.
public static class BackendPlaceholders
{
    /// An async awaiter, carrying its awaitable as the single type argument
    /// (`__Awaiter&lt;Task&lt;int&gt;&gt;`). The backend resolves the concrete awaiter
    /// struct from the awaitable's `GetAwaiter()` return type (`Task&lt;int&gt;` →
    /// `TaskAwaiter&lt;int&gt;`), exactly as a direct async emitter would.
    public const string Awaiter = "__Awaiter";
}

// External type sourced from a sibling `.cs` file in the same project. The
// Handle is implemented in the Workspace layer over Roslyn's INamedTypeSymbol;
// everything the binder and emitter need is on ICSharpTypeHandle, so this
// record carries the handle as its only state.
public sealed record ExternalCSharpType(ICSharpTypeHandle Handle, IReadOnlyList<BoundType>? TypeArguments = null) : BoundType
{
    /// The interned declaration symbol — reference identity for every consumer
    /// that would otherwise re-derive it from the name string. Threaded at
    /// registration; `with`-clones (closed generics) inherit it.
    public TypeSymbol? Symbol { get; init; }

    public IReadOnlyList<BoundType> TypeArgs => TypeArguments ?? Array.Empty<BoundType>();

    public override string EmitName => TypeArgs.Count > 0
        ? $"{Handle.Name}<{string.Join(", ", TypeArgs.Select(t => t.EmitName))}>"
        : Handle.Name;
}

/// The inference hole — "no type (yet)". The explicit, inspectable sentinel that
/// retires the `ExternalType("var")` magic string: a binder path that cannot type
/// an expression yields this, consumers pattern-match `InferredType` instead of
/// sniffing a name, and the backend maps it to `object` exactly as before. The
/// symbol-layer counterpart is `TypeRef.InferencePending`.
public sealed record InferredType : BoundType
{
    public static readonly InferredType Instance = new();
    public override string EmitName => "var";
}

public sealed record VoidType : BoundType
{
    public override string EmitName => "void";
}

public sealed record NullType : BoundType
{
    public override string EmitName => "null";
}

public sealed record NullableType(BoundType Inner) : BoundType
{
    public override string EmitName => $"{Inner.EmitName}?";
}

public sealed record InterfaceType(string Name, Esharp.Syntax.InterfaceDeclarationSyntax Decl, IReadOnlyList<BoundType>? TypeArguments = null) : BoundType
{
    /// The interned declaration symbol — reference identity for every consumer
    /// that would otherwise re-derive it from the name string. Threaded at
    /// registration; `with`-clones (closed generics) inherit it.
    public TypeSymbol? Symbol { get; init; }

    public IReadOnlyList<BoundType> TypeArgs => TypeArguments ?? System.Array.Empty<BoundType>();
    public override string EmitName => TypeArgs.Count > 0
        ? $"{Name}<{string.Join(", ", TypeArgs.Select(a => a.EmitName))}>"
        : Name;
}

public sealed record StaticFuncType(
    string Name,
    Esharp.Syntax.StaticFuncDeclarationSyntax Decl,
    IReadOnlyList<string>? TypeParameters = null,
    IReadOnlyList<BoundType>? TypeArguments = null) : BoundType
{
    /// The interned declaration symbol — reference identity for every consumer
    /// that would otherwise re-derive it from the name string. Threaded at
    /// registration; `with`-clones (closed generics) inherit it.
    public TypeSymbol? Symbol { get; init; }

    public IReadOnlyList<BoundType> TypeArgs => TypeArguments ?? [];
    public override string EmitName => TypeArgs.Count > 0
        ? $"{Name}<{string.Join(", ", TypeArgs.Select(t => t.EmitName))}>"
        : TypeParameters is { Count: > 0 }
            ? $"{Name}<{string.Join(", ", TypeParameters)}>"
            : Name;
}

public sealed record TupleType(IReadOnlyList<BoundType> ElementTypes) : BoundType
{
    public override string EmitName => $"({string.Join(", ", ElementTypes.Select(t => t.EmitName))})";
}

public sealed record ByRefBoundType(BoundType Inner) : BoundType
{
    public override string EmitName => $"ref {Inner.EmitName}";
}

/// A single-dimension CLR array `T[]` — `newarr` / `ldelem` / `stelem` / `ldlen`.
/// A reference type (no value-copy semantics); `List<T>` remains the dynamic form.
public sealed record ArrayBoundType(BoundType ElementType) : BoundType
{
    public override string EmitName => $"{ElementType.EmitName}[]";
}

/// Heap pointer to a value type — `*T` in field, local, or return position.
/// Backed by a compiler-generated sealed wrapper class at IL level.
/// Nullable (nil = wrapper is null), auto-deref on member access.
/// [Δ] EmitName now renders the E# `*T` spelling (transpiler was dropped).
/// The IL backend keys off Symbol + modifier, not EmitName, so this is display-only.
public sealed record HeapPointerBoundType(BoundType Inner) : BoundType
{
    public override string EmitName => $"*{Inner.EmitName}";
}

public sealed record FunctionPointerType(IReadOnlyList<BoundType> ParameterTypes, BoundType ReturnType) : BoundType
{
    public override string EmitName =>
        $"delegate*<{string.Join(", ", ParameterTypes.Select(p => p.EmitName))}, {ReturnType.EmitName}>";
}

/// A nominal, user-declared CLR delegate type (`delegate func Name(...) -> R`).
/// Emits as a sealed class deriving from System.MulticastDelegate — nominally
/// distinct from any structurally-identical Func/Action (the delegate analogue of
/// E#'s nominal-interface stance). Carries the declaration syntax so the binder can
/// reflect the Invoke shape on demand (a same-compilation delegate has no runtime
/// Type yet during its own compilation) for lambda / method-group conversion.
public sealed record NamedDelegateType(string Name, string? Namespace, DelegateDeclarationSyntax Decl) : BoundType
{
    /// The interned declaration symbol — reference identity for every consumer
    /// that would otherwise re-derive it from the name string. Threaded at
    /// registration; `with`-clones (closed generics) inherit it.
    public TypeSymbol? Symbol { get; init; }

    public override string EmitName => Name;
}
