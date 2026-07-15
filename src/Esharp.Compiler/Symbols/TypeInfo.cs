using Esharp.BoundTree;

namespace Esharp.Symbols;

/// A versioned, read-only projection of a `TypeSymbol` — the introspection surface
/// a `derive`/synthesizer (eventually user comptime code) binds to. The internal
/// `TypeSymbol` is free to evolve; this projection is the stable contract. Built on
/// demand via <see cref="From"/>; `Version` lets a consumer guard against a shape it
/// predates.
public sealed record TypeInfo
{
    /// The projection's schema version. Bumped when a field is added/changed.
    public const int Version = 1;

    public required string Name { get; init; }
    public string? Namespace { get; init; }
    public required TypeSymbolKind Kind { get; init; }
    public DataClassification Classification { get; init; }
    public IReadOnlyList<TypeFieldInfo> Fields { get; init; } = [];
    public IReadOnlyList<TypeCaseInfo> Cases { get; init; } = [];
    public IReadOnlyList<TypeMethodInfo> Methods { get; init; } = [];
    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// Project an interned `TypeSymbol` into the stable view. The reverse handle —
    /// the symbol behind this projection — is intentionally absent: a synthesizer
    /// reads the projection and emits members; it never reaches back into the spine.
    public static TypeInfo From(TypeSymbol symbol) => new()
    {
        Name = symbol.Name,
        Namespace = symbol.Namespace,
        Kind = symbol.TypeKind,
        Classification = symbol.Classification,
        Fields = symbol.Fields.Select(f => new TypeFieldInfo
        {
            Name = f.Name,
            TypeDisplay = f.Bound is { } b ? BoundTypeDisplay.Name(b) : "?",
            Type = f.Type,
            Mutable = f.Mutable,
            IsEmbedded = f.IsEmbedded,
            IsEvent = f.IsEvent,
            IsPublic = f.IsPublic,
        }).ToList(),
        Cases = symbol.Cases.Select(c => new TypeCaseInfo
        {
            Name = c.Name,
            Value = c.Value,
            Payloads = c.Payloads.Select(p => new TypeFieldInfo
            {
                Name = p.Name,
                TypeDisplay = p.Bound is { } pb ? BoundTypeDisplay.Name(pb) : "?",
                Type = p.Type,
            }).ToList(),
        }).ToList(),
        Methods = symbol.Members.Select(m => new TypeMethodInfo
        {
            Name = m.Name,
            DeclaredArity = m.DeclaredArity,
            IsStatic = m.IsStatic,
            IsAsync = m.IsAsync,
            ReturnDisplay = m.ReturnType is { } rt ? BoundTypeDisplay.Name(rt) : "?",
            TypeParameters = m.TypeParameters,
        }).ToList(),
    };
}

/// A field of the projected type — name, the resolved type (display + structured
/// `TypeRef`), and the mutability/embed/event facts.
public sealed record TypeFieldInfo
{
    public required string Name { get; init; }
    public required string TypeDisplay { get; init; }
    public TypeRef Type { get; init; } = TypeRef.InferencePending;
    public bool Mutable { get; init; }
    public bool IsEmbedded { get; init; }
    public bool IsEvent { get; init; }
    public bool IsPublic { get; init; } = true;
}

/// A choice/enum case of the projected type.
public sealed record TypeCaseInfo
{
    public required string Name { get; init; }
    public int? Value { get; init; }
    public IReadOnlyList<TypeFieldInfo> Payloads { get; init; } = [];
}

/// A method of the projected type — its name, source arity, and resolved return.
public sealed record TypeMethodInfo
{
    public required string Name { get; init; }
    public int DeclaredArity { get; init; }
    public bool IsStatic { get; init; }
    public bool IsAsync { get; init; }
    public required string ReturnDisplay { get; init; }
    public IReadOnlyList<string> TypeParameters { get; init; } = [];
}
