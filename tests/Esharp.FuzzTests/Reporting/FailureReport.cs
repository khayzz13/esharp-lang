using System.Text;
using Esharp.FuzzTests.Execution;

namespace Esharp.FuzzTests.Reporting;

/// A single deduplicated bug: every case in the run that shares a bucket key,
/// with one representative kept for shrinking and artifact output.
internal sealed class FailureBucket(string key, CaseRequest representative, CaseResult result, string oracle)
{
    public string Key { get; } = key;
    public string Oracle { get; } = oracle;
    public CaseRequest Representative { get; private set; } = representative;
    public CaseResult Result { get; private set; } = result;
    public int Count { get; private set; } = 1;
    public string? MinimizedSource { get; set; }

    public void Add(CaseRequest request, CaseResult result)
    {
        Count++;
        // Prefer the smallest witness as the shrink seed.
        if (request.PrimarySource.Length < Representative.PrimarySource.Length)
        {
            Representative = request;
            Result = result;
        }
    }
}

/// Collects every failure in a run instead of dying on the first, dedupes by
/// bucket key, and renders one digest at the end. A fuzzing run's product is
/// "N distinct bugs with minimized repros", not "the first assert that fired".
internal sealed class FailureReport
{
    readonly Dictionary<string, FailureBucket> _buckets = new(StringComparer.Ordinal);
    public int CasesExecuted { get; private set; }

    public IReadOnlyCollection<FailureBucket> Buckets => _buckets.Values;
    public bool HasFailures => _buckets.Count > 0;

    public void Count(int cases = 1) => CasesExecuted += cases;

    /// Record a failure. `oracle` names the invariant that was violated;
    /// `keyPrefix` lets oracle-level failures (wrong value, divergence) carve
    /// their own bucket space on top of the mechanical outcome key.
    public void Record(string oracle, CaseRequest request, CaseResult result, string? keyOverride = null)
    {
        var key = keyOverride ?? $"{oracle}::{result.BucketKey}";
        lock (_buckets)
        {
            if (_buckets.TryGetValue(key, out var bucket))
                bucket.Add(request, result);
            else
                _buckets[key] = new FailureBucket(key, request, result, oracle);
        }
    }

    /// Shrink one representative per bucket. The predicate is bucket-keyed:
    /// a candidate counts as "still failing" only if it reproduces the same
    /// bucket, so shrinking never wanders onto a different bug.
    public void ShrinkAll(Shrinking.DeltaShrinker shrinker, Func<string, CaseRequest> rebuild, Func<CaseRequest, CaseResult> execute)
    {
        foreach (var bucket in _buckets.Values.OrderByDescending(b => b.Count).Take(6))
        {
            // Wrong-value miscompiles (outcome Success, value ≠ oracle) can't be
            // shrunk against a recomputed expectation — deleting statements
            // changes the correct answer — so they keep their full witness.
            // Multi-file cases shrink only via the soak's dedicated paths.
            if (bucket.Result.Kind == OutcomeKind.Success || bucket.Representative.Files.Count != 1)
                continue;
            var targetKey = bucket.Result.BucketKey;
            bucket.MinimizedSource = shrinker.Shrink(
                bucket.Representative.PrimarySource,
                candidate => execute(rebuild(candidate)).BucketKey == targetKey);
        }
    }

    public void WriteArtifacts(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var index = 0;
        foreach (var bucket in _buckets.Values.OrderByDescending(b => b.Count))
        {
            var stem = $"{index++:d3}_{Sanitize(bucket.Key)}";
            File.WriteAllText(Path.Combine(outputDirectory, stem + ".es"),
                bucket.MinimizedSource ?? bucket.Representative.PrimarySource);
            File.WriteAllText(Path.Combine(outputDirectory, stem + ".txt"), Describe(bucket));
        }
    }

    public string Digest(int maxBuckets = 20)
    {
        if (!HasFailures)
            return $"{CasesExecuted} cases, no failures.";

        var sb = new StringBuilder();
        sb.AppendLine($"{CasesExecuted} cases, {_buckets.Count} distinct failure bucket(s):");
        foreach (var bucket in _buckets.Values.OrderByDescending(b => b.Count).Take(maxBuckets))
        {
            sb.AppendLine(new string('─', 72));
            sb.AppendLine(Describe(bucket));
        }
        if (_buckets.Count > maxBuckets)
            sb.AppendLine($"... and {_buckets.Count - maxBuckets} more bucket(s).");
        return sb.ToString();
    }

    static string Describe(FailureBucket bucket)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{bucket.Oracle}] {bucket.Key}  ({bucket.Count} case(s))");
        sb.AppendLine($"case: {bucket.Representative.Id}");
        sb.AppendLine(bucket.Result.Describe());
        var source = bucket.MinimizedSource ?? bucket.Representative.PrimarySource;
        var label = bucket.MinimizedSource is null ? "source" : "minimized source";
        sb.AppendLine($"{label} ({source.Length} chars):");
        sb.AppendLine(source.Length <= 3000 ? source : source[..3000] + "\n… (truncated)");
        if (bucket.Representative.Files.Count > 1)
            foreach (var extra in bucket.Representative.Files.Skip(1))
            {
                sb.AppendLine($"-- {extra.FileName} --");
                sb.AppendLine(extra.Source.Length <= 2000 ? extra.Source : extra.Source[..2000] + "\n… (truncated)");
            }
        return sb.ToString();
    }

    static string Sanitize(string value)
    {
        var cleaned = string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }
}
