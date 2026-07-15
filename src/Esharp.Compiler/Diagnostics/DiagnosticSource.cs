namespace Esharp.Diagnostics;

/// Which side of a mixed compilation produced a diagnostic.
///
/// <c>Esharp</c>   — binder, IL emitter, and runtime validation errors.
/// <c>CSharp</c>   — Roslyn (parse, binder, emit) surfaced via the Workspace adapter.
/// <c>Workspace</c> — the orchestration layer's own errors: fusion failures, internal throws.
public enum DiagnosticSource
{
    Esharp,
    CSharp,
    Workspace,
}
