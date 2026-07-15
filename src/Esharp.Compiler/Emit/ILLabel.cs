using Mono.Cecil.Cil;

namespace Esharp.Emit;

/// <summary>
/// The kind of a protected-region handler, mirroring CIL's exception-clause
/// flavors. Surfaced from <c>Esharp.Emit</c> so CodeGen classifies handlers
/// without importing <c>Mono.Cecil.Cil</c>.
/// </summary>
public enum ILExceptionRegionKind
{
    Catch,
    Filter,
    Finally,
    Fault,
}

/// <summary>
/// An abstract branch target. Backed by a Cecil <see cref="Instruction"/> marker
/// (a <c>nop</c>) that is appended to the stream when the label is marked, so
/// branches resolve to a stable instruction identity across the optimizer's
/// short-form pass. Created by <see cref="ILBuilder.DefineLabel"/>; placed by
/// <see cref="ILBuilder.MarkLabel"/>.
///
/// <para>The marker is exposed to the builder only. CodeGen holds opaque labels
/// and never touches Cecil — that is the whole point of the abstraction.</para>
/// </summary>
public sealed class ILLabel
{
    /// <summary>The nop marker this label resolves to. Set when the label is defined.</summary>
    internal Instruction Marker { get; }

    /// <summary>Expected stack depth at the label, recorded the first time it is
    /// targeted or marked. Used to assert branch/label stack balance (Roslyn's
    /// "branches to same label with different stacks" invariant).</summary>
    internal int? ExpectedStack { get; set; }

    /// <summary>True once <see cref="ILBuilder.MarkLabel"/> has placed the marker.</summary>
    internal bool IsMarked { get; set; }

    /// <summary>Optional debug name for diagnostics.</summary>
    public string? Name { get; }

    internal ILLabel(Instruction marker, string? name)
    {
        Marker = marker;
        Name = name;
    }
}
