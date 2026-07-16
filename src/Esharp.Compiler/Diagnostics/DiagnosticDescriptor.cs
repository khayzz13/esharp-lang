namespace Esharp.Diagnostics;

// ============================================================================
// DiagnosticDescriptor — the catalog entry for one kind of diagnostic.
//
// A descriptor is the stable, versioned identity of a diagnostic rule. Every
// Diagnostic carries a reference to its descriptor so:
//   - The LSP maps Id → fixit class (WS6 of the LSP plan).
//   - Analyzers suppress by Id, not by fragile string matching.
//   - The catalog gives structured hover text (Title + MessageFormat with
//     numbered substitution markers: {0}, {1}, …).
//
// Descriptors are created once (typically as static readonly fields on a
// DiagnosticDescriptors static class per compiler area, then registered into
// DiagnosticCatalog). They are immutable records — sharing is safe.
//
// The `with` expression (record non-destructive mutation) is used intentionally
// in DiagnosticBag.Warn / .Info / .Hidden to override DefaultSeverity without
// creating a new catalog entry. This is the correct design: the catalog entry
// carries the *default* severity; the bag can downgrade or upgrade it per-site.
// ============================================================================

/// <summary>
/// Immutable descriptor for a single diagnostic rule.
/// </summary>
/// <param name="Id">
///   The stable identifier, e.g. <c>"ES2160"</c>. Follows the ES#### scheme.
///   The prefix encodes the area: ES0### (core/binder), ES1### (type system),
///   ES2### (semantic/flow), ES3### (codegen), ES9### (workspace/infra).
/// </param>
/// <param name="Title">
///   A short human-readable name for the rule, e.g. <c>"Undefined variable"</c>.
///   Used in IDEs as the rule name column and in diagnostic catalog browsers.
/// </param>
/// <param name="MessageFormat">
///   A <see cref="string.Format"/> template with numbered placeholders, e.g.
///   <c>"Variable '{0}' is not defined in the current scope."</c>.
///   When no substitutions are needed, a plain string with no <c>{n}</c> works.
/// </param>
/// <param name="DefaultSeverity">
///   The severity at which the compiler emits this diagnostic by default.
///   Individual call sites may override via <c>DiagnosticBag.Warn</c> / <c>.Info</c>.
/// </param>
/// <param name="Category">
///   Grouping for IDE filter panels, e.g. <c>"Binding"</c>, <c>"FlowAnalysis"</c>,
///   <c>"Codegen"</c>, <c>"Style"</c>. Free-form string; the LSP plan uses this
///   to bucket rules in the Problems panel.
/// </param>
/// <param name="HelpLink">
///   Optional URL to a docs page, e.g.
///   <c>"https://esharp-lang.vercel.app/diagnostics/ES2160"</c>. Surfaced as a
///   clickable link in the LSP hover for the diagnostic.
/// </param>
/// <param name="Fixit">
///   Optional fixit class name. The LSP WS6 code-action seam maps
///   <c>Id → Fixit</c>: when the LSP server receives a textDocument/codeAction
///   request at a diagnostic span, it looks up this name to instantiate the
///   corresponding code-action provider. Null means no automated fix.
/// </param>
public sealed record DiagnosticDescriptor(
    string Id,
    string Title,
    string MessageFormat,
    DiagnosticSeverity DefaultSeverity,
    string Category,
    string? HelpLink = null,
    string? Fixit = null)
{
    /// Format the message with the given substitution arguments.
    /// Equivalent to <c>string.Format(MessageFormat, args)</c>, but
    /// protected against format exceptions (returns the raw format on failure).
    public string Format(params object[] args)
    {
        if (args.Length == 0) return MessageFormat;
        try { return string.Format(MessageFormat, args); }
        catch { return MessageFormat; }
    }

    /// True when this descriptor triggers an error-level diagnostic by default.
    public bool IsError => DefaultSeverity == DiagnosticSeverity.Error;
}
