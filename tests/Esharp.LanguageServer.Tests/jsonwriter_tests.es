namespace Esharp.LanguageServer.Tests

using "Esharp.LanguageServer"
using "Xunit"

pub class JsonWriterTests {
    [Fact]
    pub func objectWithProps() {
        let w = JsonWriter()
        w.obj()
        w.prop("name")
        w.str("esharp")
        w.prop("version")
        w.num(1)
        w.prop("ready")
        w.flag(true)
        w.endObj()
        Assert.Equal("{\"name\":\"esharp\",\"version\":1,\"ready\":true}", w.render())
    }

    [Fact]
    pub func nestedScopesManageCommas() {
        let w = JsonWriter()
        w.obj()
        w.prop("items")
        w.arr()
        w.num(1)
        w.num(2)
        w.obj()
        w.prop("k")
        w.nul()
        w.endObj()
        w.endArr()
        w.prop("after")
        w.str("x")
        w.endObj()
        Assert.Equal("{\"items\":[1,2,{\"k\":null}],\"after\":\"x\"}", w.render())
    }

    [Fact]
    pub func escapesControlsQuotesAndBackslashes() {
        let w = JsonWriter()
        w.str("a\"b\\c\nd\te")
        Assert.Equal("\"a\\\"b\\\\c\\nd\\te\"", w.render())
    }

    [Fact]
    pub func rawSplicesVerbatim() {
        let w = JsonWriter()
        w.obj()
        w.prop("id")
        w.raw("42")
        w.prop("result")
        w.raw("{\"ok\":true}")
        w.endObj()
        Assert.Equal("{\"id\":42,\"result\":{\"ok\":true}}", w.render())
    }
}
