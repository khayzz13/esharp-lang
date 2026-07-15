using Binder = Esharp.Binder.Binder;
using Esharp.Syntax.Parsing;
using Esharp.BoundTree;        // BoundProgram
using Esharp.Binder;           // CompilationData
using Esharp.Compilation;      // CompilationPipeline
using Esharp.Diagnostics.Semantics;
using Esharp.Symbols;

namespace Esharp.Tests;

/// Facts about the symbol spine made load-bearing by the compiler reorganization.
/// Distinct from SymbolSpineTests, which are end-to-end regression reproductions;
/// these assert the spine machinery itself — CoreTypes interning and the method sets
/// populated onto TypeSymbol during binding.
public sealed class SpineTests
{
    // CoreTypes hands back the SAME symbol instance for a primitive every time:
    // identity is reference identity, the stamp the whole reorg leans on.
    [Fact]
    public void CoreTypes_PrimitiveSymbol_IsInternedByReference()
    {
        var core = new CoreTypes();
        Assert.Same(core.Int, core.Int);
        Assert.Same(core.Int, core.Primitive("int"));
        Assert.NotSame(core.Int, core.Long);
    }

    // Externally named types are interned by both metadata name and arity, so a
    // stdlib class and its generic sibling (`Spawned` / `Spawned<T>`) never alias.
    [Fact]
    public void CoreTypes_ExternalSpawnedArities_AreDistinct()
    {
        var core = new CoreTypes();
        var untyped = core.External("Spawned", 0);
        var typed = core.External("Spawned", 1);
        Assert.NotSame(untyped, typed);
        Assert.Equal(0, untyped.Arity);
        Assert.Equal(1, typed.Arity);
    }

    [Fact]
    public void CoreTypes_NonPrimitiveName_ResolvesNull()
    {
        var core = new CoreTypes();
        Assert.Null(core.Primitive("Widget"));
        Assert.False(core.IsPrimitiveName("void"));   // void is excluded from the lookup
        Assert.True(core.IsPrimitiveName("string"));
    }

    [Fact]
    public void CoreTypes_TupleSymbol_InternsPerArity()
    {
        var core = new CoreTypes();
        Assert.Same(core.Tuple(2), core.Tuple(2));
        Assert.NotSame(core.Tuple(2), core.Tuple(3));
    }

    static BoundProgram BindThroughPipeline(string source, CompilationData data)
    {
        var parser = new Parser(source, "spine.es");
        var unit = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        return new CompilationPipeline(data).Bind([unit]);
    }

    // The spine made load-bearing (step 3): a promoted free function becomes a
    // MethodSymbol attached to its receiver's TypeSymbol.Members at signature time,
    // with the receiver kind and declared arity carried on the symbol.
    [Fact]
    public void PromotedFunction_PopulatesReceiverMethodSet()
    {
        var data = new CompilationData();
        var program = BindThroughPipeline("""
namespace Test
struct Box { var v: int }
func (b: Box) tenfold() -> int = b.v * 10
func (b: *Box) bump() { b.v += 1 }
""", data);
        Assert.False(program.HasErrors);

        var box = data.Symbols.TryGet("Box", 0, "Test");
        Assert.NotNull(box);
        Assert.Equal(2, box!.Members.Count);

        var tenfold = Assert.Single(box.Members, m => m.Name == "tenfold");
        Assert.Equal(ReceiverKind.Value, tenfold.ReceiverKind);
        Assert.Equal(1, tenfold.DeclaredArity);
        Assert.Same(box, tenfold.DeclaringType);

        var bump = Assert.Single(box.Members, m => m.Name == "bump");
        Assert.Equal(ReceiverKind.Pointer, bump.ReceiverKind);
    }

    // Namespace-local promotion gate, symbol-side: a function whose receiver type
    // lives in ANOTHER namespace does not promote, so no MethodSymbol attaches.
    [Fact]
    public void CrossNamespaceFunction_DoesNotAttachToReceiverSymbol()
    {
        var data = new CompilationData();
        var program = BindThroughPipeline("""
namespace A
struct Widget { var v: int }
""" + "\n", data);
        Assert.False(program.HasErrors);
        var parser = new Parser("""
namespace B
using "A"
func (w: Widget) describe() -> int = w.v
""", "b.es");
        var unitB = parser.ParseCompilationUnit();
        var program2 = new CompilationPipeline(data).Bind([unitB]);
        Assert.False(program2.HasErrors);

        var widget = data.Symbols.TryGet("Widget", 0, "A");
        Assert.NotNull(widget);
        Assert.DoesNotContain(widget!.Members, m => m.Name == "describe");
        Assert.Null(data.Symbols.TryGetPromoted("describe"));
    }

    // The tooling tap (step 6, F#'s TcResultsSink): binding with a collecting sink
    // yields type declarations, the promoted method's declaration, and the call
    // site's use — each with a valid source span — with no second pipeline.
    [Fact]
    public void SemanticSink_CollectsDeclarationsAndPromotedUse()
    {
        var sink = new CollectingSemanticSink();
        var data = new CompilationData { Sink = sink };
        var program = BindThroughPipeline("""
namespace Test
struct Box { var v: int }
func (b: Box) tenfold() -> int = b.v * 10
func go() -> int {
    let p = Box { v: 5 }
    return p.tenfold()
}
""", data);
        Assert.False(program.HasErrors);

        var types = sink.Occurrences.Where(o => o.Symbol is Esharp.Symbols.TypeSymbol).ToList();
        var methods = sink.Occurrences.Where(o => o.Symbol is Esharp.Symbols.MethodSymbol).ToList();

        var boxDecl = Assert.Single(types, t => t.Symbol.Name == "Box" && t.Occurrence == SymbolOccurrence.Declaration);
        Assert.True(boxDecl.Span.IsValid);

        Assert.Contains(methods, m =>
            m.Symbol.Name == "tenfold" && m.Occurrence == SymbolOccurrence.Declaration);
        var use = Assert.Single(methods, m => m.Symbol.Name == "tenfold" && m.Occurrence == SymbolOccurrence.Use);
        Assert.Equal("tenfold", use.Symbol.Name);
        // Declaration and use report the SAME interned symbol — reference identity
        // is what makes find-references a list lookup instead of a name search.
        Assert.Same(
            methods.First(m => m.Occurrence == SymbolOccurrence.Declaration && m.Symbol.Name == "tenfold").Symbol,
            use.Symbol);
    }

    // Type annotations are structured nodes with real source locations — the
    // contract a future LSP hover/go-to-definition on a type name depends on.
    [Fact]
    public void TypeAnnotations_CarryValidSpans()
    {
        var parser = new Parser("""
namespace Test
struct Wrap<T> { v: T }
func head(xs: List<*Wrap<int>>, f: &(int -> bool)) -> (int, string)? {
    return nil
}
""", "spans.es");
        var unit = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var fn = Assert.Single(unit.Members.OfType<Esharp.Syntax.FunctionDeclarationSyntax>(),
            f => f.Name == "head");

        foreach (var p in fn.Parameters)
        {
            Assert.True(p.Type.Span.IsValid, $"parameter '{p.Name}' type span");
            Assert.Equal("spans.es", p.Type.Span.File);
        }
        Assert.True(fn.ReturnType.Span.IsValid, "return type span");

        // Nested type nodes carry spans too, not just the outermost annotation.
        var listArg = Assert.IsType<Esharp.Syntax.GenericTypeSyntax>(fn.Parameters[0].Type);
        var pointerElem = Assert.Single(listArg.Args);
        Assert.True(pointerElem.Span.IsValid, "nested type-argument span");
    }
}
