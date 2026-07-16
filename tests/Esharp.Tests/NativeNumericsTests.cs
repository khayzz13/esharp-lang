using System.Reflection;
using Esharp.CodeGen;
using Esharp.Compilation;
using Esharp.Syntax;
using Esharp.Syntax.Parsing;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Esharp.Tests;

public sealed class NativeNumericsTests
{
    [Fact]
    public void DecimalLiteral_IsContextualAndExact()
    {
        const string source = """
namespace Test
func value() -> decimal = 0.1
""";

        Assert.Equal(0.1m, EsHarness.Run(source, "value"));
    }

    [Fact]
    public void Decimal_IsAFirstClassArithmeticType()
    {
        const string source = """
namespace Test
func calculate(price: decimal, rate: decimal) -> decimal {
    let adjusted = price + price * rate
    return adjusted / 2.0 - 0.05
}
func remainder(value: decimal) -> decimal = value % 2.0
func ordered(left: decimal, right: decimal) -> bool = left < right
""";

        Assert.Equal(10.95m, EsHarness.Run(source, "calculate", 20m, 0.1m));
        Assert.Equal(1.5m, EsHarness.Run(source, "remainder", 5.5m));
        Assert.Equal(true, EsHarness.Run(source, "ordered", 1.1m, 1.2m));
    }

    [Fact]
    public void Decimal_ContextFlowsThroughWholeArithmeticExpression()
    {
        const string source = """
namespace Test
func value() -> decimal = -(1.25 + 0.75)
""";

        Assert.Equal(-2.00m, EsHarness.Run(source, "value"));
    }

    [Fact]
    public void Decimal_ContextFlowsIntoCallArguments()
    {
        const string source = """
namespace Test
func identity(value: decimal) -> decimal = value
func value() -> decimal = identity(19.99)
""";
        Assert.Equal(19.99m, EsHarness.Run(source, "value"));
    }

    [Fact]
    public void Decimal_LiteralBindsToConcreteOperandInEitherOrder()
    {
        const string source = """
namespace Test
func left(value: decimal) -> decimal = 0.25 + value
func right(value: decimal) -> decimal = value + 0.25
""";
        Assert.Equal(1.25m, EsHarness.Run(source, "left", 1m));
        Assert.Equal(1.25m, EsHarness.Run(source, "right", 1m));
    }

    [Fact]
    public void MixedNonliteralNumericTypes_RequireExplicitConversion()
    {
        var diagnostics = EsHarness.Diagnostics("""
namespace Test
func invalid(left: decimal, right: double) -> decimal = left + right
""");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "ES2235");
    }

    [Fact]
    public void UnconstrainedNumericLiterals_KeepDefaultInferenceOrder()
    {
        const string source = """
namespace Test
func small() -> int = 42
func large() -> long = 5_000_000_000
func fractional() -> double = 1.25
""";
        Assert.Equal(42, EsHarness.Run(source, "small"));
        Assert.Equal(5_000_000_000L, EsHarness.Run(source, "large"));
        Assert.Equal(1.25d, EsHarness.Run(source, "fractional"));
    }

    [Fact]
    public void ContextualFloatLiteral_EmitsSinglePrecision()
    {
        const string source = """
namespace Test
func value() -> float = 19.99
""";
        Assert.Equal(19.99f, EsHarness.Run(source, "value"));
    }

    [Fact]
    public void Decimal_DivisionByZeroRetainsSystemDecimalBehavior()
    {
        const string source = """
namespace Test
func divide(value: decimal) -> decimal = value / 0.0
""";
        var exception = Assert.Throws<TargetInvocationException>(() => EsHarness.Run(source, "divide", 1m));
        Assert.IsType<DivideByZeroException>(exception.InnerException);
    }

    [Fact]
    public void Decimal_ToIntegerTruncatesThenChecks()
    {
        const string source = """
namespace Test
func convert(value: decimal) -> int = int(value)
""";
        Assert.Equal(12, EsHarness.Run(source, "convert", 12.99m));
        Assert.Equal(-12, EsHarness.Run(source, "convert", -12.99m));
    }

    [Fact]
    public void CharacterConversion_IsChecked()
    {
        const string source = """
namespace Test
func convert(value: int) -> char = char(value)
""";
        Assert.Equal('A', EsHarness.Run(source, "convert", 65));
        var exception = Assert.Throws<TargetInvocationException>(() => EsHarness.Run(source, "convert", 65536));
        Assert.IsType<OverflowException>(exception.InnerException);
    }

    [Fact]
    public void NativeNumericConversion_IsCheckedAtRuntime()
    {
        const string source = """
namespace Test
func narrow(value: int) -> byte = byte(value)
func money(value: int) -> decimal = decimal(value)
""";

        Assert.Equal((byte)255, EsHarness.Run(source, "narrow", 255));
        Assert.Equal(42m, EsHarness.Run(source, "money", 42));
        var exception = Assert.Throws<TargetInvocationException>(() => EsHarness.Run(source, "narrow", 256));
        Assert.IsType<OverflowException>(exception.InnerException);
    }

    [Fact]
    public void NativeIntegerConversions_UseNativeClrTypes()
    {
        const string source = """
namespace Test
func signed(value: long) -> nint = nint(value)
func unsigned(value: ulong) -> nuint = nuint(value)
""";

        Assert.Equal((nint)42, EsHarness.Run(source, "signed", 42L));
        Assert.Equal((nuint)42, EsHarness.Run(source, "unsigned", 42UL));
    }

    [Fact]
    public void DecimalOptionalDefault_UsesClrDecimalConstantMetadata()
    {
        const string source = """
namespace Test
pub func price(value: decimal = 19.99) -> decimal = value
""";
        var path = EsHarness.CompileToPath(source, "DecimalDefault");
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var parameter = Method(assembly, "price").Parameters.Single();
        Assert.True(parameter.IsOptional);
        Assert.Contains(parameter.CustomAttributes, attribute =>
            attribute.AttributeType.FullName == "System.Runtime.CompilerServices.DecimalConstantAttribute");
    }

    [Fact]
    public void PublicDecimalConstant_UsesClrMetadataAndRuntimeInitialization()
    {
        const string source = """
namespace Test
pub const Price: decimal = 19.99
func value() -> decimal = Price
""";
        var path = EsHarness.CompileToPath(source, "DecimalConstant");
        using (var assembly = AssemblyDefinition.ReadAssembly(path))
        {
            var field = assembly.MainModule.Types.Single(type => type.Name == "Test")
                .Fields.Single(candidate => candidate.Name == "Price");
            Assert.True(field.IsInitOnly);
            Assert.Contains(field.CustomAttributes, attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.DecimalConstantAttribute");
        }
        var runtime = Assembly.LoadFrom(path);
        var runtimeField = runtime.GetType("Test.Test")!.GetField("Price", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(19.99m, runtimeField.GetValue(null));
    }

    [Fact]
    public void NativeNumericConversion_RejectsOutOfRangeLiteral()
    {
        var diagnostics = EsHarness.Diagnostics("""
namespace Test
func invalid() -> byte = byte(300)
""");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "ES2236");
    }

    [Fact]
    public void NativeNumericConversion_FoldsConstantExpressionsAndChecksTheirResult()
    {
        const string valid = """
namespace Test
func value() -> byte = byte(100 + 27)
""";
        Assert.Equal((byte)127, EsHarness.Run(valid, "value"));

        var diagnostics = EsHarness.Diagnostics("""
namespace Test
func invalid() -> byte = byte(200 + 100)
""");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "ES2236");
    }

    [Fact]
    public void DecimalConversionOfLiteral_DoesNotPassThroughDouble()
    {
        const string source = """
namespace Test
func value() -> decimal = decimal(0.1)
""";
        Assert.Equal(0.1m, EsHarness.Run(source, "value"));
    }

    [Fact]
    public void UnsignedNarrowing_UsesUnsignedCheckedOpcode()
    {
        const string source = """
namespace Test
func narrow(value: uint) -> byte = byte(value)
""";
        var (assembly, diagnostics) = EsHarness.EmitCecil(source, "UnsignedChecked");
        using (assembly)
        {
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
            Assert.Contains(Method(assembly, "narrow").Body.Instructions,
                instruction => instruction.OpCode == OpCodes.Conv_Ovf_U1_Un);
        }
    }

    [Fact]
    public void FloatMode_PrintsCanonicallyAndRemainsSeparateFromAttributes()
    {
        var parser = new Parser("""
namespace Test
[Obsolete]
@floatMode(contractFma: true)
func multiplyAdd(a: double, b: double, c: double) -> double = a * b + c
""", "directive.es");
        var unit = parser.ParseCompilationUnit();

        Assert.Empty(parser.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(unit.Members));
        Assert.Single(function.Attributes);
        Assert.Single(function.CompilerDirectives);
        var printed = SyntaxPrinter.PrintCanonical(unit);
        Assert.Contains("@floatMode(contractFma: true)", printed);
    }

    [Fact]
    public void FloatMode_ContractsOnlyOptedInReleaseFunctions()
    {
        const string source = """
namespace Test
@floatMode(contractFma: true)
func contracted(a: double, b: double, c: double) -> double = a * b + c
func strict(a: double, b: double, c: double) -> double = a * b + c
""";
        var program = EsHarness.BindAndLower(source);
        var (debugAssembly, debugDiagnostics) = CodeGenerator.Generate(program, "FmaDebug",
            optimization: OptimizationLevel.Debug);
        var (releaseAssembly, releaseDiagnostics) = CodeGenerator.Generate(program, "FmaRelease",
            optimization: OptimizationLevel.Release);
        using (debugAssembly)
        using (releaseAssembly)
        {
            Assert.DoesNotContain(debugDiagnostics, diagnostic => diagnostic.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
            Assert.DoesNotContain(releaseDiagnostics, diagnostic => diagnostic.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
            var debugContracted = Method(debugAssembly, "contracted");
            var releaseContracted = Method(releaseAssembly, "contracted");
            var releaseStrict = Method(releaseAssembly, "strict");
            Assert.DoesNotContain(debugContracted.Body.Instructions, IsFmaCall);
            Assert.Contains(releaseContracted.Body.Instructions, IsFmaCall);
            Assert.DoesNotContain(releaseStrict.Body.Instructions, IsFmaCall);
            Assert.Contains(releaseStrict.Body.Instructions, instruction => instruction.OpCode == OpCodes.Mul);
        }
    }

    [Fact]
    public void FloatMode_UsesMathFForFloat()
    {
        const string source = """
namespace Test
@floatMode(contractFma: true)
func value(a: float, b: float, c: float) -> float = a * b + c
""";
        var program = EsHarness.BindAndLower(source);
        var (assembly, diagnostics) = CodeGenerator.Generate(program, "FmaFloat",
            optimization: OptimizationLevel.Release);
        using (assembly)
        {
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
            Assert.Contains(Method(assembly, "value").Body.Instructions, instruction =>
                IsFmaCall(instruction) && ((MethodReference)instruction.Operand).DeclaringType.FullName == "System.MathF");
        }
    }

    [Fact]
    public void FloatMode_NeverContractsDecimal()
    {
        const string source = """
namespace Test
@floatMode(contractFma: true)
func value(a: decimal, b: decimal, c: decimal) -> decimal = a * b + c
""";
        var program = EsHarness.BindAndLower(source);
        var (assembly, diagnostics) = CodeGenerator.Generate(program, "NoDecimalFma",
            optimization: OptimizationLevel.Release);
        using (assembly)
        {
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
            Assert.DoesNotContain(Method(assembly, "value").Body.Instructions, IsFmaCall);
            Assert.Contains(Method(assembly, "value").Body.Instructions, instruction =>
                instruction.Operand is MethodReference { DeclaringType.FullName: "System.Decimal", Name: "op_Multiply" });
        }
    }

    [Fact]
    public void FloatMode_PropagatesToAsyncStateMachineBody()
    {
        const string source = """
namespace Test
using "System.Threading.Tasks"
@floatMode(contractFma: true)
func value(a: double, b: double, c: double) -> Task<double> {
    await Task.Delay(1)
    return a * b + c
}
""";
        var program = EsHarness.BindAndLower(source);
        var (assembly, diagnostics) = CodeGenerator.Generate(program, "AsyncFma",
            optimization: OptimizationLevel.Release);
        using (assembly)
        {
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
            Assert.Contains(assembly.MainModule.Types.SelectMany(AllMethods), method =>
                method.Name == "MoveNext" && method.HasBody && method.Body.Instructions.Any(IsFmaCall));
        }
    }

    [Fact]
    public void FloatMode_PreservesSourceOperandEvaluationOrder()
    {
        const string source = """
namespace Test
var trace = 0
func mark(id: int, value: double) -> double {
    trace = trace * 10 + id
    return value
}
@floatMode(contractFma: true)
func run() -> int {
    let result = mark(1, 10.0) - mark(2, 2.0) * mark(3, 3.0)
    return trace
}
""";
        var program = EsHarness.BindAndLower(source);
        var assemblyName = $"FmaOrder_{Guid.NewGuid():N}";
        var (assembly, diagnostics) = CodeGenerator.Generate(program, assemblyName,
            optimization: OptimizationLevel.Release);
        var path = Path.Combine(Path.GetTempPath(), assemblyName + ".dll");
        using (assembly)
        {
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
            assembly.Write(path);
        }
        var runtime = Assembly.LoadFrom(path);
        var result = runtime.GetType("Test.Test")!.GetMethod("run", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        Assert.Equal(123, result);
    }

    [Theory]
    [InlineData("a * b + c")]
    [InlineData("c + a * b")]
    [InlineData("a * b - c")]
    [InlineData("c - a * b")]
    public void FloatMode_ContractsEveryApprovedShape(string expression)
    {
        var source = $$"""
namespace Test
@floatMode(contractFma: true)
func value(a: double, b: double, c: double) -> double = {{expression}}
""";
        var program = EsHarness.BindAndLower(source);
        var (assembly, diagnostics) = CodeGenerator.Generate(program, "FmaShape",
            optimization: OptimizationLevel.Release);
        using (assembly)
        {
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
            Assert.Contains(Method(assembly, "value").Body.Instructions, IsFmaCall);
        }
    }

    [Fact]
    public void FloatMode_OnClass_IsInheritedByConcreteMethods()
    {
        const string source = """
namespace Test
@floatMode(contractFma: true)
class Calculator {
    func value(a: double, b: double, c: double) -> double = a * b + c
}
""";
        AssertTypeMethodContracts(source, "Calculator", "value", expected: true);
    }

    [Fact]
    public void FloatMode_OnStruct_IsInheritedByConcreteMethods()
    {
        const string source = """
namespace Test
@floatMode(contractFma: true)
struct Calculator {
    func value(a: double, b: double, c: double) -> double = a * b + c
}
""";
        AssertTypeMethodContracts(source, "Calculator", "value", expected: true);
    }

    [Fact]
    public void MethodFloatMode_OverridesContainingTypeDefault()
    {
        const string source = """
namespace Test
@floatMode(contractFma: true)
class Calculator {
    @floatMode(contractFma: false)
    func value(a: double, b: double, c: double) -> double = a * b + c
}
""";
        AssertTypeMethodContracts(source, "Calculator", "value", expected: false);
    }

    [Fact]
    public void CompilerDirective_AttachesToInterfaceAndPrintsCanonically()
    {
        var parser = new Parser("""
namespace Test
@floatMode(contractFma: true)
interface ICalculator {
    func value(a: double, b: double, c: double) -> double
}
""", "interface-directive.es");
        var unit = parser.ParseCompilationUnit();
        Assert.Empty(parser.Diagnostics);
        var declaration = Assert.IsType<InterfaceDeclarationSyntax>(Assert.Single(unit.Members));
        Assert.Single(declaration.CompilerDirectives);
        Assert.Contains("@floatMode(contractFma: true)", SyntaxPrinter.PrintCanonical(unit));
        _ = EsHarness.BindAndLower(SyntaxPrinter.PrintCanonical(unit));
    }

    [Fact]
    public void DuplicateFloatMode_IsDiagnosed()
    {
        var diagnostics = EsHarness.Diagnostics("""
namespace Test
@floatMode(contractFma: true)
@floatMode(contractFma: false)
func value() -> double = 1.0
""");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "ES2238");
    }

    [Fact]
    public void MalformedFloatMode_IsDiagnosedByParser()
    {
        var parser = new Parser("""
namespace Test
@floatMode(contractFma = true)
func value() -> double = 1.0
""", "malformed-directive.es");
        _ = parser.ParseCompilationUnit();
        Assert.Contains(parser.Diagnostics, diagnostic => diagnostic.Code == "ES2237");
    }

    [Fact]
    public void UnknownCompilerDirective_IsDiagnosed()
    {
        var diagnostics = EsHarness.Diagnostics("""
namespace Test
@fast(enabled: true)
func value() -> double = 1.0
""");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "ES2237");
    }

    static MethodDefinition Method(AssemblyDefinition assembly, string name) =>
        assembly.MainModule.Types.Single(type => type.Name == "Test").Methods.Single(method => method.Name == name);

    static bool IsFmaCall(Instruction instruction) =>
        instruction.OpCode == OpCodes.Call
        && instruction.Operand is MethodReference { Name: "FusedMultiplyAdd" };

    static void AssertTypeMethodContracts(string source, string typeName, string methodName, bool expected)
    {
        var program = EsHarness.BindAndLower(source);
        var (assembly, diagnostics) = CodeGenerator.Generate(program, "TypeFma",
            optimization: OptimizationLevel.Release);
        using (assembly)
        {
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error);
            var method = assembly.MainModule.Types.Single(type => type.Name == typeName)
                .Methods.Single(candidate => candidate.Name == methodName);
            Assert.Equal(expected, method.Body.Instructions.Any(IsFmaCall));
        }
    }

    static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type)
    {
        foreach (var method in type.Methods) yield return method;
        foreach (var nested in type.NestedTypes)
            foreach (var method in AllMethods(nested))
                yield return method;
    }
}
