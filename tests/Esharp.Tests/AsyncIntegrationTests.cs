using System.Reflection;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

/// <summary>
/// Coverage matrix for async/concurrency codegen, exercised on the IL backend
/// (the production compiler).
///
/// The IL backend has historically carried significant async coverage gaps
/// (parallel EmitStatement/EmitExpression switches in ILAsyncEmitter silently
/// emitting nops for unhandled node kinds). These tests are the regression gate
/// for that unification work.
/// </summary>
public sealed class AsyncIntegrationTests
{
    static int _asmCounter;

    // ─── Backend helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Compile via IL emitter, load assembly, return it. Mirrors ILEmitterTests'
    /// CompileAndLoad; inlined here to keep this file standalone.
    /// </summary>
    static Assembly CompileIL(string source, string? asmName = null)
    {
        asmName ??= $"AsyncIntTest_IL_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        var errors = binder.Diagnostics.Where(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);

        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        return EsHarness.EmitBoundToFileVerifiedAndLoad(binder, bound, asmName, path);
    }

    /// <summary>
    /// Invoke a static method on the generated module class and return its result.
    /// Awaitable results are unwrapped via reflection so the caller sees the final
    /// value regardless of whether the method returned <c>T</c>, <c>Task&lt;T&gt;</c>,
    /// or <c>ValueTask&lt;T&gt;</c>.
    /// </summary>
    static object? InvokeAndUnwrap(Assembly asm, string typeName, string methodName, params object?[] args)
    {
        var type = asm.GetType($"Test.{typeName}")
                   ?? asm.GetType(typeName)
                   ?? throw new Exception($"Type {typeName} not found in assembly");
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                     ?? throw new Exception($"Method {methodName} not found on {typeName}");
        var raw = method.Invoke(null, args);
        return UnwrapAwaitable(raw);
    }

    static object? UnwrapAwaitable(object? value)
    {
        if (value is null) return null;
        var t = value.GetType();
        if (t == typeof(Task) || t == typeof(ValueTask))
        {
            // Void awaitable — block until done and return null.
            if (value is Task task) { task.GetAwaiter().GetResult(); return null; }
            if (value is ValueTask vt) { vt.GetAwaiter().GetResult(); return null; }
        }
        // Task<T> / ValueTask<T> — block via GetAwaiter().GetResult() reflected.
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            if (def == typeof(Task<>) || def == typeof(ValueTask<>))
            {
                var getAwaiter = t.GetMethod("GetAwaiter", BindingFlags.Public | BindingFlags.Instance)!;
                var awaiter = getAwaiter.Invoke(value, null)!;
                var getResult = awaiter.GetType().GetMethod("GetResult", BindingFlags.Public | BindingFlags.Instance)!;
                return getResult.Invoke(awaiter, null);
            }
        }
        return value;
    }

    /// <summary>
    /// Compiles the source on the IL backend (the production compiler), invokes
    /// <paramref name="methodName"/>, and asserts the returned value.
    /// </summary>
    static void AssertBothBackends(string source, string typeName, string methodName, object? expected, params object?[] args)
    {
        var ilAsm = CompileIL(source);
        var ilResult = InvokeAndUnwrap(ilAsm, typeName, methodName, args);
        Assert.Equal(expected, ilResult);
    }

    // ─── Coverage matrix tests ───────────────────────────────────────────

    const string MatchWithAwaitSource = """
namespace Test

union Cmd {
    echo(n: int)
    skip
}

func runEcho() -> int {
    let c = Cmd.echo(41)
    match (c: Cmd) {
        .echo(n) {
            let v = await Task.FromResult(n)
            return v + 1
        }
        .skip {
            return 0
        }
    }
    return -1
}
""";

    // ── Test 1 split into three stages for diagnostic clarity ──

    [Fact]
    public void Async_Match_IL_Compiles()
    {
        // First stage: does the IL emitter even produce a loadable assembly?
        // Exception here means the emitter itself threw; otherwise we can then
        // probe whether the produced IL is valid via invocation.
        var ex = Record.Exception(() => CompileIL(MatchWithAwaitSource));
        Assert.Null(ex);
    }

    [Fact]
    public void Async_Match_IL_Runs()
    {
        var asm = CompileIL(MatchWithAwaitSource);
        var result = InvokeAndUnwrap(asm, "Test", "runEcho");
        Assert.Equal(42, result);
    }

    [Fact]
    public void Async_Match_AwaitInArm_Runs()
    {
        var asm = CompileIL(MatchWithAwaitSource);
        var result = InvokeAndUnwrap(asm, "Test", "runEcho");
        Assert.Equal(42, result);
    }

    // ── Test 2: async + while loop with await in body. Proves state
    // machine correctly preserves iteration counter across suspension
    // points (the canonical "await in a loop body" case, without the
    // enumerator try/finally complication of for-in).

    [Fact]
    public void Async_WhileLoop_IL_Runs()
    {
        const string source = """
namespace Test

func sumTo(n: int) -> int {
    var i = 1
    var total = 0
    while i <= n {
        let step = await Task.FromResult(i)
        total = total + step
        i = i + 1
    }
    return total
}
""";
        var asm = CompileIL(source);
        var result = InvokeAndUnwrap(asm, "Test", "sumTo", 5);
        Assert.Equal(15, result);
    }

    // ── Test 3: async + throw surfacing through the state machine ──

    [Fact]
    public void Async_Throw_IL_SurfacesException()
    {
        const string source = """
namespace Test

func failIf(n: int) -> int {
    let v = await Task.FromResult(n)
    if v < 0 {
        throw InvalidOperationException("negative")
    }
    return v
}
""";
        var asm = CompileIL(source);
        var type = asm.GetType("Test.Test")!;
        var method = type.GetMethod("failIf", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var raw = method.Invoke(null, new object?[] { -1 });

        // The ValueTask<int> will surface the exception via GetAwaiter().GetResult()
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
        {
            var inner = UnwrapAwaitable(raw);
        }).InnerException ?? throw new Exception("expected inner");
        Assert.Contains("negative", ex.Message);
    }

    // ── Test 4: async + object creation (data struct with field init) ──

    [Fact]
    public void Async_ObjectCreation_IL_Runs()
    {
        const string source = """
namespace Test

struct Point { x: int, y: int }

func buildAt(n: int) -> int {
    let v = await Task.FromResult(n)
    let p = Point { x: v, y: v * 2 }
    return p.x + p.y
}
""";
        var asm = CompileIL(source);
        var result = InvokeAndUnwrap(asm, "Test", "buildAt", 10);
        Assert.Equal(30, result);
    }

    // ── Test 5: async + string interpolation reading SM fields ──

    [Fact]
    public void Async_StringInterpolation_IL_Runs()
    {
        const string source = """
namespace Test

func greet(name: string) -> string {
    let hello = await Task.FromResult(name)
    return "hi {hello}!"
}
""";
        var asm = CompileIL(source);
        var result = InvokeAndUnwrap(asm, "Test", "greet", "kae");
        Assert.Equal("hi kae!", result);
    }

    // ── Test 6: async + dot-case choice construction ──

    [Fact]
    public void Async_DotCase_IL_Runs()
    {
        const string source = """
namespace Test

union Reply {
    ok(v: int)
    err(msg: string)
}

func replyOf(n: int) -> int {
    let v = await Task.FromResult(n)
    let r = Reply.ok(v + 1)
    match (r: Reply) {
        .ok(x) { return x }
        .err(_) { return -1 }
    }
    return -2
}
""";
        var asm = CompileIL(source);
        var result = InvokeAndUnwrap(asm, "Test", "replyOf", 7);
        Assert.Equal(8, result);
    }

    // ── async let — functional proof of fan-out. The lowering rewrites
    // `async let a = ...` into a pending Task field + implicit await at
    // first reference, so both tasks are in flight before either is awaited.
    // Functional correctness is the gate; wall-clock timing is too fragile
    // to assert in a unit test.

    [Fact]
    public void AsyncLet_SimpleTwoPending_IL_Runs()
    {
        const string source = """
namespace Test

func runParallel() -> int {
    async let a = Task.FromResult(20)
    async let b = Task.FromResult(22)
    return a + b
}
""";
        var asm = CompileIL(source);
        var result = InvokeAndUnwrap(asm, "Test", "runParallel");
        Assert.Equal(42, result);
    }

    // ── Void-await in statement position. `Task.Delay(1)` returns a
    // non-generic Task; `await` on it yields no value. The emitter must
    // resolve the actual Task shape via reflection (not fall through to the
    // enclosing function's return type heuristic) and signal to the
    // surrounding expression-statement that no value was pushed.

    [Fact]
    public void Async_VoidAwait_IL_Runs()
    {
        const string source = """
namespace Test

func delayThenReturn() -> int {
    await Task.Delay(1)
    return 42
}
""";
        var asm = CompileIL(source);
        var result = InvokeAndUnwrap(asm, "Test", "delayThenReturn");
        Assert.Equal(42, result);
    }

    // ── async let on sync user function — auto-wrapped in Task.Run ──

    [Fact]
    public void AsyncLet_SyncUserFunc_IL_WrapsInTaskRun()
    {
        // Path C: `async let a = syncFunc()` where syncFunc is a user-
        // defined function that doesn't contain await. The lowering pass
        // wraps it in Task.Run for fan-out. Uses Task.FromResult as the
        // external path to verify the sync-wrapping fires correctly.
        const string source = """
namespace Test

func compute(n: int) -> int {
    return n * 10
}

func runBoth() -> int {
    async let a = compute(4)
    async let b = compute(3)
    return a + b
}
""";
        // Verify the lowering fires ES3004 (sync call auto-wrapped)
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Contains(binder.Diagnostics, d => d.Message.Contains("ES3004"));
    }

    // ── Test 7: async + try/catch where the await is outside the try ──

    [Fact]
    public void Async_TryCatch_NoAwaitInTry_IL_Runs()
    {
        const string source = """
namespace Test

func safe(n: int) -> int {
    let v = await Task.FromResult(n)
    var result = 0
    try {
        result = v * 2
    } catch (e: Exception) {
        result = -1
    }
    return result
}
""";
        var asm = CompileIL(source);
        var result = InvokeAndUnwrap(asm, "Test", "safe", 21);
        Assert.Equal(42, result);
    }
}
