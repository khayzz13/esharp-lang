using Mono.Cecil;
using Esharp.BoundTree;
using Esharp.Diagnostics;

namespace Esharp.CodeGen;

// Nested type declarations — `class Outer { struct Inner { ... } }`, including
// nested enums/choices/interfaces/delegates and a `static func` host nesting types.
//
// Strategy (the rest of the pipeline stays untouched): a nested type's bound
// declaration is hoisted to a top-level bound member by the binder, carrying the
// enclosing type's metadata key in `DeclaringTypeKey`. The emitter builds its shell
// and members exactly like a top-level type — so sibling references (`Arm` ->
// `Kind`, `Select(Arm[])`) resolve by name against the module type table. THEN this
// pass moves each such TypeDefinition out of `module.Types` and into the enclosing
// type's `NestedTypes`, converting its visibility to the nested form and clearing
// its namespace (a nested type carries none). Cecil writes the member tokens from
// the TypeDefinition's final state, so references stay valid across the move.
public static partial class CodeGenerator
{
    /// Async lowering first declares its generated state machine as a top-level shell so
    /// the normal type and method-resolution passes can refer to it. Once every body has
    /// been emitted, make it the private nested implementation detail of the method's
    /// actual CLR owner. This is the same two-phase shape used for source nested types
    /// below, and keeps the AsyncStateMachineAttribute's type token and the metadata
    /// hierarchy in agreement for free functions, static-func members, and instance methods.
    static void ReparentAsyncStateMachines(ModuleDefinition module,
        IEnumerable<(BoundFunctionDeclaration Function, MethodDefinition Method)> methods,
        DiagnosticBag diagnostics)
    {
        foreach (var (function, method) in methods)
        {
            if (function.AsyncStateMachineType is null) continue;

            var owner = method.DeclaringType;
            // The attribute was attached while the correct enclosing generic context
            // was active. Reuse that exact TypeReference here; resolving the bound
            // type again after contexts are popped would turn its `T` into object.
            var stateMachine = method.CustomAttributes
                .FirstOrDefault(a => a.AttributeType.FullName == typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute).FullName)
                ?.ConstructorArguments.FirstOrDefault().Value as TypeReference;
            var stateMachineDef = stateMachine?.Resolve();
            if (owner is null || stateMachineDef is null)
            {
                diagnostics.Report("", 0, 0,
                    $"IL: async state-machine shell for '{method.FullName}' was not available for nesting");
                continue;
            }
            if (stateMachineDef.DeclaringType is not null) continue;
            if (!module.Types.Remove(stateMachineDef))
            {
                diagnostics.Report("", 0, 0,
                    $"IL: async state-machine shell '{stateMachineDef.FullName}' was not a top-level module type");
                continue;
            }

            stateMachineDef.Attributes &= ~TypeAttributes.VisibilityMask;
            stateMachineDef.Attributes |= TypeAttributes.NestedPrivate;
            stateMachineDef.Namespace = "";
            owner.NestedTypes.Add(stateMachineDef);
        }
    }

    /// Re-parent every hoisted nested type into its enclosing type. Builds a
    /// metadata-name -> TypeDefinition index over the module's top-level types (the
    /// child and its parent are both there before this pass), then for each bound
    /// member carrying a DeclaringTypeKey moves the child into the parent.
    static void ReparentNestedTypes(ModuleDefinition module, IReadOnlyList<BoundMember> allMembers, DiagnosticBag diagnostics)
    {
        // Collect the (childKey, parentKey) edges first so the lookup index is built
        // once and is stable while we mutate module.Types.
        var edges = new List<(string childKey, string parentKey, string display, Esharp.Syntax.Visibility vis)>();
        foreach (var m in allMembers)
        {
            var info = NestedInfoOf(m);
            if (info is { } e) edges.Add(e);
        }
        if (edges.Count == 0) return;

        // metadata Name -> TypeDefinition. A type's emitted Name is its metadata
        // simple name (`Inner`, `Pair`1`, the static-func host `ChanSelect`), which is
        // exactly the key the binder records in DeclaringTypeKey. Nested-type simple
        // names are assumed unique within the module (the practical contract).
        var byName = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var t in module.Types)
            byName[t.Name] = t;

        foreach (var (childKey, parentKey, display, vis) in edges)
        {
            if (!byName.TryGetValue(childKey, out var child))
            {
                diagnostics.Report("", 0, 0, $"IL: nested type '{display}' shell not found for re-parenting");
                continue;
            }
            if (!byName.TryGetValue(parentKey, out var parent))
            {
                diagnostics.Report("", 0, 0, $"IL: enclosing type '{parentKey}' not found for nested type '{display}'");
                continue;
            }
            if (child == parent) continue;

            module.Types.Remove(child);
            // A nested type's visibility uses the Nested* flags, never Public/NotPublic
            // (the CLR rejects a top-level visibility on a nested type). The declared
            // E# visibility maps directly — `pub` -> NestedPublic, `internal` ->
            // NestedAssembly, `priv`/default -> NestedPrivate (the C# nested default).
            child.Attributes &= ~TypeAttributes.VisibilityMask;
            child.Attributes |= vis switch
            {
                Esharp.Syntax.Visibility.Public => TypeAttributes.NestedPublic,
                Esharp.Syntax.Visibility.Internal => TypeAttributes.NestedAssembly,
                _ => TypeAttributes.NestedPrivate,
            };
            child.Namespace = "";
            parent.NestedTypes.Add(child); // sets child.DeclaringType
        }
    }

    /// The (childKey, parentKey, display, visibility) for a bound member that is a
    /// nested type, or null when it is top-level. childKey is the member's own metadata
    /// name; parentKey is its DeclaringTypeKey; visibility drives the nested flag.
    static (string childKey, string parentKey, string display, Esharp.Syntax.Visibility vis)? NestedInfoOf(BoundMember m) => m switch
    {
        BoundDataDeclaration d when d.DeclaringTypeKey is { } p
            => (Esharp.Binder.Binder.NestedParentKey(d.Name, d.TypeParameters.Count), p, d.Name, d.Visibility),
        BoundEnumDeclaration e when e.DeclaringTypeKey is { } p
            => (e.Name, p, e.Name, e.Visibility),
        BoundChoiceDeclaration c when c.DeclaringTypeKey is { } p
            => (Esharp.Binder.Binder.NestedParentKey(c.Name, c.TypeParameters.Count), p, c.Name, c.Visibility),
        BoundInterfaceDeclaration i when i.DeclaringTypeKey is { } p
            => (Esharp.Binder.Binder.NestedParentKey(i.Name, i.TypeParameters.Count), p, i.Name, i.Visibility),
        BoundDelegateDeclaration dg when dg.DeclaringTypeKey is { } p
            => (dg.Name, p, dg.Name, dg.Visibility),
        BoundStaticFuncDeclaration s when s.DeclaringTypeKey is { } p
            => (s.Name, p, s.Name, s.Visibility),
        _ => null,
    };
}
