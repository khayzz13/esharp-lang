using Esharp.Diagnostics.Semantics;
using Esharp.Symbols;
using Esharp.Compilation;

namespace Esharp.Tests;

/// The semantic model is a by-product of binding — a single bind with a collecting
/// sink answers position → symbol, symbol → references, and position → in-scope
/// symbols, all by reference identity over interned symbols. These pin the public
/// query contract; coverage deepens underneath it as the sink widens.
public sealed class SemanticModelTests
{
    static (SemanticModel Model, string Uri, Esharp.Compilation.SourceText Text) Bind(string source)
    {
        var ws = new Workspace("SemTest");
        var uri = "sem.es";
        var doc = ws.AddDocument(uri, source);
        return (ws.CurrentCompilation.GetSemanticModel(), uri, doc.Text);
    }

    static int Offset(Esharp.Compilation.SourceText text, string needle, int which = 0)
    {
        var idx = -1;
        for (var i = 0; i <= which; i++)
            idx = text.Content.IndexOf(needle, idx + 1, StringComparison.Ordinal);
        return idx;
    }

    // GetSymbolAt on a type's declaration and on a use of that type return the SAME
    // interned symbol — the identity hover / go-to-definition stand on.
    [Fact]
    public void GetSymbolAt_TypeDeclarationAndUse_SameSymbol()
    {
        var (model, uri, text) = Bind("""
namespace Test
struct Point { x: int, y: int }
func origin() -> Point = Point { x: 0, y: 0 }
""");
        // Use site: the `Point` return annotation.
        var usePos = Offset(text, "Point", which: 1); // first use after the decl
        var useSym = model.GetSymbolAt(uri, usePos);
        Assert.NotNull(useSym);
        Assert.Equal("Point", useSym!.Name);
        Assert.IsType<TypeSymbol>(useSym);

        // Declaration site.
        var declPos = Offset(text, "Point", which: 0);
        var declSym = model.GetSymbolAt(uri, declPos);
        Assert.Same(useSym, declSym);
    }

    // FindReferences round-trips a function: its declaration plus every call site,
    // by reference identity (not a name search).
    [Fact]
    public void FindReferences_Function_RoundTripsDeclarationAndCalls()
    {
        var (model, uri, text) = Bind("""
namespace Test
func helper() -> int = 42
func a() -> int = helper()
func b() -> int = helper() + helper()
""");
        var callPos = Offset(text, "helper", which: 1); // first call
        var sym = model.GetSymbolAt(uri, callPos);
        Assert.NotNull(sym);
        Assert.IsType<MethodSymbol>(sym);

        var refs = model.FindReferences(sym!);
        // One declaration + three call sites.
        Assert.Equal(1, refs.Count(r => r.Occurrence == SymbolOccurrence.Declaration));
        Assert.Equal(3, refs.Count(r => r.Occurrence == SymbolOccurrence.Use));
    }

    // FindReferences on a local: declaration + each use.
    [Fact]
    public void FindReferences_Local_RoundTrips()
    {
        var (model, uri, text) = Bind("""
namespace Test
func go() -> int {
    let total = 10
    return total + total
}
""");
        var usePos = Offset(text, "total", which: 1); // first use
        var sym = model.GetSymbolAt(uri, usePos);
        Assert.NotNull(sym);
        var local = Assert.IsType<LocalSymbol>(sym);
        Assert.Equal("total", local.Name);

        var refs = model.FindReferences(sym!);
        Assert.Equal(1, refs.Count(r => r.Occurrence == SymbolOccurrence.Declaration));
        Assert.Equal(2, refs.Count(r => r.Occurrence == SymbolOccurrence.Use));
    }

    // FindReferences on a field: declaration + member-access uses.
    [Fact]
    public void FindReferences_Field_RoundTrips()
    {
        var (model, uri, text) = Bind("""
namespace Test
struct Box { v: int }
func (b: Box) read() -> int = b.v + b.v
""");
        var usePos = Offset(text, ".v", which: 0) + 1; // the `v` member
        var sym = model.GetSymbolAt(uri, usePos);
        Assert.NotNull(sym);
        Assert.IsType<FieldSymbol>(sym);
        Assert.Equal("v", sym!.Name);

        var refs = model.FindReferences(sym!);
        Assert.Equal(1, refs.Count(r => r.Occurrence == SymbolOccurrence.Declaration));
        Assert.True(refs.Count(r => r.Occurrence == SymbolOccurrence.Use) >= 2);
    }

    // LookupSymbolsInScope sees a local AFTER its declaration, not before, and sees
    // the function's parameters throughout.
    [Fact]
    public void LookupSymbolsInScope_SeesLocalAfterDeclaration()
    {
        var (model, uri, text) = Bind("""
namespace Test
func go(seed: int) -> int {
    let mid = seed + 1
    let last = mid + 1
    return last
}
""");
        // At the `return`, both locals + the parameter are in view.
        var returnPos = Offset(text, "return last", 0);
        var atReturn = model.LookupSymbolsInScope(uri, returnPos).Select(s => s.Name).ToHashSet();
        Assert.Contains("seed", atReturn);
        Assert.Contains("mid", atReturn);
        Assert.Contains("last", atReturn);

        // At the declaration of `mid`, `last` is not yet in scope.
        var midPos = Offset(text, "let mid", 0);
        var atMid = model.LookupSymbolsInScope(uri, midPos).Select(s => s.Name).ToHashSet();
        Assert.Contains("seed", atMid);
        Assert.DoesNotContain("last", atMid);
    }

    // Namespace-visible declarations (types, free functions) appear in scope.
    [Fact]
    public void LookupSymbolsInScope_SeesNamespaceDeclarations()
    {
        var (model, uri, text) = Bind("""
namespace Test
struct Widget { id: int }
func make() -> Widget = Widget { id: 1 }
func use() -> int {
    let w = make()
    return w.id
}
""");
        var pos = Offset(text, "return w.id", 0);
        var names = model.LookupSymbolsInScope(uri, pos).Select(s => s.Name).ToHashSet();
        Assert.Contains("Widget", names);
        Assert.Contains("make", names);
        Assert.Contains("w", names);
    }

    // SymbolDisplay.Describe renders an LSP hover line per symbol kind, in E# spelling.
    [Fact]
    public void Describe_RendersHoverLines()
    {
        var (model, uri, text) = Bind("""
namespace Test
struct Point { x: int, y: int }
func (p: Point) dist() -> int = p.x + p.y
""");
        var typeSym = model.GetSymbolAt(uri, Offset(text, "Point", 0))!;
        Assert.Equal("struct Point", SymbolDisplay.Describe(typeSym));

        var fieldSym = model.GetSymbolAt(uri, Offset(text, ".x", 0) + 1)!;
        Assert.Equal("x: int", SymbolDisplay.Describe(fieldSym));

        var funcSym = model.GetSymbolAt(uri, Offset(text, "dist", 0))!;
        Assert.Equal("func (p: Point) dist() -> int", SymbolDisplay.Describe(funcSym));
    }

    // DeclarationsIn enumerates a file's declarations in source order — the document
    // outline (LSP documentSymbol). Locals are present (the caller filters by kind).
    [Fact]
    public void DeclarationsIn_EnumeratesFileDeclarationsInOrder()
    {
        var (model, uri, text) = Bind("""
namespace Test
struct Point { x: int, y: int }
func (p: Point) dist() -> int = p.x + p.y
const LIMIT = 10
""");
        var decls = model.DeclarationsIn(uri);
        var names = decls.Select(d => d.Symbol.Name).ToList();
        Assert.Contains("Point", names);
        Assert.Contains("dist", names);

        // Source order: the type precedes the function.
        Assert.True(names.IndexOf("Point") < names.IndexOf("dist"));

        // Every entry is a declaration occurrence in this file.
        Assert.All(decls, d => Assert.Equal(SymbolOccurrence.Declaration, d.Occurrence));
        Assert.All(decls, d => Assert.Equal(uri, d.Span.File));

        // Another file's name never appears.
        Assert.Empty(model.DeclarationsIn("other.es"));
    }

    // GetTypeOf maps a binding to its interned type symbol — the completion data source
    // (the member surface to offer after `x.`).
    [Fact]
    public void GetTypeOf_MapsBindingToTypeSymbol()
    {
        var (model, uri, text) = Bind("""
namespace Test
struct Point { x: int, y: int }
func origin() -> Point {
    let p = Point { x: 0, y: 0 }
    return p
}
""");
        var localSym = model.GetSymbolAt(uri, Offset(text, "let p", 0) + 4)!;
        var typeOfP = model.GetTypeOf(localSym);
        Assert.NotNull(typeOfP);
        Assert.Equal("Point", typeOfP!.Name);

        // The type symbol exposes its fields — the member completion list after `p.`.
        Assert.Contains(typeOfP.Fields, f => f.Name == "x");
        Assert.Contains(typeOfP.Fields, f => f.Name == "y");
    }

    // Occurrences land on the NAME token, never the whole node: a declaration's span
    // is exactly its identifier, so hover on a keyword or a body resolves to nothing.
    [Fact]
    public void Occurrences_LandOnTheNameToken()
    {
        var (model, uri, text) = Bind("""
namespace Test
struct Point { x: int, y: int }
func (p: Point) dist() -> int = p.x + p.y
""");
        // The data declaration's occurrence covers `Point`, not `struct Point { … }`.
        var declSym = model.GetSymbolAt(uri, Offset(text, "Point", 0));
        Assert.IsType<TypeSymbol>(declSym);
        var decl = model.DeclarationsIn(uri).First(d => d.Symbol.Name == "Point");
        Assert.Equal("Point".Length, decl.Span.Length);

        // The `struct` keyword itself resolves to NO symbol.
        Assert.Null(model.GetSymbolAt(uri, Offset(text, "struct", 0)));
        // Inside the field list, the `int` annotation answers the PRIMITIVE type,
        // never the enclosing data declaration.
        var fieldTypePos = Offset(text, "x: int", 0) + 3;
        var annotation = Assert.IsType<TypeSymbol>(model.GetSymbolAt(uri, fieldTypePos));
        Assert.Equal("int", annotation.Name);
    }

    // A method declared INSIDE a `class` body resolves to the method, never the
    // enclosing type — and its declaration span is the method's name token.
    [Fact]
    public void GetSymbolAt_InlineRefDataMethod_ResolvesTheMethod()
    {
        var (model, uri, text) = Bind("""
namespace Test
class Counter {
    n: int
    init(n: int) { self.n = n }
    func bump(by: int) -> int {
        return self.n + by
    }
}
""");
        var sym = model.GetSymbolAt(uri, Offset(text, "bump", 0));
        var method = Assert.IsType<MethodSymbol>(sym);
        Assert.Equal("bump", method.Name);
        Assert.Equal("Counter", method.DeclaringType?.Name);
        Assert.StartsWith("method on Counter", SymbolDisplay.DescribeKind(method));
    }

    // A call's method-use occurrence covers the callee NAME, not the whole call —
    // hover on an argument resolves to the argument, not the function.
    [Fact]
    public void CallUse_CoversTheCalleeName_NotTheArguments()
    {
        var (model, uri, text) = Bind("""
namespace Test
func add(a: int, b: int) -> int = a + b
func go(x: int) -> int = add(x, 2)
""");
        var callPos = Offset(text, "add(", 0);
        Assert.IsType<MethodSymbol>(model.GetSymbolAt(uri, callPos));
        // The `x` argument inside the call is the parameter, never `add`.
        var argPos = Offset(text, "add(x", 0) + 4;
        var argSym = model.GetSymbolAt(uri, argPos);
        var local = Assert.IsType<LocalSymbol>(argSym);
        Assert.Equal("x", local.Name);
    }

    // BCL members carry interned external symbols: hover on `.Add` describes the
    // List method (not the receiver's type), and the receiver's type expands its
    // member surface for dot-completion.
    [Fact]
    public void ExternalMember_HoverAndCompletionSurface()
    {
        var (model, uri, text) = Bind("""
namespace Test
func go() -> int {
    let msgs = List<string>()
    msgs.Add("hi")
    return msgs.Count
}
""");
        // Hover on `Add` → the external MethodSymbol.
        var addSym = model.GetSymbolAt(uri, Offset(text, "Add", 0));
        var addMethod = Assert.IsType<MethodSymbol>(addSym);
        Assert.Equal("Add", addMethod.Name);
        Assert.Equal("method on List<string>", SymbolDisplay.DescribeKind(addMethod));

        // Hover on `Count` → the external property as a FieldSymbol.
        var countSym = model.GetSymbolAt(uri, Offset(text, "Count", 0));
        var countField = Assert.IsType<FieldSymbol>(countSym);
        Assert.Equal("Count", countField.Name);

        // Dot-completion: the local's type expands to the List<string> surface.
        var msgsSym = model.GetSymbolAt(uri, Offset(text, "msgs.Add", 0));
        Assert.NotNull(msgsSym);
        var recvType = model.GetTypeOf(msgsSym!);
        Assert.NotNull(recvType);
        Assert.Contains(recvType!.Members, m => m.Name == "Add");
        Assert.Contains(recvType.Fields, f => f.Name == "Count");
    }

    // Choice/enum cases are first-class occurrences: the declaration and the
    // `.case` use report the SAME interned CaseSymbol.
    [Fact]
    public void CaseOccurrences_DeclarationAndDotCaseUse_SameSymbol()
    {
        var (model, uri, text) = Bind("""
namespace Test
union Shape {
    circle(r: int)
    square(side: int)
}
func make() -> Shape = .circle(2)
""");
        var declSym = model.GetSymbolAt(uri, Offset(text, "circle", 0));
        var caseSym = Assert.IsType<CaseSymbol>(declSym);
        var useSym = model.GetSymbolAt(uri, Offset(text, "circle", 1));
        Assert.Same(caseSym, useSym);
        Assert.Equal(2, model.FindReferences(caseSym).Count);
    }

    // Match bindings, foreach variables, and let-else bindings are interned locals —
    // they hover and find-reference like any `let`.
    [Fact]
    public void BindingForms_AreInternedLocals()
    {
        var (model, uri, text) = Bind("""
namespace Test
func sum(items: List<int>) -> int {
    var total = 0
    for item in items {
        total = total + item
    }
    return total
}
""");
        var loopVar = model.GetSymbolAt(uri, Offset(text, "item in", 0));
        var local = Assert.IsType<LocalSymbol>(loopVar);
        Assert.Equal("item", local.Name);
        // Declaration + the body use.
        Assert.Equal(2, model.FindReferences(local).Count);
    }

    // A type annotation that names a BCL/primitive type answers the TYPE at that
    // position — hover after `->` names `string`, never the enclosing function.
    [Fact]
    public void ReturnTypeAnnotation_ResolvesTheType_NotTheFunction()
    {
        var (model, uri, text) = Bind("""
namespace Test
func greet(name: string) -> string = "hi {name}"
""");
        var retPos = Offset(text, "string", 1); // the `-> string` annotation
        var sym = model.GetSymbolAt(uri, retPos);
        var ts = Assert.IsType<TypeSymbol>(sym);
        Assert.Equal("string", ts.Name);
    }
}
