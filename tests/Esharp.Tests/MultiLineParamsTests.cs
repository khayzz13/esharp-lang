namespace Esharp.Tests;

/// Multi-line parameter & argument lists (plan 09 #1): inside any `( … )`
/// parameter or argument list, a newline separates like a comma — comma,
/// newline, or both — and a trailing separator before `)` is allowed. Holds
/// uniformly for `func` declarations, `init` blocks, positional headers,
/// `delegate func`, interface methods, choice payloads, and call sites.
public sealed class MultiLineParamsTests
{
    // ── func declarations ───────────────────────────────────────────────────

    [Fact]
    public void Func_Params_CommaNewline_TrailingComma()
        => Assert.Equal(6, EsHarness.Run("""
namespace Test
func add3(
    a: int,
    b: int,
    c: int,
) -> int = a + b + c
func go() -> int = add3(1, 2, 3)
""", "go"));

    [Fact]
    public void Func_Params_NewlineOnly_NoCommas()
        => Assert.Equal(6, EsHarness.Run("""
namespace Test
func add3(
    a: int
    b: int
    c: int
) -> int = a + b + c
func go() -> int = add3(1, 2, 3)
""", "go"));

    [Fact]
    public void Func_Params_MixedSeparators()
        => Assert.Equal(10, EsHarness.Run("""
namespace Test
func add4(a: int, b: int,
          c: int
          d: int) -> int = a + b + c + d
func go() -> int = add4(1, 2, 3, 4)
""", "go"));

    [Fact]
    public void Func_Params_PointerAndOut_MultiLine()
        => Assert.Equal(42, EsHarness.Run("""
namespace Test
struct Box { v: int }
func fill(
    b: *Box,
    out r: int,
) {
    b.v = 40
    r = b.v + 2
}
func go() -> int {
    var bx = Box { v: 0 }
    fill(&bx, out var got)
    return got
}
""", "go"));

    // ── call sites ──────────────────────────────────────────────────────────

    [Fact]
    public void Call_Args_MultiLine_TrailingComma()
        => Assert.Equal(6, EsHarness.Run("""
namespace Test
func add3(a: int, b: int, c: int) -> int = a + b + c
func go() -> int = add3(
    1,
    2,
    3,
)
""", "go"));

    [Fact]
    public void Call_Args_NewlineOnly()
        => Assert.Equal(6, EsHarness.Run("""
namespace Test
func add3(a: int, b: int, c: int) -> int = a + b + c
func go() -> int {
    let r = add3(
        1
        2
        3
    )
    return r
}
""", "go"));

    // ── init blocks ─────────────────────────────────────────────────────────

    [Fact]
    public void Init_Params_MultiLine()
        => Assert.Equal(30, EsHarness.Run("""
namespace Test
class Pair {
    a: int
    b: int
    init(
        a: int,
        b: int,
    ) {
        self.a = a
        self.b = b
    }
    func sum() -> int = self.a + self.b
}
func go() -> int {
    let p = Pair(10, 20)
    return p.sum()
}
""", "go"));

    // ── positional data header ──────────────────────────────────────────────

    [Fact]
    public void PositionalData_Header_MultiLine()
        => Assert.Equal(7, EsHarness.Run("""
namespace Test
struct Vec2(
    x: int,
    y: int,
)
func go() -> int {
    let v = Vec2(3, 4)
    return v.x + v.y
}
""", "go"));

    // ── delegate func ───────────────────────────────────────────────────────

    [Fact]
    public void DelegateFunc_Params_MultiLine()
        => Assert.Equal(42, EsHarness.Run("""
namespace Test
delegate func BinOp(
    a: int,
    b: int,
) -> int
func add(a: int, b: int) -> int = a + b
func go() -> int {
    let f: BinOp = add
    return f(20, 22)
}
""", "go"));

    // ── interface methods ───────────────────────────────────────────────────

    [Fact]
    public void Interface_Method_Params_MultiLine()
        => Assert.Equal(9, EsHarness.Run("""
namespace Test
interface ICombine {
    func combine(
        a: int,
        b: int,
    ) -> int
}
class Adder : ICombine {
    func combine(a: int, b: int) -> int = a + b
}
func go() -> int {
    let c = Adder()
    return c.combine(4, 5)
}
""", "go"));

    // ── choice payloads ─────────────────────────────────────────────────────

    [Fact]
    public void Choice_Payloads_MultiLine()
        => Assert.Equal(15, EsHarness.Run("""
namespace Test
union Op {
    add(
        a: int,
        b: int,
    )
    neg(v: int)
}
func eval(o: Op) -> int = match o {
    .add(a, b) => a + b
    .neg(v)    => 0 - v
}
func go() -> int = eval(Op.add(7, 8))
""", "go"));

    // ── lambdas ─────────────────────────────────────────────────────────────

    [Fact]
    public void Lambda_FuncLiteral_Params_MultiLine()
        => Assert.Equal(11, EsHarness.Run("""
namespace Test
func go() -> int {
    let f: Func<int, int, int> = func(
        a: int,
        b: int,
    ) -> int { return a + b }
    return f(5, 6)
}
""", "go"));

    // ── adversarial ─────────────────────────────────────────────────────────

    [Fact]
    public void Unclosed_ParamList_StillErrors()
    {
        var parser = new Esharp.Syntax.Parsing.Parser("""
namespace Test
func broken(a: int,
""", "test.es");
        parser.ParseCompilationUnit();
        Assert.NotEmpty(parser.Diagnostics);
    }

    [Fact]
    public void Empty_MultiLine_ParamList()
        => Assert.Equal(1, EsHarness.Run("""
namespace Test
func one(
) -> int = 1
func go() -> int = one()
""", "go"));
}
