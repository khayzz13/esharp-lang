using Esharp.CodeGen;
using Xunit.Abstractions;

namespace Esharp.Tests;

/// Calibration probe (not a permanent assertion): compiles a spread of KNOWN-GOOD
/// E# programs — including the features that legitimately emit unverifiable-but-
/// runnable IL (managed pointers `*T`, function pointers `&f`/calli, async state
/// machines, channels, closures) — and dumps every ILVerify finding. Used to
/// confirm the fatal/non-fatal partition in IlVerificationFinding does not flag
/// correct code as fatal. The final assert is that NO known-good program yields a
/// FATAL finding.
public sealed class ILVerificationProbe(ITestOutputHelper output)
{
    static readonly (string name, string src)[] Programs =
    [
        ("pointer_byref", """
namespace Test
struct Counter { var n: int }
func bump(c: *Counter) { c.n += 1 }
func go() -> int {
    var c = Counter { n: 41 }
    bump(&c)
    return c.n
}
"""),
        ("funcptr_calli", """
namespace Test
func add(a: int, b: int) -> int { return a + b }
func go() -> int {
    let p = &add
    return p(3, 4)
}
"""),
        ("async_await", """
namespace Test
func go() -> int {
    let v = await Task.FromResult(42)
    return v
}
"""),
        ("closure_capture", """
namespace Test
func go() -> int {
    var total = 0
    let inc = func() { total = total + 1 }
    inc()
    inc()
    return total
}
"""),
        ("ref_choice_match", """
namespace Test
ref union Expr {
    literal(value: int)
    add(left: Expr, right: Expr)
}
func eval(e: Expr) -> int {
    match e {
        .literal(l) { return l.value }
        .add(a) { return eval(a.left) + eval(a.right) }
    }
    return 0
}
func go() -> int {
    return eval(Expr.add(Expr.literal(3), Expr.literal(4)))
}
"""),
        ("value_choice_match", """
namespace Test
union Cmd { help, add(a: int, b: int) }
func go() -> int {
    let c = Cmd.add(3, 4)
    match c {
        .help { return 0 }
        .add(a, b) { return a + b }
    }
    return -1
}
"""),
        ("result_propagation", """
namespace Test
func parse(s: string) -> Result<int, string> {
    if s == "" { return error("empty") }
    return ok(int.Parse(s))
}
func chain(s: string) -> Result<int, string> {
    let n = parse(s)?
    return ok(n + 1)
}
func go() -> int {
    let r = chain("41")
    return r.IsOk ? r.Value : -1
}
"""),
        ("defer_trycatch", """
namespace Test
func go() -> int {
    var n = 0
    try {
        n = int.Parse("notnum")
    } catch (e: FormatException) {
        n = -1
    }
    return n
}
"""),
        ("generics_list", """
namespace Test
func go() -> int {
    let xs = [1, 2, 3, 4, 5]
    var total = 0
    for x in xs { total += x }
    return total
}
"""),
        ("interpolation", """
namespace Test
func go() -> string {
    let a = 3
    let b = 4
    return "sum={a + b}"
}
"""),
        ("readonly_with", """
namespace Test
readonly struct P { x: int, y: int }
func go() -> int {
    let a = P { x: 3, y: 4 }
    let b = a with { x: 9 }
    return b.x + b.y
}
"""),
        ("inheritance_virtual", """
namespace Test
abstract class Shape {
    init() { }
    abstract func area() -> int
}
class Square : Shape {
    side: int
    init(s: int) : base() { self.side = s }
    : func area() -> int { return self.side * self.side }
}
func go() -> int {
    let sq = Square(5)
    return sq.area()
}
"""),
    ];

    [Fact]
    public void KnownGoodPrograms_ProduceNoFatalVerificationFindings()
    {
        var fatalReport = new List<string>();
        var allCodes = new SortedSet<string>();

        foreach (var (name, src) in Programs)
        {
            var asm = EsHarness.Compile(src, name);
            var findings = IlVerification.Verify(asm.Location);
            foreach (var f in findings)
            {
                allCodes.Add($"{(f.IsFatal ? "FATAL " : "ok    ")}{f.CodeName}");
                if (f.IsFatal)
                    fatalReport.Add($"[{name}] {f.Method}: {f.CodeName} — {f.Message}");
            }
            output.WriteLine($"{name}: {findings.Count} findings ({findings.Count(f => f.IsFatal)} fatal)");
        }

        output.WriteLine("");
        output.WriteLine("=== distinct codes across known-good corpus ===");
        foreach (var c in allCodes) output.WriteLine(c);

        Assert.True(fatalReport.Count == 0,
            "Known-good programs produced FATAL verification findings (partition false positives):\n" + string.Join("\n", fatalReport));
    }
}
