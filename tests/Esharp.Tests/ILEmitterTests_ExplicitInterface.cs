namespace Esharp.Tests;

/// Phase 3 — explicit / per-interface member implementation. Two interfaces with a
/// same-named method are routed to distinct slots via `func IFace.method(...)`, emitted
/// as private/final/virtual/newslot with a single MethodImpl override. This is what
/// `Chan<T>`'s dual `GetEnumerator` (generic `IEnumerable<T>` + non-generic `IEnumerable`)
/// needs.
public sealed class ILEmitterTests_ExplicitInterface
{
    // Same method name on two interfaces, different return types — routed explicitly.
    // Each interface's slot is invoked via reflection on the emitted assembly.
    [Fact]
    public void ExplicitMembers_TwoInterfaces_SameName()
    {
        var asm = EsHarness.Compile("""
namespace Test
interface IReadInt { func read() -> int }
interface IReadStr { func read() -> string }
class Dual : IReadInt, IReadStr {
    func IReadInt.read() -> int = 7
    func IReadStr.read() -> string = "hi"
}
func make() -> Dual = Dual()
""");
        var dual = EsHarness.Invoke(asm, "make")!;
        var iReadInt = asm.GetType("Test.IReadInt")!;
        var iReadStr = asm.GetType("Test.IReadStr")!;
        Assert.Equal(7, iReadInt.GetMethod("read")!.Invoke(dual, null));
        Assert.Equal("hi", iReadStr.GetMethod("read")!.Invoke(dual, null));
    }
}
