using System.Reflection;

namespace Esharp.Tests;

/// <summary>
/// Executable matrix for the property contract in the declarations specification.
/// Every positive row reaches the IL backend, passes verification, and invokes the
/// emitted assembly. The rows intentionally vary inputs rather than merely parsing
/// the same spelling: accessor lowering must preserve reads, writes, and storage.
/// </summary>
public sealed class PropertyContractMatrixTests
{
    public static TheoryData<int, int, int> AutoPropertyCases => new()
    {
        { 0, 0, 0 }, { 0, 1, 1 }, { 1, 0, 0 }, { 1, 9, 9 }, { 7, 7, 7 },
        { -3, 4, 4 }, { 42, -1, -1 }, { 100, 101, 101 }, { -10, -20, -20 }, { 19, 23, 23 },
    };

    [Theory]
    [MemberData(nameof(AutoPropertyCases))]
    public void AutoProperty_ReadAfterWrite_RoundTrips(int initial, int written, int expected)
    {
        var source = $$"""
namespace Test
class Cell {
    var value: int { }
    init(initial: int) { self.value = initial }
}
func go() -> int {
    let cell = Cell({{initial}})
    cell.value = {{written}}
    return cell.value
}
""";
        Assert.Equal(expected, EsHarness.Run(source, "go"));
    }

    public static TheoryData<int, int, int> CustomSetterCases => new()
    {
        { 0, -1, 0 }, { 0, 0, 0 }, { 0, 1, 1 }, { 4, -9, 0 },
        { 4, 9, 9 }, { -2, -3, 0 }, { 100, -100, 0 }, { 8, 42, 42 },
    };

    [Theory]
    [MemberData(nameof(CustomSetterCases))]
    public void CustomSetter_StoresItsValueExpression(int initial, int written, int expected)
    {
        var source = $$"""
namespace Test
class Clamp {
    var value: int { set(v) => v < 0 ? 0 : v }
    init(initial: int) { self.value = initial }
}
func go() -> int {
    let clamp = Clamp({{initial}})
    clamp.value = {{written}}
    return clamp.value
}
""";
        Assert.Equal(expected, EsHarness.Run(source, "go"));
    }

    public static TheoryData<int, int, int, int> ComputedPropertyCases => new()
    {
        { 0, 0, 1, 0 }, { 1, 2, 1, 4 }, { 2, 3, 2, 9 }, { 4, 5, 3, 20 },
        { -1, 9, 2, 27 }, { 10, -4, 2, -12 }, { 7, 8, 4, 40 }, { -3, -2, 5, -12 },
    };

    [Theory]
    [MemberData(nameof(ComputedPropertyCases))]
    public void ComputedProperty_ReevaluatesAfterBackingFieldMutation(int initial, int replacement, int factor, int expected)
    {
        var source = $$"""
namespace Test
class Pair {
    var left: int
    var right: int
    init(left: int, right: int) { self.left = left  self.right = right }
    let score: int => self.left * {{factor}} + self.right
}
func go() -> int {
    let pair = Pair({{initial}}, {{replacement}})
    pair.left = {{replacement}}
    return pair.score
}
""";
        Assert.Equal(expected, EsHarness.Run(source, "go"));
    }

    public static TheoryData<int, int, int> RequiredInitPropertyCases => new()
    {
        { 1, 2, 3 }, { 0, 0, 0 }, { -3, 9, 6 }, { 40, 2, 42 },
    };

    [Theory]
    [MemberData(nameof(RequiredInitPropertyCases))]
    public void RequiredInitProperties_CompositeLiteralSuppliesReadableValues(int id, int quantity, int expected)
    {
        var source = $$"""
namespace Test
class Ticket {
    required let id: int { }
    required let quantity: int { }
}
func go() -> int {
    let ticket = Ticket { id: {{id}}, quantity: {{quantity}} }
    return ticket.id + ticket.quantity
}
""";
        Assert.Equal(expected, EsHarness.Run(source, "go"));
    }

    [Fact]
    public void StoredProperty_EmitsPrivateBackingStorageAndAccessorMetadata()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Record {
    pub var value: int { }
    init() { self.value = 1 }
}
""");
        var type = asm.GetType("Test.Record")!;
        var property = type.GetProperty("value")!;

        Assert.NotNull(property.GetMethod);
        Assert.NotNull(property.SetMethod);
        Assert.True(property.GetMethod!.IsPublic);
        Assert.True(property.SetMethod!.IsPublic);
        Assert.Null(type.GetField("value", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(type.GetField("<value>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance));
    }

    [Fact]
    public void ComputedProperty_EmitsGetterWithoutBackingStorage()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Square {
    var side: int
    init(n: int) { self.side = n }
    pub let area: int => self.side * self.side
}
""");
        var type = asm.GetType("Test.Square")!;
        var property = type.GetProperty("area")!;

        Assert.NotNull(property.GetMethod);
        Assert.Null(property.SetMethod);
        Assert.Null(type.GetField("area", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.Null(type.GetField("<area>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance));
    }

    public static TheoryData<string> InvalidStructPropertyCases => new()
    {
        { "struct One { let value: int { } }" },
        { "struct Pair { let left: int { }  right: int }" },
        { "struct Named { let label: string { } }" },
        { "readonly struct Frozen { let value: int { } }" },
    };

    [Theory]
    [MemberData(nameof(InvalidStructPropertyCases))]
    public void Struct_GetOnlyStoredProperty_IsRejected(string declaration)
    {
        var exception = Assert.ThrowsAny<Exception>(() => EsHarness.Compile($"namespace Test\n{declaration}"));
        Assert.Contains("ES2193", exception.Message);
    }
}
