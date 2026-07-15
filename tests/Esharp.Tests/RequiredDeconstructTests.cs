using Mono.Cecil;

namespace Esharp.Tests;

/// `required` field marker (composite-literal coverage, ES2189, [RequiredMember]
/// interop) and positional-data `Deconstruct` (synthesized out-param method +
/// `let (a, b) = v` destructure). Plan 08 #7, Wave 6.
public class RequiredDeconstructTests
{
    // ── required: coverage ─────────────────────────────────────────────────

    [Fact]
    public void Required_AllSupplied_Compiles()
    {
        var result = EsHarness.Run("""
            namespace Test

            struct Span {
                required lo: int
                required hi: int
            }

            func run() -> int {
                let s = Span { lo: 2, hi: 9 }
                return s.hi - s.lo
            }
            """, "run");
        Assert.Equal(7, result);
    }

    [Fact]
    public void Required_Omitted_ES2189()
    {
        var diags = EsHarness.Diagnostics("""
            namespace Test

            struct Span {
                required lo: int
                required hi: int
            }

            func run() -> int {
                let s = Span { lo: 2 }
                return s.lo
            }
            """);
        Assert.Contains(diags, d => d.Message.Contains("ES2189") && d.Message.Contains("hi"));
    }

    [Fact]
    public void Required_OnClass_CompositeLiteralChecked()
    {
        var diags = EsHarness.Diagnostics("""
            namespace Test

            class Options {
                required var name: string
                var verbose: bool
            }

            func run() -> Options {
                return Options { verbose: true }
            }
            """);
        Assert.Contains(diags, d => d.Message.Contains("ES2189") && d.Message.Contains("name"));
    }

    [Fact]
    public void Required_NonRequiredFieldsStillDefaultSilently()
    {
        var result = EsHarness.Run("""
            namespace Test

            struct Cfg {
                required port: int
                retries: int
            }

            func run() -> int {
                let c = Cfg { port: 80 }
                return c.port + c.retries
            }
            """, "run");
        Assert.Equal(80, result);
    }

    [Fact]
    public void Required_LetField_Works()
    {
        var result = EsHarness.Run("""
            namespace Test

            class Tag {
                required let id: int
            }

            func run() -> int {
                let t = Tag { id: 5 }
                return t.id
            }
            """, "run");
        Assert.Equal(5, result);
    }

    // ── required: metadata ─────────────────────────────────────────────────

    [Fact]
    public void Required_EmitsRequiredMemberAttribute()
    {
        var path = EsHarness.CompileToPath("""
            namespace Test

            struct Span {
                required lo: int
                hi: int
            }

            func touch() -> int {
                let s = Span { lo: 1 }
                return s.lo
            }
            """);
        using var asm = AssemblyDefinition.ReadAssembly(path);
        var type = asm.MainModule.Types.First(t => t.Name == "Span" || t.FullName.EndsWith(".Span"));
        Assert.Contains(type.CustomAttributes, a => a.AttributeType.Name == "RequiredMemberAttribute");
        var lo = type.Fields.Single(f => f.Name == "lo");
        Assert.Contains(lo.CustomAttributes, a => a.AttributeType.Name == "RequiredMemberAttribute");
        var hi = type.Fields.Single(f => f.Name == "hi");
        Assert.DoesNotContain(hi.CustomAttributes, a => a.AttributeType.Name == "RequiredMemberAttribute");
    }

    // ── Deconstruct: destructure binding ───────────────────────────────────

    [Fact]
    public void Deconstruct_LetDestructure_OverPositionalData()
    {
        var result = EsHarness.Run("""
            namespace Test

            struct Vec2(x: int, y: int)

            func run() -> int {
                let v = Vec2(3, 4)
                let (x, y) = v
                return x * 10 + y
            }
            """, "run");
        Assert.Equal(34, result);
    }

    [Fact]
    public void Deconstruct_MixedTypes()
    {
        var result = EsHarness.Run("""
            namespace Test

            struct Entry(name: string, score: int)

            func run() -> string {
                let e = Entry("kae", 9)
                let (n, s) = e
                return n + s.ToString()
            }
            """, "run");
        Assert.Equal("kae9", result);
    }

    [Fact]
    public void Deconstruct_TuplesStillDestructure()
    {
        var result = EsHarness.Run("""
            namespace Test

            func pair() -> (int, int) {
                return (3, 4)
            }

            func run() -> int {
                let (a, b) = pair()
                return a + b
            }
            """, "run");
        Assert.Equal(7, result);
    }

    [Fact]
    public void Deconstruct_ArityMismatch_Errors()
    {
        var diags = EsHarness.Diagnostics("""
            namespace Test

            struct Vec2(x: int, y: int)

            func run() -> int {
                let v = Vec2(3, 4)
                let (a, b, c) = v
                return a
            }
            """);
        Assert.Contains(diags, d => d.Message.Contains("destructures into 2"));
    }

    // ── Deconstruct: metadata + C#-style invocation ────────────────────────

    [Fact]
    public void Deconstruct_MethodEmittedWithOutParams()
    {
        var path = EsHarness.CompileToPath("""
            namespace Test

            struct Vec2(x: int, y: int)

            func touch() -> int {
                let v = Vec2(1, 2)
                return v.x
            }
            """);
        using var asm = AssemblyDefinition.ReadAssembly(path);
        var type = asm.MainModule.Types.First(t => t.Name == "Vec2" || t.FullName.EndsWith(".Vec2"));
        var decon = type.Methods.SingleOrDefault(m => m.Name == "Deconstruct");
        Assert.NotNull(decon);
        Assert.Equal(2, decon!.Parameters.Count);
        Assert.All(decon.Parameters, p => Assert.True(p.IsOut));
        Assert.All(decon.Parameters, p => Assert.True(p.ParameterType.IsByReference));
    }

    [Fact]
    public void Deconstruct_InvocableViaReflection()
    {
        var asm = EsHarness.Compile("""
            namespace Test

            struct Vec2(x: int, y: int)

            func make() -> Vec2 {
                return Vec2(8, 9)
            }
            """);
        var v = EsHarness.Invoke(asm, "make")!;
        var args = new object?[] { null, null };
        v.GetType().GetMethod("Deconstruct")!.Invoke(v, args);
        Assert.Equal(8, args[0]);
        Assert.Equal(9, args[1]);
    }

    [Fact]
    public void Deconstruct_NotEmittedOnBodyFormData()
    {
        var path = EsHarness.CompileToPath("""
            namespace Test

            struct Plain {
                a: int
            }

            func touch() -> int {
                let p = Plain { a: 1 }
                return p.a
            }
            """);
        using var asm = AssemblyDefinition.ReadAssembly(path);
        var type = asm.MainModule.Types.First(t => t.Name == "Plain" || t.FullName.EndsWith(".Plain"));
        Assert.Null(type.Methods.SingleOrDefault(m => m.Name == "Deconstruct"));
    }

    // ── End-to-end: every construction feature in one program ──────────────

    [Fact]
    public void ConstructionBatch_EndToEnd()
    {
        var result = EsHarness.Run("""
            namespace Test

            struct Span {
                required lo: int
                required hi: int
            }

            struct Vec2(x: int, y: int)

            class Conn(
                host: string
                port: int = 80,
            ) {
                var label: string = host
                init { if port == 0 { label = "invalid" } }
                init(host: string) : this(host, 8080) { }
                func describe() -> string { return self.label + ":" + port.ToString() }
            }

            func run() -> string {
                let primary = Conn(port: 443, host: "api")
                let secondary = Conn("dev")
                // Arity-1 has two surfaces: the secondary init (exact match) and the
                // primary with its port default. The zero-defaults match wins.
                let defaulted = Conn("plain")
                let s = Span { lo: 1, hi: 5 }
                let (x, y) = Vec2(s.lo, s.hi)
                let head = primary.describe() + " " + secondary.describe()
                return head + " " + defaulted.describe() + " " + (x + y).ToString()
            }
            """, "run");
        Assert.Equal("api:443 dev:8080 plain:8080 6", result);
    }

    [Fact]
    public void Deconstruct_GenericPositionalData()
    {
        var result = EsHarness.Run("""
            namespace Test

            struct Pair<A, B>(first: A, second: B)

            func run() -> int {
                let p = Pair<int, int>(20, 3)
                let (a, b) = p
                return a + b
            }
            """, "run");
        Assert.Equal(23, result);
    }
}
