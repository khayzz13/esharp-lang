using System.Reflection;

namespace Esharp.Tests;

/// <summary>Closure and async lowering preserve the declaration's representation category.</summary>
public sealed class LocalRepresentationCaptureTests
{
    static IReadOnlyList<Esharp.Diagnostics.Diagnostic> Diagnostics(string source) =>
        EsHarness.AllDiagnostics(source);

    [Fact]
    public void ClosureCapture_BareTypedLocalRemainsMutable()
    {
        Assert.Equal(42, EsHarness.Run("""
namespace Test
func go() -> int {
    value: int = 41
    let increment = func() { value += 1 }
    increment()
    return value
}
""", "go"));
    }

    [Fact]
    public void ClosureCapture_BareTypedLocalRemainsNonAddressable()
    {
        var diagnostics = Diagnostics("""
namespace Test
func observe(value: readonly *int) -> int { return value }
func bad() {
    value: int = 41
    let read = func() -> int { return observe(&value) }
    read()
}
""");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("typed value binding 'value'"));
    }

    [Fact]
    public void ClosureCapture_VarLocalPreservesWritableAliasing()
    {
        Assert.Equal(42, EsHarness.Run("""
namespace Test
func increment(value: *int) { value += 1 }
func go() -> int {
    var value: int = 41
    let write = func() { increment(&value) }
    write()
    return value
}
""", "go"));
    }

    [Fact]
    public void ClosureCapture_LetLocalPreservesReadonlyAliasing()
    {
        Assert.Equal(42, EsHarness.Run("""
namespace Test
func observe(value: readonly *int) -> int { return value }
func go() -> int {
    let value: int = 42
    let read = func() -> int { return observe(&value) }
    return read()
}
""", "go"));
    }

    [Fact]
    public void ClosureCapture_LetLocalRejectsWritableBorrow()
    {
        var diagnostics = Diagnostics("""
namespace Test
func increment(value: *int) { value += 1 }
func bad() {
    let value: int = 41
    let write = func() { increment(&value) }
    write()
}
""");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("immutable location"));
    }

    [Fact]
    public void AsyncCapture_BareTypedLocalRemainsMutableAcrossAwait()
    {
        var assembly = EsHarness.Compile("""
namespace Test
using "System.Threading.Tasks"
func go() -> Task<int> {
    value: int = 41
    await Task.Delay(1)
    value += 1
    return value
}
""");
        Assert.Equal(42, EsHarness.Await(EsHarness.Invoke(assembly, "go")));
    }

    [Fact]
    public void AsyncCapture_BareTypedLocalRemainsNonAddressableAfterAwait()
    {
        var diagnostics = Diagnostics("""
namespace Test
using "System.Threading.Tasks"
func observe(value: readonly *int) -> int { return value }
func bad() -> Task<int> {
    value: int = 42
    await Task.Delay(1)
    return observe(&value)
}
""");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("typed value binding 'value'"));
    }

    [Fact]
    public void AsyncCapture_VarLocalPreservesWritableAliasingAcrossAwait()
    {
        var assembly = EsHarness.Compile("""
namespace Test
using "System.Threading.Tasks"
func increment(value: *int) { value += 1 }
func go() -> Task<int> {
    var value: int = 41
    await Task.Delay(1)
    increment(&value)
    return value
}
""");
        Assert.Equal(42, EsHarness.Await(EsHarness.Invoke(assembly, "go")));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var machine = assembly.GetTypes().Single(type => type.Name.Contains("StateMachine", StringComparison.Ordinal));
        Assert.DoesNotContain(machine.GetFields(flags), field => field.FieldType.IsByRef);
    }

    [Fact]
    public void AsyncCapture_LetLocalPreservesReadonlyAliasingAcrossAwait()
    {
        var assembly = EsHarness.Compile("""
namespace Test
using "System.Threading.Tasks"
func observe(value: readonly *int) -> int { return value }
func go() -> Task<int> {
    let value: int = 42
    await Task.Delay(1)
    return observe(&value)
}
""");
        Assert.Equal(42, EsHarness.Await(EsHarness.Invoke(assembly, "go")));
    }

    [Fact]
    public void AsyncCapture_LetLocalRejectsWritableBorrowAfterAwait()
    {
        var diagnostics = Diagnostics("""
namespace Test
using "System.Threading.Tasks"
func increment(value: *int) { value += 1 }
func bad() -> Task<int> {
    let value: int = 41
    await Task.Delay(1)
    increment(&value)
    return value
}
""");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("immutable location"));
    }
}
