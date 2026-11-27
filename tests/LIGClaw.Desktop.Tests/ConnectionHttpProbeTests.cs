using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using LIGClaw.Application.Connections;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class ConnectionHttpProbeTests
{
    [Fact]
    public async Task SendsOneFixedGetWithBearerAndReturnsNoResponseData()
    {
        RecordedRequest? recorded = null;
        var handler = new RecordingHandler(request =>
        {
            recorded = new(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Version,
                request.VersionPolicy,
                request.Headers.ConnectionClose is true,
                request.Content?.Headers.ContentLength);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var credentialReads = 0;
        var probe = new ConnectionHttpProbe(
            (_, _, _) =>
            {
                credentialReads++;
                return Task.FromResult<string?>("probe-secret");
            },
            handler);

        var result = await probe.ProbeAsync(Profile(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(1, credentialReads);
        Assert.Equal(
            new RecordedRequest(
                "GET",
                "https://internal.example/api/",
                "Bearer",
                "probe-secret",
                HttpVersion.Version11,
                HttpVersionPolicy.RequestVersionExact,
                true,
                0),
            recorded);
        Assert.DoesNotContain("probe-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCredentialFailsBeforeAnyNetworkSend()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("must not send"));
        var probe = new ConnectionHttpProbe(
            (_, _, _) => Task.FromResult<string?>(null),
            handler);

        var result = await probe.ProbeAsync(Profile(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("connection.probe.credential_unavailable", result.ErrorCode);
        Assert.Equal(0, handler.SendCount);
    }

    [Theory]
    [InlineData("token with whitespace")]
    [InlineData("token=followed-by-data")]
    [InlineData("token:colon")]
    public async Task InvalidBearerGrammarFailsBeforeAnyNetworkSend(string secret)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("must not send"));
        var probe = new ConnectionHttpProbe(
            (_, _, _) => Task.FromResult<string?>(secret),
            handler);

        var result = await probe.ProbeAsync(Profile(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("connection.probe.credential_unavailable", result.ErrorCode);
        Assert.Equal(0, handler.SendCount);
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectIsNotFollowedAndReturnsOnlyAFixedError()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri("https://outside.example/secret");
        var handler = new RecordingHandler(_ => response);
        var probe = new ConnectionHttpProbe(
            (_, _, _) => Task.FromResult<string?>("probe-secret"),
            handler);

        var result = await probe.ProbeAsync(Profile(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("connection.probe.redirect_denied", result.ErrorCode);
        Assert.Equal(1, handler.SendCount);
        Assert.DoesNotContain("outside.example", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportExceptionIsReducedToAFixedError()
    {
        var handler = new RecordingHandler(_ =>
            throw new HttpRequestException("raw endpoint and token must not escape"));
        var probe = new ConnectionHttpProbe(
            (_, _, _) => Task.FromResult<string?>("probe-secret"),
            handler);

        var result = await probe.ProbeAsync(Profile(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("connection.probe.transport_failed", result.ErrorCode);
        Assert.DoesNotContain("raw endpoint", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("probe-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrematureResponseCloseAfterHeadersDoesNotReplayTheProbe()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = CountRequestsClosedAfterHeadersAsync(listener);
        var probe = new ConnectionHttpProbe(
            (_, _, _) => Task.FromResult<string?>(null));

        var result = await probe.ProbeAsync(
            Profile(
                $"http://127.0.0.1:{port}/api/",
                ConnectionAuthenticationKind.None),
            CancellationToken.None);
        var requestCount = await server;

        Assert.False(result.Succeeded);
        Assert.Equal("connection.probe.transport_failed", result.ErrorCode);
        Assert.Equal(1, requestCount);
    }

    private static HttpConnectionProfile Profile(
        string baseUrl = "https://internal.example/api/",
        ConnectionAuthenticationKind authentication = ConnectionAuthenticationKind.Bearer)
    {
        var now = DateTimeOffset.Parse("2026-08-02T00:00:00+00:00");
        return new HttpConnectionProfile(
            ConnectionProfileId.Parse("probe-fixture"),
            "Probe fixture",
            baseUrl,
            authentication,
            true,
            ConnectionProfileLifecycleState.Stable,
            3,
            1,
            1,
            HttpConnectionProfilePolicy.ComputeTargetHash(
                baseUrl,
                authentication),
            null,
            now,
            now);
    }

    private static async Task<int> CountRequestsClosedAfterHeadersAsync(TcpListener listener)
    {
        var count = 0;
        while (count < 4)
        {
            using var timeout = new CancellationTokenSource(
                count == 0 ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(750));
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            using (client)
            using (var reader = new StreamReader(
                       client.GetStream(),
                       Encoding.ASCII,
                       detectEncodingFromByteOrderMarks: false,
                       bufferSize: 1_024,
                       leaveOpen: false))
            {
                var requestLine = await reader.ReadLineAsync(timeout.Token);
                Assert.StartsWith("GET ", requestLine, StringComparison.Ordinal);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token)))
                {
                }
                count++;
            }
        }
        return count;
    }

    private sealed record RecordedRequest(
        string Method,
        string Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        Version Version,
        HttpVersionPolicy VersionPolicy,
        bool ConnectionClose,
        long? ContentLength);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(response(request));
        }
    }
}
