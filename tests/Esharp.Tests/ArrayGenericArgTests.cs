using System.Collections.Generic;

namespace Esharp.Tests;

/// An array used as a generic type ARGUMENT (`Dictionary<string, byte[]>`,
/// `List<int[]>`). ResolveBoundTypeToRuntime must map an ArrayBoundType element
/// to `el.MakeArrayType()`; before the fix it erased to `object`, so a
/// `TryGetValue(k, out var v)` slot and the element of `List<int[]>` came back
/// object-typed and `.Length` / indexing / `for` over them failed. Surfaced by
/// ModelFile's `Dictionary<string, byte[]>` metadata map.
public sealed class ArrayGenericArgTests
{
    // The `out var` slot of a Dictionary<string, byte[]>.TryGetValue types as byte[],
    // so `.Length` binds.
    [Fact]
    public void DictByteArray_TryGetValue_OutVarTypedAsArray()
    {
        var d = new Dictionary<string, byte[]> { ["k"] = new byte[] { 1, 2, 3 } };
        var got = EsHarness.Run("""
namespace Test
using "System.Collections.Generic"
func go(d: Dictionary<string, byte[]>) -> int {
    if d.TryGetValue("k", out var v) {
        return v.Length
    }
    return -1
}
""", "go", d);
        Assert.Equal(3, got);
    }

    // The retrieved array is indexable at its element type.
    [Fact]
    public void DictByteArray_OutVar_IsIndexable()
    {
        var d = new Dictionary<string, byte[]> { ["k"] = new byte[] { 10, 20, 30 } };
        var got = EsHarness.Run("""
namespace Test
using "System.Collections.Generic"
func go(d: Dictionary<string, byte[]>) -> int {
    if d.TryGetValue("k", out var v) {
        return int(v[1])
    }
    return -1
}
""", "go", d);
        Assert.Equal(20, got);
    }

    // The retrieved int[] iterates with its element typed as int.
    [Fact]
    public void DictIntArray_OutVar_IteratesElements()
    {
        var d = new Dictionary<string, int[]> { ["k"] = new[] { 2, 3, 4 } };
        var got = EsHarness.Run("""
namespace Test
using "System.Collections.Generic"
func go(d: Dictionary<string, int[]>) -> int {
    if d.TryGetValue("k", out var v) {
        var s = 0
        for x in v { s += x }
        return s
    }
    return -1
}
""", "go", d);
        Assert.Equal(9, got);
    }

    // An array element of a List<int[]> types as int[], so `.Length` binds.
    [Fact]
    public void ListOfIntArray_ElementTypedAsArray()
    {
        var xs = new List<int[]> { new[] { 1, 2 }, new[] { 3, 4, 5 } };
        var got = EsHarness.Run("""
namespace Test
using "System.Collections.Generic"
func go(xs: List<int[]>) -> int = xs[1].Length
""", "go", xs);
        Assert.Equal(3, got);
    }
}
