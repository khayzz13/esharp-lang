using System.Xml;

namespace Esharp.Compilation;

/// Loads an <c>.esproj</c> file and resolves its full reference closure — the
/// set of <see cref="MetadataReference"/>s (NuGet packages, framework assemblies,
/// and project-to-project references) that a <see cref="Workspace"/> needs to
/// bind and emit a project.
///
/// This is the design-time build equivalent for E#: a lightweight reader that
/// parses the MSBuild XML directly without running MSBuild, resolves NuGet
/// package paths via the <c>project.assets.json</c> lock file, and follows
/// <c>&lt;ProjectReference&gt;</c> chains recursively.
///
/// <para>
/// The output of <see cref="LoadAsync"/> is a <see cref="ProjectModel"/> value
/// that carries the resolved reference list and the ordered set of source
/// documents. It is the sole input to <see cref="Workspace"/>'s constructor —
/// the workspace has no knowledge of MSBuild or NuGet, only of documents and
/// metadata references.
/// </para>
public sealed class ProjectLoader
{
    // ── Entry point ────────────────────────────────────────────────────────

    /// Load the project at <paramref name="esProjPath"/> and resolve its full
    /// reference closure. Returns a <see cref="ProjectModel"/> ready to feed into
    /// a <see cref="Workspace"/>.
    public static async Task<ProjectModel> LoadAsync(string esProjPath,
        CancellationToken cancellationToken = default)
    {
        var loader = new ProjectLoader(esProjPath);
        return await loader.BuildModelAsync(cancellationToken);
    }

    // ── Internal state ─────────────────────────────────────────────────────

    readonly string _esProjPath;
    readonly string _projectDir;
    // Visited project paths — prevents P→Q→P cycles in reference chains.
    readonly HashSet<string> _visitedProjects = new(StringComparer.OrdinalIgnoreCase);
    readonly List<MetadataReference> _references = new();
    readonly List<string> _sourcePaths = new();

    ProjectLoader(string esProjPath)
    {
        _esProjPath = Path.GetFullPath(esProjPath);
        _projectDir = Path.GetDirectoryName(_esProjPath)!;
    }

    async Task<ProjectModel> BuildModelAsync(CancellationToken ct)
    {
        await LoadProjectAsync(_esProjPath, ct);
        // Always add the trusted platform assemblies (BCL) last — they are the
        // implicit foundation every project builds on top of.
        AppendPlatformAssemblies();
        return new ProjectModel(
            ProjectId.FromPath(_esProjPath),
            Path.GetFileNameWithoutExtension(_esProjPath),
            _sourcePaths.AsReadOnly(),
            _references.AsReadOnly());
    }

    async Task LoadProjectAsync(string esProjPath, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(esProjPath);
        if (!_visitedProjects.Add(fullPath)) return; // already visited

        if (!File.Exists(fullPath)) return;

        var projectDir = Path.GetDirectoryName(fullPath)!;
        var props = ParseMsBuildProps(fullPath);
        var targetFramework = props.TryGetValue("TargetFramework", out var tf) ? tf : "net10.0";

        // 1. Collect E# source files from the project directory.
        var sourceGlob = props.TryGetValue("EsharpSource", out var glob) ? glob : "**/*.es";
        foreach (var src in ResolveGlob(projectDir, sourceGlob))
            _sourcePaths.Add(src);

        // 2. Resolve project-to-project references recursively.
        foreach (var projRef in ReadItems(fullPath, "ProjectReference"))
        {
            var refPath = Path.Combine(projectDir, projRef);
            await LoadProjectAsync(refPath, ct);
        }

        // 3. Resolve NuGet package references via project.assets.json lock file.
        var assetsPath = Path.Combine(projectDir, "obj", "project.assets.json");
        if (File.Exists(assetsPath))
        {
            foreach (var asmPath in ResolveNuGetAssets(assetsPath, targetFramework))
                AddReference(asmPath);
        }
        else
        {
            // No assets file — try to resolve package references via NuGet fallback
            // folders (global packages cache). This covers projects that have been
            // freshly restored but whose obj/ was cleaned.
            foreach (var pkgRef in ReadItemsWithVersion(fullPath, "PackageReference"))
            {
                foreach (var candidate in NuGetFallbackPaths(pkgRef.name, pkgRef.version, targetFramework))
                    AddReference(candidate);
            }
        }
    }

    // ── MSBuild XML reader (minimal — no MSBuild SDK) ─────────────────────

    static Dictionary<string, string> ParseMsBuildProps(string projPath)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var reader = XmlReader.Create(projPath, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreWhitespace = true,
            });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.Name is "TargetFramework" or "AssemblyName" or "EsharpSource")
                    props[reader.Name] = reader.ReadElementContentAsString().Trim();
            }
        }
        catch { /* malformed XML — return what we have */ }
        return props;
    }

    static IEnumerable<string> ReadItems(string projPath, string itemType)
    {
        var results = new List<string>();
        try
        {
            using var reader = XmlReader.Create(projPath, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreWhitespace = true,
            });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == itemType)
                {
                    var include = reader.GetAttribute("Include");
                    if (include is not null) results.Add(include);
                }
            }
        }
        catch { }
        return results;
    }

    static IEnumerable<(string name, string version)> ReadItemsWithVersion(string projPath, string itemType)
    {
        var results = new List<(string, string)>();
        try
        {
            using var reader = XmlReader.Create(projPath, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreWhitespace = true,
            });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == itemType)
                {
                    var include = reader.GetAttribute("Include");
                    var version = reader.GetAttribute("Version") ?? "*";
                    if (include is not null) results.Add((include, version));
                }
            }
        }
        catch { }
        return results;
    }

    // ── NuGet asset resolution ─────────────────────────────────────────────

    /// Parse the <c>project.assets.json</c> lock file and return the set of DLL
    /// paths for the given target framework. We only need the "compile" assets —
    /// the ref/impl DLL paths under the global NuGet packages folder.
    ///
    /// This is a lightweight JSON reader — we don't take a JSON dependency; the
    /// assets file is line-oriented enough to be read with a simple scan.
    static IEnumerable<string> ResolveNuGetAssets(string assetsPath, string targetFramework)
    {
        // Locate the NuGet global packages folder from the assets file itself.
        string? packagesFolder = null;
        var lines = File.ReadLines(assetsPath);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("\"packageFolders\"", StringComparison.Ordinal)) break;
            // "path": "C:\\Users\\.../.nuget/packages/"
            if (trimmed.StartsWith("\"path\":", StringComparison.Ordinal))
            {
                var colon = trimmed.IndexOf(':');
                if (colon > 0)
                {
                    var raw = trimmed[(colon + 1)..].Trim().Trim('"').Replace("\\\\", "\\").Replace("/", Path.DirectorySeparatorChar.ToString());
                    if (Directory.Exists(raw)) { packagesFolder = raw; break; }
                }
            }
        }

        // Walk the "targets" section for the requested framework, extracting
        // compile-time DLL paths.
        var inTargets = false;
        var inFramework = false;
        var depth = 0;
        foreach (var raw in File.ReadLines(assetsPath))
        {
            var line = raw.Trim();
            if (!inTargets)
            {
                if (line.StartsWith("\"targets\"", StringComparison.Ordinal)) inTargets = true;
                continue;
            }
            if (!inFramework)
            {
                if (line.Contains(targetFramework, StringComparison.OrdinalIgnoreCase))
                    inFramework = true;
                continue;
            }
            if (line == "{") { depth++; continue; }
            if (line is "}" or "},") { depth--; if (depth <= 0) yield break; continue; }

            // "lib/net6.0/Foo.dll": {} lines inside compile sections
            if (depth == 3 && line.EndsWith(".dll\":", StringComparison.OrdinalIgnoreCase) && packagesFolder is not null)
            {
                var dllRel = line.Trim('"').TrimEnd(':').Trim();
                // The full path = packagesFolder / PackageName.version / dllRel
                // We get the package name from the parent key; approximate by
                // scanning the packages folder for any version that has this DLL.
                foreach (var candidate in Directory.EnumerateFiles(packagesFolder, Path.GetFileName(dllRel), SearchOption.AllDirectories))
                    if (File.Exists(candidate)) { yield return candidate; break; }
            }
        }
    }

    static IEnumerable<string> NuGetFallbackPaths(string name, string version, string targetFramework)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var packagesRoot = Path.Combine(home, ".nuget", "packages", name.ToLowerInvariant(), version);
        if (!Directory.Exists(packagesRoot)) yield break;

        // Prefer ref/ then lib/ for the target framework.
        foreach (var sub in new[] { "ref", "lib" })
        {
            var tfDir = Path.Combine(packagesRoot, sub, targetFramework);
            if (!Directory.Exists(tfDir)) continue;
            foreach (var dll in Directory.EnumerateFiles(tfDir, "*.dll"))
                yield return dll;
        }
    }

    // ── Glob resolver ─────────────────────────────────────────────────────

    static IEnumerable<string> ResolveGlob(string root, string pattern)
    {
        // Support "**/*.es" and simple "*.es" patterns.
        if (pattern.StartsWith("**/", StringComparison.Ordinal))
        {
            var filePattern = pattern[3..];
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, filePattern, SearchOption.AllDirectories)
                : [];
        }
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)
            : [];
    }

    // ── Platform assemblies ────────────────────────────────────────────────

    void AppendPlatformAssemblies()
    {
        var tpa = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")) ?? "";
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            AddReference(path);
    }

    // ── Reference deduplication ───────────────────────────────────────────

    readonly HashSet<string> _seenRefs = new(StringComparer.OrdinalIgnoreCase);

    void AddReference(string path)
    {
        if (!File.Exists(path)) return;
        var full = Path.GetFullPath(path);
        if (_seenRefs.Add(full))
            _references.Add(new MetadataReference(full));
    }
}

/// The resolved output of <see cref="ProjectLoader.LoadAsync"/>: the source file
/// list and the full reference closure for one <c>.esproj</c>.
public sealed record ProjectModel(
    ProjectId ProjectId,
    string AssemblyName,
    IReadOnlyList<string> SourcePaths,
    IReadOnlyList<MetadataReference> References)
{
    /// Build a <see cref="Workspace"/> from this model. All source files are added
    /// as documents; all references are passed to the workspace constructor.
    public async Task<Workspace> CreateWorkspaceAsync()
    {
        var ws = new Workspace(AssemblyName, References);
        foreach (var path in SourcePaths)
        {
            var text = await File.ReadAllTextAsync(path);
            ws.AddDocument(path, text);
        }
        return ws;
    }
}
