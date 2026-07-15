namespace Esharp.LanguageServer

using "Esharp.Compilation"
using "Esharp.Diagnostics.Semantics"
using "Esharp.Diagnostics"
using "Esharp.Syntax"

// The server's view of the project: one compiler Workspace keyed by canonical
// LSP uris. The compiler treats a document uri as an opaque file name, so every
// SourceSpan.File and Diagnostic.FilePath it reports IS the uri the client sent —
// responses round-trip with zero path mapping.
pub class DocumentStore {
    ws: Workspace
    seeded: List<string>      // uris loaded from disk — project members
    opened: List<string>      // uris currently open in the editor
    published: List<string>   // uris the last diagnostics round pushed to
    projectDirs: List<string> // esproj directories currently in scope

    init() {
        // The platform reference set (the BCL) so a document's external types bind the
        // way a real compile's do — without it the editor reports phantom diagnostics.
        self.ws = Workspace("lsp", Workspace.PlatformReferences())
        self.seeded = List<string>()
        self.opened = List<string>()
        self.published = List<string>()
        self.projectDirs = List<string>()
    }

    // Canonical spelling of a client uri — normalized through System.Uri so a
    // seeded document and the editor's didOpen of the same file key identically.
    pub func canon(uri: string) -> string {
        try {
            return Uri(uri).AbsoluteUri
        } catch {
            return uri
        }
    }

    // The nearest ancestor directory of `filePath` that holds a *.esproj — the file's
    // owning project — or "" when the file belongs to no project (a loose file).
    func owningProjectDir(filePath: string) -> string {
        try {
            var dir = Path.GetDirectoryName(filePath) ?? ""
            while dir.Length > 0 {
                if Directory.GetFiles(dir, "*.esproj").Length > 0 { return dir }
                let parent = Path.GetDirectoryName(dir) ?? ""
                if parent == dir { break }
                dir = parent
            }
        } catch {
        }
        return ""
    }

    // Seed every .es under one project directory (skipping build output) so cross-file
    // definition / references resolve WITHIN that project — and never beyond it.
    func seedProjectDir(dir: string) {
        if dir.Length == 0 || self.projectDirs.Contains(dir) { return }
        self.projectDirs.Add(dir)
        if !Directory.Exists(dir) { return }
        for f in Directory.GetFiles(dir, "*.es", SearchOption.AllDirectories) {
            if f.Contains("/bin/") || f.Contains("/obj/") { continue }
            try {
                let uri = self.canon(Uri(f).AbsoluteUri)
                if !self.hasDoc(uri) {
                    self.ws.AddDocument(uri, File.ReadAllText(f))
                    self.seeded.Add(uri)
                }
            } catch {
                // An unreadable file never takes the whole workspace down.
            }
        }
    }

    // initialize: if the workspace root holds exactly one project, seed it. With zero
    // or several, defer — each opened file seeds its OWN project on open, so unrelated
    // projects are never fused into one cross-file scope.
    pub func seed(rootUri: string) {
        var root = ""
        try {
            root = Uri(rootUri).LocalPath
        } catch {
            return
        }
        if !Directory.Exists(root) { return }
        let real = List<string>()
        for p in Directory.GetFiles(root, "*.esproj", SearchOption.AllDirectories) {
            if p.Contains("/bin/") || p.Contains("/obj/") { continue }
            real.Add(p)
        }
        if real.Count == 1 {
            self.seedProjectDir(Path.GetDirectoryName(real[0]) ?? root)
        }
    }

    // Explicit project selection — the "E#: Select Project" command points the server
    // at one esproj, seeding its tree as the active cross-file scope.
    pub func setProject(esprojUri: string) {
        try {
            let dir = Path.GetDirectoryName(Uri(esprojUri).LocalPath) ?? ""
            self.seedProjectDir(dir)
        } catch {
        }
    }

    pub func openDoc(uri: string, content: string) {
        // Pull in the file's owning project so its siblings resolve — bounded to that
        // one esproj tree, so navigation stays inside the project the file belongs to.
        try {
            self.seedProjectDir(self.owningProjectDir(Uri(uri).LocalPath))
        } catch {
        }
        if self.hasDoc(uri) {
            self.ws.UpdateDocument(uri, content)
        } else {
            self.ws.AddDocument(uri, content)
        }
        if !self.opened.Contains(uri) { self.opened.Add(uri) }
    }

    pub func changeDoc(uri: string, content: string) {
        if self.hasDoc(uri) {
            self.ws.UpdateDocument(uri, content)
        } else {
            self.ws.AddDocument(uri, content)
        }
    }

    // Close keeps a seeded document (it is still part of the project) and drops
    // an ad-hoc one the editor opened outside the root.
    pub func closeDoc(uri: string) {
        self.opened.Remove(uri)
        if !self.seeded.Contains(uri) && self.hasDoc(uri) {
            self.ws.RemoveDocument(uri)
        }
    }

    pub func hasDoc(uri: string) -> bool {
        for d in self.ws.Documents {
            if d.Uri == uri { return true }
        }
        return false
    }

    pub func textOf(uri: string) -> SourceText? {
        for d in self.ws.Documents {
            if d.Uri == uri { return d.Text }
        }
        return nil
    }

    pub func model() -> SemanticModel = self.ws.CurrentCompilation.GetSemanticModel()

    // The retained parse tree for a document — the hover fallback walks it to
    // answer literals (no symbol occurrence covers a `"…"` or a `42`).
    pub func treeOf(uri: string) -> CompilationUnitSyntax? = self.ws.CurrentCompilation.GetSyntaxTree(uri)

    pub func diags() -> IReadOnlyList<Diagnostic> = self.ws.CurrentCompilation.GetDiagnostics()

    // One complete publishDiagnostics notification per open document — including
    // an empty list when a document came clean (clears stale squiggles), plus a
    // final clear for anything published last round that is no longer open.
    pub func publishPayloads() -> List<string> {
        let payloads = List<string>()
        let next = List<string>()
        let all = self.diags()
        for uri in self.opened {
            payloads.Add(self.publishFor(uri, all))
            next.Add(uri)
        }
        let empty = List<Diagnostic>()
        for uri in self.published {
            if !self.opened.Contains(uri) {
                payloads.Add(self.publishFor(uri, empty))
            }
        }
        self.published = next
        return payloads
    }

    func publishFor(uri: string, all: IReadOnlyList<Diagnostic>) -> string {
        let w = JsonWriter()
        w.obj()
        w.prop("jsonrpc")
        w.str("2.0")
        w.prop("method")
        w.str("textDocument/publishDiagnostics")
        w.prop("params")
        w.obj()
        w.prop("uri")
        w.str(uri)
        w.prop("diagnostics")
        w.arr()
        let text = self.textOf(uri)
        if text != nil {
            for d in all {
                if d.FilePath == uri {
                    w.writeDiagnostic(text, d)
                }
            }
        }
        w.endArr()
        w.endObj()
        w.endObj()
        return w.render()
    }
}
