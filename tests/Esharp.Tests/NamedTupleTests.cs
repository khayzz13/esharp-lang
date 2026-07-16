namespace Esharp.Tests;

/// Named tuple elements (proposal P7) — `(name: T, other: U)` labels an element, and
/// `t.name` reads it, resolving to the underlying `.ItemN` field. Names are metadata; a
/// labeled tuple is the same `ValueTuple<…>` as its unlabeled twin.
public sealed class NamedTupleTests
{
    [Fact]
    public void NamedAccess_ReadsByLabel() => Assert.Equal(34, EsHarness.Run("""
namespace Test
func go() -> int {
    let p: (x: int, y: int) = (3, 4)
    return p.x * 10 + p.y
}
""", "go"));

    // A named tuple return type carries labels onto the binding at the call site.
    [Fact]
    public void NamedReturnType_AccessByLabel() => Assert.Equal(7, EsHarness.Run("""
namespace Test
func mk() -> (lo: int, hi: int) = (2, 9)
func go() -> int {
    let r = mk()
    return r.hi - r.lo
}
""", "go"));

    // `.ItemN` still works alongside labels — the label is sugar over the positional field.
    [Fact]
    public void PositionalAccess_StillWorks() => Assert.Equal(34, EsHarness.Run("""
namespace Test
func go() -> int {
    let p: (x: int, y: int) = (3, 4)
    return p.Item1 * 10 + p.Item2
}
""", "go"));

    // Mixed labeled and bare elements: label the ones that read better, leave the rest.
    [Fact]
    public void MixedLabels_ByLabelAndOrdinal() => Assert.Equal(15, EsHarness.Run("""
namespace Test
func go() -> int {
    let p: (count: int, int) = (5, 3)
    return p.count * p.Item2
}
""", "go"));
}
