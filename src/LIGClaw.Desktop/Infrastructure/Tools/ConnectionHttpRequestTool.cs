using System.Buffers;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class ConnectionHttpRequestTool : IWorkflowOneShotToolAdapter
{
    private const int MaximumResponseHeaderBytes = 16 * 1024;
    private const int MaximumResponseBytes = 48 * 1024;
    private const int MaximumTextRunes = 16_000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly IConnectionHttpExecutionResolver resolver;
    private readonly HttpClient httpClient;

    public ConnectionHttpRequestTool(IConnectionHttpExecutionResolver resolver)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        httpClient = CreateDefaultHttpClient();
    }

    internal ConnectionHttpRequestTool(
        IConnectionHttpExecutionResolver resolver,
        HttpMessageHandler handler)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        ArgumentNullException.ThrowIfNull(handler);
        httpClient = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    public string Name => "connection.http.request.v1";
    public string Risk => "R3";
    public WindowsCapability RequiredCapabilities => WindowsCapability.WebAccess;
    public int Priority => 0;
    public TimeSpan Timeout => TimeSpan.FromSeconds(35);

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(
        IReadOnlyDictionary<string, object?> input) => null;

    public Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken) =>
        Task.FromResult(Failure(
            "이 연결 요청은 고정된 Workflow 컨텍스트에서만 실행할 수 있어요.",
            "connection.http.context_required"));

    public async Task<WorkflowEffectPreparationResult> PrepareWorkflowEffectAsync(
        ToolInvokeParams invocation,
        DateTimeOffset nowUtc,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.CapabilityContext is not { } context ||
            nowUtc.Offset != TimeSpan.Zero || lifetime <= TimeSpan.Zero ||
            !IsExactCapabilityContext(context))
            return PreparationFailure(
                "고정된 Workflow 연결 컨텍스트를 확인하지 못했어요.",
                "connection.http.context_required");
        if (!TryReadInputSafely(invocation.Input, out var input) ||
            !ConnectionHttpTemplatePolicy.TryBuild(
                input.Method,
                input.PathTemplate,
                input.PathParameters,
                input.QueryParameters,
                out var target,
                out _))
            return PreparationFailure(
                "연결 요청 입력이 허용된 형식과 일치하지 않아요.",
                "connection.http.input_invalid");

        ConnectionHttpExecutionResolution resolution;
        try
        {
            resolution = await resolver.ResolveAsync(
                context,
                input.ConnectionSlot,
                ConnectionHttpResolutionPurpose.Preparation,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return PreparationFailure(
                "등록된 연결을 확인하지 못했어요.",
                "connection.http.binding_unavailable");
        }
        if (!resolution.Success || resolution.Binding is null)
            return PreparationFailure(
                "등록된 연결을 확인하지 못했어요.",
                NormalizeResolverError(resolution.ErrorCode));
        var binding = resolution.Binding;
        if (!StringComparer.Ordinal.Equals(
                binding.FrozenBindingHash,
                context.ConnectionBindingHash))
            return PreparationFailure(
                "Workflow에 고정된 연결이 현재 연결과 일치하지 않아요.",
                "connection.http.binding_mismatch");
        if (!TryValidateBinding(binding, requireCredential: false, out var canonicalBaseUri))
            return PreparationFailure(
                "등록된 연결 설정을 안전하게 사용할 수 없어요.",
                "connection.http.binding_invalid");
        if (!TryBuildAbsoluteRequestUri(canonicalBaseUri, target!.RelativeTarget, out _))
            return PreparationFailure(
                "연결 요청 경로가 등록된 연결 범위를 벗어나요.",
                "connection.http.input_invalid");

        var preparedBinding = new PreparedBinding(
            string.Concat(binding.DisplayName),
            canonicalBaseUri,
            string.Concat(binding.FrozenBindingHash),
            string.Concat(binding.ExecutionFingerprint),
            binding.AuthenticationKind);
        return new WorkflowEffectPreparationResult(
            true,
            new PreparedConnectionHttpEffect(
                resolver,
                httpClient,
                CopyContext(context),
                string.Concat(input.ConnectionSlot),
                string.Concat(input.PathTemplate),
                string.Concat(input.Reason),
                string.Concat(target.RelativeTarget),
                string.Concat(target.CanonicalMaterial),
                preparedBinding,
                nowUtc.Add(lifetime)));
    }

    internal static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression =
                DecompressionMethods.GZip |
                DecompressionMethods.Deflate |
                DecompressionMethods.Brotli,
            MaxResponseHeadersLength = MaximumResponseHeaderBytes / 1024,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = RequestTimeout,
        };
    }

    private static bool TryReadInput(
        IReadOnlyDictionary<string, object?> source,
        out RequestInput input)
    {
        input = default!;
        if (!ToolInputReader.HasOnlyKeys(
                source,
                "connectionSlot",
                "method",
                "pathTemplate",
                "reason",
                "pathParameters",
                "queryParameters") ||
            !TryReadExactString(source, "connectionSlot", 1, 64, out var connectionSlot) ||
            !TryReadExactString(source, "method", 3, 3, out var method) ||
            !StringComparer.Ordinal.Equals(method, "GET") ||
            !TryReadExactString(source, "pathTemplate", 1, 512, out var pathTemplate) ||
            !TryReadExactString(source, "reason", 1, 160, out var reason) ||
            !TryReadParameters(source, "pathParameters", 16, out var pathParameters) ||
            !TryReadParameters(source, "queryParameters", 32, out var queryParameters))
            return false;
        input = new RequestInput(
            connectionSlot,
            method,
            pathTemplate,
            reason,
            pathParameters,
            queryParameters);
        return true;
    }

    private static bool TryReadInputSafely(
        IReadOnlyDictionary<string, object?> source,
        out RequestInput input)
    {
        try
        {
            return TryReadInput(source, out input);
        }
        catch (Exception)
        {
            input = default!;
            return false;
        }
    }

    private static bool TryReadParameters(
        IReadOnlyDictionary<string, object?> source,
        string key,
        int maximumItems,
        out IReadOnlyList<ConnectionHttpParameter> parameters)
    {
        parameters = [];
        if (!source.TryGetValue(key, out var raw)) return true;
        IEnumerable<object?>? items = raw switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } element =>
                element.EnumerateArray().Select(item => (object?)item.Clone()),
            IEnumerable<object?> direct => direct,
            _ => null,
        };
        if (items is null) return false;
        var result = new List<ConnectionHttpParameter>();
        foreach (var item in items)
        {
            if (result.Count == maximumItems ||
                !TryReadParameter(item, out var parameter))
                return false;
            result.Add(parameter);
        }
        parameters = result;
        return true;
    }

    private static bool TryReadParameter(object? raw, out ConnectionHttpParameter parameter)
    {
        parameter = default!;
        IReadOnlyDictionary<string, object?>? fields = raw switch
        {
            IReadOnlyDictionary<string, object?> direct => direct,
            IDictionary<string, object?> dictionary =>
                new Dictionary<string, object?>(dictionary, StringComparer.Ordinal),
            JsonElement { ValueKind: JsonValueKind.Object } element => element
                .EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => (object?)property.Value.Clone(),
                    StringComparer.Ordinal),
            _ => null,
        };
        if (fields is null || !ToolInputReader.HasOnlyKeys(fields, "name", "value") ||
            !TryReadExactString(fields, "name", 1, 64, out var name) ||
            !TryReadExactString(fields, "value", 0, 2_048, out var value))
            return false;
        parameter = new ConnectionHttpParameter(name, value);
        return true;
    }

    private static bool TryReadExactString(
        IReadOnlyDictionary<string, object?> source,
        string key,
        int minimumRunes,
        int maximumRunes,
        out string value)
    {
        value = string.Empty;
        if (!source.TryGetValue(key, out var raw)) return false;
        var text = raw switch
        {
            string direct => direct,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
        if (text is null) return false;
        var runeCount = text.EnumerateRunes().Count();
        if (runeCount < minimumRunes || runeCount > maximumRunes ||
            text.EnumerateRunes().Any(rune => Rune.IsControl(rune)))
            return false;
        value = string.Concat(text);
        return true;
    }

    private static bool IsExactCapabilityContext(CapabilityInvocationContext context)
    {
        if (!CapabilityRuntimeSnapshotHash.TryParse(context.SnapshotHash, out _) ||
            !IsHash(context.ContentHash) || !IsHash(context.ConnectionBindingHash) ||
            context.CatalogRevision is < 1 or > 9_007_199_254_740_991L)
            return false;
        try
        {
            var exactVersion = new CapabilityVersionRef(
                CapabilityId.Parse(context.CapabilityId),
                CapabilitySemanticVersion.Parse(context.Version),
                CapabilityContentHash.Parse(context.ContentHash));
            return StringComparer.Ordinal.Equals(
                context.ModelToolName,
                CapabilityRuntimeHasher.ComputeModelToolName(exactVersion));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static bool TryValidateBinding(
        ConnectionHttpRuntimeBinding binding,
        bool requireCredential,
        out string canonicalBaseUri)
    {
        canonicalBaseUri = string.Empty;
        try
        {
            if (binding.BaseUri is null || binding.DisplayName is null ||
                binding.FrozenBindingHash is null || binding.ExecutionFingerprint is null ||
                !binding.BaseUri.IsAbsoluteUri ||
                binding.BaseUri.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(binding.BaseUri.UserInfo) ||
                !string.IsNullOrEmpty(binding.BaseUri.Query) ||
                !string.IsNullOrEmpty(binding.BaseUri.Fragment) ||
                binding.DisplayName.EnumerateRunes().Count() is < 1 or > 128 ||
                binding.DisplayName.EnumerateRunes().Any(rune => Rune.IsControl(rune)) ||
                !IsHash(binding.FrozenBindingHash) ||
                !IsHash(binding.ExecutionFingerprint) ||
                binding.AuthenticationKind is not (
                    ConnectionHttpAuthenticationKind.None or
                    ConnectionHttpAuthenticationKind.Bearer))
                return false;
            if (binding.AuthenticationKind == ConnectionHttpAuthenticationKind.None &&
                binding.Credential is not null)
                return false;
            if (binding.AuthenticationKind == ConnectionHttpAuthenticationKind.Bearer)
            {
                if (requireCredential
                        ? !IsSafeCredential(binding.Credential)
                        : binding.Credential is not null)
                    return false;
            }

            var baseUri = binding.BaseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
                ? binding.BaseUri
                : new Uri($"{binding.BaseUri.AbsoluteUri}/", UriKind.Absolute);
            canonicalBaseUri = baseUri.AbsoluteUri;
            return canonicalBaseUri.Length <= 2_048;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsSafeCredential(ConnectionHttpCredential? credential) =>
        credential is not null && ConnectionHttpBearerTokenPolicy.IsValid(credential.Reveal());

    private static bool TryBuildAbsoluteRequestUri(
        string canonicalBaseUri,
        string relativeTarget,
        out Uri requestUri)
    {
        requestUri = default!;
        try
        {
            var baseUri = new Uri(canonicalBaseUri, UriKind.Absolute);
            var candidate = new Uri(baseUri, relativeTarget);
            var expectedAbsoluteUri = string.Concat(canonicalBaseUri, relativeTarget);
            if (!StringComparer.Ordinal.Equals(candidate.AbsoluteUri, expectedAbsoluteUri) ||
                !candidate.AbsoluteUri.StartsWith(canonicalBaseUri, StringComparison.Ordinal))
                return false;
            requestUri = candidate;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsHash(string? value) =>
        value is not null && value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static string NormalizeResolverError(string? errorCode) => errorCode switch
    {
        "connection.http.binding_unavailable" => errorCode,
        "connection.http.binding_mismatch" => errorCode,
        "connection.http.credential_unavailable" => errorCode,
        _ => "connection.http.binding_unavailable",
    };

    private static CapabilityInvocationContext CopyContext(CapabilityInvocationContext context) =>
        new(
            string.Concat(context.SnapshotHash),
            string.Concat(context.ModelToolName),
            string.Concat(context.CapabilityId),
            string.Concat(context.Version),
            string.Concat(context.ContentHash),
            context.CatalogRevision,
            string.Concat(context.ConnectionBindingHash));

    private static WorkflowEffectPreparationResult PreparationFailure(
        string message,
        string errorCode) => new(false, Error: message, ErrorCode: errorCode);

    private static WindowsToolExecutionResult Failure(string message, string errorCode) =>
        new(false, new Dictionary<string, object?>(), message, ErrorCode: errorCode);

    private sealed record RequestInput(
        string ConnectionSlot,
        string Method,
        string PathTemplate,
        string Reason,
        IReadOnlyList<ConnectionHttpParameter> PathParameters,
        IReadOnlyList<ConnectionHttpParameter> QueryParameters);

    private sealed record PreparedBinding(
        string DisplayName,
        string CanonicalBaseUri,
        string FrozenBindingHash,
        string ExecutionFingerprint,
        ConnectionHttpAuthenticationKind AuthenticationKind);

    private sealed class PreparedConnectionHttpEffect : IPreparedWorkflowEffect
    {
        private readonly IConnectionHttpExecutionResolver resolver;
        private readonly HttpClient httpClient;
        private readonly CapabilityInvocationContext context;
        private readonly string connectionSlot;
        private readonly string relativeTarget;
        private readonly PreparedBinding preparedBinding;
        private int state;

        public PreparedConnectionHttpEffect(
            IConnectionHttpExecutionResolver resolver,
            HttpClient httpClient,
            CapabilityInvocationContext context,
            string connectionSlot,
            string pathTemplate,
            string reason,
            string relativeTarget,
            string canonicalMaterial,
            PreparedBinding preparedBinding,
            DateTimeOffset expiresAtUtc)
        {
            this.resolver = resolver;
            this.httpClient = httpClient;
            this.context = context;
            this.connectionSlot = connectionSlot;
            this.relativeTarget = relativeTarget;
            this.preparedBinding = preparedBinding;
            ExpiresAtUtc = expiresAtUtc;
            ApprovalPrompt = new WindowsToolApprovalPrompt(
                "등록된 연결로 HTTP 요청을 보낼까요?",
                $"{preparedBinding.DisplayName} 연결에 승인된 GET 요청을 보냅니다.",
                $"상대 요청 대상: {relativeTarget}\n경로 템플릿: {pathTemplate}\n\n요청 이유: {reason}",
                Risk);
            CanonicalPayloadHash = WorkflowPreparedEffectHash.Compute(
                ToolName,
                Risk,
                connectionSlot,
                canonicalMaterial,
                reason);
            CanonicalBindingHash = WorkflowPreparedEffectHash.Compute(
                ToolName,
                context.SnapshotHash,
                context.ModelToolName,
                context.CapabilityId,
                context.Version,
                context.ContentHash,
                context.CatalogRevision.ToString(CultureInfo.InvariantCulture),
                context.ConnectionBindingHash,
                connectionSlot,
                preparedBinding.CanonicalBaseUri,
                preparedBinding.FrozenBindingHash,
                preparedBinding.ExecutionFingerprint,
                preparedBinding.AuthenticationKind.ToString());
        }

        public string ToolName => "connection.http.request.v1";
        public string Risk => "R3";
        public string CanonicalPayloadHash { get; }
        public string CanonicalBindingHash { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public WindowsToolApprovalPrompt ApprovalPrompt { get; }

        public async Task<WorkflowPreparedEffectExecution> ExecuteOnceAsync(
            DateTimeOffset trustedNowUtc,
            CancellationToken cancellationToken)
        {
            if (trustedNowUtc.Offset != TimeSpan.Zero || trustedNowUtc >= ExpiresAtUtc)
            {
                _ = Interlocked.CompareExchange(ref state, 2, 0);
                return NotStarted(
                    "준비된 연결 요청이 만료됐어요.",
                    "connection.http.expired");
            }
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
                return NotStarted(
                    "준비된 연결 요청은 이미 소비됐어요.",
                    "connection.http.already_consumed");
            if (cancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref state, 2);
                return NotStarted(
                    "연결 요청을 보내기 전에 실행이 중단됐어요.",
                    "connection.http.cancelled");
            }

            ConnectionHttpExecutionResolution resolution;
            try
            {
                resolution = await resolver.ResolveAsync(
                    context,
                    connectionSlot,
                    ConnectionHttpResolutionPurpose.Execution,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref state, 2);
                return NotStarted(
                    "연결 요청을 보내기 전에 실행이 중단됐어요.",
                    "connection.http.cancelled");
            }
            catch (Exception)
            {
                Volatile.Write(ref state, 2);
                return NotStarted(
                    "등록된 연결을 다시 확인하지 못했어요.",
                    "connection.http.binding_unavailable");
            }
            await using var executionLease = resolution.ExecutionLease;
            if (!resolution.Success || resolution.Binding is null)
            {
                Volatile.Write(ref state, 2);
                return NotStarted(
                    "등록된 연결을 다시 확인하지 못했어요.",
                    NormalizeResolverError(resolution.ErrorCode));
            }
            var currentBinding = resolution.Binding;
            if (!TryValidateBinding(currentBinding, requireCredential: true, out var canonicalBaseUri))
            {
                Volatile.Write(ref state, 2);
                return NotStarted(
                    "현재 등록된 연결 설정을 안전하게 사용할 수 없어요.",
                    "connection.http.binding_invalid");
            }
            if (!StringComparer.Ordinal.Equals(
                    currentBinding.FrozenBindingHash,
                    context.ConnectionBindingHash) ||
                !StringComparer.Ordinal.Equals(
                    currentBinding.FrozenBindingHash,
                    preparedBinding.FrozenBindingHash) ||
                !StringComparer.Ordinal.Equals(
                    currentBinding.ExecutionFingerprint,
                    preparedBinding.ExecutionFingerprint) ||
                !StringComparer.Ordinal.Equals(
                    canonicalBaseUri,
                    preparedBinding.CanonicalBaseUri) ||
                currentBinding.AuthenticationKind != preparedBinding.AuthenticationKind)
            {
                Volatile.Write(ref state, 2);
                return NotStarted(
                    "승인 후 등록된 연결이 변경되어 요청을 보내지 않았어요.",
                    "connection.http.binding_mismatch");
            }
            if (cancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref state, 2);
                return NotStarted(
                    "연결 요청을 보내기 전에 실행이 중단됐어요.",
                    "connection.http.cancelled");
            }

            if (!TryCreateRequest(currentBinding, canonicalBaseUri, relativeTarget, out var request))
            {
                Volatile.Write(ref state, 2);
                return NotStarted(
                    "승인된 연결 요청을 안전하게 만들지 못했어요.",
                    "connection.http.binding_invalid");
            }
            using (request)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeout);
                try
                {
                    using var response = await httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token).ConfigureAwait(false);
                    var statusCode = (int)response.StatusCode;
                    if (statusCode is >= 300 and <= 399)
                        return Finish(FailedKnown(
                            "응답이 다른 위치로 이동하라는 요청을 반환해 전송을 중단했어요.",
                            "connection.http.redirect_denied"));
                    if (statusCode is < 200 or > 299)
                        return Finish(FailedKnown(
                            "등록된 연결이 성공 응답을 반환하지 않았어요.",
                            "connection.http.remote_failed"));
                    if (CountResponseHeaderBytes(response) > MaximumResponseHeaderBytes)
                        return Finish(FailedKnown(
                            "연결 응답 헤더가 허용된 크기를 초과했어요.",
                            "connection.http.response_too_large"));
                    if (!TryGetAllowedContentType(response.Content.Headers.ContentType, out var contentType))
                        return Finish(FailedKnown(
                            "연결 응답의 콘텐츠 형식을 사용할 수 없어요.",
                            "connection.http.content_unsupported"));
                    if (ContainsCredentialReflection(contentType, currentBinding.Credential))
                        return Finish(FailedKnown(
                            "연결 응답에 자격 증명 값이 포함되어 결과를 폐기했어요.",
                            "connection.http.credential_reflected"));
                    if (response.Content.Headers.ContentEncoding.Count > 0)
                        return Finish(FailedKnown(
                            "연결 응답의 콘텐츠 인코딩을 안전하게 해제하지 못했어요.",
                            "connection.http.content_unsupported"));
                    var bytes = await ReadBoundedBodyAsync(
                        response.Content,
                        timeout.Token).ConfigureAwait(false);
                    if (bytes is null)
                        return Finish(FailedKnown(
                            "연결 응답 본문이 허용된 크기를 초과했어요.",
                            "connection.http.response_too_large"));
                    string text;
                    try
                    {
                        text = new UTF8Encoding(
                            encoderShouldEmitUTF8Identifier: false,
                            throwOnInvalidBytes: true).GetString(bytes);
                    }
                    catch (DecoderFallbackException)
                    {
                        return Finish(FailedKnown(
                            "연결 응답이 올바른 UTF-8 텍스트가 아니에요.",
                            "connection.http.content_unsupported"));
                    }
                    if (ContainsCredentialReflection(text, currentBinding.Credential))
                        return Finish(FailedKnown(
                            "연결 응답에 자격 증명 값이 포함되어 결과를 폐기했어요.",
                            "connection.http.credential_reflected"));
                    var boundedText = TruncateRunes(text, MaximumTextRunes, out var truncated);
                    return Finish(new WorkflowPreparedEffectExecution(
                        WorkflowEffectDisposition.Succeeded,
                        new WindowsToolExecutionResult(
                            true,
                            new Dictionary<string, object?>
                            {
                                ["statusCode"] = (long)statusCode,
                                ["contentType"] = contentType,
                                ["text"] = boundedText,
                                ["truncated"] = truncated,
                                ["responseBytes"] = (long)bytes.Length,
                                ["untrusted"] = true,
                            },
                            ActivitySummary: "승인된 등록 연결에서 제한된 텍스트 응답을 받았어요.")));
                }
                catch (OperationCanceledException)
                {
                    var code = cancellationToken.IsCancellationRequested
                        ? "connection.http.cancelled"
                        : "connection.http.timeout";
                    return Finish(Unknown(
                        "요청 전송 후 결과를 확정하지 못했어요.",
                        code));
                }
                catch (Exception)
                {
                    return Finish(Unknown(
                        "요청 전송 후 결과를 확정하지 못했어요.",
                        "connection.http.transport_failed"));
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _ = Interlocked.CompareExchange(ref state, 2, 0);
            return ValueTask.CompletedTask;
        }

        private WorkflowPreparedEffectExecution Finish(WorkflowPreparedEffectExecution execution)
        {
            Volatile.Write(ref state, 2);
            return execution;
        }

        private static bool TryCreateRequest(
            ConnectionHttpRuntimeBinding binding,
            string canonicalBaseUri,
            string relativeTarget,
            out HttpRequestMessage request)
        {
            request = default!;
            if (!TryBuildAbsoluteRequestUri(canonicalBaseUri, relativeTarget, out var requestUri))
                return false;
            HttpRequestMessage? candidate = null;
            try
            {
                candidate = new HttpRequestMessage(HttpMethod.Get, requestUri)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    // SocketsHttpHandler may replay a bodyless request after the
                    // headers were sent when the peer closes before responding.
                    // A non-null, zero-length body makes that path non-replayable
                    // while preserving this contract's bodyless GET semantics.
                    Content = new ByteArrayContent([]),
                };
                candidate.Headers.ConnectionClose = true;
                candidate.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                candidate.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
                candidate.Headers.UserAgent.ParseAdd("LIGClaw-ConnectionHttp/1.0");
                if (binding.AuthenticationKind == ConnectionHttpAuthenticationKind.Bearer)
                    candidate.Headers.Authorization = new AuthenticationHeaderValue(
                        "Bearer",
                        binding.Credential!.Reveal());
                request = candidate;
                return true;
            }
            catch (Exception)
            {
                candidate?.Dispose();
                return false;
            }
        }

        private static async Task<byte[]?> ReadBoundedBodyAsync(
            HttpContent content,
            CancellationToken cancellationToken)
        {
            if (content.Headers.ContentLength is > MaximumResponseBytes) return null;
            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(8_192);
            try
            {
                using var output = new MemoryStream(MaximumResponseBytes);
                while (true)
                {
                    var remaining = MaximumResponseBytes + 1 - checked((int)output.Length);
                    if (remaining <= 0) return null;
                    var read = await stream.ReadAsync(
                        buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0) return output.ToArray();
                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        private static int CountResponseHeaderBytes(HttpResponseMessage response)
        {
            var total = 0;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                foreach (var value in header.Value)
                {
                    total = checked(total + Encoding.UTF8.GetByteCount(header.Key) + 2 +
                                    Encoding.UTF8.GetByteCount(value) + 2);
                    if (total > MaximumResponseHeaderBytes) return total;
                }
            }
            return total;
        }

        private static bool TryGetAllowedContentType(
            MediaTypeHeaderValue? header,
            out string contentType)
        {
            contentType = string.Empty;
            var mediaType = header?.MediaType;
            var charset = header?.CharSet?.Trim('"');
            if (mediaType is null || mediaType.Length is < 1 or > 128 ||
                charset is not null && !StringComparer.OrdinalIgnoreCase.Equals(charset, "utf-8"))
                return false;
            var normalized = mediaType.ToLowerInvariant();
            if (normalized != "application/json" &&
                normalized != "text/plain" &&
                !normalized.EndsWith("+json", StringComparison.Ordinal))
                return false;
            contentType = normalized;
            return true;
        }

        private static string TruncateRunes(string value, int maximumRunes, out bool truncated)
        {
            var runes = value.EnumerateRunes();
            var builder = new StringBuilder(Math.Min(value.Length, maximumRunes));
            var count = 0;
            foreach (var rune in runes)
            {
                if (count == maximumRunes)
                {
                    truncated = true;
                    return builder.ToString();
                }
                builder.Append(rune);
                count++;
            }
            truncated = false;
            return value;
        }

        private static bool ContainsCredentialReflection(
            string value,
            ConnectionHttpCredential? credential)
        {
            if (credential is null) return false;
            var secret = credential.Reveal();
            if (value.Contains(secret, StringComparison.Ordinal)) return true;
            if (!value.Contains('\\')) return false;

            var decoded = new StringBuilder(value.Length);
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character != '\\' || index + 1 >= value.Length)
                {
                    decoded.Append(character);
                    continue;
                }

                var escaped = value[index + 1];
                switch (escaped)
                {
                    case '"':
                    case '\\':
                    case '/':
                        decoded.Append(escaped);
                        index++;
                        break;
                    case 'b':
                        decoded.Append('\b');
                        index++;
                        break;
                    case 'f':
                        decoded.Append('\f');
                        index++;
                        break;
                    case 'n':
                        decoded.Append('\n');
                        index++;
                        break;
                    case 'r':
                        decoded.Append('\r');
                        index++;
                        break;
                    case 't':
                        decoded.Append('\t');
                        index++;
                        break;
                    case 'u' when index + 5 < value.Length &&
                                       ushort.TryParse(
                                           value.AsSpan(index + 2, 4),
                                           NumberStyles.HexNumber,
                                           CultureInfo.InvariantCulture,
                                           out var codeUnit):
                        decoded.Append((char)codeUnit);
                        index += 5;
                        break;
                    default:
                        decoded.Append(character);
                        break;
                }
            }
            return decoded.ToString().Contains(secret, StringComparison.Ordinal);
        }

        private static WorkflowPreparedEffectExecution NotStarted(string message, string errorCode) =>
            new(
                WorkflowEffectDisposition.NotStarted,
                Failure(message, errorCode));

        private static WorkflowPreparedEffectExecution FailedKnown(string message, string errorCode) =>
            new(
                WorkflowEffectDisposition.FailedKnown,
                Failure(message, errorCode));

        private static WorkflowPreparedEffectExecution Unknown(string message, string errorCode) =>
            new(
                WorkflowEffectDisposition.Unknown,
                Failure(message, errorCode));
    }
}
