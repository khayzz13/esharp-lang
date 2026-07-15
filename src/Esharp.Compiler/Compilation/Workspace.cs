namespace Esharp.Compilation;

// The editor's mutable view of a compilation. Holds the document set, exposes
// the immutable Compilation snapshot at the current point in time, and fires
// Changed on every Add/Update/Remove so subscribers (LSP, build daemons) can
// react without polling.
//
// [Δ] Keyed accessors: Documents is now a Dictionary-view wrapper (no per-call
//     ToList). TryGetDocument and DocumentById give O(1) lookups. The old
//     _documents.Values.ToList() materialisation on every property access is
//     gone — the Compilation snapshot receives the values view directly at
//     Rebuild() time, snapshotted ONCE inside Compilation.Snapshot.
public sealed class Workspace : IDisposable
{
    readonly string _assemblyName;
    readonly OutputKind _outputKind;
    readonly List<MetadataReference> _references;
    readonly ProjectOptions _options;
    readonly Dictionary<DocumentId, Document> _documents = new();
    readonly Dictionary<string, DocumentId> _uriIndex = new(StringComparer.Ordinal);
    Compilation _currentCompilation;

    public Workspace(string assemblyName, IEnumerable<MetadataReference>? references = null,
        OutputKind outputKind = OutputKind.Library, ProjectOptions? options = null)
    {
        _assemblyName = assemblyName;
        _outputKind = outputKind;
        _references = (references ?? []).ToList();
        _options = options ?? new ProjectOptions();
        _currentCompilation = Compilation.Snapshot(_assemblyName, [], _references, _outputKind, _options);
    }

    /// The trusted-platform-assembly reference set — the BCL every real compile
    /// binds against. An IDE Workspace seeded with these resolves a document's
    /// external types exactly as the build does.
    public static IReadOnlyList<MetadataReference> PlatformReferences()
    {
        var tpa = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")) ?? "";
        var refs = new List<MetadataReference>();
        foreach (var path in tpa.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (System.IO.File.Exists(path))
                refs.Add(new MetadataReference(path));
        return refs;
    }

    public ProjectOptions Options => _options;
    public OutputKind OutputKind => _outputKind;
    public string AssemblyName => _assemblyName;
    public IReadOnlyList<MetadataReference> References => _references;

    // [Δ] No per-call ToList — expose a stable view over the dictionary's values.
    // Callers that need a snapshot materialise it themselves; the Workspace never
    // pays the allocation unless something asks for a list explicitly.
    public IReadOnlyCollection<Document> Documents => _documents.Values;

    public Compilation CurrentCompilation => _currentCompilation;

    // [Δ] Keyed accessors — O(1) instead of LINQ search over _documents.Values.
    public Document? TryGetDocument(string uri)
        => _uriIndex.TryGetValue(uri, out var id) && _documents.TryGetValue(id, out var doc)
            ? doc : null;

    public Document? TryGetDocument(DocumentId id)
        => _documents.TryGetValue(id, out var doc) ? doc : null;

    public event EventHandler<WorkspaceChangedEventArgs>? Changed;

    public Document AddDocument(string uri, string text)
    {
        if (_uriIndex.ContainsKey(uri))
            throw new InvalidOperationException($"Document already in workspace: {uri}");

        var doc = Document.Create(uri, text);
        _documents[doc.Id] = doc;
        _uriIndex[uri] = doc.Id;
        Rebuild();
        Changed?.Invoke(this, new WorkspaceChangedEventArgs(WorkspaceChangeKind.DocumentAdded, doc.Id, _currentCompilation));
        return doc;
    }

    public Document UpdateDocument(string uri, string newText)
    {
        if (!_uriIndex.TryGetValue(uri, out var id))
            throw new InvalidOperationException($"Document not in workspace: {uri}");

        var prev = _documents[id];
        // No-op short-circuit: same content → return existing snapshot, no Changed event.
        var newSourceText = new SourceText(newText);
        if (newSourceText.ContentHash == prev.Text.ContentHash && newSourceText.Content == prev.Text.Content)
            return prev;

        var updated = prev with { Text = newSourceText };
        _documents[id] = updated;
        Rebuild();
        Changed?.Invoke(this, new WorkspaceChangedEventArgs(WorkspaceChangeKind.DocumentChanged, id, _currentCompilation));
        return updated;
    }

    public bool RemoveDocument(string uri)
    {
        if (!_uriIndex.TryGetValue(uri, out var id))
            return false;
        _documents.Remove(id);
        _uriIndex.Remove(uri);
        Rebuild();
        Changed?.Invoke(this, new WorkspaceChangedEventArgs(WorkspaceChangeKind.DocumentRemoved, id, _currentCompilation));
        return true;
    }

    void Rebuild()
    {
        // [Δ] Snapshot materialises the document list ONCE here; callers see the
        // immutable snapshot's list, not the live dictionary. The dictionary itself
        // is never enumerated outside Rebuild.
        //
        // NOTE on disposal: Compilation is IDisposable (holds MetadataReader PEReaders),
        // but we do NOT eagerly dispose the outgoing snapshot here. A caller may have
        // captured the previous `CurrentCompilation` reference and still be reading
        // its MetadataTypeResolver or diagnostics. Snapshots are disposed by the owner
        // (e.g. WorkspaceBackgroundCompiler or a solution manager) when it knows no
        // consumer holds a reference. The Compilation's MetadataReader also releases
        // file handles in its finalizer via the PEReader's finalizer chain.
        _currentCompilation = Compilation.Snapshot(
            _assemblyName,
            [.._documents.Values],   // single allocation, at rebuild time only
            _references,
            _outputKind,
            _options);
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Changed = null; // detach all subscribers before releasing
        try { _currentCompilation.Dispose(); } catch { /* best-effort */ }
    }
}

// Explicit choice for what the final assembly is.
public enum OutputKind { Library, Console }

// C#-half compilation settings. Defaults match the modern Microsoft.NET.Sdk
// template: implicit global usings on, nullable reference types on.
public sealed record ProjectOptions(bool EnableImplicitUsings = true, bool Nullable = true);
