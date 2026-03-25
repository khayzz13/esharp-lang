using Esharp.Compiler.Diagnostics;

namespace Esharp.Compiler;

public sealed record CompilationResult(string GeneratedCode, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Success => Diagnostics.Count == 0;
}

public sealed record FileCompilationResult(string FilePath, string GeneratedCode, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Success => Diagnostics.Count == 0;
}

public sealed record ProjectCompilationResult(IReadOnlyList<FileCompilationResult> Files, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Success => Diagnostics.Count == 0;
}
