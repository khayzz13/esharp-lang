using Esharp.Syntax.Parsing;
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

/// <summary>
/// ES2145 — indexing a value type the binder fully owns (`data` / `class`, `choice`, `enum`)
/// is rejected at BIND time with a source location, not deferred to a locationless
/// `ES0001: IL: unresolved indexer` from the backend. E# has no indexer overloading, so these
/// types are never indexable; external/collection types (`List`, arrays, `string`) still resolve
/// their indexer in the IL backend and must NOT trip this.
/// </summary>
public sealed class IndexDiagnosticTests
{
    static IReadOnlyList<Esharp.Diagnostics.Diagnostic> BindDiags(string source)
    {
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        binder.Bind(syntax);
        return binder.Diagnostics;
    }

    [Fact]
    public void IndexingData_Errors()
    {
        var diag = Assert.Single(BindDiags("""
namespace Test

struct P { x: int }

func (p: P) f() -> int { return p[0] }
"""), d => d.Message.Contains("ES2145"));
        Assert.Contains("P", diag.Message);
    }

    [Fact]
    public void IndexingChoice_Errors()
    {
        Assert.Contains(BindDiags("""
namespace Test

union C { a  b(n: int) }

func f(c: C) -> int { return c[0] }
"""), d => d.Message.Contains("ES2145"));
    }

    [Fact]
    public void IndexingEnum_Errors()
    {
        Assert.Contains(BindDiags("""
namespace Test

enum E { a  b  c }

func f(e: E) -> int { return e[0] }
"""), d => d.Message.Contains("ES2145"));
    }

    [Fact]
    public void IndexingList_DoesNotError()
    {
        Assert.DoesNotContain(BindDiags("""
namespace Test

func f(xs: List<int>) -> int { return xs[0] }
"""), d => d.Message.Contains("ES2145"));
    }

    [Fact]
    public void IndexingString_DoesNotError()
    {
        Assert.DoesNotContain(BindDiags("""
namespace Test

func first(s: string) -> char { return s[0] }
"""), d => d.Message.Contains("ES2145"));
    }
}
