using System.Reflection;

namespace Esharp.Compiler;

/// <summary>
/// Locates and loads the E#-authored standard library (<c>Esharp.Stdlib.dll</c>) so
/// that BOTH compiler phases see the same assembly: the binder resolves stdlib types
/// reflectively (e.g. the <c>Result.Ok</c>/<c>Result.Error</c> static factory class
/// <c>Esharp.Stdlib.Result</c>) while the IL emitter resolves them by metadata name
/// (<c>Esharp.Stdlib.Result`2</c>) through Mono.Cecil.
/// </summary>
/// <remarks>
/// The compiler binds language builtins (<c>Result</c>, …) by metadata name from this
/// disk-loaded assembly. The emitter (<c>ILTypeResolver.ResultOpenType</c>) used to be the
/// only thing that loaded the stdlib — and it did so lazily, during EMISSION. That left
/// a window where the BINDER ran first against an AppDomain in which <c>Esharp.Stdlib</c>
/// was not yet loaded, so reflective lookups of the static factory class silently
/// returned <c>null</c> and the call typed as <c>var</c> (dropping the trailing
/// <c>.Value</c>/<c>.Error</c> field access at emit). The failure was order-dependent —
/// it vanished once any earlier compilation's emission had probe-loaded the assembly.
/// This type makes the load deterministic: the binder calls <see cref="EnsureLoaded"/>
/// before resolving names, so the stdlib is in the AppDomain for the whole compilation.
/// </remarks>
public static class StdlibProbe
{
    static readonly object _lock = new();
    static bool _loaded;
    static Assembly? _stdlib;

    /// <summary>
    /// Candidate on-disk locations for <c>Esharp.Stdlib.dll</c>, most-specific first:
    /// the explicit <c>ESHARP_STDLIB</c> override, then the process base directory, then
    /// the directory holding the compiler assembly (the stdlib is copied alongside it).
    /// </summary>
    public static IEnumerable<string> CandidatePaths()
    {
        var explicitPath = Environment.GetEnvironmentVariable("ESHARP_STDLIB");
        if (!string.IsNullOrEmpty(explicitPath)) yield return explicitPath;

        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir)) yield return Path.Combine(baseDir, "Esharp.Stdlib.dll");

        var asmDir = Path.GetDirectoryName(typeof(StdlibProbe).Assembly.Location);
        if (!string.IsNullOrEmpty(asmDir)) yield return Path.Combine(asmDir, "Esharp.Stdlib.dll");
    }

    /// <summary>
    /// Loads <c>Esharp.Stdlib.dll</c> into the AppDomain exactly once (idempotent and
    /// thread-safe) so reflective name resolution can see <c>Esharp.Stdlib.*</c> types.
    /// Returns <c>null</c> only when the compiler was launched without its required stdlib
    /// sidecar; callers report the missing standard-library surface directly.
    /// </summary>
    public static Assembly? EnsureLoaded()
    {
        if (_loaded) return _stdlib;
        lock (_lock)
        {
            if (_loaded) return _stdlib;
            foreach (var path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    _stdlib = Assembly.LoadFrom(path);
                    break;
                }
                catch { /* unreadable / wrong-arch candidate — keep probing */ }
            }
            _loaded = true;
            return _stdlib;
        }
    }

    /// <summary>
    /// Resolves a type from the loaded stdlib by metadata name
    /// (e.g. <c>"Esharp.Stdlib.Result`2"</c>), or <c>null</c> if the stdlib is absent or
    /// has no such type.
    /// </summary>
    public static Type? ProbeType(string metadataName)
        => EnsureLoaded()?.GetType(metadataName, throwOnError: false);
}
