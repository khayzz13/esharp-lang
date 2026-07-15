using Esharp.Syntax;

namespace Esharp.Diagnostics;

/// <summary>
/// A mutable accumulator for diagnostics produced during a single compilation phase.
/// All Report / Warn / Info / Hidden overloads route through <see cref="DiagnosticDescriptor"/>
/// so every diagnostic carries a stable id, title, and optional fixit class — the code-action
/// seam the LSP plan's WS6 maps <c>Id → fixit</c>.
///
/// The span-carrying overloads are the primary path. The positional (file/line/col)
/// overloads are kept for call sites that have not yet been updated to carry a full span;
/// they synthesize a point-span (End == Start) so the descriptor catalog round-trip still works.
/// </summary>
public sealed class DiagnosticBag
{
    readonly List<Diagnostic> _diagnostics = [];

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public bool Any => _diagnostics.Count > 0;

    // ------------------------------------------------------------------ descriptor-routed (primary)

    /// Report using an explicit descriptor from <see cref="DiagnosticCatalog"/>.
    /// The format args are substituted into <see cref="DiagnosticDescriptor.MessageFormat"/>
    /// at render time; pass none when the format needs no substitutions.
    public void Report(SourceSpan span, DiagnosticDescriptor descriptor, params object[] args) =>
        _diagnostics.Add(new Diagnostic(span, descriptor, DiagnosticSource.Esharp, args));

    public void Warn(SourceSpan span, DiagnosticDescriptor descriptor, params object[] args) =>
        _diagnostics.Add(new Diagnostic(span, descriptor with { DefaultSeverity = DiagnosticSeverity.Warning }, DiagnosticSource.Esharp, args));

    public void Info(SourceSpan span, DiagnosticDescriptor descriptor, params object[] args) =>
        _diagnostics.Add(new Diagnostic(span, descriptor with { DefaultSeverity = DiagnosticSeverity.Info }, DiagnosticSource.Esharp, args));

    public void Hidden(SourceSpan span, DiagnosticDescriptor descriptor, params object[] args) =>
        _diagnostics.Add(new Diagnostic(span, descriptor with { DefaultSeverity = DiagnosticSeverity.Hidden }, DiagnosticSource.Esharp, args));

    // ------------------------------------------------------------------ span + message (ad-hoc fast path)

    /// <summary>
    /// Located report: the span of the syntax (or span-stamped bound node) is in hand.
    /// An invalid span degrades to ("", 0, 0) rather than lying about a location.
    /// Uses the ES0001 ad-hoc descriptor — prefer the descriptor-routed overload for
    /// any diagnostic that will appear in the catalog and have a fixit.
    /// </summary>
    public void Report(SourceSpan span, string message) =>
        _diagnostics.Add(new Diagnostic(span, DiagnosticCatalog.AdHocError(message)));

    public void Warn(SourceSpan span, string message) =>
        _diagnostics.Add(new Diagnostic(span, DiagnosticCatalog.AdHocWarning(message)));

    public void Info(SourceSpan span, string message) =>
        _diagnostics.Add(new Diagnostic(span, DiagnosticCatalog.AdHocInfo(message)));

    public void Hidden(SourceSpan span, string message) =>
        _diagnostics.Add(new Diagnostic(span, DiagnosticCatalog.AdHocHidden(message)));

    // ------------------------------------------------------------------ Error / Warning aliases (flow-analysis call sites)
    //
    // FlowAnalysis (pillar 2) emits inline ES-coded diagnostics via `.Error` / `.Warning`.
    // These alias the span+message ad-hoc path so those call sites resolve; the (code, message)
    // forms route through the catalog's GetOrAdd so an inline "ES2173" string becomes a real
    // (if transitional) descriptor. Migrate to descriptor-routed Report/Warn as the catalog is
    // populated against the spec's ES#### set.

    public void Error(SourceSpan span, string message) => Report(span, message);
    public void Warning(SourceSpan span, string message) => Warn(span, message);

    public void Error(SourceSpan span, string code, string message) =>
        _diagnostics.Add(new Diagnostic(span, DiagnosticCatalog.GetOrAdd(code, message, DiagnosticSeverity.Error)));

    public void Warning(SourceSpan span, string code, string message) =>
        _diagnostics.Add(new Diagnostic(span, DiagnosticCatalog.GetOrAdd(code, message, DiagnosticSeverity.Warning)));

    // ------------------------------------------------------------------ positional (transitional back-compat)

    public void Report(string filePath, int line, int column, string message) =>
        _diagnostics.Add(new Diagnostic(new SourceSpan(filePath, line, column), DiagnosticCatalog.AdHocError(message)));

    public void Warn(string filePath, int line, int column, string message) =>
        _diagnostics.Add(new Diagnostic(new SourceSpan(filePath, line, column), DiagnosticCatalog.AdHocWarning(message)));

    // ------------------------------------------------------------------ bulk

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _diagnostics.AddRange(diagnostics);

    // ------------------------------------------------------------------ external-source overloads

    /// Report a diagnostic that originated from Roslyn or the Workspace layer.
    public void ReportExternal(SourceSpan span, DiagnosticDescriptor descriptor, DiagnosticSource source, params object[] args) =>
        _diagnostics.Add(new Diagnostic(span, descriptor, source, args));
}
