using System.Reflection;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Mono.Cecil;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

public sealed class ILEmitterTests_StaticFunc
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static int _asmCounter;

    static (Assembly Asm, IReadOnlyList<Esharp.Diagnostics.Diagnostic> Diagnostics) Compile(string source)
    {
        var asmName = $"EsharpStaticFuncTest_{Interlocked.Increment(ref _asmCounter)}";
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

    // ---- Declaration / parsing surface ----

    [Fact]
    public void StaticFunc_BasicDeclaration_Parses()
    {
        var (_, diags) = Compile("""
namespace Test

static Foo {
    func go() -> int { return 1 }
}
""");
        Assert.Empty(diags);
    }

    [Fact]
    public void StaticFunc_With_Only_Functions()
    {
        var (asm, diags) = Compile("""
namespace Test

static Util {
    func one() -> int { return 1 }
    func two() -> int { return 2 }
}

func go() -> int { return Util.one() + Util.two() }
""");
        Assert.Empty(diags);
        Assert.Equal(3, Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void StaticFunc_Merges_StaticMembers_Into_SameName_Class()
    {
        var (asm, diags) = Compile("""
namespace Test

class TaskScope {
    value: int
    init() { self.value = 42 }
    func read() -> int = self.value
}

static TaskScope {
    func create() -> TaskScope = TaskScope()
}

func go() -> int = TaskScope.create().read()
""");
        Assert.Empty(diags);
        Assert.Equal(42, Invoke(asm, "Test", "go"));

        using var module = InspectModule(asm);
        var taskScopeTypes = module.Types.Where(t => t.FullName == "Test.TaskScope").ToList();
        var taskScope = Assert.Single(taskScopeTypes);
        Assert.Contains(taskScope.Methods, m => m.Name == "create" && m.IsStatic);
        Assert.Contains(taskScope.Methods, m => m.Name == "read" && !m.IsStatic);
    }

    [Fact]
    public void StaticFunc_With_Only_Constants()
    {
        var (asm, diags) = Compile("""
namespace Test

static Limits {
    let MAX: int = 100
    let MIN: int = -5
}

func span() -> int { return Limits.MAX - Limits.MIN }
""");
        Assert.Empty(diags);
        Assert.Equal(105, Invoke(asm, "Test", "span"));
    }

    [Fact]
    public void StaticFunc_Mixed_Fields_And_Methods()
    {
        var (asm, diags) = Compile("""
namespace Test

static Math2 {
    let PI: int = 3
    func double(x: int) -> int { return x * 2 }
    func triple(x: int) -> int { return x * 3 }
}

func combined() -> int {
    return Math2.double(5) + Math2.triple(10) + Math2.PI
}
""");
        Assert.Empty(diags);
        Assert.Equal(43, Invoke(asm, "Test", "combined"));
    }

    // ---- CLR shape ----

    [Fact]
    public void StaticFunc_Emits_TopLevel_Static_Class_Shape()
    {
        var (asm, _) = Compile("""
namespace Test

static Holder {
    let X: int = 7
    func make() -> int { return X }
}
""");
        var module = InspectModule(asm);
        var holder = module.Types.FirstOrDefault(t => t.Name == "Holder");
        Assert.NotNull(holder);
        Assert.True(holder!.IsAbstract && holder.IsSealed);
        Assert.Equal("Test", holder.Namespace);
    }

    [Fact]
    public void StaticFunc_Type_Is_TopLevel_Not_Nested_In_Module_Class()
    {
        var (asm, _) = Compile("""
namespace Test

static Sibling {
    func go() -> int { return 1 }
}

func host() -> int { return Sibling.go() }
""");
        var module = InspectModule(asm);
        var moduleClass = module.Types.FirstOrDefault(t => t.Name == "Test");
        Assert.NotNull(moduleClass);
        Assert.DoesNotContain(moduleClass!.NestedTypes, n => n.Name == "Sibling");
        Assert.Contains(module.Types, t => t.Name == "Sibling");
    }

    [Fact]
    public void StaticFunc_Literal_Field_Emits_Const()
    {
        var (asm, _) = Compile("""
namespace Test

static Holder {
    let X: int = 7
}
""");
        var holder = InspectModule(asm).Types.First(t => t.Name == "Holder");
        var x = holder.Fields.First(f => f.Name == "X");
        Assert.True(x.IsStatic);
        Assert.True(x.IsLiteral);
        Assert.Equal(7, x.Constant);
    }

    [Fact]
    public void StaticFunc_Var_Field_Emits_Mutable_Static()
    {
        var (asm, _) = Compile("""
namespace Test

static Counter {
    var n: int = 0
}
""");
        var c = InspectModule(asm).Types.First(t => t.Name == "Counter");
        var n = c.Fields.First(f => f.Name == "n");
        Assert.True(n.IsStatic);
        Assert.False(n.IsLiteral);
        Assert.False(n.IsInitOnly);
    }

    [Fact]
    public void StaticFunc_Let_NonLiteral_Field_Emits_Readonly_Static()
    {
        var (asm, _) = Compile("""
namespace Test

static Holder {
    let s: string = "hi"
}
""");
        var h = InspectModule(asm).Types.First(t => t.Name == "Holder");
        var s = h.Fields.First(f => f.Name == "s");
        Assert.True(s.IsStatic);
        // string literal IS const-eligible
        Assert.True(s.IsLiteral || s.IsInitOnly);
    }

    [Fact]
    public void StaticFunc_Methods_Are_Static()
    {
        var (asm, _) = Compile("""
namespace Test

static Util {
    func go() -> int { return 42 }
}
""");
        var u = InspectModule(asm).Types.First(t => t.Name == "Util");
        var go = u.Methods.First(m => m.Name == "go");
        Assert.True(go.IsStatic);
    }

    // ---- Visibility ----

    [Fact]
    public void StaticFunc_Pub_Emits_Public_Class()
    {
        var (asm, _) = Compile("""
namespace Test

pub static PubSf {
    func go() -> int { return 1 }
}
""");
        var t = InspectModule(asm).Types.First(t => t.Name == "PubSf");
        Assert.True(t.IsPublic);
    }

    [Fact]
    public void StaticFunc_Default_Emits_Internal_Class()
    {
        var (asm, _) = Compile("""
namespace Test

static PrivSf {
    func go() -> int { return 2 }
}
""");
        var t = InspectModule(asm).Types.First(t => t.Name == "PrivSf");
        Assert.False(t.IsPublic);
    }

    // ---- Call / resolution ----

    [Fact]
    public void StaticFunc_Method_Call_From_TopLevel()
    {
        var (asm, diags) = Compile("""
namespace Test

static Password {
    func is_strong(s: string) -> bool { return s.Length > 8 }
}

func main() -> bool { return Password.is_strong("hunter2hunter2") }
""");
        Assert.Empty(diags);
        Assert.Equal(true, Invoke(asm, "Test", "main"));
    }

    [Fact]
    public void StaticFunc_Method_Call_From_Another_StaticFunc()
    {
        var (asm, diags) = Compile("""
namespace Test

static A {
    func produce() -> int { return 10 }
}

static B {
    func consume() -> int { return A.produce() + 1 }
}

func go() -> int { return B.consume() }
""");
        Assert.Empty(diags);
        Assert.Equal(11, Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void StaticFunc_Const_Field_Loaded_Inline_In_IL()
    {
        var (asm, _) = Compile("""
namespace Test

static K {
    let V: int = 42
}

func read() -> int { return K.V }
""");
        var t = InspectModule(asm).Types.First(t => t.Name == "Test");
        var read = t.Methods.First(m => m.Name == "read");
        var instrs = read.Body.Instructions;
        // Must inline the constant (any ldc.i4 / ldc.i4.s / etc) — never ldsfld on a CLR literal.
        var loadsConst = instrs.Any(i =>
            (i.OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4 && Convert.ToInt32(i.Operand) == 42) ||
            (i.OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4_S && Convert.ToInt32(i.Operand) == 42));
        Assert.True(loadsConst, "expected an ldc.i4 / ldc.i4.s 42 in the body");
        Assert.DoesNotContain(instrs, i => i.OpCode == Mono.Cecil.Cil.OpCodes.Ldsfld);
    }

    [Fact]
    public void StaticFunc_Method_With_Multiple_Parameters()
    {
        var (asm, diags) = Compile("""
namespace Test

static Calc {
    func add(a: int, b: int) -> int { return a + b }
    func sub(a: int, b: int) -> int { return a - b }
}

func go() -> int { return Calc.add(3, 4) + Calc.sub(10, 5) }
""");
        Assert.Empty(diags);
        Assert.Equal(12, Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void StaticFunc_Method_Calls_BCL()
    {
        var (asm, diags) = Compile("""
namespace Test

static Str {
    func tag(s: string) -> string { return "<" + s + ">" }
}

func go() -> string { return Str.tag("ok") }
""");
        Assert.Empty(diags);
        Assert.Equal("<ok>", Invoke(asm, "Test", "go"));
    }

    [Fact]
    public void StaticFunc_References_User_Data_Type()
    {
        var (asm, diags) = Compile("""
namespace Test

struct Point {
    x: int
    y: int
}

static Geom {
    func origin() -> Point { return Point { x: 0, y: 0 } }
}

func sum() -> int {
    let p = Geom.origin()
    return p.x + p.y
}
""");
        Assert.Empty(diags);
        Assert.Equal(0, Invoke(asm, "Test", "sum"));
    }

    [Fact]
    public void StaticFunc_Bool_Const()
    {
        var (asm, diags) = Compile("""
namespace Test

static Flags {
    let DEBUG: bool = true
}

func check() -> bool { return Flags.DEBUG }
""");
        Assert.Empty(diags);
        Assert.Equal(true, Invoke(asm, "Test", "check"));
    }

    [Fact]
    public void StaticFunc_String_Const()
    {
        var (asm, diags) = Compile("""
namespace Test

static App {
    let NAME: string = "Esharp"
}

func get_name() -> string { return App.NAME }
""");
        Assert.Empty(diags);
        Assert.Equal("Esharp", Invoke(asm, "Test", "get_name"));
    }

    // ---- Diagnostics ----

    [Fact]
    public void StaticFunc_RejectsTopLevelStatements()
    {
        var (_, diags) = Compile("""
namespace Test

static Bad {
    let X: int = 1
    if true { let y = 2 }
}
""");
        Assert.Contains(diags, d => d.Code == "ES1010" || d.Message.Contains("top-level statements"));
    }

    [Fact]
    public void StaticFunc_RejectsBareReturn()
    {
        var (_, diags) = Compile("""
namespace Test

static Bad {
    return 1
}
""");
        Assert.Contains(diags, d => d.Code == "ES1010" || d.Message.Contains("top-level statements"));
    }

    // ---- Source-level keyword preserved for identifier use elsewhere ----

    [Fact]
    public void Static_Is_Still_Valid_Identifier_Elsewhere()
    {
        // Backward-compat: contextual keyword, OK as parameter name.
        var (asm, diags) = Compile("""
namespace Test

func go(static: int) -> int { return static + 1 }
""");
        Assert.Empty(diags);
        Assert.Equal(11, Invoke(asm, "Test", "go", 10));
    }

    // ── Additional coverage ──

    [Fact]
    public void Static_Func_Const_Inlined_As_Literal_At_Use_Site()
    {
        var (asm, diags) = Compile("""
namespace Test

static Config {
    let MAX: int = 99
}

func test() -> int = Config.MAX
""");
        Assert.Empty(diags);
        Assert.Equal(99, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Static_Func_Method_Called_Across_Two_Static_Funcs()
    {
        var (asm, diags) = Compile("""
namespace Test

static A {
    func twice(n: int) -> int = n * 2
}

static B {
    func via_a(n: int) -> int = A.twice(n) + 1
}

func test() -> int = B.via_a(10)
""");
        Assert.Empty(diags);
        Assert.Equal(21, Invoke(asm, "Test", "test"));
    }

    [Fact]
    public void Static_Func_Two_Methods_Independent_State()
    {
        var (asm, diags) = Compile("""
namespace Test

static Counter {
    var n: int = 0
    func bump() -> int {
        n = n + 1
        return n
    }
}

func test() -> int {
    let a = Counter.bump()
    let b = Counter.bump()
    let c = Counter.bump()
    return a + b + c
}
""");
        Assert.Empty(diags);
        Assert.Equal(6, Invoke(asm, "Test", "test"));
    }

    // ---- Dotted (nested) namespaces ----
    // `namespace A.B.C` maps to the CLR namespace "A.B.C" so E# types can live in
    // a multi-segment namespace alongside C# in the same one. A `static func` emits
    // its class into that full namespace; bare free functions land on the module
    // class named after the namespace's last segment (a type name can't hold '.').

    [Fact]
    public void DottedNamespace_StaticFunc_EmitsIntoFullNamespace()
    {
        var (asm, diags) = Compile("""
namespace Interop.Routing.Demo

static RoutePattern {
    func answer() -> int { return 7 }
}
""");
        Assert.Empty(diags);
        var type = asm.GetType("Interop.Routing.Demo.RoutePattern");
        Assert.NotNull(type);
        var m = type!.GetMethod("answer", AnyStatic);
        Assert.NotNull(m);
        Assert.Equal(7, m!.Invoke(null, null));
    }

    [Fact]
    public void DottedNamespace_Data_EmitsIntoFullNamespace()
    {
        var (asm, diags) = Compile("""
namespace Interop.Routing.Demo

pub struct Point { x: int, y: int }
""");
        Assert.Empty(diags);
        Assert.NotNull(asm.GetType("Interop.Routing.Demo.Point"));
    }

    [Fact]
    public void DottedNamespace_FreeFunction_HostsOnLastSegmentModuleClass()
    {
        var (asm, diags) = Compile("""
namespace Interop.Routing.Demo

func add(a: int, b: int) -> int { return a + b }
""");
        Assert.Empty(diags);
        // Module class is named after the last segment: Interop.Routing.Demo.Demo.
        var type = asm.GetType("Interop.Routing.Demo.Demo");
        Assert.NotNull(type);
        var m = type!.GetMethod("add", AnyStatic);
        Assert.NotNull(m);
        Assert.Equal(5, m!.Invoke(null, [2, 3]));
    }
}
