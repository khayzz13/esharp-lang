// Style note: E# source uses readable """ raw-string blocks (these double as the
// E# corpus) — do not collapse them into inline \n-escaped one-liners.
namespace Esharp.Tests;

/// choice / ref choice / enum / match / Result coverage.
public sealed class ILEmitterTests_Coverage_Choice
{
    [Fact]
    public void ValueChoice_MatchZeroPayload()
    {
        const string src = """
namespace Test

union Color {
    red
    green
    blue
}

func code(c: Color) -> int {
    match (c: Color) {
        .red   { return 1 }
        .green { return 2 }
        .blue  { return 3 }
    }
    return 0
}

func go() -> int {
    return code(Color.green())
}
""";
        Assert.Equal(2, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void ValueChoice_SinglePayload()
    {
        const string src = """
namespace Test

union Shape {
    circle(radius: int)
    square(side: int)
}

func area(s: Shape) -> int {
    match (s: Shape) {
        .circle(r) { return 3 * r * r }
        .square(side) { return side * side }
    }
    return 0
}

func go() -> int {
    return area(Shape.square(5))
}
""";
        Assert.Equal(25, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void ValueChoice_MultiPayload()
    {
        const string src = """
namespace Test

union Entry {
    log(level: int, code: int)
    blank
}

func score(e: Entry) -> int {
    match (e: Entry) {
        .log(lvl, code) { return lvl * 100 + code }
        .blank { return 0 }
    }
    return -1
}

func go() -> int {
    return score(Entry.log(2, 7))
}
""";
        Assert.Equal(207, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void RefChoice_RecursiveEval()
    {
        const string src = """
namespace Test

ref union Expr {
    lit(value: int)
    add(left: Expr, right: Expr)
    mul(left: Expr, right: Expr)
}

func eval(e: Expr) -> int {
    match (e: Expr) {
        .lit(v) { return v.value }
        .add(a) { return eval(a.left) + eval(a.right) }
        .mul(m) { return eval(m.left) * eval(m.right) }
    }
    return 0
}

func go() -> int {
    let tree = Expr.add(Expr.lit(3), Expr.mul(Expr.lit(4), Expr.lit(5)))
    return eval(tree)
}
""";
        Assert.Equal(23, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void RefChoice_PositionalMultiPayloadBinding()
    {
        const string src = """
namespace Test

ref union Expr {
    lit(value: int)
    add(left: Expr, right: Expr)
    mul(left: Expr, right: Expr)
}

func eval(e: Expr) -> int {
    match (e: Expr) {
        .lit(v) { return v.value }
        .add(l, r) { return eval(l) + eval(r) }
        .mul(l, r) { return eval(l) * eval(r) }
    }
    return 0
}

func go() -> int {
    let tree = Expr.add(Expr.lit(3), Expr.mul(Expr.lit(4), Expr.lit(5)))
    return eval(tree)
}
""";
        Assert.Equal(23, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void Enum_ExplicitAndAutoValues()
    {
        const string src = """
namespace Test

enum Level {
    a
    b = 10
    c
}

func rank(l: Level) -> int {
    match (l: Level) {
        .a { return 0 }
        .b { return 10 }
        .c { return 11 }
    }
    return -1
}

func go() -> int {
    return rank(Level.a()) + rank(Level.b()) + rank(Level.c())
}
""";
        // a=0, b=10, c=11
        Assert.Equal(21, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void Match_LiteralInt()
    {
        const string src = """
namespace Test

func classify(n: int) -> int {
    match n {
        200 { return 1 }
        404 { return 2 }
        500 { return 3 }
        default { return 0 }
    }
}

func go() -> int {
    return classify(404) + classify(999)
}
""";
        Assert.Equal(2, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void Match_LiteralString()
    {
        const string src = """
namespace Test

func perm(name: string) -> int {
    match name {
        "admin" { return 7 }
        "guest" { return 1 }
        default { return 0 }
    }
}

func go() -> int {
    return perm("admin") + perm("guest") + perm("other")
}
""";
        Assert.Equal(8, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void Match_AsExpression()
    {
        const string src = """
namespace Test

func go() -> int {
    let status = 404
    let label = match status {
        200 { 1 }
        404 { 4 }
        default { 0 }
    }
    return label * 10
}
""";
        Assert.Equal(40, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void Result_OkPropagation()
    {
        const string src = """
namespace Test

func half(n: int) -> Result<int, string> {
    if n % 2 != 0 {
        return error("odd")
    }
    return ok(n / 2)
}

func chain(n: int) -> Result<int, string> {
    let a = half(n)?
    let b = half(a)?
    return ok(b)
}

func go() -> int {
    let r = chain(8)
    return r.UnwrapOr(0 - 1)
}
""";
        Assert.Equal(2, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void Result_ErrorShortCircuits()
    {
        const string src = """
namespace Test

func half(n: int) -> Result<int, string> {
    if n % 2 != 0 {
        return error("odd")
    }
    return ok(n / 2)
}

func chain(n: int) -> Result<int, string> {
    let a = half(n)?
    let b = half(a)?
    return ok(b)
}

func go() -> int {
    let r = chain(6)
    return r.IsError ? 99 : r.UnwrapOr(0)
}
""";
        // chain(6): half(6)=3, half(3)=error -> chain is error
        Assert.Equal(99, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void LetElse_Guard()
    {
        const string src = """
namespace Test

func find(id: int) -> string? {
    if id == 0 {
        return nil
    }
    return "ok"
}

func lookup(id: int) -> string {
    let v = find(id) else {
        return "miss"
    }
    return v
}

func go() -> string {
    return lookup(5) + "-" + lookup(0)
}
""";
        Assert.Equal("ok-miss", EsHarness.Run(src, "go"));
    }

    [Fact]
    public void Match_OnReceiverInferred()
    {
        const string src = """
namespace Test

union State {
    on
    off
}

struct Switch {
    var s: State
}

func (sw: Switch) describe() -> int {
    match sw.s {
        .on  { return 1 }
        .off { return 0 }
    }
    return -1
}

func go() -> int {
    let sw = Switch { s: State.on() }
    return sw.describe()
}
""";
        Assert.Equal(1, EsHarness.Run(src, "go"));
    }
}
