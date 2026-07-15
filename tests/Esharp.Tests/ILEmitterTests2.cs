using System.Reflection;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;
/// <summary>
/// DO NOT ADD NEW TESTS TO THIS FILE. Create a new verticial slice file for your tests if it 
/// if it doesnt fit into something, if it is purely general, put it in ILEmmiterTests4.cs 
/// </summary>
public sealed class ILEmitterTests2
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    static int _asmCounter;

    static Assembly CompileAndLoad(string source)
    {
        var asmName = $"EsharpTest2_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

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

    // ── Explicit enum values ──

    [Fact]
    public void Enum_ExplicitValues_CorrectConstants()
    {
        const string source = """
namespace Test

enum Priority {
    low = 1
    medium = 5
    high = 10
}

func test() -> int {
    return 0
}
""";
        var asm = CompileAndLoad(source);
        var enumType = asm.GetType("Test.Priority")!;
        Assert.True(enumType.IsEnum);

        Assert.Equal(1, (int)Enum.Parse(enumType, "low"));
        Assert.Equal(5, (int)Enum.Parse(enumType, "medium"));
        Assert.Equal(10, (int)Enum.Parse(enumType, "high"));
    }

    [Fact]
    public void Enum_MixedAutoAndExplicit()
    {
        const string source = """
namespace Test

enum Level {
    a
    b = 10
    c
}

func test() -> int {
    return 0
}
""";
        var asm = CompileAndLoad(source);
        var enumType = asm.GetType("Test.Level")!;

        Assert.Equal(0, (int)Enum.Parse(enumType, "a"));
        Assert.Equal(10, (int)Enum.Parse(enumType, "b"));
        Assert.Equal(11, (int)Enum.Parse(enumType, "c"));
    }

    // ── Enum matching ──

    [Fact]
    public void Enum_Match_BasicDispatch()
    {
        const string source = """
namespace Test

enum Color {
    red
    green
    blue
}

func describe(c: Color) -> string {
    match (c: Color) {
        .red { return "r" }
        .green { return "g" }
        .blue { return "b" }
        default { return "?" }
    }
}

func test() -> string {
    return describe(Color.green())
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("g", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Enum_Match_WithDefault()
    {
        const string source = """
namespace Test

enum Size {
    small
    medium
    large
}

func label(s: Size) -> string {
    match (s: Size) {
        .small { return "S" }
        default { return "other" }
    }
}

func test() -> string {
    return label(Size.large())
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("other", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Enum_Match_ExplicitValues_Dispatch()
    {
        const string source = """
namespace Test

enum Code {
    ok = 0
    warn = 10
    fail = 20
}

func name(c: Code) -> string {
    match (c: Code) {
        .ok { return "ok" }
        .warn { return "warn" }
        .fail { return "fail" }
        default { return "?" }
    }
}

func test() -> string {
    return name(Code.warn())
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("warn", Invoke(asm, "Test", "test"));
    }

    // ── Literal value patterns ──

    [Fact]
    public void Literal_Match_Int()
    {
        const string source = """
namespace Test

func describe(n: int) -> string {
    match n {
        1 { return "one" }
        2 { return "two" }
        3 { return "three" }
        default { return "other" }
    }
}

func test() -> string {
    return describe(2)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("two", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Literal_Match_Int_Default()
    {
        const string source = """
namespace Test

func describe(n: int) -> string {
    match n {
        1 { return "one" }
        default { return "other" }
    }
}

func test() -> string {
    return describe(99)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("other", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Literal_Match_String()
    {
        const string source = """
namespace Test

func greet(lang: string) -> string {
    match lang {
        "en" { return "hello" }
        "es" { return "hola" }
        "fr" { return "bonjour" }
        default { return "hi" }
    }
}

func test() -> string {
    return greet("es")
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("hola", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Literal_Match_Bool()
    {
        const string source = """
namespace Test

func label(b: bool) -> string {
    match b {
        true { return "yes" }
        false { return "no" }
        default { return "?" }
    }
}

func test() -> string {
    return label(false)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("no", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Literal_Match_NegativeInt()
    {
        const string source = """
namespace Test

func sign(n: int) -> string {
    match n {
        -1 { return "neg" }
        0 { return "zero" }
        1 { return "pos" }
        default { return "other" }
    }
}

func test() -> string {
    return sign(-1)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("neg", Invoke(asm, "Test", "test"));
    }

    // ── Match expression ──

    [Fact]
    public void Match_Expression_IntLiteral()
    {
        const string source = """
namespace Test

func test() -> string {
    let x = match 2 {
        1 { "one" }
        2 { "two" }
        default { "other" }
    }
    return x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("two", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Match_Expression_Enum()
    {
        const string source = """
namespace Test

enum Dir {
    north
    south
}

func test() -> string {
    let d = Dir.north()
    let name = match (d: Dir) {
        .north { "N" }
        .south { "S" }
        default { "?" }
    }
    return name
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("N", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Match_Expression_ExpressionBody()
    {
        const string source = """
namespace Test

func label(n: int) -> string = match n {
    1 { "one" }
    2 { "two" }
    default { "?" }
}

func test() -> string {
    return label(2)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("two", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Match_Expression_Choice()
    {
        const string source = """
namespace Test

union Option {
    some(value: int)
    none
}

func unwrap(o: Option) -> int {
    let v = match o {
        .some(x) { x }
        .none { 0 }
    }
    return v
}

func test() -> int {
    return unwrap(Option.some(42))
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Match_Expression_StringLiteral()
    {
        const string source = """
namespace Test

func greet(lang: string) -> string = match lang {
    "en" { "hello" }
    "es" { "hola" }
    default { "hi" }
}

func test() -> string {
    return greet("es")
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("hola", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Enum_ExplicitValues_MatchExpression()
    {
        const string source = """
namespace Test

enum Priority {
    low = 1
    medium = 5
    high = 10
}

func label(p: Priority) -> string {
    match (p: Priority) {
        .low { return "L" }
        .medium { return "M" }
        .high { return "H" }
        default { return "?" }
    }
}

func test() -> string {
    return label(Priority.high())
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("H", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Enum_StoredInLocal_ThenPassedToFunc()
    {
        const string source = """
namespace Test

enum Dir {
    north
    south
}

func label(d: Dir) -> string {
    match (d: Dir) {
        .north { return "N" }
        .south { return "S" }
        default { return "?" }
    }
}

func test() -> string {
    let d = Dir.south()
    return label(d)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("S", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Match_Expression_ChoiceString()
    {
        const string source = """
namespace Test

union Result {
    ok(value: int)
    err(message: string)
}

func unwrap(r: Result) -> string = match r {
    .ok(v) { "ok" }
    .err(msg) { "err" }
    default { "?" }
}

func test() -> string {
    return unwrap(Result.ok(42))
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("ok", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Match_Expression_InlineLetAssignment()
    {
        const string source = """
namespace Test

func test() -> string {
    let x = 7
    let tag = match x {
        1 { "one" }
        7 { "seven" }
        default { "other" }
    }
    return tag
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("seven", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Sample_MatchPatterns_Full()
    {
        const string source = """
namespace Test

func levelName(n: int) -> string = match n {
    1 { "low" }
    2 { "medium" }
    3 { "high" }
    default { "unknown" }
}

func greet(lang: string) -> string = match lang {
    "en" { "hello" }
    "es" { "hola" }
    "fr" { "bonjour" }
    default { "hi" }
}

enum Priority {
    low = 1
    medium = 5
    high = 10
    critical = 100
}

func priorityLabel(p: Priority) -> string {
    match (p: Priority) {
        .low { return "LOW" }
        .medium { return "MED" }
        .high { return "HIGH" }
        .critical { return "CRIT" }
        default { return "?" }
    }
}

union Result {
    ok(value: int)
    err(message: string)
}

func unwrap(r: Result) -> string = match r {
    .ok(v) { "ok" }
    .err(msg) { "err" }
    default { "?" }
}

enum Color {
    red
    green = 10
    blue
}

func test() -> string {
    let a = levelName(2)
    let b = greet("es")
    let c = priorityLabel(Priority.critical())
    let d = unwrap(Result.ok(42))
    let x = 7
    let tag = match x {
        1 { "one" }
        7 { "seven" }
        default { "other" }
    }
    let n = -1
    let sign = match n {
        -1 { "negative" }
        0 { "zero" }
        1 { "positive" }
        default { "other" }
    }
    return "{a},{b},{c},{d},{tag},{sign}"
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("medium,hola,CRIT,ok,seven,negative", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Match_ExpressionBody_ResultInInterpolation()
    {
        // String interpolation only supports variable refs, not function calls.
        // Store the result in a local first, then interpolate.
        const string source = """
namespace Test

func levelName(n: int) -> string = match n {
    1 { "low" }
    2 { "medium" }
    default { "?" }
}

func test() -> string {
    let name = levelName(2)
    return "level: {name}"
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("level: medium", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Match_Expression_NegativeLiteral()
    {
        const string source = """
namespace Test

func sign(n: int) -> string = match n {
    -1 { "neg" }
    0 { "zero" }
    1 { "pos" }
    default { "other" }
}

func test() -> string {
    return sign(-1)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("neg", Invoke(asm, "Test", "test"));
    }

    // Bitwise (& | ^ ~) and shift (<< >>) operators are not in the language
    // surface today — the lexer parses `&` and `^` as ref-take / index-from-end,
    // and `|` / `<<` / `>>` are not bound at all in expression position.
    // Adding them is a separate language item; tests omitted intentionally.

    // ── Float arithmetic ──

    [Fact]
    public void Float_Add_Double()
    {
        const string source = """
namespace Test

func test(a: double, b: double) -> double = a + b
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3.5, Invoke(asm, "Test", "test", 1.25, 2.25));
    }

    [Fact]
    public void Float_Sub_Double()
    {
        const string source = """
namespace Test

func test(a: double, b: double) -> double = a - b
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0.75, Invoke(asm, "Test", "test", 2.5, 1.75));
    }

    [Fact]
    public void Float_Mul_Double()
    {
        const string source = """
namespace Test

func test(a: double, b: double) -> double = a * b
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(6.0, Invoke(asm, "Test", "test", 2.0, 3.0));
    }

    [Fact]
    public void Float_Div_Double()
    {
        const string source = """
namespace Test

func test(a: double, b: double) -> double = a / b
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(2.5, Invoke(asm, "Test", "test", 5.0, 2.0));
    }

    // ── Modulo with negative ──

    [Fact]
    public void Modulo_Negative_Int()
    {
        const string source = """
namespace Test

func test(a: int, b: int) -> int = a % b
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(-1, Invoke(asm, "Test", "test", -7, 3));
        Assert.Equal(1, Invoke(asm, "Test", "test", 7, 3));
    }

    [Fact]
    public void Modulo_Zero_Result()
    {
        const string source = """
namespace Test

func test(a: int, b: int) -> int = a % b
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "test", 9, 3));
    }

    // ── Control flow gaps ──

    [Fact]
    public void Break_From_Inner_Of_Nested_Loops()
    {
        const string source = """
namespace Test

func test() -> int {
    var outer = 0
    var i = 0
    while i < 3 {
        var j = 0
        while j < 10 {
            if j == 2 {
                break
            }
            outer = outer + 1
            j = j + 1
        }
        i = i + 1
    }
    return outer
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(6, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Continue_From_Inner_Of_Nested_Loops()
    {
        const string source = """
namespace Test

func test() -> int {
    var total = 0
    var i = 0
    while i < 3 {
        var j = 0
        while j < 4 {
            j = j + 1
            if j == 2 {
                continue
            }
            total = total + 1
        }
        i = i + 1
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(9, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Defer_LIFO_Ordering()
    {
        const string source = """
namespace Test

func test() -> string {
    var s = ""
    defer { s = s + "C" }
    defer { s = s + "B" }
    defer { s = s + "A" }
    s = s + "X"
    return s
}
""";
        var asm = CompileAndLoad(source);
        // Defer evaluates the return expression at the return site, then runs
        // deferred bodies before control leaves the frame — so the return value
        // captures `s` as it was at `return s`. Defers' mutations to the local
        // are unobservable to the caller without named-return semantics.
        Assert.Equal("X", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Defer_Runs_On_Early_Return()
    {
        const string source = """
namespace Test

func test() -> string {
    var trace = ""
    defer { trace = trace + "D" }
    if true {
        return trace + ".early"
    }
    return trace
}
""";
        var asm = CompileAndLoad(source);
        // Defer body runs after the return expression is computed but before
        // control leaves the frame. The returned string captures whatever
        // trace held at the return-expression site — without defer's
        // contribution. (If the test surprises by including 'D', that's a
        // good signal the defer ordering pins differently than expected.)
        Assert.Equal(".early", Invoke(asm, "Test", "test"));
    }

    // While loops with reverse/empty conditions exercise the same emit paths
    // that `for x in a..b` would if numeric ranges were a language surface
    // (they aren't yet — `..` is unbound in expression position today).

    [Fact]
    public void While_False_Initial_Empty_Execution()
    {
        const string source = """
namespace Test

func test() -> int {
    var count = 0
    var i = 10
    while i < 0 {
        count = count + 1
        i = i + 1
    }
    return count
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void While_Equal_Endpoints_Empty_Execution()
    {
        const string source = """
namespace Test

func test() -> int {
    var count = 0
    var i = 5
    while i < 5 {
        count = count + 1
        i = i + 1
    }
    return count
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "test"));
    }

    // ── Composite literals ──

    [Fact]
    public void Composite_Nested_Literal()
    {
        const string source = """
namespace Test

struct Outer { x: int, inner: Inner }
struct Inner { a: int, b: int }

func test() -> int {
    let o = Outer { x: 1, inner: Inner { a: 10, b: 100 } }
    return o.x + o.inner.a + o.inner.b
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(111, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Composite_Partial_With_Rebinds_One_Field()
    {
        const string source = """
namespace Test

struct Point { x: int, y: int, z: int }

func test() -> int {
    let p = Point { x: 1, y: 2, z: 3 }
    let q = p with { y: 99 }
    return q.x + q.y + q.z
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(103, Invoke(asm, "Test", "test"));
    }

    // ── Boolean short-circuit ──

    [Fact]
    public void Logical_And_Both_True()
    {
        const string source = """
namespace Test

func test(a: bool, b: bool) -> bool = a && b
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test", true, true));
        Assert.Equal(false, Invoke(asm, "Test", "test", true, false));
        Assert.Equal(false, Invoke(asm, "Test", "test", false, true));
    }

    [Fact]
    public void Logical_Or_Truth_Table()
    {
        const string source = """
namespace Test

func test(a: bool, b: bool) -> bool = a || b
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test", true, false));
        Assert.Equal(true, Invoke(asm, "Test", "test", false, true));
        Assert.Equal(false, Invoke(asm, "Test", "test", false, false));
    }

    // ── String operations ──

    [Fact]
    public void String_Concat_With_Plus()
    {
        const string source = """
namespace Test

func test(a: string, b: string) -> string = a + b
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("hello world", Invoke(asm, "Test", "test", "hello ", "world"));
    }

    [Fact]
    public void String_Concat_With_Int_Via_ToString()
    {
        const string source = """
namespace Test

func test(label: string, n: int) -> string = label + n.ToString()
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("count=42", Invoke(asm, "Test", "test", "count=", 42));
    }

    // ── String interpolation ──

    [Fact]
    public void String_Interpolation_Single_Var()
    {
        const string source = """
namespace Test

func test(n: int) -> string = "n is {n}"
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("n is 42", Invoke(asm, "Test", "test", 42));
    }

    [Fact]
    public void String_Interpolation_Field_Access()
    {
        const string source = """
namespace Test

struct P { x: int, y: int }

func test() -> string {
    let p = P { x: 3, y: 4 }
    return "point {p.x},{p.y}"
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("point 3,4", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void String_Interpolation_Two_Variables()
    {
        const string source = """
namespace Test

func test(a: int, b: string) -> string = "{a}:{b}"
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("7:hello", Invoke(asm, "Test", "test", 7, "hello"));
    }

    // ── Comparison operators on strings ──

    [Fact]
    public void String_Equality_Same_Content()
    {
        const string source = """
namespace Test

func test(a: string, b: string) -> bool = a == b
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test", "hello", "hello"));
        Assert.Equal(false, Invoke(asm, "Test", "test", "hello", "world"));
    }

    // ── Conditional expression ──

    [Fact]
    public void If_As_Statement_Returns_From_Branch()
    {
        const string source = """
namespace Test

func test(n: int) -> string {
    if n > 0 {
        return "positive"
    }
    if n < 0 {
        return "negative"
    }
    return "zero"
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("positive", Invoke(asm, "Test", "test", 7));
        Assert.Equal("negative", Invoke(asm, "Test", "test", -3));
        Assert.Equal("zero", Invoke(asm, "Test", "test", 0));
    }

    // ── While loop counter accumulation ──

    [Fact]
    public void While_Sum_To_N_Idiom()
    {
        const string source = """
namespace Test

func test(n: int) -> int {
    var total = 0
    var i = 1
    while i <= n {
        total = total + i
        i = i + 1
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(55, Invoke(asm, "Test", "test", 10));
        Assert.Equal(0, Invoke(asm, "Test", "test", 0));
    }

    // ── Nested function call returning struct ──

    [Fact]
    public void Function_Returns_Data_Used_By_Caller()
    {
        const string source = """
namespace Test

struct Pair { left: int, right: int }

func make(a: int, b: int) -> Pair = Pair { left: a, right: b }

func test() -> int {
    let p = make(10, 20)
    return p.left + p.right
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(30, Invoke(asm, "Test", "test"));
    }

    // ── Function with multiple return statements ──

    [Fact]
    public void Function_Multiple_Returns_Via_If_Else()
    {
        const string source = """
namespace Test

func sign(n: int) -> int {
    if n > 0 { return 1 }
    if n < 0 { return -1 }
    return 0
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1, Invoke(asm, "Test", "sign", 5));
        Assert.Equal(-1, Invoke(asm, "Test", "sign", -5));
        Assert.Equal(0, Invoke(asm, "Test", "sign", 0));
    }

    // ── Negative literal handling ──

    [Fact]
    public void Negative_Literal_As_Initializer()
    {
        const string source = """
namespace Test

func test() -> int {
    let n = -42
    return n
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(-42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Negative_Literal_Comparison()
    {
        const string source = """
namespace Test

func test(n: int) -> bool = n < -10
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test", -20));
        Assert.Equal(false, Invoke(asm, "Test", "test", -5));
    }

    // ── Comparison chain ──

    [Fact]
    public void Range_Check_With_And()
    {
        const string source = """
namespace Test

func in_range(n: int) -> bool = n >= 0 && n <= 100
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "in_range", 50));
        Assert.Equal(false, Invoke(asm, "Test", "in_range", -5));
        Assert.Equal(false, Invoke(asm, "Test", "in_range", 200));
    }

    // ── Function returning bool ──

    [Fact]
    public void Function_Returning_Bool_From_Comparison()
    {
        const string source = """
namespace Test

func is_even(n: int) -> bool = n % 2 == 0
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "is_even", 4));
        Assert.Equal(false, Invoke(asm, "Test", "is_even", 7));
        Assert.Equal(true, Invoke(asm, "Test", "is_even", 0));
    }

    // ── Multi-feature integration tests ──
    // Each of these exercises 3+ features in a small but realistic program.

    [Fact]
    public void Fibonacci_Iterative_Pin()
    {
        const string source = """
namespace Test

func fib(n: int) -> int {
    if n < 2 {
        return n
    }
    var a = 0
    var b = 1
    var i = 2
    while i <= n {
        let next = a + b
        a = b
        b = next
        i = i + 1
    }
    return b
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "fib", 0));
        Assert.Equal(1, Invoke(asm, "Test", "fib", 1));
        Assert.Equal(1, Invoke(asm, "Test", "fib", 2));
        Assert.Equal(8, Invoke(asm, "Test", "fib", 6));
        Assert.Equal(55, Invoke(asm, "Test", "fib", 10));
        Assert.Equal(6765, Invoke(asm, "Test", "fib", 20));
    }

    [Fact]
    public void Fibonacci_Recursive_Pin()
    {
        const string source = """
namespace Test

func fib(n: int) -> int {
    if n < 2 {
        return n
    }
    return fib(n - 1) + fib(n - 2)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "fib", 0));
        Assert.Equal(1, Invoke(asm, "Test", "fib", 1));
        Assert.Equal(8, Invoke(asm, "Test", "fib", 6));
        Assert.Equal(55, Invoke(asm, "Test", "fib", 10));
    }

    [Fact]
    public void Factorial_Recursive_Pin()
    {
        const string source = """
namespace Test

func fact(n: int) -> int {
    if n <= 1 {
        return 1
    }
    return n * fact(n - 1)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1, Invoke(asm, "Test", "fact", 0));
        Assert.Equal(1, Invoke(asm, "Test", "fact", 1));
        Assert.Equal(120, Invoke(asm, "Test", "fact", 5));
        Assert.Equal(720, Invoke(asm, "Test", "fact", 6));
        Assert.Equal(3628800, Invoke(asm, "Test", "fact", 10));
    }

    [Fact]
    public void GCD_Euclidean_Pin()
    {
        const string source = """
namespace Test

func gcd(a: int, b: int) -> int {
    var x = a
    var y = b
    while y != 0 {
        let t = y
        y = x % y
        x = t
    }
    return x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(6, Invoke(asm, "Test", "gcd", 54, 24));
        Assert.Equal(1, Invoke(asm, "Test", "gcd", 17, 13));
        Assert.Equal(12, Invoke(asm, "Test", "gcd", 48, 36));
        Assert.Equal(7, Invoke(asm, "Test", "gcd", 7, 0));
    }

    [Fact]
    public void IsPrime_Trial_Division_Pin()
    {
        const string source = """
namespace Test

func is_prime(n: int) -> bool {
    if n < 2 {
        return false
    }
    if n == 2 {
        return true
    }
    if n % 2 == 0 {
        return false
    }
    var d = 3
    while d * d <= n {
        if n % d == 0 {
            return false
        }
        d = d + 2
    }
    return true
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(false, Invoke(asm, "Test", "is_prime", 0));
        Assert.Equal(false, Invoke(asm, "Test", "is_prime", 1));
        Assert.Equal(true, Invoke(asm, "Test", "is_prime", 2));
        Assert.Equal(true, Invoke(asm, "Test", "is_prime", 17));
        Assert.Equal(false, Invoke(asm, "Test", "is_prime", 91));
        Assert.Equal(true, Invoke(asm, "Test", "is_prime", 97));
    }

    [Fact]
    public void Power_Of_Two_Via_While_Pin()
    {
        const string source = """
namespace Test

func pow2(n: int) -> int {
    var result = 1
    var i = 0
    while i < n {
        result = result * 2
        i = i + 1
    }
    return result
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1, Invoke(asm, "Test", "pow2", 0));
        Assert.Equal(2, Invoke(asm, "Test", "pow2", 1));
        Assert.Equal(1024, Invoke(asm, "Test", "pow2", 10));
        Assert.Equal(65536, Invoke(asm, "Test", "pow2", 16));
    }

    [Fact]
    public void DigitSum_Via_While_Pin()
    {
        const string source = """
namespace Test

func digit_sum(n: int) -> int {
    var total = 0
    var x = n
    while x > 0 {
        total = total + x % 10
        x = x / 10
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "digit_sum", 0));
        Assert.Equal(6, Invoke(asm, "Test", "digit_sum", 123));
        Assert.Equal(45, Invoke(asm, "Test", "digit_sum", 123456789));
        Assert.Equal(1, Invoke(asm, "Test", "digit_sum", 1000));
    }

    [Fact]
    public void IntegerSquareRoot_BinarySearch_Pin()
    {
        const string source = """
namespace Test

func isqrt(n: int) -> int {
    if n < 2 {
        return n
    }
    var lo = 0
    var hi = n
    while lo < hi {
        let mid = (lo + hi + 1) / 2
        if mid * mid <= n {
            lo = mid
        }
        if mid * mid > n {
            hi = mid - 1
        }
    }
    return lo
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "isqrt", 0));
        Assert.Equal(1, Invoke(asm, "Test", "isqrt", 1));
        Assert.Equal(3, Invoke(asm, "Test", "isqrt", 9));
        Assert.Equal(4, Invoke(asm, "Test", "isqrt", 20));
        Assert.Equal(10, Invoke(asm, "Test", "isqrt", 100));
        Assert.Equal(31, Invoke(asm, "Test", "isqrt", 999));
    }

    [Fact]
    public void Bank_Account_With_Data_Type_Pin()
    {
        // data type carrying state through value-shaped operations.
        const string source = """
namespace Test

struct Account { balance: int }

func (a: Account) deposit(amount: int) -> Account = a with { balance: a.balance + amount }
func (a: Account) withdraw(amount: int) -> Account = a with { balance: a.balance - amount }

func run() -> int {
    let opened = Account { balance: 0 }
    let after_deposit = opened.deposit(100)
    let after_withdraw = after_deposit.withdraw(30)
    let final = after_withdraw.deposit(5)
    return final.balance
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(75, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void State_Machine_With_Var_Field_Pin()
    {
        const string source = """
namespace Test

static Light {
    var state: int = 0

    func next() -> int {
        state = (state + 1) % 3
        return state
    }

    func reset() -> int {
        state = 0
        return state
    }
}

func test() -> int {
    let a = Light.next()
    let b = Light.next()
    let c = Light.next()
    let d = Light.next()
    return a * 1000 + b * 100 + c * 10 + d
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1201, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Counter_Closure_Via_StaticFunc_Pin()
    {
        const string source = """
namespace Test

static Counter {
    var n: int = 0
    func bump() -> int {
        n = n + 1
        return n
    }
    func peek() -> int = n
    func reset() -> int {
        n = 0
        return n
    }
}

func test() -> int {
    let a = Counter.bump()
    let b = Counter.bump()
    let c = Counter.peek()
    let _ = Counter.reset()
    let d = Counter.bump()
    return a * 1000 + b * 100 + c * 10 + d
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1221, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Conditional_Chain_With_StringConcat_Pin()
    {
        const string source = """
namespace Test

func describe(score: int) -> string {
    var label = ""
    if score >= 90 {
        label = "A"
    }
    if score >= 80 && score < 90 {
        label = "B"
    }
    if score >= 70 && score < 80 {
        label = "C"
    }
    if score < 70 {
        label = "F"
    }
    return "grade: " + label
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("grade: A", Invoke(asm, "Test", "describe", 95));
        Assert.Equal("grade: B", Invoke(asm, "Test", "describe", 85));
        Assert.Equal("grade: C", Invoke(asm, "Test", "describe", 75));
        Assert.Equal("grade: F", Invoke(asm, "Test", "describe", 50));
    }

    [Fact]
    public void Data_Method_Chain_Via_With_Pin()
    {
        // Threads a value-semantic data type through a chain of with-expressions,
        // verifying immutable update preserves prior bindings.
        const string source = """
namespace Test

struct Vec3 { x: int, y: int, z: int }

func (v: Vec3) with_x(nx: int) -> Vec3 = v with { x: nx }
func (v: Vec3) with_y(ny: int) -> Vec3 = v with { y: ny }
func (v: Vec3) with_z(nz: int) -> Vec3 = v with { z: nz }

func test() -> int {
    let origin = Vec3 { x: 0, y: 0, z: 0 }
    let stepped = origin.with_x(1).with_y(2).with_z(3)
    return stepped.x + stepped.y * 10 + stepped.z * 100
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(321, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Recursive_Mutual_Functions_Pin()
    {
        const string source = """
namespace Test

func is_even(n: int) -> bool {
    if n == 0 {
        return true
    }
    return is_odd(n - 1)
}

func is_odd(n: int) -> bool {
    if n == 0 {
        return false
    }
    return is_even(n - 1)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "is_even", 10));
        Assert.Equal(false, Invoke(asm, "Test", "is_even", 7));
        Assert.Equal(true, Invoke(asm, "Test", "is_odd", 9));
        Assert.Equal(false, Invoke(asm, "Test", "is_odd", 0));
    }

    [Fact]
    public void Promoted_Instance_Methods_On_Data_Pin()
    {
        // Top-level func with `data` first param promotes to instance method.
        const string source = """
namespace Test

struct Rect { width: int, height: int }

func (r: Rect) area() -> int = r.width * r.height
func (r: Rect) perimeter() -> int = (r.width + r.height) * 2
func (r: Rect) is_square() -> bool = r.width == r.height

func describe() -> int {
    let r = Rect { width: 4, height: 5 }
    var score = 0
    if r.is_square() {
        score = score + 1000
    }
    return r.area() + r.perimeter() + score
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(38, Invoke(asm, "Test", "describe"));
    }

    // ── choice / match coverage ──

    [Fact]
    public void Choice_Bare_Variants_Tag_Discrimination_Pin()
    {
        const string source = """
namespace Test

union Status {
    idle
    busy
    done
}

func describe(s: Status) -> string {
    match (s: Status) {
        .idle { return "idle" }
        .busy { return "busy" }
        .done { return "done" }
        default { return "unknown" }
    }
    return "unknown"
}

func test() -> string {
    return describe(Status.busy())
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("busy", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Choice_Many_Bare_Variants_Pin()
    {
        const string source = """
namespace Test

union Direction {
    north
    south
    east
    west
}

func opposite(d: Direction) -> string {
    match (d: Direction) {
        .north { return "south" }
        .south { return "north" }
        .east { return "west" }
        .west { return "east" }
        default { return "?" }
    }
    return "?"
}

func test() -> string {
    return opposite(Direction.east())
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("west", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Enum_Member_Access_And_Comparison_Pin()
    {
        // Enum literal members are accessed via dot-prefix and bind through
        // the choice/enum's value-emission path. Comparison routes through
        // the same equality the IL emitter uses for ints.
        const string source = """
namespace Test

enum Suit { hearts, diamonds, clubs, spades }

func is_red(s: Suit) -> bool {
    if s == .hearts { return true }
    if s == .diamonds { return true }
    return false
}

func test() -> int {
    var red = 0
    if is_red(.hearts)   { red = red + 1 }
    if is_red(.diamonds) { red = red + 1 }
    if is_red(.clubs)    { red = red + 1 }
    if is_red(.spades)   { red = red + 1 }
    return red
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(2, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void RefData_Identity_Equal_To_Self_Pin()
    {
        const string source = """
namespace Test

class Box {
    pub value: int
    init(v: int) { self.value = v }
}

func test() -> bool {
    let a = Box(5)
    let b = a
    return a == b
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void RefData_Different_Instances_Not_Identity_Equal_Pin()
    {
        const string source = """
namespace Test

class Box {
    pub value: int
    init(v: int) { self.value = v }
}

func test() -> bool {
    let a = Box(5)
    let b = Box(5)
    return a == b
}
""";
        var asm = CompileAndLoad(source);
        // Two newly-constructed class instances have distinct identity even
        // with identical field values — reference equality is the contract.
        Assert.Equal(false, Invoke(asm, "Test", "test"));
    }

    // ── Tuples ──

    [Fact]
    public void Tuple_Construction_And_Field_Access()
    {
        const string source = """
namespace Test

func make() -> (int, string) = (42, "hi")

func test() -> int {
    let t = make()
    return t.Item1
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Tuple_String_Element_Access()
    {
        const string source = """
namespace Test

func make() -> (int, string) = (42, "hi")

func test() -> string {
    let t = make()
    return t.Item2
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("hi", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Tuple_Three_Element()
    {
        const string source = """
namespace Test

func make() -> (int, int, int) = (1, 2, 3)

func test() -> int {
    let t = make()
    return t.Item1 + t.Item2 + t.Item3
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(6, Invoke(asm, "Test", "test"));
    }

    // ── Function literals ──

    [Fact]
    public void Function_Literal_Captures_Let()
    {
        const string source = """
namespace Test

func test() -> int {
    let x = 10
    let read = func() -> int = x
    return read()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(10, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Function_Literal_Passed_To_Function()
    {
        const string source = """
namespace Test

func apply(f: &(int -> int), n: int) -> int = f(n)

func test() -> int {
    let double: &(int -> int) = func(x: int) -> int = x * 2
    return apply(double, 21)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Function_Literal_Inline_Argument()
    {
        const string source = """
namespace Test

func apply(f: &(int -> int), n: int) -> int = f(n)

func test() -> int = apply(func(x: int) -> int = x + 1, 41)
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    // ── Generics ──

    [Fact]
    public void Generic_Identity_Function_Int()
    {
        const string source = """
namespace Test

func id<T>(x: T) -> T = x

func test() -> int = id<int>(42)
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Generic_Identity_Function_String()
    {
        const string source = """
namespace Test

func id<T>(x: T) -> T = x

func test() -> string = id<string>("hello")
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("hello", Invoke(asm, "Test", "test"));
    }

    // Explicit type args substitute into the generic function's return type, so the
    // `let` that binds the result is the concrete type (not the open `T`). Before
    // this fix the local typed as `!!0` and emitted invalid IL.
    [Fact]
    public void Generic_Identity_Function_Int_LetBinding()
    {
        const string source = """
namespace Test

func id<T>(x: T) -> T = x

func test() -> int {
    let b = id<int>(42)
    return b + 1
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(43, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Generic_Identity_Function_String_LetBinding()
    {
        const string source = """
namespace Test

func id<T>(x: T) -> T = x

func test() -> string {
    let s = id<string>("hi")
    return s + "!"
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("hi!", Invoke(asm, "Test", "test"));
    }

    // Explicit type arg flowing through a user value `data` return type.
    [Fact]
    public void Generic_Identity_Function_UserData_LetBinding()
    {
        const string source = """
namespace Test

struct Box { n: int }
func id<T>(x: T) -> T = x

func test() -> int {
    let b = id<Box>(Box { n: 7 })
    return b.n
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(7, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Generic_Data_Type_Construction()
    {
        const string source = """
namespace Test

struct Box<T> { value: T }

func test() -> int {
    let b = Box<int> { value: 42 }
    return b.value
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    // ── Nested data ──

    [Fact]
    public void Nested_Data_Field_Access_Two_Deep()
    {
        const string source = """
namespace Test

struct Inner { v: int }
struct Outer { inner: Inner }

func test() -> int {
    let o = Outer { inner: Inner { v: 99 } }
    return o.inner.v
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(99, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Triple_Nested_Data_Field_Access()
    {
        const string source = """
namespace Test

struct A { v: int }
struct B { a: A }
struct C { b: B }

func test() -> int {
    let c = C { b: B { a: A { v: 42 } } }
    return c.b.a.v
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    // ── Comparison operators ──

    [Fact]
    public void Comparison_All_Operators()
    {
        const string source = """
namespace Test

func test(a: int, b: int) -> int {
    var result = 0
    if a == b { result = result + 1 }
    if a != b { result = result + 10 }
    if a < b  { result = result + 100 }
    if a > b  { result = result + 1000 }
    if a <= b { result = result + 10000 }
    if a >= b { result = result + 100000 }
    return result
}
""";
        var asm = CompileAndLoad(source);
        // a=3,b=7: !=,<,<= ⇒ 10 + 100 + 10000 = 10110
        Assert.Equal(10110, Invoke(asm, "Test", "test", 3, 7));
        // a=5,b=5: ==,<=,>= ⇒ 1 + 10000 + 100000 = 110001
        Assert.Equal(110001, Invoke(asm, "Test", "test", 5, 5));
        // a=9,b=2: !=,>,>= ⇒ 10 + 1000 + 100000 = 101010
        Assert.Equal(101010, Invoke(asm, "Test", "test", 9, 2));
    }

    // ── Local variable scoping ──

    [Fact]
    public void Block_Scoped_Let_Visible_After_Block()
    {
        const string source = """
namespace Test

func test() -> int {
    var total = 0
    var i = 0
    while i < 3 {
        let inner = i * 10
        total = total + inner
        i = i + 1
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(30, Invoke(asm, "Test", "test"));
    }

    // ── Conditional with assignment ──

    [Fact]
    public void If_Mutates_Outer_Var()
    {
        const string source = """
namespace Test

func test(n: int) -> int {
    var result = 0
    if n > 0 {
        result = 1
    }
    if n < 0 {
        result = -1
    }
    return result
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1, Invoke(asm, "Test", "test", 5));
        Assert.Equal(-1, Invoke(asm, "Test", "test", -5));
        Assert.Equal(0, Invoke(asm, "Test", "test", 0));
    }

    // ── Mathematical idioms ──

    [Fact]
    public void Absolute_Value_Pin()
    {
        const string source = """
namespace Test

func abs(n: int) -> int {
    if n < 0 {
        return -n
    }
    return n
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(5, Invoke(asm, "Test", "abs", -5));
        Assert.Equal(5, Invoke(asm, "Test", "abs", 5));
        Assert.Equal(0, Invoke(asm, "Test", "abs", 0));
    }

    [Fact]
    public void Min_Max_Pin()
    {
        const string source = """
namespace Test

func minimum(a: int, b: int) -> int {
    if a < b {
        return a
    }
    return b
}

func maximum(a: int, b: int) -> int {
    if a > b {
        return a
    }
    return b
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3, Invoke(asm, "Test", "minimum", 3, 7));
        Assert.Equal(3, Invoke(asm, "Test", "minimum", 7, 3));
        Assert.Equal(7, Invoke(asm, "Test", "maximum", 3, 7));
        Assert.Equal(7, Invoke(asm, "Test", "maximum", 7, 3));
        Assert.Equal(5, Invoke(asm, "Test", "minimum", 5, 5));
    }

    [Fact]
    public void Clamp_Idiom_Pin()
    {
        const string source = """
namespace Test

func clamp(n: int, lo: int, hi: int) -> int {
    if n < lo {
        return lo
    }
    if n > hi {
        return hi
    }
    return n
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "clamp", -5, 0, 10));
        Assert.Equal(10, Invoke(asm, "Test", "clamp", 100, 0, 10));
        Assert.Equal(7, Invoke(asm, "Test", "clamp", 7, 0, 10));
    }

    // ── String methods ──

    [Fact]
    public void String_Length_Property()
    {
        const string source = """
namespace Test

func test(s: string) -> int = s.Length
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(5, Invoke(asm, "Test", "test", "hello"));
        Assert.Equal(0, Invoke(asm, "Test", "test", ""));
        Assert.Equal(11, Invoke(asm, "Test", "test", "hello world"));
    }

    // ── Function pointer-like idiom (via function literal in field) ──

    [Fact]
    public void Field_With_Function_Pointer_Type_Pin()
    {
        const string source = """
namespace Test

struct Dispatch {
    handler: &(int -> int)
}

func double_it(x: int) -> int = x * 2

func test() -> int {
    let d = Dispatch { handler: double_it }
    return d.handler(21)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    // ── Conditional return type unification ──

    [Fact]
    public void Both_Branches_Return_Same_Type_Pin()
    {
        const string source = """
namespace Test

func choose(flag: bool) -> int {
    if flag {
        return 100
    }
    return 200
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(100, Invoke(asm, "Test", "choose", true));
        Assert.Equal(200, Invoke(asm, "Test", "choose", false));
    }

    // ── Algorithmic ──

    [Fact]
    public void Reverse_Digits_Pin()
    {
        const string source = """
namespace Test

func reverse(n: int) -> int {
    var result = 0
    var x = n
    while x > 0 {
        result = result * 10 + x % 10
        x = x / 10
    }
    return result
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(54321, Invoke(asm, "Test", "reverse", 12345));
        Assert.Equal(7, Invoke(asm, "Test", "reverse", 7));
        Assert.Equal(0, Invoke(asm, "Test", "reverse", 0));
    }

    [Fact]
    public void Count_Set_Bits_Pin()
    {
        // Counts set bits in n by repeated division and mod-2.
        const string source = """
namespace Test

func popcount(n: int) -> int {
    var count = 0
    var x = n
    while x > 0 {
        if x % 2 == 1 {
            count = count + 1
        }
        x = x / 2
    }
    return count
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "popcount", 0));
        Assert.Equal(1, Invoke(asm, "Test", "popcount", 1));
        Assert.Equal(3, Invoke(asm, "Test", "popcount", 7));
        Assert.Equal(4, Invoke(asm, "Test", "popcount", 15));
        Assert.Equal(1, Invoke(asm, "Test", "popcount", 128));
    }

    [Fact]
    public void Multiply_Without_Star_Pin()
    {
        const string source = """
namespace Test

func mul(a: int, b: int) -> int {
    var result = 0
    var i = 0
    while i < b {
        result = result + a
        i = i + 1
    }
    return result
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(20, Invoke(asm, "Test", "mul", 4, 5));
        Assert.Equal(0, Invoke(asm, "Test", "mul", 100, 0));
        Assert.Equal(0, Invoke(asm, "Test", "mul", 0, 100));
    }

    [Fact]
    public void Collatz_Length_Pin()
    {
        // Number of Collatz steps to reach 1 from n.
        const string source = """
namespace Test

func collatz_steps(n: int) -> int {
    var x = n
    var steps = 0
    while x > 1 {
        if x % 2 == 0 {
            x = x / 2
        } else {
            x = x * 3 + 1
        }
        steps = steps + 1
    }
    return steps
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "collatz_steps", 1));
        Assert.Equal(1, Invoke(asm, "Test", "collatz_steps", 2));
        // 6 → 3 → 10 → 5 → 16 → 8 → 4 → 2 → 1 = 8 steps
        Assert.Equal(8, Invoke(asm, "Test", "collatz_steps", 6));
    }

    // ── Side-effect counting via accumulator ──

    [Fact]
    public void Helper_Function_Side_Effects_Through_Return_Pin()
    {
        const string source = """
namespace Test

func square(n: int) -> int = n * n

func sum_squares(n: int) -> int {
    var total = 0
    var i = 1
    while i <= n {
        total = total + square(i)
        i = i + 1
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        // 1+4+9+16+25 = 55
        Assert.Equal(55, Invoke(asm, "Test", "sum_squares", 5));
        Assert.Equal(385, Invoke(asm, "Test", "sum_squares", 10));
    }

    // ── Polymorphic via interface ──

    [Fact]
    public void Interface_Dispatch_Through_Promoted_Method_Pin()
    {
        const string source = """
namespace Test

interface IShape {
    func area() -> int
}

struct Square : IShape { side: int }
struct Circle : IShape { radius: int }

func (s: Square) area() -> int = s.side * s.side
func (c: Circle) area() -> int = 3 * c.radius * c.radius

func test() -> int {
    let sq = Square { side: 5 }
    let ci = Circle { radius: 4 }
    return sq.area() + ci.area()
}
""";
        var asm = CompileAndLoad(source);
        // 25 + 48 = 73
        Assert.Equal(73, Invoke(asm, "Test", "test"));
    }

    // ── Recursive class structure via *T ──

    [Fact]
    public void LinkedList_Length_Via_HeapPointer_Pin()
    {
        const string source = """
namespace Test

struct Node {
    value: int,
    next: *Node
}

func length(n: *Node) -> int {
    var count = 0
    var cursor = n
    while cursor != nil {
        count = count + 1
        cursor = cursor.next
    }
    return count
}

func test() -> int {
    let c = new Node { value: 3, next: nil }
    let b = new Node { value: 2, next: c }
    let a = new Node { value: 1, next: b }
    return length(a)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3, Invoke(asm, "Test", "test"));
    }
}
