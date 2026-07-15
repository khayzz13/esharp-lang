using System.Reflection;
using Esharp.Syntax.Parsing;
using Binder = Esharp.Binder.Binder;
using Esharp.CodeGen;

namespace Esharp.Tests;

/// <summary>
/// Go-style method receivers: `func (c: *T) m()`, `func (c: T) m()`, `readonly func (c: T) m()`.
/// Attachment is explicit — a bare first-param function is a plain free function (Go model).
/// Value receivers snapshot-copy (struct); pointer receivers mutate through `ref this`.
/// </summary>
public sealed class ILEmitterTests_Receivers
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static int _n;

    static (Assembly? Asm, IReadOnlyList<Esharp.Diagnostics.Diagnostic> Diags) Compile(string src)
    {
        var parser = new Parser(src, "test.es");
        var syntax = parser.ParseCompilationUnit();
        if (parser.Diagnostics.Count > 0) return (null, parser.Diagnostics);
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        if (binder.Diagnostics.Count > 0) return (null, binder.Diagnostics);
        var name = $"EsharpRecv_{Interlocked.Increment(ref _n)}";
        var path = Path.Combine(Path.GetTempPath(), $"{name}.dll");
        EsHarness.EmitBoundToFile(binder, bound, name, path);
        return (Assembly.LoadFrom(path), []);
    }

    static Assembly Ok(string src)
    {
        var (asm, diags) = Compile(src);
        Assert.True(asm is not null, "diagnostics: " + string.Join("; ", diags.Select(d => d.Message)));
        return asm!;
    }

    static object? Call(Assembly asm, string type, string method, params object?[] args)
    {
        var t = asm.GetType($"Test.{type}") ?? throw new Exception($"Test.{type} not found");
        var m = t.GetMethod(method, AnyStatic) ?? throw new Exception($"{method} not found");
        return m.Invoke(null, args);
    }

    [Fact]
    public void ValueReceiver_ReadsField()
    {
        var asm = Ok("""
namespace Test

struct Circle { r: float }

func (c: Circle) area() -> float = c.r * c.r * 3.14159

func run() -> float {
    let c = Circle { r: 2.0 }
    return c.area()
}
""");
        Assert.Equal(2.0 * 2.0 * 3.14159, Convert.ToDouble(Call(asm, "Test", "run")), 5);
    }

    [Fact]
    public void ValueReceiver_IsSnapshot_MutationDoesNotWriteBack()
    {
        // A value receiver operates on a copy: mutating c.r inside grow() must not
        // change the caller's value.
        var asm = Ok("""
namespace Test

struct Circle { r: float }

func (c: Circle) grow() { c.r = 99.0 }

func run() -> float {
    var c = Circle { r: 2.0 }
    c.grow()
    return c.r
}
""");
        Assert.Equal(2.0, Convert.ToDouble(Call(asm, "Test", "run")), 5);
    }

    [Fact]
    public void PointerReceiver_MutatesThroughRefThis()
    {
        var asm = Ok("""
namespace Test

struct Circle { r: float }

func (c: *Circle) scale(k: float) { c.r *= k }

func run() -> float {
    var c = Circle { r: 2.0 }
    c.scale(3.0)
    return c.r
}
""");
        Assert.Equal(6.0, Convert.ToDouble(Call(asm, "Test", "run")), 5);
    }

    [Fact]
    public void ReadonlyReceiver_Reads()
    {
        var asm = Ok("""
namespace Test

struct Circle { r: float }

readonly func (c: Circle) diameter() -> float = c.r * 2.0

func run() -> float {
    let c = Circle { r: 5.0 }
    return c.diameter()
}
""");
        Assert.Equal(10.0, Convert.ToDouble(Call(asm, "Test", "run")), 5);
    }

    [Fact]
    public void BareFirstParam_IsFreeFunction_NotAMethod()
    {
        // No receiver block → plain free function. `area(c)` works; `c.area()` does not
        // resolve to a method (no implicit promotion).
        var asm = Ok("""
namespace Test

struct Circle { r: float }

func area(c: Circle) -> float = c.r * c.r

func run() -> float {
    let c = Circle { r: 3.0 }
    return area(c)
}
""");
        Assert.Equal(9.0, Convert.ToDouble(Call(asm, "Test", "run")), 5);

        var (asm2, diags) = Compile("""
namespace Test

struct Circle { r: float }

func area(c: Circle) -> float = c.r * c.r

func run() -> float {
    let c = Circle { r: 3.0 }
    return c.area()
}
""");
        Assert.True(asm2 is null, "c.area() must not resolve — area is a free function, not a method");
    }

    [Fact]
    public void GenericValueReceiver_MapsAcrossTypeArgs()
    {
        var asm = Ok("""
namespace Test

struct Wrap<T> { v: T }

func (w: Wrap<T>) mapped<T, U>(f: Func<T, U>) -> Wrap<U> = Wrap<U> { v: f(w.v) }

func run() -> int {
    let w = Wrap<int> { v: 3 }
    return w.mapped((x) => x + 5).v
}
""");
        Assert.Equal(8, Convert.ToInt32(Call(asm, "Test", "run")));
    }

    [Fact]
    public void ReadonlyReceiver_RejectsFieldMutation()
    {
        var (asm, diags) = Compile("""
namespace Test

struct Circle { r: float }

readonly func (c: Circle) bad() { c.r = 1.0 }
""");
        Assert.True(asm is null, "writing a field through a readonly receiver must be rejected");
    }

    [Fact]
    public void InlineMethod_Minimal_Class() => Assert.NotNull(Ok("""
namespace Test
class Foo { func Bar() { } }
func make() -> Foo = Foo()
"""));

    [Fact]
    public void InlineMethod_Minimal_Struct() => Assert.NotNull(Ok("""
namespace Test
struct Foo { x: int  func Bar() -> int = self.x }
"""));

    [Fact]
    public void InlineMethod_PascalCase_NotFlaggedByES2161()
    {
        var asm = Ok("""
namespace Test
using "System"
class Res<T> : IDisposable {
    v: T
    init(x: T) { self.v = x }
    func Dispose() { }
}
func make() -> Res<int> = Res<int>(1)
""");
        Assert.NotNull(asm);
    }
}
