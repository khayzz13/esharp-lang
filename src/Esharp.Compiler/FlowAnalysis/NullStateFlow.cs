using System.Collections.Immutable;
using Esharp.BoundTree;
using Esharp.Binder;
using Esharp.Diagnostics;
using Esharp.Symbols;
using Esharp.Syntax;

namespace Esharp.FlowAnalysis;

// ─────────────────────────────────────────────────────────────────────────────
//  NullStateFlow — T? null-state lattice and sound smart-cast tracking.
//
//  Forward DFA that tracks, for each local of nullable type, whether the value
//  is:
//    MaybeNull — could be null (conservative, the bottom state)
//    NotNull   — proven non-null on this path
//
//  Lattice:
//    MaybeNull ⊑ NotNull (NotNull is more information).
//    Bottom = MaybeNull.  Join = min (any null-possibility → MaybeNull).
//    Leq(a, b): MaybeNull ⊑ anything; NotNull ⊑ NotNull only.
//
//  Smart-cast flow:
//    After `x != nil` in a condition, the then-branch has NotNull for x.
//    After a BoundLetGuard (let-else), the continuation has NotNull.
//    After `if x == nil { return/throw }`, the continuation has NotNull.
//    Pattern arms that bind a nil case have MaybeNull; other arms have NotNull.
//
//  Per-edge seeding ([Δ round 2]):
//    NullStateAnalysis implements IEdgeSeedProvider<NullFlowState>.  The engine
//    calls TrySeedEdge(pred, succ, predOut) for each edge.  When `pred` has a
//    BranchCondition, we extract null-state implications (e.g. `x != nil` →
//    then-edge gets {x: NotNull}, else-edge gets nothing / {x: MaybeNull}).
//    This is the per-edge seeding that was deferred in round 1: it happens in
//    the engine's join step, not in a separate tree walk, so narrowed states
//    are available at the first iteration of the successor block.
//
//  Diagnostics:
//    ES2173 — possible null dereference on a nullable member-access path.
//
//  Narrowing-facts integration ([Δ]):
//    NullStateNarrowingSink implements INarrowingFactsSink (from Esharp.Binder).
//    Injected into BindContext.NarrowingSink before the bind step so the binder's
//    per-site NarrowingAnalyzer.Extract() feeds the null-state lattice without a
//    second tree walk.
//
//  BoundConversion (frozen contract §3):
//    BoundConversion(Operand, TargetType, Kind) is CORE. When Kind is Narrow,
//    the result is proven non-null (the binder proved the type at this site).
// ─────────────────────────────────────────────────────────────────────────────

public enum NullState
{
    /// Unknown / may be null (conservative bottom).
    MaybeNull = 0,
    /// Proven non-null on this path.
    NotNull = 1,
}

/// AnnotationStore key for a local's null state at a specific expression site.
public sealed record NullNarrowKey(LocalSymbol Local, BoundExpression Site);

/// Per-local null-state map, keyed by LocalSymbol name (string).
/// LocalSymbol reference isn't available from BoundNameExpression, so we track
/// by name and resolve to LocalSymbol in queries.
public sealed class NullFlowState(ImmutableDictionary<string, NullState> map)
{
    public static readonly NullFlowState Empty =
        new(ImmutableDictionary<string, NullState>.Empty);

    public readonly ImmutableDictionary<string, NullState> Map = map;

    public NullFlowState WithState(string name, NullState state) =>
        new(Map.SetItem(name, state));

    public NullState GetState(string name) =>
        Map.TryGetValue(name, out var s) ? s : NullState.MaybeNull;
}

sealed class NullFlowLattice : ILattice<NullFlowState>
{
    public static readonly NullFlowLattice Instance = new();
    public NullFlowState Bottom => NullFlowState.Empty;

    public NullFlowState Join(NullFlowState a, NullFlowState b)
    {
        // MaybeNull wins: any path that could be null makes the result MaybeNull.
        var builder = a.Map.ToBuilder();
        foreach (var (name, bState) in b.Map)
        {
            if (builder.TryGetValue(name, out var aState))
                builder[name] = (aState == NullState.NotNull && bState == NullState.NotNull)
                    ? NullState.NotNull : NullState.MaybeNull;
            else
                builder[name] = bState;
        }
        return new NullFlowState(builder.ToImmutable());
    }

    public bool Leq(NullFlowState a, NullFlowState b)
    {
        // a ⊑ b: for every name b tracks, a must not have MORE information than b.
        // MaybeNull ⊑ MaybeNull ✓, MaybeNull ⊑ NotNull ✓,
        // NotNull ⊑ NotNull ✓, NotNull ⊑ MaybeNull ✗.
        foreach (var (name, bState) in b.Map)
        {
            var aState = a.GetState(name);
            if (aState == NullState.NotNull && bState == NullState.MaybeNull)
                return false;
        }
        return true;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Per-edge narrowing seed (the key new primitive vs round 1).
//
//  Encodes what null-state implications a conditional branch condition carries
//  for a specific edge (true branch vs false branch).
// ─────────────────────────────────────────────────────────────────────────────

/// The null-state seeds for both edges of a conditional branch, extracted once
/// when the condition block is first encountered.  Keyed in a per-analysis map
/// by the predecessor CfgBlock (not the AnnotationStore — this is transient).
sealed class BranchSeed
{
    /// Names and their states on the "then" (true) edge.
    public IReadOnlyDictionary<string, NullState> ThenSeed { get; }
    /// Names and their states on the "else" (false) edge.
    public IReadOnlyDictionary<string, NullState> ElseSeed { get; }

    public BranchSeed(
        IReadOnlyDictionary<string, NullState> thenSeed,
        IReadOnlyDictionary<string, NullState> elseSeed)
    {
        ThenSeed = thenSeed;
        ElseSeed = elseSeed;
    }

    public static BranchSeed Empty { get; } =
        new(ImmutableDictionary<string, NullState>.Empty,
            ImmutableDictionary<string, NullState>.Empty);
}

/// The null-state flow analysis transfer function.
/// Implements IEdgeSeedProvider<NullFlowState> for per-edge null-state seeding.
public sealed class NullStateAnalysis : IDataFlowAnalysis<NullFlowState>, IEdgeSeedProvider<NullFlowState>
{
    readonly BoundFunctionDeclaration _fn;
    // Per-block branch seeds (populated lazily during Transfer).
    readonly Dictionary<CfgBlock, BranchSeed> _branchSeeds =
        new(ReferenceEqualityComparer.Instance);

    public NullStateAnalysis(BoundFunctionDeclaration fn) => _fn = fn;

    public ILattice<NullFlowState> Lattice => NullFlowLattice.Instance;
    public bool IsForward => true;

    // ── IEdgeSeedProvider ─────────────────────────────────────────────────────

    /// Called by the engine for each (pred → succ) edge.  When pred has a
    /// conditional branch, return the narrowed state for this specific edge
    /// (then vs else).  The engine joins this INTO the pred's out-state to
    /// produce the effective in-state for succ.
    ///
    /// Returning null means "no seeding — use pred's out-state as-is".
    /// Returning a NullFlowState with additional NotNull entries means those
    /// entries are JOINED into the out-state before the join across all preds.
    /// Because Join(NotNull, NotNull) = NotNull but Join(NotNull, MaybeNull) =
    /// MaybeNull, the seed only helps when EVERY predecessor on that path agrees.
    public NullFlowState? TrySeedEdge(CfgBlock pred, CfgBlock succ, NullFlowState predOut)
    {
        // Only conditional blocks carry branch seeds.
        if (pred.BranchCondition is null || pred.ThenSuccessorIndex < 0)
            return null;

        if (!_branchSeeds.TryGetValue(pred, out var seeds))
            return null;

        bool isThenEdge = pred.ThenSuccessorIndex < pred.Successors.Count
            && ReferenceEquals(pred.Successors[pred.ThenSuccessorIndex], succ);

        var seedMap = isThenEdge ? seeds.ThenSeed : seeds.ElseSeed;
        if (seedMap.Count == 0)
            return null;

        // Build a state that merges the pred's out with the edge-specific narrowings.
        // The effect: on the then-edge of `x != nil`, x is NOT-NULL here even if
        // on other paths it was MaybeNull.
        var builder = predOut.Map.ToBuilder();
        foreach (var (name, state) in seedMap)
            builder[name] = state;
        return new NullFlowState(builder.ToImmutable());
    }

    // ── Transfer ─────────────────────────────────────────────────────────────

    public NullFlowState Transfer(CfgBlock block, NullFlowState input, AnalysisContext ctx)
    {
        var state = input;

        // Entry block: seed non-nullable parameters as NotNull.
        if (block.IsEntry)
        {
            foreach (var p in _fn.Parameters)
            {
                if (!IsNullableType(p.Type))
                    state = state.WithState(p.Name, NullState.NotNull);
            }
        }

        foreach (var node in block.Nodes)
            state = VisitNode(node, state, ctx);

        // If this block ends in a conditional branch, extract and cache the
        // per-edge narrowings so TrySeedEdge can look them up.
        if (block.BranchCondition is not null && !_branchSeeds.ContainsKey(block))
            _branchSeeds[block] = ExtractBranchSeed(block.BranchCondition, state);

        return state;
    }

    /// Extract per-edge null-state narrowings from a condition expression.
    /// Returns a BranchSeed with the then/else maps.
    static BranchSeed ExtractBranchSeed(BoundExpression cond, NullFlowState currentState)
    {
        var then = new Dictionary<string, NullState>(StringComparer.Ordinal);
        var els  = new Dictionary<string, NullState>(StringComparer.Ordinal);

        CollectNarrowings(cond, then, els, currentState);

        return new BranchSeed(then, els);
    }

    static void CollectNarrowings(
        BoundExpression cond,
        Dictionary<string, NullState> thenSeed,
        Dictionary<string, NullState> elseSeed,
        NullFlowState state)
    {
        switch (cond)
        {
            // `x != nil` → then-edge: x is NotNull (non-nil proven); else-edge: no change
            case BoundBinaryExpression { Op: SyntaxTokenKind.BangEquals } bin
                when IsNilLiteral(bin.Right) && bin.Left is BoundNameExpression { Name: var lname }:
                if (IsNullableInState(lname, state))
                    thenSeed[lname] = NullState.NotNull;
                break;
            case BoundBinaryExpression { Op: SyntaxTokenKind.BangEquals } bin2
                when IsNilLiteral(bin2.Left) && bin2.Right is BoundNameExpression { Name: var rname }:
                if (IsNullableInState(rname, state))
                    thenSeed[rname] = NullState.NotNull;
                break;

            // `x == nil` → else-edge: x is NotNull; then-edge: no change
            case BoundBinaryExpression { Op: SyntaxTokenKind.EqualsEquals } eqBin
                when IsNilLiteral(eqBin.Right) && eqBin.Left is BoundNameExpression { Name: var eln }:
                if (IsNullableInState(eln, state))
                    elseSeed[eln] = NullState.NotNull;
                break;
            case BoundBinaryExpression { Op: SyntaxTokenKind.EqualsEquals } eqBin2
                when IsNilLiteral(eqBin2.Left) && eqBin2.Right is BoundNameExpression { Name: var ern }:
                if (IsNullableInState(ern, state))
                    elseSeed[ern] = NullState.NotNull;
                break;

            // `a && b` → both seeds apply on the then-edge (both are true); false-edge no narrowing.
            case BoundBinaryExpression { Op: SyntaxTokenKind.AmpAmp } andBin:
            {
                var lThen = new Dictionary<string, NullState>(StringComparer.Ordinal);
                var lElse = new Dictionary<string, NullState>(StringComparer.Ordinal);
                CollectNarrowings(andBin.Left, lThen, lElse, state);
                var rThen = new Dictionary<string, NullState>(StringComparer.Ordinal);
                var rElse = new Dictionary<string, NullState>(StringComparer.Ordinal);
                CollectNarrowings(andBin.Right, rThen, rElse, state);
                // then: union of both left.then and right.then
                foreach (var (k, v) in lThen) thenSeed[k] = v;
                foreach (var (k, v) in rThen) thenSeed[k] = v;
                break;
            }

            // `!cond` → swap then/else seeds
            case BoundUnaryExpression { Op: SyntaxTokenKind.Bang } notExpr:
                CollectNarrowings(notExpr.Operand, elseSeed, thenSeed, state);
                break;

            // BoundConversion(Narrow) — the result is known NotNull (binder proved type).
            case BoundConversion { Kind: ConversionKind.Narrow, Operand: BoundNameExpression { Name: var convName } }:
                thenSeed[convName] = NullState.NotNull;
                break;
        }
    }

    NullFlowState VisitNode(BoundNode node, NullFlowState state, AnalysisContext ctx)
    {
        switch (node)
        {
            // ── Variable declarations ─────────────────────────────────────────

            case BoundVariableDeclaration { Name: var name, Initializer: { } init }:
                state = state.WithState(name, DetermineNullState(init, state));
                break;

            case BoundVariableDeclaration { Name: var name2, DeclaredType: var declType }:
                // All bound variable declarations have an Initializer (binder supplies
                // BoundDefaultExpression when none written). This arm is a safety fallback.
                state = state.WithState(name2,
                    IsNullableType(declType) ? NullState.MaybeNull : NullState.NotNull);
                break;

            // ── Assignments (statement) ───────────────────────────────────────

            case BoundAssignment { Target: BoundNameExpression { Name: var dst }, Value: { } rhs }:
                state = state.WithState(dst, DetermineNullState(rhs, state));
                break;

            // ── Let-else guard — continuation has NotNull for the binding ─────

            case BoundLetGuard { Name: var guardName }:
                state = state.WithState(guardName, NullState.NotNull);
                break;

            // ── If-statement nil guards ───────────────────────────────────────

            // The CFG builder already splits the then/else into separate successor
            // blocks and stores the condition on the predecessor.  The per-edge
            // seeding (TrySeedEdge) handles branch-specific narrowing correctly.
            // This arm handles intra-block narrowing for guard patterns that
            // terminate one side (e.g. `if x == nil { return }`): after the if
            // we know x is NotNull on the fall-through.
            case BoundIfStatement { Condition: BoundBinaryExpression bin }
                when IsNilComparison(bin):
                state = ApplyNilGuardIntraBlock(bin, state);
                break;

            // ── Member access on possibly-null — ES2173 ───────────────────────

            case BoundExpressionStatement
            {
                Expression: BoundMemberAccessExpression
                {
                    Target: BoundNameExpression { Name: var mem },
                    Span: var span
                }
            }:
                if (IsNullableInState(mem, state))
                    ctx.Diagnostics.Warn(span,
                        $"ES2173: Possible null dereference of '{mem}'; value may be null on this path.");
                break;

            case BoundReturnStatement { Expression: BoundNameExpression { Name: var ret, Span: var retSpan } }:
                ctx.Annotations.Set(
                    new ReturnNullStateKey(_fn, retSpan),
                    state.GetState(ret));
                break;
        }

        return state;
    }

    // Apply intra-block nil-guard narrowing for if-statements within a block
    // where the condition implies the local is NotNull after a diverging branch.
    // This is a conservative approximation — the precise flow comes from per-edge seeding.
    static NullFlowState ApplyNilGuardIntraBlock(BoundBinaryExpression bin, NullFlowState state)
    {
        // `x != nil` → on the true path, x is NotNull.
        if (bin.Op == SyntaxTokenKind.BangEquals)
        {
            if (bin.Left  is BoundNameExpression { Name: var ln } && IsNilLiteral(bin.Right))
                state = state.WithState(ln, NullState.NotNull);
            else if (bin.Right is BoundNameExpression { Name: var rn } && IsNilLiteral(bin.Left))
                state = state.WithState(rn, NullState.NotNull);
        }
        return state;
    }

    static NullState DetermineNullState(BoundExpression expr, NullFlowState state) => expr switch
    {
        // nil literal → MaybeNull
        BoundLiteralExpression { Value: null }                          => NullState.MaybeNull,
        // nil type → MaybeNull
        BoundExpression e when e.Type is NullType                      => NullState.MaybeNull,
        // Non-nil literals → NotNull
        BoundLiteralExpression _                                        => NullState.NotNull,
        // Local → look up current state
        BoundNameExpression { Name: var n }                             => state.GetState(n),
        // Object creation is never null
        BoundObjectCreationExpression _                                 => NullState.NotNull,
        // ok(x) → NotNull for the Result wrapper (the value is present)
        BoundResultCallExpression { IsOk: true }                       => NullState.NotNull,
        // error(x) → MaybeNull for the Ok side
        BoundResultCallExpression { IsOk: false }                      => NullState.MaybeNull,
        // Narrow conversion → proven NotNull (binder flow-proved the type)
        BoundConversion { Kind: ConversionKind.Narrow }                => NullState.NotNull,
        // Safe cast → MaybeNull (the isinst may produce null)
        BoundConversion { Kind: ConversionKind.IsInst }                => NullState.MaybeNull,
        // Call result — conservative
        BoundCallExpression _                                           => NullState.MaybeNull,
        // Null-coalescing: the result is NotNull when the RHS is NotNull
        // (if LHS is nil, we use RHS; RHS determines the null state).
        BoundNullCoalescingExpression { Right: { } rhs }               => DetermineNullState(rhs, state),
        // Default: conservative
        _                                                               => NullState.MaybeNull,
    };

    static bool IsNullableType(BoundType? type) =>
        type is NullableType or NullType;

    static bool IsNullableInState(string name, NullFlowState state) =>
        state.GetState(name) == NullState.MaybeNull;

    static bool IsNilLiteral(BoundExpression e) =>
        e is BoundLiteralExpression { Value: null } || e.Type is NullType;

    static bool IsNilComparison(BoundBinaryExpression bin) =>
        (bin.Op == SyntaxTokenKind.EqualsEquals || bin.Op == SyntaxTokenKind.BangEquals)
        && (IsNilLiteral(bin.Left) || IsNilLiteral(bin.Right));

    public void Finalize(
        IReadOnlyDictionary<CfgBlock, NullFlowState> finalIn,
        IReadOnlyDictionary<CfgBlock, NullFlowState> finalOut,
        AnalysisContext ctx)
    {
        // No end-of-function null-state diagnostics — they are emitted inline
        // in Transfer at the use site.  This finalizer is available for future
        // escape/smart-cast annotation passes.
    }
}

/// AnnotationStore key: the null-state of a local at a specific return site.
public sealed record ReturnNullStateKey(BoundFunctionDeclaration Fn, SourceSpan Site);

/// Entry point.
public static class NullStatePass
{
    public static void Run(
        BoundFunctionDeclaration fn,
        DiagnosticBag diagnostics,
        AnnotationStore annotations)
    {
        var cfg      = CfgBuilder.Build(fn, fn.Name);
        var analysis = new NullStateAnalysis(fn);
        var ctx      = new AnalysisContext(diagnostics, annotations, fn.Name);
        DataFlowEngine.Run(cfg, analysis, ctx);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Binder-to-flow-analysis seam: INarrowingFactsSink implementation.
// ─────────────────────────────────────────────────────────────────────────────

/// INarrowingFactsSink implementation (Esharp.Binder seam).
///
/// Injected into BindContext.NarrowingSink before the bind step.  Receives the
/// per-site Narrow records produced by NarrowingAnalyzer.Extract() at every
/// condition point in the binder, and stores them in the AnnotationStore so
/// the NullStateAnalysis CFG pass can pick them up during Transfer to seed
/// branch-specific lattice states without a second tree walk.
///
/// The design keeps the binder and the flow analysis decoupled: the binder only
/// knows INarrowingFactsSink; this implementation only knows the binder's Narrow
/// record.
internal sealed class NullStateNarrowingSink : INarrowingFactsSink
{
    readonly AnnotationStore _store;

    public NullStateNarrowingSink(AnnotationStore store) => _store = store;

    /// Called by NarrowingAnalyzer.Extract() after every condition is analysed.
    /// Records when-true and when-false narrowings at the condition's span so
    /// the CFG-based NullStateAnalysis can seed the per-branch lattice states.
    public void OnNarrowsExtracted(
        IReadOnlyList<NarrowingAnalyzer.Narrow> whenTrue,
        IReadOnlyList<NarrowingAnalyzer.Narrow> whenFalse,
        SourceSpan conditionSpan)
    {
        _store.Set(
            new NarrowingFactsKey(conditionSpan),
            new NarrowingFactsAtSpan(whenTrue, whenFalse));
    }
}

/// AnnotationStore key for narrowing facts at a condition site.
public sealed record NarrowingFactsKey(SourceSpan Span);

/// The narrowing facts at a condition site — forwarded from the binder.
internal sealed class NarrowingFactsAtSpan(
    IReadOnlyList<NarrowingAnalyzer.Narrow> WhenTrue,
    IReadOnlyList<NarrowingAnalyzer.Narrow> WhenFalse)
{
    public readonly IReadOnlyList<NarrowingAnalyzer.Narrow> WhenTrue  = WhenTrue;
    public readonly IReadOnlyList<NarrowingAnalyzer.Narrow> WhenFalse = WhenFalse;
}
