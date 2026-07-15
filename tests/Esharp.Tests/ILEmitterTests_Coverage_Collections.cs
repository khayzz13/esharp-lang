// Style note: E# source uses readable """ raw-string blocks (these double as the
// E# corpus) — do not collapse them into inline \n-escaped one-liners.
namespace Esharp.Tests;

/// Strings, string interpolation, List / Dictionary, LINQ extension methods,
/// tuples and destructuring, and nullable T?. Behavioral coverage through the
/// IL backend — the surface most real E# programs lean on.
public sealed class ILEmitterTests_Coverage_Collections
{
    static object? Run(string src, string method = "go", params object?[] args)
        => EsHarness.Run(src, method, args);

    // ── string basics ──

    [Fact]
    public void String_Concatenation()
    {
        Assert.Equal("hello world", Run("""
namespace Test
func go() -> string {
    let a = "hello"
    let b = "world"
    return a + " " + b
}
""", "go"));
    }

    [Fact]
    public void String_Length()
    {
        Assert.Equal(5, Run("""
namespace Test
func go() -> int { return "hello".Length }
"""));
    }

    [Fact]
    public void String_Substring()
    {
        Assert.Equal("ell", Run("""
namespace Test
func go() -> string { return "hello".Substring(1, 3) }
""", "go"));
    }

    [Fact]
    public void String_ToUpper()
    {
        Assert.Equal("HELLO", Run("""
namespace Test
func go() -> string { return "hello".ToUpper() }
""", "go"));
    }

    [Fact]
    public void String_Contains()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool { return "hello world".Contains("world") }
"""));
    }

    [Fact]
    public void String_StartsWith()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool { return "hello".StartsWith("he") }
"""));
    }

    [Fact]
    public void String_IndexOf()
    {
        Assert.Equal(6, Run("""
namespace Test
func go() -> int { return "hello world".IndexOf("world") }
"""));
    }

    [Fact]
    public void String_Replace()
    {
        Assert.Equal("hexxo", Run("""
namespace Test
func go() -> string { return "hello".Replace("l", "x") }
""", "go"));
    }

    [Fact]
    public void String_EqualityComparison()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool {
    let a = "abc"
    let b = "abc"
    return a == b
}
"""));
    }

    [Fact]
    public void String_Inequality()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool { return "abc" != "abd" }
"""));
    }

    [Fact]
    public void String_TrimAndEmpty()
    {
        Assert.Equal(0, Run("""
namespace Test
func go() -> int { return "   ".Trim().Length }
"""));
    }

    // ── string interpolation ──

    [Fact]
    public void Interp_SimpleVariable()
    {
        Assert.Equal("hi alice", Run("""
namespace Test
func go() -> string {
    let name = "alice"
    return "hi {name}"
}
""", "go"));
    }

    [Fact]
    public void Interp_IntegerBoxing()
    {
        Assert.Equal("count=3", Run("""
namespace Test
func go() -> string {
    let n = 3
    return "count={n}"
}
""", "go"));
    }

    [Fact]
    public void Interp_ArithmeticExpression()
    {
        Assert.Equal("sum=7", Run("""
namespace Test
func go() -> string {
    let a = 3
    let b = 4
    return "sum={a + b}"
}
""", "go"));
    }

    [Fact]
    public void Interp_MemberAccess()
    {
        Assert.Equal("x=10", Run("""
namespace Test
struct Pt { x: int, y: int }
func go() -> string {
    let p = Pt { x: 10, y: 20 }
    return "x={p.x}"
}
""", "go"));
    }

    [Fact]
    public void Interp_MethodCall()
    {
        Assert.Equal("len=5", Run("""
namespace Test
func go() -> string {
    let s = "hello"
    return "len={s.Length}"
}
""", "go"));
    }

    [Fact]
    public void Interp_TernaryInsideHole()
    {
        Assert.Equal("sign=+", Run("""
namespace Test
func go() -> string {
    let n = 5
    return "sign={n > 0 ? "+" : "-"}"
}
""", "go"));
    }

    [Fact]
    public void Interp_MultipleHoles()
    {
        Assert.Equal("a1b2", Run("""
namespace Test
func go() -> string {
    let x = 1
    let y = 2
    return "a{x}b{y}"
}
""", "go"));
    }

    [Fact]
    public void Interp_LiteralBraces()
    {
        // Hole-free string → verbatim braces (Option B: `{{`/`}}` collapse only when the
        // string actually interpolates). See InterpolationBraceTests for the full matrix.
        Assert.Equal("{{x}}", Run("""
namespace Test
func go() -> string {
    return "{{x}}"
}
""", "go"));
    }

    // ── List<T> ──

    [Fact]
    public void List_LiteralAndIndex()
    {
        Assert.Equal(20, Run("""
namespace Test
func go() -> int {
    let xs = [10, 20, 30]
    return xs[1]
}
"""));
    }

    [Fact]
    public void List_Count()
    {
        Assert.Equal(3, Run("""
namespace Test
func go() -> int {
    let xs = [1, 2, 3]
    return xs.Count
}
"""));
    }

    [Fact]
    public void List_AddThenCount()
    {
        Assert.Equal(2, Run("""
namespace Test
func go() -> int {
    let xs = List<int>()
    xs.Add(1)
    xs.Add(2)
    return xs.Count
}
"""));
    }

    [Fact]
    public void List_MutateElement()
    {
        Assert.Equal(99, Run("""
namespace Test
func go() -> int {
    let xs = [1, 2, 3]
    xs[0] = 99
    return xs[0]
}
"""));
    }

    [Fact]
    public void List_ForInSum()
    {
        Assert.Equal(60, Run("""
namespace Test
func go() -> int {
    let xs = [10, 20, 30]
    var total = 0
    for x in xs {
        total += x
    }
    return total
}
"""));
    }

    [Fact]
    public void List_StringElements()
    {
        Assert.Equal("b", Run("""
namespace Test
func go() -> string {
    let xs = ["a", "b", "c"]
    return xs[1]
}
""", "go"));
    }

    [Fact]
    public void List_Contains()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool {
    let xs = [1, 2, 3]
    return xs.Contains(2)
}
"""));
    }

    [Fact]
    public void List_OfData()
    {
        Assert.Equal(30, Run("""
namespace Test
struct Pt { x: int, y: int }
func go() -> int {
    let pts = List<Pt>()
    pts.Add(Pt { x: 10, y: 20 })
    let p = pts[0]
    return p.x + p.y
}
"""));
    }

    // ── LINQ extension methods ──

    [Fact]
    public void Linq_Sum()
    {
        Assert.Equal(15, Run("""
namespace Test
func go() -> int {
    let xs = [1, 2, 3, 4, 5]
    return xs.Sum()
}
"""));
    }

    [Fact]
    public void Linq_Count()
    {
        Assert.Equal(4, Run("""
namespace Test
func go() -> int {
    let xs = [1, 2, 3, 4]
    return xs.Count()
}
"""));
    }

    [Fact]
    public void Linq_Max()
    {
        Assert.Equal(30, Run("""
namespace Test
func go() -> int {
    let xs = [10, 30, 20]
    return xs.Max()
}
"""));
    }

    [Fact]
    public void Linq_Min()
    {
        Assert.Equal(10, Run("""
namespace Test
func go() -> int {
    let xs = [30, 10, 20]
    return xs.Min()
}
"""));
    }

    // ── Dictionary<K,V> ──

    [Fact]
    public void Dict_PutGet()
    {
        Assert.Equal(42, Run("""
namespace Test
func go() -> int {
    let d = Dictionary<string, int>()
    d["answer"] = 42
    return d["answer"]
}
"""));
    }

    [Fact]
    public void Dict_Count()
    {
        Assert.Equal(2, Run("""
namespace Test
func go() -> int {
    let d = Dictionary<string, int>()
    d["a"] = 1
    d["b"] = 2
    return d.Count
}
"""));
    }

    [Fact]
    public void Dict_ContainsKey()
    {
        Assert.Equal(true, Run("""
namespace Test
func go() -> bool {
    let d = Dictionary<string, int>()
    d["k"] = 1
    return d.ContainsKey("k")
}
"""));
    }

    [Fact]
    public void Dict_OverwriteValue()
    {
        Assert.Equal(2, Run("""
namespace Test
func go() -> int {
    let d = Dictionary<string, int>()
    d["k"] = 1
    d["k"] = 2
    return d["k"]
}
"""));
    }

    [Fact]
    public void Dict_TryGetValue()
    {
        Assert.Equal(7, Run("""
namespace Test
func go() -> int {
    let d = Dictionary<string, int>()
    d["k"] = 7
    if d.TryGetValue("k", out var v) {
        return v
    }
    return -1
}
"""));
    }

    [Fact]
    public void Dict_IntKey()
    {
        Assert.Equal("hello", Run("""
namespace Test
func go() -> string {
    let d = Dictionary<int, string>()
    d[1] = "hello"
    return d[1]
}
""", "go"));
    }

    // ── nested generics ──

    [Fact]
    public void Nested_ListOfList()
    {
        Assert.Equal(3, Run("""
namespace Test
func go() -> int {
    let outer = List<List<int>>()
    let inner = [1, 2, 3]
    outer.Add(inner)
    return outer[0].Count
}
"""));
    }

    [Fact]
    public void Nested_DictOfList()
    {
        Assert.Equal(2, Run("""
namespace Test
func go() -> int {
    let d = Dictionary<string, List<int>>()
    d["nums"] = [10, 20]
    return d["nums"].Count
}
"""));
    }

    // ── tuples ──

    [Fact]
    public void Tuple_Construct()
    {
        Assert.Equal(7, Run("""
namespace Test
func go() -> int {
    let t = (3, 4)
    return t.Item1 + t.Item2
}
"""));
    }

    [Fact]
    public void Tuple_ReturnFromFunc()
    {
        Assert.Equal(5, Run("""
namespace Test
func swap(a: int, b: int) -> (int, int) {
    return (b, a)
}
func go() -> int {
    let t = swap(2, 3)
    return t.Item1 + t.Item2
}
"""));
    }

    [Fact]
    public void Tuple_DestructureInLet()
    {
        Assert.Equal(1, Run("""
namespace Test
func swap(a: int, b: int) -> (int, int) {
    return (b, a)
}
func go() -> int {
    let (x, y) = swap(2, 1)
    return x
}
"""));
    }

    [Fact]
    public void Tuple_MixedTypes()
    {
        Assert.Equal("alice:30", Run("""
namespace Test
func go() -> string {
    let t = ("alice", 30)
    return "{t.Item1}:{t.Item2}"
}
""", "go"));
    }

    [Fact]
    public void Tuple_DestructureInFor()
    {
        Assert.Equal(60, Run("""
namespace Test
func go() -> int {
    let pairs = List<(int, int)>()
    pairs.Add((10, 20))
    pairs.Add((30, 0))
    var total = 0
    for (a, b) in pairs {
        total += a + b
    }
    return total
}
"""));
    }

    // ── nullable T? ──

    [Fact]
    public void Nullable_ValuePresent()
    {
        Assert.Equal(42, Run("""
namespace Test
func find(id: int) -> int? {
    if id == 0 { return nil }
    return 42
}
func go() -> int {
    let v = find(1)
    return v ?? -1
}
"""));
    }

    [Fact]
    public void Nullable_NilFallback()
    {
        Assert.Equal(-1, Run("""
namespace Test
func find(id: int) -> int? {
    if id == 0 { return nil }
    return 42
}
func go() -> int {
    let v = find(0)
    return v ?? -1
}
"""));
    }

    [Fact]
    public void Nullable_RefTypeString()
    {
        Assert.Equal("fallback", Run("""
namespace Test
func find(id: int) -> string? {
    if id == 0 { return nil }
    return "found"
}
func go() -> string {
    return find(0) ?? "fallback"
}
""", "go"));
    }

    [Fact]
    public void Nullable_HasValueViaCoalesce()
    {
        Assert.Equal(100, Run("""
namespace Test
func maybe(present: bool) -> int? {
    if present { return 100 }
    return nil
}
func go() -> int {
    let a = maybe(true) ?? 0
    let b = maybe(false) ?? 0
    return a + b
}
"""));
    }

    // ── small string algorithms ──

    [Fact]
    public void Algo_ReverseString()
    {
        Assert.Equal("olleh", Run("""
namespace Test
func reverse(s: string) -> string {
    var result = ""
    var i = s.Length - 1
    while i >= 0 {
        result = result + s[i].ToString()
        i -= 1
    }
    return result
}
func go() -> string { return reverse("hello") }
""", "go"));
    }

    [Fact]
    public void Algo_CountChar()
    {
        Assert.Equal(3, Run("""
namespace Test
func countChar(s: string, target: char) -> int {
    var count = 0
    for c in s {
        if c == target { count += 1 }
    }
    return count
}
func go() -> int { return countChar("banana", 'a') }
"""));
    }

    [Fact]
    public void Algo_JoinWithSeparator()
    {
        Assert.Equal("a-b-c", Run("""
namespace Test
func go() -> string {
    let xs = ["a", "b", "c"]
    var result = ""
    var first = true
    for x in xs {
        if first {
            result = x
            first = false
        } else {
            result = result + "-" + x
        }
    }
    return result
}
""", "go"));
    }

    [Fact]
    public void Algo_StringBuilderAccumulate()
    {
        Assert.Equal("0123", Run("""
namespace Test
func go() -> string {
    let sb = StringBuilder()
    for i in 0..4 {
        sb.Append(i.ToString())
    }
    return sb.ToString()
}
""", "go"));
    }

    // === added: pointers / values through collections (triangulation) ===

    [Fact]
    public void Added_ListOfValueData_IndexField() => Assert.Equal(20, Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let xs = [Box { n: 10 }, Box { n: 20 }]
    return xs[1].n
}
"""));

    [Fact]
    public void Added_ListOfPointers_IndexField() => Assert.Equal(20, Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let xs = [new Box { n: 10 }, new Box { n: 20 }]
    return xs[1].n
}
"""));

    [Fact]
    public void Added_ListOfPointers_ForInSum() => Assert.Equal(60, Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let xs = [new Box { n: 10 }, new Box { n: 20 }, new Box { n: 30 }]
    var t = 0
    for b in xs { t += b.n }
    return t
}
"""));

    [Fact]
    public void Added_ListOfPointers_MutateThroughIndex() => Assert.Equal(11, Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let xs = [new Box { n: 10 }, new Box { n: 20 }]
    xs[0].n += 1
    return xs[0].n
}
"""));

    [Fact]
    public void Added_ExplicitListOfPointers_AddIndex() => Assert.Equal(7, Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    var xs = List<*Box>()
    xs.Add(new Box { n: 7 })
    return xs[0].n
}
"""));

    [Fact]
    public void Added_ListOfStrings_Concat() => Assert.Equal("a,b", Run("""
namespace Test
func go() -> string {
    let xs = ["a", "b"]
    return xs[0] + "," + xs[1]
}
"""));

    [Fact]
    public void Added_TupleOfValueData_Item() => Assert.Equal(5, Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let t = (Box { n: 2 }, Box { n: 5 })
    return t.Item2.n
}
"""));

    [Fact]
    public void Added_TupleOfPointers_ItemDeref() => Assert.Equal(5, Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let t = (new Box { n: 2 }, new Box { n: 5 })
    return t.Item2.n
}
"""));

    [Fact]
    public void Added_TupleOfPointers_Destructure() => Assert.Equal(7, Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let (a, b) = (new Box { n: 2 }, new Box { n: 5 })
    return a.n + b.n
}
"""));

    [Fact]
    public void Added_DictStringToPointer() => Assert.Equal(99, Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    var m = Dictionary<string, *Box>()
    m["k"] = new Box { n: 99 }
    return m["k"].n
}
"""));

    [Fact]
    public void Added_DictStringToInt() => Assert.Equal(3, Run("""
namespace Test
func go() -> int {
    var m = Dictionary<string, int>()
    m["a"] = 3
    return m["a"]
}
"""));

    [Fact]
    public void Added_NestedListOfLists() => Assert.Equal(2, Run("""
namespace Test
func go() -> int {
    var outer = List<List<int>>()
    var inner = List<int>()
    inner.Add(1)
    inner.Add(2)
    outer.Add(inner)
    return outer[0].Count
}
"""));

    [Fact]
    public void Added_ListOfInts_ForInSum() => Assert.Equal(15, Run("""
namespace Test
func go() -> int {
    let xs = [1, 2, 3, 4, 5]
    var t = 0
    for x in xs { t += x }
    return t
}
"""));

    [Fact]
    public void Added_ListOfDoubles_Index() => Assert.Equal(2.5, Run("""
namespace Test
func go() -> double {
    let xs = [1.5, 2.5, 3.5]
    return xs[1]
}
"""));

    [Fact]
    public void Added_ListLiteral_Count() => Assert.Equal(4, Run("""
namespace Test
func go() -> int {
    let xs = [9, 8, 7, 6]
    return xs.Count
}
"""));

    [Fact]
    public void Added_PointerListField_RoundTrip() => Assert.Equal(9, Run("""
namespace Test
struct Box { n: int }
struct Bag { items: List<*Box> }
func go() -> int {
    var bag = Bag { items: List<*Box>() }
    bag.items.Add(new Box { n: 9 })
    return bag.items[0].n
}
"""));

    [Fact]
    public void Added_GenericData_HoldsPointer() => Assert.Equal(6, Run("""
namespace Test
struct Box { n: int }
struct Cell<T> { value: T }
func go() -> int {
    let c = Cell<*Box> { value: new Box { n: 6 } }
    return c.value.n
}
"""));

    [Fact]
    public void Added_ListOfInts_LastViaIndexFromEnd() => Assert.Equal(7, Run("""
namespace Test
func go() -> int {
    let xs = [3, 5, 7]
    return xs[^1]
}
"""));

    [Fact]
    public void Added_ListOfBools_Index() => Assert.Equal(true, Run("""
namespace Test
func go() -> bool {
    let xs = [false, true]
    return xs[1]
}
"""));

    [Fact]
    public void Added_ValueDataList_ForInSum() => Assert.Equal(60, Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let xs = [Box { n: 10 }, Box { n: 20 }, Box { n: 30 }]
    var t = 0
    for b in xs { t += b.n }
    return t
}
"""));

    // ── external-interop surface the RoutePattern E# drop-in exercises ──
    // Each test isolates one capability the C# RoutePattern needs. External BCL
    // types use call-form construction, not `new`.

    [Fact]
    public void RP_Dict_ComparerCtor_IsCaseInsensitive() => Assert.Equal(1, Run("""
namespace Test
func go() -> int {
    var d = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    d["A"] = "1"
    d["a"] = "2"
    return d.Count
}
"""));

    [Fact]
    public void RP_ReadOnlyDict_CovariantAssign_AndCount() => Assert.Equal(2, Run("""
namespace Test
func go() -> int {
    var d = Dictionary<string, string>()
    d["a"] = "x"
    d["b"] = "y"
    let ro: IReadOnlyDictionary<string, string> = d
    return ro.Count
}
"""));

    [Fact]
    public void RP_StringJoin_OverList() => Assert.Equal("a/b/c", Run("""
namespace Test
func go() -> string {
    var built = List<string>()
    built.Add("a")
    built.Add("b")
    built.Add("c")
    return string.Join('/', built)
}
"""));

    [Fact]
    public void RP_TrimStart_Split_Count() => Assert.Equal(2, Run("""
namespace Test
func go() -> int {
    let segs = "/users/42".TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
    return segs.Length
}
"""));

    [Fact]
    public void RP_StringEquals_OrdinalIgnoreCase() => Assert.Equal(true, Run("""
namespace Test
func go() -> bool = string.Equals("Users", "users", StringComparison.OrdinalIgnoreCase)
"""));

    [Fact]
    public void RP_FuncParam_Invoke() => Assert.Equal("hi!", Run("""
namespace Test
func apply(f: Func<string, string>, x: string) -> string = f(x)
func go() -> string = apply((s) => s + "!", "hi")
"""));

    [Fact]
    public void RP_OutParam_TryPattern() => Assert.Equal(43, Run("""
namespace Test
func try_thing(input: int, out result: int) -> bool {
    result = input + 1
    return true
}
func go() -> int {
    var r = 0
    if try_thing(42, out r) { return r }
    return -1
}
"""));

    [Fact]
    public void RP_NilDict_ReassignThenSet() => Assert.Equal(1, Run("""
namespace Test
func go() -> int {
    var d: Dictionary<string, string> = nil
    if d == nil { d = Dictionary<string, string>() }
    d["k"] = "v"
    return d.Count
}
"""));

    // The whole RoutePattern matcher in E# as a mixed-language drop-in: a
    // `static func` static class with a PascalCase `TryMatch` (out-dict) + `Build`
    // (Func lookup) and a `static readonly EmptyParams`. Compiled, then reflect-
    // invoked exactly as the C# PageNavigator calls it.
    [Fact]
    public void RP_FullRoutePattern_TryMatchAndBuild()
    {
        var asm = EsHarness.Compile("""
namespace Test

pub static RoutePattern {
    let EmptyParams: IReadOnlyDictionary<string, string> = Dictionary<string, string>(0)

    pub func TryMatch(pattern: string, path: string, out result: IReadOnlyDictionary<string, string>) -> bool {
        let pSegs = pattern.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
        let hSegs = path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
        if pSegs.Length != hSegs.Length {
            result = EmptyParams
            return false
        }
        var captured: Dictionary<string, string> = nil
        var i = 0
        while i < pSegs.Length {
            let seg = pSegs[i]
            if seg.Length >= 2 && seg[0] == '{' && seg[seg.Length - 1] == '}' {
                if captured == nil { captured = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) }
                captured[seg.Substring(1, seg.Length - 2)] = hSegs[i]
            } else {
                if !string.Equals(seg, hSegs[i], StringComparison.OrdinalIgnoreCase) {
                    result = EmptyParams
                    return false
                }
            }
            i += 1
        }
        if captured == nil { result = EmptyParams } else { result = captured }
        return true
    }

    pub func Build(pattern: string, valueFor: Func<string, string?>) -> string? {
        let pSegs = pattern.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
        var built = List<string>()
        var i = 0
        while i < pSegs.Length {
            let seg = pSegs[i]
            if seg.Length >= 2 && seg[0] == '{' && seg[seg.Length - 1] == '}' {
                let v = valueFor(seg.Substring(1, seg.Length - 2))
                if v == nil { return nil }
                built.Add(v)
            } else {
                built.Add(seg)
            }
            i += 1
        }
        return "/" + string.Join('/', built)
    }
}
""");
        var rp = asm.GetType("Test.RoutePattern") ?? throw new Exception("Test.RoutePattern not emitted");
        var tryMatch = rp.GetMethod("TryMatch") ?? throw new Exception("TryMatch not found");
        var build = rp.GetMethod("Build") ?? throw new Exception("Build not found");

        // Param capture: pattern with a {placeholder} matches and extracts the value.
        var matchArgs = new object?[] { "/users/{id}", "/users/42", null };
        Assert.Equal(true, tryMatch.Invoke(null, matchArgs));
        var captured = (IReadOnlyDictionary<string, string>)matchArgs[2]!;
        Assert.Equal("42", captured["id"]);

        // Literal mismatch fails.
        var noMatchArgs = new object?[] { "/users/{id}", "/orders/42", null };
        Assert.Equal(false, tryMatch.Invoke(null, noMatchArgs));

        // Segment-count mismatch fails.
        var lenArgs = new object?[] { "/a/b", "/a", null };
        Assert.Equal(false, tryMatch.Invoke(null, lenArgs));

        // Build substitutes placeholders from the lookup.
        Func<string, string?> present = k => k == "id" ? "7" : null;
        Assert.Equal("/users/7", build.Invoke(null, new object?[] { "/users/{id}", present }));

        // Build returns null when a placeholder has no value.
        Func<string, string?> missing = _ => null;
        Assert.Null(build.Invoke(null, new object?[] { "/users/{id}", missing }));
    }

    // string.Join's string-separator overload over a List — exercises the same
    // overload-applicability gate as the char form (List → IEnumerable<string>,
    // not the params string[] overload).
    [Fact]
    public void RP_StringJoin_StringSeparator_OverList() => Assert.Equal("a, b, c", Run("""
namespace Test
func go() -> string {
    var xs = List<string>()
    xs.Add("a")
    xs.Add("b")
    xs.Add("c")
    return string.Join(", ", xs)
}
"""));

    // A member inherited through a *base* interface resolves: `Count` on
    // IReadOnlyList<T> is declared on IReadOnlyCollection<T>, and Type.GetProperty
    // on an interface doesn't walk bases — the emitter searches the hierarchy.
    [Fact]
    public void RP_InterfaceInheritedMember_ReadOnlyListCount() => Assert.Equal(2, Run("""
namespace Test
func go() -> int {
    var xs = List<int>()
    xs.Add(1)
    xs.Add(2)
    let ro: IReadOnlyList<int> = xs
    return ro.Count
}
"""));
}
