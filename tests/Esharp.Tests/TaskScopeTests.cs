namespace Esharp.Tests;

/// <summary>
/// End-to-end tests for the E# TaskScope/Spawned surface.  These deliberately
/// compile E# rather than linking the test assembly to a C# staging runtime: the
/// stdlib is an E# assembly and the compiler output is the public contract.
/// </summary>
public sealed class TaskScopeTests
{
    [Fact]
    public void Spawned_Join_WaitsForTheChild() => Assert.Equal(7, EsHarness.Run("""
namespace Test
func go() -> int {
    let child = spawn { let value = 1 }
    child.Join()
    return 7
}
""", "go"));

    [Fact]
    public void TaskFunc_TypedSpawnedWait_ReturnsTheChildValue() => Assert.Equal(42, EsHarness.Run("""
namespace Test
task func answer() -> int { return 42 }
func go() -> int {
    return answer().Wait()
}
""", "go"));

    [Fact]
    public void TaskFunc_PreservesArgumentsAcrossTheSpawnBoundary() => Assert.Equal(42, EsHarness.Run("""
namespace Test
task func scale(value: int, factor: int) -> int = value * factor
func go() -> int = scale(6, 7).Wait()
""", "go"));

    [Fact]
    public void TaskFunc_MultipleJoinableFutures_ComposeSynchronously() => Assert.Equal(42, EsHarness.Run("""
namespace Test
task func first() -> int = 19
task func second() -> int = 23
func go() -> int = first().Join() + second().Wait()
""", "go"));

    [Fact]
    public void TaskFunc_FaultsSurfaceAtTheJoinPoint()
    {
        var asm = EsHarness.Compile("""
namespace Test
task func fail() -> int { throw InvalidOperationException("boom") }
func go() -> int = fail().Wait()
""");
        var ex = Assert.ThrowsAny<Exception>(() => EsHarness.Invoke(asm, "go"));
        Assert.IsType<InvalidOperationException>(ex.InnerException ?? ex);
    }

    [Fact]
    public void TaskFunc_TypedSpawned_IsAwaitable() => Assert.Equal(42, EsHarness.Await(EsHarness.Run("""
namespace Test
task func answer() -> int = 40
func go() -> Task<int> {
    let value = await answer()
    return value + 2
}
""", "go")));

    [Fact]
    public void Spawned_UntypedHandle_IsAwaitable() => Assert.Equal(7, EsHarness.Await(EsHarness.Run("""
namespace Test
func go() -> Task<int> {
    let child = spawn { let ignored = 1 }
    await child
    return 7
}
""", "go")));

    [Fact]
    public void Spawned_WaitAsync_PreservesTypedResult() => Assert.Equal(42, EsHarness.Await(EsHarness.Run("""
namespace Test
task func answer() -> int = 42
func go() -> Task<int> {
    return await answer().WaitAsync()
}
""", "go")));

    [Fact]
    public void TaskScope_StaticHostAndInstanceClass_Coexist() => Assert.Equal(1, EsHarness.Run("""
namespace Test
using "System.Threading"
func go() -> int {
    let scope = TaskScope(CancellationToken.None)
    scope.Cancel()
    return 1
}
""", "go"));

    [Fact]
    public void TaskScope_Cancel_UpdatesItsExposedToken() => Assert.Equal(true, EsHarness.Run("""
namespace Test
using "System.Threading"
func go() -> bool {
    let scope = TaskScope(CancellationToken.None)
    scope.Cancel()
    return scope.Token.IsCancellationRequested
}
""", "go"));

    [Fact]
    public void TaskScope_Disposal_IsIdempotent() => Assert.Equal(1, EsHarness.Run("""
namespace Test
using "System.Threading"
func go() -> int {
    let scope = TaskScope(CancellationToken.None)
    scope.DisposeAsync().AsTask().GetAwaiter().GetResult()
    scope.DisposeAsync().AsTask().GetAwaiter().GetResult()
    return 1
}
""", "go"));

    [Fact]
    public void TaskScope_OwnedChannel_CompletesOnDisposal() => Assert.Equal(false, EsHarness.Run("""
namespace Test
using "System.Threading"
func go() -> bool {
    let scope = TaskScope(CancellationToken.None)
    let channel = scope.Chan<int>(1)
    let accepted = channel.TrySend(1)
    scope.DisposeAsync().AsTask().GetAwaiter().GetResult()
    return channel.TrySend(2)
}
""", "go"));

    [Fact]
    public void TaskScope_RunAsync_UsesTheStaticHostOnTheInstanceType() => Assert.Equal(9, EsHarness.Run("""
namespace Test
func go() -> int {
    let work: Func<TaskScope, Task> = func(scope: TaskScope) -> Task { return Task.CompletedTask }
    TaskScope.RunAsync(work).GetAwaiter().GetResult()
    return 9
}
""", "go"));

    [Fact]
    public void TaskScope_Spawn_AcceptsAnAsyncChildDelegate() => Assert.Equal(11, EsHarness.Run("""
namespace Test
using "System.Threading"
func go() -> int {
    let scope = TaskScope(CancellationToken.None)
    let child = scope.Spawn(func(ct: CancellationToken) -> Task { return Task.CompletedTask })
    child.GetAwaiter().GetResult()
    scope.DisposeAsync().AsTask().GetAwaiter().GetResult()
    return 11
}
""", "go"));
}
