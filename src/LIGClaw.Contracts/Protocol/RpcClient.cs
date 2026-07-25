using System.Text.Json;

namespace LIGClaw.Contracts.Protocol;

public sealed class RpcClient(Stream stream) : IAsyncDisposable
{
    private readonly ContentLengthMessageStream _messages = new(stream);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private long _nextId;

    public async Task<TResult> InvokeAsync<TResult>(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
        where TResult : notnull
    {
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await _messages.WriteAsync(new RpcRequest(id, method, parameters), cancellationToken).ConfigureAwait(false);
            using var document = await _messages.ReadAsync(cancellationToken).ConfigureAwait(false);
            var response = document.RootElement.Deserialize<RpcResponse>(ContentLengthMessageStream.SerializerOptions)
                ?? throw new InvalidDataException("Sidecar returned an empty RPC response.");

            if (!StringComparer.Ordinal.Equals(response.JsonRpc, ProtocolConstants.JsonRpcVersion))
            {
                throw new InvalidDataException($"Unsupported JSON-RPC version '{response.JsonRpc}'.");
            }

            if (!StringComparer.Ordinal.Equals(response.Id, id))
            {
                throw new InvalidDataException($"Expected RPC response '{id}', received '{response.Id}'.");
            }

            if (response.Error is not null && response.Result is not null)
            {
                throw new InvalidDataException("Sidecar response cannot contain both result and error.");
            }

            if (response.Error is not null)
            {
                throw new RpcException(response.Error.Code, response.Error.Message);
            }

            var resultElement = response.Result
                ?? throw new InvalidDataException("Sidecar response did not contain a result.");
            return resultElement.Deserialize<TResult>(ContentLengthMessageStream.SerializerOptions)
                ?? throw new InvalidDataException("Sidecar response result was null.");
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _requestLock.Dispose();
        await stream.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class RpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
