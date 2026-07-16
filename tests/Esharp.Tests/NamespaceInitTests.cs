using Mono.Cecil;

namespace Esharp.Tests;

/// <summary>
/// Namespace <c>init { }</c> is the explicit, lazy initialization surface for a
/// namespace host. These tests cover its source contract and the CLR shape that
/// gives it once-per-AppDomain execution.
/// </summary>
public sealed class NamespaceInitTests
{
    const string Counter = """
        var count: int = 0
        func add() { count = count + 1 }
        func read() -> int = count
        """;

    [Fact]
    public void Init_RunsBeforeFirstNamespaceFunction()
        => Assert.Equal(1, EsHarness.Run("""
            namespace Test
            """ + "\n" + Counter + "\n" + """
            init { add() }
            func value() -> int = read()
            """, "value"));

    [Fact]
    public void Init_RunsOnlyOnce()
    {
        var assembly = EsHarness.Compile("""
            namespace Test
            """ + "\n" + Counter + "\n" + """
            init { add() }
            func value() -> int = read()
            """);

        Assert.Equal(1, EsHarness.Invoke(assembly, "value"));
        Assert.Equal(1, EsHarness.Invoke(assembly, "value"));
    }

    [Fact]
    public void Init_CanCallFunctionDeclaredLater()
        => Assert.Equal(7, EsHarness.Run("""
            namespace Test
            """ + "\n" + Counter + "\n" + """
            init { seed() }
            func seed() {
                add()
                add()
                add()
                add()
                add()
                add()
                add()
            }
            func value() -> int = read()
            """, "value"));

    [Fact]
    public void Init_CoversTheWholeNamespaceAcrossFiles()
    {
        var assembly = EsHarness.CompileToPath([
            """
            namespace Test
            """ + "\n" + Counter + "\n" + """
            init { add() }
            """,
            """
            namespace Test
            func value() -> int = read()
            """
        ]);

        var loaded = System.Reflection.Assembly.LoadFrom(assembly);
        Assert.Equal(1, EsHarness.Invoke(loaded, "value"));
    }

    [Fact]
    public void Init_EmitsNamespaceHostTypeInitializer()
    {
        var (assembly, _) = EsHarness.EmitCecil("""
            namespace Test
            """ + "\n" + Counter + "\n" + """
            init { add() }
            func value() -> int = read()
            """);
        using (assembly)
        {
            var host = assembly.MainModule.GetType("Test.Test")!;
            var cctor = Assert.Single(host.Methods, m => m.Name == ".cctor");
            Assert.True(cctor.IsStatic);
            Assert.True(cctor.IsConstructor);
            Assert.False(host.IsBeforeFieldInit);
            var instructions = cctor.Body.Instructions.ToList();
            var stateWrite = instructions.FindIndex(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Stsfld);
            var initCall = instructions.FindIndex(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Call);
            Assert.True(stateWrite >= 0 && initCall > stateWrite,
                "namespace state must initialize before the explicit init body");
        }
    }

    [Fact]
    public void Init_FailureUsesClrTypeInitializationSemantics()
    {
        var assembly = EsHarness.Compile("""
            namespace Test
            init { throw InvalidOperationException("boom") }
            func value() -> int = 1
            """);

        var invocation = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => EsHarness.Invoke(assembly, "value"));
        Assert.IsType<TypeInitializationException>(invocation.InnerException);
    }

    [Fact]
    public void Init_DuplicateAcrossFiles_IsRejected()
    {
        var diags = Diagnostics([
            "namespace Test\ninit { }",
            "namespace Test\ninit { }"
        ]);

        Assert.Contains(diags, d => d.Message.Contains("ES2206"));
    }

    [Fact]
    public void Init_VisibilityModifier_IsRejected()
    {
        foreach (var visibility in new[] { "pub", "internal", "priv" })
            Assert.Contains(EsHarness.AllDiagnostics($"namespace Test\n{visibility} init {{ }}"),
                d => d.Message.Contains("ES2205"));
        Assert.Contains(EsHarness.AllDiagnostics("namespace Test\n[Obsolete]\ninit { }"),
            d => d.Message.Contains("ES2205"));
    }

    [Fact]
    public void Init_Return_IsRejected()
        => Assert.Contains(EsHarness.Diagnostics("""
            namespace Test
            init { return }
            """), d => d.Message.Contains("ES2207"));

    [Fact]
    public void Init_Yield_IsRejected()
        => Assert.Contains(EsHarness.Diagnostics("""
            namespace Test
            init { yield 1 }
            """), d => d.Message.Contains("ES2131"));

    [Fact]
    public void Init_Await_IsRejected()
        => Assert.Contains(EsHarness.Diagnostics("""
            namespace Test
            init { await 1 }
            """), d => d.Message.Contains("ES2208"));

    [Fact]
    public void Init_AsyncLet_IsRejected()
        => Assert.Contains(EsHarness.Diagnostics("""
            namespace Test
            init { async let result = 1 }
            """), d => d.Message.Contains("ES2208"));

    [Fact]
    public void Init_AwaitFor_IsRejected()
        => Assert.Contains(EsHarness.Diagnostics("""
            namespace Test
            init { await for item in [1] { } }
            """), d => d.Message.Contains("ES2208"));

    [Fact]
    public void NamespaceLet_IsReadableFromFreeFunction()
        => Assert.Equal(3, EsHarness.Run("""
            namespace Test
            let environment = "dev"
            func value() -> int = environment.Length
            """, "value"));

    [Fact]
    public void NamespaceVar_AssignmentWritesHostField()
        => Assert.Equal(9, EsHarness.Run("""
            namespace Test
            var value = 2
            func run() -> int {
                value = 9
                return value
            }
            """, "run"));

    [Fact]
    public void NamespaceVar_CompoundAssignmentWorksFromAsyncStateMachine()
        => Assert.Equal(7, EsHarness.Await(EsHarness.Run("""
            namespace Test
            var value = 2
            func run() -> int {
                await Task.Delay(1)
                value += 5
                return value
            }
            """, "run")));

    [Fact]
    public void NamespaceStateInitializer_CanReadStaticProperty()
        => Assert.Equal("production", EsHarness.Run("""
            namespace Test
            static AppConfig {
                let Environment: string = "production"
            }
            let currentEnv = AppConfig.Environment
            func read() -> string = currentEnv
            """, "read"));

    [Fact]
    public void TypedLocal_CanReadStaticProperty()
        => Assert.Equal("production", EsHarness.Run("""
            namespace Test
            static AppConfig {
                let Environment: string = "production"
            }
            func read() -> string {
                currentEnv: string = AppConfig.Environment
                return currentEnv
            }
            """, "read"));

    [Fact]
    public void NamespaceLet_AssignmentIsRejected()
        => Assert.Contains(EsHarness.Diagnostics("""
            namespace Test
            let environment: string = "dev"
            func change() { environment = "prod" }
            """), d => d.Message.Contains("immutable"));

    [Fact]
    public void NamespaceComputedProperty_RecomputesFromHostState()
        => Assert.Equal(8, EsHarness.Run("""
            namespace Test
            var basis = 3
            let doubled: int => basis * 2
            func run() -> int {
                basis = 4
                return doubled
            }
            """, "run"));

    [Fact]
    public void NamespaceStoredVarProperty_ReadsAndWritesThroughAccessors()
        => Assert.Equal(9, EsHarness.Await(EsHarness.Run("""
            namespace Test
            var count: int { }
            func run() -> int {
                await Task.Delay(1)
                count = 9
                return count
            }
            """, "run")));

    [Fact]
    public void NamespaceCustomSetter_StoresItsResult()
        => Assert.Equal(10, EsHarness.Run("""
            namespace Test
            var score: int { set(v) => v * 2 }
            func run() -> int {
                score = 5
                return score
            }
            """, "run"));

    [Fact]
    public void NamespaceCustomGetterAndBehavioralSetter_RouteThroughAuthoredAccessors()
        => Assert.Equal(42, EsHarness.Run("""
            namespace Test
            var raw: int
            func replace(value: int) { raw = value }
            var display: int {
                get => raw + 1
                set(value) => replace(value)
            }
            func run() -> int {
                display = 41
                return display
            }
            """, "run"));

    [Fact]
    public void NamespaceStoredLetProperty_IsWritableOnlyFromInit()
        => Assert.Equal("ready", EsHarness.Run("""
            namespace Test
            let status: string { }
            init { status = "ready" }
            func read() -> string = status
            """, "read"));

    [Fact]
    public void NamespaceReadOnlyProperties_RejectOrdinaryAssignment()
    {
        Assert.Contains(EsHarness.Diagnostics("""
            namespace Test
            let status: string { }
            func bad() { status = "changed" }
            """), d => d.Message.Contains("immutable"));
        Assert.Contains(EsHarness.Diagnostics("""
            namespace Test
            let status: string => "ready"
            func bad() { status = "changed" }
            """), d => d.Message.Contains("immutable"));
    }

    [Fact]
    public void NamespaceProperty_EmitsStaticClrPropertyMetadata()
    {
        var (assembly, _) = EsHarness.EmitCecil("""
            namespace Test
            pub var count: int { }
            pub let doubled: int => count * 2
            func read() -> int = count
            """);
        using (assembly)
        {
            var host = assembly.MainModule.GetType("Test.Test")!;
            var property = Assert.Single(host.Properties, p => p.Name == "count");
            Assert.False(property.HasThis);
            Assert.True(property.GetMethod!.IsStatic);
            Assert.True(property.SetMethod!.IsStatic);
            Assert.True(property.GetMethod.IsPublic);
            Assert.DoesNotContain(host.Fields, f => f.Name == "count");
            Assert.Contains(host.Fields, f => f.Name == "<count>k__BackingField" && f.IsPrivate);
            var computed = Assert.Single(host.Properties, p => p.Name == "doubled");
            Assert.True(computed.GetMethod!.IsStatic);
            Assert.Null(computed.SetMethod);
            Assert.DoesNotContain(host.Fields, f => f.Name.Contains("doubled"));
        }
    }

    [Fact]
    public void ClassInit_RemainsAnInstanceConstructorFeature()
    {
        var (assembly, _) = EsHarness.EmitCecil("""
            namespace Test
            class Box {
                var value: int = 0
                init { value = 3 }
            }
            func value() -> int = Box().value
            """);
        using (assembly)
        {
            var host = assembly.MainModule.GetType("Test.Test")!;
            Assert.DoesNotContain(host.Methods, m => m.Name == ".cctor");
            Assert.Equal(3, EsHarness.Run("""
                namespace Test
                class Box { var value: int = 0 init { value = 3 } }
                func value() -> int = Box().value
                """, "value"));
        }
    }

    static IReadOnlyList<Esharp.Diagnostics.Diagnostic> Diagnostics(IReadOnlyList<string> sources)
    {
        var data = new Esharp.Binder.CompilationData();
        var pipeline = new Esharp.Compilation.CompilationPipeline(data);
        var units = sources.Select((source, index) =>
        {
            var parser = new Esharp.Syntax.Parsing.Parser(source, $"test{index}.es");
            var unit = parser.ParseCompilationUnit();
            Assert.Empty(parser.Diagnostics);
            return unit;
        }).ToList();
        return pipeline.BindAndLower(units).Diagnostics
            .Where(d => d.Severity == Esharp.Diagnostics.DiagnosticSeverity.Error)
            .ToList();
    }
}
