using System.Text.Json;
using LIGClaw.Contracts.Protocol;

namespace LIGClaw.Contracts.Tests;

public sealed class RpcClientTests
{
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

    private static async Task<RpcClient> CreateClientAsync(RpcResponse response)
    {
        var input = new MemoryStream();
        await new ContentLengthMessageStream(input).WriteAsync(response);
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
