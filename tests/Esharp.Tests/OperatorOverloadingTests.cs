using System.Reflection;
using Esharp.BoundTree;
using Esharp.Compilation;
using Esharp.Syntax;
using Esharp.Syntax.Parsing;
using Mono.Cecil;

namespace Esharp.Tests;

public sealed class OperatorOverloadingTests
{
    static IReadOnlyList<Esharp.Diagnostics.Diagnostic> Diagnostics(string source) =>
        EsHarness.Diagnostics(source);

    [Fact]
    public void Parser_AcceptsCompactOperatorFunctionName()
    {
        var parser = new Parser("""
namespace Test
struct Vec { x: int }
static Vec { func +(left: Vec, right: Vec) -> Vec = left }
""", "operator.es");
        var unit = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var facet = Assert.Single(unit.Members.OfType<StaticFuncDeclarationSyntax>());
        Assert.Equal(SyntaxTokenKind.Plus, Assert.Single(facet.Functions).OperatorKind);
    }

    [Fact]
    public void Printer_NormalizesOperatorFunctionWithoutSpace()
    {
        var parser = new Parser("""
namespace Test
struct Vec { x: int }
static Vec { func + (left: Vec, right: Vec) -> Vec = left }
""", "operator.es");
        var printed = SyntaxPrinter.PrintCanonical(parser.ParseCompilationUnit());
        Assert.Contains("func +(left: Vec, right: Vec)", printed);
    }

    [Theory]
    [InlineData("+", 1)]
    [InlineData("-", 1)]
    [InlineData("!", 1)]
    [InlineData("~", 1)]
    [InlineData("+", 2)]
    [InlineData("-", 2)]
    [InlineData("*", 2)]
    [InlineData("/", 2)]
    [InlineData("%", 2)]
    [InlineData("&", 2)]
    [InlineData("|", 2)]
    [InlineData("^", 2)]
    [InlineData("<<", 2)]
    [InlineData(">>", 2)]
    [InlineData(">>>", 2)]
    [InlineData("==", 2)]
    [InlineData("!=", 2)]
    [InlineData("<", 2)]
    [InlineData(">", 2)]
    [InlineData("<=", 2)]
    [InlineData(">=", 2)]
    public void Parser_AcceptsEveryFixedOperatorToken(string op, int arity)
    {
        var parameters = arity == 1 ? "value: Vec" : "left: Vec, right: Vec";
        var parser = new Parser($"namespace Test\nstruct Vec {{ x: int }}\nstatic Vec {{ func {op}({parameters}) -> Vec = {(arity == 1 ? "value" : "left")} }}", "operators.es");
        _ = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
    }

    [Fact]
    public void SourceAdditionOperator_Runs()
    {
        const string source = """
namespace Test
struct Vec { x: int }
static Vec {
    pub func +(left: Vec, right: Vec) -> Vec = Vec { x: left.x + right.x }
}
func go() -> int {
    let result = Vec { x: 3 } + Vec { x: 4 }
    return result.x
}
""";
        Assert.Equal(7, EsHarness.Run(source, "go"));
    }

    [Fact]
    public void SourceUnaryOperator_Runs()
    {
        const string source = """
namespace Test
struct Vec { x: int }
static Vec { pub func -(value: Vec) -> Vec = Vec { x: -value.x } }
func go() -> int { let result = -Vec { x: 4 }
 return result.x }
""";
        Assert.Equal(-4, EsHarness.Run(source, "go"));
    }

    [Fact]
    public void OperatorEmitsClrSpecialName()
    {
        var path = EsHarness.CompileToPath("""
namespace Test
pub struct Vec { x: int }
pub static Vec { pub func +(left: Vec, right: Vec) -> Vec = left }
""", "OperatorMetadata");
        using var module = ModuleDefinition.ReadModule(path);
        var method = module.GetType("Test.Vec")!.Methods.Single(m => m.Name == "op_Addition");
        Assert.True(method.IsStatic);
        Assert.True(method.IsSpecialName);
        Assert.True(method.IsPublic);
    }

    [Fact]
    public void NamespaceOperator_IsRejected()
    {
        var diagnostics = Diagnostics("""
namespace Test
struct Vec { x: int }
func +(left: Vec, right: Vec) -> Vec = left
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("ES2270"));
    }

    [Fact]
    public void AttachedStaticReceiverOperator_Runs()
    {
        const string source = """
namespace Test
struct Vec { x: int }
static Vec { }
func (owner: static Vec) +(left: Vec, right: Vec) -> Vec = Vec { x: left.x + right.x }
func go() -> int { let result = Vec { x: 8 } + Vec { x: 5 }
 return result.x }
""";
        Assert.Equal(13, EsHarness.Run(source, "go"));
    }

    [Fact]
    public void OperatorInsideInstanceBody_IsRejected()
    {
        var diagnostics = Diagnostics("""
namespace Test
struct Vec { x: int
 func +(left: Vec, right: Vec) -> Vec = left }
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("ES2270"));
    }

    [Fact]
    public void StandaloneStaticOperatorHost_IsRejected()
    {
        var diagnostics = Diagnostics("""
namespace Test
struct Vec { x: int }
static Ops { func +(left: Vec, right: Vec) -> Vec = left }
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("ES2270"));
    }

    [Fact]
    public void InterfaceCompanionOperator_IsRejected()
    {
        var diagnostics = Diagnostics("""
namespace Test
interface IValue { }
static IValue { func +(left: IValue, right: IValue) -> IValue = left }
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("ES2270"));
    }

    [Fact]
    public void WrongOwnerOperand_IsRejected()
    {
        var diagnostics = Diagnostics("""
namespace Test
struct Vec { x: int }
struct Other { x: int }
static Vec { func +(left: Other, right: Other) -> Other = left }
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("ES2275"));
    }

    [Fact]
    public void EqualityRequiresInequalityPair()
    {
        var diagnostics = Diagnostics("""
namespace Test
struct Vec { x: int }
static Vec { func ==(left: Vec, right: Vec) -> bool = left.x == right.x }
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("ES2279") && d.Message.Contains("!='"));
    }

    [Fact]
    public void EqualityPair_Runs()
    {
        const string source = """
namespace Test
struct Vec { x: int }
static Vec {
 func ==(left: Vec, right: Vec) -> bool = left.x == right.x
 func !=(left: Vec, right: Vec) -> bool = left.x != right.x
}
func go() -> bool = Vec { x: 2 } == Vec { x: 2 }
""";
        Assert.Equal(true, EsHarness.Run(source, "go"));
    }

    [Theory]
    [InlineData("1 & 3", 1)]
    [InlineData("1 | 2", 3)]
    [InlineData("1 ^ 3", 2)]
    [InlineData("1 << 3", 8)]
    [InlineData("8 >> 2", 2)]
    [InlineData("-1 >>> 1", int.MaxValue)]
    public void PrimitiveBitwiseAndShifts_Run(string expression, int expected)
    {
        var source = $"namespace Test\nfunc go() -> int = {expression}\n";
        Assert.Equal(expected, EsHarness.Run(source, "go"));
    }

    [Fact]
    public void PrimitiveUnaryPlusAndComplement_Run()
    {
        Assert.Equal(-2, EsHarness.Run("namespace Test\nfunc go() -> int = +1 + ~2\n", "go"));
    }

    [Theory]
    [InlineData("&=", 1)]
    [InlineData("|=", 7)]
    [InlineData("^=", 6)]
    [InlineData("<<=", 20)]
    [InlineData(">>=", 1)]
    [InlineData("%=", 2)]
    public void PrimitiveCompoundAssignments_Run(string op, int expected)
    {
        var rhs = op is "<<=" or ">>=" ? 2 : 3;
        var source = $"namespace Test\nfunc go() -> int {{ var value = 5\n value {op} {rhs}\n return value }}\n";
        Assert.Equal(expected, EsHarness.Run(source, "go"));
    }

    [Fact]
    public void UserOperatorCompoundAssignment_Runs()
    {
        const string source = """
namespace Test
struct Vec { x: int }
static Vec { func +(left: Vec, right: Vec) -> Vec = Vec { x: left.x + right.x } }
func go() -> int { var value = Vec { x: 4 }
 value += Vec { x: 6 }
 return value.x }
""";
        Assert.Equal(10, EsHarness.Run(source, "go"));
    }

    [Fact]
    public void UserOperatorCompoundAssignmentAcrossAwait_Runs()
    {
        const string source = """
namespace Test
using "System.Threading.Tasks"
struct Vec { x: int }
static Vec { func +(left: Vec, right: Vec) -> Vec = Vec { x: left.x + right.x } }
func go() -> int { var value = Vec { x: 4 }
 value += Vec { x: await Task.FromResult(6) }
 return value.x }
""";
        Assert.Equal(10, EsHarness.Await(EsHarness.Run(source, "go")));
    }

    [Fact]
    public void OperatorOperandsEvaluateLeftToRightExactlyOnce()
    {
        const string source = """
namespace Test
var order: int = 0
struct Vec { x: int }
static Vec { func +(left: Vec, right: Vec) -> Vec = Vec { x: left.x + right.x } }
func make(value: int) -> Vec { order = order * 10 + value
 return Vec { x: value } }
func go() -> int { let result = make(2) + make(3)
 return order * 10 + result.x }
""";
        Assert.Equal(235, EsHarness.Run(source, "go"));
    }

    [Fact]
    public void CompoundIndexTargetEvaluatesIndexExactlyOnce()
    {
        const string source = """
namespace Test
var calls: int = 0
func next() -> int { calls += 1
 return 0 }
func go() -> int { let values = [4]
 values[next()] += 6
 return calls * 100 + values[0] }
""";
        Assert.Equal(110, EsHarness.Run(source, "go"));
    }

    [Fact]
    public void GenericCompanionFacetOperator_Runs()
    {
        const string source = """
namespace Test
struct Box<T> { value: T }
static Box<T> { func +(left: Box<T>, right: Box<T>) -> Box<T> = left }
func go() -> int { let result = Box<int> { value: 7 } + Box<int> { value: 9 }
 return result.value }
""";
        var program = EsHarness.BindAndLower(source);
        var go = program.Units.Single().Members.OfType<BoundFunctionDeclaration>().Single(f => f.Name == "go");
        var declaration = go.Body.Statements.OfType<BoundVariableDeclaration>().Single();
        Assert.NotNull(Assert.IsType<BoundBinaryExpression>(declaration.Initializer).ResolvedOperator);
        Assert.Equal(7, EsHarness.Run(source, "go"));
    }

    [Fact]
    public void CrossFileAttachedOperator_Runs()
    {
        var path = EsHarness.CompileToPath([
            "namespace Test\nstruct Vec { x: int }\nstatic Vec { }",
            "namespace Test\nfunc (owner: static Vec) +(left: Vec, right: Vec) -> Vec = Vec { x: left.x + right.x }\nfunc go() -> int { let v = Vec { x: 10 } + Vec { x: 7 }\n return v.x }",
        ], "CrossFileOperator");
        var assembly = Assembly.LoadFrom(path);
        Assert.Equal(17, assembly.GetType("Test.Test")!.GetMethod("go", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, null));
    }

    [Fact]
    public void GenericStaticReceiverOperator_Runs()
    {
        const string source = """
namespace Test
struct Box<T> { value: T }
static Box<T> { }
func (owner: static Box<T>) +(left: Box<T>, right: Box<T>) -> Box<T> = left
func go() -> int { let result = Box<int> { value: 11 } + Box<int> { value: 12 }
 return result.value }
""";
        Assert.Equal(11, EsHarness.Run(source, "go"));
    }

    [Fact]
    public void ContextualLiteralSelectsOperatorOperandType()
    {
        const string source = """
namespace Test
struct Vec { x: float }
static Vec { func *(left: Vec, right: float) -> Vec = Vec { x: left.x * right } }
func go() -> float { let result = Vec { x: 2.0 } * 1.5
 return result.x }
""";
        Assert.Equal(3f, EsHarness.Run(source, "go"));
    }

    [Fact]
    public void ImportedClrOperator_Runs()
    {
        const string source = """
namespace Test
using "System"
func add(value: DateTime, amount: TimeSpan) -> DateTime = value + amount
""";
        var program = EsHarness.BindAndLower(source);
        var add = program.Units.Single().Members.OfType<BoundFunctionDeclaration>().Single(f => f.Name == "add");
        Assert.NotNull(Assert.IsType<BoundReturnStatement>(Assert.Single(add.Body.Statements)).Expression is BoundBinaryExpression binary
            ? binary.ExternalOperator : null);
        var value = new DateTime(2025, 1, 1);
        Assert.Equal(value.AddDays(2), EsHarness.Run(source, "add", value, TimeSpan.FromDays(2)));
    }

    [Fact]
    public void InternalOperatorEmitsAssemblyVisibility()
    {
        var path = EsHarness.CompileToPath("""
namespace Test
pub struct Vec { x: int }
pub static Vec { func +(left: Vec, right: Vec) -> Vec = left }
""", "InternalOperatorMetadata");
        using var module = ModuleDefinition.ReadModule(path);
        var method = module.GetType("Test.Vec")!.Methods.Single(m => m.Name == "op_Addition");
        Assert.True(method.IsAssembly);
    }

    [Fact]
    public void DuplicateOperatorSignature_IsRejected()
    {
        var diagnostics = Diagnostics("""
namespace Test
struct Vec { x: int }
static Vec {
 func +(left: Vec, right: Vec) -> Vec = left
 func +(left: Vec, right: Vec) -> Vec = right
}
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("ES2278"));
    }

    [Fact]
    public void ComparisonMustReturnBool()
    {
        var diagnostics = Diagnostics("""
namespace Test
struct Vec { x: int }
static Vec {
 func <(left: Vec, right: Vec) -> int = 0
 func >(left: Vec, right: Vec) -> int = 0
}
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("ES2277"));
    }

    [Fact]
    public void ShiftRequiresIntRightOperand()
    {
        var diagnostics = Diagnostics("""
namespace Test
struct Vec { x: int }
static Vec { func <<(left: Vec, right: long) -> Vec = left }
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("ES2276"));
    }

    [Theory]
    [InlineData("func +(left: Vec = Vec { x: 0 }, right: Vec) -> Vec = left", "ES2274")]
    [InlineData("func +<T>(left: Vec, right: Vec) -> Vec = left", "ES2271")]
    [InlineData("task func +(left: Vec, right: Vec) -> Vec = left", "ES2271")]
    [InlineData("func +(left: Vec, right: Vec)", "ES2272")]
    public void ForbiddenOperatorFunctionShapes_AreRejected(string declaration, string code)
    {
        var diagnostics = Diagnostics($"namespace Test\nstruct Vec {{ x: int }}\nstatic Vec {{ {declaration} }}");
        Assert.Contains(diagnostics, d => d.Message.Contains(code));
    }

    [Theory]
    [InlineData("1.0 & 2.0")]
    [InlineData("1.0 << 2")]
    [InlineData("1 << long(2)")]
    [InlineData("~1.0")]
    public void InvalidPrimitiveBitwiseShapes_AreRejected(string expression)
    {
        var diagnostics = Diagnostics($"namespace Test\nfunc bad() -> int = {expression}");
        Assert.Contains(diagnostics, d => d.Message.Contains("ES2283"));
    }

    [Fact]
    public void DeriveEqualityConflictsWithExplicitPair()
    {
        var diagnostics = Diagnostics("""
namespace Test
derive equality
struct Vec { x: int }
static Vec {
 func ==(left: Vec, right: Vec) -> bool = true
 func !=(left: Vec, right: Vec) -> bool = false
}
""");
        Assert.Contains(diagnostics, d => d.Message.Contains("ES2280"));
    }
}
