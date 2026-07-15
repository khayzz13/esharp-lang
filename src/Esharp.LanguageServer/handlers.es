namespace Esharp.LanguageServer

using "Esharp.Compilation"
using "Esharp.Diagnostics.Semantics"
using "Esharp.Symbols"
using "Esharp.Syntax"

// The feature surface — one handler per LSP method, promoted onto DocumentStore.
// Each takes the request's `params` JsonElement and returns the result's JSON
// ("null" for an empty result); the server wraps it into the response envelope.

// --- request plumbing -------------------------------------------------------

func uriOf(prms: JsonElement) -> string =
    prms.GetProperty("textDocument").GetProperty("uri").GetString() ?? ""

// uri + LSP position → the char offset into `text`, or -1 when out of range.
func offsetIn(text: SourceText, prms: JsonElement) -> int {
    let pos = prms.GetProperty("position")
    let line = pos.GetProperty("line").GetInt32()
    let character = pos.GetProperty("character").GetInt32()
    try {
        return text.GetOffset(line + 1, character + 1)
    } catch {
        return -1
    }
}

// The shared prelude: the symbol under the request's cursor, or nil.
func (store: DocumentStore) symbolAt(prms: JsonElement) -> ISymbol? {
    let uri = store.canon(uriOf(prms))
    let text = store.textOf(uri)
    if text == nil { return nil }
    let offset = offsetIn(text, prms)
    if offset < 0 { return nil }
    return store.model().GetSymbolAt(uri, offset)
}

// --- lifecycle ---------------------------------------------------------------

func (store: DocumentStore) onInitialize(prms: JsonElement) -> string {
    if prms.TryGetProperty("rootUri", out var rootEl) {
        if rootEl.ValueKind == JsonValueKind.String {
            store.seed(rootEl.GetString() ?? "")
        }
    }
    let w = JsonWriter()
    w.obj()
    w.prop("capabilities")
    w.obj()
    w.prop("positionEncoding")
    w.str("utf-16")
    w.prop("textDocumentSync")
    w.num(1)
    w.prop("hoverProvider")
    w.flag(true)
    w.prop("definitionProvider")
    w.flag(true)
    w.prop("referencesProvider")
    w.flag(true)
    w.prop("documentSymbolProvider")
    w.flag(true)
    w.prop("renameProvider")
    w.flag(true)
    w.prop("documentHighlightProvider")
    w.flag(true)
    w.prop("completionProvider")
    w.obj()
    w.prop("triggerCharacters")
    w.arr()
    w.str(".")
    w.endArr()
    w.endObj()
    w.prop("semanticTokensProvider")
    w.obj()
    w.prop("legend")
    w.obj()
    w.writeSemanticTokensLegend()
    w.endObj()
    w.prop("full")
    w.flag(true)
    w.endObj()
    w.endObj()
    w.prop("serverInfo")
    w.obj()
    w.prop("name")
    w.str("esharp-lsp")
    w.endObj()
    w.endObj()
    return w.render()
}

// --- document sync (notifications) -------------------------------------------

func (store: DocumentStore) onDidOpen(prms: JsonElement) {
    let td = prms.GetProperty("textDocument")
    let uri = store.canon(td.GetProperty("uri").GetString() ?? "")
    let content = td.GetProperty("text").GetString() ?? ""
    store.openDoc(uri, content)
}

func (store: DocumentStore) onDidChange(prms: JsonElement) {
    let uri = store.canon(uriOf(prms))
    // Full sync: the last contentChanges entry carries the whole document.
    var content = ""
    var saw = false
    for change in prms.GetProperty("contentChanges").EnumerateArray() {
        if change.TryGetProperty("text", out var t) {
            content = t.GetString() ?? ""
            saw = true
        }
    }
    if saw { store.changeDoc(uri, content) }
}

func (store: DocumentStore) onDidClose(prms: JsonElement) {
    store.closeDoc(store.canon(uriOf(prms)))
}

// Custom notification `esharp/setProject` — the "E#: Select Project" command points
// the server at one esproj, seeding its tree as the active cross-file scope.
func (store: DocumentStore) onSetProject(prms: JsonElement) {
    if prms.TryGetProperty("uri", out var u) {
        if u.ValueKind == JsonValueKind.String {
            store.setProject(u.GetString() ?? "")
        }
    }
}

// --- features -----------------------------------------------------------------

// The E# spelling of a literal's type, from the decoded literal value the parser
// stored on the node. "" for nil and anything unrecognized.
func literalTypeName(v: object?) -> string {
    if v == nil { return "" }
    if v is string { return "string" }
    if v is int { return "int" }
    if v is long { return "long" }
    if v is double { return "double" }
    if v is float { return "float" }
    if v is bool { return "bool" }
    if v is char { return "char" }
    return ""
}

// The CLR identity behind an E# primitive spelling — the hover subtitle.
func clrNameOf(esName: string) -> string =
    match esName {
        "string" => "System.String"
        "int" => "System.Int32"
        "long" => "System.Int64"
        "double" => "System.Double"
        "float" => "System.Single"
        "bool" => "System.Boolean"
        "char" => "System.Char"
        default => ""
    }

// Hover fallback for positions with no symbol occurrence: a literal answers its
// type (`"…"` → string · System.String). Keywords and whitespace answer nothing.
func (store: DocumentStore) onHoverLiteral(prms: JsonElement) -> string {
    let uri = store.canon(uriOf(prms))
    let text = store.textOf(uri)
    if text == nil { return "null" }
    let offset = offsetIn(text, prms)
    if offset < 0 { return "null" }
    let tree = store.treeOf(uri)
    if tree == nil { return "null" }
    let node = SyntaxNavigator.FindNode(tree, offset)
    if node == nil { return "null" }
    let lit = node as LiteralExpressionSyntax
    if lit == nil { return "null" }
    let esName = literalTypeName(lit.Value)
    if esName.Length == 0 { return "null" }
    var value = "```esharp\n" + esName + "\n```"
    let clr = clrNameOf(esName)
    if clr.Length > 0 { value = value + "\n\n*" + esName + " literal — " + clr + "*" }
    let w = JsonWriter()
    w.obj()
    w.prop("contents")
    w.obj()
    w.prop("kind")
    w.str("markdown")
    w.prop("value")
    w.str(value)
    w.endObj()
    w.endObj()
    return w.render()
}

func (store: DocumentStore) onHover(prms: JsonElement) -> string {
    let sym = store.symbolAt(prms)
    if sym == nil { return store.onHoverLiteral(prms) }
    // Fence the signature as `esharp` (the registered language id) so the client
    // colourises it with our grammar, and add the kind ("method", "free function",
    // "field", "parameter", …) as an italic subtitle so the reader knows what it is.
    let kind = SymbolDisplay.DescribeKind(sym)
    var value = "```esharp\n" + SymbolDisplay.Describe(sym) + "\n```"
    if kind.Length > 0 { value = value + "\n\n*" + kind + "*" }
    let w = JsonWriter()
    w.obj()
    w.prop("contents")
    w.obj()
    w.prop("kind")
    w.str("markdown")
    w.prop("value")
    w.str(value)
    w.endObj()
    w.endObj()
    return w.render()
}

// Symbol-accurate occurrence highlight — replaces the editor's word-match guesswork
// with the actual references of the symbol under the cursor, in this file only.
func (store: DocumentStore) onDocumentHighlight(prms: JsonElement) -> string {
    let uri = store.canon(uriOf(prms))
    let sym = store.symbolAt(prms)
    if sym == nil { return "null" }
    let text = store.textOf(uri)
    if text == nil { return "null" }
    let w = JsonWriter()
    w.arr()
    for use in store.model().FindReferences(sym) {
        if use.Span.File == uri {
            w.obj()
            w.prop("range")
            w.writeRangeAt(text, use.Span.Start, use.Span.End)
            w.prop("kind")
            w.num(use.Occurrence == SymbolOccurrence.Declaration ? 3 : 2)
            w.endObj()
        }
    }
    w.endArr()
    return w.render()
}

// The LSP "painting" surface: every identifier coloured by what the compiler
// resolved it to, not by a regex grammar. Full-document tokens for `uri`.
func (store: DocumentStore) onSemanticTokens(prms: JsonElement) -> string {
    let uri = store.canon(uriOf(prms))
    let text = store.textOf(uri)
    if text == nil { return "null" }
    let occs = store.model().OccurrencesIn(uri)
    let w = JsonWriter()
    w.obj()
    w.writeSemanticTokensData(text, occs)
    w.endObj()
    return w.render()
}

func (store: DocumentStore) onDefinition(prms: JsonElement) -> string {
    let sym = store.symbolAt(prms)
    if sym == nil { return "null" }
    let span = sym.Span
    if !span.IsValid { return "null" }
    let text = store.textOf(span.File)
    if text == nil { return "null" }
    let w = JsonWriter()
    w.writeLocation(span.File, text, span.Start, span.End)
    return w.render()
}

func (store: DocumentStore) onReferences(prms: JsonElement) -> string {
    let sym = store.symbolAt(prms)
    if sym == nil { return "null" }
    var includeDecl = true
    if prms.TryGetProperty("context", out var ctx) {
        if ctx.TryGetProperty("includeDeclaration", out var inc) {
            includeDecl = inc.GetBoolean()
        }
    }
    let w = JsonWriter()
    w.arr()
    for r in store.model().FindReferences(sym) {
        if !includeDecl && r.Occurrence == SymbolOccurrence.Declaration { continue }
        let text = store.textOf(r.Span.File)
        if text != nil {
            w.writeLocation(r.Span.File, text, r.Span.Start, r.Span.End)
        }
    }
    w.endArr()
    return w.render()
}

func (store: DocumentStore) onCompletion(prms: JsonElement) -> string {
    let uri = store.canon(uriOf(prms))
    let text = store.textOf(uri)
    if text == nil { return "null" }
    let offset = offsetIn(text, prms)
    if offset < 0 { return "null" }
    let w = JsonWriter()
    w.arr()
    if offset >= 2 && offset <= text.Content.Length && text.Content[offset - 1] == '.' {
        // Member mode: the cursor sits after `.` — the receiver's type carries
        // the surface to offer. A TYPE receiver (`Console.`, `Color.`) offers the
        // static surface and the cases; a value receiver offers instance members.
        let recv = store.model().GetSymbolAt(uri, offset - 2)
        if recv != nil {
            let t = store.model().GetTypeOf(recv)
            if t != nil {
                let wantStatic = recv is TypeSymbol
                for m in t.Members {
                    if m.IsStatic == wantStatic { w.writeCompletionItem(m) }
                }
                for f in t.Fields { w.writeCompletionItem(f) }
                for c in t.Constants { w.writeCompletionItem(c) }
                for cs in t.Cases { w.writeCompletionItem(cs) }
            }
        }
    } else {
        for s in store.model().LookupSymbolsInScope(uri, offset) {
            w.writeCompletionItem(s)
        }
    }
    w.endArr()
    return w.render()
}

func (store: DocumentStore) onDocumentSymbol(prms: JsonElement) -> string {
    let uri = store.canon(uriOf(prms))
    let text = store.textOf(uri)
    if text == nil { return "null" }
    let w = JsonWriter()
    w.arr()
    for d in store.model().DeclarationsIn(uri) {
        if d.Symbol is not LocalSymbol {
            w.obj()
            w.prop("name")
            w.str(d.Symbol.Name)
            w.prop("kind")
            w.num(outlineKindOf(d.Symbol))
            w.prop("location")
            w.writeLocation(uri, text, d.Span.Start, d.Span.End)
            w.endObj()
        }
    }
    w.endArr()
    return w.render()
}

func (store: DocumentStore) onRename(prms: JsonElement) -> string {
    let sym = store.symbolAt(prms)
    if sym == nil { return "null" }
    if !sym.Span.IsValid { return "null" }
    let newName = prms.GetProperty("newName").GetString() ?? ""
    if newName.Length == 0 { return "null" }
    let refs = store.model().FindReferences(sym)
    // Group the edits by file: collect the distinct files, emit each file's edits.
    let files = List<string>()
    for r in refs {
        if !files.Contains(r.Span.File) { files.Add(r.Span.File) }
    }
    let w = JsonWriter()
    w.obj()
    w.prop("changes")
    w.obj()
    for f in files {
        let text = store.textOf(f)
        if text != nil {
            w.prop(f)
            w.arr()
            for r in refs {
                if r.Span.File == f {
                    w.obj()
                    w.prop("range")
                    w.writeRangeAt(text, r.Span.Start, r.Span.End)
                    w.prop("newText")
                    w.str(newName)
                    w.endObj()
                }
            }
            w.endArr()
        }
    }
    w.endObj()
    w.endObj()
    return w.render()
}
