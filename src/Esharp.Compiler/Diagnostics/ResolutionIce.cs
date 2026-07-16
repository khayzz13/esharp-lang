// ============================================================================
// ResolutionIce — the masked-resolution ICE guard.
//
// The root pattern: some resolution site (a name in the binder, a member in
// codegen, a type reference, …) fails to find an entity and is about to report an
// ordinary "not found" error — but an entity of the SAME KIND and NAME is in fact
// known to the symbol table. That is never a user error: the name is not undefined,
// a scope/context bug at the site hid a symbol that exists. Reporting it as "undefined
// X" is a red herring that sends the author hunting for a typo.
//
// This is the single choke-point that catches that pattern regardless of the symptom.
// A resolution site calls RaiseIfKnown just before its own not-found report; if the
// entity is known, an ES9501 ICE fires (naming the kind, the name, and the phase) and
// the site skips its misleading message. If genuinely unknown, RaiseIfKnown is a no-op
// and the ordinary error stands.
//
// It is deliberately independent of any specific fix — new resolution sites wire it in
// so a future masked bug self-identifies as an ICE at its root, not as a typo error.
// ============================================================================

using Esharp.Symbols;

namespace Esharp.Diagnostics;

/// The kind of entity a resolution site was looking for. Used both to phrase the ICE
/// and to pick which symbol table the existence probe consults.
public enum ResolvableKind
{
    Type,
    Method,
    Property,
    Field,
    Local,
    Namespace,
    Constant,
    Member,
}

public static class ResolutionIce
{
    /// Just before a resolution site reports its own "not found" diagnostic, call this:
    /// if a <paramref name="kind"/> named <paramref name="name"/> is known to the symbol
    /// table, the failure is a masked compiler bug — report ES9501 (naming the kind, the
    /// name, and the <paramref name="phase"/> where it fired) and return true so the
    /// caller skips its ordinary message. Returns false (a no-op) when genuinely unknown,
    /// leaving the real user-facing error to the caller.
    public static bool RaiseIfKnown(DiagnosticBag diagnostics, SymbolTable symbols,
        ResolvableKind kind, string name, string phase, SourceSpan span)
    {
        if (!symbols.IsNameKnown(kind, name)) return false;
        diagnostics.Report(span, DiagnosticDescriptors.MaskedResolutionIce,
            kind.ToString().ToLowerInvariant(), name, phase);
        return true;
    }

    /// A resolution site whose failing entity could legitimately be one of several kinds
    /// (a bare value name — a function, a constant, a type-as-value, …) probes them all;
    /// the first known kind fires the ICE.
    public static bool RaiseIfKnown(DiagnosticBag diagnostics, SymbolTable symbols,
        string name, string phase, SourceSpan span, params ResolvableKind[] kinds)
    {
        foreach (var kind in kinds)
            if (RaiseIfKnown(diagnostics, symbols, kind, name, phase, span))
                return true;
        return false;
    }
}
