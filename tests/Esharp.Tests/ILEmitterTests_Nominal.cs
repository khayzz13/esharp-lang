namespace Esharp.Tests;

/// Nominal interface conformance (#2): a type implements an interface only when it
/// names it after ':'. Structural coincidence is not conformance. The matcher is
/// exact (name + parameter types + return type), and an *T-only method set routes
/// the conformance to the wrapper, not the struct.
public sealed class ILEmitterTests_Nominal
{
    static object? Run(string body, string method, params object?[] args) =>
        EsHarness.Run(body, method, args);

    static IReadOnlyList<Esharp.Diagnostics.Diagnostic> Diags(string body) =>
        EsHarness.Diagnostics(body);

    // A value type that declares conformance and matches exactly works.
    [Fact]
    public void ExplicitConformance_ExactMatch_Works() =>
        Assert.Equal(11, Run("""
namespace Test
interface ISized { func size() -> int }
struct Crate : ISized { items: int }
func (c: Crate) size() -> int { return c.items + 1 }
func measure(s: ISized) -> int { return s.size() }
func go() -> int { let c = Crate { items: 10 } return measure(c) }
""", "go"));

    // Declared conformance with a mismatched RETURN type is no longer accepted
    // (the old name+arity matcher falsely conformed this).
    [Fact]
    public void Declared_ReturnTypeMismatch_IsError()
    {
        var d = Diags("""
namespace Test
interface ISized { func size() -> int }
struct Crate : ISized { items: int }
func (c: Crate) size() -> string { return "x" }
""");
        Assert.Contains(d, x => x.Message.Contains("does not implement all required methods"));
    }

    // Declared conformance with a mismatched PARAMETER type is no longer accepted.
    [Fact]
    public void Declared_ParamTypeMismatch_IsError()
    {
        var d = Diags("""
namespace Test
interface IAdder { func add(n: int) -> int }
struct Box : IAdder { v: int }
func (b: Box) add(n: string) -> int { return b.v }
""");
        Assert.Contains(d, x => x.Message.Contains("does not implement all required methods"));
    }

    // A type that structurally matches but does NOT declare the interface does not
    // implement it: the ES2153 migration warning points at the fix, and the
    // non-conforming pass is rejected at emit by ILVerify (ES0900) — not silently
    // accepted.
    [Fact]
    public void Undeclared_StructuralMatch_RejectedAndSuggested()
    {
        var d = EsHarness.AllDiagnostics("""
namespace Test
interface ISized { func size() -> int }
struct Crate { items: int }
func (c: Crate) size() -> int { return c.items + 1 }
func measure(s: ISized) -> int { return s.size() }
func go() -> int { let c = Crate { items: 10 } return measure(c) }
""");
        Assert.Contains(d, x => x.Severity == Esharp.Diagnostics.DiagnosticSeverity.Warning && x.Message.Contains("ES2153"));
        Assert.Contains(d, x => x.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error
            && (x.Message.Contains("ES0900") || x.Message.Contains("verification")));
    }

    // A pointer-receiver method declared on the value type routes conformance to the
    // *T wrapper: *T satisfies the interface, the value type does not.
    [Fact]
    public void PointerOnlyReceiver_Declared_SatisfiedViaWrapper() =>
        Assert.Equal(60, Run("""
namespace Test
struct Node : ISummable { value: int, next: *Node }
func (head: *Node) sum() -> int {
    var total = 0
    var current = head
    while current != nil {
        total += current.value
        current = current.next
    }
    return total
}
interface ISummable { func sum() -> int }
func report(s: ISummable) -> int { return s.sum() }
func go() -> int {
    var list: *Node = nil
    list = new Node { value: 10, next: nil }
    list = new Node { value: 20, next: list }
    list = new Node { value: 30, next: list }
    return report(list)
}
""", "go"));

    // === added: more nominal-conformance coverage ===

    [Fact]
    public void Added_RefData_DeclaresInterface_Works() => Assert.Equal(7, Run("""
namespace Test
interface ICounter { func get() -> int }
class Counter : ICounter {
    value: int
    init(v: int) { self.value = v }
    func get() -> int = self.value
}
func read(c: ICounter) -> int = c.get()
func go() -> int { let c = Counter(7) return read(c) }
""", "go"));

    [Fact]
    public void Added_TwoInterfaces_BothSatisfied() => Assert.Equal(30, Run("""
namespace Test
interface INamed { func label() -> int }
interface ISized { func size() -> int }
struct Widget : INamed, ISized { a: int, b: int }
func (w: Widget) label() -> int = w.a
func (w: Widget) size() -> int = w.b
func both(n: INamed, s: ISized) -> int = n.label() + s.size()
func go() -> int { let w = Widget { a: 10, b: 20 } return both(w, w) }
""", "go"));

    [Fact]
    public void Added_UndeclaredStructuralMatch_WarnsES2153()
    {
        var diags = EsHarness.AllDiagnostics("""
namespace Test
interface ISized { func size() -> int }
struct Crate { items: int }
func (c: Crate) size() -> int = c.items
""");
        Assert.Contains(diags, d => d.Message.Contains("ES2153"));
    }

    [Fact]
    public void Added_UndeclaredStructuralMatch_IsNotConformance()
    {
        // Without the explicit ': ISized', passing Crate where ISized is expected
        // is rejected (ILVerify ES0900 / a binder error) — structural coincidence
        // does not satisfy the interface.
        var diags = EsHarness.AllDiagnostics("""
namespace Test
interface ISized { func size() -> int }
struct Crate { items: int }
func (c: Crate) size() -> int = c.items
func measure(s: ISized) -> int = s.size()
func go() -> int { let c = Crate { items: 3 } return measure(c) }
""");
        Assert.Contains(diags, d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
    }

    [Fact]
    public void Added_ParamTypeMismatch_IsError()
    {
        var d = Diags("""
namespace Test
interface IAdder { func add(x: int) -> int }
struct Calc : IAdder { base: int }
func (c: Calc) add(x: string) -> int = c.base
""");
        Assert.Contains(d, x => x.Message.Contains("does not implement all required methods"));
    }

    [Fact]
    public void Added_MarkerInterface_NoMethods_Conforms() => Assert.Equal(5, Run("""
namespace Test
interface ITag { }
struct Item : ITag { v: int }
func tagged(t: ITag) -> int = 5
func go() -> int { let i = Item { v: 1 } return tagged(i) }
""", "go"));

    [Fact]
    public void Added_InterfaceReturnedFromFunction() => Assert.Equal(8, Run("""
namespace Test
interface ISized { func size() -> int }
class Crate : ISized {
    n: int
    init(n: int) { self.n = n }
    func size() -> int = self.n
}
func make() -> ISized = Crate(8)
func go() -> int { let s = make() return s.size() }
""", "go"));

    [Fact]
    public void Added_PointerOnlyConformance_Wrapper() => Assert.Equal(43, Run("""
namespace Test
struct Inner { var n: int }
func (i: *Inner) bump() { i.n += 1 }
func (i: Inner) value() -> int = i.n
struct Counter : ICounter { *Inner }
interface ICounter { func bump(), func value() -> int }
func tick(c: ICounter) -> int { c.bump() return c.value() }
func go() -> int {
    var c: *Counter = new Counter { Inner: new Inner { n: 42 } }
    return tick(c)
}
""", "go"));

    [Fact]
    public void Added_InterfaceMethodViaPromotion() => Assert.Equal(15, Run("""
namespace Test
interface IArea { func area() -> int }
struct Rect : IArea { w: int, h: int }
func (r: Rect) area() -> int = r.w * r.h
func go() -> int { let r = Rect { w: 3, h: 5 } let a: IArea = r return a.area() }
""", "go"));

    // A generic value `data` implementing a NON-generic interface via a promoted
    // generic method — the promoted self-param types as `Pair<T0>` against the
    // interface slot. Fields on `this` re-home onto the self-instantiation.
    [Fact]
    public void Added_GenericConformance() => Assert.Equal(9, Run("""
namespace Test
interface ISized { func size() -> int }
struct Pair<A> : ISized { a: A, count: int }
func (p: Pair<A>) size<A>() -> int = p.count
func go() -> int { let p = Pair<int> { a: 1, count: 9 } let s: ISized = p return s.size() }
""", "go"));

    // A generic `class` conforming to a GENERIC E# interface parameterized by the
    // type's OWN type argument (`Box<T> : IBoxOf<T>`). The InterfaceImplementation must
    // be the closed instance `IBoxOf<!0>`, and the method override must re-home onto it.
    [Fact]
    public void Added_GenericConformance_GenericInterface() => Assert.Equal(42, Run("""
namespace Test
interface IBoxOf<T> { func get() -> T }
class Box<T> : IBoxOf<T> {
    value: T
    init(v: T) { self.value = v }
    func get() -> T = self.value
}
func read(b: IBoxOf<int>) -> int = b.get()
func go() -> int { let b = Box<int>(42) return read(b) }
""", "go"));

    // A generic `class` conforming to a generic BCL interface parameterized by its
    // own type argument (`Seq<T> : IEnumerable<T>` + the non-generic `IEnumerable`).
    // Closed-generic InterfaceImplementation against a CoreLib variant interface, plus
    // the transitive non-generic base. Driven by a C# foreach over the boxed instance.
    [Fact]
    public void Added_GenericConformance_IEnumerable()
    {
        var asm = EsHarness.Compile("""
namespace Test
using "System.Collections"
using "System.Collections.Generic"
class Seq<T> : IEnumerable<T>, IEnumerable {
    items: List<T>
    init(xs: List<T>) { self.items = xs }
    func GetEnumerator() -> IEnumerator<T> = self.items.GetEnumerator()
}
func make() -> Seq<int> {
    var xs = List<int>()
    xs.Add(3)
    xs.Add(4)
    return Seq<int>(xs)
}
""");
        var seq = EsHarness.Invoke(asm, "make")!;
        var sum = 0;
        foreach (var x in (System.Collections.Generic.IEnumerable<int>)seq) sum += x;
        Assert.Equal(7, sum);
    }
}
