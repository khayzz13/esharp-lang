using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Syntax;
using Esharp.Lowering;
using Esharp.FlowAnalysis;
// CompilationData lives in the bind+flow+lower assembly. The binder class is referenced
// fully-qualified (Esharp.Binder.Binder) because the bare name `Binder` binds the sibling
// `Esharp.Binder` namespace here, which an alias of the same name can't override.
using CompilationData = Esharp.Binder.CompilationData;

namespace Esharp.Compilation;

/// The single bind + lower orchestrator. The pipeline drives the full
/// bind → flow → lower → (assert-CORE) → codegen sequence. Both compilation
/// front doors — the IL driver (<see cref="Compilation"/>) and the SemanticModel
/// path — run the identical RegisterTypes → RegisterSignatures → BindUnit
/// sequence over a set of already-parsed units. This is that sequence, owned in
/// one place, now extended with the lowering stage.
///
/// <para>
/// Parsing stays with the caller on purpose: mixed-language compilation must
/// interleave a Roslyn declare-only pass between parse and bind — it interns the
/// C# half's types into the shared SymbolTable before the E# binder runs — so the
/// pipeline takes units already parsed and a CompilationData the caller may have
/// pre-seeded with cross-language type handles.
/// </para>
///
/// <para>
/// <strong>[Δ] Bind ONCE.</strong> The old Compilation double-bind (one for
/// diagnostics, one for the SemanticModel) is eliminated: this pipeline produces
/// a single <see cref="BoundProgram"/> whose <see cref="CompilationData.Sink"/>
/// is populated during the single bind. The SemanticModel is built from the
/// same bound program's accumulated sink data.
/// </para>
///
/// <para>
/// <strong>[Δ] Lowering stage.</strong> <see cref="BindAndLower"/> drives the
/// ordered <see cref="LoweringPipeline"/> after bind completes, producing a
/// CORE-only <see cref="BoundProgram"/> for codegen. The <see cref="Bind"/>
/// method (no lowering) is still available for the SemanticModel path, which
/// needs FEATURE nodes intact to answer structural queries.
/// </para>
public sealed class CompilationPipeline
{
    readonly CompilationData _data;
    Esharp.Binder.Binder? _binder;

    public CompilationPipeline(CompilationData data) => _data = data;

    public CompilationData Data => _data;

    /// Post-bind bound-type → runtime-Type resolution, through the bind's own
    /// TypeResolver (so external names resolve with the same import tiers the bind
    /// used). The SemanticModel's external-expansion hook. Null before Bind runs.
    public Type? ResolveRuntime(BoundType type) => _binder?.Types.ResolveBoundTypeToRuntime(type);

    /// Bind only — the SemanticModel path. Returns a BoundProgram with FEATURE +
    /// CORE nodes intact. The Sink on the CompilationData is populated as the bind
    /// runs (the single bind — no second pass).
    ///
    /// <para>
    /// The bind is TOTAL: a registration-phase error does not skip the bind phase
    /// — broken declarations yield partial symbols, broken expressions bind to error
    /// nodes, and the rest of the program still binds. Diagnostics gate emission;
    /// they never gate binding, so tooling can query a broken program.
    /// </para>
    public BoundProgram Bind(IReadOnlyList<CompilationUnitSyntax> units)
    {
        var binder = new Esharp.Binder.Binder(_data);
        _binder = binder;
        foreach (var unit in units)
            binder.RegisterTypes(unit);
        foreach (var unit in units)
            binder.RegisterSignatures(unit);
        foreach (var unit in units)
            binder.RegisterNamespaceStates(unit);

        var bound = new List<BoundCompilationUnit>(units.Count);
        foreach (var unit in units)
            bound.Add(binder.BindUnit(unit));

        // `*T` realization is a compilation decision, not a per-file binding
        // detail.  Every callee is now visible before the fixed point chooses its
        // `ref T` fast path or durable `__Ptr_T` carrier, so callers in sibling
        // source files cannot manufacture a copied wrapper for an escaping callee.
        var realized = PointerEscapeAnalysis.Run(bound, _data.Diagnostics);
        return new BoundProgram(realized, _data);
    }

    /// Bind AND lower — the codegen path. Runs bind (via <see cref="Bind"/>)
    /// then drives the ordered <see cref="LoweringPipeline"/> to produce a
    /// CORE-only <see cref="BoundProgram"/> with every FEATURE node eliminated.
    ///
    /// <para>
    /// Synthesized symbols minted during lowering (display classes, state
    /// machines, union layout types) are registered into the shared
    /// <see cref="CompilationData.Symbols"/> table via the
    /// <see cref="SynthesizedSymbolSink"/> so codegen resolves them exactly like
    /// declared types.
    /// </para>
    public BoundProgram BindAndLower(IReadOnlyList<CompilationUnitSyntax> units)
    {
        var bound = Bind(units);
        if (bound.HasErrors) return bound; // don't lower a broken program
        var sink = new SynthesizedSymbolSink(_data.Symbols);
        return new LoweringPipeline(sink).Run(bound);
    }
}
