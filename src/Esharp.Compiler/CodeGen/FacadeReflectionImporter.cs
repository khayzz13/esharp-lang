using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace Esharp.CodeGen;

/// Scopes every reflection-imported BCL type to the canonical REFERENCE (facade)
/// assembly that publicly exposes it, at import time.
///
/// The emitter imports BCL types by reflection (`module.ImportReference(typeof(T))`).
/// Reflected over the running runtime, each corelib type's assembly is the merged
/// IMPLEMENTATION assembly `System.Private.CoreLib` — invisible to a C# consumer, which
/// compiles against the split reference assemblies (`System.Object` in `System.Runtime`,
/// `List<T>` in `System.Collections`, `Stream` in `System.IO`, ...). A metadata reference
/// to the impl corelib is then CS0012 on the C# side.
///
/// Cecil resolves the scope of every reflection-imported type — directly, and for the
/// declaring / return / parameter types reached through a method or field import — through
/// the single seam `ImportScope(Type)`. Overriding it redirects each impl-corelib type to
/// its facade as the reference is born, so no post-emit metadata rewrite is needed and the
/// emitted assembly is a first-class C# reference. Runtime binding is unaffected: each
/// facade type-forwards back to `System.Private.CoreLib`.
sealed class FacadeReflectionImporter : DefaultReflectionImporter
{
    readonly ModuleDefinition _module;
    readonly IReadOnlyDictionary<string, AssemblyNameReference> _home;
    readonly Dictionary<string, AssemblyNameReference> _interned = new(System.StringComparer.Ordinal);

    public FacadeReflectionImporter(ModuleDefinition module, IReadOnlyDictionary<string, AssemblyNameReference> home)
        : base(module)
    {
        _module = module;
        _home = home;
    }

    protected override IMetadataScope ImportScope(System.Type type)
    {
        // Reduce to the type whose assembly identity is being scoped: an array / by-ref /
        // pointer scopes as its element, a nested type as its outermost declarer, and a
        // constructed generic as its definition (whose FullName carries the `\`n` arity
        // that matches the facade map key).
        var t = type;
        if (t.HasElementType && t.GetElementType() is { } el) t = el;
        while (t.IsNested && t.DeclaringType is { } dt) t = dt;
        if (t.IsGenericType && !t.IsGenericTypeDefinition) t = t.GetGenericTypeDefinition();

        var implName = t.Assembly.GetName().Name;
        if (implName is not null && FacadeMap.ImplCorelibs.Contains(implName)
            && t.FullName is { } key && _home.TryGetValue(key, out var facade) && facade.Name != implName)
            return Intern(facade);

        return base.ImportScope(type);
    }

    AssemblyNameReference Intern(AssemblyNameReference canonical)
    {
        if (_interned.TryGetValue(canonical.Name, out var cached)) return cached;
        var existing = _module.AssemblyReferences.FirstOrDefault(a => a.Name == canonical.Name);
        if (existing is not null) { _interned[canonical.Name] = existing; return existing; }
        var clone = new AssemblyNameReference(canonical.Name, canonical.Version)
        {
            PublicKeyToken = canonical.PublicKeyToken,
            Culture = canonical.Culture,
        };
        _module.AssemblyReferences.Add(clone);
        _interned[canonical.Name] = clone;
        return clone;
    }
}

/// Installs a <see cref="FacadeReflectionImporter"/> on the module at creation, so every
/// import from the first is scoped to a reference facade.
sealed class FacadeReflectionImporterProvider : IReflectionImporterProvider
{
    readonly IReadOnlyDictionary<string, AssemblyNameReference> _home;

    public FacadeReflectionImporterProvider(IReadOnlyList<string>? referencePaths)
        => _home = FacadeMap.Build(referencePaths);

    public IReflectionImporter GetReflectionImporter(ModuleDefinition module)
        => new FacadeReflectionImporter(module, _home);
}

/// Maps every public BCL type's full name to the canonical facade assembly that exposes it.
static class FacadeMap
{
    /// Runtime implementation / legacy corelib identities a C# consumer cannot reference.
    public static readonly HashSet<string> ImplCorelibs = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "System.Private.CoreLib",
        "mscorlib",
        "netstandard",
    };

    /// Sourced from the RUNNING runtime's shared-framework directory — its facade assemblies
    /// (`System.Runtime.dll`, `System.Collections.dll`, ...) type-forward each contract type to
    /// `System.Private.CoreLib`, so their forwarder tables name exactly the assembly a C#
    /// consumer references. This needs no resolved `@(ReferencePath)`, which the .NET SDK leaves
    /// empty for a `.esproj` (it misclassifies the extension as a JavaScript project and skips
    /// managed reference resolution). Any explicit `referencePaths` are merged in as well. The
    /// impl corelibs are excluded as sources so a type is never mapped back onto them.
    ///
    /// Two passes so a genuine definition wins over a mere type-forward: all defined public
    /// types first, then forwarders fill the gaps.
    public static Dictionary<string, AssemblyNameReference> Build(IReadOnlyList<string>? referencePaths)
    {
        var map = new Dictionary<string, AssemblyNameReference>(System.StringComparer.Ordinal);
        var loaded = new List<(AssemblyDefinition Asm, AssemblyNameReference Name)>();

        var sources = new List<string>();
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        if (Directory.Exists(runtimeDir))
            sources.AddRange(Directory.EnumerateFiles(runtimeDir, "*.dll"));
        if (referencePaths is not null) sources.AddRange(referencePaths);

        foreach (var path in sources)
        {
            if (!File.Exists(path)) continue;
            if (ImplCorelibs.Contains(Path.GetFileNameWithoutExtension(path))) continue;
            AssemblyDefinition asm;
            try
            {
                asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadingMode = ReadingMode.Deferred });
            }
            catch (System.Exception e) when (e is BadImageFormatException or IOException or System.UnauthorizedAccessException)
            {
                continue; // a native/resource DLL on the path — skip it
            }
            var n = asm.Name;
            loaded.Add((asm, new AssemblyNameReference(n.Name, n.Version)
            {
                PublicKeyToken = n.PublicKeyToken,
                Culture = n.Culture,
            }));
        }

        foreach (var (asm, name) in loaded)
            foreach (var t in asm.MainModule.Types)
                if (t.IsPublic)
                    map.TryAdd(t.FullName, name);

        foreach (var (asm, name) in loaded)
            foreach (var et in asm.MainModule.ExportedTypes)
                map.TryAdd(et.FullName, name);

        foreach (var (asm, _) in loaded) asm.Dispose();
        return map;
    }
}
