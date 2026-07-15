using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;

namespace Esharp.Compilation;

// Locates and runs the framework's implicit C# source generators — the ones the
// SDK injects into every net10 compilation: System.Text.Json's
// JsonSerializerContext generator, the Interop LibraryImport/ComInterface/
// JSImport generators, GeneratedRegex. A raw `CSharpCompilation` runs no
// generators, so partial types those generators are meant to complete (e.g. a
// `[JsonSerializable] partial class … : JsonSerializerContext`) stay abstract
// and the compile fails with CS0534. We discover the generators that ship in
// the reference pack (next to the ref assemblies) and drive them before emit.
//
// Generators are stateless and the assemblies are large, so the discovered set
// is loaded once and memoized for the process.
internal static class RoslynGenerators
{
    static IReadOnlyList<ISourceGenerator>? _cached;

    public static IReadOnlyList<ISourceGenerator> Framework()
        => _cached ??= Discover();

    static IReadOnlyList<ISourceGenerator> Discover()
    {
        var dir = AnalyzerDirectory();
        if (dir is null) return [];

        var generators = new List<ISourceGenerator>();
        foreach (var dll in Directory.GetFiles(dir, "*.dll"))
        {
            Assembly asm;
            try { asm = Assembly.LoadFrom(dll); }
            catch { continue; }

            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }

            foreach (var t in types)
            {
                if (t is null || t.IsAbstract || t.GetCustomAttribute<GeneratorAttribute>() is null)
                    continue;
                object? instance;
                try { instance = Activator.CreateInstance(t); }
                catch { continue; }
                switch (instance)
                {
                    case IIncrementalGenerator inc: generators.Add(inc.AsSourceGenerator()); break;
                    case ISourceGenerator src: generators.Add(src); break;
                }
            }
        }
        return generators;
    }

    // <dotnetRoot>/packs/Microsoft.NETCore.App.Ref/<ver>/analyzers/dotnet/cs,
    // derived from the running runtime directory
    // (<dotnetRoot>/shared/Microsoft.NETCore.App/<runtimeVer>).
    static string? AnalyzerDirectory()
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
        var sharedAppDir = Path.GetDirectoryName(runtimeDir);     // .../shared/Microsoft.NETCore.App
        var sharedDir = Path.GetDirectoryName(sharedAppDir);      // .../shared
        var dotnetRoot = Path.GetDirectoryName(sharedDir);        // .../dotnet root
        if (dotnetRoot is null) return null;

        var refPackRoot = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        if (!Directory.Exists(refPackRoot)) return null;

        // Prefer the ref pack matching the runtime version; else the highest.
        var runtimeVer = Path.GetFileName(runtimeDir);
        var exact = Path.Combine(refPackRoot, runtimeVer, "analyzers", "dotnet", "cs");
        if (Directory.Exists(exact)) return exact;

        return Directory.GetDirectories(refPackRoot)
            .OrderByDescending(d => d, StringComparer.Ordinal)
            .Select(d => Path.Combine(d, "analyzers", "dotnet", "cs"))
            .FirstOrDefault(Directory.Exists);
    }
}
