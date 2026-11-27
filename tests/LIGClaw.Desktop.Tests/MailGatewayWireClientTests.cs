using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using LIGClaw.Application.Mail;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class MailGatewayWireClientTests
{
    private static readonly Uri Root = new("https://mail.example.invalid/");
    private const string Token = "wire-token-abcdefghijklmnopqrstuvwxyz-0123456789";
    private const string Sender = "sender@example.invalid";

    [Fact]
    public async Task ProbeSendsTheExactFixedRequestAndAcceptsOnlyThe300SecondContract()
    {
        RecordedRequest? recorded = null;
        var handler = new RecordingHandler(request =>
        {
            recorded = request;
            return JsonResponse(ProbeJson());
        });
        var client = new MailGatewayWireClient(handler);

        var result = await client.ProbeAsync(Root, Token, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal("GET", recorded!.Method);
        Assert.Equal("https://mail.example.invalid/.well-known/ligclaw-mail-gateway/v1", recorded.Uri);
        Assert.Equal("Bearer", recorded.AuthorizationScheme);
        Assert.Equal(Token, recorded.AuthorizationParameter);
        Assert.Equal(HttpVersion.Version11, recorded.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, recorded.VersionPolicy);
        Assert.True(recorded.ConnectionClose);
        Assert.Equal("application/json", recorded.Accept);
        Assert.Equal("LIGClaw-MailGateway/1.0", recorded.UserAgent);
        Assert.Equal(0, recorded.ContentLength);

    }

    [Fact]
    public async Task SubmitSendsOneCanonicalFixedPostAndReturnsQueued()
    {
        RecordedRequest? recorded = null;
        var draft = Draft();
        var operationId = OperationId();
        var idempotencyKey = IdempotencyKey();
        var handler = new RecordingHandler(request =>
        {
            recorded = request;
            return JsonResponse(OperationJson(operationId.Value, "queued", draft.CanonicalHash), HttpStatusCode.Accepted);
        });
        var client = new MailGatewayWireClient(handler);

        var result = await client.SubmitAsync(
            Root, Token, Sender, draft, operationId, idempotencyKey, CancellationToken.None);

        Assert.Equal(MailGatewayWireOutcome.Accepted, result.Outcome);
        Assert.Equal(MailGatewayWireDeliveryStatus.Queued, result.DeliveryStatus);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal("POST", recorded!.Method);
        Assert.Equal("https://mail.example.invalid/ligclaw-mail-gateway/v1/submissions", recorded.Uri);
        Assert.Equal("Bearer", recorded.AuthorizationScheme);
        Assert.Equal(Token, recorded.AuthorizationParameter);
        Assert.Equal(idempotencyKey.Value, recorded.IdempotencyKey);
        Assert.Equal("application/json", recorded.Accept);
        Assert.Equal("LIGClaw-MailGateway/1.0", recorded.UserAgent);
        Assert.True(recorded.ConnectionClose);
        Assert.Equal("application/json; charset=utf-8", recorded.ContentType);
        Assert.Equal(HttpVersion.Version11, recorded.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, recorded.VersionPolicy);
        Assert.Equal(
            $$"""{"contract":"ligclaw-mail-gateway-v1","operationId":"{{operationId.Value}}","idempotencyKey":"{{idempotencyKey.Value}}","draftHash":"{{draft.CanonicalHash}}","sender":"sender@example.invalid","to":["to@example.invalid"],"cc":[],"bcc":[],"subject":"Subject","body":"Body"}""",
            recorded.Body);
    }

    [Fact]
    public async Task ProbeRejectsAnyContractBodyThatDoesNotProveThe300SecondWindow()
    {
        var handler = new RecordingHandler(_ => JsonResponse(ProbeJson().Replace("300", "299", StringComparison.Ordinal)));

        var result = await new MailGatewayWireClient(handler).ProbeAsync(Root, Token, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("mail.gateway.probe.response_invalid", result.ErrorCode);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task ReconcileUsesOneFixedGetAndOnlyItAcceptsNotFound()
    {
        var draft = Draft();
        var operationId = OperationId();
        var idempotencyKey = IdempotencyKey();
        var submitHandler = new RecordingHandler(_ => JsonResponse(NotFoundJson(operationId.Value)));
        var submit = await new MailGatewayWireClient(submitHandler).SubmitAsync(
            Root, Token, Sender, draft, operationId, idempotencyKey, CancellationToken.None);
        Assert.Equal(MailGatewayWireOutcome.OutcomeUnknown, submit.Outcome);
        Assert.Equal(1, submitHandler.SendCount);

        RecordedRequest? recorded = null;
        var handler = new RecordingHandler(request =>
        {
            recorded = request;
            return JsonResponse(NotFoundJson(operationId.Value));
        });
        var reconcile = await new MailGatewayWireClient(handler).ReconcileAsync(
            Root, Token, MailOutboxHash.Parse(draft.CanonicalHash), operationId, CancellationToken.None);

        Assert.Equal(MailGatewayWireOutcome.NotFound, reconcile.Outcome);
        Assert.Equal("mail.gateway.reconcile.not_found", reconcile.ErrorCode);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal("GET", recorded!.Method);
        Assert.Equal(
            $"https://mail.example.invalid/ligclaw-mail-gateway/v1/submissions/{operationId.Value}",
            recorded.Uri);
        Assert.Null(recorded.IdempotencyKey);
        Assert.Equal(0, recorded.ContentLength);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("non_success")]
    [InlineData("malformed")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("headers_too_large")]
    [InlineData("oversized")]
    [InlineData("compressed")]
    [InlineData("identity_mismatch")]
    [InlineData("draft_mismatch")]
    public async Task SubmitReducesUnsafeResponsesToFixedUnknownErrorsAndNeverRetries(string kind)
    {
        var draft = Draft();
        var operationId = OperationId();
        var handler = new RecordingHandler(_ => ResponseFor(kind, operationId, draft));
        var client = new MailGatewayWireClient(handler);

        var result = await client.SubmitAsync(
            Root, Token, Sender, draft, operationId, IdempotencyKey(), CancellationToken.None);

        Assert.Equal(MailGatewayWireOutcome.OutcomeUnknown, result.Outcome);
        Assert.NotNull(result.ErrorCode);
        Assert.Equal(1, handler.SendCount);
        Assert.DoesNotContain(Token, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Subject", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Body", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReconcileReturnsKnownFailedOnlyForTheExactFailedResponse()
    {
        var draft = Draft();
        var operationId = OperationId();
        var handler = new RecordingHandler(_ => JsonResponse(OperationJson(operationId.Value, "failed", draft.CanonicalHash)));

        var result = await new MailGatewayWireClient(handler).ReconcileAsync(
            Root, Token, MailOutboxHash.Parse(draft.CanonicalHash), operationId, CancellationToken.None);

        Assert.Equal(MailGatewayWireOutcome.KnownFailed, result.Outcome);
        Assert.Equal("mail.gateway.reconcile.remote_failed", result.ErrorCode);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task ReconcileAcceptsDeliveredButNotFoundMustBeAnHttp200Response()
    {
        var draft = Draft();
        var operationId = OperationId();
        var deliveredHandler = new RecordingHandler(_ =>
            JsonResponse(OperationJson(operationId.Value, "delivered", draft.CanonicalHash), HttpStatusCode.Accepted));
        var delivered = await new MailGatewayWireClient(deliveredHandler).ReconcileAsync(
            Root, Token, MailOutboxHash.Parse(draft.CanonicalHash), operationId, CancellationToken.None);
        Assert.Equal(MailGatewayWireOutcome.Accepted, delivered.Outcome);
        Assert.Equal(MailGatewayWireDeliveryStatus.Delivered, delivered.DeliveryStatus);

        var notFound202Handler = new RecordingHandler(_ =>
            JsonResponse(NotFoundJson(operationId.Value), HttpStatusCode.Accepted));
        var notFound202 = await new MailGatewayWireClient(notFound202Handler).ReconcileAsync(
            Root, Token, MailOutboxHash.Parse(draft.CanonicalHash), operationId, CancellationToken.None);
        Assert.Equal(MailGatewayWireOutcome.OutcomeUnknown, notFound202.Outcome);
        Assert.Equal(1, notFound202Handler.SendCount);
    }

    [Theory]
    [InlineData("http://mail.example.invalid/")]
    [InlineData("https://mail.example.invalid/base/")]
    [InlineData("https://mail.example.invalid/?q=1")]
    [InlineData("https://user:password@mail.example.invalid/")]
    public async Task InvalidBaseUriOrCredentialFailsBeforeNetwork(string baseUri)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("must not send"));
        var client = new MailGatewayWireClient(handler);

        var result = await client.SubmitAsync(
            new Uri(baseUri), Token, Sender, Draft(), OperationId(), IdempotencyKey(), CancellationToken.None);
        var invalidCredential = await client.ReconcileAsync(
            Root, "bad token", MailOutboxHash.Parse(Draft().CanonicalHash), OperationId(), CancellationToken.None);

        Assert.Equal(MailGatewayWireOutcome.NotStarted, result.Outcome);
        Assert.Equal(MailGatewayWireOutcome.NotStarted, invalidCredential.Outcome);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task RawMailTokenAndTransportDetailNeverEscapeResultsOrExceptions()
    {
        const string rawToken = "super-secret-wire-token-abcdef012345";
        const string rawSubject = "RAW-SUBJECT-CANARY";
        const string rawBody = "RAW-BODY-CANARY";
        var draft = ValidDraft(rawSubject, rawBody);
        var handler = new RecordingHandler(_ => throw new HttpRequestException(
            $"raw transport {rawToken} {rawSubject} {rawBody}"));

        var result = await new MailGatewayWireClient(handler).SubmitAsync(
            Root, rawToken, Sender, draft, OperationId(), IdempotencyKey(), CancellationToken.None);

        Assert.Equal(MailGatewayWireOutcome.OutcomeUnknown, result.Outcome);
        var diagnostic = result.ToString();
        Assert.DoesNotContain(rawToken, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(rawSubject, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(rawBody, diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaximumUnicodeDraftStaysWithinTheRequestLimitWithoutEscapingItsBody()
    {
        var body = string.Concat(Enumerable.Repeat("😀", 6_144));
        var draft = ValidDraft("Subject", body);
        RecordedRequest? recorded = null;
        var handler = new RecordingHandler(request =>
        {
            recorded = request;
            return JsonResponse(OperationJson(OperationId().Value, "queued", draft.CanonicalHash));
        });

        var result = await new MailGatewayWireClient(handler).SubmitAsync(
            Root, Token, Sender, draft, OperationId(), IdempotencyKey(), CancellationToken.None);

        Assert.True(result.Outcome == MailGatewayWireOutcome.Accepted, result.ErrorCode);
        Assert.InRange(recorded!.ContentLength!.Value, 1, 64 * 1024);
        Assert.Contains("😀", recorded.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconcileAcceptsOnlyMetadataIdentityParameters()
    {
        var method = typeof(MailGatewayWireClient).GetMethod(nameof(MailGatewayWireClient.ReconcileAsync))!;
        var parameterTypes = method.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.DoesNotContain(typeof(MailDraft), parameterTypes);
        Assert.Contains(typeof(MailOutboxHash), parameterTypes);
    }

    private static HttpResponseMessage ResponseFor(string kind, MailOutboxOperationId operationId, MailDraft draft) => kind switch
    {
        "redirect" => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://outside.example.invalid/") },
        },
        "non_success" => JsonResponse(OperationJson(operationId.Value, "queued", draft.CanonicalHash), HttpStatusCode.BadGateway),
        "malformed" => JsonResponse("{not-json"),
        "extra" => JsonResponse(OperationJson(operationId.Value, "queued", draft.CanonicalHash)[..^1] + ",\"message\":\"raw\"}"),
        "duplicate" => JsonResponse(
            $$"""{"contract":"ligclaw-mail-gateway-v1","operationId":"{{operationId.Value}}","operationId":"{{operationId.Value}}","status":"queued","draftHash":"{{draft.CanonicalHash}}"}"""),
        "headers_too_large" => HeaderTooLargeResponse(OperationJson(operationId.Value, "queued", draft.CanonicalHash)),
        "oversized" => JsonResponse($"\"{new string('a', 4_097)}\""),
        "compressed" => CompressedJsonResponse(OperationJson(operationId.Value, "queued", draft.CanonicalHash)),
        "identity_mismatch" => JsonResponse(OperationJson(OperationId('b').Value, "queued", draft.CanonicalHash)),
        "draft_mismatch" => JsonResponse(OperationJson(
            operationId.Value, "queued", ValidDraft("Other subject", "Other body").CanonicalHash)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage CompressedJsonResponse(string json)
    {
        var response = JsonResponse(json);
        response.Content.Headers.ContentEncoding.Add("gzip");
        return response;
    }

    private static HttpResponseMessage HeaderTooLargeResponse(string json)
    {
        var response = JsonResponse(json);
        response.Headers.Add("X-Response-Canary", new string('a', 16 * 1024));
        return response;
    }

    private static string ProbeJson() =>
        """{"contract":"ligclaw-mail-gateway-v1","idempotencyWindowSeconds":300,"duplicateSuppression":"same-key-same-operation","operationLookup":true}""";

    private static string OperationJson(string operationId, string status, string draftHash) =>
        $$"""{"contract":"ligclaw-mail-gateway-v1","operationId":"{{operationId}}","status":"{{status}}","draftHash":"{{draftHash}}"}""";

    private static string NotFoundJson(string operationId) =>
        $$"""{"contract":"ligclaw-mail-gateway-v1","operationId":"{{operationId}}","status":"not_found"}""";

    private static MailDraft Draft() => ValidDraft("Subject", "Body");

    private static MailDraft ValidDraft(string subject, string body) => Assert.IsType<MailDraft>(MailDraftPolicy.Validate(
        new MailDraftInput(["to@example.invalid"], [], [], subject, body)).Draft);

    private static MailOutboxOperationId OperationId(char digit = 'a') =>
        MailOutboxOperationId.Parse($"mgo_{new string(digit, 64)}");

    private static MailOutboxIdempotencyKey IdempotencyKey() =>
        MailOutboxIdempotencyKey.Parse($"mik_{new string('b', 64)}");

    private sealed record RecordedRequest(
        string Method,
        string Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string? IdempotencyKey,
        string? Accept,
        string? UserAgent,
        bool ConnectionClose,
        Version Version,
        HttpVersionPolicy VersionPolicy,
        string? ContentType,
        long? ContentLength,
        string? Body);

    private sealed class RecordingHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            var recorded = new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("Idempotency-Key", out var idempotency) ? Assert.Single(idempotency) : null,
                request.Headers.Accept.SingleOrDefault()?.MediaType,
                request.Headers.UserAgent.ToString(),
                request.Headers.ConnectionClose is true,
                request.Version,
                request.VersionPolicy,
                request.Content?.Headers.ContentType?.ToString(),
                request.Content?.Headers.ContentLength,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(recorded);
        }
    }
}
