using Esharp.Diagnostics;

namespace Esharp.Tests;

/// Closed-world exhaustiveness for type-pattern matches (§type-narrowing slice 5). Over
/// an `abstract class` base whose leaves are all in this assembly, a type-pattern
/// `match` is checked like a `choice`: a missing leaf warns, full coverage needs no
/// `default` and satisfies definite return, and adding a leaf re-triggers the warning.
///
/// The scrutinee reaches each match through a local so the value isn't promoted onto the
/// user type — the tests exercise exhaustiveness, not the promotion rule.
public sealed class HierarchyExhaustivenessTests
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

    static IReadOnlyList<Diagnostic> Warns(string src) =>
        EsHarness.AllDiagnostics(src).Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();

    [Fact]
    public void FullCoverage_NeedsNoDefault_AndSatisfiesDefiniteReturn()
    {
        // Every leaf covered → no `default` needed, and the match returns on every path,
        // so the non-void `go` is well-formed (definite-return is satisfied).
        Assert.Equal("type a/1", (string)EsHarness.Run(SymHierarchy + """
func go() -> string {
    let s: Sym = TypeSym("a", 1)
    match s {
        (t: TypeSym)   { return "type {t.name}/{t.arity}" }
        (m: MethodSym) { return "func {m.name}" }
    }
}
""", "go")!);
    }

    [Fact]
    public void FullCoverage_NoWarning()
    {
        var w = Warns(SymHierarchy + """
func go() -> string {
    let s: Sym = TypeSym("a", 1)
    match s {
        (t: TypeSym)   { return "type {t.name}" }
        (m: MethodSym) { return "func {m.name}" }
    }
}
""");
        Assert.DoesNotContain(w, d => d.Message.Contains("Non-exhaustive match on 'Sym'"));
    }

    [Fact]
    public void MissingLeaf_NoDefault_Warns()
    {
        // Only one of two leaves handled, no default → non-exhaustive warning naming the
        // missing leaf.
        var w = Warns(SymHierarchy + """
func go() -> string =
    runStat()
func runStat() -> string {
    let s: Sym = TypeSym("a", 1)
    return match s {
        (t: TypeSym) => "type {t.name}"
    }
}
""");
        Assert.Contains(w, d => d.Message.Contains("Non-exhaustive match on 'Sym'") && d.Message.Contains("MethodSym"));
    }

    [Fact]
    public void Default_SuppressesWarning()
    {
        var w = Warns(SymHierarchy + """
func go() -> int {
    let s: Sym = TypeSym("a", 1)
    match s {
        (t: TypeSym) { return t.arity }
        default      { return -1 }
    }
}
""");
        Assert.DoesNotContain(w, d => d.Message.Contains("Non-exhaustive match on 'Sym'"));
    }

    [Fact]
    public void GuardedArm_DoesNotCount_AsCoverage()
    {
        // A guarded MethodSym arm doesn't cover MethodSym — the match is still missing it.
        var w = Warns(SymHierarchy + """
func go() -> string {
    let s: Sym = TypeSym("a", 1)
    return match s {
        (t: TypeSym)                 => "type {t.name}"
        (m: MethodSym) if m.isStatic => "static {m.name}"
    }
}
""");
        Assert.Contains(w, d => d.Message.Contains("Non-exhaustive match on 'Sym'") && d.Message.Contains("MethodSym"));
    }

    [Fact]
    public void OpenBase_NoExhaustivenessCheck()
    {
        // An `open` base is instantiable + inheritable — the world is open, so a
        // type-pattern match over it is not exhaustiveness-checked (a `default` is needed
        // for definite return, but there is no missing-leaf warning).
        var w = Warns("""
namespace Test
open class Base { init() {} }
class Leaf : Base { init() : base() {} }
func go() -> string {
    let b: Base = Leaf()
    return match b {
        (l: Leaf) => "leaf"
        default   => "base"
    }
}
""");
        Assert.DoesNotContain(w, d => d.Message.Contains("Non-exhaustive match on 'Base'"));
    }
}
