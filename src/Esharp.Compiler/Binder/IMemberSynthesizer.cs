using Esharp.Symbols;

namespace Esharp.Binder;

/// The member-emission seam — the staging hook `derive` (and, later, user comptime
/// code) plugs into. Given the read-only projection of a `data` type, a synthesizer
/// returns members to add INTO that type. The binder invokes it after the promotion
/// sort (Pass 3) and before the type is bound (Pass 4), so the synthesized members
/// flow through interface-satisfaction and the emitters exactly like promoted ones —
/// no special path. Hygienic by construction: the seam hands members to one type and
/// returns members for that type; there is no cross-type write path.
public interface IMemberSynthesizer
{
    IReadOnlyList<BoundFunctionDeclaration> SynthesizeMembers(TypeInfo type);
}
