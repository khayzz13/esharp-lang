using Esharp.BoundTree;
using Esharp.Lowering;
using Esharp.Syntax.Parsing;

namespace Esharp.Tests;

/// <summary>
/// State-machine liveness regressions.  These are deliberately metadata-first:
/// a function can return the right value while still retaining unnecessary
/// locals in its async struct (or retaining the wrong shadowed binding).
/// </summary>
public sealed class AsyncLivenessCorrectnessTests
{
    [Fact]
    public void DeadLocalBeforeAwait_IsNotHoistedIntoStateMachine()
    {
        var (assembly, diagnostics) = EsHarness.EmitCecil("""
namespace Test

func compute() -> Task<int> {
    dead: int = 41
    await Task.Delay(1)
    return 1
}
""", "AsyncDeadLocal");

        try
        {
            Assert.Empty(diagnostics.Where(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error));
            var host = assembly.MainModule.Types.Single(type => type.Name == "Test");
            var machine = host.NestedTypes.Single(type => type.Name.Contains("StateMachine", StringComparison.Ordinal));
            Assert.DoesNotContain(machine.Fields, field => field.Name == "__async_state_dead");
        }
        finally
        {
            assembly.Dispose();
        }
    }

    [Fact]
    public void OuterBindingAfterShadowingAndAwait_RemainsTheBindingThatIsRead()
    {
        var result = EsHarness.Await(EsHarness.Run("""
namespace Test

func compute() -> Task<int> {
    value: int = 41
    {
        value: int = 0
    }
    await Task.Delay(1)
    return value
}
""", "compute"));

        Assert.Equal(41, result);
    }

    [Fact]
    public void LiveLocalAfterAwait_IsHoistedAndRestored()
    {
        var (assembly, diagnostics) = EsHarness.EmitCecil("""
namespace Test

func compute() -> Task<int> {
    retained: int = 40
    await Task.Delay(1)
    return retained + 2
}
""", "AsyncLiveLocal");

        try
        {
            Assert.Empty(diagnostics.Where(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error));
            var host = assembly.MainModule.Types.Single(type => type.Name == "Test");
            var machine = host.NestedTypes.Single(type => type.Name.Contains("StateMachine", StringComparison.Ordinal));
            Assert.Contains(machine.Fields, field => field.Name == "__async_state_retained");
        }
        finally
        {
            assembly.Dispose();
        }
    }

    [Fact]
    public void ReadonlyAddressTakenAfterAwait_HoistsTheAddressedLetLocal()
    {
        var (assembly, diagnostics) = EsHarness.EmitCecil("""
namespace Test

func observe(value: readonly *int) -> int { return value }

func compute() -> Task<int> {
    let retained: int = 42
    await Task.Delay(1)
    return observe(&retained)
}
""", "AsyncReadonlyAddressLiveness");

        try
        {
            Assert.Empty(diagnostics.Where(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error));
            var host = assembly.MainModule.Types.Single(type => type.Name == "Test");
            var machine = host.NestedTypes.Single(type => type.Name.Contains("StateMachine", StringComparison.Ordinal));
            Assert.Contains(machine.Fields, field => field.Name == "__async_state_retained");
            Assert.DoesNotContain(machine.Fields, field => field.FieldType.IsByReference);
        }
        finally
        {
            assembly.Dispose();
        }

        var result = EsHarness.Await(EsHarness.Run("""
namespace Test

func observe(value: readonly *int) -> int { return value }

func compute() -> Task<int> {
    let retained: int = 42
    await Task.Delay(1)
    return observe(&retained)
}
""", "compute"));
        Assert.Equal(42, result);
    }

    [Fact]
    public void PointerAliasCreatedBeforeAwait_IsRaisedToDurableState()
    {
        var assembly = EsHarness.Compile("""
namespace Test
using "System.Threading.Tasks"

struct Cell { var value: int }

func compute() -> Task<int> {
    var cell = Cell { value: 1 }
    var pointer = &cell
    await Task.Delay(1)
    pointer.value = 42
    return pointer.value
}
""", "AsyncPointerAliasLiveness");

        Assert.Equal(42, EsHarness.Await(EsHarness.Invoke(assembly, "compute")));

        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var machine = assembly.GetTypes().Single(type => type.Name.Contains("StateMachine", StringComparison.Ordinal));
        Assert.Contains(machine.GetFields(flags), field => field.Name == "__async_state_pointer"
            && field.FieldType.Name.Contains("__Ptr_Cell", StringComparison.Ordinal));
        Assert.DoesNotContain(machine.GetFields(flags), field => field.FieldType.IsByRef);
    }

    [Fact]
    public void PointerAliasChainAcrossAwait_PreservesOneDurableCellForEveryAlias()
    {
        var assembly = EsHarness.Compile("""
namespace Test
using "System.Threading.Tasks"

struct Cell { var value: int }

func compute() -> Task<int> {
    var cell = Cell { value: 41 }
    var first = &cell
    var live = first
    await Task.Delay(1)
    live.value += 1
    return cell.value
}
""", "AsyncPointerAliasChain");

        Assert.Equal(42, EsHarness.Await(EsHarness.Invoke(assembly, "compute")));
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var machine = assembly.GetTypes().Single(type => type.Name.Contains("StateMachine", StringComparison.Ordinal));
        Assert.Contains(machine.GetFields(flags), field => field.Name == "__async_state_cell"
            && field.FieldType.Name.Contains("__Ptr_Cell", StringComparison.Ordinal));
        Assert.Contains(machine.GetFields(flags), field => field.Name == "__async_state_live"
            && field.FieldType.Name.Contains("__Ptr_Cell", StringComparison.Ordinal));
        Assert.DoesNotContain(machine.GetFields(flags), field => field.FieldType.IsByRef);
    }

    [Fact]
    public void PointerParameterLiveAcrossAwait_RaisesCallerAddressToOneDurableCell()
    {
        var assembly = EsHarness.Compile("""
namespace Test
using "System.Threading.Tasks"

struct Cell { var value: int }

func bump(cell: *Cell) -> Task<int> {
    await Task.Delay(1)
    cell.value += 1
    return cell.value
}

func observe() -> Task<int> {
    var cell = Cell { value: 41 }
    let result = await bump(&cell)
    return cell.value * 100 + result
}
""", "AsyncPointerParameterLiveness");

        Assert.Equal(4242, EsHarness.Await(EsHarness.Invoke(assembly, "observe")));

        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var machine = assembly.GetTypes().Single(type => type.Name.Contains("bump") && type.Name.Contains("StateMachine", StringComparison.Ordinal));
        Assert.Contains(machine.GetFields(flags), field => field.Name == "cell"
            && field.FieldType.Name.Contains("__Ptr_Cell", StringComparison.Ordinal));
        Assert.DoesNotContain(machine.GetFields(flags), field => field.FieldType.IsByRef);
    }

    [Fact]
    public void AwaitedDurablePointerCall_HoistsTheCallerCarrierNotACopiedPointee()
    {
        var (assembly, diagnostics) = EsHarness.EmitCecil("""
namespace Test
using "System.Threading.Tasks"

struct Cell { var value: int }

func bump(cell: *Cell) -> Task<int> {
    await Task.Delay(1)
    cell.value += 1
    return cell.value
}

func observe() -> Task<int> {
    var cell = Cell { value: 41 }
    let result = await bump(&cell)
    return cell.value * 100 + result
}
""", "AsyncPointerCallerCarrier");

        try
        {
            Assert.Empty(diagnostics.Where(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error));
            var host = assembly.MainModule.Types.Single(type => type.Name == "Test");
            var machine = host.NestedTypes.Single(type => type.Name.Contains("observe", StringComparison.Ordinal)
                && type.Name.Contains("StateMachine", StringComparison.Ordinal));
            var stateCell = Assert.Single(machine.Fields.Where(field => field.Name == "__async_state_cell"));
            Assert.Contains("__Ptr_Cell", stateCell.FieldType.Name, StringComparison.Ordinal);
            Assert.NotEqual("Cell", stateCell.FieldType.Name);
            Assert.False(stateCell.FieldType.IsByReference);
        }
        finally
        {
            assembly.Dispose();
        }
    }

    [Fact]
    public void SyncFuturePointerCall_PreservesTheDurableCarrierUntilItsImplicitJoin()
    {
        const string source = """
namespace Test
using "System.Threading.Tasks"

struct Cell { var value: int }

func bump(cell: *Cell) -> int {
    await Task.Delay(1)
    cell.value += 1
    return cell.value
}

func observe() -> int {
    var cell = Cell { value: 41 }
    let result = bump(&cell)
    return cell.value * 100 + result
}
""";

        var (assembly, diagnostics) = EsHarness.EmitCecil(source, "SyncFutureDurablePointer");
        try
        {
            Assert.Empty(diagnostics.Where(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error));
            var host = assembly.MainModule.Types.Single(type => type.Name == "Test");
            var bump = host.Methods.Single(method => method.Name == "bump");
            Assert.Contains("__Ptr_Cell", bump.Parameters.Single().ParameterType.Name, StringComparison.Ordinal);
            var observe = host.Methods.Single(method => method.Name == "observe");
            Assert.Contains(observe.Body.Variables, variable => variable.VariableType.Name.Contains("__Ptr_Cell", StringComparison.Ordinal));
            Assert.Contains(observe.Body.Instructions, instruction => instruction.Operand is Mono.Cecil.MethodReference
                { Name: "GetResult" });
        }
        finally
        {
            assembly.Dispose();
        }

        Assert.Equal(4242, EsHarness.Run(source, "observe"));
    }

    [Fact]
    public void AwaitInsideBranch_PreservesOuterLocalUsedAfterTheBranch()
    {
        var result = EsHarness.Await(EsHarness.Run("""
namespace Test

func compute(flag: bool) -> Task<int> {
    total: int = 40
    if flag {
        await Task.Delay(1)
    }
    return total + 2
}
""", "compute", true));

        Assert.Equal(42, result);
    }

    [Fact]
    public void AwaitInsideLoop_PreservesLocalForTheNextIteration()
    {
        var result = EsHarness.Await(EsHarness.Run("""
namespace Test

func compute() -> Task<int> {
    total: int = 40
    while total < 42 {
        await Task.Delay(1)
        total = total + 1
    }
    return total
}
""", "compute"));

        Assert.Equal(42, result);
    }

    [Fact]
    public void AsyncSpillTemporaryInsideTry_IsHoistedIntoStateMachine()
    {
        const string source = """
namespace Test

func compute() -> Task<int> {
    total: int = 1
    try {
        total = total + (await Task.FromResult(5))
    } catch (e: Exception) {
        total = -1
    }
    return total
}
""";

        var parser = new Parser(source, "async-spill-try.es");
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(parser.ParseCompilationUnit());
        var sourceFunction = Assert.IsType<BoundFunctionDeclaration>(
            bound.Members.Single(member => member is BoundFunctionDeclaration { Name: "compute" }));
        var sourceTry = Assert.IsType<BoundTryStatement>(sourceFunction.Body.Statements
            .Single(statement => statement is BoundTryStatement));
        Assert.Contains(sourceTry.Body.Statements.OfType<BoundVariableDeclaration>(),
            variable => variable.Name == "__spill_0");
        Assert.Contains(sourceTry.Body.Statements.OfType<BoundVariableDeclaration>(),
            variable => variable.Name == "__spill_1");
        var sourceAnalysis = AwaitPointAnalyzer.Analyze(sourceFunction.Body);
        Assert.Contains(sourceAnalysis.SpilledLocals, local => local.Name == "__spill_0");
        Assert.Contains(sourceAnalysis.SpilledLocals, local => local.Name == "__spill_1");

        var program = EsHarness.BindAndLower(source);

        Assert.Empty(program.Diagnostics.Where(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error));
        var machine = program.Units.Single().Members.OfType<BoundDataDeclaration>()
            .Single(type => type.Name.Contains("StateMachine", StringComparison.Ordinal));
        Assert.Contains(machine.Fields, field => field.Name == "__async_state___spill_0");
        Assert.Contains(machine.Fields, field => field.Name == "__async_state___spill_1");
    }

    [Fact]
    public void DeferredAsyncRegion_RetainsItsResultBindingThroughLowering()
    {
        var program = EsHarness.BindAndLower("""
namespace Test

func work() -> int {
    defer { var dropped = 0 }
    let value = await Task.FromResult(41)
    return value + 1
}
""");

        Assert.Empty(program.Diagnostics.Where(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error));
        var machine = program.Units.Single().Members.OfType<BoundDataDeclaration>()
            .Single(type => type.Name.Contains("StateMachine", StringComparison.Ordinal));
        var moveNext = machine.InstanceMethods.Single(method => method.Name == "MoveNext");
        var locals = new LocalCollector();
        locals.VisitNode(moveNext.Body);
        Assert.Contains("value", locals.Declarations);
        Assert.Contains("value", locals.Names);

        var result = EsHarness.Await(EsHarness.Run("""
namespace Test

func work() -> int {
    defer { var dropped = 0 }
    let value = await Task.FromResult(41)
    return value + 1
}
""", "work"));
        Assert.Equal(42, result);
    }

    sealed class LocalCollector : BoundTreeVisitor
    {
        public List<string> Declarations { get; } = [];
        public List<string> Names { get; } = [];

        protected override void VisitVariableDeclaration(BoundVariableDeclaration node)
        {
            Declarations.Add(node.Name);
            base.VisitVariableDeclaration(node);
        }

        protected override void VisitNameExpression(BoundNameExpression node)
        {
            Names.Add(node.Name);
            base.VisitNameExpression(node);
        }
    }
}
