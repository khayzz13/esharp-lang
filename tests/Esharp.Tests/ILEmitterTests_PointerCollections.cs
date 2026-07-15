using Esharp.Diagnostics;

namespace Esharp.Tests;

/// Triangulation suite for `*T` flowing through generic containers, tuples, and
/// generic functions. The seam under test: a heap pointer's rich element type
/// (`HeapPointerBoundType`) must survive into `List<*T>` / `Dictionary<K,*T>` /
/// `(*T, *T)` / `T = *X` so that indexing, iteration, destructuring, and
/// generic-arg substitution still auto-deref to the underlying fields.
///
/// Plain-value control cases sit next to each pointer case so a failure pinpoints
/// whether the loss is pointer-specific (literal element inference) or a deeper
/// index/iterate/substitute gap.
public sealed class ILEmitterTests_PointerCollections
{
    // ── List literals: value control vs pointer ─────────────────────────────

    [Fact]
    public void List_OfInts_Index() => Assert.Equal(20, EsHarness.Run("""
namespace Test
func go() -> int {
    let xs = [10, 20, 30]
    return xs[1]
}
""", "go"));

    [Fact]
    public void List_OfValueData_Index() => Assert.Equal(20, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let xs = [Box { n: 10 }, Box { n: 20 }]
    return xs[1].n
}
""", "go"));

    [Fact]
    public void List_OfPointers_Index() => Assert.Equal(20, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let xs = [new Box { n: 10 }, new Box { n: 20 }]
    return xs[1].n
}
""", "go"));

    [Fact]
    public void List_OfPointers_IndexZero() => Assert.Equal(10, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let xs = [new Box { n: 10 }, new Box { n: 20 }]
    return xs[0].n
}
""", "go"));

    [Fact]
    public void List_OfPointers_MutateThroughIndex() => Assert.Equal(11, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let xs = [new Box { n: 10 }, new Box { n: 20 }]
    xs[0].n += 1
    return xs[0].n
}
""", "go"));

    [Fact]
    public void List_OfPointers_ForIn_Sum() => Assert.Equal(60, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let xs = [new Box { n: 10 }, new Box { n: 20 }, new Box { n: 30 }]
    var total = 0
    for b in xs {
        total += b.n
    }
    return total
}
""", "go"));

    [Fact]
    public void List_OfValueData_ForIn_Sum() => Assert.Equal(60, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let xs = [Box { n: 10 }, Box { n: 20 }, Box { n: 30 }]
    var total = 0
    for b in xs {
        total += b.n
    }
    return total
}
""", "go"));

    [Fact]
    public void List_OfPointers_Count() => Assert.Equal(3, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let xs = [new Box { n: 1 }, new Box { n: 2 }, new Box { n: 3 }]
    return xs.Count
}
""", "go"));

    // ── Explicitly typed List<*T> (isolates literal-inference from index/iter) ─

    [Fact]
    public void ExplicitListOfPointers_AddThenIndex() => Assert.Equal(7, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    var xs = List<*Box>()
    xs.Add(new Box { n: 7 })
    return xs[0].n
}
""", "go"));

    [Fact]
    public void ExplicitListOfPointers_ForIn() => Assert.Equal(12, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    var xs = List<*Box>()
    xs.Add(new Box { n: 5 })
    xs.Add(new Box { n: 7 })
    var t = 0
    for b in xs { t += b.n }
    return t
}
""", "go"));

    // ── List<*T> as field / parameter / return ──────────────────────────────

    [Fact]
    public void PointerListField_RoundTrip() => Assert.Equal(9, EsHarness.Run("""
namespace Test
struct Box { n: int }
struct Bag { items: List<*Box> }
func go() -> int {
    var bag = Bag { items: List<*Box>() }
    bag.items.Add(new Box { n: 9 })
    return bag.items[0].n
}
""", "go"));

    [Fact]
    public void PointerListReturn() => Assert.Equal(2, EsHarness.Run("""
namespace Test
struct Box { n: int }
func build() -> List<*Box> {
    var xs = List<*Box>()
    xs.Add(new Box { n: 1 })
    xs.Add(new Box { n: 2 })
    return xs
}
func go() -> int {
    let xs = build()
    return xs.Count
}
""", "go"));

    [Fact]
    public void PointerListParam_Sum() => Assert.Equal(8, EsHarness.Run("""
namespace Test
struct Box { n: int }
func total(xs: List<*Box>) -> int {
    var t = 0
    for b in xs { t += b.n }
    return t
}
func go() -> int {
    var xs = List<*Box>()
    xs.Add(new Box { n: 3 })
    xs.Add(new Box { n: 5 })
    return total(xs)
}
""", "go"));

    // ── Tuples of pointers ──────────────────────────────────────────────────

    [Fact]
    public void TupleOfInts_Item() => Assert.Equal(5, EsHarness.Run("""
namespace Test
func go() -> int {
    let t = (2, 5)
    return t.Item2
}
""", "go"));

    [Fact]
    public void TupleOfPointers_ItemDeref() => Assert.Equal(5, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let t = (new Box { n: 2 }, new Box { n: 5 })
    return t.Item2.n
}
""", "go"));

    [Fact]
    public void TupleOfPointers_Destructure() => Assert.Equal(7, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let (a, b) = (new Box { n: 2 }, new Box { n: 5 })
    return a.n + b.n
}
""", "go"));

    // ── Generic functions with T = *X ───────────────────────────────────────

    // Deferred (#5 type inference): omitting `<T>` so the compiler infers `T = *Box`
    // from the argument is generic-argument inference for user functions — not yet
    // wired. Explicit value-type args (`identity<int>(…)`) DO work; see
    // ILEmitterTests2.Generic_Identity_Function_Int_LetBinding.
    [Fact(Skip = "generic-argument inference for user functions is item #5 (type inference), deferred")]
    public void GenericIdentity_OnPointer() => Assert.Equal(13, EsHarness.Run("""
namespace Test
struct Box { n: int }
func identity<T>(v: T) -> T { return v }
func go() -> int {
    let b = identity(new Box { n: 13 })
    return b.n
}
""", "go"));

    // Deferred: a generic user function whose body constructs over its own type
    // parameter (`xs: List<T>` → must emit `List<!!0>`, currently erased to
    // `List<object>`) plus passing a `*T` argument needs generic-method-body +
    // call-site coercion support for module type args. Separate subsystem from the
    // collection element-typing fixed in this suite.
    [Fact(Skip = "generic user function with List<T> body + *T type arg (generic-method module type args) not yet implemented")]
    public void GenericFirst_OverPointerList() => Assert.Equal(4, EsHarness.Run("""
namespace Test
struct Box { n: int }
func first<T>(xs: List<T>) -> T { return xs[0] }
func go() -> int {
    var xs = List<*Box>()
    xs.Add(new Box { n: 4 })
    let b = first<*Box>(xs)
    return b.n
}
""", "go"));

    // ── Pointer as a generic data field ─────────────────────────────────────

    [Fact]
    public void GenericData_HoldingPointer() => Assert.Equal(6, EsHarness.Run("""
namespace Test
struct Box { n: int }
struct Cell<T> { value: T }
func go() -> int {
    let c = Cell<*Box> { value: new Box { n: 6 } }
    return c.value.n
}
""", "go"));

    // ── Dictionary with pointer values ──────────────────────────────────────

    [Fact]
    public void DictionaryStringToPointer() => Assert.Equal(99, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    var m = Dictionary<string, *Box>()
    m["a"] = new Box { n: 99 }
    return m["a"].n
}
""", "go"));

    // ── Nested containers ───────────────────────────────────────────────────

    [Fact]
    public void NestedListOfPointerLists() => Assert.Equal(2, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    var outer = List<List<*Box>>()
    var inner = List<*Box>()
    inner.Add(new Box { n: 1 })
    inner.Add(new Box { n: 2 })
    outer.Add(inner)
    return outer[0].Count
}
""", "go"));

    // === added: deeper pointer/generic plumbing ───────────────────────────

    [Fact]
    public void NestedListOfPointers_IndexDeref() => Assert.Equal(2, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    var outer = List<List<*Box>>()
    var inner = List<*Box>()
    inner.Add(new Box { n: 1 })
    inner.Add(new Box { n: 2 })
    outer.Add(inner)
    return outer[0][1].n
}
""", "go"));

    [Fact]
    public void PointerList_IterateAndMutate() => Assert.Equal(6, EsHarness.Run("""
namespace Test
struct Box { var n: int }
func go() -> int {
    let xs = [new Box { n: 1 }, new Box { n: 2 }, new Box { n: 3 }]
    for b in xs { b.n += 0 }
    var t = 0
    for b in xs { t += b.n }
    return t
}
""", "go"));

    [Fact]
    public void GenericData_PointerField_Mutate() => Assert.Equal(8, EsHarness.Run("""
namespace Test
struct Box { var n: int }
struct Cell<T> { value: T }
func go() -> int {
    let c = Cell<*Box> { value: new Box { n: 7 } }
    c.value.n += 1
    return c.value.n
}
""", "go"));

    [Fact]
    public void Dictionary_PointerValue_Iterate() => Assert.Equal(30, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    var m = Dictionary<string, *Box>()
    m["a"] = new Box { n: 10 }
    m["b"] = new Box { n: 20 }
    return m["a"].n + m["b"].n
}
""", "go"));

    [Fact(Skip = "generic-argument inference (#5) + generic-method List<T> body, deferred")]
    public void GenericFirst_PointerInferred() => Assert.Equal(4, EsHarness.Run("""
namespace Test
struct Box { n: int }
func first<T>(xs: List<T>) -> T = xs[0]
func go() -> int {
    var xs = List<*Box>()
    xs.Add(new Box { n: 4 })
    let b = first(xs)
    return b.n
}
""", "go"));

    [Fact]
    public void TupleOfMixed_PointerAndInt() => Assert.Equal(15, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let t = (new Box { n: 5 }, 10)
    return t.Item1.n + t.Item2
}
""", "go"));

    [Fact]
    public void PointerList_PassToValueDataMethod() => Assert.Equal(9, EsHarness.Run("""
namespace Test
struct Box { n: int }
func sumList(xs: List<*Box>) -> int {
    var t = 0
    for b in xs { t += b.n }
    return t
}
func go() -> int {
    let xs = [new Box { n: 4 }, new Box { n: 5 }]
    return sumList(xs)
}
""", "go"));

    [Fact]
    public void ValueDataList_Index_Control() => Assert.Equal(20, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let xs = [Box { n: 10 }, Box { n: 20 }]
    return xs[1].n
}
""", "go"));

    [Fact]
    public void PointerList_NestedFieldAccess() => Assert.Equal(7, EsHarness.Run("""
namespace Test
struct Inner { v: int }
struct Outer { inner: *Inner }
func go() -> int {
    let xs = [new Outer { inner: new Inner { v: 7 } }]
    return xs[0].inner.v
}
""", "go"));

    [Fact]
    public void PointerReturnedFromListBoundMethod() => Assert.Equal(2, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    var xs = List<*Box>()
    xs.Add(new Box { n: 1 })
    xs.Add(new Box { n: 2 })
    let last = xs[xs.Count - 1]
    return last.n
}
""", "go"));

    [Fact]
    public void GenericData_Nested_PointerInner() => Assert.Equal(3, EsHarness.Run("""
namespace Test
struct Box { n: int }
struct Cell<T> { value: T }
func go() -> int {
    let c = Cell<Cell<*Box>> { value: Cell<*Box> { value: new Box { n: 3 } } }
    return c.value.value.n
}
""", "go"));

    [Fact]
    public void PointerList_CountAfterAdds() => Assert.Equal(3, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    var xs = List<*Box>()
    xs.Add(new Box { n: 1 })
    xs.Add(new Box { n: 2 })
    xs.Add(new Box { n: 3 })
    return xs.Count
}
""", "go"));

    [Fact]
    public void TupleOfThreePointers_Destructure() => Assert.Equal(6, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let (a, b, c) = (new Box { n: 1 }, new Box { n: 2 }, new Box { n: 3 })
    return a.n + b.n + c.n
}
""", "go"));
}
