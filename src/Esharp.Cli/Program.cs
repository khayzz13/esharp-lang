using Esharp.Compiler;
using Esharp.Compiler.Binding;
using Esharp.Compiler.Parsing;
using Esharp.ILEmit;

if (args.Length < 1)
{
    PrintUsage();
    return 1;
}

var command = args[0].ToLowerInvariant();

switch (command)
{
    case "compile" when args.Length == 3:
        return await CompileSingle(args[1], args[2]);
    case "compile-project" when args.Length >= 2:
        var outDir = args.Length >= 4 && args[2] == "--out" ? args[3] : null;
        return await CompileProject(args[1], outDir);
    case "compile-il" when args.Length == 3:
        return await CompileIL(args[1], args[2]);
    default:
        PrintUsage();
        return 1;
}

static async Task<int> CompileSingle(string input, string output)
{
    var inputPath = Path.GetFullPath(input);
    var outputPath = Path.GetFullPath(output);

    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Input file not found: {inputPath}");
        return 1;
    }

    var source = await File.ReadAllTextAsync(inputPath);
    var transpiler = new EsharpTranspiler();
    var result = transpiler.Transpile(source, inputPath);

    if (!result.Success)
    {
        foreach (var diagnostic in result.Diagnostics)
            Console.Error.WriteLine(diagnostic.ToString());
        return 1;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    await File.WriteAllTextAsync(outputPath, result.GeneratedCode);
    Console.WriteLine($"Generated {outputPath}");
    return 0;
}

static async Task<int> CompileProject(string dir, string? outDir)
{
    var dirPath = Path.GetFullPath(dir);
    if (!Directory.Exists(dirPath))
    {
        Console.Error.WriteLine($"Directory not found: {dirPath}");
        return 1;
    }

    var esFiles = Directory.GetFiles(dirPath, "*.es");
    if (esFiles.Length == 0)
    {
        Console.Error.WriteLine($"No .es files found in {dirPath}");
        return 1;
    }

    var files = new List<(string source, string filePath)>();
    foreach (var esFile in esFiles)
    {
        var source = await File.ReadAllTextAsync(esFile);
        files.Add((source, esFile));
    }

    var transpiler = new EsharpTranspiler();
    var result = transpiler.TranspileProject(files);

    if (!result.Success)
    {
        foreach (var diagnostic in result.Diagnostics)
            Console.Error.WriteLine(diagnostic.ToString());
        return 1;
    }

    var outputDir = outDir is not null ? Path.GetFullPath(outDir) : dirPath;
    Directory.CreateDirectory(outputDir);

    foreach (var file in result.Files)
    {
        if (!file.Success)
        {
            foreach (var diagnostic in file.Diagnostics)
                Console.Error.WriteLine(diagnostic.ToString());
            continue;
        }
        var outputName = Path.GetFileNameWithoutExtension(file.FilePath) + ".g.cs";
        var outputPath = Path.Combine(outputDir, outputName);
        await File.WriteAllTextAsync(outputPath, file.GeneratedCode);
        Console.WriteLine($"Generated {outputPath}");
    }

    return result.Files.All(f => f.Success) ? 0 : 1;
}

static async Task<int> CompileIL(string input, string output)
{
    var inputPath = Path.GetFullPath(input);
    var outputPath = Path.GetFullPath(output);

    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Input file not found: {inputPath}");
        return 1;
    }

    var source = await File.ReadAllTextAsync(inputPath);
    var parser = new Parser(source, inputPath);
    var syntax = parser.ParseCompilationUnit();
    if (parser.Diagnostics.Count > 0)
    {
        foreach (var d in parser.Diagnostics) Console.Error.WriteLine(d.ToString());
        return 1;
    }

    var binder = new Binder();
    var boundTree = binder.Bind(syntax);
    if (binder.Diagnostics.Count > 0)
    {
        foreach (var d in binder.Diagnostics) Console.Error.WriteLine(d.ToString());
        return 1;
    }

    var assemblyName = Path.GetFileNameWithoutExtension(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    ILEmitter.EmitToFile(boundTree, assemblyName, outputPath);
    Console.WriteLine($"Compiled {outputPath}");
    return 0;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  esharpc compile <input.es> <output.g.cs>");
    Console.Error.WriteLine("  esharpc compile-project <dir> [--out <outdir>]");
    Console.Error.WriteLine("  esharpc compile-il <input.es> <output.dll>");
}
