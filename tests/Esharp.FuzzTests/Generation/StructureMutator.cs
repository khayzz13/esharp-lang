using System.Text.RegularExpressions;
using Esharp.Diagnostics;
using Esharp.Syntax.Parsing;
using Esharp.Syntax;

namespace Esharp.FuzzTests.Generation;

/// Structure-aware mutation of corpus programs: parse with the real parser,
/// mutate at the declaration level (remove / duplicate a member, flip the
/// allocation pin, jitter literals, canonical-reprint). Stays near-valid, so
/// the result exercises bind/emit rather than the first parse error.
internal sealed class StructureMutator(int seed)
{
    readonly Random _random = new(seed);

    public (string Source, string Mutation) Mutate(string source, string origin)
    {
        CompilationUnitSyntax? syntax = null;
        try
        {
            var parser = new Parser(source, origin);
            var parsed = parser.ParseCompilationUnit();
            if (!parser.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                syntax = parsed;
        }
        catch
        {
            // Unparseable corpus entry — fall through to text-level mutation.
        }

        if (syntax is null || syntax.Members.Count == 0)
            return (JitterIntLiteral(source), "jitter-literal");

        switch (_random.Next(5))
        {
            case 0 when syntax.Members.Count > 1:
            {
                var span = MemberSpan(source, syntax, _random.Next(syntax.Members.Count));
                return span is var (start, end) && end > start
                    ? (source.Remove(start, end - start), "remove-member")
                    : (JitterIntLiteral(source), "jitter-literal");
            }
            case 1:
            {
                var span = MemberSpan(source, syntax, _random.Next(syntax.Members.Count));
                if (span is var (start, end) && end > start)
                {
                    var member = source[start..end];
                    return (source.Insert(end, "\n" + member), "duplicate-member");
                }
                return (JitterIntLiteral(source), "jitter-literal");
            }
            case 2:
                return (FlipAllocationPin(source), "flip-allocation-pin");
            case 3:
                try
                {
                    return (SyntaxPrinter.PrintCanonical(syntax), "canonical-print");
                }
                catch
                {
                    return (JitterIntLiteral(source), "jitter-literal");
                }
            default:
                return (JitterIntLiteral(source), "jitter-literal");
        }
    }

    /// Parse → canonical print, for the printer-roundtrip oracle: the reprint
    /// must itself parse cleanly and reprint to the same text (fixpoint).
    public static string? CanonicalPrint(string source, string origin)
    {
        var parser = new Parser(source, origin);
        var syntax = parser.ParseCompilationUnit();
        if (parser.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return null;
        return SyntaxPrinter.PrintCanonical(syntax);
    }

    static (int Start, int End) MemberSpan(string source, CompilationUnitSyntax syntax, int index)
    {
        var span = syntax.Members[index].Span;
        if (!span.IsValid || span.End <= span.Start || span.End > source.Length)
            return (0, 0);
        var start = span.Start;
        while (start > 0 && source[start - 1] is ' ' or '\t')
            start--;
        var end = span.End;
        while (end < source.Length && source[end] is '\r' or '\n')
            end++;
        return (start, end);
    }

    string JitterIntLiteral(string source)
    {
        var matches = Regex.Matches(source, @"(?<![\w.])\d+(?![\w.])");
        if (matches.Count == 0)
            return source;
        var match = matches[_random.Next(matches.Count)];
        var replacement = _random.Next(5) switch
        {
            0 => "0",
            1 => "2147483647",
            2 => "1",
            _ => _random.Next(-99, 100).ToString(),
        };
        return source.Remove(match.Index, match.Length).Insert(match.Index, replacement);
    }

    static string FlipAllocationPin(string source)
    {
        if (source.Contains("[Struct]", StringComparison.Ordinal))
            return source.Replace("[Struct]", "[Class]", StringComparison.Ordinal);
        if (source.Contains("[Class]", StringComparison.Ordinal))
            return source.Replace("[Class]", "[Struct]", StringComparison.Ordinal);
        // No pin present: pin the first data declaration to a struct.
        var index = source.IndexOf("\nstruct ", StringComparison.Ordinal);
        return index < 0 ? source : source.Insert(index + 1, "[Struct]\n");
    }
}
