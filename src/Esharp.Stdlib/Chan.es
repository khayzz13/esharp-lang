namespace Esharp.Stdlib

// ═════════════════════════════════════════════════════════════════════════════
// Chan<T> — the standard library's typed channel, written in E#. Backs the `chan<T>`
// language builtin and the `select { }` statement; the compiler lowers `chan<T>(n)`
// to `Chan<T>(n)` and routes `ch.Send`/`ch.Receive`/etc. through this type (or the
// `ChanOps` static dispatch below when the element type is user-defined), resolved by
// metadata name (`Esharp.Stdlib.Chan\`1`) off the compiled E# standard library.
//
// It is a `class` (identity/reference semantics): a channel is a shared coordination
// object, not a value — copying one would split the producer/consumer halves. Backed by
// System.Threading.Channels.Channel<T>; the member surface mirrors the C# seed byte-for-
// byte so the strangler reflection (ctor, SendAsync/ReceiveAsync, the ChanOps statics)
// binds against either assembly.
// ═════════════════════════════════════════════════════════════════════════════

pub class Chan<T> : IEnumerable<T>, IEnumerable, IAsyncEnumerable<T> {
    c: Channel<T>

    // capacity > 0 → bounded (backpressure); otherwise unbounded.
    init(capacity: int = 0) {
        if capacity > 0 {
            self.c = Channel.CreateBounded<T>(capacity)
        } else {
            self.c = Channel.CreateUnbounded<T>()
        }
    }

    // --- async API (primary) -------------------------------------------------

    // Async write. Completes when the value is accepted by the channel.
    pub func SendAsync(value: T, cancellationToken: CancellationToken = default) -> ValueTask =
        self.c.Writer.WriteAsync(value, cancellationToken)

    // Async read. Completes when a value is available or the channel completes.
    pub func ReceiveAsync(cancellationToken: CancellationToken = default) -> ValueTask<T> =
        self.c.Reader.ReadAsync(cancellationToken)

    // Non-throwing completion probe for compiler-generated async-stream iterators.
    // false means the channel has completed and all buffered values were drained.
    pub func WaitToReadAsync(cancellationToken: CancellationToken = default) -> ValueTask<bool> =
        self.c.Reader.WaitToReadAsync(cancellationToken)

    // --- non-blocking variants ----------------------------------------------

    // Non-blocking write. Returns false if the channel is full or completed.
    pub func TrySend(value: T) -> bool = self.c.Writer.TryWrite(value)

    // Non-blocking read. Returns false if no value is currently available.
    pub func TryReceive(out value: T) -> bool = self.c.Reader.TryRead(out value)

    // --- blocking convenience (safe inside a spawn body) ---------------------

    // Blocking write, for use inside a `spawn { }` body (which runs on its own Task and
    // can safely block its worker). Fast path takes the synchronous write; the slow path
    // waits for capacity via the awaiter-free AsTask().Wait() (no threadpool pin on the
    // awaiter side, matching the seed).
    pub func Send(value: T) {
        if self.c.Writer.TryWrite(value) {
            return
        }
        while true {
            // Keep the BCL contract explicit: WaitToWriteAsync completes with a
            // bool, and its Task bridge carries that result into the blocking path.
            let waitTask: Task<bool> = self.c.Writer.WaitToWriteAsync().AsTask()
            waitTask.Wait()
            if not waitTask.Result {
                return
            }
            if self.c.Writer.TryWrite(value) {
                return
            }
        }
    }

    // Signal that no more values will be sent. Idempotent.
    pub func Complete() {
        self.c.Writer.TryComplete()
    }

    // Legacy alias for Complete — existing source calling `ch.Close()` still compiles.
    pub func Close() {
        self.Complete()
    }

    // --- iteration -----------------------------------------------------------

    // Blocking range enumerator used by `for v in ch`, honoring an external cancellation
    // token so a scope-driven unwind terminates a forgotten-Complete iteration.
    pub func GetEnumerator(cancellationToken: CancellationToken) -> IEnumerator<T> =
        self.c.Reader.ReadAllAsync(cancellationToken).ToBlockingEnumerable(cancellationToken).GetEnumerator()

    pub func GetEnumerator() -> IEnumerator<T> = self.GetEnumerator(CancellationToken.None)

    // Async range enumerator used by `await for`.
    pub func GetAsyncEnumerator(cancellationToken: CancellationToken = default) -> IAsyncEnumerator<T> =
        self.c.Reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken)
}

// ═════════════════════════════════════════════════════════════════════════════
// ChanOps — static generic dispatch helpers mirroring the C# seed's `ChanOps`. The IL
// compiler routes `ch.Send(v)` / `ch.Close()` / etc. through these when the channel's
// element type is user-defined (and so cannot be closed against a runtime Type): Cecil's
// generic-METHOD instantiation handles a Cecil-only type argument cleanly, unlike a
// method reference rooted on a generic-TYPE instance. Names + arities are byte-compatible
// with what the emitter reflects over.
// ═════════════════════════════════════════════════════════════════════════════

pub static ChanOps {
    pub func Send<T>(ch: Chan<T>, value: T) {
        ch.Send(value)
    }
    pub func TrySend<T>(ch: Chan<T>, value: T) -> bool = ch.TrySend(value)
    pub func SendAsync<T>(ch: Chan<T>, value: T, ct: CancellationToken = default) -> ValueTask =
        ch.SendAsync(value, ct)
    pub func WaitToReadAsync<T>(ch: Chan<T>, ct: CancellationToken = default) -> ValueTask<bool> =
        ch.WaitToReadAsync(ct)
    pub func ReceiveAsync<T>(ch: Chan<T>, ct: CancellationToken = default) -> ValueTask<T> =
        ch.ReceiveAsync(ct)
    pub func TryReceive<T>(ch: Chan<T>, out value: T) -> bool = ch.TryReceive(out value)
    pub func Complete<T>(ch: Chan<T>) {
        ch.Complete()
    }
    pub func Close<T>(ch: Chan<T>) {
        ch.Close()
    }
}
