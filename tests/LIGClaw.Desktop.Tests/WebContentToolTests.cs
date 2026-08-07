using System.Net;
using System.Net.Http;
using System.Text;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class WebContentToolTests
{
    [Fact]
    public async Task FetchesPublicOrPrivateAddressWithoutNetworkRangeBlockingAndExtractsHtml()
    {
        var handler = new SequenceHandler(
            request =>
            {
                Assert.Equal("http://203.0.113.10/start", request.RequestUri!.ToString());
                return Redirect("/article");
            },
            request =>
            {
                Assert.Equal("http://203.0.113.10/article", request.RequestUri!.ToString());
                return Response("<html><script>ignore()</script><body><h1>제목</h1><p>본문&amp;내용</p></body></html>", "text/html");
            });
        var client = new BoundedWebContentClient(new HttpClient(handler));

        var result = await client.FetchAsync(new Uri("http://203.0.113.10/start#fragment"), CancellationToken.None);

        Assert.Equal("http://203.0.113.10/start", result.RequestedUri.ToString());
        Assert.Equal("http://203.0.113.10/article", result.FinalUri.ToString());
        Assert.Equal(1, result.RedirectCount);
        Assert.Contains("제목", result.Text, StringComparison.Ordinal);
        Assert.Contains("본문&내용", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ignore", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundsResponseAndRejectsBinaryContent()
    {
        var oversized = new BoundedWebContentClient(new HttpClient(new SequenceHandler(_ =>
            Response(new string('x', BoundedWebContentClient.MaximumResponseBytes + 100), "text/plain"))));
        var bounded = await oversized.FetchAsync(new Uri("https://example.test/large"), CancellationToken.None);
        Assert.True(bounded.Truncated);
        Assert.True(bounded.Text.Length <= BoundedWebContentClient.MaximumOutputCharacters + 1);

        var binary = new BoundedWebContentClient(new HttpClient(new SequenceHandler(_ =>
            Response("binary", "application/octet-stream"))));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            binary.FetchAsync(new Uri("https://example.test/file"), CancellationToken.None));
    }

    [Fact]
    public async Task RejectsCredentialsAndExcessiveRedirects()
    {
        Assert.Throws<ArgumentException>(() =>
            BoundedWebContentClient.ValidateUri(new Uri("https://user:password@example.test/")));
        var redirects = new BoundedWebContentClient(new HttpClient(new SequenceHandler(_ => Redirect("/again"))));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            redirects.FetchAsync(new Uri("https://example.test/start"), CancellationToken.None));
    }

    [Fact]
    public async Task SearchUsesConfiguredTemplateAndRequiresR3Preview()
    {
        Uri? requested = null;
        var client = new FakeWebContentClient(uri =>
        {
            requested = uri;
            return new WebContentResult(uri, uri, 200, "application/json", "{}", false, 0);
        });
        var tool = new WebSearchTool(
            client,
            () => new WebSearchSettings("http://127.0.0.1:8080/search?q={query}"));
        var input = new Dictionary<string, object?>
        {
            ["query"] = "보안 정책",
            ["reason"] = "사용자가 관련 문서를 요청했기 때문에",
        };

        var preview = tool.CreateApprovalPrompt(input);
        var result = await tool.ExecuteAsync(input, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.Equal("R3", preview.Risk);
        Assert.Equal("http://127.0.0.1:8080/search?q=%EB%B3%B4%EC%95%88%20%EC%A0%95%EC%B1%85", requested!.AbsoluteUri);
        Assert.True(result.Success);
        Assert.Equal("보안 정책", result.Output["query"]);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/search?q={query}", true)]
    [InlineData("https://search.example/api/{query}", true)]
    [InlineData("https://search.example/api", false)]
    [InlineData("file:///C:/search/{query}", false)]
    public void ValidatesSearchTemplatesWithoutBlockingHostRanges(string template, bool expected)
    {
        var valid = WebSearchSettingsPolicy.TryValidate(template, out _, out _);
        Assert.Equal(expected, valid);
    }

    private static HttpResponseMessage Response(string text, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, mediaType),
    };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = responses[Math.Min(_index++, responses.Length - 1)](request);
            return Task.FromResult(response);
        }
    }

    private sealed class FakeWebContentClient(Func<Uri, WebContentResult> fetch) : IWebContentClient
    {
        public Task<WebContentResult> FetchAsync(Uri uri, CancellationToken cancellationToken) =>
            Task.FromResult(fetch(uri));
    }
}
