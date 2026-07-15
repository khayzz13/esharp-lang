namespace Esharp.Tests;

/// The complete programs in spec/statements/bindings-and-assignment.md. These
/// tests keep the reference examples executable rather than illustrative-only.
public sealed class ILEmitterTests_BindingExamples
{
    static object? Run(string source, string entry, params object?[] args) => EsHarness.Run(source, entry, args);

    [Fact]
    public void TypedLet_TargetTypes_MethodGroup()
    {
        Assert.Equal(20, Run("""
namespace Test

func double(value: int) -> int = value * 2

func applyTwice(value: int) -> int {
    let initial: int = value
    let transform: Func<int, int> = double
    return transform(transform(initial))
}
""", "applyTwice", 5));
    }

    [Fact]
    public void FieldOrderedTypedLocal_AndImplicitPropertyLocation()
    {
        Assert.Equal(1011, Run("""
namespace Test

class AppConfig {
    environment: string = "development"
    var reloads: int
    let source: string = "built-in"
    var displayName: string { }
}

func addOne(value: *int) { value += 1 }

func selectEnvironment() -> int {
    let config = AppConfig()

    currentEnv: string = config.environment
    let configuredEnv: string = currentEnv
    var selectedEnv: string = configuredEnv
    selectedEnv = "production"
    currentEnv = selectedEnv

    var localReloads: int = 0
    addOne(&localReloads)
    addOne(&config.reloads)

    config.environment = currentEnv
    return config.environment.Length * 100 + localReloads * 10 + config.reloads
}
""", "selectEnvironment"));
    }

    [Fact]
    public void AddressOf_LocalAndImplicitPropertyLocation()
    {
        Assert.Equal(610, Run("""
namespace Test

class AppConfig {
    var environment: int = 40
    let source: string = "built-in"
}

func addOne(value: *int) { value += 1 }

func addressableLocations() -> int {
    let config = AppConfig()
    var local = 1

    addOne(&local)
    addOne(&config.environment)

    return local * 100 + config.environment * 10
}
""", "addressableLocations"));
    }

    [Fact]
    public void AddressOf_Property_UsesImplicitLoca()
    {
        Assert.Equal(1, Run("""
namespace Test

class AppConfig { var displayValue: int { } }

func addOne(value: *int) { value += 1 }

func update() -> int {
    let config = AppConfig()
    addOne(&config.displayValue)
    return config.displayValue
}
""", "update"));
    }

    [Fact]
    public void AddressOf_RawClassField_IsRejected()
    {
        var diagnostics = EsHarness.AllDiagnostics("""
namespace Test

class AppConfig { displayValue: int }

func addOne(value: *int) { value += 1 }

func invalid(config: AppConfig) {
    addOne(&config.displayValue)
}
""");
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Cannot take the address of class field 'displayValue'"));
    }

    [Fact]
    public void Assignment_LocalFieldAndIndexTargets()
    {
        Assert.Equal(43, Run("""
namespace Test

struct Totals { var value: int }

func update() -> int {
    var retries = 1
    retries += 2

    var totals = Totals { value: 10 }
    totals.value += retries

    let slots = int[](2)
    slots[0] = totals.value
    slots[1] = retries * 10
    slots[0] += slots[1]

    return slots[0]
}
""", "update"));
    }

    [Fact]
    public void CompoundIndexAssignment_EvaluatesOperandsOnce()
    {
        Assert.Equal(511, Run("""
namespace Test

struct Calls {
    var indexCalls: int
    var valueCalls: int
}

func nextIndex(calls: *Calls) -> int {
    calls.indexCalls += 1
    return 0
}

func nextValue(calls: *Calls) -> int {
    calls.valueCalls += 1
    return 5
}

func updateOnce() -> int {
    var calls: *Calls = new Calls { indexCalls: 0, valueCalls: 0 }
    let slots = int[](1)

    slots[nextIndex(calls)] += nextValue(calls)

    return slots[0] * 100 + calls.indexCalls * 10 + calls.valueCalls
}
""", "updateOnce"));
    }
}
