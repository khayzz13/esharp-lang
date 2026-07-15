using System.Reflection;
using Esharp.Diagnostics;

namespace Esharp.Tests;

/// Nested type declarations — `class Outer { struct Inner { ... } }`, nested enums,
/// and a `static func` host nesting types (the ChanSelect shape: a nested enum + a
/// nested struct with delegate-typed fields). Compiles via the IL backend (the
/// source of truth), runs the code, and inspects the emitted metadata to confirm the
/// CLR nesting (DeclaringType, nested visibility). Name scoping is C#-faithful: a
/// bare nested name resolves only inside its enclosing type; from outside it needs
/// the qualified `Outer.Inner`.
public sealed class NestedTypeTests
{
    static object? Run(string source, string method, params object?[] args) =>
        EsHarness.Run(source, method, args);
    static Assembly Compile(string source) => EsHarness.Compile(source);

    // Bare nested name resolves INSIDE the enclosing type — a member constructs its
    // own nested struct by simple name.
    [Fact]
    public void NestedStruct_BareNameInsideEnclosingType() => Assert.Equal(7, Run("""
namespace Test
static Outer {
    struct Inner { v: int }
    func make() -> int {
        let i = Inner { v: 7 }
        return i.v
    }
}
func go() -> int = Outer.make()
""", "go"));

    // Qualified `Outer.Inner` reaches a public nested type from OUTSIDE the enclosing
    // type — the C# way to name a nested type externally.
    [Fact]
    public void NestedStruct_QualifiedNameFromOutside() => Assert.Equal(9, Run("""
namespace Test
static Outer {
    pub struct Inner { v: int }
}
func go() -> int {
    let i = Outer.Inner { v: 9 }
    return i.v
}
""", "go"));

    // A bare nested name does NOT leak into the enclosing namespace: a free function
    // referencing `Inner` (not `Outer.Inner`) is an error, exactly as in C#.
    [Fact]
    public void NestedName_DoesNotLeak_BareFromOutsideIsError()
    {
        var diags = EsHarness.AllDiagnostics("""
namespace Test
class Outer {
    pub struct Inner { v: int }
}
func go() -> int {
    let i = Inner { v: 1 }
    return i.v
}
""");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error);
    }

    // PROBE: is the enum-match-expression-in-static-func-member shape sound for a
    // TOP-LEVEL enum? (Isolates whether the nested-enum failure is nesting-specific.)
    [Fact]
    public void Probe_TopLevelEnum_MatchExprInStaticFuncMember() => Assert.Equal(1, Run("""
namespace Test
enum Kind { Red, Green }
static Host {
    func pick() -> int {
        let k = Kind.Green()
        return match k {
            .Red   => 0
            .Green => 1
        }
    }
}
func go() -> int = Host.pick()
""", "go"));

    // A nested enum inside a static host, used inside the host.
    [Fact]
    public void NestedEnum_InStaticFunc_Reads() => Assert.Equal(1, Run("""
namespace Test
static Host {
    enum Kind { Red, Green }
    func pick() -> int {
        let k = Kind.Green()
        return match k {
            .Red   => 0
            .Green => 1
        }
    }
}
func go() -> int = Host.pick()
""", "go"));

    // The ChanSelect shape: a static host nesting an enum and a struct whose
    // fields are delegate-typed plus a sibling-enum-typed field (resolved bare,
    // sibling scope). Confirms the emitted CLR nesting + field types.
    [Fact]
    public void NestedStruct_DelegateAndSiblingEnumFields_InStaticFunc()
    {
        var asm = Compile("""
namespace Test
static Sel {
    enum Kind { Recv, Send }
    pub struct Arm {
        kind: Kind
        tryOp: Func<bool>
        body: Action
    }
}
func go() -> int = 0
""");
        var arm = asm.GetType("Test.Sel+Arm") ?? throw new Xunit.Sdk.XunitException(
            "nested type Test.Sel+Arm not found: " + string.Join(", ", asm.GetTypes().Select(t => t.FullName)));
        Assert.NotNull(arm.DeclaringType);
        Assert.Equal("Sel", arm.DeclaringType!.Name);
        Assert.True(arm.IsNested);
        const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Assert.Equal(typeof(Func<bool>), arm.GetField("tryOp", AnyInstance)!.FieldType);
        Assert.Equal(typeof(Action), arm.GetField("body", AnyInstance)!.FieldType);
        // The sibling-enum-typed field resolved (bare) to the nested enum.
        var kindField = arm.GetField("kind", AnyInstance)!;
        Assert.True(kindField.FieldType.IsNested);
        Assert.Equal("Kind", kindField.FieldType.Name);
    }

    // Visibility: a nested type defaults to PRIVATE (C# rule — tighter than the
    // top-level internal default); `pub` makes it public.
    [Fact]
    public void NestedType_DefaultsPrivate_PubMakesPublic()
    {
        var asm = Compile("""
namespace Test
class Outer {
    struct Hidden { v: int }
    pub struct Shown { v: int }
}
func go() -> int = 0
""");
        var hidden = asm.GetType("Test.Outer+Hidden")!;
        var shown = asm.GetType("Test.Outer+Shown")!;
        Assert.True(hidden.IsNestedPrivate);
        Assert.True(shown.IsNestedPublic);
        Assert.Equal("Test.Outer", shown.DeclaringType!.FullName);
    }

    // Three-state visibility on nested types: pub → NestedPublic, internal →
    // NestedAssembly, priv (and the bare default) → NestedPrivate.
    [Fact]
    public void NestedType_ThreeStateVisibility()
    {
        var asm = Compile("""
namespace Test
class Outer {
    pub struct P { v: int }
    internal struct I { v: int }
    priv struct V { v: int }
    struct D { v: int }
}
func go() -> int = 0
""");
        Assert.True(asm.GetType("Test.Outer+P")!.IsNestedPublic);
        Assert.True(asm.GetType("Test.Outer+I")!.IsNestedAssembly);
        Assert.True(asm.GetType("Test.Outer+V")!.IsNestedPrivate);
        Assert.True(asm.GetType("Test.Outer+D")!.IsNestedPrivate);
    }
}
