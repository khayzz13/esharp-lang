using System.Diagnostics;
using Esharp.CodeGen;
using Esharp.Compilation;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Esharp.Tests;

public sealed class PerformanceFoundationTests
{
    [Fact]
    public void ArrayFor_LowersToCountedLoopWithoutEnumerator()
    {
        const string source = """
namespace Test
func sum(values: int[]) -> int {
    var total = 0
    for value in values { total += value }
    return total
}
""";

        var (assembly, diagnostics) = EsHarness.EmitCecil(source, "ArrayCountedLoop");
        using (assembly)
        {
            Assert.DoesNotContain(diagnostics, d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
            var method = assembly.MainModule.Types.Single(t => t.Name == "Test")
                .Methods.Single(m => m.Name == "sum");
            var instructions = method.Body.Instructions;
            Assert.Contains(instructions, i => i.OpCode == OpCodes.Ldlen);
            Assert.Contains(instructions, i => i.OpCode.Code is Code.Ldelem_I4 or Code.Ldelem_Any);
            Assert.DoesNotContain(instructions, i => i.Operand is MethodReference mr
                && (mr.Name is "GetEnumerator" or "MoveNext" or "Dispose"));
            Assert.Empty(method.Body.ExceptionHandlers);
        }

        Assert.Equal(10, EsHarness.Run(source, "sum", new[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void RangeEnd_IsEvaluatedOnceAndCached()
    {
        const string source = """
namespace Test
var calls = 0
func end() -> int { calls += 1 return 4 }
func go() -> int {
    var sum = 0
    for i in 0..end() { sum += i }
    return sum * 10 + calls
}
""";

        Assert.Equal(61, EsHarness.Run(source, "go"));
    }

    [Fact]
    public void ArrayFor_ContinueAdvancesTheHiddenIndex()
    {
        const string source = """
namespace Test
func sum(values: int[]) -> int {
    var total = 0
    for value in values {
        if value == 2 { continue }
        total += value
    }
    return total
}
""";

        Assert.Equal(8, EsHarness.Run(source, "sum", new[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void RangeFor_ContinueAdvancesTheHiddenCounter()
    {
        const string source = """
namespace Test
func sum() -> int {
    var total = 0
    for i in 0..5 {
        if i == 2 { continue }
        total += i
    }
    return total
}
""";

        Assert.Equal(8, EsHarness.Run(source, "sum"));
    }

    [Fact]
    public void ReleaseAndDebug_EmitDistinctDebuggablePolicies()
    {
        var program = EsHarness.BindAndLower("namespace Test\nfunc value() -> int = 42");
        var (debugAssembly, _) = CodeGenerator.Generate(program, "DebugPolicy",
            optimization: OptimizationLevel.Debug);
        var (releaseAssembly, _) = CodeGenerator.Generate(program, "ReleasePolicy",
            optimization: OptimizationLevel.Release);
        using (debugAssembly)
        using (releaseAssembly)
        {
            Assert.DoesNotContain(debugAssembly.CustomAttributes,
                a => a.AttributeType.FullName == typeof(DebuggableAttribute).FullName);
            var release = DebuggingModes(releaseAssembly);
            Assert.False(release.HasFlag(DebuggableAttribute.DebuggingModes.DisableOptimizations));
            Assert.True(release.HasFlag(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints));
        }
    }

    [Fact]
    public void SpanIndexReadAndWrite_EmitVerifiableIl()
    {
        const string source = """
namespace Test
using "System"
func sum(values: ReadOnlySpan<int>) -> int {
    var total = 0
    for i in 0..values.Length { total += values[i] }
    return total
}
func scale(values: Span<int>, factor: int) {
    for i in 0..values.Length { values[i] *= factor }
}
""";

        _ = EsHarness.CompileToPath(source, "SpanIndexing");
    }

    [Theory]
    [InlineData("""
namespace Test
using "System"
class InvalidBuffer { values: Span<float> }
""", "ES2230")]
    // A span rooted in a stack buffer escapes the frame (P2 allows a span rooted in a
    // heap array / field / parameter, but a `stackalloc`-derived return still trips ES2231).
    [InlineData("""
namespace Test
using "System"
func invalidReturn() -> Span<float> {
    let scratch = stackalloc float[](4)
    return scratch
}
""", "ES2231")]
    [InlineData("""
namespace Test
using "System"
using "System.Threading.Tasks"
func invalidAsync(values: Span<float>) -> Task<float> {
    await Task.Delay(1)
    return values[0]
}
""", "ES2232")]
    [InlineData("""
namespace Test
using "System"
func invalidCapture(values: Span<float>) {
    let read = func(i: int) -> float { return values[i] }
}
""", "ES2233")]
    [InlineData("""
namespace Test
using "System"
func invalidBox(values: Span<float>) -> object = values
""", "ES2234")]
    public void ByRefLikeValues_RejectUnsafeStorageAndEscape(string source, string code)
    {
        Assert.Contains(EsHarness.Diagnostics(source), diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void ByRefLikeParameter_UsedOnlyBeforeAwait_DoesNotEnterStateMachine()
    {
        const string source = """
namespace Test
using "System"
using "System.Threading.Tasks"
func validAsync(values: Span<float>) -> Task<int> {
    let length = values.Length
    await Task.Delay(1)
    return length
}
""";

        Assert.DoesNotContain(EsHarness.Diagnostics(source), diagnostic => diagnostic.Code == "ES2232");
        _ = EsHarness.CompileToPath(source, "DeadSpanBeforeAwait");
    }

    [Fact]
    public void SystemNumericsVector_WithSpanArguments_CompilesThroughIlBackend()
    {
        const string source = """
namespace Test
using "System"
using "System.Numerics"
func saxpy(destination: Span<float>, x: ReadOnlySpan<float>, y: ReadOnlySpan<float>, scale: float) {
    let width = Vector<float>.Count
    var i = 0
    while i + width <= destination.Length {
        let vx = Vector<float>(x.Slice(i, width))
        let vy = Vector<float>(y.Slice(i, width))
        let scaled = Vector.Multiply(vx, scale)
        let result = Vector.Add(scaled, vy)
        result.CopyTo(destination.Slice(i, width))
        i += width
    }
    while i < destination.Length {
        destination[i] = x[i] * scale + y[i]
        i += 1
    }
}
func run(destination: float[], x: float[], y: float[], scale: float) -> float[] {
    let destinationSpan = Span<float>(destination)
    let xSpan = ReadOnlySpan<float>(x)
    let ySpan = ReadOnlySpan<float>(y)
    saxpy(destinationSpan, xSpan, ySpan, scale)
    return destination
}
""";

        var assembly = EsHarness.Compile(source, "SystemNumericsVector");
        var width = System.Numerics.Vector<float>.Count;
        foreach (var length in new[] { 0, 1, width - 1, width, width + 1, width * 2 + 3 })
        {
            var destination = new float[length];
            var x = Enumerable.Range(0, length).Select(i => i * 0.25f - 2f).ToArray();
            var y = Enumerable.Range(0, length).Select(i => i * -0.5f + 1f).ToArray();
            var actual = Assert.IsType<float[]>(EsHarness.Invoke(assembly, "run", destination, x, y, 1.75f));
            for (var i = 0; i < length; i++)
                Assert.InRange(MathF.Abs(actual[i] - (x[i] * 1.75f + y[i])), 0f, 1e-5f);
        }
    }

    [Fact]
    public void ArchitectureIntrinsicSupportProperty_CompilesThroughIlBackend()
    {
        const string source = """
namespace Test
using "System.Runtime.Intrinsics.X86"
func avx2Available() -> bool = Avx2.IsSupported
""";

        _ = EsHarness.CompileToPath(source, "IntrinsicSupportGuard");
    }

    [Fact]
    public void ShowAllocations_ReportsLargeCopiesButPreservesExplicitReadonlyBorrow()
    {
        const string source = """
namespace Test
struct SampleBlock { a: double, b: double, c: double, d: double }
func copied(block: SampleBlock) -> double = block.a
func borrowed(block: readonly *SampleBlock) -> double = block.a
""";

        using var workspace = new Workspace("AllocationDiagnostics",
            options: new ProjectOptions(ShowAllocations: true));
        workspace.AddDocument("test.es", source);
        var diagnostics = workspace.CurrentCompilation.GetDiagnostics();

        var copy = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "ES8001");
        Assert.Contains("copies 32 bytes", copy.Message);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Message.Contains("borrowed"));
    }

    [Fact]
    public void FloatingMultiplyAdd_PreservesOperationTreeUnlessFmaIsExplicit()
    {
        const string source = """
namespace Test
using "System"
func multiplyAdd(a: double, b: double, c: double) -> double = a * b + c
func fusedMultiplyAdd(a: double, b: double, c: double) -> double = Math.FusedMultiplyAdd(a, b, c)
""";

        var (assembly, diagnostics) = EsHarness.EmitCecil(source, "FloatingPointContract");
        using (assembly)
        {
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
            var host = assembly.MainModule.Types.Single(type => type.Name == "Test");
            var ordinary = host.Methods.Single(method => method.Name == "multiplyAdd");
            Assert.Contains(ordinary.Body.Instructions, instruction => instruction.OpCode == OpCodes.Mul);
            Assert.Contains(ordinary.Body.Instructions, instruction => instruction.OpCode == OpCodes.Add);
            Assert.DoesNotContain(ordinary.Body.Instructions, instruction =>
                instruction.Operand is MethodReference method && method.Name == "FusedMultiplyAdd");

            var fused = host.Methods.Single(method => method.Name == "fusedMultiplyAdd");
            Assert.Contains(fused.Body.Instructions, instruction =>
                instruction.Operand is MethodReference method && method.Name == "FusedMultiplyAdd");
        }
    }

    static DebuggableAttribute.DebuggingModes DebuggingModes(AssemblyDefinition assembly)
    {
        var attribute = assembly.CustomAttributes.Single(a =>
            a.AttributeType.FullName == typeof(DebuggableAttribute).FullName);
        return (DebuggableAttribute.DebuggingModes)(int)attribute.ConstructorArguments[0].Value!;
    }
}
