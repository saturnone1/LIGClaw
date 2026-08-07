using System.Text.Json;
using LIGClaw.Contracts.Generated;
using LIGClaw.Contracts.Protocol;

namespace LIGClaw.Contracts.Tests;

public sealed class RpcClientTests
{
    [Fact]
    public async Task DispatchesNotificationBeforeCompletingResponse()
    {
        var expectedEvent = new AgentEvent(
            "conversation-1",
            "run-1",
            0,
            "run_started",
            DateTimeOffset.UnixEpoch,
            null,
            null);
        await using var client = await CreateClientAsync(
            new RpcNotification("agent.event", JsonSerializer.SerializeToElement(expectedEvent)),
            new RpcResponse("1", JsonSerializer.SerializeToElement(new PingResult(DateTimeOffset.UnixEpoch)), null));
        var received = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NotificationReceived += (_, notification) =>
        {
            if (notification.Method == "agent.event")
                received.TrySetResult(notification.Params!.Value.Deserialize<AgentEvent>(ContentLengthMessageStream.SerializerOptions)!);
        };

        var result = await client.InvokeAsync<PingResult>("ping", null);
        var agentEvent = await received.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(DateTimeOffset.UnixEpoch, result.TimestampUtc);
        Assert.Equal(expectedEvent, agentEvent);
    }

    [Fact]
    public async Task RejectsUnsupportedJsonRpcVersion()
    {
        await using var client = await CreateClientAsync(new RpcResponse(
            "1",
            JsonSerializer.SerializeToElement(new PingResult(DateTimeOffset.UnixEpoch)),
            null,
            "1.0"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.InvokeAsync<PingResult>("ping", null));

        Assert.Contains("JSON-RPC version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsResponseContainingResultAndError()
    {
        await using var client = await CreateClientAsync(new RpcResponse(
            "1",
            JsonSerializer.SerializeToElement(new PingResult(DateTimeOffset.UnixEpoch)),
            new RpcError(-1, "invalid")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.InvokeAsync<PingResult>("ping", null));

        Assert.Contains("both result and error", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<RpcClient> CreateClientAsync(params object[] messages)
    {
        var input = new MemoryStream();
        var framed = new ContentLengthMessageStream(input);
        foreach (var message in messages) await framed.WriteAsync(message);
        input.Position = 0;
        return new RpcClient(new ScriptedDuplexStream(input));
    }

    private sealed class ScriptedDuplexStream(Stream input) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            if (disposing) input.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await input.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
