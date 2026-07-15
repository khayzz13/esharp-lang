using System.Text.RegularExpressions;
using Esharp.Diagnostics;
using Esharp.Syntax.Lexing;

namespace Esharp.FuzzTests.Shrinking;

/// Hierarchical delta-debugging reducer. Pass 1 is classic ddmin over lines;
/// pass 2 deletes token spans (lexed with the real lexer, spliced from the
/// original text so trivia survives); pass 3 simplifies atoms (literals,
/// identifiers, string bodies). The passes iterate to a fixpoint under an
/// execution budget. The caller's predicate must be *bucket-keyed* — "still
/// the same bug", not merely "still fails" — so reduction can't slide onto a
/// different failure, and so wrong-value miscompiles (which compile fine)
/// shrink just as well as crashes.
internal sealed class DeltaShrinker(int maxExecutions = 500)
{
    public string Shrink(string source, Func<string, bool> stillFails)
    {
        var budget = maxExecutions;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool Check(string candidate)
        {
            if (budget <= 0 || candidate.Length == 0 || !seen.Add(candidate))
                return false;
            budget--;
            try { return stillFails(candidate); }
            catch { return false; }
        }

        var current = source;
        bool progressed = true;
        while (progressed && budget > 0)
        {
            progressed = false;

            var afterLines = DdminLines(current, Check);
            if (afterLines.Length < current.Length) { current = afterLines; progressed = true; }

            var afterTokens = DeleteTokenSpans(current, Check);
            if (afterTokens.Length < current.Length) { current = afterTokens; progressed = true; }

            var afterAtoms = SimplifyAtoms(current, Check);
            if (afterAtoms.Length < current.Length) { current = afterAtoms; progressed = true; }
        }
        return current;
    }

    /// Classic ddmin over the line list: try dropping complement chunks,
    /// doubling granularity when nothing can be removed.
    static string DdminLines(string source, Func<string, bool> check)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count < 2)
            return source;

        var granularity = 2;
        while (lines.Count >= 2)
        {
            var chunkSize = Math.Max(1, lines.Count / granularity);
            var removedAny = false;

            for (var start = 0; start < lines.Count; start += chunkSize)
            {
                var candidateLines = new List<string>(lines);
                candidateLines.RemoveRange(start, Math.Min(chunkSize, lines.Count - start));
                if (candidateLines.Count == 0)
                    continue;
                var candidate = string.Join('\n', candidateLines);
                if (check(candidate))
                {
                    lines = candidateLines;
                    removedAny = true;
                    granularity = Math.Max(granularity - 1, 2);
                    break;
                }
            }

            if (!removedAny)
            {
                if (chunkSize <= 1)
                    break;
                granularity = Math.Min(granularity * 2, lines.Count);
            }
        }
        return string.Join('\n', lines);
    }

    /// Token-level reduction: delete contiguous runs of tokens by splicing
    /// their source spans out of the original text. Runs from long to short so
    /// whole constructs vanish before single-token nibbling.
    static string DeleteTokenSpans(string source, Func<string, bool> check)
    {
        var current = source;
        for (var run = 8; run >= 1; run /= 2)
        {
            var again = true;
            while (again)
            {
                again = false;
                var spans = LexSpans(current);
                if (spans.Count < 2)
                    return current;
                for (var i = 0; i + run <= spans.Count; i++)
                {
                    var start = spans[i].Start;
                    var end = spans[i + run - 1].End;
                    if (end <= start || end > current.Length)
                        continue;
                    var candidate = current.Remove(start, end - start);
                    if (check(candidate))
                    {
                        current = candidate;
                        again = true;
                        break;
                    }
                }
            }
        }
        return current;
    }

    static IReadOnlyList<(int Start, int End)> LexSpans(string source)
    {
        try
        {
            var tokens = new Lexer(source, "shrink.es", new DiagnosticBag()).Lex();
            return tokens
                .Where(t => t.Text is { Length: > 0 })
                .Select(t => (t.Position, t.End))
                .Where(s => s.End <= source.Length)
                .ToList();
        }
        catch
        {
            // If the lexer itself crashes on this input (often the bug under
            // reduction), fall back to coarse character chunks.
            var spans = new List<(int, int)>();
            for (var i = 0; i < source.Length; i += 16)
                spans.Add((i, Math.Min(i + 16, source.Length)));
            return spans;
        }
    }

    static readonly Regex Number = new(@"(?<![\w.])\d{2,}(?![\w.])", RegexOptions.Compiled);
    static readonly Regex LongIdentifier = new(@"(?<![\w])[A-Za-z_][A-Za-z0-9_]{4,}(?![\w])", RegexOptions.Compiled);
    static readonly Regex StringBody = new("\"[^\"\\n]{2,}\"", RegexOptions.Compiled);

    /// Atom simplification: big numbers to 0, long string literals to "",
    /// long identifiers to a two-letter stub (consistently, all occurrences —
    /// declarations and uses must rename together).
    static string SimplifyAtoms(string source, Func<string, bool> check)
    {
        var current = source;

        foreach (Match match in Number.Matches(current).ToArray())
        {
            if (match.Index >= current.Length) continue;
            var candidate = current.Remove(match.Index, match.Length).Insert(match.Index, "0");
            if (candidate != current && check(candidate))
                current = candidate;
        }

        foreach (Match match in StringBody.Matches(current).ToArray())
        {
            if (match.Index >= current.Length) continue;
            var candidate = current.Remove(match.Index, match.Length).Insert(match.Index, "\"\"");
            if (candidate != current && check(candidate))
                current = candidate;
        }

        var stub = 0;
        foreach (var name in LongIdentifier.Matches(current).Select(m => m.Value).Distinct().ToArray())
        {
            if (LexicalReserved(name))
                continue;
            var replacement = "q" + (char)('a' + stub % 26);
            var candidate = Regex.Replace(current, $@"(?<![\w]){Regex.Escape(name)}(?![\w])", replacement);
            if (candidate != current && check(candidate))
            {
                current = candidate;
                stub++;
            }
        }
        return current;
    }

    static bool LexicalReserved(string name) => name is
        "namespace" or "interface" or "abstract" or "virtual" or "static" or "return" or
        "returns" or "union" or "match" or "while" or "default" or "derive" or "string" or
        "spawn" or "defer" or "await" or "async" or "throw" or "catch" or "class";
}
