using System.Reflection;
using Xunit;

namespace Esharp.Tests;

// Interface property requirements (plan 11B, last Phase A item): an `interface` declares
// `let x: T { get }` / `var x: T { get set }` / `required let x: T { get init }`, emitting
// abstract get_/set_ accessor slots + a PropertyDefinition. An implementer satisfies the
// contract with an explicitly declared PROPERTY (get-only / get+set / get+init / computed).
// A field remains a field and is diagnosed rather than acquiring an implicit property ABI.
// Property accessors are wired into the interface vtable slot so interface dispatch reaches them.
public sealed class InterfacePropertyTests
{
    static object? Run(string body, string method, params object?[] args) =>
        EsHarness.Run(body, method, args);

    // --- Conformance by a property, dispatched through the interface reference ---

    // get+set auto property satisfies a `{ get set }` requirement; read through the iface.
    [Fact]
    public void Property_GetSet_DispatchesThroughInterface() => Assert.Equal(42, Run("""
namespace Test
interface IBox { var n: int { get set } }
class Box : IBox {
    pub var n: int { }
    init() { self.n = 0 }
}
func store(b: IBox) -> int { b.n = 42  return b.n }
func go() -> int { let b = Box() return store(b) }
""", "go"));

    // get-only property satisfies a `{ get }` requirement (value set in init).
    [Fact]
    public void Property_GetOnly_DispatchesThroughInterface() => Assert.Equal(7, Run("""
namespace Test
interface INamed { let id: int { get } }
class Tag : INamed {
    pub let id: int { }
    init(x: int) { self.id = x }
}
func read(n: INamed) -> int = n.id
func go() -> int { let t = Tag(7) return read(t) }
""", "go"));

    // A computed property (a getter, no backing field) satisfies a get-only requirement.
    [Fact]
    public void ComputedProperty_SatisfiesGetOnly() => Assert.Equal(20, Run("""
namespace Test
interface IArea { let area: int { get } }
class Rect : IArea {
    var w: int
    var h: int
    pub let area: int => self.w * self.h
}
func measure(a: IArea) -> int = a.area
func go() -> int { let r = Rect { w: 4, h: 5 } return measure(r) }
""", "go"));

    [Fact]
    public void CustomGetterAndBehavioralSetter_SatisfyGetSetRequirement() => Assert.Equal(42, Run("""
namespace Test
interface IDisplay { var display: int { get set } }
class Session : IDisplay {
    id: int
    init(id: int) { self.id = id }
    func replace(value: int) { self.id = value }
    pub var display: int {
        get => self.id + 1
        set(value) => self.replace(value)
    }
}
func update(value: IDisplay) -> int { value.display = 41  return value.display }
func go() -> int { return update(Session(0)) }
""", "go"));

    // A get+set property satisfies a get-only requirement (get+set ⊇ get-only).
    [Fact]
    public void Property_GetSet_SatisfiesGetOnlyRequirement() => Assert.Equal(9, Run("""
namespace Test
interface IReadable { let v: int { get } }
class Cell : IReadable {
    pub var v: int { }
    init(x: int) { self.v = x }
}
func read(r: IReadable) -> int = r.v
func go() -> int { let c = Cell(9) return read(c) }
""", "go"));

    // --- A field never becomes a property through conformance ---

    [Fact]
    public void MutableField_DoesNotSatisfyGetSetProperty()
    {
        var diagnostics = EsHarness.Diagnostics("""
namespace Test
interface ICounter { var total: int { get set } }
class Counter : ICounter {
    pub total: int
    init() { self.total = 0 }
}
""");
        var mismatch = Assert.Single(diagnostics, d => d.Code == "ES2226");
        Assert.Contains("'Counter.total' is a field", mismatch.Message);
        Assert.Contains("interface 'ICounter' requires property 'total'", mismatch.Message);
    }

    // `let` is a property declaration even without braces.
    [Fact]
    public void Property_Let_SatisfiesGetOnly() => Assert.Equal(13, Run("""
namespace Test
interface ITagged { let tag: int { get } }
class Item : ITagged {
    pub let tag: int
    init(x: int) { self.tag = x }
}
func read(t: ITagged) -> int = t.tag
func go() -> int { let i = Item(13) return read(i) }
""", "go"));

    // A value struct property survives boxing and dispatches through its accessor.
    [Fact]
    public void Struct_PropertySatisfiesProperty_BoxesThroughInterface() => Assert.Equal(5, Run("""
namespace Test
interface IHasX { let x: int { get } }
struct Point : IHasX {
    pub let x: int
    pub let y: int
}
func readX(h: IHasX) -> int = h.x
func go() -> int { let p = Point { x: 5, y: 6 } return readX(p) }
""", "go"));

    // --- get+init requirement ---

    // A `required let x { }` property (get+init) satisfies a `{ get init }` requirement;
    // value supplied by the composite literal, read through the interface.
    [Fact]
    public void Property_GetInit_SatisfiesGetInitRequirement() => Assert.Equal(77, Run("""
namespace Test
interface IIded { let id: int { get init } }
class User : IIded {
    pub required let id: int { }
}
func read(o: IIded) -> int = o.id
func go() -> int { let u = User { id: 77 } return read(u) }
""", "go"));

    // --- Metadata shape ---

    // The interface carries abstract get_/set_ accessor methods + a PropertyDefinition,
    // byte-identical to a C# interface property.
    [Fact]
    public void InterfaceProperty_EmitsAbstractAccessorsAndPropertyDefinition()
    {
        var asm = EsHarness.Compile("""
namespace Test
interface IShape {
    let area: float { get }
    var name: string { get set }
}
""");
        var iface = asm.GetType("Test.IShape")!;
        Assert.True(iface.IsInterface);

        var getArea = iface.GetMethod("get_area");
        Assert.NotNull(getArea);
        Assert.True(getArea!.IsAbstract);
        Assert.Null(iface.GetMethod("set_area")); // get-only → no setter slot

        Assert.NotNull(iface.GetMethod("get_name"));
        Assert.NotNull(iface.GetMethod("set_name"));

        // The PropertyDefinitions are present and typed.
        Assert.NotNull(iface.GetProperty("area"));
        Assert.NotNull(iface.GetProperty("name"));
        Assert.Equal(typeof(float), iface.GetProperty("area")!.PropertyType);
    }

    // The implementer's property accessor fills the interface slot: the property is an
    // interface-mapped accessor (virtual, with a method-impl).
    [Fact]
    public void Implementer_AccessorIsVirtual_FillsInterfaceSlot()
    {
        var asm = EsHarness.Compile("""
namespace Test
interface IBox { var n: int { get set } }
class Box : IBox {
    pub var n: int { }
    init() { self.n = 0 }
}
""");
        var box = asm.GetType("Test.Box")!;
        var map = box.GetInterfaceMap(asm.GetType("Test.IBox")!);
        // Every interface method maps to a concrete target method on Box.
        Assert.Equal(map.InterfaceMethods.Length, map.TargetMethods.Length);
        Assert.Contains(map.TargetMethods, m => m.Name == "get_n");
        Assert.Contains(map.TargetMethods, m => m.Name == "set_n");
        Assert.All(map.TargetMethods, m => Assert.True(m.IsVirtual));
    }

    [Fact]
    public void Field_DoesNotAcquireSynthesizedInterfaceAccessors()
    {
        var diagnostics = EsHarness.Diagnostics("""
namespace Test
interface INamed { var label: string { get set } }
class Tag : INamed {
    pub label: string
    init() { self.label = "" }
}
""");
        Assert.Single(diagnostics, d => d.Code == "ES2226");
    }

    [Fact]
    public void StructField_DoesNotSatisfyGetOnlyProperty()
    {
        var diagnostics = EsHarness.Diagnostics("""
namespace Test
interface IHasX { let x: int { get } }
struct Point : IHasX {
    pub x: int
}
""");
        Assert.Single(diagnostics, d => d.Code == "ES2226");
    }
}
