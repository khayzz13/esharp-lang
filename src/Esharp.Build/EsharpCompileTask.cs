using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Esharp.Diagnostics;
using Esharp.Compilation;

namespace Esharp.Build;

/// <summary>
/// MSBuild task that compiles .es source files to a .NET assembly via the IL compiler.
/// </summary>
public sealed class EsharpCompile : Microsoft.Build.Utilities.Task
{
    [Required]
    public ITaskItem[] Sources { get; set; } = [];

    public ITaskItem[] References { get; set; } = [];

    /// MSBuild `@(FrameworkReference)` — shared frameworks the project pulls in beyond the
    /// base runtime (`Microsoft.AspNetCore.App`, `Microsoft.WindowsDesktop.App`). Each must
    /// be listed in the generated `.runtimeconfig.json` or the host can't locate the
    /// framework's assemblies at startup (`WebApplication.CreateBuilder()` → FileNotFound on
    /// `Microsoft.AspNetCore`).
    public ITaskItem[] FrameworkReferences { get; set; } = [];

    [Required]
    public string OutputAssembly { get; set; } = "";

    public string? AssemblyName { get; set; }

    public bool DebugSymbols { get; set; } = true;

    /// <summary>
    /// E# code-generation policy. Accepted values are Debug and Release; this is
    /// deliberately independent from DebugSymbols so optimized builds may still
    /// produce portable PDBs.
    /// </summary>
    public string OptimizationLevel { get; set; } = "Debug";

    public bool ShowAllocations { get; set; }

    /// <summary>
    /// MSBuild $(OutputType). "Exe"/"WinExe" emit a console application with an
    /// entry point (E# `func main()` or a C# `Main`); anything else (or empty)
    /// emits a library. Default preserves the prior library-only behavior.
    /// </summary>
    public string? OutputType { get; set; }

    /// <summary>
    /// MSBuild $(EnableImplicitUsings) (true by default; set false via
    /// &lt;ImplicitUsings&gt;disable&lt;/ImplicitUsings&gt;). When false, unqualified
    /// type names resolve only from exact/qualified matches and explicit `using`s —
    /// the implicit standard-namespace (BCL) search is switched off.
    /// </summary>
    public bool EnableImplicitUsings { get; set; } = true;

    public override bool Execute()
    {
        try
        {
            return CompileCore();
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: true);
            return false;
        }
    }

    bool CompileCore()
    {
        if (Sources.Length == 0)
        {
            Log.LogWarning("EsharpCompile: no source files specified.");
            return true;
        }

        var asmName = AssemblyName ?? Path.GetFileNameWithoutExtension(OutputAssembly);

        var refPaths = References
            .Select(r => r.GetMetadata("FullPath"))
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Select(MetadataReference.FromFile)
            .ToList();

        var outputKind =
            string.Equals(OutputType, "Exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(OutputType, "WinExe", StringComparison.OrdinalIgnoreCase)
                ? OutputKind.Console
                : OutputKind.Library;

        var optimization = string.Equals(OptimizationLevel, "Release", StringComparison.OrdinalIgnoreCase)
            ? Esharp.Compilation.OptimizationLevel.Release
            : Esharp.Compilation.OptimizationLevel.Debug;
        var workspace = new Workspace(asmName, refPaths, outputKind,
            new ProjectOptions(EnableImplicitUsings: EnableImplicitUsings, Optimization: optimization,
                ShowAllocations: ShowAllocations));
        foreach (var item in Sources)
        {
            var filePath = item.GetMetadata("FullPath");
            if (string.IsNullOrEmpty(filePath))
                filePath = Path.GetFullPath(item.ItemSpec);

            if (!File.Exists(filePath))
            {
                Log.LogError(subcategory: "ESharp", errorCode: "ES0001",
                    helpKeyword: null, file: filePath, lineNumber: 0, columnNumber: 0,
                    endLineNumber: 0, endColumnNumber: 0,
                    message: $"Source file not found: {filePath}");
                continue;
            }

            workspace.AddDocument(filePath, File.ReadAllText(filePath));
        }

        if (Log.HasLoggedErrors || workspace.Documents.Count == 0)
            return !Log.HasLoggedErrors;

        Directory.CreateDirectory(Path.GetDirectoryName(OutputAssembly)!);
        var result = workspace.CurrentCompilation.EmitToFile(OutputAssembly, debugSymbols: DebugSymbols);

        if (ReportDiagnostics(result.Diagnostics))
            return false;

        var configPath = Path.ChangeExtension(OutputAssembly, ".runtimeconfig.json");
        File.WriteAllText(configPath, BuildRuntimeConfig());

        var stdlibSrc = Esharp.Compiler.StdlibProbe.EnsureLoaded()?.Location
            ?? Path.Combine(AppContext.BaseDirectory, "Esharp.Stdlib.dll");
        if (!string.IsNullOrEmpty(stdlibSrc) && File.Exists(stdlibSrc))
        {
            var stdlibDst = Path.Combine(Path.GetDirectoryName(OutputAssembly)!, "Esharp.Stdlib.dll");
            File.Copy(stdlibSrc, stdlibDst, overwrite: true);
        }

        return !Log.HasLoggedErrors;
    }

    // The `.runtimeconfig.json` lists every shared framework the app needs: the base
    // `Microsoft.NETCore.App` plus each `@(FrameworkReference)` (e.g. an ASP.NET app's
    // `Microsoft.AspNetCore.App`). Versions are left at `10.0.0` and rolled forward by the
    // host to whatever patch is installed. A single framework uses the `framework` object
    // form; several use the `frameworks` array form (both accepted by the host).
    string BuildRuntimeConfig()
    {
        var frameworks = new List<string> { "Microsoft.NETCore.App" };
        foreach (var fr in FrameworkReferences)
        {
            var name = fr.ItemSpec;
            if (!string.IsNullOrWhiteSpace(name)
                && !frameworks.Contains(name, StringComparer.OrdinalIgnoreCase))
                frameworks.Add(name);
        }

        static string Fx(string name) => "{\"name\":\"" + name + "\",\"version\":\"10.0.0\"}";

        var inner = frameworks.Count == 1
            ? "\"framework\":" + Fx(frameworks[0])
            : "\"frameworks\":[" + string.Join(",", frameworks.Select(Fx)) + "]";
        return "{\"runtimeOptions\":{\"tfm\":\"net10.0\"," + inner + "}}";
    }

    bool ReportDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        var hasErrors = false;
        foreach (var d in diagnostics)
        {
            if (d.Severity == DiagnosticSeverity.Warning)
            {
                Log.LogWarning(subcategory: "ESharp", warningCode: d.Message.Split(':')[0],
                    helpKeyword: null, file: d.FilePath,
                    lineNumber: d.Line, columnNumber: d.Column,
                    endLineNumber: 0, endColumnNumber: 0,
                    message: d.Message);
            }
            else
            {
                Log.LogError(subcategory: "ESharp", errorCode: "ES0001",
                    helpKeyword: null, file: d.FilePath,
                    lineNumber: d.Line, columnNumber: d.Column,
                    endLineNumber: 0, endColumnNumber: 0,
                    message: d.Message);
                hasErrors = true;
            }
        }
        return hasErrors;
    }
}
