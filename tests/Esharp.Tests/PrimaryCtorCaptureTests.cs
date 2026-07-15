using Mono.Cecil;

namespace Esharp.Tests;

/// Primary-constructor capture on `class` — `class Foo(a: T, ...)` is the capture
/// header: a header param referenced by an in-body method becomes a synthesized
/// private `let` field (capture-on-use); one used only in `init { }` / field
/// defaults stays a ctor-local. Plan 09 #2, Wave 5.
public class PrimaryCtorCaptureTests
{
    // ── Capture on use ─────────────────────────────────────────────────────

    [Fact]
    public void Capture_HeaderParamUsedInMethod_ReadsThroughField()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Greeter(name: string) {
                func greet() -> string { return "hi " + name }
            }

            func run() -> string {
                let g = Greeter("kae")
                return g.greet()
            }
            """, "run");
        Assert.Equal("hi kae", result);
    }

    [Fact]
    public void Capture_FieldIsPrivateInitOnly_AndInitOnlyParamGetsNoField()
    {
        var path = EsHarness.CompileToPath("""
            namespace Test

            class Svc(store: int, maxRetries: int) {
                var tokens: int
                init { tokens = maxRetries }
                func read() -> int { return store }
            }

            func run() -> int {
                let s = Svc(7, 3)
                return s.read() + s.tokens
            }
            """);
        using var asm = AssemblyDefinition.ReadAssembly(path);
        var type = asm.MainModule.GetType("Test.Svc")!;
        var storeField = type.Fields.SingleOrDefault(f => f.Name == "store");
        Assert.NotNull(storeField);
        Assert.True(storeField!.IsPrivate);
        Assert.True(storeField.IsInitOnly);
        // maxRetries was used only in `init { }` — it stays a ctor-local, no field.
        Assert.Null(type.Fields.FirstOrDefault(f => f.Name == "maxRetries"));
    }

    [Fact]
    public void Capture_UsedInTwoMethods_SingleField()
    {
        var path = EsHarness.CompileToPath("""
            namespace Test

            class Counter(step: int) {
                func up(v: int) -> int { return v + step }
                func down(v: int) -> int { return v - step }
            }

            func run() -> int {
                let c = Counter(5)
                return c.up(10) + c.down(10)
            }
            """);
        using var asm = AssemblyDefinition.ReadAssembly(path);
        var type = asm.MainModule.GetType("Test.Counter")!;
        Assert.Single(type.Fields.Where(f => f.Name == "step"));
    }

    [Fact]
    public void Capture_MethodParamShadowsHeaderParam()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Adder(amount: int) {
                func addOwn(v: int) -> int { return v + amount }
                func addOther(v: int, amount: int) -> int { return v + amount }
            }

            func run() -> int {
                let a = Adder(100)
                return a.addOwn(1) * 1000 + a.addOther(1, 5)
            }
            """, "run");
        Assert.Equal(101006, result);
    }

    // ── init { } epilogue + field defaults ─────────────────────────────────

    [Fact]
    public void Header_FieldDefaultReadsHeaderParam()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Retry(maxRetries: int) {
                var tokens: int = maxRetries
            }

            func run() -> int {
                let r = Retry(9)
                return r.tokens
            }
            """, "run");
        Assert.Equal(9, result);
    }

    [Fact]
    public void Header_EpilogueRunsAfterCaptureAndDefaults()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Svc(maxRetries: int) {
                var tokens: int = maxRetries
                init {
                    if maxRetries <= 0 { tokens = 1 }
                }
                func budget() -> int { return self.tokens + maxRetries }
            }

            func run() -> int {
                let bad = Svc(0)
                let good = Svc(4)
                return bad.budget() * 100 + good.budget()
            }
            """, "run");
        // bad: tokens=1, maxRetries=0 → 1; good: tokens=4 + 4 → 8
        Assert.Equal(108, result);
    }

    [Fact]
    public void Header_DefaultParam_AndNamedConstruction()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Conn(host: string, port: int = 80) {
                func describe() -> string { return host + ":" + port.ToString() }
            }

            func run() -> string {
                let a = Conn("x")
                let b = Conn(port: 9, host: "y")
                return a.describe() + " " + b.describe()
            }
            """, "run");
        Assert.Equal("x:80 y:9", result);
    }

    [Fact]
    public void Header_MultiLine_WithTrailingSeparator()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Box(
                width: int
                height: int,
            ) {
                func area() -> int { return width * height }
            }

            func run() -> int {
                let b = Box(
                    3,
                    4,
                )
                return b.area()
            }
            """, "run");
        Assert.Equal(12, result);
    }

    // ── Secondary inits on a headered class ────────────────────────────────

    [Fact]
    public void Header_SecondaryInit_DelegatesToPrimary()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Conn(host: string, port: int) {
                var dialed: bool
                init(host: string) : this(host, 80) { dialed = true }
                func describe() -> string { return host + ":" + port.ToString() }
            }

            func run() -> string {
                let c = Conn("z")
                var s = c.describe()
                if c.dialed { s = s + "!" }
                return s
            }
            """, "run");
        Assert.Equal("z:80!", result);
    }

    [Fact]
    public void Header_WithBaseClass_PrimaryCallsParameterlessBase()
    {
        var result = EsHarness.Run("""
            namespace Test

            open class Node {
                var id: int = 41
            }

            class Leaf(label: string) : Node {
                func tag() -> string { return label + self.id.ToString() }
            }

            func run() -> string {
                let l = Leaf("n")
                return l.tag()
            }
            """, "run");
        Assert.Equal("n41", result);
    }

    [Fact]
    public void Header_GenericClass_CapturesGenericParam()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Holder<T>(value: T) {
                func get() -> T { return value }
            }

            func run() -> int {
                let h = Holder<int>(42)
                return h.get()
            }
            """, "run");
        Assert.Equal(42, result);
    }

    [Fact]
    public void Header_NoBody_Constructs()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Marker(id: int)

            func run() -> int {
                let m = Marker(3)
                return 1
            }
            """, "run");
        Assert.Equal(1, result);
    }

    // ── Negatives ──────────────────────────────────────────────────────────

    [Fact]
    public void Header_SecondaryInitWithoutThis_ES2186()
    {
        var diags = EsHarness.Diagnostics("""
            namespace Test

            class Svc(a: int) {
                init(b: string) { }
            }
            """);
        Assert.Contains(diags, d => d.Message.Contains("ES2186"));
    }

    [Fact]
    public void Header_FieldNameConflict_ES2188()
    {
        var diags = EsHarness.Diagnostics("""
            namespace Test

            class Svc(count: int) {
                var count: int
            }
            """);
        Assert.Contains(diags, d => d.Message.Contains("ES2188"));
    }

    [Fact]
    public void Header_CompositeLiteral_ES2190()
    {
        var diags = EsHarness.Diagnostics("""
            namespace Test

            class Svc(a: int) {
                var b: int
            }

            func run() -> Svc {
                return Svc { b: 1 }
            }
            """);
        Assert.Contains(diags, d => d.Message.Contains("ES2190"));
    }

    [Fact]
    public void Header_SecondaryDuplicatesHeaderArity_ES2185()
    {
        var diags = EsHarness.Diagnostics("""
            namespace Test

            class Svc(a: int) {
                init(b: string) : this(1) { }
            }
            """);
        Assert.Contains(diags, d => d.Message.Contains("ES2185"));
    }

    [Fact]
    public void Header_AssignToCapturedParam_IsError()
    {
        var diags = EsHarness.Diagnostics("""
            namespace Test

            class Svc(a: int) {
                func bump() -> int {
                    a = a + 1
                    return a
                }
            }
            """);
        Assert.NotEmpty(diags);
    }

    [Fact]
    public void Data_PositionalForm_Unchanged()
    {
        // `struct Vec2(x, y)` keeps the old semantics: public fields + synthesized init.
        var result = EsHarness.Run("""
            namespace Test

            struct Vec2(x: int, y: int)

            func (v: Vec2) sum() -> int { return v.x + v.y }

            func run() -> int {
                let v = Vec2(3, 4)
                return v.sum()
            }
            """, "run");
        Assert.Equal(7, result);
    }
}
