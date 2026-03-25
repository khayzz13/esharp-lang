using System.Collections;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Esharp.Runtime;

public readonly struct Result<TValue, TError>
{
    readonly TValue? _value;
    readonly TError? _error;

    Result(bool isOk, TValue? value, TError? error)
    {
        IsOk = isOk;
        _value = value;
        _error = error;
    }

    public bool IsOk { get; }

    public bool IsError => !IsOk;

    public TValue Value => IsOk ? _value! : throw new InvalidOperationException("Result does not contain a value.");

    public TError ErrorValue => IsError ? _error! : throw new InvalidOperationException("Result does not contain an error.");

    public static Result<TValue, TError> Ok(TValue value) => new(true, value, default);

    public static Result<TValue, TError> Error(TError error) => new(false, default, error);
}

public static class Result
{
    public static Result<TValue, TError> Ok<TValue, TError>(TValue value) =>
        Esharp.Runtime.Result<TValue, TError>.Ok(value);

    public static Result<TValue, TError> Error<TValue, TError>(TError error) =>
        Esharp.Runtime.Result<TValue, TError>.Error(error);
}

public sealed class Job
{
    readonly Task _task;
    readonly CancellationTokenSource _cts;

    Job(Task task, CancellationTokenSource cts)
    {
        _task = task;
        _cts = cts;
    }

    public static Job Spawn(Action<CancellationToken> action)
    {
        var cts = new CancellationTokenSource();
        var task = Task.Run(() => action(cts.Token), cts.Token);
        return new Job(task, cts);
    }

    public void Cancel() => _cts.Cancel();

    public void Join() => _task.GetAwaiter().GetResult();

    public ValueTask JoinAsync() => new(_task);
}

public sealed class Chan<T> : IEnumerable<T>
{
    readonly Channel<T> _channel;

    public Chan(int capacity = 0)
    {
        _channel = capacity > 0
            ? Channel.CreateBounded<T>(capacity)
            : Channel.CreateUnbounded<T>();
    }

    public ValueTask SendAsync(T value, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(value, cancellationToken);

    public void Send(T value) => _channel.Writer.WriteAsync(value).AsTask().GetAwaiter().GetResult();

    public ValueTask<T> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAsync(cancellationToken);

    public bool TryReceive(out T value) => _channel.Reader.TryRead(out value!);

    public void Close() => _channel.Writer.TryComplete();

    // Go-like range: blocks per item, terminates when Close() is called
    public IEnumerator<T> GetEnumerator() =>
        _channel.Reader.ReadAllAsync().ToBlockingEnumerable().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
