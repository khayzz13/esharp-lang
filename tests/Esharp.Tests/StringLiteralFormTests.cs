namespace Esharp.Tests;

/// The E# string-literal family lowered by <c>StringLiteralLowering</c>: regular,
/// `$`-interpolated, `"""` raw, and `$"""` raw-interpolated. Regular strings keep E#'s
/// contextual interpolation; raw strings are verbatim (no escapes, no holes) unless
/// prefixed with `$`. These are the runtime-value facts; brace-escape semantics live in
/// <see cref="InterpolationBraceTests"/>.
public sealed class StringLiteralFormTests
{
    static string Str(string body) =>
        (string)EsHarness.Run("namespace Test\nfunc go() -> string {\n" + body + "\n}", "go")!;

    // ---- `$` interpolation prefix --------------------------------------------------

    [Fact]
    public void DollarPrefix_Interpolates()
    {
        // `$"…"` reads as C#; E#'s holes are already contextual, so it decodes identically
        // to a bare string — the prefix documents intent.
        Assert.Equal("v=7", Str("let n = 7\nreturn $\"v={n}\""));
    }

    [Fact]
    public void DollarPrefix_BraceEscapesWithHole()
    {
        Assert.Equal("{x}=7", Str("let n = 7\nreturn $\"{{x}}={n}\""));
    }

    // ---- `"""` raw strings (verbatim) ---------------------------------------------

    [Fact]
    public void RawString_IsVerbatim_NoEscapes()
    {
        // Backslashes and quotes are literal — a regex / Windows path needs no escaping.
        Assert.Equal("a\\d+\tb\"c", Str("return \"\"\"a\\d+\tb\"c\"\"\""));
    }

    [Fact]
    public void RawString_BracesAreLiteral_NoInterpolation()
    {
        // A `{n}` inside a bare raw string stays literal — raw strings do not interpolate.
        Assert.Equal("{\"k\":{n}}", Str("return \"\"\"{\"k\":{n}}\"\"\""));
    }

    [Fact]
    public void RawString_EmbeddedDoubleQuotes()
    {
        // Up to (fence-1) consecutive quotes are content; the JSON keeps its `"`s.
        Assert.Equal("{\"a\":\"b\"}", Str("return \"\"\"{\"a\":\"b\"}\"\"\""));
    }

    [Fact]
    public void RawString_MultiLine_StripsIndentAndFenceNewlines()
    {
        // The closing fence's indentation is removed from every line; the opening and
        // closing newlines are dropped. Authored indented, the value dedents to column 0.
        var src = "namespace Test\nfunc go() -> string {\n    return \"\"\"\n        line one\n        line two\n        \"\"\"\n}";
        Assert.Equal("line one\nline two", (string)EsHarness.Run(src, "go")!);
    }

    // ---- `$"""` raw-interpolated ---------------------------------------------------

    [Fact]
    public void RawInterpolated_HasHoles()
    {
        // The `$` turns interpolation back on inside a raw string — braces around an
        // expression-start char open a hole; other braces stay literal.
        Assert.Equal("{\"k\":9}", Str("let v = 9\nreturn $\"\"\"{\"k\":{v}}\"\"\""));
    }
}
