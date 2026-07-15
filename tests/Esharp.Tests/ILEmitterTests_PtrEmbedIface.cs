// Style note: E# source below is inline \n-escaped for brevity — do NOT copy this in new test files; prefer readable """ raw-string blocks (these tests double as the E# corpus).
namespace Esharp.Tests;

/// Interface satisfaction through embedding: an outer type promotes the inner
/// type's methods and satisfies the interface via forwarders. Pointer embedding
/// (`*Inner`) must deref the `__Ptr_Inner` wrapper for value-receiver methods;
/// pointer-receiver methods pass the wrapper as the `*Inner` argument.
public sealed class ILEmitterTests_PtrEmbedIface
{
    static object? Run(string body, string method, params object?[] args) =>
        EsHarness.Run("namespace Test\n" + body, method, args);

    const string Ibumper =
        "struct Inner { var n: int }\n" +
        "func (i: *Inner) bump() { i.n += 1 }\n" +
        "func (i: Inner) value() -> int = i.n\n" +
        "struct Outer : IBumper {\n  *Inner\n  label: string\n}\n" +
        "interface IBumper {\n  func bump()\n  func value() -> int\n}\n";

    [Fact] public void PointerEmbed_ValueReceiver_ThroughInterface() =>
        Assert.Equal(42, Run(Ibumper +
            "func twice(b: IBumper) -> int {\n  b.bump()\n  b.bump()\n  return b.value()\n}\n" +
            "func go() -> int {\n  var o: *Outer = new Outer { Inner: new Inner { n: 40 }, label: \"x\" }\n  return twice(o)\n}", "go"));

    [Fact] public void PointerEmbed_ValueReadOnly() =>
        Assert.Equal(40, Run(Ibumper +
            "func read(b: IBumper) -> int = b.value()\n" +
            "func go() -> int {\n  var o: *Outer = new Outer { Inner: new Inner { n: 40 }, label: \"x\" }\n  return read(o)\n}", "go"));

    [Fact] public void PointerEmbed_BumpOnceThenValue() =>
        Assert.Equal(41, Run(Ibumper +
            "func once(b: IBumper) -> int {\n  b.bump()\n  return b.value()\n}\n" +
            "func go() -> int {\n  var o: *Outer = new Outer { Inner: new Inner { n: 40 }, label: \"x\" }\n  return once(o)\n}", "go"));

    [Fact] public void PointerEmbed_PromotedDirectAccess() =>
        Assert.Equal(45, Run(Ibumper +
            "func go() -> int {\n  var o: *Outer = new Outer { Inner: new Inner { n: 45 }, label: \"x\" }\n  return o.value()\n}", "go"));

    [Fact] public void ValueEmbed_PromotedMethodThroughInterface() =>
        Assert.Equal("hi kae", Run(
            "struct Greeter {\n  name: string\n  func describe() -> string { return \"hi {self.name}\" }\n}\n" +
            "struct Wrapped : IDescribable {\n  Greeter\n  tag: int\n}\n" +
            "interface IDescribable { func describe() -> string }\n" +
            "func report(d: IDescribable) -> string = d.describe()\n" +
            "func go() -> string {\n  let w = Wrapped { name: \"kae\", tag: 1 }\n  return report(w)\n}", "go"));

    [Fact] public void ValueEmbed_PromotedFieldAccess() =>
        Assert.Equal(15, Run(
            "struct Vec2 {\n  var x: int\n  var y: int\n}\n" +
            "struct Transform {\n  Vec2\n  var scale: int\n}\n" +
            "func go() -> int {\n  var t = Transform { x: 10, y: 5, scale: 1 }\n  return t.x + t.y\n}", "go"));

    [Fact] public void PointerEmbed_PromotedFieldThroughDeref() =>
        Assert.Equal(15, Run(
            "struct Vec2 {\n  var x: int\n  var y: int\n}\n" +
            "struct Entity {\n  *Vec2\n  name: string\n}\n" +
            "func go() -> int {\n  var e: *Entity = new Entity { Vec2: new Vec2 { x: 10, y: 5 }, name: \"p\" }\n  return e.x + e.y\n}", "go"));

    [Fact] public void PointerEmbed_BumpMutatesThroughPointer() =>
        Assert.Equal(50, Run(Ibumper +
            "func go() -> int {\n  var o: *Outer = new Outer { Inner: new Inner { n: 48 }, label: \"x\" }\n  o.bump()\n  o.bump()\n  return o.value()\n}", "go"));

    [Fact] public void ValueEmbed_BareLabelStillAccessible() =>
        Assert.Equal("p", Run(
            "struct Vec2 {\n  var x: int\n  var y: int\n}\n" +
            "struct Entity {\n  *Vec2\n  name: string\n}\n" +
            "func go() -> string {\n  var e: *Entity = new Entity { Vec2: new Vec2 { x: 1, y: 2 }, name: \"p\" }\n  return e.name\n}", "go"));

    [Fact] public void PointerEmbed_TwiceThenInterpolate() =>
        Assert.Equal("n=42", Run(Ibumper +
            "func twice(b: IBumper) -> int {\n  b.bump()\n  b.bump()\n  return b.value()\n}\n" +
            "func go() -> string {\n  var o: *Outer = new Outer { Inner: new Inner { n: 40 }, label: \"x\" }\n  let r = twice(o)\n  return \"n={r}\"\n}", "go"));
}
