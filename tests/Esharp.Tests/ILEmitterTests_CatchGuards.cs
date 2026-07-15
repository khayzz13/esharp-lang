using Xunit;

namespace Esharp.Tests;

/// WS4 — `catch (e: T) if cond` guards (the CLR exception-filter shape, reusing `if`).
/// A guarded catch matches only when the type matches AND the guard holds; otherwise
/// control falls to the next clause. Unblocks `TaskScope`'s cancellation filter.
public sealed class ILEmitterTests_CatchGuards
{
    static object? Run(string body, string method, params object?[] args) => EsHarness.Run(body, method, args);

    // Guard true → the guarded clause wins.
    [Fact]
    public void CatchGuard_TrueTakesGuardedClause() => Assert.Equal("pos", Run("""
namespace Test
func classify(n: int) -> string {
    try { throw InvalidOperationException("boom") }
    catch (e: InvalidOperationException) if n > 0 { return "pos" }
    catch (e: InvalidOperationException) { return "nonpos" }
}
func go() -> string = classify(5)
""", "go"));

    // Guard false → falls through to the next (unguarded) clause of the same type.
    [Fact]
    public void CatchGuard_FalseFallsThrough() => Assert.Equal("nonpos", Run("""
namespace Test
func classify(n: int) -> string {
    try { throw InvalidOperationException("boom") }
    catch (e: InvalidOperationException) if n > 0 { return "pos" }
    catch (e: InvalidOperationException) { return "nonpos" }
}
func go() -> string = classify(-1)
""", "go"));

    // An untyped *bound* catch — `catch (e)` — catches any System.Exception and binds it,
    // so the body may read `e.Message` (spec: "bound, any exception"). Distinct from the
    // bare `catch` (no binding) and the typed `catch (e: T)`.
    [Fact]
    public void CatchBoundUntyped_BindsExceptionAndReadsMessage() => Assert.Equal("boom", Run("""
namespace Test
func go() -> string {
    try { throw InvalidOperationException("boom") }
    catch (e) { return e.Message }
}
""", "go"));
}
