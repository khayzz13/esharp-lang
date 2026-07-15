using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

public sealed class PdbTests
{
    static int _asmCounter;

    static string CompileToFile(string source, bool debugSymbols = true)
    {
        var asmName = $"PdbTest_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        var diags = EsHarness.EmitBoundToFile(binder, bound, asmName, path, debugSymbols: debugSymbols);
        Assert.Empty(diags.Where(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error));
        return path;
    }

    [Fact]
    public void DebugSymbols_ProducesPdbFile()
    {
        const string source = """
namespace Test

func run() -> int {
    return 42
}
""";
        var dllPath = CompileToFile(source, debugSymbols: true);
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        Assert.True(File.Exists(pdbPath), $"PDB file should exist at {pdbPath}");
        Assert.True(new FileInfo(pdbPath).Length > 0, "PDB file should not be empty");
    }

    [Fact]
    public void NoDebugSymbols_NoPdbFile()
    {
        const string source = """
namespace Test

func run() -> int {
    return 42
}
""";
        var dllPath = CompileToFile(source, debugSymbols: false);
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        Assert.False(File.Exists(pdbPath), "PDB file should not exist when debugSymbols=false");
    }

    [Fact]
    public void DebugSymbols_DllStillRunsCorrectly()
    {
        const string source = """
namespace Test

func add(a: int, b: int) -> int {
    var result = a + b
    return result
}
""";
        var dllPath = CompileToFile(source, debugSymbols: true);
        var asm = System.Reflection.Assembly.LoadFrom(dllPath);
        var type = asm.GetType("Test.Test")!;
        var method = type.GetMethod("add", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var result = method.Invoke(null, [3, 4]);
        Assert.Equal(7, result);
    }

    [Fact]
    public void Pdb_HasSequencePoints()
    {
        const string source = """
namespace Test

func compute(x: int) -> int {
    var a = x + 1
    var b = a * 2
    return b
}
""";
        var dllPath = CompileToFile(source, debugSymbols: true);
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");

        // Read the PDB back via Cecil and verify sequence points exist
        using var asm = Mono.Cecil.AssemblyDefinition.ReadAssembly(dllPath, new Mono.Cecil.ReaderParameters
        {
            ReadSymbols = true,
            SymbolReaderProvider = new Mono.Cecil.Cil.DefaultSymbolReaderProvider(throwIfNoSymbol: false),
        });

        var module = asm.MainModule;
        var hasSequencePoints = false;
        foreach (var type in module.Types)
        {
            foreach (var method in type.Methods)
            {
                if (method.DebugInformation?.SequencePoints?.Count > 0)
                {
                    hasSequencePoints = true;
                    // Verify sequence points reference the source file
                    foreach (var sp in method.DebugInformation.SequencePoints)
                    {
                        Assert.Contains("test.es", sp.Document.Url);
                        Assert.True(sp.StartLine > 0, "Sequence point should have a valid line number");
                    }
                }
            }
        }

        Assert.True(hasSequencePoints, "PDB should contain at least one method with sequence points");
    }
}
