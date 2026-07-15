// Style note: E# source uses readable """ raw-string blocks (these double as the
// E# corpus) — do not collapse them into inline \n-escaped one-liners.
namespace Esharp.Tests;

/// if/else chains, while, for..in over ranges/collections, nested loops, match
/// (literal patterns + match-as-expression + nested), defer ordering, and the
/// let..else guard. Behavioral coverage through the IL backend.
public sealed class ILEmitterTests_Coverage_ControlFlow
{
    static object? Run(string src, string method = "go", params object?[] args)
        => EsHarness.Run(src, method, args);

    // ── if / else ──

    [Fact]
    public void If_TakesThenBranch()
    {
        Assert.Equal(1, Run("""
namespace Test
func go() -> int {
    if 3 < 4 { return 1 }
    return 0
}
"""));
    }

    [Fact]
    public void If_Else_TakesElseBranch()
    {
        Assert.Equal(0, Run("""
namespace Test
func go() -> int {
    if 4 < 3 { return 1 } else { return 0 }
}
"""));
    }

    [Fact]
    public void IfElseIf_Chain_PicksMiddle()
    {
        Assert.Equal(2, Run("""
namespace Test
func classify(n: int) -> int {
    if n < 0 { return 1 }
    else if n == 0 { return 2 }
    else { return 3 }
}
func go() -> int { return classify(0) }
"""));
    }

    [Fact]
    public void If_Nested()
    {
        Assert.Equal(42, Run("""
namespace Test
func go() -> int {
    let a = 5
    let b = 10
    if a > 0 {
        if b > 0 {
            return 42
        }
    }
    return 0
}
"""));
    }

    [Fact]
    public void If_AsGuard_NoElse_FallsThrough()
    {
        Assert.Equal(7, Run("""
namespace Test
func go() -> int {
    var n = 7
    if n < 0 { n = 0 }
    return n
}
"""));
    }

    // ── while ──

    [Fact]
    public void While_AccumulatesSum()
    {
        Assert.Equal(15, Run("""
namespace Test
func go() -> int {
    var i = 1
    var total = 0
    while i <= 5 {
        total += i
        i += 1
    }
    return total
}
"""));
    }

    [Fact]
    public void While_Countdown()
    {
        Assert.Equal(0, Run("""
namespace Test
func go() -> int {
    var n = 10
    while n > 0 { n -= 1 }
    return n
}
"""));
    }

    [Fact]
    public void While_FalseCondition_NeverRuns()
    {
        Assert.Equal(99, Run("""
namespace Test
func go() -> int {
    var n = 99
    while false { n = 0 }
    return n
}
"""));
    }

    [Fact]
    public void While_NestedMultiplicationTable()
    {
        Assert.Equal(225, Run("""
namespace Test
func go() -> int {
    var i = 1
    var sum = 0
    while i <= 5 {
        var j = 1
        while j <= 5 {
            sum += i * j
            j += 1
        }
        i += 1
    }
    return sum
}
"""));
        // (1+2+3+4+5) * (1+2+3+4+5) = 15 * 15 = 225
    }

    [Fact]
    public void While_TrueWithEarlyReturn()
    {
        Assert.Equal(8, Run("""
namespace Test
func go() -> int {
    var n = 0
    while true {
        n += 2
        if n >= 8 { return n }
    }
    return -1
}
"""));
    }

    // ── for..in ──

    [Fact]
    public void For_Range_Exclusive()
    {
        Assert.Equal(10, Run("""
namespace Test
func go() -> int {
    var total = 0
    for i in 0..5 {
        total += i
    }
    return total
}
"""));
        // 0+1+2+3+4 = 10 (exclusive end)
    }

    [Fact]
    public void For_Range_WithVariableBounds()
    {
        Assert.Equal(7, Run("""
namespace Test
func sumRange(lo: int, hi: int) -> int {
    var total = 0
    for i in lo..hi {
        total += i
    }
    return total
}
func go() -> int { return sumRange(3, 5) }
"""));
        // 3 + 4 = 7
    }

    [Fact]
    public void For_OverListLiteral()
    {
        Assert.Equal(60, Run("""
namespace Test
func go() -> int {
    var total = 0
    for x in [10, 20, 30] {
        total += x
    }
    return total
}
"""));
    }

    [Fact]
    public void For_Nested_Ranges()
    {
        Assert.Equal(9, Run("""
namespace Test
func go() -> int {
    var count = 0
    for i in 0..3 {
        for j in 0..3 {
            count += 1
        }
    }
    return count
}
"""));
    }

    [Fact]
    public void For_RangeBuildsProduct()
    {
        Assert.Equal(120, Run("""
namespace Test
func go() -> int {
    var product = 1
    for i in 1..6 {
        product *= i
    }
    return product
}
"""));
        // 5! = 120
    }

    // ── match: literal patterns ──

    [Fact]
    public void Match_IntLiteral()
    {
        Assert.Equal("not found", Run("""
namespace Test
func describe(code: int) -> string {
    match code {
        200 { return "ok" }
        404 { return "not found" }
        500 { return "server error" }
        default { return "unknown" }
    }
}
func go() -> string { return describe(404) }
""", "go"));
    }

    [Fact]
    public void Match_IntLiteral_DefaultArm()
    {
        Assert.Equal("unknown", Run("""
namespace Test
func describe(code: int) -> string {
    match code {
        200 { return "ok" }
        default { return "unknown" }
    }
}
func go() -> string { return describe(301) }
""", "go"));
    }

    [Fact]
    public void Match_StringLiteral()
    {
        Assert.Equal("admin-perms", Run("""
namespace Test
func perms(role: string) -> string {
    match role {
        "admin" { return "admin-perms" }
        "guest" { return "guest-perms" }
        default { return "none" }
    }
}
func go() -> string { return perms("admin") }
""", "go"));
    }

    [Fact]
    public void Match_BoolLiteral()
    {
        Assert.Equal("on", Run("""
namespace Test
func label(flag: bool) -> string {
    match flag {
        true { return "on" }
        false { return "off" }
    }
}
func go() -> string { return label(true) }
""", "go"));
    }

    [Fact]
    public void Match_NegativeIntLiteral()
    {
        Assert.Equal("neg", Run("""
namespace Test
func sign(n: int) -> string {
    match n {
        -1 { return "neg" }
        0 { return "zero" }
        1 { return "pos" }
        default { return "?" }
    }
}
func go() -> string { return sign(-1) }
""", "go"));
    }

    // ── match as expression ──

    [Fact]
    public void Match_AsExpression_InLet()
    {
        Assert.Equal("not found", Run("""
namespace Test
func go() -> string {
    let status = 404
    let label = match status {
        200 { "ok" }
        404 { "not found" }
        default { "other" }
    }
    return label
}
""", "go"));
    }

    [Fact]
    public void Match_AsExpression_InInterpolation()
    {
        Assert.Equal("code=ok", Run("""
namespace Test
func go() -> string {
    let status = 200
    return "code={match status { 200 { "ok" } default { "?" } }}"
}
""", "go"));
    }

    [Fact]
    public void Match_AsExpression_ExpressionBodiedFunc()
    {
        Assert.Equal("two", Run("""
namespace Test
func name(n: int) -> string = match n {
    1 { "one" }
    2 { "two" }
    default { "many" }
}
func go() -> string { return name(2) }
""", "go"));
    }

    [Fact]
    public void Match_Nested()
    {
        Assert.Equal("a-inner", Run("""
namespace Test
func go() -> string {
    let outer = "a"
    let inner = 1
    match outer {
        "a" {
            match inner {
                1 { return "a-inner" }
                default { return "a-other" }
            }
        }
        default { return "other" }
    }
    return "?"
}
""", "go"));
    }

    // ── defer ──

    [Fact]
    public void Defer_LifoOrder()
    {
        Assert.Equal(12, Run("""
namespace Test
struct Box { var n: int }
func go() -> int {
    var b: *Box = new Box { n: 2 }
    apply(b)
    return b.n
}
func apply(b: *Box) {
    defer { b.n += 2 }
    defer { b.n *= 5 }
}
"""));
        // n=2 -> *5 = 10 -> +2 = 12 (LIFO)
    }

    [Fact]
    public void Defer_RunsOnEarlyReturn()
    {
        Assert.Equal(1, Run("""
namespace Test
struct Box { var n: int }
func go() -> int {
    var b: *Box = new Box { n: 0 }
    apply(b, true)
    return b.n
}
func apply(b: *Box, early: bool) {
    defer { b.n += 1 }
    if early { return }
    b.n += 100
}
"""));
    }

    [Fact]
    public void Defer_ThreeInReverseOrder()
    {
        Assert.Equal("321", Run("""
namespace Test
struct Buf { var s: string }
func go() -> string {
    var b: *Buf = new Buf { s: "" }
    apply(b)
    return b.s
}
func apply(b: *Buf) {
    defer { b.s = b.s + "1" }
    defer { b.s = b.s + "2" }
    defer { b.s = b.s + "3" }
}
""", "go"));
    }

    // ── let..else guard ──

    [Fact]
    public void LetElse_BindsOnPresent()
    {
        Assert.Equal(5, Run("""
namespace Test
func find(id: int) -> int? {
    if id == 0 { return nil }
    return 5
}
func go() -> int {
    let v = find(1) else { return -1 }
    return v
}
"""));
    }

    [Fact]
    public void LetElse_TakesElseOnNil()
    {
        Assert.Equal(-1, Run("""
namespace Test
func find(id: int) -> int? {
    if id == 0 { return nil }
    return 5
}
func go() -> int {
    let v = find(0) else { return -1 }
    return v
}
"""));
    }

    // ── ternary ──

    [Fact]
    public void Ternary_SelectsBranch()
    {
        Assert.Equal(9, Run("""
namespace Test
func go() -> int {
    let x = -9
    return x > 0 ? x : 0 - x
}
"""));
    }

    [Fact]
    public void Ternary_Nested()
    {
        Assert.Equal("zero", Run("""
namespace Test
func sign(n: int) -> string {
    return n > 0 ? "pos" : (n < 0 ? "neg" : "zero")
}
func go() -> string { return sign(0) }
""", "go"));
    }

    // ── match over enum ──

    [Fact]
    public void Match_Enum_BasicDispatch()
    {
        Assert.Equal("S", Run("""
namespace Test
enum Direction { north, south, east, west }
func glyph(d: Direction) -> string {
    match (d: Direction) {
        .north { return "N" }
        .south { return "S" }
        .east { return "E" }
        .west { return "W" }
    }
    return "?"
}
func go() -> string { return glyph(Direction.south()) }
""", "go"));
    }

    [Fact]
    public void Match_Enum_WithDefaultArm()
    {
        Assert.Equal("other", Run("""
namespace Test
enum Size { small, medium, large }
func label(s: Size) -> string {
    match (s: Size) {
        .small { return "S" }
        default { return "other" }
    }
}
func go() -> string { return label(Size.large()) }
""", "go"));
    }

    [Fact]
    public void Match_Enum_ExplicitValues()
    {
        Assert.Equal("warn", Run("""
namespace Test
enum Code { ok = 0, warn = 10, fail = 20 }
func name(c: Code) -> string {
    match (c: Code) {
        .ok { return "ok" }
        .warn { return "warn" }
        .fail { return "fail" }
        default { return "?" }
    }
}
func go() -> string { return name(Code.warn()) }
""", "go"));
    }

    [Fact]
    public void Match_Enum_AsExpression()
    {
        Assert.Equal("N", Run("""
namespace Test
enum Dir { north, south }
func go() -> string {
    let d = Dir.north()
    let name = match (d: Dir) {
        .north { "N" }
        .south { "S" }
        default { "?" }
    }
    return name
}
""", "go"));
    }

    // ── match over choice (value tagged union) ──

    [Fact]
    public void Match_Choice_ZeroPayload()
    {
        Assert.Equal("a", Run("""
namespace Test
union Cmd {
    help
    quit
}
func go() -> string {
    let c = Cmd.help()
    match c {
        .help { return "a" }
        .quit { return "b" }
    }
    return "?"
}
""", "go"));
    }

    [Fact]
    public void Match_Choice_SinglePayloadBinding()
    {
        Assert.Equal(42, Run("""
namespace Test
union Box {
    empty
    full(value: int)
}
func go() -> int {
    let b = Box.full(42)
    match b {
        .empty { return 0 }
        .full(v) { return v }
    }
    return -1
}
"""));
    }

    [Fact]
    public void Match_Choice_MultiPayloadDestructure()
    {
        Assert.Equal("[3] hi", Run("""
namespace Test
union LogEntry {
    message(level: int, text: string)
    blank
}
func go() -> string {
    let e = LogEntry.message(3, "hi")
    match e {
        .message(lvl, txt) { return "[{lvl}] {txt}" }
        .blank { return "" }
    }
    return "?"
}
""", "go"));
    }

    [Fact]
    public void Match_Choice_CaseViewProjection()
    {
        Assert.Equal(7, Run("""
namespace Test
union Pair {
    one(a: int)
    two(a: int, b: int)
}
func go() -> int {
    let p = Pair.two(3, 4)
    match p {
        .one(o) { return o.a }
        .two(t) { return t.a + t.b }
    }
    return -1
}
"""));
    }

    [Fact]
    public void Match_Choice_AsExpression()
    {
        Assert.Equal("sum=7", Run("""
namespace Test
union Cmd {
    add(a: int, b: int)
    nop
}
func go() -> string {
    let c = Cmd.add(3, 4)
    return match c {
        .add(a, b) { "sum={a + b}" }
        .nop { "noop" }
    }
}
""", "go"));
    }

    [Fact]
    public void Match_Choice_GenericOption()
    {
        Assert.Equal(99, Run("""
namespace Test
union Option<T> {
    some(value: T)
    none
}
func unwrapOr(o: Option<int>, fallback: int) -> int {
    match o {
        .some(v) { return v }
        .none { return fallback }
    }
    return fallback
}
func go() -> int {
    let o = Option<int>.some(99)
    return unwrapOr(o, -1)
}
"""));
    }

    [Fact]
    public void Match_Choice_GenericOption_NoneFallback()
    {
        Assert.Equal(-1, Run("""
namespace Test
union Option<T> {
    some(value: T)
    none
}
func unwrapOr(o: Option<int>, fallback: int) -> int {
    match o {
        .some(v) { return v }
        .none { return fallback }
    }
    return fallback
}
func go() -> int {
    let o = Option<int>.none()
    return unwrapOr(o, -1)
}
"""));
    }

    // ── match over ref choice (sealed hierarchy) ──

    [Fact]
    public void Match_RefChoice_RecursiveEval()
    {
        Assert.Equal(14, Run("""
namespace Test
ref union Expr {
    literal(value: int)
    add(left: Expr, right: Expr)
    mul(left: Expr, right: Expr)
}
func eval(e: Expr) -> int {
    match e {
        .literal(lit) { return lit.value }
        .add(a) { return eval(a.left) + eval(a.right) }
        .mul(m) { return eval(m.left) * eval(m.right) }
    }
    return 0
}
func go() -> int {
    let tree = Expr.add(Expr.literal(2), Expr.mul(Expr.literal(3), Expr.literal(4)))
    return eval(tree)
}
"""));
        // 2 + (3 * 4) = 14
    }

    [Fact]
    public void Match_RefChoice_CompositeLiteralConstruction()
    {
        Assert.Equal(7, Run("""
namespace Test
ref union Expr {
    literal(value: int)
    add(left: Expr, right: Expr)
}
func eval(e: Expr) -> int {
    match e {
        .literal(lit) { return lit.value }
        .add(a) { return eval(a.left) + eval(a.right) }
    }
    return 0
}
func go() -> int {
    let sum = Expr_add {
        left: Expr_literal { value: 3 }
        right: Expr_literal { value: 4 }
    }
    return eval(sum)
}
"""));
    }

    // ── loops driving choice/data state ──

    [Fact]
    public void For_AccumulatesIntoData()
    {
        Assert.Equal(45, Run("""
namespace Test
struct Acc { var total: int }
func go() -> int {
    var a = Acc { total: 0 }
    for i in 0..10 {
        a.total += i
    }
    return a.total
}
"""));
    }

    [Fact]
    public void While_BuildsString()
    {
        Assert.Equal("aaa", Run("""
namespace Test
func go() -> string {
    var s = ""
    var n = 3
    while n > 0 {
        s = s + "a"
        n -= 1
    }
    return s
}
""", "go"));
    }

    [Fact]
    public void For_OverString_CountsVowels()
    {
        Assert.Equal(2, Run("""
namespace Test
func go() -> int {
    let s = "hello"
    var vowels = 0
    for c in s {
        if c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u' {
            vowels += 1
        }
    }
    return vowels
}
"""));
    }

    [Fact]
    public void Match_OnLoopIndex_Categorizes()
    {
        Assert.Equal(4, Run("""
namespace Test
func go() -> int {
    var evens = 0
    for i in 0..8 {
        match i % 2 {
            0 { evens += 1 }
            default { }
        }
    }
    return evens
}
"""));
        // 0,2,4,6 → 4 evens in 0..8
    }

    // ── defer interacting with control flow ──

    [Fact]
    public void Defer_RunsAfterLoopBody()
    {
        Assert.Equal(20, Run("""
namespace Test
struct Box { var n: int }
func go() -> int {
    var b: *Box = new Box { n: 0 }
    apply(b)
    return b.n
}
func apply(b: *Box) {
    defer { b.n *= 2 }
    for i in 1..5 {
        b.n += i
    }
}
"""));
        // loop 1+2+3+4 = 10, then defer *= 2 at scope exit = 20
    }

    [Fact]
    public void Defer_NestedScopes()
    {
        Assert.Equal("inner-outer", Run("""
namespace Test
struct Buf { var s: string }
func go() -> string {
    var b: *Buf = new Buf { s: "" }
    outer(b)
    return b.s
}
func inner(b: *Buf) {
    defer { b.s = b.s + "inner-" }
}
func outer(b: *Buf) {
    defer { b.s = b.s + "outer" }
    inner(b)
}
""", "go"));
    }

    // ── ?? and ?. in control flow ──

    [Fact]
    public void NullCoalescing_ChainPicksFirstNonNull()
    {
        Assert.Equal("b", Run("""
namespace Test
func pick(a: string?, b: string?) -> string {
    return a ?? b ?? "fallback"
}
func go() -> string { return pick(nil, "b") }
""", "go"));
    }

    [Fact]
    public void NullCoalescing_AllNilUsesFallback()
    {
        Assert.Equal("fallback", Run("""
namespace Test
func pick(a: string?, b: string?) -> string {
    return a ?? b ?? "fallback"
}
func go() -> string { return pick(nil, nil) }
""", "go"));
    }

    [Fact]
    public void NullConditional_OnNilYieldsFallback()
    {
        Assert.Equal(0, Run("""
namespace Test
func go() -> int {
    let s: string? = nil
    return s?.Length ?? 0
}
"""));
    }

    [Fact]
    public void NullConditional_OnPresentReadsMember()
    {
        Assert.Equal(5, Run("""
namespace Test
func go() -> int {
    let s: string? = "hello"
    return s?.Length ?? 0
}
"""));
    }

    // ── flag-driven loop exit (no break keyword) ──

    [Fact]
    public void While_FlagBasedExit()
    {
        Assert.Equal(4, Run("""
namespace Test
func firstSquareOver(limit: int) -> int {
    var i = 0
    var done = false
    var answer = -1
    while !done {
        if i * i > limit {
            answer = i
            done = true
        }
        i += 1
    }
    return answer
}
func go() -> int { return firstSquareOver(10) }
"""));
        // 4*4=16 > 10, 3*3=9 not; first is 4
    }

    [Fact]
    public void For_EarlyReturnFromLoop()
    {
        Assert.Equal(3, Run("""
namespace Test
func indexOf(xs: List<int>, target: int) -> int {
    var i = 0
    for x in xs {
        if x == target { return i }
        i += 1
    }
    return -1
}
func go() -> int {
    let xs = [10, 20, 30, 40]
    return indexOf(xs, 40)
}
"""));
    }

    [Fact]
    public void For_NotFoundReturnsSentinel()
    {
        Assert.Equal(-1, Run("""
namespace Test
func indexOf(xs: List<int>, target: int) -> int {
    var i = 0
    for x in xs {
        if x == target { return i }
        i += 1
    }
    return -1
}
func go() -> int {
    let xs = [10, 20, 30]
    return indexOf(xs, 99)
}
"""));
    }

    [Fact]
    public void Loop_AccumulateMaxValue()
    {
        Assert.Equal(40, Run("""
namespace Test
func go() -> int {
    let xs = [10, 40, 25, 5, 30]
    var best = xs[0]
    for x in xs {
        if x > best { best = x }
    }
    return best
}
"""));
    }

    [Fact]
    public void Nested_LoopWithMatchClassifier()
    {
        Assert.Equal(3, Run("""
namespace Test
func classify(n: int) -> int {
    return match n % 3 {
        0 { 0 }
        1 { 1 }
        default { 2 }
    }
}
func go() -> int {
    var twos = 0
    for i in 0..9 {
        if classify(i) == 2 { twos += 1 }
    }
    return twos
}
"""));
        // classify==2 (n%3==2) for 2,5,8 in 0..9 → 3
    }

    [Fact]
    public void If_ReturnsValueFromBothBranches()
    {
        Assert.Equal(100, Run("""
namespace Test
func pickLarger(a: int, b: int) -> int {
    if a > b { return a } else { return b }
}
func go() -> int { return pickLarger(100, 50) }
"""));
    }

    [Fact]
    public void Ternary_DrivesCompoundAssign()
    {
        Assert.Equal(8, Run("""
namespace Test
func go() -> int {
    var n = 4
    n += n > 0 ? 4 : -4
    return n
}
"""));
    }

    // === added: control flow over pointers + values ===

    [Fact]
    public void Added_While_BuildPointerListAndSum() => Assert.Equal(10, Run("""
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
"""));

    [Fact]
    public void Added_For_RangeSum() => Assert.Equal(10, Run("""
namespace Test
func go() -> int {
    var t = 0
    for i in 0..5 { t += i }
    return t
}
"""));

    [Fact]
    public void Added_For_RangeWithPointerWork() => Assert.Equal(6, Run("""
namespace Test
struct Acc { total: int }
func go() -> int {
    var a = new Acc { total: 0 }
    for i in 1..4 { a.total += i }
    return a.total
}
"""));

    [Fact]
    public void Added_NestedWhile_Product() => Assert.Equal(12, Run("""
namespace Test
func go() -> int {
    var t = 0
    var i = 0
    while i < 3 {
        var j = 0
        while j < 4 {
            t += 1
            j += 1
        }
        i += 1
    }
    return t
}
"""));

    [Fact]
    public void Added_If_ElseIf_Chain() => Assert.Equal(2, Run("""
namespace Test
func classify(n: int) -> int {
    if n < 0 { return 0 }
    else if n == 0 { return 1 }
    else { return 2 }
}
func go() -> int { return classify(7) }
"""));

    [Fact]
    public void Added_Match_OnChoice() => Assert.Equal(5, Run("""
namespace Test
union Shape { circle(r: int), square(s: int) }
func area(sh: Shape) -> int {
    match sh {
        .circle(r) { return r }
        .square(s) { return s }
    }
    return 0
}
func go() -> int { return area(Shape.square(5)) }
"""));

    [Fact]
    public void Added_Match_PointerMutateInArm() => Assert.Equal(7, Run("""
namespace Test
struct Box { n: int }
union Sel { a, b }
func go() -> int {
    let s = Sel.a()
    var box = new Box { n: 0 }
    match s {
        .a { box.n = 7 }
        .b { box.n = 9 }
    }
    return box.n
}
"""));

    [Fact]
    public void Added_Defer_RunsInReverse() => Assert.Equal(8, Run("""
namespace Test
struct Acc { var v: int }
func work(a: *Acc) {
    defer { a.v += 2 }
    defer { a.v *= 6 }
    a.v = 1
}
func go() -> int {
    // defers run LIFO after the function body: *6 then +2 → (1*6)+2 = 8.
    var a = new Acc { v: 0 }
    work(a)
    return a.v
}
"""));

    [Fact]
    public void Added_ForIn_OverPointerList_Count() => Assert.Equal(3, Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let xs = [new Box { n: 1 }, new Box { n: 2 }, new Box { n: 3 }]
    var c = 0
    for b in xs { c += 1 }
    return c
}
"""));

    [Fact]
    public void Added_While_EarlyReturnViaPointer() => Assert.Equal(2, Run("""
namespace Test
struct Node { value: int, next: *Node }
func go() -> int {
    var head: *Node = new Node { value: 1, next: new Node { value: 2, next: nil } }
    var cur = head
    while cur != nil {
        if cur.value == 2 { return cur.value }
        cur = cur.next
    }
    return -1
}
"""));

    [Fact]
    public void Added_Ternary_NestedPointer() => Assert.Equal(2, Run("""
namespace Test
struct Box { n: int }
func go() -> int {
    let pick = true
    let b = pick ? new Box { n: 2 } : new Box { n: 9 }
    return b.n
}
"""));

    [Fact]
    public void Added_For_AccumulateString() => Assert.Equal("aaa", Run("""
namespace Test
func go() -> string {
    var s = ""
    for i in 0..3 { s = s + "a" }
    return s
}
"""));

    [Fact]
    public void Added_Match_Default() => Assert.Equal(99, Run("""
namespace Test
union Sel { a, b, c }
func pick(s: Sel) -> int {
    match s {
        .a { return 1 }
        default { return 99 }
    }
    return 0
}
func go() -> int { return pick(Sel.c()) }
"""));

    [Fact]
    public void Added_If_PointerNilGuard() => Assert.Equal(0, Run("""
namespace Test
struct Node { value: int, next: *Node }
func go() -> int {
    var head: *Node = nil
    if head == nil { return 0 }
    return head.value
}
"""));

    [Fact]
    public void Added_CompoundAssign_AllOps() => Assert.Equal(7, Run("""
namespace Test
func go() -> int {
    var n = 10
    n += 4
    n -= 2
    n *= 1
    n /= 1
    return n - 5
}
"""));

    [Fact]
    public void Added_While_DecrementToZero() => Assert.Equal(0, Run("""
namespace Test
func go() -> int {
    var n = 5
    while n > 0 { n -= 1 }
    return n
}
"""));
}
