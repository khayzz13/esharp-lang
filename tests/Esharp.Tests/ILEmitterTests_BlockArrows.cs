using System.Threading.Tasks;
using Xunit;

namespace Esharp.Tests;

/// WS2 — closures unify on `(params) => body` where the body is an expression OR a block;
/// `func(…) -> T { … }` stays the explicitly-typed-return escape hatch. Plus async lambdas:
/// a lambda whose body `await`s lowers to a state machine (unblocks `TaskScope.Spawn`).
public sealed class ILEmitterTests_BlockArrows
{
    static object? Run(string body, string method) => EsHarness.Run(body, method);

    // Block-bodied arrow as a call argument: `(x) => { stmts }`.
    [Fact]
    public void BlockArrow_AsCallArgument() => Assert.Equal(42, Run("""
namespace Test
func apply(f: Func<int, int>, x: int) -> int = f(x)
func go() -> int = apply((x) => { let y = x + 1  return y * 2 }, 20)
""", "go"));

    // Block-bodied arrow capturing and mutating an enclosing var.
    [Fact]
    public void BlockArrow_CapturesAndMutates() => Assert.Equal(7, Run("""
namespace Test
func go() -> int {
    var total = 0
    let add: Action<int> = (n) => { total = total + n }
    add(3)
    add(4)
    return total
}
""", "go"));

    // Block-bodied func literal as a call argument (the typed-return escape hatch).
    [Fact]
    public void FuncLiteral_BlockBody_AsCallArgument() => Assert.Equal(42, Run("""
namespace Test
func apply(f: Func<int, int>, x: int) -> int = f(x)
func go() -> int = apply(func(x: int) -> int { let y = x + 1  return y * 2 }, 20)
""", "go"));

    // Async lambda: a func literal whose body awaits, lowered to a state machine and run
    // through Task.Run (the TaskScope.Spawn shape).
    [Fact]
    public void AsyncLambda_AwaitsInBody() => Assert.Equal(42, Run("""
namespace Test
func go() -> int {
    let t = Task.Run(func() -> Task<int> {
        let v = await Task.FromResult(41)
        return v + 1
    })
    return t.GetAwaiter().GetResult()
}
""", "go"));
}
