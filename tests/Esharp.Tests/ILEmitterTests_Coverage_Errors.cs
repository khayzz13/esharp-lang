// Style note: E# source uses readable """ raw-string blocks (these double as the
// E# corpus) — do not collapse them into inline \n-escaped one-liners.
namespace Esharp.Tests;

/// Result<T, E> construction and ? propagation, choice-typed errors, the let..else
/// guard, and the Result combinator surface exercised from E# source (not just the
/// C# runtime). Behavioral coverage through the IL backend.
public sealed class ILEmitterTests_Coverage_Errors
{
    static object? Run(string src, string method = "go", params object?[] args)
        => EsHarness.Run(src, method, args);

    // ── ok / error construction, IsOk / Value / Error ──

    [Fact]
    public void Result_OkCarriesValue()
    {
        Assert.Equal(42, Run("""
namespace Test
func parse(s: string) -> Result<int, string> {
    return ok(42)
}
func go() -> int {
    let r = parse("x")
    return r.IsOk ? r.Value : -1
}
"""));
    }

    [Fact]
    public void Result_ErrorCarriesError()
    {
        Assert.Equal("bad", Run("""
namespace Test
func parse(s: string) -> Result<int, string> {
    return error("bad")
}
func go() -> string {
    let r = parse("x")
    return r.IsError ? r.Error : "ok"
}
""", "go"));
    }

    [Fact]
    public void Result_IsOkTrueOnOk()
    {
        Assert.Equal(true, Run("""
namespace Test
func f() -> Result<int, string> { return ok(1) }
func go() -> bool { return f().IsOk }
"""));
    }

    [Fact]
    public void Result_IsErrorTrueOnError()
    {
        Assert.Equal(true, Run("""
namespace Test
func f() -> Result<int, string> { return error("e") }
func go() -> bool { return f().IsError }
"""));
    }

    [Fact]
    public void Result_BranchOnValidation()
    {
        Assert.Equal(7, Run("""
namespace Test
func validate(n: int) -> Result<int, string> {
    if n < 0 { return error("negative") }
    return ok(n)
}
func go() -> int {
    let r = validate(7)
    if r.IsOk { return r.Value }
    return -1
}
"""));
    }

    [Fact]
    public void Result_BranchOnInvalid()
    {
        Assert.Equal(-1, Run("""
namespace Test
func validate(n: int) -> Result<int, string> {
    if n < 0 { return error("negative") }
    return ok(n)
}
func go() -> int {
    let r = validate(-5)
    if r.IsOk { return r.Value }
    return -1
}
"""));
    }

    // ── ? propagation ──

    [Fact]
    public void Propagation_ChainsThroughSuccess()
    {
        Assert.Equal(43, Run("""
namespace Test
func parse(s: string) -> Result<int, string> {
    if s == "" { return error("empty") }
    return ok(int.Parse(s))
}
func chain(s: string) -> Result<int, string> {
    let n = parse(s)?
    return ok(n + 1)
}
func go() -> int {
    let r = chain("42")
    return r.IsOk ? r.Value : -1
}
"""));
    }

    [Fact]
    public void Propagation_ShortCircuitsOnError()
    {
        Assert.Equal("empty", Run("""
namespace Test
func parse(s: string) -> Result<int, string> {
    if s == "" { return error("empty") }
    return ok(int.Parse(s))
}
func chain(s: string) -> Result<int, string> {
    let n = parse(s)?
    return ok(n + 1)
}
func go() -> string {
    let r = chain("")
    return r.IsError ? r.Error : "ok"
}
""", "go"));
    }

    [Fact]
    public void Propagation_TwoStageChain()
    {
        Assert.Equal(20, Run("""
namespace Test
func step1(n: int) -> Result<int, string> {
    if n == 0 { return error("zero") }
    return ok(n * 2)
}
func step2(n: int) -> Result<int, string> {
    return ok(n + 10)
}
func pipeline(n: int) -> Result<int, string> {
    let a = step1(n)?
    let b = step2(a)?
    return ok(b)
}
func go() -> int {
    let r = pipeline(5)
    return r.IsOk ? r.Value : -1
}
"""));
        // 5*2=10, +10=20
    }

    [Fact]
    public void Propagation_SecondStageErrorWins()
    {
        Assert.Equal("zero", Run("""
namespace Test
func step1(n: int) -> Result<int, string> {
    return ok(n)
}
func step2(n: int) -> Result<int, string> {
    if n == 0 { return error("zero") }
    return ok(n + 10)
}
func pipeline(n: int) -> Result<int, string> {
    let a = step1(n)?
    let b = step2(a)?
    return ok(b)
}
func go() -> string {
    let r = pipeline(0)
    return r.IsError ? r.Error : "ok"
}
""", "go"));
    }

    // ── choice-typed errors ──

    [Fact]
    public void Result_ChoiceErrorVariant()
    {
        Assert.Equal("notFound", Run("""
namespace Test
union DbError {
    notFound
    timeout
    connectionFailed(message: string)
}
func lookup(id: int) -> Result<int, DbError> {
    if id == 0 { return error(DbError.notFound()) }
    return ok(id)
}
func go() -> string {
    let r = lookup(0)
    if r.IsError {
        match r.Error {
            .notFound { return "notFound" }
            .timeout { return "timeout" }
            .connectionFailed(c) { return "conn" }
        }
    }
    return "ok"
}
""", "go"));
    }

    [Fact]
    public void Result_ChoiceErrorWithPayload()
    {
        Assert.Equal("conn:down", Run("""
namespace Test
union DbError {
    notFound
    connectionFailed(message: string)
}
func lookup(id: int) -> Result<int, DbError> {
    return error(DbError.connectionFailed("down"))
}
func go() -> string {
    let r = lookup(1)
    if r.IsError {
        match r.Error {
            .notFound { return "notFound" }
            .connectionFailed(c) { return "conn:{c.message}" }
        }
    }
    return "ok"
}
""", "go"));
    }

    [Fact]
    public void Result_DotCaseShorthandError()
    {
        Assert.Equal("notFound", Run("""
namespace Test
union DbError {
    notFound
    timeout
}
func lookup(id: int) -> Result<int, DbError> {
    if id == 0 { return error(.notFound) }
    return ok(id)
}
func go() -> string {
    let r = lookup(0)
    if r.IsError {
        match r.Error {
            .notFound { return "notFound" }
            .timeout { return "timeout" }
        }
    }
    return "ok"
}
""", "go"));
    }

    // ── let..else guard ──

    [Fact]
    public void LetElse_BindsPresentValue()
    {
        Assert.Equal(50, Run("""
namespace Test
func lookup(id: int) -> int? {
    if id == 0 { return nil }
    return 50
}
func go() -> int {
    let v = lookup(3) else { return -1 }
    return v
}
"""));
    }

    [Fact]
    public void LetElse_DivertsOnNil()
    {
        Assert.Equal(-99, Run("""
namespace Test
func lookup(id: int) -> int? {
    if id == 0 { return nil }
    return 50
}
func go() -> int {
    let v = lookup(0) else { return -99 }
    return v
}
"""));
    }

    [Fact]
    public void LetElse_ChainedGuards()
    {
        Assert.Equal(15, Run("""
namespace Test
func first(id: int) -> int? {
    if id < 0 { return nil }
    return 10
}
func second(n: int) -> int? {
    if n == 0 { return nil }
    return n + 5
}
func go() -> int {
    let a = first(2) else { return -1 }
    let b = second(a) else { return -2 }
    return b
}
"""));
    }

    [Fact]
    public void LetElse_SecondGuardDiverts()
    {
        Assert.Equal(-2, Run("""
namespace Test
func first(id: int) -> int? {
    return 0
}
func second(n: int) -> int? {
    if n == 0 { return nil }
    return n + 5
}
func go() -> int {
    let a = first(2) else { return -1 }
    let b = second(a) else { return -2 }
    return b
}
"""));
    }

    // ── combinators called from E# ──

    [Fact]
    public void Combinator_UnwrapOr_Ok()
    {
        Assert.Equal(42, Run("""
namespace Test
func f() -> Result<int, string> { return ok(42) }
func go() -> int { return f().UnwrapOr(-1) }
"""));
    }

    [Fact]
    public void Combinator_UnwrapOr_Error()
    {
        Assert.Equal(-1, Run("""
namespace Test
func f() -> Result<int, string> { return error("bad") }
func go() -> int { return f().UnwrapOr(-1) }
"""));
    }

    [Fact]
    public void Combinator_Unwrap_Ok()
    {
        Assert.Equal(7, Run("""
namespace Test
func f() -> Result<int, string> { return ok(7) }
func go() -> int { return f().Unwrap() }
"""));
    }

    // ── nested Result and Result in data ──

    [Fact]
    public void Result_StoredAndReread()
    {
        Assert.Equal(33, Run("""
namespace Test
func produce(n: int) -> Result<int, string> {
    return ok(n + 3)
}
func go() -> int {
    let r = produce(30)
    let s = produce(r.Value)
    return s.IsOk ? r.Value : -1
}
"""));
    }

    [Fact]
    public void Result_VoidLikeOkUnit()
    {
        Assert.Equal(1, Run("""
namespace Test
func validate(n: int) -> Result<int, string> {
    if n > 0 { return ok(n) }
    return error("non-positive")
}
func go() -> int {
    let r = validate(5)
    return r.IsOk ? 1 : 0
}
"""));
    }

    // ── try / catch / throw at the BCL boundary ──

    [Fact]
    public void TryCatch_RecoversFromParseFailure()
    {
        Assert.Equal(-1, Run("""
namespace Test
func go() -> int {
    var result = 0
    try {
        result = int.Parse("notanumber")
    } catch (e: FormatException) {
        result = -1
    }
    return result
}
"""));
    }

    [Fact]
    public void TryCatch_SuccessPathSkipsCatch()
    {
        Assert.Equal(123, Run("""
namespace Test
func go() -> int {
    var result = 0
    try {
        result = int.Parse("123")
    } catch (e: FormatException) {
        result = -1
    }
    return result
}
"""));
    }

    [Fact]
    public void TryCatch_BareCatchSwallows()
    {
        Assert.Equal(-7, Run("""
namespace Test
func go() -> int {
    var result = 0
    try {
        result = int.Parse("xyz")
    } catch {
        result = -7
    }
    return result
}
"""));
    }

    [Fact]
    public void Throw_CaughtByCallerBoundary()
    {
        Assert.Equal(-42, Run("""
namespace Test
func risky(n: int) -> int {
    if n < 0 { throw InvalidOperationException("negative") }
    return n
}
func go() -> int {
    var result = 0
    try {
        result = risky(-1)
    } catch (e: Exception) {
        result = -42
    }
    return result
}
"""));
    }

    // ── nested try / typed catch / defer cleanup ──

    [Fact]
    public void TryCatch_TypedCatchBindsMessage()
    {
        Assert.Equal("negative", Run("""
namespace Test
func go() -> string {
    var msg = "none"
    try {
        throw InvalidOperationException("negative")
    } catch (e: InvalidOperationException) {
        msg = e.Message
    }
    return msg
}
""", "go"));
    }

    [Fact]
    public void TryCatch_Nested_InnerHandlesFirst()
    {
        Assert.Equal("inner", Run("""
namespace Test
func go() -> string {
    var tag = "none"
    try {
        try {
            throw FormatException("x")
        } catch (e: FormatException) {
            tag = "inner"
        }
    } catch (e: Exception) {
        tag = "outer"
    }
    return tag
}
""", "go"));
    }

    [Fact]
    public void Defer_RunsAsCleanupInTry()
    {
        Assert.Equal(1, Run("""
namespace Test
struct Box { var closed: int }
func go() -> int {
    var b: *Box = new Box { closed: 0 }
    work(b)
    return b.closed
}
func work(b: *Box) {
    defer { b.closed = 1 }
    let n = 5
}
"""));
    }

    [Fact]
    public void Defer_RunsEvenWhenScopeReturnsEarly()
    {
        Assert.Equal(1, Run("""
namespace Test
struct Box { var closed: int }
func go() -> int {
    var b: *Box = new Box { closed: 0 }
    work(b, true)
    return b.closed
}
func work(b: *Box, bail: bool) {
    defer { b.closed = 1 }
    if bail { return }
    b.closed = 99
}
"""));
    }

    // ── deeper validation pipelines ──

    [Fact]
    public void Pipeline_ThreeStageSuccess()
    {
        Assert.Equal(26, Run("""
namespace Test
func parse(s: string) -> Result<int, string> {
    if s == "" { return error("empty") }
    return ok(int.Parse(s))
}
func positive(n: int) -> Result<int, string> {
    if n <= 0 { return error("non-positive") }
    return ok(n)
}
func doubleIt(n: int) -> Result<int, string> {
    return ok(n * 2)
}
func run(s: string) -> Result<int, string> {
    let a = parse(s)?
    let b = positive(a)?
    let c = doubleIt(b)?
    return ok(c + 0)
}
func go() -> int {
    let r = run("13")
    return r.IsOk ? r.Value : -1
}
"""));
    }

    [Fact]
    public void Pipeline_MiddleStageFails()
    {
        Assert.Equal("non-positive", Run("""
namespace Test
func parse(s: string) -> Result<int, string> {
    if s == "" { return error("empty") }
    return ok(int.Parse(s))
}
func positive(n: int) -> Result<int, string> {
    if n <= 0 { return error("non-positive") }
    return ok(n)
}
func run(s: string) -> Result<int, string> {
    let a = parse(s)?
    let b = positive(a)?
    return ok(b)
}
func go() -> string {
    let r = run("0")
    return r.IsError ? r.Error : "ok"
}
""", "go"));
    }

    // ── Result carrying a data payload ──

    [Fact]
    public void Result_OkCarriesDataValue()
    {
        Assert.Equal(30, Run("""
namespace Test
struct User { id: int, age: int }
func load(id: int) -> Result<User, string> {
    if id == 0 { return error("missing") }
    return ok(User { id: id, age: 30 })
}
func go() -> int {
    let r = load(1)
    if r.IsOk { return r.Value.age }
    return -1
}
"""));
    }

    [Fact]
    public void Result_DataPayloadThroughPropagation()
    {
        Assert.Equal(7, Run("""
namespace Test
struct User { id: int, score: int }
func load(id: int) -> Result<User, string> {
    if id == 0 { return error("missing") }
    return ok(User { id: id, score: 7 })
}
func scoreOf(id: int) -> Result<int, string> {
    let u = load(id)?
    return ok(u.score)
}
func go() -> int {
    let r = scoreOf(5)
    return r.IsOk ? r.Value : -1
}
"""));
    }

    // ── nested option-style choice (concrete, non-generic) ──

    [Fact]
    public void OptionInt_SomeUnwraps()
    {
        Assert.Equal(42, Run("""
namespace Test
union OptionInt {
    some(value: int)
    none
}
func get(present: bool) -> OptionInt {
    if present { return OptionInt.some(42) }
    return OptionInt.none()
}
func go() -> int {
    let o = get(true)
    match o {
        .some(v) { return v }
        .none { return -1 }
    }
    return -1
}
"""));
    }

    [Fact]
    public void OptionInt_NoneFallsBack()
    {
        Assert.Equal(-1, Run("""
namespace Test
union OptionInt {
    some(value: int)
    none
}
func get(present: bool) -> OptionInt {
    if present { return OptionInt.some(42) }
    return OptionInt.none()
}
func go() -> int {
    let o = get(false)
    match o {
        .some(v) { return v }
        .none { return -1 }
    }
    return -1
}
"""));
    }

    // ── error-type transformation across functions ──

    [Fact]
    public void Result_MapErrorMessageManually()
    {
        Assert.Equal("wrapped:empty", Run("""
namespace Test
func parse(s: string) -> Result<int, string> {
    if s == "" { return error("empty") }
    return ok(int.Parse(s))
}
func wrap(s: string) -> Result<int, string> {
    let r = parse(s)
    if r.IsError { return error("wrapped:{r.Error}") }
    return ok(r.Value)
}
func go() -> string {
    let r = wrap("")
    return r.IsError ? r.Error : "ok"
}
""", "go"));
    }

    [Fact]
    public void Result_AccumulateOverList()
    {
        Assert.Equal(6, Run("""
namespace Test
func parse(s: string) -> Result<int, string> {
    return ok(int.Parse(s))
}
func sumAll(items: List<string>) -> Result<int, string> {
    var total = 0
    for s in items {
        let n = parse(s)?
        total += n
    }
    return ok(total)
}
func go() -> int {
    let xs = ["1", "2", "3"]
    let r = sumAll(xs)
    return r.IsOk ? r.Value : -1
}
"""));
    }

    [Fact]
    public void Result_EarlyErrorStopsAccumulation()
    {
        Assert.Equal("bad", Run("""
namespace Test
func parse(s: string) -> Result<int, string> {
    if s == "x" { return error("bad") }
    return ok(int.Parse(s))
}
func sumAll(items: List<string>) -> Result<int, string> {
    var total = 0
    for s in items {
        let n = parse(s)?
        total += n
    }
    return ok(total)
}
func go() -> string {
    let xs = ["1", "x", "3"]
    let r = sumAll(xs)
    return r.IsError ? r.Error : "ok"
}
""", "go"));
    }
}
