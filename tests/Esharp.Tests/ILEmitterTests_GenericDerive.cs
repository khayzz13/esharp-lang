// Style note: E# source below is inline \n-escaped for brevity — do NOT copy this in new test files; prefer readable """ raw-string blocks (these tests double as the E# corpus).
namespace Esharp.Tests;

/// `derive equality` / `derive debug` on generic data types. Every reference to
/// the type inside generated members (param, isinst/unbox, fields, the
/// IEquatable<Self> interface) must target the self-instantiation `T<A,B>`, and
/// call sites must close the generic — otherwise TypeLoad/InvalidProgram/"not
/// fully instantiated".
public sealed class ILEmitterTests_GenericDerive
{
    static object? Run(string body, string method, params object?[] args) =>
        EsHarness.Run("namespace Test\n" + body, method, args);

    const string Pair = "derive equality\nstruct Pair<A, B> {\n  first: A\n  second: B\n}\n";

    [Fact] public void IntPair_Equal() =>
        Assert.Equal(true, Run(Pair + "func go() -> bool {\n  let a = Pair<int, int> { first: 3, second: 4 }\n  let b = Pair<int, int> { first: 3, second: 4 }\n  return a.Equals(b)\n}", "go"));

    [Fact] public void IntPair_NotEqual() =>
        Assert.Equal(false, Run(Pair + "func go() -> bool {\n  let a = Pair<int, int> { first: 3, second: 4 }\n  let b = Pair<int, int> { first: 3, second: 5 }\n  return a.Equals(b)\n}", "go"));

    [Fact] public void StrPair_NotEqual() =>
        Assert.Equal(false, Run(Pair + "func go() -> bool {\n  let a = Pair<string, int> { first: \"x\", second: 1 }\n  let b = Pair<string, int> { first: \"y\", second: 1 }\n  return a.Equals(b)\n}", "go"));

    [Fact] public void StrPair_Equal() =>
        Assert.Equal(true, Run(Pair + "func go() -> bool {\n  let a = Pair<string, int> { first: \"x\", second: 1 }\n  let b = Pair<string, int> { first: \"x\", second: 1 }\n  return a.Equals(b)\n}", "go"));

    [Fact] public void TwoInstantiations_Independent() =>
        Assert.Equal(true, Run(Pair +
            "func ints() -> bool {\n  let a = Pair<int, int> { first: 1, second: 2 }\n  let b = Pair<int, int> { first: 1, second: 2 }\n  return a.Equals(b)\n}\n" +
            "func strs() -> bool {\n  let a = Pair<string, string> { first: \"p\", second: \"q\" }\n  let b = Pair<string, string> { first: \"p\", second: \"z\" }\n  return a.Equals(b)\n}\n" +
            "func go() -> bool = ints() && !strs()", "go"));

    [Fact] public void MixedTypeArgs_Equal() =>
        Assert.Equal(true, Run(Pair + "func go() -> bool {\n  let a = Pair<string, bool> { first: \"y\", second: true }\n  let b = Pair<string, bool> { first: \"y\", second: true }\n  return a.Equals(b)\n}", "go"));

    [Fact] public void GetHashCode_EqualValuesEqualHash() =>
        Assert.Equal(true, Run(Pair + "func go() -> bool {\n  let a = Pair<int, int> { first: 7, second: 8 }\n  let b = Pair<int, int> { first: 7, second: 8 }\n  return a.GetHashCode() == b.GetHashCode()\n}", "go"));

    [Fact] public void ObjectEquals_DelegatesToTyped() =>
        Assert.Equal(true, Run(Pair + "func go() -> bool {\n  let a = Pair<int, int> { first: 1, second: 1 }\n  let b = Pair<int, int> { first: 1, second: 1 }\n  return a.Equals(b)\n}", "go"));

    [Fact] public void DeriveDebug_GenericToString() =>
        Assert.Equal("Pair { first = 3, second = 4 }", Run("derive debug\nstruct Pair<A, B> {\n  first: A\n  second: B\n}\nfunc go() -> string {\n  let p = Pair<int, int> { first: 3, second: 4 }\n  return p.ToString()\n}", "go"));

    [Fact] public void DeriveDebug_GenericStringFields() =>
        Assert.Equal("Pair { first = a, second = b }", Run("derive debug\nstruct Pair<A, B> {\n  first: A\n  second: B\n}\nfunc go() -> string {\n  let p = Pair<string, string> { first: \"a\", second: \"b\" }\n  return p.ToString()\n}", "go"));

    [Fact] public void DeriveBoth_EqualityAndDebug() =>
        Assert.Equal("Box { item = 5 }", Run("derive equality, debug\nstruct Box<T> {\n  item: T\n}\nfunc go() -> string {\n  let a = Box<int> { item: 5 }\n  return a.ToString()\n}", "go"));

    [Fact] public void SingleTypeParam_Equality() =>
        Assert.Equal(true, Run("derive equality\nstruct Box<T> {\n  item: T\n}\nfunc go() -> bool {\n  let a = Box<int> { item: 42 }\n  let b = Box<int> { item: 42 }\n  return a.Equals(b)\n}", "go"));

    [Fact] public void GenericEquals_AcrossFunctionBoundary() =>
        Assert.Equal(true, Run(Pair + "func same(a: Pair<int, int>, b: Pair<int, int>) -> bool = a.Equals(b)\nfunc go() -> bool {\n  let x = Pair<int, int> { first: 9, second: 9 }\n  let y = Pair<int, int> { first: 9, second: 9 }\n  return same(x, y)\n}", "go"));

    [Fact] public void NonGenericDerive_StillWorks() =>
        Assert.Equal(true, Run("derive equality\nstruct Point {\n  x: int\n  y: int\n}\nfunc go() -> bool {\n  let a = Point { x: 1, y: 2 }\n  let b = Point { x: 1, y: 2 }\n  return a.Equals(b)\n}", "go"));

    [Fact] public void GenericDebug_BoolField() =>
        Assert.Equal("Flag { on = True }", Run("derive debug\nstruct Flag<T> {\n  on: T\n}\nfunc go() -> string {\n  let f = Flag<bool> { on: true }\n  return f.ToString()\n}", "go"));

    [Fact] public void GenericEquality_ReturnedFromMatch() =>
        Assert.Equal(true, Run(Pair + "func go() -> bool {\n  let a = Pair<int, int> { first: 2, second: 3 }\n  let b = Pair<int, int> { first: 2, second: 3 }\n  let eq = a.Equals(b)\n  return match eq { true { true } false { false } }\n}", "go"));
}
