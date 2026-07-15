using Esharp.Syntax;

namespace Esharp.Diagnostics;

// ============================================================================
// Diagnostic [Δ] — carries a SourceSpan (was a point: FilePath/Line/Column).
//
// The full span is already in hand at every Report(SourceSpan, …) call site;
// before this change the .End was silently discarded. Now it survives so LSP
// range-diagnostics and squiggles have start + end.
//
// Back-compat: Line / Column remain first-class properties, returning the
// span's start-line / start-column as before — all existing callers continue
// to work without change.
//
// The code field is replaced by a DiagnosticDescriptor reference. The old
// free-string code ("ES2160", "ES0001") lives on the descriptor as Descriptor.Id
// so `ToString()` continues to print the same format. Convenience constructors
// that used to accept a bare string code are preserved for callers in the
// transitional period; they look up (or create on demand) a descriptor.
// ============================================================================
public sealed record Diagnostic
{
    /// Absolute source range this diagnostic annotates. A half-open range [Start, End).
    /// For point diagnostics (synthesized sites, external errors) End == Start.
    public SourceSpan Span { get; init; }

    /// Where the diagnostic came from.
    public DiagnosticSource Source { get; init; }

    /// The descriptor (id, title, severity, category, help link, fixit class).
    public DiagnosticDescriptor Descriptor { get; init; }

    /// Format arguments substituted into Descriptor.MessageFormat to produce the
    /// rendered message. May be empty when the message needs no arguments.
    public object[] Args { get; init; }

    // ------------------------------------------------------------------ back-compat accessors
    /// The file path. Equals Span.File.
    public string FilePath => Span.File;

    /// 1-based start line. Equals Span.Line.
    public int Line => Span.Line;

    /// 1-based start column. Equals Span.Column.
    public int Column => Span.Column;

    /// Resolved severity from the descriptor (can be overridden per-bag in the future).
    public DiagnosticSeverity Severity => Descriptor.DefaultSeverity;

    /// The diagnostic code string (e.g. "ES2160"). Equals Descriptor.Id.
    public string Code => Descriptor.Id;

    /// The fully rendered message (format string + substituted args).
    public string Message => Args.Length == 0
        ? Descriptor.MessageFormat
        : string.Format(Descriptor.MessageFormat, Args);

    // ------------------------------------------------------------------ primary constructor
    public Diagnostic(SourceSpan span, DiagnosticDescriptor descriptor, DiagnosticSource source = DiagnosticSource.Esharp, params object[] args)
    {
        Span = span;
        Descriptor = descriptor;
        Source = source;
        Args = args;
    }

    // ------------------------------------------------------------------ convenience constructors (transitional)
    /// Looks up the descriptor by id in the catalog; creates an ad-hoc Error
    /// descriptor on cache-miss so old call sites continue to compile.
    public Diagnostic(string filePath, int line, int column, DiagnosticSeverity severity, string code, string message, DiagnosticSource source = DiagnosticSource.Esharp)
        : this(new SourceSpan(filePath, line, column),
               DiagnosticCatalog.GetOrAdd(code, message, severity),
               source) { }

    public Diagnostic(string filePath, int line, int column, DiagnosticSeverity severity, string message)
        : this(filePath, line, column, severity, "ES0001", message) { }

    public Diagnostic(string filePath, int line, int column, string message)
        : this(filePath, line, column, DiagnosticSeverity.Error, "ES0001", message) { }

    // ------------------------------------------------------------------ formatting
    public override string ToString()
    {
        var prefix = Source switch
        {
            DiagnosticSource.CSharp => "csc",
            DiagnosticSource.Workspace => "ws",
            _ => "es",
        };
        var level = Severity switch
        {
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Info    => "info",
            DiagnosticSeverity.Hidden  => "hidden",
            _                          => "error",
        };
        return $"{FilePath}({Line},{Column}): [{prefix}] {level} {Code}: {Message}";
    }
}
