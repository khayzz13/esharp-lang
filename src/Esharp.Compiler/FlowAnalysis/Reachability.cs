using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Syntax;

namespace Esharp.FlowAnalysis;

// ─────────────────────────────────────────────────────────────────────────────
//  Reachability analysis.
//
//  Forward DFA pass that determines which CFG blocks are reachable from the
//  entry.  Two diagnostic categories:
//
//    ES2140 — non-void function does not return on all paths (definite return)
//    ES2141 — unreachable code after unconditional terminator
//
//  Lattice: {Unreachable=0, Reachable=1}, join = max (Reachable wins).
//  Entry block is seeded Reachable; all others start Unreachable.
//
//  Terminators (after one of these, continuation is Unreachable):
//    BoundReturnStatement, BoundThrowStatement, infinite BoundWhileStatement
//    (condition is literal `true` and body contains no BoundBreakStatement).
//
//  Match-awareness:
//    An exhaustive BoundMatchStatement where every arm body terminates counts
//    as a terminator for definite-return purposes.  Exhaustiveness is read
//    from the AnnotationStore (MatchExhaustivenessPass must run first, or the
//    key will be absent — treated conservatively as non-exhaustive).
// ─────────────────────────────────────────────────────────────────────────────

public enum ReachabilityState
{
    Unreachable = 0,
    Reachable   = 1,
}

sealed class ReachabilityLattice : ILattice<ReachabilityState>
{
    public static readonly ReachabilityLattice Instance = new();
    public ReachabilityState Bottom => ReachabilityState.Unreachable;
    // Reachable wins: any predecessor reaching a block makes it reachable.
    public ReachabilityState Join(ReachabilityState a, ReachabilityState b) =>
        (a == ReachabilityState.Reachable || b == ReachabilityState.Reachable)
            ? ReachabilityState.Reachable
            : ReachabilityState.Unreachable;
    // Leq: Unreachable ⊑ Reachable (Unreachable is less information).
    public bool Leq(ReachabilityState a, ReachabilityState b) =>
        a == ReachabilityState.Unreachable || b == ReachabilityState.Reachable;
}

/// The reachability analysis transfer function.
///
/// Transfer is identity: a reachable block propagates Reachable to its
/// successors; an unreachable block propagates Unreachable and emits ES2141
/// for its nodes.
public sealed class ReachabilityAnalysis : IDataFlowAnalysis<ReachabilityState>
{
    readonly BoundFunctionDeclaration _fn;
    readonly bool _nonVoidReturn;
    readonly AnnotationStore _annotations;

    public ReachabilityAnalysis(BoundFunctionDeclaration fn, bool nonVoidReturn, AnnotationStore annotations)
    {
        _fn            = fn;
        _nonVoidReturn = nonVoidReturn;
        _annotations   = annotations;
    }

    public ILattice<ReachabilityState> Lattice => ReachabilityLattice.Instance;
    public bool IsForward => true;

    public ReachabilityState Transfer(CfgBlock block, ReachabilityState input, AnalysisContext ctx)
    {
        if (input == ReachabilityState.Unreachable)
        {
            // The block is dead — diagnose its first node.
            if (block.Nodes.Count > 0 && !block.IsEntry)
            {
                var span = FirstSpan(block);
                if (span != default)
                {
                    ctx.Diagnostics.Warn(span, "ES2141: Unreachable code detected.");
                }
            }
            return ReachabilityState.Unreachable;
        }

        // Reachable block: scan for an internal terminator followed by more nodes.
        bool terminated = false;
        foreach (var node in block.Nodes)
        {
            if (terminated)
            {
                var span = SpanOf(node);
                if (span != default)
                    ctx.Diagnostics.Warn(span, "ES2141: Unreachable code after unconditional terminator.");
                break;
            }
            terminated = IsTerminator(node, _annotations);
        }

        return ReachabilityState.Reachable;
    }

    public void Finalize(
        IReadOnlyDictionary<CfgBlock, ReachabilityState> finalIn,
        IReadOnlyDictionary<CfgBlock, ReachabilityState> finalOut,
        AnalysisContext ctx)
    {
        if (!_nonVoidReturn) return;

        // ES2140: every reachable CFG exit block must end with a terminator.
        // A block is an exit if it has no successors or is marked IsExit.
        foreach (var (block, state) in finalIn)
        {
            if (state == ReachabilityState.Unreachable) continue;
            // Only check blocks that are exits (no successors or flagged).
            if (!block.IsExit && block.Successors.Count > 0) continue;

            var last = block.Nodes.LastOrDefault();
            if (last is null)
            {
                // Empty reachable exit — no return.
                ctx.Diagnostics.Report(
                    _fn.Span,
                    $"ES2140: Function '{_fn.Name}' does not return on all paths.");
                break;
            }

            if (IsTerminator(last, _annotations))
                continue;

            ctx.Diagnostics.Report(
                _fn.Span,
                $"ES2140: Function '{_fn.Name}' does not return on all paths.");
            break; // one error per function
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    static bool IsTerminator(BoundNode node, AnnotationStore annotations) => node switch
    {
        BoundReturnStatement     => true,
        BoundThrowStatement      => true,
        BoundWhileStatement wh   => IsInfiniteLoop(wh),
        BoundMatchStatement ms   => IsExhaustiveTerminator(ms, annotations),
        _                        => false,
    };

    static bool IsInfiniteLoop(BoundWhileStatement wh) =>
        wh.Condition is BoundLiteralExpression { Value: true }
        && !BlockContainsBreak(wh.Body);

    static bool BlockContainsBreak(BoundStatement stmt) => stmt switch
    {
        BoundBreakStatement      => true,
        BoundBlockStatement blk  => blk.Statements.Any(BlockContainsBreak),
        BoundIfStatement ifStmt  => BlockContainsBreak(ifStmt.Then)
                                    || (ifStmt.Else is not null && BlockContainsBreak(ifStmt.Else)),
        BoundWhileStatement _    => false, // inner loops own their own breaks
        _                        => false,
    };

    static bool IsExhaustiveTerminator(BoundMatchStatement ms, AnnotationStore annotations)
    {
        // Check the annotation store for the exhaustiveness verdict of this match.
        // The ExhaustivenessKey is keyed on BoundMatchExpression, not BoundMatchStatement.
        // For statement-form matches, check if all arms terminate.
        if (!annotations.TryGet<ExhaustivenessVerdict>(new MatchStatementExhaustivenessKey(ms), out var verdict))
            return false; // conservative: unknown → non-exhaustive

        if (!verdict.IsExhaustive) return false;
        return ms.Arms.All(arm => ArmTerminates(arm.Body));
    }

    static bool ArmTerminates(BoundBlockStatement body)
    {
        if (body.Statements.Count == 0) return false;
        return body.Statements.Any(s => s is BoundReturnStatement or BoundThrowStatement);
    }

    static SourceSpan FirstSpan(CfgBlock block)
    {
        foreach (var n in block.Nodes)
        {
            var s = SpanOf(n);
            if (s != default) return s;
        }
        return default;
    }

    static SourceSpan SpanOf(BoundNode node) => node switch
    {
        BoundStatement stmt => stmt.Span,
        BoundExpression expr => expr.Span,
        _ => default,
    };
}

/// AnnotationStore key for the exhaustiveness verdict of a BoundMatchStatement.
/// (Distinct from ExhaustivenessKey which is keyed on BoundMatchExpression.)
public sealed record MatchStatementExhaustivenessKey(BoundMatchStatement Match);

/// Entry point used by the pipeline.
public static class ReachabilityPass
{
    public static void Run(
        BoundFunctionDeclaration fn,
        bool nonVoidReturn,
        DiagnosticBag diagnostics,
        AnnotationStore annotations)
    {
        var cfg = CfgBuilder.Build(fn, fn.Name);
        // Seed the entry block as Reachable: wrap the analysis to override the
        // engine's Bottom-init on the entry (which has no predecessors).
        var analysis = new SeededReachabilityAnalysis(fn, nonVoidReturn, annotations, cfg.Entry);
        var ctx      = new AnalysisContext(diagnostics, annotations, fn.Name);
        DataFlowEngine.Run(cfg, analysis, ctx);
    }

    /// Wraps ReachabilityAnalysis and seeds the entry block Reachable before
    /// the engine's predecessor-join (which would leave it Unreachable since
    /// the entry has no predecessors).
    sealed class SeededReachabilityAnalysis : IDataFlowAnalysis<ReachabilityState>
    {
        readonly ReachabilityAnalysis _inner;
        readonly CfgBlock _entry;

        public SeededReachabilityAnalysis(
            BoundFunctionDeclaration fn,
            bool nonVoidReturn,
            AnnotationStore annotations,
            CfgBlock entry)
        {
            _inner = new ReachabilityAnalysis(fn, nonVoidReturn, annotations);
            _entry = entry;
        }

        public ILattice<ReachabilityState> Lattice => _inner.Lattice;
        public bool IsForward => true;

        public ReachabilityState Transfer(CfgBlock block, ReachabilityState input, AnalysisContext ctx)
        {
            // The engine hands the entry Bottom (no predecessors); override to Reachable.
            if (ReferenceEquals(block, _entry) && input == ReachabilityState.Unreachable)
                input = ReachabilityState.Reachable;
            return _inner.Transfer(block, input, ctx);
        }

        public void Finalize(
            IReadOnlyDictionary<CfgBlock, ReachabilityState> finalIn,
            IReadOnlyDictionary<CfgBlock, ReachabilityState> finalOut,
            AnalysisContext ctx)
            => _inner.Finalize(finalIn, finalOut, ctx);
    }
}
