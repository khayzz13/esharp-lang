namespace Esharp.LanguageServer.Tests

using "Esharp.Compiler.Workspace"
using "Xunit"

// The coordinate contract the server's ±1 mapping stands on: SourceText speaks
// 1-based line/column over UTF-16 chars, offsets round-trip both ways.
pub class ProtocolTests {
    [Fact]
    pub func offsetRoundTripsThroughLineColumn() {
        let text = SourceText("ab\ncde\nf")
        // Offset 4 is the 'd' on line 2, column 2 (1-based).
        let (line, col) = text.GetLineColumn(4)
        Assert.Equal(2, line)
        Assert.Equal(2, col)
        Assert.Equal(4, text.GetOffset(2, 2))
    }

    [Fact]
    pub func firstCharIsOneOne() {
        let text = SourceText("hello")
        let (line, col) = text.GetLineColumn(0)
        Assert.Equal(1, line)
        Assert.Equal(1, col)
        Assert.Equal(0, text.GetOffset(1, 1))
    }
}
