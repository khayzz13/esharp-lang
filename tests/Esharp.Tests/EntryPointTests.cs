using System.Reflection;
using Esharp.Diagnostics;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

/// <summary>
/// A program is entered through `main` in one of two shapes: a bare top-level
/// <c>func main</c> (function-style), or a <c>main</c> method on a <c>class Program</c>
/// (class-style — "my program is an object"). Both set the assembly entry point when
/// compiled as an executable; the class-style needs NO hand-written
/// <c>func main() = Program().main()</c> launcher — the compiler synthesizes
/// <c>Program().main()</c> as the entry. Both forms also accept the normal CLR
/// <c>main(args: string[])</c> signature, so a tiny CLI does not need to reach for
/// <c>Environment.GetCommandLineArgs()</c>. A library compile sets no entry point either way.
/// </summary>
public sealed class EntryPointTests
{
    static int _n;

    static (System.Reflection.Assembly asm, Mono.Cecil.MethodReference? entry) CompileExe(string source)
    {
        var name = $"EsEntry_{Interlocked.Increment(ref _n)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        var (assembly, diags) = EsHarness.EmitBound(binder, 
            new[] { bound }, name, debugSymbols: false, referencePaths: null,
            internalsVisibleTo: null, externalSymbols: null,
            outputKind: ILOutputKind.Console, implicitUsings: true);
        Assert.Empty(diags.Where(d => d.Severity == DiagnosticSeverity.Error));
        var entry = assembly.EntryPoint;
        var path = Path.Combine(Path.GetTempPath(), $"{name}.dll");
        assembly.Write(path);
        return (System.Reflection.Assembly.LoadFrom(path), entry);
    }

    static Mono.Cecil.MethodReference? CompileLibEntry(string source)
    {
        var name = $"EsLib_{Interlocked.Increment(ref _n)}";
        var parser = new Parser(source, "test.es");
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(parser.ParseCompilationUnit());
        var (assembly, _) = EsHarness.EmitBound(binder, new[] { bound }, name);   // Library default
        return assembly.EntryPoint;
    }

    const string ClassStyle = """
namespace Test
func compute(n: int) -> int = n * 7
class Program {
    func main() -> int {
        return compute(6)
    }
}
""";

    const string FunctionStyle = """
namespace Test
func compute(n: int) -> int = n * 7
func main() -> int {
    return compute(6)
}
""";

    // Deliberately a five-line CLI. Its entry method is directly the CLR entry
    // point, so the runtime supplies user arguments without the executable path.
    const string FunctionArgsStyle = """
namespace Test
func main(args: string[]) -> int {
    return args.Length * 7
}
""";

    const string ClassArgsStyle = """
namespace Test
class Program {
    init() { }
    func main(args: string[]) -> int {
        return args.Length * 9
    }
}
""";

    const string StaticFacetArgsStyle = """
namespace Test
static Cli {
    func main(args: string[]) -> int {
        return args.Length * 11
    }
}
""";

    [Fact]
    public void ClassStyle_SetsEntryPoint()
    {
        var (_, entry) = CompileExe(ClassStyle);
        Assert.NotNull(entry);
        Assert.Equal("Program", entry!.DeclaringType.Name);   // synthesized <Main> hosted on Program
    }

    [Fact]
    public void ClassStyle_EntryRuns_ConstructsAndCallsMain()
    {
        var (asm, entry) = CompileExe(ClassStyle);
        var m = asm.EntryPoint!;   // reflection entry mirrors the Cecil entry
        Assert.NotNull(m);
        Assert.Equal(42, m!.Invoke(null, null));   // Program().main() → compute(6) → 42
    }

    [Fact]
    public void FunctionStyle_SetsEntryPoint()
    {
        var (asm, entry) = CompileExe(FunctionStyle);
        Assert.NotNull(entry);
        Assert.Equal(42, asm.EntryPoint!.Invoke(null, null));
    }

    [Fact]
    public void FunctionStyle_ArgsMain_IsDirectCliEntryPoint()
    {
        var (asm, entry) = CompileExe(FunctionArgsStyle);
        Assert.NotNull(entry);
        Assert.Equal("main", entry!.Name);
        Assert.Single(entry.Parameters);
        Assert.Equal("System.String[]", entry.Parameters[0].ParameterType.FullName);
        Assert.Equal(21, asm.EntryPoint!.Invoke(null, [new[] { "one", "two", "three" }]));
    }

    [Fact]
    public void ClassStyle_ArgsMain_WrapperForwardsCliArguments()
    {
        var (asm, entry) = CompileExe(ClassArgsStyle);
        Assert.NotNull(entry);
        Assert.Equal("Program", entry!.DeclaringType.Name);
        Assert.Single(entry.Parameters);
        Assert.Equal("System.String[]", entry.Parameters[0].ParameterType.FullName);
        Assert.Equal(27, asm.EntryPoint!.Invoke(null, [new[] { "a", "b", "c" }]));
    }

    [Fact]
    public void StaticFacet_ArgsMain_IsCliEntryPoint()
    {
        var (asm, entry) = CompileExe(StaticFacetArgsStyle);
        Assert.NotNull(entry);
        Assert.Equal("Cli", entry!.DeclaringType.Name);
        Assert.Equal("main", entry.Name);
        Assert.Single(entry.Parameters);
        Assert.Equal("System.String[]", entry.Parameters[0].ParameterType.FullName);
        Assert.Equal(22, asm.EntryPoint!.Invoke(null, [new[] { "input", "output" }]));
    }

    [Fact]
    public void StaticFacet_ParameterlessMain_IsCliEntryPoint()
    {
        var (asm, entry) = CompileExe("""
namespace Test
static Cli {
    func main() -> int = 37
}
""");
        Assert.NotNull(entry);
        Assert.Equal("Cli", entry!.DeclaringType.Name);
        Assert.Equal("main", entry.Name);
        Assert.Empty(entry.Parameters);
        Assert.Equal(37, asm.EntryPoint!.Invoke(null, null));
    }

    [Fact]
    public void ClassStyle_NoLauncherNeeded_HasNoTopLevelMain()
    {
        // The module class carries no `main` — the entry is purely the class method.
        var (asm, _) = CompileExe(ClassStyle);
        var modClass = asm.GetType("Test.Test");
        Assert.Null(modClass?.GetMethod("main", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
    }

    [Fact]
    public void LibraryCompile_SetsNoEntryPoint()
    {
        // As a library, `main` (either shape) is just a method — no entry point.
        Assert.Null(CompileLibEntry(ClassStyle));
        Assert.Null(CompileLibEntry(FunctionStyle));
    }
}
