using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LIGClaw.Application.Mail;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal enum MailGatewayWireOutcome
{
    NotStarted,
    Accepted,
    KnownFailed,
    NotFound,
    OutcomeUnknown,
}

internal enum MailGatewayWireDeliveryStatus
{
    Queued,
    Delivered,
}

internal sealed record MailGatewayWireProbeResult(bool Succeeded, string? ErrorCode = null)
{
    public override string ToString() => Succeeded
        ? "MailGatewayWireProbeResult(Succeeded=True)"
        : $"MailGatewayWireProbeResult(Succeeded=False, ErrorCode={ErrorCode})";
}

internal sealed record MailGatewayWireResult(
    MailGatewayWireOutcome Outcome,
    MailGatewayWireDeliveryStatus? DeliveryStatus = null,
    string? ErrorCode = null)
{
    public override string ToString() =>
        $"MailGatewayWireResult(Outcome={Outcome}, DeliveryStatus={DeliveryStatus}, ErrorCode={ErrorCode})";
}

internal sealed class MailGatewayWireClient
{
    private const string Contract = "ligclaw-mail-gateway-v1";
    private const string UserAgent = "LIGClaw-MailGateway/1.0";
    private const int MaximumResponseHeaderBytes = 16 * 1024;
    private const int MaximumResponseBodyBytes = 4 * 1024;
    private const int MaximumRequestBodyBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlySet<string> ProbeProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "contract", "idempotencyWindowSeconds", "duplicateSuppression", "operationLookup",
    };
    private static readonly IReadOnlySet<string> FoundProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "contract", "operationId", "status", "draftHash",
    };
    private static readonly IReadOnlySet<string> NotFoundProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "contract", "operationId", "status",
    };
    private readonly HttpClient httpClient;

    public MailGatewayWireClient()
    {
        httpClient = CreateDefaultClient();
    }

    internal MailGatewayWireClient(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        httpClient = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<MailGatewayWireProbeResult> ProbeAsync(
        Uri baseUri,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        if (!IsValidRootHttpsBaseUri(baseUri)) return ProbeFailure("mail.gateway.probe.base_uri_invalid");
        if (!ConnectionHttpBearerTokenPolicy.IsValid(bearerToken))
            return ProbeFailure("mail.gateway.probe.credential_invalid");

        using var request = CreateRequest(
            HttpMethod.Get,
            new Uri(baseUri, "/.well-known/ligclaw-mail-gateway/v1"),
            bearerToken);
        var response = await SendAndReadAsync(request, "mail.gateway.probe", cancellationToken)
            .ConfigureAwait(false);
        if (!response.Succeeded) return ProbeFailure(response.ErrorCode!);
        if (response.StatusCode != HttpStatusCode.OK) return ProbeFailure("mail.gateway.probe.status_rejected");
        return IsExactProbe(response.Body!)
            ? new MailGatewayWireProbeResult(true)
            : ProbeFailure("mail.gateway.probe.response_invalid");
    }

    public async Task<MailGatewayWireResult> SubmitAsync(
        Uri baseUri,
        string bearerToken,
        string sender,
        MailDraft draft,
        MailOutboxOperationId operationId,
        MailOutboxIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!IsValidRootHttpsBaseUri(baseUri)) return NotStarted("mail.gateway.submit.base_uri_invalid");
        if (!ConnectionHttpBearerTokenPolicy.IsValid(bearerToken))
            return NotStarted("mail.gateway.submit.credential_invalid");
        if (!MailAddressPolicy.IsValidAsciiMailbox(sender)) return NotStarted("mail.gateway.submit.sender_invalid");
        if (!TryValidateDraft(draft, out var draftHash)) return NotStarted("mail.gateway.submit.draft_invalid");
        if (operationId is null || idempotencyKey is null) return NotStarted("mail.gateway.submit.identity_invalid");

        var body = CreateSubmitBody(sender, draft, operationId, idempotencyKey, draftHash!);
        if (body.Length > MaximumRequestBodyBytes) return NotStarted("mail.gateway.submit.request_too_large");

        using var request = CreateRequest(
            HttpMethod.Post,
            new Uri(baseUri, "/ligclaw-mail-gateway/v1/submissions"),
            bearerToken);
        request.Headers.Add("Idempotency-Key", idempotencyKey.Value);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        var response = await SendAndReadAsync(request, "mail.gateway.submit", cancellationToken)
            .ConfigureAwait(false);
        if (!response.Succeeded) return Unknown(response.ErrorCode!);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted))
            return Unknown("mail.gateway.submit.status_rejected");
        return ParseOperationResponse(
            response.Body!, operationId, draftHash!, allowNotFound: false, "mail.gateway.submit");
    }

    public async Task<MailGatewayWireResult> ReconcileAsync(
        Uri baseUri,
        string bearerToken,
        MailOutboxHash expectedDraftHash,
        MailOutboxOperationId operationId,
        CancellationToken cancellationToken)
    {
        if (!IsValidRootHttpsBaseUri(baseUri)) return NotStarted("mail.gateway.reconcile.base_uri_invalid");
        if (!ConnectionHttpBearerTokenPolicy.IsValid(bearerToken))
            return NotStarted("mail.gateway.reconcile.credential_invalid");
        if (expectedDraftHash is null) return NotStarted("mail.gateway.reconcile.draft_hash_invalid");
        if (operationId is null) return NotStarted("mail.gateway.reconcile.identity_invalid");

        using var request = CreateRequest(
            HttpMethod.Get,
            new Uri(baseUri, $"/ligclaw-mail-gateway/v1/submissions/{operationId.Value}"),
            bearerToken);
        var response = await SendAndReadAsync(request, "mail.gateway.reconcile", cancellationToken)
            .ConfigureAwait(false);
        if (!response.Succeeded) return Unknown(response.ErrorCode!);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted))
            return Unknown("mail.gateway.reconcile.status_rejected");
        return ParseOperationResponse(
            response.Body!, operationId, expectedDraftHash,
            allowNotFound: response.StatusCode == HttpStatusCode.OK,
            "mail.gateway.reconcile");
    }

    internal static bool IsValidRootHttpsBaseUri(Uri? value) =>
        value is { IsAbsoluteUri: true } &&
        StringComparer.OrdinalIgnoreCase.Equals(value.Scheme, Uri.UriSchemeHttps) &&
        !string.IsNullOrEmpty(value.Host) &&
        string.IsNullOrEmpty(value.UserInfo) &&
        StringComparer.Ordinal.Equals(value.AbsolutePath, "/") &&
        string.IsNullOrEmpty(value.Query) &&
        string.IsNullOrEmpty(value.Fragment);

    internal static HttpClient CreateDefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 16,
            MaxConnectionsPerServer = 1,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = RequestTimeout,
        };
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string bearerToken)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.ConnectionClose = true;
        if (method == HttpMethod.Get)
            request.Content = new ByteArrayContent([]);
        return request;
    }

    private async Task<ReadResponse> SendAndReadAsync(
        HttpRequestMessage request,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode)) return ReadResponse.Failed($"{errorPrefix}.redirect_denied");
            if (!HasBoundedHeaders(response)) return ReadResponse.Failed($"{errorPrefix}.response_headers_too_large");
            if (!IsJsonUtf8(response.Content?.Headers.ContentType))
                return ReadResponse.Failed($"{errorPrefix}.content_type_invalid");
            if (response.Content?.Headers.ContentEncoding.Any() is true)
                return ReadResponse.Failed($"{errorPrefix}.content_encoding_rejected");
            if (response.Content?.Headers.ContentLength > MaximumResponseBodyBytes)
                return ReadResponse.Failed($"{errorPrefix}.response_too_large");
            var body = await ReadBodyAsync(response.Content!, timeout.Token).ConfigureAwait(false);
            return body is null
                ? ReadResponse.Failed($"{errorPrefix}.response_too_large")
                : ReadResponse.Success(response.StatusCode, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ReadResponse.Failed($"{errorPrefix}.timeout");
        }
        catch
        {
            return ReadResponse.Failed($"{errorPrefix}.transport_failed");
        }
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[1_024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > MaximumResponseBodyBytes) return null;
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool HasBoundedHeaders(HttpResponseMessage response)
    {
        var total = 0;
        return TryAddHeaders(response.Headers, ref total) &&
               TryAddHeaders(response.Content.Headers, ref total);
    }

    private static bool TryAddHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers,
        ref int total)
    {
        foreach (var header in headers)
        {
            total += Encoding.UTF8.GetByteCount(header.Key) + 4;
            foreach (var value in header.Value)
            {
                total += Encoding.UTF8.GetByteCount(value) + 2;
                if (total > MaximumResponseHeaderBytes) return false;
            }
        }
        return total <= MaximumResponseHeaderBytes;
    }

    private static bool IsJsonUtf8(MediaTypeHeaderValue? contentType) =>
        contentType is not null &&
        StringComparer.OrdinalIgnoreCase.Equals(contentType.MediaType, "application/json") &&
        StringComparer.OrdinalIgnoreCase.Equals(contentType.CharSet, "utf-8");

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        (int)statusCode is >= 300 and <= 399;

    private static byte[] CreateSubmitBody(
        string sender,
        MailDraft draft,
        MailOutboxOperationId operationId,
        MailOutboxIdempotencyKey idempotencyKey,
        MailOutboxHash draftHash)
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteAscii(buffer, "{\"contract\":");
        WriteJsonString(buffer, Contract);
        WriteAscii(buffer, ",\"operationId\":");
        WriteJsonString(buffer, operationId.Value);
        WriteAscii(buffer, ",\"idempotencyKey\":");
        WriteJsonString(buffer, idempotencyKey.Value);
        WriteAscii(buffer, ",\"draftHash\":");
        WriteJsonString(buffer, draftHash.Value);
        WriteAscii(buffer, ",\"sender\":");
        WriteJsonString(buffer, sender);
        WriteJsonArray(buffer, "to", draft.To);
        WriteJsonArray(buffer, "cc", draft.Cc);
        WriteJsonArray(buffer, "bcc", draft.Bcc);
        WriteAscii(buffer, ",\"subject\":");
        WriteJsonString(buffer, draft.Subject);
        WriteAscii(buffer, ",\"body\":");
        WriteJsonString(buffer, draft.Body);
        WriteAscii(buffer, "}");
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteJsonArray(ArrayBufferWriter<byte> buffer, string name, IReadOnlyList<string> addresses)
    {
        WriteAscii(buffer, $",\"{name}\":[");
        for (var index = 0; index < addresses.Count; index++)
        {
            if (index != 0) WriteAscii(buffer, ",");
            WriteJsonString(buffer, addresses[index]);
        }
        WriteAscii(buffer, "]");
    }

    private static void WriteJsonString(ArrayBufferWriter<byte> buffer, string value)
    {
        WriteAscii(buffer, "\"");
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"': WriteAscii(buffer, "\\\""); break;
                case '\\': WriteAscii(buffer, "\\\\"); break;
                case '\b': WriteAscii(buffer, "\\b"); break;
                case '\f': WriteAscii(buffer, "\\f"); break;
                case '\n': WriteAscii(buffer, "\\n"); break;
                case '\r': WriteAscii(buffer, "\\r"); break;
                case '\t': WriteAscii(buffer, "\\t"); break;
                default:
                    if (rune.Value < 0x20)
                    {
                        WriteAscii(buffer, $"\\u{rune.Value:x4}");
                    }
                    else
                    {
                        WriteUtf8(buffer, rune.ToString());
                    }
                    break;
            }
        }
        WriteAscii(buffer, "\"");
    }

    private static void WriteAscii(ArrayBufferWriter<byte> buffer, string value) => WriteUtf8(buffer, value);

    private static void WriteUtf8(ArrayBufferWriter<byte> buffer, string value)
    {
        var byteCount = StrictUtf8.GetByteCount(value);
        var target = buffer.GetSpan(byteCount);
        StrictUtf8.GetBytes(value, target);
        buffer.Advance(byteCount);
    }

    private static bool TryValidateDraft(MailDraft? draft, out MailOutboxHash? draftHash)
    {
        draftHash = null;
        if (draft is null) return false;
        try
        {
            if (!MailOutboxHash.TryParse(draft.CanonicalHash, out draftHash) ||
                !StringComparer.Ordinal.Equals(MailDraftPolicy.ComputeCanonicalHash(draft), draft.CanonicalHash))
            {
                draftHash = null;
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or EncoderFallbackException)
        {
            draftHash = null;
            return false;
        }
    }

    private static bool IsExactProbe(byte[] body)
    {
        if (!TryReadObject(body, ProbeProperties, out var document))
            return false;
        using (document)
        {
            var root = document.RootElement;
            return root.GetProperty("contract").ValueKind == JsonValueKind.String &&
                   StringComparer.Ordinal.Equals(root.GetProperty("contract").GetString(), Contract) &&
                   root.GetProperty("idempotencyWindowSeconds").ValueKind == JsonValueKind.Number &&
                   root.GetProperty("idempotencyWindowSeconds").TryGetInt32(out var window) && window == 300 &&
                   root.GetProperty("duplicateSuppression").ValueKind == JsonValueKind.String &&
                   StringComparer.Ordinal.Equals(
                       root.GetProperty("duplicateSuppression").GetString(), "same-key-same-operation") &&
                   root.GetProperty("operationLookup").ValueKind is JsonValueKind.True;
        }
    }

    private static MailGatewayWireResult ParseOperationResponse(
        byte[] body,
        MailOutboxOperationId operationId,
        MailOutboxHash draftHash,
        bool allowNotFound,
        string errorPrefix)
    {
        if (!TryReadObject(body, null, out var document)) return Unknown($"{errorPrefix}.response_invalid");
        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("contract", out var contract) || contract.ValueKind != JsonValueKind.String ||
                !StringComparer.Ordinal.Equals(contract.GetString(), Contract) ||
                !root.TryGetProperty("operationId", out var responseOperationId) ||
                responseOperationId.ValueKind != JsonValueKind.String ||
                !StringComparer.Ordinal.Equals(responseOperationId.GetString(), operationId.Value))
                return Unknown($"{errorPrefix}.response_invalid");

            if (StringComparer.Ordinal.Equals(status.GetString(), "not_found"))
            {
                return allowNotFound && HasExactProperties(root, NotFoundProperties)
                    ? new MailGatewayWireResult(MailGatewayWireOutcome.NotFound, ErrorCode: "mail.gateway.reconcile.not_found")
                    : Unknown($"{errorPrefix}.response_invalid");
            }

            if (!HasExactProperties(root, FoundProperties) ||
                !root.TryGetProperty("draftHash", out var responseDraftHash) ||
                responseDraftHash.ValueKind != JsonValueKind.String ||
                !StringComparer.Ordinal.Equals(responseDraftHash.GetString(), draftHash.Value))
                return Unknown($"{errorPrefix}.response_invalid");

            return status.GetString() switch
            {
                "queued" => new MailGatewayWireResult(
                    MailGatewayWireOutcome.Accepted, MailGatewayWireDeliveryStatus.Queued),
                "delivered" => new MailGatewayWireResult(
                    MailGatewayWireOutcome.Accepted, MailGatewayWireDeliveryStatus.Delivered),
                "failed" => new MailGatewayWireResult(
                    MailGatewayWireOutcome.KnownFailed, ErrorCode: $"{errorPrefix}.remote_failed"),
                _ => Unknown($"{errorPrefix}.response_invalid"),
            };
        }
    }

    private static bool TryReadObject(byte[] body, IReadOnlySet<string>? expected, out JsonDocument document)
    {
        document = null!;
        try
        {
            document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                (expected is null || HasExactProperties(document.RootElement, expected)))
                return true;
            document.Dispose();
            document = null!;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasExactProperties(JsonElement value, IReadOnlySet<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !found.Add(property.Name)) return false;
        }
        return found.SetEquals(expected);
    }

    private static MailGatewayWireProbeResult ProbeFailure(string errorCode) => new(false, errorCode);

    private static MailGatewayWireResult Unknown(string errorCode) =>
        new(MailGatewayWireOutcome.OutcomeUnknown, ErrorCode: errorCode);

    private static MailGatewayWireResult NotStarted(string errorCode) =>
        new(MailGatewayWireOutcome.NotStarted, ErrorCode: errorCode);

    private sealed record ReadResponse(HttpStatusCode StatusCode, byte[]? Body, string? ErrorCode)
    {
        public bool Succeeded => ErrorCode is null;
        public static ReadResponse Success(HttpStatusCode statusCode, byte[] body) => new(statusCode, body, null);
        public static ReadResponse Failed(string errorCode) => new(default, null, errorCode);
    }
}
