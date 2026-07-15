using System.Text.RegularExpressions;

namespace Esharp.FuzzTests.Corpus;

/// Trusted entries are believed-valid current E#. Untrusted entries seed
/// crash-hunting only; no oracle may assume they compile.
internal sealed record CorpusEntry(string Source, string Origin, bool Trusted = true);

/// Seed corpus aggregation. The hand-written test suite is always loaded. Additional
/// trusted or untrusted `.es` trees can be supplied through
/// `ESHARP_FUZZ_TRUSTED_CORPUS` and `ESHARP_FUZZ_UNTRUSTED_CORPUS`; each variable accepts
/// multiple directories separated by the platform path separator.
internal static class CorpusProvider
{
    static readonly Lazy<IReadOnlyList<CorpusEntry>> All = new(LoadAll);
    static readonly Regex ProgramSeparator = new(@"^// ── .* ──\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static IReadOnlyList<CorpusEntry> Load(int maxSourceLength = 20_000, bool trustedOnly = false)
        => All.Value.Where(e => e.Source.Length <= maxSourceLength && (!trustedOnly || e.Trusted)).ToList();

    /// Deterministic shuffled sample.
    public static IReadOnlyList<CorpusEntry> Sample(int seed, int count, int maxSourceLength = 20_000, bool trustedOnly = false)
    {
        var corpus = Load(maxSourceLength, trustedOnly);
        if (corpus.Count == 0)
            return corpus;
        var random = new Random(seed);
        var indices = Enumerable.Range(0, corpus.Count).ToArray();
        for (var i = indices.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }
        return indices.Take(Math.Min(count, indices.Length)).Select(i => corpus[i]).ToList();
    }

    static IReadOnlyList<CorpusEntry> LoadAll()
    {
        var root = ResolveEsharpRoot();
        var entries = new List<CorpusEntry>();

        LoadTestRawStrings(Path.Combine(root, "tests", "Esharp.Tests"), entries);
        LoadConfiguredCorpora("ESHARP_FUZZ_TRUSTED_CORPUS", root, entries, trusted: true);
        LoadConfiguredCorpora("ESHARP_FUZZ_UNTRUSTED_CORPUS", root, entries, trusted: false);

        // Dedupe exact duplicates (the docs corpus is extracted from the tests).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return entries.Where(e => seen.Add(e.Source.Trim())).ToList();
    }

    static void LoadConfiguredCorpora(
        string variable,
        string root,
        List<CorpusEntry> entries,
        bool trusted)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(configured))
            return;

        foreach (var directory in configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            LoadEsFiles(Path.GetFullPath(directory), root, entries, trusted);
    }

    static void LoadTestRawStrings(string testsDir, List<CorpusEntry> entries)
    {
        if (!Directory.Exists(testsDir))
            return;
        foreach (var file in Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            var index = 0;
            foreach (var raw in ExtractRawStrings(text))
            {
                index++;
                var source = raw.Trim('\r', '\n');
                if (LooksLikeEsharp(source))
                    entries.Add(new CorpusEntry(source, $"{Path.GetFileName(file)}#{index}"));
            }
        }
    }

    static void LoadEsFiles(string directory, string root, List<CorpusEntry> entries, bool trusted)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.es", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            var origin = Path.GetRelativePath(root, file);

            var separators = ProgramSeparator.Matches(text);
            if (separators.Count >= 2)
            {
                // Multi-program corpus file: each segment runs from one header to the next.
                for (var i = 0; i < separators.Count; i++)
                {
                    var start = separators[i].Index + separators[i].Length;
                    var end = i + 1 < separators.Count ? separators[i + 1].Index : text.Length;
                    var segment = StripLeadingComments(text[start..end]);
                    if (LooksLikeEsharp(segment))
                        entries.Add(new CorpusEntry(segment, $"{origin}#{i + 1}", trusted));
                }
            }
            else if (LooksLikeEsharp(text))
            {
                entries.Add(new CorpusEntry(text.Trim('\r', '\n'), origin, trusted));
            }
        }
    }

    static string StripLeadingComments(string segment)
    {
        var lines = segment.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && (lines[0].TrimStart().StartsWith("//", StringComparison.Ordinal) || lines[0].Trim().Length == 0))
            lines.RemoveAt(0);
        return string.Join('\n', lines).TrimEnd();
    }

    static IEnumerable<string> ExtractRawStrings(string text)
    {
        var cursor = 0;
        while (cursor < text.Length)
        {
            var start = text.IndexOf("\"\"\"", cursor, StringComparison.Ordinal);
            if (start < 0)
                yield break;
            var contentStart = start + 3;
            var end = text.IndexOf("\"\"\"", contentStart, StringComparison.Ordinal);
            if (end < 0)
                yield break;
            yield return text[contentStart..end];
            cursor = end + 3;
        }
    }

    static bool LooksLikeEsharp(string source)
    {
        if (source.Length < 10)
            return false;
        // Interpolated raw strings in tests carry {{placeholders}}; those are
        // templates, not programs.
        if (source.Contains("{{", StringComparison.Ordinal))
            return false;
        return source.Contains("func ", StringComparison.Ordinal)
            || source.Contains("struct ", StringComparison.Ordinal)
            || source.Contains("union ", StringComparison.Ordinal)
            || source.Contains("interface ", StringComparison.Ordinal);
    }

    internal static string ResolveEsharpRoot()
    {
        var env = Environment.GetEnvironmentVariable("ESHARP_FUZZ_ROOT");
        if (!string.IsNullOrWhiteSpace(env))
            return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "tests", "Esharp.Tests")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Could not locate the esharp root. Set ESHARP_FUZZ_ROOT.");
    }
}
