using System.Reflection;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

public sealed class ILEmitterTests_FieldDefaults
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    static int _asmCounter;

    static Assembly CompileAndLoad(string source)
    {
        var asmName = $"EsharpFieldDefTest_{Interlocked.Increment(ref _asmCounter)}";
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
    public void RefData_FieldDefault_String()
    {
        const string source = """
namespace Test

class Config {
    let name: string = "default"
}

func test() -> string {
    let c = Config()
    return c.name
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("default", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void RefData_FieldDefault_Int()
    {
        const string source = """
namespace Test

class Counter {
    var count: int = 42
}

func test() -> int {
    let c = Counter()
    return c.count
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void RefData_FieldDefault_WithInit_DefaultsRunFirst()
    {
        const string source = """
namespace Test

class Server {
    let host: string = "localhost"
    var port: int = 8080

    init(port: int) {
        self.port = port
    }
}

func test() -> string {
    let s = Server(9090)
    return s.host
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("localhost", Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void RefData_FieldDefault_Bool()
    {
        const string source = """
namespace Test

class Flags {
    let enabled: bool = true
    let verbose: bool = false
}

func test() -> bool {
    let f = Flags()
    return f.enabled and not f.verbose
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void PubRefData_MethodsInheritPub()
    {
        const string source = """
namespace Test

pub class Widget {
    let label: string = "widget"

    func getLabel() -> string = self.label
}

func test() -> string {
    let w = Widget()
    return w.getLabel()
}
""";
        var asm = CompileAndLoad(source);
        // Verify the method is public (inherited from pub class)
        var widgetType = asm.GetType("Test.Widget")!;
        var method = widgetType.GetMethod("getLabel", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.Equal("widget", Invoke(asm, "Test", "test"));
    }
}
