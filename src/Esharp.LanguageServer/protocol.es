namespace Esharp.LanguageServer

using "Esharp.Compilation"
using "Esharp.Symbols"
using "Esharp.Diagnostics.Semantics"
using "Esharp.Diagnostics"

// Protocol shapes: the LSP↔compiler coordinate mapping and the JSON payload
// builders shared by every handler.
//
// LSP positions are 0-based line/character in UTF-16 code units; the compiler's
// SourceText speaks 1-based line/column over .NET chars — the same code units, so
// the mapping is exactly ±1 on each axis. SourceSpan.Start/End are char offsets
// into the document its `File` names (the document's workspace uri, see
// documents.es), which is what the writers below take.

// {"line":l,"character":c}
func (w: JsonWriter) writePosition(line: int, character: int) {
    w.obj()
    w.prop("line")
    w.num(line)
    w.prop("character")
    w.num(character)
    w.endObj()
}

// {"start":{...},"end":{...}} from char offsets into `text`.
func (w: JsonWriter) writeRangeAt(text: SourceText, startOffset: int, endOffset: int) {
    let (startLine, startCol) = text.GetLineColumn(startOffset)
    let (endLine, endCol) = text.GetLineColumn(endOffset)
    w.obj()
    w.prop("start")
    w.writePosition(startLine - 1, startCol - 1)
    w.prop("end")
    w.writePosition(endLine - 1, endCol - 1)
    w.endObj()
}

// {"uri":...,"range":{...}}
func (w: JsonWriter) writeLocation(uri: string, text: SourceText, startOffset: int, endOffset: int) {
    w.obj()
    w.prop("uri")
    w.str(uri)
    w.prop("range")
    w.writeRangeAt(text, startOffset, endOffset)
    w.endObj()
}

// One LSP diagnostic. The compiler's Diagnostic carries a point (1-based
// line/column), not a span — the range widens the point to the end of the word
// under it, a documented approximation until diagnostics carry spans.
func (w: JsonWriter) writeDiagnostic(text: SourceText, d: Diagnostic) {
    var start = 0
    try {
        start = text.GetOffset(d.Line, d.Column)
    } catch {
        start = 0
    }
    let end = widenToWord(text, start)
    w.obj()
    w.prop("range")
    w.writeRangeAt(text, start, end)
    w.prop("severity")
    w.num(d.Severity == DiagnosticSeverity.Error ? 1 : 2)
    w.prop("code")
    w.str(d.Code)
    w.prop("source")
    w.str("esharp")
    w.prop("message")
    w.str(d.Message)
    w.endObj()
}

// One completion item: label + kind + the hover line as detail.
func (w: JsonWriter) writeCompletionItem(sym: ISymbol) {
    w.obj()
    w.prop("label")
    w.str(sym.Name)
    w.prop("kind")
    w.num(completionKindOf(sym))
    w.prop("detail")
    w.str(SymbolDisplay.Describe(sym))
    w.endObj()
}

func widenToWord(text: SourceText, offset: int) -> int {
    let content = text.Content
    var i = offset
    while i < content.Length && isIdentChar(content[i]) {
        i += 1
    }
    return i > offset ? i : offset + 1
}

func isIdentChar(c: char) -> bool = char.IsLetterOrDigit(c) || c == '_'

// LSP CompletionItemKind for a symbol — the narrowing dogfood: one match
// discriminates the open ISymbol into its concrete kinds, a guard splits static
// functions from methods. ISymbol is an interface (open world), so `default`.
func completionKindOf(sym: ISymbol) -> int =
    match sym {
        (t: TypeSymbol)                 => typeCompletionKind(t)
        (m: MethodSymbol) if m.IsStatic => 3
        (m: MethodSymbol)               => 2
        (f: FieldSymbol)                => 5
        (c: ConstSymbol)                => 21
        (cs: CaseSymbol)                => 20
        (l: LocalSymbol)                => 6
        default                         => 6
    }

// LSP SymbolKind for the document outline — same dispatch, different code page.
func outlineKindOf(sym: ISymbol) -> int =
    match sym {
        (t: TypeSymbol)                 => typeOutlineKind(t)
        (m: MethodSymbol) if m.IsStatic => 12
        (m: MethodSymbol)               => 6
        (f: FieldSymbol)                => 8
        (c: ConstSymbol)                => 14
        (cs: CaseSymbol)                => 22
        (l: LocalSymbol)                => 13
        default                         => 13
    }

func typeCompletionKind(t: TypeSymbol) -> int {
    if t.Kind == TypeSymbolKind.Interface { return 8 }
    if t.Kind == TypeSymbolKind.Enum { return 13 }
    if t.Kind == TypeSymbolKind.Union || t.Kind == TypeSymbolKind.RefUnion { return 13 }
    if t.Kind == TypeSymbolKind.Struct { return 22 }
    return 7
}

func typeOutlineKind(t: TypeSymbol) -> int {
    if t.Kind == TypeSymbolKind.Interface { return 11 }
    if t.Kind == TypeSymbolKind.Enum { return 10 }
    if t.Kind == TypeSymbolKind.Union || t.Kind == TypeSymbolKind.RefUnion { return 10 }
    if t.Kind == TypeSymbolKind.Struct { return 23 }
    return 5
}

// === Semantic tokens =========================================================
// The LSP "painting" surface: the server, not the editor's regex grammar, decides
// the colour of every identifier. The legend below is the single source of truth —
// its order IS the integer index `semanticTokenTypeOf` returns, and onInitialize
// advertises the same arrays. Token types follow the LSP standard set so stock
// themes colour them without extra mapping.
//
//   0 namespace  1 type     2 class    3 struct   4 enum      5 interface
//   6 typeParam  7 parameter 8 variable 9 property 10 enumMember
//   11 function  12 method   13 keyword
func (w: JsonWriter) writeSemanticTokensLegend() {
    w.prop("tokenTypes")
    w.arr()
    w.str("namespace")
    w.str("type")
    w.str("class")
    w.str("struct")
    w.str("enum")
    w.str("interface")
    w.str("typeParameter")
    w.str("parameter")
    w.str("variable")
    w.str("property")
    w.str("enumMember")
    w.str("function")
    w.str("method")
    w.str("keyword")
    w.endArr()
    w.prop("tokenModifiers")
    w.arr()
    w.str("declaration")
    w.str("readonly")
    w.str("static")
    w.endArr()
}

// The token type for a symbol — the same narrowing over the open ISymbol that hover
// and completion use, now choosing a paint colour. Free functions and methods split
// (a free function is a NamespaceHost member with no receiver), so the editor can
// colour a call site by what it actually resolves to.
func semanticTokenTypeOf(sym: ISymbol) -> int =
    match sym {
        (t: TypeSymbol)                   => typeTokenKind(t)
        (m: MethodSymbol)                 => methodTokenKind(m)
        (f: FieldSymbol)                  => 9
        (c: ConstSymbol)                  => 8
        (cs: CaseSymbol)                  => 10
        (l: LocalSymbol) if l.IsParameter => 7
        (l: LocalSymbol)                  => 8
        default                           => 8
    }

func typeTokenKind(t: TypeSymbol) -> int {
    if t.Kind == TypeSymbolKind.Interface { return 5 }
    if t.Kind == TypeSymbolKind.Enum { return 4 }
    if t.Kind == TypeSymbolKind.Union { return 4 }
    if t.Kind == TypeSymbolKind.Struct { return 3 }
    if t.Kind == TypeSymbolKind.Class { return 2 }
    if t.Kind == TypeSymbolKind.RefUnion { return 2 }
    if t.Kind == TypeSymbolKind.StaticFunc { return 2 }
    if t.Kind == TypeSymbolKind.TypeParameter { return 6 }
    if t.Kind == TypeSymbolKind.NamespaceHost { return 0 }
    return 1
}

func methodTokenKind(m: MethodSymbol) -> int {
    // Free function: a namespace-host member reached without a receiver. Everything
    // else — value/pointer-receiver methods, inline `class` methods, static-func
    // members — paints as a method.
    if m.ReceiverKind != ReceiverKind.None { return 12 }
    let dt = m.DeclaringType else { return 12 }
    return dt.Kind == TypeSymbolKind.NamespaceHost ? 11 : 12
}

// The modifier bitmask: declaration (1) | readonly (2) | static (4).
func semanticTokenModsOf(sym: ISymbol, isDecl: bool) -> int {
    var mods = 0
    if isDecl { mods = mods + 1 }
    let extra = match sym {
        (c: ConstSymbol)                => 2
        (m: MethodSymbol) if m.IsStatic => 4
        default                         => 0
    }
    return mods + extra
}

// Delta-encode the file's occurrences into the LSP semantic-tokens `struct` array:
// five ints per token — deltaLine, deltaChar, length, tokenType, modifiers — each
// relative to the previous token. Occurrences carry exact identifier spans (the
// parser stamps NameSpan, the binder reports against it) and arrive sorted by
// start; an overlap (one name reported under two symbols) keeps the first so the
// stream stays valid. Primitive type names (`int`, `string`) are left to the
// grammar — painting them as `type` would recolour what reads as a keyword.
func (w: JsonWriter) writeSemanticTokensData(text: SourceText, occs: IReadOnlyList<SymbolUse>) {
    w.prop("data")
    w.arr()
    var prevLine = 0
    var prevChar = 0
    var prevEnd = -1
    let content = text.Content
    for use in occs {
        let span = use.Span
        var name = use.Symbol.Name
        if name.Length == 0 { continue }
        if span.Start < 0 || span.End > content.Length { continue }
        let prim = use.Symbol as TypeSymbol
        if prim != nil && prim.Kind == TypeSymbolKind.Primitive { continue }
        // A generic symbol paints its base name only (`Option`, not `Option<int>`).
        let lt = name.IndexOf('<')
        if lt > 0 { name = name.Substring(0, lt) }
        // Most occurrences carry the exact identifier span; a type USE can span its
        // generic arguments too — narrow to the name within the span.
        var start = span.Start
        if span.Length != name.Length {
            let idx = content.IndexOf(name, span.Start)
            if idx < 0 || idx >= span.End { continue }
            start = idx
        }
        if start < prevEnd { continue }
        let (line1, col1) = text.GetLineColumn(start)
        let line0 = line1 - 1
        let char0 = col1 - 1
        let isDecl = use.Occurrence == SymbolOccurrence.Declaration
        let ttype = semanticTokenTypeOf(use.Symbol)
        let mods = semanticTokenModsOf(use.Symbol, isDecl)
        var deltaChar = char0
        if line0 == prevLine { deltaChar = char0 - prevChar }
        w.num(line0 - prevLine)
        w.num(deltaChar)
        w.num(name.Length)
        w.num(ttype)
        w.num(mods)
        prevLine = line0
        prevChar = char0
        prevEnd = start + name.Length
    }
    w.endArr()
}
