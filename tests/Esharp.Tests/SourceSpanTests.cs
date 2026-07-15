using Esharp.Syntax.Parsing;
using Esharp.Syntax;
using Esharp.BoundTree;        // BoundFunctionDeclaration, BoundIfStatement, …
using Binder = Esharp.Binder.Binder;

namespace Esharp.Tests;

public sealed class SourceSpanTests
{
    [Fact]
    public void Parser_CapturesStatementLineNumbers()
    {
        const string source = """
namespace Test

func add(a: int, b: int) -> int {
    var total = a
    total += b
    return total
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var func = syntax.Members.OfType<FunctionDeclarationSyntax>().Single();
        var stmts = func.Body.Statements;
        Assert.Equal(3, stmts.Count);

        // var total = a  → line 4
        Assert.Equal(4, stmts[0].Span.Line);
        Assert.Equal("test.es", stmts[0].Span.File);

        // total += b  → line 5
        Assert.Equal(5, stmts[1].Span.Line);

        // return total  → line 6
        Assert.Equal(6, stmts[2].Span.Line);
    }

    [Fact]
    public void Binder_PropagatesSpanToStatements()
    {
        const string source = """
namespace Test

func run() -> int {
    let x = 42
    return x
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics);

        var func = bound.Members.OfType<BoundFunctionDeclaration>().Single();
        var stmts = func.Body.Statements;

        // let x = 42  → line 4
        Assert.Equal(4, stmts[0].Span.Line);
        Assert.Equal("test.es", stmts[0].Span.File);

        // return x  → line 5
        Assert.Equal(5, stmts[1].Span.Line);
    }

    [Fact]
    public void IfStatement_HasCorrectSpan()
    {
        const string source = """
namespace Test

func check(x: int) -> int {
    if x > 0 {
        return x
    }
    return 0
}
""";
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);

        var binder = new Esharp.Binder.Binder();
        var bound = binder.Bind(syntax);

        // Also verify the parser sets span on the if-statement syntax
        var syntaxFunc = syntax.Members.OfType<FunctionDeclarationSyntax>().Single();
        var syntaxIf = syntaxFunc.Body.Statements[0] as IfStatementSyntax;
        Assert.NotNull(syntaxIf);
        Assert.Equal(4, syntaxIf!.Span.Line); // parser should have set this

        var func = bound.Members.OfType<BoundFunctionDeclaration>().Single();
        Assert.Equal(2, func.Body.Statements.Count);
        var firstStmt = func.Body.Statements[0];
        Assert.IsType<BoundIfStatement>(firstStmt);
        Assert.Equal(4, firstStmt.Span.Line);
    }
}
