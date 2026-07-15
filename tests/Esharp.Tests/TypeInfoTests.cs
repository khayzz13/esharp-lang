using System.Reflection;
using Esharp.Diagnostics;
using Esharp.Syntax.Parsing;
using Esharp.Symbols;
using Esharp.BoundTree;        // BoundFunctionDeclaration, BoundParameter, type nodes
using Esharp.CodeGen;
using Binder = Esharp.Binder.Binder;
using IMemberSynthesizer = Esharp.Binder.IMemberSynthesizer;  // aliased — bare `using Esharp.Binder;` shadows the Binder type alias
using CompilationData = Esharp.Binder.CompilationData;
using TypeInfo = Esharp.Symbols.TypeInfo;

namespace Esharp.Tests;

/// TypeInfo is the read-only introspection projection of a TypeSymbol; the
/// member-emission seam (IMemberSynthesizer) adds members into a deriving type
/// early enough that interface satisfaction and the emitters see them like
/// promoted methods. These pin both against the IL backend — a synthesized member
/// compiles, verifies, and is invocable.
public sealed class TypeInfoTests
{
    static int _counter;

    // A data type's fields, kind, and classification project correctly.
    [Fact]
    public void TypeInfo_ProjectsFieldsKindClassification()
    {
        var data = new CompilationData();
        var parser = new Parser("""
namespace Test
struct Point { x: int, y: float }
""", "ti.es");
        new Esharp.Binder.Binder(data).Bind(parser.ParseCompilationUnit());

        var sym = data.Symbols.FindType("Point", TypeSymbolKind.Struct, TypeSymbolKind.Class)!;
        var info = TypeInfo.From(sym);

        Assert.Equal("Point", info.Name);
        Assert.Equal("Test", info.Namespace);
        Assert.Equal(TypeSymbolKind.Struct, info.Kind);
        Assert.Equal(DataClassification.Struct, info.Classification);
        Assert.Equal(2, info.Fields.Count);
        Assert.Equal("x", info.Fields[0].Name);
        Assert.Equal("int", info.Fields[0].TypeDisplay);
        Assert.Equal("y", info.Fields[1].Name);
        Assert.Equal("float", info.Fields[1].TypeDisplay);
        Assert.Equal(1, TypeInfo.Version); // contract carries a stable version
    }

    // A choice type projects its cases (and enum values).
    [Fact]
    public void TypeInfo_ProjectsEnumCasesWithValues()
    {
        var data = new CompilationData();
        var parser = new Parser("""
namespace Test
enum Color { Red, Green = 5, Blue }
""", "ti.es");
        new Esharp.Binder.Binder(data).Bind(parser.ParseCompilationUnit());

        var sym = data.Symbols.FindType("Color", TypeSymbolKind.Enum)!;
        var info = TypeInfo.From(sym);
        Assert.Equal(TypeSymbolKind.Enum, info.Kind);
        Assert.Collection(info.Cases,
            c => { Assert.Equal("Red", c.Name); Assert.Equal(0, c.Value); },
            c => { Assert.Equal("Green", c.Name); Assert.Equal(5, c.Value); },
            c => { Assert.Equal("Blue", c.Name); Assert.Equal(6, c.Value); });
    }

    // A synthesizer that adds a trivial instance method onto a data type: the
    // member compiles through the IL backend (verify:true) and is invocable.
    [Fact]
    public void MemberSynthesizer_AddedMethod_CompilesAndRuns()
    {
        var asm = CompileWithSynthesizer("""
namespace Test
struct Box { v: int }
func go() -> int {
    let b = Box { v: 7 }
    return b.answer()
}
""", new AnswerSynthesizer(targetType: "Box"));

        var result = Invoke(asm, "Test", "Test", "go");
        Assert.Equal(42, result);
    }

    // A synthesized member satisfies an interface conformance: the data declares
    // `: IAnswer` but never writes `answer` — the synthesizer fills it, and the
    // type compiles + dispatches through the interface slot.
    [Fact]
    public void MemberSynthesizer_SatisfiesInterfaceConformance()
    {
        var asm = CompileWithSynthesizer("""
namespace Test
interface IAnswer { func answer() -> int }
struct Box : IAnswer { v: int }
func go() -> int {
    let b = Box { v: 1 }
    return b.answer()
}
""", new AnswerSynthesizer(targetType: "Box"));

        Assert.Equal(42, Invoke(asm, "Test", "Test", "go"));
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    static Assembly CompileWithSynthesizer(string source, IMemberSynthesizer synth)
    {
        var data = new CompilationData { MemberSynthesizer = synth };
        var parser = new Parser(source, "ti.es");
        var unit = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var binder = new Esharp.Binder.Binder(data);
        var bound = binder.Bind(unit);
        var errors = binder.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => e.ToString())));

        var asmName = $"TypeSeam_{Interlocked.Increment(ref _counter)}";
        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        var emitDiags = EsHarness.EmitBoundToFile(binder, bound, asmName, path, verify: true);
        var emitErrors = emitDiags.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(emitErrors.Count == 0, string.Join("\n", emitErrors.Select(e => e.ToString())));
        return Assembly.LoadFrom(path);
    }

    static object? Invoke(Assembly asm, string typeName, string clrTypeName, string method, params object?[] args)
    {
        var type = asm.GetType($"{typeName}.{clrTypeName}") ?? asm.GetTypes().First(t => t.Name == clrTypeName);
        var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)!;
        return mi.Invoke(null, args);
    }

    /// Synthesizes `func answer(self: T) -> int = 42` onto the named data type —
    /// a value-receiver instance method built from the read-only projection.
    sealed class AnswerSynthesizer(string targetType) : IMemberSynthesizer
    {
        public IReadOnlyList<BoundFunctionDeclaration> SynthesizeMembers(TypeInfo type)
        {
            if (type.Name != targetType) return [];
            var intType = new PrimitiveType("int");
            var selfParam = new BoundParameter("self", new ExternalType(type.Name), ByRef: false);
            var body = new BoundBlockStatement([
                new BoundReturnStatement(new BoundLiteralExpression(42, "42", intType)),
            ]);
            return [
                new BoundFunctionDeclaration(
                    IsPublic: true, Name: "answer", TypeParameters: [],
                    Parameters: [selfParam], ReturnType: intType,
                    Body: body, Attributes: []),
            ];
        }
    }
}
