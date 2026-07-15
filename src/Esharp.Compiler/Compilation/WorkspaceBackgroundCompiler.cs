using Esharp.Diagnostics;

namespace Esharp.Compilation;

/// Drives the <see cref="CompilationTracker"/> lifecycle on behalf of a
/// <see cref="Workspace"/>: listens to <see cref="Workspace.Changed"/>, forks
/// the tracker on each edit, and fires a background task that runs the full
/// bind → lower pipeline and calls <see cref="CompilationTracker.MarkFinal"/>
/// (or <see cref="CompilationTracker.MarkFaulted"/>) when the build completes.
///
/// <para>
/// The <see cref="CompilationTracker"/> is the state machine; this class is
/// the actor that drives it. The separation means tests can advance the tracker
/// state directly without needing a live worker, while production code always
/// goes through the background task.
/// </para>
///
/// <para>
/// <strong>Coalescing:</strong> rapid edits (e.g. key-per-keystroke in the LSP)
/// produce one background task per edit. Each task checks the tracker's current
/// generation at completion — if a newer edit arrived while it was building, it
/// discards its result (via <see cref="CompilationTracker.MarkFinal"/>'s
/// generation-staleness guard) and exits without writing the snapshot. The newest
/// in-flight task always wins.
/// </para>
///
/// <para>
/// <strong>Lifetime:</strong> create one per <see cref="Workspace"/> and dispose
/// it when the workspace closes. <see cref="Dispose"/> drains the current build
/// task without blocking the caller past the drain (the task will notice the
/// cancellation token and exit early if possible).
/// </para>
public sealed class WorkspaceBackgroundCompiler : IDisposable
{
    readonly Workspace _workspace;
    readonly CompilationSnapshot _snapshot;
    readonly CancellationTokenSource _lifetimeCts = new();

    // Channel-of-one: only the newest pending build matters. _pendingRebuild and
    // _workerRunning are BOTH guarded by _workerLock — the invariant that makes
    // coalescing lossless is that "set the pending flag" and "decide whether a
    // worker is running / should exit" happen atomically under the same lock. An
    // edit landing in the window between the worker's last flag-check and its
    // return must never be dropped.
    bool _pendingRebuild;        // guarded by _workerLock
    bool _workerRunning;         // guarded by _workerLock
    Task _workerTask;
    readonly object _workerLock = new();

    public WorkspaceBackgroundCompiler(Workspace workspace, CompilationSnapshot snapshot)
    {
        _workspace = workspace;
        _snapshot = snapshot;
        workspace.Changed += OnWorkspaceChanged;
        _workerTask = Task.CompletedTask;
    }

    // ── WorkspaceChanged → ForkOnEdit + enqueue ────────────────────────────

    void OnWorkspaceChanged(object? sender, WorkspaceChangedEventArgs e)
    {
        // Fork the tracker so in-flight readers keep a consistent view and the new
        // generation gates staleness in MarkFinal. The worker reads the freshest
        // (generation, compilation) pair itself, so we don't pass them in.
        _snapshot.Tracker.ForkOnEdit(_workspace);
        EnqueueRebuild();
    }

    void EnqueueRebuild()
    {
        // Set the pending flag and decide whether to spawn a worker ATOMICALLY.
        // If a worker is already running it will observe _pendingRebuild on its
        // next exit-check (also under this lock) and loop instead of exiting, so
        // the edit can never slip through the gap between check and return.
        lock (_workerLock)
        {
            _pendingRebuild = true;
            if (_workerRunning) return;
            _workerRunning = true;
            // RunBuildAsync runs synchronously up to its first await; its opening
            // lock(_workerLock) re-enters on this thread (Monitor is reentrant).
            _workerTask = RunBuildAsync(_lifetimeCts.Token);
        }
    }

    // ── Background build loop ──────────────────────────────────────────────

    async Task RunBuildAsync(CancellationToken ct)
    {
        while (true)
        {
            // Claim the current work item under the lock: clearing the pending flag
            // and snapshotting the (generation, compilation) pair must be atomic, or
            // an edit could pair a stale compilation with a newer generation.
            int generation;
            Compilation compilation;
            lock (_workerLock)
            {
                _pendingRebuild = false;
                generation = _snapshot.Tracker.Generation;
                compilation = _workspace.CurrentCompilation;
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                // Bind + lower off the calling thread. GetDiagnostics drives the full
                // parse → bind → lower pipeline; Build() is idempotent once built.
                var diags = await Task.Run(() => compilation.GetDiagnostics(), ct);

                // MarkFinal is a no-op if a newer generation has superseded us.
                if (_snapshot.Tracker.MarkFinal(generation, compilation))
                    _snapshot.UpdateDiagnostics(diags.ToList());
            }
            catch (OperationCanceledException)
            {
                lock (_workerLock) { _workerRunning = false; }
                throw;
            }
            catch (Exception ex)
            {
                _snapshot.Tracker.MarkFaulted(generation, ex);
            }

            // Exit decision, atomic with the pending flag: if an edit arrived while
            // we built we loop; otherwise we clear _workerRunning under the lock, so
            // a concurrent EnqueueRebuild either sees us still running (and we loop)
            // or sees us stopped (and starts a fresh worker). No lost wakeup.
            lock (_workerLock)
            {
                if (!_pendingRebuild)
                {
                    _workerRunning = false;
                    return;
                }
            }
            // pending — loop to rebuild the newest snapshot
        }
    }

    // ── Public query surface ───────────────────────────────────────────────

    /// The current state of the background compilation. Useful for LSP
    /// status-bar updates ("building…" / "ready" / "error").
    public CompilationState State => _snapshot.Tracker.State;

    /// Wait for the currently-in-progress build to finish, then return the
    /// final <see cref="Compilation"/> snapshot. Returns null if no compilation
    /// has been started or if the build faulted and left no snapshot.
    public Task<Compilation?> WaitForFinalAsync(CancellationToken cancellationToken = default)
        => _snapshot.Tracker.WaitForFinalAsync(cancellationToken);

    /// Trigger a synchronous (foreground) build for the current workspace state.
    /// Use this in CLI / batch contexts where a background worker is overkill.
    /// The returned compilation is in Final state.
    public Compilation BuildNow()
    {
        var current = _workspace.CurrentCompilation;
        var gen = _snapshot.Tracker.ForkOnEdit(_workspace);
        // Build synchronously on the calling thread.
        var diags = current.GetDiagnostics();
        if (_snapshot.Tracker.MarkFinal(gen, current))
            _snapshot.UpdateDiagnostics(diags.ToList());
        return current;
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.Changed -= OnWorkspaceChanged;
        _lifetimeCts.Cancel();
        // Drain the current worker task without blocking indefinitely.
        // We give it 500ms to exit on the cancellation; after that we abandon.
        try { _workerTask.Wait(millisecondsTimeout: 500); } catch { /* abandoned */ }
        _lifetimeCts.Dispose();
    }
}
