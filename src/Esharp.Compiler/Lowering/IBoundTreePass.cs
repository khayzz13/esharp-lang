using Esharp.BoundTree;
using Esharp.Symbols;

namespace Esharp.Lowering;

/// <summary>
/// Contract for a single ordered lowering pass. Each pass takes a <see cref="BoundProgram"/>
/// carrying FEATURE+CORE nodes and produces a new <see cref="BoundProgram"/> with some FEATURE
/// nodes eliminated. The pass may register synthesized <see cref="TypeSymbol"/>s (display
/// classes, state-machine structs) into the <see cref="SynthesizedSymbolSink"/>.
///
/// Every pass is total (visits every unit unconditionally) and idempotent on already-lowered
/// input (a node that has already been lowered from FEATURE to CORE must not be re-lowered).
///
/// After the pipeline completes, <see cref="LoweringPipeline"/> asserts that no FEATURE node
/// survives. CodeGen then walks the CORE-only tree.
/// </summary>
public interface IBoundTreePass
{
    /// <summary>Applies this pass's rewriting to the full program.</summary>
    BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink);
}
