namespace Esharp.LanguageServer.Tests

using "Esharp.LanguageServer"
using "Xunit"

// Content-Length framing over in-memory streams — xUnit facts authored in E#,
// on the E#-written transport.
pub class TransportTests {
    [Fact]
    pub func framingRoundTrips() {
        let sink = MemoryStream()
        let writer = Transport(MemoryStream(), sink)
        writer.writeMessage("{\"a\":1}")
        writer.writeMessage("{\"b\":\"two\"}")
        let reader = Transport(MemoryStream(sink.ToArray()), MemoryStream())
        Assert.Equal("{\"a\":1}", reader.readMessage())
        Assert.Equal("{\"b\":\"two\"}", reader.readMessage())
        Assert.Null(reader.readMessage())
    }

    [Fact]
    pub func multibyteBodyHonorsByteLength() {
        // Content-Length counts BYTES: a non-ASCII body is longer in bytes than
        // in chars, and must still round-trip exactly.
        let sink = MemoryStream()
        let writer = Transport(MemoryStream(), sink)
        writer.writeMessage("{\"s\":\"héllo — ✓\"}")
        let reader = Transport(MemoryStream(sink.ToArray()), MemoryStream())
        Assert.Equal("{\"s\":\"héllo — ✓\"}", reader.readMessage())
    }

    [Fact]
    pub func emptyInputYieldsNil() {
        let reader = Transport(MemoryStream(), MemoryStream())
        Assert.Null(reader.readMessage())
    }

    [Fact]
    pub func unknownHeadersIgnored() {
        let raw = "Content-Type: application/vscode-jsonrpc\r\nContent-Length: 2\r\n\r\nhi"
        let reader = Transport(MemoryStream(Encoding.UTF8.GetBytes(raw)), MemoryStream())
        Assert.Equal("hi", reader.readMessage())
    }

    [Fact]
    pub func truncatedBodyYieldsNil() {
        let raw = "Content-Length: 10\r\n\r\nhi"
        let reader = Transport(MemoryStream(Encoding.UTF8.GetBytes(raw)), MemoryStream())
        Assert.Null(reader.readMessage())
    }
}
