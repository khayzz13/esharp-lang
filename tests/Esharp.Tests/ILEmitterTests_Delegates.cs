using System.Reflection;
using Esharp.Diagnostics;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

// Delegates — the GC/multicast/interop tier (distinct from function pointers, the
// zero-alloc tier). Covers method-group → delegate conversion (incl. bridging to a
// named BCL delegate) and, below, nominal `delegate func` declarations.
public sealed class ILEmitterTests_Delegates
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static int _asmCounter;

    static Assembly CompileAndLoad(string source)
    {
        var asmName = $"EsharpDelegTest_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        return EsHarness.EmitBoundToFileVerifiedAndLoad(binder, bound, asmName, path);
    }

    static object? Invoke(Assembly asm, string typeName, string methodName, params object?[] args)
    {
        var type = asm.GetType($"Test.{typeName}") ?? throw new Exception($"Type Test.{typeName} not found");
        var method = type.GetMethod(methodName, AnyStatic) ?? throw new Exception($"Method {methodName} not found");
        return method.Invoke(null, args);
    }

    // ---- Method-group → delegate conversion ----

    [Fact]
    public void MethodGroup_ToFunc_Local_Invoked()
    {
        var asm = CompileAndLoad("""
namespace Test

func dbl(x: int) -> int = x * 2

func test() -> int {
    let f: Func<int, int> = dbl
    return f(21)
}
""");
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void MethodGroup_AsArgument_Invoked()
    {
        var asm = CompileAndLoad("""
namespace Test

func dbl(x: int) -> int = x * 2
func apply(x: int, f: Func<int, int>) -> int = f(x)

func test() -> int = apply(5, dbl)
""");
        Assert.Equal(10, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void MethodGroup_ToExternalNamedDelegate_Bridges()
    {
        // Predicate<T> is a BCL *named* delegate, nominally distinct from Func<int,bool>.
        // Converting a method group to it is the cross-assembly bridging case.
        var asm = CompileAndLoad("""
namespace Test

func is_even(x: int) -> bool = x % 2 == 0

func test() -> bool {
    let p: Predicate<int> = is_even
    return p(4)
}
""");
        Assert.Equal(true, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void MethodGroup_BindsRealMethod_NotAForwarder()
    {
        // Direct binding: the delegate's Method is the real `dbl`, not a synthesized
        // forwarder — so reflection/interop sees the actual target (the CLR-citizenship
        // requirement). Proven by reading delegate.Method.Name.
        var asm = CompileAndLoad("""
namespace Test

func dbl(x: int) -> int = x * 2
func get() -> Func<int, int> = dbl
""");
        var d = (Delegate)Invoke(asm, "Test", "get")!;
        Assert.Equal("dbl", d.Method.Name);
    }

    [Fact]
    public void FuncLiteral_ReturnsCapturingFunc_WithIndependentFactoryState()
    {
        // A `func` literal may itself return Func<...>. Each factory call creates a
        // distinct display object; repeated invocation of one returned Func shares
        // its captured `var` rather than resetting it.
        var asm = CompileAndLoad("""
namespace Test

func test() -> int {
    let makeCounter: Func<int, Func<int>> = func(start: int) -> Func<int> {
        var count = start
        return func() -> int {
            count += 1
            return count
        }
    }

    let first = makeCounter(0)
    let second = makeCounter(10)
    return first() * 100 + first() * 10 + second()
}
""");
        Assert.Equal(131, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void MethodGroup_Unannotated_Let_IsRejected()
    {
        // No expected delegate type → conversion does not fire; a bare function name as
        // a value is rejected rather than silently picking fn-ptr vs delegate / allocating.
        var diags = EsHarness.AllDiagnostics("""
namespace Test

func dbl(x: int) -> int = x * 2

func test() -> int {
    let f = dbl
    return f(21)
}
""");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error);
    }

    // ---- Nominal `delegate func` declarations ----

    [Fact]
    public void DelegateFunc_EmitsMulticastDelegateSubclass_WithInvoke()
    {
        // A `delegate func` mints a real CLR delegate type: sealed, derives from
        // MulticastDelegate, identity = its Invoke signature. Exactly the shape C#
        // emits for `delegate int BinOp(int,int)` — so it bridges assemblies unchanged.
        var asm = CompileAndLoad("""
namespace Test

delegate func BinOp(a: int, b: int) -> int
""");
        var t = asm.GetType("Test.BinOp") ?? throw new Exception("Test.BinOp not emitted");
        Assert.True(typeof(MulticastDelegate).IsAssignableFrom(t), "BinOp must derive from MulticastDelegate");
        Assert.True(t.IsSealed, "delegate types are sealed");
        var invoke = t.GetMethod("Invoke") ?? throw new Exception("Invoke not found");
        Assert.Equal(typeof(int), invoke.ReturnType);
        Assert.Equal(new[] { typeof(int), typeof(int) }, invoke.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void DelegateFunc_AsParam_MethodGroupArg_Invoked()
    {
        // The delegate as a parameter type; a method group converts to it at the call
        // site (target-typed by the param), and the callee invokes the delegate value.
        var asm = CompileAndLoad("""
namespace Test

delegate func BinOp(a: int, b: int) -> int

func apply(f: BinOp, a: int, b: int) -> int = f(a, b)
func add(a: int, b: int) -> int = a + b

func test() -> int = apply(add, 20, 22)
""");
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void DelegateFunc_MethodGroup_Let_Invoked()
    {
        var asm = CompileAndLoad("""
namespace Test

delegate func BinOp(a: int, b: int) -> int

func add(a: int, b: int) -> int = a + b

func test() -> int {
    let op: BinOp = add
    return op(19, 23)
}
""");
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void DelegateFunc_Lambda_Let_Invoked()
    {
        // A lambda lands in a `delegate func`-typed slot as *that* nominal delegate
        // (param types inferred from its Invoke shape), not a default Func.
        var asm = CompileAndLoad("""
namespace Test

delegate func BinOp(a: int, b: int) -> int

func test() -> int {
    let op: BinOp = (a, b) => a + b
    return op(40, 2)
}
""");
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void DelegateFunc_ReturnedAndInvoked()
    {
        // Method-group → named delegate in *return* position, inferred local type from
        // the call, then invoke. Exercises the full author/store/cross/invoke loop.
        var asm = CompileAndLoad("""
namespace Test

delegate func BinOp(a: int, b: int) -> int

func add(a: int, b: int) -> int = a + b
func get_op() -> BinOp = add

func test() -> int {
    let op = get_op()
    return op(13, 29)
}
""");
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void DelegateFunc_IsNominal_NotAForwarder_NotAFunc()
    {
        // The materialized value's runtime type is the nominal BinOp (not a Func),
        // it binds the real `add` directly (CLR citizenship), and a structurally
        // identical Func<int,int,int> is a *different* type.
        var asm = CompileAndLoad("""
namespace Test

delegate func BinOp(a: int, b: int) -> int

func add(a: int, b: int) -> int = a + b
func get_op() -> BinOp = add
""");
        var d = (Delegate)Invoke(asm, "Test", "get_op")!;
        Assert.Equal("Test.BinOp", d.GetType().FullName);
        Assert.Equal("add", d.Method.Name);
        Assert.False(d is Func<int, int, int>, "BinOp is nominally distinct from Func<int,int,int>");
    }

    [Fact]
    public void DelegateFunc_VoidReturn_CapturingLambda_Invoked()
    {
        // A void `delegate func`, materialized from a capturing lambda via a typed let,
        // invoked repeatedly through a parameter slot. Exercises closure capture +
        // void Invoke dispatch on a same-compilation delegate.
        var asm = CompileAndLoad("""
namespace Test

delegate func Tick()

func run(t: Tick, n: int) {
    var i = 0
    while i < n {
        t()
        i += 1
    }
}

func test() -> int {
    var count = 0
    let t: Tick = func() { count += 1 }
    run(t, 3)
    return count
}
""");
        Assert.Equal(3, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void DelegateFunc_ThreeParams_MethodGroup_Invoked()
    {
        var asm = CompileAndLoad("""
namespace Test

delegate func Tri(a: int, b: int, c: int) -> int

func add3(a: int, b: int, c: int) -> int = a + b + c

func test() -> int {
    let f: Tri = add3
    return f(10, 20, 12)
}
""");
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }
}
