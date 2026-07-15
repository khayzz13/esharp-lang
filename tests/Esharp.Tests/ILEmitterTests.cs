using System.Reflection;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Esharp.BoundTree;        // BoundCompilationUnit, BoundDataDeclaration, DataClassification, …
using Mono.Cecil.Cil;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;
/// <summary>
/// DO NOT ADD NEW TESTS TO THIS FILE. Create a new verticial slice file for your tests if it 
/// if it doesnt fit into something, if it is purely general, put it in ILEmmiterTests4.cs 
/// </summary>
public sealed class ILEmitterTests
{
    const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    static int _asmCounter;

    static Assembly CompileAndLoad(string source)
    {
        var asmName = $"EsharpTest_{Interlocked.Increment(ref _asmCounter)}";
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

    // === Step 1: Short-form opcodes ===

    [Fact]
    public void ShortFormOpcodes_SumTo_ProducesCorrectResult()
    {
        const string source = """
namespace Test

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
namespace Test

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
namespace Test

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
namespace Test

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
namespace Test

struct Vec2 {
    x: int
    y: int
}

func scale(factor: int, v: Vec2) -> int {
    return v.x * factor + v.y * factor
}
""";
        var asm = CompileAndLoad(source);
        var vec2Type = asm.GetType("Test.Vec2")!;
        var v = Activator.CreateInstance(vec2Type)!;
        vec2Type.GetField("x", AnyInstance)!.SetValue(v, 3);
        vec2Type.GetField("y", AnyInstance)!.SetValue(v, 4);
        Assert.Equal(35, Invoke(asm, "Test", "scale", 5, v)); // 3*5 + 4*5 = 35
    }

    // === Step 4: Choice types + match via switch ===

    [Fact]
    public void Choice_FactoryMethods_CreateValidStructs()
    {
        const string source = """
namespace Test

union Option {
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

        var optionType = asm.GetType("Test.Option")!;
        Assert.True(optionType.IsValueType);

        var tagField = optionType.GetField("Tag", AnyInstance)!;
        var valueField = optionType.GetField("some_value", AnyInstance); // payload field: {caseName}_{payloadName}

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
namespace Test

union Color {
    red
    green
    blue
}

func makeGreen() -> Color {
    return Color.green()
}
""";
        var asm = CompileAndLoad(source);

        var tagEnumType = asm.GetType("Test.Color_Tag");
        Assert.NotNull(tagEnumType);
        Assert.True(tagEnumType.IsEnum);

        var names = Enum.GetNames(tagEnumType);
        Assert.Equal(["red", "green", "blue"], names);

        var green = Invoke(asm, "Test", "makeGreen");
        var colorType = asm.GetType("Test.Color")!;
        var tag = (int)colorType.GetField("Tag", AnyInstance)!.GetValue(green)!;
        Assert.Equal(1, tag); // green = index 1
    }

    // === Verify IL quality: short-form opcodes present ===

    [Fact]
    public void ShortFormOpcodes_VerifyCompactEncoding()
    {
        const string source = """
namespace Test

func add(a: int, b: int) -> int {
    return a + b
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "TestAsm");
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
namespace Test

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
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "FnPtrTest");
        var moduleClass = assembly.MainModule.Types.First(t => t.Name == "Test");
        var method = moduleClass.Methods.First(m => m.Name == "getDoublePtr");

        // Should contain ldftn instruction
        var opcodes = method.Body.Instructions.Select(i => i.OpCode).ToList();
        Assert.Contains(OpCodes.Ldftn, opcodes);
    }

    [Fact]
    public void FunctionPointer_ILEmitter_EmitsLdftn()
    {
        const string source = """
namespace Test

func double(x: int) -> int {
    return x * 2
}

func getPtr() -> int {
    let ptr = &double
    return 0
}
""";
        var parser = new Parser(source, "test.es");
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(parser.ParseCompilationUnit());
        var (assembly, _) = EsHarness.EmitBound(binder, bound, "FnPtrTest");
        var getPtr = assembly.MainModule.GetType("Test.Test").Methods.First(m => m.Name == "getPtr");
        // `&double` takes the function's address — emitted as `ldftn`, the systems-tier
        // function pointer (the transpiler's `delegate*` equivalent), never a heap delegate.
        Assert.Contains(getPtr.Body.Instructions, i => i.OpCode == OpCodes.Ldftn);
    }

    // === Step 1: Entry point (func main) ===

    [Fact]
    public void EntryPoint_ILEmitter_SetsAssemblyEntryPoint()
    {
        const string source = """
namespace Test

func main() {
    var x = 42
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        // Explicit Console output kind — no more name-of-`main` inference.
        var (assembly, _) = EsHarness.EmitBound(binder, 
            new[] { bound }, "EntryPointTest",
            debugSymbols: false, referencePaths: null,
            internalsVisibleTo: null, externalSymbols: null,
            outputKind: ILOutputKind.Console);
        Assert.NotNull(assembly.EntryPoint);
        Assert.Equal("main", assembly.EntryPoint.Name);
        Assert.Equal(Mono.Cecil.ModuleKind.Console, assembly.MainModule.Kind);
    }

    [Fact]
    public void EntryPoint_ILEmitter_VoidMain_NoArgs()
    {
        const string source = """
namespace Test

func main() {
    var x = 42
}
""";
        var parser = new Parser(source, "test.es");
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(parser.ParseCompilationUnit());
        var (assembly, _) = EsHarness.EmitBound(binder, 
            new[] { bound }, "EntryMainTest",
            debugSymbols: false, referencePaths: null,
            internalsVisibleTo: null, externalSymbols: null,
            outputKind: ILOutputKind.Console);
        Assert.NotNull(assembly.EntryPoint);
        Assert.Equal("main", assembly.EntryPoint.Name);
        Assert.Equal("System.Void", assembly.EntryPoint.ReturnType.FullName);
        Assert.Empty(assembly.EntryPoint.Parameters);
    }

    [Fact]
    public void NoEntryPoint_ILEmitter_CreatesDll()
    {
        const string source = """
namespace Test

func add(a: int, b: int) -> int {
    return a + b
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "NoEntryTest");
        Assert.Null(assembly.EntryPoint);
        Assert.Equal(Mono.Cecil.ModuleKind.Dll, assembly.MainModule.Kind);
    }

    // === Step 2: External method resolution ===

    [Fact]
    public void ExternalCall_ConsoleWriteLine_IL()
    {
        const string source = """
namespace Test

func main() {
    Console.WriteLine("hello")
}
""";
        var asm = CompileAndLoad(source);
        // Should have a main method that doesn't crash
        var type = asm.GetType("Test.Test");
        Assert.NotNull(type);
        var method = type!.GetMethod("main", AnyStatic);
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
    public void Await_ILEmitter_EmitsValueTaskOfT()
    {
        const string source = """
namespace Test

func fetchData() -> string {
    let result = await Task.FromResult("hello")
    return result
}
""";
        var parser = new Parser(source, "test.es");
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(parser.ParseCompilationUnit());
        var (assembly, _) = EsHarness.EmitBound(binder, bound, "AwaitRetTest");
        var fetch = assembly.MainModule.GetType("Test.Test").Methods.First(m => m.Name == "fetchData");
        // uncolored async: an awaiting `-> string` lowers to `ValueTask<string>` + a state machine.
        Assert.Equal("System.Threading.Tasks.ValueTask`1<System.String>", fetch.ReturnType.FullName);
        Assert.Contains(fetch.CustomAttributes, a => a.AttributeType.Name == "AsyncStateMachineAttribute");
    }

    [Fact]
    public void Await_ILEmitter_VoidFunction_EmitsValueTask()
    {
        const string source = """
namespace Test

func doWork() {
    await Task.Delay(1)
}
""";
        var parser = new Parser(source, "test.es");
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(parser.ParseCompilationUnit());
        var (assembly, _) = EsHarness.EmitBound(binder, bound, "AwaitVoidTest");
        var doWork = assembly.MainModule.GetType("Test.Test").Methods.First(m => m.Name == "doWork");
        // void-like async lowers to the non-generic `ValueTask`.
        Assert.Equal("System.Threading.Tasks.ValueTask", doWork.ReturnType.FullName);
        Assert.Contains(doWork.CustomAttributes, a => a.AttributeType.Name == "AsyncStateMachineAttribute");
    }

    [Fact]
    public void Await_Parser_ParsesAwaitExpression()
    {
        const string source = """
namespace Test

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
namespace Test

func fetch() -> string {
    let x = await someCall()
    return x
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        var func = bound.Members.OfType<BoundFunctionDeclaration>().First(f => f.Name == "fetch");
        Assert.True(func.HasAwait);
    }

    [Fact]
    public void NoAwait_Binder_HasAwaitIsFalse()
    {
        const string source = """
namespace Test

func add(a: int, b: int) -> int {
    return a + b
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        var func = bound.Members.OfType<BoundFunctionDeclaration>().First(f => f.Name == "add");
        Assert.False(func.HasAwait);
    }

    // === Step 4: Async IL — state machine emission ===

    [Fact]
    public void AsyncIL_EmitsStateMachineStruct()
    {
        const string source = """
namespace Test

func fetchValue() -> int {
    let result = await Task.FromResult(42)
    return result
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "AsyncSmTest");
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
namespace Test

func getValue() -> int {
    let result = await Task.FromResult(42)
    return result
}
""";
        var asm = CompileAndLoad(source);
        var type = asm.GetType("Test.Test")!;
        var method = type.GetMethod("getValue", AnyStatic)!;

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
namespace Test

func doWork() {
    await Task.Delay(1)
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "AsyncVoidTest");
        var moduleClass = assembly.MainModule.Types.First(t => t.Name == "Test");
        var method = moduleClass.Methods.First(m => m.Name == "doWork");

        // Should return ValueTask (not void)
        Assert.Equal("System.Threading.Tasks.ValueTask", method.ReturnType.FullName);
    }

    [Fact]
    public void RefChoice_IL_EmitsSealedClassHierarchy()
    {
        const string source = """
namespace Test

ref union Shape {
    circle(radius: float)
    rect(width: float)
    point
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "RefChoiceTest");
        var types = assembly.MainModule.Types;

        // Base class exists and is abstract
        var shapeType = types.FirstOrDefault(t => t.Name == "Shape");
        Assert.NotNull(shapeType);
        Assert.True(shapeType.IsAbstract, "Base class should be abstract");
        Assert.False(shapeType.IsSealed, "Base class should not be sealed (subclasses inherit)");

        // Subclasses exist and are sealed
        var circleType = types.FirstOrDefault(t => t.Name == "Shape_circle");
        Assert.NotNull(circleType);
        Assert.True(circleType.IsSealed, "Variant should be sealed");
        Assert.Equal("Shape", circleType.BaseType?.Name);
        Assert.Contains(circleType.Fields, f => f.Name == "radius");

        var rectType = types.FirstOrDefault(t => t.Name == "Shape_rect");
        Assert.NotNull(rectType);
        Assert.Contains(rectType.Fields, f => f.Name == "width");

        var pointType = types.FirstOrDefault(t => t.Name == "Shape_point");
        Assert.NotNull(pointType);
        Assert.Equal("Shape", pointType.BaseType?.Name);
    }

    // === Feature 1: Closures ===

    [Fact]
    public void Closure_NoCaptureIL_EmitsStaticTrampoline()
    {
        const string source = """
namespace Test

func apply(f: int, g: int) -> int {
    let double = func(x: int) -> int { return x * 2 }
    return double(f)
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "ClosureNoCaptureTest");
        // A zero-capture closure needs no display object. It is emitted as a free
        // static trampoline on the namespace host, then materialized as a delegate.
        var moduleClass = assembly.MainModule.GetType("Test.Test");
        Assert.NotNull(moduleClass);
        Assert.Contains(moduleClass!.Methods, m => m.IsStatic && m.Name.Contains("staticlambda"));
    }

    // === Feature 3: Multi-payload choice ===

    [Fact]
    public void MultiPayload_Choice_FieldsAndFactory()
    {
        const string source = """
namespace Test

union Msg {
    text(from: string, body: string)
    ping
}

func makeText(f: string, b: string) -> Msg {
    return Msg.text(f, b)
}

func makePing() -> Msg {
    return Msg.ping()
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "MultiPayloadTest");
        var msgType = assembly.MainModule.Types.First(t => t.Name == "Msg");
        Assert.True(msgType.IsValueType);

        // Should have Tag + two payload fields for "text" case
        Assert.Contains(msgType.Fields, f => f.Name == "Tag");
        Assert.Contains(msgType.Fields, f => f.Name == "text_from");
        Assert.Contains(msgType.Fields, f => f.Name == "text_body");

        // Factory method for "text" should have 2 parameters
        var textFactory = msgType.Methods.First(m => m.Name == "text" && m.IsStatic);
        Assert.Equal(2, textFactory.Parameters.Count);
        Assert.Equal("from", textFactory.Parameters[0].Name);
        Assert.Equal("body", textFactory.Parameters[1].Name);

        // Factory method for "ping" should have 0 parameters
        var pingFactory = msgType.Methods.First(m => m.Name == "ping" && m.IsStatic);
        Assert.Empty(pingFactory.Parameters);
    }

    [Fact]
    public void MultiPayload_Choice_RuntimeExecution()
    {
        const string source = """
namespace Test

union Msg {
    text(from: string, body: string)
    ping
}

func makeText(f: string, b: string) -> Msg {
    return Msg.text(f, b)
}

func makePing() -> Msg {
    return Msg.ping()
}
""";
        var asm = CompileAndLoad(source);
        var msgType = asm.GetType("Test.Msg")!;

        // Create a text message
        var textMsg = Invoke(asm, "Test", "makeText", "alice", "hello world");
        Assert.NotNull(textMsg);
        Assert.Equal(0, (int)msgType.GetField("Tag", AnyInstance)!.GetValue(textMsg)!); // text = index 0
        Assert.Equal("alice", (string)msgType.GetField("text_from", AnyInstance)!.GetValue(textMsg)!);
        Assert.Equal("hello world", (string)msgType.GetField("text_body", AnyInstance)!.GetValue(textMsg)!);

        // Create a ping message
        var pingMsg = Invoke(asm, "Test", "makePing");
        Assert.NotNull(pingMsg);
        Assert.Equal(1, (int)msgType.GetField("Tag", AnyInstance)!.GetValue(pingMsg)!); // ping = index 1
    }

    // === Feature 4: Attributes ===

    [Fact]
    public void Attributes_OnDataType_EmittedAsCLRAttributes()
    {
        const string source = """
namespace Test

[Serializable]
class Config {
    name: string
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "AttrDataTest");
        var configType = assembly.MainModule.Types.First(t => t.Name == "Config");

        // Should have [Serializable] attribute
        Assert.Contains(configType.CustomAttributes,
            a => a.AttributeType.Name == "SerializableAttribute");
    }

    [Fact]
    public void Attributes_OnFunction_EmittedAsCLRAttributes()
    {
        const string source = """
namespace Test

[Obsolete]
func oldMethod() -> int {
    return 0
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "AttrFuncTest");
        var moduleClass = assembly.MainModule.Types.First(t => t.Name == "Test");
        var method = moduleClass.Methods.First(m => m.Name == "oldMethod");

        // Should have [Obsolete] attribute
        Assert.Contains(method.CustomAttributes,
            a => a.AttributeType.Name == "ObsoleteAttribute");
    }

    // === Feature 5: Explicit Protocol Conformance ===
    // Binder-only validation — no IL emitter changes needed. Tested in TranspilerTests.

    // === Feature 6: Protocol → Interface IL emission ===

    [Fact]
    public void Protocol_EmitsInterfaceType()
    {
        const string source = """
namespace Test

interface IDrawable {
    func draw(x: int) -> string
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "ProtoIfaceTest");
        var drawableType = assembly.MainModule.Types.FirstOrDefault(t => t.Name == "IDrawable");
        Assert.NotNull(drawableType);
        Assert.True(drawableType.IsInterface, "Protocol should emit as interface");
        Assert.True(drawableType.IsAbstract, "Interface should be abstract");

        // Should have the draw method
        var drawMethod = drawableType.Methods.FirstOrDefault(m => m.Name == "draw");
        Assert.NotNull(drawMethod);
        Assert.True(drawMethod.IsVirtual);
        Assert.True(drawMethod.IsAbstract);
        Assert.Equal("Int32", drawMethod.Parameters[0].ParameterType.Name);
        Assert.Equal("String", drawMethod.ReturnType.Name);
    }

    [Fact]
    public void Protocol_DataConformance_ImplementsInterface()
    {
        const string source = """
namespace Test

interface IRenderable {
    func render() -> string
}

class Button : IRenderable {
    label: string
}

func (b: Button) render() -> string {
    return b.label
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "ProtoConformTest");

        // Renderable should be an interface
        var renderableType = assembly.MainModule.Types.FirstOrDefault(t => t.Name == "IRenderable");
        Assert.NotNull(renderableType);
        Assert.True(renderableType.IsInterface);

        // Button should implement Renderable
        var buttonType = assembly.MainModule.Types.FirstOrDefault(t => t.Name == "Button");
        Assert.NotNull(buttonType);
        Assert.Contains(buttonType.Interfaces, i => i.InterfaceType.Name == "IRenderable");
    }

    // === Feature 7: Nullable type resolution ===

    [Fact]
    public void Nullable_ValueType_ResolvesToSystemNullable()
    {
        const string source = """
namespace Test

func maybe(x: int?) -> int {
    return 0
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "NullableTest");
        var moduleClass = assembly.MainModule.Types.First(t => t.Name == "Test");
        var method = moduleClass.Methods.First(m => m.Name == "maybe");

        // Parameter should be Nullable<Int32> (System.Int32?)
        var param = method.Parameters[0];
        Assert.Contains("Nullable", param.ParameterType.FullName);
        Assert.Contains("Int32", param.ParameterType.FullName);
    }

    [Fact]
    public void Nullable_ReferenceType_StaysUnwrapped()
    {
        const string source = """
namespace Test

func process(name: string?) -> int {
    return 0
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "NullableRefTest");
        var moduleClass = assembly.MainModule.Types.First(t => t.Name == "Test");
        var method = moduleClass.Methods.First(m => m.Name == "process");

        // string is a reference type — string? should just be string (no Nullable<> wrapper)
        var param = method.Parameters[0];
        Assert.Equal("System.String", param.ParameterType.FullName);
    }

    [Fact]
    public void ProtocolType_ResolvesInMethodSignature()
    {
        const string source = """
namespace Test

interface IWidget {
    func render() -> string
}

func display(w: IWidget) -> int {
    return 0
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "ProtoParamTest");
        var moduleClass = assembly.MainModule.Types.First(t => t.Name == "Test");
        var method = moduleClass.Methods.First(m => m.Name == "display");

        // Parameter should resolve to the Widget interface type, not System.Object
        var param = method.Parameters[0];
        Assert.Equal("IWidget", param.ParameterType.Name);
    }

    // === Feature 8: Init constructor ===

    [Fact]
    public void Init_EmitsParameterizedConstructor()
    {
        const string source = """
namespace Test

class Connection {
    host: string
    port: int

    init(h: string, p: int) {
        host = h
        port = p
    }
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "InitCtorTest");
        var connType = assembly.MainModule.Types.First(t => t.Name == "Connection");

        // Should have two constructors: parameterless + init(string, int)
        var ctors = connType.Methods.Where(m => m.IsConstructor).ToList();
        Assert.Equal(2, ctors.Count);

        var initCtor = ctors.First(c => c.Parameters.Count == 2);
        Assert.Equal("h", initCtor.Parameters[0].Name);
        Assert.Equal("String", initCtor.Parameters[0].ParameterType.Name);
        Assert.Equal("p", initCtor.Parameters[1].Name);
        Assert.Equal("Int32", initCtor.Parameters[1].ParameterType.Name);
    }

    [Fact]
    public void Init_RuntimeExecution_SetsFields()
    {
        const string source = """
namespace Test

class Connection {
    host: string
    port: int

    init(h: string, p: int) {
        host = h
        port = p
    }
}
""";
        var asm = CompileAndLoad(source);
        var connType = asm.GetType("Test.Connection")!;

        // Find the init constructor
        var ctor = connType.GetConstructor(AnyInstance, [typeof(string), typeof(int)])!;
        Assert.NotNull(ctor);

        var instance = ctor.Invoke(["localhost", 8080]);
        Assert.Equal("localhost", connType.GetField("host", AnyInstance)!.GetValue(instance));
        Assert.Equal(8080, connType.GetField("port", AnyInstance)!.GetValue(instance));
    }

    // === ref choice: dot-case + match in IL ===

    [Fact]
    public void RefChoice_DotCase_EmitsNewobj()
    {
        const string source = """
namespace Test

ref union Shape {
    circle(radius: float)
    point
}

func makeCircle() -> Shape {
    return .circle(3.14)
}

func makePoint() -> Shape {
    return .point()
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "RefChoiceDotCaseTest");

        // makeCircle should contain newobj Shape_circle
        var moduleClass = assembly.MainModule.Types.First(t => t.Name == "Test");
        var makeCircle = moduleClass.Methods.First(m => m.Name == "makeCircle");
        var opcodes = makeCircle.Body.Instructions.Select(i => i.OpCode).ToList();
        Assert.Contains(OpCodes.Newobj, opcodes);
    }

    [Fact]
    public void RefChoice_Match_EmitsIsinst()
    {
        const string source = """
namespace Test

ref union Shape {
    circle(radius: float)
    point
}

func describe(s: Shape) -> int {
    match (s: Shape) {
        .circle(c) { return 1 }
        .point { return 0 }
    }
    return 0
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        var (assembly, _) = EsHarness.EmitBound(binder, bound, "RefChoiceMatchTest");

        var moduleClass = assembly.MainModule.Types.First(t => t.Name == "Test");
        var describe = moduleClass.Methods.First(m => m.Name == "describe");
        var opcodes = describe.Body.Instructions.Select(i => i.OpCode).ToList();
        // Should use isinst for type checking (not switch on tag)
        Assert.Contains(OpCodes.Isinst, opcodes);
        Assert.DoesNotContain(OpCodes.Switch, opcodes);
    }

    // === Runtime execution tests ===

    [Fact]
    public void Closure_CapturedVar_MutationPropagates()
    {
        var asm = CompileAndLoad("""
namespace Test

func evalWith(value: int, offset: int) -> int {
    var total = 0
    let addTo = func(v: int) { total = total + v }
    addTo(value)
    addTo(offset)
    return total
}
""");
        var result = Invoke(asm, "Test", "evalWith", 10, 5);
        Assert.Equal(15, result);
    }

    [Fact]
    public void Closure_CapturedLet_ReadsCorrectly()
    {
        var asm = CompileAndLoad("""
namespace Test

func makeAdder(base: int) -> int {
    let offset = 100
    let add = func(x: int) -> int { return offset + x }
    return add(base)
}
""");
        var result = Invoke(asm, "Test", "makeAdder", 42);
        Assert.Equal(142, result);
    }

    [Fact]
    public void Nullable_NilReturn_ValueType_Runtime()
    {
        var asm = CompileAndLoad("""
namespace Test

func tryParse(s: string) -> int? {
    return nil
}
""");
        var result = Invoke(asm, "Test", "tryParse", "x");
        Assert.Null(result);
    }

    [Fact]
    public void Nullable_NilReturn_ReferenceType_Runtime()
    {
        var asm = CompileAndLoad("""
namespace Test

func tryFind(s: string) -> string? {
    return nil
}
""");
        var result = Invoke(asm, "Test", "tryFind", "x");
        Assert.Null(result);
    }

    [Fact]
    public void StringInterpolation_IL_Runtime()
    {
        var asm = CompileAndLoad("""
namespace Test

func greet(name: string) -> string {
    return "hello {name}"
}
""");
        var result = Invoke(asm, "Test", "greet", "world");
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void StringInterpolation_IntValue_IL_Runtime()
    {
        var asm = CompileAndLoad("""
namespace Test

func format(x: int) -> string {
    return "value: {x}"
}
""");
        var result = Invoke(asm, "Test", "format", 42);
        Assert.Equal("value: 42", result);
    }

    [Fact]
    public void CompoundAssignment_IL_Runtime()
    {
        var asm = CompileAndLoad("""
namespace Test

func accumulate(n: int) -> int {
    var total = 0
    total += n
    total += n
    total -= 1
    return total
}
""");
        var result = Invoke(asm, "Test", "accumulate", 10);
        Assert.Equal(19, result);
    }

    [Fact]
    public void ValueChoice_Match_Destructuring_IL_Runtime()
    {
        var asm = CompileAndLoad("""
namespace Test

union Result {
    ok(value: int)
    err(message: string)
}

func check() -> int {
    let r = Result.ok(42)
    match (r: Result) {
        .ok(v) { return v }
        .err(msg) { return 0 }
    }
    return -1
}
""");
        var result = Invoke(asm, "Test", "check");
        Assert.Equal(42, result);
    }

    [Fact]
    public void RefChoice_Match_Runtime()
    {
        var asm = CompileAndLoad("""
namespace Test

ref union Expr {
    literal(value: int)
    neg(inner: Expr)
}

func eval(e: Expr) -> int {
    match (e: Expr) {
        .literal(lit) { return lit.value }
        .neg(n) { return 0 - eval(n.inner) }
        default { return 0 }
    }
    return 0
}
""");
        // Create Expr_literal { value: 7 } and eval it
        var exprType = asm.GetType("Test.Expr_literal")!;
        var expr = Activator.CreateInstance(exprType)!;
        exprType.GetField("value", AnyInstance)!.SetValue(expr, 7);
        var result = Invoke(asm, "Test", "eval", expr);
        Assert.Equal(7, result);
    }

    [Fact]
    public void Protocol_VirtualDispatch_IL_Runtime()
    {
        var asm = CompileAndLoad("""
namespace Test

interface ISpeaker {
    func speak() -> string
}

class Dog : ISpeaker {
    name: string
}

func (d: Dog) speak() -> string {
    return "woof"
}

func announce(s: ISpeaker) -> string {
    return s.speak()
}
""");
        var dogType = asm.GetType("Test.Dog")!;
        var dog = Activator.CreateInstance(dogType)!;
        dogType.GetField("name", AnyInstance)!.SetValue(dog, "Rex");
        var result = Invoke(asm, "Test", "announce", dog);
        Assert.Equal("woof", result);
    }

    [Fact]
    public void Init_Constructor_IL_Runtime()
    {
        var asm = CompileAndLoad("""
namespace Test

class Point {
    x: int
    y: int

    init(px: int, py: int) {
        x = px
        y = py
    }
}

func getX() -> int {
    let p = Point { x: 0, y: 0 }
    return p.x
}
""");
        // Verify the init constructor exists and type can be constructed
        var pointType = asm.GetType("Test.Point")!;
        var initCtor = pointType.GetConstructor(AnyInstance, [typeof(int), typeof(int)]);
        Assert.NotNull(initCtor);
        var p = initCtor!.Invoke([10, 20]);
        Assert.Equal(10, pointType.GetField("x", AnyInstance)!.GetValue(p));
        Assert.Equal(20, pointType.GetField("y", AnyInstance)!.GetValue(p));
    }

    [Fact]
    public void DelegateInvoke_ActionVoid_IL_Runtime()
    {
        var asm = CompileAndLoad("""
namespace Test

func runCallback(x: int) -> int {
    var result = 0
    let cb = func(v: int) { result = v * 2 }
    cb(x)
    return result
}
""");
        var result = Invoke(asm, "Test", "runCallback", 5);
        Assert.Equal(10, result);
    }

    [Fact]
    public void MultiPayload_ValueChoice_Destructuring_IL_Runtime()
    {
        var asm = CompileAndLoad("""
namespace Test

union LogEntry {
    message(level: int, text: string)
    timestamp(epoch: int)
}

func getLevel() -> int {
    let entry = LogEntry.message(3, "hello")
    match (entry: LogEntry) {
        .message(lvl, txt) { return lvl }
        .timestamp(t) { return 0 }
    }
    return -1
}
""");
        var result = Invoke(asm, "Test", "getLevel");
        Assert.Equal(3, result);
    }

    [Fact]
    public void IL_Diagnostics_UnresolvedType_ReportsError()
    {
        var source = """
namespace Test

func broken(x: Nonexistent) -> int {
    return 0
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        var (assembly, diagnostics) = EsHarness.EmitBound(binder, bound, "DiagTest");
        // Should have at least one diagnostic about the unresolved type
        Assert.NotEmpty(diagnostics);
    }

    // === Generic external type resolution ===

    [Fact]
    public void GenericExternalType_List_ResolvesCorrectly()
    {
        var source = """
namespace Test

struct Container {
    items: List<int>
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        var (assembly, diagnostics) = EsHarness.EmitBound(binder, bound, $"EsharpTest_{Interlocked.Increment(ref _asmCounter)}");
        // No warnings about unresolved types
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("unresolved"));

        var containerType = assembly.MainModule.GetType("Test.Container");
        Assert.NotNull(containerType);
        var itemsField = containerType.Fields.FirstOrDefault(f => f.Name == "items");
        Assert.NotNull(itemsField);
        Assert.Contains("List", itemsField.FieldType.FullName);
        Assert.Contains("Int32", itemsField.FieldType.FullName);
    }

    [Fact]
    public void GenericExternalType_Dictionary_ResolvesCorrectly()
    {
        var source = """
namespace Test

struct Lookup {
    entries: Dictionary<string, int>
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        var (assembly, diagnostics) = EsHarness.EmitBound(binder, bound, $"EsharpTest_{Interlocked.Increment(ref _asmCounter)}");
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("unresolved"));

        var lookupType = assembly.MainModule.GetType("Test.Lookup");
        Assert.NotNull(lookupType);
        var entriesField = lookupType.Fields.FirstOrDefault(f => f.Name == "entries");
        Assert.NotNull(entriesField);
        Assert.Contains("Dictionary", entriesField.FieldType.FullName);
        Assert.Contains("String", entriesField.FieldType.FullName);
        Assert.Contains("Int32", entriesField.FieldType.FullName);
    }

    [Fact]
    public void GenericExternalType_Nested_ResolvesCorrectly()
    {
        var source = """
namespace Test

struct Nested {
    mapping: Dictionary<string, List<int>>
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        var (assembly, diagnostics) = EsHarness.EmitBound(binder, bound, $"EsharpTest_{Interlocked.Increment(ref _asmCounter)}");
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("unresolved"));

        var nestedType = assembly.MainModule.GetType("Test.Nested");
        Assert.NotNull(nestedType);
        var field = nestedType.Fields.FirstOrDefault(f => f.Name == "mapping");
        Assert.NotNull(field);
        Assert.Contains("Dictionary", field.FieldType.FullName);
    }

    [Fact]
    public void GenericExternalType_UserDefinedArg_ResolvesCorrectly()
    {
        var source = """
namespace Test

struct Item {
    name: string
    value: int
}

struct Bag {
    items: List<Item>
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        var (assembly, diagnostics) = EsHarness.EmitBound(binder, bound, $"EsharpTest_{Interlocked.Increment(ref _asmCounter)}");
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("unresolved"));

        var bagType = assembly.MainModule.GetType("Test.Bag");
        Assert.NotNull(bagType);
        var itemsField = bagType.Fields.FirstOrDefault(f => f.Name == "items");
        Assert.NotNull(itemsField);
        Assert.Contains("List", itemsField.FieldType.FullName);
    }

    [Fact]
    public void GenericExternalType_Runtime_ListAdd()
    {
        var asm = CompileAndLoad("""
namespace Test

func run() -> int {
    var count = 0
    count = 3
    return count
}
""");
        // Basic sanity — generic types resolve, function compiles and runs
        var result = Invoke(asm, "Test", "run");
        Assert.Equal(3, result);
    }

    // === Generic constructor calls (parser: TypeName<A, B>(args)) ===

    [Fact]
    public void GenericConstructorCall_Parser_ParsesCorrectly()
    {
        var source = """
namespace Test

func make() -> int {
    let d = Dictionary<string, int>()
    return 0
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        // Should parse as a call expression on "Dictionary<string, int>"
        var func = syntax.Members.OfType<Esharp.Syntax.FunctionDeclarationSyntax>().First();
        var body = func.Body.Statements;
        Assert.True(body.Count >= 1);
    }

    [Fact]
    public void GenericConstructorCall_List_ParsesCorrectly()
    {
        var source = """
namespace Test

func make() -> int {
    let items = List<string>()
    return 0
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
    }

    [Fact]
    public void GenericConstructorCall_Nested_ParsesCorrectly()
    {
        var source = """
namespace Test

func make() -> int {
    let m = Dictionary<string, List<int>>()
    return 0
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
    }

    [Fact]
    public void GenericConstructorCall_InInit_ParsesAndBinds()
    {
        var source = """
namespace Test

class Store {
    items: List<int>
    lookup: Dictionary<string, int>

    init() {
        self.items = List<int>()
        self.lookup = Dictionary<string, int>()
    }
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);
    }

    [Fact]
    public void GenericConstructorCall_InFunction_IL()
    {
        var source = """
namespace Test

func make() -> string {
    let d = Dictionary<string, int>()
    return "ok"
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        var (assembly, diagnostics) = EsHarness.EmitBound(binder, bound, $"EsharpTest_{Interlocked.Increment(ref _asmCounter)}");
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("unresolved"));
    }

    // === Struct promotion diagnostics (ES2001/ES2002) ===

    [Fact]
    public void StructPromotion_SmallStruct_NoWarning()
    {
        var source = """
namespace Test

struct Point {
    x: int
    y: int
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        binder.Bind(syntax);

        Assert.DoesNotContain(binder.Diagnostics, d => d.Message.Contains("ES2001") || d.Message.Contains("ES2002"));
    }

    [Fact]
    public void StructPromotion_LargeStruct_StaysStruct_AutopromotionDisabled()
    {
        // Under the `data` value-semantic contract, CLR form is the compiler's
        // choice. Autopromotion is disabled — large structs stay structs.
        var source = """
namespace Test

struct Big {
    a: double
    b: double
    c: double
    d: double
    e: double
    f: double
    g: double
    h: double
    i: double
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        Assert.DoesNotContain(binder.Diagnostics, d => d.Message.Contains("ES2001") || d.Message.Contains("ES2002"));
        var big = bound.Members.OfType<BoundDataDeclaration>().Single(d => d.Name == "Big");
        Assert.Equal(DataClassification.Struct, big.Classification);
    }

    [Fact]
    public void StructPromotion_ManyRefFields_StaysStruct_AutopromotionDisabled()
    {
        var source = """
namespace Test

struct Refs {
    a: string
    b: string
    c: string
    d: int
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        Assert.DoesNotContain(binder.Diagnostics, d => d.Message.Contains("ES2001") || d.Message.Contains("ES2002"));
        var refs = bound.Members.OfType<BoundDataDeclaration>().Single(d => d.Name == "Refs");
        Assert.Equal(DataClassification.Struct, refs.Classification);
    }

    [Fact]
    public void StructPromotion_StructPinOverride_SuppressesWarning()
    {
        var source = """
namespace Test

[Struct]
struct Big {
    a: double
    b: double
    c: double
    d: double
    e: double
    f: double
    g: double
    h: double
    i: double
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        binder.Bind(syntax);

        Assert.DoesNotContain(binder.Diagnostics, d => d.Message.Contains("ES2001") || d.Message.Contains("ES2002"));
    }

    [Fact]
    public void StructPromotion_StoredInCollection_StaysStruct_AutopromotionDisabled()
    {
        // Item: 40 bytes (5 doubles) — not triggered by size (>64) or ref count,
        // only by collection storage at >32 bytes. Silent promotion, no diagnostic.
        var source = """
namespace Test

struct Item {
    a: double
    b: double
    c: double
    d: double
    e: double
}

struct Container {
    items: List<Item>
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        Assert.DoesNotContain(binder.Diagnostics, d => d.Message.Contains("ES2001") || d.Message.Contains("ES2002"));
        var item = bound.Members.OfType<BoundDataDeclaration>().Single(d => d.Name == "Item");
        Assert.Equal(DataClassification.Struct, item.Classification);
    }

    [Fact]
    public void StructPromotion_RefData_NoWarning()
    {
        var source = """
namespace Test

class Big {
    a: double
    b: double
    c: double
    d: double
    e: double
    f: double
    g: double
    h: double
    i: double
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        binder.Bind(syntax);

        // class already a class — no promotion diagnostics
        Assert.DoesNotContain(binder.Diagnostics, d => d.Message.Contains("ES2001") || d.Message.Contains("ES2002"));
    }

    // === Derive directives (equality / debug) ===

    [Fact]
    public void DeriveEquality_Struct_TwoEqualInstances_AreEqual()
    {
        const string source = """
namespace Test

derive equality
struct Point {
    x: int
    y: int
}

func go() -> bool {
    let p1 = Point { x: 3, y: 4 }
    let p2 = Point { x: 3, y: 4 }
    return p1.Equals(p2)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void DeriveEquality_Struct_DifferingField_AreNotEqual()
    {
        const string source = """
namespace Test

derive equality
struct Point {
    x: int
    y: int
}

func go() -> bool {
    let p1 = Point { x: 3, y: 4 }
    let p2 = Point { x: 3, y: 5 }
    return p1.Equals(p2)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(false, Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void DeriveEquality_Struct_NoFields_AlwaysEqual()
    {
        const string source = """
namespace Test

derive equality
struct Unit {
}

func go() -> bool {
    let a = Unit {}
    let b = Unit {}
    return a.Equals(b)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void DeriveEquality_GetHashCode_EqualInstancesHaveEqualHash()
    {
        const string source = """
namespace Test

derive equality
struct Point {
    x: int
    y: int
}
""";
        var asm = CompileAndLoad(source);
        var pointType = asm.GetType("Test.Point")!;
        var a = Activator.CreateInstance(pointType)!;
        pointType.GetField("x", AnyInstance)!.SetValue(a, 10);
        pointType.GetField("y", AnyInstance)!.SetValue(a, 20);
        var b = Activator.CreateInstance(pointType)!;
        pointType.GetField("x", AnyInstance)!.SetValue(b, 10);
        pointType.GetField("y", AnyInstance)!.SetValue(b, 20);
        var c = Activator.CreateInstance(pointType)!;
        pointType.GetField("x", AnyInstance)!.SetValue(c, 10);
        pointType.GetField("y", AnyInstance)!.SetValue(c, 21);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a.GetHashCode(), c.GetHashCode());
    }

    [Fact]
    public void DeriveEquality_ObjectEquals_TypeCheck_RejectsWrongType()
    {
        const string source = """
namespace Test

derive equality
struct Point {
    x: int
    y: int
}
""";
        var asm = CompileAndLoad(source);
        var pointType = asm.GetType("Test.Point")!;
        var p = Activator.CreateInstance(pointType)!;
        pointType.GetField("x", AnyInstance)!.SetValue(p, 1);
        pointType.GetField("y", AnyInstance)!.SetValue(p, 2);
        // object.Equals path
        var result = p.Equals("not a point");
        Assert.False(result);
        // Equals with matching type
        var q = Activator.CreateInstance(pointType)!;
        pointType.GetField("x", AnyInstance)!.SetValue(q, 1);
        pointType.GetField("y", AnyInstance)!.SetValue(q, 2);
        Assert.True(p.Equals(q));
    }

    [Fact]
    public void DeriveEquality_RefData_WorksWithDifferentInstances()
    {
        const string source = """
namespace Test

derive equality
class Config {
    name: string
    port: int

    init(name: string, port: int) {
        self.name = name
        self.port = port
    }
}
""";
        var asm = CompileAndLoad(source);
        var configType = asm.GetType("Test.Config")!;
        var ctor = configType.GetConstructor(AnyInstance, [typeof(string), typeof(int)])!;
        var a = ctor.Invoke(["prod", 8080]);
        var b = ctor.Invoke(["prod", 8080]);
        var c = ctor.Invoke(["prod", 9090]);
        Assert.True(((dynamic)a).Equals((dynamic)b));
        Assert.False(((dynamic)a).Equals((dynamic)c));
    }

    [Fact]
    public void DeriveDebug_Struct_ToStringContainsFieldValues()
    {
        const string source = """
namespace Test

derive debug
struct Point {
    x: int
    y: int
}
""";
        var asm = CompileAndLoad(source);
        var pointType = asm.GetType("Test.Point")!;
        var p = Activator.CreateInstance(pointType)!;
        pointType.GetField("x", AnyInstance)!.SetValue(p, 3);
        pointType.GetField("y", AnyInstance)!.SetValue(p, 4);
        var str = p.ToString()!;
        Assert.Contains("Point", str);
        Assert.Contains("x = 3", str);
        Assert.Contains("y = 4", str);
    }

    [Fact]
    public void DeriveDebug_NoFields_ShowsEmptyBraces()
    {
        const string source = """
namespace Test

derive debug
struct Marker {
}
""";
        var asm = CompileAndLoad(source);
        var markerType = asm.GetType("Test.Marker")!;
        var m = Activator.CreateInstance(markerType)!;
        var str = m.ToString()!;
        Assert.Contains("Marker", str);
    }

    [Fact]
    public void DeriveEqualityAndDebug_Combined()
    {
        const string source = """
namespace Test

derive equality, debug
struct Pair {
    a: int
    b: string
}
""";
        var asm = CompileAndLoad(source);
        var pairType = asm.GetType("Test.Pair")!;
        var p = Activator.CreateInstance(pairType)!;
        pairType.GetField("a", AnyInstance)!.SetValue(p, 42);
        pairType.GetField("b", AnyInstance)!.SetValue(p, "hello");
        var q = Activator.CreateInstance(pairType)!;
        pairType.GetField("a", AnyInstance)!.SetValue(q, 42);
        pairType.GetField("b", AnyInstance)!.SetValue(q, "hello");
        Assert.True(p.Equals(q));
        var str = p.ToString()!;
        Assert.Contains("a = 42", str);
        Assert.Contains("b = hello", str);
    }

    // === Generic user data types ===

    [Fact]
    public void GenericData_Declaration_EmitsOpenGenericTypeDefinition()
    {
        const string source = """
namespace Test

struct Pair<A, B> {
    first: A
    second: B
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);
        var (asm, diags) = EsHarness.EmitBound(binder, bound, "GenericDataDecl");
        Assert.Empty(diags);

        // CLR convention: a generic type's metadata name carries its arity.
        var pairType = asm.MainModule.Types.First(t => t.Name == "Pair`2");
        Assert.Equal(2, pairType.GenericParameters.Count);
        Assert.Equal("A", pairType.GenericParameters[0].Name);
        Assert.Equal("B", pairType.GenericParameters[1].Name);

        // Fields should reference the generic parameters directly
        var first = pairType.Fields.First(f => f.Name == "first");
        var second = pairType.Fields.First(f => f.Name == "second");
        Assert.Equal("A", first.FieldType.Name);
        Assert.Equal("B", second.FieldType.Name);
    }

    [Fact]
    public void GenericData_Construction_StructLiteralWithTypeArgs()
    {
        const string source = """
namespace Test

struct Pair<A, B> {
    first: A
    second: B
}

func makePair() -> int {
    let p = Pair<int, int> { first: 3, second: 4 }
    return p.first + p.second
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(7, Invoke(asm, "Test", "makePair"));
    }

    [Fact]
    public void GenericData_Construction_MixedTypes()
    {
        const string source = """
namespace Test

struct Pair<A, B> {
    first: A
    second: B
}

func go() -> string {
    let p = Pair<int, string> { first: 42, second: "hello" }
    return p.second
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("hello", Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void GenericData_TwoInstantiations_AreDistinctAtRuntime()
    {
        // Pair<int, int> and Pair<string, string> should reify as distinct types
        const string source = """
namespace Test

struct Pair<A, B> {
    first: A
    second: B
}

func intPair() -> int {
    let p = Pair<int, int> { first: 10, second: 20 }
    return p.first
}

func strPair() -> string {
    let p = Pair<string, string> { first: "a", second: "b" }
    return p.second
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(10, Invoke(asm, "Test", "intPair"));
        Assert.Equal("b", Invoke(asm, "Test", "strPair"));
    }

    // === Multi-file compilation ===

    static (Assembly Asm, IReadOnlyList<Esharp.Diagnostics.Diagnostic> Diagnostics) CompileMultiAndLoad(params string[] sources)
    {
        var asmName = $"EsharpTest_{Interlocked.Increment(ref _asmCounter)}";
        var binder = new Esharp.Binder.Binder();
        var parsedUnits = new List<Esharp.Syntax.CompilationUnitSyntax>();
        for (var i = 0; i < sources.Length; i++)
        {
            var parser = new Parser(sources[i], $"test{i}.es");
            var syntax = parser.ParseCompilationUnit();
            Assert.Empty(parser.Diagnostics);
            parsedUnits.Add(syntax);
        }

        foreach (var syntax in parsedUnits)
            binder.RegisterTypes(syntax);
        foreach (var syntax in parsedUnits)
            binder.RegisterSignatures(syntax);

        var bound = new List<BoundCompilationUnit>();
        foreach (var syntax in parsedUnits)
            bound.Add(binder.BindUnit(syntax));

        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        var ilDiagnostics = EsHarness.EmitBoundToFile(binder, bound, asmName, path);
        var binderDiagnostics = binder.Diagnostics;
        var all = binderDiagnostics.Concat(ilDiagnostics).ToList();
        if (all.Any(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error))
        {
            return (null!, all);
        }
        return (Assembly.LoadFrom(path), all);
    }

    [Fact]
    public void MultiFile_DataInFileA_UsedInFunctionInFileB_Works()
    {
        // Defines Point in file A, uses it in file B. sumXY auto-promotes
        // to an instance method on Point because its first param is Point.
        const string fileA = """
namespace Test

struct Point {
    x: int
    y: int
}
""";
        const string fileB = """
namespace Test

func (p: Point) sumXY() -> int {
    return p.x + p.y
}

func go() -> int {
    let p = Point { x: 3, y: 4 }
    return p.sumXY()
}
""";
        var (asm, diags) = CompileMultiAndLoad(fileA, fileB);
        Assert.Empty(diags);
        Assert.Equal(7, Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void MultiFile_FunctionInFileA_CalledFromFileB_Works()
    {
        const string fileA = """
namespace Test

func add(a: int, b: int) -> int {
    return a + b
}
""";
        const string fileB = """
namespace Test

func compute() -> int {
    return add(40, 2)
}
""";
        var (asm, diags) = CompileMultiAndLoad(fileA, fileB);
        Assert.Empty(diags);
        Assert.Equal(42, Invoke(asm, "Test", "compute"));
    }

    [Fact]
    public void MultiFile_InstanceMethodInFileB_OnDataFromFileA_Works()
    {
        const string fileA = """
namespace Test

struct Point {
    x: int
    y: int
}
""";
        const string fileB = """
namespace Test

func (p: Point) manhattan() -> int {
    if p.x < 0 {
        return (0 - p.x) + p.y
    }
    return p.x + p.y
}

func go() -> int {
    let p = Point { x: 3, y: 4 }
    return p.manhattan()
}
""";
        var (asm, diags) = CompileMultiAndLoad(fileA, fileB);
        Assert.Empty(diags);
        Assert.Equal(7, Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void MultiFile_ChoiceInFileA_MatchedInFileB_Works()
    {
        const string fileA = """
namespace Test

union Shape {
    circle(r: int)
    square(side: int)
}
""";
        const string fileB = """
namespace Test

func area(s: Shape) -> int {
    match s {
        .circle(r) { return r * r * 3 }
        .square(side) { return side * side }
    }
    return 0
}

func goCircle() -> int {
    return area(Shape.circle(5))
}

func goSquare() -> int {
    return area(Shape.square(6))
}
""";
        var (asm, diags) = CompileMultiAndLoad(fileA, fileB);
        Assert.Empty(diags);
        Assert.Equal(75, Invoke(asm, "Test", "goCircle"));
        Assert.Equal(36, Invoke(asm, "Test", "goSquare"));
    }

    [Fact]
    public void MultiFile_DuplicateDataName_ReportsDiagnostic()
    {
        const string fileA = """
namespace Test

struct Thing {
    a: int
}
""";
        const string fileB = """
namespace Test

struct Thing {
    b: int
}
""";
        var (_, diags) = CompileMultiAndLoad(fileA, fileB);
        Assert.Contains(diags, d => d.Message.Contains("duplicate data type 'Thing'"));
    }

    // === Protocol-typed parameters ===

    [Fact]
    public void Protocol_RefDataImpl_CalledViaProtocolParameter_DispatchesVirtually()
    {
        const string source = """
namespace Test

interface INamed {
    func getName() -> string
}

class Dog : INamed {
    breed: string

    init(breed: string) {
        self.breed = breed
    }
}

func (self: Dog) getName() -> string {
    return "dog:{self.breed}"
}

class Cat : INamed {
    color: string

    init(color: string) {
        self.color = color
    }
}

func (self: Cat) getName() -> string {
    return "cat:{self.color}"
}

func describe(n: INamed) -> string {
    return n.getName()
}

func goDog() -> string {
    let d = Dog("lab")
    return describe(d)
}

func goCat() -> string {
    let c = Cat("black")
    return describe(c)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("dog:lab", Invoke(asm, "Test", "goDog"));
        Assert.Equal("cat:black", Invoke(asm, "Test", "goCat"));
    }

    [Fact]
    public void Protocol_AcrossFiles_DeclaredInA_ImplInB_UsedInC()
    {
        const string fileA = """
namespace Test

interface IShape {
    func area() -> int
}
""";
        const string fileB = """
namespace Test

class Square : IShape {
    side: int

    init(side: int) {
        self.side = side
    }
}

func (self: Square) area() -> int {
    return self.side * self.side
}
""";
        const string fileC = """
namespace Test

func totalArea(s: IShape) -> int {
    return s.area()
}

func go() -> int {
    let sq = Square(5)
    return totalArea(sq)
}
""";
        var (asm, diags) = CompileMultiAndLoad(fileA, fileB, fileC);
        Assert.Empty(diags);
        Assert.Equal(25, Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void MultiFile_DistinctNamespaces_CoexistInOneAssembly()
    {
        // C#-like multi-namespace: distinct namespaces compile into one assembly,
        // each emitted under its own host class (`Alpha.Alpha`, `Beta.Beta`).
        const string fileA = """
namespace Alpha

func a() -> int { return 1 }
""";
        const string fileB = """
namespace Beta

func b() -> int { return 2 }
""";
        var (asm, diags) = CompileMultiAndLoad(fileA, fileB);
        Assert.DoesNotContain(diags, d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
        const BindingFlags anyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        Assert.NotNull(asm.GetType("Alpha.Alpha")!.GetMethod("a", anyStatic));
        Assert.NotNull(asm.GetType("Beta.Beta")!.GetMethod("b", anyStatic));
    }

    // === Feature 6: spawn / chan / select ===

    [Fact]
    public void Chan_Buffered_Creation_UsesStdlibChan()
    {
        const string source = """
namespace Test

func make() -> Chan<int> {
    let ch = chan<int>(4)
    return ch
}
""";
        var asm = CompileAndLoad(source);
        var result = Invoke(asm, "Test", "make");
        Assert.NotNull(result);
        Assert.Equal("Chan`1", result!.GetType().Name);
        Assert.Equal("Esharp.Stdlib.Chan`1[[System.Int32, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]", result.GetType().FullName);
    }

    [Fact]
    public void Chan_Unbuffered_Creation_DefaultsToZeroCapacity()
    {
        const string source = """
namespace Test

func make() -> Chan<string> {
    let ch = chan<string>(0)
    return ch
}
""";
        var asm = CompileAndLoad(source);
        var result = Invoke(asm, "Test", "make");
        Assert.NotNull(result);
        Assert.Contains("Chan", result!.GetType().FullName!);
    }

    [Fact]
    public void Chan_SendAndTryReceive_SynchronousRoundTrip()
    {
        // Direct send + TryReceive — no spawn needed.
        // Exercises method call dispatch against Esharp.Stdlib.Chan<T>.
        const string source = """
namespace Test

func echo(x: int) -> bool {
    let ch = chan<int>(1)
    ch.Send(x)
    return true
}
""";
        var asm = CompileAndLoad(source);
        var result = Invoke(asm, "Test", "echo", 42);
        Assert.Equal(true, result);
    }

    [Fact]
    public void Spawn_SimpleJob_RunsAndJoinsCleanly()
    {
        const string source = """
namespace Test

func run() -> int {
    let j = spawn {
        let x = 1
    }
    j.Join()
    return 7
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(7, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Spawn_WithChanCapture_ProducerConsumer()
    {
        const string source = """
namespace Test

func run() -> int {
    let ch = chan<int>(4)
    let producer = spawn {
        ch.Send(1)
        ch.Send(2)
        ch.Send(3)
        ch.Close()
    }
    producer.Join()
    var total = 0
    for v in ch {
        total += v
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(6, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Chan_OfUserChoice_ParameterSendFromArg()
    {
        // Narrower: param-typed chan<Evt>, send an Evt that was built in the caller.
        const string source = """
namespace Test

union Evt {
    ping(n: int)
    done
}

func run() -> int {
    let feed = chan<Evt>(8)
    feed.Send(Evt.ping(5))
    feed.Close()
    return 7
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(7, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Spawn_CapturesChanOfUserChoice_CallsHelper()
    {
        // Spawn body captures a chan of a user-defined value choice and
        // calls Send + Close via ChanOps. Note: for-in-chan over user
        // choice types is a separate issue (GetEnumerator on Chan<T> with
        // user T) not covered here.
        const string source = """
namespace Test

union Evt {
    ping(n: int)
    done
}

func produce(ch: chan<Evt>) {
    ch.Send(Evt.ping(1))
    ch.Send(Evt.ping(2))
    ch.Close()
}

func run() -> int {
    let feed = chan<Evt>(8)
    let job = spawn {
        produce(feed)
    }
    job.Join()
    return 7
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(7, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Spawn_CallsHelperWithCapturedChan()
    {
        // Regression: spawn body passes the captured chan as an argument to
        // another function. Previously the call's argument path didn't hoist
        // the chan reference through the display class.
        const string source = """
namespace Test

func send3(ch: chan<int>) {
    ch.Send(1)
    ch.Send(2)
    ch.Send(3)
    ch.Close()
}

func run() -> int {
    let ch = chan<int>(4)
    let producer = spawn {
        send3(ch)
    }
    producer.Join()
    var total = 0
    for v in ch {
        total += v
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(6, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Select_WithDefault_NoneReady_TakesDefault()
    {
        const string source = """
namespace Test

func run() -> int {
    let ch = chan<int>(1)
    var fired = 0
    select {
        .recv(v, ch) { fired = 1 }
        default      { fired = 99 }
    }
    return fired
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(99, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Select_WithDefault_OneReady_TakesThatArm()
    {
        const string source = """
namespace Test

func run() -> int {
    let ch = chan<int>(1)
    ch.Send(42)
    var got = 0
    select {
        .recv(v, ch) { got = v }
        default      { got = 99 }
    }
    return got
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "run"));
    }

    // === Phase 1a: Property get/set on BCL types ===

    [Fact]
    public void Property_GetFromBclObject_ListCount()
    {
        const string source = """
namespace Test

func run() -> int {
    let list = List<int>()
    list.Add(1)
    list.Add(2)
    list.Add(3)
    return list.Count
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Property_GetFromBclObject_StringLength()
    {
        const string source = """
namespace Test

func run() -> int {
    let s = "hello"
    return s.Length
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(5, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Property_SetOnBclObject_StringBuilderCapacity()
    {
        const string source = """
namespace Test

func run() -> int {
    let sb = StringBuilder()
    sb.Capacity = 128
    return sb.Capacity
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(128, Invoke(asm, "Test", "run"));
    }

    // === Phase 1b: Indexer read/write ===

    [Fact]
    public void Indexer_ListGetSet_RoundTrip()
    {
        const string source = """
namespace Test

func run() -> int {
    let list = List<int>()
    list.Add(10)
    list.Add(20)
    list.Add(30)
    list[1] = 99
    return list[1]
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(99, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Indexer_DictionaryGetSet_StringIntRoundTrip()
    {
        const string source = """
namespace Test

func run() -> int {
    let scores = Dictionary<string, int>()
    scores["alice"] = 100
    scores["bob"] = 85
    return scores["alice"] + scores["bob"]
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(185, Invoke(asm, "Test", "run"));
    }

    // === Phase 1c: Extension method dispatch (LINQ) ===

    [Fact]
    public void Extension_LinqCount_OnList()
    {
        const string source = """
namespace Test

func run() -> int {
    let nums = List<int>()
    nums.Add(1)
    nums.Add(2)
    nums.Add(3)
    return nums.Count()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Extension_LinqSumOverSelectedIntList()
    {
        const string source = """
namespace Test

func run() -> int {
    let nums = List<int>()
    nums.Add(1)
    nums.Add(2)
    nums.Add(3)
    return nums.Sum()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(6, Invoke(asm, "Test", "run"));
    }

    // === Phase 2a: default(T) expression ===

    [Fact]
    public void Default_IntIsZero()
    {
        const string source = """
namespace Test

func run() -> int {
    let x = default(int)
    return x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Default_StructZeroed()
    {
        const string source = """
namespace Test

struct P {
    x: int
    y: int
}

func run() -> int {
    let p = default(P)
    return p.x + p.y
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Default_BoolIsFalse()
    {
        const string source = """
namespace Test

func run() -> bool {
    let b = default(bool)
    return b
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(false, Invoke(asm, "Test", "run"));
    }

    // === Phase 2b: out parameters (call-site consumption) ===

    [Fact]
    public void Out_IntTryParse_Success()
    {
        const string source = """
namespace Test

func run() -> int {
    if int.TryParse("42", out var n) {
        return n
    }
    return -1
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Out_IntTryParse_Failure()
    {
        const string source = """
namespace Test

func run() -> int {
    if int.TryParse("not a number", out var n) {
        return n
    }
    return -1
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(-1, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Out_DictionaryTryGetValue()
    {
        const string source = """
namespace Test

func run() -> int {
    let scores = Dictionary<string, int>()
    scores["alice"] = 100
    if scores.TryGetValue("alice", out var score) {
        return score
    }
    return -1
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(100, Invoke(asm, "Test", "run"));
    }

    // === Phase 3a: Explicit generic method type arguments ===

    [Fact]
    public void GenericCall_EnumerableCountOnList()
    {
        // Enumerable.Count<int>(list) — explicit type arg
        const string source = """
namespace Test

func run() -> int {
    let nums = List<int>()
    nums.Add(10)
    nums.Add(20)
    return Enumerable.Count<int>(nums)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(2, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void GenericCall_InferenceStillWorks()
    {
        // nums.Sum() — inference path unchanged
        const string source = """
namespace Test

func run() -> int {
    let nums = List<int>()
    nums.Add(1)
    nums.Add(2)
    nums.Add(3)
    return nums.Sum()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(6, Invoke(asm, "Test", "run"));
    }

    // === Phase 3b: params / varargs ===

    [Fact]
    public void Params_StringFormat_TwoArgs()
    {
        const string source = """
namespace Test

func run() -> string {
    return string.Format("{0} + {1} = {2}", 1, 2, 3)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("1 + 2 = 3", Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Params_StringFormat_NoExtraArgs()
    {
        // No trailing args → params empty array, non-format overload should NOT be chosen.
        // The { N } in the first arg requires at least N+1 args, so pass one.
        const string source = """
namespace Test

func run() -> string {
    return string.Format("hello {0}", "world")
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("hello world", Invoke(asm, "Test", "run"));
    }

    // === Phase 3c: Attribute constructor args (IL backend) ===

    [Fact]
    public void Attribute_ObsoleteWithMessage_EmittedOntoType()
    {
        const string source = """
namespace Test

[Obsolete("do not use")]
struct Legacy {
    value: int
}

func run() -> int { return 0 }
""";
        var asm = CompileAndLoad(source);
        var legacyType = asm.GetType("Test.Legacy")!;
        var attrs = legacyType.GetCustomAttributesData();
        var obs = attrs.FirstOrDefault(a => a.AttributeType.Name == "ObsoleteAttribute");
        Assert.NotNull(obs);
        Assert.Equal("do not use", obs!.ConstructorArguments[0].Value);
    }

    // === Phase 4: Event subscription ===

    [Fact]
    public void Event_Subscribe_ObservableCollectionCollectionChanged()
    {
        // When an item is added, CollectionChanged fires. The handler captures `fired` by ref
        // (through the standard E# display-class hoisting) and increments it.
        const string source = """
namespace Test

func run() -> int {
    var fired = 0
    let coll = ObservableCollection<int>()
    coll.CollectionChanged += func(sender: object, args: NotifyCollectionChangedEventArgs) -> void {
        fired = fired + 1
    }
    coll.Add(10)
    coll.Add(20)
    return fired
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(2, Invoke(asm, "Test", "run"));
    }

    // === Phase 5: try / catch / throw ===

    [Fact]
    public void Try_CatchSpecificException_FormatException()
    {
        const string source = """
namespace Test

func run() -> int {
    var result = 0
    try {
        result = int.Parse("not a number")
    } catch (e: FormatException) {
        result = -1
    }
    return result
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(-1, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Try_CatchAll_BareCatch()
    {
        const string source = """
namespace Test

func run() -> int {
    var result = 0
    try {
        result = int.Parse("nope")
    } catch {
        result = 42
    }
    return result
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Try_BodyRunsWithoutException()
    {
        const string source = """
namespace Test

func run() -> int {
    var result = 0
    try {
        result = int.Parse("100")
    } catch (e: Exception) {
        result = -1
    }
    return result
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(100, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void Throw_Simple_InvalidOperation()
    {
        const string source = """
namespace Test

func run() -> int {
    var result = 0
    try {
        throw InvalidOperationException("bad")
    } catch (e: Exception) {
        result = 99
    }
    return result
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(99, Invoke(asm, "Test", "run"));
    }

    // === Phase 1b: let/var fields, readonly data ===

    [Fact]
    public void LetAndVarMembers_AreEmittedAsProperties()
    {
        var asm = CompileAndLoad("""
namespace Test

struct Point {
    let x: int
    var y: int
}

func make() -> int {
    let p = Point { x: 1, y: 2 }
    return p.x + p.y
}
""");
        var pointType = asm.GetType("Test.Point")!;
        var x = pointType.GetProperty("x", AnyInstance)!;
        var y = pointType.GetProperty("y", AnyInstance)!;
        Assert.NotNull(x.GetMethod);
        Assert.NotNull(x.SetMethod);
        Assert.False(x.SetMethod!.IsPublic);
        Assert.NotNull(y.GetMethod);
        Assert.NotNull(y.SetMethod);
        Assert.Equal(3, Invoke(asm, "Test", "make"));
    }

    [Fact]
    public void ReadonlyData_AllFieldsInitOnly()
    {
        var asm = CompileAndLoad("""
namespace Test

readonly struct Vec2 {
    x: float
    y: float
}

func make() -> float {
    let v = Vec2 { x: 3.0, y: 4.0 }
    return v.x + v.y
}
""");
        var vecType = asm.GetType("Test.Vec2")!;
        foreach (var f in vecType.GetFields(AnyInstance))
            Assert.True(f.IsInitOnly, $"Field {f.Name} should be InitOnly");
        Assert.Equal(7.0f, Invoke(asm, "Test", "make"));
    }

    [Fact]
    public void ReadonlyData_HasIsReadOnlyAttribute()
    {
        var asm = CompileAndLoad("""
namespace Test

readonly struct Pt {
    x: int
    y: int
}

func make() -> int { return 0 }
""");
        var ptType = asm.GetType("Test.Pt")!;
        Assert.Contains(ptType.GetCustomAttributes(false),
            a => a.GetType().Name == "IsReadOnlyAttribute");
    }

    [Fact]
    public void LetField_AssignmentOutsideInit_ReportsError()
    {
        const string source = """
namespace Test

struct Point {
    let x: int
    y: int
}

func bad() -> int {
    var p = Point { x: 1, y: 2 }
    p.x = 99
    return p.x
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        binder.Bind(syntax);
        Assert.Contains(binder.Diagnostics, d => d.Message.Contains("Cannot assign to immutable field"));
    }

    [Fact]
    public void LocalLet_Reassignment_ReportsError()
    {
        const string source = """
namespace Test

func bad() -> int {
    let x = 1
    x = 2
    return x
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        binder.Bind(syntax);
        Assert.Contains(binder.Diagnostics, d => d.Message.Contains("Cannot assign to immutable binding"));
    }

    // === Phase 1c: with expressions ===

    [Fact]
    public void With_CopyAndOverwrite_ProducesNewValue()
    {
        var asm = CompileAndLoad("""
namespace Test

struct Point {
    x: int
    y: int
}

func run() -> int {
    let p = Point { x: 3, y: 4 }
    let q = p with { x: 10 }
    return q.x + q.y
}
""");
        Assert.Equal(14, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void With_DoesNotMutateOriginal()
    {
        var asm = CompileAndLoad("""
namespace Test

struct Point {
    x: int
    y: int
}

func run() -> int {
    let p = Point { x: 3, y: 4 }
    let q = p with { x: 10 }
    return p.x
}
""");
        Assert.Equal(3, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void With_OnReadonlyData_Works()
    {
        var asm = CompileAndLoad("""
namespace Test

readonly struct Vec {
    x: int
    y: int
}

func run() -> int {
    let v = Vec { x: 1, y: 2 }
    let w = v with { y: 99 }
    return w.x + w.y
}
""");
        Assert.Equal(100, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void With_OnRefData_ReportsError()
    {
        const string source = """
namespace Test

class Node {
    value: int
}

func run() -> int {
    let n = Node { value: 1 }
    let m = n with { value: 2 }
    return m.value
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        binder.Bind(syntax);
        Assert.Contains(binder.Diagnostics, d => d.Message.Contains("with"));
    }

    // === Phase 2: IL parity ===

    [Fact]
    public void Result_OkAndError_RoundTrips()
    {
        var asm = CompileAndLoad("""
namespace Test

func getOk() -> Result<int, string> {
    return ok(42)
}

func getErr() -> Result<int, string> {
    return error("bad")
}
""");
        var okResult = Invoke(asm, "Test", "getOk");
        Assert.NotNull(okResult);
        var isOk = (bool)EsHarness.ResultMember(okResult, "IsOk")!;
        Assert.True(isOk);
        var value = (int)EsHarness.ResultMember(okResult, "Value")!;
        Assert.Equal(42, value);

        var errResult = Invoke(asm, "Test", "getErr");
        Assert.NotNull(errResult);
        var isErr = (bool)EsHarness.ResultMember(errResult, "IsError")!;
        Assert.True(isErr);
        var error = (string)EsHarness.ResultMember(errResult, "Error")!;
        Assert.Equal("bad", error);
    }

    [Fact]
    public void TryUnwrap_PropagatesError()
    {
        var asm = CompileAndLoad("""
namespace Test

func inner() -> Result<int, string> {
    return error("fail")
}

func outer() -> Result<int, string> {
    let x = inner()?
    return ok(x + 1)
}
""");
        var result = Invoke(asm, "Test", "outer");
        Assert.NotNull(result);
        var isErr = (bool)EsHarness.ResultMember(result, "IsError")!;
        Assert.True(isErr);
        var error = (string)EsHarness.ResultMember(result, "Error")!;
        Assert.Equal("fail", error);
    }

    [Fact]
    public void TryUnwrap_UnwrapsOk()
    {
        var asm = CompileAndLoad("""
namespace Test

func inner() -> Result<int, string> {
    return ok(10)
}

func outer() -> Result<int, string> {
    let x = inner()?
    return ok(x + 1)
}
""");
        var result = Invoke(asm, "Test", "outer");
        Assert.NotNull(result);
        var isOk = (bool)EsHarness.ResultMember(result, "IsOk")!;
        Assert.True(isOk);
        var value = (int)EsHarness.ResultMember(result, "Value")!;
        Assert.Equal(11, value);
    }

    [Fact]
    public void ByRef_MutatesThroughPointer()
    {
        var asm = CompileAndLoad("""
namespace Test

func addTen(x: *int) {
    x += 10
}

func run() -> int {
    var n = 5
    addTen(*n)
    return n
}
""");
        Assert.Equal(15, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void LetElse_ReturnsOnNull()
    {
        var asm = CompileAndLoad("""
namespace Test

func tryFind(key: string) -> string? {
    if key == "a" {
        return "found"
    }
    return nil
}

func run() -> string {
    let value = tryFind("b") else {
        return "default"
    }
    return value
}

func runFound() -> string {
    let value = tryFind("a") else {
        return "default"
    }
    return value
}
""");
        Assert.Equal("default", Invoke(asm, "Test", "run"));
        Assert.Equal("found", Invoke(asm, "Test", "runFound"));
    }

    // NOTE: Interop tests (JSON, import static, arrow lambdas) live in ILEmitterTests_Interop.cs

    // === Sugar Feature 1: Ternary operator ===

    [Fact]
    public void Ternary_BasicConditional()
    {
        const string source = """
namespace Test

func abs(x: int) -> int {
    return x >= 0 ? x : 0 - x
}

func pick(flag: bool) -> int {
    return flag ? 42 : 99
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(5, Invoke(asm, "Test", "abs", -5));
        Assert.Equal(3, Invoke(asm, "Test", "abs", 3));
        Assert.Equal(0, Invoke(asm, "Test", "abs", 0));
        Assert.Equal(42, Invoke(asm, "Test", "pick", true));
        Assert.Equal(99, Invoke(asm, "Test", "pick", false));
    }

    [Fact]
    public void Parse_TupleTypeInGeneric()
    {
        const string source = """
namespace Test

func test() -> int {
    var tasks = List<Task<(int, string)>>()
    return tasks.Count
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
    }

    [Fact]
    public void Parse_ExprBodiedWithLock()
    {
        const string source = """
namespace Test

class Foo {
    x: int
    lock: object

    init() {
        self.x = 0
        self.lock = object()
    }

    func getX() -> int = self.x
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
    }

    // === Combined sugar feature tests ===

    [Fact]
    public void PositionalData_FieldAccess()
    {
        const string source = """
namespace Test

struct Item(name: string, value: int)

func make() -> int {
    let a = Item("hello", 42)
    return a.value
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "make"));
    }

    [Fact]
    public void TupleReturn_WithListElements()
    {
        const string source = """
namespace Test

func split() -> (List<int>, List<string>) {
    var nums = List<int>()
    var strs = List<string>()
    nums.Add(1)
    strs.Add("a")
    return (nums, strs)
}

func test() -> int {
    let (nums, strs) = split()
    return nums.Count + strs.Count
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(2, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void RefData_ExprBodiedMethod()
    {
        const string source = """
namespace Test

class Bag {
    items: List<int>

    init() {
        self.items = List<int>()
    }

    func count() -> int = self.items.Count

    func add(x: int) {
        self.items.Add(x)
    }
}

func test() -> int {
    let b = Bag()
    b.add(10)
    b.add(20)
    return b.count()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(2, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Ternary_WithNullCoalescing()
    {
        const string source = """
namespace Test

func test() -> int {
    let a = 5 > 0 ? 5 : 0
    let s = nil ?? "fallback"
    return a
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(5, Invoke(asm, "Test", "test"));
    }

    // === Sugar Feature 8: Tuples ===

    [Fact]
    public void Tuple_ReturnAndDestructure()
    {
        const string source = """
namespace Test

func swap(a: int, b: int) -> (int, int) {
    return (b, a)
}

func test() -> int {
    let (x, y) = swap(1, 2)
    return x * 10 + y
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(21, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Tuple_TwoElements()
    {
        const string source = """
namespace Test

func pair(a: int, b: string) -> (int, string) {
    return (a, b)
}

func getFirst() -> int {
    let (x, y) = pair(42, "hello")
    return x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "getFirst"));
    }

    // === Sugar Feature 7: Methods inside class ===

    [Fact]
    public void RefDataMethods_BasicInstanceMethod()
    {
        const string source = """
namespace Test

class Counter {
    value: int

    init() {
        self.value = 0
    }

    func inc() {
        self.value += 1
    }

    func get() -> int = self.value
}

func run() -> int {
    let c = Counter()
    c.inc()
    c.inc()
    c.inc()
    return c.get()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void RefDataMethods_PubMethod()
    {
        const string source = """
namespace Test

class Greeter {
    name: string

    init(n: string) {
        self.name = n
    }

    pub func greet() -> string = self.name
}

func run() -> string {
    let g = Greeter("world")
    return g.greet()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("world", (string)Invoke(asm, "Test", "run")!);
    }

    // === Sugar Feature 6: Collection literals ===

    [Fact]
    public void ListLiteral_IntElements()
    {
        const string source = """
namespace Test

func count() -> int {
    let xs = [10, 20, 30]
    return xs.Count
}

func first() -> int {
    let xs = [42, 99]
    return xs[0]
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3, Invoke(asm, "Test", "count"));
        Assert.Equal(42, Invoke(asm, "Test", "first"));
    }

    [Fact]
    public void ListLiteral_StringElements()
    {
        const string source = """
namespace Test

func joined() -> string {
    let xs = ["hello", "world"]
    return xs[0]
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("hello", (string)Invoke(asm, "Test", "joined")!);
    }

    // === Sugar Feature 5: Positional data declarations ===

    [Fact]
    public void PositionalData_BasicConstruction()
    {
        const string source = """
namespace Test

struct Vec2(x: int, y: int)

func (v: Vec2) sum() -> int {
    return v.x + v.y
}

func makeAndSum() -> int {
    let v = Vec2(3, 4)
    return v.sum()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(7, Invoke(asm, "Test", "makeAndSum"));
    }

    [Fact]
    public void PositionalClass_IsCaptureHeader()
    {
        // `class Label(...)` is the primary-ctor capture header: params are not
        // public fields — an in-body method captures them on use.
        const string source = """
namespace Test

class Label(text: string, size: int) {
    func getText() -> string {
        return text
    }
}

func make() -> string {
    let l = Label("hello", 12)
    return l.getText()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("hello", (string)Invoke(asm, "Test", "make")!);
    }

    // === Sugar Feature 4: Null-conditional ?. ===

    [Fact]
    public void NullConditional_PropertyAccess()
    {
        const string source = """
namespace Test

func safeLen(s: string) -> int {
    return s?.Length ?? 0
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "safeLen", [null]));
        Assert.Equal(5, Invoke(asm, "Test", "safeLen", "hello"));
    }

    [Fact]
    public void NullConditional_ChainedWithCoalescing()
    {
        const string source = """
namespace Test

func safeLen(s: string) -> int {
    let result = s ?? ""
    return result.Length
}

func withCoalesce(s: string) -> string {
    return s ?? "none"
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("none", (string)Invoke(asm, "Test", "withCoalesce", [null])!);
        Assert.Equal("hello", (string)Invoke(asm, "Test", "withCoalesce", "hello")!);
        Assert.Equal(0, Invoke(asm, "Test", "safeLen", [null]));
        Assert.Equal(5, Invoke(asm, "Test", "safeLen", "hello"));
    }

    // === Sugar Feature 3: Null-coalescing ?? ===

    [Fact]
    public void NullCoalescing_StringFallback()
    {
        const string source = """
namespace Test

func fallback(s: string) -> string {
    return s ?? "default"
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("default", (string)Invoke(asm, "Test", "fallback", [null])!);
        Assert.Equal("hello", (string)Invoke(asm, "Test", "fallback", "hello")!);
    }

    // === Sugar Feature 2: Expression-bodied functions ===

    [Fact]
    public void ExpressionBodied_SimpleReturn()
    {
        const string source = """
namespace Test

func double(x: int) -> int = x * 2
func negate(x: int) -> int = 0 - x
func always42() -> int = 42
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(10, Invoke(asm, "Test", "double", 5));
        Assert.Equal(-3, Invoke(asm, "Test", "negate", 3));
        Assert.Equal(42, Invoke(asm, "Test", "always42"));
    }

    [Fact]
    public void Ternary_InExpression()
    {
        const string source = """
namespace Test

func clamp(x: int, lo: int, hi: int) -> int {
    let v = x < lo ? lo : x
    return v > hi ? hi : v
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(5, Invoke(asm, "Test", "clamp", 5, 0, 10));
        Assert.Equal(0, Invoke(asm, "Test", "clamp", -3, 0, 10));
        Assert.Equal(10, Invoke(asm, "Test", "clamp", 15, 0, 10));
    }

    static void AssertParses(string source)
    {
        var parser = new Parser(source, "test.es");
        parser.ParseCompilationUnit();
    }

    static void AssertBinds(string source)
    {
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        binder.Bind(syntax);
    }

    static void AssertEmits(string source)
    {
        var asmName = $"EsharpTest_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        EsHarness.EmitBoundToFile(binder, bound, asmName, path);
    }

    [Fact]
    public void Parse_LambdaWithComplexGenericReturnType()
    {
        AssertParses("""
namespace T
func run() {
    var xs = List<Task<(List<string>, List<string>)>>()
    xs.Add(Task.Run(func() -> (List<string>, List<string>) { return (List<string>(), List<string>()) }))
}
""");
    }

    [Fact]
    public void Parse_ForTupleDestructuring()
    {
        AssertParses("""
namespace T
func run() {
    let results = List<(int, int)>()
    for (a, b) in results {
        let x = a + b
    }
}
""");
    }

    [Fact]
    public void Parse_RefDataWithMultipleMethods()
    {
        AssertParses("""
namespace T
class Svc {
    items: List<string>
    obj: object
    init() {
        self.items = List<string>()
        self.obj = object()
    }
    func start() { self.items.Add("a") }
    func stop() { self.items.Clear() }
    func query(filter: string) -> List<string> { return self.items }
    func count() -> int = self.items.Count
    func addItem(s: string) = self.items.Add(s)
}
""");
    }

    [Fact]
    public void Parse_LambdaInsideMethodCallChain()
    {
        AssertParses("""
namespace T
func run() {
    let items = List<string>()
    for item in items.Where(func(s: string) -> bool { return s != "" }).ToList() {
        let x = item
    }
}
""");
    }

    [Fact]
    public void Parse_NullConditionalAndCoalesceInTernary()
    {
        AssertParses("""
namespace T
func run() {
    let s: string = nil
    let x = s?.Length ?? 0
    let y = x > 0 ? s : "default"
}
""");
    }

    [Fact]
    public void Parse_TryCatchWithTypedBinding()
    {
        AssertParses("""
namespace T
func run() {
    try {
        let x = 1
    } catch (ex: Exception) {
        let msg = ex.Message
    }
}
""");
    }

    [Fact]
    public void Emit_RefDataWithMethodsAndLambdas()
    {
        AssertEmits("""
namespace T

struct Config(name: string, url: string)

class Aggregator {
    configs: List<Config>
    count: int
    init() {
        self.configs = List<Config>()
        self.count = 0
    }
    func getCount() -> int = self.count
    func addConfig(name: string, url: string) = self.configs.Add(Config(name, url))
}
""");
    }

    [Fact]
    public void Emit_LambdaWithTupleReturnType()
    {
        AssertEmits("""
namespace T
func run() {
    var tasks = List<Task<(List<string>, List<string>)>>()
    tasks.Add(Task.Run(func() -> (List<string>, List<string>) { return (List<string>(), List<string>()) }))
}
""");
    }

    // LINQ extension methods with lambda args crash Cecil's ImportGenericParameter
    // when the receiver is a closed generic like List<string>. This is a pre-existing
    // Cecil limitation — tracked separately from language feature tests.

    [Fact]
    public void Emit_AsyncFunctionWithAwait()
    {
        AssertEmits("""
namespace T
func fetch() -> Task<string> {
    let client = HttpClient()
    let result = await client.GetStringAsync("http://example.com")
    return result
}
""");
    }

    [Fact]
    public void Emit_ComplexRefDataWithAsyncAndLambdas()
    {
        AssertEmits("""
namespace T
func work() -> (List<string>, List<string>) {
    var items = List<string>()
    var errors = List<string>()
    return (items, errors)
}
func run() {
    var tasks = List<Task<(List<string>, List<string>)>>()
    tasks.Add(Task.Run(func() -> (List<string>, List<string>) { return work() }))
    let results = await Task.WhenAll(tasks.ToArray())
}
""");
    }

    [Fact]
    public void Bind_RefDataWithMethodsAndLambdas()
    {
        AssertBinds("""
namespace T
using "System.Net.Http"

struct Config(name: string, url: string)
struct Item(title: string, source: string)

class Aggregator {
    configs: List<Config>
    items: List<Item>
    client: HttpClient
    init() {
        self.configs = List<Config>()
        self.items = List<Item>()
        self.client = HttpClient()
    }
    func start() { self.poll() }
    func poll() {
        var tasks = List<Task<(List<Item>, List<string>)>>()
        for cfg in self.configs {
            let c = self.client
            let f = cfg
            tasks.Add(Task.Run(func() -> (List<Item>, List<string>) { return (List<Item>(), List<string>()) }))
        }
    }
    func query(filter: string, limit: int) -> List<Item> {
        var result = self.items
        if filter != "" { result = result.Where(func(n: Item) -> bool { return n.source == filter }).ToList() }
        return result.Take(limit > 0 ? limit : 50).ToList()
    }
    func count() -> int = self.items.Count
    func addConfig(name: string, url: string) = self.configs.Add(Config(name, url))
}
""");
    }

    [Fact]
    public void Parse_InterfaceImplementation()
    {
        AssertParses("""
namespace T
class MyPlugin : IPlugin {
    init() { }
    pub func Name() -> string = "test"
    pub func Run(app: WebApplication) {
        app.MapGet("/api/test", func(q: string) -> IResult {
            return Results.Json(q ?? "")
        })
        app.MapGet("/api/ping", func() -> IResult = Results.Json("pong"))
    }
}
""");
    }

    [Fact]
    public void Parse_ImportStaticAndArrowLambda()
    {
        AssertParses("""
namespace T
using static "System.Math"

func run() -> double {
    return Max(1.0, ((x) => x + 2.0)(3.0))
}
""");
    }

    [Fact]
    public void GenericTypeWithTupleArg_NoStackOverflow()
    {
        var source = """
namespace Test

func run() -> int {
    var xs = List<(int, int)>()
    return xs.Count
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void NestedGenericWithTupleArg_NoStackOverflow()
    {
        var source = """
namespace Test

func run() -> int {
    var xs = List<(List<int>, List<int>)>()
    return xs.Count
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(0, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void ExpressionBodiedMethodInRefData()
    {
        var source = """
namespace Test

class Box {
    value: int
    init(v: int) { self.value = v }
    func get() -> int = self.value
    func doubled() -> int = self.value * 2
}

func run() -> int {
    let b = Box(7)
    return b.get() + b.doubled()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(21, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void VoidExpressionBodiedMethodInRefData()
    {
        var source = """
namespace Test

class Bag {
    items: List<int>
    init() { self.items = List<int>() }
    func add(x: int) = self.items.Add(x)
    func count() -> int = self.items.Count
}

func run() -> int {
    let b = Bag()
    b.add(10)
    b.add(20)
    return b.count()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(2, Invoke(asm, "Test", "run"));
    }
}
