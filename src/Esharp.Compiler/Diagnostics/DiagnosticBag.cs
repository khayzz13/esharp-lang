namespace Esharp.Compiler.Diagnostics;

public sealed class DiagnosticBag
{
    readonly List<Diagnostic> _diagnostics = [];

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public bool Any => _diagnostics.Count > 0;

    public void Report(string filePath, int line, int column, string message) =>
        _diagnostics.Add(new Diagnostic(filePath, line, column, message));

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _diagnostics.AddRange(diagnostics);
}
