namespace Esharp.LanguageServer.Tests

using "Esharp.LanguageServer"

// The session harness: frame each request onto an in-memory wire, run a full
// LspServer over it, split the server's output back into messages. No process
// spawning — the server is constructed over the streams the tests own.

pub func runSession(messages: List<string>) -> List<string> {
    let framed = MemoryStream()
    let framer = Transport(MemoryStream(), framed)
    for m in messages {
        framer.writeMessage(m)
    }
    let input = MemoryStream(framed.ToArray())
    let output = MemoryStream()
    let server = LspServer(input, output)
    server.run()
    let reader = Transport(MemoryStream(output.ToArray()), MemoryStream())
    let responses = List<string>()
    while true {
        let m = reader.readMessage() else { break }
        responses.Add(m)
    }
    return responses
}

// The same session, returning the server's exit code instead of its output.
pub func runSessionCode(messages: List<string>) -> int {
    let framed = MemoryStream()
    let framer = Transport(MemoryStream(), framed)
    for m in messages {
        framer.writeMessage(m)
    }
    let server = LspServer(MemoryStream(framed.ToArray()), MemoryStream())
    return server.run()
}

// --- request builders ---------------------------------------------------------

pub func req(id: int, method: string, prms: string) -> string =
    "{\"jsonrpc\":\"2.0\",\"id\":" + id.ToString() + ",\"method\":\"" + method + "\",\"params\":" + prms + "}"

pub func note(method: string, prms: string) -> string =
    "{\"jsonrpc\":\"2.0\",\"method\":\"" + method + "\",\"params\":" + prms + "}"

pub func initializeMsg() -> string =
    req(1, "initialize", "{\"rootUri\":null,\"capabilities\":{}}")

// didOpen params — built with the server's own JsonWriter so the source text
// (newlines, quotes) is escaped correctly.
pub func didOpenMsg(uri: string, text: string) -> string {
    let w = JsonWriter()
    w.obj()
    w.prop("textDocument")
    w.obj()
    w.prop("uri")
    w.str(uri)
    w.prop("languageId")
    w.str("esharp")
    w.prop("version")
    w.num(1)
    w.prop("text")
    w.str(text)
    w.endObj()
    w.endObj()
    return note("textDocument/didOpen", w.render())
}

pub func didChangeMsg(uri: string, text: string) -> string {
    let w = JsonWriter()
    w.obj()
    w.prop("textDocument")
    w.obj()
    w.prop("uri")
    w.str(uri)
    w.prop("version")
    w.num(2)
    w.endObj()
    w.prop("contentChanges")
    w.arr()
    w.obj()
    w.prop("text")
    w.str(text)
    w.endObj()
    w.endArr()
    w.endObj()
    return note("textDocument/didChange", w.render())
}

// Position-taking request params: {"textDocument":{"uri":u},"position":{...}}.
pub func posParams(uri: string, line: int, character: int) -> string {
    let w = JsonWriter()
    w.obj()
    w.prop("textDocument")
    w.obj()
    w.prop("uri")
    w.str(uri)
    w.endObj()
    w.prop("position")
    w.obj()
    w.prop("line")
    w.num(line)
    w.prop("character")
    w.num(character)
    w.endObj()
    w.endObj()
    return w.render()
}

pub func renameParams(uri: string, line: int, character: int, newName: string) -> string {
    let pos = posParams(uri, line, character)
    return pos.Substring(0, pos.Length - 1) + ",\"newName\":\"" + newName + "\"}"
}

pub func docParams(uri: string) -> string =
    "{\"textDocument\":{\"uri\":\"" + uri + "\"}}"

// The first output message whose body contains `marker`, or "" when none does.
pub func firstContaining(messages: List<string>, marker: string) -> string {
    for m in messages {
        if m.Contains(marker) { return m }
    }
    return ""
}
