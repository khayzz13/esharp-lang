using Esharp.BoundTree;

namespace Esharp.Lowering;

/// <summary>
/// The hand-written base every lowering rewriter derives from, sitting directly over the
/// generated <see cref="BoundTreeRewriter"/> (which supplies total identity descent into every
/// child position). It adds the one piece of shared state a lowering pass needs but the generated
/// base cannot carry: a synthetic-temp allocator.
///
/// <para><b>Base A</b> — a non-hoisting pass (Match, Closure, Concurrency, Event, ForEach, Defer)
/// derives from this directly: it overrides only the FEATURE nodes it transforms, calls
/// <c>base.*</c> for descent, and uses <see cref="FreshTemp"/> for any helper local it introduces.</para>
///
/// <para><b>Base B</b> — a hoisting pass derives from <see cref="SpillingBoundTreeRewriter"/>
/// (which extends this), adding the statement-hoist buffer and the conditional-hoist machinery on
/// top of the same temp allocator.</para>
/// </summary>
public abstract class LoweringRewriter : BoundTreeRewriter
{
    int _tempId;

    /// A fresh synthetic local name. The angle bracket makes it unspellable in E# source, so it
    /// can never collide with a user-declared name; <paramref name="role"/> documents the temp's
    /// purpose in dumped IL.
    protected string FreshTemp(string role) => $"<{role}>__tmp_{_tempId++}";
}
