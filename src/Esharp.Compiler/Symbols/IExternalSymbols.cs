using System.Reflection;

namespace Esharp.Symbols;

/// External (compiled-assembly) symbol resolver: the bridge from runtime/metadata
/// type handles to interned E# symbols. Implemented by <c>Esharp.Metadata.ExternalSymbols</c>
/// (the System.Reflection.Metadata–backed reader).
///
/// Lives in the symbols layer so low-level consumers — <c>CompilationData</c> in the
/// binder and the <c>SemanticModel</c> binder-tap in Esharp.Diagnostics — depend on this
/// contract rather than taking a hard reference on Esharp.Metadata.
public interface IExternalSymbols
{
    /// Intern (and optionally populate the members of) the symbol for a runtime <see cref="Type"/>.
    TypeSymbol ForType(Type type, bool populateMembers = false);

    /// Populate the members of an already-interned external type symbol on demand.
    void EnsureMembers(TypeSymbol sym);

    /// Intern the symbol for an external method.
    MethodSymbol Method(MethodInfo method);

    /// Intern the symbol for an external field/member.
    FieldSymbol Field(MemberInfo member);
}
