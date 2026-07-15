using Esharp.Diagnostics;

namespace Esharp.Binder;

/// THE shared transient binding state — the binder-side `TokenCursor`. One instance
/// per `Binder`, owned by the composition root and reached by every binder unit
/// (expressions, statements, match, declarations) and the `TypeResolver`. Everything
/// here resets or scopes per unit / per function; the durable cross-file registries
/// live on `CompilationData`. Holding the state in exactly one place is what lets the
/// units be real classes instead of partial-class tabs over smeared fields.
public sealed class BindContext
{
    public BindContext(CompilationData data) => Data = data;

    public CompilationData Data { get; }
    public DiagnosticBag Diagnostics => Data.Diagnostics;

    /// The lexical scope chain for the function body currently being bound.
    public BinderScope Scope { get; set; } = BinderScope.Root();

    /// Declared (unwrapped) return type of the function currently being bound —
    /// threads target-typing into `return` expressions and `ok()`/`error()` calls.
    public BoundType CurrentReturnType { get; set; } = new VoidType();

    public bool CurrentFunctionHasAwait { get; set; }
    /// True only in the direct body of an IAsyncEnumerable<T> function. Nested
    /// lambdas and spawn bodies clear this, so their yields cannot bind to an
    /// enclosing stream producer.
    public bool CurrentFunctionAllowsYield { get; set; }
    /// True only while binding the direct body of a namespace `init { }` block.
    /// It rejects suspension and return without changing nested callable semantics.
    public bool InNamespaceInitializer { get; set; }
    public string? CurrentFunctionName { get; set; }

    /// The source span of the function body currently being bound — the enclosing
    /// scope range a declared local is visible within. The semantic model's
    /// LookupSymbolsInScope answers "is this local in view at position P" by testing
    /// P against this range and the local's own declaration position.
    public Esharp.Syntax.SourceSpan CurrentFunctionBlockSpan { get; set; }

    /// Name of the enclosing `static Foo` block when binding one of its inner
    /// functions — qualifies bare-name field references so the IL emitter resolves
    /// them to the host class's static field.
    public string? CurrentStaticFuncName { get; set; }

    /// Declaring namespace of the unit currently being bound (or whose signatures
    /// are being resolved). Drives namespace-scoped name resolution; "Main" is the
    /// implicit namespace for a unit with no `namespace` declaration.
    public string CurrentNamespace { get; set; } = "Main";

    /// The type whose members/fields/nested types are currently being resolved, when
    /// any. A bare reference to a nested type resolves only within this enclosing type's
    /// scope (its own and ancestor nested types) — the C# nested-name visibility rule.
    /// Null at namespace scope (a free function body sees no nested names bare).
    public Symbols.TypeSymbol? CurrentEnclosingType { get; set; }

    /// True while binding the inner of an explicit `await` (or an `async let`
    /// initializer) — suppresses the bare-call auto-await so the lowering sees the
    /// raw awaitable.
    public bool BindingAwaitInner { get; set; }

    /// Active payload-view aliases inside match arms: viewName → (payloadName →
    /// (synthetic local, type)). Snapshotted/restored per arm so nested matches and
    /// shadowing stay correct.
    public Dictionary<string, Dictionary<string, (string Local, BoundType Type)>> PayloadViews { get; set; }
        = new(StringComparer.Ordinal);

    /// Per-unit import context, shared with the TypeResolver: `using "NS"` paths,
    /// `using static` short names → full paths, and `using X = "Full.Type"` aliases.
    public List<string> NamespaceImports { get; } = [];
    public Dictionary<string, string> StaticImports { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> TypeAliases { get; } = new(StringComparer.Ordinal);

    // === Flow narrowing (§type-narrowing-and-downcasting) ===

    /// Active smart-casts: a stable access path (a `let`/param name, or a `let`-field
    /// chain `recv.field`) → the type a runtime fact has narrowed it to in the current
    /// region. Saved/restored around guarded branches and blocks like the scope chain.
    public Dictionary<string, NarrowFact> Narrowed { get; set; } = new(StringComparer.Ordinal);

    /// Paths the binder WOULD narrow but cannot prove stable — a `var`, or a field path
    /// a call may have mutated through an alias. Recorded so a member access that relies
    /// on the narrow gets the located rebind diagnostic (ES2173) instead of a bare
    /// "no member"/locationless backend error.
    public Dictionary<string, BoundType> PoisonedNarrows { get; set; } = new(StringComparer.Ordinal);

    /// Bumped on every bound call. A call-sensitive (field-path) narrow recorded at an
    /// earlier generation is stale — a call may have mutated the field through an alias.
    /// Local/param narrows are immune (a call cannot reassign a `let`/param binding).
    public int CallGeneration { get; set; }

    /// Active primary-ctor capture context — set while binding an in-body method of
    /// a headered class. A bare name matching a header parameter binds as a
    /// `self.<name>` read of the synthesized private capture field, and the hit is
    /// recorded in Captured so Pass 4 materializes exactly the fields that were used.
    public HeaderCaptureContext? HeaderCapture { get; set; }

    /// Captured header-param names accumulated across Pass 2 method binding, keyed
    /// by class name — read by Pass 4 BindData to synthesize the capture fields.
    public Dictionary<string, HashSet<string>> HeaderCaptured { get; } = new(StringComparer.Ordinal);

    // === Flow-analysis seam (§narrowing-sink) ===

    /// The flow-analysis narrowing sink injected before binding begins.
    /// Set to NullNarrowingFactsSink.Instance when no flow analysis is active
    /// (the default — zero-allocation, no-op).  FlowAnalysis.NullStateNarrowingSink
    /// implements this to receive per-site Narrow records from NarrowingAnalyzer.Extract()
    /// and propagate them into the null-state lattice without a second tree walk.
    internal INarrowingFactsSink NarrowingSink { get; set; } = NullNarrowingFactsSink.Instance;

    int _tempCounter;

    /// Fresh synthetic-local ordinal (try-unwrap temps and friends).
    public int NextTemp() => _tempCounter++;
}

/// The capture surface of one headered class while one of its in-body methods
/// binds: header params by name, the shared captured-name sink, and the bound
/// self type the synthesized `self.<name>` access hangs off.
public sealed record HeaderCaptureContext(
    string ClassName,
    IReadOnlyDictionary<string, BoundType> Params,
    HashSet<string> Captured,
    BoundType SelfType);

/// One active smart-cast fact: the narrowed `Type`, whether it is `CallSensitive` (a
/// field path), and the call `Generation` it was recorded at (for staleness).
public sealed record NarrowFact(BoundType Type, bool CallSensitive, int Generation);
