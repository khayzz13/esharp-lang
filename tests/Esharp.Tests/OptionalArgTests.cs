using System.Reflection;
using Esharp.Diagnostics;
using Esharp.Compilation;

namespace Esharp.Tests;

/// <summary>
/// Omitting a trailing C# optional parameter at an E# call site must fill it with the
/// callee's *declared* default value — not <c>default(T)</c>. Cecil's
/// <c>ImportReference(MethodInfo)</c> drops the optional-default metadata, so the imported
/// parameter's <c>HasConstant</c> was false and the emitter fell through to <c>initobj</c>
/// (= zero/false/null). The fix copies the defaults from reflection onto the imported
/// parameter definitions.
///
/// Exercised through the mixed `.es` + `.cs` path — the exact "E# calls a C# API and omits
/// a trailing optional" scenario from the ticket.
/// </summary>
public sealed class OptionalArgTests : IDisposable
{
    static int _asmCounter;
    readonly List<string> _tempDirs = new();

    (Assembly? Assembly, IReadOnlyList<Diagnostic> Diagnostics) CompileMixed(string esSource, string csSource)
    {
        var asmName = $"EsharpOpt_{Interlocked.Increment(ref _asmCounter)}";
        var workspace = new Workspace(asmName);
        workspace.AddDocument($"test_{asmName}.es", esSource);
        workspace.AddDocument($"test_{asmName}.cs", csSource);
        var tempDir = Path.Combine(Path.GetTempPath(), $"esharp_opt_{asmName}");
        Directory.CreateDirectory(tempDir);
        _tempDirs.Add(tempDir);
        var dllPath = Path.Combine(tempDir, $"{asmName}.dll");
        var result = workspace.CurrentCompilation.EmitToFile(dllPath, debugSymbols: false);
        if (!result.Success || !File.Exists(dllPath))
            return (null, result.Diagnostics);
        return (Assembly.LoadFrom(dllPath), result.Diagnostics);
    }

    object? RunMixed(string esSource, string csSource, string method, params object?[] args)
    {
        var (asm, diags) = CompileMixed(esSource, csSource);
        Assert.True(asm is not null, "compilation failed:\n" + string.Join("\n", diags.Select(d => d.ToString())));
        var type = asm!.GetType("Test.Test")!;
        var m = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        return m.Invoke(null, args.Length == 0 ? null : args);
    }

    const string Helpers = """
namespace Test;
public enum Mode { Off = 0, Fast = 2, Full = 5 }
public static class Lib
{
    public static int scale(int x, int factor = 10) => x * factor;
    public static bool flag(bool on = true) => on;
    public static string label(string s, string suffix = "!") => s + suffix;
    public static int two(int a = 3, int b = 4) => a * 10 + b;
    public static int withMode(int x, Mode m = Mode.Full) => x + (int)m;          // enum default
    public static string repeat(string s, char sep = '-') => s + sep + s;          // char default
    public static double half(double x, double factor = 0.5) => x * factor;        // double default
    public static int lenOr(string? s = null) => s == null ? -1 : s.Length;        // null ref default
    public static long big(long n = 5000000000) => n;                              // long default
}
public class Counter                                                                // instance + ctor optionals
{
    public int Value;
    public Counter(int start = 100) { Value = start; }
    public int bump(int by = 1) => Value + by;
}
""";

    [Fact]
    public void IntDefault_Used_When_Omitted()
    {
        // scale(5) omits factor (default 10) → 50, not 5*0 = 0.
        var es = """
namespace Test
func go() -> int = Lib.scale(5)
""";
        Assert.Equal(50, RunMixed(es, Helpers, "go"));
    }

    [Fact]
    public void IntDefault_Overridden_When_Passed()
    {
        var es = """
namespace Test
func go() -> int = Lib.scale(5, 3)
""";
        Assert.Equal(15, RunMixed(es, Helpers, "go"));
    }

    [Fact]
    public void BoolDefault_Used_When_Omitted()
    {
        // flag() omits on (default true) → true, not default(bool) = false.
        var es = """
namespace Test
func go() -> bool = Lib.flag()
""";
        Assert.Equal(true, RunMixed(es, Helpers, "go"));
    }

    [Fact]
    public void StringDefault_Used_When_Omitted()
    {
        var es = """
namespace Test
func go() -> string = Lib.label("hi")
""";
        Assert.Equal("hi!", RunMixed(es, Helpers, "go"));
    }

    [Fact]
    public void MultipleDefaults_AllOmitted()
    {
        var es = """
namespace Test
func go() -> int = Lib.two()
""";
        Assert.Equal(34, RunMixed(es, Helpers, "go"));   // 3*10 + 4
    }

    [Fact]
    public void MultipleDefaults_FirstPassed_SecondDefaulted()
    {
        var es = """
namespace Test
func go() -> int = Lib.two(7)
""";
        Assert.Equal(74, RunMixed(es, Helpers, "go"));   // 7*10 + 4 (b defaulted)
    }

    [Fact]
    public void EnumDefault_Used_When_Omitted()
    {
        // withMode(10) omits m (default Mode.Full = 5) → 15, not 10 + Mode.Off(0).
        var es = """
namespace Test
func go() -> int = Lib.withMode(10)
""";
        Assert.Equal(15, RunMixed(es, Helpers, "go"));
    }

    [Fact]
    public void CharDefault_Used_When_Omitted()
    {
        var es = """
namespace Test
func go() -> string = Lib.repeat("ab")
""";
        Assert.Equal("ab-ab", RunMixed(es, Helpers, "go"));
    }

    [Fact]
    public void DoubleDefault_Used_When_Omitted()
    {
        var es = """
namespace Test
func go() -> double = Lib.half(8.0)
""";
        Assert.Equal(4.0, RunMixed(es, Helpers, "go"));
    }

    [Fact]
    public void NullReferenceDefault_Used_When_Omitted()
    {
        // lenOr() omits s (default null) → -1, proving the default really is null.
        var es = """
namespace Test
func go() -> int = Lib.lenOr()
""";
        Assert.Equal(-1, RunMixed(es, Helpers, "go"));
    }

    [Fact]
    public void LongDefault_Used_When_Omitted()
    {
        var es = """
namespace Test
func go() -> long = Lib.big()
""";
        Assert.Equal(5000000000L, RunMixed(es, Helpers, "go"));
    }

    [Fact]
    public void ConstructorDefault_Used_When_Omitted()
    {
        // new Counter() omits start (default 100) → Value == 100, not default(int) 0.
        var es = """
namespace Test
func go() -> int {
    let c = Counter()
    return c.Value
}
""";
        Assert.Equal(100, RunMixed(es, Helpers, "go"));
    }

    [Fact]
    public void InstanceMethodDefault_Used_When_Omitted()
    {
        // bump() omits by (default 1) on a Counter started at 100 → 101.
        var es = """
namespace Test
func go() -> int {
    let c = Counter(100)
    return c.bump()
}
""";
        Assert.Equal(101, RunMixed(es, Helpers, "go"));
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
            try { Directory.Delete(d, recursive: true); } catch { }
    }
}
