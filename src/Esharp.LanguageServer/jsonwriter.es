namespace Esharp.LanguageServer

// The response side of the wire: a JSON emitter over a StringBuilder with
// single-flag comma management (a comma is owed before any value or property
// except the first in its scope; `prop` clears the flag so its value follows
// bare; closing a scope makes the whole scope a value to the one outside).
// Parsing stays on System.Text.Json — this is only the writer.
pub class JsonWriter {
    sb: StringBuilder
    needComma: bool

    init() {
        self.sb = StringBuilder()
        self.needComma = false
    }

    pub func obj() {
        self.pre()
        self.sb.Append('{')
        self.needComma = false
    }

    pub func endObj() {
        self.sb.Append('}')
        self.needComma = true
    }

    pub func arr() {
        self.pre()
        self.sb.Append('[')
        self.needComma = false
    }

    pub func endArr() {
        self.sb.Append(']')
        self.needComma = true
    }

    pub func prop(name: string) {
        self.pre()
        self.writeString(name)
        self.sb.Append(':')
        self.needComma = false
    }

    pub func str(value: string) {
        self.pre()
        self.writeString(value)
        self.needComma = true
    }

    pub func num(value: int) {
        self.pre()
        self.sb.Append(value)
        self.needComma = true
    }

    pub func flag(value: bool) {
        self.pre()
        self.sb.Append(value ? "true" : "false")
        self.needComma = true
    }

    pub func nul() {
        self.pre()
        self.sb.Append("null")
        self.needComma = true
    }

    // Pre-rendered JSON spliced in verbatim — a request id echoed back, a
    // handler-built result wrapped into a response envelope.
    pub func raw(json: string) {
        self.pre()
        self.sb.Append(json)
        self.needComma = true
    }

    pub func render() -> string = self.sb.ToString()

    func pre() {
        if self.needComma { self.sb.Append(',') }
    }

    // A quoted JSON string literal: ", \, and the C0 controls escaped.
    func writeString(value: string) {
        self.sb.Append('"')
        var i = 0
        while i < value.Length {
            let c = value[i]
            if c == '"' {
                self.sb.Append("\\\"")
            } else if c == '\\' {
                self.sb.Append("\\\\")
            } else if c == '\n' {
                self.sb.Append("\\n")
            } else if c == '\r' {
                self.sb.Append("\\r")
            } else if c == '\t' {
                self.sb.Append("\\t")
            } else if Convert.ToInt32(c) < 32 {
                self.sb.Append("\\u" + Convert.ToInt32(c).ToString("x4"))
            } else {
                self.sb.Append(c)
            }
            i += 1
        }
        self.sb.Append('"')
    }
}
