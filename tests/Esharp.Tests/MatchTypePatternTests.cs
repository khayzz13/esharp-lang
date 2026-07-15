using Esharp.Diagnostics;

namespace Esharp.Tests;

/// Type-narrowing & downcasting (§type-narrowing-and-downcasting) — the `match` type
/// pattern `(name: T)`, guards, `nil` arms, `=>` expression arms, and the `is`/`as`/`as!`
/// operators, all compiled through the IL backend and run. This is the language form the
/// in-house LSP needs to discriminate an open `ISymbol` into its concrete kinds.
///
/// Helpers take their scrutinee through a non-receiver path (an `object` parameter or a
/// local) so the value isn't instance-method-promoted onto the user type — the tests
/// exercise narrowing, not the promotion rule.
public sealed class MatchTypePatternTests
{
    // A closed hierarchy in the LSP's exact shape: an abstract base, sealed leaves.
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

    static string Str(string src) => (string)EsHarness.Run(src, "go")!;
    static int Int(string src) => (int)EsHarness.Run(src, "go")!;

    [Fact]
    public void TypePattern_Dispatches_OverClosedHierarchy()
    {
        // `(t: TypeSym)` / `(m: MethodSym)` dispatch a base-typed value to its leaf,
        // binding the narrowed value — the LSP's symbol discrimination.
        Assert.Equal("type foo/2", Str(SymHierarchy + """
func go() -> string {
    let s: Sym = TypeSym("foo", 2)
    return match s {
        (t: TypeSym)   => "type {t.name}/{t.arity}"
        (m: MethodSym) => "func {m.name}"
        default        => s.name
    }
}
"""));
    }

    [Fact]
    public void TypePattern_Guard_RefinesArm()
    {
        // A guard refines a type-pattern arm; the binding is visible to the guard.
        Assert.Equal("static bar", Str(SymHierarchy + """
func go() -> string {
    let s: Sym = MethodSym("bar", true)
    return match s {
        (t: TypeSym)                 => "type {t.name}"
        (m: MethodSym) if m.isStatic => "static {m.name}"
        (m: MethodSym)               => "func {m.name}"
        default                      => s.name
    }
}
"""));
    }

    [Fact]
    public void TypePattern_Guard_FallsToNextArm()
    {
        // When the guard is false, control falls to the next matching arm.
        Assert.Equal("func baz", Str(SymHierarchy + """
func go() -> string {
    let s: Sym = MethodSym("baz", false)
    return match s {
        (m: MethodSym) if m.isStatic => "static {m.name}"
        (m: MethodSym)               => "func {m.name}"
        default                      => s.name
    }
}
"""));
    }

    [Fact]
    public void TypePattern_OpenWorld_Object_WithNilAndDefault()
    {
        // Over an open `object`, a type pattern is `isinst` per arm; `nil` matches the
        // absent value; `default` covers the rest. Value-type targets unbox.
        Assert.Equal("str hello", Str("""
namespace Test
func classify(o: object) -> string = match o {
        nil         => "nil"
        (n: int)    => "int {n}"
        (s: string) => "str {s}"
        default     => "other"
    }
func go() -> string = classify("hello")
"""));
    }

    [Fact]
    public void TypePattern_OpenWorld_Object_UnboxesValueType()
    {
        Assert.Equal("int 7", Str("""
namespace Test
func classify(o: object) -> string = match o {
        (n: int) => "int {n}"
        default  => "other"
    }
func go() -> string = classify(7)
"""));
    }

    [Fact]
    public void TypePattern_SiblingBindingsWithSameSourceName_GetDistinctLocals()
    {
        // Match arms are sibling scopes. The same friendly binding name may
        // carry unrelated CLR types, so lowering must alpha-rename both type
        // pattern bindings before allocating method-wide CLR locals.
        Assert.Equal(3, Int("""
namespace Test
func classify(o: object) -> int = match o {
        (item: int)    => item + 1
        (item: string) => item.Length
        default        => 0
    }
func go() -> int = classify("cat")
"""));
    }

    [Fact]
    public void NilArm_MatchesAbsentValue()
    {
        Assert.Equal("nil", Str("""
namespace Test
func classify(o: object) -> string = match o {
        nil     => "nil"
        default => "some"
    }
func go() -> string {
    let x: object = nil
    return classify(x)
}
"""));
    }

    [Fact]
    public void NilArm_OverNullableReference_UsesReferenceNullComparison()
    {
        // `object?` is represented by a CLR object reference, not Nullable<object>.
        // The nil arm must emit `ldnull; ceq`, never a value-type HasValue probe.
        Assert.Equal("nil", Str("""
namespace Test
func classify(o: object?) -> string = match o {
        nil        => "nil"
        (s: string) => "string {s}"
        default    => "other"
    }
func go() -> string = classify(nil)
"""));
    }

    [Fact]
    public void IsOperator_Test_And_Negation()
    {
        // `is` and `is not` as boolean tests.
        Assert.Equal(1, Int(SymHierarchy + """
func go() -> int {
    let s: Sym = TypeSym("a", 1)
    if s is TypeSym { return 1 }
    if s is not MethodSym { return 2 }
    return 3
}
"""));
    }

    [Fact]
    public void AsOperator_SafeCast_ComposesWithNullable()
    {
        // `as T` yields T?, composing with `?.` and `??`.
        Assert.Equal(2, Int(SymHierarchy + """
func go() -> int {
    let s: Sym = TypeSym("a", 2)
    let t = s as TypeSym
    return t?.arity ?? 0
}
"""));
    }

    [Fact]
    public void AsOperator_SafeCast_NilOnMiss()
    {
        // A failed `as` yields nil, so the `?? 0` fallback fires.
        Assert.Equal(0, Int(SymHierarchy + """
func go() -> int {
    let s: Sym = MethodSym("a", false)
    let t = s as TypeSym
    return t?.arity ?? 0
}
"""));
    }

    [Fact]
    public void AsBang_AssertingCast_Unbox()
    {
        // `as! int` unboxes; succeeds when the runtime type matches.
        Assert.Equal(42, Int("""
namespace Test
func mustBeInt(boxed: object) -> int = boxed as! int
func go() -> int {
    let o: object = 42
    return mustBeInt(o)
}
"""));
    }

    [Fact]
    public void AsBang_AssertingCast_Reference()
    {
        Assert.Equal("z", Str(SymHierarchy + """
func go() -> string {
    let s: Sym = TypeSym("z", 9)
    let t = s as! TypeSym
    return t.name
}
"""));
    }

    [Fact]
    public void Reified_Is_DistinguishesGenericInstantiations()
    {
        // Reified generics: `is List<int>` is a real closed-generic test.
        Assert.Equal("ints", Str("""
namespace Test
using "System.Collections.Generic"
func kind(o: object) -> string {
    if o is List<int> { return "ints" }
    if o is List<string> { return "strs" }
    return "other"
}
func go() -> string {
    let xs = List<int>()
    xs.Add(1)
    let o: object = xs
    return kind(o)
}
"""));
    }

    [Fact]
    public void ExpressionMatch_TypePattern_YieldsValue()
    {
        // A type-pattern `match` in expression position with `=>` arms.
        Assert.Equal(5, Int(SymHierarchy + """
func go() -> int {
    let s: Sym = TypeSym("a", 5)
    return match s {
        (t: TypeSym)   => t.arity
        (m: MethodSym) => 0
        default        => -1
    }
}
"""));
    }

    [Fact]
    public void ExpressionBody_Match_OnNextLine_Parses()
    {
        // A newline after `=` continues the expression body onto the next line — the
        // natural `func f() -> T =⏎    match … { … }` shape.
        Assert.Equal("type a", Str(SymHierarchy + """
func (s: Sym) describe() -> string =
    match s {
        (t: TypeSym)   => "type {t.name}"
        (m: MethodSym) => "func {m.name}"
    }
func go() -> string {
    let s: Sym = TypeSym("a", 1)
    return s.describe()
}
"""));
    }

    [Fact]
    public void IsTarget_Nullable_IsRejected()
    {
        // `x is T?` is ill-formed — `as T` already yields T?. The `?` after the type
        // parses as a dangling ternary, so this does not silently accept `is T?`.
        var d = EsHarness.AllDiagnostics("""
namespace Test
func f(o: object) -> bool = o is string?
""");
        Assert.Contains(d, x => x.Severity == DiagnosticSeverity.Error);
    }

    // The dogfood: the symbol-table tool in the LSP's exact shape — a closed `abstract
    // class` hierarchy dispatched by an exhaustive type-pattern `match` (zero
    // downcasts), plus the open-world `is`/`as` path over a boxed `object`, compiled
    // through the IL backend and run.
    [Fact]
    public void Dogfood_SymbolTable_CompilesAndRuns()
    {
        Assert.Equal("type Point/0; static origin; func translate; x: int | raw 42", Str("""
namespace Test

abstract class Sym {
    name: string
    init(n: string) { self.name = n }
}
class TypeSym : Sym {
    arity: int
    init(n: string, a: int) : base(n) { self.arity = a }
}
class MethodSym : Sym {
    isStatic: bool
    init(n: string, s: bool) : base(n) { self.isStatic = s }
}
class FieldSym : Sym {
    fieldType: string
    init(n: string, t: string) : base(n) { self.fieldType = t }
}

func (s: Sym) describe() -> string =
    match s {
        (t: TypeSym)                 => "type {t.name}/{t.arity}"
        (m: MethodSym) if m.isStatic => "static {m.name}"
        (m: MethodSym)               => "func {m.name}"
        (f: FieldSym)                => "{f.name}: {f.fieldType}"
    }

func openKind(o: object) -> string {
    if o is Sym { return o.describe() }
    let n = o as int
    return "raw {n ?? -1}"
}

func go() -> string {
    let symbols = List<Sym>()
    symbols.Add(TypeSym("Point", 0))
    symbols.Add(MethodSym("origin", true))
    symbols.Add(MethodSym("translate", false))
    symbols.Add(FieldSym("x", "int"))

    let sb = System.Text.StringBuilder()
    var i = 0
    while i < symbols.Count {
        if i > 0 { sb.Append("; ") }
        sb.Append(symbols[i].describe())
        i += 1
    }
    sb.Append(" | ")
    let boxed: object = 42
    sb.Append(openKind(boxed))
    return sb.ToString()
}
"""));
    }

    [Fact]
    public void TypePattern_OverChoice_IsRejected()
    {
        // A closed `choice` is matched by `.case`, not by type — ES2172.
        var d = EsHarness.Diagnostics("""
namespace Test
union C { a  b }
func f(c: C) -> int {
    match c {
        (x: int) => 1
        default  => 0
    }
}
""");
        Assert.Contains(d, x => x.Message.Contains("ES2172"));
    }
}
