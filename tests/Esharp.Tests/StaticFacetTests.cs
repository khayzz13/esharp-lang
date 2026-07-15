using System.Reflection;
using Esharp.Binder;
using Esharp.Compilation;
using Esharp.Symbols;
using Esharp.Syntax.Parsing;
using Mono.Cecil;

namespace Esharp.Tests;

public sealed class StaticFacetTests
{
    static object? Run(string source, string method = "go")
    {
        var assembly = EsHarness.Compile(source, "StaticFacet");
        var entry = assembly.GetType("Test.Test")!
            .GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        return entry.Invoke(null, null);
    }

    static object? Run(IReadOnlyList<string> sources, string method = "go")
    {
        var assembly = Assembly.LoadFrom(EsHarness.CompileToPath(sources, "StaticFacet"));
        var entry = assembly.GetType("Test.Test")!
            .GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        return entry.Invoke(null, null);
    }

    static (CompilationData Data, IReadOnlyList<Esharp.Diagnostics.Diagnostic> Diagnostics) Bind(string source)
    {
        var parser = new Parser(source, "static-facet.es");
        var unit = parser.ParseCompilationUnit();
        var data = new CompilationData();
        _ = new CompilationPipeline(data).Bind([unit]);
        return (data, parser.Diagnostics.Concat(data.Diagnostics.Diagnostics).ToList());
    }

    [Fact]
    public void StandaloneStaticFacet_OrdinaryReceiverAttachesAndRuns() =>
        Assert.Equal(2, Run("""
namespace Test
static Counter { var count: int = 0 }
func (x: Counter) bump() -> int { x.count += 1
 return x.count }
func go() -> int { Counter.bump()
 return Counter.bump() }
"""));

    [Fact]
    public void ExplicitStaticReceiver_AttachesToCompanionFacet() =>
        Assert.Equal(0, Run("""
namespace Test
class Counter { var value: int }
static Counter { var count: int = 3 }
func (x: static Counter) reset() -> int { x.count = 0
 return x.count }
func go() -> int = Counter.reset()
"""));

    [Fact]
    public void CompanionFacets_SelectInstanceOrStaticByReceiver() =>
        Assert.Equal(12, Run("""
namespace Test
class Counter { var value: int }
static Counter { var count: int = 2 }
func (x: Counter) read() -> int = x.value
func (x: static Counter) bump() -> int { x.count += 1
 return x.count }
func go() -> int { let c = Counter { value: 9 }
 return c.read() + Counter.bump() }
"""));

    [Fact]
    public void StaticReceiverWithoutDeclaredFacet_IsHardError()
    {
        var (_, diagnostics) = Bind("""
namespace Test
class Foo { }
func (x: static Foo) reset() { }
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("has no static facet")
            && d.Message.Contains("without the `static` keyword"));
    }

    [Fact]
    public void StaticReceiverMethod_HasNoClrReceiverParameter()
    {
        var path = EsHarness.CompileToPath("""
namespace Test
static Ops { }
func (x: Ops) add(a: int, b: int) -> int = a + b
func go() -> int = Ops.add(3, 4)
""", "StaticFacetMetadata");
        using var module = ModuleDefinition.ReadModule(path);
        var add = module.GetType("Test.Ops")!.Methods.Single(m => m.Name == "add");
        Assert.True(add.IsStatic);
        Assert.Equal(2, add.Parameters.Count);
    }

    [Fact]
    public void StaticReceiverCanCallFacetMethodsThroughItsAlias() =>
        Assert.Equal(7, Run("""
namespace Test
static Ops { func base() -> int = 6 }
func (x: Ops) next() -> int = x.base() + 1
func go() -> int = Ops.next()
"""));

    [Fact]
    public void StaticReceiverAttachmentIsCrossFileAndOrderIndependent() =>
        Assert.Equal(5, Run(["""
namespace Test
static Ops { var n: int = 4 }
func go() -> int = Ops.next()
""", """
namespace Test
func (x: Ops) next() -> int = x.n + 1
"""]));

    [Fact]
    public void StaticFacetIsRecordedOnTheSameSymbolAsItsClassCompanion()
    {
        var (data, diagnostics) = Bind("""
namespace Test
class Foo { }
static Foo { }
func (x: static Foo) reset() { }
""");
        Assert.DoesNotContain(diagnostics, d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
        var foo = data.Symbols.TryGet("Foo", 0, "Test");
        Assert.NotNull(foo);
        Assert.True(foo!.HasInstanceFacet);
        Assert.True(foo.HasStaticFacet);
        Assert.Contains(foo.Members, m => m.Name == "reset" && m.ReceiverKind == ReceiverKind.Static);
    }

    [Fact]
    public void LegacyStaticFuncSpelling_HasMigrationDiagnostic()
    {
        var (_, diagnostics) = Bind("""
namespace Test
static func Ops { }
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("static declarations are written `static Foo { ... }`"));
    }

    [Fact]
    public void ExplicitStaticReceiverAlsoWorksForStandaloneFacet() =>
        Assert.Equal(9, Run("""
namespace Test
static Ops { var n: int = 8 }
func (x: static Ops) next() -> int = x.n + 1
func go() -> int = Ops.next()
"""));

    [Fact]
    public void OrdinaryReceiverRemainsInstanceWhenBothFacetsExist() =>
        Assert.Equal(7, Run("""
namespace Test
class Box { var n: int }
static Box { var n: int = 100 }
func (x: Box) value() -> int = x.n
func go() -> int { let b = Box { n: 7 }
 return b.value() }
"""));

    [Fact]
    public void PointerStaticReceiver_IsRejected()
    {
        var (_, diagnostics) = Bind("""
namespace Test
static Ops { }
func (x: static *Ops) no() { }
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("static receiver cannot be a pointer"));
    }
}
