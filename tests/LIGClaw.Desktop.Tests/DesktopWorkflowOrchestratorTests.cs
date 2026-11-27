using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Tools;
using LIGClaw.Application.Workflows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Capabilities;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Desktop.Infrastructure.Workflows;
using LIGClaw.Domain;
using ApplicationCapabilityRuntimeCandidate = LIGClaw.Application.Capabilities.CapabilityRuntimeCandidate;
using ApplicationCapabilityRuntimeSnapshot = LIGClaw.Application.Capabilities.CapabilityRuntimeSnapshot;

namespace LIGClaw.Desktop.Tests;

public sealed class DesktopWorkflowOrchestratorTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public DesktopWorkflowOrchestratorTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task RevalidatesFrozenWorkflowExecutesOnceAfterLaterActivationAndCoalescesReplay()
    {
        var filePath = Path.Combine(directory, "workflow-input.txt");
        await File.WriteAllTextAsync(filePath, "fixture");
        using var store = new ConversationStore(Path.Combine(directory, "workflow-orchestrator.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        const string conversationId = "workflow-orchestrator-conversation";
        const string parentRunId = "workflow-orchestrator-parent";
        await store.StartRunAsync(conversationId, parentRunId, "parent request", now);

        var notifications = new BlockingNotificationService();
        var platform = WindowsPlatformProfile.Classify(10, 0, 22_631, isWorkstation: true);
        var host = new WindowsToolHost(
            platform,
            [
                new SystemGetStatusTool(platform),
                new FileGetMetadataTool(new SafeFileOperationService()),
                new SystemShowNotificationTool(notifications),
            ]);
        var workflowTools = new DesktopWorkflowToolMetadataCatalog(host);
        var dependencies = new CapabilityToolDependencyCatalog(
            host.Descriptors.Select(descriptor =>
                new CapabilityToolDependency(descriptor.Name, descriptor.Risk)));
        var catalog = new CapabilityCatalogService(store.Capabilities, dependencies, workflowTools);
        var draft = CreateDraft();
        var submission = await catalog.SubmitValidatedDraftAsync(draft, now, CancellationToken.None);
        Assert.True(
            submission.Outcome == CapabilityMutationOutcome.Succeeded,
            submission.ErrorCode ?? submission.Outcome.ToString());
        var activation = await catalog.ApproveAndActivateAsync(
            new CapabilityApprovalCommand(
                draft.DraftId,
                draft.ExactVersion,
                0,
                0,
                null,
                "review.approved",
                now.AddSeconds(1)),
            CancellationToken.None);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, activation.Outcome);
        Assert.Equal(1, activation.Catalog!.CatalogRevision);

        var candidate = new ApplicationCapabilityRuntimeCandidate(
            draft.ExactVersion,
            activation.Catalog.CatalogRevision,
            CapabilityRuntimeHasher.ComputeModelToolName(draft.ExactVersion),
            draft.Payload.Manifest.DisplayName,
            draft.Payload.Manifest.Description,
            CapabilityKind.Workflow,
            draft.Payload.Manifest.IntentExamples,
            draft.Payload.Instructions,
            draft.Payload.CanonicalInputSchemaJson,
            draft.Payload.Manifest.RequiredTools,
            $"sha256:{new string('d', 64)}",
            draft.Payload.Manifest.MaximumRisk);
        var snapshot = ApplicationCapabilityRuntimeSnapshot.Create([candidate]);
        var context = new CapabilityInvocationContext(
            snapshot.SnapshotHash.Value,
            candidate.ModelToolName,
            draft.ExactVersion.Id.Value,
            draft.ExactVersion.Version.Value,
            draft.ExactVersion.ContentHash.Value,
            candidate.CatalogRevision,
            candidate.ConnectionBindingHash);
        var replacement = CreateDraft("2.0.0", "workflow_draft_2", draft.ExactVersion);
        Assert.Equal(
            CapabilityMutationOutcome.Succeeded,
            (await catalog.SubmitValidatedDraftAsync(
                replacement,
                now.AddSeconds(2),
                CancellationToken.None)).Outcome);
        var replacementActivation = await catalog.ApproveAndActivateAsync(
            new CapabilityApprovalCommand(
                replacement.DraftId,
                replacement.ExactVersion,
                0,
                activation.Catalog.CatalogRevision,
                draft.ExactVersion,
                "review.approved",
                now.AddSeconds(3)),
            CancellationToken.None);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, replacementActivation.Outcome);
        Assert.Equal(replacement.ExactVersion, replacementActivation.Catalog!.ActiveVersion);
        var input = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(new { path = filePath }))!;
        var invocation = new WorkflowInvokeParams(
            $"wfinv_{new string('a', 48)}",
            conversationId,
            parentRunId,
            "workflow.invoke.v1",
            context,
            $"wfid_{new string('b', 64)}",
            now.AddMinutes(8),
            input);
        var orchestrator = new DesktopWorkflowOrchestrator(
            store.Capabilities,
            store.WorkflowLedger,
            workflowTools,
            host,
            policy => new ToolInvocationCoordinator(
                policy,
                host,
                (_, _) => Task.FromResult(true)));

        var firstTask = orchestrator.InvokeAsync(invocation, CancellationToken.None);
        await notifications.ExecutionStarted.WaitAsync(TimeSpan.FromSeconds(2));
        var duplicateTask = orchestrator.InvokeAsync(invocation, CancellationToken.None);
        var premature = await Task.WhenAny(duplicateTask, Task.Delay(TimeSpan.FromMilliseconds(250)));
        notifications.Release();
        Assert.NotSame(duplicateTask, premature);
        var first = await firstTask;
        var duplicate = await duplicateTask;
        var replay = await orchestrator.InvokeAsync(invocation, CancellationToken.None);

        Assert.True(first.Success);
        Assert.Equal("succeeded", first.Output!.Status);
        Assert.Equal("none", first.Output.NextAction);
        Assert.NotNull(first.Output.Result);
        Assert.Equal(("LIGClaw 상태", "운영체제 상태를 확인했습니다."), Assert.Single(notifications.Shown));
        Assert.True(replay.Success);
        Assert.Equal(first.Output.WorkflowRunId, replay.Output!.WorkflowRunId);
        Assert.Equal("succeeded", replay.Output.Status);
        Assert.True(duplicate.Success);
        Assert.Equal(first.Output.WorkflowRunId, duplicate.Output!.WorkflowRunId);
        Assert.Equal("succeeded", duplicate.Output.Status);
        Assert.Single(notifications.Shown);
        var persisted = await store.WorkflowLedger.GetRunAsync(
            first.Output.WorkflowRunId,
            CancellationToken.None);
        Assert.Equal("succeeded", persisted!.Status);
        Assert.NotNull(persisted.CompletedAtUtc);
    }

    [Fact]
    public async Task RejectsStaleCatalogContextBeforeCreatingARunOrEffect()
    {
        using var store = new ConversationStore(Path.Combine(directory, "workflow-stale.db"));
        await store.InitializeAsync();
        var notifications = new RecordingNotificationService();
        var platform = WindowsPlatformProfile.Classify(10, 0, 22_631, isWorkstation: true);
        var host = new WindowsToolHost(
            platform,
            [
                new SystemGetStatusTool(platform),
                new FileGetMetadataTool(new SafeFileOperationService()),
                new SystemShowNotificationTool(notifications),
            ]);
        var workflowTools = new DesktopWorkflowToolMetadataCatalog(host);
        var draft = CreateDraft();
        var context = new CapabilityInvocationContext(
            $"sha256:{new string('c', 64)}",
            CapabilityRuntimeHasher.ComputeModelToolName(draft.ExactVersion),
            draft.ExactVersion.Id.Value,
            draft.ExactVersion.Version.Value,
            draft.ExactVersion.ContentHash.Value,
            99,
            $"sha256:{new string('d', 64)}");
        var orchestrator = new DesktopWorkflowOrchestrator(
            store.Capabilities,
            store.WorkflowLedger,
            workflowTools,
            host,
            policy => new ToolInvocationCoordinator(
                policy,
                host,
                (_, _) => Task.FromResult(true)));
        var invocation = new WorkflowInvokeParams(
            $"wfinv_{new string('d', 48)}",
            "stale-conversation",
            "stale-parent-run",
            "workflow.invoke.v1",
            context,
            $"wfid_{new string('e', 64)}",
            DateTimeOffset.UtcNow.AddMinutes(8),
            new Dictionary<string, object?> { ["path"] = Path.Combine(directory, "missing.txt") });

        var result = await orchestrator.InvokeAsync(invocation, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("workflow.capability.stale", result.Error);
        Assert.Empty(notifications.Shown);
    }

    [Fact]
    public async Task NeedsInformationReplayReturnsTheSameStructuredRequiredPointers()
    {
        using var store = new ConversationStore(Path.Combine(directory, "workflow-needs-information.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        const string conversationId = "workflow-needs-information-conversation";
        const string parentRunId = "workflow-needs-information-parent";
        await store.StartRunAsync(conversationId, parentRunId, "parent request", now);
        var host = new WindowsToolHost(
            WindowsPlatformProfile.Classify(10, 0, 22_631, isWorkstation: true),
            []);
        var workflowTools = new DesktopWorkflowToolMetadataCatalog(host);
        var catalog = new CapabilityCatalogService(
            store.Capabilities,
            new CapabilityToolDependencyCatalog([]),
            workflowTools);
        var draft = CreateNeedsInformationDraft();
        var submission = await catalog.SubmitValidatedDraftAsync(draft, now, CancellationToken.None);
        Assert.True(
            submission.Outcome == CapabilityMutationOutcome.Succeeded,
            submission.ErrorCode ?? submission.Outcome.ToString());
        var activation = await catalog.ApproveAndActivateAsync(
            new CapabilityApprovalCommand(
                draft.DraftId,
                draft.ExactVersion,
                0,
                0,
                null,
                "review.approved",
                now.AddSeconds(1)),
            CancellationToken.None);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, activation.Outcome);
        var candidate = new ApplicationCapabilityRuntimeCandidate(
            draft.ExactVersion,
            activation.Catalog!.CatalogRevision,
            CapabilityRuntimeHasher.ComputeModelToolName(draft.ExactVersion),
            draft.Payload.Manifest.DisplayName,
            draft.Payload.Manifest.Description,
            CapabilityKind.Workflow,
            draft.Payload.Manifest.IntentExamples,
            draft.Payload.Instructions,
            draft.Payload.CanonicalInputSchemaJson,
            draft.Payload.Manifest.RequiredTools,
            $"sha256:{new string('d', 64)}",
            draft.Payload.Manifest.MaximumRisk);
        var snapshot = ApplicationCapabilityRuntimeSnapshot.Create([candidate]);
        var invocation = new WorkflowInvokeParams(
            $"wfinv_{new string('f', 48)}",
            conversationId,
            parentRunId,
            "workflow.invoke.v1",
            new CapabilityInvocationContext(
                snapshot.SnapshotHash.Value,
                candidate.ModelToolName,
                draft.ExactVersion.Id.Value,
                draft.ExactVersion.Version.Value,
                draft.ExactVersion.ContentHash.Value,
                candidate.CatalogRevision,
                candidate.ConnectionBindingHash),
            $"wfid_{new string('1', 64)}",
            now.AddMinutes(8),
            new Dictionary<string, object?>());
        var orchestrator = new DesktopWorkflowOrchestrator(
            store.Capabilities,
            store.WorkflowLedger,
            workflowTools,
            host,
            policy => new ToolInvocationCoordinator(
                policy,
                host,
                (_, _) => Task.FromResult(true)));

        var first = await orchestrator.InvokeAsync(invocation, CancellationToken.None);
        var replay = await orchestrator.InvokeAsync(invocation, CancellationToken.None);

        Assert.True(first.Success);
        Assert.Equal("needs_information", first.Output!.Status);
        Assert.Equal("provide_information", first.Output.NextAction);
        Assert.Equal(["/recipient"], first.Output.RequiredInputs);
        Assert.True(replay.Success);
        Assert.Equal(first.Output.WorkflowRunId, replay.Output!.WorkflowRunId);
        Assert.Equal("needs_information", replay.Output.Status);
        Assert.Equal(["/recipient"], replay.Output.RequiredInputs);
    }

    private static ValidatedCapabilityDraft CreateDraft(
        string versionValue = "1.0.0",
        string draftId = "workflow_draft_1",
        CapabilityVersionRef? parent = null)
    {
        var id = CapabilityId.Parse("company.sample.workflow");
        var version = CapabilitySemanticVersion.Parse(versionValue);
        var manifest = new CapabilityManifest(
            1,
            id,
            version,
            "Workflow fixture",
            "Runs the D-3 Desktop Workflow fixture.",
            CapabilityKind.Workflow,
            ["run workflow fixture"],
            ["system.get_status.v1", "file.get_metadata.v1", "system.show_notification.v1"],
            [],
            "R1",
            "workflow.json");
        var canonicalManifest = JsonSerializer.Serialize(new
        {
            description = manifest.Description,
            displayName = manifest.DisplayName,
            entrypoint = "workflow.json",
            id = id.Value,
            intentExamples = manifest.IntentExamples,
            kind = "workflow",
            maximumRisk = "R1",
            requiredConnections = Array.Empty<object>(),
            requiredTools = manifest.RequiredTools,
            schemaVersion = 1,
            version = version.Value,
        });
        var payload = new CapabilityVersionPayload(
            manifest,
            canonicalManifest,
            "Run the declared immutable Workflow.",
            "{\"additionalProperties\":false,\"properties\":{\"path\":{\"maxLength\":4096,\"minLength\":3,\"type\":\"string\"}},\"required\":[\"path\"],\"type\":\"object\"}",
            CanonicalWorkflowJson:
                "{\"schemaVersion\":1,\"entryStepId\":\"status\",\"steps\":[{\"id\":\"status\",\"type\":\"tool.call\",\"tool\":\"system.get_status.v1\",\"arguments\":{},\"next\":\"metadata\"},{\"id\":\"metadata\",\"type\":\"tool.call\",\"tool\":\"file.get_metadata.v1\",\"arguments\":{\"path\":{\"$bind\":{\"source\":\"input\",\"path\":\"/path\"}}},\"next\":\"notify\"},{\"id\":\"notify\",\"type\":\"tool.call\",\"tool\":\"system.show_notification.v1\",\"arguments\":{\"title\":\"LIGClaw 상태\",\"message\":\"운영체제 상태를 확인했습니다.\",\"reason\":\"D-3 Workflow 검증\"},\"next\":\"done\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{\"release\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"status\",\"path\":\"/windowsRelease\"}},\"metadata\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"metadata\",\"path\":\"\"}}}}]}");
        var hash = CapabilityContentHasher.Compute(payload);
        return new ValidatedCapabilityDraft(
            draftId,
            payload,
            hash,
            parent,
            "local_import",
            hash);
    }

    private static ValidatedCapabilityDraft CreateNeedsInformationDraft()
    {
        var id = CapabilityId.Parse("company.sample.needs-information");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        var manifest = new CapabilityManifest(
            1,
            id,
            version,
            "Workflow needs information fixture",
            "Requests a declared missing field without executing a Tool.",
            CapabilityKind.Workflow,
            ["request missing workflow information"],
            [],
            [],
            "R0",
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
                maximumRisk = "R0",
                requiredConnections = Array.Empty<object>(),
                requiredTools = Array.Empty<string>(),
                schemaVersion = 1,
                version = version.Value,
            }),
            "Ask only for the declared missing input.",
            "{\"additionalProperties\":false,\"properties\":{\"recipient\":{\"maxLength\":320,\"minLength\":3,\"type\":\"string\"}},\"type\":\"object\"}",
            CanonicalWorkflowJson:
                "{\"schemaVersion\":1,\"entryStepId\":\"choose\",\"steps\":[{\"id\":\"choose\",\"type\":\"condition\",\"left\":false,\"operator\":\"isTrue\",\"then\":\"done\",\"else\":\"missing\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{}},{\"id\":\"missing\",\"type\":\"needs_information\",\"requiredInputs\":[\"/recipient\"]}]}");
        var hash = CapabilityContentHasher.Compute(payload);
        return new ValidatedCapabilityDraft(
            "workflow_needs_information_draft",
            payload,
            hash,
            null,
            "local_import",
            hash);
    }

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

    private sealed class BlockingNotificationService : IUserNotificationService
    {
        private readonly TaskCompletionSource executionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(string Title, string Message)> Shown { get; } = [];
        public Task ExecutionStarted => executionStarted.Task;

        public async Task ShowAsync(string title, string message, CancellationToken cancellationToken)
        {
            Shown.Add((title, message));
            executionStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }

        public void Release() => release.TrySetResult();
    }
}
