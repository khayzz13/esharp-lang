using Esharp.FuzzTests.Execution;

namespace Esharp.FuzzTests.Reporting;

/// The oracle checks, shared verbatim between the CI facts and the soak
/// driver. Each takes one executed case and records violations into the
/// report; nothing here throws — a fuzz run always completes its budget.
internal static class Oracles
{
    /// Construction oracle: a valid-by-construction program must compile
    /// without error diagnostics and return exactly the interpreted value.
    public static void CheckConstruction(FailureReport report, CaseRequest request, CaseResult result, string expectedText)
    {
        report.Count();
        switch (result.Kind)
        {
            case OutcomeKind.Success when result.ValueText == expectedText:
                CheckDeterminism(report, request, result);
                return;
            case OutcomeKind.Success:
                report.Record("construction", request, result,
                    keyOverride: "WRONGVALUE::miscompile");
                return;
            case OutcomeKind.Rejected:
                // The binder rejected a program the generator believes valid:
                // either a compiler bug or a generator-grammar bug — both findings.
                report.Record("construction-rejected", request, result);
                return;
            default:
                report.Record("construction", request, result);
                return;
        }
    }

    /// ICE-free invariant: any input, valid or garbage, must end in Success or
    /// Rejected — never a throw, hang, crash, or unverifiable emit.
    public static void CheckNoIce(FailureReport report, CaseRequest request, CaseResult result, bool verifierIsFailure = true)
    {
        report.Count();
        switch (result.Kind)
        {
            case OutcomeKind.Success or OutcomeKind.Rejected:
                return;
            case OutcomeKind.VerifierError when !verifierIsFailure:
                return;
            case OutcomeKind.RuntimeException:
                // Only reachable when the case was invoked; mutated corpus is
                // never invoked, so treat as harness noise rather than a bug.
                return;
            default:
                report.Record("no-ice", request, result);
                return;
        }
    }

    /// Determinism oracle: when a case was emitted twice, the canonical
    /// assembly fingerprints must agree.
    public static void CheckDeterminism(FailureReport report, CaseRequest request, CaseResult result)
    {
        if (result.SecondAssemblyHash is not null && result.AssemblyHash != result.SecondAssemblyHash)
            report.Record("determinism", request, result, keyOverride: "NONDETERMINISTIC::emit");
    }

    /// Finish a run: shrink one representative per bucket (bucket-keyed
    /// predicate via a fresh single-file request), write artifacts, and return
    /// the digest (null when clean).
    public static string? Finish(
        FailureReport report,
        FuzzExecutor executor,
        Func<string, CaseRequest> rebuildForShrink,
        int shrinkBudget = 150)
    {
        if (!report.HasFailures)
            return null;

        report.ShrinkAll(new Shrinking.DeltaShrinker(shrinkBudget), rebuildForShrink, executor.Execute);
        var outDir = Environment.GetEnvironmentVariable("ESHARP_FUZZ_OUTPUT_DIR");
        if (!string.IsNullOrWhiteSpace(outDir))
            report.WriteArtifacts(outDir);
        return report.Digest();
    }
}
