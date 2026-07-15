using System.Reflection;

namespace Esharp.Tests;

/// Stdlib-readiness triage. Every capability the E#-authored stdlib needs before the
/// rewrite (Result / Chan / TaskScope / ChanSelect) gets a direct test here, grouped
/// by the stdlib type that exercises it. The point is to SEE — green means the
/// foundation supports that shape; red is a precise triage target with a known owner.
///
/// Where a runnable consumer is cheap, the test runs the code. Where the consumer is
/// heavy (BCL streaming interfaces, async dispose), the test compiles (ILVerify gates
/// every emit via verify:true) and asserts the emitted metadata — a passing compile
/// means verifiable IL that carries the right interface/return shape.
public sealed class ILEmitterTests_StdlibReadiness
{
    static object? Run(string source, string method, params object?[] args) =>
        EsHarness.Run(source, method, args);

    static Assembly Compile(string source) => EsHarness.Compile(source);
    static Type T(Assembly asm, string name) => asm.GetType(name) ?? throw new Exception($"type {name} not found: [{string.Join(", ", asm.GetTypes().Select(x => x.FullName))}]");
    static bool HasIface(Type t, string ifaceSimpleName) => t.GetInterfaces().Any(i => i.Name == ifaceSimpleName);

    // ============================================================================
    // GROUP A — Result<T,E> shape: generic value `data`, derive, combinators, key-use
    // ============================================================================

    // A1 — generic value data, two type params, construct + read both fields.
    [Fact]
    public void A01_GenericValueData_TwoParams() => Assert.Equal(7, Run("""
namespace Test
struct Res<T, E> { ok: bool, v: T, e: E }
func go() -> int {
    let r = Res<int, string> { ok: true, v: 7, e: "" }
    return r.v
}
""", "go"));

    // A2 — a method on a generic value data that returns its own T (Map/Unwrap shape).
    // Instance-method promotion forces dot-call (ES2142), so call as `b.unwrap()`.
    [Fact]
    public void A02_GenericValueData_MethodReturnsT() => Assert.Equal(5, Run("""
namespace Test
struct Box<T> { item: T }
func (b: Box<T>) unwrap<T>() -> T = b.item
func go() -> int {
    let b = Box<int> { item: 5 }
    return b.unwrap()
}
""", "go"));

    // A3 — derive equality on a two-param generic data: Equals true / false.
    [Fact]
    public void A03_DeriveEquality_TwoParam_Equal() => Assert.Equal(true, Run("""
namespace Test
derive equality
struct Res<T, E> { ok: bool, v: T, e: E }
func go() -> bool {
    let a = Res<int, string> { ok: true, v: 7, e: "" }
    let b = Res<int, string> { ok: true, v: 7, e: "" }
    return a.Equals(b)
}
""", "go"));

    // A4 — derive equality emits a typed IEquatable<Self> on the closed instance.
    [Fact]
    public void A04_DeriveEquality_EmitsIEquatable()
    {
        var asm = Compile("""
namespace Test
derive equality
struct Res<T, E> { ok: bool, v: T, e: E }
func make() -> Res<int, string> = Res<int, string> { ok: true, v: 1, e: "" }
""");
        var t = T(asm, "Test.Res`2");
        Assert.True(HasIface(t, "IEquatable`1"), "Res<T,E> should implement IEquatable<Res<T,E>>");
    }

    // A5 — a derive-equality value data is usable as a Dictionary key (structural eq +
    // hash, no reference-identity surprise). The acceptance test from the plan.
    [Fact]
    public void A05_DeriveEquality_UsableAsDictKey() => Assert.Equal(99, Run("""
namespace Test
using "System.Collections.Generic"
derive equality
struct Key { a: int, b: int }
func go() -> int {
    var m = Dictionary<Key, int>()
    m[Key { a: 1, b: 2 }] = 99
    return m[Key { a: 1, b: 2 }]
}
""", "go"));

    // A6 — derive debug on a generic data → ToString.
    [Fact]
    public void A06_DeriveDebug_Generic() => Assert.Equal("Box { item = 5 }", Run("""
namespace Test
derive debug
struct Box<T> { item: T }
func go() -> string {
    let b = Box<int> { item: 5 }
    return b.ToString()
}
""", "go"));

    // A7 — generic value data conforming to a generic E# interface, method takes T.
    [Fact]
    public void A07_ValueData_GenericInterface_ParamT() => Assert.Equal(true, Run("""
namespace Test
interface IEq<T> { func eq(other: T) -> bool }
struct Num<T> : IEq<T> { v: int }
func (n: Num<T>) eq<T>(other: T) -> bool = true
func use(x: IEq<int>) -> bool = x.eq(0)
func go() -> bool {
    let n = Num<int> { v: 3 }
    return use(n)
}
""", "go"));

    // A8 — choice generic, payload typed at the substituted argument (Option/Result core).
    [Fact]
    public void A08_GenericChoice_PayloadTyped() => Assert.Equal(42, Run("""
namespace Test
union Opt<T> { some(value: T), none }
func go() -> int {
    let o = Opt<int>.some(42)
    match o {
        .some(v) { return v }
        .none { return 0 }
    }
    return -1
}
""", "go"));

    // ============================================================================
    // GROUP B — Chan<T> shape: generic class + BCL/E# interface conformance
    // ============================================================================

    // B1 — generic class : non-generic BCL interface (IDisposable). The conformance floor.
    [Fact]
    public void B01_GenericRefData_IDisposable()
    {
        var asm = Compile("""
namespace Test
using "System"
class Res<T> : IDisposable {
    v: T
    init(x: T) { self.v = x }
    func Dispose() { }
}
func make() -> Res<int> = Res<int>(1)
""");
        Assert.True(HasIface(T(asm, "Test.Res`1"), "IDisposable"));
        var r = EsHarness.Invoke(asm, "make")!;
        ((IDisposable)r).Dispose();  // dispatches through the interface slot
    }

    // B2 — generic class wrapping a generic BCL collection field (List<T>), with an
    // element-typed accessor and mutator. Phase 2b: List<T> element must be !0, not object.
    [Fact]
    public void B02_GenericRefData_WrapsListT() => Assert.Equal(5, Run("""
namespace Test
using "System.Collections.Generic"
class Wrap<T> {
    items: List<T>
    init() { self.items = List<T>() }
    func add(x: T) { self.items.Add(x) }
    func first() -> T = self.items[0]
}
func go() -> int {
    let w = Wrap<int>()
    w.add(5)
    return w.first()
}
""", "go"));

    // B3 — generic class wrapping System.Threading.Channels.Channel<T> (the real Chan
    // backing field), constructed via a generic BCL static method.
    [Fact]
    public void B03_GenericRefData_WrapsChannelT()
    {
        var asm = Compile("""
namespace Test
using "System.Threading.Channels"
class Ch<T> {
    inner: Channel<T>
    init() { self.inner = Channel.CreateUnbounded<T>() }
    func writer() -> ChannelWriter<T> = self.inner.Writer
    func reader() -> ChannelReader<T> = self.inner.Reader
}
func make() -> Ch<int> = Ch<int>()
""");
        EsHarness.Invoke(asm, "make");  // construct → exercises Channel.CreateUnbounded<int>()
    }

    // B4 — THE CHAN SHAPE: generic class : IEnumerable<T>, IEnumerable, where
    // GetEnumerator delegates to a wrapped source and returns the *interface* IEnumerator<T>
    // (no value-struct return). Non-generic bridge via explicit interface member.
    [Fact]
    public void B04_GenericRefData_IEnumerable_InterfaceEnumeratorReturn()
    {
        var asm = Compile("""
namespace Test
using "System.Collections"
using "System.Collections.Generic"
class Seq<T> : IEnumerable<T>, IEnumerable {
    src: IEnumerable<T>
    init(s: IEnumerable<T>) { self.src = s }
    func GetEnumerator() -> IEnumerator<T> = self.src.GetEnumerator()
    func IEnumerable.GetEnumerator() -> IEnumerator = self.src.GetEnumerator()
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
        foreach (var x in (IEnumerable<int>)seq) sum += x;
        Assert.Equal(7, sum);
    }

    // B5 — the HARDER value-enumerator shape: GetEnumerator returns List<T>.Enumerator (a
    // nested generic VALUE struct) where IEnumerator<T> is declared → needs a box. This is
    // the shape `Chan` does NOT use; isolates the binder nested-value-type typing gap.
    [Fact]
    public void B05_GenericRefData_IEnumerable_ValueEnumeratorReturn()
    {
        var asm = Compile("""
namespace Test
using "System.Collections"
using "System.Collections.Generic"
class Seq<T> : IEnumerable<T>, IEnumerable {
    items: List<T>
    init(xs: List<T>) { self.items = xs }
    func GetEnumerator() -> IEnumerator<T> = self.items.GetEnumerator()
    func IEnumerable.GetEnumerator() -> IEnumerator = self.items.GetEnumerator()
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
        foreach (var x in (IEnumerable<int>)seq) sum += x;
        Assert.Equal(7, sum);
    }

    // B6 — generic class : IAsyncEnumerable<T> (await-foreach surface of Chan), delegating
    // GetAsyncEnumerator to a wrapped IAsyncEnumerable<T> source.
    [Fact]
    public void B06_GenericRefData_IAsyncEnumerable()
    {
        var asm = Compile("""
namespace Test
using "System.Collections.Generic"
using "System.Threading"
class AStream<T> : IAsyncEnumerable<T> {
    src: IAsyncEnumerable<T>
    init(s: IAsyncEnumerable<T>) { self.src = s }
    func GetAsyncEnumerator(ct: CancellationToken) -> IAsyncEnumerator<T> = self.src.GetAsyncEnumerator(ct)
}
""");
        Assert.True(HasIface(T(asm, "Test.AStream`1"), "IAsyncEnumerable`1"));
    }

    // B7 — generic class : IAsyncDisposable (Chan.Complete-on-dispose), async DisposeAsync
    // returning ValueTask. Conformance + async-return-shape on an interface slot.
    [Fact]
    public void B07_GenericRefData_IAsyncDisposable()
    {
        var asm = Compile("""
namespace Test
using "System"
using "System.Threading.Tasks"
class Res<T> : IAsyncDisposable {
    v: T
    init(x: T) { self.v = x }
    func DisposeAsync() -> ValueTask {
        await Task.CompletedTask
    }
}
func make() -> Res<int> = Res<int>(1)
""");
        Assert.True(HasIface(T(asm, "Test.Res`1"), "IAsyncDisposable"));
        var r = EsHarness.Invoke(asm, "make")!;
        ((IAsyncDisposable)r).DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // B8 — generic class : TWO E# generic interfaces over its own T (ISender<T>/IReceiver<T>
    // split capability — the genuinely-E#-specific surface Chan adds).
    [Fact]
    public void B08_GenericRefData_TwoEsharpGenericInterfaces() => Assert.Equal(11, Run("""
namespace Test
interface ISender<T> { func send(x: T) }
interface IReceiver<T> { func recv() -> T }
class Pipe<T> : ISender<T>, IReceiver<T> {
    var slot: T
    init(seed: T) { self.slot = seed }
    func send(x: T) { self.slot = x }
    func recv() -> T = self.slot
}
func go() -> int {
    let p = Pipe<int>(0)
    let s: ISender<int> = p
    s.send(11)
    let r: IReceiver<int> = p
    return r.recv()
}
""", "go"));

    // B9 — async method on a generic class returning Task<T> (SendAsync/ReceiveAsync shape).
    [Fact]
    public void B09_GenericRefData_AsyncMethodReturnsTaskOfT()
    {
        var asm = Compile("""
namespace Test
using "System.Threading.Tasks"
class Ch<T> {
    v: T
    init(x: T) { self.v = x }
    func receiveAsync() -> Task<T> {
        await Task.Delay(1)
        return self.v
    }
}
func make() -> Ch<int> = Ch<int>(8)
""");
        var ch = EsHarness.Invoke(asm, "make")!;
        var m = ch.GetType().GetMethod("receiveAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal("Task`1", m.ReturnType.Name);
        Assert.Equal(8, EsHarness.Await(m.Invoke(ch, null)));
    }

    // B10 — generic async state machine end-to-end:
    // the SM struct carries its own copy of T, so `this`/result of type T survive the
    // await. Read the result via Task.Result (no E# await) to isolate receiveAsync's own
    // state machine. This is Chan's SendAsync/ReceiveAsync capability.
    [Fact]
    public void B10_GenericRefData_AsyncStateMachine_EndToEnd()
    {
        var asm = Compile("""
namespace Test
using "System.Threading.Tasks"
class Ch<T> {
    v: T
    init(x: T) { self.v = x }
    func receiveAsync() -> Task<T> {
        await Task.Delay(1)
        return self.v
    }
}
func make() -> Ch<int> = Ch<int>(8)
""");
        var ch = EsHarness.Invoke(asm, "make")!;
        var task = (Task)ch.GetType()
            .GetMethod("receiveAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(ch, null)!;
        task.GetAwaiter().GetResult();
        var result = task.GetType().GetProperty("Result")!.GetValue(task);
        Assert.Equal(8, result);
    }

    // ============================================================================
    // GROUP C — generic mechanics the stdlib leans on
    // ============================================================================

    // C1 — a generic method (own type param) declared on a generic class.
    [Fact]
    public void C01_GenericMethod_OnGenericType() => Assert.Equal(3, Run("""
namespace Test
class Box<T> {
    v: T
    init(x: T) { self.v = x }
    func count<U>(other: U) -> int = 3
}
func go() -> int {
    let b = Box<int>(1)
    return b.count<string>("x")
}
""", "go"));

    // C2 — generic interface with TWO type params, class conforms over both.
    [Fact]
    public void C02_GenericInterface_TwoParams() => Assert.Equal(9, Run("""
namespace Test
interface IMap<K, V> { func get(k: K) -> V }
class One<K, V> : IMap<K, V> {
    val: V
    init(v: V) { self.val = v }
    func get(k: K) -> V = self.val
}
func use(m: IMap<string, int>) -> int = m.get("x")
func go() -> int {
    let o = One<string, int>(9)
    return use(o)
}
""", "go"));

    // C3 — nested generic field type Dictionary<string, List<T>>, used end to end.
    [Fact]
    public void C03_NestedGenericField() => Assert.Equal(2, Run("""
namespace Test
using "System.Collections.Generic"
class Index<T> {
    map: Dictionary<string, List<T>>
    init() { self.map = Dictionary<string, List<T>>() }
    func put(k: string, v: T) {
        let lst = List<T>()
        lst.Add(v)
        self.map[k] = lst
    }
    func size(k: string) -> int = self.map[k].Count
}
func go() -> int {
    let ix = Index<int>()
    ix.put("a", 10)
    ix.put("a", 20)
    return ix.size("a") + 1
}
""", "go"));

    // C4 — multi-param generic class : interface with two methods returning each param.
    [Fact]
    public void C04_MultiParam_ReturnsEach() => Assert.Equal(13, Run("""
namespace Test
interface IPair<A, B> {
    func left() -> A
    func right() -> B
}
class P<A, B> : IPair<A, B> {
    a: A
    b: B
    init(x: A, y: B) { self.a = x self.b = y }
    func left() -> A = self.a
    func right() -> B = self.b
}
func go() -> int {
    let p = P<int, int>(3, 10)
    let ip: IPair<int, int> = p
    return ip.left() + ip.right()
}
""", "go"));

    // C5 — value data : generic interface, passed where the interface is expected (boxing site).
    [Fact]
    public void C05_ValueData_GenericInterface_Boxes() => Assert.Equal(4, Run("""
namespace Test
interface ICount<T> { func count() -> int }
struct Bag<T> : ICount<T> { n: int }
func (b: Bag<T>) count<T>() -> int = b.n
func use(c: ICount<int>) -> int = c.count()
func go() -> int {
    let b = Bag<int> { n: 4 }
    return use(b)
}
""", "go"));

    // C6 — generic class : a non-generic interface (method independent of T).
    [Fact]
    public void C06_GenericRefData_NonGenericInterface() => Assert.Equal(1, Run("""
namespace Test
interface ITagged { func tag() -> int }
class Box<T> : ITagged {
    v: T
    init(x: T) { self.v = x }
    func tag() -> int = 1
}
func use(t: ITagged) -> int = t.tag()
func go() -> int {
    let b = Box<int>(5)
    return use(b)
}
""", "go"));

    // C7 — generic class : IComparable<T> over its own T (BCL generic interface, value param).
    [Fact]
    public void C07_GenericRefData_IComparableOfT()
    {
        var asm = Compile("""
namespace Test
using "System"
class Ver<T> : IComparable<Ver<T>> {
    n: int
    init(x: int) { self.n = x }
    func CompareTo(other: Ver<T>) -> int = self.n - other.n
}
func make() -> Ver<int> = Ver<int>(3)
""");
        Assert.True(HasIface(T(asm, "Test.Ver`1"), "IComparable`1"));
    }

    // C8 — store closed generic instances in a BCL collection (List<Box<int>>) and read back.
    [Fact]
    public void C08_ClosedGenericInstance_InCollection() => Assert.Equal(6, Run("""
namespace Test
using "System.Collections.Generic"
class Box<T> {
    v: T
    init(x: T) { self.v = x }
    func get() -> T = self.v
}
func go() -> int {
    var xs = List<Box<int>>()
    xs.Add(Box<int>(6))
    return xs[0].get()
}
""", "go"));

    // ============================================================================
    // GROUP D — nested types, generic-static dispatch, structured/async surfaces
    // ============================================================================

    // D1 — nested data type inside a class (ChanSelect.Arm shape). Nested type
    // declarations are now a language feature (parser → binder → Cecil NestedTypes,
    // with C#-faithful scoping); see NestedTypeTests for the full surface.
    [Fact]
    public void D01_NestedDataType()
    {
        var asm = Compile("""
namespace Test
class Select {
    struct Arm { kind: int, body: int }
    arms: int
    init() { self.arms = 0 }
}
func make() -> Select = Select()
""");
        EsHarness.Invoke(asm, "make");
    }

    // D2 — nested enum inside a class (ChanSelect.Kind shape). Landed with D01.
    [Fact]
    public void D02_NestedEnum()
    {
        var asm = Compile("""
namespace Test
class Select {
    enum Kind { recv, send, timeout, default }
    n: int
    init() { self.n = 0 }
}
func make() -> Select = Select()
""");
        EsHarness.Invoke(asm, "make");
    }

    // D3 — generic static-method dispatch (ChanOps generic-static shape, even though ChanOps
    // stays C#: confirms the call surface a generic static helper needs).
    [Fact]
    public void D03_GenericStaticMethod() => Assert.Equal(7, Run("""
namespace Test
static Ops {
    func identity<T>(x: T) -> T = x
}
func go() -> int = Ops.identity<int>(7)
""", "go"));

    // D4 — class : IAsyncDisposable with a real await in the dispose body (TaskScope shape).
    [Fact]
    public void D04_RefData_IAsyncDisposable_AwaitInBody()
    {
        var asm = Compile("""
namespace Test
using "System"
using "System.Threading.Tasks"
class Scope : IAsyncDisposable {
    var closed: bool
    init() { self.closed = false }
    func DisposeAsync() -> ValueTask {
        await Task.Delay(1)
        self.closed = true
    }
}
func make() -> Scope = Scope()
""");
        var s = EsHarness.Invoke(asm, "make")!;
        ((IAsyncDisposable)s).DisposeAsync().AsTask().GetAwaiter().GetResult();
        var closed = s.GetType().GetProperty("closed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(true, closed.GetValue(s));
    }

    // D5 — explicit interface member on a generic type, generic slot (func IBox<T>.get).
    [Fact]
    public void D05_ExplicitGenericInterfaceMember() => Assert.Equal(13, Run("""
namespace Test
interface IBox<T> { func get() -> T }
class Box<T> : IBox<T> {
    v: T
    init(x: T) { self.v = x }
    func IBox<T>.get() -> T = self.v
}
func read(b: IBox<int>) -> int = b.get()
func go() -> int {
    let b = Box<int>(13)
    return read(b)
}
""", "go"));

    // D6 — generic class : IEnumerable<T> ONLY (no non-generic), interface-enumerator return.
    // Isolates whether the non-generic IEnumerable bridge is the blocker vs the generic slot.
    [Fact]
    public void D06_GenericRefData_IEnumerableOfT_Only()
    {
        var asm = Compile("""
namespace Test
using "System.Collections.Generic"
class Seq<T> : IEnumerable<T> {
    src: IEnumerable<T>
    init(s: IEnumerable<T>) { self.src = s }
    func GetEnumerator() -> IEnumerator<T> = self.src.GetEnumerator()
}
func make() -> Seq<int> {
    var xs = List<int>()
    xs.Add(5)
    return Seq<int>(xs)
}
""");
        Assert.True(HasIface(T(asm, "Test.Seq`1"), "IEnumerable`1"));
    }

    // ============================================================================
    // GROUP E — value-`data` emission gaps surfaced authoring Result.es in E#
    // ============================================================================

    // E1 — default(T) for an unconstrained generic parameter instantiated with a
    // VALUE type. The old path emitted `ldnull` whenever the type wasn't known to be
    // a value type, which the verifier rejects for `default(int)` ("Nullobjref vs
    // value 'T'"). Result.es needs this for `default(TError)` in ok()/err() helpers.
    [Fact]
    public void E01_DefaultOfGenericParam_ValueInstantiation() => Assert.Equal(0, Run("""
namespace Test
[Struct]
struct Cell<T> { has: bool, val: T }
func empty<T>() -> Cell<T> = Cell<T> { has: false, val: default(T) }
func go() -> int {
    let c = empty<int>()
    return c.val
}
""", "go"));

    // E2 — returning a value-type promoted receiver BY VALUE. `self` on a value type
    // is a managed pointer (`T&`); `return c` must `ldobj` it to a value. Field access
    // keeps the address, so member reads are unaffected. Result.es needs this for
    // Inspect/InspectErr (`return r`).
    [Fact]
    public void E02_ReturnValueTypeSelf_ByValue() => Assert.Equal(5, Run("""
namespace Test
struct Counter { n: int }
func (c: Counter) same() -> Counter = c
func go() -> int {
    let c = Counter { n: 5 }
    return c.same().n
}
""", "go"));

    // ============================================================================
    // GROUP F — the strangler-fig flip: `Result` binds to the E#-authored stdlib
    // ============================================================================

    // F1 — the builtin `Result` resolves to Esharp.Stdlib.Result`2 BY METADATA NAME
    // (off the disk-loaded stdlib), not a staging C# seed. This
    // is the acceptance criterion for the flip; it holds because the test project
    // copies Esharp.Stdlib.dll into the output and the resolver probes for it.
    [Fact]
    public void F01_ResultBindsToEsharpStdlibByMetadataName()
    {
        var asm = Compile("""
namespace Test
func go() -> Result<int, string> = ok(7)
""");
        // Free funcs without `pub` emit as internal methods — search NonPublic too.
        var ret = asm.GetType("Test.Test")!
            .GetMethod("go", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .ReturnType;
        Assert.Equal("Esharp.Stdlib", ret.Assembly.GetName().Name);
        Assert.Equal("Esharp.Stdlib.Result`2", ret.GetGenericTypeDefinition().FullName);
    }

    // F2 — the propagation/destructuring surface runs end-to-end against the field-based
    // E# Result: `ok`/`error` construction, `?` propagation (across calls), and `match`
    // destructuring with typed bindings used in arithmetic. (Combinator METHOD calls —
    // Unwrap/UnwrapOr/Map/… — erase their receiver's type args to `object` on the
    // probe-loaded stdlib Result, so they don't yet compose in typed contexts.)
    [Fact]
    public void F02_ResultPropagationAndMatch_RunAgainstStdlib() => Assert.Equal(70, Run("""
namespace Test
func parse(n: int) -> Result<int, string> {
    if n < 0 { return error("neg") }
    return ok(n)
}
func step(n: int) -> Result<int, string> {
    let doubled = parse(n)?
    return ok(doubled * 2)
}
func go() -> int {
    let a = parse(10)?            // 10  (? unwraps ok)
    let b = step(a)?             // step(10) = ok(20) -> 20
    match step(b) {              // step(20) = ok(40)
        .ok(v) { return v + a + b }   // 40 + 10 + 20 = 70
        .err(e) { return -1 }
    }
    return -2
}
""", "go"));

    // F3 — .Value on the wrong variant still throws (preserve-throw, now via the
    // compiler accessor intrinsic guarding the field load rather than a C# getter).
    [Fact]
    public void F03_ValueOnError_Throws()
    {
        var asm = Compile("""
namespace Test
func bad() -> Result<int, string> = error("nope")
func go() -> int = bad().Value
""");
        var ex = Assert.ThrowsAny<Exception>(() => EsHarness.Invoke(asm, "go"));
        // The intrinsic guards the field load and throws; reflection wraps it.
        Assert.IsType<InvalidOperationException>(ex.InnerException ?? ex);
    }

    // ============================================================================
    // GROUP G — the flipped Result is FULLY idiomatic: every combinator composes in
    // typed contexts against Esharp.Stdlib.Result`2, the arity-keyed registry lets a
    // generic data + a same-name static coexist, and the static factories work.
    // ============================================================================

    // A `parse` that yields ok(n) for n>=0, error("neg") otherwise — the fixture every
    // combinator test composes on.
    const string P = """
namespace Test
func parse(n: int) -> Result<int, string> {
    if n < 0 { return error("neg") }
    return ok(n)
}

""";

    // --- receiver-typed combinators (return type comes from the receiver) ---------
    [Fact] public void G01_UnwrapOr_Ok() => Assert.Equal(10, Run(P + "func go() -> int = parse(10).UnwrapOr(7)", "go"));
    [Fact] public void G02_UnwrapOr_Err() => Assert.Equal(7, Run(P + "func go() -> int = parse(-1).UnwrapOr(7)", "go"));
    [Fact] public void G03_Unwrap_Ok() => Assert.Equal(10, Run(P + "func go() -> int = parse(10).Unwrap()", "go"));
    [Fact] public void G04_UnwrapErr_Err() => Assert.Equal("neg", Run(P + "func go() -> string = parse(-1).UnwrapErr()", "go"));
    [Fact] public void G05_UnwrapOrElse_Err() => Assert.Equal(3, Run(P + "func go() -> int = parse(-1).UnwrapOrElse((e) => e.Length)", "go"));
    [Fact] public void G06_UnwrapOrElse_Ok() => Assert.Equal(10, Run(P + "func go() -> int = parse(10).UnwrapOrElse((e) => e.Length)", "go"));

    // --- lambda-return combinators (return type comes from the lambda body) --------
    [Fact] public void G07_Map_Ok_ThenUnwrap() => Assert.Equal(15, Run(P + "func go() -> int = parse(10).Map((x) => x + 5).Unwrap()", "go"));
    [Fact] public void G08_Map_Err_Passthrough() => Assert.Equal(99, Run(P + "func go() -> int = parse(-1).Map((x) => x + 5).UnwrapOr(99)", "go"));
    [Fact] public void G09_Map_ToString_TNewIsString() => Assert.Equal("n10", Run(P + "func go() -> string = parse(10).Map((x) => \"n\" + x.ToString()).UnwrapOr(\"z\")", "go"));
    [Fact] public void G10_MapErr_Err_TNewIsInt() => Assert.Equal(3, Run(P + "func go() -> int = parse(-1).MapErr((e) => e.Length).UnwrapErr()", "go"));
    [Fact] public void G11_MapErr_Ok_Passthrough() => Assert.Equal(10, Run(P + "func go() -> int = parse(10).MapErr((e) => e.Length).UnwrapOr(0)", "go"));
    [Fact] public void G12_Bind_Ok_Chains() => Assert.Equal(20, Run(P + "func go() -> int = parse(10).Bind((x) => parse(x * 2)).Unwrap()", "go"));
    [Fact] public void G13_Bind_Err_ShortCircuits() => Assert.Equal(42, Run(P + "func go() -> int = parse(-1).Bind((x) => parse(x * 2)).UnwrapOr(42)", "go"));
    [Fact] public void G14_Match_Ok() => Assert.Equal(11, Run(P + "func go() -> int = parse(10).Match((v) => v + 1, (e) => -1)", "go"));
    [Fact] public void G15_Match_Err() => Assert.Equal(-1, Run(P + "func go() -> int = parse(-1).Match((v) => v + 1, (e) => -1)", "go"));

    // --- chaining + typed composition (the canary: combinator results as operands) -
    [Fact] public void G16_Map_Chain() => Assert.Equal(22, Run(P + "func go() -> int = parse(10).Map((x) => x + 1).Map((x) => x * 2).UnwrapOr(0)", "go"));
    [Fact] public void G17_Map_InArithmetic() => Assert.Equal(111, Run(P + """
func go() -> int {
    let m = parse(10).Map((x) => x + 1)
    return m.UnwrapOr(0) + 100
}
""", "go"));
    [Fact] public void G18_Map_Then_Bind() => Assert.Equal(42, Run(P + "func go() -> int = parse(10).Map((x) => x + 11).Bind((x) => parse(x * 2)).Unwrap()", "go"));

    // --- Inspect / InspectErr (void lambda; returns the Result unchanged) ----------
    [Fact] public void G19_Inspect_Passthrough() => Assert.Equal(10, Run(P + "func go() -> int = parse(10).Inspect((v) => Console.Out.Write(\"\")).UnwrapOr(0)", "go"));
    [Fact] public void G20_InspectErr_Passthrough() => Assert.Equal(7, Run(P + "func go() -> int = parse(-1).InspectErr((e) => Console.Out.Write(\"\")).UnwrapOr(7)", "go"));

    // --- §1 arity coexistence: a generic `data` and a same-name `static func` ------
    [Fact] public void G21_DataAndStaticFunc_SameName_Coexist() => Assert.Equal(47, Run("""
namespace Test
struct Box<T, E> { v: T, e: E }
static Box { func tag() -> int = 42 }
func go() -> int {
    let b = Box<int, string> { v: 5, e: "" }
    return b.v + Box.tag()
}
""", "go"));

    [Fact]
    public void G22_SameArityRedeclaration_StillErrors()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Compile("""
namespace Test
struct Foo { x: int }
enum Foo { a, b }
func go() -> int = 0
"""));
        Assert.Contains("ES2152", ex.Message);
    }

    // --- §2 static factories: the faithful port of the C# seed's static class ------
    [Fact] public void G23_StaticFactory_Ok_FromEsharp() => Assert.Equal(7, Run("""
namespace Test
using "Esharp.Stdlib"
func go() -> int = Result.Ok<int, string>(7).Value
""", "go"));

    // The error-side static factory, called from E#: `Result.Error<int,string>(e)`
    // resolves to the static class `Esharp.Stdlib.Result`'s `Error` member (arity 0 in
    // the registry), distinct from the `Result`2` data type the generic args would
    // instantiate. The returned Result's `.Error` field reads the payload back.
    [Fact] public void G24_StaticFactory_Error_FromEsharp() => Assert.Equal("boom", Run("""
namespace Test
using "Esharp.Stdlib"
func go() -> string = Result.Error<int, string>("boom").Error
""", "go"));

    // ============================================================================
    // GROUP H — adjacent surface triage. Semi-unrelated shapes that the §1-§3 changes
    // touch or sit near: explicit/return-only type-arg inference, generic-call member
    // chains, lambda inference in BCL contexts, arity coexistence variants.
    // ============================================================================

    // H1 — explicit type arg on a free function whose type param is RETURN-ONLY.
    [Fact] public void H01_FreeFunc_ReturnOnly_ExplicitTypeArg() => Assert.Equal(0, Run("""
namespace Test
func zero<T>() -> T = default(T)
func go() -> int = zero<int>()
""", "go"));

    // H2 — external static generic with a return-only type param (Array.Empty<int>).
    [Fact] public void H02_ExternalStatic_ReturnOnly_ExplicitTypeArg() => Assert.Equal(0, Run("""
namespace Test
using "System"
func go() -> int {
    let a = Array.Empty<int>()
    return a.Length
}
""", "go"));

    // H3 — a member access trailing a GENERIC call (the shape `f<T>(x).member`).
    [Fact] public void H03_GenericCall_ThenMemberAccess() => Assert.Equal(7, Run("""
namespace Test
struct Box<T> { v: T }
func boxed<T>(x: T) -> Box<T> = Box<T> { v: x }
func go() -> int = boxed<int>(7).v
""", "go"));

    // H4 — BCL LINQ Select with a lambda; lambda return inference into a generic call.
    // [1, 2] → Select(x => x + 1) → [2, 3] → Sum() = 5.
    [Fact] public void H04_Select_LambdaReturnInference() => Assert.Equal(5, Run("""
namespace Test
using "System.Linq"
using "System.Collections.Generic"
func go() -> int {
    let xs = List<int>()
    xs.Add(1)
    xs.Add(2)
    return xs.Select((x) => x + 1).Sum()
}
""", "go"));

    // H5 — match destructuring with an error-branch payload binding used in arithmetic.
    [Fact] public void H05_Match_ErrPayload() => Assert.Equal(3, Run(P + """
func go() -> int {
    match parse(-1) { .ok(v) { return v } .err(e) { return e.Length } }
    return -99
}
""", "go"));

    // H6 — `?` propagation chained across two calls.
    [Fact] public void H06_TryUnwrap_Chained() => Assert.Equal(40, Run(P + """
func step(n: int) -> Result<int, string> = ok(n * 2)
func go() -> int {
    let a = parse(10)?
    let b = step(a)?
    return b * 2
}
""", "go"));

    // H7 — a NON-Result promoted generic combinator (the same nested-inference path as Map).
    [Fact] public void H07_NonResult_PromotedGenericMap() => Assert.Equal(8, Run("""
namespace Test
struct Wrap<T> { v: T }
func (w: Wrap<T>) mapped<T, U>(f: Func<T, U>) -> Wrap<U> = Wrap<U> { v: f(w.v) }
func go() -> int {
    let w = Wrap<int> { v: 3 }
    let r = w.mapped((x) => x + 5)
    return r.v
}
""", "go"));

    // H8 — a generic BCL collection of Result elements; element type must be the Result.
    [Fact] public void H08_ListOfResult_ElementTyped() => Assert.Equal(9, Run("""
namespace Test
using "System.Collections.Generic"

func parse(n: int) -> Result<int, string> {
    if n < 0 { return error("neg") }
    return ok(n)
}

func go() -> int {
    let xs = List<Result<int, string>>()
    xs.Add(parse(9))
    return xs[0].UnwrapOr(0)
}
""", "go"));

    // H9 — arity coexistence across two generic arities: `struct Pair<A>` and `struct Pair<A,B>`.
    [Fact] public void H09_TwoGenericArities_Coexist() => Assert.Equal(12, Run("""
namespace Test
struct Pair<A> { a: A }
struct Pair<A, B> { a: A, b: B }
func go() -> int {
    let one = Pair<int> { a: 5 }
    let two = Pair<int, int> { a: 3, b: 4 }
    return one.a + two.a + two.b
}
""", "go"));

    // Non-generic and generic type declarations sharing a source name are a
    // distinct CLR pair (`Spawned` and `Spawned`1`), not one last-write-wins
    // resolver entry. The stdlib task-handle surface relies on this exact shape.
    [Fact] public void H09b_NonGenericAndGenericType_Coexist() => Assert.Equal(12, Run("""
namespace Test
struct Spawned { value: int }
struct Spawned<T> { value: T }
func go() -> int {
    let plain = Spawned { value: 5 }
    let typed = Spawned<int> { value: 7 }
    return plain.value + typed.value
}
""", "go"));

    // H10 — a user function taking a `Func<>` parameter, invoked with a lambda.
    [Fact] public void H10_UserFunc_TakesFuncParam() => Assert.Equal(15, Run("""
namespace Test
func apply(f: Func<int, int>, x: int) -> int = f(x)
func go() -> int = apply((n) => n + 5, 10)
""", "go"));

    [Fact]
    public void G25_StaticFactory_ReflectableFromCSharp()
    {
        // The stdlib is the assembly the flipped Result resolves into; reflect the
        // static class `Esharp.Stdlib.Result` and invoke Ok/Error exactly as C# would.
        var asm = Compile("""
namespace Test
func probe() -> Result<int, string> = ok(1)
""");
        var stdlib = asm.GetType("Test.Test")!
            .GetMethod("probe", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .ReturnType.Assembly;
        var staticCls = stdlib.GetType("Esharp.Stdlib.Result")!;
        var ok = staticCls.GetMethod("Ok")!.MakeGenericMethod(typeof(int), typeof(string));
        var r = ok.Invoke(null, [42])!;
        Assert.True((bool)r.GetType().GetField("IsOk")!.GetValue(r)!);
        Assert.Equal(42, r.GetType().GetField("Value")!.GetValue(r));
        var err = staticCls.GetMethod("Error")!.MakeGenericMethod(typeof(int), typeof(string));
        var e = err.Invoke(null, ["x"])!;
        Assert.False((bool)e.GetType().GetField("IsOk")!.GetValue(e)!);
        Assert.Equal("x", e.GetType().GetField("Error")!.GetValue(e));
    }

    // ============================================================================
    // GROUP I — Result surface depth: combinator compositions, the static factories
    // in expression contexts, `?` interplay, and Result-in-collections. All against
    // the flipped Esharp.Stdlib.Result`2.
    // ============================================================================

    [Fact] public void I01_Map_ToString_Length() => Assert.Equal(3, Run(P + "func go() -> int = parse(10).Map((x) => \"n\" + x.ToString()).UnwrapOr(\"\").Length", "go"));
    [Fact] public void I02_Map_Twice_Arithmetic() => Assert.Equal(43, Run(P + "func go() -> int = parse(10).Map((x) => x * 2).Map((x) => x + 3).UnwrapOr(0) + 20", "go"));
    [Fact] public void I03_Bind_Ok_Twice() => Assert.Equal(40, Run(P + "func step(n: int) -> Result<int, string> = ok(n * 2)\nfunc go() -> int = parse(10).Bind((x) => step(x)).Bind((x) => step(x)).Unwrap()", "go"));
    [Fact] public void I04_Bind_Err_ShortCircuits() => Assert.Equal(-1, Run(P + "func step(n: int) -> Result<int, string> = ok(n * 2)\nfunc go() -> int = parse(-5).Bind((x) => step(x)).UnwrapOr(-1)", "go"));
    [Fact] public void I05_MapErr_Then_Match() => Assert.Equal(3, Run(P + "func go() -> int = parse(-1).MapErr((e) => e.Length).Match((v) => v, (e) => e)", "go"));
    [Fact] public void I06_Match_Ok_Branch() => Assert.Equal(20, Run(P + "func go() -> int = parse(10).Match((v) => v * 2, (e) => -1)", "go"));
    [Fact] public void I07_Inspect_ReturnsValue() => Assert.Equal(10, Run(P + "func go() -> int = parse(10).Inspect((v) => Console.Out.Write(\"\")).UnwrapOr(0)", "go"));
    [Fact] public void I08_UnwrapOrElse_FromError() => Assert.Equal(3, Run(P + "func go() -> int = parse(-1).UnwrapOrElse((e) => e.Length)", "go"));
    [Fact] public void I09_Unwrap_Ok_InArithmetic() => Assert.Equal(13, Run(P + "func go() -> int = parse(10).Unwrap() + 3", "go"));
    [Fact] public void I10_UnwrapErr_Err() => Assert.Equal("neg", Run(P + "func go() -> string = parse(-1).UnwrapErr()", "go"));

    [Fact] public void I11_StaticOk_Value() => Assert.Equal(7, Run("""
namespace Test
using "Esharp.Stdlib"
func go() -> int = Result.Ok<int, string>(7).Value
""", "go"));
    [Fact] public void I12_StaticError_Payload() => Assert.Equal("boom", Run("""
namespace Test
using "Esharp.Stdlib"
func go() -> string = Result.Error<int, string>("boom").Error
""", "go"));
    [Fact] public void I13_StaticOk_IsOk() => Assert.Equal(true, Run("""
namespace Test
using "Esharp.Stdlib"
func go() -> bool = Result.Ok<int, string>(7).IsOk
""", "go"));
    [Fact] public void I14_StaticError_IsError() => Assert.Equal(true, Run("""
namespace Test
using "Esharp.Stdlib"
func go() -> bool = Result.Error<int, string>("e").IsError
""", "go"));
    [Fact] public void I15_StaticOk_ThenMap() => Assert.Equal(8, Run("""
namespace Test
using "Esharp.Stdlib"
func go() -> int = Result.Ok<int, string>(7).Map((x) => x + 1).UnwrapOr(0)
""", "go"));

    // parse(10)?=10 → step=20 → step=40.
    [Fact] public void I16_TryUnwrap_OkChain() => Assert.Equal(40, Run(P + """
func step(n: int) -> Result<int, string> = ok(n * 2)
func go() -> int {
    let a = parse(10)?
    let b = step(a)?
    let c = step(b)?
    return c
}
""", "go"));
    [Fact] public void I17_TryUnwrap_ErrPropagates() => Assert.Equal(-1, Run(P + """
func chain(n: int) -> Result<int, string> {
    let a = parse(n)?
    return ok(a + 1)
}
func go() -> int = chain(-5).UnwrapOr(-1)
""", "go"));
    // parse(8)→ok(8) UnwrapOr=8; parse(-1)→err UnwrapOr=10 → 18.
    [Fact] public void I18_ListOfResult_UnwrapOr() => Assert.Equal(18, Run("""
namespace Test
using "System.Collections.Generic"

func parse(n: int) -> Result<int, string> {
    if n < 0 { return error("neg") }
    return ok(n)
}

func go() -> int {
    let xs = List<Result<int, string>>()
    xs.Add(parse(8))
    xs.Add(parse(-1))
    return xs[0].UnwrapOr(0) + xs[1].UnwrapOr(10)
}
""", "go"));
    [Fact] public void I19_ResultReturnedFromBranch() => Assert.Equal(5, Run(P + """
func pick(b: bool) -> Result<int, string> {
    if b { return ok(5) }
    return error("no")
}
func go() -> int = pick(true).UnwrapOr(0)
""", "go"));
    [Fact] public void I20_Map_Bind_Match_Pipeline() => Assert.Equal(21, Run(P + """
func dbl(n: int) -> Result<int, string> = ok(n * 2)
func go() -> int = parse(10).Map((x) => x + 1).Bind((x) => dbl(x)).Match((v) => v - 1, (e) => -1)
""", "go"));

    // ============================================================================
    // GROUP J — generic inference: promoted user generics (receiver-bound + lambda-
    // inferred type args), BCL extension lambdas (Select/Where/Any/...), and arity
    // coexistence across data/static-func/generic-arity boundaries.
    // ============================================================================

    // Wrap + a promoted generic `mapped<T,U>` reused across the promoted-generic tests.
    const string WG = """
namespace Test
struct Wrap<T> { v: T }
func (w: Wrap<T>) mapped<T, U>(f: Func<T, U>) -> Wrap<U> = Wrap<U> { v: f(w.v) }

""";

    [Fact] public void J01_PromotedGeneric_IntToInt() => Assert.Equal(8, Run(WG + """
func go() -> int {
    let w = Wrap<int> { v: 3 }
    return w.mapped((x) => x + 5).v
}
""", "go"));
    // "n" + "7" = "n7", Length = 2 (U inferred as string).
    [Fact] public void J02_PromotedGeneric_IntToString() => Assert.Equal(2, Run(WG + """
func go() -> int {
    let w = Wrap<int> { v: 7 }
    return w.mapped((x) => "n" + x.ToString()).v.Length
}
""", "go"));
    [Fact] public void J03_PromotedGeneric_Chained() => Assert.Equal(16, Run(WG + """
func go() -> int {
    let w = Wrap<int> { v: 3 }
    return w.mapped((x) => x + 5).mapped((y) => y * 2).v
}
""", "go"));
    [Fact] public void J04_PromotedGeneric_StringReceiver() => Assert.Equal(5, Run(WG + """
func go() -> int {
    let w = Wrap<string> { v: "hello" }
    return w.mapped((s) => s.Length).v
}
""", "go"));

    // [1,2,3] → Select(x*2) → [2,4,6] → Sum = 12.
    [Fact] public void J05_Select_Sum() => Assert.Equal(12, Run("""
namespace Test
using "System.Linq"
using "System.Collections.Generic"
func go() -> int {
    let xs = List<int>()
    xs.Add(1)
    xs.Add(2)
    xs.Add(3)
    return xs.Select((x) => x * 2).Sum()
}
""", "go"));
    [Fact] public void J06_Where_Count() => Assert.Equal(2, Run("""
namespace Test
using "System.Linq"
using "System.Collections.Generic"
func go() -> int {
    let xs = List<int>()
    xs.Add(1)
    xs.Add(5)
    xs.Add(8)
    return xs.Where((x) => x > 3).Count()
}
""", "go"));
    [Fact] public void J07_Select_ToString_Count() => Assert.Equal(2, Run("""
namespace Test
using "System.Linq"
using "System.Collections.Generic"
func go() -> int {
    let xs = List<int>()
    xs.Add(10)
    xs.Add(20)
    return xs.Select((x) => x.ToString()).Count()
}
""", "go"));
    [Fact] public void J08_Where_Select_Sum() => Assert.Equal(26, Run("""
namespace Test
using "System.Linq"
using "System.Collections.Generic"
func go() -> int {
    let xs = List<int>()
    xs.Add(1)
    xs.Add(5)
    xs.Add(8)
    return xs.Where((x) => x > 3).Select((x) => x * 2).Sum()
}
""", "go"));
    [Fact] public void J09_Any_Predicate() => Assert.Equal(true, Run("""
namespace Test
using "System.Linq"
using "System.Collections.Generic"
func go() -> bool {
    let xs = List<int>()
    xs.Add(1)
    xs.Add(9)
    return xs.Any((x) => x > 5)
}
""", "go"));
    [Fact] public void J10_All_Predicate() => Assert.Equal(false, Run("""
namespace Test
using "System.Linq"
using "System.Collections.Generic"
func go() -> bool {
    let xs = List<int>()
    xs.Add(2)
    xs.Add(9)
    return xs.All((x) => x > 5)
}
""", "go"));

    [Fact] public void J11_Arity_DataAndStaticFunc() => Assert.Equal(47, Run("""
namespace Test
struct Tag<T> { v: T }
static Tag { func id() -> int = 42 }
func go() -> int {
    let t = Tag<int> { v: 5 }
    return t.v + Tag.id()
}
""", "go"));
    [Fact] public void J12_Arity_TwoGenericArities() => Assert.Equal(12, Run("""
namespace Test
struct Cell<A> { a: A }
struct Cell<A, B> { a: A, b: B }
func go() -> int {
    let one = Cell<int> { a: 5 }
    let two = Cell<int, int> { a: 3, b: 4 }
    return one.a + two.a + two.b
}
""", "go"));
    [Fact] public void J13_NestedGenericData() => Assert.Equal(9, Run("""
namespace Test
struct Box<T> { v: T }
func go() -> int {
    let inner = Box<int> { v: 9 }
    let outer = Box<Box<int>> { v: inner }
    return outer.v.v
}
""", "go"));
    [Fact] public void J14_UserFunc_FuncParam_Lambda() => Assert.Equal(15, Run("""
namespace Test
func apply(f: Func<int, int>, x: int) -> int = f(x)
func go() -> int = apply((n) => n + 5, 10)
""", "go"));
    [Fact] public void J15_PromotedGeneric_ResultElement() => Assert.Equal(11, Run(P + WG_INLINE_BODY, "go"));

    // J15 body: a promoted generic mapping over a Result payload type. Declared here so
    // the source keeps `data`/`func`/`using` ordering valid (parse + Wrap + mapped + go).
    const string WG_INLINE_BODY = """
struct Wrap<T> { v: T }
func (w: Wrap<T>) mapped<T, U>(f: Func<T, U>) -> Wrap<U> = Wrap<U> { v: f(w.v) }
func go() -> int {
    let w = Wrap<int> { v: parse(10).UnwrapOr(0) }
    return w.mapped((x) => x + 1).v
}
""";
}
