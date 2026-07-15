namespace Esharp.Tests;

/// Phase 2 — a generic `class` / `data` conforming to an interface, including a
/// GENERIC interface parameterized by the type's own parameter (`Box<T> : IBox<T>`).
/// This is the gating capability for the E# stdlib (`Chan<T> : IEnumerable<T>`).
public sealed class ILEmitterTests_GenericConformance
{
    static object? Run(string body, string method, params object?[] args) =>
        EsHarness.Run(body, method, args);

    // Generic class implementing a generic E# interface over its own T.
    [Fact]
    public void RefData_Generic_EsharpGenericInterface() => Assert.Equal(42, Run("""
namespace Test
interface IBox<T> { func get() -> T }
class Box<T> : IBox<T> {
    v: T
    init(x: T) { self.v = x }
    func get() -> T = self.v
}
func read(b: IBox<int>) -> int = b.get()
func go() -> int { let b = Box<int>(42) return read(b) }
""", "go"));

    // Isolation: plain generic class construction + method dispatch, no interface.
    [Fact]
    public void PlainGeneric_RefData_Construct() => Assert.Equal(42, Run("""
namespace Test
class Box2<T> {
    v: T
    init(x: T) { self.v = x }
    func get() -> T = self.v
}
func go() -> int { let b = Box2<int>(42) return b.get() }
""", "go"));

    // Generic value data implementing a generic E# interface over its own T.
    [Fact]
    public void ValueData_Generic_EsharpGenericInterface() => Assert.Equal(7, Run("""
namespace Test
interface IHolder<T> { func value() -> T }
struct Holder<T> : IHolder<T> { item: T }
func (h: Holder<T>) value<T>() -> T = h.item
func use(h: IHolder<int>) -> int = h.value()
func go() -> int { let h = Holder<int> { item: 7 } return use(h) }
""", "go"));
}
