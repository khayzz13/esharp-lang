namespace Esharp.Tests;

/// <summary>
/// Type references between user-declared types must resolve regardless of declaration
/// order. The IL backend used to emit each type fully (shell + fields) in source order,
/// so a field/payload whose type was declared LATER in the file resolved to `object`
/// (silent erasure → ES0900 verification failure) or a hard `unresolved type` error.
/// The two-phase emission (register every shell, THEN populate members) fixes it.
///
/// Each test puts the referenced type AFTER its user, and asserts the program both
/// compiles (through ILVerify) and runs to the right value.
/// </summary>
public sealed class ForwardReferenceTests
{
    static int Int(string src) => (int)EsHarness.Run(src, "go")!;
    static string Str(string src) => (string)EsHarness.Run(src, "go")!;

    [Fact]
    public void Data_Field_Of_LaterData()
    {
        Assert.Equal(7, Int("""
namespace Test
struct A { b: B }
struct B { n: int }
func go() -> int {
    let a = A { b: B { n: 7 } }
    return a.b.n
}
"""));
    }

    [Fact]
    public void Data_ListField_Of_LaterData()
    {
        // List<B> where B is declared after A — the generic argument used to erase to
        // List<object>, tripping ILVerify at the consuming load.
        Assert.Equal(2, Int("""
namespace Test
struct A { bs: List<B> }
struct B { n: int }
func go() -> int {
    let xs = List<B>()
    xs.Add(B { n: 1 })
    xs.Add(B { n: 2 })
    let a = A { bs: xs }
    return a.bs.Count
}
"""));
    }

    [Fact]
    public void Data_PointerField_Of_LaterData()
    {
        Assert.Equal(42, Int("""
namespace Test
struct Holder { p: *Cell }
struct Cell { value: int }
func go() -> int {
    let h = Holder { p: new Cell { value: 42 } }
    return h.p.value
}
"""));
    }

    [Fact]
    public void Data_Field_Of_LaterChoice()
    {
        Assert.Equal(5, Int("""
namespace Test
struct Wrap { c: Pick }
union Pick { a(n: int)  b(n: int) }
func go() -> int {
    let w = Wrap { c: Pick.a(5) }
    match w.c {
        .a(x) { return x }
        .b(x) { return x }
    }
}
"""));
    }

    [Fact]
    public void Data_Field_Of_LaterEnum()
    {
        Assert.Equal(2, Int("""
namespace Test
struct Row { d: Dir }
enum Dir { north  south  east }
func go() -> int {
    let r = Row { d: Dir.east() }
    match r.d {
        .north { return 0 }
        .south { return 1 }
        .east  { return 2 }
    }
}
"""));
    }

    [Fact]
    public void Choice_Payload_Of_LaterData()
    {
        // value choice payload that names a data declared afterward.
        Assert.Equal(9, Int("""
namespace Test
union Box { full(p: Item)  empty }
struct Item { weight: int }
func go() -> int {
    let b = Box.full(Item { weight: 9 })
    match b {
        .full(c) { return c.p.weight }
        .empty   { return 0 }
    }
}
"""));
    }

    [Fact]
    public void RefChoice_Payload_Of_LaterData()
    {
        Assert.Equal(3, Int("""
namespace Test
ref union Node { leaf  branch(items: List<Leaf>) }
struct Leaf { v: int }
func go() -> int {
    let xs = List<Leaf>()
    xs.Add(Leaf { v: 1 })
    xs.Add(Leaf { v: 2 })
    xs.Add(Leaf { v: 3 })
    let n = Node.branch(xs)
    match n {
        .leaf      { return 0 }
        .branch(c) { return c.items.Count }
    }
}
"""));
    }

    [Fact]
    public void Data_And_RefChoice_MutualCycle()
    {
        // The JSON shape: a value `data` holds a `ref choice`, and one of the choice's
        // cases holds a `List<data>`. Neither order can satisfy both without two phases.
        Assert.Equal(2, Int("""
namespace Test
struct Field { key: string, value: J }
ref union J {
    jnull
    jobj(fields: List<Field>)
}
func go() -> int {
    let fs = List<Field>()
    fs.Add(Field { key: "a", value: J.jnull() })
    fs.Add(Field { key: "b", value: J.jnull() })
    let j = J.jobj(fs)
    match j {
        .jnull    { return 0 }
        .jobj(c)  { return c.fields.Count }
    }
}
"""));
    }

    [Fact]
    public void Choice_Payload_Of_LaterChoice()
    {
        Assert.Equal(11, Int("""
namespace Test
union Outer { wraps(inner: Inner)  none }
union Inner { has(n: int)  nothing }
func go() -> int {
    let o = Outer.wraps(Inner.has(11))
    match o {
        .wraps(c) {
            match c.inner {
                .has(x)   { return x }
                .nothing  { return 0 }
            }
        }
        .none { return 0 }
    }
}
"""));
    }

    [Fact]
    public void RefData_Inherits_LaterOpenBase()
    {
        // Derived `class` declared before its `open` base — base class reference
        // must resolve across the forward gap, and base-ctor chaining must bind.
        Assert.Equal(7, Int("""
namespace Test
class Dog : Animal {
    extra: int
    init(e: int) : base(4) { self.extra = e }
}
open class Animal {
    legs: int
    init(l: int) { self.legs = l }
}
func go() -> int {
    let d = Dog(3)
    return d.legs + d.extra
}
"""));
    }

    [Fact]
    public void Data_Embeds_LaterData()
    {
        // Struct embedding of a type declared afterward — promoted field access.
        Assert.Equal(40, Int("""
namespace Test
struct Transform {
    Vec2
    scale: int
}
struct Vec2 {
    var x: int
    var y: int
}
func go() -> int {
    var t = Transform { x: 10, y: 20, scale: 5 }
    t.x += 5
    return t.x + t.y + t.scale   // 15 + 20 + 5
}
"""));
    }

    [Fact]
    public void EndToEnd_ForwardDeclared_JsonLike()
    {
        // A miniature serializer with every declaration written AFTER its first use:
        // the consuming function first, then the choice, then the data it references.
        Assert.Equal("[1,2,3]", Str("""
namespace Test
using "System.Text"

func render(v: Val) -> string {
    match v {
        .num(c) { return "{c.value}" }
        .arr(c) {
            let sb = StringBuilder()
            sb.Append("[")
            var i = 0
            while i < c.items.Count {
                if i > 0 { sb.Append(",") }
                sb.Append(render(c.items[i]))
                i += 1
            }
            sb.Append("]")
            return sb.ToString()
        }
    }
}

ref union Val {
    num(value: int)
    arr(items: List<Val>)
}

func go() -> string {
    let xs = List<Val>()
    xs.Add(Val.num(1))
    xs.Add(Val.num(2))
    xs.Add(Val.num(3))
    return render(Val.arr(xs))
}
"""));
    }
}
