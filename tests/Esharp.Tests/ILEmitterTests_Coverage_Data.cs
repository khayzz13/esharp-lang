// Style note: E# source uses readable """ raw-string blocks (these double as the
// E# corpus) — do not collapse them into inline \n-escaped one-liners.
namespace Esharp.Tests;

/// data / generics / tuples / collections / closures / with / derive coverage.
public sealed class ILEmitterTests_Coverage_Data
{
    static object? Run(string src) => EsHarness.Run(src, "go");

    [Fact]
    public void Data_ConstructAndFieldAccess()
    {
        Assert.Equal(30, Run("""
namespace Test
struct Point { x: int, y: int }
func go() -> int {
    let p = Point { x: 10, y: 20 }
    return p.x + p.y
}
"""));
    }

    [Fact]
    public void Data_PositionalConstruction()
    {
        Assert.Equal(7, Run("""
namespace Test
struct Vec2(x: int, y: int)
func go() -> int {
    let v = Vec2(3, 4)
    return v.x + v.y
}
"""));
    }

    [Fact]
    public void Data_MutableField()
    {
        Assert.Equal(15, Run("""
namespace Test
struct Counter { var n: int }
func go() -> int {
    var c = Counter { n: 10 }
    c.n += 5
    return c.n
}
"""));
    }

    [Fact]
    public void Data_InstanceMethodPromotion()
    {
        Assert.Equal(25, Run("""
namespace Test
struct Sq { side: int }
func (s: Sq) area() -> int {
    return s.side * s.side
}
func go() -> int {
    let s = Sq { side: 5 }
    return s.area()
}
"""));
    }

    [Fact]
    public void Data_FactoryFunction()
    {
        Assert.Equal(42, Run("""
namespace Test
struct Box { v: int }
func makeBox(n: int) -> Box {
    return Box { v: n * 2 }
}
func go() -> int {
    return makeBox(21).v
}
"""));
    }

    [Fact]
    public void Generic_Pair()
    {
        Assert.Equal(8, Run("""
namespace Test
struct Pair<A, B> { first: A, second: B }
func go() -> int {
    let p = Pair<int, int> { first: 3, second: 5 }
    return p.first + p.second
}
"""));
    }

    [Fact]
    public void Generic_IdentityFunction()
    {
        Assert.Equal(99, Run("""
namespace Test
func identity<T>(value: T) -> T {
    return value
}
func go() -> int {
    return identity<int>(99)
}
"""));
    }

    [Fact]
    public void Generic_SwapPair()
    {
        Assert.Equal(1, Run("""
namespace Test
struct Pair<A, B> { first: A, second: B }
func go() -> int {
    let p = Pair<int, int> { first: 2, second: 1 }
    let q = Pair<int, int> { first: p.second, second: p.first }
    return q.first
}
"""));
    }

    [Fact]
    public void ReadonlyData_With()
    {
        Assert.Equal(13, Run("""
namespace Test
readonly struct P { x: int, y: int }
func go() -> int {
    let a = P { x: 3, y: 4 }
    let b = a with { x: 9 }
    return b.x + b.y
}
"""));
    }

    [Fact]
    public void Tuple_Construction()
    {
        Assert.Equal(7, Run("""
namespace Test
func go() -> int {
    let t = (3, 4)
    return t.Item1 + t.Item2
}
"""));
    }

    [Fact]
    public void Tuple_DestructuringInLet()
    {
        Assert.Equal(5, Run("""
namespace Test
func swap(a: int, b: int) -> (int, int) {
    return (b, a)
}
func go() -> int {
    let (x, y) = swap(2, 3)
    return x + y
}
"""));
    }

    [Fact]
    public void Collection_ListLiteralIndex()
    {
        Assert.Equal(60, Run("""
namespace Test
func go() -> int {
    let xs = [10, 20, 30]
    return xs[0] + xs[1] + xs[2]
}
"""));
    }

    [Fact]
    public void Collection_ListCount()
    {
        Assert.Equal(4, Run("""
namespace Test
func go() -> int {
    let xs = [1, 2, 3, 4]
    return xs.Count
}
"""));
    }

    [Fact]
    public void Collection_ForInSum()
    {
        Assert.Equal(15, Run("""
namespace Test
func go() -> int {
    let xs = [1, 2, 3, 4, 5]
    var total = 0
    for x in xs {
        total += x
    }
    return total
}
"""));
    }

    [Fact]
    public void Closure_MutableCapture()
    {
        Assert.Equal(2, Run("""
namespace Test
func go() -> int {
    var total = 0
    let inc = func() {
        total = total + 1
    }
    inc()
    inc()
    return total
}
"""));
    }

    [Fact]
    public void Closure_Accumulator()
    {
        Assert.Equal(15, Run("""
namespace Test
func go() -> int {
    var sum = 0
    let addTo = func(value: int) {
        sum = sum + value
    }
    for i in 1..6 {
        addTo(i)
    }
    return sum
}
"""));
    }

    [Fact]
    public void Lambda_ArrowExpression()
    {
        Assert.Equal(36, Run("""
namespace Test
func apply(x: int, f: Func<int, int>) -> int = f(x)
func go() -> int {
    return apply(6, (x) => x * x)
}
"""));
    }

    [Fact]
    public void FunctionPointer_CallThroughLocal()
    {
        Assert.Equal(7, Run("""
namespace Test
func add(a: int, b: int) -> int {
    return a + b
}
func go() -> int {
    let p = &add
    return p(3, 4)
}
"""));
    }

    [Fact]
    public void Defer_RunsInLifoOrder()
    {
        Assert.Equal(12, Run("""
namespace Test
struct Box { var n: int }
func go() -> int {
    var b: *Box = new Box { n: 0 }
    bump(b)
    return b.n
}
func bump(b: *Box) {
    defer { b.n += 2 }
    defer { b.n *= 5 }
    b.n = 2
}
"""));
        // n=2, then *5 => 10, then +2 => 12 (LIFO)
    }

    [Fact]
    public void Derive_Equality()
    {
        Assert.Equal(true, Run("""
namespace Test
derive equality
struct P { x: int, y: int }
func go() -> bool {
    let a = P { x: 1, y: 2 }
    let b = P { x: 1, y: 2 }
    return a == b
}
"""));
    }

    [Fact]
    public void Derive_Equality_Distinct()
    {
        Assert.Equal(false, Run("""
namespace Test
derive equality
struct P { x: int, y: int }
func go() -> bool {
    let a = P { x: 1, y: 2 }
    let b = P { x: 1, y: 9 }
    return a == b
}
"""));
    }

    [Fact]
    public void StructEmbedding_PromotedAccess()
    {
        Assert.Equal(35, Run("""
namespace Test
struct Vec2 {
    var x: int
    var y: int
}
struct Transform {
    Vec2
    var scale: int
}
func go() -> int {
    var t = Transform { x: 10, y: 20, scale: 5 }
    t.x += 5
    return t.x + t.y
}
"""));
    }

    [Fact]
    public void NullCoalescing_Fallback()
    {
        Assert.Equal(8, Run("""
namespace Test
func find(id: int) -> string? {
    if id == 0 { return nil }
    return "x"
}
func go() -> int {
    let s = find(0) ?? "fallback"
    return s.Length
}
"""));
    }

    [Fact]
    public void NullConditional_Access()
    {
        Assert.Equal(0, Run("""
namespace Test
func go() -> int {
    let s: string? = nil
    return s?.Length ?? 0
}
"""));
    }

    [Fact]
    public void StaticFunc_Bag()
    {
        Assert.Equal(true, Run("""
namespace Test
static Password {
    let MIN: int = 8
    func strong(s: string) -> bool {
        return s.Length > MIN
    }
}
func go() -> bool {
    return Password.strong("hunter2hunter2")
}
"""));
    }

    [Fact]
    public void ExpressionBodied_Function()
    {
        Assert.Equal(20, Run("""
namespace Test
func double(x: int) -> int = x * 2
func go() -> int {
    return double(10)
}
"""));
    }

    [Fact]
    public void Dictionary_PutGet()
    {
        Assert.Equal(42, Run("""
namespace Test
func go() -> int {
    let d = Dictionary<string, int>()
    d["answer"] = 42
    return d["answer"]
}
"""));
    }

    [Fact]
    public void RefData_IdentityMutation()
    {
        Assert.Equal(3, Run("""
namespace Test
class Counter {
    var value: int
    init() { self.value = 0 }
    func inc() { self.value += 1 }
}
func go() -> int {
    let c = Counter()
    c.inc()
    c.inc()
    c.inc()
    return c.value
}
"""));
    }

    [Fact]
    public void RefData_Inheritance_Override()
    {
        Assert.Equal(25, Run("""
namespace Test
abstract class Shape {
    init() { }
    abstract func area() -> int
}
class Square : Shape {
    side: int
    init(s: int) : base() { self.side = s }
    : func area() -> int { return self.side * self.side }
}
func go() -> int {
    let sq = Square(5)
    return sq.area()
}
"""));
    }
}
