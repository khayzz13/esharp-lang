namespace Esharp.Tests;

/// Regression tests for compiler gaps surfaced by a production-shaped ASP.NET E#
/// program. Each test is a minimal, BCL-only reproduction of one gap.
///
/// These follow TDD: some pass already (the gaps fixed so far — A, 5a), the rest are
/// RED until their phase lands, then flip green. They are intentionally NOT skipped —
/// a red here is the to-do list. `EsHarness.Compile` runs ILVerify, so a gap that emits
/// unverifiable IL fails the assert; the fixed gaps additionally assert runtime behavior.
public sealed class SymbolSpineTests
{
    // GAP A — a `*T` in a generic-argument position must materialize as the heap wrapper
    // `__Ptr_T`, never the managed pointer `T&` (which is illegal as a generic argument).
    // The field/collection side is always the wrapper; the generic-call side must agree.
    [Fact]
    public void PointerGenerics_PointerAsGenericArgument_UnifiesToWrapper()
    {
        var src = @"
namespace Test
struct Box { var v: int }
func head<T>(xs: List<T>) -> T { return xs[0] }
func gapA() -> int {
    let xs = List<*Box>()
    xs.Add(new Box { v: 42 })
    let b = head<*Box>(xs)
    return b.v
}";
        Assert.Equal(42, EsHarness.Run(src, "gapA"));
    }

    // GAP B — a reified generic function's delegate parameter must carry the explicit type
    // argument, not erase it to `object`. `tally<*Box>(xs, (w) => w.v > 0)` must bind the
    // lambda parameter `w` as `*Box`, so `w.v` resolves (not `object.v`).
    [Fact]
    public void PointerGenerics_GenericDelegateParam_DoesNotEraseToObject()
    {
        var src = @"
namespace Test
struct Box { var v: int }
func tally<T>(xs: List<T>, pred: Func<T, bool>) -> int {
    var c = 0
    for it in xs { if pred(it) { c += 1 } }
    return c
}
func gapB() -> int {
    let xs = List<*Box>()
    xs.Add(new Box { v: 3 })
    xs.Add(new Box { v: 0 })
    xs.Add(new Box { v: 7 })
    return tally<*Box>(xs, (w) => w.v > 0)
}";
        Assert.Equal(2, EsHarness.Run(src, "gapB"));
    }

    // GAP 1 — a value-receiver promoted method (`func f(x: T)`) is in `*T`'s method set too
    // (auto-deref), so it must be callable on a `*T` receiver.
    [Fact]
    public void PointerMethods_ValueReceiverMethod_CallableOnPointer()
    {
        var src = @"
namespace Test
struct Box { var v: int }
func (b: Box) tenfold() -> int = b.v * 10
func gap1() -> int {
    let p = new Box { v: 5 }
    return p.tenfold()
}";
        Assert.Equal(50, EsHarness.Run(src, "gap1"));
    }

    // GAP 4 — a `self.sibling(...)` call to another instance method that returns `Task`
    // (async) must resolve. The async rewrite must not hide the method from sibling lookup.
    [Fact]
    public void AsyncMethods_SelfAsyncSiblingCall_Resolves()
    {
        var src = @"
namespace Test
class Svc {
    var n: int
    init() { self.n = 0 }
    func work() -> Task { await Task.Delay(1) }
    func kick() {
        let running = self.work()
        running.Wait()
    }
}
func gap4() -> int {
    let s = Svc()
    s.kick()
    return 1
}";
        Assert.Equal(1, EsHarness.Run(src, "gap4"));
    }

    // GAP 5a — awaiting a bare (non-generic) `Task` inside a non-void async method must use
    // `TaskAwaiter`, derived from the awaited expression, not `Task<methodReturnType>`.
    [Fact]
    public void AsyncCodegen_AwaitBareTask_InNonVoidAsync()
    {
        var src = @"
namespace Test
func bare() -> Task { await Task.Delay(1) }
func gap5a() -> int {
    await bare()
    return 7
}";
        Assert.Equal(7, EsHarness.Await(EsHarness.Run(src, "gap5a")));
    }

    // GAP 5b — an `await` nested inside a loop + if/else must not place its state-machine
    // resume label where the dispatch branches into a try region (BranchIntoTry).
    [Fact]
    public void AsyncCodegen_AwaitInsideLoopBranch_NoBranchIntoTry()
    {
        var src = @"
namespace Test
func gap5b() -> int {
    var total = 0
    var i = 0
    while i < 3 {
        if i == 1 {
            total += 1
        } else {
            await Task.Delay(1)
            total += 10
        }
        i += 1
    }
    return total
}";
        Assert.Equal(21, EsHarness.Await(EsHarness.Run(src, "gap5b")));
    }

    // GAP 6 — a chained generic external call must carry the prior call's resolved return
    // type, not degrade to `object`, so the next generic extension resolves.
    [Fact]
    public void Interop_ChainedGenericExternalCall_PropagatesType()
    {
        var src = @"
namespace Test
using ""System.Linq""
func gap6() -> int {
    let xs = List<int>()
    xs.Add(1)
    xs.Add(2)
    xs.Add(3)
    return xs.Where((n) => n > 1).Count()
}";
        Assert.Equal(2, EsHarness.Run(src, "gap6"));
    }

    // GAP 7 — a lambda passed to a generic external delegate slot must take the delegate's
    // declared return type, caught at bind time, not deferred to a verify-time mismatch.
    [Fact]
    public void Interop_LambdaIntoGenericExternalDelegate_TypesReturn()
    {
        var src = @"
namespace Test
using ""System.Linq""
func gap7() -> int {
    let xs = List<int>()
    xs.Add(2)
    xs.Add(5)
    let labels = xs.Select((n) => ""n={n}"")
    return labels.Count()
}";
        Assert.Equal(2, EsHarness.Run(src, "gap7"));
    }

    // ── Real-shape variants ──────────────────────────────────────────────────────
    // The gaps above pass on some minimal shapes but fail on the shapes the ASP.NET
    // example actually used — a `for x in List<*T>` loop binds the element as a
    // pointer the value-method path mishandles; `for-in` lowers to a foreach with a
    // try/finally that traps a nested await; a `*T` returned from a function is the
    // wrapper form member access mis-derefs. These reproduce the genuine failures.

    // GAP A (member access) — a `*T` value returned from a function is the heap wrapper;
    // reading a field through it must deref the wrapper, not assume a managed pointer.
    [Fact]
    public void PointerGenerics_MemberAccessThroughReturnedPointer()
    {
        var src = @"
namespace Test
struct Box { var v: int }
func make(n: int) -> *Box = new Box { v: n }
func gapAmember() -> int {
    let p = make(9)
    return p.v
}";
        Assert.Equal(9, EsHarness.Run(src, "gapAmember"));
    }

    // GAP 1 (foreach) — value-receiver promoted method called on the `*T` element of a
    // `for x in List<*T>` loop. The loop binds the element as a pointer receiver.
    [Fact]
    public void PointerMethods_ValueMethodOnForeachPointerElement()
    {
        var src = @"
namespace Test
struct Box { var v: int }
func (b: Box) tenfold() -> int = b.v * 10
func gap1foreach() -> int {
    let xs = List<*Box>()
    xs.Add(new Box { v: 5 })
    xs.Add(new Box { v: 2 })
    var total = 0
    for item in xs { total += item.tenfold() }
    return total
}";
        Assert.Equal(70, EsHarness.Run(src, "gap1foreach"));
    }

    // GAP 1 (pointer-receiver mutate then value-receiver read on the same `*T`).
    [Fact]
    public void PointerMethods_PointerMutateThenValueRead()
    {
        var src = @"
namespace Test
struct Box { var v: int }
func (b: *Box) bump() { b.v += 1 }
func (b: Box) show() -> int = b.v
func gap1mix() -> int {
    let p = new Box { v: 4 }
    p.bump()
    return p.show()
}";
        Assert.Equal(5, EsHarness.Run(src, "gap1mix"));
    }

    // GAP 4 (field assignment) — assign the Task from a `self.asyncSibling()` call into a
    // `Task` field (the hosted-service StartAsync shape: `self.worker = self.drain(...)`).
    [Fact]
    public void AsyncMethods_AsyncSiblingIntoTaskField()
    {
        var src = @"
namespace Test
class Svc {
    var worker: Task
    var n: int
    init() { self.n = 0 }
    func work() -> Task { await Task.Delay(1) }
    func start() { self.worker = self.work() }
}
func gap4field() -> int {
    let s = Svc()
    s.start()
    return 1
}";
        Assert.Equal(1, EsHarness.Run(src, "gap4field"));
    }

    // GAP 4 (start/stop pattern) — a sync method fires an async sibling and stores it; a
    // second method awaits the stored Task. Mirrors IHostedService Start/StopAsync.
    [Fact]
    public void AsyncMethods_StartStorThenAwaitStored()
    {
        var src = @"
namespace Test
class Svc {
    var worker: Task
    var n: int
    init() { self.n = 0 }
    func run() -> Task { await Task.Delay(1) }
    func start() { self.worker = self.run() }
    func stop() -> Task { await self.worker }
}
func gap4ss() -> int {
    let s = Svc()
    s.start()
    let t = s.stop()
    t.Wait()
    return 2
}";
        Assert.Equal(2, EsHarness.Run(src, "gap4ss"));
    }

    // GAP 5b (foreach) — an `await` inside a `for x in xs` loop. The foreach lowers to a
    // try/finally (enumerator dispose); the await's resume label must not be branched into.
    [Fact]
    public void AsyncCodegen_AwaitInsideForeach()
    {
        var src = @"
namespace Test
func gap5bfor() -> int {
    let xs = List<int>()
    xs.Add(1)
    xs.Add(2)
    xs.Add(3)
    var total = 0
    for x in xs {
        await Task.Delay(1)
        total += x
    }
    return total
}";
        Assert.Equal(6, EsHarness.Await(EsHarness.Run(src, "gap5bfor")));
    }

    // GAP 5b (foreach + branch) — await in only one branch of an if/else inside a foreach.
    [Fact]
    public void AsyncCodegen_AwaitInForeachBranch()
    {
        var src = @"
namespace Test
func gap5bbranch() -> int {
    let xs = List<int>()
    xs.Add(1)
    xs.Add(2)
    var total = 0
    for x in xs {
        if x == 1 {
            total += 100
        } else {
            await Task.Delay(1)
            total += x
        }
    }
    return total
}";
        Assert.Equal(102, EsHarness.Await(EsHarness.Run(src, "gap5bbranch")));
    }

    // ── Additional variants on the reliably-failing shapes (B, 5b, A) ────────────

    // GAP B — generic `map` over a `Func<T,int>` with a pointer type argument.
    [Fact]
    public void PointerGenerics_GenericMapPointerArg()
    {
        var src = @"
namespace Test
struct Box { var v: int }
func sumBy<T>(xs: List<T>, f: Func<T, int>) -> int {
    var s = 0
    for it in xs { s += f(it) }
    return s
}
func gapBmap() -> int {
    let xs = List<*Box>()
    xs.Add(new Box { v: 10 })
    xs.Add(new Box { v: 5 })
    return sumBy<*Box>(xs, (w) => w.v)
}";
        Assert.Equal(15, EsHarness.Run(src, "gapBmap"));
    }

    // GAP B — generic identity returning the pointer arg through a delegate that selects it.
    [Fact]
    public void PointerGenerics_GenericPredicateCountPointer()
    {
        var src = @"
namespace Test
struct Box { var v: int }
func countIf<T>(xs: List<T>, keep: Func<T, bool>) -> int {
    var c = 0
    for it in xs { if keep(it) { c += 1 } }
    return c
}
func gapBcount() -> int {
    let xs = List<*Box>()
    xs.Add(new Box { v: 1 })
    xs.Add(new Box { v: 9 })
    xs.Add(new Box { v: 4 })
    return countIf<*Box>(xs, (w) => w.v >= 4)
}";
        Assert.Equal(2, EsHarness.Run(src, "gapBcount"));
    }

    // GAP A — store the `*T` a generic returns into a local, then read through it.
    [Fact]
    public void PointerGenerics_GenericReturnedPointerStoredThenRead()
    {
        var src = @"
namespace Test
struct Box { var v: int }
func pick<T>(xs: List<T>, i: int) -> T { return xs[i] }
func gapApick() -> int {
    let xs = List<*Box>()
    xs.Add(new Box { v: 11 })
    xs.Add(new Box { v: 22 })
    let chosen = pick<*Box>(xs, 1)
    return chosen.v
}";
        Assert.Equal(22, EsHarness.Run(src, "gapApick"));
    }

    // GAP 5b — await inside a range `for` loop.
    [Fact]
    public void AsyncCodegen_AwaitInRangeLoop()
    {
        var src = @"
namespace Test
func gap5brange() -> int {
    var total = 0
    for i in 0..3 {
        await Task.Delay(1)
        total += i
    }
    return total
}";
        Assert.Equal(3, EsHarness.Await(EsHarness.Run(src, "gap5brange")));
    }

    // GAP 5b — await inside a nested foreach.
    [Fact]
    public void AsyncCodegen_AwaitInNestedForeach()
    {
        var src = @"
namespace Test
func gap5bnested() -> int {
    let xs = List<int>()
    xs.Add(1)
    xs.Add(2)
    var total = 0
    for x in xs {
        for y in xs {
            await Task.Delay(1)
            total += x * y
        }
    }
    return total
}";
        Assert.Equal(9, EsHarness.Await(EsHarness.Run(src, "gap5bnested")));
    }

    // GAP 5b — await inside a foreach whose body also has a try/catch.
    [Fact]
    public void AsyncCodegen_AwaitInForeachWithTryCatch()
    {
        var src = @"
namespace Test
func gap5btry() -> int {
    let xs = List<int>()
    xs.Add(1)
    xs.Add(2)
    var total = 0
    for x in xs {
        try {
            await Task.Delay(1)
            total += x
        } catch {
            total += 100
        }
    }
    return total
}";
        Assert.Equal(3, EsHarness.Await(EsHarness.Run(src, "gap5btry")));
    }

    // GAP B — the lambda body calls a value-receiver method on the pointer element, so the
    // parameter must be typed `*Box` (not `object`) for the method to resolve.
    [Fact]
    public void PointerGenerics_LambdaCallsMethodOnPointerParam()
    {
        var src = @"
namespace Test
struct Box { var v: int }
func (b: Box) score() -> int = b.v * 2
func sumBy<T>(xs: List<T>, f: Func<T, int>) -> int {
    var s = 0
    for it in xs { s += f(it) }
    return s
}
func gapBmethod() -> int {
    let xs = List<*Box>()
    xs.Add(new Box { v: 3 })
    xs.Add(new Box { v: 4 })
    return sumBy<*Box>(xs, (w) => w.score())
}";
        Assert.Equal(14, EsHarness.Run(src, "gapBmethod"));
    }

    // GAP 5b — await inside a foreach over a user `List<*T>` (the exact drain shape).
    [Fact]
    public void AsyncCodegen_AwaitInForeachOverPointerList()
    {
        var src = @"
namespace Test
struct Box { var v: int }
func gap5bptr() -> int {
    let xs = List<*Box>()
    xs.Add(new Box { v: 2 })
    xs.Add(new Box { v: 5 })
    var total = 0
    for b in xs {
        await Task.Delay(1)
        total += b.v
    }
    return total
}";
        Assert.Equal(7, EsHarness.Await(EsHarness.Run(src, "gap5bptr")));
    }

    // GAP 4 (declaration order) — a method calls an async sibling declared LATER in the
    // type. Method signatures are declared before any body emits, so `self.run(...)`
    // resolves even though `run`'s wrapper is materialized after `start` is visited.
    [Fact]
    public void AsyncMethods_ForwardDeclaredAsyncSibling_Resolves()
    {
        var src = @"
namespace Test
class Svc {
    var worker: Task
    var n: int
    init() { self.n = 0 }
    func start() { self.worker = self.run(2) }
    func run(k: int) -> Task { await Task.Delay(k) }
}
func gap4fwd() -> int {
    let s = Svc()
    s.start()
    s.worker.Wait()
    return 1
}";
        Assert.Equal(1, EsHarness.Run(src, "gap4fwd"));
    }

    // GAP 1 (declaration order across types) — a method calls a value-receiver method
    // promoted onto a `data` declared LATER. All instance-method signatures (across every
    // type) are on their typeDefs before any body emits, so `item.tenfold()` resolves
    // regardless of which type the emitter visits first.
    [Fact]
    public void PointerMethods_ValueMethodOnLaterDeclaredType_Resolves()
    {
        var src = @"
namespace Test
class Svc {
    batch: List<*Box>
    var last: int
    init(b: List<*Box>) { self.batch = b }
    func drain() {
        for item in self.batch {
            self.last = item.tenfold()
        }
    }
}
struct Box { var v: int }
func (b: Box) tenfold() -> int = b.v * 10
func gap1order() -> int {
    let xs = List<*Box>()
    xs.Add(new Box { v: 4 })
    let s = Svc(xs)
    s.drain()
    return s.last
}";
        Assert.Equal(40, EsHarness.Run(src, "gap1order"));
    }

    // GAP 1 / async codegen — a compound assignment to a field of a `class` (class)
    // `self` inside an ASYNC method. `self` is a state-machine field, so the receiver
    // must be loaded by REFERENCE (ldfld self), not by address (ldflda self) the way a
    // value-type receiver would be.
    [Fact]
    public void AsyncCodegen_CompoundAssignFieldOnClassSelf()
    {
        var src = @"
namespace Test
class Counter {
    var n: int
    init() { self.n = 0 }
    func bump() -> Task {
        await Task.Delay(1)
        self.n += 5
    }
}
func gapselfcompound() -> int {
    let c = Counter()
    let t = c.bump()
    t.Wait()
    return c.n
}";
        Assert.Equal(5, EsHarness.Run(src, "gapselfcompound"));
    }
}
