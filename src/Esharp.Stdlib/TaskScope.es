namespace Esharp.Stdlib

using "System.Collections.Concurrent"
using "System.Collections.Generic"
using "System.Threading"
using "System.Threading.Tasks"

// ═════════════════════════════════════════════════════════════════════════════
// TaskScope — the structured-concurrency supervisor. A scope OWNS its child tasks and
// guarantees none outlive it: the scope does not exit until every child completes, a
// child's first fault cancels its siblings and is re-raised on exit, cancellation flows
// both ways, channels opened through the scope auto-complete, and deferred cleanups run
// LIFO. This is the shape that structurally fixes the old task-wrapper bugs — a fault cannot be
// lost, a child cannot leak on cancel, the parent cannot forget to propagate.
//
// The implementation is self-hosted in the E# standard library. Its deferred-cleanup
// store is a lock-free ConcurrentStack, and the parent-cancellation link is a plain
// closure rather than a static-delegate-plus-state-object dance.
// ═════════════════════════════════════════════════════════════════════════════

// `static` is E#'s static-method host.  Keep the public TaskScope.RunAsync
// entry points here, and keep the mutable supervisor as a separate instance type.
// This maps directly to the two roles in structured concurrency: an entry point
// creates a scope, then the scope owns children/resources for the callback.
pub static TaskScope {
    pub func RunAsync(body: Func<TaskScope, Task>) -> Task = RunAsync(CancellationToken.None, body)

    pub func RunAsync(ct: CancellationToken, body: Func<TaskScope, Task>) -> Task {
        let scope = TaskScope(ct)
        var bodyError: Exception? = nil
        try {
            await body.Invoke(scope)
        } catch (e: Exception) {
            bodyError = e
            scope.Cancel()
        }
        await scope.drainAsync(bodyError)
    }

    pub func RunAsync<T>(body: Func<TaskScope, Task<T>>) -> Task<T> {
        let scope = TaskScope(CancellationToken.None)
        var result: T = default
        var bodyError: Exception? = nil
        try {
            result = await body.Invoke(scope)
        } catch (e: Exception) {
            bodyError = e
            scope.Cancel()
        }
        await scope.drainAsync(bodyError)
        return result
    }

    pub func RunAsync<T>(ct: CancellationToken, body: Func<TaskScope, Task<T>>) -> Task<T> {
        let scope = TaskScope(ct)
        var result: T = default
        var bodyError: Exception? = nil
        try {
            result = await body.Invoke(scope)
        } catch (e: Exception) {
            bodyError = e
            scope.Cancel()
        }
        await scope.drainAsync(bodyError)
        return result
    }

}

pub class TaskScope : IAsyncDisposable {
    cts: CancellationTokenSource
    parentLink: CancellationTokenRegistration
    children: ConcurrentBag<Task>
    deferred: ConcurrentStack<Func<ValueTask>>
    var disposed: int = 0

    // The scope's cancellation token — thread into child work and BCL APIs. Computed
    // property forwarding to the linked source (no backing field).
    pub let Token: CancellationToken => self.cts.Token

    init(parent: CancellationToken) {
        // The linked source is the BCL's cancellation-registration primitive:
        // parent cancellation propagates without an E# closure that could outlive
        // the scope, and disposing the scope tears the registration down.
        self.cts = CancellationTokenSource.CreateLinkedTokenSource(parent)
        self.children = ConcurrentBag<Task>()
        self.deferred = ConcurrentStack<Func<ValueTask>>()
        self.parentLink = default(CancellationTokenRegistration)
    }

    // --- child spawning ------------------------------------------------------

    pub func Cancel() { self.cts.Cancel() }

    // Spawn a child under this scope. The supervision wrapper swallows cooperative
    // cancellation but cancels siblings on any real fault, then rethrows so drain
    // observes it. Written as an async lambda offloaded to the thread pool.
    pub func Spawn(work: Func<CancellationToken, Task>) -> Task {
        self.throwIfDisposed()
        let task = Task.Run(func() -> Task = self.runChild(work))
        self.children.Add(task)
        return task
    }

    pub func Spawn<T>(work: Func<CancellationToken, Task<T>>) -> Task<T> {
        self.throwIfDisposed()
        let task: Task<T> = Task.Run<T>(func() -> Task<T> = self.runChild<T>(work))
        self.children.Add(task)
        return task
    }

    // Keep the async supervision logic in ordinary instance methods. The Task.Run
    // lambdas above remain deliberately synchronous factories, so the work is still
    // queued to the pool while the state machine captures a normal receiver/argument
    // set rather than a nested async closure.
    func runChild(work: Func<CancellationToken, Task>) -> Task {
        try {
            await work.Invoke(self.Token)
        } catch (oce: OperationCanceledException) if self.Token.IsCancellationRequested {
            // cooperative cancellation is not a scope error
        } catch {
            self.cts.Cancel()
            throw
        }
    }

    func runChild<T>(work: Func<CancellationToken, Task<T>>) -> Task<T> {
        try {
            return await work.Invoke(self.Token)
        } catch (oce: OperationCanceledException) if self.Token.IsCancellationRequested {
            return default
        } catch {
            self.cts.Cancel()
            throw
        }
    }

    // --- resource ownership --------------------------------------------------

    // A channel auto-completed when the scope exits.
    pub func Chan<T>(capacity: int = 0) -> Chan<T> {
        self.throwIfDisposed()
        let ownedChan = Chan<T>(capacity)
        self.Defer(func() { ownedChan.Complete() })
        return ownedChan
    }

    // Register synchronous cleanup (LIFO).
    pub func Defer(cleanup: Action) {
        self.deferAsync(func() -> ValueTask {
            cleanup.Invoke()
            return ValueTask.CompletedTask
        })
    }

    // Register async cleanup (LIFO).
    pub func deferAsync(cleanup: Func<ValueTask>) {
        self.throwIfDisposed()
        self.deferred.Push(cleanup)
    }

    // --- disposal ------------------------------------------------------------

    func drainAsync(bodyError: Exception?) -> Task {
        if Interlocked.Exchange(&self.disposed, 1) != 0 {
            return
        }

        // Wait for every child; WhenAll rethrows only the first, so we collect each
        // task's fault below rather than trusting the await.
        try {
            await Task.WhenAll(self.children)
        } catch {
            // collected per-task below
        }

        let childErrors = List<Exception>()
        for t in self.children {
            let agg = t.Exception
            if agg != nil {
                for inner in agg.Flatten().InnerExceptions {
                    if inner is OperationCanceledException and self.Token.IsCancellationRequested {
                        continue
                    }
                    childErrors.Add(inner)
                }
            }
        }

        // Deferred cleanups, LIFO. ConcurrentStack pops in push-reverse order already.
        var cleanupError: Exception? = nil
        var action: Func<ValueTask> = nil
        while self.deferred.TryPop(out action) {
            try {
                await action.Invoke()
            } catch (e: Exception) {
                if cleanupError == nil { cleanupError = e }
            }
        }

        self.parentLink.Dispose()
        self.cts.Dispose()

        self.raiseAggregate(bodyError, childErrors, cleanupError)
    }

    // Fold the body fault, the collected child faults, and any cleanup fault into the single
    // exception the scope raises on exit — a lone fault rethrows as itself, several aggregate.
    // A cooperative-cancellation body fault is not an error.
    func raiseAggregate(bodyError: Exception?, childErrors: List<Exception>, cleanupError: Exception?) {
        if bodyError != nil and not (bodyError is OperationCanceledException) {
            if childErrors.Count == 0 and cleanupError == nil {
                throw bodyError
            }
            let all = List<Exception>()
            all.Add(bodyError)
            all.AddRange(childErrors)
            if cleanupError != nil { all.Add(cleanupError) }
            throw AggregateException(all)
        }
        if childErrors.Count == 1 and cleanupError == nil {
            throw childErrors[0]
        }
        if childErrors.Count > 0 {
            let all = List<Exception>(childErrors)
            if cleanupError != nil { all.Add(cleanupError) }
            throw AggregateException(all)
        }
        if cleanupError != nil {
            throw cleanupError
        }
    }

    pub func DisposeAsync() -> ValueTask = ValueTask(self.drainAsync(nil))

    func throwIfDisposed() {
        if Interlocked.CompareExchange(&self.disposed, 0, 0) != 0 {
            throw ObjectDisposedException("TaskScope")
        }
    }
}
