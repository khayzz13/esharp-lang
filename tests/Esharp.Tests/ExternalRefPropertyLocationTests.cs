using System.Reflection;
using Mono.Cecil;

namespace Esharp.Tests;

/// <summary>CLR ref-returning properties are the external form of durable <c>loca</c>.</summary>
public sealed class ExternalRefPropertyLocationTests
{
    static int _assemblyId;

    static (Assembly? Assembly, IReadOnlyList<Esharp.Diagnostics.Diagnostic> Diagnostics) CompileMixed(
        string esSource, string csSource)
    {
        var name = $"ExternalRefPropertyMixed_{Interlocked.Increment(ref _assemblyId)}";
        using var workspace = new Esharp.Compilation.Workspace(name);
        workspace.AddDocument($"{name}.cs", csSource);
        workspace.AddDocument($"{name}.es", esSource);
        var path = Path.Combine(Path.GetTempPath(), $"{name}.dll");
        var result = workspace.CurrentCompilation.EmitToFile(path, debugSymbols: false);
        return result.Success
            ? (Assembly.LoadFrom(path), result.Diagnostics)
            : (null, result.Diagnostics);
    }

    static object? Invoke(Assembly assembly, string method) => assembly.GetType("Test.Test")!
        .GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
        .Invoke(null, null);

    const string CSharpCell = """
namespace ExternalCells;
public sealed class RefCell
{
    private int _value = 41;
    public ref int Value => ref _value;
    public ref readonly int ReadonlyValue => ref _value;
    public int OrdinaryValue { get => _value; set => _value = value; }
}
""";

    [Fact]
    public void MixedCSharpWritableRefProperty_ProvidesWritableLoca()
    {
        var (assembly, diagnostics) = CompileMixed("""
namespace Test
using "ExternalCells"
func increment(value: *int) { value += 1 }
func go() -> int { let cell = RefCell() increment(&cell.Value) return cell.Value }
""", CSharpCell);
        Assert.NotNull(assembly);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
        Assert.Equal(42, Invoke(assembly!, "go"));
    }

    [Fact]
    public void MixedCSharpReadonlyRefProperty_ProvidesReadonlyLoca()
    {
        var (assembly, diagnostics) = CompileMixed("""
namespace Test
using "ExternalCells"
func observe(value: readonly *int) -> int { return value }
func go() -> int { let cell = RefCell() return observe(&cell.ReadonlyValue) }
""", CSharpCell);
        Assert.NotNull(assembly);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
        Assert.Equal(41, Invoke(assembly!, "go"));
    }

    [Fact]
    public void MixedCSharpReadonlyRefProperty_RejectsWritableBorrow()
    {
        var (assembly, diagnostics) = CompileMixed("""
namespace Test
using "ExternalCells"
func increment(value: *int) { value += 1 }
func bad(cell: RefCell) { increment(&cell.ReadonlyValue) }
""", CSharpCell);
        Assert.Null(assembly);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("immutable location"));
    }

    [Fact]
    public void MixedCSharpOrdinaryProperty_RemainsNonAddressable()
    {
        var (assembly, diagnostics) = CompileMixed("""
namespace Test
using "ExternalCells"
func increment(value: *int) { value += 1 }
func bad(cell: RefCell) { increment(&cell.OrdinaryValue) }
""", CSharpCell);
        Assert.Null(assembly);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("Cannot take the address of property 'OrdinaryValue'"));
    }

    [Fact]
    public void MixedCSharpRefProperty_OrdinaryReadDereferencesTheGetterResult()
    {
        var (assembly, diagnostics) = CompileMixed("""
namespace Test
using "ExternalCells"
func go() -> int { let cell = RefCell() return cell.Value + cell.ReadonlyValue }
""", CSharpCell);
        Assert.NotNull(assembly);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
        Assert.Equal(82, Invoke(assembly!, "go"));
    }

    [Fact]
    public void MetadataOnlyCSharpRefProperty_PreservesLocaAndDirection()
    {
        var producerName = $"ExternalRefPropertyProducer_{Interlocked.Increment(ref _assemblyId)}";
        var producerPath = Path.Combine(Path.GetTempPath(), $"{producerName}.dll");
        using (var producer = new Esharp.Compilation.Workspace(producerName))
        {
            producer.AddDocument($"{producerName}.cs", CSharpCell);
            var emitted = producer.CurrentCompilation.EmitToFile(producerPath, debugSymbols: false);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        }

        var references = Esharp.Compilation.Workspace.PlatformReferences().ToList();
        references.Add(Esharp.Compilation.MetadataReference.FromFile(producerPath));
        var consumerPath = Path.Combine(Path.GetTempPath(), $"ExternalRefPropertyConsumer_{Guid.NewGuid():N}.dll");
        using var consumer = new Esharp.Compilation.Workspace("ExternalRefPropertyConsumer", references);
        consumer.AddDocument("consumer.es", """
namespace Consumer
using "ExternalCells"
func increment(value: *int) { value += 1 }
func observe(value: readonly *int) -> int { return value }
func write(cell: RefCell) { increment(&cell.Value) }
func read(cell: RefCell) -> int { return observe(&cell.ReadonlyValue) }
""");
        var result = consumer.CurrentCompilation.EmitToFile(consumerPath, debugSymbols: false);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        using var assembly = AssemblyDefinition.ReadAssembly(consumerPath);
        var host = assembly.MainModule.GetType("Consumer.Consumer")!;
        Assert.Contains(host.Methods.Single(method => method.Name == "write").Body.Instructions,
            instruction => instruction.Operand is MethodReference method && method.Name == "get_Value");
        Assert.Contains(host.Methods.Single(method => method.Name == "read").Body.Instructions,
            instruction => instruction.Operand is MethodReference method && method.Name == "get_ReadonlyValue");
    }

    [Fact]
    public void MetadataOnlyCSharpReadonlyRefProperty_RejectsWritableBorrow()
    {
        var producerName = $"ExternalReadonlyRefProducer_{Interlocked.Increment(ref _assemblyId)}";
        var producerPath = Path.Combine(Path.GetTempPath(), $"{producerName}.dll");
        using (var producer = new Esharp.Compilation.Workspace(producerName))
        {
            producer.AddDocument($"{producerName}.cs", CSharpCell);
            var emitted = producer.CurrentCompilation.EmitToFile(producerPath, debugSymbols: false);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        }

        var references = Esharp.Compilation.Workspace.PlatformReferences().ToList();
        references.Add(Esharp.Compilation.MetadataReference.FromFile(producerPath));
        using var consumer = new Esharp.Compilation.Workspace("ExternalReadonlyRefConsumer", references);
        consumer.AddDocument("consumer.es", """
namespace Consumer
using "ExternalCells"
func increment(value: *int) { value += 1 }
func bad(cell: RefCell) { increment(&cell.ReadonlyValue) }
""");
        var diagnostics = consumer.CurrentCompilation.GetDiagnostics();
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("immutable location"));
    }
}
