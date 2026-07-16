using System.Linq;
using System.Reflection;

namespace Esharp.Tests;

/// Per-accessor visibility (proposal P8) — `pub var X { priv set }` emits a public
/// getter and a private setter over a private backing field. A per-accessor modifier
/// may only NARROW the property's visibility; widening is ES2229.
public sealed class PerAccessorVisibilityTests
{
    [Fact]
    public void PublicGetPrivateSet_AccessorsCarryTheirOwnVisibility()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Box {
    pub var value: int { priv set }
    init() { self.value = 7 }
}
func go() -> int = 0
""", "PerAccessor");
        var box = asm.GetTypes().Single(t => t.Name == "Box");
        var get = box.GetMethod("get_value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var set = box.GetMethod("set_value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(get);
        Assert.NotNull(set);
        Assert.True(get!.IsPublic, "get_value should be public");
        Assert.True(set!.IsPrivate, "set_value should be private");
    }

    // The backing field stays private no matter the accessor visibility.
    [Fact]
    public void BackingField_StaysPrivate()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Box {
    pub var value: int { priv set }
    init() { self.value = 7 }
}
func go() -> int = 0
""", "PerAccessorField");
        var box = asm.GetTypes().Single(t => t.Name == "Box");
        var backing = box.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(f => f.Name.Contains("value"));
        Assert.True(backing.IsPrivate);
    }

    // An accessor may not widen the property's visibility — ES2229.
    [Fact]
    public void WideningAccessor_IsRejected()
    {
        var diags = EsHarness.Diagnostics("""
namespace Test
class Box {
    priv var value: int { pub set }
    init() { self.value = 0 }
}
""");
        Assert.Contains(diags, d => d.Code == "ES2229");
    }
}
