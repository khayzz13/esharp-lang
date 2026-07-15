namespace Esharp.Compilation;

// Typed wrapper for a reference assembly path. Today the binder + IL emitter
// consume `IReadOnlyList<string>` reference paths; this is the API-side type
// callers see. Chunk 6 (Roslyn co-bind) extends this with in-memory PE refs.
public sealed record MetadataReference(string Path)
{
    public static MetadataReference FromFile(string path) => new(System.IO.Path.GetFullPath(path));
}
