namespace Esharp.Tests;

/// Direct-field, property, and borrow-boundary tests. These intentionally use
/// complete programs: addressability is a bind + emit + runtime contract, not a
/// parser-only feature.
public sealed class ILEmitterTests_AddressableLocations
{
    static object? Run(string source, string method = "go") => EsHarness.Run(source, method);
    static IReadOnlyList<Esharp.Diagnostics.Diagnostic> Diagnostics(string source) => EsHarness.AllDiagnostics(source);

    [Fact]
    public void BareTypeMember_DefaultsToMutableDirectField()
    {
        Assert.Equal(42, Run("""
namespace Test

class Counter { value: int = 41 }

func go() -> int {
    let counter = Counter()
    counter.value += 1
    return counter.value
}
"""));
    }

    [Fact]
    public void ExplicitVarTypeMember_RemainsMutableDirectField()
    {
        Assert.Equal(42, Run("""
namespace Test

class Counter { var value: int = 41 }

func go() -> int {
    let counter = Counter()
    counter.value += 1
    return counter.value
}
"""));
    }

    [Fact]
    public void LetTypeMember_RejectsAssignment()
    {
        var diagnostics = Diagnostics("""
namespace Test

class Settings { let retries: int = 3 }

func invalid(settings: Settings) {
    settings.retries = 4
}
""");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("Cannot assign to immutable field 'retries'"));
    }

    [Fact]
    public void AddressOf_FieldOrderedTypedLocal_WritesThrough()
    {
        Assert.Equal(42, Run("""
namespace Test

func addOne(value: *int) { value += 1 }

func go() -> int {
    var count: int = 41
    addOne(&count)
    return count
}
"""));
    }

    [Fact]
    public void AddressOf_DirectStructField_WritesThrough()
    {
        Assert.Equal(42, Run("""
namespace Test

struct Counter { value: int }
func addOne(value: *int) { value += 1 }

func go() -> int {
    var counter = Counter { value: 41 }
    addOne(&counter.value)
    return counter.value
}
"""));
    }

    [Fact]
    public void AddressOf_NestedDirectStructField_WritesThrough()
    {
        Assert.Equal(42, Run("""
namespace Test

struct Inner { value: int }
struct Outer { inner: Inner }
func addOne(value: *int) { value += 1 }

func go() -> int {
    var outer = Outer { inner: Inner { value: 41 } }
    addOne(&outer.inner.value)
    return outer.inner.value
}
"""));
    }

    [Fact]
    public void AddressOf_DirectFieldThroughHeapPointer_WritesThrough()
    {
        Assert.Equal(42, Run("""
namespace Test

struct Counter { value: int }
func addOne(value: *int) { value += 1 }

func go() -> int {
    var counter: *Counter = new Counter { value: 41 }
    addOne(&counter.value)
    return counter.value
}
"""));
    }

    [Fact]
    public void AddressOf_ComputedProperty_IsRejected()
    {
        var diagnostics = Diagnostics("""
namespace Test

class Counter {
    value: int = 21
    let doubled: int => self.value * 2
}

func addOne(value: *int) { value += 1 }
func invalid(counter: Counter) { addOne(&counter.doubled) }
""");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("Cannot take the address of property 'doubled'"));
    }

    [Fact]
    public void AddressOf_ExternalProperty_IsRejected()
    {
        var diagnostics = Diagnostics("""
namespace Test

func addOne(value: *int) { value += 1 }

func invalid() {
    let stamp = DateTime.Now
    addOne(&stamp.Day)
}
""");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("Cannot take the address of property 'Day'"));
    }

    [Fact]
    public void MutableBorrow_RejectsLetLocal()
    {
        var diagnostics = Diagnostics("""
namespace Test

func addOne(value: *int) { value += 1 }

func invalid() {
    let count = 41
    addOne(&count)
}
""");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("immutable location to a mutable '*T'"));
    }

    [Fact]
    public void MutableBorrow_RejectsLetStructField()
    {
        var diagnostics = Diagnostics("""
namespace Test

struct Settings { let retries: int }
func addOne(value: *int) { value += 1 }

func invalid() {
    var settings = Settings { retries: 3 }
    addOne(&settings.retries)
}
""");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("immutable location to a mutable '*T'"));
    }

    [Fact]
    public void ReadonlyBorrow_AcceptsLetLocal()
    {
        Assert.Equal(42, Run("""
namespace Test

func observe(value: readonly *int) -> int = value

func go() -> int {
    let count = 42
    return observe(&count)
}
"""));
    }

    [Fact]
    public void ReadonlyBorrow_AcceptsLetStructField()
    {
        Assert.Equal(42, Run("""
namespace Test

struct Settings { let retries: int }
func observe(value: readonly *int) -> int = value

func go() -> int {
    let settings = Settings { retries: 42 }
    return observe(&settings.retries)
}
"""));
    }
}
