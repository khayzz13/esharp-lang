namespace Esharp.Tests;

/// <summary>
/// Method chaining ("fluent" APIs). A method on a <c>class</c> that returns its receiver
/// (<c>-> Self</c>) chains, because a reference type returns the SAME object — so each call
/// mutates and hands back the one instance, no rebinding of the receiver. Covers single-line
/// and multi-line leading-dot layouts, if/else dispatch and a nested enum <c>match</c> inside
/// a chained call (the turtle), and the value-<c>data</c> transformation chain.
/// </summary>
public sealed class FluentChainTests
{
    static int Int(string src) => (int)EsHarness.Run(src, "go")!;

    [Fact]
    public void RefData_SelfReturn_ChainsOnOneLine()
    {
        Assert.Equal(10, Int("""
namespace Test
class Acc {
    var n: int
    init() { self.n = 0 }
}
func (a: Acc) add(x: int) -> Acc {
    a.n += x
    return a
}
func go() -> int {
    let a = Acc().add(5).add(3).add(2)
    return a.n
}
"""));
    }

    [Fact]
    public void RefData_SelfReturn_MultiLineLeadingDot()
    {
        Assert.Equal(10, Int("""
namespace Test
class Acc {
    var n: int
    init() { self.n = 0 }
}
func (a: Acc) add(x: int) -> Acc {
    a.n += x
    return a
}
func go() -> int {
    let a = Acc()
        .add(5)
        .add(3)
        .add(2)
    return a.n
}
"""));
    }

    [Fact]
    public void Chain_PreservesIdentity_SameObject()
    {
        // The chain returns the same instance it started with: mutate via the chain
        // result, observe through the original binding.
        Assert.Equal(88, Int("""
namespace Test
class Acc {
    var n: int
    init() { self.n = 0 }
}
func (a: Acc) add(x: int) -> Acc {
    a.n += x
    return a
}
func go() -> int {
    let a = Acc()
    let b = a.add(40).add(4)
    return a.n + b.n      // 44 + 44 — a and b are the same object
}
"""));
    }

    [Fact]
    public void ChainedCall_WithIfGuardedMutation()
    {
        // An if-guarded field write inside a chained .forward — the guard reads a field
        // on the same receiver the chain threads.
        Assert.Equal(8, Int("""
namespace Test
union Command { forward(steps: int) }
class Turtle {
    var y: int
    var facing: int
    init() { self.y = 0  self.facing = 0 }
}
func (t: Turtle) apply(cmd: Command) -> Turtle {
    match cmd {
        .forward(steps) {
            if t.facing == 0 { t.y += steps }
        }
    }
    return t
}
func go() -> int {
    let t = Turtle().apply(Command.forward(5)).apply(Command.forward(3))
    return t.y
}
"""));
    }

    [Fact]
    public void ChainedCall_WithNestedEnumMatch_UpdatesField()
    {
        // A .turn command whose arm runs a nested `match (enum)` that mutates `facing`.
        Assert.Equal(1, Int("""
namespace Test
enum Turn { left  right }
union Command { turn(direction: Turn) }
class Turtle {
    var facing: int
    init() { self.facing = 0 }
}
func (t: Turtle) apply(cmd: Command) -> Turtle {
    match cmd {
        .turn(direction) {
            match (direction: Turn) {
                .left  { t.facing = (t.facing + 3) % 4 }
                .right { t.facing = (t.facing + 1) % 4 }
            }
        }
    }
    return t
}
func go() -> int {
    let t = Turtle().apply(Command.turn(Turn.right()))
    return t.facing
}
"""));
    }

    [Fact]
    public void Turtle_FullDrive_FluentChain()
    {
        // The full turtle: forward (heading-dependent) + turn (enum), driven by one chain.
        // start (0,0) facing N → fwd5 (0,5) → R(E) → fwd3 (3,5) → R(S) → fwd2 (3,3) → 303
        Assert.Equal(303, Int("""
namespace Test
enum Turn { left  right }
union Command { forward(steps: int)  turn(direction: Turn) }
class Turtle {
    var x: int
    var y: int
    var facing: int
    init() { self.x = 0  self.y = 0  self.facing = 0 }
}
func (t: Turtle) apply(cmd: Command) -> Turtle {
    match cmd {
        .forward(steps) {
            if t.facing == 0 { t.y += steps }
            else if t.facing == 1 { t.x += steps }
            else if t.facing == 2 { t.y -= steps }
            else { t.x -= steps }
        }
        .turn(direction) {
            match (direction: Turn) {
                .left  { t.facing = (t.facing + 3) % 4 }
                .right { t.facing = (t.facing + 1) % 4 }
            }
        }
    }
    return t
}
func go() -> int {
    let t = Turtle()
        .apply(Command.forward(5))
        .apply(Command.turn(Turn.right()))
        .apply(Command.forward(3))
        .apply(Command.turn(Turn.right()))
        .apply(Command.forward(2))
    return t.x * 100 + t.y
}
"""));
    }

    [Fact]
    public void ValueData_TransformChain()
    {
        // Value `data` chains too — each step returns a fresh value (transformation,
        // not mutation), like Vec.add(b).scaled(2).
        Assert.Equal(40, Int("""
namespace Test
struct Vec { x: int, y: int }
func (v: Vec) add(o: Vec) -> Vec = Vec { x: v.x + o.x, y: v.y + o.y }
func (v: Vec) scaled(k: int) -> Vec = Vec { x: v.x * k, y: v.y * k }
func go() -> int {
    let r = (Vec { x: 3, y: 2 }).add(Vec { x: 1, y: 4 }).scaled(4)
    return r.x + r.y    // (4,6)*4 = (16,24) → 40
}
"""));
    }
}
