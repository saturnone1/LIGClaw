using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using LIGClaw.Application.Capabilities;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class ConnectionHttpRequestToolTests
{
    private const string Secret = "fixture-secret-that-must-not-leak";

    [Fact]
    public async Task DirectInvocationAndMissingCapabilityContextFailClosedWithoutSending()
    {
        var handler = new RecordingHandler(_ => JsonResponse("{}"));
        var resolver = new SequenceResolver([Binding()]);
        var tool = new ConnectionHttpRequestTool(resolver, handler);
        var host = new WindowsToolHost(Profile(), [tool]);
        var direct = await host.ExecuteAsync(Invocation(Context()));
        var missingContext = await host.PrepareWorkflowEffectAsync(
            Invocation(null),
            DateTimeOffset.Parse("2026-08-02T00:00:00Z"));

        Assert.False(direct.Success);
        Assert.Equal("connection.http.context_required", direct.ErrorCode);
        Assert.False(missingContext.Success);
        Assert.Equal("connection.http.context_required", missingContext.ErrorCode);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task SendsOneEscapedGetWithRuntimeCredentialAndReturnsOnlyBoundedUntrustedText()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal("Bearer", request.AuthorizationScheme);
            Assert.Equal(Secret, request.AuthorizationParameter);
            return JsonResponse("{\"ok\":true}");
        });
        var resolver = new SequenceResolver([Binding(), Binding()]);
        var input = Input();
        var tool = new ConnectionHttpRequestTool(resolver, handler);
        var preparation = await tool.PrepareWorkflowEffectAsync(
            Invocation(Context(), input),
            DateTimeOffset.Parse("2026-08-02T00:00:00Z"),
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        var effect = Assert.IsAssignableFrom<IPreparedWorkflowEffect>(preparation.Effect);

        ((Dictionary<string, object?>)((List<object?>)input["pathParameters"]!)[0]!)["value"] = "tampered";
        var first = await effect.ExecuteOnceAsync(
            DateTimeOffset.Parse("2026-08-02T00:00:01Z"),
            CancellationToken.None);
        var second = await effect.ExecuteOnceAsync(
            DateTimeOffset.Parse("2026-08-02T00:00:02Z"),
            CancellationToken.None);

        Assert.True(preparation.Success);
        Assert.Equal(WorkflowEffectDisposition.Succeeded, first.Disposition);
        Assert.True(first.Result.Success);
        Assert.Equal(
            "https://api.example.test/root/users/a%2Fb%20caf%C3%A9/items?q=x%20y%26z",
            Assert.Single(handler.Requests).RequestUri);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal(HttpVersion.Version11, handler.Requests[0].Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, handler.Requests[0].VersionPolicy);
        Assert.True(handler.Requests[0].ConnectionClose);
        Assert.Equal(0, handler.Requests[0].ContentLength);
        Assert.Equal(
            new[] { "statusCode", "contentType", "text", "truncated", "responseBytes", "untrusted" }
                .OrderBy(key => key, StringComparer.Ordinal),
            first.Result.Output.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(200L, first.Result.Output["statusCode"]);
        Assert.Equal("application/json", first.Result.Output["contentType"]);
        Assert.Equal("{\"ok\":true}", first.Result.Output["text"]);
        Assert.Equal(false, first.Result.Output["truncated"]);
        Assert.Equal(true, first.Result.Output["untrusted"]);
        Assert.DoesNotContain("api.example", Flatten(first.Result), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, Flatten(first.Result), StringComparison.Ordinal);
        Assert.DoesNotContain("api.example", effect.ApprovalPrompt.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, effect.ApprovalPrompt.Details, StringComparison.Ordinal);
        Assert.Equal(
            [ConnectionHttpResolutionPurpose.Preparation, ConnectionHttpResolutionPurpose.Execution],
            resolver.Purposes);
        Assert.Equal(WorkflowEffectDisposition.NotStarted, second.Disposition);
        Assert.Equal("connection.http.already_consumed", second.Result.ErrorCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ChangedRuntimeFingerprintStopsBeforeTheNetwork()
    {
        var handler = new RecordingHandler(_ => JsonResponse("{}"));
        var resolver = new SequenceResolver([
            Binding(executionFingerprint: Hash('e')),
            Binding(executionFingerprint: Hash('f')),
        ]);
        var tool = new ConnectionHttpRequestTool(resolver, handler);
        var preparation = await tool.PrepareWorkflowEffectAsync(
            Invocation(Context()),
            DateTimeOffset.Parse("2026-08-02T00:00:00Z"),
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        var execution = await preparation.Effect!.ExecuteOnceAsync(
            DateTimeOffset.Parse("2026-08-02T00:00:01Z"),
            CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.NotStarted, execution.Disposition);
        Assert.Equal("connection.http.binding_mismatch", execution.Result.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task PathParametersCannotNormalizeOrEscapeTheRegisteredBasePath(string value)
    {
        var handler = new RecordingHandler(_ => JsonResponse("{}"));
        var resolver = new SequenceResolver([Binding()]);
        var tool = new ConnectionHttpRequestTool(resolver, handler);
        var input = Input();
        input["pathTemplate"] = "{userId}/admin";
        ((Dictionary<string, object?>)((List<object?>)input["pathParameters"]!)[0]!)["value"] = value;

        var preparation = await tool.PrepareWorkflowEffectAsync(
            Invocation(Context(), input),
            DateTimeOffset.Parse("2026-08-02T00:00:00Z"),
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.False(preparation.Success);
        Assert.Equal("connection.http.input_invalid", preparation.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FirstRedirectIsNotFollowedAndDoesNotLeakItsLocation()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri("https://leak.example.test/private?token=raw-secret");
        response.Content = new StringContent("redirect body", Encoding.UTF8, "text/plain");
        var execution = await ExecuteAsync(_ => response);

        Assert.Equal(WorkflowEffectDisposition.FailedKnown, execution.Execution.Disposition);
        Assert.Equal("connection.http.redirect_denied", execution.Execution.Result.ErrorCode);
        Assert.DoesNotContain("leak.example", Flatten(execution.Execution.Result), StringComparison.Ordinal);
        Assert.DoesNotContain("raw-secret", Flatten(execution.Execution.Result), StringComparison.Ordinal);
        Assert.Single(execution.Handler.Requests);
    }

    [Fact]
    public async Task RejectsNonSuccessUnsupportedContentAndOversizeBodiesWithFixedCodes()
    {
        var nonSuccess = await ExecuteAsync(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream detail", Encoding.UTF8, "text/plain"),
        });
        var unsupported = await ExecuteAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>raw</html>", Encoding.UTF8, "text/html"),
        });
        var invalidUtf8 = await ExecuteAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = PlainTextContent([0xc3, 0x28]),
        });
        var oversize = await ExecuteAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[(48 * 1024) + 1])
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
            },
        });

        AssertFailure(nonSuccess.Execution, "connection.http.remote_failed");
        AssertFailure(unsupported.Execution, "connection.http.content_unsupported");
        AssertFailure(invalidUtf8.Execution, "connection.http.content_unsupported");
        AssertFailure(oversize.Execution, "connection.http.response_too_large");
        Assert.DoesNotContain("upstream detail", Flatten(nonSuccess.Execution.Result), StringComparison.Ordinal);
        Assert.DoesNotContain("<html>", Flatten(unsupported.Execution.Result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseByteLimitAcceptsExactLengthAndRejectsDeclaredOverflowBeforeReading()
    {
        var exact = await ExecuteAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = PlainTextContent(new byte[48 * 1024]),
        });
        var declaredOverflow = new UnreadableOversizeContent((48 * 1024) + 1);
        var overflow = await ExecuteAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = declaredOverflow,
        });

        Assert.Equal(WorkflowEffectDisposition.Succeeded, exact.Execution.Disposition);
        Assert.Equal(48L * 1024, exact.Execution.Result.Output["responseBytes"]);
        AssertFailure(overflow.Execution, "connection.http.response_too_large");
        Assert.False(declaredOverflow.WasRead);
    }

    [Fact]
    public async Task UnknownLengthResponseIsRejectedAtTheFirstOverflowByte()
    {
        var exact = await ExecuteAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[48 * 1024]),
        });
        var overflow = await ExecuteAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[(48 * 1024) + 1]),
        });

        Assert.Equal(WorkflowEffectDisposition.Succeeded, exact.Execution.Disposition);
        Assert.Equal(48L * 1024, exact.Execution.Result.Output["responseBytes"]);
        AssertFailure(overflow.Execution, "connection.http.response_too_large");
    }

    [Fact]
    public async Task RejectsResidualContentEncodingAndInvalidBearerGrammarBeforeExposure()
    {
        var encodedResponse = JsonResponse("encoded-but-not-decoded");
        encodedResponse.Content.Headers.ContentEncoding.Add("gzip");
        var encoded = await ExecuteAsync(_ => encodedResponse);

        AssertFailure(encoded.Execution, "connection.http.content_unsupported");
        foreach (var invalidSecret in new[] { "secret with whitespace", "====" })
        {
            var handler = new RecordingHandler(_ => JsonResponse("{}"));
            var invalidCredential = new SequenceResolver([
                Binding(),
                new ConnectionHttpRuntimeBinding(
                    "재고 API",
                    new Uri("https://api.example.test/root", UriKind.Absolute),
                    Hash('c'),
                    Hash('e'),
                    ConnectionHttpAuthenticationKind.Bearer,
                    new ConnectionHttpCredential(invalidSecret)),
            ]);
            var tool = new ConnectionHttpRequestTool(invalidCredential, handler);
            var preparation = await tool.PrepareWorkflowEffectAsync(
                Invocation(Context()),
                DateTimeOffset.Parse("2026-08-02T00:00:00Z"),
                TimeSpan.FromMinutes(2),
                CancellationToken.None);
            var invalidExecution = await preparation.Effect!.ExecuteOnceAsync(
                DateTimeOffset.Parse("2026-08-02T00:00:01Z"),
                CancellationToken.None);

            Assert.Equal(WorkflowEffectDisposition.NotStarted, invalidExecution.Disposition);
            Assert.Equal("connection.http.binding_invalid", invalidExecution.Result.ErrorCode);
            Assert.Empty(handler.Requests);
        }
    }

    [Fact]
    public async Task ReflectedRawOrJsonEscapedCredentialIsDiscardedWithoutOutput()
    {
        const string reflectedSecret = "reflected/token+value==";
        foreach (var responseText in new[]
                 {
                     $"Bearer {reflectedSecret}",
                     $"{{\"token\":\"{reflectedSecret.Replace("/", "\\/", StringComparison.Ordinal)}\"}}",
                     "{\"token\":\"\\u0072eflected\\/token+value==\"}",
                 })
        {
            var handler = new RecordingHandler(_ => JsonResponse(responseText));
            var resolver = new SequenceResolver([
                Binding(secret: reflectedSecret),
                Binding(secret: reflectedSecret),
            ]);
            var tool = new ConnectionHttpRequestTool(resolver, handler);
            var preparation = await tool.PrepareWorkflowEffectAsync(
                Invocation(Context()),
                DateTimeOffset.Parse("2026-08-02T00:00:00Z"),
                TimeSpan.FromMinutes(2),
                CancellationToken.None);
            var execution = await preparation.Effect!.ExecuteOnceAsync(
                DateTimeOffset.Parse("2026-08-02T00:00:01Z"),
                CancellationToken.None);

            Assert.Equal(WorkflowEffectDisposition.FailedKnown, execution.Disposition);
            Assert.Equal("connection.http.credential_reflected", execution.Result.ErrorCode);
            Assert.Empty(execution.Result.Output);
            Assert.DoesNotContain(reflectedSecret, Flatten(execution.Result), StringComparison.Ordinal);
        }

        const string contentTypeSecret = "reflectedtoken99";
        var contentTypeHandler = new RecordingHandler(_ =>
        {
            var response = JsonResponse("{}");
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                $"application/vnd.{contentTypeSecret}+json");
            return response;
        });
        var contentTypeTool = new ConnectionHttpRequestTool(
            new SequenceResolver([
                Binding(secret: contentTypeSecret),
                Binding(secret: contentTypeSecret),
            ]),
            contentTypeHandler);
        var contentTypePreparation = await contentTypeTool.PrepareWorkflowEffectAsync(
            Invocation(Context()),
            DateTimeOffset.Parse("2026-08-02T00:00:00Z"),
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        var contentTypeExecution = await contentTypePreparation.Effect!.ExecuteOnceAsync(
            DateTimeOffset.Parse("2026-08-02T00:00:01Z"),
            CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.FailedKnown, contentTypeExecution.Disposition);
        Assert.Equal("connection.http.credential_reflected", contentTypeExecution.Result.ErrorCode);
        Assert.Empty(contentTypeExecution.Result.Output);
    }

    [Fact]
    public async Task ExceptionsAndCancellationAfterSendStartsHaveUnknownDispositionAndFixedErrors()
    {
        var transport = await ExecuteAsync(_ => throw new HttpRequestException("raw-host-token"));
        var timeout = await ExecuteAsync(_ => throw new TaskCanceledException("raw-timeout-detail"));

        Assert.Equal(WorkflowEffectDisposition.Unknown, transport.Execution.Disposition);
        Assert.Equal("connection.http.transport_failed", transport.Execution.Result.ErrorCode);
        Assert.Equal(WorkflowEffectDisposition.Unknown, timeout.Execution.Disposition);
        Assert.Equal("connection.http.timeout", timeout.Execution.Result.ErrorCode);
        Assert.DoesNotContain("raw-host-token", Flatten(transport.Execution.Result), StringComparison.Ordinal);
        Assert.DoesNotContain("raw-timeout-detail", Flatten(timeout.Execution.Result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrematureResponseCloseAfterHeadersDoesNotReplayTheGet()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var binding = new ConnectionHttpRuntimeBinding(
            "loopback",
            new Uri($"http://127.0.0.1:{port}/root/", UriKind.Absolute),
            Hash('c'),
            Hash('e'),
            ConnectionHttpAuthenticationKind.None);
        var server = CountRequestsClosedAfterHeadersAsync(listener);
        var tool = new ConnectionHttpRequestTool(new SequenceResolver([binding, binding]));
        var preparation = await tool.PrepareWorkflowEffectAsync(
            Invocation(Context()),
            DateTimeOffset.Parse("2026-08-02T00:00:00Z"),
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.True(preparation.Success);
        var execution = await preparation.Effect!.ExecuteOnceAsync(
            DateTimeOffset.Parse("2026-08-02T00:00:01Z"),
            CancellationToken.None);
        var requestCount = await server;

        Assert.Equal(WorkflowEffectDisposition.Unknown, execution.Disposition);
        Assert.Equal("connection.http.transport_failed", execution.Result.ErrorCode);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task ExecutionLeaseStaysHeldUntilTheHttpAttemptCompletes()
    {
        var leases = new ConnectionExecutionLeaseCoordinator();
        var handler = new BlockingHandler();
        var tool = new ConnectionHttpRequestTool(
            new SequenceResolver([Binding(), Binding()], leases),
            handler);
        var preparation = await tool.PrepareWorkflowEffectAsync(
            Invocation(Context()),
            DateTimeOffset.Parse("2026-08-02T00:00:00Z"),
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        var executionTask = preparation.Effect!.ExecuteOnceAsync(
            DateTimeOffset.Parse("2026-08-02T00:00:01Z"),
            CancellationToken.None);
        await handler.Entered.Task;

        var mutationLeaseTask = leases.AcquireAsync(CancellationToken.None).AsTask();
        Assert.False(mutationLeaseTask.IsCompleted);
        handler.Release.SetResult();
        var execution = await executionTask;
        await using var mutationLease = await mutationLeaseTask;

        Assert.Equal(WorkflowEffectDisposition.Succeeded, execution.Disposition);
    }

    [Fact]
    public async Task TruncatesTextAtSixteenThousandUnicodeScalars()
    {
        var source = string.Concat(
            string.Concat(Enumerable.Repeat("😀", 8_000)),
            new string('x', 8_001));
        var execution = await ExecuteAsync(_ => JsonResponse(source));
        var text = Assert.IsType<string>(execution.Execution.Result.Output["text"]);

        Assert.Equal(16_000, text.EnumerateRunes().Count());
        Assert.Equal(true, execution.Execution.Result.Output["truncated"]);
        Assert.True((long)execution.Execution.Result.Output["responseBytes"]! <= 48 * 1024);
    }

    private static async Task<(WorkflowPreparedEffectExecution Execution, RecordingHandler Handler)> ExecuteAsync(
        Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var handler = new RecordingHandler(respond);
        var tool = new ConnectionHttpRequestTool(
            new SequenceResolver([Binding(), Binding()]),
            handler);
        var preparation = await tool.PrepareWorkflowEffectAsync(
            Invocation(Context()),
            DateTimeOffset.Parse("2026-08-02T00:00:00Z"),
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        Assert.True(preparation.Success);
        var execution = await preparation.Effect!.ExecuteOnceAsync(
            DateTimeOffset.Parse("2026-08-02T00:00:01Z"),
            CancellationToken.None);
        return (execution, handler);
    }

    private static void AssertFailure(
        WorkflowPreparedEffectExecution execution,
        string errorCode)
    {
        Assert.Equal(WorkflowEffectDisposition.FailedKnown, execution.Disposition);
        Assert.False(execution.Result.Success);
        Assert.Equal(errorCode, execution.Result.ErrorCode);
        Assert.Empty(execution.Result.Output);
    }

    private static ByteArrayContent PlainTextContent(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        return content;
    }

    private static HttpResponseMessage JsonResponse(string text) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(text, Encoding.UTF8, "application/json"),
        };

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

    private static WindowsPlatformProfile Profile() =>
        WindowsPlatformProfile.Classify(10, 0, 22_631, isWorkstation: true);

    private static ToolInvokeParams Invocation(
        CapabilityInvocationContext? context,
        IReadOnlyDictionary<string, object?>? input = null) =>
        new(
            "workflow-http-step",
            "conversation-1",
            "workflow-run-1",
            "connection.http.request.v1",
            "R3",
            input ?? Input(),
            context);

    private static Dictionary<string, object?> Input() => new(StringComparer.Ordinal)
    {
        ["connectionSlot"] = "inventory",
        ["method"] = "GET",
        ["pathTemplate"] = "users/{userId}/items",
        ["reason"] = "승인된 재고 조회",
        ["pathParameters"] = new List<object?>
        {
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = "userId",
                ["value"] = "a/b café",
            },
        },
        ["queryParameters"] = new List<object?>
        {
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = "q",
                ["value"] = "x y&z",
            },
        },
    };

    private static CapabilityInvocationContext Context()
    {
        var exactVersion = new CapabilityVersionRef(
            CapabilityId.Parse("company.inventory.lookup"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            CapabilityContentHash.Parse(Hash('b')));
        return new CapabilityInvocationContext(
            Hash('a'),
            CapabilityRuntimeHasher.ComputeModelToolName(exactVersion),
            exactVersion.Id.Value,
            exactVersion.Version.Value,
            exactVersion.ContentHash.Value,
            7,
            Hash('c'));
    }

    private static ConnectionHttpRuntimeBinding Binding(
        string? executionFingerprint = null,
        string secret = Secret) => new(
            "재고 API",
            new Uri("https://api.example.test/root", UriKind.Absolute),
            Hash('c'),
            executionFingerprint ?? Hash('e'),
            ConnectionHttpAuthenticationKind.Bearer,
            new ConnectionHttpCredential(secret));

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    private static string Flatten(WindowsToolExecutionResult result) =>
        string.Join("|", result.Output.Select(item => $"{item.Key}:{item.Value}")) +
        $"|{result.Error}|{result.ActivitySummary}|{result.ErrorCode}";

    private sealed class SequenceResolver(
        IReadOnlyList<ConnectionHttpRuntimeBinding> bindings,
        ConnectionExecutionLeaseCoordinator? executionLeases = null)
        : IConnectionHttpExecutionResolver
    {
        public int CallCount { get; private set; }
        public List<ConnectionHttpResolutionPurpose> Purposes { get; } = [];

        public async Task<ConnectionHttpExecutionResolution> ResolveAsync(
            CapabilityInvocationContext capabilityContext,
            string connectionSlot,
            ConnectionHttpResolutionPurpose purpose,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = bindings[Math.Min(CallCount, bindings.Count - 1)];
            CallCount++;
            Purposes.Add(purpose);
            var binding = purpose == ConnectionHttpResolutionPurpose.Preparation &&
                          source.AuthenticationKind == ConnectionHttpAuthenticationKind.Bearer
                ? new ConnectionHttpRuntimeBinding(
                    source.DisplayName,
                    source.BaseUri,
                    source.FrozenBindingHash,
                    source.ExecutionFingerprint,
                    source.AuthenticationKind)
                : source;
            var executionLease = purpose == ConnectionHttpResolutionPurpose.Execution && executionLeases is not null
                ? await executionLeases.AcquireAsync(cancellationToken)
                : null;
            return new ConnectionHttpExecutionResolution(
                true,
                binding,
                ExecutionLease: executionLease);
        }
    }

    private sealed record RecordedRequest(
        string Method,
        string RequestUri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        Version Version,
        HttpVersionPolicy VersionPolicy,
        bool ConnectionClose,
        long? ContentLength);

    private sealed class UnreadableOversizeContent : HttpContent
    {
        private readonly long length;

        public UnreadableOversizeContent(long length)
        {
            this.length = length;
            Headers.ContentLength = length;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        }

        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasRead = true;
            throw new InvalidOperationException("Oversize content must be rejected before reading.");
        }

        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = length;
            return true;
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] bytes;

        public UnknownLengthContent(byte[] bytes)
        {
            this.bytes = bytes;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await stream.WriteAsync(bytes);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private sealed class RecordingHandler(Func<RecordedRequest, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var recorded = new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Version,
                request.VersionPolicy,
                request.Headers.ConnectionClose is true,
                request.Content?.Headers.ContentLength);
            Requests.Add(recorded);
            return Task.FromResult(respond(recorded));
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return JsonResponse("{}");
        }
    }
}
