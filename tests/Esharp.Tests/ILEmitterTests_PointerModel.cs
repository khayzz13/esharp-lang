// Style note: prefer readable """ raw-string blocks for E# source (these tests
// double as the E# corpus) — do NOT use inline \n-escaped one-liners.
namespace Esharp.Tests;

/// The unified `*T` pointer model: one Go-pointer semantic, a compiler-chosen CLR
/// representation (escape-analysis picks the `__Ptr_T` wrapper vs the managed
/// pointer `ref T`), call-site coercion between the two, the escaping-`&local`
/// hoist, `*refData` as an error, and escaping-primitive support. The
/// representation is invisible to the source — every test asserts *behavior*.
public sealed class ILEmitterTests_PointerModel
{
    // A non-escaping by-ref parameter downgrades to a managed pointer and must
    // still alias the caller's local — `bump(&c)` mutates `c` in place.
    [Fact]
    public void Downgrade_ByRefParam_AliasesCallerLocal()
    {
        const string src = """
namespace Test

struct Counter {
    var value: int
}

func bump(p: *Counter) {
    p.value += 1
}

func go() -> int {
    var c = Counter { value: 41 }
    bump(&c)
    return c.value
}
""";
        Assert.Equal(42, EsHarness.Run(src, "go"));
    }

    // Two mutating calls through a downgraded by-ref parameter accumulate on the
    // same caller storage.
    [Fact]
    public void Downgrade_ByRefParam_RepeatedMutationAccumulates()
    {
        const string src = """
namespace Test

struct Acc {
    var n: int
}

func add(a: *Acc, amount: int) {
    a.n += amount
}

func go() -> int {
    var acc = Acc { n: 0 }
    add(&acc, 10)
    add(&acc, 20)
    add(&acc, 12)
    return acc.n
}
""";
        Assert.Equal(42, EsHarness.Run(src, "go"));
    }

    // A `*T` parameter that escapes (stored into a heap node) keeps the wrapper
    // representation, so a nil head and a recursive build both work.
    [Fact]
    public void Escaping_Param_StaysWrapper_RecursiveBuild()
    {
        const string src = """
namespace Test

struct Node {
    value: int
    next: *Node
}

func prepend(head: *Node, v: int) -> *Node {
    return new Node { value: v, next: head }
}

func go() -> int {
    var list: *Node = nil
    list = prepend(list, 1)
    list = prepend(list, 2)
    list = prepend(list, 3)
    var total = 0
    var cur = list
    while cur != nil {
        total += cur.value
        cur = cur.next
    }
    return total
}
""";
        Assert.Equal(6, EsHarness.Run(src, "go"));
    }

    // A `*T` parameter compared against nil is nullable, forcing the wrapper
    // representation even though it is otherwise non-escaping.
    [Fact]
    public void NullCompared_Param_ForcesWrapper()
    {
        const string src = """
namespace Test

struct Box {
    var v: int
}

func valueOr(b: *Box, fallback: int) -> int {
    if b == nil {
        return fallback
    }
    return b.v
}

func go() -> int {
    let present = new Box { v: 7 }
    let a = valueOr(present, 99)
    let nothing: *Box = nil
    let bb = valueOr(nothing, 99)
    return a + bb
}
""";
        Assert.Equal(106, EsHarness.Run(src, "go"));
    }

    // Regression: passing a wrapper `*T` with `*p` ref-pass syntax to a `*T`
    // parameter used to `ldflda` a reference type → invalid IL. Coercion is now
    // representation-driven, so the wrapper is unwrapped (or passed) correctly.
    [Fact]
    public void Coercion_WrapperPassedWithStarSyntax()
    {
        const string src = """
namespace Test

struct Cell {
    var n: int
}

func inc(c: *Cell) {
    c.n += 1
}

func go() -> int {
    var p: *Cell = new Cell { n: 10 }
    inc(*p)
    inc(p)
    return p.n
}
""";
        Assert.Equal(12, EsHarness.Run(src, "go"));
    }

    // A wrapper-form `*T` (from `new T{}`) handed to a downgraded `ref T` parameter
    // is unwrapped at the call site and mutated in place.
    [Fact]
    public void Coercion_WrapperArg_ToByRefParam_Mutates()
    {
        const string src = """
namespace Test

struct Vec {
    var x: int
    var y: int
}

func shift(v: *Vec, dx: int, dy: int) {
    v.x += dx
    v.y += dy
}

func go() -> int {
    var v: *Vec = new Vec { x: 1, y: 2 }
    shift(v, 10, 20)
    return v.x + v.y
}
""";
        Assert.Equal(33, EsHarness.Run(src, "go"));
    }

    // `*T` where T is a `class` is illegal (a class is already a reference).
    [Fact]
    public void StarRefData_IsError()
    {
        const string src = """
namespace Test

class Session {
    id: int
}

func (s: *Session) touch() {
    s.id = 1
}
""";
        var diags = EsHarness.Diagnostics(src);
        Assert.Contains(diags, d => d.Message.Contains("ES2003") && d.Message.Contains("Session"));
    }

    // A `*primitive` field is the wrapper form (nullable, heap) — it used to be
    // silently degraded to a bare value.
    [Fact]
    public void PrimitivePointer_Field_IsNullableWrapper()
    {
        const string src = """
namespace Test

class Slot {
    p: *int
}

func go() -> bool {
    var s = Slot()
    return s.p == nil
}
""";
        Assert.Equal(true, EsHarness.Run(src, "go"));
    }

    // Taking `&local` and storing it into a heap field — the forward value reads
    // back correctly (the local's value lives on the heap via the wrapper).
    [Fact]
    public void EscapingAddressOfLocal_ForwardValue()
    {
        const string src = """
namespace Test

struct P {
    var x: int
}

class H {
    slot: *P
}

func go() -> int {
    var local = P { x: 5 }
    var h = H()
    h.slot = &local
    return h.slot.x
}
""";
        Assert.Equal(5, EsHarness.Run(src, "go"));
    }

    // An escaped `&local` aliases the local: mutating the local after its address
    // escaped is visible through the escaped pointer (Go pointer semantics).
    [Fact]
    public void EscapingAddressOfLocal_AliasesLocal()
    {
        const string src = """
namespace Test

struct P {
    var x: int
}

class H {
    slot: *P
}

func go() -> int {
    var local = P { x: 5 }
    var h = H()
    h.slot = &local
    local.x = 99
    return h.slot.x
}
""";
        Assert.Equal(99, EsHarness.Run(src, "go"));
    }

    // Returning `&local` heap-promotes the local; the caller reads through the
    // returned wrapper after the callee frame is gone.
    [Fact]
    public void ReturnHoist_AddressOfLocal()
    {
        const string src = """
namespace Test

struct Node {
    value: int
    next: *Node
}

func makeNode(v: int) -> *Node {
    var n = Node { value: v, next: nil }
    return &n
}

func go() -> int {
    let p = makeNode(7)
    return p.value
}
""";
        Assert.Equal(7, EsHarness.Run(src, "go"));
    }

    // Two escaped pointers to the same heap-promoted local both observe a later
    // mutation — single shared cell.
    [Fact]
    public void EscapingAddressOfLocal_TwoAliasesShareCell()
    {
        const string src = """
namespace Test

struct P {
    var x: int
}

class Pair {
    a: *P
    b: *P
}

func go() -> int {
    var local = P { x: 1 }
    var pr = Pair()
    pr.a = &local
    pr.b = &local
    local.x = 50
    return pr.a.x + pr.b.x
}
""";
        Assert.Equal(100, EsHarness.Run(src, "go"));
    }

    // Mixed method set: a value-receiver read and a pointer-receiver mutation on
    // the same `*T`, both downgraded, both aliasing.
    [Fact]
    public void MixedReceivers_Downgraded_Alias()
    {
        const string src = """
namespace Test

struct Reg {
    var hi: int
    var lo: int
}

func raise(r: *Reg, by: int) {
    r.hi += by
}

func (r: Reg) total() -> int {
    return r.hi + r.lo
}

func go() -> int {
    var r = Reg { hi: 10, lo: 5 }
    raise(&r, 7)
    return r.total()
}
""";
        Assert.Equal(22, EsHarness.Run(src, "go"));
    }
}
