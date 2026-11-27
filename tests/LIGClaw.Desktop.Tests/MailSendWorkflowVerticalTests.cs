using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Mail;
using LIGClaw.Application.Tools;
using LIGClaw.Application.Workflows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Desktop.Infrastructure.Workflows;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class MailSendWorkflowVerticalTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-02T05:00:00Z", CultureInfo.InvariantCulture);

    private const string SenderCanary = "d6b-sender-canary@example.invalid";
    private const string ToCanary = "d6b-to-canary@example.invalid";
    private const string CcCanary = "d6b-cc-canary@example.invalid";
    private const string BccCanary = "d6b-bcc-canary@example.invalid";
    private const string SubjectCanary = "D6B_SUBJECT_CANARY_9b71f3";
    private const string BodyCanary = "D6B_BODY_CANARY_284ac7";
    private const string BearerTokenCanary = "D6B-TOKEN-CANARY-4f91c2a8e7";
    private const string ConnectionSlot = "primary-mail";
    private const string SnapshotHash =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FrozenBindingHash =
        "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string LocalBindingHash =
        "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string ExecutionFingerprint =
        "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    private static readonly string[] RawCanaries =
    [
        SenderCanary,
        ToCanary,
        CcCanary,
        BccCanary,
        SubjectCanary,
        BodyCanary,
        BearerTokenCanary,
    ];

    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public MailSendWorkflowVerticalTests() => Directory.CreateDirectory(directory);

    [Fact]
    public Task RealWorkflowPersistsOneShotQueuedSubmissionWithoutRawMailOrCredential() =>
        RunScenarioAsync(postOutcomeIsAmbiguous: false, expectedGetCount: 0, expectedOutboxRevision: 2);

    [Fact]
    public Task AmbiguousPostPerformsExactlyOneLookupAndPersistsAcceptedWithoutRetry() =>
        RunScenarioAsync(postOutcomeIsAmbiguous: true, expectedGetCount: 1, expectedOutboxRevision: 3);

    private async Task RunScenarioAsync(
        bool postOutcomeIsAmbiguous,
        int expectedGetCount,
        long expectedOutboxRevision)
    {
        var databasePath = Path.Combine(
            directory,
            postOutcomeIsAmbiguous
                ? "mail-send-workflow-ambiguous.db"
                : "mail-send-workflow.db");
        using var store = new ConversationStore(databasePath, new FixedTimeProvider(Now));
        await store.InitializeAsync();
        const string conversationId = "mail-send-workflow-conversation";
        const string parentRunId = "mail-send-workflow-parent";
        await store.StartRunAsync(
            conversationId,
            parentRunId,
            "send the reviewed mail through the registered gateway",
            Now);

        var draft = CreateCapabilityDraft();
        var creation = await store.Capabilities.CreateValidatedDraftAsync(
            draft,
            Now,
            CancellationToken.None);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, creation.Outcome);
        var activation = await store.Capabilities.ApproveAndActivateAsync(
            new CapabilityApprovalCommand(
                draft.DraftId,
                draft.ExactVersion,
                ExpectedDraftRevision: 0,
                ExpectedCatalogRevision: 0,
                ExpectedActiveVersion: null,
                "review.approved",
                Now.AddMilliseconds(1)),
            CancellationToken.None);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, activation.Outcome);

        var expectedDraft = Assert.IsType<MailDraft>(MailDraftPolicy.Validate(
            new MailDraftInput(
                [ToCanary],
                [CcCanary],
                [BccCanary],
                SubjectCanary,
                $"{BodyCanary}\nsecond line")).Draft);
        var resolver = new StableMailGatewayResolver();
        var handler = new QueuedGatewayHandler(
            expectedDraft.CanonicalHash,
            postOutcomeIsAmbiguous);
        var wireClient = new MailGatewayWireClient(handler);
        var host = new WindowsToolHost(
            WindowsPlatformProfile.Classify(10, 0, 22_631, isWorkstation: true),
            [
                new MailCreateDraftTool(),
                new MailSendTool(resolver, store.MailOutbox, wireClient),
            ]);
        var workflowTools = new DesktopWorkflowToolMetadataCatalog(host);
        var audit = new ForwardingAuditSink(store);
        var approvalCount = 0;
        var approvalPreviewWasExact = false;
        var approvalVisibleText = string.Empty;
        var orchestrator = new DesktopWorkflowOrchestrator(
            store.Capabilities,
            store.WorkflowLedger,
            workflowTools,
            host,
            policy => new ToolInvocationCoordinator(
                policy,
                host,
                (prompt, allowAlways, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref approvalCount);
                    approvalPreviewWasExact = !allowAlways &&
                                              prompt.Risk == "R3" &&
                                              prompt.Action.Contains(SenderCanary, StringComparison.Ordinal) &&
                                              prompt.Details.Contains(ToCanary, StringComparison.Ordinal) &&
                                              prompt.Details.Contains(CcCanary, StringComparison.Ordinal) &&
                                              prompt.Details.Contains(BccCanary, StringComparison.Ordinal) &&
                                              prompt.Details.Contains(SubjectCanary, StringComparison.Ordinal) &&
                                              prompt.Details.Contains(BodyCanary, StringComparison.Ordinal) &&
                                              !prompt.Details.Contains(BearerTokenCanary, StringComparison.Ordinal);
                    approvalVisibleText = prompt.ToString();
                    return Task.FromResult(ToolApprovalChoice.AllowOnce);
                },
                audit,
                store),
            new FixedTimeProvider(Now));
        var context = new CapabilityInvocationContext(
            SnapshotHash,
            CapabilityRuntimeHasher.ComputeModelToolName(draft.ExactVersion),
            draft.ExactVersion.Id.Value,
            draft.ExactVersion.Version.Value,
            draft.ExactVersion.ContentHash.Value,
            activation.Catalog!.CatalogRevision,
            FrozenBindingHash);
        var invocation = new WorkflowInvokeParams(
            $"wfinv_{new string('f', 48)}",
            conversationId,
            parentRunId,
            "workflow.invoke.v1",
            context,
            $"wfid_{new string('9', 64)}",
            Now.AddMinutes(8),
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["to"] = new[] { ToCanary },
                ["cc"] = new[] { CcCanary },
                ["bcc"] = new[] { BccCanary },
                ["subject"] = SubjectCanary,
                ["body"] = $"{BodyCanary}\nsecond line",
            });

        var result = await orchestrator.InvokeAsync(invocation, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var output = Assert.IsType<WorkflowInvokeV1Result>(result.Output);
        Assert.Equal("succeeded", output.Status);
        Assert.Equal("none", output.NextAction);
        Assert.Null(output.ErrorCode);
        var publicResult = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(output.Result);
        Assert.Equal(
            ["draftHash", "operationId", "status"],
            publicResult.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(expectedDraft.CanonicalHash, Assert.IsType<string>(publicResult["draftHash"]));
        Assert.Equal("queued", Assert.IsType<string>(publicResult["status"]));
        var operationId = MailOutboxOperationId.Parse(
            Assert.IsType<string>(publicResult["operationId"]));

        Assert.Equal(1, Volatile.Read(ref approvalCount));
        Assert.True(approvalPreviewWasExact);
        Assert.Equal(2, resolver.Purposes.Count);
        Assert.Equal(
            [MailGatewayResolutionPurpose.Preparation, MailGatewayResolutionPurpose.Execution],
            resolver.Purposes);
        Assert.All(resolver.Slots, slot => Assert.Equal(ConnectionSlot, slot));
        Assert.Equal(1, handler.PostCount);
        Assert.Equal(expectedGetCount, handler.GetCount);
        Assert.True(handler.ExactRequestObserved);
        Assert.Equal(postOutcomeIsAmbiguous, handler.ExactReconciliationObserved);

        var workflowRun = Assert.IsType<WorkflowRunRecord>(
            await store.WorkflowLedger.GetRunAsync(output.WorkflowRunId, CancellationToken.None));
        Assert.Equal("succeeded", workflowRun.Status);
        Assert.Equal("R3", workflowRun.ActualMaximumRisk);
        Assert.Equal(1, workflowRun.CurrentSequence);
        Assert.Null(workflowRun.FixedNextAction);
        Assert.Null(workflowRun.FixedErrorCode);
        Assert.Matches("^sha256:[0-9a-f]{64}$", workflowRun.ResultHash);

        var draftStep = Assert.IsType<WorkflowStepRunRecord>(
            await store.WorkflowLedger.GetStepAsync(
                ToolCallId(output.WorkflowRunId, sequence: 0, attempt: 1),
                CancellationToken.None));
        Assert.Equal("mail.create_draft.v1", draftStep.ToolName);
        Assert.Equal("R0", draftStep.Risk);
        Assert.Equal("read_only", draftStep.RetryClass);
        Assert.Equal("observation", draftStep.EffectClass);
        Assert.Equal("succeeded", draftStep.Status);
        Assert.Null(draftStep.TicketItemId);

        var sendStep = Assert.IsType<WorkflowStepRunRecord>(
            await store.WorkflowLedger.GetStepAsync(
                ToolCallId(output.WorkflowRunId, sequence: 1, attempt: 1),
                CancellationToken.None));
        Assert.Equal("mail.send.v1", sendStep.ToolName);
        Assert.Equal("R3", sendStep.Risk);
        Assert.Equal("never", sendStep.RetryClass);
        Assert.Equal(0, sendStep.MaximumRetries);
        Assert.Equal("effect", sendStep.EffectClass);
        Assert.Equal("succeeded", sendStep.Status);
        Assert.Equal("succeeded", sendStep.Disposition);
        Assert.NotNull(sendStep.TicketItemId);
        Assert.Matches("^sha256:[0-9a-f]{64}$", sendStep.OutputHash);

        var ticketItem = Assert.IsType<WorkflowAuthorizationTicketItemRecord>(
            await store.WorkflowLedger.GetTicketItemAsync(
                sendStep.TicketItemId!,
                CancellationToken.None));
        Assert.Equal("consumed", ticketItem.Status);
        Assert.Equal(sendStep.ToolCallId, ticketItem.ConsumedByToolCallId);
        Assert.Equal("mail.send.v1", ticketItem.ToolName);
        Assert.Equal("R3", ticketItem.Risk);
        var ticket = Assert.IsType<WorkflowAuthorizationTicketRecord>(
            await store.WorkflowLedger.GetTicketAsync(ticketItem.TicketId, CancellationToken.None));
        Assert.Equal("consumed", ticket.Status);
        Assert.Equal("R3", ticket.ActualMaximumRisk);

        var outbox = Assert.IsType<MailOutboxOperation>(
            await store.MailOutbox.GetAsync(operationId, CancellationToken.None));
        Assert.Equal(MailOutboxSubmissionState.Accepted, outbox.SubmissionState);
        Assert.Equal(MailOutboxDeliveryState.Queued, outbox.DeliveryState);
        Assert.Equal(1, outbox.AttemptCount);
        Assert.Equal(expectedOutboxRevision, outbox.Revision);
        Assert.Null(outbox.FixedErrorCode);
        Assert.Equal(conversationId, outbox.ConversationId);
        Assert.Equal(output.WorkflowRunId, outbox.WorkflowRunId);
        Assert.Equal(sendStep.ToolCallId, outbox.ToolCallId);
        Assert.Equal(draft.ExactVersion.Id.Value, outbox.CapabilityId);
        Assert.Equal(draft.ExactVersion.Version.Value, outbox.CapabilityVersion);
        Assert.Equal(draft.ExactVersion.ContentHash.Value, outbox.CapabilityContentHash.Value);
        Assert.Equal(activation.Catalog.CatalogRevision, outbox.CatalogRevision);
        Assert.Equal(SnapshotHash, outbox.SnapshotHash.Value);
        Assert.Equal(ConnectionSlot, outbox.ConnectionSlot);
        Assert.Equal(LocalBindingHash, outbox.BindingHash.Value);
        Assert.Equal(expectedDraft.CanonicalHash, outbox.DraftHash.Value);
        Assert.Equal("[mail-outbox-idempotency-key]", outbox.IdempotencyKey.ToString());

        var activities = await store.GetRecentToolActivityAsync(cancellationToken: CancellationToken.None);
        Assert.Contains(activities, activity => activity.ToolName == "mail.create_draft.v1");
        Assert.Contains(activities, activity =>
            activity.ToolName == $"Workflow · {draft.ExactVersion.Id.Value}@{draft.ExactVersion.Version.Value}" &&
            activity.Status == "succeeded");
        Assert.Single(audit.Approvals);
        Assert.Single(audit.Executions);

        var auditVisibleText = string.Join(
            "\n",
            audit.Approvals.Concat(audit.Executions).Append(approvalVisibleText));
        var persistedVisibleText = string.Join(
            "\n",
            workflowRun,
            draftStep,
            sendStep,
            ticketItem,
            ticket,
            outbox,
            outbox.IdempotencyKey,
            resolver,
            handler,
            string.Join("\n", activities));
        var publicJson = JsonSerializer.Serialize(output);
        AssertNoRawCanaries(auditVisibleText, persistedVisibleText, publicJson);
        AssertDatabaseFilesDoNotContainRawCanaries(databasePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static ValidatedCapabilityDraft CreateCapabilityDraft()
    {
        var id = CapabilityId.Parse("company.sample.mail-send-vertical");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        var manifest = new CapabilityManifest(
            1,
            id,
            version,
            "Mail send vertical",
            "Validates and submits one bounded mail draft through one fixed gateway.",
            CapabilityKind.Workflow,
            ["Send a reviewed mail"],
            ["mail.create_draft.v1", "mail.send.v1"],
            [new CapabilityConnectionRequirement(ConnectionSlot, "mail-api")],
            "R3",
            "workflow.json");
        var payload = new CapabilityVersionPayload(
            manifest,
            JsonSerializer.Serialize(new
            {
                description = manifest.Description,
                displayName = manifest.DisplayName,
                entrypoint = manifest.Entrypoint,
                id = id.Value,
                intentExamples = manifest.IntentExamples,
                kind = "workflow",
                maximumRisk = manifest.MaximumRisk,
                requiredConnections = new[] { new { kind = "mail-api", slot = ConnectionSlot } },
                requiredTools = manifest.RequiredTools,
                schemaVersion = 1,
                version = version.Value,
            }),
            "Validate the draft locally, request R3 one-shot approval, then use the fixed gateway.",
            InputSchema,
            CanonicalWorkflowJson: WorkflowJson);
        var hash = CapabilityContentHasher.Compute(payload);
        return new ValidatedCapabilityDraft(
            "mail_send_workflow_vertical_draft",
            payload,
            hash,
            null,
            "local_import",
            hash);
    }

    private static string ToolCallId(string runId, int sequence, int attempt)
    {
        var hash = WorkflowPreparedEffectHash.Compute(
            "workflow-step-tool-call-v1",
            runId,
            sequence.ToString(CultureInfo.InvariantCulture),
            attempt.ToString(CultureInfo.InvariantCulture));
        return $"wfs-{hash[7..]}";
    }

    private static void AssertNoRawCanaries(params string[] visibleValues)
    {
        foreach (var value in visibleValues)
            foreach (var canary in RawCanaries)
                Assert.DoesNotContain(canary, value, StringComparison.Ordinal);
    }

    private static void AssertDatabaseFilesDoNotContainRawCanaries(string databasePath)
    {
        var files = new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }
            .Where(File.Exists)
            .ToArray();
        Assert.Contains(databasePath, files, StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            foreach (var canary in RawCanaries)
                Assert.False(
                    Contains(bytes, Encoding.UTF8.GetBytes(canary)),
                    $"Raw canary was found in {Path.GetFileName(path)}: {canary}");
        }
    }

    private static bool Contains(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return true;
        for (var index = 0; index <= source.Length - value.Length; index++)
        {
            if (source.Slice(index, value.Length).SequenceEqual(value)) return true;
        }
        return false;
    }

    private const string InputSchema =
        "{\"additionalProperties\":false,\"properties\":{\"bcc\":{\"items\":{\"format\":\"email\",\"maxLength\":254,\"minLength\":3,\"type\":\"string\"},\"maxItems\":16,\"minItems\":1,\"type\":\"array\",\"uniqueItems\":true},\"body\":{\"maxLength\":4096,\"minLength\":1,\"type\":\"string\"},\"cc\":{\"items\":{\"format\":\"email\",\"maxLength\":254,\"minLength\":3,\"type\":\"string\"},\"maxItems\":16,\"minItems\":1,\"type\":\"array\",\"uniqueItems\":true},\"subject\":{\"maxLength\":200,\"minLength\":1,\"type\":\"string\"},\"to\":{\"items\":{\"format\":\"email\",\"maxLength\":254,\"minLength\":3,\"type\":\"string\"},\"maxItems\":16,\"minItems\":1,\"type\":\"array\",\"uniqueItems\":true}},\"required\":[\"to\",\"cc\",\"bcc\",\"subject\",\"body\"],\"type\":\"object\"}";

    private const string WorkflowJson =
        "{\"schemaVersion\":1,\"entryStepId\":\"draft\",\"steps\":[{\"id\":\"draft\",\"type\":\"tool.call\",\"tool\":\"mail.create_draft.v1\",\"arguments\":{\"to\":{\"$bind\":{\"source\":\"input\",\"path\":\"/to\"}},\"cc\":{\"$bind\":{\"source\":\"input\",\"path\":\"/cc\"}},\"bcc\":{\"$bind\":{\"source\":\"input\",\"path\":\"/bcc\"}},\"subject\":{\"$bind\":{\"source\":\"input\",\"path\":\"/subject\"}},\"body\":{\"$bind\":{\"source\":\"input\",\"path\":\"/body\"}}},\"next\":\"send\"},{\"id\":\"send\",\"type\":\"tool.call\",\"tool\":\"mail.send.v1\",\"arguments\":{\"connectionSlot\":\"primary-mail\",\"draftHash\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"draft\",\"path\":\"/draftHash\"}},\"to\":{\"$bind\":{\"source\":\"input\",\"path\":\"/to\"}},\"cc\":{\"$bind\":{\"source\":\"input\",\"path\":\"/cc\"}},\"bcc\":{\"$bind\":{\"source\":\"input\",\"path\":\"/bcc\"}},\"subject\":{\"$bind\":{\"source\":\"input\",\"path\":\"/subject\"}},\"body\":{\"$bind\":{\"source\":\"input\",\"path\":\"/body\"}}},\"next\":\"done\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{\"operationId\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"send\",\"path\":\"/operationId\"}},\"status\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"send\",\"path\":\"/status\"}},\"draftHash\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"send\",\"path\":\"/draftHash\"}}}}]}";

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }

    private sealed class StableMailGatewayResolver : IMailGatewayExecutionResolver
    {
        public List<MailGatewayResolutionPurpose> Purposes { get; } = [];
        public List<string> Slots { get; } = [];

        public Task<MailGatewayExecutionResolution> ResolveAsync(
            CapabilityInvocationContext capabilityContext,
            string connectionSlot,
            MailGatewayResolutionPurpose purpose,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Purposes.Add(purpose);
            Slots.Add(connectionSlot);
            var credential = purpose == MailGatewayResolutionPurpose.Execution
                ? new MailGatewayCredential(BearerTokenCanary)
                : null;
            var binding = new MailGatewayRuntimeBinding(
                "등록 메일 Gateway",
                new Uri("https://mail.example.invalid/", UriKind.Absolute),
                SenderCanary,
                FrozenBindingHash,
                LocalBindingHash,
                ExecutionFingerprint,
                credential);
            return Task.FromResult(new MailGatewayExecutionResolution(
                true,
                binding,
                ExecutionLease: purpose == MailGatewayResolutionPurpose.Execution
                    ? new NoopLease()
                    : null));
        }

        public override string ToString() => "Stable Mail Gateway resolver [redacted]";
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class QueuedGatewayHandler(
        string expectedDraftHash,
        bool postOutcomeIsAmbiguous) : HttpMessageHandler
    {
        private int postCount;
        private int getCount;
        private string? submittedOperationId;

        public int PostCount => Volatile.Read(ref postCount);
        public int GetCount => Volatile.Read(ref getCount);
        public bool ExactRequestObserved { get; private set; }
        public bool ExactReconciliationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref getCount);
                var lookupOperationId = request.RequestUri!.Segments[^1];
                ExactReconciliationObserved = postOutcomeIsAmbiguous &&
                                              lookupOperationId == submittedOperationId &&
                                              request.RequestUri == new Uri(
                                                  $"https://mail.example.invalid/ligclaw-mail-gateway/v1/submissions/{submittedOperationId}") &&
                                              request.Headers.Authorization?.Scheme == "Bearer" &&
                                              request.Headers.Authorization.Parameter == BearerTokenCanary;
                return JsonResponse(
                    HttpStatusCode.OK,
                    $"{{\"contract\":\"ligclaw-mail-gateway-v1\",\"operationId\":\"{lookupOperationId}\",\"status\":\"queued\",\"draftHash\":\"{expectedDraftHash}\"}}");
            }
            if (request.Method != HttpMethod.Post)
            {
                return JsonResponse(HttpStatusCode.BadRequest, "{}");
            }

            Interlocked.Increment(ref postCount);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var operationId = root.GetProperty("operationId").GetString()!;
            submittedOperationId = operationId;
            var draftHash = root.GetProperty("draftHash").GetString()!;
            ExactRequestObserved = request.RequestUri ==
                                   new Uri("https://mail.example.invalid/ligclaw-mail-gateway/v1/submissions") &&
                                   request.Headers.Authorization?.Scheme == "Bearer" &&
                                   request.Headers.Authorization.Parameter == BearerTokenCanary &&
                                   root.GetProperty("contract").GetString() == "ligclaw-mail-gateway-v1" &&
                                   root.GetProperty("idempotencyKey").GetString() ==
                                   request.Headers.GetValues("Idempotency-Key").Single() &&
                                   draftHash == expectedDraftHash &&
                                   root.GetProperty("sender").GetString() == SenderCanary &&
                                   ReadAddresses(root, "to").SequenceEqual([ToCanary]) &&
                                   ReadAddresses(root, "cc").SequenceEqual([CcCanary]) &&
                                   ReadAddresses(root, "bcc").SequenceEqual([BccCanary]) &&
                                   root.GetProperty("subject").GetString() == SubjectCanary &&
                                   root.GetProperty("body").GetString() == $"{BodyCanary}\nsecond line";
            if (postOutcomeIsAmbiguous)
                throw new HttpRequestException("Deterministic ambiguous POST outcome.");
            return JsonResponse(
                HttpStatusCode.OK,
                $"{{\"contract\":\"ligclaw-mail-gateway-v1\",\"operationId\":\"{operationId}\",\"status\":\"queued\",\"draftHash\":\"{draftHash}\"}}");
        }

        public override string ToString() =>
            $"Queued Gateway handler (POST={PostCount}, GET={GetCount}) [redacted]";

        private static string[] ReadAddresses(JsonElement root, string property) =>
            root.GetProperty(property).EnumerateArray().Select(item => item.GetString()!).ToArray();

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class ForwardingAuditSink(ConversationStore store) : IToolAuditSink
    {
        public List<string> Approvals { get; } = [];
        public List<string> Executions { get; } = [];

        public async Task RecordApprovalAsync(
            ToolApprovalAuditRecord record,
            CancellationToken cancellationToken)
        {
            Approvals.Add(record.ToString());
            await store.RecordApprovalAsync(record, cancellationToken);
        }

        public async Task RecordExecutionAsync(
            ToolExecutionAuditRecord record,
            CancellationToken cancellationToken)
        {
            Executions.Add(record.ToString());
            await store.RecordExecutionAsync(record, cancellationToken);
        }
    }
}
