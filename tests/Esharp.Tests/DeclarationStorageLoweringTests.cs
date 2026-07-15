using Esharp.BoundTree;

namespace Esharp.Tests;

/// <summary>
/// Regression coverage for the declaration-storage lowering seams. These assert
/// bound representation as well as emitted behavior: an initializer conversion
/// must be explicit before type/property emission, never rediscovered ad hoc by
/// a particular CodeGen store path.
/// </summary>
public sealed class DeclarationStorageLoweringTests
{
    [Theory]
    [InlineData("var stored: int? = 7", true)]
    [InlineData("stored: int? = 7", false)]
    public void TypeMemberStorage_DefaultValue_IsLoweredToTheDeclaredNullableSlot(string declaration, bool property)
    {
        var program = EsHarness.BindAndLower($$"""
namespace Test
class Meter { {{declaration}} }
func go() -> int { return 0 }
""");

        var meter = Assert.IsType<BoundDataDeclaration>(program.Units.Single().Members
            .Single(member => member is BoundDataDeclaration));
        var field = Assert.Single(meter.Fields);

        Assert.Equal(property, field.IsProperty);
        var conversion = Assert.IsType<BoundConversion>(field.DefaultValue);
        Assert.Equal(ConversionKind.NullableWrap, conversion.Kind);
        Assert.IsType<NullableType>(conversion.TargetType);
    }

    [Fact]
    public void StoredPropertyNullableDefault_EmitsAndReadsAsAValue()
    {
        Assert.Equal(7, EsHarness.Run("""
namespace Test
class Meter { var stored: int? = 7 }
func go() -> int {
    let meter = Meter()
    return meter.stored.Value
}
""", "go"));
    }
}
