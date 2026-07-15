namespace Esharp.Tests;

/// <summary>
/// A single-name match binding over a single-payload <c>ref choice</c> case is a
/// *transparent* case view: the name IS the payload value (both the bare name and
/// <c>name.field</c> resolve), matching value-choice semantics. Previously the binding was
/// the variant subclass instance, so the natural <c>.jbool(b) { b ? … }</c> failed to bind
/// and fell through to a locationless backend error. Multi-payload cases keep the case view
/// (<c>.add(n) { n.left }</c>).
/// </summary>
public sealed class RefChoiceCaseViewTests
{
    static int Int(string src) => (int)EsHarness.Run(src, "go")!;
    static string Str(string src) => (string)EsHarness.Run(src, "go")!;
    static bool Bool(string src) => (bool)EsHarness.Run(src, "go")!;

    [Fact]
    public void SinglePayload_BareName_IsPayloadValue()
    {
        // `.lit(v)` — v IS the int payload, used directly.
        Assert.Equal(42, Int("""
namespace Test
ref union E { lit(value: int)  neg(inner: E) }
func eval(e: E) -> int {
    match e {
        .lit(v)   { return v }
        .neg(inner) { return 0 - eval(inner) }
    }
}
func go() -> int = eval(E.lit(42))
"""));
    }

    [Fact]
    public void SinglePayload_Recursion_ThroughBareName()
    {
        // `.neg(inner)` — inner IS the nested E payload, recursed on directly.
        Assert.Equal(-7, Int("""
namespace Test
ref union E { lit(value: int)  neg(inner: E) }
func eval(e: E) -> int {
    match e {
        .lit(v)     { return v }
        .neg(inner) { return 0 - eval(inner) }
    }
}
func go() -> int = eval(E.neg(E.lit(7)))
"""));
    }

    [Fact]
    public void SinglePayload_DotField_StillResolves()
    {
        // The transparent view keeps `name.payload` working alongside the bare name.
        Assert.Equal(9, Int("""
namespace Test
ref union Box { full(value: int)  empty }
func read(b: Box) -> int {
    match b {
        .full(c) { return c.value }   // c.value still works (aliases the bare binding)
        .empty   { return 0 }
    }
}
func go() -> int = read(Box.full(9))
"""));
    }

    [Fact]
    public void SinglePayload_BoolTransparent()
    {
        Assert.True(Bool("""
namespace Test
ref union Flag { on(value: bool)  off }
func read(f: Flag) -> bool {
    match f {
        .on(v) { return v }
        .off   { return false }
    }
}
func go() -> bool = read(Flag.on(true))
"""));
    }

    [Fact]
    public void SinglePayload_ListTransparent_Indexing()
    {
        // `.arr(items)` — items IS the List<int>, indexable directly.
        Assert.Equal(30, Int("""
namespace Test
ref union Seq { arr(items: List<int>)  empty }
func total(s: Seq) -> int {
    match s {
        .arr(items) {
            var sum = 0
            var i = 0
            while i < items.Count {
                sum += items[i]
                i += 1
            }
            return sum
        }
        .empty { return 0 }
    }
}
func go() -> int {
    let xs = List<int>()
    xs.Add(10)
    xs.Add(20)
    return total(Seq.arr(xs))
}
"""));
    }

    [Fact]
    public void MultiPayload_CaseView_StillWorks()
    {
        // `.add(n)` (two payloads) stays the subclass case view: n.left / n.right.
        Assert.Equal(12, Int("""
namespace Test
ref union E { lit(value: int)  add(left: E, right: E) }
func eval(e: E) -> int {
    match e {
        .lit(v)  { return v }
        .add(n)  { return eval(n.left) + eval(n.right) }
    }
}
func go() -> int = eval(E.add(E.lit(5), E.lit(7)))
"""));
    }

    [Fact]
    public void MultiPayload_Positional_StillWorks()
    {
        // `.add(l, r)` positional destructure unaffected.
        Assert.Equal(20, Int("""
namespace Test
ref union E { lit(value: int)  add(left: E, right: E) }
func eval(e: E) -> int {
    match e {
        .lit(v)    { return v }
        .add(l, r) { return eval(l) + eval(r) }
    }
}
func go() -> int = eval(E.add(E.lit(8), E.lit(12)))
"""));
    }

    [Fact]
    public void Json_Transparent_EndToEnd()
    {
        // The authored json.es shape in its transparent form: nested arrays/objects,
        // single-payload bare bindings, recursion.
        Assert.Equal("[1,[2,3]]", Str(JsonSource + "\nfunc go() -> string {\n" +
            "    let inner = List<J>()\n    inner.Add(J.num(2))\n    inner.Add(J.num(3))\n" +
            "    let outer = List<J>()\n    outer.Add(J.num(1))\n    outer.Add(J.arr(inner))\n" +
            "    return render(J.arr(outer))\n}"));
    }

    const string JsonSource = """
namespace Test
using "System.Text"
ref union J { num(value: int)  arr(items: List<J>) }
func render(j: J) -> string {
    match j {
        .num(n)     { return "{n}" }
        .arr(items) {
            let sb = StringBuilder()
            sb.Append("[")
            var i = 0
            while i < items.Count {
                if i > 0 { sb.Append(",") }
                sb.Append(render(items[i]))
                i += 1
            }
            sb.Append("]")
            return sb.ToString()
        }
    }
}
""";
}
