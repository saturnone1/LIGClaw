using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Tools;
using LIGClaw.Application.Workflows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Capabilities;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Desktop.Infrastructure.Workflows;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class GenericCapabilityBuilderVerticalTests : IDisposable
{
    private const string Requirement =
        "현재 Windows 시스템 상태를 조회해서 간결한 상태 요약을 만드는 기능을 추가해 줘.";
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-02T06:00:00Z");
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public GenericCapabilityBuilderVerticalTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task NaturalLanguageBuilderActivatesAndRunsGenericR0WorkflowIntoActivity()
    {
        using var store = new ConversationStore(
            Path.Combine(directory, "generic-builder-vertical.db"),
            new FixedTimeProvider(Now));
        await store.InitializeAsync();

        var statusTool = new RecordingSystemStatusTool();
        var host = new WindowsToolHost(
            WindowsPlatformProfile.Classify(10, 0, 22_631, isWorkstation: true),
            [statusTool]);
        var dependencies = new CapabilityToolDependencyCatalog(
            host.Descriptors.Select(descriptor =>
                new CapabilityToolDependency(descriptor.Name, descriptor.Risk)));
        var workflowTools = new DesktopWorkflowToolMetadataCatalog(host);
        Assert.True(workflowTools.TryGet("system.get_status.v1", out var statusMetadata));
        Assert.Equal(WorkflowToolEffectClass.Observation, statusMetadata!.EffectClass);
        Assert.Equal(WorkflowToolPreparationMode.None, statusMetadata.PreparationMode);

        var scanner = new DesktopFeatureBuilderProposalScanner(dependencies, workflowTools);
        var runtime = new QuestionThenStatusWorkflowRuntime();
        var builder = new FeatureBuilderManagementService(store.FeatureBuilder, runtime, scanner);
        var inventory = new FeatureBuilderInventory(
            host.Descriptors.Select(descriptor => descriptor.Name).ToArray(),
            [],
            []);

        var started = await builder.StartSessionAsync(
            new StartFeatureBuilderSessionCommand(
                $"fbs_{new string('1', 32)}",
                Requirement,
                inventory,
                ExpectedCatalogRevision: 0,
                RevisionTarget: null,
                Now),
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, started.Outcome);
        Assert.Equal(FeatureBuilderSessionState.Collecting, started.Session!.State);

        var questions = await builder.GenerateAsync(
            new GenerateFeatureBuilderProposalCommand(
                started.Session.SessionId,
                started.Session.Revision,
                $"fbgen_{new string('2', 32)}",
                Answers: [],
                inventory,
                Now.AddMinutes(1),
                Now.AddSeconds(1)),
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, questions.Outcome);
        Assert.Equal(FeatureBuilderSessionState.AwaitingInformation, questions.Session!.State);
        var question = Assert.Single(questions.Session.Questions);
        Assert.Equal("q_summary_style", question.QuestionId);

        var generated = await builder.GenerateAsync(
            new GenerateFeatureBuilderProposalCommand(
                questions.Session.SessionId,
                questions.Session.Revision,
                $"fbgen_{new string('3', 32)}",
                [new FeatureBuilderAnswer(question.QuestionId, "핵심 항목만 간결하게")],
                inventory,
                Now.AddSeconds(50),
                Now.AddSeconds(2)),
            CancellationToken.None);
        Assert.True(
            generated.Outcome == FeatureBuilderManagementOutcome.Succeeded,
            $"Builder generation failed: {generated.Outcome} / {generated.ErrorCode}");
        var proposal = Assert.IsType<FeatureBuilderProposal>(generated.Proposal);
        Assert.Equal(CapabilityKind.Workflow, proposal.Candidate!.Manifest.Kind);
        Assert.Equal(["system.get_status.v1"], proposal.Candidate.Manifest.RequiredTools);
        Assert.Equal("R0", proposal.Candidate.Manifest.MaximumRisk);
        Assert.Empty(proposal.Candidate.Manifest.RequiredConnections);
        Assert.Equal(2, runtime.Requests.Count);
        Assert.Equal(
            "핵심 항목만 간결하게",
            Assert.Single(runtime.Requests[1].Answers).Value);

        var runner = new DesktopFeatureBuilderTestRunner(
            builder,
            store.FeatureBuilder,
            scanner,
            new FixedTimeProvider(Now.AddSeconds(3)));
        var tested = await runner.RunRequiredTestsAsync(proposal, CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, tested.Outcome);
        Assert.Equal(FeatureBuilderTestRunState.Passed, tested.TestRun!.State);
        var testRuns = await store.FeatureBuilder.ListTestRunsAsync(
            proposal.ProposalId,
            CancellationToken.None);
        Assert.Equal(2, testRuns.Count);
        Assert.All(testRuns, run => Assert.Equal(FeatureBuilderTestRunState.Passed, run.State));
        Assert.Contains(testRuns, run => run.Kind == FeatureBuilderTestRunKind.StaticScan);
        Assert.Contains(testRuns, run => run.Kind == FeatureBuilderTestRunKind.DryRun);

        var applied = await builder.ApplyProposalAsync(
            new ApplyFeatureBuilderProposalCommand(
                proposal.ProposalId,
                proposal.Revision,
                "draft_generic_status_summary",
                Now.AddSeconds(4)),
            CancellationToken.None);
        var reviewDraft = Assert.IsType<CapabilityDraft>(applied.Draft);
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, applied.Outcome);
        Assert.Equal(CapabilityDraftState.ReadyForReview, reviewDraft.State);
        Assert.Equal("builder", reviewDraft.SourceKind);

        var snapshots = new CapabilityRuntimeSnapshotBuilder(
            store.CapabilityRuntimeCandidates,
            dependencies);
        Assert.Empty((await snapshots.BuildAsync(Requirement, CancellationToken.None)).Candidates);

        var capabilityManagement = new CapabilityManagementService(
            new CapabilityCatalogService(
                store.Capabilities,
                dependencies,
                workflowTools),
            new ZipLigcapPackageCodec());
        var activationReview = await capabilityManagement.GetActivationReviewAsync(
            reviewDraft.DraftId,
            reviewDraft.ExactVersion,
            expectedCatalogRevision: 0,
            CancellationToken.None);
        Assert.Equal(CapabilityManagementOutcome.Succeeded, activationReview.Outcome);
        Assert.True(activationReview.Review!.CanActivate);
        var activated = await capabilityManagement.ActivateAsync(
            activationReview.Review,
            Now.AddSeconds(5),
            CancellationToken.None);
        Assert.Equal(CapabilityManagementOutcome.Succeeded, activated.Outcome);
        Assert.Equal(reviewDraft.ExactVersion, activated.Catalog!.ActiveVersion);
        Assert.Equal(1, activated.Catalog.CatalogRevision);

        var snapshot = await snapshots.BuildAsync(Requirement, CancellationToken.None);
        var candidate = Assert.Single(snapshot.Candidates);
        Assert.Equal(reviewDraft.ExactVersion, candidate.ExactVersion);
        Assert.Equal(activated.Catalog.CatalogRevision, candidate.CatalogRevision);
        Assert.Equal(CapabilityKind.Workflow, candidate.Kind);
        Assert.Equal(["system.get_status.v1"], candidate.RequiredTools);
        Assert.Equal("R0", candidate.MaximumRisk);

        var policy = new ToolInvocationPolicy();
        var ids = new Queue<string>(["generic-builder-conversation", "generic-builder-parent-run"]);
        var conversationRun = new ConversationRunController(policy, ids.Dequeue);
        var parentRun = conversationRun.BeginRun();
        try
        {
            await store.StartRunAsync(
                parentRun.ConversationId,
                parentRun.RunId,
                Requirement,
                Now.AddSeconds(6));
            conversationRun.BindCapabilitySnapshot(
                parentRun.ConversationId,
                parentRun.RunId,
                snapshot);

            var selector = new ToolInvokeParams(
                "generic-builder-selector",
                parentRun.ConversationId,
                parentRun.RunId,
                "capability.select.v1",
                "R0",
                new Dictionary<string, object?>
                {
                    ["snapshotHash"] = snapshot.SnapshotHash.Value,
                    ["modelToolName"] = candidate.ModelToolName,
                    ["capabilityId"] = candidate.ExactVersion.Id.Value,
                    ["version"] = candidate.ExactVersion.Version.Value,
                    ["contentHash"] = candidate.ExactVersion.ContentHash.Value,
                    ["catalogRevision"] = candidate.CatalogRevision,
                    ["connectionBindingHash"] = candidate.ConnectionBindingHash,
                });
            var selected = await new ConversationToolInvocationController(conversationRun)
                .ExecuteAsync(selector, execute: null);
            Assert.True(selected.Execution.Success, selected.Execution.Error);
            Assert.Equal(true, selected.Execution.Output["selected"]);
            var selectedContext = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                selected.Execution.Output["context"]);
            Assert.Equal(candidate.ExactVersion.Id.Value, selectedContext["capabilityId"]);
            Assert.Equal(candidate.ExactVersion.ContentHash.Value, selectedContext["contentHash"]);
            Assert.Equal(candidate.CatalogRevision, selectedContext["catalogRevision"]);

            var approvalRequests = 0;
            var orchestrator = new DesktopWorkflowOrchestrator(
                store.Capabilities,
                store.WorkflowLedger,
                workflowTools,
                host,
                workflowPolicy => new ToolInvocationCoordinator(
                    workflowPolicy,
                    host,
                    (_, _) =>
                    {
                        Interlocked.Increment(ref approvalRequests);
                        return Task.FromResult(false);
                    },
                    store,
                    store),
                new FixedTimeProvider(Now.AddSeconds(7)));
            var workflowController = new ConversationWorkflowInvocationController(
                conversationRun,
                orchestrator);
            var workflow = await workflowController.ExecuteAsync(
                new WorkflowInvokeParams(
                    $"wfinv_{new string('4', 48)}",
                    parentRun.ConversationId,
                    parentRun.RunId,
                    "workflow.invoke.v1",
                    new CapabilityInvocationContext(
                        (string)selectedContext["snapshotHash"]!,
                        (string)selectedContext["modelToolName"]!,
                        (string)selectedContext["capabilityId"]!,
                        (string)selectedContext["version"]!,
                        (string)selectedContext["contentHash"]!,
                        (long)selectedContext["catalogRevision"]!,
                        (string)selectedContext["connectionBindingHash"]!),
                    $"wfid_{new string('5', 64)}",
                    Now.AddMinutes(8),
                    new Dictionary<string, object?>()));

            Assert.True(workflow.Success, workflow.Error);
            var output = Assert.IsType<WorkflowInvokeV1Result>(workflow.Output);
            Assert.Equal("succeeded", output.Status);
            Assert.Equal("none", output.NextAction);
            Assert.Equal("Windows 11", output.Result!["windowsRelease"]);
            Assert.Equal(22_631L, output.Result["buildNumber"]);
            Assert.Equal("시스템 상태 조회를 완료했습니다.", output.Result["summary"]);
            Assert.Equal(1, statusTool.ExecutionCount);
            Assert.Equal(0, Volatile.Read(ref approvalRequests));

            var persistedRun = Assert.IsType<WorkflowRunRecord>(
                await store.WorkflowLedger.GetRunAsync(
                    output.WorkflowRunId,
                    CancellationToken.None));
            Assert.Equal("succeeded", persistedRun.Status);
            Assert.Equal(reviewDraft.ExactVersion.Id.Value, persistedRun.CapabilityId);
            Assert.Equal(reviewDraft.ExactVersion.ContentHash.Value, persistedRun.CapabilityContentHash);
            Assert.Equal(snapshot.SnapshotHash.Value, persistedRun.SnapshotHash);

            var activity = Assert.Single((await store.GetRecentToolActivityAsync(cancellationToken: CancellationToken.None))
, item => item.ToolName ==
                    $"Workflow · {reviewDraft.ExactVersion.Id.Value}@{reviewDraft.ExactVersion.Version.Value}");
            Assert.Equal("succeeded", activity.Status);
            Assert.Equal("완료", activity.StatusDisplay);
            Assert.Contains("작업 흐름을 완료했습니다.", activity.Summary, StringComparison.Ordinal);
            Assert.Contains("다음 행동: 없음", activity.Summary, StringComparison.Ordinal);
        }
        finally
        {
            Assert.True(conversationRun.CompleteActiveRun());
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static IReadOnlyList<FeatureBuilderRawProposalArtifact> StatusWorkflowArtifacts() =>
    [
        new(
            "manifest.json",
            "application/json",
            """
            {
              "schemaVersion": 1,
              "id": "company.builder.system-status-summary",
              "version": "1.0.0",
              "displayName": "System status summary",
              "description": "Summarizes current Windows system status with a registered read-only Tool.",
              "kind": "workflow",
              "intentExamples": ["system status summary", "현재 시스템 상태를 요약해 줘"],
              "requiredTools": ["system.get_status.v1"],
              "requiredConnections": [],
              "maximumRisk": "R0",
              "entrypoint": "workflow.json"
            }
            """),
        new(
            "instructions.md",
            "text/markdown",
            "등록된 읽기 전용 시스템 상태 Tool을 한 번 호출하고 결과를 간결하게 요약합니다."),
        new(
            "input.schema.json",
            "application/json",
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": {},
              "additionalProperties": false
            }
            """),
        new(
            "workflow.json",
            "application/json",
            """
            {
              "schemaVersion": 1,
              "entryStepId": "read-status",
              "steps": [
                {
                  "id": "read-status",
                  "type": "tool.call",
                  "tool": "system.get_status.v1",
                  "arguments": {},
                  "next": "done"
                },
                {
                  "id": "done",
                  "type": "output",
                  "value": {
                    "summary": "시스템 상태 조회를 완료했습니다.",
                    "windowsRelease": {
                      "$bind": { "source": "step", "stepId": "read-status", "path": "/windowsRelease" }
                    },
                    "buildNumber": {
                      "$bind": { "source": "step", "stepId": "read-status", "path": "/buildNumber" }
                    }
                  }
                }
              ]
            }
            """),
        new(
            "tests.json",
            "application/json",
            """
            [
              { "name": "accept empty input", "input": {}, "expected": "accepted" },
              { "name": "reject unknown input", "input": { "unexpected": true }, "expected": "rejected" }
            ]
            """),
    ];

    private sealed class QuestionThenStatusWorkflowRuntime : IFeatureBuilderRuntimeClient
    {
        private readonly List<FeatureBuilderRuntimeRequest> requests = [];

        public IReadOnlyList<FeatureBuilderRuntimeRequest> Requests => requests.AsReadOnly();

        public Task<FeatureBuilderRuntimeResult> GenerateAsync(
            FeatureBuilderRuntimeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Add(request);
            return Task.FromResult(requests.Count == 1
                ? new FeatureBuilderRuntimeResult(
                    request.SchemaVersion,
                    request.GenerationId,
                    request.RequestHash,
                    request.SessionId,
                    request.SessionRevision,
                    request.InventoryHash,
                    request.DeadlineUtc,
                    FeatureBuilderRuntimeOutcome.NeedsInformation,
                    Questions:
                    [
                        new FeatureBuilderQuestion(
                            "q_summary_style",
                            "상태 요약은 어느 정도로 자세하게 만들까요?",
                            FeatureBuilderAnswerKind.SingleChoice,
                            ["핵심 항목만 간결하게", "모든 항목을 자세하게"]),
                    ])
                : new FeatureBuilderRuntimeResult(
                    request.SchemaVersion,
                    request.GenerationId,
                    request.RequestHash,
                    request.SessionId,
                    request.SessionRevision,
                    request.InventoryHash,
                    request.DeadlineUtc,
                    FeatureBuilderRuntimeOutcome.Proposal,
                    Artifacts: StatusWorkflowArtifacts(),
                    Limitations: ["현재는 등록된 비민감 시스템 상태 항목만 요약합니다."]));
        }
    }

    private sealed class RecordingSystemStatusTool : IWindowsToolAdapter
    {
        public int ExecutionCount { get; private set; }
        public string Name => "system.get_status.v1";
        public string Risk => "R0";
        public WindowsCapability RequiredCapabilities => WindowsCapability.None;
        public int Priority => 0;

        public Task<WindowsToolExecutionResult> ExecuteAsync(
            IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Empty(input);
            ExecutionCount++;
            return Task.FromResult(new WindowsToolExecutionResult(
                true,
                new Dictionary<string, object?>
                {
                    ["windowsRelease"] = "Windows 11",
                    ["buildNumber"] = 22_631L,
                    ["architecture"] = "x64",
                    ["localTime"] = Now,
                    ["timeZone"] = "Asia/Seoul",
                    ["powerSource"] = "ac",
                },
                ActivitySummary: "시스템 상태를 확인했어요."));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }
}
