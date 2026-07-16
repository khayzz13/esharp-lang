using System.Reflection;
using Xunit;

namespace Esharp.Tests;

// Phase A — property declarations (plan 11B). First slice: computed get-only
// (`let x: T => expr`). The getter is a `get_X` method + a PropertyDefinition, no
// backing field; reads of `obj.X` route through the accessor. ILVerify-gated by
// EsHarness.Compile (it asserts no verification errors).
public class PropertyTests
{
    [Fact]
    public void ComputedProperty_Class_RecomputesFromFields()
    {
        var result = EsHarness.Run("""
namespace Test
class Box {
    n: int
    init(v: int) { self.n = v }
    let doubled: int => self.n * 2
}
func go() -> int {
    let b = Box(21)
    return b.doubled
}
""", "go");
        Assert.Equal(42, (int)result!);
    }

    [Fact]
    public void ComputedProperty_GenericClass_ReadsBackingVar()
    {
        var result = EsHarness.Run("""
namespace Test
class Holder<T> {
    var cur: T
    init(initial: T) { self.cur = initial }
    let current: T => self.cur
}
func go() -> int {
    let h = Holder<int>(42)
    return h.current
}
""", "go");
        Assert.Equal(42, (int)result!);
    }

    [Fact]
    public void AutoProperty_Class_GetSet_RoundTrips()
    {
        var result = EsHarness.Run("""
namespace Test
class Counter {
    var count: int {}
    init() { self.count = 0 }
    func bump() { self.count = self.count + 1 }
}
func go() -> int {
    let c = Counter()
    c.bump()
    c.bump()
    return c.count
}
""", "go");
        Assert.Equal(2, (int)result!);
    }

    [Fact]
    public void GetOnlyProperty_Class_SetInInit()
    {
        var result = EsHarness.Run("""
namespace Test
class Point {
    let x: int {}
    let y: int {}
    init(a: int, b: int) { self.x = a  self.y = b }
}
func go() -> int {
    let p = Point(3, 4)
    return p.x + p.y
}
""", "go");
        Assert.Equal(7, (int)result!);
    }

    [Fact]
    public void RequiredInitProperty_Data_SetByCompositeLiteral()
    {
        var result = EsHarness.Run("""
namespace Test
struct Span {
    required let lo: int {}
    required let hi: int {}
}
func go() -> int {
    let s = Span { lo: 2, hi: 9 }
    return s.hi - s.lo
}
""", "go");
        Assert.Equal(7, (int)result!);
    }

    [Fact]
    public void AutoProperty_EmitsGetSet_BackingFieldHidden()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Counter {
    var count: int {}
    init() { self.count = 0 }
}
""");
        var t = asm.GetType("Test.Counter")!;
        var prop = t.GetProperty("count", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetGetMethod(nonPublic: true));
        Assert.NotNull(prop.GetSetMethod(nonPublic: true));
        // No PUBLIC field named `count`; storage is the private backing field.
        Assert.Null(t.GetField("count", BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void GetOnlyStored_OnData_IsRejected()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => EsHarness.Compile("""
namespace Test
struct Bad {
    let x: int {}
}
"""));
        Assert.Contains("ES2193", ex.Message);
    }

    [Fact]
    public void CustomSetter_Class_ClampsCrossTypeWrites()
    {
        // `set(x) => valueExpr` — the body is a value expression stored to the property.
        // A cross-type write (`c.v = …`) routes through set_v and is clamped; the value
        // read back reflects the clamp.
        var result = EsHarness.Run("""
namespace Test
class Clamp {
    var v: int { set(x) => x < 0 ? 0 : x }
    init() { self.v = 5 }
}
func go() -> int {
    let c = Clamp()
    c.v = -3
    let a = c.v
    c.v = 42
    return a + c.v
}
""", "go");
        Assert.Equal(42, (int)result!);
    }

    [Fact]
    public void CustomSetter_EmitsSetMethod_NoPublicField()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Clamp {
    var v: int { set(x) => x < 0 ? 0 : x }
    init() { self.v = 0 }
}
""");
        var t = asm.GetType("Test.Clamp")!;
        var prop = t.GetProperty("v", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetGetMethod(nonPublic: true));
        Assert.NotNull(prop.GetSetMethod(nonPublic: true));
        Assert.Null(t.GetField("v", BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void ComputedProperty_EmitsPropertyMetadata_NoPublicField()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Circle {
    r: float
    init(radius: float) { self.r = radius }
    let area: float => self.r * self.r * 3.0
}
""");
        var t = asm.GetType("Test.Circle")!;
        // A real CLR property `area` exists, backed by get_area; no field named `area`.
        Assert.NotNull(t.GetProperty("area", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.Null(t.GetField("area", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(t.GetMethod("get_area", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
    }

    [Fact]
    public void CustomGetter_LetAccessor_RecomputesWithoutBackingStorage()
    {
        var result = EsHarness.Run("""
namespace Test
class Session {
    id: int
    init(id: int) { self.id = id }
    let displayId: int {
        get => self.id + 100
    }
}
func go() -> int { return Session(23).displayId }
""", "go");
        Assert.Equal(123, result);
    }

    [Fact]
    public void CustomGetter_VarPairsWithBehavioralSetter()
    {
        var result = EsHarness.Run("""
namespace Test
class Session {
    id: int
    init(id: int) { self.id = id }
    func replaceId(value: int) { self.id = value }
    var displayId: int {
        get => self.id + 1
        set(value) => self.replaceId(value)
    }
}
func go() -> int {
    let session = Session(4)
    session.displayId = 41
    return session.displayId
}
""", "go");
        Assert.Equal(42, result);
    }

    [Fact]
    public void CustomGetterAndSetter_EmitPropertyWithoutBackingStorage()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Session {
    id: int
    init(id: int) { self.id = id }
    func replaceId(value: int) { self.id = value }
    pub var displayId: int {
        get => self.id
        set(value) => self.replaceId(value)
    }
}
""");
        var type = asm.GetType("Test.Session")!;
        var property = type.GetProperty("displayId")!;
        Assert.NotNull(property.GetMethod);
        Assert.NotNull(property.SetMethod);
        Assert.Null(type.GetField("<displayId>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance));
    }

    [Fact]
    public void VarArrowProperty_IsRejectedInsteadOfSilentlyBecomingReadonly()
    {
        var diagnostics = EsHarness.Diagnostics("""
namespace Test
class Session {
    id: int
    var displayId: int => self.id
}
""");
        Assert.Contains(diagnostics, d => d.Code == "ES2227");
    }

    [Fact]
    public void VarCustomGetter_RequiresWriteBehavior()
    {
        var diagnostics = EsHarness.Diagnostics("""
namespace Test
class Session {
    id: int
    var displayId: int {
        get => self.id
    }
}
""");
        Assert.Contains(diagnostics, d => d.Code == "ES2228");
    }

    [Fact]
    public void OwningTypeMethod_AssignmentInvokesCustomSetter()
    {
        var result = EsHarness.Run("""
namespace Test
class Clamp {
    var value: int { set(v) => v < 0 ? 0 : v }
    init() { self.value = 5 }
    func replace(v: int) { self.value = v }
}
func go() -> int {
    let clamp = Clamp()
    clamp.replace(-10)
    return clamp.value
}
""", "go");
        Assert.Equal(0, result);
    }

    [Fact]
    public void GeneratedAsyncIterator_CurrentIsAPropertyNotAFieldFacade()
    {
        var asm = EsHarness.Compile("""
namespace Test
func numbers() -> IAsyncEnumerable<int> {
    yield 1
}
""");
        var iterator = Assert.Single(asm.GetTypes(), type =>
            type.Name.Contains("d__Iterator", StringComparison.Ordinal));
        var current = iterator.GetProperty("Current",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(current);
        Assert.NotNull(current!.GetGetMethod(nonPublic: true));
        Assert.Null(iterator.GetField("Current",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
    }
}
