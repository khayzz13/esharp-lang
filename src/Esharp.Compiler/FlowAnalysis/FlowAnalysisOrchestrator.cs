using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Symbols;

namespace Esharp.FlowAnalysis;

// ─────────────────────────────────────────────────────────────────────────────
//  FlowAnalysisOrchestrator — B3's single entry point for the pipeline.
//
//  Called by the compilation pipeline (Pillar 3, Esharp.Compilation) after
//  the binder produces a BoundProgram and before Pillar 3 lowering begins.
//
//  Pass order (all analyses share the same AnnotationStore so they can
//  observe each other's results):
//
//    1. MatchExhaustivenessPass (whole-program, structural)
//       Runs first because:
//         a) ReachabilityAnalysis reads exhaustiveness verdicts to determine
//            whether a match counts as a definite terminator.
//         b) It is a tree walk (no CFG needed) and always completes cleanly.
//
//    2. Per-function CFG-based passes (for every BoundFunctionDeclaration):
//       a. NullStatePass      — null-state lattice and ES2173 smart-cast checks.
//       b. DefiniteAssignment — ES2301 use-before-assign, ES2302 out-params.
//       c. ReachabilityPass   — ES2140 definite-return, ES2141 dead code.
//
//    3. InterproceduralEscapeOrchestrator (whole-program, *T ref-vs-heap):
//       Monotone worklist over the whole-compilation call graph.  Writes final
//       EscapeDecision per LocalSymbol once the fixpoint converges.  Supersedes
//       per-function EscapePass — strictly more precise (not all-args-escape).
//
//  Output: the AnnotationStore is returned to the pipeline; lowering passes
//  read it for:
//    - EscapeDecision per LocalSymbol   (via PtrEscapeKey)
//    - ExhaustivenessVerdict per match  (via MatchStatementKey / MatchExpressionKey)
//    - NullNarrowKey annotations        (for BoundConversion elision in lowering)
// ─────────────────────────────────────────────────────────────────────────────

/// The result of a flow-analysis run over one BoundProgram.
public sealed class FlowAnalysisResult
{
    /// All diagnostics emitted by the flow-analysis passes.
    public DiagnosticBag Diagnostics { get; }

    /// Annotations produced by the passes (escape decisions, exhaustiveness
    /// verdicts, null-state narrowings).  Passed to Pillar 3 lowering.
    public AnnotationStore Annotations { get; }

    public FlowAnalysisResult(DiagnosticBag diagnostics, AnnotationStore annotations)
    {
        Diagnostics  = diagnostics;
        Annotations  = annotations;
    }
}

public static class FlowAnalysisOrchestrator
{
    /// Run all flow-analysis passes over the bound compilation units.
    ///
    /// <param name="units">
    ///   All BoundCompilationUnits produced by the binder (RegisterTypes →
    ///   RegisterSignatures → BindUnit complete).
    /// </param>
    /// <param name="ptrLocalsByFunction">
    ///   Map from function (reference identity) to its *T local symbols —
    ///   supplied by the binder's local-symbol table.  Functions absent from
    ///   the map are treated as having no pointer locals (EscapePass skips them).
    /// </param>
    public static FlowAnalysisResult Run(
        IReadOnlyList<BoundCompilationUnit> units,
        IReadOnlyDictionary<BoundFunctionDeclaration, IReadOnlyList<LocalSymbol>>? ptrLocalsByFunction = null)
    {
        var diagnostics = new DiagnosticBag();
        var annotations = new AnnotationStore();

        // ── Pass 1: Match Exhaustiveness (whole-program, structural) ──────────

        MatchExhaustivenessPass.Run(units, diagnostics, annotations);

        // ── Pass 2: Per-function CFG-based passes ─────────────────────────────
        //    Null-state, definite assignment, reachability.
        //    Escape is handled separately in Pass 3 (interprocedural fixpoint).

        foreach (var unit in units)
            RunPerFunction(unit, diagnostics, annotations, ptrLocalsByFunction);

        // ── Pass 3: Interprocedural Escape Fixpoint ───────────────────────────
        //    Supersedes the per-function EscapePass.Run from Pass 2.
        //    Runs a monotone worklist over the whole-program call graph to decide
        //    *T ref-vs-heap precisely (not all-args-escape).  Writes final
        //    EscapeDecision annotations to the shared AnnotationStore via Flush().

        InterproceduralEscapeOrchestrator.Run(units, ptrLocalsByFunction, diagnostics, annotations);

        return new FlowAnalysisResult(diagnostics, annotations);
    }

    static void RunPerFunction(
        BoundCompilationUnit unit,
        DiagnosticBag diagnostics,
        AnnotationStore annotations,
        IReadOnlyDictionary<BoundFunctionDeclaration, IReadOnlyList<LocalSymbol>>? ptrLocals)
    {
        foreach (var member in unit.Members)
            RunMember(member, diagnostics, annotations, ptrLocals);
    }

    static void RunMember(
        BoundMember member,
        DiagnosticBag diagnostics,
        AnnotationStore annotations,
        IReadOnlyDictionary<BoundFunctionDeclaration, IReadOnlyList<LocalSymbol>>? ptrLocals)
    {
        switch (member)
        {
            case BoundFunctionDeclaration fn when fn.Body is not null:
                RunFunction(fn, diagnostics, annotations, ptrLocals);
                break;

            case BoundDataDeclaration data:
                foreach (var m in data.InstanceMethods)
                    if (m.Body is not null) RunFunction(m, diagnostics, annotations, ptrLocals);
                break;

            case BoundStaticFuncDeclaration sf:
                foreach (var fn in sf.Functions)
                    if (fn.Body is not null) RunFunction(fn, diagnostics, annotations, ptrLocals);
                break;
        }
    }

    static void RunFunction(
        BoundFunctionDeclaration fn,
        DiagnosticBag diagnostics,
        AnnotationStore annotations,
        IReadOnlyDictionary<BoundFunctionDeclaration, IReadOnlyList<LocalSymbol>>? ptrLocals)
    {
        // 2a. Null-state flow (smart-cast / ES2173).
        NullStatePass.Run(fn, diagnostics, annotations);

        // 2b. Definite assignment (ES2301 / ES2302).
        DefiniteAssignmentPass.Run(fn, diagnostics, annotations);

        // 2c. Reachability (ES2140 definite-return, ES2141 dead code).
        bool nonVoid = fn.ReturnType is not VoidType;
        ReachabilityPass.Run(fn, nonVoid, diagnostics, annotations);

        // NOTE: Escape analysis (2d) is intentionally omitted here.
        // InterproceduralEscapeOrchestrator (Pass 3) handles all escape decisions
        // with interprocedural precision — the per-function EscapePass.Run would
        // only produce intra-procedural (conservative) results and would conflict
        // with the stable PtrEscapeKey instances written by the orchestrator.
        _ = ptrLocals; // consumed by Pass 3 only
    }

}
