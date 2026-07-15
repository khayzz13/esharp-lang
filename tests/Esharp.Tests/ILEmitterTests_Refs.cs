using System.Reflection;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

public sealed class ILEmitterTests_Refs
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    static int _asmCounter;

    static Assembly CompileAndLoad(string source)
    {
        var asmName = $"EsharpRefTest_{Interlocked.Increment(ref _asmCounter)}";
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
    public void AddressOf_PassedToByRefParam_Works()
    {
        // &x passed directly to a *T parameter — no ref local needed
        const string source = """
namespace Test

func increment(x: *int) {
    x += 1
}

func test() -> int {
    var count = 0
    increment(&count)
    increment(&count)
    return count
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(2, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void RefLocal_ReadThroughPointer_Works()
    {
        const string source = """
namespace Test

func test() -> int {
    var x = 42
    var p = &x
    return p
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void RefLocal_WriteThroughPointer_MutatesOriginal()
    {
        const string source = """
namespace Test

func test() -> int {
    var x = 10
    var p = &x
    p += 5
    return x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(15, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void RefLocal_MultipleWrites_Accumulate()
    {
        const string source = """
namespace Test

func test() -> int {
    var count = 0
    var p = &count
    p += 1
    p += 1
    p += 1
    return count
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void ReadOnlyByRef_CanReadThrough()
    {
        const string source = """
namespace Test

func readVal(x: readonly *int) -> int {
    return x
}

func test() -> int {
    var n = 42
    return readVal(&n)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void ReadOnlyByRef_StructFieldAccess_Works()
    {
        const string source = """
namespace Test

struct Rect {
    x: float
    y: float
    w: float
    h: float
}

func area(r: readonly *Rect) -> float {
    return r.w * r.h
}

func test() -> float {
    let r = Rect { x: 0.0, y: 0.0, w: 10.0, h: 5.0 }
    let result = area(&r)
    return result
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(50.0f, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void ReadOnlyByRef_HasInAttribute()
    {
        const string source = """
namespace Test

func readOnly(x: readonly *int) -> int {
    return x
}
""";
        var asm = CompileAndLoad(source);
        var type = asm.GetType("Test.Test")!;
        var method = type.GetMethod("readOnly", AnyStatic)!;
        var param = method.GetParameters()[0];
        Assert.True(param.IsIn, "Parameter should have [In] attribute");
    }

    // ---- `out` parameters ----
    // `out name: T` emits a CLR `[Out] T&` with implicit deref on assignment
    // (`result = v` stores through the byref), matching C# out-parameters — so a
    // C# caller binds it with `out var`.

    [Fact]
    public void OutParam_Int_WritesThrough()
    {
        var asm = CompileAndLoad("""
namespace Test

func try_inc(input: int, out result: int) -> bool {
    result = input + 1
    return true
}
""");
        var args = new object?[] { 41, 0 };
        var ok = Invoke(asm, "Test", "try_inc", args);
        Assert.Equal(true, ok);
        Assert.Equal(42, args[1]);
    }

    [Fact]
    public void OutParam_ReferenceType_WritesThrough()
    {
        // A reference-typed out (string) is a true `string&`, not a value wrapper —
        // the shape RoutePattern's `out IReadOnlyDictionary` relies on.
        var asm = CompileAndLoad("""
namespace Test

func label(n: int, out text: string) -> bool {
    if n == 0 {
        text = "zero"
        return true
    }
    text = "other"
    return false
}
""");
        var args = new object?[] { 0, null };
        Assert.Equal(true, Invoke(asm, "Test", "label", args));
        Assert.Equal("zero", args[1]);

        var args2 = new object?[] { 5, null };
        Assert.Equal(false, Invoke(asm, "Test", "label", args2));
        Assert.Equal("other", args2[1]);
    }

    [Fact]
    public void OutParam_HasByRefAndOutAttribute()
    {
        var asm = CompileAndLoad("""
namespace Test

func emit(out value: int) {
    value = 7
}
""");
        var param = asm.GetType("Test.Test")!.GetMethod("emit", AnyStatic)!.GetParameters()[0];
        Assert.True(param.ParameterType.IsByRef, "out parameter should be by-ref (T&)");
        Assert.True(param.IsOut, "out parameter should carry [Out]");
        Assert.False(param.IsIn, "out parameter must not be [In]");
    }

    [Fact]
    public void OutParam_EsharpCallSite_PassesByRef()
    {
        // E# can also *call* an out-function: `out name` passes an existing local
        // by-ref; `out var name` inline-declares the local for the guarded scope.
        var asm = CompileAndLoad("""
namespace Test

func try_inc(input: int, out result: int) -> bool {
    result = input + 1
    return true
}

func use_existing() -> int {
    var n = 0
    try_inc(41, out n)
    return n
}

func use_declared() -> int {
    if try_inc(99, out var m) {
        return m
    }
    return -1
}
""");
        Assert.Equal(42, Invoke(asm, "Test", "use_existing"));
        Assert.Equal(100, Invoke(asm, "Test", "use_declared"));
    }

    [Fact]
    public void OutParam_TryPattern_BothBranchesAssign()
    {
        var asm = CompileAndLoad("""
namespace Test

func divide(a: int, b: int, out q: int) -> bool {
    if b == 0 {
        q = 0
        return false
    }
    q = a / b
    return true
}
""");
        var ok = new object?[] { 10, 2, 0 };
        Assert.Equal(true, Invoke(asm, "Test", "divide", ok));
        Assert.Equal(5, ok[2]);

        var bad = new object?[] { 10, 0, 99 };
        Assert.Equal(false, Invoke(asm, "Test", "divide", bad));
        Assert.Equal(0, bad[2]);
    }
}
