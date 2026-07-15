using System.Collections.Immutable;
using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Symbols;
using Esharp.Syntax;

namespace Esharp.FlowAnalysis;

// ─────────────────────────────────────────────────────────────────────────────
//  Escape Analysis — *T ref-vs-heap realization.
//
//  Determines, per `*T` parameter / local in the function, whether the pointer
//  can be represented as a managed `ref T` (zero-allocation, stack-aliased) or
//  must be heap-allocated as a `__Ptr_T` reference cell.
//
//  A pointer ESCAPES the frame when any of the following hold:
//    - It is stored into a field (BoundAssignment where LHS is a member access).
//    - It is returned from the function.
//    - It is passed as an argument AND the callee parameter is HeapCell (wrapper)
//      (interprocedural constraint).
//    - It is captured by a closure or spawn expression.
//    - It is compared to nil (nil implies nullable → implies a reference cell).
//    - It is initialised to nil.
//    - It is put into an object creation / collection literal (stored in a heap structure).
//
//  When NONE of the above apply, the pointer is emitted as `ref T` — zero alloc,
//  aliasing caller storage.
//
//  INTRA-PROCEDURAL PASS (EscapeAnalysis / EscapePass):
//    A forward one-shot DFA pass.  Calls are handled with a supplied EscapeDecisionMap
//    (name → EscapeDecision) for interprocedural precision.  When no map is supplied,
//    all same-compilation calls default to RefLocal (non-escaping) and BCL calls are
//    always RefLocal-compatible.
//
//  INTERPROCEDURAL FIXPOINT (InterproceduralEscapeOrchestrator):
//    Folds the interprocedural worklist fixpoint from PointerEscapeAnalysis.cs
//    directly into the framework using a private EscapeDecisionMap (separate from
//    the AnnotationStore, which is write-once/idempotent and uses ReferenceEquality
//    for its key map).  The fixpoint:
//      1. Seeds every *T parameter as RefLocal (the lattice bottom).
//      2. Runs EscapeAnalysis.RunIntraProc per function with current callee summaries.
//      3. For each *T arg that flows into a HeapCell callee param, marks the arg
//         as HeapCell in the mutable EscapeDecisionMap and re-enqueues callers.
//      4. Repeats until stable (monotone — decisions only ever rise RefLocal→HeapCell).
//    At convergence, writes all final decisions to the shared AnnotationStore.
//    External / BCL calls are treated as RefLocal-compatible (they take `ref`/`in`).
// ─────────────────────────────────────────────────────────────────────────────

/// The escape decision for one `*T` binding.
public enum EscapeDecision
{
    /// Provably non-escaping — represent as `ref T` (zero-alloc).
    RefLocal,
    /// Escapes the frame — represent as `__Ptr_T` heap cell.
    HeapCell,
}

/// AnnotationStore key for a local's escape decision.
/// NOTE: Because AnnotationStore uses ReferenceEqualityComparer, callers must
/// hold on to the SAME PtrEscapeKey instance to retrieve an annotation they set.
/// The interprocedural orchestrator manages this via EscapeDecisionMap; only
/// final (stable) decisions are written to the AnnotationStore.
public sealed class PtrEscapeKey(LocalSymbol Local)
{
    public LocalSymbol Local { get; } = Local;
}

// ── Internal lattice ────────────────────────────────────────────────────────

public enum EscapeState
{
    NonEscaping = 0,
    Escaping    = 1,
}

sealed class EscapeLattice : ILattice<EscapeState>
{
    public static readonly EscapeLattice Instance = new();
    public EscapeState Bottom => EscapeState.NonEscaping;
    public EscapeState Join(EscapeState a, EscapeState b) =>
        (a == EscapeState.Escaping || b == EscapeState.Escaping)
            ? EscapeState.Escaping : EscapeState.NonEscaping;
    public bool Leq(EscapeState a, EscapeState b) =>
        a == EscapeState.NonEscaping || b == EscapeState.Escaping;
}

/// Per-function escape state: map from pointer-local name to current escape state.
public sealed class EscapeFlowState(ImmutableDictionary<string, EscapeState> map)
{
    public static readonly EscapeFlowState Empty =
        new(ImmutableDictionary<string, EscapeState>.Empty);

    public readonly ImmutableDictionary<string, EscapeState> Map = map;

    public EscapeFlowState MarkEscaping(string name) =>
        Map.TryGetValue(name, out var s) && s == EscapeState.Escaping
            ? this
            : new(Map.SetItem(name, EscapeState.Escaping));

    public EscapeState GetState(string name) =>
        Map.TryGetValue(name, out var s) ? s : EscapeState.NonEscaping;
}

sealed class EscapeFlowLattice : ILattice<EscapeFlowState>
{
    public static readonly EscapeFlowLattice Instance = new();
    public EscapeFlowState Bottom => EscapeFlowState.Empty;

    public EscapeFlowState Join(EscapeFlowState a, EscapeFlowState b)
    {
        var builder = a.Map.ToBuilder();
        foreach (var (name, bState) in b.Map)
        {
            builder[name] = builder.TryGetValue(name, out var aState)
                ? EscapeLattice.Instance.Join(aState, bState)
                : bState;
        }
        return new EscapeFlowState(builder.ToImmutable());
    }

    public bool Leq(EscapeFlowState a, EscapeFlowState b)
    {
        foreach (var (name, aState) in a.Map)
        {
            var bState = b.GetState(name);
            if (!EscapeLattice.Instance.Leq(aState, bState))
                return false;
        }
        return true;
    }
}

// ── Interprocedural callee summaries ─────────────────────────────────────────

/// Escape summary for one callee function: which parameter positions have been
/// decided HeapCell.  Produced by InterproceduralEscapeOrchestrator from the
/// mutable EscapeDecisionMap; supplied to EscapeAnalysis for call-site analysis.
public sealed class EscapeCalleeInfo(IReadOnlyDictionary<int, bool> paramIsHeapCell)
{
    /// Parameter index → true if the parameter is HeapCell (the arg must also be HeapCell).
    public IReadOnlyDictionary<int, bool> ParamIsHeapCell { get; } = paramIsHeapCell;

    /// Conservative: every parameter forces HeapCell.
    public static readonly EscapeCalleeInfo AllHeapCell =
        new(new Dictionary<int, bool>());

    /// Optimistic: no parameter forces HeapCell (used for BCL/external, RefLocal-compatible).
    public static readonly EscapeCalleeInfo AllRefLocal =
        new(new Dictionary<int, bool>());

    public bool ParamEscapes(int idx) =>
        ParamIsHeapCell.TryGetValue(idx, out var v) && v;
}

/// Mutable parallel escape-decision store used by the interprocedural fixpoint.
/// Keyed by LocalSymbol reference (object identity) — avoids the AnnotationStore's
/// ReferenceEqualityComparer + record-key mismatch problem.
/// At fixpoint convergence this is read to write all decisions into the AnnotationStore.
public sealed class EscapeDecisionMap
{
    // Each LocalSymbol maps to a stable PtrEscapeKey instance + current decision.
    // The key is the same object across all iterations so Flush() can write to
    // AnnotationStore without key-identity conflicts.
    readonly Dictionary<LocalSymbol, (PtrEscapeKey Key, EscapeDecision Decision)> _map =
        new(ReferenceEqualityComparer.Instance);

    public void Seed(LocalSymbol local)
    {
        if (!_map.ContainsKey(local))
            _map[local] = (new PtrEscapeKey(local), EscapeDecision.RefLocal);
    }

    /// Monotone raise: RefLocal → HeapCell.  Returns true if the decision changed.
    public bool Raise(LocalSymbol local, EscapeDecision newDecision)
    {
        if (!_map.TryGetValue(local, out var entry))
        {
            _map[local] = (new PtrEscapeKey(local), newDecision);
            return newDecision == EscapeDecision.HeapCell;
        }
        if (entry.Decision == EscapeDecision.HeapCell) return false; // already at top
        if (newDecision == EscapeDecision.HeapCell)
        {
            _map[local] = (entry.Key, EscapeDecision.HeapCell);
            return true;
        }
        return false;
    }

    public EscapeDecision Get(LocalSymbol local) =>
        _map.TryGetValue(local, out var e) ? e.Decision : EscapeDecision.RefLocal;

    /// Write all final decisions to the AnnotationStore using the cached PtrEscapeKey instances.
    public void Flush(AnnotationStore store)
    {
        foreach (var (_, (key, decision)) in _map)
            store.Set(key, decision);
    }

    /// Build a callee-info summary for the given function based on current decisions.
    public EscapeCalleeInfo BuildCalleeInfo(BoundFunctionDeclaration fn,
        IReadOnlyList<LocalSymbol> ptrLocals)
    {
        var paramHeapCell = new Dictionary<int, bool>();
        for (var i = 0; i < fn.Parameters.Count; i++)
        {
            var pName = fn.Parameters[i].Name;
            var local = ptrLocals.FirstOrDefault(l => l.Name == pName);
            if (local is null) continue;
            if (Get(local) == EscapeDecision.HeapCell)
                paramHeapCell[i] = true;
        }
        return new EscapeCalleeInfo(paramHeapCell);
    }
}

// ── Transfer function ─────────────────────────────────────────────────────────

/// Forward DFA transfer function for per-function escape analysis.
/// Interprocedurally precise when a callee-info map is supplied.
public sealed class EscapeAnalysis : IDataFlowAnalysis<EscapeFlowState>
{
    readonly IReadOnlyList<LocalSymbol> _ptrLocals;
    readonly HashSet<string> _ptrNames;
    /// Callee-escape summaries keyed by function name.
    /// Absent entries → BCL/external call → RefLocal-compatible.
    readonly IReadOnlyDictionary<string, EscapeCalleeInfo>? _calleeInfo;
    /// Captured during Finalize; read by InterproceduralEscapeOrchestrator.
    readonly Dictionary<string, EscapeState> _finalState = new(StringComparer.Ordinal);

    public EscapeAnalysis(
        BoundFunctionDeclaration fn,
        IReadOnlyList<LocalSymbol> ptrLocals,
        IReadOnlyDictionary<string, EscapeCalleeInfo>? calleeInfo = null)
    {
        _ = fn; // used only for documentation; body accessed via CfgBuilder in EscapePass.Run
        _ptrLocals  = ptrLocals;
        _ptrNames   = new HashSet<string>(ptrLocals.Select(l => l.Name), StringComparer.Ordinal);
        _calleeInfo = calleeInfo;
    }

    /// After DataFlowEngine.Run returns, the escape decision for each tracked local.
    public IReadOnlyDictionary<string, EscapeState> FinalState => _finalState;

    public ILattice<EscapeFlowState> Lattice => EscapeFlowLattice.Instance;
    public bool IsForward => true;

    public EscapeFlowState Transfer(CfgBlock block, EscapeFlowState input, AnalysisContext ctx)
    {
        var state = input;
        foreach (var node in block.Nodes)
            state = VisitNode(node, state);
        return state;
    }

    EscapeFlowState VisitNode(BoundNode node, EscapeFlowState state) => node switch
    {
        // Declaration with nil initialiser / heap-allocation on the RHS.
        BoundVariableDeclaration { Name: var name, Initializer: { } init }
            when _ptrNames.Contains(name)
            => IsHeapRhs(init) ? state.MarkEscaping(name) : state,

        // Assignment to a pointer local.
        BoundAssignment { Target: BoundNameExpression { Name: var dst }, Value: { } rhs }
            when _ptrNames.Contains(dst)
            => IsHeapRhs(rhs) ? state.MarkEscaping(dst) : state,

        // Storing a pointer into a field — the RHS pointer escapes.
        BoundAssignment
        {
            Target: BoundMemberAccessExpression,
            Value : BoundNameExpression { Name: var stored }
        } when _ptrNames.Contains(stored) => state.MarkEscaping(stored),

        // Returning a pointer escapes it.
        BoundReturnStatement { Expression: BoundNameExpression { Name: var ret } }
            when _ptrNames.Contains(ret) => state.MarkEscaping(ret),

        // Nil comparisons: x == nil / x != nil forces a nullable reference cell.
        BoundExpressionStatement { Expression: BoundBinaryExpression bin }
            when IsNilComparison(bin) => MarkNilComparisonOperands(bin, state),

        BoundBinaryExpression bin when IsNilComparison(bin)
            => MarkNilComparisonOperands(bin, state),

        // Calls — interprocedurally precise.
        BoundExpressionStatement { Expression: BoundCallExpression call }
            => VisitCallArgs(call, state),

        BoundCallExpression call => VisitCallArgs(call, state),

        _ => state,
    };

    EscapeFlowState MarkNilComparisonOperands(BoundBinaryExpression bin, EscapeFlowState state)
    {
        if (bin.Left  is BoundNameExpression { Name: var nl } && _ptrNames.Contains(nl))
            state = state.MarkEscaping(nl);
        if (bin.Right is BoundNameExpression { Name: var nr } && _ptrNames.Contains(nr))
            state = state.MarkEscaping(nr);
        return state;
    }

    EscapeFlowState VisitCallArgs(BoundCallExpression call, EscapeFlowState state)
    {
        string? calleeName = call.Target switch
        {
            BoundNameExpression n => n.Name,
            BoundMemberAccessExpression { MemberName: var m } => m,
            _ => null,
        };

        // If the callee is registered in the map, use its summary.
        // If not (BCL/external), treat as RefLocal-compatible — they take `ref`/`in`.
        EscapeCalleeInfo info = EscapeCalleeInfo.AllRefLocal;
        if (calleeName is not null && _calleeInfo is not null)
            _calleeInfo.TryGetValue(calleeName, out info!);
        info ??= EscapeCalleeInfo.AllRefLocal;

        for (var i = 0; i < call.Arguments.Count; i++)
        {
            var arg = call.Arguments[i];

            // Direct pointer-name argument — check interprocedural constraint.
            if (arg is BoundNameExpression { Name: var n } && _ptrNames.Contains(n))
            {
                if (info.ParamEscapes(i))
                    state = state.MarkEscaping(n);
            }

            // Closure / spawn: captures leave the frame — always HeapCell.
            if (arg is BoundFunctionLiteralExpression fl)
                foreach (var cap in fl.CapturedVariables)
                    if (_ptrNames.Contains(cap.Name))
                        state = state.MarkEscaping(cap.Name);

            if (arg is BoundSpawnExpression sp)
                foreach (var cap in sp.CapturedVariables)
                    if (_ptrNames.Contains(cap.Name))
                        state = state.MarkEscaping(cap.Name);
        }
        return state;
    }

    public void Finalize(
        IReadOnlyDictionary<CfgBlock, EscapeFlowState> finalIn,
        IReadOnlyDictionary<CfgBlock, EscapeFlowState> finalOut,
        AnalysisContext ctx)
    {
        var exitStates = finalOut
            .Where(kv => kv.Key.IsExit)
            .Select(kv => kv.Value)
            .ToList();
        if (exitStates.Count == 0)
            exitStates = [.. finalOut.Values];

        foreach (var local in _ptrLocals)
        {
            var joined = EscapeState.NonEscaping;
            foreach (var es in exitStates)
                joined = EscapeLattice.Instance.Join(joined, es.GetState(local.Name));

            _finalState[local.Name] = joined;

            // For the intra-procedural path (non-interprocedural), write directly.
            // The interprocedural orchestrator uses a separate EscapeDecisionMap and
            // passes a throw-away AnnotationStore so these writes don't conflict.
            ctx.Annotations.Set(new PtrEscapeKey(local),
                joined == EscapeState.Escaping ? EscapeDecision.HeapCell : EscapeDecision.RefLocal);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    static bool IsHeapRhs(BoundExpression expr) =>
        IsNilLiteral(expr) ||
        expr is BoundObjectCreationExpression or BoundListLiteralExpression;

    static bool IsNilLiteral(BoundExpression expr) =>
        expr is BoundLiteralExpression { Value: null } || expr.Type is NullType;

    static bool IsNilComparison(BoundBinaryExpression bin) =>
        (bin.Op == SyntaxTokenKind.EqualsEquals || bin.Op == SyntaxTokenKind.BangEquals)
        && (IsNilLiteral(bin.Left) || IsNilLiteral(bin.Right));
}

// ── Single-function entry point ───────────────────────────────────────────────

/// Entry point for single-function escape analysis.  Used by FlowAnalysisOrchestrator
/// for the intra-procedural pass (before InterproceduralEscapeOrchestrator refines).
public static class EscapePass
{
    /// Run escape analysis for one function, writing decisions to <paramref name="annotations"/>.
    /// Returns the analysis object so the caller can read FinalState for the interprocedural pass.
    public static EscapeAnalysis Run(
        BoundFunctionDeclaration fn,
        IReadOnlyList<LocalSymbol> ptrLocals,
        DiagnosticBag diagnostics,
        AnnotationStore annotations,
        IReadOnlyDictionary<string, EscapeCalleeInfo>? calleeInfo = null)
    {
        var analysis = new EscapeAnalysis(fn, ptrLocals, calleeInfo);
        if (ptrLocals.Count == 0) return analysis;

        var cfg = CfgBuilder.Build(fn, fn.Name);
        var ctx = new AnalysisContext(diagnostics, annotations, fn.Name);
        DataFlowEngine.Run(cfg, analysis, ctx);
        return analysis;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Interprocedural Escape Orchestrator.
//
//  Folds the interprocedural worklist fixpoint from PointerEscapeAnalysis.cs
//  directly into the framework.
//
//  Design decisions:
//
//  1. Uses EscapeDecisionMap (not AnnotationStore) for mutable fixpoint state.
//     AnnotationStore uses ReferenceEqualityComparer for its internal dictionary,
//     so PtrEscapeKey record instances created across iterations would be treated
//     as distinct keys and conflict on write — making AnnotationStore unusable as
//     a mutable fixpoint store.  EscapeDecisionMap keys by LocalSymbol reference
//     (the same object across all iterations) and holds a stable PtrEscapeKey.
//
//  2. At convergence, Flush() writes all final decisions to the shared AnnotationStore
//     using the stable PtrEscapeKey instances.
//
//  3. The worklist is monotone: only a RefLocal→HeapCell raise can re-enqueue callers.
//     Convergence is guaranteed: the lattice has height 1 per local (RefLocal ≤ HeapCell)
//     so at most N raises occur (N = total *T locals in the compilation).
// ─────────────────────────────────────────────────────────────────────────────
public static class InterproceduralEscapeOrchestrator
{
    /// Run the interprocedural fixpoint over all compilation units.
    ///
    /// <param name="units">All BoundCompilationUnits produced by the binder.</param>
    /// <param name="ptrLocalsByFunction">
    ///   *T locals per function from the binder's local-symbol table.
    ///   Functions absent from the map fall back to parameter-only tracking.
    /// </param>
    /// <param name="diagnostics">Diagnostic accumulator.</param>
    /// <param name="annotations">
    ///   Shared AnnotationStore.  This method writes ALL final escape decisions at
    ///   convergence.  Call this INSTEAD OF per-function EscapePass.Run for escape
    ///   (FlowAnalysisOrchestrator calls this at the end of its escape pass).
    /// </param>
    public static void Run(
        IReadOnlyList<BoundCompilationUnit> units,
        IReadOnlyDictionary<BoundFunctionDeclaration, IReadOnlyList<LocalSymbol>>? ptrLocalsByFunction,
        DiagnosticBag diagnostics,
        AnnotationStore annotations)
    {
        var allFns = FlattenFunctions(units).ToList();
        if (allFns.Count == 0) return;

        // Per-function ptr-locals (stable across iterations).
        var ptrLocalsMap = new Dictionary<BoundFunctionDeclaration, IReadOnlyList<LocalSymbol>>(
            ReferenceEqualityComparer.Instance);
        foreach (var fn in allFns)
            ptrLocalsMap[fn] = GetPtrLocals(fn, ptrLocalsByFunction);

        // Seed all locals at RefLocal (lattice bottom).
        var decisions = new EscapeDecisionMap();
        foreach (var fn in allFns)
            foreach (var local in ptrLocalsMap[fn])
                decisions.Seed(local);

        // Initial callee-info map: all params at RefLocal (no param forces HeapCell yet).
        var calleeInfoMap = BuildCalleeInfoMap(allFns, ptrLocalsMap, decisions);

        // Reverse call graph: callee name → list of BoundFunctionDeclarations that call it.
        var callGraph = BuildCallGraph(allFns);

        // Monotone worklist.
        var worklist = new Queue<BoundFunctionDeclaration>(allFns);
        var inQueue  = new HashSet<BoundFunctionDeclaration>(allFns, ReferenceEqualityComparer.Instance);

        while (worklist.Count > 0)
        {
            var fn   = worklist.Dequeue();
            inQueue.Remove(fn);

            var ptrs = ptrLocalsMap[fn];
            if (ptrs.Count == 0) continue;

            // Throw-away AnnotationStore per iteration — we manage decisions in EscapeDecisionMap.
            var iterAnnotations = new AnnotationStore();
            var analysis = EscapePass.Run(fn, ptrs, diagnostics, iterAnnotations, calleeInfoMap);

            bool anyRaised = false;
            foreach (var local in ptrs)
            {
                var intraResult = analysis.FinalState.TryGetValue(local.Name, out var fs)
                    ? fs : EscapeState.NonEscaping;
                var newDecision = intraResult == EscapeState.Escaping
                    ? EscapeDecision.HeapCell
                    : EscapeDecision.RefLocal;

                if (decisions.Raise(local, newDecision))
                    anyRaised = true;
            }

            if (!anyRaised) continue;

            // Rebuild callee-info for this function — its param decisions may have risen.
            calleeInfoMap[fn.Name] = decisions.BuildCalleeInfo(fn, ptrs);

            // Re-enqueue all callers of this function.
            if (callGraph.TryGetValue(fn.Name, out var callers))
            {
                foreach (var caller in callers)
                {
                    if (!inQueue.Contains(caller))
                    {
                        worklist.Enqueue(caller);
                        inQueue.Add(caller);
                    }
                }
            }
        }

        // Write stable decisions to the shared AnnotationStore.
        decisions.Flush(annotations);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    static Dictionary<string, EscapeCalleeInfo> BuildCalleeInfoMap(
        IReadOnlyList<BoundFunctionDeclaration> allFns,
        Dictionary<BoundFunctionDeclaration, IReadOnlyList<LocalSymbol>> ptrLocalsMap,
        EscapeDecisionMap decisions)
    {
        var map = new Dictionary<string, EscapeCalleeInfo>(StringComparer.Ordinal);
        foreach (var fn in allFns)
            map[fn.Name] = decisions.BuildCalleeInfo(fn, ptrLocalsMap[fn]);
        return map;
    }

    /// Reverse call graph: callee name → list of BoundFunctionDeclarations that call it.
    static Dictionary<string, List<BoundFunctionDeclaration>> BuildCallGraph(
        IReadOnlyList<BoundFunctionDeclaration> allFns)
    {
        var graph = new Dictionary<string, List<BoundFunctionDeclaration>>(StringComparer.Ordinal);
        foreach (var fn in allFns)
        {
            var calleeNames = new HashSet<string>(StringComparer.Ordinal);
            CollectCalleeNames(fn.Body, calleeNames);
            foreach (var callee in calleeNames)
            {
                if (!graph.TryGetValue(callee, out var callers))
                    graph[callee] = callers = [];
                callers.Add(fn);
            }
        }
        return graph;
    }

    static void CollectCalleeNames(BoundStatement stmt, HashSet<string> names)
    {
        switch (stmt)
        {
            case BoundBlockStatement blk:
                foreach (var s in blk.Statements) CollectCalleeNames(s, names);
                break;
            case BoundExpressionStatement { Expression: var e }:
                CollectNamesInExpr(e, names);
                break;
            case BoundReturnStatement { Expression: { } e }:
                CollectNamesInExpr(e, names);
                break;
            case BoundIfStatement ifStmt:
                CollectNamesInExpr(ifStmt.Condition, names);
                CollectCalleeNames(ifStmt.Then, names);
                if (ifStmt.Else is not null) CollectCalleeNames(ifStmt.Else, names);
                break;
            case BoundWhileStatement wh:
                CollectNamesInExpr(wh.Condition, names);
                CollectCalleeNames(wh.Body, names);
                break;
            case BoundVariableDeclaration { Initializer: { } init }:
                CollectNamesInExpr(init, names);
                break;
            case BoundAssignment { Value: { } v }:
                CollectNamesInExpr(v, names);
                break;
        }
    }

    static void CollectNamesInExpr(BoundExpression expr, HashSet<string> names)
    {
        switch (expr)
        {
            case BoundCallExpression call:
                if (call.Target is BoundNameExpression { Name: var n1 }) names.Add(n1);
                if (call.Target is BoundMemberAccessExpression { MemberName: var m }) names.Add(m);
                foreach (var a in call.Arguments) CollectNamesInExpr(a, names);
                break;
            case BoundBinaryExpression bin:
                CollectNamesInExpr(bin.Left, names);
                CollectNamesInExpr(bin.Right, names);
                break;
            case BoundUnaryExpression un:
                CollectNamesInExpr(un.Operand, names);
                break;
            case BoundMemberAccessExpression ma:
                CollectNamesInExpr(ma.Target, names);
                break;
            case BoundConversion conv:
                CollectNamesInExpr(conv.Operand, names);
                break;
        }
    }

    static IEnumerable<BoundFunctionDeclaration> FlattenFunctions(
        IEnumerable<BoundCompilationUnit> units)
    {
        foreach (var unit in units)
            foreach (var member in unit.Members)
                foreach (var fn in FlattenMember(member))
                    yield return fn;
    }

    static IEnumerable<BoundFunctionDeclaration> FlattenMember(BoundMember member)
    {
        switch (member)
        {
            case BoundFunctionDeclaration fn:
                yield return fn;
                break;
            case BoundDataDeclaration data:
                foreach (var m in data.InstanceMethods) yield return m;
                break;
            case BoundStaticFuncDeclaration sf:
                foreach (var fn in sf.Functions) yield return fn;
                break;
        }
    }

    static IReadOnlyList<LocalSymbol> GetPtrLocals(
        BoundFunctionDeclaration fn,
        IReadOnlyDictionary<BoundFunctionDeclaration, IReadOnlyList<LocalSymbol>>? ptrLocals)
    {
        if (ptrLocals is not null && ptrLocals.TryGetValue(fn, out var locals))
            return locals;

        // Fallback: synthesise from parameters only.
        var result = new List<LocalSymbol>();
        foreach (var p in fn.Parameters)
        {
            if (p.Type is HeapPointerBoundType or ByRefBoundType)
            {
                result.Add(new LocalSymbol
                {
                    Name        = p.Name,
                    IsParameter = true,
                    Mutable     = !p.ReadOnlyByRef,
                    Type        = p.Type,
                });
            }
        }
        return result;
    }
}
