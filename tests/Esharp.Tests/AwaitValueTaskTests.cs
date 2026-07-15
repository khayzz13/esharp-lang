using System.Reflection;

namespace Esharp.Tests;

/// Awaiting a bare non-generic `ValueTask` (no `.AsTask()` bridge). The async
/// emitter must recognize the awaited expression as a `ValueTask` struct — spill it,
/// take its address, call the struct `GetAwaiter`/`GetResult` — rather than defaulting
/// an unresolved void-result await to the reference-typed `Task` path (which would
/// call `Task.GetAwaiter` on a `ValueTask` value left on the stack → bad IL, caught
/// here by verify-on-compile as ES0900).
public sealed class AwaitValueTaskTests
{
    // Awaiting a bare `ValueTask` returned by a member call on a non-static receiver
    // (`s.DisposeAsync()` → `System.IO.Stream.DisposeAsync()`). The receiver is a local,
    // so the awaitable type resolves through the bound type rather than a static
    // MethodInfo — EmitAwait's void-result fallback path.
    [Fact]
    public void AwaitBareValueTask_MemberCall_Compiles()
    {
        EsHarness.Compile("""
namespace Test
func drain(s: MemoryStream) -> Task {
    await s.DisposeAsync()
}
""");
    }

    // End-to-end run: the bare-ValueTask await actually executes.
    [Fact]
    public void AwaitBareValueTask_MemberCall_Runs()
    {
        var asm = EsHarness.Compile("""
namespace Test
static Test {
    func go() -> int {
        let s = MemoryStream()
        drain(s).Wait()
        return 7
    }
}
func drain(s: MemoryStream) -> Task {
    await s.DisposeAsync()
}
""");
        Assert.Equal(7, EsHarness.Invoke(asm, "go"));
    }
}
