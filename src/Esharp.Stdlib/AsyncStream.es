namespace Esharp.Stdlib

using "System.Threading"
using "System.Threading.Tasks"

// ═════════════════════════════════════════════════════════════════════════════
// AsyncStream — backs E# async streams. A `func f() -> IAsyncEnumerable<T>` whose body
// uses `yield` lowers (AsyncStreamDesugar) to `AsyncStream.Create<T>((w, ct) => producer)`,
// with each `yield e` rewritten to `await AsyncStream.Write<T>(w, e, ct)`. The producer
// writes to a bounded `Chan<T>(1)`; the returned IAsyncEnumerable drains it.
//
// `Create` is resolved from the E# standard library by name. It is the lowering *target*
// of `yield`/`await for`, so it cannot use them
// to define itself — the enumerator is hand-rolled below over the channel's own async
// enumerator, adding the three guarantees a naive drain lacks:
//   • lazy: the producer starts when enumeration first begins (GetAsyncEnumerator), not at Create;
//   • producer-fault propagation: a fault surfaces to the consumer after the buffered items drain;
//   • early-dispose cancellation: a consumer that stops early cancels the producer so a blocked
//     send unwinds — no leak.
// ═════════════════════════════════════════════════════════════════════════════

pub static AsyncStream {
    // Static dispatch shim for an async channel send (mirrors ChanOps): the desugared
    // producer emits `await AsyncStream.Write(w, value, ct)`. Returns a Task.
    pub func Write<T>(ch: Chan<T>, value: T, ct: CancellationToken) -> Task =
        ch.SendAsync(value, ct).AsTask()

    pub func Create<T>(producer: Func<Chan<T>, CancellationToken, Task>, ct: CancellationToken = default) -> IAsyncEnumerable<T> =
        AsyncStreamSource<T>(producer, ct)
}

// The IAsyncEnumerable the stream returns. Lazy: each enumeration creates its own
// channel, linked cancellation, and producer task, so the source may be enumerated
// more than once.
pub class AsyncStreamSource<T> : IAsyncEnumerable<T> {
    producer: Func<Chan<T>, CancellationToken, Task>
    outerToken: CancellationToken

    init(producer: Func<Chan<T>, CancellationToken, Task>, outerToken: CancellationToken) {
        self.producer = producer
        self.outerToken = outerToken
    }

    pub func GetAsyncEnumerator(ct: CancellationToken = default) -> IAsyncEnumerator<T> {
        let cts = CancellationTokenSource.CreateLinkedTokenSource(self.outerToken, ct)
        let ch = Chan<T>(1)                         // bounded capacity 1 → backpressure
        let prod = runProducer<T>(self.producer, ch, cts.Token)
        return AsyncStreamEnumerator<T>(ch.GetAsyncEnumerator(cts.Token), prod, cts)
    }
}

// Start the producer on the thread pool. The `defer` completes the channel however the
// producer body exits (normal or fault) so the consumer's drain terminates; a fault rides
// the returned Task and is observed by the enumerator after the buffer drains.
// NOTE: ideally `Task.Run(func() -> Task { … })` to offload the producer's synchronous
// prefix to the thread pool, but async function literals are not yet emitted (a Phase B
// compiler item). A named async function overlaps correctly via its state machine — the
// bounded(1) channel makes the producer run at most one element ahead either way — so this
// is faithful for the stream; it just doesn't pool-offload the pre-first-await prefix.
func runProducer<T>(producer: Func<Chan<T>, CancellationToken, Task>, ch: Chan<T>, token: CancellationToken) -> Task {
    defer { ch.Complete() }
    await producer.Invoke(ch, token)
}

// Drains the channel's async enumerator, observing the producer task on completion to
// surface its fault, and cancelling it on disposal so an early-break consumer never leaks
// a blocked producer.
pub class AsyncStreamEnumerator<T> : IAsyncEnumerator<T> {
    inner: IAsyncEnumerator<T>
    producer: Task
    cts: CancellationTokenSource
    var observed: bool = false

    init(inner: IAsyncEnumerator<T>, producer: Task, cts: CancellationTokenSource) {
        self.inner = inner
        self.producer = producer
        self.cts = cts
    }

    pub let Current: T => self.inner.Current

    pub func MoveNextAsync() -> ValueTask<bool> {
        let has = await self.inner.MoveNextAsync()
        if not has and not self.observed {
            // Drained normally — surface any producer fault.
            self.observed = true
            await self.producer
        }
        return has
    }

    pub func DisposeAsync() -> ValueTask {
        // Consumer stopped (drained, broke early, or cancelled): unwind the producer and
        // observe it so its exception is never left unobserved.
        self.cts.Cancel()
        await self.inner.DisposeAsync()
        if not self.observed {
            self.observed = true
            try {
                await self.producer
            } catch {
                // cancellation / already-surfaced fault
            }
        }
        self.cts.Dispose()
    }
}
