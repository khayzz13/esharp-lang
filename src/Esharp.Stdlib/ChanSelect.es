namespace Esharp.Stdlib

using "System.Collections.Generic"
using "System.Runtime.CompilerServices"
using "System.Threading"
using "System.Threading.Tasks"

// ═════════════════════════════════════════════════════════════════════════════
// ChanSelect — the lowering target for the `select { }` statement. The compiler emits an
// `Arm[]` from the source arms (`.recv(x, ch) { … }` etc.) and calls `Select`/`SelectAsync`;
// end users never build an `Arm` by hand. Resolved probe-first by metadata name
// (`Esharp.Stdlib.ChanSelect`) off the compiled E# standard library.
//
// `Kind` and `Arm` are NESTED types (the emitter reflects `ChanSelect.Arm` / `ChanSelect.Kind`,
// the parameterless `Arm` ctor, and the public `Kind`/`TryOp`/`BlockingOp`/`Body` fields) —
// byte-compatible with the seed.
//
// Semantics:
//   • Non-blocking pass first, arms checked in a randomized fairness order so none is
//     structurally preferred; the first ready `.recv`/`.send` fires.
//   • `default` is held back on that pass, so a ready op always wins over it; if nothing
//     is ready and a `default` exists, it fires.
//   • Otherwise the blocking pass awaits every arm's BlockingOp via Task.WhenAny; a
//     cancellation sentinel lets the scope token abort the wait. On cancel: return -1.
// ═════════════════════════════════════════════════════════════════════════════

pub static ChanSelect {
    enum Kind { Recv, Send, Timeout, Default }

    // One select arm. The emitter constructs it with the parameterless ctor and sets the
    // public fields directly, so the names/kinds are part of the contract with the backend.
    pub class Arm {
        pub Kind: Kind
        pub TryOp: Func<bool>?         // returns true if the op fired now (recv/send)
        pub BlockingOp: Func<Task>?    // a task that completes when the op can fire
        pub Body: Action
        init() { }
    }

    // Execute a select. Returns the index of the arm that fired, or -1 if no arm could
    // fire and none was `default`.
    pub func Select(arms: Arm[]) -> int = ChanSelect.Select(arms, CancellationToken.None)

    // Cancellation-aware overload — spawn-body lowering threads the supervising scope's
    // token in so a blocked select unwinds when the scope cancels.
    pub func Select(arms: Arm[], cancellationToken: CancellationToken) -> int {
        // Keep each generic receiver closed through the synchronous bridge. This
        // is deliberately blocking (the Select contract), while SelectAsync stays
        // the non-blocking primitive used by async consumers.
        let task: Task<int> = ChanSelect.SelectAsync(arms, cancellationToken).AsTask()
        let awaiter: TaskAwaiter<int> = task.GetAwaiter()
        return awaiter.GetResult()
    }

    pub func SelectAsync(arms: Arm[]) -> ValueTask<int> = ChanSelect.SelectAsync(arms, CancellationToken.None)

    pub func SelectAsync(arms: Arm[], cancellationToken: CancellationToken) -> ValueTask<int> {
        // Fairness shuffle so no arm is structurally preferred.
        var order = int[](arms.Length)
        for i in 0..arms.Length { order[i] = i }
        Random.Shared.Shuffle(order)

        // Non-blocking pass — the first ready non-default arm fires. `default` is held back.
        var defaultIdx = -1
        for i in order {
            if arms[i].Kind == .Default {
                defaultIdx = i
                continue
            }
            let tryOp = arms[i].TryOp
            if tryOp != nil and tryOp.Invoke() {
                let body = arms[i].Body
                body.Invoke()
                return i
            }
        }

        // Nothing ready — take a `default` if present.
        if defaultIdx >= 0 {
            let body = arms[defaultIdx].Body
            body.Invoke()
            return defaultIdx
        }

        // Blocking pass — wait on every BlockingOp.
        let blockingIdx = List<int>()
        let blockingTasks = List<Task>()
        for i in order {
            let blockOp = arms[i].BlockingOp
            if blockOp != nil {
                blockingIdx.Add(i)
            blockingTasks.Add(blockOp.Invoke())
            }
        }
        if blockingTasks.Count == 0 { return -1 }

        // A cancellation sentinel lets WhenAny unwind without an arm firing.
        var cancelSentinel: Task? = nil
        if cancellationToken.CanBeCanceled {
            cancelSentinel = Task.Delay(Timeout.Infinite, cancellationToken)
            blockingTasks.Add(cancelSentinel)
        }

        let winner = await Task.WhenAny(blockingTasks)
        if cancelSentinel != nil and Object.ReferenceEquals(winner, cancelSentinel) {
            return -1
        }
        let winnerIdx = blockingIdx[blockingTasks.IndexOf(winner)]
        let body = arms[winnerIdx].Body
        body.Invoke()
        return winnerIdx
    }
}
