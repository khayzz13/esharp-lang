using Esharp.BoundTree;
using Esharp.Symbols;
using Esharp.Syntax;

// Moved to Esharp.Binder (Pillar 2 — B2) per module-map.md.
// [Δ] NarrowingAnalyzer now FEEDS the flow-analysis null-state lattice (B3):
//     the per-site Narrow records (path, type, callSensitive, stable) produced
//     by Extract() are forwarded to an INarrowingFactsSink — implemented by
//     FlowAnalysis.NullStateFlow — so the null-state lattice refines T? → T at
//     every is/!= nil site without re-walking the bound tree. The sink is injected
//     into BindContext so binders call Narrowing.Extract() exactly once and the
//     result fans out to both the local binder scope and the lattice.
namespace Esharp.Binder;

/// Sink for narrowing facts produced by NarrowingAnalyzer.Extract(). FlowAnalysis.NullStateFlow
/// implements this to receive the per-site Narrow records and update the null-state lattice
/// without re-walking the bound tree. Inject via BindContext.NarrowingSink.
/// A null implementation (NullNarrowingFactsSink) is used when no flow analysis is running.
internal interface INarrowingFactsSink
{
    /// Called after Extract() returns, before the narrows are applied to the binder scope.
    /// `whenTrue` / `whenFalse` are the Narrow lists for the condition's two regions.
    void OnNarrowsExtracted(
        IReadOnlyList<NarrowingAnalyzer.Narrow> whenTrue,
        IReadOnlyList<NarrowingAnalyzer.Narrow> whenFalse,
        SourceSpan conditionSpan);
}

/// No-op narrowing sink used when flow analysis is absent (the default). Allocation-free.
internal sealed class NullNarrowingFactsSink : INarrowingFactsSink
{
    public static readonly NullNarrowingFactsSink Instance = new();
    private NullNarrowingFactsSink() { }
    public void OnNarrowsExtracted(
        IReadOnlyList<NarrowingAnalyzer.Narrow> whenTrue,
        IReadOnlyList<NarrowingAnalyzer.Narrow> whenFalse,
        SourceSpan conditionSpan) { }
}

/// The flow-narrowing fact extractor (§type-narrowing-and-downcasting). One engine,
/// both axes: a runtime fact about a binding — "is a `MethodSym`" (the type axis) or
/// "is not nil" (the null axis) — refines the binding's static type inside the region
/// where the fact holds. This unit reads a bound condition and reports the narrowings
/// implied when it is true and when it is false; the statement/expression binders apply
/// them over the matching region.
///
/// Soundness is by stability: only a `let` local, a parameter, or a `let`-field chain
/// with no intervening call narrows. A `var` (reassignable) and a call-crossed field
/// path are reported as *poison* — the binder records the would-be narrow so a reliant
/// member access is diagnosed (ES2173) rather than silently unsound.
internal sealed class NarrowingAnalyzer : BinderUnit
{
    internal NarrowingAnalyzer(Binder binder) : base(binder) { }

    /// One narrowing to apply: the access path, the type it narrows to, whether it is a
    /// call-sensitive field path, and whether it is `Stable` (false → a poison hint).
    internal readonly record struct Narrow(string Path, BoundType Type, bool CallSensitive, bool Stable);

    /// The narrowings a condition implies when true (`Positive`) and when false
    /// (`Negative`). Also forwards to the injected INarrowingFactsSink so that
    /// FlowAnalysis.NullStateFlow can update the null-state lattice without a second
    /// tree walk. The sink is injected via BindContext.NarrowingSink (defaults to
    /// NullNarrowingFactsSink when no flow analysis is running).
    internal (List<Narrow> Positive, List<Narrow> Negative) Extract(BoundExpression cond)
    {
        var pos = new List<Narrow>();
        var neg = new List<Narrow>();
        Collect(cond, pos, neg);
        // [Δ] Feed the flow-analysis null-state lattice sink.
        Ctx.NarrowingSink.OnNarrowsExtracted(pos, neg, cond.Span);
        return (pos, neg);
    }

    void Collect(BoundExpression cond, List<Narrow> whenTrue, List<Narrow> whenFalse)
    {
        switch (cond)
        {
            // `x is T` narrows x:T when true; `x is not T` narrows x:T when FALSE
            // (the open-world complement of one type is not itself a type).
            case BoundTypeTestExpression tt when NarrowFor(tt.Operand, tt.TargetType) is { } n:
                (tt.Negated ? whenFalse : whenTrue).Add(n);
                break;

            // `a && b` — true region carries both sides' positive facts; the false
            // region is `!a || !b`, from which nothing narrows.
            case BoundBinaryExpression { Op: SyntaxTokenKind.AmpAmp or SyntaxTokenKind.AndKeyword } b:
            {
                var scratch = new List<Narrow>();
                Collect(b.Left, whenTrue, scratch);
                Collect(b.Right, whenTrue, scratch);
                break;
            }

            // `x != nil` narrows x to non-nil when true; `x == nil` narrows it when false.
            case BoundBinaryExpression { Op: SyntaxTokenKind.BangEquals } b when NonNilOperand(b) is { } op1:
                AddNonNil(op1, whenTrue);
                break;
            case BoundBinaryExpression { Op: SyntaxTokenKind.EqualsEquals } b when NonNilOperand(b) is { } op2:
                AddNonNil(op2, whenFalse);
                break;

            // `!cond` swaps the regions.
            case BoundUnaryExpression { Op: SyntaxTokenKind.Bang or SyntaxTokenKind.NotKeyword } u:
                Collect(u.Operand, whenFalse, whenTrue);
                break;
        }
    }

    // The non-nil side of a `== nil` / `!= nil` comparison (the operand that is not the
    // `nil` literal), or null if neither side is `nil`.
    static BoundExpression? NonNilOperand(BoundBinaryExpression b) =>
        IsNil(b.Right) ? b.Left : IsNil(b.Left) ? b.Right : null;

    static bool IsNil(BoundExpression e) => e is BoundLiteralExpression { Value: null } || e.Type is NullType;

    Narrow? NarrowFor(BoundExpression operand, BoundType target)
    {
        var (path, callSensitive, stable) = ClassifyPath(operand);
        return path is null ? null : new Narrow(path, target, callSensitive, stable);
    }

    void AddNonNil(BoundExpression operand, List<Narrow> into)
    {
        // Only a value-type `T?` changes type on non-nil (→ T). A reference type is
        // already its own non-nil form, so there is no type to refine.
        if (operand.Type is not NullableType nt) return;
        var (path, callSensitive, stable) = ClassifyPath(operand);
        if (path is not null) into.Add(new Narrow(path, nt.Inner, callSensitive, stable));
    }

    // The access path of an operand, plus its call-sensitivity and stability:
    //   - a `let`/param name  → stable, not call-sensitive
    //   - a `var` name        → unstable (poison)
    //   - `recv.field`        → call-sensitive; stable iff recv is stable AND field is `let`
    //
    // [Δ] BoundNarrowedExpression (phantom) → BoundConversion(Narrow) (frozen contract §3).
    //     BoundParenthesizedExpression is dropped — binder unwraps in-place.
    (string? Path, bool CallSensitive, bool Stable) ClassifyPath(BoundExpression e) => e switch
    {
        // A Narrow conversion wraps a name: the narrowed type is active; classify
        // the inner operand so the path still maps to the original local name.
        BoundConversion { Kind: ConversionKind.Narrow, Operand: var inner } => ClassifyPath(inner),
        BoundNameExpression name => (name.Name, false, IsStableBinding(name.Name)),
        BoundMemberAccessExpression { Target: BoundNameExpression recv, MemberName: var m }
            => ($"{recv.Name}.{m}", true, IsStableBinding(recv.Name) && IsLetField(recv.Type, m)),
        _ => (null, false, false),
    };

    // A `let` or a parameter is stable; a `var` is not. Parameters are declared mutable
    // but are not reassigned in idiomatic E#, and the spec narrows them freely.
    bool IsStableBinding(string name) =>
        Scope.LookupLocal(name) is { } sym ? sym.IsParameter || !sym.Mutable : false;

    bool IsLetField(BoundType recvType, string member)
    {
        var dt = recvType is HeapPointerBoundType hp ? hp.Inner as DataType : recvType as DataType;
        if (dt is null) return false;
        for (var sym = SymbolOf(dt); sym is not null; sym = sym.BaseType)
            if (sym.Fields.FirstOrDefault(f => f.Name == member) is { } f)
                return !f.Mutable;
        return false;
    }
}
