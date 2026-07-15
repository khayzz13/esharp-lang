using Esharp.BoundTree;
using System.Reflection;

namespace Esharp.Tests;

/// <summary>
/// Pointer realization regressions: source-level <c>*T</c> aliases retain one
/// identity regardless of whether lowering selects a managed borrow or a durable
/// heap carrier.  These tests intentionally cover multi-file, alias, and async
/// boundaries the ordinary pointer model does not exercise.
/// </summary>
public sealed class EscapeLifetimeCorrectnessTests
{
    [Fact]
    public void ReturningAddressThroughLocalAlias_UsesDurablePointerLocal()
    {
        var program = EsHarness.BindAndLower("""
namespace Test

struct Cell { var value: int }

func create() -> *Cell {
    var cell = Cell { value: 7 }
    var pointer = &cell
    return pointer
}
""");

        Assert.Empty(program.Diagnostics.Where(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error));
        var function = Assert.IsType<BoundFunctionDeclaration>(program.Units.Single().Members.Single(member => member is BoundFunctionDeclaration { Name: "create" }));
        var pointer = Assert.Single(function.Body.Statements.OfType<BoundVariableDeclaration>().Where(v => v.Name == "pointer"));
        Assert.IsType<HeapPointerBoundType>(pointer.DeclaredType);
    }

    [Fact]
    public void ReturningAddressThroughLocalAlias_EmitsVerifiedDurableCarrier()
    {
        var assembly = EsHarness.Compile("""
namespace Test

struct Cell { var value: int }

func create() -> *Cell {
    var cell = Cell { value: 7 }
    var pointer = &cell
    return pointer
}

func observe() -> int {
    var pointer = create()
    pointer.value = 13
    return pointer.value
}
""", "PointerAliasReturn");

        Assert.Equal(13, EsHarness.Invoke(assembly, "observe"));
    }

    [Fact]
    public void DurableAliasChain_PromotesEveryAliasOfTheEscapedCell()
    {
        var program = EsHarness.BindAndLower("""
namespace Test

struct Cell { var value: int }

func create() -> *Cell {
    var cell = Cell { value: 7 }
    var first = &cell
    var second = first
    return second
}
""");

        Assert.Empty(program.Diagnostics.Where(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error));
        var function = Assert.IsType<BoundFunctionDeclaration>(program.Units.Single().Members.Single(member => member is BoundFunctionDeclaration { Name: "create" }));
        var aliases = function.Body.Statements.OfType<BoundVariableDeclaration>()
            .Where(variable => variable.Name is "first" or "second")
            .ToList();

        Assert.Equal(2, aliases.Count);
        Assert.All(aliases, alias => Assert.IsType<HeapPointerBoundType>(alias.DeclaredType));
    }

    [Fact]
    public void DurableAliasChain_EmitsVerifiedSingleCarrier()
    {
        var assembly = EsHarness.Compile("""
namespace Test

struct Cell { var value: int }

func create() -> *Cell {
    var cell = Cell { value: 7 }
    var first = &cell
    var second = first
    first.value = 23
    return second
}

func observe() -> int {
    var pointer = create()
    return pointer.value
}
""", "PointerAliasChain");

        Assert.Equal(23, EsHarness.Invoke(assembly, "observe"));
    }

    [Fact]
    public void DurablePointerParameter_AliasReturnUsesOneCarrier()
    {
        var assembly = EsHarness.Compile("""
namespace Test

struct Cell { var value: int }

func identity(cell: *Cell) -> *Cell {
    var alias = cell
    return alias
}

func observe() -> int {
    var cell = Cell { value: 7 }
    var pointer = identity(&cell)
    pointer.value = 29
    return cell.value
}
""", "PointerParameterAlias");

        Assert.Equal(29, EsHarness.Invoke(assembly, "observe"));
    }

    [Fact]
    public void CrossUnitDurablePointerContract_PromotesCallerAlias()
    {
        var producer = """
namespace Test

struct Cell { var value: int }

func identity(cell: *Cell) -> *Cell {
    return cell
}
""";
        var consumer = """
namespace Test

func observe() -> int {
    var cell = Cell { value: 7 }
    var alias = &cell
    var escaped = identity(alias)
    escaped.value = 31
    return cell.value
}
""";

        var assembly = Assembly.LoadFrom(EsHarness.CompileToPath([producer, consumer], "CrossUnitPointer"));
        Assert.Equal(31, EsHarness.Invoke(assembly, "observe"));
    }

    [Fact]
    public void PointerAliasStoredThroughGenericContainerCall_PreservesOneCell()
    {
        var assembly = EsHarness.Compile("""
namespace Test

struct Cell { var value: int }

func observe() -> int {
    var cell = Cell { value: 1 }
    var pointer = &cell
    var cells = List<*Cell>()
    cells.Add(pointer)
    pointer.value = 42
    return cells[0].value
}
""", "PointerListAliasIdentity");

        Assert.Equal(42, EsHarness.Invoke(assembly, "observe"));
    }

    [Fact]
    public void PointerAliasStoredThroughGenericIndex_PreservesOneCell()
    {
        var assembly = EsHarness.Compile("""
namespace Test

struct Cell { var value: int }

func observe() -> int {
    var cell = Cell { value: 1 }
    var pointer = &cell
    var cells = Dictionary<string, *Cell>()
    cells["cell"] = pointer
    pointer.value = 43
    return cells["cell"].value
}
""", "PointerDictionaryAliasIdentity");

        Assert.Equal(43, EsHarness.Invoke(assembly, "observe"));
    }

    [Fact]
    public void ClosureCaptureOfPointerAlias_PreservesOneDurableCell()
    {
        var assembly = EsHarness.Compile("""
namespace Test

struct Cell { var value: int }

func build() -> Func<int> {
    var cell = Cell { value: 40 }
    var pointer = &cell
    let next = func() -> int {
        pointer.value += 1
        return pointer.value
    }
    return next
}

func observe() -> int {
    let next = build()
    return next() + next()
}
""", "PointerClosureAliasIdentity");

        Assert.Equal(83, EsHarness.Invoke(assembly, "observe"));
    }

    [Fact]
    public void FunctionPointerCall_AdaptsAddressToDurablePointerContract()
    {
        var assembly = EsHarness.Compile("""
namespace Test

struct Cell { var value: int }

func retain(cell: *Cell) -> *Cell {
    return cell
}

func observe() -> int {
    var cell = Cell { value: 1 }
    let fn = &retain
    var pointer: *Cell = fn(*cell)
    pointer.value = 42
    return cell.value
}
""", "FunctionPointerDurableAlias");

        Assert.Equal(42, EsHarness.Invoke(assembly, "observe"));
    }
}
