using System.Reflection;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Mono.Cecil;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

public sealed class ILEmitterTests_Embedding
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    static int _asmCounter;

    static (Assembly asm, AssemblyDefinition cecil) CompileAndLoadWithCecil(string source)
    {
        var asmName = $"EmbedTest_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var (cecilAsm, diags) = EsHarness.EmitBound(binder, bound, asmName);
        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        cecilAsm.Write(path);
        var loaded = Assembly.LoadFrom(path);
        return (loaded, cecilAsm);
    }

    static Assembly CompileAndLoad(string source)
    {
        var (asm, cecil) = CompileAndLoadWithCecil(source);
        cecil.Dispose();
        return asm;
    }

    static string DumpAllTypes(AssemblyDefinition cecil)
    {
        var lines = new List<string>();
        foreach (var type in cecil.MainModule.Types)
        {
            lines.Add($"=== {type.FullName} ({(type.IsValueType ? "struct" : "class")}) ===");
            foreach (var f in type.Fields)
                lines.Add($"  field {f.FieldType.FullName} {f.Name}");
            foreach (var m in type.Methods)
            {
                lines.Add($"  {m.FullName}");
                if (!m.HasBody) continue;
                foreach (var v in m.Body.Variables)
                    lines.Add($"    .local [{v.Index}] {v.VariableType.FullName}");
                foreach (var inst in m.Body.Instructions)
                    lines.Add($"    {inst}");
            }
        }
        return string.Join("\n", lines);
    }

    static string DumpMethod(AssemblyDefinition cecil, string typeName, string methodName)
    {
        var lines = new List<string>();
        foreach (var type in cecil.MainModule.Types)
        {
            if (!type.FullName.EndsWith(typeName)) continue;
            foreach (var m in type.Methods)
            {
                if (m.Name != methodName) continue;
                lines.Add($"Method: {m.FullName}");
                if (!m.HasBody) continue;
                foreach (var v in m.Body.Variables)
                    lines.Add($"  .local [{v.Index}] {v.VariableType.FullName}");
                foreach (var inst in m.Body.Instructions)
                    lines.Add($"  {inst}");
            }
        }
        return string.Join("\n", lines);
    }

    static object? Invoke(Assembly asm, string typeName, string methodName, params object?[] args)
    {
        var type = asm.GetType($"Test.{typeName}")
            ?? throw new Exception($"Type Test.{typeName} not found");
        var method = type.GetMethod(methodName, AnyStatic)
            ?? throw new Exception($"Method {methodName} not found on {typeName}");
        return method.Invoke(null, args);
    }

    // === Test 1: Value embedding — field declaration compiles ===

    [Fact]
    public void ValueEmbed_FieldExists()
    {
        const string source = """
namespace Test

struct Base {
    var x: int
    var y: int
}

struct Widget {
    Base
    label: string
}

func getLabel() -> string {
    var w = Widget { x: 10, y: 20, label: "hello" }
    return w.label
}
""";
        var (asm, cecil) = CompileAndLoadWithCecil(source);
        var dump = DumpAllTypes(cecil);
        cecil.Dispose();

        // Widget should have a Base field
        Assert.Contains("field Test.Base Base", dump);

        var result = Invoke(asm, "Test", "getLabel");
        Assert.Equal("hello", result);
    }

    // === Test 2: Value embedding — promoted field read ===

    [Fact]
    public void ValueEmbed_PromotedFieldRead()
    {
        const string source = """
namespace Test

struct Base {
    var x: int
    var y: int
}

struct Widget {
    Base
    label: string
}

func getX() -> int {
    var w = Widget { x: 42, y: 7, label: "test" }
    return w.x
}

func getY() -> int {
    var w = Widget { x: 42, y: 7, label: "test" }
    return w.y
}
""";
        var asm = CompileAndLoad(source);

        Assert.Equal(42, Invoke(asm, "Test", "getX"));
        Assert.Equal(7, Invoke(asm, "Test", "getY"));
    }

    // === Test 3: Value embedding — promoted field write ===

    [Fact]
    public void ValueEmbed_PromotedFieldWrite()
    {
        const string source = """
namespace Test

struct Base {
    var x: int
    var y: int
}

struct Widget {
    Base
    label: string
}

func mutateX() -> int {
    var w = Widget { x: 10, y: 20, label: "test" }
    w.x = 99
    return w.x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(99, Invoke(asm, "Test", "mutateX"));
    }

    // === Test 4: Value embedding — promoted method call ===

    [Fact]
    public void ValueEmbed_PromotedMethodCall()
    {
        const string source = """
namespace Test

struct Vec2 {
    var x: int
    var y: int

    func sum() -> int {
        return self.x + self.y
    }
}

struct Entity {
    Vec2
    name: string
}

func getSum() -> int {
    var e = Entity { x: 10, y: 20, name: "test" }
    return e.sum()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(30, Invoke(asm, "Test", "getSum"));
    }

    // === Test 5: Value embedding — mutating method call ===

    [Fact]
    public void ValueEmbed_MutatingMethodCall()
    {
        const string source = """
namespace Test

struct Vec2 {
    var x: int
    var y: int

    func addTo(dx: int, dy: int) {
        self.x += dx
        self.y += dy
    }
}

struct Entity {
    Vec2
    name: string
}

func moveAndGet() -> int {
    var e = Entity { x: 10, y: 20, name: "test" }
    e.addTo(5, 3)
    return e.x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(15, Invoke(asm, "Test", "moveAndGet"));
    }

    // === Test 6: Pointer embedding — field declaration ===

    [Fact]
    public void PointerEmbed_FieldExists()
    {
        const string source = """
namespace Test

struct Base {
    var x: int
    var y: int
}

struct WidgetRef {
    *Base
    label: string
}

func getLabel() -> string {
    var w = WidgetRef { Base: new Base { x: 10, y: 20 }, label: "hello" }
    return w.label
}
""";
        var (asm, cecil) = CompileAndLoadWithCecil(source);
        var dump = DumpAllTypes(cecil);
        cecil.Dispose();

        // WidgetRef should have a Base field typed as the wrapper class
        Assert.Contains("__Ptr_Base", dump);

        var result = Invoke(asm, "Test", "getLabel");
        Assert.Equal("hello", result);
    }

    // === Test 7: Pointer embedding — promoted field read through auto-deref ===

    [Fact]
    public void PointerEmbed_PromotedFieldRead()
    {
        const string source = """
namespace Test

struct Base {
    var x: int
    var y: int
}

struct WidgetRef {
    *Base
    label: string
}

func getX() -> int {
    var w = WidgetRef { Base: new Base { x: 42, y: 7 }, label: "test" }
    return w.x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "getX"));
    }

    // === Test 8: Pointer embedding — promoted field write through auto-deref ===

    [Fact]
    public void PointerEmbed_PromotedFieldWrite()
    {
        const string source = """
namespace Test

struct Base {
    var x: int
    var y: int
}

struct WidgetRef {
    *Base
    label: string
}

func mutateX() -> int {
    var w = WidgetRef { Base: new Base { x: 10, y: 20 }, label: "test" }
    w.x = 99
    return w.x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(99, Invoke(asm, "Test", "mutateX"));
    }

    // === Test 9: Value embedding — compound assignment on promoted field ===

    [Fact]
    public void ValueEmbed_CompoundAssignment()
    {
        const string source = """
namespace Test

struct Base {
    var x: int
}

struct Wrapper {
    Base
    tag: int
}

func addToX() -> int {
    var w = Wrapper { x: 10, tag: 1 }
    w.x += 5
    return w.x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(15, Invoke(asm, "Test", "addToX"));
    }

    // === Test 10: Value embedding — direct access to embedded field by name ===

    [Fact]
    public void ValueEmbed_DirectFieldAccess()
    {
        const string source = """
namespace Test

struct Base {
    var x: int
    var y: int
}

struct Widget {
    Base
    label: string
}

func getBaseY() -> int {
    var w = Widget { x: 10, y: 20, label: "test" }
    return w.Base.y
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(20, Invoke(asm, "Test", "getBaseY"));
    }

    // === Test 11: Embedding with own fields mixed ===

    [Fact]
    public void ValueEmbed_MixedFields()
    {
        const string source = """
namespace Test

struct Position {
    var x: int
    var y: int
}

struct Player {
    Position
    name: string
    var health: int
}

func checkPlayer() -> int {
    var p = Player { x: 5, y: 10, name: "hero", health: 100 }
    p.x += 3
    p.health -= 20
    return p.x + p.health
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(88, Invoke(asm, "Test", "checkPlayer")); // 8 + 80
    }

    // === Test 12: Pointer embedding — nil check ===

    [Fact]
    public void PointerEmbed_NilCheck()
    {
        const string source = """
namespace Test

struct Inner {
    var x: int
}

struct Outer {
    *Inner
    tag: int
}

func isNil() -> bool {
    var o = Outer { tag: 1 }
    return o.Inner == nil
}

func isNotNil() -> bool {
    var o = Outer { Inner: new Inner { x: 42 }, tag: 1 }
    return o.Inner != nil
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "isNil"));
        Assert.Equal(true, Invoke(asm, "Test", "isNotNil"));
    }

    // === Test 13: Value embedding in class ===

    [Fact]
    public void ValueEmbed_InRefData()
    {
        const string source = """
namespace Test

struct Vec2 {
    var x: int
    var y: int
}

class Entity {
    Vec2
    name: string
}

func getX() -> int {
    var e = Entity()
    e.x = 42
    return e.x
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "getX"));
    }

    // === Test 14: Value embedding — outer method calling promoted method ===

    [Fact]
    public void ValueEmbed_OuterCallsPromoted()
    {
        const string source = """
namespace Test

struct Base {
    var value: int

    func doubled() -> int {
        return self.value * 2
    }
}

struct Wrapper {
    Base

    func quadrupled() -> int {
        return self.doubled() * 2
    }
}

func getQuad() -> int {
    var w = Wrapper { value: 5 }
    return w.quadrupled()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(20, Invoke(asm, "Test", "getQuad"));
    }

    // ── Additional coverage ──

    [Fact]
    public void Embedded_Field_Read_Returns_Inner_Value()
    {
        const string source = """
namespace Test

struct Inner { value: int }

struct Outer {
    pub Inner
}

func test() -> int {
    let o = Outer { Inner: Inner { value: 42 } }
    return o.value
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    // === added: embedding + new + promoted access ===

    [Fact]
    public void Added_ValueEmbed_PromotedField()
    {
        var asm = CompileAndLoad("""
namespace Test

struct Vec2 { var x: int, var y: int }
struct Transform { Vec2, var scale: int }

func test() -> int {
    var t = Transform { x: 10, y: 20, scale: 2 }
    t.x += 5
    return t.x + t.y    // 15 + 20
}
""");
        Assert.Equal(35, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Added_ValueEmbed_PromotedMethod()
    {
        var asm = CompileAndLoad("""
namespace Test

struct Vec2 { var x: int, var y: int }
func (v: Vec2) magnitude() -> int = v.x * v.x + v.y * v.y
struct Transform { Vec2, var scale: int }

func test() -> int {
    let t = Transform { x: 3, y: 4, scale: 1 }
    return t.magnitude()
}
""");
        Assert.Equal(25, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Added_PointerEmbed_NewConstruction()
    {
        var asm = CompileAndLoad("""
namespace Test

struct Vec2 { var x: int, var y: int }
struct Entity { *Vec2, name: string }

func test() -> int {
    var e = new Entity { Vec2: new Vec2 { x: 10, y: 20 }, name: "p" }
    e.x += 12
    return e.x + e.y
}
""");
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Added_ValueEmbed_DirectAccessByName()
    {
        var asm = CompileAndLoad("""
namespace Test

struct Vec2 { var x: int, var y: int }
struct Transform { Vec2, var scale: int }

func test() -> int {
    let t = Transform { x: 7, y: 0, scale: 1 }
    let v = t.Vec2
    return v.x
}
""");
        Assert.Equal(7, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Added_OuterFieldShadowsPromoted()
    {
        var asm = CompileAndLoad("""
namespace Test

struct Base { tag: int }
struct Wrap { Base, tag: int }

func test() -> int {
    let w = Wrap { Base: Base { tag: 1 }, tag: 99 }
    return w.tag
}
""");
        Assert.Equal(99, Invoke(asm, "Test", "test"));
    }

    [Theory]
    [InlineData("t.x", 3)]
    [InlineData("t.y", 4)]
    [InlineData("t.x + t.y", 7)]
    public void Added_PromotedFieldRead(string expr, int expected)
    {
        var asm = CompileAndLoad($$"""
namespace Test

struct Vec2 { var x: int, var y: int }
struct Transform { Vec2, var scale: int }

func test() -> int {
    let t = Transform { x: 3, y: 4, scale: 1 }
    return {{expr}}
}
""");
        Assert.Equal(expected, Invoke(asm, "Test", "test"));
    }
}
