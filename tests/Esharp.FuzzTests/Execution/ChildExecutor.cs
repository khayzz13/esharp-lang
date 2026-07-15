using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Esharp.FuzzTests.Execution;

/// Runs cases in a worker child process (`Esharp.FuzzTests --child`) so that
/// compiler stack overflows, runaway loops, and miscompiled-program hangs kill
/// or stall the child — never the test runner. One long-lived child handles a
/// stream of cases (forkserver-style); it is recycled periodically to bound
/// leaked LoadFrom assemblies, and replaced after any crash or timeout.
internal sealed class ChildExecutor : IDisposable
{
    const int RecycleAfterCases = 256;

    readonly object _gate = new();
    Process? _child;
    StreamWriter? _stdin;
    StreamReader? _stdout;
    StringBuilder _stderr = new();
    int _casesOnChild;

    public CaseResult Execute(CaseRequest request)
    {
        lock (_gate)
        {
            if (_child is null || _child.HasExited || _casesOnChild >= RecycleAfterCases)
                Restart();

            _casesOnChild++;
            var lastStage = FuzzStage.Parse;
            try
            {
                _stdin!.WriteLine(Protocol.Serialize(request));
                _stdin.Flush();

                var deadline = DateTime.UtcNow.AddMilliseconds(request.TimeoutMs);
                while (true)
                {
                    var line = ReadLineWithDeadline(deadline);
                    if (line is null)
                    {
                        // EOF: the child died mid-case.
                        var exit = WaitForExitCode();
                        var stderrTail = StderrTail();
                        KillChild();
                        return new CaseResult(request.Id, OutcomeKind.ProcessCrash, lastStage, [],
                            ExceptionType: "ProcessCrash",
                            ExceptionMessage: $"exit={exit} stderr={stderrTail}");
                    }
                    if (line.StartsWith("PHASE ", StringComparison.Ordinal))
                    {
                        Enum.TryParse(line["PHASE ".Length..], out lastStage);
                        continue;
                    }
                    if (line.StartsWith(Protocol.ResultPrefix, StringComparison.Ordinal))
                        return Protocol.Deserialize<CaseResult>(line[Protocol.ResultPrefix.Length..]);
                    // Anything else is stray output from the compiler or the
                    // generated program (Console.Write etc.) — ignore it.
                }
            }
            catch (TimeoutException)
            {
                KillChild();
                return new CaseResult(request.Id, OutcomeKind.Timeout, lastStage, [],
                    ExceptionType: "Timeout",
                    ExceptionMessage: $"no result within {request.TimeoutMs}ms (last stage: {lastStage})");
            }
        }
    }

    string? ReadLineWithDeadline(DateTime deadline)
    {
        var readTask = _stdout!.ReadLineAsync();
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero || !readTask.Wait(remaining))
            throw new TimeoutException();
        return readTask.Result;
    }

    void Restart()
    {
        KillChild();
        var assemblyPath = typeof(ChildExecutor).Assembly.Location;
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(assemblyPath);
        psi.ArgumentList.Add("--child");

        _stderr = new StringBuilder();
        _child = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start fuzz child process.");
        _child.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderr)
            {
                _stderr.AppendLine(e.Data);
                if (_stderr.Length > 8000) _stderr.Remove(0, _stderr.Length - 8000);
            }
        };
        _child.BeginErrorReadLine();
        _stdin = _child.StandardInput;
        _stdout = _child.StandardOutput;
        _casesOnChild = 0;

        // The child prints READY once its runtime is warm; wait so the first
        // case's timeout budget isn't consumed by process startup.
        var readyDeadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var line = ReadLineWithDeadline(readyDeadline);
            if (line is null)
                throw new InvalidOperationException($"Fuzz child died during startup. stderr: {StderrTail()}");
            if (line == Protocol.ReadyLine)
                return;
        }
    }

    int WaitForExitCode()
    {
        try { _child!.WaitForExit(2000); return _child.HasExited ? _child.ExitCode : -1; }
        catch { return -1; }
    }

    string StderrTail()
    {
        lock (_stderr)
        {
            var text = _stderr.ToString().Trim();
            return text.Length <= 600 ? text : text[^600..];
        }
    }

    void KillChild()
    {
        if (_child is null) return;
        try { if (!_child.HasExited) _child.Kill(entireProcessTree: true); } catch { }
        try { _child.Dispose(); } catch { }
        _child = null;
        _stdin = null;
        _stdout = null;
    }

    public void Dispose()
    {
        lock (_gate) KillChild();
    }
}

/// A pool of child executors that drains a batch of cases concurrently and
/// returns results in request order. `Execute` (single case) is what the
/// shrinker's predicate uses.
internal sealed class FuzzExecutor : IDisposable
{
    readonly ChildExecutor[] _workers;

    public FuzzExecutor(int workers = 0)
    {
        if (workers <= 0)
            workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        _workers = Enumerable.Range(0, workers).Select(_ => new ChildExecutor()).ToArray();
    }

    public CaseResult Execute(CaseRequest request) => _workers[0].Execute(request);

    public IReadOnlyList<(CaseRequest Request, CaseResult Result)> ExecuteAll(
        IReadOnlyList<CaseRequest> requests, Action<CaseRequest, CaseResult>? onResult = null)
    {
        var queue = new ConcurrentQueue<int>(Enumerable.Range(0, requests.Count));
        var results = new CaseResult[requests.Count];
        var tasks = _workers.Select(worker => Task.Run(() =>
        {
            while (queue.TryDequeue(out var index))
            {
                var result = worker.Execute(requests[index]);
                results[index] = result;
                onResult?.Invoke(requests[index], result);
            }
        })).ToArray();
        Task.WaitAll(tasks);
        return requests.Select((r, i) => (r, results[i])).ToList();
    }

    public void Dispose()
    {
        foreach (var worker in _workers) worker.Dispose();
    }
}
