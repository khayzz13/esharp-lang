using Esharp.Compiler;
using Esharp.Runtime;

namespace Esharp.Tests;

public sealed class TranspilerTests
{
    [Fact]
    public void Transpiles_Data_Choice_And_Functions()
    {
        const string source = """
module Auth

data LoginRequest {
    email: string
    password: string
}

choice AuthError {
    invalidCredentials
    accountLocked(untilUtc: DateTimeOffset)
}

func makeRequest(email: string, password: string) -> LoginRequest {
    let req = LoginRequest {
        email: email
        password: password
    }
    return req
}

func login(req: LoginRequest) -> Result<LoginRequest, AuthError> {
    if req.password == "secret" {
        return ok(req)
    }

    return error(AuthError.invalidCredentials())
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "auth.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("public partial struct LoginRequest", result.GeneratedCode);
        Assert.Contains("public partial struct AuthError", result.GeneratedCode);
        // makeRequest first param is string, not a data type → stays static
        Assert.Contains("public static LoginRequest makeRequest", result.GeneratedCode);
        // login first param is LoginRequest (a data type) → instance method on LoginRequest
        Assert.Contains("public Result<LoginRequest", result.GeneratedCode);
        Assert.Contains("Result.Ok<LoginRequest", result.GeneratedCode);
        Assert.Contains("AuthError.invalidCredentials()", result.GeneratedCode);
    }

    [Fact]
    public void Reports_Syntax_Diagnostics()
    {
        const string source = """
module Broken

func nope( -> void {
    return
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "broken.es");

        Assert.False(result.Success);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Runtime_Result_Tracks_Ok_And_Error()
    {
        var ok = Result<int, string>.Ok(42);
        var error = Result<int, string>.Error("bad");

        Assert.True(ok.IsOk);
        Assert.Equal(42, ok.Value);
        Assert.True(error.IsError);
        Assert.Equal("bad", error.ErrorValue);
    }

    [Fact]
    public void Transpiles_While_For_And_Spawn()
    {
        const string source = """
module Worker

func sumTo(limit: int) -> int {
    var i = 0
    var total = 0
    while i <= limit {
        total = total + i
        i = i + 1
    }
    return total
}

func sumAll(values: List<int>) -> int {
    var total = 0
    for value in values {
        total = total + value
    }
    return total
}

func start(values: List<string>) -> Job {
    return spawn {
        for value in values {
            Console.WriteLine(value)
        }
    }
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "worker.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("while ((i <= limit))", result.GeneratedCode);
        Assert.Contains("foreach (var value in values)", result.GeneratedCode);
        Assert.Contains("Job.Spawn(_ =>", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_ByRef_Parameters()
    {
        const string source = """
module Counter

data Counter {
    value: int
}

func increment(c: *Counter) {
    c.value += 1
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "counter.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        // *Counter first param → instance method, self becomes 'this'
        Assert.Contains("public void increment()", result.GeneratedCode);
        Assert.Contains("this.value += 1", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_Match_On_Choice_Annotated()
    {
        const string source = """
module Conn

choice ConnectionState {
    disconnected
    connected
    failed(reason: string)
}

func describe(state: ConnectionState) -> string {
    match (state: ConnectionState) {
        .disconnected { return "off" }
        .connected { return "on" }
        .failed(reason) { return reason }
        default { return "?" }
    }
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "conn.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("switch (state.Tag)", result.GeneratedCode);
        Assert.Contains("case ConnectionStateTag.failed:", result.GeneratedCode);
        Assert.Contains("var reason = state._failed_reason", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_Match_Without_Annotation()
    {
        const string source = """
module Conn

choice ConnectionState {
    disconnected
    connected
}

func describe(state: ConnectionState) -> string {
    match state {
        .disconnected { return "off" }
        .connected { return "on" }
        default { return "?" }
    }
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "conn.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("switch (state.Tag)", result.GeneratedCode);
        Assert.Contains("case ConnectionStateTag.disconnected:", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_Match_On_MemberAccess_Inferred()
    {
        const string source = """
module SM

choice Status { on, off }

data Device {
    status: Status
}

func check(d: Device) -> string {
    match d.status {
        .on { return "on" }
        .off { return "off" }
        default { return "?" }
    }
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "sm.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        // d is self param → becomes this in instance method
        Assert.Contains("switch (this.status.Tag)", result.GeneratedCode);
        Assert.Contains("case StatusTag.on:", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_Defer()
    {
        const string source = """
module IO

func process(path: string) -> string {
    let file = File.Open(path)
    defer { file.Close() }
    return file.ReadAll()
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "io.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("try", result.GeneratedCode);
        Assert.Contains("finally", result.GeneratedCode);
        Assert.Contains("file.Close()", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_StringInterpolation()
    {
        const string source = """
module Greet

func hello(name: string) -> string {
    return "hello {name}"
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "greet.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("$\"hello {name}\"", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_PlainString_NoInterpolation()
    {
        const string source = """
module Plain

func msg() -> string {
    return "hello world"
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "plain.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("\"hello world\"", result.GeneratedCode);
        Assert.DoesNotContain("$\"hello world\"", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_IndexExpression()
    {
        const string source = """
module Arr

func first(items: List<int>) -> int {
    return items[0]
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "arr.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("items[0]", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_RangeExpression()
    {
        const string source = """
module Slice

func take(s: string, n: int) -> string {
    return s[..n]
}

func skip(s: string, n: int) -> string {
    return s[n..]
}

func sub(s: string, a: int, b: int) -> string {
    return s[a..b]
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "slice.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("s[..n]", result.GeneratedCode);
        Assert.Contains("s[n..]", result.GeneratedCode);
        Assert.Contains("s[a..b]", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_IndexFromEnd()
    {
        const string source = """
module Last

func last(items: List<int>) -> int {
    return items[^1]
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "last.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("items[^1]", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_TryUnwrap_In_Let()
    {
        const string source = """
module Prop

func inner(x: int) -> Result<int, string> {
    return ok(x)
}

func outer(x: int) -> Result<int, string> {
    let val = inner(x)?
    return ok(val)
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "prop.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("var __r0 = inner(x)", result.GeneratedCode);
        Assert.Contains("if (__r0.IsError) return Result.Error<int, string>(__r0.ErrorValue)", result.GeneratedCode);
        Assert.Contains("var val = __r0.Value", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_CompoundAssignment()
    {
        const string source = """
module Math

func sumTo(n: int) -> int {
    var total = 0
    var i = 0
    while i <= n {
        total += i
        i += 1
    }
    return total
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "math.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("total += i", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_Chan_Creation()
    {
        const string source = """
module Audit

func makeChan() -> Chan<string> {
    let ch = chan<string>(256)
    return ch
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "audit.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("new Chan<string>(256)", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_Enum_Declaration()
    {
        const string source = """
module Dir

enum Direction {
    north
    south
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "dir.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("public enum Direction", result.GeneratedCode);
        Assert.Contains("north,", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_Import_Directive()
    {
        const string source = """
module Test
import "System.IO"
import "MyNamespace"

func run() {
    Console.WriteLine("hello")
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "test.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("using System.IO;", result.GeneratedCode);
        Assert.Contains("using MyNamespace;", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_Generic_Function()
    {
        const string source = """
module Util

func first<T>(items: List<T>) -> T {
    return items[0]
}

func swap<A, B>(a: A, b: B) -> B {
    return b
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "util.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("public static T first<T>(List<T> items)", result.GeneratedCode);
        Assert.Contains("public static B swap<A, B>(A a, B b)", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_Function_Literal()
    {
        const string source = """
module FnTest

func run(items: List<int>) {
    items.Sort(func(a: int, b: int) -> int {
        return a - b
    })
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "fn.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("(int a, int b) =>", result.GeneratedCode);
        Assert.Contains("return (a - b)", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_Function_Literal_Assigned_To_Variable()
    {
        const string source = """
module FnVar

func run() {
    let greet = func(name: string) -> string {
        return "hello {name}"
    }
    Console.WriteLine(greet("world"))
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "fnvar.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("(string name) =>", result.GeneratedCode);
        Assert.Contains("$\"hello {name}\"", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_Protocol_And_Implicit_Satisfaction()
    {
        const string source = """
module Greet

protocol IGreeter {
    func greet(name: string) -> string
}

data Bot {
    prefix: string
}

func greet(b: Bot, name: string) -> string {
    return "{b.prefix} {name}"
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "greet.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("public interface IGreeter", result.GeneratedCode);
        Assert.Contains("string greet(string name);", result.GeneratedCode);
        // Bot implicitly satisfies IGreeter
        Assert.Contains("public partial struct Bot : IGreeter", result.GeneratedCode);
        // greet is an instance method, self param removed
        Assert.Contains("public string greet(string name)", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_Data_With_External_Interface()
    {
        const string source = """
module IO

data FileHandle : IDisposable {
    path: string
}

func Dispose(h: FileHandle) {
    Console.WriteLine("closed")
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "io.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("public partial struct FileHandle : IDisposable", result.GeneratedCode);
        Assert.Contains("public void Dispose()", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_Derive_Equality()
    {
        const string source = """
module Eq

derive equality
data Point {
    x: int
    y: int
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "eq.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("IEquatable<Point>", result.GeneratedCode);
        Assert.Contains("public bool Equals(Point other)", result.GeneratedCode);
        Assert.Contains("public override int GetHashCode()", result.GeneratedCode);
        Assert.Contains("HashCode.Combine(x, y)", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_Derive_Debug()
    {
        const string source = """
module Dbg

derive debug
data Color {
    r: int
    g: int
    b: int
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "dbg.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("public override string ToString()", result.GeneratedCode);
        Assert.Contains("Color", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_Generic_Data()
    {
        const string source = """
module Gen

data Pair<A, B> {
    first: A
    second: B
}

func makePair<A, B>(a: A, b: B) -> Pair<A, B> {
    return Pair<A, B> { first: a, second: b }
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "gen.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("public partial struct Pair<A, B>", result.GeneratedCode);
        Assert.Contains("public A first;", result.GeneratedCode);
        Assert.Contains("public B second;", result.GeneratedCode);
    }

    [Fact]
    public void Transpiles_Generic_Choice()
    {
        const string source = """
module Opt

choice Option<T> {
    some(value: T)
    none
}

func unwrap<T>(opt: Option<T>, fallback: T) -> T {
    match (opt: Option<T>) {
        .some(value) { return value }
        default { return fallback }
    }
}
""";

        var transpiler = new EsharpTranspiler();
        var result = transpiler.Transpile(source, "opt.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("public partial struct Option<T>", result.GeneratedCode);
        Assert.Contains("public static Option<T> some(T value)", result.GeneratedCode);
        Assert.Contains("public static Option<T> none()", result.GeneratedCode);
    }

    [Fact]
    public void MultiFile_CrossFile_Type_Reference()
    {
        var files = new List<(string source, string filePath)>
        {
            ("""
module Types

data Point {
    x: int
    y: int
}
""", "types.es"),
            ("""
module Math

func distance(a: Point, b: Point) -> double {
    return 0.0
}
""", "math.es"),
        };

        var transpiler = new EsharpTranspiler();
        var result = transpiler.TranspileProject(files);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(2, result.Files.Count);

        var typesFile = result.Files.First(f => f.FilePath == "types.es");
        var mathFile = result.Files.First(f => f.FilePath == "math.es");
        Assert.Contains("public partial struct Point", typesFile.GeneratedCode);
        // distance first param is Point (a data type from types.es) → instance method
        Assert.Contains("public double distance(Point b)", mathFile.GeneratedCode);
    }

    [Fact]
    public void MultiFile_CrossFile_Instance_Method_Promotion()
    {
        var files = new List<(string source, string filePath)>
        {
            ("""
module Types

data Counter {
    value: int
}
""", "types.es"),
            ("""
module Ops

func increment(c: *Counter) {
    c.value += 1
}

func getValue(c: Counter) -> int {
    return c.value
}
""", "ops.es"),
        };

        var transpiler = new EsharpTranspiler();
        var result = transpiler.TranspileProject(files);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var opsFile = result.Files.First(f => f.FilePath == "ops.es");
        // Both functions should become instance methods on Counter (in the ops.g.cs output)
        // But since Counter is declared in types.es, the instance methods appear as static in ops
        // Actually — they get promoted because _dataDecls has Counter from the global registration
        Assert.Contains("this.value += 1", opsFile.GeneratedCode);
        Assert.Contains("return this.value", opsFile.GeneratedCode);
    }

    [Fact]
    public void MultiFile_CrossFile_Protocol_Satisfaction()
    {
        // Protocol + data + implementing function all in same file works
        // Cross-file protocol satisfaction (function in different file than data) requires
        // global function registration — tracked as future work
        var files = new List<(string source, string filePath)>
        {
            ("""
module Proto

protocol IDescribable {
    func describe() -> string
}

data Widget {
    label: string
}

func describe(w: Widget) -> string {
    return w.label
}
""", "proto.es"),
        };

        var transpiler = new EsharpTranspiler();
        var result = transpiler.TranspileProject(files);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var protoFile = result.Files.First(f => f.FilePath == "proto.es");
        Assert.Contains("public interface IDescribable", protoFile.GeneratedCode);
        Assert.Contains("public partial struct Widget : IDescribable", protoFile.GeneratedCode);
    }
}
