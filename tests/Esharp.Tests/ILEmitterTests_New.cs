using System.Reflection;
using Esharp.Diagnostics;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Mono.Cecil;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

/// `new` — the sole fresh-heap-allocation keyword (#1). `new T { ... }` /
/// `new T(args)` constructs a value `data` and places it on the heap, yielding a
/// `*T`. It is a contextual keyword (recognized only before an upper-case type name
/// + `{` / `(` / `<`) and the clean counterpart to `&` (address-of-existing):
/// `new` allocates what does not exist yet, `&` takes the address of what does.
///
/// The legacy `&T { ... }` allocation spelling still compiles but warns (ES2143).
/// `new RefData{}` is ES2003 (a class is already a reference); `new` on a
/// non-`data` type is ES2144. These tests pin the behavior, every position the
/// keyword appears in, the contextual-identifier escape hatch, and IL parity with
/// the legacy spelling.
public sealed class ILEmitterTests_New
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static int _asmCounter;

    // Compile ignoring warnings (the `&T{}` deprecation is a warning), failing on
    // any error, and load with Cecil for IL inspection.
    static (Assembly asm, AssemblyDefinition cecil) CompileWithCecil(string source)
    {
        var asmName = $"NewTest_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        var (cecil, diags) = EsHarness.EmitBound(binder, bound, asmName);
        Assert.Empty(diags.Where(d => d.Severity == DiagnosticSeverity.Error));
        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        cecil.Write(path);
        return (Assembly.LoadFrom(path), cecil);
    }

    static List<string> MethodInstructions(AssemblyDefinition cecil, string methodName)
    {
        var lines = new List<string>();
        foreach (var type in cecil.MainModule.Types)
            foreach (var m in type.Methods)
                if (m.Name == methodName && m.HasBody)
                    foreach (var inst in m.Body.Instructions)
                        lines.Add(inst.ToString());
        return lines;
    }

    // ── Composite-form construction + dereference ───────────────────────────

    [Fact]
    public void New_Composite_ReadsFirstField()
        => Assert.Equal(3, EsHarness.Run("""
namespace Test
struct Point { x: int, y: int }
func go() -> int {
    let p = new Point { x: 3, y: 4 }
    return p.x
}
""", "go"));

    [Fact]
    public void New_Composite_ReadsSecondField()
        => Assert.Equal(4, EsHarness.Run("""
namespace Test
struct Point { x: int, y: int }
func go() -> int {
    let p = new Point { x: 3, y: 4 }
    return p.y
}
""", "go"));

    [Fact]
    public void New_Composite_SumsFields()
        => Assert.Equal(7, EsHarness.Run("""
namespace Test
struct Point { x: int, y: int }
func go() -> int {
    let p = new Point { x: 3, y: 4 }
    return p.x + p.y
}
""", "go"));

    [Fact]
    public void New_Composite_MutatesThroughPointer()
        => Assert.Equal(6, EsHarness.Run("""
namespace Test
struct Counter { value: int }
func go() -> int {
    var c = new Counter { value: 1 }
    c.value += 5
    return c.value
}
""", "go"));

    [Fact]
    public void New_Composite_NewlineSeparatedFields()
        => Assert.Equal(30, EsHarness.Run("""
namespace Test
struct Point { x: int, y: int }
func go() -> int {
    let p = new Point {
        x: 10
        y: 20
    }
    return p.x + p.y
}
""", "go"));

    [Fact]
    public void New_Composite_StringField()
        => Assert.Equal("hi", EsHarness.Run("""
namespace Test
struct Label { text: string }
func go() -> string {
    let l = new Label { text: "hi" }
    return l.text
}
""", "go"));

    [Fact]
    public void New_Composite_BoolField()
        => Assert.Equal(true, EsHarness.Run("""
namespace Test
struct Flag { on: bool }
func go() -> bool {
    let f = new Flag { on: true }
    return f.on
}
""", "go"));

    [Fact]
    public void New_Composite_DoubleField()
        => Assert.Equal(2.5, EsHarness.Run("""
namespace Test
struct Num { v: double }
func go() -> double {
    let n = new Num { v: 2.5 }
    return n.v
}
""", "go"));

    [Fact]
    public void New_Var_Reassign()
        => Assert.Equal(2, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    var b = new Box { n: 1 }
    b = new Box { n: 2 }
    return b.n
}
""", "go"));

    [Fact]
    public void New_ExplicitPointerAnnotation()
        => Assert.Equal(9, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    var b: *Box = new Box { n: 9 }
    return b.n
}
""", "go"));

    [Fact]
    public void New_TwoInstances_IndependentIdentity()
        => Assert.Equal(3, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    var a = new Box { n: 1 }
    var b = new Box { n: 2 }
    a.n += 0
    return a.n + b.n
}
""", "go"));

    [Fact]
    public void New_NotNil()
        => Assert.Equal(true, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> bool {
    let b = new Box { n: 1 }
    return b != nil
}
""", "go"));

    // ── Positional-form construction ────────────────────────────────────────

    [Fact]
    public void New_Positional_TwoArgs()
        => Assert.Equal(7, EsHarness.Run("""
namespace Test
struct Vec2(x: int, y: int)
func go() -> int {
    let v = new Vec2(3, 4)
    return v.x + v.y
}
""", "go"));

    [Fact]
    public void New_Positional_OneArg()
        => Assert.Equal(42, EsHarness.Run("""
namespace Test
struct Wrap(v: int)
func go() -> int {
    let w = new Wrap(42)
    return w.v
}
""", "go"));

    [Fact]
    public void New_Positional_ExprArgs()
        => Assert.Equal(13, EsHarness.Run("""
namespace Test
struct Vec2(x: int, y: int)
func go() -> int {
    let a = 5
    let v = new Vec2(a + 1, a + 2)
    return v.x + v.y
}
""", "go"));

    [Fact]
    public void New_Positional_Mutation()
        => Assert.Equal(15, EsHarness.Run("""
namespace Test
struct Vec2(x: int, y: int)
func go() -> int {
    var v = new Vec2(3, 4)
    v.x = 11
    return v.x + v.y
}
""", "go"));

    [Fact]
    public void New_Positional_AndComposite_SameType()
        => Assert.Equal(20, EsHarness.Run("""
namespace Test
struct Vec2(x: int, y: int)
func go() -> int {
    let a = new Vec2(3, 4)
    let b = new Vec2 { x: 6, y: 7 }
    return a.x + a.y + b.x + b.y
}
""", "go"));

    // ── Generic construction ────────────────────────────────────────────────

    [Fact]
    public void New_Generic_Pair_First()
        => Assert.Equal(42, EsHarness.Run("""
namespace Test
struct Pair<A, B> { first: A, second: B }
func go() -> int {
    let p = new Pair<int, string> { first: 42, second: "x" }
    return p.first
}
""", "go"));

    [Fact]
    public void New_Generic_Pair_Second()
        => Assert.Equal("hello", EsHarness.Run("""
namespace Test
struct Pair<A, B> { first: A, second: B }
func go() -> string {
    let p = new Pair<int, string> { first: 1, second: "hello" }
    return p.second
}
""", "go"));

    [Fact]
    public void New_Generic_SameTypeArgs()
        => Assert.Equal(5, EsHarness.Run("""
namespace Test
struct Pair<A, B> { first: A, second: B }
func go() -> int {
    let p = new Pair<int, int> { first: 2, second: 3 }
    return p.first + p.second
}
""", "go"));

    // ── Recursive / linked-list construction ────────────────────────────────

    [Fact]
    public void New_LinkedList_SumsThree()
        => Assert.Equal(6, EsHarness.Run("""
namespace Test
struct Node { value: int, next: *Node }
func go() -> int {
    var n3: *Node = new Node { value: 3, next: nil }
    var n2: *Node = new Node { value: 2, next: n3 }
    var n1: *Node = new Node { value: 1, next: n2 }
    var total = 0
    var cur = n1
    while cur != nil {
        total += cur.value
        cur = cur.next
    }
    return total
}
""", "go"));

    [Fact]
    public void New_LinkedList_PrependReturnsPointer()
        => Assert.Equal(3, EsHarness.Run("""
namespace Test
struct Node { value: int, next: *Node }
func prepend(head: *Node, value: int) -> *Node {
    return new Node { value: value, next: head }
}
func go() -> int {
    var head: *Node = nil
    head = prepend(head, 10)
    head = prepend(head, 20)
    head = prepend(head, 30)
    var count = 0
    var cur = head
    while cur != nil {
        count += 1
        cur = cur.next
    }
    return count
}
""", "go"));

    [Fact]
    public void New_LinkedList_NilTailTerminates()
        => Assert.Equal(10, EsHarness.Run("""
namespace Test
struct Node { value: int, next: *Node }
func go() -> int {
    let only = new Node { value: 10, next: nil }
    return only.value
}
""", "go"));

    [Fact]
    public void New_BuildListInLoop()
        => Assert.Equal(10, EsHarness.Run("""
namespace Test
struct Node { value: int, next: *Node }
func go() -> int {
    var head: *Node = nil
    var i = 1
    while i <= 4 {
        head = new Node { value: i, next: head }
        i += 1
    }
    var sum = 0
    var cur = head
    while cur != nil {
        sum += cur.value
        cur = cur.next
    }
    return sum
}
""", "go"));

    // ── Position coverage ───────────────────────────────────────────────────

    [Fact]
    public void New_AsExpressionBodiedReturn()
        => Assert.Equal(3, EsHarness.Run("""
namespace Test
struct Point { x: int, y: int }
func make() -> *Point = new Point { x: 1, y: 2 }
func go() -> int {
    let p = make()
    return p.x + p.y
}
""", "go"));

    [Fact]
    public void New_AsFunctionArgument()
        => Assert.Equal(8, EsHarness.Run("""
namespace Test
struct Box { n: int }
func read(b: *Box) -> int = b.n
func go() -> int {
    return read(new Box { n: 8 })
}
""", "go"));

    [Fact]
    public void New_NestedInFieldInitializer()
        => Assert.Equal(30, EsHarness.Run("""
namespace Test
struct Inner { n: int }
struct Outer { inner: *Inner, label: string }
func go() -> int {
    let o = new Outer { inner: new Inner { n: 30 }, label: "x" }
    return o.inner.n
}
""", "go"));

    [Fact]
    public void New_InListLiteral_ThenIndex()
        => Assert.Equal(20, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let xs = [new Box { n: 10 }, new Box { n: 20 }]
    return xs[1].n
}
""", "go"));

    [Fact]
    public void New_InListLiteral_IndexThenMutate()
        => Assert.Equal(11, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let xs = [new Box { n: 10 }, new Box { n: 20 }]
    xs[0].n += 1
    return xs[0].n
}
""", "go"));

    [Fact]
    public void New_InTernaryBranch()
        => Assert.Equal(2, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let cond = false
    let b = cond ? new Box { n: 1 } : new Box { n: 2 }
    return b.n
}
""", "go"));

    [Fact]
    public void New_InIfBody()
        => Assert.Equal(5, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    var b = new Box { n: 0 }
    if true {
        b = new Box { n: 5 }
    }
    return b.n
}
""", "go"));

    [Fact]
    public void New_InMatchArm()
        => Assert.Equal(7, EsHarness.Run("""
namespace Test
struct Box { n: int }
union Sel { a, b }
func go() -> int {
    let s = Sel.a()
    var box = new Box { n: 0 }
    match s {
        .a { box = new Box { n: 7 } }
        .b { box = new Box { n: 9 } }
    }
    return box.n
}
""", "go"));

    [Fact]
    public void New_AsConditionArgument()
        => Assert.Equal(true, EsHarness.Run("""
namespace Test
struct Box { n: int }
func positive(b: *Box) -> bool = b.n > 0
func go() -> bool {
    if positive(new Box { n: 3 }) {
        return true
    }
    return false
}
""", "go"));

    // ── Auto-deref: promoted methods + embedding through `new` ───────────────

    [Fact]
    public void New_CallsPointerReceiverMethod()
        => Assert.Equal(43, EsHarness.Run("""
namespace Test
struct Counter { value: int }
func bump(c: *Counter) { c.value += 1 }
func go() -> int {
    var c = new Counter { value: 42 }
    bump(c)
    return c.value
}
""", "go"));

    [Fact]
    public void New_PointerEmbedding_PromotedAccess()
        => Assert.Equal(15, EsHarness.Run("""
namespace Test
struct Vec2 { var x: int, var y: int }
struct Entity { *Vec2, name: string }
func go() -> int {
    var e = new Entity { Vec2: new Vec2 { x: 10, y: 5 }, name: "p" }
    e.x += 0
    return e.x + e.y
}
""", "go"));

    [Fact]
    public void New_PointerSatisfiesInterface()
        => Assert.Equal(42, EsHarness.Run("""
namespace Test
struct Inner { var n: int }
func (i: *Inner) bump() { i.n += 1 }
func (i: Inner) value() -> int = i.n
struct Outer : IBumper { *Inner, label: string }
interface IBumper { func bump(), func value() -> int }
func twice(b: IBumper) -> int {
    b.bump()
    b.bump()
    return b.value()
}
func go() -> int {
    var o: *Outer = new Outer { Inner: new Inner { n: 40 }, label: "x" }
    return twice(o)
}
""", "go"));

    // ── `new` as an ordinary identifier (contextual keyword) ─────────────────

    [Fact]
    public void Ident_New_AsLocalVariable()
        => Assert.Equal(5, EsHarness.Run("""
namespace Test
func go() -> int {
    let new = 5
    return new
}
""", "go"));

    [Fact]
    public void Ident_New_AsMutableVariable()
        => Assert.Equal(3, EsHarness.Run("""
namespace Test
func go() -> int {
    var new = 1
    new += 2
    return new
}
""", "go"));

    [Fact]
    public void Ident_New_AsFunctionName()
        => Assert.Equal(7, EsHarness.Run("""
namespace Test
func new() -> int = 7
func go() -> int {
    return new()
}
""", "go"));

    [Fact]
    public void Ident_New_AsFunctionParameter()
        => Assert.Equal(8, EsHarness.Run("""
namespace Test
func inc(new: int) -> int = new + 1
func go() -> int {
    return inc(7)
}
""", "go"));

    [Fact]
    public void Ident_New_AsFieldName()
        => Assert.Equal(11, EsHarness.Run("""
namespace Test
struct Holder { new: int }
func go() -> int {
    let h = Holder { new: 11 }
    return h.new
}
""", "go"));

    [Fact]
    public void Ident_New_FollowedByComparison_NotTriggered()
        => Assert.Equal(1, EsHarness.Run("""
namespace Test
func go() -> int {
    let new = 5
    return new < 10 ? 1 : 0
}
""", "go"));

    [Fact]
    public void Ident_New_LowercaseTypeNotTriggered()
        => Assert.Equal(4, EsHarness.Run("""
namespace Test
func go() -> int {
    var new = 1
    let other = 3
    return new + other
}
""", "go"));

    // ── Negative cases ──────────────────────────────────────────────────────

    [Fact]
    public void Neg_NewRefData_IsES2003()
    {
        var errs = EsHarness.Diagnostics("""
namespace Test
class R {
    x: int
    init(x: int) { self.x = x }
}
func go() -> int {
    let r = new R { x: 1 }
    return r.x
}
""");
        Assert.Contains(errs, d => d.Message.Contains("ES2003"));
    }

    [Fact]
    public void Neg_NewExternalGeneric_IsES2144()
    {
        var errs = EsHarness.Diagnostics("""
namespace Test
func go() -> int {
    let xs = new List<int>()
    return 0
}
""");
        Assert.Contains(errs, d => d.Message.Contains("ES2144"));
    }

    [Fact]
    public void Neg_NewExternalType_IsES2144()
    {
        var errs = EsHarness.Diagnostics("""
namespace Test
func go() -> int {
    let s = new StringBuilder { }
    return 0
}
""");
        Assert.Contains(errs, d => d.Message.Contains("ES2144"));
    }

    [Fact]
    public void Neg_NewRefData_NotHeapAlloc_NoCascade()
    {
        // ES2003 is the only error — the binder doesn't cascade into spurious
        // type errors after rejecting `new` on a class.
        var errs = EsHarness.Diagnostics("""
namespace Test
class R {
    x: int
    init(x: int) { self.x = x }
}
func go() -> int {
    let r = new R { x: 1 }
    return r.x
}
""");
        Assert.Single(errs.Where(d => d.Message.Contains("ES2003")));
    }

    // ── Legacy `&T{}` deprecation (ES2143) ──────────────────────────────────

    [Fact]
    public void Deprecated_Amp_StillCompilesAndRuns()
        => Assert.Equal(7, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let b = &Box { n: 7 }
    return b.n
}
""", "go"));

    [Fact]
    public void Deprecated_Amp_EmitsES2143Warning()
    {
        var diags = EsHarness.AllDiagnostics("""
namespace Test
struct Box { n: int }
func go() -> int {
    let b = &Box { n: 7 }
    return b.n
}
""");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("ES2143"));
    }

    [Fact]
    public void Deprecated_Amp_IsNotAnError()
    {
        // The deprecation is a warning, not an error — `&T{}` keeps compiling.
        var errs = EsHarness.Diagnostics("""
namespace Test
struct Box { n: int }
func go() -> int {
    let b = &Box { n: 7 }
    return b.n
}
""");
        Assert.Empty(errs);
    }

    [Fact]
    public void New_EmitsNoDeprecationWarning()
    {
        var diags = EsHarness.AllDiagnostics("""
namespace Test
struct Box { n: int }
func go() -> int {
    let b = new Box { n: 7 }
    return b.n
}
""");
        Assert.DoesNotContain(diags, d => d.Message.Contains("ES2143"));
    }

    [Fact]
    public void Equivalence_NewAndAmp_SameResult()
    {
        const string template = """
namespace Test
struct Node { value: int, next: *Node }
func go() -> int {
    var n2: *Node = SPELL Node { value: 2, next: nil }
    var n1: *Node = SPELL Node { value: 1, next: n2 }
    return n1.value + n1.next.value
}
""";
        var withNew = EsHarness.Run(template.Replace("SPELL", "new"), "go");
        var withAmp = EsHarness.Run(template.Replace("SPELL", "&"), "go");
        Assert.Equal(3, withNew);
        Assert.Equal(withNew, withAmp);
    }

    // ── IL parity with the legacy spelling ──────────────────────────────────

    [Fact]
    public void IL_NewAndAmp_EmitIdenticalBody()
    {
        const string template = """
namespace Test
struct Node { value: int, next: *Node }
func go() -> int {
    let n = SPELL Node { value: 7, next: nil }
    return n.value
}
""";
        var (_, newCecil) = CompileWithCecil(template.Replace("SPELL", "new"));
        var (_, ampCecil) = CompileWithCecil(template.Replace("SPELL", "&"));
        var newBody = MethodInstructions(newCecil, "go");
        var ampBody = MethodInstructions(ampCecil, "go");
        Assert.NotEmpty(newBody);
        Assert.Equal(ampBody, newBody);
        newCecil.Dispose();
        ampCecil.Dispose();
    }

    [Fact]
    public void IL_New_EmitsHeapAllocation()
    {
        var (_, cecil) = CompileWithCecil("""
namespace Test
struct Node { value: int, next: *Node }
func go() -> int {
    let n = new Node { value: 7, next: nil }
    return n.value
}
""");
        var body = MethodInstructions(cecil, "go");
        // The heap alloc constructs the `__Ptr_Node` wrapper via `newobj`.
        Assert.Contains(body, i => i.Contains("newobj") && i.Contains("Node"));
        cecil.Dispose();
    }

    [Fact]
    public void IL_New_LocalIsHeapPointerWrapper()
    {
        var (_, cecil) = CompileWithCecil("""
namespace Test
struct Node { value: int, next: *Node }
func go() -> int {
    var n: *Node = new Node { value: 7, next: nil }
    return n.value
}
""");
        var hasWrapperLocal = false;
        foreach (var type in cecil.MainModule.Types)
            foreach (var m in type.Methods)
                if (m.Name == "go" && m.HasBody)
                    foreach (var v in m.Body.Variables)
                        if (v.VariableType.FullName.Contains("Node"))
                            hasWrapperLocal = true;
        Assert.True(hasWrapperLocal);
        cecil.Dispose();
    }

    // === added: more positions + interactions ===

    [Fact]
    public void New_ChainedListBuild_LengthFive() => Assert.Equal(5, EsHarness.Run("""
namespace Test
struct Node { value: int, next: *Node }
func go() -> int {
    var head: *Node = nil
    var i = 0
    while i < 5 {
        head = new Node { value: i, next: head }
        i += 1
    }
    var c = 0
    var cur = head
    while cur != nil { c += 1 cur = cur.next }
    return c
}
""", "go"));

    [Fact]
    public void New_ReturnedFromBranch() => Assert.Equal(9, EsHarness.Run("""
namespace Test
struct Box { n: int }
func make(big: bool) -> *Box {
    if big { return new Box { n: 9 } }
    return new Box { n: 1 }
}
func go() -> int { let b = make(true) return b.n }
""", "go"));

    // Deferred (#5): inferring `T = *Box` from a `new Box{}` argument to a user
    // generic function. Explicit value-type args work (see ILEmitterTests2).
    [Fact(Skip = "generic-argument inference for user functions is item #5, deferred")]
    public void New_PassedToGenericFunction() => Assert.Equal(13, EsHarness.Run("""
namespace Test
struct Box { n: int }
func identity<T>(v: T) -> T = v
func go() -> int { let b = identity(new Box { n: 13 }) return b.n }
""", "go"));

    [Fact]
    public void New_NestedThreeLevels() => Assert.Equal(7, EsHarness.Run("""
namespace Test
struct A { n: int }
struct B { a: *A }
struct C { b: *B }
func go() -> int {
    let c = new C { b: new B { a: new A { n: 7 } } }
    return c.b.a.n
}
""", "go"));

    [Fact]
    public void New_InWhileConditionArgument() => Assert.Equal(3, EsHarness.Run("""
namespace Test
struct Box { n: int }
func alive(b: *Box) -> bool = b.n > 0
func go() -> int {
    var count = 0
    var n = 3
    while alive(new Box { n: n }) {
        count += 1
        n -= 1
    }
    return count
}
""", "go"));

    [Fact]
    public void New_AssignedToFieldThenRead() => Assert.Equal(42, EsHarness.Run("""
namespace Test
struct Inner { v: int }
struct Holder { var p: *Inner }
func go() -> int {
    var h = Holder { p: nil }
    h.p = new Inner { v: 42 }
    return h.p.v
}
""", "go"));

    [Fact]
    public void New_PositionalGenericExternal_IsError_List()
    {
        var errs = EsHarness.Diagnostics("""
namespace Test
func go() -> int {
    let d = new Dictionary<string, int>()
    return 0
}
""");
        Assert.Contains(errs, d => d.Message.Contains("ES2144"));
    }

    [Fact]
    public void New_ReadonlyData() => Assert.Equal(5, EsHarness.Run("""
namespace Test
readonly struct Point { x: int, y: int }
func go() -> int {
    let p = new Point { x: 2, y: 3 }
    return p.x + p.y
}
""", "go"));

    [Fact]
    public void New_DataWithDefaultField() => Assert.Equal(8, EsHarness.Run("""
namespace Test
struct Box { n: int }
func twice(b: *Box) -> int = b.n + b.n
func go() -> int { return twice(new Box { n: 4 }) }
""", "go"));

    [Fact]
    public void New_GenericNestedTypeArg() => Assert.Equal(3, EsHarness.Run("""
namespace Test
struct Pair<A, B> { first: A, second: B }
func go() -> int {
    let p = new Pair<int, int> { first: 1, second: 2 }
    return p.first + p.second
}
""", "go"));

    [Fact]
    public void New_MultipleInOneExpression() => Assert.Equal(3, EsHarness.Run("""
namespace Test
struct Box { n: int }
func add(a: *Box, b: *Box) -> int = a.n + b.n
func go() -> int { return add(new Box { n: 1 }, new Box { n: 2 }) }
""", "go"));

    [Fact]
    public void New_PointerComparedEqual() => Assert.Equal(true, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> bool {
    let a = new Box { n: 1 }
    let b = a
    return a == b
}
""", "go"));

    [Fact]
    public void New_TwoPointersNotEqual() => Assert.Equal(false, EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> bool {
    let a = new Box { n: 1 }
    let b = new Box { n: 1 }
    return a == b
}
""", "go"));

    [Fact]
    public void New_InStaticFuncBody() => Assert.Equal(6, EsHarness.Run("""
namespace Test
struct Box { n: int }
static Maker {
    func build(v: int) -> *Box = new Box { n: v }
}
func go() -> int {
    let b = Maker.build(6)
    return b.n
}
""", "go"));

    [Fact]
    public void New_DeepLinkedListSum() => Assert.Equal(55, EsHarness.Run("""
namespace Test
struct Node { value: int, next: *Node }
func go() -> int {
    var head: *Node = nil
    var i = 1
    while i <= 10 {
        head = new Node { value: i, next: head }
        i += 1
    }
    var sum = 0
    var cur = head
    while cur != nil { sum += cur.value cur = cur.next }
    return sum
}
""", "go"));

    [Fact]
    public void Ident_New_AsLetThenArithmetic() => Assert.Equal(25, EsHarness.Run("""
namespace Test
func go() -> int {
    let new = 5
    return new * new
}
""", "go"));

    [Fact]
    public void Ident_New_AsLoopVariableName() => Assert.Equal(10, EsHarness.Run("""
namespace Test
func go() -> int {
    var new = 0
    for i in 0..5 { new += i }
    return new
}
""", "go"));

    [Fact]
    public void New_FollowedByMethodCallViaLocal() => Assert.Equal(50, EsHarness.Run("""
namespace Test
struct Box { n: int }
func scaled(b: *Box) -> int = b.n * 10
func go() -> int {
    let b = new Box { n: 5 }
    return scaled(b)
}
""", "go"));

    [Fact]
    public void New_StringInterpolationViaLocal() => Assert.Equal("n=5", EsHarness.Run("""
namespace Test
struct Box { n: int }
func go() -> string {
    let b = new Box { n: 5 }
    return "n={b.n}"
}
""", "go"));

    [Fact]
    public void New_MutateLinkedListNode() => Assert.Equal(99, EsHarness.Run("""
namespace Test
struct Node { var value: int, next: *Node }
func go() -> int {
    var head = new Node { value: 1, next: nil }
    head.value = 99
    return head.value
}
""", "go"));
}
