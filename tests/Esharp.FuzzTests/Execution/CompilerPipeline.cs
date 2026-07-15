using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Esharp.Diagnostics;
using Esharp.Syntax.Parsing;
using Esharp.Syntax;
using Esharp.Emit;        // ILOptimizer
using Esharp.CodeGen;     // CodeGenerator
using Esharp.BoundTree;   // BoundProgram, BoundCompilationUnit
using Esharp.Lowering;    // LoweringPipeline, SynthesizedSymbolSink
using Binder = Esharp.Binder.Binder;

namespace Esharp.FuzzTests.Execution;

/// The staged lex→parse→bind→emit→verify→load→invoke pipeline, with each
/// stage's exceptions attributed to that stage. This runs inside the child
/// process — a stack overflow or runaway loop here kills/hangs the child,
/// never the test runner.
internal sealed class CompilerPipeline
{
    static int _assemblyCounter;
    readonly string _workDir;

    public CompilerPipeline(string? workDir = null)
    {
        _workDir = workDir ?? Path.Combine(Path.GetTempPath(), "esharp-fuzz", Environment.ProcessId.ToString());
        Directory.CreateDirectory(_workDir);
    }

    public CaseResult Execute(CaseRequest request, Action<FuzzStage>? onStage = null)
    {
        var sw = Stopwatch.StartNew();
        var diagnostics = new List<Diagnostic>();
        var stage = FuzzStage.Parse;
        void Enter(FuzzStage next) { stage = next; onStage?.Invoke(next); }
        try
        {
            ILOptimizer.ShortenOpcodesEnabled = request.ShortenOpcodes;
            onStage?.Invoke(FuzzStage.Parse);

            var units = new List<CompilationUnitSyntax>();
            foreach (var file in request.Files)
            {
                var parser = new Parser(file.Source, file.FileName);
                units.Add(parser.ParseCompilationUnit());
                diagnostics.AddRange(parser.Diagnostics);
            }

            if (HasErrors(diagnostics))
                return Finish(request, OutcomeKind.Rejected, FuzzStage.Parse, diagnostics, sw);

            Enter(FuzzStage.Bind);
            var binder = new Esharp.Binder.Binder();
            foreach (var unit in units) binder.RegisterTypes(unit);
            foreach (var unit in units) binder.RegisterSignatures(unit);
            var bound = units.Select(binder.BindUnit).ToList();
            diagnostics.AddRange(binder.Diagnostics);

            if (HasErrors(diagnostics))
                return Finish(request, OutcomeKind.Rejected, FuzzStage.Bind, diagnostics, sw);

            Enter(FuzzStage.Emit);
            var asmName = $"EsFuzz_{Environment.ProcessId}_{Interlocked.Increment(ref _assemblyCounter)}";
            var path = Path.Combine(_workDir, $"{asmName}.dll");
            var emitDiagnostics = EmitToFile(binder, bound, asmName, path, verify: true);
            diagnostics.AddRange(emitDiagnostics);

            if (HasErrors(diagnostics))
            {
                // ES0900 is the ILVerify oracle firing: the binder accepted the
                // program and the backend emitted unverifiable IL.
                var kind = emitDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && d.Code == "ES0900")
                    ? OutcomeKind.VerifierError
                    : OutcomeKind.Rejected;
                return Finish(request, kind, FuzzStage.Emit, diagnostics, sw);
            }

            Enter(FuzzStage.Verify);
            var hash = AssemblyCanonicalizer.Hash(path);

            string? secondHash = null;
            if (request.EmitTwice)
            {
                var path2 = Path.Combine(_workDir, $"{asmName}_b.dll");
                var binder2 = new Esharp.Binder.Binder();
                var units2 = new List<CompilationUnitSyntax>();
                foreach (var file in request.Files)
                {
                    var parser2 = new Parser(file.Source, file.FileName);
                    units2.Add(parser2.ParseCompilationUnit());
                }
                foreach (var unit in units2) binder2.RegisterTypes(unit);
                foreach (var unit in units2) binder2.RegisterSignatures(unit);
                EmitToFile(binder2, units2.Select(binder2.BindUnit).ToList(), asmName, path2, verify: false);
                secondHash = AssemblyCanonicalizer.Hash(path2);
                TryDelete(path2);
            }

            if (!request.Invoke)
            {
                TryDelete(path);
                return Finish(request, OutcomeKind.Success, FuzzStage.Verify, diagnostics, sw,
                    assemblyHash: hash, secondHash: secondHash);
            }

            Enter(FuzzStage.Load);
            var assembly = Assembly.LoadFrom(path);
            var entryType = assembly.GetType(request.EntryType)
                ?? throw new InfrastructureException($"Entry type {request.EntryType} not found.");
            var method = entryType.GetMethod(request.EntryMethod,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InfrastructureException($"Entry method {request.EntryMethod} not found on {request.EntryType}.");

            Enter(FuzzStage.Invoke);
            object? value;
            try
            {
                value = Await(method.Invoke(null, null));
            }
            catch (Exception ex)
            {
                var inner = Unwrap(ex);
                var kind = IsJitOrLoaderReject(inner) ? OutcomeKind.JitReject : OutcomeKind.RuntimeException;
                return Finish(request, kind, FuzzStage.Invoke, diagnostics, sw,
                    exception: inner, assemblyHash: hash, secondHash: secondHash);
            }

            var (valueType, valueText) = Render(value);
            return Finish(request, OutcomeKind.Success, FuzzStage.Invoke, diagnostics, sw,
                valueType: valueType, valueText: valueText, assemblyHash: hash, secondHash: secondHash);
        }
        catch (InfrastructureException ex)
        {
            return Finish(request, OutcomeKind.Infrastructure, stage, diagnostics, sw, exception: ex);
        }
        catch (Exception ex)
        {
            // Loader/JIT-class exceptions can surface outside Invoke too (e.g. at
            // Assembly.LoadFrom); anything else thrown before Invoke is an ICE.
            var kind = stage >= FuzzStage.Load && IsJitOrLoaderReject(ex)
                ? OutcomeKind.JitReject
                : stage >= FuzzStage.Load ? OutcomeKind.Infrastructure : OutcomeKind.Ice;
            return Finish(request, kind, stage, diagnostics, sw, exception: ex);
        }
    }

    CaseResult Finish(
        CaseRequest request, OutcomeKind kind, FuzzStage stage, List<Diagnostic> diagnostics, Stopwatch sw,
        Exception? exception = null, string? valueType = null, string? valueText = null,
        string? assemblyHash = null, string? secondHash = null)
        => new(
            request.Id,
            kind,
            stage,
            diagnostics.Select(d => new DiagnosticInfo(d.Severity.ToString(), d.Code ?? "", d.Message, d.FilePath, d.Line, d.Column)).ToList(),
            ValueType: valueType,
            ValueText: valueText,
            ExceptionType: exception?.GetType().FullName,
            ExceptionMessage: exception?.Message,
            TopCompilerFrame: exception is null ? null : TopCompilerFrame(exception),
            StackTrace: exception?.StackTrace is { } st ? Truncate(st, 4000) : null,
            AssemblyHash: assemblyHash,
            SecondAssemblyHash: secondHash,
            DurationMs: sw.ElapsedMilliseconds);

    // The split backend made lowering an explicit stage between bind and codegen
    // (the old single-call ILEmitter.Emit folded it in). Lower the bound units
    // through the binder's own CompilationData — so synthesized symbols register
    // against the same table — then emit + verify through the real backend.
    static IReadOnlyList<Diagnostic> EmitToFile(Esharp.Binder.Binder binder, IReadOnlyList<BoundCompilationUnit> bound,
        string assemblyName, string outputPath, bool verify)
    {
        var program = new BoundProgram(bound.ToList(), binder.Data);
        if (!program.HasErrors)
            program = new LoweringPipeline(new SynthesizedSymbolSink(binder.Data.Symbols)).Run(program);
        return CodeGenerator.EmitToFile(program.Units, assemblyName, outputPath, verify: verify);
    }

    static bool HasErrors(IEnumerable<Diagnostic> diagnostics)
        => diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    static Exception Unwrap(Exception ex)
        => ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    static bool IsJitOrLoaderReject(Exception ex) => ex switch
    {
        InvalidProgramException or BadImageFormatException or TypeLoadException
            or MissingMethodException or MissingFieldException
            or MethodAccessException or FieldAccessException => true,
        TypeInitializationException { InnerException: { } inner } => IsJitOrLoaderReject(inner),
        _ => ex.GetType().FullName == "System.Security.VerificationException",
    };

    /// The topmost stack frame inside the compiler itself — the bucket
    /// discriminator. Frames from the harness or the BCL don't identify the bug.
    static string? TopCompilerFrame(Exception ex)
    {
        var trace = new StackTrace(ex, fNeedFileInfo: false);
        foreach (var frame in trace.GetFrames())
        {
            var method = frame.GetMethod();
            var ns = method?.DeclaringType?.Namespace;
            if (ns is not null && ns.StartsWith("Esharp.", StringComparison.Ordinal) &&
                !ns.StartsWith("Esharp.FuzzTests", StringComparison.Ordinal))
                return $"{method!.DeclaringType!.Name}.{method.Name}";
        }
        return null;
    }

    /// Unwrap Task/ValueTask shells (E# uncolored async surfaces ValueTask<T>,
    /// and the runtime box is often an internal Task<T> subclass).
    static object? Await(object? v)
    {
        if (v is null) return null;
        var t = v.GetType();
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var awaiter = t.GetMethod("GetAwaiter")!.Invoke(v, null)!;
            return awaiter.GetType().GetMethod("GetResult")!.Invoke(awaiter, null);
        }
        if (v is Task task)
        {
            task.GetAwaiter().GetResult();
            for (var cur = t; cur is not null; cur = cur.BaseType)
                if (cur.IsGenericType && cur.GetGenericTypeDefinition() == typeof(Task<>))
                    return cur.GetProperty("Result")!.GetValue(v);
            return null;
        }
        if (v is ValueTask vt) { vt.GetAwaiter().GetResult(); return null; }
        return v;
    }

    static (string Type, string Text) Render(object? value)
    {
        if (value is null) return ("null", "null");
        var text = value switch
        {
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        return (value.GetType().FullName ?? "?", text);
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* loaded assemblies stay mapped; best-effort */ }
    }

    sealed class InfrastructureException(string message) : Exception(message);
}
