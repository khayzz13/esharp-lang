using System.Threading.Tasks;
using Esharp.Syntax.Parsing;
using Xunit;

namespace Esharp.Tests;

/// WS5/WS6 — async state-machine + runtime edges (probe4 #1/#2) and the ES2130 message fix (#3).
public sealed class ILEmitterTests_AsyncRuntime
{
    // probe4 #2 — an `await` inside a STATIC-receiver call argument (`Math.Max(await …, 2)`)
    // must not spill the type-name receiver as a slot-backed local in MoveNext.
    [Fact]
    public async Task AwaitInStaticReceiverCallArg_Works()
    {
        var r = await EsHarness.AwaitAsync(EsHarness.Run("""
namespace Test
func go() -> int {
    let m = Math.Max(await Task.FromResult(40), 2)
    return m
}
""", "go"));
        Assert.Equal(40, r);
    }

    // probe4 #2 (sibling) — an await routed through string interpolation (a static
    // String.Concat call argument).
    [Fact]
    public async Task AwaitInInterpolation_Works()
    {
        var r = await EsHarness.AwaitAsync(EsHarness.Run("""
namespace Test
func go() -> string = "v {await Task.FromResult(7)}"
""", "go"));
        Assert.Equal("v 7", r);
    }

    // probe4 #1 — `select` with a `.timeout` arm over an empty channel fires the timeout
    // (the blocking pass), rather than throwing MissingMethodException on ValueTask<T>.AsTask().
    [Fact]
    public async Task SelectTimeoutArm_FiresOnEmptyChannel()
    {
        var r = await EsHarness.AwaitAsync(EsHarness.Run("""
namespace Test
func go() -> int {
    let ch = chan<int>(1)
    var got = 0
    select {
        .recv(v, ch) { got = v }
        .timeout(50) { got = 7 }
    }
    return got
}
""", "go"));
        Assert.Equal(7, r);
    }

    // probe4 #3 — ES2130 (a function literal capturing a `var` inside a `task func`) advises
    // capturing the channel as a `let` (the achievable fix), not a chan<T> *parameter*
    // (task func parameters are passed directly to the spawned invocation).
    [Fact]
    public void ES2130_Message_AdvisesLetCapture()
    {
        var parser = new Parser("""
namespace Test
task func worker() -> int {
    var local = 5
    let bump = func() -> int { return local }
    return bump()
}
""", "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        binder.Bind(syntax);
        var es2130 = Assert.Single(binder.Diagnostics, d => d.Message.Contains("ES2130"));
        Assert.Contains("let", es2130.Message);
        Assert.DoesNotContain("parameter", es2130.Message);
    }
}
