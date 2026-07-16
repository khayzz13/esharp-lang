using Esharp.BoundTree;
using Esharp.Diagnostics;
using Esharp.Syntax.Parsing;
using Esharp.Syntax;
using Esharp.CodeGen;
using Esharp.Metadata;
// CompilationData lives in the bind+flow+lower assembly; alias past the sibling
// `Esharp.Binder` namespace (reachable here as the bare name `Binder`).
using CompilationData = Esharp.Binder.CompilationData;
using Microsoft.CodeAnalysis.CSharp;
using Diagnostic = Esharp.Diagnostics.Diagnostic;
using DiagnosticSeverity = Esharp.Diagnostics.DiagnosticSeverity;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using RoslynMetadataReference = Microsoft.CodeAnalysis.MetadataReference;
using RoslynOutputKind = Microsoft.CodeAnalysis.OutputKind;

namespace Esharp.Compilation;

/// An immutable snapshot of a Workspace's document set, captured atomically.
/// Callers obtain a Compilation through <see cref="Workspace.CurrentCompilation"/>;
/// the snapshot's documents/diagnostics/emit-result are queryable but the snapshot
/// itself does not mutate. <see cref="Workspace.UpdateDocument"/> produces a new
/// snapshot; older snapshot references stay valid for any consumer that captured one.
///
/// <para>
/// <strong>[Δ] Bind ONCE.</strong> The old double-bind is gone: the single
/// <see cref="Build"/> call runs <see cref="CompilationPipeline.BindAndLower"/>
/// which populates the <see cref="CompilationData.Sink"/> during the single pass.
/// <see cref="GetSemanticModel"/> reads the same sink data and wraps it into a
/// <see cref="Esharp.Diagnostics.Semantics.SemanticModel"/> — no second bind.
/// </para>
///
/// <para>
/// <strong>[Δ] C# transpiler path dropped.</strong> <c>CSharpEmitter</c> is not
/// carried forward; IL (<see cref="ILEmitter"/>) is the only backend. The mixed-
/// language path (E# + .cs sources, ILRepack fusion) is preserved.
/// </para>
///
/// <para>
/// <strong>[Δ] Lowering stage inserted.</strong> The pipeline drives bind → lower
/// via <see cref="CompilationPipeline.BindAndLower"/>. Codegen receives a CORE-
/// only <see cref="BoundProgram"/>; the SemanticModel uses the pre-lowering FEATURE
/// + CORE tree (built from the sink data captured during bind).
/// </para>
///
/// <para>
/// <strong>[Δ] MetadataReader wired.</strong> Each Compilation snapshot holds a
/// <see cref="Esharp.Metadata.MetadataReader"/> pre-loaded with the project's
/// reference set. The LSP hover / completion / go-to-def paths query it for
/// external type and member resolution without reflection or assembly loading.
/// </para>
public sealed class Compilation : IDisposable
{
    readonly string _assemblyName;
    readonly IReadOnlyList<Document> _documents;
    readonly IReadOnlyList<MetadataReference> _references;
    readonly OutputKind _outputKind;
    readonly ProjectOptions _options;
    readonly CompilationData _data;

    // [Δ] MetadataReader — one per snapshot. Loaded during Build() alongside
    // ILTypeResolver.PreloadReferenceAssemblies so both consumers share one pass.
    // Exposed to LSP consumers via MetadataTypeResolver.
    readonly Esharp.Metadata.MetadataXmlDocs _xmlDocs;
    readonly Esharp.Metadata.ExternalSymbols _externalSymbols;
    readonly Esharp.Metadata.MetadataReader _metadataReader;

    List<CompilationUnitSyntax>? _parsedUnits;
    // [Δ] _loweredProgram holds the post-lowering CORE-only program for codegen.
    BoundProgram? _loweredProgram;
    List<Diagnostic>? _parseDiagnostics;
    CSharpCompilation? _csharpCompilation;
    List<Diagnostic>? _csharpDiagnostics;
    bool _built;
    bool _disposed;

    // The retained parse products — the snapshot owns them (the Document record
    // stays pure input). GetSyntaxTree exposes them without re-parsing.
    readonly Dictionary<string, CompilationUnitSyntax> _treesByUri = new(StringComparer.Ordinal);

    // [Δ] SemanticModel is built from sink data captured during the single bind,
    // not from a separate rebind. Lazy on first call to GetSemanticModel().
    Esharp.Diagnostics.Semantics.SemanticModel? _semanticModel;

    Compilation(string assemblyName, IReadOnlyList<Document> documents,
        IReadOnlyList<MetadataReference> references, OutputKind outputKind, ProjectOptions options)
    {
        _assemblyName = assemblyName;
        _documents = documents;
        _references = references;
        _outputKind = outputKind;
        _options = options;
        _data = new CompilationData
        {
            EnableImplicitUsings = options.EnableImplicitUsings,
            ShowAllocations = options.ShowAllocations,
        };
        _xmlDocs = new Esharp.Metadata.MetadataXmlDocs();
        _externalSymbols = new Esharp.Metadata.ExternalSymbols();
        _metadataReader = new Esharp.Metadata.MetadataReader(_externalSymbols, _xmlDocs);
    }

    internal static Compilation Snapshot(
        string assemblyName,
        IReadOnlyList<Document> documents,
        IReadOnlyList<MetadataReference> references,
        OutputKind outputKind = OutputKind.Library,
        ProjectOptions? options = null)
        => new(assemblyName, documents, references, outputKind, options ?? new ProjectOptions());

    /// The metadata-backed external type resolver for this snapshot. LSP consumers
    /// (hover, completion, go-to-def) call this to resolve external types and members
    /// without reflection or assembly loading. Populated during <see cref="Build"/>.
    public Esharp.Metadata.MetadataReader MetadataTypeResolver
    {
        get { Build(); return _metadataReader; }
    }

    public string AssemblyName => _assemblyName;
    public IReadOnlyList<Document> Documents => _documents;
    public IReadOnlyList<MetadataReference> References => _references;

    /// The retained parse tree for a document — the snapshot's parse product, kept
    /// per document URI so tooling re-reads structure without re-parsing. Null for a
    /// <c>.cs</c> document (Roslyn owns those) or an unknown URI.
    public CompilationUnitSyntax? GetSyntaxTree(string documentUri)
    {
        Build();
        return _treesByUri.TryGetValue(documentUri, out var t) ? t : null;
    }

    /// The queryable semantic view of this snapshot.
    ///
    /// <para>
    /// <strong>[Δ] No second bind.</strong> The <see cref="CompilationData.Sink"/>
    /// was populated during the single <see cref="Build"/> pass. The SemanticModel
    /// wraps that accumulated sink data; it does NOT trigger another
    /// RegisterTypes → RegisterSignatures → BindUnit round-trip.
    /// </para>
    public Esharp.Diagnostics.Semantics.SemanticModel GetSemanticModel()
    {
        if (_semanticModel is not null) return _semanticModel;
        Build();

        // The sink on _data was populated by the single BindAndLower call in Build().
        // Cast it to the collecting variant the SemanticModel needs; if it isn't one
        // (the default no-op sink) we install a collecting one and do ONE targeted
        // bind (still no double-bind on the normal path — this branch fires only when
        // a snapshot was built without a collecting sink, e.g. a bare Compilation
        // created before the Workspace installs the LSP sink).
        if (_data.Sink is Esharp.Diagnostics.Semantics.CollectingSemanticSink existing)
        {
            return _semanticModel = new Esharp.Diagnostics.Semantics.SemanticModel(
                existing, _data.Externals,
                type => new CompilationPipeline(_data).ResolveRuntime(type));
        }

        // Fallback: install a collecting sink and re-bind without lowering (the
        // SemanticModel needs FEATURE nodes intact).
        var collector = new Esharp.Diagnostics.Semantics.CollectingSemanticSink();
        var modelData = new CompilationData
        {
            Sink = collector,
            EnableImplicitUsings = _options.EnableImplicitUsings,
            ShowAllocations = _options.ShowAllocations,
        };
        if (_csharpCompilation is not null)
            foreach (var handle in RoslynSymbolAdapter.CollectSourceDeclaredTypes(_csharpCompilation))
                if (modelData.Symbols.ResolveBound(handle.Name, 0) is null)
                    modelData.Symbols.RegisterCSharpType(handle);

        var modelPipeline = new CompilationPipeline(modelData);
        modelPipeline.Bind(_parsedUnits!);
        return _semanticModel = new Esharp.Diagnostics.Semantics.SemanticModel(
            collector, modelData.Externals, modelPipeline.ResolveRuntime);
    }

    public IReadOnlyList<Diagnostic> GetDiagnostics()
    {
        Build();
        var combined = new List<Diagnostic>(
            _parseDiagnostics!.Count
            + _data.Diagnostics.Diagnostics.Count
            + (_csharpDiagnostics?.Count ?? 0));
        combined.AddRange(_parseDiagnostics);
        if (_csharpDiagnostics is not null) combined.AddRange(_csharpDiagnostics);
        combined.AddRange(_data.Diagnostics.Diagnostics);
        return combined;
    }

    public EmitResult EmitToFile(string path, bool debugSymbols = true)
    {
        Build();

        var bindDiagnostics = GetDiagnostics();
        if (bindDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return new EmitResult(false, bindDiagnostics);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var refPaths = _references.Select(r => r.Path).ToList();

        // The lowered BoundProgram — CORE-only, ready for codegen.
        var program = _loweredProgram!;

        if (_csharpCompilation is null)
        {
            // E#-only path.
            try
            {
                var (esharpOnlyAsm, ilDiagnostics) = CodeGenerator.Generate(
                    program, _assemblyName, debugSymbols, refPaths,
                    internalsVisibleTo: null,
                    externalSymbols: null,
                    outputKind: TranslateOutputKind(_outputKind),
                    implicitUsings: _options.EnableImplicitUsings,
                    optimization: _options.Optimization);
                using (esharpOnlyAsm)
                {
                    if (debugSymbols)
                        Esharp.CodeGen.ILPdbWriter.WriteWithPdb(esharpOnlyAsm, path);
                    else
                        Esharp.CodeGen.ILPdbWriter.WriteWithoutPdb(esharpOnlyAsm, path);
                }
                var combined = bindDiagnostics.Concat(ilDiagnostics).ToList();
                if (!combined.Any(d => d.Severity == DiagnosticSeverity.Error))
                {
                    var refDirs = refPaths
                        .Select(p => System.IO.Path.GetDirectoryName(p))
                        .Where(d => !string.IsNullOrEmpty(d))
                        .Select(d => d!);
                    foreach (var f in Esharp.CodeGen.IlVerification.VerifyFatal(path, refDirs))
                        combined.Add(new Diagnostic("", 0, 0, DiagnosticSeverity.Error, "ES0900",
                            $"emitted IL failed verification at {f.Method}: {f.CodeName} — {f.Message}"));
                }
                return new EmitResult(!combined.Any(d => d.Severity == DiagnosticSeverity.Error), combined);
            }
            catch (Exception ex)
            {
                return new EmitResult(false, bindDiagnostics
                    .Append(MakeInternalDiagnostic("ES9002", "E# IL emission threw", ex, path))
                    .ToList());
            }
        }

        // Mixed-language emit: each side writes to memory, Fuser merges to disk.
        var csharpHalfName = CSharpHalfAssemblyName(_assemblyName);
        byte[] esharpBytes;
        try
        {
            using var esharpPe = new MemoryStream();
            var (esharpAsm, esharpIlDiagnostics) = CodeGenerator.Generate(
                program, _assemblyName, debugSymbols: false,
                referencePaths: refPaths, internalsVisibleTo: csharpHalfName,
                externalSymbols: _data.Symbols,
                outputKind: TranslateOutputKind(_outputKind),
                implicitUsings: _options.EnableImplicitUsings,
                optimization: _options.Optimization);
            using (esharpAsm)
                esharpAsm.Write(esharpPe);
            bindDiagnostics = bindDiagnostics.Concat(esharpIlDiagnostics).ToList();
            if (bindDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                return new EmitResult(false, bindDiagnostics);
            esharpBytes = esharpPe.ToArray();
        }
        catch (Exception ex)
        {
            return new EmitResult(false, bindDiagnostics
                .Append(MakeInternalDiagnostic("ES9002", "E# IL emission threw during mixed-language build", ex, path))
                .ToList());
        }

        // C# emits referencing the just-written E# half.
        byte[] csharpBytes;
        try
        {
            var esharpRef = RoslynMetadataReference.CreateFromImage(esharpBytes);
            var csharpComp = _csharpCompilation.AddReferences(esharpRef);
            using var csharpPe = new MemoryStream();
            var csharpEmit = csharpComp.Emit(csharpPe);
            var csharpEmitDiags = csharpEmit.Diagnostics.Select(ConvertRoslynDiagnostic).ToList();
            bindDiagnostics = bindDiagnostics.Concat(csharpEmitDiags).ToList();
            if (csharpEmitDiags.Any(d => d.Severity == DiagnosticSeverity.Error))
                return new EmitResult(false, bindDiagnostics);
            csharpBytes = csharpPe.ToArray();
        }
        catch (Exception ex)
        {
            return new EmitResult(false, bindDiagnostics
                .Append(MakeInternalDiagnostic("ES9003", "C# half (Roslyn) emission threw", ex, path))
                .ToList());
        }

        // Fuse both PEs into one assembly at the target path.
        var fuseDiags = AssemblyFuser.Fuse(esharpBytes, csharpBytes, _assemblyName, path, refPaths, _outputKind);
        var all = bindDiagnostics.Concat(fuseDiags).ToList();
        if (!all.Any(d => d.Severity == DiagnosticSeverity.Error))
            all.AddRange(VerifyFusedOutput(path, refPaths));
        return new EmitResult(!all.Any(d => d.Severity == DiagnosticSeverity.Error), all);
    }

    public EmitResult EmitTo(Stream peStream)
    {
        Build();

        var bindDiagnostics = GetDiagnostics();
        if (bindDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return new EmitResult(false, bindDiagnostics);

        var refPaths = _references.Select(r => r.Path).ToList();
        var program = _loweredProgram!;

        if (_csharpCompilation is null)
        {
            var (assembly, ilDiagnostics) = CodeGenerator.Generate(
                program, _assemblyName, debugSymbols: false,
                referencePaths: refPaths, implicitUsings: _options.EnableImplicitUsings,
                optimization: _options.Optimization);
            using (assembly) assembly.Write(peStream);
            var all = bindDiagnostics.Concat(ilDiagnostics).ToList();
            return new EmitResult(!all.Any(d => d.Severity == DiagnosticSeverity.Error), all);
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"{_assemblyName}_{Guid.NewGuid():N}.dll");
        try
        {
            var result = EmitToFile(tempPath, debugSymbols: false);
            if (result.Success && File.Exists(tempPath))
            {
                using var fs = File.OpenRead(tempPath);
                fs.CopyTo(peStream);
            }
            return result;
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    void Build()
    {
        if (_built) return;
        _built = true;

        // 1. Split documents by extension.
        var esDocs = new List<Document>();
        var csDocs = new List<Document>();
        foreach (var doc in _documents)
        {
            if (doc.Uri.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                csDocs.Add(doc);
            else
                esDocs.Add(doc);
        }

        // 2. Parse E# documents.
        _parsedUnits = new List<CompilationUnitSyntax>(esDocs.Count);
        _parseDiagnostics = new List<Diagnostic>();
        foreach (var doc in esDocs)
        {
            var parser = new Parser(doc.Text.Content, doc.Uri);
            var syntax = parser.ParseCompilationUnit();
            _parseDiagnostics.AddRange(parser.Diagnostics);
            _parsedUnits.Add(syntax);
            _treesByUri[doc.Uri] = syntax;
        }

        // Parse errors do NOT short-circuit binding: trees carry error-recovery nodes;
        // the binder binds what it can; diagnostics gate emission, not binding.

        // 3. Roslyn declare-only pass over .cs documents (mixed-language seam).
        _csharpDiagnostics = new List<Diagnostic>();
        if (csDocs.Count > 0)
        {
            var csTrees = csDocs.Select(d => CSharpSyntaxTree.ParseText(d.Text.Content, path: d.Uri)).ToList();
            if (_options.EnableImplicitUsings)
                csTrees.Add(CSharpSyntaxTree.ParseText(ImplicitGlobalUsings, path: "<synth_global_usings>"));
            csTrees.Add(CSharpSyntaxTree.ParseText(
                $"""[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("{_assemblyName}")]""",
                path: "<synth_ivt>"));
            var refs = BuildRoslynReferences(_references);
            var roslynKind = _outputKind == OutputKind.Console
                ? RoslynOutputKind.ConsoleApplication
                : RoslynOutputKind.DynamicallyLinkedLibrary;
            _csharpCompilation = CSharpCompilation.Create(
                CSharpHalfAssemblyName(_assemblyName),
                csTrees, refs,
                new CSharpCompilationOptions(roslynKind, allowUnsafe: true,
                    nullableContextOptions: _options.Nullable
                        ? Microsoft.CodeAnalysis.NullableContextOptions.Enable
                        : Microsoft.CodeAnalysis.NullableContextOptions.Disable));

            var generators = RoslynGenerators.Framework();
            if (generators.Count > 0)
            {
                var driver = Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(generators);
                driver.RunGeneratorsAndUpdateCompilation(_csharpCompilation, out var generated, out var genDiags);
                _csharpCompilation = (CSharpCompilation)generated;
                foreach (var d in genDiags)
                    if (d.Severity == RoslynSeverity.Error)
                        _csharpDiagnostics.Add(ConvertRoslynDiagnostic(d));
            }

            foreach (var d in _csharpCompilation.GetParseDiagnostics())
                _csharpDiagnostics.Add(ConvertRoslynDiagnostic(d));

            if (_csharpDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                _loweredProgram = new BoundProgram([], _data);
                return;
            }

            // 4. Project C# types into ICSharpTypeHandle and register into the
            //    shared SymbolTable the E# binder reads from.
            var handles = RoslynSymbolAdapter.CollectSourceDeclaredTypes(_csharpCompilation);
            foreach (var handle in handles)
            {
                if (_data.Symbols.ResolveBound(handle.Name, 0) is not null) continue;
                _data.Symbols.RegisterCSharpType(handle);
            }
        }

        // 5. Preload reference assemblies — both consumers in one pass.
        //    a) ILTypeResolver (Cecil path): populates TypeReference caches for IL emission.
        //    b) MetadataReader (SRM path): populates the FQN type index for no-reflection
        //       external type resolution; the same assembly list, no double I/O.
        if (_references.Count > 0)
        {
            var refPaths = _references.Select(r => r.Path).ToList();
            ILTypeResolver.PreloadReferenceAssemblies(refPaths);
            _metadataReader.LoadReferences(refPaths);
        }

        // 6. [Δ] BindAndLower — the SINGLE bind + lowering pass. Produces a CORE-only
        //    BoundProgram. The sink on _data captures occurrence data during the bind
        //    phase; GetSemanticModel reads that sink without a second bind.
        var pipeline = new CompilationPipeline(_data);
        _loweredProgram = pipeline.BindAndLower(_parsedUnits);
    }

    internal static string CSharpHalfAssemblyName(string finalAssemblyName) => finalAssemblyName + "_CSharp";

    const string ImplicitGlobalUsings = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    static Esharp.CodeGen.ILOutputKind TranslateOutputKind(OutputKind kind) => kind switch
    {
        OutputKind.Console => Esharp.CodeGen.ILOutputKind.Console,
        _ => Esharp.CodeGen.ILOutputKind.Library,
    };

    static IReadOnlyList<RoslynMetadataReference> BuildRoslynReferences(
        IReadOnlyList<MetadataReference> projectRefs)
    {
        var refs = new List<RoslynMetadataReference>();
        var tpa = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")) ?? "";
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(path)) refs.Add(RoslynMetadataReference.CreateFromFile(path));
        foreach (var r in projectRefs)
            if (File.Exists(r.Path)) refs.Add(RoslynMetadataReference.CreateFromFile(r.Path));
        return refs;
    }

    static IReadOnlyList<Diagnostic> VerifyFusedOutput(string path, IReadOnlyList<string> refPaths)
    {
        var searchDirs = refPaths
            .Select(p => Path.GetDirectoryName(p) ?? "")
            .Where(d => d.Length > 0)
            .Distinct();
        return Esharp.CodeGen.IlVerification.VerifyFatal(path, searchDirs)
            .Select(f => new Diagnostic(path, 0, 0, DiagnosticSeverity.Error, "ES0900",
                $"emitted IL failed verification at {f.Method}: {f.CodeName} — {f.Message}",
                DiagnosticSource.Workspace))
            .ToList();
    }

    static Diagnostic MakeInternalDiagnostic(string code, string label, Exception ex, string path)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(label).Append(": ").Append(ex.Message);
        var inner = ex.InnerException;
        while (inner is not null) { sb.Append(" → ").Append(inner.Message); inner = inner.InnerException; }
        return new Diagnostic(path, 0, 0, DiagnosticSeverity.Error, code, sb.ToString(), DiagnosticSource.Workspace);
    }

    static Diagnostic ConvertRoslynDiagnostic(RoslynDiagnostic d)
    {
        var severity = d.Severity == RoslynSeverity.Error
            ? DiagnosticSeverity.Error
            : DiagnosticSeverity.Warning;
        var lineSpan = d.Location.GetLineSpan();
        return new Diagnostic(
            lineSpan.Path ?? "",
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1,
            severity, d.Id, d.GetMessage(), DiagnosticSource.CSharp);
    }

    // ── IDisposable ────────────────────────────────────────────────────────
    // The MetadataReader holds open PEReaders for each referenced assembly.
    // Snapshots are immutable; callers that hold a snapshot should dispose when done.
    // Workspace.Rebuild() creates a new snapshot and does not dispose the old one —
    // the Workspace itself calls Dispose on the previous snapshot when it replaces it.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _metadataReader.Dispose();
    }
}
