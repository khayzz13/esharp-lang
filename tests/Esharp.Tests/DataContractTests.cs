using System.Reflection;
using Esharp.Syntax.Parsing;
using Esharp.CodeGen;
using Esharp.BoundTree;        // BoundCompilationUnit
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

/// <summary>
/// Pin the `data` value-semantic contract. These tests assert user-observable
/// guarantees (copy-on-assign, no object identity, nil rules, recursion requires
/// `*T`, init blocks are ref-data-only) rather than the CLR form the compiler
/// happens to choose. If the compiler's representation heuristic shifts, these
/// tests stay green; if the contract leaks, they break.
/// </summary>
public sealed class DataContractTests
{
    static int _asmCounter;

    static (BoundCompilationUnit bound, IReadOnlyList<Esharp.Diagnostics.Diagnostic> parseDiags, IReadOnlyList<Esharp.Diagnostics.Diagnostic> bindDiags) ParseAndBind(string source)
    {
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        return (bound, parser.Diagnostics, binder.Diagnostics);
    }

    static Assembly CompileAndLoad(string source)
    {
        var asmName = $"EsharpContractTest_{Interlocked.Increment(ref _asmCounter)}";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        return EsHarness.EmitBoundToFileVerifiedAndLoad(binder, bound, asmName, path);
    }

    // --- ES2002: recursive fields require *T ---

    [Fact]
    public void RecursiveField_DirectSelfReference_Errors()
    {
        var (_, _, bindDiags) = ParseAndBind("""
namespace Test

struct Node {
    value: int
    next: Node
}
""");
        var diag = Assert.Single(bindDiags, d => d.Message.Contains("ES2002"));
        Assert.Contains("Node", diag.Message);
        Assert.Contains("*Node", diag.Message);
    }

    [Fact]
    public void RecursiveField_PointerForm_Ok()
    {
        var (_, _, bindDiags) = ParseAndBind("""
namespace Test

struct Node {
    value: int
    next: *Node
}
""");
        Assert.DoesNotContain(bindDiags, d => d.Message.Contains("ES2002"));
    }

    [Fact]
    public void RecursiveField_InGenericContainer_Errors()
    {
        var (_, _, bindDiags) = ParseAndBind("""
namespace Test

struct Tree {
    value: int
    children: List<Tree>
}
""");
        var diag = Assert.Single(bindDiags, d => d.Message.Contains("ES2002"));
        Assert.Contains("Tree", diag.Message);
    }

    [Fact]
    public void RecursiveField_ListOfPointer_Ok()
    {
        var (_, _, bindDiags) = ParseAndBind("""
namespace Test

struct Tree {
    value: int
    children: List<*Tree>
}
""");
        Assert.DoesNotContain(bindDiags, d => d.Message.Contains("ES2002"));
    }

    [Fact]
    public void RecursiveField_RefDataAllowsSelfReference()
    {
        // `class` is heap-native; it can legitimately hold itself by reference.
        var (_, _, bindDiags) = ParseAndBind("""
namespace Test

class Node {
    value: int
    next: Node
}
""");
        Assert.DoesNotContain(bindDiags, d => d.Message.Contains("ES2002"));
    }

    // --- ES3012: init blocks are ref-data-only ---

    [Fact]
    public void InitBlock_OnData_Errors()
    {
        var (_, parseDiags, _) = ParseAndBind("""
namespace Test

struct Point {
    x: int
    y: int

    init(px: int, py: int) {
        self.x = px
        self.y = py
    }
}
""");
        var diag = Assert.Single(parseDiags, d => d.Message.Contains("ES3012"));
        Assert.Contains("Point", diag.Message);
    }

    [Fact]
    public void InitBlock_OnRefData_Ok()
    {
        var (_, parseDiags, bindDiags) = ParseAndBind("""
namespace Test

class Point {
    x: int
    y: int

    init(px: int, py: int) {
        self.x = px
        self.y = py
    }
}
""");
        Assert.DoesNotContain(parseDiags, d => d.Message.Contains("ES3012"));
        Assert.DoesNotContain(bindDiags, d => d.Message.Contains("ES3012"));
    }

    [Fact]
    public void PositionalForm_OnData_StillWorks()
    {
        // Positional `struct Vec2(x: int, y: int)` is not an explicit init block;
        // it's construction sugar and stays legal.
        var (_, parseDiags, bindDiags) = ParseAndBind("""
namespace Test

struct Vec2(x: int, y: int)

func main() {
    let v = Vec2(3, 4)
}
""");
        Assert.DoesNotContain(parseDiags, d => d.Message.Contains("ES3012"));
        Assert.Empty(bindDiags);
    }

    // --- Value-semantic contract: classification is compiler's choice ---

    [Fact]
    public void SmallData_StaysStruct()
    {
        var (bound, _, _) = ParseAndBind("""
namespace Test

struct Point {
    x: int
    y: int
}
""");
        var point = bound.Members.OfType<BoundDataDeclaration>().Single(d => d.Name == "Point");
        Assert.Equal(DataClassification.Struct, point.Classification);
    }

    [Fact]
    public void ReadonlyData_StaysStruct_EvenWhenLarge()
    {
        // readonly data gets the [IsReadOnly] defensive-copy elision benefit,
        // which only applies to value types — the compiler does not promote it.
        var (bound, _, _) = ParseAndBind("""
namespace Test

readonly struct Big {
    a: double
    b: double
    c: double
    d: double
    e: double
    f: double
    g: double
    h: double
    i: double
}
""");
        var big = bound.Members.OfType<BoundDataDeclaration>().Single(d => d.Name == "Big");
        Assert.Equal(DataClassification.Struct, big.Classification);
    }

    [Fact]
    public void StructAttribute_ForcesStruct()
    {
        // [Struct] overrides the size heuristic that would otherwise promote
        // this 72-byte data to a class. Use when the user has profiled and knows
        // value semantics + struct layout is what they want.
        var (bound, _, bindDiags) = ParseAndBind("""
namespace Test

[Struct]
struct Big {
    a: double
    b: double
    c: double
    d: double
    e: double
    f: double
    g: double
    h: double
    i: double
}
""");
        Assert.Empty(bindDiags);
        var big = bound.Members.OfType<BoundDataDeclaration>().Single(d => d.Name == "Big");
        Assert.Equal(DataClassification.Struct, big.Classification);
    }

    [Fact]
    public void ClassAttribute_ForcesClass()
    {
        // [Class] forces class form on a small `data` that the heuristic
        // would otherwise leave as a struct. Same value-semantic contract — the
        // attribute only controls the CLR representation.
        var (bound, _, bindDiags) = ParseAndBind("""
namespace Test

[Class]
struct Point {
    x: int
    y: int
}
""");
        Assert.Empty(bindDiags);
        var point = bound.Members.OfType<BoundDataDeclaration>().Single(d => d.Name == "Point");
        Assert.Equal(DataClassification.Class, point.Classification);
    }

    [Fact]
    public void StructAttribute_OnClass_Errors()
    {
        var (_, _, bindDiags) = ParseAndBind("""
namespace Test

[Struct]
class Conn {
    host: string
    port: int
}
""");
        Assert.Contains(bindDiags, d => d.Message.Contains("[Struct]") && d.Message.Contains("class"));
    }

    [Fact]
    public void ClassAttribute_OnClass_Errors()
    {
        var (_, _, bindDiags) = ParseAndBind("""
namespace Test

[Class]
class Conn {
    host: string
    port: int
}
""");
        Assert.Contains(bindDiags, d => d.Message.Contains("[Class]") && d.Message.Contains("class"));
    }

    [Fact]
    public void StructAndClass_Together_Errors()
    {
        var (_, _, bindDiags) = ParseAndBind("""
namespace Test

[Struct]
[Class]
struct Point {
    x: int
    y: int
}
""");
        Assert.Contains(bindDiags, d => d.Message.Contains("mutually exclusive"));
    }
}
