using Mono.Cecil;

namespace Esharp.Tests;

/// Constructor overloading on `class` — multiple `init` blocks resolved by arity,
/// `: this(...)` delegation, and sub-public ctor visibility (`priv init`,
/// `protected init`). Plan 08 #6/#7 construction batch, Wave 4.
public class CtorOverloadTests
{
    // ── Arity overloads ────────────────────────────────────────────────────

    [Fact]
    public void CtorOverload_TwoArities_BothConstruct()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Point {
                let x: int
                let y: int
                init(x: int, y: int) {
                    self.x = x
                    self.y = y
                }
                init(v: int) {
                    self.x = v
                    self.y = v
                }
            }

            func run() -> int {
                let a = Point(3, 4)
                let b = Point(7)
                return a.x * 100 + a.y * 10 + b.x + b.y - 7
            }
            """, "run");
        Assert.Equal(347, result);
    }

    [Fact]
    public void CtorOverload_ZeroArgInit_ServesAsParameterlessCtor()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Counter {
                var n: int
                init() { n = 100 }
                init(start: int) { n = start }
            }

            func run() -> int {
                let a = Counter()
                let b = Counter(5)
                return a.n + b.n
            }
            """, "run");
        Assert.Equal(105, result);
    }

    [Fact]
    public void CtorOverload_ZeroArgInit_NoSecondImplicitParameterlessCtor()
    {
        var path = EsHarness.CompileToPath("""
            namespace Test

            class Conn {
                var live: bool
                init() { live = true }
            }

            func touch() -> int {
                let c = Conn()
                return 1
            }
            """);
        using var asm = AssemblyDefinition.ReadAssembly(path);
        var type = asm.MainModule.GetType("Test.Conn");
        Assert.NotNull(type);
        Assert.Single(type!.Methods.Where(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0));
    }

    [Fact]
    public void CtorOverload_FieldDefaults_RunInEveryNonDelegatingCtor()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Widget {
                var tag: int = 42
                var size: int
                init(size: int) { self.size = size }
                init(size: int, scale: int) { self.size = size * scale }
            }

            func run() -> int {
                let a = Widget(2)
                let b = Widget(2, 3)
                return a.tag + b.tag + a.size + b.size
            }
            """, "run");
        Assert.Equal(42 + 42 + 2 + 6, result);
    }

    // ── `: this(...)` delegation ───────────────────────────────────────────

    [Fact]
    public void CtorDelegation_ThisChain_RunsDelegateThenBody()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Buf {
                var cap: int
                var labeled: bool
                init(cap: int) { self.cap = cap }
                init() : this(16) { labeled = true }
            }

            func run() -> int {
                let b = Buf()
                var r = b.cap
                if b.labeled { r = r + 1 }
                return r
            }
            """, "run");
        Assert.Equal(17, result);
    }

    [Fact]
    public void CtorDelegation_FieldDefaults_NotDoubleApplied()
    {
        // The delegate stamps the default; the delegating ctor's body then mutates
        // it. If the delegating ctor re-ran field defaults after the chain, `hits`
        // would reset to 1 and the increment below would read stale state.
        var result = EsHarness.Run("""
            namespace Test

            class Tally {
                var hits: int = 1
                init(bump: int) { hits = hits + bump }
                init() : this(10) { hits = hits + 100 }
            }

            func run() -> int {
                let t = Tally()
                return t.hits
            }
            """, "run");
        Assert.Equal(111, result);
    }

    [Fact]
    public void CtorDelegation_TwoHopChain_Runs()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Chain {
                var trace: int
                init(a: int, b: int) { trace = a * 10 + b }
                init(a: int) : this(a, 2) { trace = trace + 100 }
                init() : this(1) { trace = trace + 1000 }
            }

            func run() -> int {
                let c = Chain()
                return c.trace
            }
            """, "run");
        Assert.Equal(1112, result);
    }

    [Fact]
    public void CtorDelegation_ThisAsCall_NotNewobj()
    {
        var path = EsHarness.CompileToPath("""
            namespace Test

            class Box {
                var v: int
                init(v: int) { self.v = v }
                init() : this(9) { }
            }

            func touch() -> int {
                let b = Box()
                return b.v
            }
            """);
        using var asm = AssemblyDefinition.ReadAssembly(path);
        var type = asm.MainModule.GetType("Test.Box")!;
        var delegating = type.Methods.Single(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
        var calls = delegating.Body.Instructions
            .Where(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Call && i.Operand is MethodReference { Name: ".ctor" } mr
                        && mr.DeclaringType.Name == "Box")
            .ToList();
        Assert.Single(calls);
        Assert.DoesNotContain(delegating.Body.Instructions, i => i.OpCode == Mono.Cecil.Cil.OpCodes.Newobj);
    }

    [Fact]
    public void CtorDelegation_WithInheritance_BaseRunsOnce()
    {
        var result = EsHarness.Run("""
            namespace Test

            open class Base {
                var stamps: int
                init(seed: int) { stamps = stamps + seed }
            }

            class Derived : Base {
                var extra: int
                init(seed: int) : base(seed) { extra = 1 }
                init() : this(50) { extra = extra + 2 }
            }

            func run() -> int {
                let d = Derived()
                return d.stamps + d.extra
            }
            """, "run");
        Assert.Equal(53, result);
    }

    // ── Named args + defaults across overloads ─────────────────────────────

    [Fact]
    public void CtorOverload_NamedArgs_SelectByNames()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Cfg {
                var v: int
                init(host: int, port: int) { v = host * 1000 + port }
                init(port: int) { v = port }
            }

            func run() -> int {
                let a = Cfg(port: 80, host: 2)
                let b = Cfg(port: 9)
                return a.v + b.v
            }
            """, "run");
        Assert.Equal(2089, result);
    }

    [Fact]
    public void CtorOverload_DefaultParam_MaterializedAtConstruction()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Retry {
                var max: int
                var delay: int
                init(max: int, delay: int = 250) {
                    self.max = max
                    self.delay = delay
                }
            }

            func run() -> int {
                let a = Retry(3)
                let b = Retry(3, 10)
                return a.delay + b.delay
            }
            """, "run");
        Assert.Equal(260, result);
    }

    // ── Visibility ─────────────────────────────────────────────────────────

    [Fact]
    public void CtorVisibility_PrivInit_EmitsPrivateCtor()
    {
        var path = EsHarness.CompileToPath("""
            namespace Test

            class Singleton {
                var v: int
                priv init(v: int) { self.v = v }
                init() : this(7) { }
            }

            func run() -> int {
                let s = Singleton()
                return s.v
            }
            """);
        using var asm = AssemblyDefinition.ReadAssembly(path);
        var type = asm.MainModule.GetType("Test.Singleton")!;
        var priv = type.Methods.Single(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1);
        Assert.True(priv.IsPrivate);
        var pub = type.Methods.Single(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
        Assert.True(pub.IsPublic);
    }

    [Fact]
    public void CtorVisibility_PrivInit_DelegationStillRuns()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Singleton {
                var v: int
                priv init(v: int) { self.v = v }
                init() : this(7) { }
            }

            func run() -> int {
                let s = Singleton()
                return s.v
            }
            """, "run");
        Assert.Equal(7, result);
    }

    [Fact]
    public void CtorVisibility_ProtectedInit_EmitsFamilyCtor()
    {
        var path = EsHarness.CompileToPath("""
            namespace Test

            abstract class Shape {
                var sides: int
                protected init(sides: int) { self.sides = sides }
            }

            class Square : Shape {
                init() : base(4) { }
            }

            func run() -> int {
                let s = Square()
                return s.sides
            }
            """);
        using var asm = AssemblyDefinition.ReadAssembly(path);
        var type = asm.MainModule.GetType("Test.Shape")!;
        var prot = type.Methods.Single(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 1);
        Assert.True(prot.IsFamily);
    }

    [Fact]
    public void CtorVisibility_ProtectedInit_BaseChainRuns()
    {
        var result = EsHarness.Run("""
            namespace Test

            abstract class Shape {
                var sides: int
                protected init(sides: int) { self.sides = sides }
            }

            class Square : Shape {
                init() : base(4) { }
            }

            func run() -> int {
                let s = Square()
                return s.sides
            }
            """, "run");
        Assert.Equal(4, result);
    }

    // ── Negatives ──────────────────────────────────────────────────────────

    [Fact]
    public void CtorOverload_DuplicateArity_ES2185()
    {
        var diags = EsHarness.Diagnostics("""
            namespace Test

            class Dup {
                var v: int
                init(a: int) { v = a }
                init(b: int) { v = b }
            }
            """);
        Assert.Contains(diags, d => d.Message.Contains("ES2185"));
    }

    [Fact]
    public void CtorDelegation_SelfCycle_ES2187()
    {
        var diags = EsHarness.Diagnostics("""
            namespace Test

            class Loop {
                var v: int
                init(a: int) : this(a) { v = a }
            }
            """);
        Assert.Contains(diags, d => d.Message.Contains("ES2187"));
    }

    [Fact]
    public void CtorDelegation_MutualCycle_ES2187()
    {
        var diags = EsHarness.Diagnostics("""
            namespace Test

            class Loop {
                var v: int
                init(a: int) : this(a, 1) { v = a }
                init(a: int, b: int) : this(a) { v = a + b }
            }
            """);
        Assert.Contains(diags, d => d.Message.Contains("ES2187"));
    }

    [Fact]
    public void CtorDelegation_NoSiblingWithArity_Errors()
    {
        var diags = EsHarness.Diagnostics("""
            namespace Test

            class Lost {
                var v: int
                init(a: int) : this(a, 2, 3) { v = a }
            }
            """);
        Assert.NotEmpty(diags);
    }

    [Fact]
    public void CtorBase_ArityMismatch_ES2128()
    {
        var diags = EsHarness.Diagnostics("""
            namespace Test

            open class Base {
                var v: int
                init(a: int) { v = a }
            }

            class Child : Base {
                init() : base(1, 2) { }
            }
            """);
        Assert.Contains(diags, d => d.Message.Contains("ES2128"));
    }

    [Fact]
    public void CtorOverload_ConstructionArityMismatch_Errors()
    {
        var diags = EsHarness.Diagnostics("""
            namespace Test

            class P {
                var x: int
                init(x: int) { self.x = x }
                init(x: int, y: int) { self.x = x + y }
            }

            func run() -> int {
                let p = P(1, 2, 3)
                return p.x
            }
            """);
        Assert.NotEmpty(diags);
    }

    [Fact]
    public void Data_InitBlock_StillES3012()
    {
        // ES3012 fires in the parser, not the binder — collect every stage.
        var diags = EsHarness.AllDiagnostics("""
            namespace Test

            struct Vec {
                x: int
                init(x: int) { self.x = x }
            }
            """);
        Assert.Contains(diags, d => d.Message.Contains("ES3012"));
    }
}
