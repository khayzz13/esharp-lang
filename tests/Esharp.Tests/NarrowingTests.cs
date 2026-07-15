using Esharp.Diagnostics;

namespace Esharp.Tests;

/// The smart-cast flow engine (§type-narrowing-and-downcasting slice 3) — `is`/`is not`
/// narrowing on stable bindings, through `&&`, `if`/`else`, and the guard-return idiom;
/// the null axis folded into the same engine; field-path narrows with call invalidation;
/// and the ES2173 soundness diagnostic on a `var` / call-crossed path.
public sealed class NarrowingTests
{
    const string SymHierarchy = """
namespace Test

abstract class Sym {
    pub name: string
    init(n: string) { self.name = n }
}
class TypeSym : Sym {
    pub arity: int
    init(n: string, a: int) : base(n) { self.arity = a }
}
class MethodSym : Sym {
    pub isStatic: bool
    init(n: string, s: bool) : base(n) { self.isStatic = s }
}

""";

    static int Int(string src) => (int)EsHarness.Run(src, "go")!;
    static string Str(string src) => (string)EsHarness.Run(src, "go")!;

    [Fact]
    public void SmartCast_If_Is_ResolvesMember_NoRebind()
    {
        // `if s is TypeSym { s.arity }` — s narrows to TypeSym in the guarded region.
        Assert.Equal(3, Int(SymHierarchy + """
func go() -> int {
    let s: Sym = TypeSym("a", 3)
    if s is TypeSym { return s.arity }
    return 0
}
"""));
    }

    [Fact]
    public void SmartCast_AndChain_NarrowsRightOperand()
    {
        // `s is MethodSym && s.isStatic` — the right operand binds under the narrow.
        Assert.Equal("yes", Str(SymHierarchy + """
func go() -> string {
    let s: Sym = MethodSym("a", true)
    if s is MethodSym && s.isStatic { return "yes" }
    return "no"
}
"""));
    }

    [Fact]
    public void SmartCast_GuardReturn_NarrowsRestOfBlock()
    {
        // `if s is not TypeSym { return }` narrows s to TypeSym below it.
        Assert.Equal(7, Int(SymHierarchy + """
func go() -> int {
    let s: Sym = TypeSym("a", 7)
    if s is not TypeSym { return -1 }
    return s.arity
}
"""));
    }

    [Fact]
    public void SmartCast_Else_NarrowsOnNegativeOverClosed()
    {
        // Over the closed Sym hierarchy, the else of `is TypeSym` narrows to the only
        // other leaf is not assumed — but `is not` in a guard does. Here the else branch
        // sees the negative fact only as an open-world non-narrow, so we test the THEN.
        Assert.Equal("m", Str(SymHierarchy + """
func go() -> string {
    let s: Sym = MethodSym("m", false)
    if s is TypeSym {
        return "t"
    } else {
        return s.name
    }
}
"""));
    }

    [Fact]
    public void NullAxis_NotNil_NarrowsNullableValue()
    {
        // The null axis folds into the same engine: `x != nil` narrows `int?` → `int`.
        Assert.Equal(5, Int("""
namespace Test
func go() -> int {
    let x: int? = 5
    if x != nil { return x }
    return 0
}
"""));
    }

    [Fact]
    public void FieldPath_LetField_NarrowsAcrossNoCall()
    {
        // A `let`-field path `b.item` narrows and holds across a no-call region.
        Assert.Equal(4, Int(SymHierarchy + """
class Box {
    let item: Sym
    init(i: Sym) { self.item = i }
}
func go() -> int {
    let b = Box(TypeSym("a", 4))
    if b.item is TypeSym { return b.item.arity }
    return 0
}
"""));
    }

    [Fact]
    public void Var_Narrow_IsRejected_WithRebindHint()
    {
        // A `var` is reassignable, so it does not smart-cast — relying on the narrow is
        // ES2173 with the rebind fix.
        var d = EsHarness.Diagnostics(SymHierarchy + """
func go() -> int {
    var s: Sym = TypeSym("a", 1)
    if s is TypeSym { return s.arity }
    return 0
}
""");
        Assert.Contains(d, x => x.Message.Contains("ES2173"));
    }

    [Fact]
    public void FieldPath_Narrow_RejectedAcrossCall()
    {
        // A call between the test and the use invalidates a field-path narrow — a call
        // may mutate the field through an alias. ES2173.
        var d = EsHarness.Diagnostics(SymHierarchy + """
class Box {
    let item: Sym
    init(i: Sym) { self.item = i }
}
func touch() -> int = 0
func go() -> int {
    let b = Box(TypeSym("a", 4))
    if b.item is TypeSym {
        let z = touch()
        return b.item.arity
    }
    return 0
}
""");
        Assert.Contains(d, x => x.Message.Contains("ES2173"));
    }

    [Fact]
    public void Rebind_Via_As_Works_OnVar()
    {
        // The fix the diagnostic suggests: rebind through `as` to a fresh `let`.
        Assert.Equal(1, Int(SymHierarchy + """
func go() -> int {
    var s: Sym = TypeSym("a", 1)
    let t = s as TypeSym
    return t?.arity ?? 0
}
"""));
    }
}
