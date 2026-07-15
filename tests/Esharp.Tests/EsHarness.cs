using System.Reflection;
using Esharp.BoundTree;        // BoundProgram
using Esharp.Diagnostics;      // Diagnostic, DiagnosticSeverity
using Esharp.Syntax;           // CompilationUnitSyntax
using Esharp.Syntax.Parsing;   // Parser
using CompilationData = Esharp.Binder.CompilationData;  // aliased: a bare `using Esharp.Binder;` would let the sibling Esharp.Binder namespace shadow the `Binder` type alias from `namespace Esharp.Tests`
using Esharp.Compilation;      // CompilationPipeline
using Esharp.CodeGen;          // CodeGenerator, ILOutputKind
using Esharp.Lowering;         // LoweringPipeline, SynthesizedSymbolSink
using Mono.Cecil;              // AssemblyDefinition
// NB: the Binder type is referenced fully-qualified (Esharp.Binder.Binder) below, not via a
// `using Binder = ...` alias. This file imports the Esharp.Binder *assembly* (through its
// Esharp.Lowering / Esharp.Compilation namespaces), which surfaces the sibling `Esharp.Binder`
// namespace; from `namespace Esharp.Tests` that namespace shadows a same-named alias — the
// trap documented in CompilationPipeline.cs. Full qualification sidesteps it.

namespace Esharp.Tests;

/// Shared compile-and-run harness for the feature-seam test suites. Compiles an
/// `.es` source through the IL backend (the primary, source-of-truth backend),
/// loads the assembly, and invokes static functions on the `Test.Test` host
/// class. Keeps each topical test file free of boilerplate.
///
/// The pipeline is the post-rewrite one: parse → <see cref="CompilationPipeline.BindAndLower"/>
/// (bind + ordered lowering, producing a CORE-only <see cref="BoundProgram"/>) →
/// <see cref="CodeGenerator"/>. The old single-call `ILEmitter.Emit` ran lowering
/// implicitly; the split backend makes it an explicit stage, which this harness owns.
internal static class EsHarness
{
    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static int _counter;

    // -----------------------------------------------------------------------
    // Core pipeline. Parse every source, then bind + lower into a single
    // codegen-ready BoundProgram. Parse diagnostics are surfaced alongside,
    // since they live on the parser, not on the bound program.
    // -----------------------------------------------------------------------
    static (BoundProgram Program, IReadOnlyList<Diagnostic> ParseDiagnostics) Pipeline(IReadOnlyList<string> sources)
    {
        var data = new CompilationData();
        var pipeline = new CompilationPipeline(data);
        var units = new List<CompilationUnitSyntax>(sources.Count);
        var parseDiags = new List<Diagnostic>();
        for (var i = 0; i < sources.Count; i++)
        {
            var parser = new Parser(sources[i], sources.Count == 1 ? "test.es" : $"test{i}.es");
            units.Add(parser.ParseCompilationUnit());
            parseDiags.AddRange(parser.Diagnostics);
        }
        return (pipeline.BindAndLower(units), parseDiags);
    }

    /// Parse + bind + lower a single source into a codegen-ready BoundProgram,
    /// asserting the parse is clean (a parse error in a positive test is a bug).
    public static BoundProgram BindAndLower(string source)
    {
        var (program, parseDiags) = Pipeline([source]);
        Assert.Empty(parseDiags);
        return program;
    }

    // -----------------------------------------------------------------------
    // Emit to an in-memory Cecil AssemblyDefinition — the metadata/IL-inspection
    // entry. Mirrors the old `ILEmitter.Emit(bound, name)` return shape; the
    // caller writes/disposes the AssemblyDefinition as it needs.
    // -----------------------------------------------------------------------
    public static (AssemblyDefinition Assembly, IReadOnlyList<Diagnostic> Diagnostics) EmitCecil(
        string source, string tag = "EsFeat", bool debugSymbols = false, ILOutputKind outputKind = ILOutputKind.Library)
        => EmitCecil([source], tag, debugSymbols, outputKind);

    public static (AssemblyDefinition Assembly, IReadOnlyList<Diagnostic> Diagnostics) EmitCecil(
        IReadOnlyList<string> sources, string tag = "EsFeat", bool debugSymbols = false, ILOutputKind outputKind = ILOutputKind.Library)
    {
        var asmName = $"{tag}_{Interlocked.Increment(ref _counter)}";
        var (program, parseDiags) = Pipeline(sources);
        Assert.Empty(parseDiags);
        AssertNoErrors(program.Diagnostics);
        return CodeGenerator.Generate(program, asmName, debugSymbols, outputKind: outputKind);
    }

    /// Compile source via the IL backend; fails on any error. Failure messages
    /// carry EVERY diagnostic (warnings included) — the warning context is often
    /// the actual explanation of the error next to it.
    public static Assembly Compile(string source, string tag = "EsFeat")
        => Assembly.LoadFrom(CompileToPath(source, tag));

    /// Compile via the IL backend and return the emitted dll path (for metadata
    /// inspection that needs Cecil rather than a loaded Assembly).
    public static string CompileToPath(string source, string tag = "EsFeat")
        => CompileToPath([source], tag);

    public static string CompileToPath(IReadOnlyList<string> sources, string tag = "EsFeat")
    {
        var asmName = $"{tag}_{Interlocked.Increment(ref _counter)}";
        var (program, parseDiags) = Pipeline(sources);
        Assert.Empty(parseDiags);
        AssertNoErrors(program.Diagnostics);
        var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
        // Verification is on for every compiled assembly: unverifiable IL (the
        // InvalidProgramException class) surfaces here as an ES0900 error instead
        // of a runtime crash when the method is eventually invoked.
        var emitDiags = CodeGenerator.EmitToFile(program.Units, asmName, path, verify: true);
        AssertNoErrors(program.Diagnostics.Concat(emitDiags));
        return path;
    }

    // -----------------------------------------------------------------------
    // Bound-tree entry points — for the topical suites that bind explicitly (to
    // assert on binder.Diagnostics) and then want a Cecil assembly. They hold a
    // `binder` + `bound`; these lower that bound program through the binder's own
    // CompilationData (so synthesized symbols register against the same table) and
    // emit through the real backend. The split backend made lowering an explicit
    // stage — the old `ILEmitter.Emit(bound)` rolled it in; these put it back.
    // -----------------------------------------------------------------------
    static BoundProgram Lower(Esharp.Binder.Binder binder, IReadOnlyList<BoundCompilationUnit> units)
    {
        var program = new BoundProgram(units.ToList(), binder.Data);
        if (program.HasErrors) return program; // don't lower a broken program
        var sink = new SynthesizedSymbolSink(binder.Data.Symbols);
        return new LoweringPipeline(sink).Run(program);
    }

    // Param list mirrors CodeGenerator.Generate (and the old ILEmitter.Emit) so existing
    // call sites pass debugSymbols / internalsVisibleTo / externalSymbols / outputKind unchanged.
    public static (AssemblyDefinition Assembly, IReadOnlyList<Diagnostic> Diagnostics) EmitBound(
        Esharp.Binder.Binder binder, BoundCompilationUnit bound, string assemblyName,
        bool debugSymbols = false, IReadOnlyList<string>? referencePaths = null,
        string? internalsVisibleTo = null, Esharp.Symbols.SymbolTable? externalSymbols = null,
        ILOutputKind outputKind = ILOutputKind.Library, bool implicitUsings = true)
        => EmitBound(binder, [bound], assemblyName, debugSymbols, referencePaths, internalsVisibleTo, externalSymbols, outputKind, implicitUsings);

    public static (AssemblyDefinition Assembly, IReadOnlyList<Diagnostic> Diagnostics) EmitBound(
        Esharp.Binder.Binder binder, IReadOnlyList<BoundCompilationUnit> units, string assemblyName,
        bool debugSymbols = false, IReadOnlyList<string>? referencePaths = null,
        string? internalsVisibleTo = null, Esharp.Symbols.SymbolTable? externalSymbols = null,
        ILOutputKind outputKind = ILOutputKind.Library, bool implicitUsings = true)
        => CodeGenerator.Generate(Lower(binder, units), assemblyName, debugSymbols, referencePaths, internalsVisibleTo, externalSymbols, outputKind, implicitUsings);

    public static IReadOnlyList<Diagnostic> EmitBoundToFile(
        Esharp.Binder.Binder binder, BoundCompilationUnit bound, string assemblyName, string outputPath,
        bool debugSymbols = false, IReadOnlyList<string>? referencePaths = null, bool verify = false)
        => EmitBoundToFile(binder, [bound], assemblyName, outputPath, debugSymbols, referencePaths, verify);

    public static IReadOnlyList<Diagnostic> EmitBoundToFile(
        Esharp.Binder.Binder binder, IReadOnlyList<BoundCompilationUnit> units, string assemblyName, string outputPath,
        bool debugSymbols = false, IReadOnlyList<string>? referencePaths = null, bool verify = false)
        => CodeGenerator.EmitToFile(Lower(binder, units).Units, assemblyName, outputPath, debugSymbols, referencePaths, verify);

    /// Emit a bound program to disk **with ILVerify on**, fail the test on any
    /// error (unverifiable IL surfaces as ES0900), then load it. This is the only
    /// sanctioned load-and-invoke entry: an unverifiable assembly is the
    /// `InvalidProgramException`/`ExecutionEngineException` class that, when run
    /// in-process, takes down the whole xUnit host and truncates the run. Gating
    /// here turns that fatal host crash into a clean per-test failure, so the suite
    /// always completes and stays measurable — and enforces the spec invariant that
    /// every emitted assembly passes ILVerify. Negative/diagnostic tests that *want*
    /// to inspect an ES0900 keep calling EmitBoundToFile directly.
    public static Assembly EmitBoundToFileVerifiedAndLoad(
        Esharp.Binder.Binder binder, BoundCompilationUnit bound, string assemblyName, string outputPath,
        bool debugSymbols = false, IReadOnlyList<string>? referencePaths = null)
        => EmitBoundToFileVerifiedAndLoad(binder, [bound], assemblyName, outputPath, debugSymbols, referencePaths);

    public static Assembly EmitBoundToFileVerifiedAndLoad(
        Esharp.Binder.Binder binder, IReadOnlyList<BoundCompilationUnit> units, string assemblyName, string outputPath,
        bool debugSymbols = false, IReadOnlyList<string>? referencePaths = null)
    {
        var diags = EmitBoundToFile(binder, units, assemblyName, outputPath, debugSymbols, referencePaths, verify: true);
        AssertNoErrors(diags);
        return Assembly.LoadFrom(outputPath);
    }

    /// Fail when any ERROR is present; the failure message lists all collected
    /// diagnostics — errors and warnings — so the warning context travels with it.
    static void AssertNoErrors(IEnumerable<Diagnostic> diagnostics)
    {
        var all = diagnostics.ToList();
        if (all.Any(d => d.Severity == DiagnosticSeverity.Error))
            Assert.Fail(string.Join("\n", all.Select(d => d.ToString())));
    }

    /// Compile and invoke `Test.Test::method(args)` in one step.
    public static object? Run(string source, string method, params object?[] args)
        => Invoke(Compile(source), method, args);

    public static object? Invoke(Assembly asm, string method, params object?[] args)
    {
        var type = asm.GetType("Test.Test") ?? throw new Exception("Type Test.Test not found");
        var m = type.GetMethod(method, AnyStatic) ?? throw new Exception($"Method {method} not found");
        return m.Invoke(null, args.Length == 0 ? null : args);
    }

    /// Await a Task/ValueTask result (E# async functions surface as ValueTask<T>
    /// to reflection) and return the unwrapped value.
    public static object? Await(object? v)
    {
        if (v is null) return null;
        var t = v.GetType();
        // ValueTask<T> (the default async shape). A ValueTask is single-consumption: polling
        // GetResult() before completion throws NotSupportedException (it happens whenever the
        // body actually suspends). Convert to a Task and block on that — correct whether the
        // ValueTask completed synchronously or is still pending.
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ValueTask<>))
            return Await(t.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance)!.Invoke(v, null));
        // Task / Task<T>. The runtime type is often an internal Task<T> SUBCLASS (the
        // async builder's state-machine box), so a `GetGenericTypeDefinition() == Task<>`
        // check misses it. Match the base Task, complete it, then read the Result via the
        // Task<TResult> found by walking the base chain (non-generic Task has no Result).
        if (v is Task task)
        {
            task.GetAwaiter().GetResult(); // block + propagate exceptions
            for (var cur = t; cur is not null; cur = cur.BaseType)
                if (cur.IsGenericType && cur.GetGenericTypeDefinition() == typeof(Task<>))
                    return cur.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)!.GetValue(v);
            return null; // non-generic Task → void
        }
        if (v is ValueTask vt) { vt.GetAwaiter().GetResult(); return null; }
        return v;
    }

    /// Truly-async unwrap (no thread blocking) — for tests whose body genuinely suspends
    /// (e.g. an `await for` drain). Blocking via GetResult() under the parallel test runner can
    /// starve the thread pool the async producer needs; awaiting here keeps threads free.
    public static async Task<object?> AwaitAsync(object? v)
    {
        if (v is null) return null;
        var t = v.GetType();
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ValueTask<>))
            v = t.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance)!.Invoke(v, null);
        if (v is Task task)
        {
            await task.ConfigureAwait(false);
            for (var cur = v!.GetType(); cur is not null; cur = cur.BaseType)
                if (cur.IsGenericType && cur.GetGenericTypeDefinition() == typeof(Task<>))
                    return cur.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)!.GetValue(v);
            return null;
        }
        if (v is ValueTask vt) { await vt.ConfigureAwait(false); return null; }
        return v;
    }

    /// Read a Result member by name, tolerant of which Result the resolver bound:
    /// the E#-authored stdlib Result exposes IsOk/Value/Error as FIELDS (and has no
    /// IsError member — it is computed), while the C# seed exposes them as PROPERTIES.
    /// Tests read the live value through this so they pass against either model.
    public static object? ResultMember(object result, string name)
    {
        if (name == "IsError")
            return !(bool)ResultMember(result, "IsOk")!;
        var t = result.GetType();
        return t.GetField(name)?.GetValue(result) ?? t.GetProperty(name)?.GetValue(result);
    }

    /// Compile, invoke an async function, await it, and return (IsOk, Value/Error)
    /// for a `Result<T, E>` return. Throws if the value isn't a Result.
    public static (bool isOk, object? value, object? error) RunResultAsync(string source, string method, params object?[] args)
    {
        var r = Await(Invoke(Compile(source), method, args))!;
        var isOk = (bool)ResultMember(r, "IsOk")!;
        // Read only the live variant — `.Value` / `.Error` throw on the other.
        var value = isOk ? ResultMember(r, "Value") : null;
        var error = isOk ? null : ResultMember(r, "Error");
        return (isOk, value, error);
    }

    /// Collect binder error diagnostics for negative tests (expecting a diagnostic).
    /// Bind only — no lowering — since these assert on the binder's own diagnostics.
    public static IReadOnlyList<Diagnostic> Diagnostics(string source)
    {
        var parser = new Parser(source, "test.es");
        var syntax = parser.ParseCompilationUnit();
        var binder = new Esharp.Binder.Binder();
        binder.Bind(syntax);
        return binder.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    }

    /// All diagnostics (parser + binder + emit/verify), every severity, no asserts.
    /// For negative tests that expect an emit-time signal — e.g. an ILVerify ES0900
    /// error or a warning — rather than a binder error.
    public static IReadOnlyList<Diagnostic> AllDiagnostics(string source, string tag = "EsNeg")
    {
        var asmName = $"{tag}_{Interlocked.Increment(ref _counter)}";
        var (program, parseDiags) = Pipeline([source]);
        var all = new List<Diagnostic>();
        all.AddRange(parseDiags);
        all.AddRange(program.Diagnostics);
        // Only emit when parse + bind are error-free; a bad bound tree (or one
        // carrying error-recovery nodes) can throw in emit.
        if (!parseDiags.Concat(program.Diagnostics).Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            var path = Path.Combine(Path.GetTempPath(), $"{asmName}.dll");
            try { all.AddRange(CodeGenerator.EmitToFile(program.Units, asmName, path, verify: true)); }
            catch { /* an emit crash is itself a failure signal; leave collected diags as-is */ }
        }
        return all;
    }
}
