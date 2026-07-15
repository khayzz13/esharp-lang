using Esharp.Diagnostics;

namespace Esharp.Tests;

/// The bare `default` literal (C#-style, target-typed) alongside the explicit
/// `default(T)`. A bare `default` takes its type from the surrounding expected type —
/// a typed parameter default, a return, an annotated `let`, or an assignment target.
public sealed class DefaultLiteralTests
{
    // Bare `default` as a parameter default — the call site that omits the argument
    // materializes the type's zero value.
    [Fact]
    public void BareDefault_ParameterDefault_ZeroValue() => Assert.Equal(0, EsHarness.Run("""
namespace Test
func f(x: int = default) -> int = x
func go() -> int = f()
""", "go"));

    // Bare `default` target-typed by the function's return type.
    [Fact]
    public void BareDefault_Return_ZeroValue() => Assert.Equal(0, EsHarness.Run("""
namespace Test
func go() -> int {
    return default
}
""", "go"));

    // Bare `default` target-typed by an annotated `let`.
    [Fact]
    public void BareDefault_AnnotatedLet_ZeroValue() => Assert.Equal(0, EsHarness.Run("""
namespace Test
func go() -> int {
    let x: int = default
    return x
}
""", "go"));

    // Explicit `default(T)` still works (regression).
    [Fact]
    public void ExplicitDefaultOfType_StillWorks() => Assert.Equal(0, EsHarness.Run("""
namespace Test
func go() -> int = default(int)
""", "go"));

    // A bare `default` with no target type is a located error (ES2181), not silent.
    [Fact]
    public void BareDefault_NoTargetType_IsError()
    {
        var diags = EsHarness.Diagnostics("""
namespace Test
func go() -> int {
    let x = default
    return 0
}
""");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("ES2181"));
    }
}
