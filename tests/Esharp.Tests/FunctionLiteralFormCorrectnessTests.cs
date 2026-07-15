using Xunit;

namespace Esharp.Tests;

/// <summary>
/// The explicit <c>func</c> and inference-first <c>=&gt;</c> spellings share closure
/// conversion, but their source typing paths are deliberately different. Keep both
/// paths covered at the async state-machine boundary: that is where an inferred
/// declaration type can otherwise become <c>object</c> after conversion.
/// </summary>
public sealed class FunctionLiteralFormCorrectnessTests
{
    [Fact]
    public void ExplicitFuncClosureAcrossAwait_SpillsItsConcreteDelegateAndDurableCapture()
    {
        var assembly = EsHarness.Compile("""
namespace Test

struct Cell { var value: int }

func compute() -> Task<int> {
    var cell = Cell { value: 40 }
    var pointer = &cell
    let increment = func() -> int {
        pointer.value += 1
        return pointer.value
    }
    await Task.Delay(1)
    return increment() + cell.value
}
""", "ExplicitFuncClosureAcrossAwait");

        Assert.Equal(82, EsHarness.Await(EsHarness.Invoke(assembly, "compute")));
        AssertConcreteDelegateSpillAndNoByRefState(assembly, "increment");
    }

    [Fact]
    public void ArrowClosureAcrossAwait_SpillsItsConcreteDelegateAndDurableCapture()
    {
        var assembly = EsHarness.Compile("""
namespace Test

struct Cell { var value: int }

func compute() -> Task<int> {
    var cell = Cell { value: 40 }
    var pointer = &cell
    let increment = () => {
        pointer.value += 1
        return pointer.value
    }
    await Task.Delay(1)
    return increment() + cell.value
}
""", "ArrowClosureAcrossAwait");

        Assert.Equal(82, EsHarness.Await(EsHarness.Invoke(assembly, "compute")));
        AssertConcreteDelegateSpillAndNoByRefState(assembly, "increment");
    }

    [Fact]
    public void ExplicitFuncLiteral_OwnsAwaitAndReturnsItsDeclaredTaskShape()
    {
        var result = EsHarness.Await(EsHarness.Run("""
namespace Test

func compute() -> Task<int> {
    let work = func() -> Task<int> {
        let value = await Task.FromResult(41)
        return value + 1
    }
    return await work()
}
""", "compute"));

        Assert.Equal(42, result);
    }

    [Fact]
    public void ArrowLiteral_OwnsAwaitAndInfersItsCallableResult()
    {
        var result = EsHarness.Await(EsHarness.Run("""
namespace Test

func compute() -> Task<int> {
    let work = () => {
        let value = await Task.FromResult(41)
        return value + 1
    }
    return await work()
}
""", "compute"));

        Assert.Equal(42, result);
    }

    [Fact]
    public void TypedArrowParameter_CanEstablishAStandaloneClosureSignature()
    {
        var result = EsHarness.Run("""
namespace Test

func compute() -> int {
    let increment = (value: int) => {
        return value + 1
    }
    return increment(41)
}
""", "compute");

        Assert.Equal(42, result);
    }

    static void AssertConcreteDelegateSpillAndNoByRefState(System.Reflection.Assembly assembly, string localName)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var machine = assembly.GetTypes().Single(type => type.Name.Contains("StateMachine", StringComparison.Ordinal));
        var delegateField = Assert.Single(machine.GetFields(flags), field => field.Name == "__async_state_" + localName);
        Assert.Equal(typeof(Func<int>), delegateField.FieldType);
        Assert.DoesNotContain(machine.GetFields(flags), field => field.FieldType.IsByRef);
    }
}
