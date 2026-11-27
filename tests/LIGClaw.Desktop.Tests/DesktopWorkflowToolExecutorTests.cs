using LIGClaw.Application.Tools;
using LIGClaw.Application.Workflows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Desktop.Infrastructure.Workflows;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class DesktopWorkflowToolExecutorTests : IDisposable
{
    private const string WorkflowInputSchema =
        "{\"additionalProperties\":false,\"properties\":{\"path\":{\"maxLength\":4096,\"minLength\":3,\"type\":\"string\"}},\"required\":[\"path\"],\"type\":\"object\"}";
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public DesktopWorkflowToolExecutorTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task ExecutesThreeDesktopToolsAndConsumesNotificationTicketOnce()
    {
        var path = Path.Combine(directory, "report.txt");
        await File.WriteAllTextAsync(path, "fixture");
        var databasePath = Path.Combine(directory, "workflow.db");
        using var store = new ConversationStore(databasePath);
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-08-02T01:00:00Z");
        const string conversationId = "workflow-conversation";
        const string parentRunId = "workflow-parent-run";
        const string workflowRunId = "workflow-child-run";
        await store.StartRunAsync(conversationId, parentRunId, "workflow parent", now);

        var notifications = new RecordingNotificationService();
        var platform = WindowsPlatformProfile.Classify(10, 0, 22_631, isWorkstation: true);
        var host = new WindowsToolHost(
            platform,
            [
                new SystemGetStatusTool(platform),
                new FileGetMetadataTool(new SafeFileOperationService()),
                new SystemShowNotificationTool(notifications),
            ]);
        var catalog = new DesktopWorkflowToolMetadataCatalog(host);
        var plan = BuildPlan(catalog);
        var input = Object(("path", new WorkflowStringValue(path)));
        Assert.True(WorkflowRuntimeContractValidator.TryComputeCanonicalHash(
            input,
            WorkflowInterpreter.MaximumInputBytes,
            out var inputHash));
        var exactVersion = new CapabilityVersionRef(
            CapabilityId.Parse("company.sample.workflow"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            CapabilityContentHash.Parse(Hash('a')));
        var run = new WorkflowRunIdentity(
            workflowRunId,
            conversationId,
            parentRunId,
            "workflow-parent-tool-call",
            exactVersion,
            7);
        var context = new CapabilityInvocationContext(
            Hash('b'),
            "cap_0123456789abcdef01234567",
            exactVersion.Id.Value,
            exactVersion.Version.Value,
            exactVersion.ContentHash.Value,
            run.CatalogRevision,
            Hash('c'));
        var started = await store.WorkflowLedger.StartOrCoalesceRunAsync(
            new WorkflowRunStart(
                workflowRunId,
                conversationId,
                parentRunId,
                run.ParentToolCallId,
                "workflow-idempotency-key",
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
            CancellationToken.None);
        Assert.Equal(WorkflowRunStartOutcome.Created, started.Outcome);

        var policy = new ToolInvocationPolicy();
        policy.BeginRun(conversationId, workflowRunId);
        var coordinator = new ToolInvocationCoordinator(
            policy,
            host,
            (_, _) => Task.FromResult(true));
        var executor = new DesktopWorkflowToolExecutor(
            run,
            plan,
            context,
            catalog,
            host,
            coordinator,
            new WorkflowExecutionLedger(store.WorkflowLedger));

        var result = await new WorkflowInterpreter().ExecuteAsync(
            plan,
            WorkflowInputSchema,
            input,
            run,
            executor);
        policy.EndRun(conversationId, workflowRunId);

        Assert.Equal(WorkflowRunStatus.Completed, result.Status);
        Assert.Equal(4, result.Trace.Count(item => item.Status == "succeeded"));
        Assert.Equal(("LIGClaw 상태", "운영체제 상태를 확인했습니다."), Assert.Single(notifications.Shown));
        var statusStep = await store.WorkflowLedger.GetStepAsync(ToolCallId(workflowRunId, 0, 1), CancellationToken.None);
        var metadataStep = await store.WorkflowLedger.GetStepAsync(ToolCallId(workflowRunId, 1, 1), CancellationToken.None);
        var effectStep = await store.WorkflowLedger.GetStepAsync(ToolCallId(workflowRunId, 2, 1), CancellationToken.None);
        Assert.Equal("succeeded", statusStep!.Status);
        Assert.Equal("succeeded", metadataStep!.Status);
        Assert.Equal("succeeded", effectStep!.Status);
        Assert.NotNull(effectStep.TicketItemId);
        Assert.Equal(
            "consumed",
            (await store.WorkflowLedger.GetTicketItemAsync(effectStep.TicketItemId!, CancellationToken.None))!.Status);

        Assert.True(WorkflowRuntimeContractValidator.TryComputeCanonicalHash(
            result.Output!,
            WorkflowInterpreter.MaximumFinalOutputBytes,
            out var resultHash));
        var activeRun = (await store.WorkflowLedger.GetRunAsync(workflowRunId, CancellationToken.None))!;
        Assert.True(await store.WorkflowLedger.CompleteRunAsync(
            new WorkflowRunCompletion(
                workflowRunId,
                WorkflowRunTerminalStatus.Completed,
                WorkflowRunNextAction.None,
                null,
                "Workflow completed.",
                resultHash,
                activeRun.Revision,
                activeRun.CurrentSequence,
                DateTimeOffset.UtcNow),
            CancellationToken.None));
        Assert.Equal(
            "succeeded",
            (await store.WorkflowLedger.GetRunAsync(workflowRunId, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task DoesNotExecutePreparedEffectWhenAtomicReservationFails()
    {
        var path = Path.Combine(directory, "reservation-report.txt");
        await File.WriteAllTextAsync(path, "fixture");
        using var store = new ConversationStore(Path.Combine(directory, "reservation-failure.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-08-02T02:00:00Z");
        const string conversationId = "workflow-reservation-conversation";
        const string parentRunId = "workflow-reservation-parent";
        const string workflowRunId = "workflow-reservation-child";
        await store.StartRunAsync(conversationId, parentRunId, "workflow parent", now);

        var notifications = new RecordingNotificationService();
        var platform = WindowsPlatformProfile.Classify(10, 0, 22_631, isWorkstation: true);
        var host = new WindowsToolHost(
            platform,
            [
                new SystemGetStatusTool(platform),
                new FileGetMetadataTool(new SafeFileOperationService()),
                new SystemShowNotificationTool(notifications),
            ]);
        var catalog = new DesktopWorkflowToolMetadataCatalog(host);
        var plan = BuildPlan(catalog);
        var input = Object(("path", new WorkflowStringValue(path)));
        Assert.True(WorkflowRuntimeContractValidator.TryComputeCanonicalHash(
            input,
            WorkflowInterpreter.MaximumInputBytes,
            out var inputHash));
        var exactVersion = new CapabilityVersionRef(
            CapabilityId.Parse("company.sample.workflow"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            CapabilityContentHash.Parse(Hash('c')));
        var run = new WorkflowRunIdentity(
            workflowRunId,
            conversationId,
            parentRunId,
            "workflow-reservation-parent-tool",
            exactVersion,
            8);
        var context = new CapabilityInvocationContext(
            Hash('d'),
            "cap_1123456789abcdef01234567",
            exactVersion.Id.Value,
            exactVersion.Version.Value,
            exactVersion.ContentHash.Value,
            run.CatalogRevision,
            Hash('e'));
        Assert.Equal(
            WorkflowRunStartOutcome.Created,
            (await store.WorkflowLedger.StartOrCoalesceRunAsync(
                new WorkflowRunStart(
                    workflowRunId,
                    conversationId,
                    parentRunId,
                    run.ParentToolCallId,
                    "workflow-reservation-idempotency",
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
        var rejectingLedger = new RejectEffectReservationLedger(
            new WorkflowExecutionLedger(store.WorkflowLedger));
        var executor = new DesktopWorkflowToolExecutor(
            run,
            plan,
            context,
            catalog,
            host,
            new ToolInvocationCoordinator(policy, host, (_, _) => Task.FromResult(true)),
            rejectingLedger);

        var result = await new WorkflowInterpreter().ExecuteAsync(
            plan,
            WorkflowInputSchema,
            input,
            run,
            executor);
        policy.EndRun(conversationId, workflowRunId);

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.Equal("workflow.ledger.reservation_failed", result.ErrorCode);
        Assert.Equal(1, rejectingLedger.ReserveAttempts);
        Assert.Empty(notifications.Shown);
        Assert.Equal(
            "not_started",
            (await store.WorkflowLedger.GetStepAsync(
                ToolCallId(workflowRunId, 2, 1),
                CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task RejectsSelfConsistentForgedNotificationMetadataBeforePolicyOrEffect()
    {
        var notifications = new RecordingNotificationService();
        var platform = WindowsPlatformProfile.Classify(10, 0, 22_631, isWorkstation: true);
        var host = new WindowsToolHost(
            platform,
            [
                new SystemGetStatusTool(platform),
                new FileGetMetadataTool(new SafeFileOperationService()),
                new SystemShowNotificationTool(notifications),
            ]);
        var catalog = new DesktopWorkflowToolMetadataCatalog(host);
        var valid = BuildPlan(catalog);
        var forgedSteps = valid.Steps.Select(step => step.Ordinal == 2
            ? new ValidatedWorkflowPlanStep(
                step.Ordinal,
                step.Id,
                step.Kind,
                step.SuccessorOrdinals,
                step.CanonicalToolName,
                "R0",
                step.MaximumRetries,
                WorkflowToolPreparationMode.None,
                WorkflowRetryClass.ReadOnly,
                WorkflowToolEffectClass.Observation,
                step.InputContractHash,
                step.ResultContractHash)
            : step).ToArray();
        var forgedHash = WorkflowPlanHasher.ComputePlanHash(
            valid.DefinitionHash,
            valid.InputContractHash,
            forgedSteps,
            valid.RequiredTools,
            "R0");
        var forged = new ValidatedWorkflowPlan(
            valid.Definition,
            valid.DefinitionHash,
            valid.InputContractHash,
            forgedSteps,
            valid.RequiredTools,
            "R0",
            forgedHash);
        var exactVersion = new CapabilityVersionRef(
            CapabilityId.Parse("company.sample.workflow"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            CapabilityContentHash.Parse(Hash('a')));
        var run = new WorkflowRunIdentity(
            "forged-workflow-run",
            "forged-conversation",
            "forged-parent-run",
            "forged-parent-tool",
            exactVersion,
            7);
        var context = new CapabilityInvocationContext(
            Hash('b'),
            "cap_0123456789abcdef01234567",
            exactVersion.Id.Value,
            exactVersion.Version.Value,
            exactVersion.ContentHash.Value,
            run.CatalogRevision,
            Hash('c'));
        var policy = new ToolInvocationPolicy();
        policy.BeginRun(run.ConversationId, run.WorkflowRunId);
        var executor = new DesktopWorkflowToolExecutor(
            run,
            forged,
            context,
            catalog,
            host,
            new ToolInvocationCoordinator(policy, host, (_, _) => Task.FromResult(true)),
            new ThrowingLedger());

        var result = await new WorkflowInterpreter().ExecuteAsync(
            forged,
            WorkflowInputSchema,
            Object(("path", new WorkflowStringValue(Path.Combine(directory, "unused.txt")))),
            run,
            new ForwardNotificationStepExecutor(executor));

        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.Equal("workflow.catalog.drift", result.ErrorCode);
        Assert.Empty(notifications.Shown);
    }

    [Fact]
    public void PersistsOnlyCanonicalSmtpOutcomeUnknownActivitySummary()
    {
        const string fixedPrefix = "메일 서버의 접수 여부를 확인해야 해요. 메시지 식별자: ";
        const string defaultSummary = "작업 흐름의 실행 단계를 완료했습니다.";
        const string rawCanary = "RAW-MAIL-CREDENTIAL-CANARY";
        var messageId = $"<ligclaw.{new string('a', 64)}@ligclaw.invalid>";
        var valid = fixedPrefix + messageId;

        Assert.Equal(
            valid,
            DesktopWorkflowToolExecutor.SelectEffectRedactedSummary(
                "mail.send.v1",
                Execution(WorkflowEffectDisposition.Unknown, "mail.send.outcome_unknown", valid)));
        Assert.Equal(
            defaultSummary,
            DesktopWorkflowToolExecutor.SelectEffectRedactedSummary(
                "mail.send.v1",
                Execution(WorkflowEffectDisposition.Unknown, "mail.send.outcome_unknown", rawCanary)));
        Assert.Equal(
            defaultSummary,
            DesktopWorkflowToolExecutor.SelectEffectRedactedSummary(
                "mail.send.v1",
                Execution(
                    WorkflowEffectDisposition.Unknown,
                    "mail.send.outcome_unknown",
                    fixedPrefix + messageId.Replace('a', 'A'))));
        Assert.Equal(
            defaultSummary,
            DesktopWorkflowToolExecutor.SelectEffectRedactedSummary(
                "mail.send.v1",
                Execution(
                    WorkflowEffectDisposition.Unknown,
                    "mail.send.outcome_unknown",
                    valid + rawCanary)));
        Assert.Equal(
            defaultSummary,
            DesktopWorkflowToolExecutor.SelectEffectRedactedSummary(
                "mail.send.v1",
                Execution(WorkflowEffectDisposition.FailedKnown, "mail.send.outcome_unknown", valid)));
        Assert.Equal(
            defaultSummary,
            DesktopWorkflowToolExecutor.SelectEffectRedactedSummary(
                "mail.send.v1",
                Execution(WorkflowEffectDisposition.Unknown, "mail.send.submit_failed", valid)));
        Assert.Equal(
            defaultSummary,
            DesktopWorkflowToolExecutor.SelectEffectRedactedSummary(
                "connection.http.request.v1",
                Execution(WorkflowEffectDisposition.Unknown, "mail.send.outcome_unknown", valid)));

        static WorkflowPreparedEffectExecution Execution(
            WorkflowEffectDisposition disposition,
            string errorCode,
            string activitySummary) => new(
            disposition,
            new WindowsToolExecutionResult(
                false,
                new Dictionary<string, object?>(),
                Error: rawCanary,
                ActivitySummary: activitySummary,
                ErrorCode: errorCode));
    }

    private static ValidatedWorkflowPlan BuildPlan(IWorkflowToolMetadataCatalog catalog)
    {
        var parsed = new WorkflowDefinitionParser().Parse(WorkflowJson());
        Assert.True(parsed.Succeeded, parsed.Issues.FirstOrDefault()?.Code);
        var manifest = new CapabilityManifest(
            1,
            CapabilityId.Parse("company.sample.workflow"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            "Sample Workflow",
            "Runs the D-3 three Tool fixture.",
            CapabilityKind.Workflow,
            ["Run the D-3 fixture"],
            ["system.get_status.v1", "file.get_metadata.v1", "system.show_notification.v1"],
            [],
            "R1",
            "workflow.json");
        var validation = new WorkflowStaticValidator(catalog).Validate(
            parsed.Definition!,
            manifest,
            WorkflowInputSchema);
        Assert.True(validation.Succeeded, validation.Issues.FirstOrDefault()?.Code);
        return validation.Plan!;
    }

    private static string WorkflowJson() => """
        {"schemaVersion":1,"entryStepId":"status","steps":[
          {"id":"status","type":"tool.call","tool":"system.get_status.v1","arguments":{},"next":"metadata"},
          {"id":"metadata","type":"tool.call","tool":"file.get_metadata.v1","arguments":{"path":{"$bind":{"source":"input","path":"/path"}}},"next":"notify"},
          {"id":"notify","type":"tool.call","tool":"system.show_notification.v1","arguments":{"title":"LIGClaw 상태","message":"운영체제 상태를 확인했습니다.","reason":"D-3 Workflow 검증"},"next":"done"},
          {"id":"done","type":"output","value":{"release":{"$bind":{"source":"step","stepId":"status","path":"/windowsRelease"}},"metadata":{"$bind":{"source":"step","stepId":"metadata","path":""}}}}
        ]}
        """;

    private static WorkflowObjectValue Object(params (string Name, WorkflowValue Value)[] properties) =>
        new(properties.ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal));

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

    private sealed class RecordingNotificationService : IUserNotificationService
    {
        public List<(string Title, string Message)> Shown { get; } = [];

        public Task ShowAsync(string title, string message, CancellationToken cancellationToken)
        {
            Shown.Add((title, message));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingLedger : IWorkflowExecutionLedger
    {
        private static InvalidOperationException Unexpected() =>
            new("Forged metadata must be rejected before the ledger is read.");

        public Task<bool> AddPlannedStepAsync(WorkflowStepPlan step, CancellationToken cancellationToken) =>
            throw Unexpected();
        public Task<bool> SaveAuthorizationTicketAsync(
            WorkflowAuthorizationTicketDraft ticket,
            CancellationToken cancellationToken) => throw Unexpected();
        public Task<bool> TryStartObservationStepAsync(
            WorkflowObservationReservation reservation,
            CancellationToken cancellationToken) => throw Unexpected();
        public Task<bool> TryReserveEffectStepAsync(
            WorkflowEffectReservation reservation,
            CancellationToken cancellationToken) => throw Unexpected();
        public Task<bool> AbandonPlannedStepAsync(
            WorkflowPlannedStepAbandonment abandonment,
            CancellationToken cancellationToken) => throw Unexpected();
        public Task<bool> CompleteStepAsync(
            WorkflowStepCompletion completion,
            CancellationToken cancellationToken) => throw Unexpected();
        public Task<WorkflowRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken) =>
            throw Unexpected();
        public Task<WorkflowStepRunRecord?> GetStepAsync(string toolCallId, CancellationToken cancellationToken) =>
            throw Unexpected();
    }

    private sealed class ForwardNotificationStepExecutor(IWorkflowToolExecutor inner) : IWorkflowToolExecutor
    {
        public Task<WorkflowToolExecutionResponse> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken cancellationToken) => request.StepOrdinal switch
            {
                0 => Task.FromResult(new WorkflowToolExecutionResponse(
                    WorkflowToolExecutionDisposition.Succeeded,
                    Object(
                        ("windowsRelease", new WorkflowStringValue("Windows 11")),
                        ("buildNumber", new WorkflowNumberValue("22631", true)),
                        ("architecture", new WorkflowStringValue("x64")),
                        ("localTime", new WorkflowStringValue("2026-08-02T00:00:00Z")),
                        ("timeZone", new WorkflowStringValue("UTC")),
                        ("powerSource", new WorkflowStringValue("unknown"))))),
                1 => Task.FromResult(new WorkflowToolExecutionResponse(
                    WorkflowToolExecutionDisposition.Succeeded,
                    Object(
                        ("path", request.Payload.Properties["path"]),
                        ("name", new WorkflowStringValue("unused.txt")),
                        ("isDirectory", new WorkflowBooleanValue(false)),
                        ("sizeBytes", new WorkflowNumberValue("0", true)),
                        ("lastWriteTimeUtc", new WorkflowStringValue("2026-08-02T00:00:00Z")),
                        ("readOnly", new WorkflowBooleanValue(false))))),
                _ => inner.ExecuteAsync(request, cancellationToken),
            };
    }

    private sealed class RejectEffectReservationLedger(IWorkflowExecutionLedger inner) : IWorkflowExecutionLedger
    {
        public int ReserveAttempts { get; private set; }

        public Task<bool> AddPlannedStepAsync(WorkflowStepPlan step, CancellationToken cancellationToken) =>
            inner.AddPlannedStepAsync(step, cancellationToken);

        public Task<bool> SaveAuthorizationTicketAsync(
            WorkflowAuthorizationTicketDraft ticket,
            CancellationToken cancellationToken) => inner.SaveAuthorizationTicketAsync(ticket, cancellationToken);

        public Task<bool> TryStartObservationStepAsync(
            WorkflowObservationReservation reservation,
            CancellationToken cancellationToken) => inner.TryStartObservationStepAsync(reservation, cancellationToken);

        public Task<bool> TryReserveEffectStepAsync(
            WorkflowEffectReservation reservation,
            CancellationToken cancellationToken)
        {
            ReserveAttempts++;
            return Task.FromResult(false);
        }

        public Task<bool> AbandonPlannedStepAsync(
            WorkflowPlannedStepAbandonment abandonment,
            CancellationToken cancellationToken) => inner.AbandonPlannedStepAsync(abandonment, cancellationToken);

        public Task<bool> CompleteStepAsync(
            WorkflowStepCompletion completion,
            CancellationToken cancellationToken) => inner.CompleteStepAsync(completion, cancellationToken);

        public Task<WorkflowRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken) =>
            inner.GetRunAsync(runId, cancellationToken);

        public Task<WorkflowStepRunRecord?> GetStepAsync(string toolCallId, CancellationToken cancellationToken) =>
            inner.GetStepAsync(toolCallId, cancellationToken);
    }
}
