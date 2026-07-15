namespace Esharp.BoundTree;

// [CORE] — both pointer nodes survive lowering and reach CodeGen directly.

/// &varName / &s.field — address-of a variable, yielding a managed pointer (*T).
public sealed partial record BoundAddressOfVariableExpression(
    BoundExpression Target, BoundType PointeeType)
    : BoundExpression(new ByRefBoundType(PointeeType))
{
    // True only for `&property` where the property declared a scoped `mut` body.
    // Such a value is valid solely as a direct, non-escaping borrow argument; the
    // ScopedMutLowering pass replaces that call site with setup/try/finally/resume.
    public bool IsScopedPropertyBorrow { get; init; }

    // A property may deliberately declare both scoped `mut` and durable `loca`.
    // Direct borrowing prefers the scoped protocol; any non-call use is rewritten
    // to the durable protocol instead of being rejected as a scoped escape.
    public bool HasDurablePropertyFallback { get; init; }
}

/// &T{...} / &varName (escaping) — heap-allocate a value type into a *T wrapper.
/// The Inner expression produces a value of type T; the result is a HeapPointerBoundType(T).
public sealed partial record BoundHeapAllocExpression(
    BoundExpression Inner, BoundType PointeeType)
    : BoundExpression(new HeapPointerBoundType(PointeeType));
