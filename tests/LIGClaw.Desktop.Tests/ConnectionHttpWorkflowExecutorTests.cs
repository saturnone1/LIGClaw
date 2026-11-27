using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Tools;
using LIGClaw.Application.Workflows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Desktop.Infrastructure.Workflows;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class ConnectionHttpWorkflowExecutorTests : IDisposable
{
    private const string InputSchema =
        "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}";
    private const string WorkflowJson = """
        {
          "schemaVersion": 1,
          "entryStepId": "request",
          "steps": [
            {
              "id": "request",
              "type": "tool.call",
              "tool": "connection.http.request.v1",
              "arguments": {
                "connectionSlot": "inventory",
                "method": "GET",
                "pathTemplate": "health",
                "reason": "등록된 재고 연결 상태 확인"
              },
              "next": "done"
            },
            { "id": "done", "type": "output", "value": { "ok": true } }
          ]
        }
        """;
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public ConnectionHttpWorkflowExecutorTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task PreservesSafeConnectionFailureInWorkflowResponseAndLedger()
    {
        using var store = new ConversationStore(Path.Combine(directory, "connection-http.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-08-02T03:00:00Z");
        const string conversationId = "connection-http-conversation";
        const string parentRunId = "connection-http-parent";
        const string workflowRunId = "connection-http-workflow";
        await store.StartRunAsync(conversationId, parentRunId, "workflow parent", now);

        var exactVersion = new CapabilityVersionRef(
            CapabilityId.Parse("company.inventory.lookup"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            CapabilityContentHash.Parse(Hash('b')));
        var run = new WorkflowRunIdentity(
            workflowRunId,
            conversationId,
            parentRunId,
            "connection-http-parent-tool",
            exactVersion,
            7);
        var context = new CapabilityInvocationContext(
            Hash('a'),
            CapabilityRuntimeHasher.ComputeModelToolName(exactVersion),
            exactVersion.Id.Value,
            exactVersion.Version.Value,
            exactVersion.ContentHash.Value,
            run.CatalogRevision,
            Hash('c'));
        var platform = WindowsPlatformProfile.Classify(10, 0, 22_631, isWorkstation: true);
        var adapter = new ConnectionHttpRequestTool(
            new StableResolver(),
            new FixedHandler());
        var host = new WindowsToolHost(platform, [adapter]);
        var catalog = new DesktopWorkflowToolMetadataCatalog(host);
        var plan = BuildPlan(catalog);
        var input = new WorkflowObjectValue(new Dictionary<string, WorkflowValue>());
        Assert.True(WorkflowRuntimeContractValidator.TryComputeCanonicalHash(
            input,
            WorkflowInterpreter.MaximumInputBytes,
            out var inputHash));
        Assert.Equal(
            WorkflowRunStartOutcome.Created,
            (await store.WorkflowLedger.StartOrCoalesceRunAsync(
                new WorkflowRunStart(
                    workflowRunId,
                    conversationId,
                    parentRunId,
                    run.ParentToolCallId,
                    "connection-http-idempotency",
                    context.SnapshotHash,
                    context.ModelToolName,
                    exactVersion.Id.Value,
                    exactVersion.Version.Value,
                    run.CatalogRevision,
                    exactVersion.ContentHash.Value,
                    plan.DefinitionHash.Value,
                    inputHash,
                    plan.PlanHash.Value,
                    plan.MaximumRisk,
                    now),
                CancellationToken.None)).Outcome);

        var policy = new ToolInvocationPolicy();
        policy.BeginRun(conversationId, workflowRunId);
        var executor = new DesktopWorkflowToolExecutor(
            run,
            plan,
            context,
            catalog,
            host,
            new ToolInvocationCoordinator(policy, host, (_, _) => Task.FromResult(true)),
            new WorkflowExecutionLedger(store.WorkflowLedger));

        var result = await new WorkflowInterpreter().ExecuteAsync(
            plan,
            InputSchema,
            input,
            run,
            executor);
        policy.EndRun(conversationId, workflowRunId);

        Assert.Equal(WorkflowRunStatus.AttentionRequired, result.Status);
        Assert.Equal("connection.http.remote_failed", result.ErrorCode);
        var step = await store.WorkflowLedger.GetStepAsync(
            ToolCallId(workflowRunId, 0, 1),
            CancellationToken.None);
        Assert.NotNull(step);
        Assert.Equal("failed_known", step.Status);
        Assert.Equal("connection.http.remote_failed", step.FixedErrorCode);
        Assert.DoesNotContain(
            "upstream-private-detail",
            step.RedactedSummary ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersistsUnknownTransportOutcomeAsAttentionRequiredRun()
    {
        using var store = new ConversationStore(Path.Combine(directory, "connection-http-unknown.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        const string conversationId = "connection-http-unknown-conversation";
        const string parentRunId = "connection-http-unknown-parent";
        await store.StartRunAsync(conversationId, parentRunId, "workflow parent", now);
        var (draft, catalogRevision) = await ActivateCapabilityAsync(store, now);
        var handler = new ThrowingHandler();
        var host = new WindowsToolHost(
            WindowsPlatformProfile.Classify(10, 0, 22_631, isWorkstation: true),
            [new ConnectionHttpRequestTool(new StableResolver(), handler)]);
        var workflowTools = new DesktopWorkflowToolMetadataCatalog(host);
        var orchestrator = new DesktopWorkflowOrchestrator(
            store.Capabilities,
            store.WorkflowLedger,
            workflowTools,
            host,
            policy => new ToolInvocationCoordinator(
                policy,
                host,
                (_, _) => Task.FromResult(true)));
        var invocation = CreateInvocation(
            draft.ExactVersion,
            catalogRevision,
            conversationId,
            parentRunId,
            now,
            'd',
            'e');

        var result = await orchestrator.InvokeAsync(invocation, CancellationToken.None);

        Assert.True(result.Success);
        var output = Assert.IsType<WorkflowInvokeV1Result>(result.Output);
        Assert.Equal("attention_required", output.Status);
        Assert.Equal("review_outcome", output.NextAction);
        Assert.Equal("connection.http.transport_failed", output.ErrorCode);
        Assert.Equal(1, handler.SendCount);
        var step = Assert.IsType<WorkflowStepRunRecord>(
            await store.WorkflowLedger.GetStepAsync(
                ToolCallId(output.WorkflowRunId, 0, 1),
                CancellationToken.None));
        Assert.Equal("outcome_unknown", step.Status);
        Assert.Equal("unknown", step.Disposition);
        Assert.Equal("connection.http.transport_failed", step.FixedErrorCode);
        Assert.DoesNotContain(
            ThrowingHandler.PrivateDetail,
            step.RedactedSummary ?? string.Empty,
            StringComparison.Ordinal);
        var run = Assert.IsType<WorkflowRunRecord>(
            await store.WorkflowLedger.GetRunAsync(output.WorkflowRunId, CancellationToken.None));
        Assert.Equal("attention_required", run.Status);
        Assert.Equal("review_outcome", run.FixedNextAction);
        Assert.Equal("connection.http.transport_failed", run.FixedErrorCode);
        Assert.NotNull(run.CompletedAtUtc);
    }

    [Fact]
    public async Task CancellationAfterApprovalDoesNotSendHttpRequest()
    {
        using var store = new ConversationStore(Path.Combine(directory, "connection-http-cancelled.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        const string conversationId = "connection-http-cancelled-conversation";
        const string parentRunId = "connection-http-cancelled-parent";
        await store.StartRunAsync(conversationId, parentRunId, "workflow parent", now);
        var (draft, catalogRevision) = await ActivateCapabilityAsync(store, now);
        var handler = new ThrowingHandler();
        var host = new WindowsToolHost(
            WindowsPlatformProfile.Classify(10, 0, 22_631, isWorkstation: true),
            [new ConnectionHttpRequestTool(new StableResolver(), handler)]);
        var workflowTools = new DesktopWorkflowToolMetadataCatalog(host);
        using var cancellation = new CancellationTokenSource();
        var approvalCount = 0;
        var orchestrator = new DesktopWorkflowOrchestrator(
            store.Capabilities,
            store.WorkflowLedger,
            workflowTools,
            host,
            policy => new ToolInvocationCoordinator(
                policy,
                host,
                (_, _) =>
                {
                    Interlocked.Increment(ref approvalCount);
                    cancellation.Cancel();
                    return Task.FromResult(true);
                }));
        var invocation = CreateInvocation(
            draft.ExactVersion,
            catalogRevision,
            conversationId,
            parentRunId,
            now,
            'f',
            '1');

        var result = await orchestrator.InvokeAsync(invocation, cancellation.Token);

        Assert.True(result.Success);
        var output = Assert.IsType<WorkflowInvokeV1Result>(result.Output);
        Assert.Equal("cancelled", output.Status);
        Assert.Equal("retry_from_start", output.NextAction);
        Assert.Equal("workflow.cancelled", output.ErrorCode);
        Assert.Equal(1, Volatile.Read(ref approvalCount));
        Assert.Equal(0, handler.SendCount);
        var step = Assert.IsType<WorkflowStepRunRecord>(
            await store.WorkflowLedger.GetStepAsync(
                ToolCallId(output.WorkflowRunId, 0, 1),
                CancellationToken.None));
        Assert.Equal("not_started", step.Status);
        Assert.Equal("not_started", step.Disposition);
        Assert.Equal("workflow.cancelled", step.FixedErrorCode);
    }

    private static ValidatedWorkflowPlan BuildPlan(IWorkflowToolMetadataCatalog catalog)
    {
        var parsed = new WorkflowDefinitionParser().Parse(WorkflowJson);
        Assert.True(parsed.Succeeded, parsed.Issues.FirstOrDefault()?.Code);
        var manifest = new CapabilityManifest(
            1,
            CapabilityId.Parse("company.inventory.lookup"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            "Inventory lookup",
            "Uses one exact registered inventory connection.",
            CapabilityKind.Workflow,
            ["Check inventory"],
            ["connection.http.request.v1"],
            [new CapabilityConnectionRequirement("inventory", "http")],
            "R3",
            "workflow.json");
        var validation = new WorkflowStaticValidator(catalog).Validate(
            parsed.Definition!,
            manifest,
            InputSchema);
        Assert.True(validation.Succeeded, validation.Issues.FirstOrDefault()?.Code);
        return validation.Plan!;
    }

    private static async Task<(ValidatedCapabilityDraft Draft, long CatalogRevision)> ActivateCapabilityAsync(
        ConversationStore store,
        DateTimeOffset now)
    {
        var draft = CreateDraft();
        var creation = await store.Capabilities.CreateValidatedDraftAsync(
            draft,
            now,
            CancellationToken.None);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, creation.Outcome);
        var activation = await store.Capabilities.ApproveAndActivateAsync(
            new CapabilityApprovalCommand(
                draft.DraftId,
                draft.ExactVersion,
                0,
                0,
                null,
                "review.approved",
                now.AddMilliseconds(1)),
            CancellationToken.None);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, activation.Outcome);
        return (draft, activation.Catalog!.CatalogRevision);
    }

    private static ValidatedCapabilityDraft CreateDraft()
    {
        var id = CapabilityId.Parse("company.inventory.lookup");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        var manifest = new CapabilityManifest(
            1,
            id,
            version,
            "Inventory lookup",
            "Uses one exact registered inventory connection.",
            CapabilityKind.Workflow,
            ["Check inventory"],
            ["connection.http.request.v1"],
            [new CapabilityConnectionRequirement("inventory", "http")],
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
                requiredConnections = new[] { new { kind = "http", slot = "inventory" } },
                requiredTools = manifest.RequiredTools,
                schemaVersion = 1,
                version = version.Value,
            }),
            "Use the exact registered inventory connection.",
            InputSchema,
            CanonicalWorkflowJson: WorkflowJson);
        var hash = CapabilityContentHasher.Compute(payload);
        return new ValidatedCapabilityDraft(
            "connection_http_workflow_draft",
            payload,
            hash,
            null,
            "local_import",
            hash);
    }

    private static WorkflowInvokeParams CreateInvocation(
        CapabilityVersionRef exactVersion,
        long catalogRevision,
        string conversationId,
        string parentRunId,
        DateTimeOffset now,
        char workflowCallIdValue,
        char idempotencyKeyValue) =>
        new(
            $"wfinv_{new string(workflowCallIdValue, 48)}",
            conversationId,
            parentRunId,
            "workflow.invoke.v1",
            new CapabilityInvocationContext(
                Hash('a'),
                CapabilityRuntimeHasher.ComputeModelToolName(exactVersion),
                exactVersion.Id.Value,
                exactVersion.Version.Value,
                exactVersion.ContentHash.Value,
                catalogRevision,
                Hash('c')),
            $"wfid_{new string(idempotencyKeyValue, 64)}",
            now.AddMinutes(8),
            new Dictionary<string, object?>());

    private static string ToolCallId(string runId, int sequence, int attempt)
    {
        var hash = WorkflowPreparedEffectHash.Compute(
            "workflow-step-tool-call-v1",
            runId,
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return $"wfs-{hash[7..]}";
    }

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class StableResolver : IConnectionHttpExecutionResolver
    {
        public Task<ConnectionHttpExecutionResolution> ResolveAsync(
            CapabilityInvocationContext capabilityContext,
            string connectionSlot,
            ConnectionHttpResolutionPurpose purpose,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionHttpExecutionResolution(
                true,
                new ConnectionHttpRuntimeBinding(
                    "재고 API",
                    new Uri("https://api.example.test/root", UriKind.Absolute),
                    Hash('c'),
                    Hash('d'),
                    ConnectionHttpAuthenticationKind.None)));
    }

    private sealed class FixedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(
                    "upstream-private-detail",
                    Encoding.UTF8,
                    "text/plain"),
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public const string PrivateDetail = "transport-private-detail";
        private int sendCount;

        public int SendCount => Volatile.Read(ref sendCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref sendCount);
            throw new HttpRequestException(PrivateDetail);
        }
    }
}
