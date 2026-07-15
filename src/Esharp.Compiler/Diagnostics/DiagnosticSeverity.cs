namespace Esharp.Diagnostics;

// [Δ] Info and Hidden added for analyzer-level reporting.
// Error / Warning are the compiler-proper levels; Info is a suggestion (e.g.
// "redundant cast"); Hidden is an annotation visible only to tooling (e.g.
// "this token is unused" for greying out in the IDE).
public enum DiagnosticSeverity
{
    /// Fatal: the compilation cannot succeed. The binder and codegen report these.
    Error,
    /// Non-fatal: valid but suspicious. The binder reports these; the user chooses to fix.
    Warning,
    /// [Δ] Informational suggestion — correct code, but a style note or improvement opportunity.
    Info,
    /// [Δ] Tool-only annotation — drives IDE graying / fade, not shown in CLI output.
    Hidden,
}
