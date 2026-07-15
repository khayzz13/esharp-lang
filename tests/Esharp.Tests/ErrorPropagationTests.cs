namespace Esharp.Tests;

/// <summary>
/// The `?` propagation operator works in *any* expression position, not just
/// <c>let x = expr?</c>. Previously `?` was special-cased only in the let-declaration
/// path, so <c>return f()?</c>, <c>g(f()?)</c>, and a bare <c>f()?</c> statement emitted
/// unbalanced IL (StackUnderflow). The emitter now lowers `expr?` as a value-producing
/// expression: propagate the error out of the enclosing function, else leave the
/// unwrapped value on the stack.
/// </summary>
public sealed class ErrorPropagationTests
{
    // Invoke a function returning Result<T,string> and read (isOk, value, error).
    static (bool ok, object? value, object? error) Run(string src, string method = "go", params object?[] args)
    {
        var r = EsHarness.Run(src, method, args)!;
        var ok = (bool)EsHarness.ResultMember(r, "IsOk")!;
        return (ok, ok ? EsHarness.ResultMember(r, "Value") : null, ok ? null : EsHarness.ResultMember(r, "Error"));
    }

    const string Helper = """
namespace Test
func parse(n: int) -> Result<int, string> {
    if n < 0 { return error("negative: {n}") }
    return ok(n * 2)
}
""";

    [Fact]
    public void Question_InLetPosition_Ok()
    {
        var (ok, value, _) = Run(Helper + "\nfunc go() -> Result<int, string> {\n let x = parse(5)?\n return ok(x + 1) }");
        Assert.True(ok);
        Assert.Equal(11, value);   // 5*2 + 1
    }

    [Fact]
    public void Question_InReturnPosition_Ok()
    {
        var (ok, value, _) = Run(Helper + "\nfunc go() -> Result<int, string> { return ok(parse(5)? + 1) }");
        Assert.True(ok);
        Assert.Equal(11, value);
    }

    [Fact]
    public void Question_InReturnPosition_PropagatesError()
    {
        var (ok, _, error) = Run(Helper + "\nfunc go() -> Result<int, string> { return ok(parse(-3)? + 1) }");
        Assert.False(ok);
        Assert.Equal("negative: -3", error);
    }

    [Fact]
    public void Question_AsBareStatement_Ok_Continues()
    {
        // The unwrapped value is discarded; execution proceeds to the next statement.
        var (ok, value, _) = Run(Helper + "\nfunc go() -> Result<int, string> {\n parse(5)?\n return ok(99) }");
        Assert.True(ok);
        Assert.Equal(99, value);
    }

    [Fact]
    public void Question_AsBareStatement_Error_Propagates()
    {
        var (ok, _, error) = Run(Helper + "\nfunc go() -> Result<int, string> {\n parse(-1)?\n return ok(99) }");
        Assert.False(ok);
        Assert.Equal("negative: -1", error);
    }

    [Fact]
    public void Question_AsCallArgument()
    {
        // A single `?` in argument position: parse(3)? evaluates first (clean stack),
        // unwraps to 6, then 100 is pushed and add() runs. (Two *sibling* `?` in one
        // expression — add(f()?, g()?) — needs operand spilling and is tracked in the
        // try-unwrap-spill ticket; none of the corpus examples hit it.)
        var src = Helper + """

func add(a: int, b: int) -> int = a + b
func go() -> Result<int, string> {
    return ok(add(parse(3)?, 100))   // 6 + 100
}
""";
        var (ok, value, _) = Run(src);
        Assert.True(ok);
        Assert.Equal(106, value);
    }

    [Fact]
    public void Question_Nested()
    {
        // parse(parse(3)?)  →  parse(6)  →  12
        var (ok, value, _) = Run(Helper + "\nfunc go() -> Result<int, string> { return ok(parse(parse(3)?)?) }");
        Assert.True(ok);
        Assert.Equal(12, value);
    }

    [Fact]
    public void Question_ChainedStatements_StopAtFirstError()
    {
        var src = Helper + """

func go() -> Result<int, string> {
    parse(1)?       // ok, discarded
    parse(-9)?      // error here — propagates, the rest never runs
    return ok(0)
}
""";
        var (ok, _, error) = Run(src);
        Assert.False(ok);
        Assert.Equal("negative: -9", error);
    }
}

/// <summary>
/// `for (k, v) in dict` destructures a <c>KeyValuePair&lt;K, V&gt;</c> via its Key/Value
/// getters — not ValueTuple's Item1/Item2. The old lowering hard-coded ItemN, missed on a
/// KeyValuePair, and fell through to `ldnull` (a null reference where the value type was
/// expected).
/// </summary>
public sealed class DictionaryDestructureTests
{
    static int Int(string src) => (int)EsHarness.Run(src, "go")!;
    static string Str(string src) => (string)EsHarness.Run(src, "go")!;

    [Fact]
    public void ForKV_SumsValues()
    {
        Assert.Equal(60, Int("""
namespace Test
using "System.Collections.Generic"
func go() -> int {
    let d = Dictionary<string, int>()
    d["a"] = 10
    d["b"] = 20
    d["c"] = 30
    var sum = 0
    for (k, v) in d {
        sum += v
    }
    return sum
}
"""));
    }

    [Fact]
    public void ForKV_UsesKey()
    {
        Assert.Equal(3, Int("""
namespace Test
using "System.Collections.Generic"
func go() -> int {
    let d = Dictionary<string, int>()
    d["xy"] = 1
    d["z"] = 1
    var totalKeyLen = 0
    for (k, v) in d {
        totalKeyLen += k.Length
    }
    return totalKeyLen
}
"""));
    }

    [Fact]
    public void ForKV_MaxByValue()
    {
        Assert.Equal("b", Str("""
namespace Test
using "System.Collections.Generic"
func go() -> string {
    let d = Dictionary<string, int>()
    d["a"] = 3
    d["b"] = 9
    d["c"] = 5
    var best = ""
    var bestN = 0
    for (k, v) in d {
        if v > bestN {
            bestN = v
            best = k
        }
    }
    return best
}
"""));
    }
}
