using System.Reflection;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Mono.Cecil;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

public sealed class ILEmitterTests_TaskFunc
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static int _asmCounter;

    static (Assembly Asm, IReadOnlyList<Esharp.Diagnostics.Diagnostic> Diagnostics) Compile(string source)
    {
        var asmName = $"EsharpTask_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        var allDiags = parser.Diagnostics.Concat(binder.Diagnostics).ToList();

        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        var ilDiags = EsHarness.EmitBoundToFile(binder, bound, asmName, path);
        allDiags.AddRange(ilDiags);
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

    // ---- Declaration / parsing ----

    [Fact]
    public void TaskFunc_Void_Parses_And_Emits_Spawned_Return()
    {
        var (asm, diags) = Compile("""
namespace Test

task func ping() { }
""");
        Assert.Empty(diags);
        var m = InspectModule(asm).Types.First(t => t.Name == "Test")
            .Methods.First(x => x.Name == "ping");
        Assert.Equal("Esharp.Stdlib.Spawned", m.ReturnType.FullName);
    }

    [Fact]
    public void TaskFunc_Typed_Parses_And_Emits_SpawnedT_Return()
    {
        var (asm, diags) = Compile("""
namespace Test

task func produce() -> int { return 42 }
""");
        Assert.Empty(diags);
        var m = InspectModule(asm).Types.First(t => t.Name == "Test")
            .Methods.First(x => x.Name == "produce");
        Assert.StartsWith("Esharp.Stdlib.Spawned`1", m.ReturnType.FullName);
        Assert.Contains("Int32", m.ReturnType.FullName);
    }

    [Fact]
    public void TaskFunc_Synthesizes_Hidden_Body_Method()
    {
        var (asm, _) = Compile("""
namespace Test

task func produce() -> int { return 99 }
""");
        var t = InspectModule(asm).Types.First(t => t.Name == "Test");
        Assert.Contains(t.Methods, m => m.Name == "__taskfunc_body_produce" && m.IsPrivate);
    }

    [Fact]
    public void TaskFunc_Inner_Body_Has_User_Return_Type()
    {
        var (asm, _) = Compile("""
namespace Test

task func produce() -> int { return 99 }
""");
        var inner = InspectModule(asm).Types.First(t => t.Name == "Test")
            .Methods.First(m => m.Name == "__taskfunc_body_produce");
        Assert.Equal("System.Int32", inner.ReturnType.FullName);
    }

    [Fact]
    public void TaskFunc_Inner_Body_Has_Void_Return_For_Void_Decl()
    {
        var (asm, _) = Compile("""
namespace Test

task func ping() { }
""");
        var inner = InspectModule(asm).Types.First(t => t.Name == "Test")
            .Methods.First(m => m.Name == "__taskfunc_body_ping");
        Assert.Equal("System.Void", inner.ReturnType.FullName);
    }

    // ---- Runtime behaviour ----

    [Fact]
    public void TaskFunc_Returns_Job_Of_Int_That_Waits_To_42()
    {
        var (asm, diags) = Compile("""
namespace Test

task func produce() -> int { return 42 }
""");
        Assert.Empty(diags);
        var job = Invoke(asm, "Test", "produce");
        Assert.NotNull(job);
        var wait = job!.GetType().GetMethod("Wait", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!;
        var result = wait.Invoke(job, null);
        Assert.Equal(42, result);
    }

    [Fact]
    public void TaskFunc_Void_Returns_Spawned_That_Completes()
    {
        var (asm, diags) = Compile("""
namespace Test

task func ping() { }
""");
        Assert.Empty(diags);
        var spawned = Invoke(asm, "Test", "ping");
        Assert.Equal("Esharp.Stdlib.Spawned", spawned!.GetType().FullName);
        var wait = spawned.GetType().GetMethod("Wait", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!;
        wait.Invoke(spawned, null); // should not throw
    }

    [Fact]
    public void TaskFunc_Returns_String_Job()
    {
        var (asm, diags) = Compile("""
namespace Test

task func tag() -> string { return "ok" }
""");
        Assert.Empty(diags);
        var job = Invoke(asm, "Test", "tag");
        var wait = job!.GetType().GetMethod("Wait", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!;
        Assert.Equal("ok", wait.Invoke(job, null));
    }

    [Fact]
    public void TaskFunc_Returns_Boolean_Job()
    {
        var (asm, diags) = Compile("""
namespace Test

task func check() -> bool { return true }
""");
        Assert.Empty(diags);
        var job = Invoke(asm, "Test", "check");
        var wait = job!.GetType().GetMethod("Wait", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!;
        Assert.Equal(true, wait.Invoke(job, null));
    }

    [Fact]
    public void TaskFunc_Body_Computes_Arithmetic()
    {
        var (asm, diags) = Compile("""
namespace Test

task func sum() -> int {
    let a = 3
    let b = 4
    return a * b + 1
}
""");
        Assert.Empty(diags);
        var job = Invoke(asm, "Test", "sum");
        var wait = job!.GetType().GetMethod("Wait", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!;
        Assert.Equal(13, wait.Invoke(job, null));
    }

    [Fact]
    public void TaskFunc_Call_Site_Returns_Job_Typed_Result_To_Caller()
    {
        var (asm, diags) = Compile("""
namespace Test

task func produce() -> int { return 5 }

func caller() -> int {
    let j = produce()
    return j.Wait()
}
""");
        Assert.Empty(diags);
        Assert.Equal(5, Invoke(asm, "Test", "caller"));
    }

    [Fact]
    public void TaskFunc_Wrapper_Invokes_Inner()
    {
        var (asm, _) = Compile("""
namespace Test

task func produce() -> int { return 99 }
""");
        var t = InspectModule(asm).Types.First(t => t.Name == "Test");
        var wrapper = t.Methods.First(m => m.Name == "produce");
        // The wrapper should ldftn the inner body — verify by string-matching the
        // operand on a Ldftn instruction.
        var hasLdftnInner = wrapper.Body.Instructions.Any(i =>
            i.OpCode == Mono.Cecil.Cil.OpCodes.Ldftn
            && (i.Operand?.ToString() ?? "").Contains("__taskfunc_body_produce"));
        Assert.True(hasLdftnInner, "wrapper must ldftn the inner body");
    }

    [Fact]
    public void TaskFunc_Wrapper_Calls_Stdlib_SpawnedOps_Spawn()
    {
        var (asm, _) = Compile("""
namespace Test

task func produce() -> int { return 7 }
""");
        var t = InspectModule(asm).Types.First(t => t.Name == "Test");
        var wrapper = t.Methods.First(m => m.Name == "produce");
        var hasSpawn = wrapper.Body.Instructions.Any(i =>
            i.OpCode == Mono.Cecil.Cil.OpCodes.Call
            && (i.Operand?.ToString() ?? "").Contains("Esharp.Stdlib.SpawnedOps::Spawn"));
        Assert.True(hasSpawn, "wrapper must call SpawnedOps.Spawn");
    }

    // ---- ES2130: var-capture-across-task-boundary ----

    [Fact]
    public void TaskFunc_Var_Capture_In_Function_Literal_Reports_ES2130()
    {
        var (_, diags) = Compile("""
namespace Test

task func go() {
    var counter = 0
    let bump = func() { counter = counter + 1 }
    bump()
}
""");
        Assert.Contains(diags, d => d.Message.Contains("ES2130"));
    }

    [Fact]
    public void TaskFunc_Let_Capture_In_Function_Literal_Is_OK()
    {
        var (_, diags) = Compile("""
namespace Test

task func go() {
    let value = 7
    let read = func() -> int { return value }
    let v = read()
}
""");
        Assert.DoesNotContain(diags, d => d.Message.Contains("ES2130"));
    }

    [Fact]
    public void RegularFunc_Var_Capture_Is_Not_ES2130()
    {
        // ES2130 applies only inside `task func`, never inside a plain `func`.
        var (_, diags) = Compile("""
namespace Test

func go() {
    var counter = 0
    let bump = func() { counter = counter + 1 }
    bump()
}
""");
        Assert.DoesNotContain(diags, d => d.Message.Contains("ES2130"));
    }

    [Fact]
    public void TaskFunc_Captures_Let_Across_Boundary_Compiles()
    {
        // Sanity: let captures are fine; the program should bind and emit.
        var (asm, diags) = Compile("""
namespace Test

task func produce() -> int {
    let x = 11
    return x
}

func caller() -> int { return produce().Wait() }
""");
        Assert.Empty(diags);
        Assert.Equal(11, Invoke(asm, "Test", "caller"));
    }

    [Fact]
    public void TaskFunc_With_Parameters_CapturesInvocationArguments()
    {
        var (asm, diags) = Compile("""
namespace Test

task func with_arg(n: int) -> int { return n }
func caller() -> int = with_arg(42).Wait()
""");
        Assert.Empty(diags);
        Assert.Equal(42, Invoke(asm, "Test", "caller"));
    }

    [Fact]
    public void TaskFunc_Job_Cancel_Available()
    {
        var (asm, _) = Compile("""
namespace Test

task func produce() -> int { return 1 }
""");
        var job = Invoke(asm, "Test", "produce");
        var cancel = job!.GetType().GetMethod("Cancel", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!;
        cancel.Invoke(job, null); // smoke test
    }

    [Fact]
    public void TaskFunc_Multiple_TaskFuncs_In_Same_File()
    {
        var (asm, diags) = Compile("""
namespace Test

task func two() -> int { return 2 }
task func three() -> int { return 3 }

func caller() -> int { return two().Wait() + three().Wait() }
""");
        Assert.Empty(diags);
        Assert.Equal(5, Invoke(asm, "Test", "caller"));
    }

    [Fact]
    public void Task_Is_Still_Valid_Identifier_Elsewhere()
    {
        var (asm, diags) = Compile("""
namespace Test

func go(task: int) -> int { return task + 1 }
""");
        Assert.Empty(diags);
        Assert.Equal(11, Invoke(asm, "Test", "go", 10));
    }

    // ── Additional coverage ──

    [Fact]
    public void TaskFunc_Two_Independent_Tasks_Wait_Independently()
    {
        var (asm, diags) = Compile("""
namespace Test

task func one() -> int { return 100 }
task func two() -> int { return 200 }

func caller() -> int = one().Wait() + two().Wait()
""");
        Assert.Empty(diags);
        Assert.Equal(300, Invoke(asm, "Test", "caller"));
    }

    [Fact]
    public void TaskFunc_Spawned_Type_Is_Stdlib_Spawned_Of_T()
    {
        var (asm, diags) = Compile("""
namespace Test

task func produce() -> int { return 7 }
""");
        Assert.Empty(diags);
        var spawned = Invoke(asm, "Test", "produce");
        Assert.NotNull(spawned);
        Assert.StartsWith("Esharp.Stdlib.Spawned`1", spawned!.GetType().FullName);
    }

    [Fact]
    public void TaskFunc_Multiple_Awaits_Sum_To_Expected()
    {
        var (asm, diags) = Compile("""
namespace Test

task func first() -> int { return 10 }
task func second() -> int { return 20 }
task func third() -> int { return 30 }

func caller() -> int = first().Wait() + second().Wait() + third().Wait()
""");
        Assert.Empty(diags);
        Assert.Equal(60, Invoke(asm, "Test", "caller"));
    }
}
