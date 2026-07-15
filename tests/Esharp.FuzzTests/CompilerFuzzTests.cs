using Esharp.FuzzTests.Corpus;
using Esharp.FuzzTests.Execution;
using Esharp.FuzzTests.Generation;
using Esharp.FuzzTests.Reporting;

namespace Esharp.FuzzTests;

/// One executor pool for the whole test class — child processes are recycled
/// across facts instead of restarted per case.
public sealed class FuzzExecutorFixture : IDisposable
{
    internal FuzzExecutor Executor { get; } = new();
    public void Dispose() => Executor.Dispose();
}

/// The CI surface of the fuzzer: bounded, fixed-seed, deterministic runs of
/// every oracle. Each fact runs its whole budget, dedupes failures into
/// buckets, shrinks the crash-shaped ones, and fails once with the digest.
/// `ESHARP_FUZZ_CASES` scales the budgets; the unbounded discovery loop is
/// `dotnet run -- soak`.
public sealed class CompilerFuzzTests(FuzzExecutorFixture fixture) : IClassFixture<FuzzExecutorFixture>
{
    FuzzExecutor Executor => fixture.Executor;

    static int Scale(int baseline)
    {
        var raw = Environment.GetEnvironmentVariable("ESHARP_FUZZ_CASES");
        return int.TryParse(raw, out var scale) && scale > 0
            ? Math.Max(1, baseline * scale / 100)
            : baseline;
    }

    static CaseRequest RebuildValid(string source)
        => new("shrink", [new SourceFile("main.es", source)], Invoke: true, TimeoutMs: 5000);

    static CaseRequest RebuildFrontEnd(string source)
        => new("shrink", [new SourceFile("main.es", source)], Invoke: false, TimeoutMs: 5000);

    void AssertClean(FailureReport report, Func<string, CaseRequest> rebuild)
    {
        var digest = Oracles.Finish(report, Executor, rebuild);
        if (digest is not null)
            Assert.Fail(digest);
    }

    // ── Construction oracle ──────────────────────────────────────────────────

    [Fact]
    public void GeneratedPrograms_RunToTheInterpretedResult()
    {
        var report = new FailureReport();
        var count = Scale(24);
        for (var i = 0; i < count; i++)
        {
            var program = new ProgramGenerator(0xE5F0 + i, GeneratorProfile.Ci).Generate();
            var request = program.BuildRequest($"gen-ci-{i}", emitTwice: i % 4 == 0);
            Oracles.CheckConstruction(report, request, Executor.Execute(request), program.ExpectedText);
        }
        AssertClean(report, RebuildValid);
    }

    [Fact]
    public void GeneratedPrograms_BoundaryProfile_RunToTheInterpretedResult()
    {
        var report = new FailureReport();
        var count = Scale(6);
        for (var i = 0; i < count; i++)
        {
            var program = new ProgramGenerator(0xB0DA + i, GeneratorProfile.BoundaryProfile).Generate();
            var request = program.BuildRequest($"gen-boundary-{i}", timeoutMs: 40_000);
            Oracles.CheckConstruction(report, request, Executor.Execute(request), program.ExpectedText);
        }
        AssertClean(report, RebuildValid);
    }

    // ── Representation metamorphism ──────────────────────────────────────────

    [Fact]
    public void RepresentationPins_StructAndClass_AgreeWithTheOracle()
    {
        var report = new FailureReport();
        var count = Scale(10);
        for (var i = 0; i < count; i++)
        {
            var program = new ProgramGenerator(0x51A5 + i, GeneratorProfile.Ci).Generate();
            foreach (var pin in new[] { RepresentationPin.Struct, RepresentationPin.Class })
            {
                var request = program.BuildRequest($"gen-pin-{pin}-{i}", pin);
                Oracles.CheckConstruction(report, request, Executor.Execute(request), program.ExpectedText);
            }
        }
        AssertClean(report, RebuildValid);
    }

    // ── Peephole differential ────────────────────────────────────────────────

    [Fact]
    public void ShortenOpcodes_LongFormBodies_AgreeWithTheOracle()
    {
        var report = new FailureReport();
        var count = Scale(8);
        for (var i = 0; i < count; i++)
        {
            // Boundary programs cross the ±127-byte branch range — exactly
            // where short-form compaction can go wrong.
            var profile = i % 2 == 0 ? GeneratorProfile.Ci : GeneratorProfile.BoundaryProfile;
            var program = new ProgramGenerator(0x0FF0 + i, profile).Generate();
            var request = program.BuildRequest($"gen-longform-{i}", shortenOpcodes: false, timeoutMs: 40_000);
            Oracles.CheckConstruction(report, request, Executor.Execute(request), program.ExpectedText);
        }
        AssertClean(report, RebuildValid);
    }

    // ── ICE-free invariant ───────────────────────────────────────────────────

    [Fact]
    public void TokenMutations_NeverIceTheFrontEnd()
    {
        var report = new FailureReport();
        var mutator = new TokenMutator(0x70CE);
        var seeds = CorpusProvider.Sample(0x70CE, Scale(30), maxSourceLength: 6000);
        var index = 0;
        foreach (var seed in seeds)
        {
            for (var m = 0; m < 4; m++)
            {
                var (source, mutation) = mutator.Mutate(seed.Source);
                var request = new CaseRequest($"tokmut-{index++}-{mutation}", [new SourceFile("main.es", source)], Invoke: false);
                Oracles.CheckNoIce(report, request, Executor.Execute(request));
            }
        }
        for (var s = 0; s < Scale(20); s++)
        {
            var (source, mutation) = mutator.Synthesize();
            var request = new CaseRequest($"synth-{s}-{mutation}", [new SourceFile("main.es", source)], Invoke: false);
            Oracles.CheckNoIce(report, request, Executor.Execute(request));
        }
        AssertClean(report, RebuildFrontEnd);
    }

    [Fact]
    public void TrustedCorpus_NeverIces_AndEmitsVerifiableIl()
    {
        var report = new FailureReport();
        foreach (var entry in CorpusProvider.Sample(0xC02B, Scale(60), trustedOnly: true))
        {
            var request = new CaseRequest($"corpus::{entry.Origin}", [new SourceFile("main.es", entry.Source)], Invoke: false);
            Oracles.CheckNoIce(report, request, Executor.Execute(request), verifierIsFailure: true);
        }
        AssertClean(report, RebuildFrontEnd);
    }

    [Fact]
    public void StructureMutations_NeverIce()
    {
        var report = new FailureReport();
        var index = 0;
        foreach (var entry in CorpusProvider.Sample(0x57AB, Scale(30), maxSourceLength: 6000, trustedOnly: true))
        {
            var (source, mutation) = new StructureMutator(0x57AB + index).Mutate(entry.Source, entry.Origin);
            var request = new CaseRequest($"structmut-{index++}-{mutation}", [new SourceFile("main.es", source)], Invoke: false);
            Oracles.CheckNoIce(report, request, Executor.Execute(request), verifierIsFailure: false);
        }
        AssertClean(report, RebuildFrontEnd);
    }

    // ── Printer round-trip ───────────────────────────────────────────────────

    [Fact]
    public void CanonicalPrint_RoundTripsToAFixpoint()
    {
        var report = new FailureReport();
        var index = 0;
        foreach (var entry in CorpusProvider.Sample(0x9217, Scale(30), maxSourceLength: 6000, trustedOnly: true))
        {
            index++;
            string? printed;
            try
            {
                printed = StructureMutator.CanonicalPrint(entry.Source, entry.Origin);
            }
            catch (Exception ex)
            {
                report.Count();
                report.Record("printer", new CaseRequest($"print-{index}", [new SourceFile("main.es", entry.Source)], Invoke: false),
                    new CaseResult($"print-{index}", OutcomeKind.Ice, FuzzStage.Parse, [],
                        ExceptionType: ex.GetType().FullName, ExceptionMessage: ex.Message, StackTrace: ex.StackTrace));
                continue;
            }
            if (printed is null)
                continue; // entry doesn't parse cleanly — not printable, not a finding

            // The reprint must parse cleanly through the real front end…
            var request = new CaseRequest($"print::{entry.Origin}", [new SourceFile("main.es", printed)], Invoke: false);
            var result = Executor.Execute(request);
            report.Count();
            if (result.Kind is not (OutcomeKind.Success or OutcomeKind.VerifierError) &&
                !(result.Kind == OutcomeKind.Rejected && result.Stage > FuzzStage.Parse))
            {
                report.Record("printer-reparse", request, result);
                continue;
            }

            // …and reprinting the reprint must be a fixpoint.
            try
            {
                var second = StructureMutator.CanonicalPrint(printed, entry.Origin);
                if (second is not null && second != printed)
                    report.Record("printer-fixpoint", request,
                        new CaseResult(request.Id, OutcomeKind.Infrastructure, FuzzStage.Parse, [],
                            ExceptionType: "PrinterNotFixpoint",
                            ExceptionMessage: $"second print differs at offset {Diff(printed, second)}"),
                        keyOverride: "PRINTER::not-fixpoint");
            }
            catch (Exception ex)
            {
                report.Record("printer-fixpoint", request,
                    new CaseResult(request.Id, OutcomeKind.Ice, FuzzStage.Parse, [],
                        ExceptionType: ex.GetType().FullName, ExceptionMessage: ex.Message, StackTrace: ex.StackTrace));
            }
        }
        AssertClean(report, RebuildFrontEnd);
    }

    static int Diff(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
            if (a[i] != b[i])
                return i;
        return n;
    }

    // (The transpiler differential test was removed in the backend rewrite: the
    // transpiler backend is archived — IL is the single source-of-truth backend now.)
}
