using System.Reflection;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

// Stress matrix for the seams between independently working language features.
// Each test deliberately combines several features that have historically lived
// in separate vertical slices.
public sealed class FeatureMixingTests
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    static int _asmCounter;

    static Assembly CompileStrict(string source)
    {
        var asmName = $"FeatMix_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        var errs = binder.Diagnostics
            .Where(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error)
            .ToList();
        Assert.Empty(errs);

        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        return EsHarness.EmitBoundToFileVerifiedAndLoad(binder, bound, asmName, path);
    }

    static Assembly CompileLoose(string source)
    {
        var asmName = $"FeatMix_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        return EsHarness.EmitBoundToFileVerifiedAndLoad(binder, bound, asmName, path);
    }

    static object? Invoke(Assembly asm, string typeName, string methodName, params object?[] args)
    {
        var type = asm.GetType($"Test.{typeName}")
            ?? throw new Exception($"Type Test.{typeName} not found");
        var method = type.GetMethod(methodName, AnyStatic)
            ?? throw new Exception($"Method {methodName} not found on {typeName}");
        return method.Invoke(null, args);
    }

    static object? Unwrap(object? v)
    {
        if (v is null) return null;
        var t = v.GetType();
        if (v is Task task) { task.GetAwaiter().GetResult(); return null; }
        if (v is ValueTask vt) { vt.GetAwaiter().GetResult(); return null; }
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            if (def == typeof(Task<>) || def == typeof(ValueTask<>))
            {
                var awaiter = t.GetMethod("GetAwaiter", AnyInstance)!.Invoke(v, null)!;
                return awaiter.GetType().GetMethod("GetResult", AnyInstance)!.Invoke(awaiter, null);
            }
        }
        return v;
    }

    static object? InvokeAsync(Assembly asm, string typeName, string methodName, params object?[] args)
        => Unwrap(Invoke(asm, typeName, methodName, args));

    // ─────────────────────────────────────────────────────────────────────
    // Axis 1: async × match × ref choice × try/catch
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Axis1_AsyncEvalRefChoice_TryCatchAroundAwait()
    {
        // ref union AST + async eval that awaits inside one arm and throws
        // inside another. Outer try/catch converts the throw to a sentinel.
        const string source = """
namespace Test

ref union Expr {
    literal(value: int)
    add(left: Expr, right: Expr)
    fail(reason: string)
}

func eval(e: Expr) -> int {
    match (e: Expr) {
        .literal(lit) {
            let v = await Task.FromResult(lit.value)
            return v
        }
        .add(a) {
            let l = eval(a.left)
            let r = eval(a.right)
            return l + r
        }
        .fail(f) {
            throw InvalidOperationException(f.reason)
        }
    }
    return -1
}

func run() -> int {
    let tree = Expr_add { left: Expr_literal { value: 3 }, right: Expr_fail { reason: "no" } }
    var result = 0
    try {
        result = await eval(tree)
    } catch (e: Exception) {
        result = -42
    }
    return result
}
""";
        var asm = CompileStrict(source);
        Assert.Equal(-42, InvokeAsync(asm, "Test", "run"));
    }

    [Fact]
    public void Axis1_AsyncMatch_AwaitInEveryArm()
    {
        // Each arm of a ref-choice match has its own await — the state machine
        // must preserve correct resume points per arm.
        const string source = """
namespace Test

ref union Cmd {
    one(n: int)
    two(a: int, b: int)
    three(s: string)
}

func handle(c: Cmd) -> int {
    match (c: Cmd) {
        .one(o) {
            let v = await Task.FromResult(o.n)
            return v + 1
        }
        .two(t) {
            let a = await Task.FromResult(t.a)
            let b = await Task.FromResult(t.b)
            return a + b
        }
        .three(t) {
            let s = await Task.FromResult(t.s)
            return s.Length
        }
    }
    return -1
}

func one() -> int = await handle(Cmd_one { n: 41 })
func two() -> int = await handle(Cmd_two { a: 10, b: 20 })
func three() -> int = await handle(Cmd_three { s: "hello" })
""";
        var asm = CompileStrict(source);
        Assert.Equal(42, InvokeAsync(asm, "Test", "one"));
        Assert.Equal(30, InvokeAsync(asm, "Test", "two"));
        Assert.Equal(5, InvokeAsync(asm, "Test", "three"));
    }

    [Fact]
    public void Axis1_AsyncResultMatch_TryCatchInsideArm()
    {
        // Async fn returns Result<T,E>; the caller wraps an await in try/catch and
        // converts an exception to error(). The await auto-promotes the caller to
        // async too (uncolored async). Tests the full chain: Task → await → try →
        // catch → Result construction → match unwrap.
        const string source = """
namespace Test

func parseAsync(s: string) -> int {
    let v = await Task.FromResult(int.Parse(s))
    return v
}

func safeParse(s: string) -> Result<int, string> {
    var n = 0
    try {
        n = await parseAsync(s)
    } catch (e: Exception) {
        return error("bad: {e.Message}")
    }
    return ok(n)
}

func runOk() -> int {
    let r = await safeParse("42")
    match (r: Result<int, string>) {
        .ok(v) { return v }
        .err(_) { return -1 }
    }
    return -2
}

func runErr() -> int {
    let r = await safeParse("nope")
    match (r: Result<int, string>) {
        .ok(_) { return -1 }
        .err(_) { return 99 }
    }
    return -2
}
""";
        var asm = CompileStrict(source);
        Assert.Equal(42, InvokeAsync(asm, "Test", "runOk"));
        Assert.Equal(99, InvokeAsync(asm, "Test", "runErr"));
    }

    [Fact]
    public void Axis1_AsyncResultMatch_SyncCallerForceJoinsUncoloredAsyncCall()
    {
        // A sync caller binds the eventual int, starts parseAsync eagerly, and
        // blocks only when ok(n) consumes it.
        const string source = """
namespace Test

func parseAsync(s: string) -> int {
    let v = await Task.FromResult(int.Parse(s))
    return v
}

func safeParse(s: string) -> int {
    let n = parseAsync(s)
    return n
}
""";
        Assert.Equal(42, EsHarness.Run(source, "safeParse", "42"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Axis 2: spawn × *T capture × defer  (TaskScope is C#-only today)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Axis2_SpawnCapturesPointer_MutatesThroughIt()
    {
        // Spawn body captures *T to shared heap-allocated state, mutates it,
        // outer scope reads after Join. Pointer must survive display-class hoist.
        const string source = """
namespace Test

struct Counter {
    var value: int
}

func run() -> int {
    var box: *Counter = new Counter { value: 0 }
    let job = spawn {
        box.value = box.value + 10
        box.value = box.value + 32
    }
    job.Join()
    return box.value
}
""";
        var asm = CompileStrict(source);
        Assert.Equal(42, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Axis2_DeferLifoOrder_InsideAndAroundSpawn()
    {
        // defer ordering: inner spawn body has its own defers (LIFO at spawn
        // exit), outer function has its own (LIFO at run exit). Both must fire.
        const string source = """
namespace Test

struct Log {
    var lines: List<string>
}

func run() -> int {
    var log: *Log = new Log { lines: List<string>() }
    let job = spawn {
        defer { log.lines.Add("inner-1") }
        defer { log.lines.Add("inner-2") }
        log.lines.Add("body")
    }
    defer { log.lines.Add("outer-1") }
    defer { log.lines.Add("outer-2") }
    job.Join()
    log.lines.Add("after-join")
    return log.lines.Count
}

func line(i: int) -> string {
    var log: *Log = new Log { lines: List<string>() }
    let job = spawn {
        defer { log.lines.Add("inner-1") }
        defer { log.lines.Add("inner-2") }
        log.lines.Add("body")
    }
    job.Join()
    return log.lines[i]
}
""";
        var asm = CompileStrict(source);
        // body, inner-2, inner-1 (LIFO inside spawn)
        Assert.Equal("body", Invoke(asm, "Test", "line", 0));
        Assert.Equal("inner-2", Invoke(asm, "Test", "line", 1));
        Assert.Equal("inner-1", Invoke(asm, "Test", "line", 2));
    }

    [Fact]
    public void Axis2_SpawnPtrCapture_ChanCloseInDefer()
    {
        // chan + spawn + *T capture + defer: producer captures *State and chan,
        // defers Close(). Consumer drains. Tests no forgotten-close hang.
        const string source = """
namespace Test

struct State {
    var produced: int
}

func run() -> int {
    var s: *State = new State { produced: 0 }
    let ch = chan<int>(4)
    let producer = spawn {
        defer { ch.Close() }
        ch.Send(1)
        ch.Send(2)
        ch.Send(3)
        s.produced = 3
    }
    producer.Join()
    var total = 0
    for v in ch {
        total += v
    }
    return total + s.produced
}
""";
        var asm = CompileStrict(source);
        Assert.Equal(9, Invoke(asm, "Test", "run"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Axis 3: with × readonly data × generic × embedding
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Axis3_WithOnReadonlyData_OverwritingEmbeddedField()
    {
        // readonly data containing an embedded data; `with` must produce a new
        // instance with the embedded field overwritten and others preserved.
        const string source = """
namespace Test

struct Vec2 {
    var x: int
    var y: int
}

readonly struct Transform {
    Vec2
    scale: int
}

func run() -> int {
    let t = Transform { x: 1, y: 2, scale: 3 }
    let u = t with { Vec2: Vec2 { x: 10, y: 20 } }
    return u.x + u.y + u.scale + t.x
}
""";
        // Original t.x should still be 1, u.x should be 10. 10+20+3+1 = 34.
        var asm = CompileStrict(source);
        Assert.Equal(34, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Axis3_GenericReadonlyData_WithChain()
    {
        // Generic readonly data + chained `with` updates. Verifies the
        // generic instantiation is preserved across each `with` copy.
        const string source = """
namespace Test

readonly struct Pair<A, B> {
    first: A
    second: B
}

func run() -> int {
    let p = Pair<int, int> { first: 1, second: 2 }
    let q = p with { first: 100 }
    let r = q with { second: 200 }
    return r.first + r.second + p.first + p.second
}
""";
        var asm = CompileStrict(source);
        Assert.Equal(303, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Axis3_WithUpdatesEmbeddedField_PromotedAccess()
    {
        // Hardest seam: `with { x: 99 }` where x is a promoted field from an
        // embedded type. Today users may have to write `with { Vec2: ... }`.
        // This pins which form is supported.
        const string source = """
namespace Test

struct Vec2 {
    var x: int
    var y: int
}

struct Wrap {
    Vec2
    tag: int
}

func run() -> int {
    let w = Wrap { x: 1, y: 2, tag: 3 }
    let q = w with { Vec2: Vec2 { x: 10, y: 20 } }
    return q.x + q.y + q.tag
}
""";
        var asm = CompileStrict(source);
        Assert.Equal(33, Invoke(asm, "Test", "run"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Axis 4: embedding × interface satisfaction × *T method sets
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Axis4_EmbeddedMethodPromoted_SatisfiesProtocol()
    {
        // The outer type satisfies the protocol via a method promoted from
        // the embedded type — implicit conformance at the seam between
        // protocol matching and embedding.
        const string source = """
namespace Test

struct Greeter {
    name: string

    func describe() -> string {
        return "hi {self.name}"
    }
}

struct Wrapped : IDescribable {
    Greeter
    tag: int
}

interface IDescribable {
    func describe() -> string
}

func report(d: IDescribable) -> string = d.describe()

func run() -> string {
    let w = Wrapped { name: "kae", tag: 1 }
    return report(w)
}
""";
        var asm = CompileStrict(source);
        Assert.Equal("hi kae", Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Axis4_PointerEmbedded_PointerReceiverSatisfiesProtocol()
    {
        // *T satisfies a protocol via a method promoted through pointer
        // embedding (auto-deref). Combines: pointer embedding, *T method
        // set, protocol wrapper class.
        const string source = """
namespace Test

struct Inner {
    var n: int
}

func (i: *Inner) bump() {
    i.n += 1
}

func (i: Inner) value() -> int = i.n

struct Outer : IBumper {
    *Inner
    label: string
}

interface IBumper {
    func bump()
    func value() -> int
}

func twice(b: IBumper) -> int {
    b.bump()
    b.bump()
    return b.value()
}

func run() -> int {
    var o: *Outer = new Outer { Inner: new Inner { n: 40 }, label: "x" }
    return twice(o)
}
""";
        var asm = CompileStrict(source);
        Assert.Equal(42, Invoke(asm, "Test", "run"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Axis 5: nested choice × choice × Result
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Axis5_NestedChoiceInChoiceInResult_FullMatch()
    {
        // Result<choice<choice>, E> three-level dispatch. Each match arm peels
        // one layer; mistakes here produce wrong-tag reads or boxing.
        const string source = """
namespace Test

union Token {
    word(text: string)
    number(value: int)
}

union Lex {
    one(t: Token)
    pair(a: Token, b: Token)
}

func parse(input: string) -> Result<Lex, string> {
    if input == "fail" { return error("nope") }
    if input == "num" { return ok(Lex.one(Token.number(7))) }
    return ok(Lex.pair(Token.word("hi"), Token.number(9)))
}

func describe(input: string) -> string {
    let r = parse(input)
    match (r: Result<Lex, string>) {
        .ok(lex) {
            match (lex: Lex) {
                .one(o) {
                    match (o.t: Token) {
                        .word(w) { return "one-word:{w.text}" }
                        .number(n) { return "one-num:{n.value}" }
                    }
                }
                .pair(p) {
                    var first = ""
                    match (p.a: Token) {
                        .word(w) { first = "w:{w.text}" }
                        .number(n) { first = "n:{n.value}" }
                    }
                    var second = ""
                    match (p.b: Token) {
                        .word(w) { second = "w:{w.text}" }
                        .number(n) { second = "n:{n.value}" }
                    }
                    return "pair:{first},{second}"
                }
            }
        }
        .err(e) { return "err:{e}" }
    }
    return "?"
}
""";
        var asm = CompileStrict(source);
        Assert.Equal("one-num:7", Invoke(asm, "Test", "describe", "num"));
        Assert.Equal("pair:w:hi,n:9", Invoke(asm, "Test", "describe", "go"));
        Assert.Equal("err:nope", Invoke(asm, "Test", "describe", "fail"));
    }

    [Fact]
    public void Axis5_ResultOfChoice_PropagateThroughQuestion()
    {
        // `?` propagation through a Result whose payload is itself a choice —
        // payload type-arg threading at the unwrap site.
        const string source = """
namespace Test

union Reply {
    accepted(id: int)
    rejected(reason: string)
}

func fetch() -> Result<Reply, string> = ok(Reply.accepted(7))

func process() -> Result<int, string> {
    let r = fetch()?
    match (r: Reply) {
        .accepted(a) { return ok(a.id * 2) }
        .rejected(_) { return error("nope") }
    }
    return error("unreachable")
}
""";
        var asm = CompileStrict(source);
        var result = Invoke(asm, "Test", "process");
        Assert.NotNull(result);
        var isOk = (bool)EsHarness.ResultMember(result, "IsOk")!;
        Assert.True(isOk);
        Assert.Equal(14, (int)EsHarness.ResultMember(result, "Value")!);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Axis 6: derive equality × generic × embedded fields
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Axis6_DeriveEquality_OnTypeWithEmbeddedField()
    {
        // derive equality on a type whose first field is an embedded type:
        // generated Equals must descend into the embedded value's fields.
        const string source = """
namespace Test

struct Vec2 {
    x: int
    y: int
}

derive equality
struct Box {
    Vec2
    tag: int
}

func sameTag() -> bool {
    let a = Box { x: 1, y: 2, tag: 7 }
    let b = Box { x: 1, y: 2, tag: 7 }
    return a.Equals(b)
}

func differingEmbedded() -> bool {
    let a = Box { x: 1, y: 2, tag: 7 }
    let b = Box { x: 99, y: 2, tag: 7 }
    return a.Equals(b)
}
""";
        var asm = CompileStrict(source);
        Assert.Equal(true, Invoke(asm, "Test", "sameTag"));
        Assert.Equal(false, Invoke(asm, "Test", "differingEmbedded"));
    }

    [Fact]
    public void Axis6_DeriveEquality_OnGenericData()
    {
        // derive equality on a generic data type, instantiated with two
        // different concrete types. Each instantiation gets its own Equals.
        const string source = """
namespace Test

derive equality
struct Pair<A, B> {
    first: A
    second: B
}

func intPair() -> bool {
    let a = Pair<int, int> { first: 3, second: 4 }
    let b = Pair<int, int> { first: 3, second: 4 }
    return a.Equals(b)
}

func strPair() -> bool {
    let a = Pair<string, int> { first: "x", second: 1 }
    let b = Pair<string, int> { first: "y", second: 1 }
    return a.Equals(b)
}
""";
        var asm = CompileStrict(source);
        Assert.Equal(true, Invoke(asm, "Test", "intPair"));
        Assert.Equal(false, Invoke(asm, "Test", "strPair"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Axis 7: async let × Result × `?` at await site
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Axis7_AsyncLet_ResultOk_ImplicitAwaitThenUnwrap()
    {
        // Two async lets returning Result<int,string>; consume both via `?`
        // unwrap at the implicit-await site. Both must run concurrently then
        // fold cleanly into the function's own Result.
        const string source = """
namespace Test

func loadAsync(n: int) -> Result<int, string> {
    let v = await Task.FromResult(n)
    if v < 0 { return error("neg:{v}") }
    return ok(v * 10)
}

func combine() -> Result<int, string> {
    async let a = loadAsync(2)
    async let b = loadAsync(3)
    let av = a?
    let bv = b?
    return ok(av + bv)
}
""";
        var asm = CompileStrict(source);
        var r = Invoke(asm, "Test", "combine");
        var unwrapped = Unwrap(r)!;
        var isOk = (bool)EsHarness.ResultMember(unwrapped, "IsOk")!;
        Assert.True(isOk);
        Assert.Equal(50, (int)EsHarness.ResultMember(unwrapped, "Value")!);
    }

    [Fact]
    public void Axis7_AsyncLet_FirstError_ShortCircuitsViaQuestion()
    {
        // First async let succeeds, second errors. The `?` at the second's
        // await site should propagate the error out without touching the
        // success path further.
        const string source = """
namespace Test

func loadAsync(n: int) -> Result<int, string> {
    let v = await Task.FromResult(n)
    if v < 0 { return error("neg") }
    return ok(v)
}

func combine() -> Result<int, string> {
    async let a = loadAsync(5)
    async let b = loadAsync(0 - 1)
    let av = a?
    let bv = b?
    return ok(av + bv)
}
""";
        var asm = CompileStrict(source);
        var unwrapped = Unwrap(Invoke(asm, "Test", "combine"))!;
        var isErr = (bool)EsHarness.ResultMember(unwrapped, "IsError")!;
        Assert.True(isErr);
        Assert.Equal("neg", EsHarness.ResultMember(unwrapped, "Error"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Char literals — independent of the matrix above; pinned here because
    // the JSON parser sample below depends on them.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Char_LiteralEqualityAgainstStringIndex()
    {
        const string source = """
namespace Test

func isSpace(s: string, i: int) -> bool {
    return s[i] == ' '
}

func isQuote(s: string, i: int) -> bool {
    return s[i] == '"'
}

func isNewline(s: string, i: int) -> bool {
    return s[i] == '\n'
}
""";
        var asm = CompileStrict(source);
        Assert.Equal(true, Invoke(asm, "Test", "isSpace", "abc def", 3));
        Assert.Equal(false, Invoke(asm, "Test", "isSpace", "abcdef", 3));
        Assert.Equal(true, Invoke(asm, "Test", "isQuote", "a\"b", 1));
        Assert.Equal(true, Invoke(asm, "Test", "isNewline", "a\nb", 1));
    }

    [Fact]
    public void Char_LetThenCompare()
    {
        const string source = """
namespace Test

func check(s: string, i: int) -> bool {
    let target = 'X'
    return s[i] == target
}
""";
        var asm = CompileStrict(source);
        Assert.Equal(true, Invoke(asm, "Test", "check", "aXb", 1));
        Assert.Equal(false, Invoke(asm, "Test", "check", "abc", 1));
    }

    [Fact]
    public void Char_PassedToBclMethod()
    {
        const string source = """
namespace Test

func isLetterAt(s: string, i: int) -> bool {
    return char.IsLetter(s[i])
}

func isDigitAt(s: string, i: int) -> bool {
    return char.IsDigit(s[i])
}
""";
        var asm = CompileStrict(source);
        Assert.Equal(true, Invoke(asm, "Test", "isLetterAt", "abc", 0));
        Assert.Equal(true, Invoke(asm, "Test", "isDigitAt", "123", 0));
        Assert.Equal(false, Invoke(asm, "Test", "isLetterAt", "123", 0));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Mini end-to-end programs — small but real. Per the plan: "small enough
    // that they run end-to-end in a test, large enough that any single feature
    // regression breaks them visibly."
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void MiniProgram_ConnectionLifecycleStateMachine()
    {
        // Ports the existing StateMachine sample into an executable test plus
        // adds: Result on transitions, *T mutation, match exhaustion.
        const string source = """
namespace Test

union ConnState {
    disconnected
    connecting
    connected(sessionId: int)
    failed(reason: string)
}

struct Client {
    var state: ConnState
    name: string
}

func startConnect(c: *Client) -> Result<int, string> {
    match (c.state: ConnState) {
        .disconnected {
            c.state = ConnState.connecting()
            return ok(1)
        }
        .connecting { return error("already connecting") }
        .connected(sid) { return error("already connected") }
        .failed(reason) { return error("in failed: {reason}") }
    }
    return error("unreachable")
}

func markConnected(c: *Client, sid: int) {
    c.state = ConnState.connected(sid)
}

func markFailed(c: *Client, reason: string) {
    c.state = ConnState.failed(reason)
}

func describe(c: *Client) -> string {
    match (c.state: ConnState) {
        .disconnected { return "disconnected" }
        .connecting { return "connecting" }
        .connected(sid) { return "connected:{sid}" }
        .failed(reason) { return "failed:{reason}" }
    }
    return "?"
}

func run() -> string {
    var c: *Client = new Client { state: ConnState.disconnected(), name: "node-1" }
    let r1 = startConnect(c)
    markConnected(c, 99)
    let mid = describe(c)
    markFailed(c, "timeout")
    let end = describe(c)
    return "{mid}|{end}"
}
""";
        var asm = CompileStrict(source);
        Assert.Equal("connected:99|failed:timeout", Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void MiniProgram_TinyJsonParser_NumbersAndStrings()
    {
        // Hand-rolled parser kernel: scans a tiny subset (whitespace, integer,
        // quoted string), dispatches via ref choice, returns Result. Exercises
        // ref choice + match + Result + `?` + string indexing + closures.
        const string source = """
namespace Test

ref union Token {
    integer(value: int)
    str(text: string)
    eof
}

struct Lex {
    var src: string
    var pos: int
}

func skipSpace(l: *Lex) {
    while l.pos < l.src.Length {
        let c = l.src[l.pos]
        if c == ' ' || c == '\t' || c == '\n' {
            l.pos += 1
        } else {
            return
        }
    }
}

func nextToken(l: *Lex) -> Result<Token, string> {
    skipSpace(l)
    if l.pos >= l.src.Length { return ok(Token.eof()) }
    let c = l.src[l.pos]
    if c == '"' {
        l.pos += 1
        let start = l.pos
        while l.pos < l.src.Length && l.src[l.pos] != '"' {
            l.pos += 1
        }
        if l.pos >= l.src.Length { return error("unterminated string") }
        let text = l.src.Substring(start, l.pos - start)
        l.pos += 1
        return ok(Token.str(text))
    }
    if char.IsDigit(c) {
        let start = l.pos
        while l.pos < l.src.Length && char.IsDigit(l.src[l.pos]) {
            l.pos += 1
        }
        let text = l.src.Substring(start, l.pos - start)
        return ok(Token.integer(int.Parse(text)))
    }
    return error("unexpected char")
}

func sumIntegersUntilEof(input: string) -> Result<int, string> {
    var l: *Lex = new Lex { src: input, pos: 0 }
    var total = 0
    while true {
        let tok = nextToken(l)?
        match (tok: Token) {
            .integer(i) { total += i.value }
            .str(_) { }
            .eof { return ok(total) }
        }
    }
    return ok(total)
}
""";
        var asm = CompileStrict(source);
        var r = Unwrap(Invoke(asm, "Test", "sumIntegersUntilEof", "1 2  3   \"hello\" 4"))!;
        var isOk = (bool)EsHarness.ResultMember(r, "IsOk")!;
        Assert.True(isOk);
        Assert.Equal(10, (int)EsHarness.ResultMember(r, "Value")!);

        var bad = Unwrap(Invoke(asm, "Test", "sumIntegersUntilEof", "\"unterminated"))!;
        var isErr = (bool)EsHarness.ResultMember(bad, "IsError")!;
        Assert.True(isErr);
    }

    [Fact]
    public void MiniProgram_CliArgRouter()
    {
        // Argument router shaped like a real CLI dispatch: choice of Cmd,
        // nested match, defaulted args, Result-flavored errors. Exercises:
        // choice multi-payload, match-as-expression, `?`, Dictionary lookup.
        const string source = """
namespace Test

union Cmd {
    help
    add(a: int, b: int)
    greet(name: string)
    unknown(name: string)
}

func parseCmd(args: List<string>) -> Cmd {
    if args.Count == 0 { return Cmd.help() }
    let head = args[0]
    if head == "help" { return Cmd.help() }
    if head == "add" {
        if args.Count < 3 { return Cmd.unknown("add: need 2 args") }
        return Cmd.add(int.Parse(args[1]), int.Parse(args[2]))
    }
    if head == "greet" {
        let n = args.Count >= 2 ? args[1] : "world"
        return Cmd.greet(n)
    }
    return Cmd.unknown(head)
}

func dispatch(args: List<string>) -> string {
    let c = parseCmd(args)
    match (c: Cmd) {
        .help { return "usage: help|add|greet" }
        .add(a, b) { return "sum={a + b}" }
        .greet(name) { return "hi {name}" }
        .unknown(n) { return "?{n}" }
    }
    return "?"
}

func run1() -> string {
    var args = List<string>()
    args.Add("add")
    args.Add("3")
    args.Add("4")
    return dispatch(args)
}

func run2() -> string {
    var args = List<string>()
    args.Add("greet")
    return dispatch(args)
}

func run3() -> string {
    var args = List<string>()
    args.Add("nope")
    return dispatch(args)
}
""";
        var asm = CompileStrict(source);
        Assert.Equal("sum=7", Invoke(asm, "Test", "run1"));
        Assert.Equal("hi world", Invoke(asm, "Test", "run2"));
        Assert.Equal("?nope", Invoke(asm, "Test", "run3"));
    }
}
