using System.Reflection;
using Esharp.Compiler.Binding;
using Esharp.Compiler.Parsing;
using Esharp.ILEmit;
using Mono.Cecil.Cil;
using Binder = Esharp.Compiler.Binding.Binder;

namespace Esharp.Tests;

public sealed class ILEmitterTests
{
    static int _asmCounter;

    static Assembly CompileAndLoad(string source)
    {
        var asmName = $"EsharpTest_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        ILEmitter.EmitToFile(bound, asmName, path);
        return Assembly.LoadFrom(path);
    }

    static object? Invoke(Assembly asm, string typeName, string methodName, params object?[] args)
    {
        var type = asm.GetType($"Esharp.Generated.{typeName}")
            ?? throw new Exception($"Type Esharp.Generated.{typeName} not found");
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new Exception($"Method {methodName} not found on {typeName}");
        return method.Invoke(null, args);
    }

    // === Step 1: Short-form opcodes ===

    [Fact]
    public void ShortFormOpcodes_SumTo_ProducesCorrectResult()
    {
        const string source = """
module Test

func sumTo(n: int) -> int {
    var total = 0
    var i = 0
    while i <= n {
        total += i
        i += 1
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(55, Invoke(asm, "Test", "sumTo", 10));
        Assert.Equal(5050, Invoke(asm, "Test", "sumTo", 100));
    }

    [Fact]
    public void ShortFormOpcodes_Abs_BranchesWork()
    {
        const string source = """
module Test

func abs(x: int) -> int {
    if x < 0 {
        return 0 - x
    }
    return x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "abs", -42));
        Assert.Equal(7, Invoke(asm, "Test", "abs", 7));
        Assert.Equal(0, Invoke(asm, "Test", "abs", 0));
    }

    // === Step 2: Tail calls ===

    [Fact]
    public void TailCall_RecursiveFunction_NoStackOverflow()
    {
        const string source = """
module Test

func countdown(n: int) -> int {
    if n <= 0 {
        return 0
    }
    return countdown(n - 1)
}
""";
        var asm = CompileAndLoad(source);
        // With tail call, this should not stack overflow even with deep recursion
        // 100_000 would overflow without tail.call on most stacks
        Assert.Equal(0, Invoke(asm, "Test", "countdown", 100_000));
    }

    [Fact]
    public void TailCall_AccumulatorPattern()
    {
        const string source = """
module Test

func sumTail(n: int, acc: int) -> int {
    if n <= 0 {
        return acc
    }
    return sumTail(n - 1, acc + n)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(5050, Invoke(asm, "Test", "sumTail", 100, 0));
        // Deep recursion — only works with tail call (would stack overflow without it)
        Assert.Equal(50005000, Invoke(asm, "Test", "sumTail", 10_000, 0));
    }

    // === Step 3: Struct operations ===

    [Fact]
    public void Struct_FieldAccess_And_Creation()
    {
        // First param is int (not a data type) so binder won't promote to instance method
        const string source = """
module Test

data Vec2 {
    x: int
    y: int
}

func scale(factor: int, v: Vec2) -> int {
    return v.x * factor + v.y * factor
}
""";
        var asm = CompileAndLoad(source);
        var vec2Type = asm.GetType("Esharp.Generated.Vec2")!;
        var v = Activator.CreateInstance(vec2Type)!;
        vec2Type.GetField("x")!.SetValue(v, 3);
        vec2Type.GetField("y")!.SetValue(v, 4);
        Assert.Equal(35, Invoke(asm, "Test", "scale", 5, v)); // 3*5 + 4*5 = 35
    }

    // === Step 4: Choice types + match via switch ===

    [Fact]
    public void Choice_FactoryMethods_CreateValidStructs()
    {
        const string source = """
module Test

choice Option {
    some(value: int)
    none
}

func makeNone() -> Option {
    return Option.none()
}

func makeSome(v: int) -> Option {
    return Option.some(v)
}
""";
        var asm = CompileAndLoad(source);

        var optionType = asm.GetType("Esharp.Generated.Option")!;
        Assert.True(optionType.IsValueType);

        var tagField = optionType.GetField("Tag")!;
        var valueField = optionType.GetField("some"); // payload field named after case

        // Test factory methods
        var none = Invoke(asm, "Test", "makeNone");
        Assert.NotNull(none);
        Assert.Equal(1, (int)tagField.GetValue(none)!); // none is index 1

        var some = Invoke(asm, "Test", "makeSome", 42);
        Assert.NotNull(some);
        Assert.Equal(0, (int)tagField.GetValue(some)!); // some is index 0
        Assert.Equal(42, (int)valueField!.GetValue(some)!);
    }

    [Fact]
    public void Choice_TagEnum_HasCorrectValues()
    {
        const string source = """
module Test

choice Color {
    red
    green
    blue
}

func makeGreen() -> Color {
    return Color.green()
}
""";
        var asm = CompileAndLoad(source);

        var tagEnumType = asm.GetType("Esharp.Generated.Color_Tag");
        Assert.NotNull(tagEnumType);
        Assert.True(tagEnumType.IsEnum);

        var names = Enum.GetNames(tagEnumType);
        Assert.Equal(["red", "green", "blue"], names);

        var green = Invoke(asm, "Test", "makeGreen");
        var colorType = asm.GetType("Esharp.Generated.Color")!;
        var tag = (int)colorType.GetField("Tag")!.GetValue(green)!;
        Assert.Equal(1, tag); // green = index 1
    }

    // === Verify IL quality: short-form opcodes present ===

    [Fact]
    public void ShortFormOpcodes_VerifyCompactEncoding()
    {
        const string source = """
module Test

func add(a: int, b: int) -> int {
    return a + b
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Binder();
        var bound = binder.Bind(syntax);

        var assembly = ILEmitter.Emit(bound, "TestAsm");
        var moduleClass = assembly.MainModule.Types.First(t => t.Name == "Test");
        var addMethod = moduleClass.Methods.First(m => m.Name == "add");

        // Should use ldarg.0 and ldarg.1 (short forms) not ldarg P
        var opcodes = addMethod.Body.Instructions.Select(i => i.OpCode).ToList();
        Assert.Contains(OpCodes.Ldarg_0, opcodes);
        Assert.Contains(OpCodes.Ldarg_1, opcodes);
        Assert.Contains(OpCodes.Add, opcodes);
        Assert.Contains(OpCodes.Ret, opcodes);
    }

    // === Step 5: Function pointers (ldftn) ===

    [Fact]
    public void FunctionPointer_Ldftn_EmitsCorrectOpcode()
    {
        const string source = """
module Test

func double(x: int) -> int {
    return x * 2
}

func getDoublePtr() -> int {
    let ptr = &double
    return 0
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Binder();
        var bound = binder.Bind(syntax);

        var assembly = ILEmitter.Emit(bound, "FnPtrTest");
        var moduleClass = assembly.MainModule.Types.First(t => t.Name == "Test");
        var method = moduleClass.Methods.First(m => m.Name == "getDoublePtr");

        // Should contain ldftn instruction
        var opcodes = method.Body.Instructions.Select(i => i.OpCode).ToList();
        Assert.Contains(OpCodes.Ldftn, opcodes);
    }

    [Fact]
    public void FunctionPointer_TranspilerEmitsDelegateStar()
    {
        const string source = """
module Test

func double(x: int) -> int {
    return x * 2
}

func getPtr() -> int {
    let ptr = &double
    return 0
}
""";
        var transpiler = new Esharp.Compiler.EsharpTranspiler();
        var result = transpiler.Transpile(source, "test.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        // C# emitter should produce delegate* syntax
        Assert.Contains("delegate*", result.GeneratedCode);
        Assert.Contains("&double", result.GeneratedCode);
    }

    // === Step 1: Entry point (func main) ===

    [Fact]
    public void EntryPoint_ILEmitter_SetsAssemblyEntryPoint()
    {
        const string source = """
module Test

func main() {
    var x = 42
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Binder();
        var bound = binder.Bind(syntax);

        var assembly = ILEmitter.Emit(bound, "EntryPointTest");
        Assert.NotNull(assembly.EntryPoint);
        Assert.Equal("main", assembly.EntryPoint.Name);
        Assert.Equal(Mono.Cecil.ModuleKind.Console, assembly.MainModule.Kind);
    }

    [Fact]
    public void EntryPoint_Transpiler_EmitsMain()
    {
        const string source = """
module Test

func main() {
    var x = 42
}
""";
        var transpiler = new Esharp.Compiler.EsharpTranspiler();
        var result = transpiler.Transpile(source, "test.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("void Main()", result.GeneratedCode);
        Assert.DoesNotContain("void main()", result.GeneratedCode);
    }

    [Fact]
    public void NoEntryPoint_ILEmitter_CreatesDll()
    {
        const string source = """
module Test

func add(a: int, b: int) -> int {
    return a + b
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Binder();
        var bound = binder.Bind(syntax);

        var assembly = ILEmitter.Emit(bound, "NoEntryTest");
        Assert.Null(assembly.EntryPoint);
        Assert.Equal(Mono.Cecil.ModuleKind.Dll, assembly.MainModule.Kind);
    }

    // === Step 2: External method resolution ===

    [Fact]
    public void ExternalCall_ConsoleWriteLine_IL()
    {
        const string source = """
module Test

func main() {
    Console.WriteLine("hello")
}
""";
        var asm = CompileAndLoad(source);
        // Should have a main method that doesn't crash
        var type = asm.GetType("Esharp.Generated.Test");
        Assert.NotNull(type);
        var method = type!.GetMethod("main");
        Assert.NotNull(method);

        // Verify it contains a call (doesn't throw on invoke)
        using var sw = new System.IO.StringWriter();
        var prev = Console.Out;
        Console.SetOut(sw);
        try
        {
            method!.Invoke(null, null);
        }
        finally
        {
            Console.SetOut(prev);
        }
        Assert.Equal("hello", sw.ToString().TrimEnd());
    }

    // === Step 3: await keyword ===

    [Fact]
    public void Await_Transpiler_EmitsAsyncSignature()
    {
        const string source = """
module Test

func fetchData() -> string {
    let result = await Task.FromResult("hello")
    return result
}
""";
        var transpiler = new Esharp.Compiler.EsharpTranspiler();
        var result = transpiler.Transpile(source, "test.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("async", result.GeneratedCode);
        Assert.Contains("ValueTask<string>", result.GeneratedCode);
        Assert.Contains("await", result.GeneratedCode);
    }

    [Fact]
    public void Await_Transpiler_VoidFunction_EmitsValueTask()
    {
        const string source = """
module Test

func doWork() {
    await Task.Delay(1)
}
""";
        var transpiler = new Esharp.Compiler.EsharpTranspiler();
        var result = transpiler.Transpile(source, "test.es");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("async ValueTask doWork()", result.GeneratedCode);
    }

    [Fact]
    public void Await_Parser_ParsesAwaitExpression()
    {
        const string source = """
module Test

func fetch() -> string {
    let x = await someTask()
    return x
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
    }

    [Fact]
    public void Await_Binder_SetsHasAwait()
    {
        const string source = """
module Test

func fetch() -> string {
    let x = await someCall()
    return x
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Binder();
        var bound = binder.Bind(syntax);

        var func = bound.Members.OfType<BoundFunctionDeclaration>().First(f => f.Name == "fetch");
        Assert.True(func.HasAwait);
    }

    [Fact]
    public void NoAwait_Binder_HasAwaitIsFalse()
    {
        const string source = """
module Test

func add(a: int, b: int) -> int {
    return a + b
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Binder();
        var bound = binder.Bind(syntax);

        var func = bound.Members.OfType<BoundFunctionDeclaration>().First(f => f.Name == "add");
        Assert.False(func.HasAwait);
    }

    // === Step 4: Async IL — state machine emission ===

    [Fact]
    public void AsyncIL_EmitsStateMachineStruct()
    {
        const string source = """
module Test

func fetchValue() -> int {
    let result = await Task.FromResult(42)
    return result
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var assembly = ILEmitter.Emit(bound, "AsyncSmTest");
        var moduleClass = assembly.MainModule.Types.First(t => t.Name == "Test");

        // Method should return ValueTask<int> (not int)
        var method = moduleClass.Methods.First(m => m.Name == "fetchValue");
        Assert.Contains("ValueTask", method.ReturnType.FullName);

        // Should have a nested state machine struct
        Assert.NotEmpty(moduleClass.NestedTypes);
        var smType = moduleClass.NestedTypes.First(t => t.Name.Contains("StateMachine"));
        Assert.True(smType.IsValueType);

        // State machine should implement IAsyncStateMachine
        Assert.Contains(smType.Interfaces, i => i.InterfaceType.Name == "IAsyncStateMachine");

        // Should have _state and _builder fields
        Assert.Contains(smType.Fields, f => f.Name == "_state");
        Assert.Contains(smType.Fields, f => f.Name == "_builder");

        // Should have MoveNext method
        Assert.Contains(smType.Methods, m => m.Name == "MoveNext");

        // Should have SetStateMachine method
        Assert.Contains(smType.Methods, m => m.Name == "SetStateMachine");
    }

    [Fact]
    public void AsyncIL_TaskFromResult_ProducesCorrectValue()
    {
        const string source = """
module Test

func getValue() -> int {
    let result = await Task.FromResult(42)
    return result
}
""";
        var asm = CompileAndLoad(source);
        var type = asm.GetType("Esharp.Generated.Test")!;
        var method = type.GetMethod("getValue")!;

        // Method returns ValueTask<int> — await it
        var valueTask = method.Invoke(null, null);
        Assert.NotNull(valueTask);

        // Get the result via reflection (ValueTask<int>.Result)
        var resultProp = valueTask!.GetType().GetProperty("Result");
        Assert.NotNull(resultProp);
        var result = resultProp!.GetValue(valueTask);
        Assert.Equal(42, result);
    }

    [Fact]
    public void AsyncIL_VoidFunction_ReturnsValueTask()
    {
        const string source = """
module Test

func doWork() {
    await Task.Delay(1)
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Binder();
        var bound = binder.Bind(syntax);

        var assembly = ILEmitter.Emit(bound, "AsyncVoidTest");
        var moduleClass = assembly.MainModule.Types.First(t => t.Name == "Test");
        var method = moduleClass.Methods.First(m => m.Name == "doWork");

        // Should return ValueTask (not void)
        Assert.Equal("System.Threading.Tasks.ValueTask", method.ReturnType.FullName);
    }
}
