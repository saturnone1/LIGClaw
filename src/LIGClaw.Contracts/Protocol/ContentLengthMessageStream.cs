using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LIGClaw.Contracts.Protocol;

public sealed class ContentLengthMessageStream(Stream stream)
{
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
    };

    public async Task WriteAsync<T>(T message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        if (payload.Length > ProtocolConstants.MaximumPayloadBytes)
        {
            throw new InvalidDataException($"RPC payload exceeds {ProtocolConstants.MaximumPayloadBytes} bytes.");
        }

        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        var frame = GC.AllocateUninitializedArray<byte>(header.Length + payload.Length);
        header.CopyTo(frame, 0);
        payload.CopyTo(frame, header.Length);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<JsonDocument> ReadAsync(CancellationToken cancellationToken = default)
    {
        var headerBuffer = ArrayPool<byte>.Shared.Rent(ProtocolConstants.MaximumHeaderBytes);
        try
        {
            var headerLength = await ReadHeaderAsync(headerBuffer, cancellationToken).ConfigureAwait(false);
            var contentLength = ParseContentLength(headerBuffer.AsSpan(0, headerLength));
            if (contentLength is < 0 or > ProtocolConstants.MaximumPayloadBytes)
            {
                throw new InvalidDataException("RPC Content-Length is outside the allowed range.");
            }

            var payload = ArrayPool<byte>.Shared.Rent(contentLength);
            try
            {
                await stream.ReadExactlyAsync(payload.AsMemory(0, contentLength), cancellationToken).ConfigureAwait(false);
                var reader = new Utf8JsonReader(payload.AsSpan(0, contentLength));
                return JsonDocument.ParseValue(ref reader);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload, clearArray: true);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer, clearArray: true);
        }
    }

    private async Task<int> ReadHeaderAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var length = 0;
        while (length < ProtocolConstants.MaximumHeaderBytes)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException("RPC stream ended while reading a header.");
            }

            length += bytesRead;
            if (length >= HeaderTerminator.Length &&
                buffer.AsSpan(length - HeaderTerminator.Length, HeaderTerminator.Length).SequenceEqual(HeaderTerminator))
            {
                return length - HeaderTerminator.Length;
            }
        }

        throw new InvalidDataException("RPC header exceeds the allowed size.");
    }

    private static int ParseContentLength(ReadOnlySpan<byte> headerBytes)
    {
        var header = Encoding.ASCII.GetString(headerBytes);
        foreach (var line in header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "Content-Length:";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[prefix.Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            {
                return length;
            }
        }

        throw new InvalidDataException("RPC header does not contain a valid Content-Length.");
    }
}
