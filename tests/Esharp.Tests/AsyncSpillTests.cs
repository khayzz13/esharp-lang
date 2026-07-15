namespace Esharp.Tests;

/// Async spill-sequence lowering (fuzzer finding #1). An `await` nested inside a
/// larger expression used to leave the sub-expressions evaluated before it on the
/// state machine's eval stack across the suspend — unverifiable IL (ES0900). The
/// spill pass hoists every await (and the operands evaluated before it) into temps
/// so no await suspends with a live stack. These tests run through the IL backend
/// (source of truth) and assert both the value and the preserved evaluation order.
public sealed class AsyncSpillTests
{
    static int RunAsyncInt(string src) => (int)EsHarness.Await(EsHarness.Run(src, "go"))!;

    // 1 ── the canonical repro: await as the right operand of a binary expression.
    [Fact]
    public void AwaitAsBinaryRightOperand_Compiles_AndComputes()
    {
        Assert.Equal(2, RunAsyncInt("""
namespace Test
func asy(n: int) -> int {
    return await Task.FromResult(n + 1)
}
func go() -> int {
    var acc = 1
    acc = acc * (await asy(acc))
    return acc
}
"""));
    }

    // 2 ── evaluation order: the left operand must be read BEFORE the await runs.
    // `g` then the awaited `h` ⇒ trace 12; result 1 + 2 = 3 (returned via the trace).
    [Fact]
    public void EvaluationOrder_LeftOperandReadBeforeAwait()
    {
        Assert.Equal(12, RunAsyncInt("""
namespace Test
struct Log { var v: int }
func g(l: *Log) -> int {
    l.v = l.v * 10 + 1
    return 1
}
func h(l: *Log) -> int {
    l.v = l.v * 10 + 2
    return 2
}
func go() -> int {
    var l: *Log = new Log { v: 0 }
    let r = g(l) + (await Task.FromResult(h(l)))
    return l.v
}
"""));
    }

    // 2b ── call arguments around an await keep left-to-right order: g, await, k ⇒ 123.
    [Fact]
    public void EvaluationOrder_CallArgsAroundAwait()
    {
        Assert.Equal(123, RunAsyncInt("""
namespace Test
struct Log { var v: int }
func g(l: *Log) -> int {
    l.v = l.v * 10 + 1
    return 1
}
func h(l: *Log) -> int {
    l.v = l.v * 10 + 2
    return 2
}
func k(l: *Log) -> int {
    l.v = l.v * 10 + 3
    return 3
}
func sum3(a: int, b: int, c: int) -> int {
    return a + b + c
}
func go() -> int {
    var l: *Log = new Log { v: 0 }
    let r = sum3(g(l), await Task.FromResult(h(l)), k(l))
    return l.v
}
"""));
    }

    // 3 ── compound assignment: the target read happens before the await's effect.
    // old = field (5), then await mutates field to 100, then field = old + 7 = 12.
    [Fact]
    public void CompoundAssignment_ReadsTargetBeforeAwait()
    {
        Assert.Equal(12, RunAsyncInt("""
namespace Test
struct Cell { var n: int }
func bump(c: *Cell) -> int {
    c.n = 100
    return 7
}
func go() -> int {
    var c: *Cell = new Cell { n: 5 }
    c.n += await Task.FromResult(bump(c))
    return c.n
}
"""));
    }

    // 4 ── complex l-value target: member target sub-expression evaluated before await.
    [Fact]
    public void MemberTargetAssignment_WithAwaitValue()
    {
        Assert.Equal(9, RunAsyncInt("""
namespace Test
struct Cell { var n: int }
func go() -> int {
    var c: *Cell = new Cell { n: 0 }
    c.n = await Task.FromResult(9)
    return c.n
}
"""));
    }

    // 5 ── `&&` short-circuit: a false left must NOT await/run the right.
    [Fact]
    public void AndAlso_ShortCircuit_DoesNotAwaitRight()
    {
        // left false ⇒ aflag never runs ⇒ trace stays 0.
        Assert.Equal(0, RunAsyncInt("""
namespace Test
struct Log { var v: int }
func aflag(l: *Log) -> bool {
    l.v = 99
    return true
}
func go() -> int {
    var l: *Log = new Log { v: 0 }
    let b = false && (await Task.FromResult(aflag(l)))
    return l.v
}
"""));
    }

    [Fact]
    public void AndAlso_TrueLeft_RunsRight()
    {
        Assert.Equal(99, RunAsyncInt("""
namespace Test
struct Log { var v: int }
func aflag(l: *Log) -> bool {
    l.v = 99
    return true
}
func go() -> int {
    var l: *Log = new Log { v: 0 }
    let b = true && (await Task.FromResult(aflag(l)))
    return l.v
}
"""));
    }

    // 6 ── `||` short-circuit: a true left must NOT await/run the right.
    [Fact]
    public void OrElse_ShortCircuit_DoesNotAwaitRight()
    {
        Assert.Equal(0, RunAsyncInt("""
namespace Test
struct Log { var v: int }
func aflag(l: *Log) -> bool {
    l.v = 99
    return false
}
func go() -> int {
    var l: *Log = new Log { v: 0 }
    let b = true || (await Task.FromResult(aflag(l)))
    return l.v
}
"""));
    }

    // 7 ── ternary: only the taken branch's await runs; result is correct.
    [Fact]
    public void Conditional_AwaitInBranches_OnlyTakenRuns()
    {
        Assert.Equal(7, RunAsyncInt("""
namespace Test
func go() -> int {
    let cond = true
    let r = cond ? (await Task.FromResult(7)) : (await Task.FromResult(13))
    return r
}
"""));
        Assert.Equal(13, RunAsyncInt("""
namespace Test
func go() -> int {
    let cond = false
    let r = cond ? (await Task.FromResult(7)) : (await Task.FromResult(13))
    return r
}
"""));
    }

    // 8 ── null-coalescing with the await in the (conditional) right operand.
    [Fact]
    public void NullCoalescing_AwaitInRight_OnlyWhenLeftNil()
    {
        // left non-nil ⇒ right (await) never runs; result is the left value.
        Assert.Equal(5, RunAsyncInt("""
namespace Test
func maybe(present: bool) -> int? {
    if present { return 5 }
    return nil
}
func go() -> int {
    let r = maybe(true) ?? (await Task.FromResult(99))
    return r
}
"""));
        // left nil ⇒ right (await) supplies the value.
        Assert.Equal(99, RunAsyncInt("""
namespace Test
func maybe(present: bool) -> int? {
    if present { return 5 }
    return nil
}
func go() -> int {
    let r = maybe(false) ?? (await Task.FromResult(99))
    return r
}
"""));
    }

    // 9 ── while-condition await re-evaluates each iteration (with a `continue`).
    [Fact]
    public void WhileConditionAwait_ReEvaluatesEachIteration()
    {
        Assert.Equal(5, RunAsyncInt("""
namespace Test
struct Counter { var n: int }
func more(c: *Counter) -> bool { return c.n < 5 }
func go() -> int {
    var c: *Counter = new Counter { n: 0 }
    while await Task.FromResult(more(c)) {
        c.n = c.n + 1
        continue
    }
    return c.n
}
"""));
    }

    // 10 ── for-in collection awaited once, body sums.
    [Fact]
    public void ForEachCollectionAwait()
    {
        Assert.Equal(6, RunAsyncInt("""
namespace Test
func go() -> int {
    var total = 0
    for x in (await Task.FromResult([1, 2, 3])) {
        total += x
    }
    return total
}
"""));
    }

    // 11 ── nested awaits and a multi-await arithmetic expression.
    [Fact]
    public void NestedAwaits()
    {
        Assert.Equal(8, RunAsyncInt("""
namespace Test
func inc(n: int) -> int { return await Task.FromResult(n + 1) }
func go() -> int {
    return await inc(await inc(6))
}
"""));
    }

    [Fact]
    public void MultipleAwaitsInArithmetic()
    {
        // (1) + (2) * (3) = 7
        Assert.Equal(7, RunAsyncInt("""
namespace Test
func go() -> int {
    return (await Task.FromResult(1)) + (await Task.FromResult(2)) * (await Task.FromResult(3))
}
"""));
    }

    // 12 ── await inside container literals and interpolation.
    [Fact]
    public void AwaitInListLiteral()
    {
        Assert.Equal(6, RunAsyncInt("""
namespace Test
func go() -> int {
    let xs = [await Task.FromResult(1), 2, await Task.FromResult(3)]
    var total = 0
    for x in xs { total += x }
    return total
}
"""));
    }

    [Fact]
    public void AwaitInInterpolationHole()
    {
        var r = EsHarness.Await(EsHarness.Run("""
namespace Test
func go() -> string {
    let n = await Task.FromResult(42)
    return "value={await Task.FromResult(n)}!"
}
""", "go"));
        Assert.Equal("value=42!", r);
    }

    // 13 ── await inside a try body (exercises SM-field hoisting for spill temps).
    [Fact]
    public void AwaitInTryBody_NestedInExpression()
    {
        Assert.Equal(6, RunAsyncInt("""
namespace Test
func go() -> int {
    var acc = 1
    try {
        acc = acc + (await Task.FromResult(5))
    } catch (e: Exception) {
        acc = -1
    }
    return acc
}
"""));
    }

    // 14 ── let-else (BoundLetGuard) initializer with a nested await.
    [Fact]
    public void LetElse_AwaitInInitializer()
    {
        Assert.Equal(20, RunAsyncInt("""
namespace Test
func lookup(present: bool) -> int? {
    if present { return 10 }
    return nil
}
func go() -> int {
    let v = lookup(true) else { return -1 }
    let w = v + (await Task.FromResult(10))
    return w
}
"""));
    }

    // 15 ── definite-return preserved when the sole return feeds a rewritten ternary.
    [Fact]
    public void DefiniteReturn_ThroughRewrittenConditional()
    {
        Assert.Equal(7, RunAsyncInt("""
namespace Test
func go() -> int {
    let cond = true
    return cond ? (await Task.FromResult(7)) : 0
}
"""));
    }

    // 16 ── a non-awaiting sub-expression in an async fn stays correct (no over-rewrite).
    [Fact]
    public void SyncExpressionInAsyncBody_Unchanged()
    {
        Assert.Equal(30, RunAsyncInt("""
namespace Test
func go() -> int {
    let a = 10 * 2
    let b = await Task.FromResult(10)
    return a + b
}
"""));
    }
}
