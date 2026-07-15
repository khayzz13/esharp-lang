using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Binder = Esharp.Binder.Binder;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Xunit;

namespace Esharp.Tests;

/// Locks in readiness-probe fixes. Each test is the minimal reproduction the probe
/// surfaced, now passing.
public class ILEmitterTests_V01Findings
{
    // Fix 1 — a user-defined attribute applied to a type round-trips through metadata.
    [Fact]
    public void UserAttribute_AppliedToType_RoundTripsViaReflection()
    {
        var asm = EsHarness.Compile("""
namespace Test
using "System"
class TagAttribute : Attribute {
    var note: string
    init(n: string) { self.note = n }
}
[Tag("hello")]
class Widget { var id: int  init(i: int) { self.id = i } }
""", "UserAttr");
        var widget = asm.GetType("Test.Widget")!;
        var attrs = widget.GetCustomAttributes(false);
        Assert.Contains(attrs, a => a.GetType().Name == "TagAttribute");
    }

    // Fix 1 (prereq) — a class may inherit an external (BCL) base class; the base ctor
    // chains via `: base(args)`, and an instance is the external type.
    [Fact]
    public void ExternalBaseClass_CtorChains_AndIsBaseType()
    {
        var thrown = EsHarness.Run("""
namespace Test
using "System"
class ParseError : Exception {
    init(msg: string) : base(msg) {}
}
func go() -> string {
    try { throw ParseError("bad input") }
    catch (e: Exception) { return e.Message }
    return "unreached"
}
""", "go");
        Assert.Equal("bad input", thrown);
    }

    // Fix 2 — type-argument inference into a USER generic function (no explicit `<...>`).
    [Fact]
    public void GenericInference_UserFunction_NoExplicitArgs()
    {
        var sum = EsHarness.Run("""
namespace Test
using "System"
using "System.Collections.Generic"
func mapSum<T, R>(xs: List<T>, f: Func<T, R>) -> List<R> {
    var outl = List<R>()
    for x in xs { outl.Add(f(x)) }
    return outl
}
func go() -> int {
    var xs = List<int>()
    xs.Add(1)  xs.Add(2)  xs.Add(3)
    let doubled = mapSum(xs, (x) => x * 2)   // inferred <int, int>
    var s = 0
    for d in doubled { s += d }
    return s
}
""", "go");
        Assert.Equal(12, sum);
    }

    // Fix 3 — `for v in` over a CONCRETE user type implementing IEnumerable<T> binds the
    // element type (was erased to object → invalid IL).
    [Fact]
    public void ForIn_ConcreteUserIEnumerable_BindsElementType()
    {
        var total = EsHarness.Run("""
namespace Test
using "System.Collections.Generic"
class Seq : IEnumerable<int> {
    src: IEnumerable<int>
    init(s: IEnumerable<int>) { self.src = s }
    func GetEnumerator() -> IEnumerator<int> = self.src.GetEnumerator()
}
func go() -> int {
    var xs = List<int>()
    xs.Add(10)  xs.Add(20)  xs.Add(30)
    var total = 0
    for v in Seq(xs) { total += v }
    return total
}
""", "go");
        Assert.Equal(60, total);
    }

    // Fix 4 — `await for v in src` consumes an async stream (yield producer + await drain).
    [Fact]
    public async Task AwaitFor_DrainsAsyncStream()
    {
        var total = await EsHarness.AwaitAsync(EsHarness.Run("""
namespace Test
func nums() -> IAsyncEnumerable<int> {
    yield 1
    let x = await Task.FromResult(40)
    yield x
    yield 100
}
func go() -> int {
    var total = 0
    await for v in nums() { total += v }
    return total
}
""", "go"));
        Assert.Equal(141, total);
    }

    // Fix 5 — `await E?` parses as `(await E)?`: await first, then try-unwrap.
    [Fact]
    public void AwaitThenTryUnwrap_PrecedenceIsAwaitFirst()
    {
        var (isOk, value, _) = EsHarness.RunResultAsync("""
namespace Test
func load(n: int) -> Result<int, string> {
    let v = await Task.FromResult(n)
    if v < 0 { return error("neg") }
    return ok(v)
}
func go() -> Result<int, string> {
    let a = await load(10)?
    let b = await load(a + 1)?
    return ok(a + b)
}
""", "go");
        Assert.True(isOk);
        Assert.Equal(21, value); // (await load(...))? — await first, then unwrap
    }

    // Fix 5 (guard) — `?` on a non-Result is a located ES2191 diagnostic, not invalid IL.
    [Fact]
    public void TryUnwrap_OnNonResult_ReportsES2191()
    {
        var parser = new Parser("""
namespace Test
func go() -> int {
    let x = 5
    let y = x?
    return y
}
""", "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        binder.Bind(syntax);
        Assert.Contains(binder.Diagnostics, d => d.Message.Contains("ES2191"));
    }

    // Adjacent fix — `defer` followed by `await` in an async function (a `defer` is now a
    // protected async region, so the await resumes inside it instead of `BranchIntoTry`).
    [Fact]
    public void DeferThenAwait_InAsyncFunction_Runs()
    {
        var r = EsHarness.Await(EsHarness.Run("""
namespace Test
func work() -> int {
    defer { var _drop = 0 }
    let x = await Task.FromResult(41)
    return x + 1
}
""", "work"));
        Assert.Equal(42, r);
    }

    // Adjacent fix — `await for` disposes on EVERY exit, including an early `return` from the
    // loop body (the defer/finally runs the synchronous DisposeAsync).
    [Fact]
    public async Task AwaitFor_EarlyReturn_StillDisposes()
    {
        var first = await EsHarness.AwaitAsync(EsHarness.Run("""
namespace Test
func nums() -> IAsyncEnumerable<int> { yield 1  yield 2  yield 3 }
func go() -> int {
    await for v in nums() { return v }
    return -1
}
""", "go"));
        Assert.Equal(1, first);
    }

    // Composition — all five fixes plus async-for disposal in one program, proving they
    // compose rather than only passing in isolation (probe p9_compose).
    [Fact]
    public async Task AllFixes_ComposeInOneProgram()
    {
        var total = await EsHarness.AwaitAsync(EsHarness.Run("""
namespace Test
using "System"
using "System.Collections.Generic"
class TagAttribute : Attribute {
    var note: string
    init(n: string) { self.note = n }
}
[Tag("seq")]
class Seq : IEnumerable<int> {
    src: IEnumerable<int>
    init(s: IEnumerable<int>) { self.src = s }
    func GetEnumerator() -> IEnumerator<int> = self.src.GetEnumerator()
}
func mapSum<T, R>(xs: List<T>, f: Func<T, R>) -> List<R> {
    var outl = List<R>()
    for x in xs { outl.Add(f(x)) }
    return outl
}
func nums() -> IAsyncEnumerable<int> {
    yield 1
    let x = await Task.FromResult(40)
    yield x
    yield 100
}
func load(n: int) -> Result<int, string> {
    let v = await Task.FromResult(n)
    return ok(v)
}
func go() -> int {
    var xs = List<int>()
    xs.Add(1)  xs.Add(2)  xs.Add(3)
    let doubled = mapSum(xs, (x) => x * 2)
    var sum = 0
    for v in Seq(doubled) { sum += v }
    var streamed = 0
    await for v in nums() { streamed += v }
    let r = await load(5)?
    return sum + streamed + r
}
""", "go"));
        Assert.Equal(158, total); // 12 (mapSum) + 141 (stream) + 5 (load)
    }

    // Catch is name-first (`catch (e: Type)`), consistent with the rest of the language.
    [Fact]
    public void Catch_NameFirstBinding_Works()
    {
        var msg = EsHarness.Run("""
namespace Test
using "System"
func go() -> string {
    try { throw Exception("boom") }
    catch (e: Exception) { return e.Message }
    return "unreached"
}
""", "go");
        Assert.Equal("boom", msg);
    }
}
