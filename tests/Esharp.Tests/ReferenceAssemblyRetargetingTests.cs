using System.Linq;
using Mono.Cecil;

namespace Esharp.Tests;

/// The emitter imports BCL types by reflection, which scopes them to the runtime
/// implementation assembly `System.Private.CoreLib`. A C# consumer compiles against the
/// split reference assemblies, so consuming such an assembly fails with CS0012 unless the
/// references are scoped to the canonical facades (`System.Object` → `System.Runtime`,
/// `List<T>` → `System.Collections`, ...). FacadeReflectionImporter does that at import
/// time (Cecil's `ImportScope` seam); these tests pin it, so an E# assembly stays a
/// first-class C# reference.
public sealed class ReferenceAssemblyRetargetingTests
{
    // A public surface exercising a spread of BCL types across facade assemblies:
    // Object (base), String, Action<T>, Stream, List<T>, Dictionary<,>, Exception.
    const string Src = """
namespace Test
using "System.IO"
using "System.Collections.Generic"

pub class Widget {
    pub var names: List<string> { priv set }
    pub var tags: Dictionary<string, int> { priv set }
    init() {
        self.names = List<string>()
        self.tags = Dictionary<string, int>()
    }
    pub func run(s: Stream, act: Action<int>) -> string {
        act(self.names.Count)
        return "ok"
    }
}

pub class WidgetError : Exception {
    init(message: string) : base(message) {}
}

func go() -> int = 0
""";

    static ModuleDefinition Emit()
    {
        var path = EsHarness.CompileToPath(Src, "RetargetProbe");
        return ModuleDefinition.ReadModule(path);
    }

    // No BCL type in the emitted metadata is left scoped to an implementation corelib —
    // System.Private.CoreLib (reflection import) or mscorlib / netstandard (the module's
    // TypeSystem corlib). A C# consumer references none of these.
    [Theory]
    [InlineData("System.Private.CoreLib")]
    [InlineData("mscorlib")]
    [InlineData("netstandard")]
    public void NoTypeReference_ScopedToImplementationCorelib(string implCorelib)
    {
        using var module = Emit();
        var leaked = module.GetTypeReferences()
            .Where(tr => tr.Scope is AssemblyNameReference a && a.Name == implCorelib)
            .Select(tr => tr.FullName)
            .ToList();
        Assert.True(leaked.Count == 0, $"type refs still on {implCorelib}: " + string.Join(", ", leaked));
    }

    // The canonical facades are referenced instead — proof the redirect ran, not that the
    // types simply vanished.
    [Fact]
    public void CanonicalFacadeAssemblies_AreReferenced()
    {
        using var module = Emit();
        var refs = module.AssemblyReferences.Select(a => a.Name).ToHashSet();
        Assert.Contains("System.Runtime", refs);
    }

    // Spot-check the well-known types resolve to a real, non-impl facade each. Only types
    // that emit a TypeRef row are listed: `System.String` is not — as a method return type
    // it encodes as the ELEMENT_TYPE_STRING signature primitive, never a TypeRef (the same
    // reason `Object` appears only via `Widget`'s implicit base-type token, not its use as a
    // return/parameter type).
    [Theory]
    [InlineData("System.Object")]
    [InlineData("System.Action`1")]
    [InlineData("System.IO.Stream")]
    [InlineData("System.Collections.Generic.List`1")]
    [InlineData("System.Collections.Generic.Dictionary`2")]
    [InlineData("System.Exception")]
    public void KnownBclType_ScopesToAFacade_NotImplCorelib(string fullName)
    {
        using var module = Emit();
        var tr = module.GetTypeReferences().FirstOrDefault(t => t.FullName == fullName);
        Assert.NotNull(tr);
        var scope = Assert.IsAssignableFrom<AssemblyNameReference>(tr!.Scope);
        Assert.NotEqual("System.Private.CoreLib", scope.Name);
        Assert.StartsWith("System.", scope.Name);
    }
}
