namespace Esharp.Stdlib

using "System.Threading"
using "System.Threading.Tasks"
using "System.Runtime.CompilerServices"

// ═════════════════════════════════════════════════════════════════════════════
// Spawned — the handle `spawn { }` and `task func` lower to, replacing the legacy
// the retired runtime task wrapper. A VALUE handle (struct) over a running Task and its
// CancellationTokenSource: `Wait`/`Join` surface the body's result and any fault, `Cancel`
// trips cooperative cancellation, and the handle is awaitable (it forwards the task's
// awaiter). A struct may hold reference-typed fields — ES2002 forbids only by-value
// self-containment — so the handle is cheap to pass by value and copies share the one task.
//
// The compiler emits the spawn lowering — a fresh linked CTS, `Task.Run` over the body,
// and a `Spawned { task, cts }` composite literal — so the fields are `pub` for that
// cross-assembly construction (the same reason `Result`'s fields are public).
// ═════════════════════════════════════════════════════════════════════════════

pub class Spawned {
    pub task: Task
    pub cts: CancellationTokenSource

    pub func Cancel() { self.cts.Cancel() }
    pub func Wait() { self.task.GetAwaiter().GetResult() }
    pub func Join() { self.Wait() }
    pub func WaitAsync() -> ValueTask = ValueTask(self.task)
    pub func AsTask() -> Task = self.task
    // Await support: `await handle` forwards to the underlying task awaiter.
    pub func GetAwaiter() -> TaskAwaiter = self.task.GetAwaiter()
}

// Typed result handle for `task func name() -> T`. Wait returns the body's T.
pub class Spawned<T> {
    pub task: Task<T>
    pub cts: CancellationTokenSource

    pub func Cancel() { self.cts.Cancel() }
    pub func Wait() -> T {
        let awaiter: TaskAwaiter<T> = self.task.GetAwaiter()
        return awaiter.GetResult()
    }
    pub func Join() -> T = self.Wait()
    pub func WaitAsync() -> ValueTask<T> = ValueTask<T>(self.task)
    pub func AsTask() -> Task<T> = self.task
    // Typed await support preserves the spawned body's result type.
    pub func GetAwaiter() -> TaskAwaiter<T> = self.task.GetAwaiter()
}

// Compiler-facing construction helpers. `static` is E#'s static host shape;
// the compiler lowers spawn bodies into delegates and calls these helpers instead
// of depending on a runtime support assembly.
pub static SpawnedOps {
    pub func Spawn(work: Action) -> Spawned {
        let cts = CancellationTokenSource()
        let task = Task.Run(work, cts.Token)
        return Spawned { task: task, cts: cts }
    }

    pub func Spawn<T>(work: Func<T>) -> Spawned<T> {
        let cts = CancellationTokenSource()
        // Preserve the generic task result through the BCL overload boundary.
        let task: Task<T> = Task.Run<T>(work, cts.Token)
        return Spawned<T> { task: task, cts: cts }
    }

    // Task-function wrappers pass their declared arguments directly to these
    // overloads.  This preserves ordinary function-call syntax while still
    // constructing the no-argument Task.Run closure inside the E# stdlib.
    pub func Spawn<A>(work: Action<A>, a: A) -> Spawned {
        let cts = CancellationTokenSource()
        let task = Task.Run(func() { work.Invoke(a) }, cts.Token)
        return Spawned { task: task, cts: cts }
    }

    pub func Spawn<A, B>(work: Action<A, B>, a: A, b: B) -> Spawned {
        let cts = CancellationTokenSource()
        let task = Task.Run(func() { work.Invoke(a, b) }, cts.Token)
        return Spawned { task: task, cts: cts }
    }

    pub func Spawn<A, R>(work: Func<A, R>, a: A) -> Spawned<R> {
        let cts = CancellationTokenSource()
        let task: Task<R> = Task.Run<R>(func() -> R = work.Invoke(a), cts.Token)
        return Spawned<R> { task: task, cts: cts }
    }

    pub func Spawn<A, B, R>(work: Func<A, B, R>, a: A, b: B) -> Spawned<R> {
        let cts = CancellationTokenSource()
        let task: Task<R> = Task.Run<R>(func() -> R = work.Invoke(a, b), cts.Token)
        return Spawned<R> { task: task, cts: cts }
    }
}
