using Xunit;
using Esharp.Diagnostics;
using Esharp.Syntax.Parsing;
using Mono.Cecil.Cil;

namespace Esharp.Tests;

/// <summary>
/// Executable counterparts of the pointer specification's location-identity
/// examples. These deliberately distinguish an address of existing storage,
/// a fresh <c>new</c> location, value copying, pointer copying, and promotion
/// of an address that escapes its creating frame.
/// </summary>
public sealed class PointerLocationSemanticsTests
{
    [Fact]
    public void AddressOf_AliasesTheExistingNamedLocation()
    {
        var result = EsHarness.Run("""
namespace Test

struct Cell { var value: int }

func addressAliasesExistingLocation() -> int {
    var cell = Cell { value: 40 }
    var pointer = &cell
    pointer.value += 1
    cell.value += 1
    return pointer.value
}
""", "addressAliasesExistingLocation");

        Assert.Equal(42, result);
    }

    [Fact]
    public void New_MintsAnIndependentPointerOwnedLocation()
    {
        var result = EsHarness.Run("""
namespace Test

struct Cell { var value: int }

func allocationIsIndependentFromAddressOf() -> int {
    var cell = Cell { value: 40 }
    var alias = &cell
    var fresh: *Cell = new Cell { value: 40 }
    alias.value += 1
    fresh.value += 2
    return cell.value * 100 + fresh.value
}
""", "allocationIsIndependentFromAddressOf");

        Assert.Equal(4142, result);
    }

    [Fact]
    public void New_AlwaysAllocatesAHeapCarrier_NotAManagedAddressBorrow()
    {
        var parser = new Parser("""
namespace Test

struct Cell { var value: int }

func allocate() -> int {
    var pointer: *Cell = new Cell { value: 40 }
    return pointer.value
}
""", "new-is-heap-allocation.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.DoesNotContain(parser.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.DoesNotContain(binder.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var (assembly, diagnostics) = EsHarness.EmitBound(binder, bound, "NewIsHeapAllocation");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        using (assembly)
        {
            var method = assembly.MainModule.Types
                .SelectMany(type => type.Methods)
                .Single(candidate => candidate.Name == "allocate");

            Assert.Contains(method.Body.Instructions, instruction =>
                instruction.OpCode == OpCodes.Newobj
                && instruction.Operand?.ToString()?.Contains("__Ptr_Cell", StringComparison.Ordinal) == true);
            Assert.Contains(method.Body.Variables, variable =>
                variable.VariableType.Name.Contains("__Ptr_Cell", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void EscapingAddressOf_PromotesTheOriginalLocationRatherThanCopyingIt()
    {
        var result = EsHarness.Run("""
namespace Test

struct Cell { var value: int }

func makeCell() -> *Cell {
    var cell = Cell { value: 40 }
    return &cell
}

func usePromotedAddress() -> int {
    let pointer = makeCell()
    pointer.value += 2
    return pointer.value
}
""", "usePromotedAddress");

        Assert.Equal(42, result);
    }

    [Fact]
    public void PointerCopyAliasesWhileValueCopyGetsIndependentStorage()
    {
        var result = EsHarness.Run("""
namespace Test

struct Cell { var value: int }

func pointerAndValueCopiesDiffer() -> int {
    var cell = Cell { value: 40 }
    var valueCopy = cell
    var pointer = &cell
    var pointerCopy = pointer
    pointerCopy.value += 1
    valueCopy.value += 2
    return cell.value * 100 + valueCopy.value
}
""", "pointerAndValueCopiesDiffer");

        Assert.Equal(4142, result);
    }
}
