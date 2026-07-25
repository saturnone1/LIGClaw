using System.Text;
using System.Text.Json;
using LIGClaw.Contracts.Protocol;

namespace LIGClaw.Contracts.Tests;

public sealed class ContentLengthMessageStreamTests
{
    [Fact]
    public async Task RoundTripsTypedMessage()
    {
        await using var stream = new MemoryStream();
        var framed = new ContentLengthMessageStream(stream);
        var expected = new InitializeParams("1.0", "0.1.0", "hash", new string('a', 32));

        await framed.WriteAsync(expected);
        stream.Position = 0;
        using var document = await framed.ReadAsync();
        var actual = document.RootElement.Deserialize<InitializeParams>(ContentLengthMessageStream.SerializerOptions);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task RejectsMissingContentLength()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("Other: 1\r\n\r\n{}"));
        var framed = new ContentLengthMessageStream(stream);

        await Assert.ThrowsAsync<InvalidDataException>(() => framed.ReadAsync());
    }

    [Fact]
    public async Task ReadsConsecutiveMessagesWithoutLosingBytes()
    {
        await using var stream = new MemoryStream();
        var framed = new ContentLengthMessageStream(stream);
        await framed.WriteAsync(new PingResult(DateTimeOffset.UnixEpoch));
        await framed.WriteAsync(new PingResult(DateTimeOffset.UnixEpoch.AddSeconds(1)));
        stream.Position = 0;

        using var first = await framed.ReadAsync();
        using var second = await framed.ReadAsync();

        Assert.Equal(DateTimeOffset.UnixEpoch, first.RootElement.Deserialize<PingResult>(ContentLengthMessageStream.SerializerOptions)!.TimestampUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1), second.RootElement.Deserialize<PingResult>(ContentLengthMessageStream.SerializerOptions)!.TimestampUtc);
    }
}
