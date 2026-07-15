using System.Reflection;

namespace Esharp.Tests;

file static class StreamDrain
{
    public static int Sum(object? stream)
    {
        var e = (IAsyncEnumerable<int>)stream!;
        var total = 0;
        Task.Run(async () => { await foreach (var x in e) total += x; }).GetAwaiter().GetResult();
        return total;
    }
}

/// Async return-shape parity (#4): async stays uncolored (presence of `await` is the
/// only signal), but the declared return type selects the async method builder —
/// `ValueTask<T>` by default, `Task`/`Task<T>` on request (for interface slots and
/// Task-demanding BCL APIs). The body is identical; only the emitted wrapper changes.
public sealed class ILEmitterTests_AsyncParity
{
    static MethodInfo Method(string source, string name)
    {
        var asm = EsHarness.Compile(source);
        return asm.GetType("Test.Test")!.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
    }

    // Default (bare value return) async emits ValueTask<T>.
    [Fact]
    public void Default_AwaitValue_EmitsValueTask()
    {
        var m = Method("""
namespace Test
func compute() -> int {
    let x = await Task.FromResult(40)
    return x + 2
}
""", "compute");
        Assert.Equal("ValueTask`1", m.ReturnType.Name);
        Assert.Equal(typeof(int), m.ReturnType.GetGenericArguments()[0]);
    }

    // Explicit `-> Task<T>` emits Task<T> — the body still just awaits and returns
    // the unwrapped value.
    [Fact]
    public void TaskOfT_EmitsTaskReturn()
    {
        var m = Method("""
namespace Test
func compute() -> Task<int> {
    let x = await Task.FromResult(40)
    return x + 2
}
""", "compute");
        Assert.Equal("Task`1", m.ReturnType.Name);
        Assert.Equal(typeof(int), m.ReturnType.GetGenericArguments()[0]);
    }

    // The Task<T>-returning async runs and produces the right value when awaited.
    [Fact]
    public void TaskOfT_RunsAndAwaitsToValue() =>
        Assert.Equal(42, EsHarness.Await(EsHarness.Run("""
namespace Test
func compute() -> Task<int> {
    let x = await Task.FromResult(40)
    return x + 2
}
""", "compute")));

    // Explicit `-> Task` (no result) emits the non-generic Task.
    [Fact]
    public void Task_Void_EmitsTaskReturn()
    {
        var m = Method("""
namespace Test
func ping() -> Task {
    let _ = await Task.FromResult(0)
}
""", "ping");
        Assert.Equal("Task", m.ReturnType.Name);
    }

    // Explicit `-> ValueTask<T>` is the same as the default but spelled out.
    [Fact]
    public void ValueTaskOfT_Explicit_EmitsValueTask()
    {
        var m = Method("""
namespace Test
func compute() -> ValueTask<int> {
    let x = await Task.FromResult(7)
    return x
}
""", "compute");
        Assert.Equal("ValueTask`1", m.ReturnType.Name);
        Assert.Equal(typeof(int), m.ReturnType.GetGenericArguments()[0]);
    }

    // Explicit `-> void` on an await-using function is async-void (event handlers):
    // emits a CLR `void` return over AsyncVoidMethodBuilder.
    [Fact]
    public void ExplicitVoid_Await_EmitsAsyncVoid()
    {
        var m = Method("""
namespace Test
func handler() -> void {
    let _ = await Task.FromResult(0)
}
""", "handler");
        Assert.Equal(typeof(void), m.ReturnType);
    }

    // An OMITTED return on an await-using function stays the awaitable ValueTask
    // default — only an explicit `-> void` opts into async-void.
    [Fact]
    public void OmittedReturn_Await_StaysValueTask()
    {
        var m = Method("""
namespace Test
func handler() {
    let _ = await Task.FromResult(0)
}
""", "handler");
        Assert.Equal("ValueTask", m.ReturnType.Name);
    }

    // Async stream: `-> IAsyncEnumerable<T>` + `yield` (and `await`) — desugars to a
    // channel-backed producer and is consumable via `await foreach` from the host.
    [Fact]
    public void AsyncStream_YieldAndAwait_Streams()
    {
        var asm = EsHarness.Compile("""
namespace Test
func nums() -> IAsyncEnumerable<int> {
    yield 1
    let x = await Task.FromResult(40)
    yield x
    yield 100
}
""");
        var m = asm.GetType("Test.Test")!.GetMethod("nums", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal("IAsyncEnumerable`1", m.ReturnType.Name);
        Assert.Equal(141, StreamDrain.Sum(EsHarness.Invoke(asm, "nums")));
    }

    // Parameterized async stream — exercises closure capture of the parameter and a
    // pure-yield loop (no explicit await; the yield→WriteAsync await drives the SM).
    [Fact]
    public void AsyncStream_Parameterized_LoopYield_Streams()
    {
        var asm = EsHarness.Compile("""
namespace Test
func range(n: int) -> IAsyncEnumerable<int> {
    var i = 0
    while i < n {
        yield i
        i += 1
    }
}
""");
        Assert.Equal(10, StreamDrain.Sum(EsHarness.Invoke(asm, "range", 5)));   // 0+1+2+3+4
    }

    // A Task<T>-returning async can satisfy a C#-style Task<T> interface slot — the
    // exact interop gap this feature closes. (Compiles + verifies via the harness.)
    [Fact]
    public void TaskOfT_RunsAndComposesWithBcl() =>
        Assert.Equal(10, EsHarness.Await(EsHarness.Run("""
namespace Test
func one() -> Task<int> {
    let x = await Task.FromResult(4)
    return x + 1
}
func two() -> Task<int> {
    let y = await one()
    return y + 5
}
""", "two")));

    // === added: more async return-shape coverage ===

    [Fact]
    public void Added_Default_AwaitChain_Value() =>
        Assert.Equal(12, EsHarness.Await(EsHarness.Run("""
namespace Test
func a() -> int { let x = await Task.FromResult(10) return x }
func b() -> int { let y = await a() return y + 2 }
""", "b")));

    [Fact]
    public void Added_TaskOfT_ReturnsTaskType()
    {
        var m = Method("""
namespace Test
func fetch() -> Task<int> {
    let x = await Task.FromResult(7)
    return x
}
""", "fetch");
        Assert.Equal("Task`1", m.ReturnType.Name);
    }

    [Fact]
    public void Added_NonGenericTask_Void()
    {
        var m = Method("""
namespace Test
func run() -> Task {
    await Task.Delay(1)
}
""", "run");
        Assert.Equal("Task", m.ReturnType.Name);
    }

    [Fact]
    public void Added_ExplicitVoid_AsyncVoid()
    {
        var m = Method("""
namespace Test
func handler() -> void {
    await Task.Delay(1)
}
""", "handler");
        Assert.Equal(typeof(void), m.ReturnType);
    }

    [Fact]
    public void Added_DefaultAsync_IsValueTask()
    {
        var m = Method("""
namespace Test
func compute() -> int {
    let x = await Task.FromResult(40)
    return x + 2
}
""", "compute");
        Assert.Equal("ValueTask`1", m.ReturnType.Name);
    }

    [Fact]
    public void Added_AwaitArithmeticResult() =>
        Assert.Equal(15, EsHarness.Await(EsHarness.Run("""
namespace Test
func sum() -> int {
    let a = await Task.FromResult(5)
    let b = await Task.FromResult(10)
    return a + b
}
""", "sum")));

    [Fact]
    public void Added_TaskOfT_AwaitedToValue() =>
        Assert.Equal(8, EsHarness.Await(EsHarness.Run("""
namespace Test
func get() -> Task<int> {
    let x = await Task.FromResult(8)
    return x
}
func use() -> int {
    let v = await get()
    return v
}
""", "use")));

    [Fact]
    public void Added_AwaitInsideIf() =>
        Assert.Equal(1, EsHarness.Await(EsHarness.Run("""
namespace Test
func pick(b: bool) -> int {
    if b {
        let x = await Task.FromResult(1)
        return x
    }
    return 0
}
""", "pick", true)));
}
