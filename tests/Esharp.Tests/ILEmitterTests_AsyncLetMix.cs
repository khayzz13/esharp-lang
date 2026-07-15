// Style note: E# source below is inline \n-escaped for brevity — do NOT copy this in new test files; prefer readable """ raw-string blocks (these tests double as the E# corpus).
using Binder = Esharp.Binder.Binder;
using Esharp.Diagnostics;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;

namespace Esharp.Tests;

/// `async let` fan-out crossed with async user functions, Result, and `?`
/// propagation. The async-let lowering must (a) keep an async user-fn call as a
/// raw awaitable (not auto-awaited into a non-call node), and (b) the `?` early
/// return inside the resulting async state machine must complete via the builder
/// (SetResult), not a raw `ret`.
public sealed class ILEmitterTests_AsyncLetMix
{
    const string Loader =
        "func loadAsync(n: int) -> Result<int, string> {\n" +
        "  let v = await Task.FromResult(n)\n" +
        "  if v < 0 { return error(\"neg\") }\n" +
        "  return ok(v * 10)\n" +
        "}\n";

    static (bool ok, object? val, object? err) RunR(string body, string method = "combine") =>
        EsHarness.RunResultAsync("namespace Test\n" + body, method);

    static object? RunAwait(string body, string method) =>
        EsHarness.Await(EsHarness.Invoke(EsHarness.Compile("namespace Test\n" + body), method));

    [Fact] public void AsyncUserFn_TwoPending_OkFold()
    {
        var (ok, val, _) = RunR(Loader +
            "func combine() -> Result<int, string> {\n  async let a = loadAsync(2)\n  async let b = loadAsync(3)\n  let av = a?\n  let bv = b?\n  return ok(av + bv)\n}");
        Assert.True(ok);
        Assert.Equal(50, val);
    }

    [Fact] public void AsyncUserFn_FirstError_ShortCircuits()
    {
        var (ok, _, err) = RunR(Loader +
            "func combine() -> Result<int, string> {\n  async let a = loadAsync(5)\n  async let b = loadAsync(0 - 1)\n  let av = a?\n  let bv = b?\n  return ok(av + bv)\n}");
        Assert.False(ok);
        Assert.Equal("neg", err);
    }

    [Fact] public void AsyncUserFn_SingleAsyncLet()
    {
        var (ok, val, _) = RunR(Loader +
            "func combine() -> Result<int, string> {\n  async let a = loadAsync(7)\n  let av = a?\n  return ok(av)\n}");
        Assert.True(ok);
        Assert.Equal(70, val);
    }

    [Fact] public void AsyncUserFn_ThreePending()
    {
        var (ok, val, _) = RunR(Loader +
            "func combine() -> Result<int, string> {\n  async let a = loadAsync(1)\n  async let b = loadAsync(2)\n  async let c = loadAsync(3)\n  let av = a?\n  let bv = b?\n  let cv = c?\n  return ok(av + bv + cv)\n}");
        Assert.True(ok);
        Assert.Equal(60, val);
    }

    [Fact] public void ExternalAwaitable_TaskFromResult()
    {
        // async let over an external call (Task.FromResult) — used as-is.
        var r = RunAwait(
            "func combine() -> int {\n  async let a = Task.FromResult(20)\n  async let b = Task.FromResult(22)\n  return a + b\n}", "combine");
        Assert.Equal(42, r);
    }

    [Fact] public void SyncUserFn_AsyncLet_Runs()
    {
        // `async let` over a SYNC user fn auto-wraps the call in `Task.Run<T>` (ES3004),
        // so the awaited result type stays correct. Closing the wrapper lambda's
        // delegate through the generic method (`Func<!!0>` → `Func<int>`) emits
        // VERIFIABLE IL: the two computes run on the pool and fold — compute(4)=8,
        // compute(3)=6 → 14. `Compile` runs with `verify: true`, so a clean run is
        // itself proof the state machine's MoveNext verifies.
        const string src = """
namespace Test

func compute(n: int) -> int = n * 2

func combine() -> int {
    async let a = compute(4)
    async let b = compute(3)
    return a + b
}
""";
        var r = EsHarness.Await(EsHarness.Invoke(EsHarness.Compile(src), "combine"));
        Assert.Equal(14, r);
    }

    [Fact] public void QuestionPropagation_InAsync_OkPath()
    {
        // `?` inside an async function must route the early return through the
        // builder (SetResult), not a raw ret — `await` makes `run` async.
        const string src = """
namespace Test

func loadAsync(n: int) -> Result<int, string> {
    let v = await Task.FromResult(n)
    if v < 0 { return error("neg") }
    return ok(v * 10)
}

func run() -> Result<int, string> {
    let r = await loadAsync(4)
    let v = r?
    return ok(v + 1)
}
""";
        var (ok, val, _) = EsHarness.RunResultAsync(src, "run");
        Assert.True(ok);
        Assert.Equal(41, val);
    }

    [Fact] public void QuestionPropagation_InAsync_ErrorPath()
    {
        const string src = """
namespace Test

func loadAsync(n: int) -> Result<int, string> {
    let v = await Task.FromResult(n)
    if v < 0 { return error("neg") }
    return ok(v * 10)
}

func run() -> Result<int, string> {
    let r = await loadAsync(0 - 5)
    let v = r?
    return ok(v + 1)
}
""";
        var (ok, _, err) = EsHarness.RunResultAsync(src, "run");
        Assert.False(ok);
        Assert.Equal("neg", err);
    }

    [Fact] public void AsyncLet_SecondError_Propagates()
    {
        var (ok, _, err) = RunR(Loader +
            "func combine() -> Result<int, string> {\n  async let a = loadAsync(3)\n  async let b = loadAsync(0 - 9)\n  let av = a?\n  let bv = b?\n  return ok(av + bv)\n}");
        Assert.False(ok);
        Assert.Equal("neg", err);
    }

    [Fact] public void AsyncLet_ResultInterpolatedAfterUnwrap()
    {
        // unwrap both, then use the values — exercises the await + ? + later use.
        var (ok, val, _) = RunR(Loader +
            "func combine() -> Result<int, string> {\n  async let a = loadAsync(2)\n  async let b = loadAsync(2)\n  let av = a?\n  let bv = b?\n  let total = av + bv\n  return ok(total)\n}");
        Assert.True(ok);
        Assert.Equal(40, val);
    }

    [Fact] public void PlainAwait_AsyncUserFn()
    {
        // Direct await of an async user function (no async let).
        var (ok, val, _) = EsHarness.RunResultAsync(
            "namespace Test\n" + Loader +
            "func run() -> Result<int, string> {\n  let r = await loadAsync(6)\n  return r\n}", "run");
        Assert.True(ok);
        Assert.Equal(60, val);
    }

    [Fact] public void AsyncLet_OrderIndependentUse()
    {
        // reference b before a — implicit awaits hoist per first use.
        var (ok, val, _) = RunR(Loader +
            "func combine() -> Result<int, string> {\n  async let a = loadAsync(1)\n  async let b = loadAsync(2)\n  let bv = b?\n  let av = a?\n  return ok(av + bv)\n}");
        Assert.True(ok);
        Assert.Equal(30, val);
    }
}
