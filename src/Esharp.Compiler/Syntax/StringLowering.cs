using System.Text;

namespace Esharp.Syntax;

/// The single home for E# string-literal decoding. Every string form — regular,
/// `$`-interpolated, `"""` raw, and `$"""` raw-interpolated — enters here as its raw
/// lexeme (delimiters and prefix intact) and leaves as either a constant value or a
/// list of interpolation segments the binder turns into a `BoundInterpolatedString`.
///
/// The forms mirror C# 11, adapted to E#'s contextual interpolation (a `{` opens a
/// hole only before an expression-start char, so most literal braces need no escaping):
///
///   - `"..."`            regular. Backslash escapes; contextual holes; `{{`/`}}`
///                        collapse ONLY when a hole is present (so hole-free JSON like
///                        `{"a":{}}` round-trips verbatim — the C# `$""`-only rule).
///   - `$"..."`           interpolated. Identical decode to regular (E# holes are
///                        contextual, so `$` documents intent and reads as C#); the
///                        prefix is accepted for familiarity and explicitness.
///   - `"""..."""`        raw. Verbatim — NO backslash escapes, NO interpolation, so a
///                        `{n}` inside stays literal. Multi-line raw strings strip the
///                        opening/closing newline and the closing delimiter's indentation.
///   - `$"""..."""`       raw-interpolated. Raw normalization (no escapes, dedent) AND
///                        contextual holes with Option-B brace collapse.
public static class StringLiteralLowering
{
    public enum Form { Regular, Interpolated, Raw, RawInterpolated }

    /// A decoded template fragment: literal text, or the source of an interpolation
    /// hole (the expression text between `{` and its matching `}`) for the binder to parse.
    public readonly record struct Segment(string? Text, string? HoleSource);

    /// Classify a raw string lexeme by its prefix and delimiter width.
    public static Form FormOf(string lexeme)
    {
        var dollar = lexeme.Length > 0 && lexeme[0] == '$';
        var body = dollar ? lexeme.AsSpan(1) : lexeme.AsSpan();
        var raw = body.Length >= 6 && body[0] == '"' && body[1] == '"' && body[2] == '"';
        return (dollar, raw) switch
        {
            (true, true) => Form.RawInterpolated,
            (false, true) => Form.Raw,
            (true, false) => Form.Interpolated,
            (false, false) => Form.Regular,
        };
    }

    /// Interpolation is active for every form except a bare `"""` raw string.
    public static bool Interpolates(Form form) => form is not Form.Raw;

    /// Decode a lexeme to its template text — delimiters stripped, backslash escapes
    /// applied (non-raw forms), raw forms newline/indent-normalized. Hole splitting
    /// runs separately, on this template, via <see cref="SplitInterpolation"/>.
    public static string DecodeTemplate(string lexeme, Form form) => form switch
    {
        Form.Raw or Form.RawInterpolated => NormalizeRaw(StripRawDelimiters(lexeme, form)),
        _ => DecodeEscapes(StripQuotes(lexeme, form)),
    };

    /// Convenience over <see cref="FormOf"/> + <see cref="DecodeTemplate"/>.
    public static string DecodeTemplate(string lexeme) => DecodeTemplate(lexeme, FormOf(lexeme));

    // ---- regular / interpolated: quotes + backslash escapes -----------------------

    static string StripQuotes(string lexeme, Form form)
    {
        var s = form is Form.Interpolated ? lexeme.AsSpan(1) : lexeme.AsSpan(); // drop `$`
        if (s.Length < 2) return string.Empty;
        return s[1..^1].ToString(); // drop the surrounding `"`
    }

    static string DecodeEscapes(string inner)
    {
        if (inner.IndexOf('\\') < 0) return inner;
        var sb = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\\' || i + 1 >= inner.Length) { sb.Append(inner[i]); continue; }
            i++;
            switch (inner[i])
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '0': sb.Append('\0'); break;
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                case '\'': sb.Append('\''); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case 'a': sb.Append('\a'); break;
                case 'u' when i + 4 < inner.Length:
                    sb.Append((char)Convert.ToInt32(inner.Substring(i + 1, 4), 16));
                    i += 4;
                    break;
                case 'x':
                {
                    var start = i + 1;
                    var n = 0;
                    while (n < 4 && i + 1 < inner.Length && Uri.IsHexDigit(inner[i + 1])) { i++; n++; }
                    sb.Append(n > 0 ? (char)Convert.ToInt32(inner.Substring(start, n), 16) : 'x');
                    break;
                }
                default: sb.Append(inner[i]); break;
            }
        }
        return sb.ToString();
    }

    // ---- raw strings: """-delimited, verbatim, indent-aware -----------------------

    /// Strip the opening/closing `"""` (or longer) fences, plus a leading `$`. The
    /// fence width is the run of `"` at the start; the same count closes it.
    static string StripRawDelimiters(string lexeme, Form form)
    {
        var s = form is Form.RawInterpolated ? lexeme.AsSpan(1) : lexeme.AsSpan(); // drop `$`
        var open = 0;
        while (open < s.Length && s[open] == '"') open++;
        var close = open; // closing fence is the same width
        if (s.Length < open + close) return string.Empty;
        return s[open..^close].ToString();
    }

    /// C# 11 raw-string normalization. A single-line raw string is verbatim. A
    /// multi-line one (content opens with a newline) drops that opening newline and the
    /// final newline, then removes the common indentation dictated by the whitespace on
    /// the closing-fence line — so the text dedents to where the author wrote it.
    static string NormalizeRaw(string content)
    {
        var nl = content.IndexOf('\n');
        if (nl < 0) return content; // single-line: verbatim between fences

        // Opening line (between the opening fence and the first newline) must be blank
        // for a multi-line raw string; drop it along with its newline.
        var afterOpen = nl + 1;
        // Find the last newline — the closing fence sits on its own line, so everything
        // after the final newline is that line's leading whitespace (the dedent unit).
        var lastNl = content.LastIndexOf('\n');
        var closingIndent = content[(lastNl + 1)..];
        var bodyEnd = lastNl; // drop the final newline + closing-indent run
        if (afterOpen > bodyEnd) return string.Empty;

        var body = content[afterOpen..bodyEnd];
        var lines = body.Split('\n');
        var sb = new StringBuilder(body.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            // Strip a trailing '\r' so CRLF sources normalize to the source's own breaks.
            var hasCr = line.EndsWith('\r');
            if (hasCr) line = line[..^1];
            var trimmed = line.StartsWith(closingIndent) ? line[closingIndent.Length..] : line.TrimStart();
            sb.Append(trimmed);
            if (hasCr) sb.Append('\r');
            if (i < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    // ---- interpolation: contextual holes + Option-B brace collapse ----------------

    /// A `{` opens a hole only before an identifier start, `(`, or `!`.
    public static bool IsHoleStart(char c) => char.IsLetter(c) || c == '_' || c == '(' || c == '!';

    /// True when the template carries at least one real hole (skipping escaped `{{`).
    public static bool HasHole(string template)
    {
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{') { i += 2; continue; }
                if (i + 1 < template.Length && IsHoleStart(template[i + 1])) return true;
            }
            i++;
        }
        return false;
    }

    /// Split a decoded template into literal-text and hole-source segments, applying
    /// Option-B brace collapse. Returns null when the template has no hole — the caller
    /// then treats the template as a plain constant string (braces verbatim). Brace
    /// escapes (`{{`/`}}` → `{`/`}`) fire only here, i.e. only for an interpolating
    /// string, so hole-free text keeps its braces.
    public static List<Segment>? SplitInterpolation(string template)
    {
        if (!HasHole(template)) return null;

        var segments = new List<Segment>();
        var literal = new StringBuilder();
        var i = 0;

        void Flush()
        {
            if (literal.Length > 0) { segments.Add(new Segment(literal.ToString(), null)); literal.Clear(); }
        }

        while (i < template.Length)
        {
            var c = template[i];

            // `{{` / `}}` collapse to a single literal brace.
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{') { literal.Append('{'); i += 2; continue; }
            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}') { literal.Append('}'); i += 2; continue; }

            // A `{` opens a hole only before an expression-start char; BCL placeholders
            // (`{0}`, `{0:d}`) fail the test and stay literal.
            if (c == '{' && i + 1 < template.Length && IsHoleStart(template[i + 1]))
            {
                Flush();
                // Match the closing `}` by brace depth, skipping nested string/char
                // literals so a `"`/`'` (and its braces) inside the hole never miscounts.
                var depth = 1;
                var j = i + 1;
                while (j < template.Length && depth > 0)
                {
                    var d = template[j];
                    if (d is '"' or '\'')
                    {
                        var quote = d;
                        j++;
                        while (j < template.Length && template[j] != quote)
                        {
                            if (template[j] == '\\' && j + 1 < template.Length) j++;
                            j++;
                        }
                        if (j < template.Length) j++; // closing quote
                        continue;
                    }
                    if (d == '{') depth++;
                    else if (d == '}') { depth--; if (depth == 0) break; }
                    j++;
                }
                segments.Add(new Segment(null, template[(i + 1)..j]));
                i = j + 1;
                continue;
            }

            literal.Append(c);
            i++;
        }

        Flush();
        return segments;
    }
}
