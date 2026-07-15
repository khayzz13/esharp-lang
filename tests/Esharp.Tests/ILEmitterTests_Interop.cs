using System.Reflection;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

public sealed class ILEmitterTests_Interop
{
    static int _asmCounter;

    static Assembly CompileAndLoad(string source, IReadOnlyList<string>? referencePaths = null)
    {
        var asmName = $"EsharpInteropTest_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        return EsHarness.EmitBoundToFileVerifiedAndLoad(binder, bound, asmName, path, referencePaths: referencePaths);
    }

    static object? Invoke(Assembly asm, string typeName, string methodName, params object?[] args)
    {
        var type = asm.GetType($"Test.{typeName}")
            ?? throw new Exception($"Type Test.{typeName} not found");
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception($"Method {methodName} not found on {typeName}");
        return method.Invoke(null, args);
    }

    static readonly string JsonAsmPath = typeof(System.Text.Json.JsonDocument).Assembly.Location;

    // === JSON chained member access ===

    [Fact]
    public void JsonDocument_Parse_RootElement_ValueKind_Stepped()
    {
        const string source = """
namespace Test

using "System.Text.Json"

func run() -> string {
    let doc = JsonDocument.Parse("{}")
    let elem = doc.RootElement
    let kind = elem.ValueKind
    let isObj = kind == JsonValueKind.Object
    return isObj ? "object" : "other"
}
""";
        var asm = CompileAndLoad(source, [JsonAsmPath]);
        Assert.Equal("object", Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void JsonDocument_ChainedMemberAccess()
    {
        const string source = """
namespace Test

using "System.Text.Json"

func run() -> string {
    let doc = JsonDocument.Parse("{}")
    return doc.RootElement.ValueKind == JsonValueKind.Object ? "object" : "other"
}
""";
        var asm = CompileAndLoad(source, [JsonAsmPath]);
        Assert.Equal("object", Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void JsonSerializer_Serialize()
    {
        const string source = """
namespace Test

using "System.Text.Json"

func run() -> string {
    return JsonSerializer.Serialize("hello")
}
""";
        var asm = CompileAndLoad(source, [JsonAsmPath]);
        var result = (string)Invoke(asm, "Test", "run")!;
        Assert.Contains("hello", result);
    }

    // === import static ===

    [Fact]
    public void ImportStatic_MathMax()
    {
        const string source = """
namespace Test

using static "System.Math"

func run() -> double {
    return Max(2.5, 4.5)
}
""";
        var asm = CompileAndLoad(source, [typeof(Math).Assembly.Location]);
        Assert.Equal(4.5, (double)Invoke(asm, "Test", "run")!, 5);
    }

    // === Arrow lambdas ===

    [Fact]
    public void ArrowLambda_InfersParameterType()
    {
        const string source = """
namespace Test

func apply(x: int, f: Func<int, int>) -> int {
    return f(x)
}

func run() -> int {
    return apply(5, (x) => x + 2)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(7, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void ArrowLambda_ZeroArgMultiArgCaptures()
    {
        const string source = """
namespace Test

func compute(f: Func<int>) -> int {
    return f()
}

func combine(f: Func<int, int, int>) -> int {
    return f(3, 4)
}

func run() -> int {
    let offset = 5
    let left = compute(() => 7)
    let right = combine((x, y) => x + y + offset)
    return left + right
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(19, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void ArrowLambda_TypedParameter()
    {
        const string source = """
namespace Test

func applyText(f: Func<string, string>) -> string {
    return f("ok")
}

func run() -> string {
    return applyText((value: string) => value)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("ok", Invoke(asm, "Test", "run"));
    }

    // === Enum literal resolution ===

    [Fact]
    public void EnumLiteral_JsonValueKind_EmitsConstant()
    {
        // Verifies enum literals emit ldc.i4 (not ldsfld which causes MissingFieldException)
        const string source = """
namespace Test

using "System.Text.Json"

func run() -> int {
    let kind = JsonValueKind.Object
    return kind == JsonValueKind.Object ? 1 : 0
}
""";
        var asm = CompileAndLoad(source, [JsonAsmPath]);
        Assert.Equal(1, Invoke(asm, "Test", "run"));
    }

    // === Optional value-type parameters ===

    [Fact]
    public void OptionalStructParam_DefaultInitialized()
    {
        // Verifies that optional struct parameters get initobj, not ldnull
        const string source = """
namespace Test

using "System.Text.Json"

func run() -> string {
    let doc = JsonDocument.Parse("{}")
    return "ok"
}
""";
        var asm = CompileAndLoad(source, [JsonAsmPath]);
        Assert.Equal("ok", Invoke(asm, "Test", "run"));
    }

    // === for..in element type inference ===

    [Fact]
    public void ForIn_ListOfInt_SumsElements()
    {
        const string source = """
namespace Test

func run() -> int {
    let xs = [10, 20, 30]
    var total = 0
    for x in xs { total += x }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(60, Invoke(asm, "Test", "run"));
    }

    [Fact]
    public void ForIn_JsonEnumerateArray_GetProperty()
    {
        // Verifies that for..in over EnumerateArray() infers JsonElement,
        // allowing GetProperty/GetInt32 calls on the loop variable.
        const string source = """
namespace Test

using "System.Text.Json"

func run() -> string {
    let doc = JsonDocument.Parse("[1, 2, 3]")
    let root = doc.RootElement
    var total = 0
    for el in root.EnumerateArray() {
        total += el.GetInt32()
    }
    return total.ToString()
}
""";
        var asm = CompileAndLoad(source, [JsonAsmPath]);
        Assert.Equal("6", Invoke(asm, "Test", "run"));
    }

    // === Chained property type resolution ===

    [Fact]
    public void ChainedMember_JsonElement_GetArrayLength()
    {
        // Verifies that chained member access resolves through intermediate types.
        const string source = """
namespace Test

using "System.Text.Json"

func run() -> int {
    let doc = JsonDocument.Parse("[10, 20, 30]")
    return doc.RootElement.GetArrayLength()
}
""";
        var asm = CompileAndLoad(source, [JsonAsmPath]);
        Assert.Equal(3, Invoke(asm, "Test", "run"));
    }
}
