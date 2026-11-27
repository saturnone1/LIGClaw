using System.Net;
using System.Net.Http;
using System.Text;
using LIGClaw.Application.Connections;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class MailGatewayProbeAdapterTests
{
    private const string Token = "mail-gateway-token-abcdefghijklmnopqrstuvwxyz-0123456789";

    [Fact]
    public async Task ReadsExactlyTheMailGatewayCredentialAndMakesOneFixedWireProbe()
    {
        (ConnectionProfileId Id, ConnectionCredentialKind Kind)? credentialRead = null;
        RecordedRequest? request = null;
        var handler = new RecordingHandler(current =>
        {
            request = current;
            return JsonResponse(ProbeJson());
        });
        var adapter = new MailGatewayProbeAdapter(
            (id, kind, _) =>
            {
                credentialRead = (id, kind);
                return Task.FromResult<string?>(Token);
            },
            new MailGatewayWireClient(handler));

        var result = await adapter.ProbeAsync(Profile(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.Equal((Profile().Id, ConnectionCredentialKind.MailGatewayBearerToken), credentialRead);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal("GET", request!.Method);
        Assert.Equal("https://gateway.example.invalid/.well-known/ligclaw-mail-gateway/v1", request.Uri);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal(Token, request.AuthorizationParameter);
    }

    [Fact]
    public async Task InvalidOrNotReadyProfileFailsBeforeCredentialOrNetwork()
    {
        var credentialReads = 0;
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("must not send"));
        var adapter = new MailGatewayProbeAdapter(
            (_, _, _) =>
            {
                credentialReads++;
                return Task.FromResult<string?>(Token);
            },
            new MailGatewayWireClient(handler));

        var disabled = await adapter.ProbeAsync(Profile() with { Enabled = false }, CancellationToken.None);
        var unstable = await adapter.ProbeAsync(
            Profile() with { State = ConnectionProfileLifecycleState.Staged }, CancellationToken.None);
        var malformed = await adapter.ProbeAsync(
            Profile() with { BaseUrl = "https://gateway.example.invalid/not-root/" }, CancellationToken.None);

        Assert.False(disabled.Succeeded);
        Assert.False(unstable.Succeeded);
        Assert.False(malformed.Succeeded);
        Assert.All(new[] { disabled, unstable, malformed }, result =>
            Assert.Equal("mail.gateway.probe.profile_invalid", result.ErrorCode));
        Assert.Equal(0, credentialReads);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task MissingInvalidOrCrossKindCredentialFailsBeforeNetwork()
    {
        foreach (var credential in new[]
                 {
                     (MailGateway: (string?)null, Generic: "generic-token-abcdefghijklmnopqrstuvwxyz"),
                     (MailGateway: "invalid token", Generic: "generic-token-abcdefghijklmnopqrstuvwxyz"),
                     (MailGateway: (string?)null, Generic: "other-kind-only-token"),
                 })
        {
            var reads = new List<ConnectionCredentialKind>();
            var handler = new RecordingHandler(_ => throw new InvalidOperationException("must not send"));
            var adapter = new MailGatewayProbeAdapter(
                (_, kind, _) =>
                {
                    reads.Add(kind);
                    return Task.FromResult(kind == ConnectionCredentialKind.MailGatewayBearerToken
                        ? credential.MailGateway
                        : credential.Generic);
                },
                new MailGatewayWireClient(handler));

            var result = await adapter.ProbeAsync(Profile(), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("mail.gateway.probe.credential_unavailable", result.ErrorCode);
            Assert.Equal([ConnectionCredentialKind.MailGatewayBearerToken], reads);
            Assert.Equal(0, handler.SendCount);
        }
    }

    [Fact]
    public async Task RedirectAndMalformedGatewayResponsesRemainFixedAndRedacted()
    {
        foreach (var response in new[]
                 {
                     new HttpResponseMessage(HttpStatusCode.Found)
                     {
                         Headers = { Location = new Uri("https://outside.example.invalid/raw") },
                     },
                     JsonResponse("{not-json"),
                 })
        {
            var handler = new RecordingHandler(_ => response);
            var adapter = new MailGatewayProbeAdapter(
                (_, _, _) => Task.FromResult<string?>(Token),
                new MailGatewayWireClient(handler));

            var result = await adapter.ProbeAsync(Profile(), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.NotNull(result.ErrorCode);
            Assert.Equal(1, handler.SendCount);
            Assert.DoesNotContain("outside.example", result.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Token, result.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithoutNetwork()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("must not send"));
        var adapter = new MailGatewayProbeAdapter(
            (_, _, token) => Task.FromCanceled<string?>(token),
            new MailGatewayWireClient(handler));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.ProbeAsync(Profile(), cancelled.Token));
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task RawProfileTokenAndTransportCanariesNeverEscape()
    {
        const string rawToken = "raw-token-canary-abcdefghijklmnopqrstuvwxyz";
        const string rawEndpoint = "raw-endpoint-canary";
        var handler = new RecordingHandler(_ => throw new HttpRequestException(
            $"{rawEndpoint} {rawToken}"));
        var adapter = new MailGatewayProbeAdapter(
            (_, _, _) => Task.FromResult<string?>(rawToken),
            new MailGatewayWireClient(handler));

        var result = await adapter.ProbeAsync(Profile(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("mail.gateway.probe.transport_failed", result.ErrorCode);
        Assert.DoesNotContain(rawToken, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(rawEndpoint, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(rawToken, adapter.ToString(), StringComparison.Ordinal);
    }

    private static MailGatewayConnectionProfile Profile()
    {
        var now = DateTimeOffset.Parse("2026-08-02T00:00:00Z");
        const string baseUrl = "https://gateway.example.invalid/";
        const string sender = "sender@example.invalid";
        return new MailGatewayConnectionProfile(
            ConnectionProfileId.Parse("mail-gateway-fixture"),
            "Mail gateway fixture",
            baseUrl,
            sender,
            true,
            ConnectionProfileLifecycleState.Stable,
            1,
            1,
            1,
            MailGatewayConnectionProfilePolicy.ComputeTargetHash(baseUrl, sender),
            null,
            now,
            now);
    }

    private static string ProbeJson() =>
        """{"contract":"ligclaw-mail-gateway-v1","idempotencyWindowSeconds":300,"duplicateSuppression":"same-key-same-operation","operationLookup":true}""";

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed record RecordedRequest(
        string Method,
        string Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter);

    private sealed class RecordingHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(respond(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter)));
        }
    }
}
