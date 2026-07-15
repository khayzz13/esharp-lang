namespace Esharp.LanguageServer

// The LSP wire: Content-Length framing over a pair of streams. main wires the
// process's stdin/stdout; tests wire MemoryStreams. Content-Length counts BYTES,
// so the body is read at the byte level and decoded as UTF-8 — a header-level
// char read would corrupt any non-ASCII source in string literals.
pub class Transport {
    input: Stream
    output: Stream
    reader: BinaryReader

    init(input: Stream, output: Stream) {
        self.input = input
        self.output = output
        self.reader = BinaryReader(input)
    }

    // One framed message's body, or nil when the peer hung up / the frame is
    // unreadable. Headers are ASCII lines ending in \r\n; a blank line starts the
    // body; any header other than Content-Length is ignored.
    pub func readMessage() -> string? {
        var contentLength = -1
        while true {
            let line = self.readLine() else { return nil }
            if line.Length == 0 {
                if contentLength < 0 { return nil }
                return self.readBody(contentLength)
            }
            let lower = line.ToLowerInvariant()
            if lower.StartsWith("content-length:") {
                let value = line.Substring(15).Trim()
                if int.TryParse(value, out var n) { contentLength = n }
            }
        }
    }

    // One header line, without its \r\n. nil on EOF before any byte of a line.
    func readLine() -> string? {
        let sb = StringBuilder()
        while true {
            let b = self.input.ReadByte()
            if b < 0 {
                if sb.Length == 0 { return nil }
                return sb.ToString()
            }
            if b == 10 { return sb.ToString() }
            if b != 13 { sb.Append(Convert.ToChar(b)) }
        }
    }

    // Exactly `length` body bytes, UTF-8 decoded. BinaryReader.ReadBytes loops the
    // underlying stream until the count is met or EOF; short means the peer died.
    func readBody(length: int) -> string? {
        let buffer = self.reader.ReadBytes(length)
        if buffer.Length < length { return nil }
        return Encoding.UTF8.GetString(buffer)
    }

    pub func writeMessage(json: string) {
        let body = Encoding.UTF8.GetBytes(json)
        let header = Encoding.ASCII.GetBytes("Content-Length: {body.Length}\r\n\r\n")
        self.output.Write(header, 0, header.Length)
        self.output.Write(body, 0, body.Length)
        self.output.Flush()
    }
}
