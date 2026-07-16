using Esharp.Syntax.Parsing;
using Esharp.Syntax;

namespace Esharp.Tests;

/// The structured type grammar (tranche 2): `TypeParser` produces a `TypeSyntax`
/// tree — no phase re-parses type text. Part A asserts the exact tree shapes the
/// grammar guarantees (the contract `TypeResolver`'s structural switch dispatches
/// on); Part B drives the same shapes end-to-end through the IL backend; Part C
/// pins the parser's error-recovery contract (error nodes + forward progress,
/// never a crash, never silent emission).
public sealed class TypeGrammarTests
{
    /// Parse `<type>` as a parameter annotation and return its structured node.
    static TypeSyntax ParseAnnotation(string type)
    {
        var parser = new Parser($"namespace T\nfunc f(x: {type}) {{ }}\n", "type.es");
        var unit = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var fn = Assert.Single(unit.Members.OfType<FunctionDeclarationSyntax>());
        return Assert.Single(fn.Parameters).Type;
    }

    // ════════════════════════════════════════════════════════════════════════
    // A. Grammar shapes — the TypeSyntax tree is exactly what the resolver
    //    pattern-matches on.
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Named_Primitive() =>
        Assert.Equal("int", Assert.IsType<NamedTypeSyntax>(ParseAnnotation("int")).Name);

    [Fact]
    public void Named_User() =>
        Assert.Equal("Box", Assert.IsType<NamedTypeSyntax>(ParseAnnotation("Box")).Name);

    [Fact]
    public void Named_Dotted_StaysOneLeaf()
    {
        // Dotted names are name-RESOLUTION concerns, not structure: one leaf.
        var n = Assert.IsType<NamedTypeSyntax>(ParseAnnotation("System.Text.StringBuilder"));
        Assert.Equal("System.Text.StringBuilder", n.Name);
    }

    [Fact]
    public void Generic_SingleArg()
    {
        var g = Assert.IsType<GenericTypeSyntax>(ParseAnnotation("List<int>"));
        Assert.Equal("List", g.Name);
        Assert.Equal("int", Assert.IsType<NamedTypeSyntax>(Assert.Single(g.Args)).Name);
    }

    [Fact]
    public void Generic_MultiArg()
    {
        var g = Assert.IsType<GenericTypeSyntax>(ParseAnnotation("Dictionary<string, int>"));
        Assert.Equal("Dictionary", g.Name);
        Assert.Equal(2, g.Args.Count);
        Assert.Equal("string", Assert.IsType<NamedTypeSyntax>(g.Args[0]).Name);
        Assert.Equal("int", Assert.IsType<NamedTypeSyntax>(g.Args[1]).Name);
    }

    [Fact]
    public void Generic_Nested()
    {
        var outer = Assert.IsType<GenericTypeSyntax>(ParseAnnotation("List<List<int>>"));
        var inner = Assert.IsType<GenericTypeSyntax>(Assert.Single(outer.Args));
        Assert.Equal("List", inner.Name);
        Assert.Equal("int", Assert.IsType<NamedTypeSyntax>(Assert.Single(inner.Args)).Name);
    }

    [Fact]
    public void Generic_DottedBase()
    {
        var g = Assert.IsType<GenericTypeSyntax>(ParseAnnotation("A.Widget<int>"));
        Assert.Equal("A.Widget", g.Name);
        Assert.Single(g.Args);
    }

    [Fact]
    public void Tuple_Pair()
    {
        var t = Assert.IsType<TupleTypeSyntax>(ParseAnnotation("(int, string)"));
        Assert.Equal(2, t.Elements.Count);
        Assert.Equal("int", Assert.IsType<NamedTypeSyntax>(t.Elements[0]).Name);
        Assert.Equal("string", Assert.IsType<NamedTypeSyntax>(t.Elements[1]).Name);
    }

    [Fact]
    public void Tuple_SingleElement_StaysTuple()
    {
        // Exact parity with the old SplitTupleArgs: `(T)` is a 1-tuple, never
        // unwrapped to T.
        var t = Assert.IsType<TupleTypeSyntax>(ParseAnnotation("(int)"));
        Assert.Equal("int", Assert.IsType<NamedTypeSyntax>(Assert.Single(t.Elements)).Name);
    }

    [Fact]
    public void Tuple_OfGenerics()
    {
        var t = Assert.IsType<TupleTypeSyntax>(ParseAnnotation("(List<int>, bool)"));
        Assert.IsType<GenericTypeSyntax>(t.Elements[0]);
        Assert.IsType<NamedTypeSyntax>(t.Elements[1]);
    }

    [Fact]
    public void FnPtr_WithArrow()
    {
        var f = Assert.IsType<FunctionPointerTypeSyntax>(ParseAnnotation("&(int, int -> bool)"));
        Assert.Equal(2, f.ParameterTypes.Count);
        Assert.Equal("bool", Assert.IsType<NamedTypeSyntax>(f.ReturnType).Name);
    }

    [Fact]
    public void FnPtr_NoArrow_IsVoidReturn()
    {
        var f = Assert.IsType<FunctionPointerTypeSyntax>(ParseAnnotation("&(int, string)"));
        Assert.Equal(2, f.ParameterTypes.Count);
        Assert.Equal("void", Assert.IsType<NamedTypeSyntax>(f.ReturnType).Name);
    }

    [Fact]
    public void FnPtr_NoParams_WithReturn()
    {
        var f = Assert.IsType<FunctionPointerTypeSyntax>(ParseAnnotation("&(-> int)"));
        Assert.Empty(f.ParameterTypes);
        Assert.Equal("int", Assert.IsType<NamedTypeSyntax>(f.ReturnType).Name);
    }

    [Fact]
    public void FnPtr_NestedFnPtrParam()
    {
        // The arrow scan is depth-aware: the inner `->` belongs to the inner type.
        var f = Assert.IsType<FunctionPointerTypeSyntax>(ParseAnnotation("&(&(int -> bool), int -> int)"));
        Assert.Equal(2, f.ParameterTypes.Count);
        var inner = Assert.IsType<FunctionPointerTypeSyntax>(f.ParameterTypes[0]);
        Assert.Equal("bool", Assert.IsType<NamedTypeSyntax>(inner.ReturnType).Name);
        Assert.Equal("int", Assert.IsType<NamedTypeSyntax>(f.ReturnType).Name);
    }

    [Fact]
    public void FnPtr_PointerParam()
    {
        var f = Assert.IsType<FunctionPointerTypeSyntax>(ParseAnnotation("&(*Box -> bool)"));
        var p = Assert.IsType<PointerTypeSyntax>(Assert.Single(f.ParameterTypes));
        Assert.Equal("Box", Assert.IsType<NamedTypeSyntax>(p.Inner).Name);
    }

    [Fact]
    public void Nullable_Named()
    {
        var n = Assert.IsType<NullableTypeSyntax>(ParseAnnotation("int?"));
        Assert.Equal("int", Assert.IsType<NamedTypeSyntax>(n.Inner).Name);
    }

    [Fact]
    public void Nullable_Generic()
    {
        // `List<int>?` — the trailing `?` wraps after ANY complete form.
        var n = Assert.IsType<NullableTypeSyntax>(ParseAnnotation("List<int>?"));
        Assert.IsType<GenericTypeSyntax>(n.Inner);
    }

    [Fact]
    public void Nullable_Tuple()
    {
        var n = Assert.IsType<NullableTypeSyntax>(ParseAnnotation("(int, string)?"));
        Assert.IsType<TupleTypeSyntax>(n.Inner);
    }

    [Fact]
    public void Nullable_FnPtr()
    {
        var n = Assert.IsType<NullableTypeSyntax>(ParseAnnotation("&(int -> bool)?"));
        Assert.IsType<FunctionPointerTypeSyntax>(n.Inner);
    }

    [Fact]
    public void Pointer_Mutable()
    {
        var p = Assert.IsType<PointerTypeSyntax>(ParseAnnotation("*Box"));
        Assert.False(p.ReadOnly);
        Assert.Equal("Box", Assert.IsType<NamedTypeSyntax>(p.Inner).Name);
    }

    [Fact]
    public void Pointer_ReadOnly()
    {
        var p = Assert.IsType<PointerTypeSyntax>(ParseAnnotation("readonly *Box"));
        Assert.True(p.ReadOnly);
        Assert.Equal("Box", Assert.IsType<NamedTypeSyntax>(p.Inner).Name);
    }

    [Fact]
    public void Pointer_ToNullable()
    {
        // Prefix binds before suffix: `*T?` is pointer-to-nullable.
        var p = Assert.IsType<PointerTypeSyntax>(ParseAnnotation("*Box?"));
        var n = Assert.IsType<NullableTypeSyntax>(p.Inner);
        Assert.Equal("Box", Assert.IsType<NamedTypeSyntax>(n.Inner).Name);
    }

    [Fact]
    public void Pointer_InGenericArg()
    {
        var g = Assert.IsType<GenericTypeSyntax>(ParseAnnotation("List<*Box>"));
        var p = Assert.IsType<PointerTypeSyntax>(Assert.Single(g.Args));
        Assert.Equal("Box", Assert.IsType<NamedTypeSyntax>(p.Inner).Name);
    }

    [Fact]
    public void Pointer_OfGeneric()
    {
        var p = Assert.IsType<PointerTypeSyntax>(ParseAnnotation("*Wrap<int>"));
        Assert.Equal("Wrap", Assert.IsType<GenericTypeSyntax>(p.Inner).Name);
    }

    [Fact]
    public void Chan_IsGenericKeywordBase()
    {
        var g = Assert.IsType<GenericTypeSyntax>(ParseAnnotation("chan<int>"));
        Assert.Equal("chan", g.Name);
        Assert.Equal("int", Assert.IsType<NamedTypeSyntax>(Assert.Single(g.Args)).Name);
    }

    [Fact]
    public void Chan_Nullable()
    {
        var n = Assert.IsType<NullableTypeSyntax>(ParseAnnotation("chan<int>?"));
        Assert.Equal("chan", Assert.IsType<GenericTypeSyntax>(n.Inner).Name);
    }

    [Fact]
    public void DeepComposite_EveryNodeSpanStamped()
    {
        // Every node in a deep composite carries a valid span — the LSP contract.
        var t = Assert.IsType<TupleTypeSyntax>(ParseAnnotation("(List<*Box?>, &(int -> bool))"));
        void Walk(TypeSyntax node)
        {
            Assert.True(node.Span.IsValid, $"{node.GetType().Name} span");
            Assert.Equal("type.es", node.Span.File);
            switch (node)
            {
                case GenericTypeSyntax g: foreach (var a in g.Args) Walk(a); break;
                case TupleTypeSyntax tu: foreach (var e in tu.Elements) Walk(e); break;
                case FunctionPointerTypeSyntax f:
                    foreach (var pt in f.ParameterTypes) Walk(pt);
                    Walk(f.ReturnType); break;
                case PointerTypeSyntax p: Walk(p.Inner); break;
                case NullableTypeSyntax nu: Walk(nu.Inner); break;
            }
        }
        Walk(t);
    }

    [Fact]
    public void ReturnType_ParsesStructurally()
    {
        var parser = new Parser("namespace T\nfunc f() -> List<(int, string)> { return [] }\n");
        var unit = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var fn = Assert.Single(unit.Members.OfType<FunctionDeclarationSyntax>());
        var g = Assert.IsType<GenericTypeSyntax>(fn.ReturnType);
        Assert.IsType<TupleTypeSyntax>(Assert.Single(g.Args));
    }

    [Fact]
    public void FieldAnnotation_ParsesStructurally()
    {
        var parser = new Parser("namespace T\nstruct D {\n    m: Dictionary<string, List<int>>\n}\n");
        var unit = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var d = Assert.Single(unit.Members.OfType<DataDeclarationSyntax>());
        var g = Assert.IsType<GenericTypeSyntax>(Assert.Single(d.Fields).Type);
        Assert.Equal("Dictionary", g.Name);
        Assert.IsType<GenericTypeSyntax>(g.Args[1]);
    }

    [Fact]
    public void LocalAnnotation_VarKeyword_IsInferredNode()
    {
        var parser = new Parser("namespace T\nfunc f(x: var) { }\n");
        var unit = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var fn = Assert.Single(unit.Members.OfType<FunctionDeclarationSyntax>());
        Assert.IsType<InferredTypeSyntax>(Assert.Single(fn.Parameters).Type);
    }

    [Fact]
    public void DataInterfaces_AreStructuredNodes()
    {
        var parser = new Parser("namespace T\ninterface IShape { func area() -> int }\nstruct Circle : IShape {\n    r: int\n    func area() -> int { return 3 * this.r }\n}\n");
        var unit = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var d = Assert.Single(unit.Members.OfType<DataDeclarationSyntax>(), x => x.Name == "Circle");
        Assert.Equal("IShape", Assert.IsType<NamedTypeSyntax>(Assert.Single(d.Interfaces)).Name);
    }

    // ════════════════════════════════════════════════════════════════════════
    // B. End-to-end — the same shapes through bind + IL emit + ILVerify + run.
    // ════════════════════════════════════════════════════════════════════════

    static object? Run(string source, string method = "go") => EsHarness.Run(source, method);

    const string WRAP = """
namespace Test
struct Wrap<T> { v: T }
func (w: Wrap<T>) mapped<T, U>(f: Func<T, U>) -> Wrap<U> = Wrap<U> { v: f(w.v) }

""";

    [Fact]
    public void PromotedGeneric_StringReceiver_ToInt() => Assert.Equal(5, Run(WRAP + """
func go() -> int {
    let w = Wrap<string> { v: "hello" }
    return w.mapped((s) => s.Length).v
}
"""));

    [Fact]
    public void PromotedGeneric_CrossType_RoundTrip() => Assert.Equal(3, Run(WRAP + """
func go() -> int {
    let w = Wrap<int> { v: 12 }
    return w.mapped((x) => x.ToString()).mapped((s) => s.Length + 1).v
}
"""));

    [Fact]
    public void PromotedGeneric_BoolResult() => Assert.Equal(true, Run(WRAP + """
func go() -> bool {
    let w = Wrap<int> { v: 9 }
    return w.mapped((x) => x > 5).v
}
"""));

    [Fact]
    public void PromotedGeneric_TwoReceiverTypeParams() => Assert.Equal(7, Run("""
namespace Test
struct Pair<A, B> { first: A, second: B }
func (p: Pair<A, B>) swapped<A, B>() -> Pair<B, A> = Pair<B, A> { first: p.second, second: p.first }

func go() -> int {
    let p = Pair<string, int> { first: "x", second: 7 }
    return p.swapped().first
}
"""));

    [Fact]
    public void GenericField_SubstitutesClosedArgs() => Assert.Equal(8, Run("""
namespace Test
struct Pair<A, B> { first: A, second: B }

func go() -> int {
    let p = Pair<string, int> { first: "abc", second: 5 }
    return p.first.Length + p.second
}
"""));

    [Fact]
    public void ExplicitTypeArgs_CloseDeclaredReturn() => Assert.Equal(42, Run("""
namespace Test
func id<T>(x: T) -> T { return x }

func go() -> int {
    let n = id<int>(41)
    return n + 1
}
"""));

    [Fact]
    public void GenericCallArgs_AngleVsComparison_Disambiguates() => Assert.Equal(1, Run("""
namespace Test
func two(x: bool, y: bool) -> int {
    if x && y { return 1 }
    return 0
}

func go() -> int {
    let a = 1
    let b = 2
    let c = 3
    let d = 2
    return two(a < b, c > d)
}
"""));

    [Fact]
    public void Tuple_ReturnAndItemAccess() => Assert.Equal(8, Run("""
namespace Test
func pair() -> (int, string) { return (5, "abc") }

func go() -> int {
    let t = pair()
    return t.Item1 + t.Item2.Length
}
"""));

    [Fact]
    public void Nullable_ParamAndNilCheck() => Assert.Equal(2, Run("""
namespace Test
func maybe(x: int?) -> int {
    if x == nil { return 1 }
    return 2
}

func go() -> int { return maybe(7) }
"""));

    [Fact]
    public void FnPtr_ParamTypechecksAndInvokes() => Assert.Equal(42, Run("""
namespace Test
func twice(x: int) -> int { return x * 2 }
func apply(f: &(int -> int), value: int) -> int { return f(value) }

func go() -> int { return apply(&twice, 21) }
"""));

    [Fact]
    public void FnPtr_VoidReturnForm() => Assert.Equal(5, Run("""
namespace Test
func push(xs: List<int>, v: int) { xs.Add(v) }
func run(f: &(List<int>, int), xs: List<int>, n: int) { f(xs, n) }

func go() -> int {
    var xs = List<int>()
    run(&push, xs, 5)
    return xs[0]
}
"""));

    [Fact]
    public void Result_IntrinsicGeneric() => Assert.Equal(10, Run("""
namespace Test
func half(x: int) -> Result<int, string> {
    if x % 2 == 0 { return ok(x / 2) }
    return error("odd")
}

func go() -> int {
    let r = half(20)
    if r.IsOk { return r.Value }
    return -1
}
"""));

    [Fact]
    public void HeapPointer_AsGenericArg() => Assert.Equal(9, Run("""
namespace Test
struct Box { var v: int }

func go() -> int {
    var xs = List<*Box>()
    xs.Add(new Box { v: 4 })
    xs[0].v = 9
    return xs[0].v
}
"""));

    [Fact]
    public void ReadonlyPointer_ParamReads() => Assert.Equal(6, Run("""
namespace Test
struct Box { var v: int }
func peek(b: readonly *Box) -> int { return b.v }

func go() -> int {
    let b = Box { v: 6 }
    return peek(&b)
}
"""));

    [Fact]
    public void CatchClause_TypedException() => Assert.Equal(3, Run("""
namespace Test
func go() -> int {
    try {
        throw InvalidOperationException("boom")
    } catch (e: InvalidOperationException) {
        return 3
    }
    return 0
}
"""));

    [Fact]
    public void DefaultExpression_PrimitiveAndString() => Assert.Equal(1, Run("""
namespace Test
func go() -> int {
    let n = default(int)
    let s = default(string)
    if n == 0 && s == nil { return 1 }
    return 0
}
"""));

    [Fact]
    public void QualifiedType_CrossNamespaceAnnotation()
    {
        var (asm, diags) = CompileMulti("""
namespace A
struct Widget { size: int }
""", """
namespace Test
using "A"
func (w: A.Widget) grow() -> int { return w.size + 1 }

func go() -> int {
    let w = Widget { size: 10 }
    return grow(w)
}
""");
        Assert.True(asm is not null, string.Join("\n", diags));
        Assert.Equal(11, EsHarness.Invoke(asm!, "go"));
    }

    [Fact]
    public void QualifiedGeneric_CrossNamespaceAnnotation()
    {
        var (asm, diags) = CompileMulti("""
namespace A
struct Holder<T> { item: T }
""", """
namespace Test
using "A"
func get(h: A.Holder<int>) -> int { return h.item }

func go() -> int {
    let h = Holder<int> { item: 21 }
    return get(h)
}
""");
        Assert.True(asm is not null, string.Join("\n", diags));
        Assert.Equal(21, EsHarness.Invoke(asm!, "go"));
    }

    /// Multi-unit compile (cross-namespace facts): same pipeline shape as the
    /// single-source EsHarness, with verification on.
    static (System.Reflection.Assembly? Asm, IReadOnlyList<Esharp.Diagnostics.Diagnostic> Diags) CompileMulti(params string[] sources)
    {
        var asmName = $"EsTypeGrammar_{Interlocked.Increment(ref _asmCounter)}";
        var binder = new Esharp.Binder.Binder();
        var parsed = new List<CompilationUnitSyntax>();
        for (var i = 0; i < sources.Length; i++)
        {
            var parser = new Parser(sources[i], $"tg{i}.es");
            var unit = parser.ParseCompilationUnit();
            Assert.Empty(parser.Diagnostics);
            parsed.Add(unit);
        }
        foreach (var u in parsed) binder.RegisterTypes(u);
        foreach (var u in parsed) binder.RegisterSignatures(u);
        var bound = parsed.Select(binder.BindUnit).ToList();
        if (binder.Diagnostics.Any(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error))
            return (null, binder.Diagnostics.ToList());
        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        var ilDiags = EsHarness.EmitBoundToFile(binder, bound, asmName, path, verify: true);
        var all = binder.Diagnostics.Concat(ilDiags).ToList();
        if (all.Any(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error))
            return (null, all);
        return (System.Reflection.Assembly.LoadFrom(path), all);
    }
    static int _asmCounter;

    [Fact]
    public void Chan_AnnotatedParamRoundTrip() => Assert.Equal(13, Run("""
namespace Test
func pump(ch: chan<int>, v: int) { ch.Send(v) }

func go() -> int {
    let ch = chan<int>(1)
    pump(ch, 13)
    var v = 0
    if ch.TryReceive(out v) { return v }
    return -1
}
"""));

    [Fact]
    public void Select_DefaultArm_FiresOnEmptyChannel() => Assert.Equal(99, Run("""
namespace Test
func go() -> int {
    let ch = chan<int>(1)
    var fired = 0
    select {
        .recv(v, ch) { fired = 1 }
        default      { fired = 99 }
    }
    return fired
}
"""));

    [Fact]
    public void Select_RecvArm_FiresOnReadyChannel() => Assert.Equal(42, Run("""
namespace Test
func go() -> int {
    let ch = chan<int>(1)
    ch.Send(42)
    var got = 0
    select {
        .recv(v, ch) { got = v }
        default      { got = -1 }
    }
    return got
}
"""));

    [Fact]
    public void NestedGeneric_AnnotationBindsAndRuns() => Assert.Equal(6, Run("""
namespace Test
func total(xss: List<List<int>>) -> int {
    var sum = 0
    for xs in xss {
        for x in xs {
            sum = sum + x
        }
    }
    return sum
}

func go() -> int {
    var inner1 = List<int>()
    inner1.Add(1)
    inner1.Add(2)
    var inner2 = List<int>()
    inner2.Add(3)
    var xss = List<List<int>>()
    xss.Add(inner1)
    xss.Add(inner2)
    return total(xss)
}
"""));

    // ════════════════════════════════════════════════════════════════════════
    // C. Error recovery — diagnostics, never crashes, never silent emission.
    // ════════════════════════════════════════════════════════════════════════

    static IReadOnlyList<Esharp.Diagnostics.Diagnostic> ParseDiags(string source)
    {
        var parser = new Parser(source, "err.es");
        parser.ParseCompilationUnit(); // must terminate — forward progress is the contract
        return parser.Diagnostics;
    }

    [Fact]
    public void SelectArm_UnknownKind_Reports()
    {
        var diags = ParseDiags("""
namespace Test
func go() {
    let ch = chan<int>(1)
    select {
        .poll(v, ch) { }
        default { }
    }
}
""");
        Assert.Contains(diags, d => d.Message.Contains("Unknown select arm kind 'poll'"));
    }

    [Fact]
    public void Expression_UnexpectedToken_ReportsAndRecovers()
    {
        var diags = ParseDiags("""
namespace Test
func go() -> int {
    let x = ;
    return 1
}
""");
        Assert.Contains(diags, d => d.Message.Contains("Unexpected token"));
    }

    [Fact]
    public void Member_Unexpected_ReportsAndContinues()
    {
        // The bad token becomes an ErrorMemberSyntax; the NEXT member still parses.
        var parser = new Parser("""
namespace Test
%%%
func go() -> int { return 1 }
""", "err.es");
        var unit = parser.ParseCompilationUnit();
        Assert.Contains(parser.Diagnostics, d => d.Message.Contains("Unexpected member"));
        Assert.Contains(unit.Members, m => m is ErrorMemberSyntax);
        Assert.Contains(unit.Members, m => m is FunctionDeclarationSyntax { Name: "go" });
    }

    [Fact]
    public void ErrorMember_CarriesValidSpan()
    {
        // `%%%` lexes to three tokens — one ErrorMemberSyntax per token, each
        // with a real location.
        var parser = new Parser("namespace Test\n%%%\n", "err.es");
        var unit = parser.ParseCompilationUnit();
        var errs = unit.Members.OfType<ErrorMemberSyntax>().ToList();
        Assert.NotEmpty(errs);
        Assert.All(errs, e => Assert.True(e.Span.IsValid));
    }

    [Fact]
    public void MissingTypeName_ReportsAndRecovers()
    {
        var diags = ParseDiags("""
namespace Test
func go(x: ) -> int { return 1 }
""");
        Assert.Contains(diags, d => d.Message.Contains("Expected type name."));
    }

    [Fact]
    public void GarbageSoup_TerminatesWithDiagnostics()
    {
        // Pathological input must terminate (cursor forward-progress guarantee)
        // with diagnostics, never hang or throw.
        var diags = ParseDiags("namespace Test\n} ) > ;; func ( < data %% choice -> *? &( match {{{\n");
        Assert.NotEmpty(diags);
    }

    [Fact]
    public void ParseError_GatesEmission()
    {
        // A parse error means NO emit-phase diagnostics can appear: the harness
        // (like the pipeline) never hands an error-recovery tree to the backend.
        var all = EsHarness.AllDiagnostics("""
namespace Test
func go() -> int {
    let x = ;
    return 1
}
""");
        Assert.Contains(all, d => d.Message.Contains("Unexpected token"));
        Assert.DoesNotContain(all, d => d.Code == "ES0900");
    }

    [Fact]
    public void ErrorExpression_BindsWithoutDuplicateDiagnostic()
    {
        // One parse diagnostic, zero binder additions: BoundErrorExpression is
        // silent (the parser already spoke).
        var parser = new Parser("""
namespace Test
func go() -> int {
    let x = ;
    return 1
}
""", "err.es");
        var unit = parser.ParseCompilationUnit();
        var parseCount = parser.Diagnostics.Count;
        Assert.True(parseCount > 0);
        var binder = new Esharp.Binder.Binder();
        binder.Bind(unit);
        Assert.DoesNotContain(binder.Diagnostics, d => d.Message.Contains("Unexpected token"));
    }
}
