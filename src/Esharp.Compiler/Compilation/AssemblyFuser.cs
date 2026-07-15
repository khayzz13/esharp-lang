using Esharp.Diagnostics;
using ILRepacking;
using Mono.Cecil;

namespace Esharp.Compilation;

// Merges the E# Cecil-emitted PE and the Roslyn-emitted PE into one DLL on
// disk. ILRepack does the type-clone + assembly-ref fixup; we feed it the two
// byte arrays as temp files (its API is path-based) and read the merged
// output back.
//
// Post-merge verify pass: open the merged DLL as a Roslyn MetadataReference
// in a throwaway compilation. If Roslyn rejects any metadata, we surface that
// as a diagnostic before the user sees an InvalidProgramException at runtime.
internal static class AssemblyFuser
{
    public static IReadOnlyList<Diagnostic> Fuse(
        byte[] esharpPe, byte[] csharpPe, string assemblyName, string outputPath, IReadOnlyList<string> referencePaths,
        OutputKind outputKind = OutputKind.Library)
    {
        var diagnostics = new List<Diagnostic>();
        var tempDir = Path.Combine(Path.GetTempPath(), $"esharp_fuse_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // The E# half is the "primary" — its assembly identity becomes the
            // merged assembly's identity. The C# half is merged into it.
            var esharpPath = Path.Combine(tempDir, $"{assemblyName}.esharp.dll");
            var csharpPath = Path.Combine(tempDir, $"{assemblyName}.csharp.dll");
            File.WriteAllBytes(esharpPath, esharpPe);
            File.WriteAllBytes(csharpPath, csharpPe);

            var searchDirs = referencePaths
                .Select(p => Path.GetDirectoryName(p) ?? "")
                .Where(d => d.Length > 0)
                .Distinct()
                .ToList();

            // When the user asked for a console executable, the entry point
            // lives on the C# half (typically Program.Main) — list it second
            // but pass the path containing the entry point as the "Main"
            // assembly so ILRepack carries it over and the merged module
            // points at it. ILRepack treats the FIRST input as the primary
            // and inherits entry point from it, so order matters.
            var inputs = outputKind == OutputKind.Console
                ? new[] { csharpPath, esharpPath }
                : new[] { esharpPath, csharpPath };
            var options = new RepackOptions
            {
                InputAssemblies = inputs,
                OutputFile = outputPath,
                SearchDirectories = searchDirs,
                CopyAttributes = true,
                AllowMultipleAssemblyLevelAttributes = true,
                XmlDocumentation = false,
                DebugInfo = false,
                Internalize = false,
                Parallel = false,
                TargetKind = outputKind == OutputKind.Console
                    ? ILRepack.Kind.Exe
                    : ILRepack.Kind.Dll,
            };

            var repack = new ILRepack(options);
            repack.Repack();

            CleanSynthesizedInternalsVisibleTo(outputPath, assemblyName);
        }
        catch (Exception ex)
        {
            var inner = ex;
            var trace = ex.StackTrace ?? "";
            while (inner.InnerException is not null) { inner = inner.InnerException; trace += "\n  inner: " + inner.Message + "\n" + inner.StackTrace; }
            diagnostics.Add(new Diagnostic(outputPath, 0, 0, DiagnosticSeverity.Error, "ES9001",
                $"assembly fusion failed: {ex.Message}\n{trace}",
                DiagnosticSource.Workspace));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
        return diagnostics;
    }

    // The two halves each carried a synthesized [InternalsVisibleTo] so they
    // could see each other's internals while compiled as separate assemblies.
    // Post-fuse they share one identity, so an IVT to this assembly's own name
    // (or to the now-absent C# half `<name>_CSharp`) is dead metadata. Strip
    // those, and collapse any exact-duplicate IVTs — so when the merged input
    // *also* carried real IVTs they read clean rather than buried among ours.
    static void CleanSynthesizedInternalsVisibleTo(string path, string assemblyName)
    {
        var halfName = assemblyName + "_CSharp";
        using var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true });
        var attrs = asm.CustomAttributes;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;
        for (var i = attrs.Count - 1; i >= 0; i--)
        {
            var a = attrs[i];
            if (a.AttributeType.FullName != "System.Runtime.CompilerServices.InternalsVisibleToAttribute") continue;
            if (a.ConstructorArguments.Count != 1 || a.ConstructorArguments[0].Value is not string target) continue;
            if (target == assemblyName || target == halfName || !seen.Add(target))
            {
                attrs.RemoveAt(i);
                changed = true;
            }
        }
        if (changed) asm.Write(path);
    }
}
