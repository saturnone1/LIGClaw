using System.Net;
using System.Net.Http;
using System.Text;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class SemanticMemoryEmbeddingClientTests
{
    [Fact]
    public async Task CallsOpenAiCompatibleEndpointWithoutBlockingReachableHosts()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"index\":1,\"embedding\":[0,1]},{\"index\":0,\"embedding\":[1,0]}]}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var client = new OpenAiSemanticMemoryEmbeddingClient(new HttpClient(handler));

        var result = await client.EmbedAsync(
            new SemanticMemorySettings(true, "http://203.0.113.20:11434", "embed-local", "secret"),
            ["첫 번째", "두 번째"],
            CancellationToken.None);

        Assert.Equal("http://203.0.113.20:11434/v1/embeddings", captured!.RequestUri!.ToString());
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal([1f, 0f], result[0]);
        Assert.Equal([0f, 1f], result[1]);
    }

    [Fact]
    public async Task RejectsMalformedOrOversizedRequestsAndResponses()
    {
        var malformed = new OpenAiSemanticMemoryEmbeddingClient(new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") })));
        var settings = new SemanticMemorySettings(true, "https://embedding.example/v1", "model");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => malformed.EmbedAsync(
            settings, [new string('x', SemanticMemorySettingsPolicy.MaximumInputCharacters + 1)], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => malformed.EmbedAsync(
            settings, ["query"], CancellationToken.None));
    }

    [Theory]
    [InlineData("https://host.example/api", "https://host.example/api/v1/embeddings")]
    [InlineData("https://host.example/v1", "https://host.example/v1/embeddings")]
    [InlineData("https://host.example/v1/embeddings", "https://host.example/v1/embeddings")]
    public void ResolvesCompatibleEmbeddingPaths(string baseUrl, string expected) =>
        Assert.Equal(expected, SemanticMemorySettingsPolicy.ResolveEndpoint(baseUrl).ToString());

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
