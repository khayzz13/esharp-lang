using System.Reflection;
using Xunit;

namespace Esharp.Tests;

// Field & property visibility: `pub` → CLR public, `priv` → CLR private, bare → assembly
// (internal). For a property the visibility lands on the get_/set_ accessors; the backing
// field stays private regardless.
public class FieldVisibilityTests
{
    [Fact]
    public void PrivField_EmitsPrivate_PubField_EmitsPublic()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Box {
    priv secret: int
    pub shown: int
    bare: int
    init() { self.secret = 1  self.shown = 2  self.bare = 3 }
}
""");
        var t = asm.GetType("Test.Box")!;
        Assert.True(t.GetField("secret", BindingFlags.NonPublic | BindingFlags.Instance)!.IsPrivate);
        Assert.True(t.GetField("shown", BindingFlags.Public | BindingFlags.Instance)!.IsPublic);
        // bare field is assembly/internal — neither public nor private.
        var bare = t.GetField("bare", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.True(bare.IsAssembly);
    }

    [Fact]
    public void PrivProperty_EmitsPrivateAccessors()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Counter {
    priv var n: int {}
    init() { self.n = 0 }
    func bump() { self.n = self.n + 1 }
}
""");
        var t = asm.GetType("Test.Counter")!;
        var getter = t.GetMethod("get_n", BindingFlags.NonPublic | BindingFlags.Instance);
        var setter = t.GetMethod("set_n", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(getter);
        Assert.NotNull(setter);
        Assert.True(getter!.IsPrivate);
        Assert.True(setter!.IsPrivate);
    }

    [Fact]
    public void PubProperty_EmitsPublicAccessors_BackingStaysPrivate()
    {
        var asm = EsHarness.Compile("""
namespace Test
class Counter {
    pub var n: int {}
    init() { self.n = 0 }
}
""");
        var t = asm.GetType("Test.Counter")!;
        Assert.True(t.GetMethod("get_n", BindingFlags.Public | BindingFlags.Instance)!.IsPublic);
        Assert.True(t.GetMethod("set_n", BindingFlags.Public | BindingFlags.Instance)!.IsPublic);
        // The synthesized backing field is private regardless of the property's visibility.
        var backing = t.GetField("<n>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(backing);
        Assert.True(backing!.IsPrivate);
    }
}
