// Adversarial coverage: each test tries to re-break a subsystem that a prior
// root-cause pass fixed — string interpolation (sub-lexer), value-type generics,
// namespace-scoped resolution (C#-like usings + qualifiers), definite-return,
// lexer numeric exponents, and self-reflection over the emitted assembly. Every
// compiled assembly is run through ILVerify in the harness, so unverifiable IL
// fails here as an ES0900 diagnostic rather than a runtime InvalidProgramException.
//
// Style note: E# source stays in readable """ raw-string blocks (they double as
// the language corpus) — do not collapse into inline \n one-liners.
using System.Reflection;
using Esharp.Diagnostics;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

public sealed class ILEmitterTests_Coverage_Adversarial
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static int _asmCounter;

    static object? Run(string src, string method = "go", params object?[] args)
        => EsHarness.Run(src, method, args);

    // Multi-file compile through one shared binder (cross-namespace resolution),
    // emitting a single library. Returns the assembly (null on error) and the full
    // diagnostic list so negative tests can assert on the error code.
    static (Assembly? Asm, IReadOnlyList<Diagnostic> Diagnostics) CompileMulti(params string[] sources)
    {
        var asmName = $"EsAdversarial_{Interlocked.Increment(ref _asmCounter)}";
        var binder = new Esharp.Binder.Binder();
        var parsed = new List<Esharp.Syntax.CompilationUnitSyntax>();
        for (var i = 0; i < sources.Length; i++)
        {
            var parser = new Parser(sources[i], $"adv{i}.es");
            var syntax = parser.ParseCompilationUnit();
            Assert.Empty(parser.Diagnostics);
            parsed.Add(syntax);
        }
        foreach (var s in parsed) binder.RegisterTypes(s);
        foreach (var s in parsed) binder.RegisterSignatures(s);
        var bound = parsed.Select(binder.BindUnit).ToList();
        if (binder.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return (null, binder.Diagnostics.ToList());
        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        var ilDiags = EsHarness.EmitBoundToFile(binder, bound, asmName, path, verify: true);
        var all = binder.Diagnostics.Concat(ilDiags).ToList();
        if (all.Any(d => d.Severity == DiagnosticSeverity.Error))
            return (null, all);
        return (Assembly.LoadFrom(path), all);
    }

    static object? Invoke(Assembly asm, string typeName, string method, params object?[] args)
    {
        var type = asm.GetType($"Test.{typeName}") ?? throw new Exception($"Type Test.{typeName} not found");
        var m = type.GetMethod(method, AnyStatic) ?? throw new Exception($"Method {method} not found");
        return m.Invoke(null, args.Length == 0 ? null : args);
    }

    static IReadOnlyList<Diagnostic> Diags(string source) => EsHarness.Diagnostics(source);

    // ════════════════════════════════════════════════════════════════════
    // Interpolation sub-lexer — nested holes, escapes, chains, calls
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Interp_ObjectLiteralInHole()
    {
        const string src = """
namespace Test

struct P { x: int, y: int }

func go() -> string = "p={P { x: 3, y: 4 }.x}"
""";
        Assert.Equal("p=3", Run(src));
    }

    [Fact]
    public void Interp_NestedMatchInHole()
    {
        const string src = """
namespace Test

func go() -> string {
    let n = 2
    return "v={match n { 1 { "one" } 2 { "two" } default { "?" } }}"
}
""";
        Assert.Equal("v=two", Run(src));
    }

    [Fact]
    public void Interp_EscapedBracesMixedWithHole()
    {
        const string src = """
namespace Test

func go() -> string {
    let n = 7
    return "{{literal}} = {n}"
}
""";
        Assert.Equal("{literal} = 7", Run(src));
    }

    [Fact]
    public void Interp_ChainedMemberAccess()
    {
        const string src = """
namespace Test

struct Inner { v: int }
struct Outer { inner: Inner }

func go() -> string {
    let o = Outer { inner: Inner { v: 42 } }
    return "got {o.inner.v}"
}
""";
        Assert.Equal("got 42", Run(src));
    }

    [Fact]
    public void Interp_MethodCallInHole()
    {
        const string src = """
namespace Test

func square(n: int) -> int = n * n

func go() -> string = "sq={square(5)}"
""";
        Assert.Equal("sq=25", Run(src));
    }

    [Fact]
    public void Interp_TernaryWithNestedStringQuotes()
    {
        const string src = """
namespace Test

func go() -> string {
    let n = 0 - 3
    return "sign={n > 0 ? "+" : "-"}"
}
""";
        Assert.Equal("sign=-", Run(src));
    }

    [Fact]
    public void Interp_PropertyGetterHole_ValueTypeBoxes()
    {
        const string src = """
namespace Test

func go() -> string {
    let s = "hello"
    return "len={s.Length}"
}
""";
        Assert.Equal("len=5", Run(src));
    }

    [Fact]
    public void Interp_MultipleHolesAndOperators()
    {
        const string src = """
namespace Test

func go() -> string {
    let a = 3
    let b = 4
    return "{a}+{b}={a + b}"
}
""";
        Assert.Equal("3+4=7", Run(src));
    }

    // ════════════════════════════════════════════════════════════════════
    // Value-type generics — collections, nullables, tuples, generic choices
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Generics_ListOfValueData_AddAndIndex()
    {
        const string src = """
namespace Test

struct Pt { x: int, y: int }

func go() -> int {
    let xs = List<Pt>()
    xs.Add(Pt { x: 10, y: 20 })
    xs.Add(Pt { x: 30, y: 40 })
    return xs[0].y + xs[1].x
}
""";
        Assert.Equal(50, Run(src));
    }

    [Fact]
    public void Generics_DictionaryStringToValueData()
    {
        const string src = """
namespace Test

struct Score { points: int }

func go() -> int {
    let m = Dictionary<string, Score>()
    m["a"] = Score { points: 5 }
    m["b"] = Score { points: 9 }
    return m["a"].points + m["b"].points
}
""";
        Assert.Equal(14, Run(src));
    }

    [Fact]
    public void Generics_ListOfNullableInt()
    {
        const string src = """
namespace Test

func go() -> int {
    let xs = List<int?>()
    xs.Add(7)
    xs.Add(nil)
    return xs[0] ?? 0
}
""";
        Assert.Equal(7, Run(src));
    }

    [Fact]
    public void Generics_NullableDoubleCoalesceAndConditional()
    {
        const string src = """
namespace Test

func pick(v: double?) -> double = v ?? 1.5

func go() -> double {
    return pick(nil) + pick(2.5)
}
""";
        Assert.Equal(4.0, Run(src));
    }

    [Fact]
    public void Generics_TupleListForDestructure()
    {
        const string src = """
namespace Test

func go() -> int {
    let pairs = List<(int, int)>()
    pairs.Add((1, 2))
    pairs.Add((3, 4))
    var total = 0
    for (a, b) in pairs {
        total += a * b
    }
    return total
}
""";
        Assert.Equal(14, Run(src)); // 1*2 + 3*4
    }

    [Fact]
    public void Generics_OptionPayloadMatch()
    {
        const string src = """
namespace Test

union Option<T> {
    some(value: T)
    none
}

func unwrapOr(o: Option<int>, fallback: int) -> int {
    match o {
        .some(v) { return v }
        .none { return fallback }
    }
    return fallback
}

func go() -> int {
    let a = Option<int>.some(99)
    let b = Option<int>.none()
    return unwrapOr(a, -1) + unwrapOr(b, 5)
}
""";
        Assert.Equal(104, Run(src));
    }

    [Fact]
    public void Generics_UserPairSwap()
    {
        const string src = """
namespace Test

struct Pair<A, B> {
    first: A
    second: B
}

func go() -> int {
    let p = Pair<int, int> { first: 3, second: 7 }
    let q = Pair<int, int> { first: p.second, second: p.first }
    return q.first - q.second
}
""";
        Assert.Equal(4, Run(src)); // 7 - 3
    }

    [Fact]
    public void Generics_NestedTuple()
    {
        const string src = """
namespace Test

func go() -> int {
    let t = (1, (2, 3))
    return t.Item1 + t.Item2.Item1 + t.Item2.Item2
}
""";
        Assert.Equal(6, Run(src));
    }

    // ════════════════════════════════════════════════════════════════════
    // Namespace-scoped resolution — usings, qualifiers, collisions, casing
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Ns_UsingBringsTypeAndFreeFuncBare()
    {
        // `using "Lib"` = `using Lib` + `using static Lib.Lib`: both the type (Vec,
        // constructed bare) AND a genuinely-bare free function (`incr`, no data
        // receiver) are brought into scope by the single import.
        const string lib = """
namespace Lib

struct Vec { x: int }

func incr(n: int) -> int {
    return n + 1
}
""";
        const string app = """
namespace Test
using "Lib"

func go() -> int {
    let v = Vec { x: 10 }
    return incr(v.x)
}
""";
        var (asm, diags) = CompileMulti(lib, app);
        Assert.True(asm is not null, string.Join("\n", diags.Select(d => d.Message)));
        Assert.Equal(11, Invoke(asm!, "Test", "go"));
    }

    [Fact]
    public void Ns_PromotedMethodReachableViaReceiverType()
    {
        // `bump(v: Vec)` promotes to `Vec.bump` — it is a method on Vec, not a free
        // function. Go method-set semantics on the CLR: the method travels with its
        // receiver type, so once Vec is in scope (here via `using "Lib"`) the method
        // is callable as `v.bump()` regardless of which namespace declared it — no
        // `Lib.bump` qualification, no function-level import. The free-call spelling
        // `bump(v)` is NOT valid (see Ns_ValueReceiverFreeCall_IsError).
        const string lib = """
namespace Lib

struct Vec { x: int }

func (v: Vec) bump() -> int {
    return v.x + 1
}
""";
        const string app = """
namespace Test
using "Lib"

func go() -> int {
    let v = Vec { x: 10 }
    return v.bump()
}
""";
        var (asm, diags) = CompileMulti(lib, app);
        Assert.True(asm is not null, string.Join("\n", diags.Select(d => d.Message)));
        Assert.Equal(11, Invoke(asm!, "Test", "go"));
    }

    [Fact]
    public void Ns_ValueReceiverFreeCall_IsError()
    {
        // The free-call spelling of a value-receiver promoted method is a hard error
        // (ES2142) — it must be called as a method. (Go-strict: value receiver = `.`
        // dispatch only; if you want a free function on a struct, take `*T`.)
        const string src = """
namespace Test

struct Vec { x: int }

func (v: Vec) bump() -> int {
    return v.x + 1
}

func go() -> int {
    let v = Vec { x: 10 }
    return bump(v)
}
""";
        Assert.Contains(Diags(src), d => d.Message.Contains("ES2142"));
    }

    [Fact]
    public void Ns_PointerReceiverFreeCall_IsError()
    {
        // The free-call spelling of a pointer-receiver method is a hard error (ES2142) —
        // it must be called as a method. A pointer receiver emits a static host, but that
        // host is reachable only as `v.bump()`, never `bump(v)`. (A plain free function
        // `func bump(v: *Vec)` — no receiver block — stays free-callable; that's the
        // difference. Go-strict: a method is method-only.)
        const string src = """
namespace Test

struct Vec { x: int }

func (v: *Vec) bump() {
    v.x += 1
}

func go() -> int {
    var v: *Vec = new Vec { x: 10 }
    bump(v)
    return v.x
}
""";
        Assert.Contains(Diags(src), d => d.Message.Contains("ES2142"));
    }

    [Fact]
    public void Ns_PromotedMethodReachableViaQualifiedReceiverType()
    {
        // Same as above but the receiver type is reached by qualifier (`Lib.Vec`)
        // rather than an import — the promoted method is still attached to the type
        // and dispatches as `v.bump()`.
        const string lib = """
namespace Lib

struct Vec { x: int }

func (v: Vec) bump() -> int {
    return v.x + 1
}
""";
        const string app = """
namespace Test

func go() -> int {
    let v = Lib.Vec { x: 41 }
    return v.bump()
}
""";
        var (asm, diags) = CompileMulti(lib, app);
        Assert.True(asm is not null, string.Join("\n", diags.Select(d => d.Message)));
        Assert.Equal(42, Invoke(asm!, "Test", "go"));
    }

    [Fact]
    public void Ns_QualifiedValueReceiverFreeCall_IsError()
    {
        // The QUALIFIED free-call spelling of a value-receiver promoted method is the same
        // hard error as the bare form (ES2142). `bump(v: Vec)` and `Vec` share namespace
        // `Lib`, so `bump` promotes onto `Vec` and is a method, not a free function on the
        // `Lib` host class — `Lib.bump(v)` does not name a free function. Call it `v.bump()`
        // (the spec-correct method form is covered by Ns_PromotedMethodReachableViaQualified-
        // ReceiverType). Cross-namespace promotion never happens, so a method is never reached
        // by qualifying it as a free function.
        const string lib = """
namespace Lib

struct Vec { x: int }

func (v: Vec) bump() -> int {
    return v.x + 1
}
""";
        const string app = """
namespace Test

func go() -> int {
    let v = Lib.Vec { x: 41 }
    return Lib.bump(v)
}
""";
        var (_, diags) = CompileMulti(lib, app);
        Assert.Contains(diags, d => d.Message.Contains("ES2142"));
    }

    [Fact]
    public void Ns_ThreeNamespacesChainedViaUsings()
    {
        const string core = """
namespace Core

struct Money { cents: int }
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
using "Ledger"

func go() -> int {
    let a = Money { cents: 150 }
    return total(a, Money { cents: 350 })
}
""";
        // `total(a: Money, …)` lives in `Ledger`; `Money` lives in `Core` — different
        // namespaces, so `total` does NOT promote onto `Money`. It stays a free function on
        // `Ledger`'s host class, reached bare via `using "Ledger"` (or `Ledger.total(...)`),
        // never `a.total(...)`. (Cross-namespace functions never gain a method form.)
        var (asm, diags) = CompileMulti(core, ledger, app);
        Assert.True(asm is not null, string.Join("\n", diags.Select(d => d.Message)));
        Assert.Equal(500, Invoke(asm!, "Test", "go"));
    }

    [Fact]
    public void Ns_BareCrossNamespace_WithoutUsing_IsError()
    {
        const string lib = """
namespace Lib

struct Vec { x: int }
""";
        const string app = """
namespace Test

func go() -> int {
    let v = Vec { x: 1 }
    return v.x
}
""";
        var (asm, diags) = CompileMulti(lib, app);
        Assert.Null(asm);
        Assert.Contains(diags, d => d.Message.Contains("ES2150"));
    }

    [Fact]
    public void Ns_AmbiguousAcrossUsings_IsError()
    {
        // Two imported namespaces both declare `Widget`; a bare reference is
        // ambiguous and must be qualified (ES2151).
        const string a = """
namespace A

struct Widget { a: int }
""";
        const string b = """
namespace B

struct Widget { b: int }
""";
        const string app = """
namespace Test
using "A"
using "B"

func go() -> int {
    let w = Widget { a: 1 }
    return w.a
}
""";
        var (asm, diags) = CompileMulti(a, b, app);
        Assert.Null(asm);
        Assert.Contains(diags, d => d.Message.Contains("ES2151"));
    }

    [Fact]
    public void Ns_AmbiguousResolvedByQualifier()
    {
        const string a = """
namespace A

struct Widget { a: int }
""";
        const string b = """
namespace B

struct Widget { b: int }
""";
        const string app = """
namespace Test
using "A"
using "B"

func go() -> int {
    let w = A.Widget { a: 9 }
    return w.a
}
""";
        var (asm, diags) = CompileMulti(a, b, app);
        Assert.True(asm is not null, string.Join("\n", diags.Select(d => d.Message)));
        Assert.Equal(9, Invoke(asm!, "Test", "go"));
    }

    [Fact]
    public void Ns_LowerCaseTypeName_IsError()
    {
        const string src = """
namespace Test

struct widget { x: int }
""";
        Assert.Contains(Diags(src), d => d.Message.Contains("ES2160"));
    }

    [Fact]
    public void Ns_SnakeCaseTypeName_IsError()
    {
        const string src = """
namespace Test

union my_choice {
    a
    b
}
""";
        Assert.Contains(Diags(src), d => d.Message.Contains("ES2160"));
    }

    // ════════════════════════════════════════════════════════════════════
    // Definite-return — fall-through is an error; exhaustive terminates clean
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void DefiniteReturn_FallThrough_IsError()
    {
        const string src = """
namespace Test

func go(n: int) -> int {
    if n > 0 {
        return 1
    }
}
""";
        Assert.Contains(Diags(src), d => d.Message.Contains("ES2140"));
    }

    [Fact]
    public void DefiniteReturn_ExhaustiveBool_NoTrailingReturn_Clean()
    {
        const string src = """
namespace Test

func label(b: bool) -> string {
    match b {
        true { return "on" }
        false { return "off" }
    }
}

func go() -> string = label(true)
""";
        Assert.Equal("on", Run(src));
    }

    [Fact]
    public void DefiniteReturn_ExhaustiveChoice_NoTrailingReturn_Clean()
    {
        const string src = """
namespace Test

union Dir { north, south }

func name(d: Dir) -> string {
    match d {
        .north { return "N" }
        .south { return "S" }
    }
}

func go() -> string = name(Dir.south())
""";
        Assert.Equal("S", Run(src));
    }

    [Fact]
    public void DefiniteReturn_ExhaustiveEnum_NoTrailingReturn_Clean()
    {
        const string src = """
namespace Test

enum Color { red, green, blue }

func code(c: Color) -> int {
    match (c: Color) {
        .red { return 1 }
        .green { return 2 }
        .blue { return 3 }
    }
}

func go() -> int = code(Color.green())
""";
        Assert.Equal(2, Run(src));
    }

    [Fact]
    public void DefiniteReturn_InfiniteLoopTerminates_Clean()
    {
        const string src = """
namespace Test

func go() -> int {
    var i = 0
    while true {
        i += 1
        if i >= 5 {
            return i
        }
    }
}
""";
        Assert.Equal(5, Run(src));
    }

    // ════════════════════════════════════════════════════════════════════
    // Lexer numeric exponents
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Lexer_ExponentLowercaseE()
    {
        const string src = """
namespace Test

func go() -> double = 1.0e10
""";
        Assert.Equal(1.0e10, Run(src));
    }

    [Fact]
    public void Lexer_ExponentNegative()
    {
        const string src = """
namespace Test

func go() -> double = 1.5e-3
""";
        Assert.Equal(1.5e-3, Run(src));
    }

    [Fact]
    public void Lexer_ExponentUppercaseE()
    {
        const string src = """
namespace Test

func go() -> double = 2E8
""";
        Assert.Equal(2e8, Run(src));
    }

    [Fact]
    public void Lexer_ExponentLargeMantissa()
    {
        const string src = """
namespace Test

func go() -> double = 6.022e23
""";
        Assert.Equal(6.022e23, Run(src));
    }

    [Fact]
    public void Lexer_UnderscoreSeparators()
    {
        const string src = """
namespace Test

func go() -> int = 1_000_000
""";
        Assert.Equal(1_000_000, Run(src));
    }

    // ════════════════════════════════════════════════════════════════════
    // Self-reflection over the emitted assembly + executable shapes
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Reflection_GetTypeNameOfData()
    {
        const string src = """
namespace Test

struct Widget { id: int }

func go() -> string {
    let w = Widget { id: 1 }
    return w.GetType().Name
}
""";
        Assert.Equal("Widget", Run(src));
    }

    [Fact]
    public void Reflection_CountDeclaredMethodsViaInstanceType()
    {
        const string src = """
namespace Test
using "System.Reflection"

struct Widget { id: int }

func go() -> bool {
    let w = Widget { id: 1 }
    let methods = w.GetType().GetMethods()
    return methods.Length >= 1
}
""";
        Assert.Equal(true, Run(src));
    }
}
