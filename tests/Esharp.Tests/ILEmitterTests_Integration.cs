// Style note: E# source uses readable """ raw-string blocks (these double as the
// E# corpus) — do not collapse them into inline \n-escaped one-liners.
//
// Integration-scale coverage: a full recursive-descent JSON parser, E# code that
// reflects over its own emitted assembly, an executable entry point (func main on
// a Console-kind module), and cross-namespace type/function resolution across two
// compilation units. These exercise many language seams composed together rather
// than one feature in isolation.
using System.Reflection;
using Esharp.Diagnostics;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Mono.Cecil;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

public sealed class ILEmitterTests_Integration
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static int _asmCounter;

    static object? Run(string src, string method = "go", params object?[] args)
        => EsHarness.Run(src, method, args);

    // Bind several source files through one shared binder (cross-file / cross-namespace
    // visibility), emit a single library assembly, and load it. Mirrors the canonical
    // multi-file harness in ILEmitterTests.cs.
    static (Assembly Asm, IReadOnlyList<Diagnostic> Diagnostics) CompileMulti(params string[] sources)
    {
        var asmName = $"EsIntegration_{Interlocked.Increment(ref _asmCounter)}";
        var binder = new Esharp.Binder.Binder();
        var parsed = new List<Esharp.Syntax.CompilationUnitSyntax>();
        for (var i = 0; i < sources.Length; i++)
        {
            var parser = new Parser(sources[i], $"test{i}.es");
            var syntax = parser.ParseCompilationUnit();
            Assert.Empty(parser.Diagnostics);
            parsed.Add(syntax);
        }
        foreach (var syntax in parsed)
            binder.RegisterTypes(syntax);
        foreach (var syntax in parsed)
            binder.RegisterSignatures(syntax);
        var bound = parsed.Select(binder.BindUnit).ToList();

        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        var ilDiags = EsHarness.EmitBoundToFile(binder, bound, asmName, path);
        var all = binder.Diagnostics.Concat(ilDiags).ToList();
        if (all.Any(d => d.Severity == DiagnosticSeverity.Error))
            return (null!, all);
        return (Assembly.LoadFrom(path), all);
    }

    static object? Invoke(Assembly asm, string typeName, string method, params object?[] args)
    {
        var type = asm.GetType($"Test.{typeName}") ?? throw new Exception($"Type Test.{typeName} not found");
        var m = type.GetMethod(method, AnyStatic) ?? throw new Exception($"Method {method} not found");
        return m.Invoke(null, args.Length == 0 ? null : args);
    }

    // ════════════════════════════════════════════════════════════════════
    // 1. JSON parser — recursive descent over a ref-union AST with Result/?
    // ════════════════════════════════════════════════════════════════════

    // A JSON value subset: null, bool, integer number, string, and (recursive)
    // array. Built as a ref choice so the array variant can hold List<Json> and
    // the structure can nest arbitrarily.
    const string JsonParser = """
namespace Test

ref union Json {
    jnull
    jbool(b: bool)
    jnum(n: int)
    jstr(s: string)
    jarr(items: List<Json>)
}

struct P {
    var src: string
    var pos: int
}

func atEnd(p: *P) -> bool {
    return p.pos >= p.src.Length
}

func skipWs(p: *P) {
    while !atEnd(p) {
        let c = p.src[p.pos]
        if c == ' ' || c == '\t' || c == '\n' || c == '\r' {
            p.pos += 1
        } else {
            return
        }
    }
}

func parseValue(p: *P) -> Result<Json, string> {
    skipWs(p)
    if atEnd(p) { return error("unexpected end of input") }
    let c = p.src[p.pos]
    if c == '[' { return parseArray(p) }
    if c == '"' { return parseString(p) }
    if c == 't' || c == 'f' { return parseBool(p) }
    if c == 'n' { return parseNull(p) }
    if c == '-' || char.IsDigit(c) { return parseNumber(p) }
    return error("unexpected character")
}

func parseArray(p: *P) -> Result<Json, string> {
    p.pos += 1
    let items = List<Json>()
    skipWs(p)
    if !atEnd(p) && p.src[p.pos] == ']' {
        p.pos += 1
        return ok(Json.jarr(items))
    }
    while true {
        let v = parseValue(p)?
        items.Add(v)
        skipWs(p)
        if atEnd(p) { return error("unterminated array") }
        let sep = p.src[p.pos]
        if sep == ',' {
            p.pos += 1
        } else if sep == ']' {
            p.pos += 1
            return ok(Json.jarr(items))
        } else {
            return error("expected ',' or ']'")
        }
    }
    return error("unreachable")
}

func parseString(p: *P) -> Result<Json, string> {
    p.pos += 1
    let start = p.pos
    while !atEnd(p) && p.src[p.pos] != '"' {
        p.pos += 1
    }
    if atEnd(p) { return error("unterminated string") }
    let text = p.src.Substring(start, p.pos - start)
    p.pos += 1
    return ok(Json.jstr(text))
}

func parseNumber(p: *P) -> Result<Json, string> {
    let start = p.pos
    if p.src[p.pos] == '-' { p.pos += 1 }
    while !atEnd(p) && char.IsDigit(p.src[p.pos]) {
        p.pos += 1
    }
    let text = p.src.Substring(start, p.pos - start)
    return ok(Json.jnum(int.Parse(text)))
}

func parseBool(p: *P) -> Result<Json, string> {
    if p.src[p.pos] == 't' {
        p.pos += 4
        return ok(Json.jbool(true))
    }
    p.pos += 5
    return ok(Json.jbool(false))
}

func parseNull(p: *P) -> Result<Json, string> {
    p.pos += 4
    return ok(Json.jnull())
}

func parse(src: string) -> Result<Json, string> {
    var p: *P = new P { src: src, pos: 0 }
    let v = parseValue(p)?
    return ok(v)
}

// Recursive walks over the parsed tree.
func sumNumbers(j: Json) -> int {
    match j {
        .jnull { return 0 }
        .jbool(node) { return 0 }
        .jnum(node) { return node.n }
        .jstr(node) { return 0 }
        .jarr(node) {
            var total = 0
            for item in node.items {
                total += sumNumbers(item)
            }
            return total
        }
    }
    return 0
}

func countNodes(j: Json) -> int {
    match j {
        .jarr(node) {
            var count = 1
            for item in node.items {
                count += countNodes(item)
            }
            return count
        }
        default { return 1 }
    }
    return 0
}

func depth(j: Json) -> int {
    match j {
        .jarr(node) {
            var best = 0
            for item in node.items {
                let d = depth(item)
                if d > best { best = d }
            }
            return best + 1
        }
        default { return 0 }
    }
    return 0
}
""";

    [Fact]
    public void Json_SumsNestedNumbers()
    {
        Assert.Equal(10, Run(JsonParser + """

func go() -> int {
    let r = parse("[1, [2, 3], 4]")
    if r.IsError { return -1 }
    return sumNumbers(r.Value)
}
"""));
    }

    [Fact]
    public void Json_SumIgnoresNonNumbers()
    {
        Assert.Equal(6, Run(JsonParser + """

func go() -> int {
    let r = parse("[1, true, \"hi\", null, [2, 3]]")
    if r.IsError { return -1 }
    return sumNumbers(r.Value)
}
"""));
    }

    [Fact]
    public void Json_CountsAllNodes()
    {
        Assert.Equal(6, Run(JsonParser + """

func go() -> int {
    let r = parse("[1, [2, 3], 4]")
    if r.IsError { return -1 }
    return countNodes(r.Value)
}
"""));
        // outer array + 1 + inner array + 2 + 3 + 4 = 6 nodes
    }

    [Fact]
    public void Json_ComputesNestingDepth()
    {
        Assert.Equal(3, Run(JsonParser + """

func go() -> int {
    let r = parse("[1, [2, [3, 4]]]")
    if r.IsError { return -1 }
    return depth(r.Value)
}
"""));
    }

    [Fact]
    public void Json_ParsesScalarString()
    {
        Assert.Equal("hello", Run(JsonParser + """

func go() -> string {
    let r = parse("\"hello\"")
    if r.IsError { return "ERR" }
    match r.Value {
        .jstr(node) { return node.s }
        default { return "?" }
    }
    return "?"
}
""", "go"));
    }

    [Fact]
    public void Json_EmptyArray()
    {
        // just the empty outer array → 1 node (the array itself), 0 numbers
        Assert.Equal(1, Run(JsonParser + """

func go() -> int {
    let r = parse("[]")
    if r.IsError { return -1 }
    return countNodes(r.Value)
}
"""));
    }

    [Fact]
    public void Json_UnterminatedArrayIsError()
    {
        Assert.Equal("unterminated array", Run(JsonParser + """

func go() -> string {
    let r = parse("[1, 2")
    return r.IsError ? r.Error : "ok"
}
""", "go"));
    }

    [Fact]
    public void Json_UnterminatedStringIsError()
    {
        Assert.Equal("unterminated string", Run(JsonParser + """

func go() -> string {
    let r = parse("\"abc")
    return r.IsError ? r.Error : "ok"
}
""", "go"));
    }

    [Fact]
    public void Json_NegativeNumbers()
    {
        Assert.Equal(-5, Run(JsonParser + """

func go() -> int {
    let r = parse("[-2, -3]")
    if r.IsError { return 999 }
    return sumNumbers(r.Value)
}
"""));
    }

    [Fact]
    public void Json_WhitespaceTolerant()
    {
        Assert.Equal(6, Run(JsonParser + """

func go() -> int {
    let r = parse("  [ 1 ,\t2 ,\n 3 ]  ")
    if r.IsError { return -1 }
    return sumNumbers(r.Value)
}
"""));
    }

    // ════════════════════════════════════════════════════════════════════
    // 2. Reflection — E# code reflecting over its own emitted types/assembly
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Reflection_RuntimeTypeName()
    {
        Assert.Equal("Widget", Run("""
namespace Test
pub class Widget {
    id: int
    init(id: int) { self.id = id }
}
func go() -> string {
    let w = Widget(7)
    return w.GetType().Name
}
""", "go"));
    }

    [Fact]
    public void Reflection_TypeNamespace()
    {
        Assert.Equal("Test", Run("""
namespace Test
pub class Widget {
    id: int
    init(id: int) { self.id = id }
}
func go() -> string {
    let w = Widget(1)
    return w.GetType().Namespace
}
""", "go"));
    }

    [Fact]
    public void Reflection_ValueDataTypeName()
    {
        Assert.Equal("Point", Run("""
namespace Test
struct Point { x: int, y: int }
func go() -> string {
    let p = Point { x: 1, y: 2 }
    return p.GetType().Name
}
""", "go"));
    }

    [Fact]
    public void Reflection_FindsDeclaredMethod()
    {
        Assert.Equal(true, Run("""
namespace Test
using "System.Reflection"
pub class Widget {
    id: int
    init(id: int) { self.id = id }
    pub func describe() -> string = "w{self.id}"
}
func go() -> bool {
    let w = Widget(1)
    let m = w.GetType().GetMethod("describe")
    return m != nil
}
"""));
    }

    [Fact]
    public void Reflection_AssemblyContainsTypes()
    {
        Assert.Equal(true, Run("""
namespace Test
using "System.Reflection"
struct Marker { v: int }
func go() -> bool {
    let t = Marker { v: 1 }
    let asm = t.GetType().Assembly
    return asm.GetTypes().Length > 0
}
"""));
    }

    [Fact]
    public void Reflection_InvokeMethodDynamically()
    {
        Assert.Equal("w42", Run("""
namespace Test
using "System.Reflection"
pub class Widget {
    id: int
    init(id: int) { self.id = id }
    pub func describe() -> string = "w{self.id}"
}
func go() -> string {
    let w = Widget(42)
    let m = w.GetType().GetMethod("describe")
    let result = m.Invoke(w, nil)
    return result.ToString()
}
""", "go"));
    }

    [Fact]
    public void Reflection_BaseTypeOfRefData()
    {
        // class emits as a sealed class; its base is System.Object.
        Assert.Equal("Object", Run("""
namespace Test
pub class Widget {
    id: int
    init(id: int) { self.id = id }
}
func go() -> string {
    let w = Widget(1)
    return w.GetType().BaseType.Name
}
""", "go"));
    }

    // ════════════════════════════════════════════════════════════════════
    // 3. Executable entry point — func main on a Console-kind module
    // ════════════════════════════════════════════════════════════════════

    // Compile a program to a Console-kind assembly (the exe path), assert the
    // entry point is wired to `main`, then load and run it. A `class Program`
    // holds the application logic; `func main` is the static entry that drives it.
    static (AssemblyDefinition Cecil, Assembly Loaded, string OutPath) CompileExe(string src)
    {
        var asmName = $"EsExe_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(src, "main.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var (assembly, diags) = EsHarness.EmitBound(binder, 
            new[] { bound }, asmName,
            debugSymbols: false, referencePaths: null,
            internalsVisibleTo: null, externalSymbols: null,
            outputKind: ILOutputKind.Console);
        Assert.DoesNotContain(diags, d => d.Severity == DiagnosticSeverity.Error);

        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        assembly.Write(path);
        return (assembly, Assembly.LoadFrom(path), path);
    }

    [Fact]
    public void Exe_EntryPointIsWiredToMain()
    {
        var (cecil, _, _) = CompileExe("""
namespace Test

pub class Program {
    var counter: int
    init() { self.counter = 0 }
    func run() -> int {
        for i in 1..5 {
            self.counter += i
        }
        return self.counter
    }
}

func main() -> int {
    let p = Program()
    return p.run()
}
""");
        Assert.NotNull(cecil.EntryPoint);
        Assert.Equal("main", cecil.EntryPoint.Name);
        Assert.Equal(ModuleKind.Console, cecil.MainModule.Kind);
        cecil.Dispose();
    }

    [Fact]
    public void Exe_MainRunsProgramAndReturnsExitCode()
    {
        var (cecil, loaded, _) = CompileExe("""
namespace Test

pub class Program {
    var counter: int
    init() { self.counter = 0 }
    func run() -> int {
        for i in 1..5 {
            self.counter += i
        }
        return self.counter
    }
}

func main() -> int {
    let p = Program()
    return p.run()
}
""");
        cecil.Dispose();
        var result = Invoke(loaded, "Test", "main");
        Assert.Equal(10, result);   // 1+2+3+4 = 10
    }

    [Fact]
    public void Exe_MainProducesConsoleOutput()
    {
        var (cecil, loaded, _) = CompileExe("""
namespace Test

func main() -> int {
    let total = 7 * 6
    Console.WriteLine("answer={total}")
    return total
}
""");
        cecil.Dispose();
        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            var result = Invoke(loaded, "Test", "main");
            Assert.Equal(42, result);
        }
        finally
        {
            Console.SetOut(original);
        }
        Assert.Contains("answer=42", buffer.ToString());
    }

    [Fact]
    public void Library_NoEntryPointEvenWithMain()
    {
        // The same `main` in a library (Dll) module is just a regular function —
        // no entry point is wired. This is the EsHarness/EmitToFile default.
        var asm = EsHarness.Compile("""
namespace Test
func main() -> int { return 5 }
""");
        var module = ModuleDefinition.ReadModule(asm.Location);
        Assert.Equal(ModuleKind.Dll, module.Kind);
        Assert.Null(module.EntryPoint);
    }

    // ════════════════════════════════════════════════════════════════════
    // 4. Cross-namespace resolution — two units, distinct namespaces
    // ════════════════════════════════════════════════════════════════════

    // A `data` type defined in `namespace Geometry` is brought into bare scope in
    // `namespace Test` via `using "Geometry"` (C#-like: cross-namespace access needs
    // the import or a qualifier — bare visibility is not implicit).
    [Fact]
    public void CrossNamespace_TypeVisibleViaUsing()
    {
        const string geometry = """
namespace Geometry

struct Point {
    x: int
    y: int
}
""";
        const string app = """
namespace Test
using "Geometry"

func go() -> int {
    let p = Point { x: 3, y: 4 }
    return p.x + p.y
}
""";
        var (asm, diags) = CompileMulti(geometry, app);
        Assert.Empty(diags);
        Assert.Equal(7, Invoke(asm, "Test", "go"));
    }

    // The same type referenced cross-namespace by qualifier (`Geometry.Point`)
    // instead of an import — both forms resolve, mirroring C#.
    [Fact]
    public void CrossNamespace_TypeVisibleByQualifier()
    {
        const string geometry = """
namespace Geometry

struct Point {
    x: int
    y: int
}
""";
        const string app = """
namespace Test

func go() -> int {
    let p = Geometry.Point { x: 3, y: 4 }
    return p.x + p.y
}
""";
        var (asm, diags) = CompileMulti(geometry, app);
        Assert.Empty(diags);
        Assert.Equal(7, Invoke(asm, "Test", "go"));
    }

    // Bare cross-namespace access WITHOUT a `using` or qualifier is a hard error
    // (ES2150) — the spec's "no implicit cross-namespace visibility" rule.
    [Fact]
    public void CrossNamespace_BareWithoutUsing_IsError()
    {
        const string geometry = """
namespace Geometry

struct Point {
    x: int
    y: int
}
""";
        const string app = """
namespace Test

func go() -> int {
    let p = Point { x: 3, y: 4 }
    return p.x + p.y
}
""";
        var (_, diags) = CompileMulti(geometry, app);
        Assert.Contains(diags, d => d.Message.Contains("ES2150"));
    }

    // An instance method promoted onto a type in one namespace is callable from
    // another namespace — promotion attaches to the type, not the namespace.
    [Fact]
    public void CrossNamespace_PromotedInstanceMethod()
    {
        const string geometry = """
namespace Geometry

struct Point {
    x: int
    y: int
}

func (p: Point) area() -> int {
    return p.x * p.y
}
""";
        const string app = """
namespace Test
using "Geometry"

func go() -> int {
    let p = Point { x: 5, y: 6 }
    return p.area()
}
""";
        var (asm, diags) = CompileMulti(geometry, app);
        Assert.Empty(diags);
        Assert.Equal(30, Invoke(asm, "Test", "go"));
    }

    // A free function in another namespace, called qualified as `Geometry.add(...)`.
    [Fact]
    public void CrossNamespace_QualifiedFreeFunctionCall()
    {
        const string mathns = """
namespace MathNs

func add(a: int, b: int) -> int {
    return a + b
}
""";
        const string app = """
namespace Test

func go() -> int {
    return MathNs.add(40, 2)
}
""";
        var (asm, diags) = CompileMulti(mathns, app);
        Assert.Empty(diags);
        Assert.Equal(42, Invoke(asm, "Test", "go"));
    }

    // A choice declared in one namespace, constructed and matched in another.
    [Fact]
    public void CrossNamespace_ChoiceConstructedAndMatched()
    {
        const string model = """
namespace Model

union Status {
    active
    suspended(reason: string)
}
""";
        const string app = """
namespace Test
using "Model"

func describe(s: Status) -> string {
    match s {
        .active { return "active" }
        .suspended(node) { return "suspended" }
    }
    return "?"
}

func go() -> string {
    let s = Status.active()
    return describe(s)
}
""";
        var (asm, diags) = CompileMulti(model, app);
        Assert.Empty(diags);
        Assert.Equal("active", Invoke(asm, "Test", "go"));
    }

    // An enum in one namespace, referenced in a match in another.
    [Fact]
    public void CrossNamespace_EnumMatched()
    {
        const string model = """
namespace Model

enum Role {
    admin
    editor
    viewer
}
""";
        const string app = """
namespace Test
using "Model"

func label(r: Role) -> string {
    match (r: Role) {
        .admin { return "A" }
        .editor { return "E" }
        .viewer { return "V" }
        default { return "?" }
    }
}

func go() -> string {
    return label(Role.editor())
}
""";
        var (asm, diags) = CompileMulti(model, app);
        Assert.Empty(diags);
        Assert.Equal("E", Invoke(asm, "Test", "go"));
    }

    // Three namespaces chained: a class in one, a value data in another, a
    // consumer in the third that touches both.
    [Fact]
    public void CrossNamespace_ThreeUnitsChained()
    {
        const string core = """
namespace Core

struct Money {
    cents: int
}
""";
        const string ledger = """
namespace Ledger
using "Core"

func (a: Money) total(b: Money) -> int {
    return a.cents + b.cents
}
""";
        const string app = """
namespace Test
using "Core"

func go() -> int {
    let a = Money { cents: 150 }
    let b = Money { cents: 350 }
    return Ledger.total(a, b)
}
""";
        var (asm, diags) = CompileMulti(core, ledger, app);
        Assert.Empty(diags);
        Assert.Equal(500, Invoke(asm, "Test", "go"));
    }

    // ── Cross-namespace free functions are STATIC members of their host class ──────
    // A free function with no value-`data` first parameter never promotes; reached from
    // another namespace it is a static call on the namespace's host class (`A.B` → type
    // `A.B.B`), NOT a member call on an erased `object`. (Regression: a nested-namespace
    // qualifier like `Demo.Hosting.seedBatch()` resolved the qualifier as an external
    // TYPE → object → "method not found on Object" → StackUnderflow.)

    // The seedBatch shape: a nested-namespace free function returning a collection,
    // called qualified from another namespace.
    [Fact]
    public void CrossNamespace_QualifiedFreeFunctionReturningCollection()
    {
        const string lib = """
namespace Lib.Inner

func seed() -> List<int> {
    let xs = List<int>()
    xs.Add(7)
    xs.Add(8)
    return xs
}
""";
        const string app = """
namespace Test
using "Lib.Inner"

func go() -> int {
    let xs = Lib.Inner.seed()
    return xs.Count
}
""";
        var (asm, diags) = CompileMulti(lib, app);
        Assert.Empty(diags);
        Assert.Equal(2, Invoke(asm, "Test", "go"));
    }

    // A bare call into an imported namespace resolves to the static host method too
    // (the binder qualifies it; the emitter calls the host class's static method).
    [Fact]
    public void CrossNamespace_BareFreeFunctionViaUsing()
    {
        const string lib = """
namespace Lib

func answer() -> int = 42
""";
        const string app = """
namespace Test
using "Lib"

func go() -> int {
    return answer()
}
""";
        var (asm, diags) = CompileMulti(lib, app);
        Assert.Empty(diags);
        Assert.Equal(42, Invoke(asm, "Test", "go"));
    }

    // A GENERIC cross-namespace free function called qualified with explicit type args —
    // the host static is closed over its type args at the call site.
    [Fact]
    public void CrossNamespace_QualifiedGenericFreeFunction()
    {
        const string lib = """
namespace Lib

func identity<T>(x: T) -> T {
    return x
}
""";
        const string app = """
namespace Test
using "Lib"

func go() -> int {
    return Lib.identity<int>(5)
}
""";
        var (asm, diags) = CompileMulti(lib, app);
        Assert.Empty(diags);
        Assert.Equal(5, Invoke(asm, "Test", "go"));
    }

    // Guard: a SAME-namespace value-receiver promotion still attaches and dispatches as a
    // method — the cross-namespace static-resolution change must not break in-namespace
    // promotion (the method lives on the type, callable wherever the type is visible).
    [Fact]
    public void CrossNamespace_SameNamespacePromotionStillResolves()
    {
        const string geo = """
namespace Geo

struct Rect {
    w: int
    h: int
}

func (r: Rect) area() -> int {
    return r.w * r.h
}
""";
        const string app = """
namespace Test
using "Geo"

func go() -> int {
    let r = Geo.Rect { w: 4, h: 5 }
    return r.area()
}
""";
        var (asm, diags) = CompileMulti(geo, app);
        Assert.Empty(diags);
        Assert.Equal(20, Invoke(asm, "Test", "go"));
    }

    // The core rule, stated negatively: a free function NEVER promotes across namespaces, so
    // it never gains a method form. `scaled(r: Rect, …)` is declared in `Calc` while `Rect`
    // lives in `Geo` — different namespaces — so `scaled` stays a free function on `Calc`'s
    // host class (reached `scaled(r, k)` bare via `using "Calc"`, or `Calc.scaled(r, k)`).
    // `r.scaled(k)` does NOT resolve: there is no such method on `Rect`. (Declared in `Geo`
    // alongside `Rect` it WOULD promote — see CrossNamespace_SameNamespacePromotionStillResolves.)
    [Fact]
    public void CrossNamespace_FunctionGainsNoMethodFormAcrossNamespaces_IsError()
    {
        const string geo = """
namespace Geo

struct Rect {
    w: int
    h: int
}
""";
        const string calc = """
namespace Calc
using "Geo"

func (r: Rect) scaled(k: int) -> int {
    return r.w * r.h * k
}
""";
        const string app = """
namespace Test
using "Geo"
using "Calc"

func go() -> int {
    let r = Geo.Rect { w: 6, h: 7 }
    return r.scaled(2)
}
""";
        var (asm, diags) = CompileMulti(geo, calc, app);
        Assert.True(asm is null, "expected r.scaled(2) to be unresolved (no cross-namespace promotion); diags: "
            + string.Join("\n", diags.Select(d => d.Message)));
    }

    // The gate is uniform across kinds: a `class` (class) receiver promotes exactly like a
    // value `data` — same namespace only. `describe(a: Account)` lives in `Report` while the
    // `class Account` lives in `Bank`, so it does NOT promote; `a.describe()` is unresolved.
    [Fact]
    public void CrossNamespace_RefDataGainsNoMethodFormAcrossNamespaces_IsError()
    {
        const string bank = """
namespace Bank

class Account {
    var balance: int
}
""";
        const string report = """
namespace Report
using "Bank"

func (a: Account) describe() -> int {
    return a.balance
}
""";
        const string app = """
namespace Test
using "Bank"
using "Report"

func go() -> int {
    let a = Bank.Account { balance: 99 }
    return a.describe()
}
""";
        var (asm, diags) = CompileMulti(bank, report, app);
        Assert.True(asm is null, "expected a.describe() to be unresolved (class does not promote across namespaces); diags: "
            + string.Join("\n", diags.Select(d => d.Message)));
    }

    // The same `class` promotion IS reachable as a method when the function shares the
    // type's namespace (here across files of namespace `Bank`) — the gate allows same-namespace
    // promotion for `class` just as for value `data`. Guards against the gate over-blocking.
    [Fact]
    public void CrossNamespace_RefDataSameNamespacePromotionResolves()
    {
        const string decl = """
namespace Bank

class Account {
    var balance: int
}
""";
        const string ops = """
namespace Bank

func (a: Account) describe() -> int {
    return a.balance
}
""";
        const string app = """
namespace Test
using "Bank"

func go() -> int {
    let a = Bank.Account { balance: 99 }
    return a.describe()
}
""";
        var (asm, diags) = CompileMulti(decl, ops, app);
        Assert.Empty(diags);
        Assert.Equal(99, Invoke(asm, "Test", "go"));
    }

    // ════════════════════════════════════════════════════════════════════
    // 5. Per-file import scoping — bare external names resolve in THEIR OWN
    //    file's `using` scope, not the unioned import set of all files.
    // ════════════════════════════════════════════════════════════════════

    // Two files in one assembly, each importing a different namespace that both
    // declare a `Timer` type. A bare `Timer` in each file must bind to its own
    // file's import — `System.Timers.Timer` in file A, `System.Threading.Timer`
    // in file B. The emitter previously unioned all files' imports into one
    // (unordered) resolver, so both bare `Timer`s resolved to whichever the
    // search hit first; this regresses that. Verified via the emitted parameter
    // types so no Timer instance has to be constructed.
    [Fact]
    public void PerFileImportScoping_BareExternalNameResolvesToOwnFileImport()
    {
        const string fileA = """
namespace Test
using "System.Timers"
func ta(x: Timer) -> int = 1
""";
        const string fileB = """
namespace Test
using "System.Threading"
func tb(x: Timer) -> int = 2
""";
        var (asm, diags) = CompileMulti(fileA, fileB);
        Assert.True(asm is not null, string.Join("\n", diags.Select(d => d.Message)));
        var t = asm.GetType("Test.Test")!;
        var pa = t.GetMethod("ta", AnyStatic)!.GetParameters()[0].ParameterType.FullName;
        var pb = t.GetMethod("tb", AnyStatic)!.GetParameters()[0].ParameterType.FullName;
        Assert.Equal("System.Timers.Timer", pa);
        Assert.Equal("System.Threading.Timer", pb);
    }
}
