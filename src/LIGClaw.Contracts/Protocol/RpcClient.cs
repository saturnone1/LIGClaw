using System.Collections.Concurrent;
using System.Text.Json;

namespace LIGClaw.Contracts.Protocol;

public sealed class RpcClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly ContentLengthMessageStream _messages;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RpcResponse>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _readerSync = new();
    private Task? _readerTask;
    private long _nextId;

    public RpcClient(Stream stream)
    {
        _stream = stream;
        _messages = new ContentLengthMessageStream(stream);
    }

    public event EventHandler<RpcNotification>? NotificationReceived;

    public async Task<TResult> InvokeAsync<TResult>(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
        where TResult : notnull
    {
        ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
        EnsureReaderStarted();

        var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException($"Duplicate RPC request id '{id}'.");

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending)) pending.TrySetCanceled(cancellationToken);
        });

        try
        {
            await _messages.WriteAsync(new RpcRequest(id, method, parameters), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        var response = await completion.Task.ConfigureAwait(false);
        ValidateResponse(response, id);
        if (response.Error is not null) throw new RpcException(response.Error.Code, response.Error.Message);

        var resultElement = response.Result
            ?? throw new InvalidDataException("Sidecar response did not contain a result.");
        return resultElement.Deserialize<TResult>(ContentLengthMessageStream.SerializerOptions)
            ?? throw new InvalidDataException("Sidecar response result was null.");
    }

    private void EnsureReaderStarted()
    {
        lock (_readerSync)
        {
            _readerTask ??= Task.Run(() => ReadLoopAsync(_lifetime.Token));
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var document = await _messages.ReadAsync(cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out _))
                {
                    var parsed = root.Deserialize<RpcResponse>(ContentLengthMessageStream.SerializerOptions)
                        ?? throw new InvalidDataException("Sidecar returned an empty RPC response.");
                    var response = parsed with { Result = parsed.Result?.Clone() };
                    if (_pending.TryRemove(response.Id, out var completion)) completion.TrySetResult(response);
                    continue;
                }

                var notification = root.Deserialize<RpcNotification>(ContentLengthMessageStream.SerializerOptions)
                    ?? throw new InvalidDataException("Sidecar returned an empty RPC notification.");
                if (!StringComparer.Ordinal.Equals(notification.JsonRpc, ProtocolConstants.JsonRpcVersion))
                    throw new InvalidDataException($"Unsupported JSON-RPC version '{notification.JsonRpc}'.");
                PublishNotification(notification with { Params = notification.Params?.Clone() });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailPending(exception);
        }
    }

    private static void ValidateResponse(RpcResponse response, string expectedId)
    {
        if (!StringComparer.Ordinal.Equals(response.JsonRpc, ProtocolConstants.JsonRpcVersion))
            throw new InvalidDataException($"Unsupported JSON-RPC version '{response.JsonRpc}'.");
        if (!StringComparer.Ordinal.Equals(response.Id, expectedId))
            throw new InvalidDataException($"Expected RPC response '{expectedId}', received '{response.Id}'.");
        if (response.Error is not null && response.Result is not null)
            throw new InvalidDataException("Sidecar response cannot contain both result and error.");
    }

    private void PublishNotification(RpcNotification notification)
    {
        var handlers = NotificationReceived;
        if (handlers is null) return;
        foreach (EventHandler<RpcNotification> handler in handlers.GetInvocationList())
        {
            try { handler(this, notification); }
            catch { /* A consumer must not stop the transport reader. */ }
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var completion)) completion.TrySetException(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        FailPending(new ObjectDisposedException(nameof(RpcClient)));
        await _stream.DisposeAsync().ConfigureAwait(false);
        if (_readerTask is not null)
        {
            try { await _readerTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
    }
}

public sealed class RpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
