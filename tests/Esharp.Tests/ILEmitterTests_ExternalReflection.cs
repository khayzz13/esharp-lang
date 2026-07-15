using Xunit;

namespace Esharp.Tests;

/// <summary>
/// Interop surfaces the `extract_corpus` tool (itself written in E#) leans on:
/// boxing into <c>List&lt;object&gt;</c>, <c>.ToArray()</c>, <c>.Count</c> on
/// concrete vs interface-typed collections (the <c>parser.Diagnostics.Count</c>
/// shape), and an int-valued external property inside string interpolation.
///
/// These were surfaced by the dogfood: the `.esproj` build path emits via the
/// Workspace (which does NOT run ILVerify), so a bad emit only blew up at JIT
/// time (InvalidProgramException). EsHarness compiles with verify:true, so the
/// same bad IL surfaces here as a located ES0900 / failing assert instead.
/// </summary>
public sealed class ILEmitterTests_ExternalReflection
{
    [Fact]
    public void ListObject_BoxInt_ToArray_Length()
    {
        const string src = """
namespace Test
func go() -> int {
    let xs = List<object>()
    xs.Add(10)
    xs.Add(20)
    let arr = xs.ToArray()
    return arr.Length
}
""";
        Assert.Equal(2, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void ListInt_Count_AsInt()
    {
        const string src = """
namespace Test
func go() -> int {
    let xs = List<int>()
    xs.Add(1)
    xs.Add(2)
    xs.Add(3)
    return xs.Count
}
""";
        Assert.Equal(3, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void ListInt_Count_Interpolated()
    {
        const string src = """
namespace Test
func go() -> string {
    let xs = List<int>()
    xs.Add(1)
    xs.Add(2)
    return "count={xs.Count}"
}
""";
        Assert.Equal("count=2", EsHarness.Run(src, "go"));
    }

    // The `parser.Diagnostics.Count` shape: `.Count` on an interface-typed
    // (IReadOnlyList<T>) receiver, not the concrete List<T>.
    [Fact]
    public void IReadOnlyListInt_Count_AsInt()
    {
        const string src = """
namespace Test
func makeList() -> IReadOnlyList<int> {
    let xs = List<int>()
    xs.Add(1)
    xs.Add(2)
    return xs
}
func go() -> int {
    let ro = makeList()
    return ro.Count
}
""";
        Assert.Equal(2, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void IReadOnlyListInt_Count_Interpolated()
    {
        const string src = """
namespace Test
func makeList() -> IReadOnlyList<int> {
    let xs = List<int>()
    xs.Add(7)
    return xs
}
func go() -> string {
    let ro = makeList()
    return "n={ro.Count}"
}
""";
        Assert.Equal("n=1", EsHarness.Run(src, "go"));
    }

    // A bare external name resolving to distinct CLR types across two imported
    // namespaces is ambiguous — the compiler must report ES2151 and demand a
    // qualified reference, not silently bind whichever the search hits first.
    // `Timer` exists in both System.Threading and System.Timers.
    [Fact]
    public void AmbiguousExternalType_AcrossImports_ReportsES2151()
    {
        const string src = """
namespace Test
using "System.Threading"
using "System.Timers"
func go() -> int {
    let t = Timer()
    return 0
}
""";
        var diags = EsHarness.Diagnostics(src);
        Assert.Contains(diags, d => d.Message.Contains("ES2151"));
    }

    // Qualifying silences the ES2151 ambiguity — the actionable remedy the error
    // names. (Param position avoids the separate qualified-construction gap.)
    [Fact]
    public void QualifiedExternalType_SilencesAmbiguity()
    {
        const string src = """
namespace Test
using "System.Threading"
using "System.Timers"
func go(t: System.Timers.Timer) -> double {
    return t.Interval
}
""";
        var diags = EsHarness.Diagnostics(src);
        Assert.DoesNotContain(diags, d => d.Message.Contains("ES2151"));
    }

    // The `Console.WriteLine(coll.Count)` shape: an int from an interface member
    // passed directly as a call argument (overloaded / object sink). Must reach
    // the call correctly (no StackUnexpected).
    [Fact]
    public void IReadOnlyListInt_Count_PassedAsCallArg()
    {
        const string src = """
namespace Test
using "System.Text"
func makeList() -> IReadOnlyList<int> {
    let xs = List<int>()
    xs.Add(2)
    xs.Add(5)
    return xs
}
func go() -> string {
    let ro = makeList()
    let sb = StringBuilder()
    sb.Append(ro.Count)
    return sb.ToString()
}
""";
        Assert.Equal("2", EsHarness.Run(src, "go"));
    }

    // --- external-qualification gaps surfaced by the dogfood ---

    // Gap 1: qualified construction of an external type — `Ns.Type()` must emit
    // newobj, not route the dotted chain through member-access (StackUnderflow).
    [Fact]
    public void QualifiedConstruction_External_Works()
    {
        const string src = """
namespace Test
func go() -> int {
    let sb = System.Text.StringBuilder()
    sb.Append(42)
    return sb.Length
}
""";
        Assert.Equal(2, EsHarness.Run(src, "go")); // "42".Length
    }

    // Gap 2: qualified static call with a multi-segment namespace —
    // `System.IO.Path.GetExtension(...)` (namespace System.IO, type Path).
    [Fact]
    public void MultiSegmentQualifiedStaticCall_Works()
    {
        const string src = """
namespace Test
func go() -> string {
    return System.IO.Path.GetExtension("file.txt")
}
""";
        Assert.Equal(".txt", EsHarness.Run(src, "go"));
    }

    // Gap 3: `using static` of a multi-segment-namespace type, then a bare static call.
    [Fact]
    public void UsingStaticMultiSegment_BareCall_Works()
    {
        const string src = """
namespace Test
using static "System.IO.Path"
func go() -> string {
    return GetExtension("file.txt")
}
""";
        Assert.Equal(".txt", EsHarness.Run(src, "go"));
    }

    // --- C#-style type aliases: `using Baz = "Full.Type"` ---

    [Fact]
    public void TypeAlias_Construction_Works()
    {
        const string src = """
namespace Test
using SB = "System.Text.StringBuilder"
func go() -> int {
    let sb = SB()
    sb.Append(42)
    return sb.Length
}
""";
        Assert.Equal(2, EsHarness.Run(src, "go")); // "42".Length
    }

    // The headline use case: alias resolves a collision without qualifying at each
    // use. `Timer` is ambiguous (System.Threading + System.Timers); the alias picks
    // System.Timers.Timer (which has `Interval`).
    [Fact]
    public void TypeAlias_ResolvesCollision()
    {
        const string src = """
namespace Test
using "System.Threading"
using TTimer = "System.Timers.Timer"
func go() -> double {
    let t = TTimer()
    t.Interval = 3.0
    return t.Interval
}
""";
        Assert.Equal(3.0, EsHarness.Run(src, "go"));
    }

    [Fact]
    public void TypeAlias_ParameterPosition_Works()
    {
        const string src = """
namespace Test
using SB = "System.Text.StringBuilder"
func length(b: SB) -> int = b.Length
func go() -> int {
    let sb = SB()
    sb.Append("hello")
    return length(sb)
}
""";
        Assert.Equal(5, EsHarness.Run(src, "go"));
    }

    // Boxing an int into List<object>, then reading it back through the array.
    [Fact]
    public void ListObject_BoxInt_ReadBackElement()
    {
        const string src = """
namespace Test
func go() -> int {
    let xs = List<object>()
    xs.Add(42)
    let arr = xs.ToArray()
    return arr.Length
}
""";
        Assert.Equal(1, EsHarness.Run(src, "go"));
    }

    // --- explicit generic type args on LINQ extension methods ---

    // `xs.OfType<int>()` — the type parameter appears in NO parameter (OfType takes a
    // bare non-generic IEnumerable), so the explicit call-site arg is the ONLY source.
    // The emitter previously dropped it and emitted OfType<object> (no filtering); the
    // binder left the result typed IEnumerable<TResult>, so the loop var was object and
    // `sum += n` couldn't bind. Surfaced by the extract_corpus dogfood
    // (`root.DescendantNodes().OfType<MethodDeclarationSyntax>()`).
    [Fact]
    public void OfType_ExplicitGenericArg_FiltersHeterogeneousList()
    {
        const string src = """
namespace Test
func go() -> int {
    let xs = List<object>()
    xs.Add(1)
    xs.Add("two")
    xs.Add(3)
    xs.Add("four")
    var sum = 0
    for n in xs.OfType<int>() {
        sum += n
    }
    return sum
}
""";
        Assert.Equal(4, EsHarness.Run(src, "go")); // 1 + 3
    }

    // `xs.Cast<int>()` — same shape; the result element type must be `int` so the loop
    // body type-checks and the elements unbox.
    [Fact]
    public void Cast_ExplicitGenericArg_TypesElements()
    {
        const string src = """
namespace Test
func go() -> int {
    let xs = List<object>()
    xs.Add(10)
    xs.Add(20)
    var sum = 0
    for n in xs.Cast<int>() {
        sum += n
    }
    return sum
}
""";
        Assert.Equal(30, EsHarness.Run(src, "go"));
    }

    // The OfType<T> result element type must be precise enough to flow into a member
    // access in the loop body (the dogfood walks `.OfType<MethodDeclarationSyntax>()`
    // then reads members off each element). Here the elements are `string` and the
    // body calls `.Length` — only resolvable if the loop var is typed `string`.
    [Fact]
    public void OfType_ResultElementType_AllowsMemberAccess()
    {
        const string src = """
namespace Test
func go() -> int {
    let xs = List<object>()
    xs.Add("ab")
    xs.Add(7)
    xs.Add("cde")
    var total = 0
    for s in xs.OfType<string>() {
        total += s.Length
    }
    return total
}
""";
        Assert.Equal(5, EsHarness.Run(src, "go")); // "ab"(2) + "cde"(3)
    }
}
