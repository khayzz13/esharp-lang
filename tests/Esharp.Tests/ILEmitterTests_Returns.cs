using System.Reflection;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Mono.Cecil;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

/// <summary>
/// Tests for the `returns Type` clause inside `class` / `static func` bodies
/// (the default return type for nested methods that omit `-> Type`). `returns` is
/// no longer a signature synonym for `-> Type` — see the negative test below.
/// </summary>
public sealed class ILEmitterTests_Returns
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static int _asmCounter;

    static (Assembly Asm, IReadOnlyList<Esharp.Diagnostics.Diagnostic> Diagnostics) Compile(string source)
    {
        var asmName = $"EsharpReturnsTest_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        var allDiags = parser.Diagnostics.Concat(binder.Diagnostics).ToList();

        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        EsHarness.EmitBoundToFile(binder, bound, asmName, path);
        return (Assembly.LoadFrom(path), allDiags);
    }

    static object? Invoke(Assembly asm, string typeName, string methodName, params object?[] args)
    {
        var type = asm.GetType($"Test.{typeName}")
            ?? throw new Exception($"Type Test.{typeName} not found");
        var method = type.GetMethod(methodName, AnyStatic)
            ?? throw new Exception($"Method Test.{typeName}.{methodName} not found");
        return method.Invoke(null, args);
    }

    static ModuleDefinition InspectModule(Assembly asm) => ModuleDefinition.ReadModule(asm.Location);

    // ---- `returns` is NOT a signature synonym for `->` ----

    [Fact]
    public void Returns_As_Signature_Synonym_Is_Rejected()
    {
        // `func f() returns int { ... }` no longer parses: with no `->`, the
        // signature has no return type and `returns` is left dangling where a
        // body `{` is expected, so the parser reports.
        var (_, diags) = Compile("""
namespace Test

func add(a: int, b: int) returns int { return a + b }
""");
        Assert.NotEmpty(diags);
    }

    // ---- `returns` clause on static body ----

    [Fact]
    public void Returns_Clause_On_StaticFunc_Sets_Default_Return_Type()
    {
        var (asm, diags) = Compile("""
namespace Test

static IntBag {
    returns int
    func two() { return 2 }
    func three() { return 3 }
    func sum() { return two() + three() }
}

func main() -> int { return IntBag.sum() }
""");
        Assert.Empty(diags);
        Assert.Equal(5, Invoke(asm, "Test", "main"));
    }

    [Fact]
    public void Returns_Clause_On_StaticFunc_All_Methods_Without_Arrow_Get_Type()
    {
        var (asm, diags) = Compile("""
namespace Test

static Strs {
    returns string
    func a() { return "a" }
    func b() { return "b" }
}

func go() -> string { return Strs.a() + Strs.b() }
""");
        Assert.Empty(diags);
        Assert.Equal("ab", Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void Returns_Clause_On_StaticFunc_Explicit_Arrow_Wins()
    {
        var (asm, diags) = Compile("""
namespace Test

static S {
    returns int
    func num() { return 5 }
    func tag() -> string { return "hello" }
}

func combine() -> string { return S.tag() }
""");
        Assert.Empty(diags);
        Assert.Equal("hello", Invoke(asm, "Test", "combine"));
    }

    [Fact]
    public void Returns_Clause_On_StaticFunc_Bound_Method_Has_Correct_ReturnType()
    {
        var (asm, _) = Compile("""
namespace Test

static S {
    returns int
    func go() { return 99 }
}
""");
        var s = InspectModule(asm).Types.First(t => t.Name == "S");
        var go = s.Methods.First(m => m.Name == "go");
        Assert.Equal("System.Int32", go.ReturnType.FullName);
    }

    [Fact]
    public void Returns_Clause_On_StaticFunc_Internal_Cross_Method_Call_Sees_Type()
    {
        // sum() calls two() and three() — those must bind with int return type
        // (from the returns clause) for the addition to type-check correctly.
        var (asm, diags) = Compile("""
namespace Test

static IntBag {
    returns int
    func two() { return 2 }
    func three() { return 3 }
    func sum() { return two() + three() }
}

func go() -> int { return IntBag.sum() }
""");
        Assert.Empty(diags);
        Assert.Equal(5, Invoke(asm, "Test", "go"));
    }

    // ---- `returns` clause on class body ----

    [Fact]
    public void Returns_Clause_On_RefData_Sets_Default_For_Methods()
    {
        var (asm, diags) = Compile("""
namespace Test

class Q {
    returns Q
    n: int

    init(n: int) { self.n = n }

    func bump() { return Q(self.n + 1) }
}

func go() -> int {
    let q = Q(0)
    let r = q.bump()
    return r.n
}
""");
        Assert.Empty(diags);
        Assert.Equal(1, Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void Returns_Clause_On_RefData_Fluent_Chaining()
    {
        var (asm, diags) = Compile("""
namespace Test

class Q {
    returns Q
    n: int

    init(n: int) { self.n = n }

    func bump() { return Q(self.n + 1) }
}

func go() -> int {
    let q = Q(0)
    let r = q.bump().bump().bump()
    return r.n
}
""");
        Assert.Empty(diags);
        Assert.Equal(3, Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void Returns_Clause_On_RefData_Explicit_Arrow_Wins()
    {
        var (asm, diags) = Compile("""
namespace Test

class Q {
    returns Q
    n: int

    init(n: int) { self.n = n }

    func bump() { return Q(self.n + 1) }
    func count() -> int { return self.n }
}

func go() -> int {
    let q = Q(0)
    return q.bump().count()
}
""");
        Assert.Empty(diags);
        Assert.Equal(1, Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void Returns_Clause_On_RefData_Bound_Method_Has_Correct_ReturnType()
    {
        var (asm, _) = Compile("""
namespace Test

class Q {
    returns Q
    n: int

    init(n: int) { self.n = n }

    func bump() { return Q(self.n + 1) }
}
""");
        var q = InspectModule(asm).Types.First(t => t.Name == "Q");
        var bump = q.Methods.First(m => m.Name == "bump");
        Assert.Equal("Test.Q", bump.ReturnType.FullName);
    }

    // ---- Negative: no returns clause means missing -> defaults to void ----

    [Fact]
    public void NoReturnsClause_NoArrow_DefaultsToVoid()
    {
        var (asm, diags) = Compile("""
namespace Test

class Q {
    n: int

    init(n: int) { self.n = n }

    func touch() { } // no -> Type, no returns clause: void
}

func go() {
    let q = Q(0)
    q.touch()
}
""");
        Assert.Empty(diags);
        var q = InspectModule(asm).Types.First(t => t.Name == "Q");
        var touch = q.Methods.First(m => m.Name == "touch");
        Assert.Equal("System.Void", touch.ReturnType.FullName);
    }

    [Fact]
    public void NoReturnsClause_OnlyExplicitArrowsTakeEffect()
    {
        var (asm, diags) = Compile("""
namespace Test

static S {
    func a() { } // void
    func b() -> int { return 1 }
}

func go() -> int {
    S.a()
    return S.b()
}
""");
        Assert.Empty(diags);
        Assert.Equal(1, Invoke(asm, "Test", "go"));
    }

    // ---- Returns with various types ----

    [Fact]
    public void Returns_Clause_With_Primitive_Type()
    {
        var (asm, diags) = Compile("""
namespace Test

static B {
    returns bool
    func t() { return true }
    func f() { return false }
    func both() { return t() and f() }
}

func go() -> bool { return B.both() }
""");
        Assert.Empty(diags);
        Assert.Equal(false, Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void Returns_Clause_With_String_Type()
    {
        var (asm, diags) = Compile("""
namespace Test

static S {
    returns string
    func hi() { return "hi" }
}

func go() -> string { return S.hi() }
""");
        Assert.Empty(diags);
        Assert.Equal("hi", Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void Returns_Clause_With_User_Data_Type()
    {
        var (asm, diags) = Compile("""
namespace Test

struct Point {
    x: int
    y: int
}

static PointBag {
    returns Point
    func origin() { return Point { x: 0, y: 0 } }
    func one() { return Point { x: 1, y: 1 } }
}

func sum() -> int {
    let p = PointBag.one()
    return p.x + p.y
}
""");
        Assert.Empty(diags);
        Assert.Equal(2, Invoke(asm, "Test", "sum"));
    }

    [Fact]
    public void Returns_Clause_Sees_Cross_Method_Resolution_With_DataType()
    {
        var (asm, diags) = Compile("""
namespace Test

class Counter {
    returns Counter
    n: int

    init(n: int) { self.n = n }

    func inc() { return Counter(self.n + 1) }
    func twice() { return self.inc().inc() }
}

func go() -> int { return Counter(0).twice().n }
""");
        Assert.Empty(diags);
        Assert.Equal(2, Invoke(asm, "Test", "go"));
    }

    // ---- Source-level keyword backward compat ----

    [Fact]
    public void Returns_Is_Still_Valid_Identifier_Elsewhere()
    {
        var (asm, diags) = Compile("""
namespace Test

func go(returns: int) -> int { return returns + 1 }
""");
        Assert.Empty(diags);
        Assert.Equal(11, Invoke(asm, "Test", "go", 10));
    }

    // ── Additional coverage ──

    [Fact]
    public void Returns_Clause_With_Two_Methods_Both_Inherit()
    {
        var (asm, diags) = Compile("""
namespace Test

static Calc {
    returns int

    func double_it(n: int) = n * 2
    func triple_it(n: int) = n * 3
}

func test() -> int = Calc.double_it(5) + Calc.triple_it(5)
""");
        Assert.Empty(diags);
        Assert.Equal(25, Invoke(asm, "Test", "test"));
    }
}
