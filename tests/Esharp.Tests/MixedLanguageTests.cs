using System.Reflection;
using Esharp.Diagnostics;
using Esharp.Compilation;

namespace Esharp.Tests;

// Vertical-slice tests for mixed `.es` + `.cs` compilation. Each test feeds an
// .es source and a .cs source through the Workspace, asserts the combined
// build succeeds, then loads the resulting assembly and exercises the
// cross-language reference at runtime.
//
// CompileMixed helper writes both sources to a temp directory, drives the
// Workspace through Compilation.EmitToFile, and loads the resulting assembly.
// Diagnostics from both halves come back in one list so tests can assert on
// either ES-codes (E#) or CS-codes (Roslyn).
public sealed partial class MixedLanguageTests : IDisposable
{
    static int _asmCounter;
    readonly List<string> _tempDirs = new();

    (Assembly Assembly, IReadOnlyList<Diagnostic> Diagnostics) CompileMixed(string esSource, string csSource)
    {
        var asmName = $"EsharpMixed_{Interlocked.Increment(ref _asmCounter)}";
        var workspace = new Workspace(asmName);
        if (!string.IsNullOrEmpty(esSource))
            workspace.AddDocument($"test_{asmName}.es", esSource);
        if (!string.IsNullOrEmpty(csSource))
            workspace.AddDocument($"test_{asmName}.cs", csSource);

        var tempDir = Path.Combine(Path.GetTempPath(), $"esharp_mixed_{asmName}");
        Directory.CreateDirectory(tempDir);
        _tempDirs.Add(tempDir);
        var dllPath = Path.Combine(tempDir, $"{asmName}.dll");

        var result = workspace.CurrentCompilation.EmitToFile(dllPath, debugSymbols: false);

        if (!result.Success || !File.Exists(dllPath))
            return (null!, result.Diagnostics);
        return (Assembly.LoadFrom(dllPath), result.Diagnostics);
    }

    static object? Invoke(Assembly asm, string fullName, string methodName, params object?[] args)
    {
        var type = asm.GetType(fullName)
            ?? throw new Exception($"Type {fullName} not found in {asm.FullName}");
        var method = type.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            ?? throw new Exception($"Method {fullName}.{methodName} not found");
        return method.Invoke(null, args);
    }

    // ============================================================
    // Vertical slice — one end-to-end test proving the architecture
    // ============================================================

    [Fact]
    public void CSharp_Class_Field_Read_From_Esharp_Function()
    {
        var es = """
namespace Test

func (v: Vec) sum() -> int = v.X + v.Y
""";
        var cs = """
namespace Test;

public class Vec
{
    public int X;
    public int Y;
    public Vec(int x, int y) { X = x; Y = y; }
}
""";
        var (asm, diags) = CompileMixed(es, cs);
        Assert.True(asm is not null, $"compilation failed: {string.Join("\n", diags.Select(d => d.ToString()))}");

        var vecType = asm!.GetType("Test.Vec");
        Assert.NotNull(vecType);
        var vec = Activator.CreateInstance(vecType!, 3, 4);

        var result = Invoke(asm, "Test.Test", "sum", vec);
        Assert.Equal(7, result);
    }

    [Fact]
    public void CSharp_Instance_Method_Called_From_Esharp()
    {
        var es = """
namespace Test

func (g: Greeter) describe() -> string = g.Hello("world")
""";
        var cs = """
namespace Test;

public class Greeter
{
    public string Hello(string who) => "hi " + who;
}
""";
        var (asm, diags) = CompileMixed(es, cs);
        Assert.True(asm is not null, $"compilation failed: {string.Join("\n", diags.Select(d => d.ToString()))}");
        var greeterType = asm!.GetType("Test.Greeter");
        var greeter = Activator.CreateInstance(greeterType!);
        Assert.Equal("hi world", Invoke(asm, "Test.Test", "describe", greeter));
    }

    [Fact]
    public void CSharp_Static_Method_Called_From_Esharp()
    {
        var es = """
namespace Test

func double_it(x: int) -> int = MathHelper.Twice(x)
""";
        var cs = """
namespace Test;

public static class MathHelper
{
    public static int Twice(int n) => n * 2;
}
""";
        var (asm, diags) = CompileMixed(es, cs);
        Assert.True(asm is not null, $"compilation failed: {string.Join("\n", diags.Select(d => d.ToString()))}");
        Assert.Equal(14, Invoke(asm, "Test.Test", "double_it", 7));
    }

    [Fact]
    public void CSharp_Property_Read_From_Esharp()
    {
        var es = """
namespace Test

func (p: Person) name_of() -> string = p.Name
""";
        var cs = """
namespace Test;

public class Person
{
    public string Name { get; set; } = "anon";
}
""";
        var (asm, diags) = CompileMixed(es, cs);
        Assert.True(asm is not null, $"compilation failed: {string.Join("\n", diags.Select(d => d.ToString()))}");
        var personType = asm!.GetType("Test.Person");
        var person = Activator.CreateInstance(personType!);
        personType!.GetProperty("Name")!.SetValue(person, "alice");
        Assert.Equal("alice", Invoke(asm, "Test.Test", "name_of", person));
    }

    [Fact]
    // `valueOf` is a FREE function, not a method on Box. Promotion is gated on E#-declared
    // `data`/`class` (IsPromotedInstanceFunction → DataDecls), so a function whose first
    // parameter is an external C# class never attaches as a method — hence no `partial` Box and
    // no method injection into the foreign type. It's called free (`Test.valueOf(box)`), and
    // `box.valueOf()` would not resolve.
    public void CSharp_Class_Constructed_In_Esharp()
    {
        var es = """
namespace Test

func make(n: int) -> Box = Box(n)

func (b: Box) valueOf() -> int = b.N
""";
        var cs = """
namespace Test;

public class Box
{
    public int N;
    public Box(int n) { N = n; }
}
""";
        var (asm, diags) = CompileMixed(es, cs);
        Assert.True(asm is not null, $"compilation failed: {string.Join("\n", diags.Select(d => d.ToString()))}");
        var made = Invoke(asm, "Test.Test", "make", 42);
        Assert.Equal(42, Invoke(asm, "Test.Test", "valueOf", made));
    }

    [Fact]
    public void CSharp_Enum_Switch_In_Esharp()
    {
        var es = """
namespace Test

func label(c: Color) -> string {
    if c == Color.Red {
        return "red"
    }
    if c == Color.Green {
        return "green"
    }
    return "other"
}
""";
        var cs = """
namespace Test;

public enum Color { Red = 0, Green = 1, Blue = 2 }
""";
        var (asm, diags) = CompileMixed(es, cs);
        Assert.True(asm is not null, $"compilation failed: {string.Join("\n", diags.Select(d => d.ToString()))}");
        Assert.Equal("red", Invoke(asm, "Test.Test", "label", 0));
        Assert.Equal("green", Invoke(asm, "Test.Test", "label", 1));
        Assert.Equal("other", Invoke(asm, "Test.Test", "label", 2));
    }

    [Fact]
    public void Esharp_Func_Called_From_Csharp()
    {
        var es = """
namespace Test

func triple(n: int) -> int = n * 3
""";
        var cs = """
namespace Test;

public static class Caller
{
    public static int Use(int x) => global::Test.Test.triple(x) + 1;
}
""";
        var (asm, diags) = CompileMixed(es, cs);
        Assert.True(asm is not null, $"compilation failed: {string.Join("\n", diags.Select(d => d.ToString()))}");
        var result = Invoke(asm, "Test.Caller", "Use", 5);
        Assert.Equal(16, result);
    }

    [Fact]
    public void CSharp_Interface_Implemented_By_Esharp_Data()
    {
        // E# `struct Greeter : IDescribable {}` plus a promoted top-level
        // `func describe(g: Greeter)` should satisfy the C#-defined interface.
        // This currently fails because the IL emitter doesn't synthesize the
        // method-impl bridge from the promoted func to the C# interface slot —
        // existing machinery covers E# interfaces only. Failing-on-purpose
        // as a load-bearing TODO until cross-language promotion lands.
        var es = """
namespace Test

struct Greeter : IDescribable {}

func (g: Greeter) describe() -> string = "greeter"
""";
        var cs = """
namespace Test;

public interface IDescribable { string describe(); }
""";
        var (asm, diags) = CompileMixed(es, cs);
        Assert.True(asm is not null, $"compilation failed: {string.Join("\n", diags.Select(d => d.ToString()))}");
        var iface = asm!.GetType("Test.IDescribable");
        Assert.NotNull(iface);
        var greeter = asm.GetType("Test.Greeter");
        Assert.NotNull(greeter);
        Assert.Contains(iface!, greeter!.GetInterfaces());
        // Construct a Greeter and call describe through the C# interface contract.
        var instance = Activator.CreateInstance(greeter!);
        Assert.NotNull(instance);
        var describe = iface!.GetMethod("describe")!;
        Assert.Equal("greeter", describe.Invoke(instance, null));
    }

    [Fact]
    public void CSharp_Ctor_With_Multiple_Args_Constructed_In_Esharp()
    {
        var es = """
namespace Test

func make(x: int, y: int, label: string) -> Rect = Rect(x, y, label)

func (r: Rect) area() -> int = r.X * r.Y
""";
        var cs = """
namespace Test;

public class Rect
{
    public int X;
    public int Y;
    public string Label;
    public Rect(int x, int y, string label) { X = x; Y = y; Label = label; }
}
""";
        var (asm, diags) = CompileMixed(es, cs);
        Assert.True(asm is not null, $"compilation failed: {string.Join("\n", diags.Select(d => d.ToString()))}");
        var rect = Invoke(asm, "Test.Test", "make", 4, 5, "size");
        Assert.Equal(20, Invoke(asm, "Test.Test", "area", rect));
    }

    [Fact]
    public void Esharp_Data_Type_Used_From_Csharp()
    {
        var es = """
namespace Test

pub struct Point { x: int, y: int }
""";
        var cs = """
namespace Test;

public static class Util
{
    public static int Sum(Point p) => p.x + p.y;
}
""";
        var (asm, diags) = CompileMixed(es, cs);
        Assert.True(asm is not null, $"compilation failed: {string.Join("\n", diags.Select(d => d.ToString()))}");
        var pointType = asm!.GetType("Test.Point");
        Assert.NotNull(pointType);
        var p = Activator.CreateInstance(pointType!);
        pointType!.GetField("x")!.SetValue(p, 3);
        pointType!.GetField("y")!.SetValue(p, 4);
        Assert.Equal(7, Invoke(asm, "Test.Util", "Sum", p));
    }

    [Fact]
    public void Esharp_OutParam_BoundByCsharp_OutVar()
    {
        // The drop-in shape: an E# function with an `out` parameter, consumed by
        // C# via `out var`. Roslyn binds the emitted `[Out] int&` exactly as it
        // would a C# out-parameter — proving PageNavigator-style call sites work.
        var es = """
namespace Test

func try_parse(input: int, out doubled: int) -> bool {
    doubled = input * 2
    return true
}
""";
        var cs = """
namespace Test;

public static class Caller
{
    public static int Use(int n)
    {
        if (global::Test.Test.try_parse(n, out var d)) return d;
        return -1;
    }
}
""";
        var (asm, diags) = CompileMixed(es, cs);
        Assert.True(asm is not null, $"compilation failed: {string.Join("\n", diags.Select(d => d.ToString()))}");
        Assert.Equal(42, Invoke(asm!, "Test.Caller", "Use", 21));
    }

    public void Dispose()
    {
        // Keep temp dirs around for now so we can inspect failed builds.
    }
}
