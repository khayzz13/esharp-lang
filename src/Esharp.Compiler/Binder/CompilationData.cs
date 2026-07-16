using Esharp.Diagnostics;
using Esharp.Symbols;
using Esharp.Syntax;
using Esharp.BoundTree;

namespace Esharp.Binder;

// Multi-file binder state — what the binder accumulates across RegisterTypes /
// RegisterSignatures / BindUnit calls. Hoisted out of Binder so a
// Workspace.Compilation can own it and re-bind documents into the same shared
// registry without going through the Binder's public surface.
//
// This is the whole of it: diagnostics, the symbol spine, the well-known types,
// the tooling sink, and one knob. Identity lives on interned symbols
// (TypeSymbol / MethodSymbol / FieldSymbol …) — there is no string-keyed
// blackboard to fall out of sync with them.
public sealed record CompilationData
{
    public DiagnosticBag Diagnostics { get; init; } = new();

    /// The symbol spine: interned TypeSymbols (reference identity) for every declared
    /// type, with arity and the resolved method set. Populated additively during
    /// RegisterTypes alongside the legacy string-keyed dictionaries above, and consumed by
    /// type/member resolution that would otherwise re-derive identity from strings.
    public SymbolTable Symbols { get; init; } = new();

    /// The pre-resolved well-known types — primitives and the runtime generics
    /// (Result / Spawned / Chan / tuples). Kept apart from the user-type interner above
    /// so a primitive never collides with namespace-scoped resolution. The single
    /// source of identity for `int`, `Result<…>`, etc. across binder and backend.
    public CoreTypes Core { get; init; } = new();

    /// The tooling tap (F#'s TcResultsSink): the binder reports declared/resolved
    /// symbols here as a side effect of binding. Batch passes the no-op; tooling
    /// passes a CollectingSemanticSink and gets the symbol-use index for free.
    public Esharp.Diagnostics.Semantics.ISemanticSink Sink { get; init; }
        = Esharp.Diagnostics.Semantics.NullSemanticSink.Instance;

    /// The external half of the symbol spine: interned TypeSymbol/MethodSymbol/
    /// FieldSymbol identities for BCL members the binder resolves by reflection,
    /// so tooling occurrences on `msgs.Add(…)` carry real symbols.
    public IExternalSymbols Externals { get; init; } = new Esharp.Metadata.ExternalSymbols();

    // When false, the implicit standard-namespace (BCL) search is disabled during
    // type/member resolution: an unqualified name resolves only from an exact /
    // qualified match or an explicit `using`. Mirrors the emitter-side knob so the
    // binder and IL emitter agree on what resolves. Default true.
    public bool EnableImplicitUsings { get; init; } = true;

    /// Emit opt-in performance diagnostics for source constructs that copy values,
    /// box, or require runtime allocation. These warnings never change lowering.
    public bool ShowAllocations { get; init; }

    /// The member-emission seam (null by default — batch compilation synthesizes
    /// nothing). When set, the binder invokes it per `data` declaration between the
    /// promotion sort and BindData, so synthesized members satisfy interfaces and
    /// reach the emitters like any promoted member. The hook `derive` plugs into.
    public IMemberSynthesizer? MemberSynthesizer { get; init; }

    /// Namespace names which already supplied an `init { }` block in this
    /// compilation. The declaration is one-per-host even across source files.
    public HashSet<string> NamespaceInitializers { get; } = new(StringComparer.Ordinal);

    /// Bound namespace-host storage, registered after all function signatures and
    /// before any body binds so every file in a namespace sees the same fields.
    public Dictionary<NamespaceStateDeclarationSyntax, BoundNamespaceStateDeclaration> NamespaceStateDeclarations { get; }
        = new(ReferenceEqualityComparer.Instance);
    public Dictionary<string, Dictionary<string, (BoundType Type, bool Mutable)>> NamespaceStateScopes { get; }
        = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> NamespaceInitWritableProperties { get; }
        = new(StringComparer.Ordinal);
}
