using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Esharp.Tests;

/// Member-call return-type resolution is scoped to the RECEIVER's own type
/// (functions/methods.md — method-only, attached to the receiver type), never a
/// module-wide bare-name function table. Regression cover for the dogfood bug
/// where `stream.Read(span)` bound a same-named E# method's return
/// (`ModelFile.Read -> SectionReader`) instead of `Stream.Read -> int`, storing
/// an int into a SectionReader-typed local and failing ILVerify.
///
/// The proof in every case is behavioral: a wrong return type makes the result
/// flow into a context that either fails to compile/verify or yields the wrong
/// value, so a clean run at the asserted value pins correct per-receiver typing.
public sealed class ReceiverMethodResolutionTests
{
    // ── the flagship: a BCL receiver whose method name collides with a user method ──

    // `struct Reader` declares `Read` returning itself, mirroring ModelFile's
    // `Read -> SectionReader`. `ms.Read(buf)` must still be `Stream.Read -> int`.
    [Fact]
    public void BclStreamRead_TypesInt_NotCollidingUserReader()
    {
        var got = EsHarness.Run("""
namespace Test
using "System.IO"
struct Reader { pos: int }
func (r: Reader) Read(name: string) -> Reader = r
func go(ms: Stream) -> int {
    let buf = byte[](2)
    let n = ms.Read(buf)
    return n
}
""", "go", new MemoryStream(new byte[] { 10, 20, 30 }));
        Assert.Equal(2, got);
    }

    // A zero-arg BCL method (`ReadByte -> int`) colliding with a user `ReadByte`.
    [Fact]
    public void BclReadByte_TypesInt_NotCollidingUserMethod()
    {
        var got = EsHarness.Run("""
namespace Test
using "System.IO"
struct Tag { v: int }
func (t: Tag) ReadByte() -> Tag = t
func go(ms: Stream) -> int = ms.ReadByte()
""", "go", new MemoryStream(new byte[] { 7, 8 }));
        Assert.Equal(7, got);
    }

    // List<int>.IndexOf(int) -> int, colliding with a user `IndexOf -> Marker`.
    [Fact]
    public void BclListIndexOf_TypesInt_NotCollidingUserMarker()
    {
        var got = EsHarness.Run("""
namespace Test
using "System.Collections.Generic"
struct Marker { z: int }
func (m: Marker) IndexOf(v: int) -> Marker = m
func go(xs: List<int>) -> int = xs.IndexOf(9)
""", "go", new List<int> { 1, 9, 4 });
        Assert.Equal(1, got);
    }

    // List<int>.Contains(int) -> bool, colliding with a user `Contains -> Marker`.
    // A wrong (Marker) typing makes `return xs.Contains(..)` a bool/Marker mismatch.
    [Fact]
    public void BclListContains_TypesBool_NotCollidingUserMethod()
    {
        var got = EsHarness.Run("""
namespace Test
using "System.Collections.Generic"
struct Marker { z: int }
func (m: Marker) Contains(v: int) -> Marker = m
func go(xs: List<int>) -> bool = xs.Contains(3)
""", "go", new List<int> { 1, 3, 5 });
        Assert.Equal(true, got);
    }

    // Dictionary<string,int>.ContainsKey(string) -> bool colliding with a user method.
    [Fact]
    public void BclDictContainsKey_TypesBool_NotCollidingUserMethod()
    {
        var d = new Dictionary<string, int> { ["a"] = 1 };
        var got = EsHarness.Run("""
namespace Test
using "System.Collections.Generic"
struct Q { z: int }
func (q: Q) ContainsKey(k: string) -> Q = q
func go(d: Dictionary<string, int>) -> bool = d.ContainsKey("a")
""", "go", d);
        Assert.Equal(true, got);
    }

    // string.IndexOf(string) -> int colliding with a user `IndexOf -> Q`.
    [Fact]
    public void BclStringIndexOf_TypesInt_NotCollidingUserMethod()
    {
        var got = EsHarness.Run("""
namespace Test
struct Q { z: int }
func (q: Q) IndexOf(s: string) -> Q = q
func go(s: string) -> int = s.IndexOf("c")
""", "go", "abcd");
        Assert.Equal(2, got);
    }

    // StringBuilder.Append(string) -> StringBuilder chains; a user `Append -> Marker`
    // must not hijack the first link (which would then verify-fail on the second).
    [Fact]
    public void BclStringBuilderAppend_ChainsAsBuilder_NotUserType()
    {
        var got = EsHarness.Run("""
namespace Test
using "System.Text"
struct Marker { z: int }
func (m: Marker) Append(s: string) -> Marker = m
func go(sb: StringBuilder) -> string {
    sb.Append("x").Append("y")
    return sb.ToString()
}
""", "go", new StringBuilder("s:"));
        Assert.Equal("s:xy", got);
    }

    // ── pure-E# cross-type collision: the receiver, not declaration order, decides ──

    // Two structs each declare `peek` with a DIFFERENT return type. Under the old
    // bare-name table only one `peek` existed; the wrong receiver got the wrong
    // return and `x.av + y` would be `int + A` — a bind error.
    [Fact]
    public void TwoStructs_SameMethodName_ResolvePerReceiver()
    {
        Assert.Equal(16, EsHarness.Run("""
namespace Test
struct A { av: int }
struct B { bv: int }
func (a: A) peek() -> A = a
func (b: B) peek() -> int = b.bv
func go() -> int {
    let a = A { av: 7 }
    let b = B { bv: 9 }
    let x = a.peek()
    let y = b.peek()
    return x.av + y
}
""", "go"));
    }

    // Same collision, declaration order reversed — resolution is by receiver, not
    // by which method registered first.
    [Fact]
    public void DeclarationOrderReversed_StillResolvesPerReceiver()
    {
        Assert.Equal(16, EsHarness.Run("""
namespace Test
struct B { bv: int }
func (b: B) peek() -> int = b.bv
struct A { av: int }
func (a: A) peek() -> A = a
func go() -> int {
    let a = A { av: 7 }
    let b = B { bv: 9 }
    return a.peek().av + b.peek()
}
""", "go"));
    }

    // Distinct return KINDS (string vs int) on the same method name.
    [Fact]
    public void SameName_StringVsInt_ResolvePerReceiver()
    {
        Assert.Equal(5, EsHarness.Run("""
namespace Test
struct A { av: int }
struct B { bv: int }
func (a: A) label() -> string = "a"
func (b: B) label() -> int = b.bv
func go() -> int {
    let a = A { av: 0 }
    let b = B { bv: 4 }
    let s = a.label()
    let n = b.label()
    return s.Length + n
}
""", "go"));
    }

    // A BCL-receiver call and a user-receiver call with the SAME method name in one
    // function body — each resolves against its own receiver.
    [Fact]
    public void BclAndUserCollision_BothResolve_InOneFunction()
    {
        var got = EsHarness.Run("""
namespace Test
using "System.Collections.Generic"
struct Marker { z: int }
func (m: Marker) IndexOf(v: int) -> Marker = m
func go(xs: List<int>) -> int {
    let a = Marker { z: 100 }
    let mk = a.IndexOf(0)
    let idx = xs.IndexOf(9)
    return mk.z + idx
}
""", "go", new List<int> { 1, 9 });
        Assert.Equal(101, got);
    }

    // ── the substitution the branch also performs must survive the rewrite ──

    // `get` on a closed generic receiver returns the closed argument, not the open T.
    [Fact]
    public void GenericReceiverGet_ClosesTypeArg()
    {
        Assert.Equal(8, EsHarness.Run("""
namespace Test
class Box<T> { v: T  init(x: T) { self.v = x }  func get() -> T = self.v }
func go() -> int {
    let b = Box<int>(7)
    return b.get() + 1
}
""", "go"));
    }

    // Nested closed generic: `bb.get()` is `Box<int>`, then `.get()` is `int`.
    [Fact]
    public void NestedGenericReceiverGet_Chains()
    {
        Assert.Equal(6, EsHarness.Run("""
namespace Test
class Box<T> { v: T  init(x: T) { self.v = x }  func get() -> T = self.v }
func go() -> int {
    let inner = Box<int>(5)
    let bb = Box<Box<int>>(inner)
    return bb.get().get() + 1
}
""", "go"));
    }

    // ── inheritance: the method may live on a base class, reached by the chain walk ──

    [Fact]
    public void MethodOnBaseClass_CalledOnDerived_Resolves()
    {
        Assert.Equal(7, EsHarness.Run("""
namespace Test
open class Base { init() {}  func tag() -> int = 7 }
class Derived : Base { init() : base() {} }
func go() -> int {
    let d = Derived()
    return d.tag()
}
""", "go"));
    }

    [Fact]
    public void DerivedOwnAndBaseMethods_BothResolve()
    {
        Assert.Equal(15, EsHarness.Run("""
namespace Test
open class Base { init() {}  func baseV() -> int = 10 }
class Derived : Base { init() : base() {}  func ownV() -> int = 5 }
func go() -> int {
    let d = Derived()
    return d.ownV() + d.baseV()
}
""", "go"));
    }

    // ── receiver kinds: value, pointer, readonly all resolve their return ──

    [Fact]
    public void PointerReceiverMethod_TypesReturn()
    {
        Assert.Equal(6, EsHarness.Run("""
namespace Test
struct Counter { v: int }
func (c: *Counter) bump() -> int { c.v += 1  return c.v }
func go() -> int {
    var c = Counter { v: 5 }
    return c.bump()
}
""", "go"));
    }

    [Fact]
    public void ReadonlyReceiverMethod_TypesReturn()
    {
        Assert.Equal(25, EsHarness.Run("""
namespace Test
struct Vec { x: int  y: int }
readonly func (v: Vec) mag2() -> int = v.x * v.x + v.y * v.y
func go() -> int {
    let v = Vec { x: 3, y: 4 }
    return v.mag2()
}
""", "go"));
    }

    // A self-returning struct method threads a fluent chain, each link re-typed.
    [Fact]
    public void FluentSelfReturningStruct_Chains()
    {
        Assert.Equal(12, EsHarness.Run("""
namespace Test
struct Acc { total: int }
func (a: Acc) add(n: int) -> Acc = Acc { total: a.total + n }
func go() -> int {
    let a = Acc { total: 0 }
    return a.add(3).add(4).add(5).total
}
""", "go"));
    }

    // A same-named method on TWO unrelated types, one returning a value struct whose
    // field is then projected — the receiver picks the right overload's return so the
    // projection binds.
    [Fact]
    public void SameName_StructReturn_ProjectsFieldPerReceiver()
    {
        Assert.Equal(30, EsHarness.Run("""
namespace Test
struct A { av: int }
struct B { bv: int }
struct Pair { lo: int  hi: int }
func (a: A) span() -> Pair = Pair { lo: a.av, hi: a.av * 2 }
func (b: B) span() -> int = b.bv
func go() -> int {
    let a = A { av: 10 }
    let b = B { bv: 10 }
    return a.span().hi + b.span()
}
""", "go"));
    }
}
