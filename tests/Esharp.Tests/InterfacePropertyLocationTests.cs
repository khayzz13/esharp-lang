using System.Reflection;
using Mono.Cecil;

namespace Esharp.Tests;

/// <summary>Durable property-location contracts across an interface boundary.</summary>
public sealed class InterfacePropertyLocationTests
{
    static object? Run(string source) => EsHarness.Run(source, "go");
    static IReadOnlyList<Esharp.Diagnostics.Diagnostic> Diagnostics(string source) =>
        EsHarness.AllDiagnostics(source);

    [Fact]
    public void ImplicitVarLoca_DispatchesThroughInterfaceAndWritesThrough()
    {
        Assert.Equal(42, Run("""
namespace Test
interface ICounter { var value: int { get set loca } }
class Counter : ICounter { pub var value: int = 41 }
func increment(value: *int) { value += 1 }
func update(counter: ICounter) -> int { increment(&counter.value) return counter.value }
func go() -> int { return update(Counter()) }
"""));
    }

    [Fact]
    public void ImplicitLetLoca_DispatchesReadonlyBorrowThroughInterface()
    {
        Assert.Equal(42, Run("""
namespace Test
interface ISnapshot { let value: int { get loca } }
class Snapshot : ISnapshot { pub let value: int = 42 }
func observe(value: readonly *int) -> int { return value }
func read(snapshot: ISnapshot) -> int { return observe(&snapshot.value) }
func go() -> int { return read(Snapshot()) }
"""));
    }

    [Fact]
    public void ReadonlyInterfaceLoca_RejectsWritableBorrow()
    {
        var diagnostics = Diagnostics("""
namespace Test
interface ISnapshot { let value: int { get loca } }
class Snapshot : ISnapshot { pub let value: int = 42 }
func increment(value: *int) { value += 1 }
func bad(snapshot: ISnapshot) { increment(&snapshot.value) }
""");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("immutable location"));
    }

    [Fact]
    public void ExplicitLoca_SatisfiesDurableInterfaceLocation()
    {
        Assert.Empty(Diagnostics("""
namespace Test
interface ICounter { var value: int { get set loca } }
class Counter : ICounter {
    init(value: int) { priv self.storage: int = value }
    pub var value: int { loca => &self.storage }
}
"""));
    }

    [Fact]
    public void DirectMut_SatisfiesDurableInterfaceLocation()
    {
        Assert.Empty(Diagnostics("""
namespace Test
interface ICounter { var value: int { get set loca } }
class Counter : ICounter {
    init(value: int) { priv self.storage: int = value }
    pub var value: int { mut => &self.storage }
}
"""));
    }

    [Fact]
    public void DirectField_SynthesizesDurableInterfaceLocation()
    {
        Assert.Equal(42, Run("""
namespace Test
interface ICounter { var value: int { get set loca } }
class Counter : ICounter { pub value: int = 41 }
func increment(value: *int) { value += 1 }
func update(counter: ICounter) -> int { increment(&counter.value) return counter.value }
func go() -> int { return update(Counter()) }
"""));
    }

    [Fact]
    public void ComputedProperty_CannotPromiseDurableInterfaceLocation()
    {
        var diagnostics = Diagnostics("""
namespace Test
interface IValue { let value: int { get loca } }
class Value : IValue { pub let value: int => 42 }
""");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("does not implement all required"));
    }

    [Fact]
    public void ScopedMutOnlyProperty_CannotPromiseDurableInterfaceLocation()
    {
        var diagnostics = Diagnostics("""
namespace Test
interface IValue { var value: int { get set loca } }
class Value : IValue {
    init(value: int) { priv self.storage: int = value }
    pub let value: int { mut { var working: int = self.storage yield &working self.storage = working } }
}
""");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("does not implement all required"));
    }

    [Fact]
    public void UnacknowledgedCustomSetter_CannotPromiseDurableInterfaceLocation()
    {
        var diagnostics = Diagnostics("""
namespace Test
interface IValue { var value: int { get set loca } }
class Value : IValue { pub var value: int { set(next) => next } }
""");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("does not implement all required"));
    }

    [Fact]
    public void InterfaceLoca_EmitsAbstractRefAccessorAndCapabilityMetadata()
    {
        var assembly = EsHarness.Compile("""
namespace Test
interface ICounter { var value: int { get set loca } }
""");
        var iface = assembly.GetType("Test.ICounter")!;
        var location = iface.GetMethod("getloca_value")!;
        Assert.True(location.IsAbstract);
        Assert.True(location.IsVirtual);
        Assert.True(location.ReturnType.IsByRef);
        var property = iface.GetProperty("value")!;
        var bits = (int)property.CustomAttributes
            .Single(attribute => attribute.AttributeType.Name == "__EsharpPropertyCapabilityAttribute")
            .ConstructorArguments.Single().Value!;
        Assert.Equal(0b010010, bits);
    }

    [Fact]
    public void ImplementerLocaAccessor_FillsTheInterfaceSlot()
    {
        var assembly = EsHarness.Compile("""
namespace Test
interface ICounter { var value: int { get set loca } }
class Counter : ICounter { pub var value: int = 41 }
""");
        var counter = assembly.GetType("Test.Counter")!;
        var iface = assembly.GetType("Test.ICounter")!;
        var map = counter.GetInterfaceMap(iface);
        var slot = Array.FindIndex(map.InterfaceMethods, method => method.Name == "getloca_value");
        Assert.True(slot >= 0);
        Assert.Equal("getloca_value", map.TargetMethods[slot].Name);
        Assert.True(map.TargetMethods[slot].IsVirtual);
        Assert.True(map.TargetMethods[slot].ReturnType.IsByRef);
    }

    [Fact]
    public void MetadataOnlyImport_PreservesInterfaceLocaDispatch()
    {
        var producer = EsHarness.CompileToPath("""
namespace Producer
pub interface ICounter { var value: int { get set loca } }
pub class Counter : ICounter { pub var value: int = 41 }
""", "InterfaceLocaProducer");
        var output = Path.Combine(Path.GetTempPath(), $"InterfaceLocaConsumer_{Guid.NewGuid():N}.dll");
        var references = Esharp.Compilation.Workspace.PlatformReferences().ToList();
        references.Add(Esharp.Compilation.MetadataReference.FromFile(producer));
        using var workspace = new Esharp.Compilation.Workspace("InterfaceLocaConsumer", references);
        workspace.AddDocument("consumer.es", """
namespace Consumer
using "Producer"
func increment(value: *int) { value += 1 }
func update(counter: ICounter) { increment(&counter.value) }
""");
        var emitted = workspace.CurrentCompilation.EmitToFile(output, debugSymbols: false);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));

        using var assembly = AssemblyDefinition.ReadAssembly(output);
        var update = assembly.MainModule.GetType("Consumer.Consumer")!.Methods.Single(method => method.Name == "update");
        Assert.Contains(update.Body.Instructions, instruction =>
            instruction.Operand is MethodReference method && method.Name == "getloca_value");
    }

    [Fact]
    public void CanonicalPrinter_PreservesInterfaceLocaCapability()
    {
        const string source = "namespace Test\ninterface ICounter { var value: int { get set loca } }";
        var firstParser = new Esharp.Syntax.Parsing.Parser(source, "interface-loca.es");
        var first = firstParser.ParseCompilationUnit();
        Assert.Empty(firstParser.Diagnostics);
        var printed = Esharp.Syntax.SyntaxPrinter.PrintCanonical(first);
        Assert.Contains("var value: int { get set loca }", printed);
        var secondParser = new Esharp.Syntax.Parsing.Parser(printed, "interface-loca-roundtrip.es");
        var second = secondParser.ParseCompilationUnit();
        Assert.Empty(secondParser.Diagnostics);
        var property = second.Members.OfType<Esharp.Syntax.InterfaceDeclarationSyntax>()
            .Single().Properties!.Single();
        Assert.True(property.HasGet);
        Assert.True(property.HasSet);
        Assert.True(property.HasLoca);
    }

    [Fact]
    public void SemanticModel_ReportsInterfaceLocaAsDurableProperty()
    {
        const string source = "namespace Test\ninterface ICounter { var value: int { get set loca } }";
        using var workspace = new Esharp.Compilation.Workspace("InterfaceLocaSymbols");
        var document = workspace.AddDocument("interface-loca-symbols.es", source);
        var model = workspace.CurrentCompilation.GetSemanticModel();
        var position = document.Text.Content.IndexOf("value: int", StringComparison.Ordinal);
        var symbol = Assert.IsType<Esharp.Symbols.FieldSymbol>(
            model.GetSymbolAt("interface-loca-symbols.es", position));
        Assert.Equal(Esharp.Symbols.SymbolKind.Property, symbol.Kind);
        Assert.True(((Esharp.Symbols.IFieldSymbol)symbol).HasDurableLocation);
        Assert.True(((Esharp.Symbols.IFieldSymbol)symbol).IsMutable);
    }
}
