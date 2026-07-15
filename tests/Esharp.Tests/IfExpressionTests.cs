using Xunit;

namespace Esharp.Tests;

// Plan 11 Part E — expression-oriented `if`. `if`/`else if`/`else` in expression position
// produces the taken branch's value; a branch body's value is its trailing expression. An
// `else` is required when consumed; branch types must agree (or a branch diverges). Statement
// position `if` is unchanged. ILVerify-gated by EsHarness.Compile.
public class IfExpressionTests
{
    [Fact]
    public void IfExpression_TwoArm_BindsValue()
    {
        var result = EsHarness.Run("""
namespace Test
func go() -> int {
    let n = 5
    let label = if n > 3 { 1 } else { 0 }
    return label
}
""", "go");
        Assert.Equal(1, (int)result!);
    }

    [Fact]
    public void IfExpression_ElseIfChain_SelectsBranch()
    {
        var result = EsHarness.Run("""
namespace Test
func classify(rsi: int) -> int {
    return if rsi < 30 { 1 } else if rsi > 70 { 2 } else { 0 }
}
func go() -> int = classify(80)
""", "go");
        Assert.Equal(2, (int)result!);
    }

    [Fact]
    public void IfExpression_BranchWithLeadingStatements_UsesTrailingValue()
    {
        var result = EsHarness.Run("""
namespace Test
func go() -> int {
    let x = 4
    let r = if x > 0 {
        let doubled = x * 2
        doubled + 1
    } else {
        0
    }
    return r
}
""", "go");
        Assert.Equal(9, (int)result!);
    }

    [Fact]
    public void IfExpression_DivergingBranch_Exempt()
    {
        // The `then` branch diverges (returns from the function), so it contributes no value;
        // the common type is the single `else` value.
        var result = EsHarness.Run("""
namespace Test
func go() -> int {
    let n = 2
    let v = if n < 0 { return -1 } else { n + 40 }
    return v
}
""", "go");
        Assert.Equal(42, (int)result!);
    }

    [Fact]
    public void IfExpression_AsArgument()
    {
        var result = EsHarness.Run("""
namespace Test
func twice(x: int) -> int = x * 2
func go() -> int {
    let flag = true
    return twice(if flag { 10 } else { 1 })
}
""", "go");
        Assert.Equal(20, (int)result!);
    }

    [Fact]
    public void IfExpression_AsCustomSetterBody_ClampsCrossTypeWrites()
    {
        // The spec's custom-setter shape (plan 11B): the setter body is a value expression,
        // here an `if`-expression — clamp negatives to zero.
        var result = EsHarness.Run("""
namespace Test
class Account {
    var balance: float { set(v) => if v < 0.0 { 0.0 } else { v } }
    init() { self.balance = 0.0 }
}
func go() -> float {
    let a = Account()
    a.balance = -50.0
    let lo = a.balance
    a.balance = 125.0
    return lo + a.balance
}
""", "go");
        Assert.Equal(125.0f, (float)result!);
    }

    [Fact]
    public void IfExpression_AsValue_RequiresElse()
    {
        var diags = EsHarness.Diagnostics("""
namespace Test
func go() -> int {
    let x = if true { 1 }
    return x
}
""");
        Assert.Contains(diags, d => d.Message.Contains("ES2197"));
    }

    [Fact]
    public void IfStatement_StillWorks_NoValueForced()
    {
        // Statement position is unchanged — the `if` here drives an assignment, not a value.
        var result = EsHarness.Run("""
namespace Test
func go() -> int {
    var total = 0
    if total < 1 { total = 7 } else { total = 99 }
    return total
}
""", "go");
        Assert.Equal(7, (int)result!);
    }
}
