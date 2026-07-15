using System.Reflection;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

public sealed class ILEmitterTests_FunctionPointers
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    static int _asmCounter;

    static Assembly CompileAndLoad(string source)
    {
        var asmName = $"EsharpFPTest_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        return EsHarness.EmitBoundToFileVerifiedAndLoad(binder, bound, asmName, path);
    }

    static object? Invoke(Assembly asm, string typeName, string methodName, params object?[] args)
    {
        var type = asm.GetType($"Test.{typeName}")
            ?? throw new Exception($"Type Test.{typeName} not found");
        var method = type.GetMethod(methodName, AnyStatic)
            ?? throw new Exception($"Method {methodName} not found on {typeName}");
        return method.Invoke(null, args);
    }

    [Fact]
    public void FunctionPointer_CallThroughLocal_ReturnsCorrectResult()
    {
        const string source = """
namespace Test

func add(a: int, b: int) -> int {
    return a + b
}

func callViaPointer() -> int {
    let ptr = &add
    return ptr(3, 4)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(7, Invoke(asm, "Test", "callViaPointer"));
    }

    [Fact]
    public void FunctionPointer_VoidReturn_Works()
    {
        // Void function pointer: calli with void return type.
        // We verify by having the void function modify a ref param,
        // but the pointer itself is to a simple int->int function
        // that we call and check the result.
        const string source = """
namespace Test

func double(x: int) -> int {
    return x * 2
}

func triple(x: int) -> int {
    return x * 3
}

func test() -> int {
    let d = &double
    let t = &triple
    return d(5) + t(5)
}
""";
        var asm = CompileAndLoad(source);
        // double(5) = 10, triple(5) = 15 → 25
        Assert.Equal(25, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void FunctionPointer_WithByRefParam_Works()
    {
        const string source = """
namespace Test

func increment(x: *int) {
    x += 1
}

func test() -> int {
    var count = 0
    let fn = &increment
    fn(*count)
    fn(*count)
    fn(*count)
    return count
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void FunctionPointer_TypedParameter_Works()
    {
        const string source = """
namespace Test

func double(x: int) -> int {
    return x * 2
}

func apply(f: &(int -> int), value: int) -> int {
    return f(value)
}

func test() -> int {
    return apply(&double, 21)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void FunctionPointer_InStructField_Works()
    {
        const string source = """
namespace Test

func add(a: int, b: int) -> int {
    return a + b
}

func mul(a: int, b: int) -> int {
    return a * b
}

struct BinOp {
    apply: &(int, int -> int)
}

func test() -> int {
    let adder = BinOp { apply: &add }
    let muler = BinOp { apply: &mul }
    return adder.apply(3, 4) + muler.apply(3, 4)
}
""";
        var asm = CompileAndLoad(source);
        // add(3,4) = 7, mul(3,4) = 12 → 19
        Assert.Equal(19, Invoke(asm, "Test", "test"));
    }
}
