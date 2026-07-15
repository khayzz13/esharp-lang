using Esharp.Diagnostics;

namespace Esharp.Compilation;

// Outcome of Compilation.EmitTo / EmitToFile. Success is false when any
// diagnostic carries Severity == Error or emission itself threw.
public sealed record EmitResult(bool Success, IReadOnlyList<Diagnostic> Diagnostics);
