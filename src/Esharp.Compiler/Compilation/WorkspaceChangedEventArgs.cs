namespace Esharp.Compilation;

public enum WorkspaceChangeKind
{
    DocumentAdded,
    DocumentChanged,
    DocumentRemoved,
}

// Payload for Workspace.Changed. Carries the kind, the affected document id,
// and the new compilation snapshot so subscribers (LSP, build daemon) can
// publish diagnostics or trigger work without re-querying.
public sealed class WorkspaceChangedEventArgs : EventArgs
{
    public WorkspaceChangeKind Kind { get; }
    public DocumentId DocumentId { get; }
    public Compilation NewCompilation { get; }

    public WorkspaceChangedEventArgs(WorkspaceChangeKind kind, DocumentId documentId, Compilation newCompilation)
    {
        Kind = kind;
        DocumentId = documentId;
        NewCompilation = newCompilation;
    }
}
