using System.Reflection;

namespace Esharp.Compiler;

/// <summary>
/// Process-wide, mutable singleton holding the shared external-type RESOLUTION SCOPE
/// that both compiler phases consult — the binder's <c>TypeResolver</c> and the IL
/// emitter's <c>ILTypeResolver</c>. It is the single source of truth for how an
/// unqualified BCL / cross-assembly name is auto-resolved, so the two phases can never
/// disagree about what's in scope.
///
/// These are NOT "implicit usings" in the C# sense (the SDK auto-injecting
/// <c>global using System;</c> &amp; friends so source can omit them). This is a
/// resolver convenience: a curated set of namespaces the type/member lookup SEARCHES
/// (and force-loads the backing assembly for) when a name arrives unqualified.
///
/// It carries:
/// <list type="bullet">
///   <item><see cref="CommonNamespaces"/> — the standard namespaces auto-searched (and,
///   when absent from the AppDomain, force-loaded) for an unqualified name. This used to
///   live as a private <c>static readonly string[]</c> in EACH resolver, and the two
///   arrays drifted apart: the emitter listed <c>System.Threading.Channels</c>,
///   <c>System.Net</c>, <c>System.Reflection</c>; the binder did not — so the same
///   source resolved differently across binding vs. emission. Unified here.</item>
///   <item><see cref="ExternalNamespaces"/> — additional always-on namespaces a host
///   registers at runtime (searched + force-loaded AHEAD of the common set). Empty by
///   default; the mutable extension point for embedding scenarios.</item>
///   <item><see cref="SearchCommonNamespaces"/> — the "disable common" gate. When false
///   (a project's <c>&lt;ImplicitUsings&gt;disable&lt;/&gt;</c>), the common-namespace
///   search is skipped: an unqualified name resolves only from an exact / qualified
///   match, an explicit <c>using</c>, or a registered <see cref="ExternalNamespaces"/>
///   entry. It is a build-wide setting, not per-unit, so the singleton is its home.</item>
/// </list>
///
/// What it deliberately does NOT carry: a compilation unit's own <c>using "Ns"</c>
/// imports (and the <c>using static</c> / alias forms). Those are scoped to each resolver
/// instance, driven by the unit's bound <c>using</c> list, so parallel compilations in
/// one process never cross-pollinate each other's imports. This singleton holds only the
/// genuinely global, read-mostly configuration.
/// </summary>
public sealed class UsingEnvironment
{
    /// The shared instance. Mutable: a host may toggle <see cref="SearchCommonNamespaces"/>
    /// or extend <see cref="CommonNamespaces"/> / <see cref="ExternalNamespaces"/>.
    public static UsingEnvironment Instance { get; } = new();

    UsingEnvironment() { }

    /// Whether the curated common-namespace search is active. Default true; set false to
    /// disable it (the build-wide "no implicit BCL search" mode).
    public bool SearchCommonNamespaces { get; set; } = true;

    /// The standard namespaces auto-searched for an unqualified name when
    /// <see cref="SearchCommonNamespaces"/> is on. Order is precedence: earlier entries
    /// win a simple-name collision.
    public List<string> CommonNamespaces { get; } =
    [
        "System",
        "System.Collections.Generic",
        "System.Collections.Concurrent",
        "System.Collections.ObjectModel",
        "System.Collections.Specialized",
        "System.Collections",
        "System.Text",
        "System.Text.Json",
        "System.Text.Json.Serialization",
        "System.Text.RegularExpressions",
        "System.Threading",
        "System.Threading.Tasks",
        "System.Threading.Channels",
        "System.IO",
        "System.Linq",
        "System.Diagnostics",
        "System.Timers",
        "System.Reflection",
        "System.Globalization",
        "System.Net",
        "System.Net.Http",
        "System.ComponentModel",
        // The E# standard library owns the language concurrency and collection surface.
        "Esharp.Stdlib",
    ];

    /// Host-registered always-on namespaces, searched and force-loaded AHEAD of
    /// <see cref="CommonNamespaces"/>. Empty by default. Unaffected by
    /// <see cref="SearchCommonNamespaces"/> — an explicit registration always applies.
    public List<string> ExternalNamespaces { get; } = [];

    /// The namespaces to auto-search / force-load for an unqualified name, in precedence
    /// order: host-registered external namespaces first, then (only when
    /// <see cref="SearchCommonNamespaces"/> is on) the common set.
    public IEnumerable<string> AutoSearchNamespaces =>
        SearchCommonNamespaces ? ExternalNamespaces.Concat(CommonNamespaces) : ExternalNamespaces;

    /// <summary>
    /// Force-load the assembly that backs namespace <paramref name="ns"/> (or a dotted
    /// prefix of it) and return the type <c>{ns}.{typeName}</c> from it, or <c>null</c>.
    /// The framework convention pairs a namespace with an assembly of the same name or a
    /// shorter prefix (System.Threading.Channels → System.Threading.Channels.dll), so a
    /// type whose backing assembly is not yet in the AppDomain still resolves — the
    /// loaded-assembly search alone never finds it because nothing has loaded it. Shared
    /// by both resolvers so the binder and emitter force-load identically.
    /// <paramref name="typeName"/> may be a simple name (<c>Channel</c>) or a mangled
    /// generic name (<c>Channel`1</c>).
    /// </summary>
    public static Type? ForceLoadType(string ns, string typeName)
    {
        var full = $"{ns}.{typeName}";
        var probe = ns;
        while (!string.IsNullOrEmpty(probe))
        {
            try
            {
                var asm = Assembly.Load(new AssemblyName(probe));
                if (asm.GetType(full) is { } t) return t;
            }
            catch { /* `probe` isn't an assembly name — try a shorter prefix */ }
            var dot = probe.LastIndexOf('.');
            probe = dot < 0 ? "" : probe[..dot];
        }
        return null;
    }
}
