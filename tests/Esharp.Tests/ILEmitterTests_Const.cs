using System.Reflection;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Mono.Cecil;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

public sealed class ILEmitterTests_Const
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    static int _asmCounter;

    static (Assembly asm, AssemblyDefinition cecil) CompileAndLoadWithCecil(string source)
    {
        var asmName = $"ConstTest_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);
        var (cecilAsm, diags) = EsHarness.EmitBound(binder, bound, asmName);
        Assert.Empty(diags);
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

    static object? Invoke(Assembly asm, string typeName, string methodName, params object?[] args)
    {
        var type = asm.GetType($"Test.{typeName}")
            ?? throw new Exception($"Type Test.{typeName} not found");
        var method = type.GetMethod(methodName, AnyStatic)
            ?? throw new Exception($"Method {methodName} not found on {typeName}");
        return method.Invoke(null, args);
    }

    // 1. Top-level int const, used inside a func.
    [Fact]
    public void Toplevel_Const_Int_Used_In_Func()
    {
        const string source = """
namespace Test

const MAX = 1024

func test() -> int = MAX
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(1024, Invoke(asm, "Test", "test"));
    }

    // 2. Top-level string const.
    [Fact]
    public void Toplevel_Const_String()
    {
        const string source = """
namespace Test

const GREETING = "hello"

func test() -> string = GREETING
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("hello", Invoke(asm, "Test", "test"));
    }

    // 3. Top-level double const.
    [Fact]
    public void Toplevel_Const_Double()
    {
        const string source = """
namespace Test

const PI = 3.14

func test() -> double = PI
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3.14, Invoke(asm, "Test", "test"));
    }

    // 4. Top-level bool const.
    [Fact]
    public void Toplevel_Const_Bool()
    {
        const string source = """
namespace Test

const ENABLED = true

func test() -> bool = ENABLED
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test"));
    }

    // 5. Top-level const with explicit type annotation.
    [Fact]
    public void Toplevel_Const_With_Type_Annotation()
    {
        const string source = """
namespace Test

const N: int = 42

func test() -> int = N
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    // 6. Top-level const folded from arithmetic.
    [Fact]
    public void Toplevel_Const_Folded_Arithmetic()
    {
        const string source = """
namespace Test

const SUM = 10 + 20 + 30

func test() -> int = SUM
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(60, Invoke(asm, "Test", "test"));
    }

    // 7. Top-level const used in arithmetic expression.
    [Fact]
    public void Toplevel_Const_In_Arithmetic()
    {
        const string source = """
namespace Test

const BASE = 100

func test(x: int) -> int = BASE + x
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(105, Invoke(asm, "Test", "test", 5));
    }

    // 8. Top-level const used in loop bound.
    [Fact]
    public void Toplevel_Const_In_Loop_Bound()
    {
        const string source = """
namespace Test

const COUNT = 5

func test() -> int {
    var total = 0
    for i in 0..COUNT {
        total = total + i
    }
    return total
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(10, Invoke(asm, "Test", "test"));
    }

    // 9. Negative const.
    [Fact]
    public void Toplevel_Const_Negative()
    {
        const string source = """
namespace Test

const NEG = -7

func test() -> int = NEG
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(-7, Invoke(asm, "Test", "test"));
    }

    // 10. Public const emits as CLR public literal field for C# interop.
    [Fact]
    public void Pub_Toplevel_Const_Emits_Public_Literal_Field()
    {
        const string source = """
namespace Test

pub const VERSION = 7

func test() -> int = VERSION
""";
        var (asm, cecil) = CompileAndLoadWithCecil(source);
        var moduleClass = cecil.MainModule.Types.First(t => t.Name == "Test" && t.Namespace == "Test");
        var versionField = moduleClass.Fields.First(f => f.Name == "VERSION");
        Assert.True(versionField.IsPublic);
        Assert.True(versionField.IsLiteral);
        Assert.True(versionField.IsStatic);
        Assert.Equal(7, versionField.Constant);
        cecil.Dispose();
        Assert.Equal(7, Invoke(asm, "Test", "test"));
    }

    // 11. Func-body const, used in same function.
    [Fact]
    public void Body_Const_Used_Locally()
    {
        const string source = """
namespace Test

func test() -> int {
    const X = 21
    return X * 2
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(42, Invoke(asm, "Test", "test"));
    }

    // 12. Func-body const folded from arithmetic.
    [Fact]
    public void Body_Const_Folded_Arithmetic()
    {
        const string source = """
namespace Test

func test() -> int {
    const A = 3
    const B = 4
    return A * B + A + B
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(19, Invoke(asm, "Test", "test"));
    }

    // 13. Body const shadowing same name in nested scope.
    [Fact]
    public void Body_Const_Shadowed_In_Inner_Scope()
    {
        const string source = """
namespace Test

func test() -> int {
    const X = 1
    var outer = X
    if true {
        const X = 100
        outer = outer + X
    }
    return outer
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(101, Invoke(asm, "Test", "test"));
    }

    // 14. Body const used as `for` range start.
    [Fact]
    public void Body_Const_In_For_Range_Start()
    {
        const string source = """
namespace Test

func test() -> int {
    const FROM = 3
    var sum = 0
    for i in FROM..6 {
        sum = sum + i
    }
    return sum
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(12, Invoke(asm, "Test", "test"));
    }

    // 15. Const in `static func` block (treated like `let X = literal`).
    [Fact]
    public void Static_Func_Const_Block_Member()
    {
        const string source = """
namespace Test

static Caps {
    const MAX_USERS = 50
    func limit() -> int = MAX_USERS
}

func test() -> int = Caps.limit()
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(50, Invoke(asm, "Test", "test"));
    }

    // 16. Const inside `class` body.
    [Fact]
    public void RefData_Const_Member()
    {
        const string source = """
namespace Test

class Config {
    const VERSION = 3
    init() {}
    func version() -> int = self.VERSION
}

func test() -> int {
    let c = Config()
    return c.version()
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(3, Invoke(asm, "Test", "test"));
    }

    // 17. Top-level const cross-referenced from another const.
    [Fact]
    public void Toplevel_Const_References_Another_Const()
    {
        const string source = """
namespace Test

const A = 5
const B = A * 2

func test() -> int = B
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(10, Invoke(asm, "Test", "test"));
    }

    // 18. Body const used in a return expression.
    [Fact]
    public void Body_Const_In_Return_Expression()
    {
        const string source = """
namespace Test

func test(n: int) -> int {
    const SCALE = 10
    return n * SCALE
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(70, Invoke(asm, "Test", "test", 7));
    }

    // 19. Boolean expression with consts.
    [Fact]
    public void Toplevel_Const_Bool_Expression()
    {
        const string source = """
namespace Test

const ON = true
const OFF = false

func test() -> bool = ON and not OFF
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "test"));
    }

    // 20. String concat folded.
    [Fact]
    public void Toplevel_Const_String_Concat_Folded()
    {
        const string source = """
namespace Test

const PREFIX = "hello, "
const NAME = "world"
const GREETING = PREFIX + NAME

func test() -> string = GREETING
""";
        var asm = CompileAndLoad(source);
        Assert.Equal("hello, world", Invoke(asm, "Test", "test"));
    }

    // 21. Body const promoted to an arg of another function.
    [Fact]
    public void Body_Const_Passed_As_Arg()
    {
        const string source = """
namespace Test

func triple(x: int) -> int = x * 3

func test() -> int {
    const N = 7
    return triple(N)
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(21, Invoke(asm, "Test", "test"));
    }

    // 22. Top-level const used as comparison RHS.
    [Fact]
    public void Toplevel_Const_In_Comparison()
    {
        const string source = """
namespace Test

const THRESHOLD = 100

func is_big(n: int) -> bool = n > THRESHOLD
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(true, Invoke(asm, "Test", "is_big", 200));
        Assert.Equal(false, Invoke(asm, "Test", "is_big", 50));
    }

    // 23. Bare namespace-scope `let` / `var` — Go-style package-level state, declared
    // A namespace const referenced from a `class` *instance method* body. Instance
    // method bodies bind in Pass 4 (inside BindData), earlier than the source-order
    // const slot — and unlike free functions they have no module-static fallback in
    // the emitter, so before consts were folded ahead of all bodies (Pass 1.9) this
    // surfaced as `IL: undefined variable`. This is the gap the E# language server's
    // own test fixtures (top-level const URIs read inside `[Fact]` methods) hit.
    [Fact]
    public void Toplevel_Const_Used_In_RefData_InstanceMethod()
    {
        const string source = """
namespace Test

const GREETING = "hello"

pub class Box {
    pub func get() -> string = GREETING
}
""";
        var asm = CompileAndLoad(source);
        var boxType = asm.GetType("Test.Box")!;
        var box = Activator.CreateInstance(boxType, nonPublic: true)!;
        Assert.Equal("hello", boxType.GetMethod("get")!.Invoke(box, null));
    }

    // with no enclosing func or type body. Target behavior: they become static members
    // of the namespace host class (a `static partial class`) — `let` → `static readonly`
    // field, `var` → `static` field — so a `let` is read-only and a `var` is mutable and
    // persists across calls. Not yet parsed: the parser rejects a bare `let`/`var` at
    // namespace scope with ES0001 (only `const` is accepted there today). This pins the
    // intended behavior for when it lands.
    [Fact(Skip = "Namespace-scope `let`/`var` (Go-style package state) not yet parsed — only `const` is accepted at namespace scope today (ES0001 on `let`/`var`).")]
    public void Toplevel_LetAndVar_AreModuleState()
    {
        const string source = """
namespace Test

let BASE = 100
var counter = 5

func bump() -> int {
    counter = counter + 1
    return BASE + counter
}
""";
        var asm = CompileAndLoad(source);
        Assert.Equal(106, Invoke(asm, "Test", "bump"));   // BASE + (5 → 6)
        Assert.Equal(107, Invoke(asm, "Test", "bump"));   // var persists across calls (6 → 7)
    }
}
