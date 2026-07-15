namespace Esharp.LanguageServer.Tests

using "Esharp.LanguageServer"
using "Xunit"

// Full JSON-RPC sessions against the in-memory server: every feature exercised
// through the wire, fixtures inline. The Sym fixture runs the narrowing-built
// code paths (match type patterns) the server itself is written with.

const FIX_URI = "file:///fix.es"
const FIX = "namespace Fix\nfunc add(a: int, b: int) -> int = a + b\nfunc go() -> int = add(1, 2)\n"

const SYM_URI = "file:///sym.es"
const SYM = "namespace Fix2\nabstract class Sym {\n    name: string\n    init(n: string) { self.name = n }\n}\nclass TypeSym : Sym {\n    arity: int\n    init(n: string, a: int) : base(n) { self.arity = a }\n}\nfunc describe(s: Sym) -> string =\n    match s {\n        (t: TypeSym) => t.name\n        default => s.name\n    }\n"

const PT_URI = "file:///pt.es"
const PT = "namespace Fix3\nstruct Point { x: int, y: int }\nfunc go() -> int {\n    let pt = Point { x: 1, y: 2 }\n    return pt.x\n}\n"

pub class ServerTests {
    [Fact]
    pub func initializeAnswersCapabilities() {
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        let resp = firstContaining(runSession(msgs), "\"id\":1")
        Assert.Contains("\"hoverProvider\":true", resp)
        Assert.Contains("\"definitionProvider\":true", resp)
        Assert.Contains("\"referencesProvider\":true", resp)
        Assert.Contains("\"renameProvider\":true", resp)
        Assert.Contains("\"documentSymbolProvider\":true", resp)
        Assert.Contains("\"triggerCharacters\":[\".\"]", resp)
        Assert.Contains("\"name\":\"esharp-lsp\"", resp)
    }

    [Fact]
    pub func didOpenPublishesAndDidChangeClears() {
        let bad = "namespace Fix\nfunc go() -> int = missingFn(1)\n"
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg(FIX_URI, bad))
        msgs.Add(didChangeMsg(FIX_URI, FIX))
        let received = runSession(msgs)
        // First publish carries a diagnostic; the post-fix publish is empty.
        let withDiag = firstContaining(received, "\"diagnostics\":[{")
        Assert.Contains("publishDiagnostics", withDiag)
        let clean = firstContaining(received, "\"diagnostics\":[]")
        Assert.Contains("publishDiagnostics", clean)
    }

    [Fact]
    pub func hoverDescribesTheSymbolUnderTheCursor() {
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg(FIX_URI, FIX))
        msgs.Add(req(2, "textDocument/hover", posParams(FIX_URI, 2, 19)))
        let resp = firstContaining(runSession(msgs), "\"id\":2")
        Assert.Contains("func add(int, int) -> int", resp)
        Assert.Contains("markdown", resp)
    }

    [Fact]
    pub func hoverRunsTheNarrowedSymFixture() {
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg(SYM_URI, SYM))
        msgs.Add(req(2, "textDocument/hover", posParams(SYM_URI, 9, 5)))
        let resp = firstContaining(runSession(msgs), "\"id\":2")
        Assert.Contains("func describe(Sym) -> string", resp)
    }

    [Fact]
    pub func definitionLandsOnTheDeclaration() {
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg(FIX_URI, FIX))
        msgs.Add(req(3, "textDocument/definition", posParams(FIX_URI, 2, 19)))
        let resp = firstContaining(runSession(msgs), "\"id\":3")
        Assert.Contains("\"uri\":\"file:///fix.es\"", resp)
        Assert.Contains("\"line\":1", resp)
    }

    [Fact]
    pub func referencesCountDeclarationAndUse() {
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg(FIX_URI, FIX))
        msgs.Add(req(4, "textDocument/references", posParams(FIX_URI, 2, 19)))
        let resp = firstContaining(runSession(msgs), "\"id\":4")
        let doc = JsonDocument.Parse(resp)
        Assert.Equal(2, doc.RootElement.GetProperty("result").GetArrayLength())
    }

    [Fact]
    pub func renameEditsEveryOccurrence() {
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg(FIX_URI, FIX))
        msgs.Add(req(5, "textDocument/rename", renameParams(FIX_URI, 2, 19, "sum")))
        let resp = firstContaining(runSession(msgs), "\"id\":5")
        let doc = JsonDocument.Parse(resp)
        let edits = doc.RootElement.GetProperty("result").GetProperty("changes").GetProperty(FIX_URI)
        Assert.Equal(2, edits.GetArrayLength())
        Assert.Contains("\"newText\":\"sum\"", resp)
    }

    [Fact]
    pub func completionAfterDotOffersMembers() {
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg(PT_URI, PT))
        msgs.Add(req(6, "textDocument/completion", posParams(PT_URI, 4, 14)))
        let resp = firstContaining(runSession(msgs), "\"id\":6")
        Assert.Contains("\"label\":\"x\"", resp)
        Assert.Contains("\"label\":\"y\"", resp)
    }

    [Fact]
    pub func completionInScopeOffersLocalsAndDeclarations() {
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg(PT_URI, PT))
        msgs.Add(req(6, "textDocument/completion", posParams(PT_URI, 4, 4)))
        let resp = firstContaining(runSession(msgs), "\"id\":6")
        Assert.Contains("\"label\":\"pt\"", resp)
        Assert.Contains("\"label\":\"Point\"", resp)
    }

    [Fact]
    pub func documentSymbolOutlinesTheFile() {
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg(PT_URI, PT))
        msgs.Add(req(7, "textDocument/documentSymbol", docParams(PT_URI)))
        let resp = firstContaining(runSession(msgs), "\"id\":7")
        Assert.Contains("\"name\":\"Point\"", resp)
        Assert.Contains("\"name\":\"go\"", resp)
    }

    [Fact]
    pub func unknownRequestAnswersMethodNotFound() {
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(req(9, "textDocument/typeDefinition", posParams(FIX_URI, 0, 0)))
        let resp = firstContaining(runSession(msgs), "\"id\":9")
        Assert.Contains("-32601", resp)
    }

    [Fact]
    pub func cleanShutdownExitsZero() {
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(req(2, "shutdown", "{}"))
        msgs.Add("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}")
        Assert.Equal(0, runSessionCode(msgs))
    }

    [Fact]
    pub func droppedWireExitsOne() {
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        Assert.Equal(1, runSessionCode(msgs))
    }

    [Fact]
    pub func semanticTokensPaintTheFile() {
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg(PT_URI, PT))
        msgs.Add(req(8, "textDocument/semanticTokens/full", docParams(PT_URI)))
        let resp = firstContaining(runSession(msgs), "\"id\":8")
        let doc = JsonDocument.Parse(resp)
        let tokens = doc.RootElement.GetProperty("result").GetProperty("data")
        // The painter emits at least the Point type, its x/y fields, the go function,
        // and the pt local — five 5-int tokens, so well over a dozen integers.
        Assert.True(tokens.GetArrayLength() > 10)
    }

    [Fact]
    pub func voidFunctionHoverShowsArrowVoid() {
        let src = "namespace V\nfunc shout(name: string) { System.Console.WriteLine(name) }\n"
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg("file:///v.es", src))
        msgs.Add(req(2, "textDocument/hover", posParams("file:///v.es", 1, 6)))
        let resp = firstContaining(runSession(msgs), "\"id\":2")
        Assert.Contains("-> void", resp)
    }

    [Fact]
    pub func hoverOnAStringLiteralNamesTheType() {
        let src = "namespace L\nfunc greet() -> string = \"hello world\"\n"
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg("file:///lit.es", src))
        // Inside the "hello world" literal on line 1.
        msgs.Add(req(2, "textDocument/hover", posParams("file:///lit.es", 1, 28)))
        let resp = firstContaining(runSession(msgs), "\"id\":2")
        Assert.Contains("System.String", resp)
    }

    [Fact]
    pub func hoverOnABclMethodDescribesTheMethod() {
        let src = "namespace L2\nfunc go() -> int {\n    let msgs = List<string>()\n    msgs.Add(\"hi\")\n    return msgs.Count\n}\n"
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg("file:///bcl.es", src))
        // The `Add` member on line 3 (0-based), after `msgs.`.
        msgs.Add(req(2, "textDocument/hover", posParams("file:///bcl.es", 3, 10)))
        let resp = firstContaining(runSession(msgs), "\"id\":2")
        Assert.Contains("func Add", resp)
        Assert.Contains("method on List<string>", resp)
    }

    [Fact]
    pub func completionAfterDotOnBclReceiverOffersMembers() {
        let src = "namespace L3\nfunc go() -> int {\n    let msgs = List<string>()\n    msgs.Add(\"hi\")\n    return msgs.Count\n}\n"
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg("file:///bclc.es", src))
        // Right after the `.` in `msgs.Add` (line 3, char 9).
        msgs.Add(req(3, "textDocument/completion", posParams("file:///bclc.es", 3, 9)))
        let resp = firstContaining(runSession(msgs), "\"id\":3")
        Assert.Contains("\"label\":\"Add\"", resp)
        Assert.Contains("\"label\":\"Count\"", resp)
    }

    // Real typing is never well-formed: the moment completion fires, the document
    // has a dangling `pt.` or a half-typed word. These pin the mid-typing states.
    [Fact]
    pub func completionAfterDanglingDotOffersMembers() {
        let src = "namespace T1\nstruct Point { x: int, y: int }\nfunc go() -> int {\n    let pt = Point { x: 1, y: 2 }\n    pt.\n    return 1\n}\n"
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg("file:///dot.es", src))
        // Cursor right after `pt.` on line 4 (0-based), char 7.
        msgs.Add(req(2, "textDocument/completion", posParams("file:///dot.es", 4, 7)))
        let resp = firstContaining(runSession(msgs), "\"id\":2")
        Assert.Contains("\"label\":\"x\"", resp)
        Assert.Contains("\"label\":\"y\"", resp)
    }

    [Fact]
    pub func completionMidWordOffersScope() {
        let src = "namespace T2\nfunc go() -> int {\n    let total = 10\n    to\n    return 1\n}\n"
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg("file:///mid.es", src))
        // Cursor after the half-typed `to` on line 3, char 6.
        msgs.Add(req(2, "textDocument/completion", posParams("file:///mid.es", 3, 6)))
        let resp = firstContaining(runSession(msgs), "\"id\":2")
        Assert.Contains("\"label\":\"total\"", resp)
    }

    [Fact]
    pub func completionAfterDanglingDotOnBclReceiverOffersMembers() {
        let src = "namespace T3\nfunc go() -> int {\n    let msgs = List<string>()\n    msgs.\n    return 1\n}\n"
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg("file:///bcldot.es", src))
        // Cursor right after `msgs.` on line 3, char 9.
        msgs.Add(req(2, "textDocument/completion", posParams("file:///bcldot.es", 3, 9)))
        let resp = firstContaining(runSession(msgs), "\"id\":2")
        Assert.Contains("\"label\":\"Add\"", resp)
    }

    [Fact]
    pub func hoverOnAKeywordAnswersNothing() {
        let src = "namespace L4\nfunc go() -> int {\n    return 1\n}\n"
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg("file:///kw.es", src))
        // On the `return` keyword (line 2, char 5).
        msgs.Add(req(2, "textDocument/hover", posParams("file:///kw.es", 2, 5)))
        let resp = firstContaining(runSession(msgs), "\"id\":2")
        Assert.Contains("\"result\":null", resp)
    }

    [Fact]
    pub func hoverLabelsTheSymbolKind() {
        let msgs = List<string>()
        msgs.Add(initializeMsg())
        msgs.Add(didOpenMsg(PT_URI, PT))
        // The `pt` local on line 3 (0-based) — hover subtitles it as a local.
        msgs.Add(req(2, "textDocument/hover", posParams(PT_URI, 3, 8)))
        let resp = firstContaining(runSession(msgs), "\"id\":2")
        Assert.Contains("local", resp)
    }
}
