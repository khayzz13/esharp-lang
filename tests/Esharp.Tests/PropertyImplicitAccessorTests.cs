using System.Reflection;

namespace Esharp.Tests;

/// <summary>
/// Independent coverage for the caller-visible contract synthesized by stored
/// <c>let</c>/<c>var</c> properties.  These tests deliberately keep get, set,
/// and location behavior separate so one generated accessor cannot mask another.
/// </summary>
public sealed class PropertyImplicitAccessorTests
{
    static object? Run(string source) => EsHarness.Run(source, "go");
    static IReadOnlyList<Esharp.Diagnostics.Diagnostic> Diagnostics(string source) =>
        EsHarness.AllDiagnostics(source);

    [Fact]
    public void ImplicitVarGetter_ReadsTheCurrentBackingValue()
    {
        Assert.Equal(41, Run("""
namespace Test
class Meter { var value: int init(value: int) { self.value = value } }
func go() -> int { return Meter(41).value }
"""));
    }

    [Fact]
    public void ImplicitVarSetter_ReplacesTheBackingValue()
    {
        Assert.Equal(42, Run("""
namespace Test
class Meter { var value: int init(value: int) { self.value = value } }
func go() -> int { let meter = Meter(1) meter.value = 42 return meter.value }
"""));
    }

    [Fact]
    public void ImplicitVarCompoundAssignment_ReadsAndWritesThroughAccessors()
    {
        Assert.Equal(42, Run("""
namespace Test
class Meter { var value: int init(value: int) { self.value = value } }
func go() -> int { let meter = Meter(41) meter.value += 1 return meter.value }
"""));
    }

    [Fact]
    public void ImplicitVarLoca_AllowsWritableAliasing()
    {
        Assert.Equal(42, Run("""
namespace Test
class Meter { var value: int init(value: int) { self.value = value } }
func increment(value: *int) { value += 1 }
func go() -> int { let meter = Meter(41) increment(&meter.value) return meter.value }
"""));
    }

    [Fact]
    public void ImplicitLetGetter_ReadsInitializedStorage()
    {
        Assert.Equal(42, Run("""
namespace Test
class Snapshot { let value: int = 42 }
func go() -> int { return Snapshot().value }
"""));
    }

    [Fact]
    public void ImplicitLetLoca_AllowsReadonlyAliasing()
    {
        Assert.Equal(42, Run("""
namespace Test
class Snapshot { let value: int = 42 }
func observe(value: readonly *int) -> int { return value }
func go() -> int { let snapshot = Snapshot() return observe(&snapshot.value) }
"""));
    }

    [Fact]
    public void ImplicitLetLoca_RejectsWritableAliasing()
    {
        var diagnostics = Diagnostics("""
namespace Test
class Snapshot { let value: int = 42 }
func increment(value: *int) { value += 1 }
func bad(snapshot: Snapshot) { increment(&snapshot.value) }
""");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("immutable location"));
    }

    [Fact]
    public void ImplicitAccessors_EmitClrPropertyAndThreeAccessorMethodsForVar()
    {
        var assembly = EsHarness.Compile("""
namespace Test
class Meter { pub var value: int = 1 }
""");
        var type = assembly.GetType("Test.Meter")!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var property = type.GetProperty("value", flags)!;

        Assert.NotNull(property.GetMethod);
        Assert.NotNull(property.SetMethod);
        Assert.NotNull(type.GetMethod("getloca_value", flags));
        Assert.True(type.GetMethod("getloca_value", flags)!.ReturnType.IsByRef);
        Assert.NotNull(type.GetField("<value>k__BackingField", flags));
        Assert.Null(type.GetField("value", flags));
    }

    [Fact]
    public void ImplicitLet_EmitsGetterInitSetterAndReadonlyLoca()
    {
        var assembly = EsHarness.Compile("""
namespace Test
class Snapshot { pub let value: int = 1 }
""");
        var type = assembly.GetType("Test.Snapshot")!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var property = type.GetProperty("value", flags)!;

        Assert.NotNull(property.GetMethod);
        Assert.NotNull(property.SetMethod);
        Assert.Contains(typeof(System.Runtime.CompilerServices.IsExternalInit),
            property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
        Assert.NotNull(type.GetMethod("getloca_value", flags));
        Assert.True(type.GetMethod("getloca_value", flags)!.ReturnType.IsByRef);
    }

    [Fact]
    public void ImplicitGetter_EvaluatesItsReceiverExactlyOnce()
    {
        Assert.Equal(142, Run("""
namespace Test
class Meter { var value: int = 42 }
var calls: int = 0
func make() -> Meter { calls += 1 return Meter() }
func go() -> int { let value: int = make().value return calls * 100 + value }
"""));
    }

    [Fact]
    public void ImplicitSetter_EvaluatesItsReceiverExactlyOnce()
    {
        Assert.Equal(1, Run("""
namespace Test
class Meter { var value: int }
var calls: int = 0
func make() -> Meter { calls += 1 return Meter() }
func go() -> int { make().value = 42 return calls }
"""));
    }

    [Fact]
    public void ImplicitLoca_EvaluatesItsReceiverExactlyOnce()
    {
        Assert.Equal(142, Run("""
namespace Test
class Meter { var value: int = 41 }
var calls: int = 0
func make() -> Meter { calls += 1 return Meter() }
func increment(value: *int) { value += 1 }
func go() -> int { let meter = make() increment(&meter.value) return calls * 100 + meter.value }
"""));
    }
}
