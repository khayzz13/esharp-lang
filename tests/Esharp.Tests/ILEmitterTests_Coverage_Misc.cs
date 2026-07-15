// Style note: E# source uses readable """ raw-string blocks (these double as the
// E# corpus) — do not collapse them into inline \n-escaped one-liners.
namespace Esharp.Tests;

/// Async, concurrency, BCL interop, ranges and assorted feature coverage.
public sealed class ILEmitterTests_Coverage_Misc
{
    static object? Run(string src, string method = "go") => EsHarness.Run(src, method);
    static object? Await(string src, string method = "go") =>
        EsHarness.Await(EsHarness.Invoke(EsHarness.Compile(src), method));

    [Fact]
    public void Await_TaskFromResult()
    {
        Assert.Equal(42, Await("""
namespace Test
func go() -> int {
    let v = await Task.FromResult(42)
    return v
}
"""));
    }

    [Fact]
    public void Await_TwoSequential()
    {
        Assert.Equal(30, Await("""
namespace Test
func go() -> int {
    let a = await Task.FromResult(10)
    let b = await Task.FromResult(20)
    return a + b
}
"""));
    }

    [Fact]
    public void AsyncLet_Fanout()
    {
        Assert.Equal(42, Await("""
namespace Test
func go() -> int {
    async let a = Task.FromResult(20)
    async let b = Task.FromResult(22)
    return a + b
}
"""));
    }

    [Fact]
    public void TaskFunc_SpawnAndWait()
    {
        Assert.Equal(42, Run("""
namespace Test
task func produce() -> int {
    return 42
}
func go() -> int {
    let job = produce()
    return job.Wait()
}
"""));
    }

    [Fact]
    public void TryCatch_ConvertsException()
    {
        Assert.Equal(-1, Run("""
namespace Test
func go() -> int {
    var result = 0
    try {
        result = int.Parse("not a number")
    } catch (e: Exception) {
        result = 0 - 1
    }
    return result
}
"""));
    }

    [Fact]
    public void TryCatch_Succeeds()
    {
        Assert.Equal(123, Run("""
namespace Test
func go() -> int {
    var result = 0
    try {
        result = int.Parse("123")
    } catch {
        result = 0 - 1
    }
    return result
}
"""));
    }

    [Theory]
    [InlineData("Math.Max(3, 7)", 7)]
    [InlineData("Math.Min(3, 7)", 3)]
    [InlineData("Math.Abs(0 - 5)", 5)]
    [InlineData("int.Parse(\"256\")", 256)]
    public void BclStatic(string expr, int expected)
    {
        Assert.Equal(expected, Run("namespace Test\nfunc go() -> int {\n  return " + expr + "\n}"));
    }

    [Fact]
    public void Bcl_StringBuilder()
    {
        Assert.Equal(6, Run("""
namespace Test
func go() -> int {
    let sb = StringBuilder()
    sb.Append("foo")
    sb.Append("bar")
    return sb.ToString().Length
}
"""));
    }

    [Fact]
    public void Bcl_ListAddSum()
    {
        Assert.Equal(6, Run("""
namespace Test
func go() -> int {
    let xs = List<int>()
    xs.Add(1)
    xs.Add(2)
    xs.Add(3)
    var total = 0
    for x in xs {
        total += x
    }
    return total
}
"""));
    }

    [Fact]
    public void Linq_Sum()
    {
        Assert.Equal(10, Run("""
namespace Test
func go() -> int {
    let xs = [1, 2, 3, 4]
    return xs.Sum()
}
"""));
    }

    [Fact]
    public void OutParam_TryParse()
    {
        Assert.Equal(77, Run("""
namespace Test
func go() -> int {
    if int.TryParse("77", out var n) {
        return n
    }
    return 0
}
"""));
    }

    [Fact]
    public void Range_IndexFromEnd()
    {
        Assert.Equal(30, Run("""
namespace Test
func go() -> int {
    let xs = [10, 20, 30]
    return xs[^1]
}
"""));
    }

    [Fact]
    public void Range_IndexFromEnd_Second()
    {
        Assert.Equal(20, Run("""
namespace Test
func go() -> int {
    let xs = [10, 20, 30]
    return xs[^2]
}
"""));
    }

    [Fact]
    public void NullableValueLetElse()
    {
        Assert.Equal(49, Run("""
namespace Test
func find(id: int) -> int? {
    if id == 0 {
        return nil
    }
    return id * 10
}
func lookup(id: int) -> int {
    let v = find(id) else {
        return 0 - 1
    }
    return v
}
func go() -> int {
    return lookup(5) + lookup(0)
}
"""));
    }

    [Fact]
    public void NestedData_FieldChain()
    {
        Assert.Equal(7, Run("""
namespace Test
struct Inner { v: int }
struct Outer { inner: Inner, tag: int }
func go() -> int {
    let o = Outer { inner: Inner { v: 4 }, tag: 3 }
    return o.inner.v + o.tag
}
"""));
    }

    [Fact]
    public void Spawn_ChannelProducerConsumer()
    {
        Assert.Equal(6, Run("""
namespace Test
func go() -> int {
    let ch = chan<int>(8)
    let producer = spawn {
        ch.Send(1)
        ch.Send(2)
        ch.Send(3)
        ch.Close()
    }
    producer.Wait()
    var total = 0
    for v in ch {
        total += v
    }
    return total
}
"""));
    }

    [Fact]
    public void Pointer_LinkedListLength()
    {
        Assert.Equal(3, Run("""
namespace Test
struct Node {
    value: int
    next: *Node
}
func go() -> int {
    var head: *Node = new Node { value: 1, next: nil }
    head = new Node { value: 2, next: head }
    head = new Node { value: 3, next: head }
    var len = 0
    var cur = head
    while cur != nil {
        len += 1
        cur = cur.next
    }
    return len
}
"""));
    }

    [Fact]
    public void Pointer_ByRefIntMutation()
    {
        Assert.Equal(15, Run("""
namespace Test
func addTen(x: *int) {
    x += 10
}
func go() -> int {
    var n = 5
    addTen(&n)
    return n
}
"""));
    }

    [Fact]
    public void Enum_MatchWithDefault()
    {
        Assert.Equal(1, Run("""
namespace Test
enum Dir { north, south, east, west }
func go() -> int {
    let d = Dir.north()
    match (d: Dir) {
        .north { return 1 }
        default { return 0 }
    }
    return -1
}
"""));
    }

    [Fact]
    public void Enum_ToString_BoxesForReferenceBaseDispatch()
    {
        Assert.Equal("north", Run("""
namespace Test
enum Dir { north, south }
func go() -> string = Dir.north().ToString().ToLower()
"""));
    }

    [Fact]
    public void Recursion_Factorial()
    {
        Assert.Equal(120, Run("""
namespace Test
func fact(n: int) -> int {
    if n <= 1 {
        return 1
    }
    return n * fact(n - 1)
}
func go() -> int {
    return fact(5)
}
"""));
    }

    [Fact]
    public void Recursion_Fibonacci()
    {
        Assert.Equal(13, Run("""
namespace Test
func fib(n: int) -> int {
    if n < 2 {
        return n
    }
    return fib(n - 1) + fib(n - 2)
}
func go() -> int {
    return fib(7)
}
"""));
    }

    [Fact]
    public void Interface_Conformance()
    {
        Assert.Equal(11, Run("""
namespace Test
interface ISized {
    func size() -> int
}
struct Crate : ISized {
    items: int
}
func (c: Crate) size() -> int {
    return c.items + 1
}
func measure(s: ISized) -> int {
    return s.size()
}
func go() -> int {
    let c = Crate { items: 10 }
    return measure(c)
}
"""));
    }
}
