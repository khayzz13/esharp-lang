// Style note: E# source below is inline \n-escaped for brevity — do NOT copy this in new test files; prefer readable """ raw-string blocks (these tests double as the E# corpus).
namespace Esharp.Tests;

/// Value-`choice` match payload binding: positional destructuring AND single-
/// binding case views (`view.payloadName`), with the single-payload view being
/// transparent (bare use == the value). Covers nesting, Result, interpolation,
/// and match-expression position.
public sealed class ILEmitterTests_CaseView
{
    static object? Run(string body, string method, params object?[] args) =>
        EsHarness.Run("namespace Test\n" + body, method, args);

    const string Token = "union Token { word(text: string) number(value: int) }\n";
    const string Pairs = "union Pair2 { single(x: int) couple(a: int, b: int) }\n";

    // ── positional binding still works ──
    [Fact] public void Positional_SinglePayload_UsedBare() =>
        Assert.Equal(7, Run(Token + "func go() -> int {\n  let t = Token.number(7)\n  match (t: Token) { .word(w) { return 0 } .number(n) { return n } }\n  return -1\n}", "go"));

    [Fact] public void Positional_TwoPayloads() =>
        Assert.Equal(30, Run(Pairs + "func go() -> int {\n  let p = Pair2.couple(10, 20)\n  match (p: Pair2) { .single(x) { return x } .couple(a, b) { return a + b } }\n  return -1\n}", "go"));

    // ── single-binding view: multi-payload ──
    [Fact] public void View_MultiPayload_FieldA() =>
        Assert.Equal(10, Run(Pairs + "func go() -> int {\n  let p = Pair2.couple(10, 20)\n  match (p: Pair2) { .single(x) { return x } .couple(c) { return c.a } }\n  return -1\n}", "go"));

    [Fact] public void View_MultiPayload_FieldB() =>
        Assert.Equal(20, Run(Pairs + "func go() -> int {\n  let p = Pair2.couple(10, 20)\n  match (p: Pair2) { .single(x) { return x } .couple(c) { return c.b } }\n  return -1\n}", "go"));

    [Fact] public void View_MultiPayload_BothFields() =>
        Assert.Equal(30, Run(Pairs + "func go() -> int {\n  let p = Pair2.couple(10, 20)\n  match (p: Pair2) { .single(x) { return x } .couple(c) { return c.a + c.b } }\n  return -1\n}", "go"));

    // ── single-binding view: single-payload is transparent ──
    [Fact] public void View_SinglePayload_FieldAccess() =>
        Assert.Equal(9, Run(Pairs + "func go() -> int {\n  let p = Pair2.single(9)\n  match (p: Pair2) { .single(s) { return s.x } .couple(c) { return 0 } }\n  return -1\n}", "go"));

    [Fact] public void View_SinglePayload_BareUse() =>
        Assert.Equal(9, Run(Pairs + "func go() -> int {\n  let p = Pair2.single(9)\n  match (p: Pair2) { .single(s) { return s } .couple(c) { return 0 } }\n  return -1\n}", "go"));

    // ── view payload is itself a choice; nested match on the projection ──
    const string LexDecl = Token + "union Lex { one(t: Token) pair(a: Token, b: Token) }\n";

    [Fact] public void NestedView_OneThenInnerMatch() =>
        Assert.Equal("num:7", Run(LexDecl + "func desc(tok: Token) -> string {\n  match (tok: Token) { .word(w) { return \"w:{w.text}\" } .number(n) { return \"num:{n.value}\" } }\n  return \"?\"\n}\nfunc go() -> string {\n  let lx = Lex.one(Token.number(7))\n  match (lx: Lex) { .one(o) { return desc(o.t) } .pair(p) { return \"pair\" } }\n  return \"?\"\n}", "go"));

    [Fact] public void NestedView_PairProjectsTwoTokens() =>
        Assert.Equal("hi/9", Run(LexDecl + "func desc(tok: Token) -> string {\n  match (tok: Token) { .word(w) { return w.text } .number(n) { let v = n.value\n    return \"{v}\" } }\n  return \"?\"\n}\nfunc go() -> string {\n  let lx = Lex.pair(Token.word(\"hi\"), Token.number(9))\n  match (lx: Lex) { .one(o) { return \"one\" } .pair(p) { return \"{desc(p.a)}/{desc(p.b)}\" } }\n  return \"?\"\n}", "go"));

    // ── view projection directly in interpolation ──
    [Fact] public void ViewProjection_InInterpolation() =>
        Assert.Equal("x=5,y=6", Run(Pairs + "func go() -> string {\n  let p = Pair2.couple(5, 6)\n  match (p: Pair2) { .single(s) { return \"{s.x}\" } .couple(c) { return \"x={c.a},y={c.b}\" } }\n  return \"?\"\n}", "go"));

    [Fact] public void SinglePayloadView_InInterpolation() =>
        Assert.Equal("v=42", Run(Pairs + "func go() -> string {\n  let p = Pair2.single(42)\n  match (p: Pair2) { .single(s) { return \"v={s.x}\" } .couple(c) { return \"?\" } }\n  return \"?\"\n}", "go"));

    // ── Result whose payload is a choice, projected via view ──
    const string Reply = "union Reply { accepted(id: int) rejected(reason: string) }\n";

    [Fact] public void ResultOfChoice_ViewProjection() =>
        Assert.Equal(14, Run(Reply + "func fetch() -> Result<Reply, string> = ok(Reply.accepted(7))\nfunc go() -> int {\n  let r = fetch()?\n  match (r: Reply) { .accepted(a) { return a.id * 2 } .rejected(x) { return 0 } }\n  return -1\n}", "go"));

    // ── view in match-expression position ──
    [Fact] public void View_InMatchExpression() =>
        Assert.Equal(11, Run(Pairs + "func go() -> int {\n  let p = Pair2.couple(5, 6)\n  return match (p: Pair2) { .single(s) { s.x } .couple(c) { c.a + c.b } }\n}", "go"));

    // ── mixing: one arm positional, sibling arm view in same match is fine ──
    [Fact] public void MixedArms_PositionalAndView() =>
        Assert.Equal(2, Run(Pairs + "func go() -> int {\n  let p = Pair2.single(2)\n  match (p: Pair2) { .single(s) { return s.x } .couple(a, b) { return a + b } }\n  return -1\n}", "go"));

    // ── shadowed view names across nested matches ──
    [Fact] public void NestedView_SameNameShadowing() =>
        Assert.Equal(3, Run(Pairs + "func go() -> int {\n  let outer = Pair2.couple(1, 2)\n  match (outer: Pair2) {\n    .single(c) { return c.x }\n    .couple(c) {\n      let inner = Pair2.single(3)\n      match (inner: Pair2) { .single(c) { return c.x } .couple(c) { return c.a } }\n      return -2\n    }\n  }\n  return -1\n}", "go"));

    // ── word-text projection ──
    [Fact] public void View_StringPayloadProjection() =>
        Assert.Equal("hello", Run(Token + "func go() -> string {\n  let t = Token.word(\"hello\")\n  match (t: Token) { .word(w) { return w.text } .number(n) { return \"n\" } }\n  return \"?\"\n}", "go"));

    [Fact] public void View_SinglePayload_ArithmeticOnProjection() =>
        Assert.Equal(100, Run(Pairs + "func go() -> int {\n  let p = Pair2.single(10)\n  match (p: Pair2) { .single(s) { return s.x * s.x } .couple(c) { return 0 } }\n  return -1\n}", "go"));

    [Fact] public void View_MultiPayload_FieldOrderIndependent() =>
        Assert.Equal(7, Run(Pairs + "func go() -> int {\n  let p = Pair2.couple(3, 4)\n  match (p: Pair2) { .single(x) { return x } .couple(c) { let b = c.b\n    let a = c.a\n    return a + b } }\n  return -1\n}", "go"));

    // ── three-level nested choice matches with views (the original Axis5 shape) ──
    [Fact] public void ThreeLevel_NestedChoiceViews() =>
        Assert.Equal("pair:w:hi,n:9", Run(
            Token +
            "union Lex { one(t: Token) pair(a: Token, b: Token) }\n" +
            "func describe(lx: Lex) -> string {\n" +
            "  match (lx: Lex) {\n" +
            "    .one(o) { match (o.t: Token) { .word(w) { return \"one-word:{w.text}\" } .number(n) { return \"one-num:{n.value}\" } } }\n" +
            "    .pair(p) {\n" +
            "      var first = \"\"\n" +
            "      match (p.a: Token) { .word(w) { first = \"w:{w.text}\" } .number(n) { first = \"n:{n.value}\" } }\n" +
            "      var second = \"\"\n" +
            "      match (p.b: Token) { .word(w) { second = \"w:{w.text}\" } .number(n) { second = \"n:{n.value}\" } }\n" +
            "      return \"pair:{first},{second}\"\n" +
            "    }\n" +
            "  }\n" +
            "  return \"?\"\n" +
            "}\n" +
            "func go() -> string = describe(Lex.pair(Token.word(\"hi\"), Token.number(9)))", "go"));

    [Fact] public void ThreeLevel_OnePath() =>
        Assert.Equal("one-num:7", Run(
            Token +
            "union Lex { one(t: Token) pair(a: Token, b: Token) }\n" +
            "func describe(lx: Lex) -> string {\n" +
            "  match (lx: Lex) {\n" +
            "    .one(o) { match (o.t: Token) { .word(w) { return \"one-word:{w.text}\" } .number(n) { return \"one-num:{n.value}\" } } }\n" +
            "    .pair(p) { return \"pair\" }\n" +
            "  }\n" +
            "  return \"?\"\n" +
            "}\n" +
            "func go() -> string = describe(Lex.one(Token.number(7)))", "go"));

    // ── view with three payloads ──
    const string Triple = "union Triple { only(a: int, b: int, c: int) }\n";

    [Fact] public void View_ThreePayloads() =>
        Assert.Equal(6, Run(Triple + "func go() -> int {\n  let t = Triple.only(1, 2, 3)\n  match (t: Triple) { .only(v) { return v.a + v.b + v.c } }\n  return -1\n}", "go"));

    [Fact] public void Positional_ThreePayloads() =>
        Assert.Equal(6, Run(Triple + "func go() -> int {\n  let t = Triple.only(1, 2, 3)\n  match (t: Triple) { .only(a, b, c) { return a + b + c } }\n  return -1\n}", "go"));

    // ── enum payload-less arms unaffected ──
    [Fact] public void Enum_NoBindingsStillWorks() =>
        Assert.Equal("N", Run("enum Dir { north south }\nfunc go() -> string {\n  let d = Dir.north()\n  match (d: Dir) { .north { return \"N\" } .south { return \"S\" } }\n  return \"?\"\n}", "go"));
}
