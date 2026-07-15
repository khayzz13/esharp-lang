using Esharp.Diagnostics;

namespace Esharp.Tests;

/// Default parameter values + named arguments (plan 09 #3). A parameter may
/// declare `= expr` (constant shape, ES2180); a call may name arguments after the
/// positional ones, in any order. Defaults materialize at the call site;
/// arguments evaluate in parameter order. Literal defaults also emit
/// `[Optional]` + a `.param` constant so C# callers see them.
public sealed class DefaultNamedArgsTests
{
    const string ConnectSource = """
namespace Test
func connect(host: string, port: int = 8080, timeoutMs: int = 5000, useTls: bool = true) -> string {
    let tls = useTls ? "tls" : "plain"
    return "{host}:{port}/{timeoutMs}/{tls}"
}
""";

    // ── defaults ────────────────────────────────────────────────────────────

    [Fact]
    public void AllDefaults()
        => Assert.Equal("localhost:8080/5000/tls", EsHarness.Run(ConnectSource + """
func go() -> string = connect("localhost")
""", "go"));

    [Fact]
    public void TrailingDefaults_PositionalPrefix()
        => Assert.Equal("h:9090/5000/tls", EsHarness.Run(ConnectSource + """
func go() -> string = connect("h", 9090)
""", "go"));

    [Fact]
    public void Default_Expression_Arithmetic()
        => Assert.Equal(64, EsHarness.Run("""
namespace Test
func bufSize(kb: int = 8 * 8) -> int = kb
func go() -> int = bufSize()
""", "go"));

    [Fact]
    public void Default_NegativeLiteral()
        => Assert.Equal(-1, EsHarness.Run("""
namespace Test
func find(sentinel: int = -1) -> int = sentinel
func go() -> int = find()
""", "go"));

    [Fact]
    public void Default_CompositeLiteral_FreshPerCall()
        => Assert.Equal(0, EsHarness.Run("""
namespace Test
struct Opts { depth: int = 0 }
func walk(o: Opts = Opts { depth: 0 }) -> int = o.depth
func go() -> int = walk()
""", "go"));

    [Fact]
    public void Default_OnMethod()
        => Assert.Equal(15, EsHarness.Run("""
namespace Test
struct Counter { v: int }
func (c: Counter) bump(by: int = 5) -> int = c.v + by
func go() -> int {
    let c = Counter { v: 10 }
    return c.bump()
}
""", "go"));

    [Fact]
    public void Default_OnStaticFuncMember()
        => Assert.Equal(21, EsHarness.Run("""
namespace Test
static Math2 {
    func triple(x: int = 7) -> int = x * 3
}
func go() -> int = Math2.triple()
""", "go"));

    // ── named arguments ─────────────────────────────────────────────────────

    [Fact]
    public void Named_SkipMiddle()
        => Assert.Equal("h:8080/5000/plain", EsHarness.Run(ConnectSource + """
func go() -> string = connect("h", useTls: false)
""", "go"));

    [Fact]
    public void Named_OutOfOrder()
        => Assert.Equal("h:9090/250/tls", EsHarness.Run(ConnectSource + """
func go() -> string = connect("h", timeoutMs: 250, port: 9090)
""", "go"));

    [Fact]
    public void Named_AllNamed_NoDefaults()
        => Assert.Equal(7, EsHarness.Run("""
namespace Test
func sub(a: int, b: int) -> int = a - b
func go() -> int = sub(b: 3, a: 10)
""", "go"));

    [Fact]
    public void Named_OnConstruction_PositionalData()
        => Assert.Equal(7, EsHarness.Run("""
namespace Test
struct Vec2(x: int, y: int)
func go() -> int {
    let v = Vec2(y: 4, x: 3)
    return v.x + v.y
}
""", "go"));

    [Fact]
    public void Named_OnClassInit()
        => Assert.Equal(42, EsHarness.Run("""
namespace Test
class Server {
    port: int
    init(port: int = 42) { self.port = port }
    func get() -> int = self.port
}
func go() -> int {
    let s = Server()
    return s.get()
}
""", "go"));

    [Fact]
    public void Named_MultiLine_Construction()
        => Assert.Equal("db:5432", EsHarness.Run("""
namespace Test
func dsn(host: string, port: int = 5432, user: string = "root") -> string = "{host}:{port}"
func go() -> string = dsn(
    host: "db",
)
""", "go"));

    // ── external interop: omit C# optionals ─────────────────────────────────

    [Fact]
    public void External_OmittedOptional_Fills()
        => Assert.Equal(3, EsHarness.Run("""
namespace Test
func go() -> int {
    let parts = "a,b,,c".Split(",", StringSplitOptions.RemoveEmptyEntries)
    return parts.Length
}
""", "go"));

    // ── diagnostics ─────────────────────────────────────────────────────────

    [Fact]
    public void NonConstant_Default_IsES2180()
    {
        var diags = EsHarness.Diagnostics("""
namespace Test
var seed: int = 1
func f(x: int = seed) -> int = x
""");
        Assert.Contains(diags, d => d.Message.Contains("ES2180"));
    }

    [Fact]
    public void Default_ReferencingPriorParam_IsES2180()
    {
        var diags = EsHarness.Diagnostics("""
namespace Test
func f(a: int, b: int = a) -> int = a + b
""");
        Assert.Contains(diags, d => d.Message.Contains("ES2180"));
    }

    [Fact]
    public void Default_CallExpression_IsES2180()
    {
        var diags = EsHarness.Diagnostics("""
namespace Test
func now() -> int = 1
func f(x: int = now()) -> int = x
""");
        Assert.Contains(diags, d => d.Message.Contains("ES2180"));
    }

    [Fact]
    public void Positional_AfterNamed_IsES2181()
    {
        var diags = EsHarness.Diagnostics("""
namespace Test
func sub(a: int, b: int) -> int = a - b
func go() -> int = sub(a: 1, 2)
""");
        Assert.Contains(diags, d => d.Message.Contains("ES2181"));
    }

    [Fact]
    public void UnknownName_IsES2182()
    {
        var diags = EsHarness.Diagnostics("""
namespace Test
func sub(a: int, b: int) -> int = a - b
func go() -> int = sub(1, c: 2)
""");
        Assert.Contains(diags, d => d.Message.Contains("ES2182"));
    }

    [Fact]
    public void UnfilledParam_IsES2183()
    {
        var diags = EsHarness.Diagnostics("""
namespace Test
func sub(a: int, b: int) -> int = a - b
func go() -> int = sub(b: 2)
""");
        Assert.Contains(diags, d => d.Message.Contains("ES2183"));
    }

    [Fact]
    public void DuplicateArg_IsES2184()
    {
        var diags = EsHarness.Diagnostics("""
namespace Test
func sub(a: int, b: int) -> int = a - b
func go() -> int = sub(1, a: 2)
""");
        Assert.Contains(diags, d => d.Message.Contains("ES2184"));
    }

    [Fact]
    public void OutParam_Default_IsES2180()
    {
        var diags = EsHarness.Diagnostics("""
namespace Test
func f(out r: int = 3) { r = 1 }
""");
        Assert.Contains(diags, d => d.Message.Contains("ES2180"));
    }

    // ── C#-facing metadata ──────────────────────────────────────────────────

    [Fact]
    public void LiteralDefault_Emits_OptionalConstant()
    {
        var path = EsHarness.CompileToPath("""
namespace Test
func connect(host: string, port: int = 8080) -> int = port
""");
        using var asm = Mono.Cecil.AssemblyDefinition.ReadAssembly(path);
        var method = asm.MainModule.Types.SelectMany(t => t.Methods).First(m => m.Name == "connect");
        var port = method.Parameters[1];
        Assert.True(port.IsOptional);
        Assert.True(port.HasConstant);
        Assert.Equal(8080, port.Constant);
        // Non-defaulted param stays plain.
        Assert.False(method.Parameters[0].IsOptional);
    }

    [Fact]
    public void CompositeDefault_NotStampedAsConstant()
    {
        var path = EsHarness.CompileToPath("""
namespace Test
struct Opts { depth: int = 0 }
func walk(n: int, o: Opts = Opts { depth: 1 }) -> int = n + o.depth
""");
        using var asm = Mono.Cecil.AssemblyDefinition.ReadAssembly(path);
        var method = asm.MainModule.Types.SelectMany(t => t.Methods).First(m => m.Name == "walk");
        Assert.False(method.Parameters[1].HasConstant);
    }
}
