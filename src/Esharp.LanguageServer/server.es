namespace Esharp.LanguageServer

// JSON-RPC dispatch: one synchronous loop — read a frame, route on `method`,
// write the response, push diagnostics after every document change. LSP permits
// serial servers, and serial keeps responses ordered for free. stdout is the
// wire; every log line goes to stderr.
pub class LspServer {
    transport: Transport
    store: DocumentStore
    sawShutdown: bool
    exited: bool

    init(input: Stream, output: Stream) {
        self.transport = Transport(input, output)
        self.store = DocumentStore()
        self.sawShutdown = false
        self.exited = false
    }

    // Serve until `exit` or EOF. Exit code 0 when the client shut down cleanly
    // (shutdown then exit), 1 when the wire just dropped.
    pub func run() -> int {
        while true {
            let message = self.transport.readMessage() else { break }
            self.handle(message)
            if self.exited { break }
        }
        return self.sawShutdown ? 0 : 1
    }

    func handle(json: string) {
        let doc = self.parse(json) else { return }
        self.dispatch(doc.RootElement)
    }

    func parse(json: string) -> JsonDocument? {
        try {
            return JsonDocument.Parse(json)
        } catch (Exception e) {
            Console.Error.WriteLine("esharp-lsp: unparseable frame: {e.Message}")
            return nil
        }
    }

    func dispatch(root: JsonElement) {
        var method = ""
        if root.TryGetProperty("method", out var methodEl) {
            method = methodEl.GetString() ?? ""
        }
        if method.Length == 0 { return }

        var id = ""
        if root.TryGetProperty("id", out var idEl) {
            id = idEl.GetRawText()
        }
        var prms = root
        if root.TryGetProperty("params", out var paramsEl) {
            prms = paramsEl
        }

        if id.Length > 0 {
            if isRequest(method) {
                // A handler that throws becomes a JSON-RPC error response; the
                // loop survives every request.
                try {
                    self.respond(id, self.request(method, prms))
                } catch (Exception e) {
                    self.respondError(id, -32603, e.Message)
                }
            } else {
                self.respondError(id, -32601, "method not found: " + method)
            }
            return
        }
        self.notify(method, prms)
    }

    func request(method: string, prms: JsonElement) -> string =
        match method {
            "initialize"                  => self.store.onInitialize(prms)
            "shutdown"                    => self.onShutdown()
            "textDocument/hover"          => self.store.onHover(prms)
            "textDocument/definition"     => self.store.onDefinition(prms)
            "textDocument/references"     => self.store.onReferences(prms)
            "textDocument/completion"     => self.store.onCompletion(prms)
            "textDocument/documentSymbol" => self.store.onDocumentSymbol(prms)
            "textDocument/rename"         => self.store.onRename(prms)
            "textDocument/documentHighlight" => self.store.onDocumentHighlight(prms)
            "textDocument/semanticTokens/full" => self.store.onSemanticTokens(prms)
            default                       => "null"
        }

    func notify(method: string, prms: JsonElement) {
        match method {
            "textDocument/didOpen" {
                self.store.onDidOpen(prms)
                self.publish()
            }
            "textDocument/didChange" {
                self.store.onDidChange(prms)
                self.publish()
            }
            "textDocument/didClose" {
                self.store.onDidClose(prms)
                self.publish()
            }
            "esharp/setProject" {
                self.store.onSetProject(prms)
                self.publish()
            }
            "exit" {
                self.exited = true
            }
            default {
                // `initialized`, `$/...` progress, and anything else: no-op.
            }
        }
    }

    func onShutdown() -> string {
        self.sawShutdown = true
        return "null"
    }

    func publish() {
        for payload in self.store.publishPayloads() {
            self.transport.writeMessage(payload)
        }
    }

    func respond(id: string, result: string) {
        let w = JsonWriter()
        w.obj()
        w.prop("jsonrpc")
        w.str("2.0")
        w.prop("id")
        w.raw(id)
        w.prop("result")
        w.raw(result)
        w.endObj()
        self.transport.writeMessage(w.render())
    }

    func respondError(id: string, code: int, message: string) {
        let w = JsonWriter()
        w.obj()
        w.prop("jsonrpc")
        w.str("2.0")
        w.prop("id")
        w.raw(id)
        w.prop("error")
        w.obj()
        w.prop("code")
        w.num(code)
        w.prop("message")
        w.str(message)
        w.endObj()
        w.endObj()
        self.transport.writeMessage(w.render())
    }
}

func isRequest(method: string) -> bool =
    match method {
        "initialize"                  => true
        "shutdown"                    => true
        "textDocument/hover"          => true
        "textDocument/definition"     => true
        "textDocument/references"     => true
        "textDocument/completion"     => true
        "textDocument/documentSymbol" => true
        "textDocument/rename"         => true
        "textDocument/documentHighlight" => true
        "textDocument/semanticTokens/full" => true
        default                       => false
    }
