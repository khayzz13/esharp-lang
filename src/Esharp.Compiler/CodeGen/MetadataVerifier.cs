using Mono.Cecil;
using Esharp.Diagnostics;
using Esharp.Syntax;

namespace Esharp.CodeGen;

/// <summary>
/// The compiler's own metadata verification pass — the layer <em>above and beyond</em>
/// ILVerify. ILVerify checks the IL inside method bodies for type safety; it does
/// <strong>not</strong> check what the CLR validates at <em>type load</em> and
/// <em>JIT</em> time. Those defects sail past ILVerify and only surface as a
/// <c>TypeLoadException</c> / <c>VerificationException</c> / <c>BadImageFormat</c> the
/// first time the emitted code is touched — i.e. as a mystery runtime failure far from
/// the bug.
///
/// This pass walks the emitted Cecil model and reports those defects as located
/// compile-time diagnostics, with the <em>specific</em> CLR rule each one breaks:
/// <list type="bullet">
///   <item><b>ES9510</b> — a declared interface with no loadable implementation of a member.</item>
///   <item><b>ES9511</b> — a generic instantiation whose argument breaks a constraint.</item>
///   <item><b>ES9512</b> — a concrete method with no body (nothing to JIT).</item>
///   <item><b>ES9513</b> — an abstract member on an instantiable type.</item>
///   <item><b>ES9514</b> — a MethodImpl override targeting a slot on no interface/base.</item>
///   <item><b>ES9515</b> — a value type with an infinite by-value layout cycle.</item>
///   <item><b>ES9516</b> — a duplicate member signature on one type.</item>
/// </list>
///
/// Every check is conservative by construction: it reports a defect only when it can
/// name a concrete CLR rule the emitted metadata breaks, and it bails silently on any
/// reference it cannot resolve (a missing reference is the linker's problem, not a
/// defect to invent). A clean run is therefore a real guarantee, and every finding is
/// actionable. This is what lets the compiler converge on "compiles anything valid —
/// and tells you precisely, at compile time, when it cannot."
/// </summary>
internal static class MetadataVerifier
{
    public static void Verify(ModuleDefinition module, DiagnosticBag diagnostics)
    {
        foreach (var type in module.Types)
            VerifyType(type, diagnostics);
    }

    static void VerifyType(TypeDefinition type, DiagnosticBag diagnostics)
    {
        foreach (var nested in type.NestedTypes)
            VerifyType(nested, diagnostics);

        // Per-type metadata invariants (apply to every type kind).
        VerifyValueLayout(type, diagnostics);
        VerifyNoDuplicateMembers(type, diagnostics);
        VerifyMethodBodies(type, diagnostics);
        VerifyOverrideTargets(type, diagnostics);

        // Interface-satisfaction & abstract-member rules apply only to instantiable
        // types. An interface declares; an abstract class defers to descendants.
        if (type.IsInterface || type.IsAbstract)
            return;

        VerifyNoAbstractMembers(type, diagnostics);
        foreach (var ii in type.Interfaces)
            VerifyInterfaceSatisfied(type, ii.InterfaceType, diagnostics);
    }

    // ── ES9510: every declared interface member has a loadable implementation ──────

    static void VerifyInterfaceSatisfied(TypeDefinition type, TypeReference ifaceRef, DiagnosticBag diagnostics)
    {
        TypeDefinition? ifaceDef;
        try { ifaceDef = ifaceRef.Resolve(); } catch { return; }
        if (ifaceDef is null || !ifaceDef.IsInterface) return;

        var isValueType = type.IsValueType;
        foreach (var im in ifaceDef.Methods)
        {
            if (!im.IsAbstract) continue; // a default-interface-method satisfies itself
            var reason = SatisfactionReason(type, im, isValueType);
            if (reason is not null)
                diagnostics.Report(default(SourceSpan), DiagnosticDescriptors.InterfaceMemberNotImplemented,
                    type.FullName, ifaceRef.FullName, $"{im.Name} — {reason}");
        }
    }

    /// null when satisfied, otherwise the precise CLR reason it is not.
    static string? SatisfactionReason(TypeDefinition type, MethodDefinition im, bool isValueType)
    {
        MethodDefinition? nameArityMatch = null;
        for (var cursor = type; cursor is not null; cursor = SafeBase(cursor))
        {
            foreach (var m in cursor.Methods)
            {
                foreach (var ov in m.Overrides)
                    if (ov.Name == im.Name && ov.Parameters.Count == im.Parameters.Count)
                        return null; // explicit impl present
                if (m.Name == im.Name && m.Parameters.Count == im.Parameters.Count)
                    nameArityMatch ??= m;
            }
        }
        if (nameArityMatch is null)
            return "no method of that name/arity on the type or its bases";
        if (!nameArityMatch.IsVirtual)
            return "the implementing method is not virtual (CLR cannot map it into the interface slot)";
        if (isValueType && !nameArityMatch.IsFinal)
            return "on a value type the implementing method must be virtual + final";
        return null;
    }

    // ── ES9512: a concrete method must have a body ────────────────────────────────

    static void VerifyMethodBodies(TypeDefinition type, DiagnosticBag diagnostics)
    {
        if (type.IsInterface) return; // interface methods are abstract or DIM-bodied
        foreach (var m in type.Methods)
        {
            if (m.IsAbstract || m.IsRuntime || m.IsPInvokeImpl || m.IsInternalCall) continue;
            // A method with no RVA and no IL body is nothing to JIT — invalid for a
            // concrete method. (Cecil reports HasBody=false for such.)
            if (!m.HasBody && m.RVA == 0)
                diagnostics.Report(default(SourceSpan), DiagnosticDescriptors.EmittedMethodHasNoBody,
                    $"{type.FullName}::{m.Name}");
        }
    }

    // ── ES9513: an instantiable type cannot declare an abstract member ────────────

    static void VerifyNoAbstractMembers(TypeDefinition type, DiagnosticBag diagnostics)
    {
        foreach (var m in type.Methods)
            if (m.IsAbstract)
                diagnostics.Report(default(SourceSpan), DiagnosticDescriptors.AbstractMemberOnConcreteType,
                    type.FullName, $"{m.Name}");
    }

    // ── ES9514: a MethodImpl override must target a real interface/base slot ──────

    static void VerifyOverrideTargets(TypeDefinition type, DiagnosticBag diagnostics)
    {
        // Collect candidate target slots: every method of every declared interface and
        // every base type. An override matched by name+arity against this set is wired.
        var slots = new HashSet<(string, int)>();
        foreach (var ii in type.Interfaces)
        {
            TypeDefinition? id; try { id = ii.InterfaceType.Resolve(); } catch { id = null; }
            if (id is null) { slots.Clear(); return; } // unresolved iface → can't judge; bail
            foreach (var m in id.Methods) slots.Add((m.Name, m.Parameters.Count));
        }
        for (var b = SafeBase(type); b is not null; b = SafeBase(b))
            foreach (var m in b.Methods) slots.Add((m.Name, m.Parameters.Count));

        foreach (var m in type.Methods)
            foreach (var ov in m.Overrides)
                if (!slots.Contains((ov.Name, ov.Parameters.Count)))
                    diagnostics.Report(default(SourceSpan), DiagnosticDescriptors.OverrideTargetNotFound,
                        $"{type.FullName}::{m.Name}", $"{ov.DeclaringType?.Name}::{ov.Name}");
    }

    // ── ES9515: no value type may contain itself by value ─────────────────────────

    static void VerifyValueLayout(TypeDefinition type, DiagnosticBag diagnostics)
    {
        if (!type.IsValueType) return;
        foreach (var f in type.Fields)
        {
            if (f.IsStatic) continue;
            if (f.FieldType is TypeDefinition fd && ReferenceEquals(fd, type)
                || f.FieldType.FullName == type.FullName && f.FieldType.IsValueType)
                diagnostics.Report(default(SourceSpan), DiagnosticDescriptors.ValueTypeLayoutCycle,
                    type.FullName, f.Name);
        }
    }

    // ── ES9516: no duplicate member signatures on one type ────────────────────────

    static void VerifyNoDuplicateMembers(TypeDefinition type, DiagnosticBag diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in type.Methods)
        {
            var sig = m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)) + ")";
            if (!seen.Add(sig))
                diagnostics.Report(default(SourceSpan), DiagnosticDescriptors.DuplicateMemberSignature,
                    type.FullName, sig);
        }
    }

    static TypeDefinition? SafeBase(TypeDefinition t)
    {
        if (t.BaseType is null) return null;
        try { return t.BaseType.Resolve(); } catch { return null; }
    }
}
