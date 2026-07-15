using System.Diagnostics;
using Esharp.FuzzTests.Corpus;
using Esharp.FuzzTests.Execution;
using Esharp.FuzzTests.Generation;
using Esharp.FuzzTests.Reporting;

namespace Esharp.FuzzTests;

/// The unbounded discovery loop: rotates every oracle family for a wall-clock
/// budget, never stops at a failure, dedupes findings into buckets, shrinks
/// the crash-shaped ones at the end, and writes one artifact pair per bucket.
/// Its product is `N distinct bugs with minimized source files`, printed and saved;
/// confirmed findings graduate into committed regression tests by hand.
internal static class Soak
{
    public static int Run(string[] args)
    {
        var minutes = ArgInt(args, "--minutes", 10);
        var seed = ArgInt(args, "--seed", Environment.TickCount & 0x7FFFFFFF);
        var workers = ArgInt(args, "--workers", 0);
        var outDir = ArgString(args, "--out") ?? Path.Combine(CorpusProvider.ResolveEsharpRoot(), "fuzz-out",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        Console.WriteLine($"soak: minutes={minutes} seed={seed} out={outDir}");
        using var executor = new FuzzExecutor(workers);
        var report = new FailureReport();
        var deadline = Stopwatch.StartNew();
        var rng = new Random(seed);
        var round = 0;
        var tokenMutator = new TokenMutator(seed);

        while (deadline.Elapsed < TimeSpan.FromMinutes(minutes))
        {
            round++;
            var family = rng.Next(7);
            var batch = new List<(CaseRequest Request, string? Expected)>();
            var caseSeed = unchecked(seed + round * 747796405);

            switch (family)
            {
                case 0: // construction, CI-size
                case 1: // construction, soak-size
                case 2: // construction, boundary
                {
                    var profile = family switch
                    {
                        0 => GeneratorProfile.Ci,
                        1 => GeneratorProfile.Soak,
                        _ => GeneratorProfile.BoundaryProfile,
                    };
                    for (var i = 0; i < 16; i++)
                    {
                        var program = new ProgramGenerator(caseSeed + i, profile).Generate();
                        var pin = rng.Next(4) switch
                        {
                            0 => RepresentationPin.Struct,
                            1 => RepresentationPin.Class,
                            _ => RepresentationPin.None,
                        };
                        batch.Add((program.BuildRequest($"soak-r{round}-c{i}", pin,
                            shortenOpcodes: rng.Next(4) != 0,
                            emitTwice: rng.Next(4) == 0,
                            timeoutMs: 40_000), program.ExpectedText));
                    }
                    break;
                }
                case 3: // token mutations over the corpus
                {
                    foreach (var entry in CorpusProvider.Sample(caseSeed, 24, maxSourceLength: 8000))
                    {
                        var (source, mutation) = tokenMutator.Mutate(entry.Source);
                        batch.Add((new CaseRequest($"soak-r{round}-tok-{mutation}",
                            [new SourceFile("main.es", source)], Invoke: false), null));
                    }
                    break;
                }
                case 4: // synthesized adversarial inputs
                {
                    for (var i = 0; i < 24; i++)
                    {
                        var (source, mutation) = tokenMutator.Synthesize();
                        batch.Add((new CaseRequest($"soak-r{round}-syn-{mutation}",
                            [new SourceFile("main.es", source)], Invoke: false), null));
                    }
                    break;
                }
                case 5: // structure mutations over the trusted corpus
                {
                    var index = 0;
                    foreach (var entry in CorpusProvider.Sample(caseSeed, 24, maxSourceLength: 8000, trustedOnly: true))
                    {
                        var (source, mutation) = new StructureMutator(caseSeed + index++).Mutate(entry.Source, entry.Origin);
                        batch.Add((new CaseRequest($"soak-r{round}-str-{mutation}",
                            [new SourceFile("main.es", source)], Invoke: false), null));
                    }
                    break;
                }
                case 6: // raw trusted corpus (catches regressions in the front end itself)
                {
                    foreach (var entry in CorpusProvider.Sample(caseSeed, 24, trustedOnly: true))
                        batch.Add((new CaseRequest($"soak-r{round}-corpus::{entry.Origin}",
                            [new SourceFile("main.es", entry.Source)], Invoke: false), null));
                    break;
                }
            }

            var expectedById = batch.Where(b => b.Expected is not null)
                .ToDictionary(b => b.Request.Id, b => b.Expected!);
            executor.ExecuteAll(batch.Select(b => b.Request).ToList(), (request, result) =>
            {
                if (expectedById.TryGetValue(request.Id, out var expected))
                    Oracles.CheckConstruction(report, request, result, expected);
                else
                    Oracles.CheckNoIce(report, request, result,
                        verifierIsFailure: request.Id.Contains("corpus", StringComparison.Ordinal));
            });

            Console.WriteLine($"round={round} family={family} cases={report.CasesExecuted} buckets={report.Buckets.Count} elapsed={deadline.Elapsed:hh\\:mm\\:ss}");
        }

        Console.WriteLine($"soak complete: {report.CasesExecuted} cases, {report.Buckets.Count} distinct failure bucket(s).");
        if (report.HasFailures)
        {
            Console.WriteLine("shrinking representatives…");
            // Invoke stays on so invoke-stage buckets (JIT rejects, runtime
            // crashes) reproduce; the tight timeout bounds shrink wall-clock.
            report.ShrinkAll(new Shrinking.DeltaShrinker(400),
                source => new CaseRequest("shrink", [new SourceFile("main.es", source)], Invoke: true, TimeoutMs: 4000),
                executor.Execute);
            report.WriteArtifacts(outDir);
            Console.WriteLine(report.Digest());
            Console.WriteLine($"artifacts: {outDir}");
            return 1;
        }
        return 0;
    }

    static int ArgInt(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value)
            ? value
            : fallback;
    }

    static string? ArgString(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
