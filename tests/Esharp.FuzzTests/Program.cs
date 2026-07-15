using Esharp.FuzzTests.Execution;

namespace Esharp.FuzzTests;

/// Entry point for the two non-xunit modes:
///   --child         the execution worker (spawned by ChildExecutor; speaks
///                   the line protocol on stdin/stdout)
///   soak [options]  the unbounded discovery loop — see Soak.cs
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--child"))
            return ChildLoop();
        if (args.Length > 0 && args[0] == "soak")
            return Soak.Run(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "gen")
            return PrintGenerated(args.Skip(1).ToArray());
        if (args.Length > 1 && args[0] == "shrink")
            return ShrinkFile(args.Skip(1).ToArray());

        Console.Error.WriteLine("""
usage:
  dotnet run -- soak [--minutes N] [--seed S] [--workers W] [--out DIR] [--transpiler]
  dotnet run -- --child        (internal: execution worker)
""");
        return 2;
    }

    /// `shrink <file.es> [companion.es …] [--no-invoke] [--budget N]` — execute,
    /// take the failure bucket, ddmin the FIRST file while the same bucket
    /// reproduces (companions ride along untouched), write `<file>.min.es`.
    static int ShrinkFile(string[] args)
    {
        var paths = args.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var path = paths[0];
        var source = File.ReadAllText(path);
        var companions = paths.Skip(1)
            .Select(p => new SourceFile(Path.GetFileName(p), File.ReadAllText(p)))
            .ToList();
        var invoke = !args.Contains("--no-invoke");
        var budget = ArgInt(args, "--budget", 400);

        using var executor = new FuzzExecutor(1);
        CaseRequest Build(string s) => new("shrink",
            [new SourceFile("main.es", s), .. companions], Invoke: invoke, TimeoutMs: 8000);

        var original = executor.Execute(Build(source));
        Console.WriteLine($"original: {original.BucketKey}");
        if (original.Kind == OutcomeKind.Success)
        {
            Console.Error.WriteLine("input does not fail — nothing to shrink.");
            return 2;
        }

        var targetKey = original.BucketKey;
        var minimized = new Shrinking.DeltaShrinker(budget).Shrink(source,
            candidate => executor.Execute(Build(candidate)).BucketKey == targetKey);

        var outPath = Path.ChangeExtension(path, ".min.es");
        File.WriteAllText(outPath, minimized);
        Console.WriteLine($"shrunk {source.Length} → {minimized.Length} chars: {outPath}");
        Console.WriteLine(minimized);
        return 0;
    }

    /// `gen --seed N [--profile ci|soak|boundary] [--run]` — print one generated
    /// program with its interpreted value; with --run, execute it through the
    /// pipeline and report agreement. The generator-debugging loop.
    static int PrintGenerated(string[] args)
    {
        var seed = ArgInt(args, "--seed", 1);
        var profile = ArgString(args, "--profile") switch
        {
            "soak" => Generation.GeneratorProfile.Soak,
            "boundary" => Generation.GeneratorProfile.BoundaryProfile,
            _ => Generation.GeneratorProfile.Ci,
        };
        var program = new Generation.ProgramGenerator(seed, profile).Generate();
        var files = program.RenderFiles(Generation.RepresentationPin.None);
        if (ArgString(args, "--save") is { } saveDir)
        {
            Directory.CreateDirectory(saveDir);
            foreach (var file in files)
                File.WriteAllText(Path.Combine(saveDir, file.FileName), file.Source);
            Console.WriteLine($"saved {files.Count} file(s) to {saveDir} (expected: {program.ExpectedText})");
        }
        foreach (var file in files)
        {
            Console.WriteLine($"// ── {file.FileName} ──");
            Console.WriteLine(file.Source);
        }
        Console.WriteLine($"// expected: {program.ExpectedText}");
        if (!args.Contains("--run"))
            return 0;

        var result = new CompilerPipeline().Execute(program.BuildRequest($"gen-{seed}"));
        Console.WriteLine(result.Describe());
        var agree = result.Kind == OutcomeKind.Success && result.ValueText == program.ExpectedText;
        Console.WriteLine(agree ? "AGREE" : "DISAGREE");
        return agree ? 0 : 1;
    }

    static int ArgInt(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var v) ? v : fallback;
    }

    static string? ArgString(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    static int ChildLoop()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);

        var pipeline = new CompilerPipeline();
        // Warm the pipeline (JIT + metadata caches) before signalling READY so
        // the first real case's timeout budget isn't spent on startup.
        pipeline.Execute(new CaseRequest("warmup",
            [new SourceFile("warm.es", "namespace Test\n\nfunc go() -> int {\n    return 1\n}\n")]));
        stdout.WriteLine(Protocol.ReadyLine);

        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (line.Length == 0)
                continue;
            CaseResult result;
            try
            {
                var request = Protocol.Deserialize<CaseRequest>(line);
                result = pipeline.Execute(request, stage => stdout.WriteLine($"PHASE {stage}"));
            }
            catch (Exception ex)
            {
                result = new CaseResult("?", OutcomeKind.Infrastructure, FuzzStage.Parse, [],
                    ExceptionType: ex.GetType().FullName, ExceptionMessage: ex.Message);
            }
            stdout.WriteLine(Protocol.ResultPrefix + Protocol.Serialize(result));
        }
        return 0;
    }
}
