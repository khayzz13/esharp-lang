using System.Collections.Concurrent;
using Esharp.Diagnostics;

namespace Esharp.Compilation;

/// Per-project compilation state machine: None → InProgress → Final.
///
/// The tracker is the incremental-rebind foundation: each <see cref="Workspace"/>
/// edit forks the tracker (via <see cref="ForkOnEdit"/>), so in-flight consumers
/// holding an older <see cref="CompilationSnapshot"/> continue to read a
/// consistent view while the background worker builds the new one.
///
/// States:
/// <list type="bullet">
///   <item><see cref="CompilationState.None"/> — no compilation has been started
///     for this project (fresh workspace, or after a destructive reset).</item>
///   <item><see cref="CompilationState.InProgress"/> — a compilation is being
///     built (bind + lower running in the background). Consumers that need a
///     complete result block or await <see cref="WaitForFinalAsync"/>.</item>
///   <item><see cref="CompilationState.Final"/> — the compilation is complete and
///     its <see cref="Snapshot"/> is immutable and queryable.</item>
/// </list>
///
/// Thread-safety: all state transitions are atomic via <see cref="Interlocked"/>
/// / <see cref="TaskCompletionSource"/>. Multiple readers can call
/// <see cref="Snapshot"/> / <see cref="WaitForFinalAsync"/> concurrently.
public sealed class CompilationTracker
{
    // ── State ─────────────────────────────────────────────────────────────

    volatile CompilationState _state = CompilationState.None;
    Compilation? _snapshot;

    // Completion source for the in-progress → final transition.
    // Re-created on each ForkOnEdit.
    TaskCompletionSource<Compilation>? _tcs;

    // Generation counter: incremented on each ForkOnEdit so a background worker
    // started on generation N can self-abort when it notices generation N+1 is now
    // current.
    int _generation;

    // ── Construction ──────────────────────────────────────────────────────

    public CompilationTracker() { }

    private CompilationTracker(CompilationState state, Compilation? snapshot, int generation)
    {
        _state = state;
        _snapshot = snapshot;
        _generation = generation;
    }

    // ── Public API ────────────────────────────────────────────────────────

    public CompilationState State => _state;

    /// The most recent compilation snapshot, if one exists. Returns the in-progress
    /// compilation as the last-known state while a new one is being built, or null
    /// when in the <see cref="CompilationState.None"/> state.
    public Compilation? Snapshot => _snapshot;

    /// The current generation. A background worker should compare this to the
    /// generation it started on; if they differ, the work is stale.
    public int Generation => _generation;

    /// Transition to InProgress with a new generation counter, discarding any
    /// previous <see cref="Snapshot"/>. Returns the generation token the background
    /// worker should carry to detect staleness.
    public int ForkOnEdit(Workspace workspace)
    {
        var gen = Interlocked.Increment(ref _generation);
        _tcs = new TaskCompletionSource<Compilation>(TaskCreationOptions.RunContinuationsAsynchronously);
        _state = CompilationState.InProgress;
        // The workspace's CurrentCompilation is still the old snapshot — the
        // new one will be set by MarkFinal. We do NOT eagerly snapshot here
        // to avoid forcing a build before the background worker is ready.
        return gen;
    }

    /// Mark the compilation as Final with the given snapshot. If the provided
    /// generation is stale (a newer ForkOnEdit happened while this was building),
    /// the snapshot is discarded and the method returns false — the caller should
    /// abandon this work and let the newer worker complete.
    public bool MarkFinal(int generation, Compilation snapshot)
    {
        // Check staleness atomically: if _generation no longer matches, another
        // ForkOnEdit happened after we started building.
        if (Volatile.Read(ref _generation) != generation) return false;

        _snapshot = snapshot;
        _state = CompilationState.Final;
        _tcs?.TrySetResult(snapshot);
        return true;
    }

    /// Mark the in-progress compilation as faulted (background worker threw).
    /// Does not set the state to None — the last known snapshot is preserved so
    /// diagnostics can still be surfaced. Logs the error but does not throw.
    public void MarkFaulted(int generation, Exception ex)
    {
        if (Volatile.Read(ref _generation) != generation) return;
        _state = CompilationState.None; // allow retry
        _tcs?.TrySetException(ex);
    }

    /// Wait for the current in-progress compilation to reach Final state.
    /// Returns immediately if already Final or None.
    public Task<Compilation?> WaitForFinalAsync(CancellationToken cancellationToken = default)
    {
        if (_state == CompilationState.Final && _snapshot is not null)
            return Task.FromResult<Compilation?>(_snapshot);
        if (_state == CompilationState.None)
            return Task.FromResult<Compilation?>(null);

        var tcs = _tcs;
        if (tcs is null) return Task.FromResult<Compilation?>(null);

        return tcs.Task.ContinueWith(
            t => t.IsCompletedSuccessfully ? (Compilation?)t.Result : null,
            cancellationToken,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// Fork this tracker for a new document edit. The forked tracker carries the
    /// last Final snapshot as stale-but-usable context while the new build runs.
    public CompilationTracker ForkForNewEdit()
    {
        // Pass the existing final snapshot as the stale read while building.
        var forked = new CompilationTracker(
            CompilationState.InProgress,
            _state == CompilationState.Final ? _snapshot : null,
            _generation + 1);
        forked._tcs = new TaskCompletionSource<Compilation>(TaskCreationOptions.RunContinuationsAsynchronously);
        return forked;
    }
}

public enum CompilationState
{
    /// No compilation has been built for this project.
    None,

    /// A compilation is actively being built (bind + lower running).
    InProgress,

    /// The compilation is complete and the Snapshot is immutable.
    Final,
}

/// A per-project snapshot keyed by <see cref="ProjectId"/>. The compilation
/// orchestration layer holds one <see cref="CompilationSnapshot"/> per project in
/// the solution and uses <see cref="CompilationTracker"/> to manage its lifecycle.
public sealed class CompilationSnapshot
{
    public ProjectId ProjectId { get; }
    public CompilationTracker Tracker { get; }
    public IReadOnlyList<Esharp.Diagnostics.Diagnostic> LastDiagnostics { get; private set; }

    public CompilationSnapshot(ProjectId projectId)
    {
        ProjectId = projectId;
        Tracker = new CompilationTracker();
        LastDiagnostics = [];
    }

    public void UpdateDiagnostics(IReadOnlyList<Esharp.Diagnostics.Diagnostic> diags)
        => LastDiagnostics = diags;
}

/// Stable identity for a project in a multi-project solution.
public readonly record struct ProjectId(Guid Value)
{
    public static ProjectId New() => new(Guid.NewGuid());
    public static ProjectId FromPath(string esProjPath) =>
        new(GuidFromPath(esProjPath));

    // Deterministic GUID from a path so the same esproj always produces the
    // same ProjectId — important for caching and diff-aware rebuilds.
    static Guid GuidFromPath(string path)
    {
        // FNV-1a 64 → truncate to 128 bits across two 64-bit words.
        const ulong fnvOffset = 14695981039346656037;
        const ulong fnvPrime  = 1099511628211;
        var hash1 = fnvOffset;
        var hash2 = fnvOffset ^ 0xDEADBEEFCAFEBABEUL;
        foreach (var c in path.ToUpperInvariant()) // normalise
        {
            hash1 ^= (byte)(c & 0xff);   hash1 *= fnvPrime;
            hash2 ^= (byte)(c >> 8 & 0xff); hash2 *= fnvPrime;
        }
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), hash1);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), hash2);
        return new Guid(bytes);
    }

    public override string ToString() => Value.ToString("N");
}
