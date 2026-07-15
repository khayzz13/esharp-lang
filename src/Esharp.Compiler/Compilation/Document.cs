namespace Esharp.Compilation;

// A single source file in a compilation. DocumentId is the stable handle the
// Workspace assigns on first add and preserves across snapshots; Uri is the
// user-facing path (renameable in the future); SourceText carries the content
// plus a precomputed content hash for cheap "did this change" checks.
//
// Chunk 3 attaches a parsed SyntaxTree handle. Chunk 6 adds a Language field.
public sealed record Document(DocumentId Id, string Uri, SourceText Text)
{
    // Convenience for Chunk-1 call sites that didn't carry an id around.
    public static Document Create(string uri, string text) =>
        new(DocumentId.New(), uri, new SourceText(text));
}

// Stable cross-snapshot identity for a document. Two Document instances from
// different snapshots refer to the same file iff their DocumentIds are equal —
// Uri alone isn't sufficient because the user could rename a file in the editor.
public readonly record struct DocumentId(Guid Value)
{
    public static DocumentId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}
