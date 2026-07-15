using System.Linq;
using Esharp.Syntax.Parsing;
using Xunit;

namespace Esharp.Tests;

/// Locks in generics-resolution fixes for shapes where the "reified, never erased to
/// object" guarantee broke: nested user generics, same-name/different-arity user generics,
/// generic ref unions, and inference-from-result (which must be a located error, never
/// open-generic IL).
public sealed class ILEmitterTests_GenericResolution
{
    static object? Run(string body, string method) => EsHarness.Run(body, method);

    // probe3 #1 — a nested user generic `Box<Box<int>>` keeps its inner closed type; the
    // second `.get()` dispatches on `Box<int>`, not the erased `object`.
    [Fact]
    public void NestedUserGeneric_DispatchesOnClosedInner() => Assert.Equal(42, Run("""
namespace Test
class Box<T> { v: T  init(x: T) { self.v = x }  func get() -> T = self.v }
func go() -> int {
    let bb = Box<Box<int>>(Box<int>(41))
    return bb.get().get() + 1
}
""", "go"));

    // probe3 #2 — `Cell<A>` and `Cell<A, B>` coexist by arity; the arity-1 constructor
    // resolves even though an arity-2 sibling exists.
    [Fact]
    public void SameNameDifferentArity_BothConstruct() => Assert.Equal(14, Run("""
namespace Test
class Cell<A> { a: A  init(x: A) { self.a = x } }
class Cell<A, B> { a: A  b: B  init(x: A, y: B) { self.a = x  self.b = y } }
func go() -> int {
    let one = Cell<int>(7)
    let two = Cell<int, int>(3, 4)
    return one.a + two.a + two.b
}
""", "go"));

    // probe3 #3 — a generic `ref union Box<T>` carries the closed `T` through its per-case
    // sealed subclasses, so `match` over `Box<int>` binds an `int` payload.
    [Fact]
    public void GenericRefUnion_PayloadKeepsClosedT() => Assert.Equal(42, Run("""
namespace Test
ref union Box<T> { full(v: T), empty }
func unwrap(b: Box<int>) -> int = match b { .full(v) { v }  .empty { 0 } }
func go() -> int = unwrap(Box<int>.full(42))
""", "go"));

    // probe3 #4 — inference does NOT flow from the expected result type into the call, so
    // `let x: List<int> = make()` (with `make<T>() -> List<T>`) cannot pin T. The failure
    // must be a LOCATED inference-failure diagnostic, never an open `List<!!0>` reaching IL.
    [Fact]
    public void InferenceFromResult_IsLocatedError_NotOpenGenericIL()
    {
        var parser = new Parser("""
namespace Test
func make<T>() -> List<T> = List<T>()
func go() -> int {
    let x: List<int> = make()
    return x.Count
}
""", "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        binder.Bind(syntax);
        Assert.Contains(binder.Diagnostics, d => d.Message.Contains("ES2194"));
    }

    // Gap #1 — a `Chan<T>` nested inside `Func<Chan<T>, int>` in a generic function keeps
    // its enclosing type parameter rather than erasing to `Chan<object>`.
    [Fact]
    public void ChanT_NestedInFunc_KeepsTypeParameter() => Assert.Equal(5, Run("""
namespace Test
func runIt<T>(ch: Chan<T>, f: Func<Chan<T>, int>) -> int = f(ch)
func go() -> int {
    let ch = chan<int>(1)
    return runIt<int>(ch, (c) => 5)
}
""", "go"));

    // The same nested delegate shape must survive async lowering: MoveNext reads
    // producer/ch/token from its state-machine fields, not erased object placeholders.
    [Fact]
    public void AsyncChanT_NestedInFunc_KeepsCapturedParameters() => Assert.Equal(7, Run("""
namespace Test
using "System.Threading"
func runProducer<T>(producer: Func<Chan<T>, CancellationToken, Task>, ch: Chan<T>, token: CancellationToken) -> Task {
    await producer(ch, token)
}
func go() -> int {
    let ch = chan<int>(1)
    let producer: Func<Chan<int>, CancellationToken, Task> = (c, ct) => Task.CompletedTask
    runProducer<int>(producer, ch, CancellationToken.None).GetAwaiter().GetResult()
    return 7
}
""", "go"));

    // Gap #2 — a receiver method on a generic struct that re-declares the receiver's type
    // parameter (`func (w: Wrap<T>) get<T>()`) binds the method's `T` to the receiver type's
    // `T`, and resolves the BCL overload on the CLOSED field type (`Task<T>::GetAwaiter`).
    [Fact]
    public void GenericStructReceiver_FlowsReceiverT() => Assert.Equal(42, Run("""
namespace Test
struct Wrap<T> { task: Task<T> }
func (w: Wrap<T>) get<T>() -> T = w.task.GetAwaiter().GetResult()
func go() -> int {
    let w = Wrap<int> { task: Task.FromResult(42) }
    return w.get()
}
""", "go"));
}
