using Esharp.Compiler;
using Esharp.Diagnostics;
using Esharp.Compilation;
using System.Diagnostics;
using System.Text.Json;

if (args.Length < 1)
{
    PrintUsage();
    return 1;
}

var command = args[0].ToLowerInvariant();

switch (command)
{
    case "compile-il" when args.Length >= 3:
        return await CompileIL(args[1], args[2], args.Skip(3).ToArray());
    default:
        PrintUsage();
        return 1;
}

static async Task<int> CompileIL(string input, string output, string[] optionArgs)
{
    var inputPath = Path.GetFullPath(input);
    var outputPath = Path.GetFullPath(output);
    var referencePaths = await ResolveReferencePaths(optionArgs);
    if (referencePaths is null)
        return 1;

    List<string> sourceFiles;
    if (Directory.Exists(inputPath))
    {
        // Recurse: a real project is a tree of nested directories, and partial
        // classes split across files mean every fragment must be discovered or
        // the compile breaks. Skip build output (bin/obj) so a stray build of
        // the source tree never leaks compiled artifacts into the input set.
        static bool IsBuildOutput(string path)
        {
            foreach (var seg in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                if (seg is "bin" or "obj") return true;
            return false;
        }
        sourceFiles = Directory.GetFiles(inputPath, "*.es", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(inputPath, "*.cs", SearchOption.AllDirectories))
            .Where(p => !IsBuildOutput(Path.GetRelativePath(inputPath, p)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (sourceFiles.Count == 0)
        {
            Console.Error.WriteLine($"No .es or .cs files found in {inputPath}");
            return 1;
        }
    }
    else if (File.Exists(inputPath))
    {
        sourceFiles = [inputPath];
    }
    else
    {
        Console.Error.WriteLine($"Input not found: {inputPath}");
        return 1;
    }

    var outputKind = optionArgs.Contains("--exe")
        ? OutputKind.Console
        : OutputKind.Library;
    var workspace = new Workspace(
        assemblyName: Path.GetFileNameWithoutExtension(outputPath),
        references: referencePaths.Select(MetadataReference.FromFile),
        outputKind: outputKind);
    foreach (var srcFile in sourceFiles)
        workspace.AddDocument(srcFile, await File.ReadAllTextAsync(srcFile));

    EmitResult result;
    try
    {
        result = workspace.CurrentCompilation.EmitToFile(outputPath, debugSymbols: true);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"IL emission failed: {ex.Message}");
        Console.Error.WriteLine(ex.StackTrace);
        if (ex.InnerException is not null)
            Console.Error.WriteLine($"  → {ex.InnerException.Message}");
        return 1;
    }

    foreach (var d in result.Diagnostics) Console.Error.WriteLine(d.ToString());
    if (!result.Success) return 1;

    // IL verification is a standing contract of the compiler: any method whose IL
    // the JIT would reject (the InvalidProgramException class) is a hard error here.
    // Suppress with --no-verify for diagnostic/bootstrapping builds.
    if (!optionArgs.Contains("--no-verify"))
    {
        var findings = Esharp.CodeGen.IlVerification.VerifyFatal(outputPath);
        foreach (var f in findings)
            Console.Error.WriteLine($"error ES0900: emitted IL failed verification at {f.Method}: {f.CodeName} — {f.Message}");
        if (findings.Count > 0) return 1;
    }

    var configPath = Path.ChangeExtension(outputPath, ".runtimeconfig.json");
    File.WriteAllText(configPath, """{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""");

    var stdlibSrc = Esharp.Compiler.StdlibProbe.EnsureLoaded()?.Location
        ?? Path.Combine(AppContext.BaseDirectory, "Esharp.Stdlib.dll");
    if (!string.IsNullOrEmpty(stdlibSrc) && File.Exists(stdlibSrc))
    {
        var stdlibDst = Path.Combine(Path.GetDirectoryName(outputPath)!, "Esharp.Stdlib.dll");
        File.Copy(stdlibSrc, stdlibDst, overwrite: true);
    }

    Console.WriteLine($"Compiled {outputPath}");
    return 0;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  esharpc compile-il <input.es | dir> <output.dll> [--ref <assembly.dll> ...] [--refs-from-esproj <project.esproj>]");
}

static async Task<IReadOnlyList<string>?> ResolveReferencePaths(string[] optionArgs)
{
    var refs = new HashSet<string>(StringComparer.Ordinal);

    for (var i = 0; i < optionArgs.Length; i++)
    {
        switch (optionArgs[i])
        {
            case "--ref":
                if (i + 1 >= optionArgs.Length)
                {
                    Console.Error.WriteLine("Missing path after --ref.");
                    return null;
                }
                refs.Add(Path.GetFullPath(optionArgs[++i]));
                break;
            case "--refs-from-esproj":
                if (i + 1 >= optionArgs.Length)
                {
                    Console.Error.WriteLine("Missing project path after --refs-from-esproj.");
                    return null;
                }
                var projectRefs = await ResolveReferencesFromEsproj(Path.GetFullPath(optionArgs[++i]));
                if (projectRefs is null)
                    return null;
                foreach (var r in projectRefs)
                    refs.Add(r);
                break;
            case "--exe":
            case "--no-verify":
                // Consumed by the caller (output-kind / verification flags); no payload.
                break;
            default:
                Console.Error.WriteLine($"Unknown compile-il option: {optionArgs[i]}");
                return null;
        }
    }

    return refs.ToList();
}

static async Task<IReadOnlyList<string>?> ResolveReferencesFromEsproj(string projectPath)
{
    if (!File.Exists(projectPath))
    {
        Console.Error.WriteLine($"Project file not found: {projectPath}");
        return null;
    }

    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add("msbuild");
    psi.ArgumentList.Add(projectPath);
    psi.ArgumentList.Add("-nologo");
    psi.ArgumentList.Add("-t:ResolveReferences");
    psi.ArgumentList.Add("-getItem:ReferencePath");

    using var process = Process.Start(psi);
    if (process is null)
    {
        Console.Error.WriteLine("Failed to start dotnet msbuild.");
        return null;
    }

    var stdout = await process.StandardOutput.ReadToEndAsync();
    var stderr = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    if (process.ExitCode != 0)
    {
        if (!string.IsNullOrWhiteSpace(stderr))
            Console.Error.WriteLine(stderr.Trim());
        Console.Error.WriteLine($"dotnet msbuild failed for {projectPath}");
        return null;
    }

    try
    {
        using var doc = JsonDocument.Parse(stdout);
        if (!doc.RootElement.TryGetProperty("Items", out var items) ||
            !items.TryGetProperty("ReferencePath", out var refsEl) ||
            refsEl.ValueKind != JsonValueKind.Array)
            return [];

        var refs = new List<string>();
        foreach (var entry in refsEl.EnumerateArray())
        {
            string? path = null;
            if (entry.ValueKind == JsonValueKind.String)
                path = entry.GetString();
            else if (entry.ValueKind == JsonValueKind.Object)
            {
                if (entry.TryGetProperty("FullPath", out var fullPath))
                    path = fullPath.GetString();
                else if (entry.TryGetProperty("Identity", out var identity))
                    path = identity.GetString();
            }

            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                refs.Add(path);
        }

        return refs.Distinct(StringComparer.Ordinal).ToList();
    }
    catch (JsonException)
    {
        Console.Error.WriteLine("Failed to parse ReferencePath output from dotnet msbuild.");
        return null;
    }
}
