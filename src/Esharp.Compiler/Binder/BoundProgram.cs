using Esharp.BoundTree;
using Esharp.Binder;
using Esharp.Diagnostics;

namespace Esharp.BoundTree;

/// <summary>
/// The result of binding (and optionally lowering) a whole compilation: the bound
/// compilation units plus the shared <see cref="CompilationData"/> they were bound into.
///
/// <para>
/// A backend (IL emitter or any other consumer) is a leaf reader of this — it reads
/// <see cref="Units"/> and never re-runs the bind sequence.
/// </para>
/// <para>
/// Post-lowering: the same record type carries the CORE-only tree after the lowering
/// pipeline runs. The distinction is behavioural, not structural —
/// <see cref="HasErrors"/> gates emission in both cases.
/// </para>
/// <para>
/// Architecture note: this type intentionally lives in <c>Esharp.BoundTree</c>
/// rather than <c>Esharp.Compilation</c> so that <c>Esharp.Lowering</c> — which cannot
/// reference <c>Esharp.Compilation</c> without creating a circular dependency — can still
/// use BoundProgram as the I/O type of the <c>IBoundTreePass</c> contract.
/// The definition previously in <c>Esharp.Compilation/BoundProgram.cs</c> should be
/// removed in favour of this canonical location.
/// </para>
/// </summary>
public sealed record BoundProgram(
    IReadOnlyList<BoundCompilationUnit> Units,
    CompilationData Data)
{
    public IReadOnlyList<Diagnostic> Diagnostics => Data.Diagnostics.Diagnostics;

    public bool HasErrors =>
        Data.Diagnostics.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}
