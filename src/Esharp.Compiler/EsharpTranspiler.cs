using Esharp.Compiler.Binding;
using Esharp.Compiler.Diagnostics;
using Esharp.Compiler.Emit;
using Esharp.Compiler.Parsing;
using Esharp.Compiler.Syntax;

namespace Esharp.Compiler;

public sealed class EsharpTranspiler
{
    public CompilationResult Transpile(string source, string filePath = "input.es")
    {
        var parser = new Parser(source, filePath);
        var syntax = parser.ParseCompilationUnit();
        if (parser.Diagnostics.Count > 0)
            return new CompilationResult(string.Empty, parser.Diagnostics);

        var binder = new Binder();
        var boundTree = binder.Bind(syntax);
        if (binder.Diagnostics.Count > 0)
            return new CompilationResult(string.Empty, binder.Diagnostics);

        var generated = CSharpEmitter.Emit(boundTree, filePath);
        return new CompilationResult(generated, []);
    }

    public ProjectCompilationResult TranspileProject(IReadOnlyList<(string source, string filePath)> files)
    {
        // Parse all files
        var parsed = new List<(CompilationUnitSyntax syntax, string filePath)>();
        var allDiagnostics = new List<Diagnostic>();
        foreach (var (source, filePath) in files)
        {
            var parser = new Parser(source, filePath);
            var syntax = parser.ParseCompilationUnit();
            if (parser.Diagnostics.Count > 0)
                allDiagnostics.AddRange(parser.Diagnostics);
            else
                parsed.Add((syntax, filePath));
        }

        if (allDiagnostics.Count > 0)
            return new ProjectCompilationResult([], allDiagnostics);

        // Register types from all files into shared binder
        var binder = new Binder();
        foreach (var (syntax, _) in parsed)
            binder.RegisterTypes(syntax);

        if (binder.Diagnostics.Count > 0)
            return new ProjectCompilationResult([], binder.Diagnostics);

        // Bind and emit each file
        var results = new List<FileCompilationResult>();
        foreach (var (syntax, filePath) in parsed)
        {
            var boundTree = binder.BindUnit(syntax);
            if (binder.Diagnostics.Count > 0)
            {
                results.Add(new FileCompilationResult(filePath, string.Empty, binder.Diagnostics));
                continue;
            }
            var generated = CSharpEmitter.Emit(boundTree, filePath);
            results.Add(new FileCompilationResult(filePath, generated, []));
        }

        return new ProjectCompilationResult(results, []);
    }
}
