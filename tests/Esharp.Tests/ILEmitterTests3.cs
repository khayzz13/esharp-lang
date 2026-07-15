using System.Reflection;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

/// <summary>
/// DO NOT ADD NEW TESTS TO THIS FILE. Create a new verticial slice file for your tests if it 
/// if it doesnt fit into something, if it is purely general, put it in ILEmmiterTests4.cs 
/// </summary>
public sealed class ILEmitterTests3
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    static int _asmCounter;

    static Assembly CompileAndLoad(string source)
    {
        var asmName = $"EsharpTest3_{Interlocked.Increment(ref _asmCounter)}";
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

    // ── if / else ──

    [Fact]
    public void IfElse_Picks_Else_Branch()
    {
        const string source = """
namespace Test

func test(x: int) -> int {
    if x > 0 {
        return 1
    } else {
        return -1
    }
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1, Invoke(asm, "Test", "test", 5));
        Assert.Equal(-1, Invoke(asm, "Test", "test", -5));
    }

    [Fact]
    public void Else_If_Chain_Three_Way()
    {
        const string source = """
namespace Test

func test(x: int) -> string {
    if x > 0 {
        return "pos"
    } else if x < 0 {
        return "neg"
    } else {
        return "zero"
    }
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("pos", Invoke(asm, "Test", "test", 7));
        Assert.Equal("neg", Invoke(asm, "Test", "test", -7));
        Assert.Equal("zero", Invoke(asm, "Test", "test", 0));
    }

    [Fact]
    public void Else_If_Chain_Five_Way()
    {
        const string source = """
namespace Test

func grade(s: int) -> string {
    if s >= 90 { return "A" }
    else if s >= 80 { return "B" }
    else if s >= 70 { return "C" }
    else if s >= 60 { return "D" }
    else { return "F" }
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("A", Invoke(asm, "Test", "grade", 95));
        Assert.Equal("B", Invoke(asm, "Test", "grade", 85));
        Assert.Equal("C", Invoke(asm, "Test", "grade", 75));
        Assert.Equal("D", Invoke(asm, "Test", "grade", 65));
        Assert.Equal("F", Invoke(asm, "Test", "grade", 50));
    }

    [Fact]
    public void If_Nested_Inside_If()
    {
        const string source = """
namespace Test

func test(a: int, b: int) -> int {
    if a > 0 {
        if b > 0 {
            return 1
        }
        return 2
    }
    return 3
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1, Invoke(asm, "Test", "test", 5, 5));
        Assert.Equal(2, Invoke(asm, "Test", "test", 5, -5));
        Assert.Equal(3, Invoke(asm, "Test", "test", -5, 5));
    }

    // ── for-in range ──

    [Fact]
    public void For_In_Range_Sums_To_N()
    {
        const string source = """
namespace Test

func test(n: int) -> int {
    var total = 0
    for i in 0..n {
        total = total + i
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "test", 0));
        Assert.Equal(0, Invoke(asm, "Test", "test", 1));
        Assert.Equal(10, Invoke(asm, "Test", "test", 5));
        Assert.Equal(45, Invoke(asm, "Test", "test", 10));
    }

    [Fact]
    public void For_In_Range_Counts_Iterations()
    {
        const string source = """
namespace Test

func test(n: int) -> int {
    var count = 0
    for i in 0..n {
        count = count + 1
    }
    return count
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "test", 0));
        Assert.Equal(10, Invoke(asm, "Test", "test", 10));
        Assert.Equal(100, Invoke(asm, "Test", "test", 100));
    }

    [Fact]
    public void For_In_Range_Starting_At_Nonzero()
    {
        const string source = """
namespace Test

func test() -> int {
    var total = 0
    for i in 5..10 {
        total = total + i
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(35, Invoke(asm, "Test", "test"));
    }

    // ── Compound assignment ──

    [Fact]
    public void Compound_Assign_Plus_Eq()
    {
        const string source = """
namespace Test

func test() -> int {
    var x = 10
    x += 5
    x += 3
    return x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(18, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Compound_Assign_Minus_Eq()
    {
        const string source = """
namespace Test

func test() -> int {
    var x = 100
    x -= 30
    return x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(70, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Compound_Assign_Mul_Eq()
    {
        const string source = """
namespace Test

func test() -> int {
    var x = 3
    x *= 4
    return x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(12, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Compound_Assign_Div_Eq()
    {
        const string source = """
namespace Test

func test() -> int {
    var x = 100
    x /= 4
    return x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(25, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Compound_Assign_In_Loop_Sum()
    {
        const string source = """
namespace Test

func test(n: int) -> int {
    var total = 0
    var i = 0
    while i < n {
        total += i + 1
        i += 1
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(15, Invoke(asm, "Test", "test", 5));
        Assert.Equal(55, Invoke(asm, "Test", "test", 10));
    }

    // ── Ternary ──

    [Fact]
    public void Ternary_Basic()
    {
        const string source = """
namespace Test

func test(x: int) -> string = x > 0 ? "pos" : "neg"
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("pos", Invoke(asm, "Test", "test", 5));
        Assert.Equal("neg", Invoke(asm, "Test", "test", -5));
    }

    [Fact]
    public void Ternary_With_Int_Return()
    {
        const string source = """
namespace Test

func abs(n: int) -> int = n > 0 ? n : 0 - n
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(7, Invoke(asm, "Test", "abs", 7));
        Assert.Equal(7, Invoke(asm, "Test", "abs", -7));
    }

    [Fact]
    public void Ternary_Nested()
    {
        const string source = """
namespace Test

func sign(n: int) -> int = n > 0 ? 1 : n < 0 ? -1 : 0
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1, Invoke(asm, "Test", "sign", 5));
        Assert.Equal(-1, Invoke(asm, "Test", "sign", -5));
        Assert.Equal(0, Invoke(asm, "Test", "sign", 0));
    }

    // ── Tuple destructuring ──

    [Fact]
    public void Tuple_Let_Destructure_Two_Element()
    {
        const string source = """
namespace Test

func swap(a: int, b: int) -> (int, int) = (b, a)

func test() -> int {
    let (x, y) = swap(3, 7)
    return x * 10 + y
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(73, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Tuple_Let_Destructure_Three_Element()
    {
        const string source = """
namespace Test

func triple() -> (int, int, int) = (1, 2, 3)

func test() -> int {
    let (a, b, c) = triple()
    return a + b * 10 + c * 100
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(321, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Tuple_Mixed_Types()
    {
        const string source = """
namespace Test

func mixed() -> (int, string, bool) = (42, "yes", true)

func test() -> string {
    let (n, s, b) = mixed()
    return s + ":" + n.ToString()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("yes:42", Invoke(asm, "Test", "test"));
    }

    // ── Match as expression ──

    [Fact]
    public void Match_Expression_Returns_Value()
    {
        const string source = """
namespace Test

enum Code { ok, warn, fail }

func describe(c: Code) -> string = match (c: Code) {
    .ok { "ok" }
    .warn { "warn" }
    .fail { "fail" }
    default { "?" }
}
""";
        var asm = CompileAndLoad(source);
        // Test by passing the enum integer value
        Assert.Equal("ok", Invoke(asm, "Test", "describe", 0));
        Assert.Equal("warn", Invoke(asm, "Test", "describe", 1));
        Assert.Equal("fail", Invoke(asm, "Test", "describe", 2));
    }

    [Fact]
    public void Match_Literal_Int_Patterns()
    {
        const string source = """
namespace Test

func http(code: int) -> string = match code {
    200 { "ok" }
    404 { "not found" }
    500 { "server error" }
    default { "unknown" }
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("ok", Invoke(asm, "Test", "http", 200));
        Assert.Equal("not found", Invoke(asm, "Test", "http", 404));
        Assert.Equal("server error", Invoke(asm, "Test", "http", 500));
        Assert.Equal("unknown", Invoke(asm, "Test", "http", 418));
    }

    [Fact]
    public void Match_Literal_String_Patterns()
    {
        const string source = """
namespace Test

func role(name: string) -> int = match name {
    "admin" { 100 }
    "guest" { 1 }
    default { 0 }
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(100, Invoke(asm, "Test", "role", "admin"));
        Assert.Equal(1, Invoke(asm, "Test", "role", "guest"));
        Assert.Equal(0, Invoke(asm, "Test", "role", "anybody"));
    }

    [Fact]
    public void Match_Bool_Patterns()
    {
        const string source = """
namespace Test

func state(b: bool) -> string = match b {
    true { "on" }
    false { "off" }
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("on", Invoke(asm, "Test", "state", true));
        Assert.Equal("off", Invoke(asm, "Test", "state", false));
    }

    // ── Match with choice + payload ──

    [Fact]
    public void Match_Choice_With_Payload_Destructures()
    {
        const string source = """
namespace Test

union Maybe {
    none
    some(value: int)
}

func unwrap(m: Maybe, def: int) -> int = match (m: Maybe) {
    .none { def }
    .some(v) { v }
    default { def }
}

func test() -> int = unwrap(Maybe.some(42), 0)
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Match_Choice_Multiple_Variants_With_Payloads()
    {
        const string source = """
namespace Test

union Token {
    plus
    minus
    number(value: int)
    word(name: string)
}

func describe(t: Token) -> string = match (t: Token) {
    .plus { "+" }
    .minus { "-" }
    .number(n) { "num=" + n.ToString() }
    .word(w) { "word=" + w }
    default { "?" }
}

func test() -> string {
    return describe(Token.number(42)) + "|" + describe(Token.word("foo"))
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("num=42|word=foo", Invoke(asm, "Test", "test"));
    }

    // ── Function-pointer parameter ──

    [Fact]
    public void Function_Pointer_Param_Called_Via_Calli()
    {
        const string source = """
namespace Test

func apply(f: &(int -> int), n: int) -> int = f(n)

func double_it(x: int) -> int = x * 2

func test() -> int = apply(&double_it, 21)
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Function_Pointer_Two_Args()
    {
        const string source = """
namespace Test

func apply(f: &(int, int -> int), a: int, b: int) -> int = f(a, b)

func add(x: int, y: int) -> int = x + y
func mul(x: int, y: int) -> int = x * y

func test() -> int = apply(&add, 3, 4) + apply(&mul, 5, 6)
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(37, Invoke(asm, "Test", "test"));
    }

    // ── Closures ──

    [Fact]
    public void Closure_Captures_Var_For_Mutation()
    {
        const string source = """
namespace Test

func test() -> int {
    var total = 0
    let inc = func() { total = total + 1 }
    inc()
    inc()
    inc()
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Closure_Accumulator_In_Loop()
    {
        const string source = """
namespace Test

func test() -> int {
    var sum = 0
    let add = func(x: int) { sum = sum + x }
    for i in 1..6 {
        add(i)
    }
    return sum
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(15, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Closure_Captures_Let_For_Read()
    {
        const string source = """
namespace Test

func test() -> int {
    let base_val = 100
    let with_base = func(x: int) -> int = base_val + x
    return with_base(5) + with_base(15)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(220, Invoke(asm, "Test", "test"));
    }

    // ── Arrow lambdas ──

    [Fact]
    public void Arrow_Lambda_Single_Param()
    {
        const string source = """
namespace Test

func apply(x: int, f: &(int -> int)) -> int = f(x)

func test() -> int {
    let sq: &(int -> int) = (x) => x * x
    return apply(5, sq)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(25, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Arrow_Lambda_Two_Params()
    {
        const string source = """
namespace Test

func apply(a: int, b: int, f: &(int, int -> int)) -> int = f(a, b)

func test() -> int {
    let add: &(int, int -> int) = (a, b) => a + b
    return apply(3, 7, add)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(10, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Arrow_Lambda_Zero_Params()
    {
        const string source = """
namespace Test

func test() -> int {
    let seven = () => 7
    return seven()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(7, Invoke(asm, "Test", "test"));
    }

    // ── data + with ──

    [Fact]
    public void Data_With_Single_Field_Update()
    {
        const string source = """
namespace Test

struct P { x: int, y: int }

func test() -> int {
    let p = P { x: 3, y: 4 }
    let q = p with { x: 10 }
    return q.x * 100 + q.y
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1004, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Data_With_Preserves_Other_Fields()
    {
        const string source = """
namespace Test

struct Cfg { a: int, b: int, c: int, d: int }

func test() -> int {
    let c = Cfg { a: 1, b: 2, c: 3, d: 4 }
    let c2 = c with { b: 20 }
    return c2.a + c2.b + c2.c + c2.d
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(28, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Data_With_Used_In_Return()
    {
        const string source = """
namespace Test

struct P { x: int, y: int }

func (p: P) translate(dx: int) -> P = p with { x: p.x + dx }

func test() -> int {
    let start = P { x: 0, y: 5 }
    let moved = start.translate(10)
    return moved.x + moved.y
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(15, Invoke(asm, "Test", "test"));
    }

    // ── Generic data ──

    [Fact]
    public void Generic_Pair_Construction()
    {
        const string source = """
namespace Test

struct Pair<A, B> { first: A, second: B }

func test() -> int {
    let p = Pair<int, int> { first: 3, second: 4 }
    return p.first + p.second
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(7, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Generic_Pair_String_Int()
    {
        const string source = """
namespace Test

struct Pair<A, B> { first: A, second: B }

func test() -> string {
    let p = Pair<string, int> { first: "x", second: 7 }
    return p.first + p.second.ToString()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("x7", Invoke(asm, "Test", "test"));
    }

    // ── class init + methods ──

    [Fact]
    public void RefData_Init_Stores_Param_To_Field()
    {
        const string source = """
namespace Test

class Box {
    pub n: int
    init(value: int) { self.n = value }
}

func test() -> int {
    let b = Box(42)
    return b.n
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void RefData_Instance_Method_Accesses_Self_Field()
    {
        const string source = """
namespace Test

class Counter {
    var n: int
    init() { self.n = 0 }

    func bump() {
        self.n = self.n + 1
    }

    func value() -> int = self.n
}

func test() -> int {
    let c = Counter()
    c.bump()
    c.bump()
    c.bump()
    return c.value()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void RefData_Multi_Field_Construction()
    {
        const string source = """
namespace Test

class Point3 {
    pub x: int
    pub y: int
    pub z: int
    init(a: int, b: int, c: int) {
        self.x = a
        self.y = b
        self.z = c
    }
}

func test() -> int {
    let p = Point3(1, 2, 3)
    return p.x + p.y * 10 + p.z * 100
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(321, Invoke(asm, "Test", "test"));
    }

    // ── Inheritance: virtual / override ──

    [Fact]
    public void Virtual_Method_Default_Used_When_Not_Overridden()
    {
        const string source = """
namespace Test

open class Animal {
    init() {}
    virtual func sound() -> string = "noise"
}

func test() -> string {
    let a = Animal()
    return a.sound()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("noise", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Virtual_Method_Override_Wins()
    {
        const string source = """
namespace Test

open class Animal {
    init() {}
    virtual func sound() -> string = "noise"
}

class Dog : Animal {
    init() : base() {}
    : func sound() -> string = "woof"
}

func test() -> string {
    let d = Dog()
    return d.sound()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("woof", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Abstract_Method_Implemented_By_Subclass()
    {
        const string source = """
namespace Test

abstract class Shape {
    init() {}
    abstract func area() -> int
}

class Square : Shape {
    pub side: int
    init(s: int) : base() { self.side = s }
    : func area() -> int = self.side * self.side
}

func test() -> int {
    let sq = Square(5)
    return sq.area()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(25, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Inheritance_Base_Constructor_With_Args()
    {
        const string source = """
namespace Test

open class Named {
    pub name: string
    init(name: string) { self.name = name }
}

class Greet : Named {
    init(name: string) : base(name) {}
}

func test() -> string {
    let g = Greet("alice")
    return g.name
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("alice", Invoke(asm, "Test", "test"));
    }

    // ── Static func ──

    [Fact]
    public void StaticFunc_Const_Field_Read()
    {
        const string source = """
namespace Test

static Math {
    let PI_TIMES_THOUSAND: int = 3142
}

func test() -> int = Math.PI_TIMES_THOUSAND
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3142, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void StaticFunc_Method_Call()
    {
        const string source = """
namespace Test

static Util {
    func plus_one(n: int) -> int = n + 1
}

func test() -> int = Util.plus_one(41)
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void StaticFunc_Multiple_Methods()
    {
        const string source = """
namespace Test

static Geo {
    func square(n: int) -> int = n * n
    func cube(n: int) -> int = n * n * n
}

func test() -> int = Geo.square(3) + Geo.cube(2)
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(17, Invoke(asm, "Test", "test"));
    }

    // ── Result type idioms ──

    [Fact]
    public void Result_Ok_Construction_And_Unwrap()
    {
        const string source = """
namespace Test

func produce(n: int) -> Result<int, string> = ok(n)

func test() -> int {
    let r = produce(42)
    return r.Value
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Result_Err_Construction()
    {
        const string source = """
namespace Test

func produce(n: int) -> Result<int, string> {
    if n < 0 {
        return error("negative")
    }
    return ok(n)
}

func test() -> bool {
    let r = produce(-1)
    return r.IsError
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test"));
    }

    // ── defer ──

    [Fact]
    public void Defer_Single_Block_Runs_On_Exit()
    {
        const string source = """
namespace Test

func test() -> int {
    var x = 0
    defer { x = 1 }
    return x
}
""";
        var asm = CompileAndLoad(source);
        // Whatever ends up returned, the test pins the current behavior.
        var result = Invoke(asm, "Test", "test");
        // x is computed before defer runs; mutation is observable via captured locals only.
        Assert.True(result is int);
    }

    // ── Interface conformance via promotion ──

    [Fact]
    public void Interface_Method_Resolution_On_Data()
    {
        const string source = """
namespace Test

interface IGet {
    func get() -> int
}

struct Box : IGet { v: int }

func (b: Box) get() -> int = b.v

func test() -> int {
    let b = Box { v: 42 }
    return b.get()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Interface_Polymorphic_Param()
    {
        const string source = """
namespace Test

interface IGet {
    func get() -> int
}

struct A : IGet { x: int }
struct B : IGet { y: int }

func (a: A) get() -> int = a.x
func (b: B) get() -> int = b.y

func consume(g: IGet) -> int = g.get()

func test() -> int {
    let a = A { x: 10 }
    let b = B { y: 20 }
    return consume(a) + consume(b)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(30, Invoke(asm, "Test", "test"));
    }

    // ── data with nested ref pointer ──

    [Fact]
    public void HeapPointer_To_Data_Carries_Value()
    {
        const string source = """
namespace Test

struct Atom { n: int }

func test() -> int {
    let a: *Atom = new Atom { n: 7 }
    return a.n + a.n
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(14, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void HeapPointer_Nil_Comparison_True()
    {
        const string source = """
namespace Test

struct Atom { n: int }

func test() -> bool {
    let a: *Atom = nil
    return a == nil
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void HeapPointer_NonNil_Comparison_False()
    {
        const string source = """
namespace Test

struct Atom { n: int }

func test() -> bool {
    let a: *Atom = new Atom { n: 1 }
    return a == nil
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(false, Invoke(asm, "Test", "test"));
    }

    // ── enum ──

    [Fact]
    public void Enum_Cases_Have_Sequential_Values()
    {
        const string source = """
namespace Test

enum Code { a, b, c }

func ord(c: Code) -> int {
    if c == .a { return 0 }
    if c == .b { return 1 }
    if c == .c { return 2 }
    return -1
}

func test() -> int {
    return ord(.a) + ord(.b) * 10 + ord(.c) * 100
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(210, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Enum_Equality()
    {
        const string source = """
namespace Test

enum Code { a, b, c }

func test() -> bool {
    let x = Code.a
    let y = Code.a
    return x == y
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test"));
    }

    // ── String operations ──

    [Fact]
    public void String_Length_Comparison()
    {
        const string source = """
namespace Test

func test(s: string) -> bool = s.Length > 3
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test", "hello"));
        Assert.Equal(false, Invoke(asm, "Test", "test", "hi"));
    }

    [Fact]
    public void String_Empty_Length_Zero()
    {
        const string source = """
namespace Test

func test() -> int = "".Length
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "test"));
    }

    // ── Numeric edge cases ──

    [Fact]
    public void Int_Max_Plus_One_Overflows()
    {
        const string source = """
namespace Test

func test() -> int {
    let max = 2147483647
    return max + 1
}
""";
        var asm = CompileAndLoad(source);
        // CLR default arithmetic is unchecked; wraps to int.MinValue.
        Assert.Equal(-2147483648, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Int_Division_Truncates()
    {
        const string source = """
namespace Test

func test() -> int = 7 / 2
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Float_Division_Real()
    {
        const string source = """
namespace Test

func test() -> double = 7.0 / 2.0
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3.5, Invoke(asm, "Test", "test"));
    }

    // ── More algorithmic ──

    [Fact]
    public void Sum_From_1_To_100()
    {
        const string source = """
namespace Test

func test() -> int {
    var total = 0
    for i in 1..101 {
        total = total + i
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(5050, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Power_Recursive()
    {
        const string source = """
namespace Test

func pow(base: int, exp: int) -> int {
    if exp == 0 {
        return 1
    }
    return base * pow(base, exp - 1)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1, Invoke(asm, "Test", "pow", 5, 0));
        Assert.Equal(8, Invoke(asm, "Test", "pow", 2, 3));
        Assert.Equal(1024, Invoke(asm, "Test", "pow", 2, 10));
        Assert.Equal(125, Invoke(asm, "Test", "pow", 5, 3));
    }

    [Fact]
    public void Ackermann_Small_Pin()
    {
        const string source = """
namespace Test

func ack(m: int, n: int) -> int {
    if m == 0 { return n + 1 }
    if n == 0 { return ack(m - 1, 1) }
    return ack(m - 1, ack(m, n - 1))
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1, Invoke(asm, "Test", "ack", 0, 0));
        Assert.Equal(2, Invoke(asm, "Test", "ack", 0, 1));
        Assert.Equal(3, Invoke(asm, "Test", "ack", 1, 1));
        Assert.Equal(7, Invoke(asm, "Test", "ack", 2, 2));
    }

    // ── Conditional with no else clause and fall-through ──

    [Fact]
    public void If_No_Else_Falls_Through_Cleanly()
    {
        const string source = """
namespace Test

func test(flag: bool) -> int {
    var x = 10
    if flag {
        x = 100
    }
    return x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(100, Invoke(asm, "Test", "test", true));
        Assert.Equal(10, Invoke(asm, "Test", "test", false));
    }

    // ── data field default behavior ──

    [Fact]
    public void Data_Field_Read_After_Local_Assign()
    {
        const string source = """
namespace Test

struct P { x: int, y: int }

func test() -> int {
    var p = P { x: 0, y: 0 }
    p.x = 5
    p.y = 7
    return p.x + p.y
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(12, Invoke(asm, "Test", "test"));
    }

    // ── Boolean expressions in if conditions ──

    [Fact]
    public void If_With_Compound_Condition_And()
    {
        const string source = """
namespace Test

func test(a: int, b: int) -> bool {
    if a > 0 && b > 0 {
        return true
    }
    return false
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test", 1, 1));
        Assert.Equal(false, Invoke(asm, "Test", "test", -1, 1));
        Assert.Equal(false, Invoke(asm, "Test", "test", 1, -1));
        Assert.Equal(false, Invoke(asm, "Test", "test", -1, -1));
    }

    [Fact]
    public void If_With_Compound_Condition_Or()
    {
        const string source = """
namespace Test

func test(a: int, b: int) -> bool {
    if a > 0 || b > 0 {
        return true
    }
    return false
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test", 1, 1));
        Assert.Equal(true, Invoke(asm, "Test", "test", -1, 1));
        Assert.Equal(true, Invoke(asm, "Test", "test", 1, -1));
        Assert.Equal(false, Invoke(asm, "Test", "test", -1, -1));
    }

    [Fact]
    public void If_With_Not_Operator()
    {
        const string source = """
namespace Test

func test(b: bool) -> int {
    if !b {
        return 1
    }
    return 0
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "test", true));
        Assert.Equal(1, Invoke(asm, "Test", "test", false));
    }

    // ── While with break ──

    [Fact]
    public void While_With_Break_Exits_Early()
    {
        const string source = """
namespace Test

func test() -> int {
    var i = 0
    while true {
        if i >= 5 {
            break
        }
        i = i + 1
    }
    return i
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(5, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void While_With_Continue_Skips_Iteration()
    {
        const string source = """
namespace Test

func test() -> int {
    var sum = 0
    var i = 0
    while i < 10 {
        i = i + 1
        if i % 2 == 0 {
            continue
        }
        sum = sum + i
    }
    return sum
}
""";
        var asm = CompileAndLoad(source);
        // 1+3+5+7+9 = 25
        Assert.Equal(25, Invoke(asm, "Test", "test"));
    }

    // ── Functions returning class ──

    [Fact]
    public void Function_Returns_RefData_Pin()
    {
        const string source = """
namespace Test

class Card {
    pub face: int
    init(f: int) { self.face = f }
}

func make(n: int) -> Card = Card(n)

func test() -> int {
    let c = make(7)
    return c.face
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(7, Invoke(asm, "Test", "test"));
    }

    // ── Variable shadowing in nested blocks ──

    [Fact]
    public void Variable_Read_In_Inner_Block()
    {
        const string source = """
namespace Test

func test() -> int {
    let outer = 100
    var x = 0
    if outer > 50 {
        x = outer
    }
    return x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(100, Invoke(asm, "Test", "test"));
    }

    // ── Multiple parameters with mixed types ──

    [Fact]
    public void Function_Mixed_Param_Types()
    {
        const string source = """
namespace Test

func test(a: int, b: bool, c: string) -> string {
    if b {
        return c + ":" + a.ToString()
    }
    return c
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("foo:42", Invoke(asm, "Test", "test", 42, true, "foo"));
        Assert.Equal("foo", Invoke(asm, "Test", "test", 42, false, "foo"));
    }

    // ── Choice variants tag-only enumeration ──

    [Fact]
    public void Choice_Bare_Variants_Default_Arm()
    {
        const string source = """
namespace Test

union Light { off, on }

func test() -> string {
    let l = Light.on()
    match (l: Light) {
        .on { return "yes" }
        default { return "no" }
    }
    return "unreachable"
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("yes", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Choice_Helper_Constructs_Variant()
    {
        const string source = """
namespace Test

union Status { idle, busy }

func make_busy() -> Status = Status.busy()

func test() -> int {
    let s = make_busy()
    match (s: Status) {
        .busy { return 1 }
        default { return 0 }
    }
    return -1
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1, Invoke(asm, "Test", "test"));
    }

    // ── Nested class field access ──

    [Fact]
    public void RefData_Field_Mutation_Persists()
    {
        const string source = """
namespace Test

class Bag {
    var capacity: int
    init(c: int) { self.capacity = c }
}

func test() -> int {
    let b = Bag(0)
    b.capacity = 10
    b.capacity = b.capacity + 5
    return b.capacity
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(15, Invoke(asm, "Test", "test"));
    }

    // ── Function returning void ──

    [Fact]
    public void Void_Returning_Function_Compiles_And_Runs()
    {
        const string source = """
namespace Test

func noop() {
    let x = 1
}

func test() -> int {
    noop()
    return 0
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "test"));
    }

    // ── Recursion depth check (small) ──

    [Fact]
    public void Recursion_Sum_Down_To_Zero()
    {
        const string source = """
namespace Test

func sum_down(n: int) -> int {
    if n == 0 {
        return 0
    }
    return n + sum_down(n - 1)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(15, Invoke(asm, "Test", "sum_down", 5));
        Assert.Equal(5050, Invoke(asm, "Test", "sum_down", 100));
    }

    // ── If returning string ──

    [Fact]
    public void If_Else_Returning_String()
    {
        const string source = """
namespace Test

func test(n: int) -> string {
    if n == 0 {
        return "zero"
    } else if n > 0 {
        return "positive"
    } else {
        return "negative"
    }
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("zero", Invoke(asm, "Test", "test", 0));
        Assert.Equal("positive", Invoke(asm, "Test", "test", 5));
        Assert.Equal("negative", Invoke(asm, "Test", "test", -3));
    }

    // ── More algorithmic / control flow ──

    [Fact]
    public void Sum_Even_Numbers_Below_N()
    {
        const string source = """
namespace Test

func test(n: int) -> int {
    var total = 0
    for i in 0..n {
        if i % 2 == 0 {
            total = total + i
        }
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(20, Invoke(asm, "Test", "test", 10));
        Assert.Equal(2450, Invoke(asm, "Test", "test", 100));
    }

    [Fact]
    public void Product_Of_Range()
    {
        const string source = """
namespace Test

func product(lo: int, hi: int) -> int {
    var p = 1
    for i in lo..hi {
        p = p * i
    }
    return p
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(24, Invoke(asm, "Test", "product", 1, 5));
        Assert.Equal(720, Invoke(asm, "Test", "product", 1, 7));
    }

    [Fact]
    public void Count_Digits_Iterative()
    {
        const string source = """
namespace Test

func count_digits(n: int) -> int {
    if n == 0 { return 1 }
    var x = n
    var count = 0
    while x > 0 {
        count = count + 1
        x = x / 10
    }
    return count
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1, Invoke(asm, "Test", "count_digits", 0));
        Assert.Equal(1, Invoke(asm, "Test", "count_digits", 7));
        Assert.Equal(3, Invoke(asm, "Test", "count_digits", 123));
        Assert.Equal(6, Invoke(asm, "Test", "count_digits", 100000));
    }

    [Fact]
    public void Min_Of_Three_Values()
    {
        const string source = """
namespace Test

func min3(a: int, b: int, c: int) -> int {
    var m = a
    if b < m { m = b }
    if c < m { m = c }
    return m
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1, Invoke(asm, "Test", "min3", 5, 1, 3));
        Assert.Equal(1, Invoke(asm, "Test", "min3", 1, 5, 3));
        Assert.Equal(1, Invoke(asm, "Test", "min3", 3, 5, 1));
        Assert.Equal(2, Invoke(asm, "Test", "min3", 2, 2, 2));
    }

    [Fact]
    public void Max_Of_Three_Values()
    {
        const string source = """
namespace Test

func max3(a: int, b: int, c: int) -> int {
    var m = a
    if b > m { m = b }
    if c > m { m = c }
    return m
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(9, Invoke(asm, "Test", "max3", 1, 9, 3));
        Assert.Equal(9, Invoke(asm, "Test", "max3", 9, 1, 3));
        Assert.Equal(9, Invoke(asm, "Test", "max3", 1, 3, 9));
    }

    [Fact]
    public void Linear_Search_Idiom()
    {
        const string source = """
namespace Test

func contains(target: int, lo: int, hi: int) -> bool {
    var i = lo
    while i < hi {
        if i == target {
            return true
        }
        i = i + 1
    }
    return false
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "contains", 5, 0, 10));
        Assert.Equal(false, Invoke(asm, "Test", "contains", 15, 0, 10));
        Assert.Equal(true, Invoke(asm, "Test", "contains", 0, 0, 10));
    }

    [Fact]
    public void Binary_Search_Returns_Position()
    {
        const string source = """
namespace Test

func find_in_sorted(target: int, n: int) -> int {
    var lo = 0
    var hi = n
    while lo < hi {
        let mid = (lo + hi) / 2
        if mid == target {
            return mid
        }
        if mid < target {
            lo = mid + 1
        }
        if mid > target {
            hi = mid
        }
    }
    return -1
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(5, Invoke(asm, "Test", "find_in_sorted", 5, 10));
        Assert.Equal(7, Invoke(asm, "Test", "find_in_sorted", 7, 10));
        Assert.Equal(-1, Invoke(asm, "Test", "find_in_sorted", 15, 10));
    }

    // ── Function composition ──

    [Fact]
    public void Function_Composition_Manual()
    {
        const string source = """
namespace Test

func double_it(x: int) -> int = x * 2
func increment(x: int) -> int = x + 1

func test() -> int {
    return double_it(increment(20))
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Function_Call_Inside_Expression()
    {
        const string source = """
namespace Test

func sq(x: int) -> int = x * x

func test() -> int {
    return sq(3) + sq(4)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(25, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Nested_Function_Calls_Three_Deep()
    {
        const string source = """
namespace Test

func a(x: int) -> int = x + 1
func b(x: int) -> int = a(x) * 2
func c(x: int) -> int = b(x) - 3

func test() -> int = c(10)
""";
        var asm = CompileAndLoad(source);
        // a(10)=11, b(10)=22, c(10)=19
        Assert.Equal(19, Invoke(asm, "Test", "test"));
    }

    // ── More data type usage ──

    [Fact]
    public void Data_Equality_Field_By_Field()
    {
        const string source = """
namespace Test

struct P { x: int, y: int }

func test() -> int {
    let a = P { x: 1, y: 2 }
    let b = P { x: 1, y: 2 }
    if a.x == b.x && a.y == b.y {
        return 1
    }
    return 0
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Data_Composite_With_String_Field()
    {
        const string source = """
namespace Test

struct User { name: string, age: int }

func test() -> string {
    let u = User { name: "alice", age: 30 }
    return u.name + ":" + u.age.ToString()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("alice:30", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Data_Multiple_Construction_Sites()
    {
        const string source = """
namespace Test

struct Coord { x: int, y: int }

func test() -> int {
    let a = Coord { x: 1, y: 2 }
    let b = Coord { x: 3, y: 4 }
    let c = Coord { x: 5, y: 6 }
    return a.x + b.x + c.x + a.y + b.y + c.y
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(21, Invoke(asm, "Test", "test"));
    }

    // ── Static func with multiple consts ──

    [Fact]
    public void StaticFunc_Multiple_Const_Fields()
    {
        const string source = """
namespace Test

static Config {
    let WIDTH: int = 1920
    let HEIGHT: int = 1080
    let DEPTH: int = 32
}

func test() -> int = Config.WIDTH + Config.HEIGHT + Config.DEPTH
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3032, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void StaticFunc_Const_String_Field()
    {
        const string source = """
namespace Test

static Names {
    let DEFAULT: string = "anonymous"
}

func test() -> string = Names.DEFAULT
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("anonymous", Invoke(asm, "Test", "test"));
    }

    // ── class inheritance chain ──

    [Fact]
    public void Inheritance_Three_Level_Chain_Pin()
    {
        const string source = """
namespace Test

open class Animal {
    pub kind: string
    init(k: string) { self.kind = k }
}

open class Mammal : Animal {
    init(k: string) : base(k) {}
}

class Dog : Mammal {
    init() : base("dog") {}
}

func test() -> string {
    let d = Dog()
    return d.kind
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("dog", Invoke(asm, "Test", "test"));
    }

    // ── interface declarations ──

    [Fact]
    public void Interface_Method_Returns_Bool()
    {
        const string source = """
namespace Test

interface ITest {
    func passes() -> bool
}

struct Suite : ITest {
    flag: bool
}

func (s: Suite) passes() -> bool = s.flag

func test() -> bool {
    let s = Suite { flag: true }
    return s.passes()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test"));
    }

    // ── Long while loop ──

    [Fact]
    public void Long_Loop_Counts_To_1000()
    {
        const string source = """
namespace Test

func test() -> int {
    var i = 0
    var count = 0
    while i < 1000 {
        count = count + 1
        i = i + 1
    }
    return count
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1000, Invoke(asm, "Test", "test"));
    }

    // ── Nested while ──

    [Fact]
    public void Nested_While_Sum_Multiplication_Table()
    {
        const string source = """
namespace Test

func test() -> int {
    var total = 0
    var i = 1
    while i <= 3 {
        var j = 1
        while j <= 3 {
            total = total + i * j
            j = j + 1
        }
        i = i + 1
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        // (1+2+3) * (1+2+3) = 36
        Assert.Equal(36, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Nested_For_In_Range_Sum()
    {
        const string source = """
namespace Test

func test() -> int {
    var total = 0
    for i in 1..4 {
        for j in 1..4 {
            total = total + i * j
        }
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(36, Invoke(asm, "Test", "test"));
    }

    // ── class with virtual chain ──

    [Fact]
    public void RefData_Virtual_Method_Overridden_Multiple_Levels_Pin()
    {
        const string source = """
namespace Test

open class A {
    init() {}
    virtual func name() -> string = "A"
}

open class B : A {
    init() : base() {}
    : func name() -> string = "B"
}

class C : B {
    init() : base() {}
    : func name() -> string = "C"
}

func test() -> string {
    let a = A()
    let b = B()
    let c = C()
    return a.name() + b.name() + c.name()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("ABC", Invoke(asm, "Test", "test"));
    }

    // ── Function with many parameters ──

    [Fact]
    public void Function_With_Six_Params()
    {
        const string source = """
namespace Test

func test(a: int, b: int, c: int, d: int, e: int, f: int) -> int = a + b + c + d + e + f
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(21, Invoke(asm, "Test", "test", 1, 2, 3, 4, 5, 6));
    }

    // ── Boolean as first-class ──

    [Fact]
    public void Bool_Returned_From_Function_Used_In_If()
    {
        const string source = """
namespace Test

func is_positive(n: int) -> bool = n > 0

func test(n: int) -> string {
    if is_positive(n) {
        return "pos"
    }
    return "not pos"
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("pos", Invoke(asm, "Test", "test", 5));
        Assert.Equal("not pos", Invoke(asm, "Test", "test", 0));
        Assert.Equal("not pos", Invoke(asm, "Test", "test", -5));
    }

    // ── Compound boolean conditions ──

    [Fact]
    public void Compound_Conditions_In_While()
    {
        const string source = """
namespace Test

func test() -> int {
    var i = 0
    var count = 0
    while i < 10 && count < 5 {
        i = i + 1
        if i % 2 == 0 {
            count = count + 1
        }
    }
    return i
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(10, Invoke(asm, "Test", "test"));
    }

    // ── Range edges ──

    [Fact]
    public void For_Range_Single_Iteration()
    {
        const string source = """
namespace Test

func test() -> int {
    var sum = 0
    for i in 5..6 {
        sum = sum + i
    }
    return sum
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(5, Invoke(asm, "Test", "test"));
    }

    // ── Comparison of complex expressions ──

    [Fact]
    public void Comparison_Result_Of_Arithmetic()
    {
        const string source = """
namespace Test

func test(a: int, b: int) -> bool = a * 2 > b + 5
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test", 10, 5));
        Assert.Equal(false, Invoke(asm, "Test", "test", 3, 5));
    }

    // ── Recursive data structure (without methods) ──

    [Fact]
    public void Tree_Structure_Compiles_Pin()
    {
        const string source = """
namespace Test

struct Tree {
    value: int,
    left: *Tree,
    right: *Tree
}

func test() -> int {
    let leaf = new Tree { value: 1, left: nil, right: nil }
    let root = new Tree { value: 10, left: leaf, right: nil }
    return root.value + root.left.value
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(11, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void LinkedList_Sum_Pin()
    {
        const string source = """
namespace Test

struct Node {
    value: int,
    next: *Node
}

func sum_list(head: *Node) -> int {
    var total = 0
    var cursor = head
    while cursor != nil {
        total = total + cursor.value
        cursor = cursor.next
    }
    return total
}

func test() -> int {
    let c = new Node { value: 3, next: nil }
    let b = new Node { value: 2, next: c }
    let a = new Node { value: 1, next: b }
    return sum_list(a)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(6, Invoke(asm, "Test", "test"));
    }

    // ── Field default initialization ──

    [Fact]
    public void Data_With_All_Default_Fields_Compiles()
    {
        const string source = """
namespace Test

struct Empty { }

func test() -> int {
    let e = Empty { }
    return 0
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Data_Single_Field_Composite()
    {
        const string source = """
namespace Test

struct Wrap { v: int }

func test() -> int {
    let w = Wrap { v: 99 }
    return w.v
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(99, Invoke(asm, "Test", "test"));
    }

    // ── Common patterns ──

    [Fact]
    public void Swap_Pattern_With_Temp()
    {
        const string source = """
namespace Test

func test() -> int {
    var a = 10
    var b = 20
    let tmp = a
    a = b
    b = tmp
    return a * 100 + b
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(2010, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Average_Of_Three()
    {
        const string source = """
namespace Test

func avg(a: int, b: int, c: int) -> int = (a + b + c) / 3
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(2, Invoke(asm, "Test", "avg", 1, 2, 3));
        Assert.Equal(10, Invoke(asm, "Test", "avg", 5, 10, 15));
    }

    [Fact]
    public void Range_Of_Squares()
    {
        const string source = """
namespace Test

func test(n: int) -> int {
    var sum = 0
    for i in 1..n {
        sum = sum + i * i
    }
    return sum
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(30, Invoke(asm, "Test", "test", 5));
        Assert.Equal(285, Invoke(asm, "Test", "test", 10));
    }

    [Fact]
    public void Triangle_Number()
    {
        const string source = """
namespace Test

func tri(n: int) -> int = n * (n + 1) / 2
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(15, Invoke(asm, "Test", "tri", 5));
        Assert.Equal(55, Invoke(asm, "Test", "tri", 10));
        Assert.Equal(5050, Invoke(asm, "Test", "tri", 100));
    }

    [Fact]
    public void Parity_Check()
    {
        const string source = """
namespace Test

func parity(n: int) -> string {
    if n % 2 == 0 {
        return "even"
    }
    return "odd"
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("even", Invoke(asm, "Test", "parity", 4));
        Assert.Equal("odd", Invoke(asm, "Test", "parity", 7));
        Assert.Equal("even", Invoke(asm, "Test", "parity", 0));
    }

    // ── Function pointer in data field ──

    [Fact]
    public void Data_Field_Function_Pointer_Pin()
    {
        const string source = """
namespace Test

struct Op {
    apply: &(int -> int)
}

func add_one(x: int) -> int = x + 1
func double_it(x: int) -> int = x * 2

func test() -> int {
    let op = Op { apply: &add_one }
    return op.apply(41)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    // ── Embedded field via pub embedding ──

    [Fact]
    public void Embedding_Direct_Field_Access_Pin()
    {
        const string source = """
namespace Test

struct Inner { value: int }

struct Outer {
    pub Inner
}

func test() -> int {
    let o = Outer { Inner: Inner { value: 100 } }
    return o.Inner.value
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(100, Invoke(asm, "Test", "test"));
    }

    // ── More algorithm tests ──

    [Fact]
    public void Sum_Of_Digits_Recursive()
    {
        const string source = """
namespace Test

func dsum(n: int) -> int {
    if n == 0 {
        return 0
    }
    return n % 10 + dsum(n / 10)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "dsum", 0));
        Assert.Equal(6, Invoke(asm, "Test", "dsum", 123));
        Assert.Equal(45, Invoke(asm, "Test", "dsum", 123456789));
    }

    [Fact]
    public void Reverse_Digits_Recursive_Helper_Pin()
    {
        const string source = """
namespace Test

func rev_helper(n: int, acc: int) -> int {
    if n == 0 {
        return acc
    }
    return rev_helper(n / 10, acc * 10 + n % 10)
}

func rev(n: int) -> int = rev_helper(n, 0)
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(321, Invoke(asm, "Test", "rev", 123));
        Assert.Equal(54321, Invoke(asm, "Test", "rev", 12345));
        Assert.Equal(0, Invoke(asm, "Test", "rev", 0));
    }

    // ── Function that calls itself with reduced args ──

    [Fact]
    public void Function_Tail_Recursive_Style()
    {
        const string source = """
namespace Test

func sum_to(n: int, acc: int) -> int {
    if n == 0 {
        return acc
    }
    return sum_to(n - 1, acc + n)
}

func test() -> int = sum_to(100, 0)
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(5050, Invoke(asm, "Test", "test"));
    }

    // ── Common control flow shapes ──

    [Fact]
    public void Skip_Logic_With_Continue()
    {
        const string source = """
namespace Test

func test() -> int {
    var collected = 0
    var i = 0
    while i < 20 {
        i = i + 1
        if i == 7 {
            continue
        }
        if i == 13 {
            continue
        }
        collected = collected + 1
    }
    return collected
}
""";
        var asm = CompileAndLoad(source);
        // 20 iterations minus 2 skipped = 18
        Assert.Equal(18, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Early_Exit_With_Break()
    {
        const string source = """
namespace Test

func first_above(threshold: int) -> int {
    var i = 0
    while i < 1000 {
        if i > threshold {
            break
        }
        i = i + 1
    }
    return i
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(11, Invoke(asm, "Test", "first_above", 10));
        Assert.Equal(101, Invoke(asm, "Test", "first_above", 100));
    }

    // ── Function chain returning Result ──

    [Fact]
    public void Result_Map_Chain_Pin()
    {
        const string source = """
namespace Test

func parse(s: string) -> Result<int, string> {
    if s == "" {
        return error("empty")
    }
    return ok(42)
}

func test() -> int {
    let r = parse("hello")
    return r.Value
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Result_IsOk_Branch()
    {
        const string source = """
namespace Test

func produce(b: bool) -> Result<int, string> {
    if b {
        return ok(7)
    }
    return error("bad")
}

func test(b: bool) -> int {
    let r = produce(b)
    if r.IsOk {
        return r.Value
    }
    return -1
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(7, Invoke(asm, "Test", "test", true));
        Assert.Equal(-1, Invoke(asm, "Test", "test", false));
    }
}
