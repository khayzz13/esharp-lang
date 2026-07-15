namespace Esharp.Tests;

/// <summary>
/// Durable <c>*T</c> calls are a single-location contract. These cases keep the
/// direct-call and forwarding paths independent from the function-pointer
/// regression: a wrapper ABI may never copy the pointee into another carrier.
/// </summary>
public sealed class DurablePointerCallTests
{
    [Fact]
    public void DirectDurableCall_RefPassPreservesTheCallerLocation()
    {
        Assert.Equal(42, EsHarness.Run("""
namespace Test
struct Cell { var value: int }
func retain(cell: *Cell) -> *Cell { return cell }
func go() -> int {
    var cell = Cell { value: 1 }
    var pointer: *Cell = retain(*cell)
    pointer.value = 42
    return cell.value
}
""", "go"));
    }

    [Fact]
    public void DurablePointerParameter_ForwardedRefPassPreservesItsCarrier()
    {
        Assert.Equal(42, EsHarness.Run("""
namespace Test
struct Cell { var value: int }
func retain(cell: *Cell) -> *Cell { return cell }
func forward(cell: *Cell) -> *Cell { return retain(*cell) }
func go() -> int {
    var cell = Cell { value: 1 }
    var pointer: *Cell = forward(*cell)
    pointer.value = 42
    return cell.value
}
""", "go"));
    }
}
