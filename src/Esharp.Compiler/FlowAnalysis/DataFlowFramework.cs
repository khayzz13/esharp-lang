using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Syntax;

namespace Esharp.FlowAnalysis;

// ─────────────────────────────────────────────────────────────────────────────
//  DataFlow framework — generic forward/backward fixpoint over a bound
//  function's control-flow graph.
//
//  Design:
//    • One Block = one straight-line sequence of BoundNode values (statements /
//      expressions) that ends with a Terminator (branch, return, throw, loop-
//      back, fall-through).
//    • An analysis supplies a Lattice (join, leq, bottom) and a Transfer
//      function (forward: pre → post per block, backward: post → pre per block).
//    • The engine drives a standard worklist until in/out sets reach a fixed
//      point.  Convergence is guaranteed when the lattice has finite height and
//      the transfer functions are monotone — both hold for every analysis here.
//    • Per-edge seeding: analyses can supply an IEdgeSeedProvider that pre-seeds
//      the in-state on each (predecessor → successor) edge before the join.
//      NullStateAnalysis uses this to push narrowed types through branch edges
//      (the "then" edge gets NotNull for a `!= nil` condition, the "else" edge
//      gets MaybeNull) without a second tree walk.
//
//  Output:
//    Analyses produce (a) diagnostics via the DiagnosticBag passed in, and (b)
//    annotations recorded in an AnnotationStore the pipeline hands downstream.
//    They do NOT rewrite the bound tree — that is Pillar 3's job.
//
//  Contract (module-map §B3):
//    DataFlowFramework is the single shared engine; each analysis in this module
//    is an instance of IDataFlowAnalysis<TState>.  EscapeAnalysis replaces the
//    old standalone PointerEscapeAnalysis (retained at src/Esharp.FlowAnalysis/
//    PointerEscapeAnalysis.cs for history) and is layered on this framework.
//    InterproceduralEscapeOrchestrator extends EscapeAnalysis with a cross-
//    function fixpoint for *T pointer representation decisions.
//
//  BoundNode:
//    CfgBlock.Nodes is typed as IReadOnlyList<BoundNode> — the frozen-contract
//    base class shared by BoundStatement and BoundExpression. Pattern matching
//    on BoundStatement / BoundExpression still works via the is-subtype check.
// ─────────────────────────────────────────────────────────────────────────────

/// The abstract value lattice an analysis works over.  The engine only calls
/// Join, Leq, and Bottom; monotonicity and finite-height are analysis invariants.
public interface ILattice<TState>
{
    /// The least element: no information — start state for every block before
    /// the first pass touches it.
    TState Bottom { get; }

    /// Least-upper-bound (merge point): join information from two predecessor
    /// paths into a conservative approximation satisfying both.
    TState Join(TState a, TState b);

    /// Whether a ⊑ b (a is no more information than b).
    /// Used by the fixpoint check: if new state ⊑ old state, the block is done.
    bool Leq(TState a, TState b);
}

/// A forward or backward data-flow analysis over CFG blocks.
public interface IDataFlowAnalysis<TState>
{
    ILattice<TState> Lattice { get; }

    /// Direction: true = forward (entry → exit), false = backward (exit → entry).
    bool IsForward { get; }

    /// Transfer function: given the input state at the block's entry (forward)
    /// or exit (backward), produce the output state at its exit (forward) or
    /// entry (backward).  May emit diagnostics and record annotations as a side
    /// effect.
    TState Transfer(CfgBlock block, TState input, AnalysisContext ctx);

    /// Called once after the fixpoint converges with the final per-block states.
    /// The analysis may make a final diagnostic / annotation pass here rather
    /// than scattering checks across Transfer.
    void Finalize(IReadOnlyDictionary<CfgBlock, TState> finalIn,
                  IReadOnlyDictionary<CfgBlock, TState> finalOut,
                  AnalysisContext ctx);
}

/// Per-edge state seeding: provides the state to propagate along a specific
/// (predecessor → successor) CFG edge, BEFORE the normal join across all
/// predecessors.  The engine merges the edge seed into the predecessor's out-
/// state before using it in the join.
///
/// NullStateAnalysis implements this to push narrowed null-states through
/// condition branches:
///   • for a conditional block ending in `x != nil`, the then-edge is seeded
///     with {x: NotNull}, the else-edge with {x: MaybeNull}.
///   • the engine joins (outState[pred] ⊔ seed) rather than just outState[pred].
///
/// When no seed is provided for an edge the normal outState[pred] is used.
public interface IEdgeSeedProvider<TState>
{
    /// Return the seed state for the edge from <paramref name="pred"/> to
    /// <paramref name="succ"/>, or null if no seeding is needed for this edge.
    TState? TrySeedEdge(CfgBlock pred, CfgBlock succ, TState predOutState);
}

/// A single basic block in the CFG: an ordered list of bound-tree nodes and
/// the successor / predecessor edges that wire the graph.
///
/// Nodes are typed as BoundNode — the common base of BoundStatement and
/// BoundExpression established in Esharp.BoundTree.  Analyses pattern-match on
/// BoundStatement / BoundExpression as appropriate.
public sealed class CfgBlock
{
    public int Id { get; }
    public string Label { get; }

    /// The bound-tree nodes in this block, in program order.
    public IReadOnlyList<BoundNode> Nodes { get; }

    /// Successor blocks (fall-through and branch targets).
    public List<CfgBlock> Successors { get; } = [];

    /// Predecessor blocks.
    public List<CfgBlock> Predecessors { get; } = [];

    /// True when this block is the CFG entry (the function's first block).
    public bool IsEntry { get; init; }

    /// True when this block is a CFG exit (contains a return or throw).
    public bool IsExit { get; init; }

    /// If this block ends in a conditional branch, the condition expression
    /// is stored here so IEdgeSeedProvider implementations can inspect it.
    public BoundExpression? BranchCondition { get; init; }

    /// The "then" successor index in Successors (index 0) for conditional blocks.
    /// -1 for unconditional blocks.
    public int ThenSuccessorIndex { get; init; } = -1;

    public CfgBlock(int id, string label, IReadOnlyList<BoundNode> nodes)
    {
        Id = id;
        Label = label;
        Nodes = nodes;
    }

    public override string ToString() => $"Block[{Id}:{Label}]";
}

/// The context passed to transfer functions and finalizers — access to
/// diagnostics, the bound-tree annotation store, and the source-level
/// function name (for diagnostic messages).
public sealed class AnalysisContext
{
    public DiagnosticBag Diagnostics { get; }
    public AnnotationStore Annotations { get; }
    public string FunctionName { get; }

    public AnalysisContext(DiagnosticBag diagnostics, AnnotationStore annotations, string functionName)
    {
        Diagnostics = diagnostics;
        Annotations = annotations;
        FunctionName = functionName;
    }
}

/// Sparse key→value map for per-node and per-block annotations the pipeline
/// passes downstream (escape decisions on *T TypeRefs, narrowed types feeding
/// BoundConversion, exhaustiveness verdicts for MatchLowering).
/// All writes are idempotent: re-annotating a key with the same value is a
/// no-op; a conflicting write is a framework bug and throws.
public sealed class AnnotationStore
{
    readonly Dictionary<object, object?> _map = new(ReferenceEqualityComparer.Instance);

    public void Set<T>(object key, T value)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            if (!Equals(existing, value))
                throw new InvalidOperationException(
                    $"AnnotationStore conflict on {key}: existing={existing}, new={value}");
            return;
        }
        _map[key] = value;
    }

    public bool TryGet<T>(object key, out T value)
    {
        if (_map.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }
        value = default!;
        return false;
    }

    public T GetOrDefault<T>(object key, T fallback = default!) =>
        TryGet<T>(key, out var v) ? v : fallback;
}

/// The CFG of one function body — entry, exit, all reachable blocks in RPO.
public sealed class ControlFlowGraph
{
    public CfgBlock Entry { get; }
    public CfgBlock Exit { get; }

    /// All blocks in reverse-post-order (forward analyses iterate this list;
    /// backward analyses iterate its reverse).
    public IReadOnlyList<CfgBlock> BlocksInRpo { get; }

    public ControlFlowGraph(CfgBlock entry, CfgBlock exit, IReadOnlyList<CfgBlock> rpo)
    {
        Entry = entry;
        Exit = exit;
        BlocksInRpo = rpo;
    }
}

/// The fixpoint engine.  Drive an IDataFlowAnalysis over a CFG until the
/// in/out states converge.  Convergence is guaranteed for finite-height
/// lattices with monotone transfer functions.
///
/// Per-edge seeding: if the analysis also implements IEdgeSeedProvider<TState>,
/// the engine calls TrySeedEdge for each (pred→succ) pair and joins the seed
/// into the join before computing the in-state.  This enables precise branch-
/// specific narrowing (e.g. NotNull on the then-edge of `x != nil`) without
/// a separate pass or annotation-store indirection.
public static class DataFlowEngine
{
    /// Run <paramref name="analysis"/> over <paramref name="cfg"/> with the
    /// supplied context.  Returns the fixed-point per-block IN and OUT maps.
    public static (
        IReadOnlyDictionary<CfgBlock, TState> In,
        IReadOnlyDictionary<CfgBlock, TState> Out
    ) Run<TState>(
        ControlFlowGraph cfg,
        IDataFlowAnalysis<TState> analysis,
        AnalysisContext ctx)
    {
        var lattice  = analysis.Lattice;
        var seeder   = analysis as IEdgeSeedProvider<TState>;
        var blocks   = analysis.IsForward
            ? cfg.BlocksInRpo
            : (IReadOnlyList<CfgBlock>)cfg.BlocksInRpo.Reverse().ToList();

        var inState  = new Dictionary<CfgBlock, TState>(ReferenceEqualityComparer.Instance);
        var outState = new Dictionary<CfgBlock, TState>(ReferenceEqualityComparer.Instance);

        foreach (var b in cfg.BlocksInRpo)
        {
            inState[b]  = lattice.Bottom;
            outState[b] = lattice.Bottom;
        }

        var worklist   = new Queue<CfgBlock>(blocks);
        var inWorklist = new HashSet<CfgBlock>(blocks, ReferenceEqualityComparer.Instance);

        while (worklist.Count > 0)
        {
            var block = worklist.Dequeue();
            inWorklist.Remove(block);

            // Compute input: join over predecessors (forward) or successors
            // (backward), with optional per-edge seed contributions.
            var preds = analysis.IsForward ? block.Predecessors : block.Successors;
            TState input = lattice.Bottom;
            foreach (var pred in preds)
            {
                var predOut = analysis.IsForward ? outState[pred] : inState[pred];
                TState edgeState = predOut;
                if (seeder is not null)
                {
                    // Let the analysis refine the state along this specific edge.
                    var seed = seeder.TrySeedEdge(
                        analysis.IsForward ? pred : block,
                        analysis.IsForward ? block : pred,
                        predOut);
                    if (seed is not null)
                        edgeState = lattice.Join(predOut, seed);
                }
                input = lattice.Join(input, edgeState);
            }
            inState[block] = input;

            // Apply the transfer function.
            var output = analysis.Transfer(block, input, ctx);

            // If the output changed, re-enqueue affected neighbours.
            var oldOutput = outState[block];
            if (!lattice.Leq(output, oldOutput) || !lattice.Leq(oldOutput, output))
            {
                outState[block] = output;
                var next = analysis.IsForward ? block.Successors : block.Predecessors;
                foreach (var s in next)
                {
                    if (inWorklist.Add(s))
                        worklist.Enqueue(s);
                }
            }
        }

        analysis.Finalize(inState, outState, ctx);
        return (inState, outState);
    }
}

/// Builds a ControlFlowGraph from one BoundFunctionDeclaration body.
///
/// Visits the flat statement list of BoundBlockStatement and groups nodes into
/// basic blocks, emitting edges for branches, loops, and early returns.
/// BoundParameter nodes are NOT added to the block node list (they have no
/// span and are not bound nodes) — instead, the entry block's parameter
/// pre-assignment is handled by the DA analysis consulting fn.Parameters
/// directly.
///
/// For conditional branches (BoundIfStatement), the builder records the branch
/// condition on the predecessor block and marks the then-successor index so that
/// IEdgeSeedProvider implementations can look up the condition when seeding.
public static class CfgBuilder
{
    public static ControlFlowGraph Build(BoundFunctionDeclaration fn, string fnName)
    {
        var b = new Builder(fnName);
        b.VisitFunction(fn);
        return b.Build();
    }

    sealed class Builder
    {
        readonly string _fnName;
        int _nextId;

        // Each raw block: mutable node list, outgoing edge ids, entry/exit flags,
        // optional branch condition and then-index.
        readonly List<(
            List<BoundNode> Nodes,
            List<int> SuccIds,
            bool IsEntry,
            bool IsExit,
            BoundExpression? BranchCondition,
            int ThenSuccIdx)> _rawBlocks = [];

        // Nodes accumulating into the current raw block before it is flushed.
        readonly List<BoundNode> _pending = [];

        // Stack of (breakTarget, continueTarget) block indices for loop nesting.
        readonly Stack<(int Break, int Continue)> _loopStack = new();

        public Builder(string fnName) => _fnName = fnName;

        // ── block management ──────────────────────────────────────────────────

        int NewRawBlock(bool isEntry = false, bool isExit = false)
        {
            var id = _nextId++;
            _rawBlocks.Add(([], [], isEntry, isExit, null, -1));
            return id;
        }

        void Emit(BoundNode node) => _pending.Add(node);

        void Flush(int blockIdx)
        {
            var (nodes, succIds, isEntry, isExit, cond, thenIdx) = _rawBlocks[blockIdx];
            nodes.AddRange(_pending);
            _rawBlocks[blockIdx] = (nodes, succIds, isEntry, isExit, cond, thenIdx);
            _pending.Clear();
        }

        void SetBranchCondition(int blockIdx, BoundExpression condition, int thenSuccIdx)
        {
            var (nodes, succIds, isEntry, isExit, _, _) = _rawBlocks[blockIdx];
            _rawBlocks[blockIdx] = (nodes, succIds, isEntry, isExit, condition, thenSuccIdx);
        }

        void AddEdge(int from, int to) => _rawBlocks[from].SuccIds.Add(to);

        // ── visit ─────────────────────────────────────────────────────────────

        public void VisitFunction(BoundFunctionDeclaration fn)
        {
            var entry = NewRawBlock(isEntry: true);
            // Parameters are pre-assigned; analyses read fn.Parameters directly.
            VisitBlock(fn.Body, entry);
        }

        void VisitBlock(BoundBlockStatement block, int cur)
        {
            Flush(cur);
            var currentIdx = cur;
            foreach (var stmt in block.Statements)
                currentIdx = VisitStatement(stmt, currentIdx);
        }

        /// Dispatch a BoundStatement into a block: if it IS a BoundBlockStatement
        /// use the optimised VisitBlock path; otherwise emit it as a single node.
        void VisitStmtIntoBlock(BoundStatement stmt, int blockIdx)
        {
            if (stmt is BoundBlockStatement blk)
                VisitBlock(blk, blockIdx);
            else
                VisitStatement(stmt, blockIdx);
        }

        int VisitStatement(BoundStatement stmt, int cur)
        {
            switch (stmt)
            {
                // ── terminators ───────────────────────────────────────────────

                case BoundReturnStatement ret:
                {
                    Emit(ret);
                    var exitId = NewRawBlock(isExit: true);
                    Flush(cur);
                    AddEdge(cur, exitId);
                    // The continuation after a return is a dead block.
                    var dead = NewRawBlock();
                    return dead;
                }

                case BoundThrowStatement th:
                {
                    Emit(th);
                    var exitId = NewRawBlock(isExit: true);
                    Flush(cur);
                    AddEdge(cur, exitId);
                    var dead = NewRawBlock();
                    return dead;
                }

                // ── branches ──────────────────────────────────────────────────

                case BoundIfStatement ifStmt:
                {
                    // Emit the condition into the predecessor block (cur), then
                    // record it so IEdgeSeedProvider can look it up.
                    Emit(ifStmt.Condition);
                    Flush(cur);

                    var thenId  = NewRawBlock();
                    var elseId  = NewRawBlock();
                    var afterId = NewRawBlock();

                    // then-edge is Successors[0], else-edge is Successors[1].
                    AddEdge(cur, thenId);
                    AddEdge(cur, elseId);
                    SetBranchCondition(cur, ifStmt.Condition, thenSuccIdx: 0);

                    VisitStmtIntoBlock(ifStmt.Then, thenId);
                    Flush(thenId);
                    AddEdge(thenId, afterId);

                    if (ifStmt.Else is { } elseStmt)
                    {
                        VisitStmtIntoBlock(elseStmt, elseId);
                        Flush(elseId);
                    }
                    AddEdge(elseId, afterId);

                    return afterId;
                }

                // ── loops ─────────────────────────────────────────────────────

                case BoundWhileStatement wh:
                {
                    Flush(cur);
                    var condId    = NewRawBlock();
                    var bodyId    = NewRawBlock();
                    var afterWhId = NewRawBlock();

                    AddEdge(cur, condId);
                    Emit(wh.Condition);
                    Flush(condId);
                    // then-edge (Successors[0]) = body, else-edge = after
                    AddEdge(condId, bodyId);
                    AddEdge(condId, afterWhId);
                    SetBranchCondition(condId, wh.Condition, thenSuccIdx: 0);

                    _loopStack.Push((afterWhId, condId));
                    VisitStmtIntoBlock(wh.Body, bodyId);
                    _loopStack.Pop();
                    Flush(bodyId);
                    AddEdge(bodyId, condId); // loop-back

                    return afterWhId;
                }

                case BoundBreakStatement brk:
                {
                    Emit(brk);
                    Flush(cur);
                    if (_loopStack.TryPeek(out var loop))
                        AddEdge(cur, loop.Break);
                    var dead = NewRawBlock();
                    return dead;
                }

                case BoundContinueStatement cont:
                {
                    Emit(cont);
                    Flush(cur);
                    if (_loopStack.TryPeek(out var loop))
                        AddEdge(cur, loop.Continue);
                    var dead = NewRawBlock();
                    return dead;
                }

                // ── try/catch — simplified ─────────────────────────────────────

                case BoundTryStatement tryStmt:
                {
                    Emit(tryStmt);
                    var bodyId  = NewRawBlock();
                    var afterId = NewRawBlock();

                    AddEdge(cur, bodyId);
                    VisitBlock(tryStmt.Body, bodyId);
                    Flush(bodyId);
                    AddEdge(bodyId, afterId);

                    foreach (var clause in tryStmt.Catches)
                    {
                        var catchId = NewRawBlock();
                        // Any statement in the try body can throw → the cur block edges to catch.
                        AddEdge(cur, catchId);
                        VisitBlock(clause.Body, catchId);
                        Flush(catchId);
                        AddEdge(catchId, afterId);
                    }
                    return afterId;
                }

                // ── nested blocks ─────────────────────────────────────────────

                case BoundBlockStatement inner:
                    VisitBlock(inner, cur);
                    return cur;

                // ── linear nodes ──────────────────────────────────────────────

                default:
                    Emit(stmt);
                    return cur;
            }
        }

        // ── materialise ───────────────────────────────────────────────────────

        public ControlFlowGraph Build()
        {
            // Flush any remaining pending nodes into the last raw block.
            if (_pending.Count > 0 && _rawBlocks.Count > 0)
            {
                var last = _rawBlocks[^1];
                last.Nodes.AddRange(_pending);
                _rawBlocks[^1] = last;
                _pending.Clear();
            }

            // Materialise CfgBlocks.
            var cfgBlocks = new CfgBlock[_rawBlocks.Count];
            for (var i = 0; i < _rawBlocks.Count; i++)
            {
                var (nodes, _, isEntry, isExit, cond, thenIdx) = _rawBlocks[i];
                cfgBlocks[i] = new CfgBlock(i, $"{_fnName}#{i}", nodes)
                {
                    IsEntry          = isEntry,
                    IsExit           = isExit,
                    BranchCondition  = cond,
                    ThenSuccessorIndex = thenIdx,
                };
            }

            // Wire edges and build predecessor lists.
            for (var i = 0; i < _rawBlocks.Count; i++)
            {
                var (_, succIds, _, _, _, _) = _rawBlocks[i];
                foreach (var succId in succIds)
                {
                    if ((uint)succId < (uint)cfgBlocks.Length)
                    {
                        cfgBlocks[i].Successors.Add(cfgBlocks[succId]);
                        cfgBlocks[succId].Predecessors.Add(cfgBlocks[i]);
                    }
                }
            }

            // Compute RPO via iterative post-order DFS.
            var rpo   = ComputeRpo(cfgBlocks[0]);
            var entry = cfgBlocks[0];

            // Synthetic exit: the last IsExit block, or the last block if none.
            var exit = cfgBlocks.LastOrDefault(b => b.IsExit) ?? cfgBlocks[^1];

            return new ControlFlowGraph(entry, exit, rpo);
        }

        static IReadOnlyList<CfgBlock> ComputeRpo(CfgBlock entry)
        {
            var visited   = new HashSet<CfgBlock>(ReferenceEqualityComparer.Instance);
            var postOrder = new List<CfgBlock>();
            DfsPost(entry, visited, postOrder);
            postOrder.Reverse();
            return postOrder;
        }

        static void DfsPost(CfgBlock b, HashSet<CfgBlock> visited, List<CfgBlock> post)
        {
            if (!visited.Add(b)) return;
            foreach (var s in b.Successors)
                DfsPost(s, visited, post);
            post.Add(b);
        }
    }
}
