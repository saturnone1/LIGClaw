using System.Globalization;
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

public sealed class MailDraftWorkflowVerticalTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-02T03:00:00Z", CultureInfo.InvariantCulture);

    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public MailDraftWorkflowVerticalTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task ParserValidatorOrchestratorAndLedgerReturnOnlyBoundedMailDraftMetadata()
    {
        const string conversationId = "mail-draft-workflow-conversation";
        const string parentRunId = "mail-draft-workflow-parent";
        const string toRecipient = "d6a-to-canary@example.com";
        const string ccRecipient = "d6a-cc-canary@example.com";
        const string bccRecipient = "d6a-bcc-canary@example.com";
        const string subject = "D6A_SUBJECT_CANARY";
        const string body = "D6A_BODY_CANARY\nsecond line";
        using var store = new ConversationStore(Path.Combine(directory, "mail-workflow.db"));
        await store.InitializeAsync();
        await store.StartRunAsync(conversationId, parentRunId, "prepare a mail draft", Now);

        var host = MailOnlyHost();
        var workflowTools = new DesktopWorkflowToolMetadataCatalog(host);
        var draft = CreateMailWorkflowDraft("mail_draft_workflow_vertical");
        var parsed = new WorkflowDefinitionParser().Parse(draft.Payload.CanonicalWorkflowJson!);
        Assert.True(parsed.Succeeded, parsed.Issues.FirstOrDefault()?.Code);
        var validated = new WorkflowStaticValidator(workflowTools).Validate(
            parsed.Definition!,
            draft.Payload.Manifest,
            draft.Payload.CanonicalInputSchemaJson);
        Assert.True(validated.Succeeded, validated.Issues.FirstOrDefault()?.Code);
        Assert.Equal("R0", validated.Plan!.MaximumRisk);
        Assert.True(workflowTools.TryGet("mail.create_draft.v1", out var mailMetadata));
        Assert.Equal(WorkflowToolEffectClass.Observation, mailMetadata!.EffectClass);
        Assert.Equal(WorkflowRetryClass.ReadOnly, mailMetadata.RetryClass);

        var dependencies = new CapabilityToolDependencyCatalog(
            host.Descriptors.Select(descriptor =>
                new CapabilityToolDependency(descriptor.Name, descriptor.Risk)));
        var catalog = new CapabilityCatalogService(store.Capabilities, dependencies, workflowTools);
        var submitted = await catalog.SubmitValidatedDraftAsync(draft, Now, CancellationToken.None);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, submitted.Outcome);
        var activation = await catalog.ApproveAndActivateAsync(
            new CapabilityApprovalCommand(
                draft.DraftId,
                draft.ExactVersion,
                ExpectedDraftRevision: 0,
                ExpectedCatalogRevision: 0,
                ExpectedActiveVersion: null,
                "review.approved",
                Now.AddSeconds(1)),
            CancellationToken.None);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, activation.Outcome);

        var context = CreateInvocationContext(draft, activation.Catalog!);
        var invocation = new WorkflowInvokeParams(
            $"wfinv_{new string('a', 48)}",
            conversationId,
            parentRunId,
            "workflow.invoke.v1",
            context,
            $"wfid_{new string('b', 64)}",
            Now.AddMinutes(8),
            new Dictionary<string, object?>
            {
                ["to"] = new[] { toRecipient },
                ["cc"] = new[] { ccRecipient },
                ["bcc"] = new[] { bccRecipient },
                ["subject"] = subject,
                ["body"] = body,
            });
        var orchestrator = new DesktopWorkflowOrchestrator(
            store.Capabilities,
            store.WorkflowLedger,
            workflowTools,
            host,
            policy => new ToolInvocationCoordinator(
                policy,
                host,
                (_, _) => Task.FromResult(true)),
            new FixedTimeProvider(Now));

        var result = await orchestrator.InvokeAsync(invocation, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("succeeded", result.Output!.Status);
        Assert.Equal("none", result.Output.NextAction);
        var output = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Output.Result);
        Assert.Equal(
            ["bodyLength", "draftHash", "ready", "recipientCount", "subjectLength"],
            output.Keys.Order(StringComparer.Ordinal));
        Assert.True(Assert.IsType<bool>(output["ready"]));
        Assert.Matches("^sha256:[0-9a-f]{64}$", Assert.IsType<string>(output["draftHash"]));
        Assert.Equal(3L, Assert.IsType<long>(output["recipientCount"]));
        Assert.Equal((long)subject.Length, Assert.IsType<long>(output["subjectLength"]));
        Assert.Equal((long)body.Length, Assert.IsType<long>(output["bodyLength"]));

        var publicResult = JsonSerializer.Serialize(result.Output);
        Assert.DoesNotContain(toRecipient, publicResult, StringComparison.Ordinal);
        Assert.DoesNotContain(ccRecipient, publicResult, StringComparison.Ordinal);
        Assert.DoesNotContain(bccRecipient, publicResult, StringComparison.Ordinal);
        Assert.DoesNotContain(subject, publicResult, StringComparison.Ordinal);
        Assert.DoesNotContain(body, publicResult, StringComparison.Ordinal);

        var persistedRun = Assert.IsType<WorkflowRunRecord>(
            await store.WorkflowLedger.GetRunAsync(result.Output.WorkflowRunId, CancellationToken.None));
        Assert.Equal("succeeded", persistedRun.Status);
        Assert.Null(persistedRun.FixedNextAction);
        Assert.Matches("^sha256:[0-9a-f]{64}$", persistedRun.ResultHash);
        Assert.DoesNotContain(toRecipient, persistedRun.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ccRecipient, persistedRun.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(bccRecipient, persistedRun.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(subject, persistedRun.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(body, persistedRun.ToString(), StringComparison.Ordinal);

        var persistedStep = Assert.IsType<WorkflowStepRunRecord>(
            await store.WorkflowLedger.GetStepAsync(
                ToolCallId(result.Output.WorkflowRunId, sequence: 0, attempt: 1),
                CancellationToken.None));
        Assert.Equal("mail.create_draft.v1", persistedStep.ToolName);
        Assert.Equal("R0", persistedStep.Risk);
        Assert.Equal("observation", persistedStep.EffectClass);
        Assert.Equal("read_only", persistedStep.RetryClass);
        Assert.Equal("succeeded", persistedStep.Status);
        Assert.Null(persistedStep.TicketItemId);
        Assert.Matches("^sha256:[0-9a-f]{64}$", persistedStep.OutputHash);
        Assert.DoesNotContain(toRecipient, persistedStep.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ccRecipient, persistedStep.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(bccRecipient, persistedStep.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(subject, persistedStep.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(body, persistedStep.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FeatureBuilderMailWorkflowPassesNoEffectTestsAndCreatesOnlyAReviewDraft()
    {
        using var store = new ConversationStore(Path.Combine(directory, "mail-builder.db"));
        await store.InitializeAsync();
        var host = MailOnlyHost();
        var descriptor = Assert.Single(host.Descriptors);
        Assert.Equal("mail.create_draft.v1", descriptor.Name);
        Assert.DoesNotContain(host.Descriptors, item => item.Name == "connection.http.request.v1");
        var inventory = new FeatureBuilderInventory([descriptor.Name], [], []);
        Assert.Empty(inventory.ConnectionKinds);
        var dependencies = new CapabilityToolDependencyCatalog(
            [new CapabilityToolDependency(descriptor.Name, descriptor.Risk)]);
        var workflowTools = new DesktopWorkflowToolMetadataCatalog(host);
        var scanner = new DesktopFeatureBuilderProposalScanner(dependencies, workflowTools);
        var runtime = new FixedProposalRuntime(MailBuilderArtifacts());
        var service = new FeatureBuilderManagementService(store.FeatureBuilder, runtime, scanner);
        var sessionId = $"fbs_{new string('c', 32)}";
        var started = await service.StartSessionAsync(
            new StartFeatureBuilderSessionCommand(
                sessionId,
                "받는 사람, 제목, 본문을 입력받아 전송하지 않는 메일 초안 검증 기능을 만들어 줘.",
                inventory,
                ExpectedCatalogRevision: 0,
                RevisionTarget: null,
                Now),
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, started.Outcome);

        var generated = await service.GenerateAsync(
            new GenerateFeatureBuilderProposalCommand(
                sessionId,
                ExpectedSessionRevision: 0,
                $"fbgen_{new string('d', 32)}",
                Answers: [],
                inventory,
                DeadlineUtc: Now.AddSeconds(30),
                NowUtc: Now),
            CancellationToken.None);
        var proposal = Assert.IsType<FeatureBuilderProposal>(generated.Proposal);
        Assert.True(
            generated.Outcome == FeatureBuilderManagementOutcome.Succeeded,
            generated.ErrorCode ?? generated.Outcome.ToString());
        Assert.Equal(1, runtime.CallCount);
        Assert.Equal(CapabilityKind.Workflow, proposal.Candidate!.Manifest.Kind);
        Assert.Equal(["mail.create_draft.v1"], proposal.Candidate.Manifest.RequiredTools);
        Assert.Empty(proposal.Candidate.Manifest.RequiredConnections);
        Assert.Null(proposal.Candidate.CanonicalSettingsSchemaJson);
        Assert.Equal("R0", proposal.Candidate.Manifest.MaximumRisk);

        var runner = new DesktopFeatureBuilderTestRunner(
            service,
            store.FeatureBuilder,
            scanner,
            new FixedTimeProvider(Now.AddMinutes(1)));
        var tested = await runner.RunRequiredTestsAsync(proposal, CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, tested.Outcome);
        Assert.Equal(FeatureBuilderTestRunState.Passed, tested.TestRun!.State);
        var testRuns = await store.FeatureBuilder.ListTestRunsAsync(
            proposal.ProposalId,
            CancellationToken.None);
        Assert.Equal(2, testRuns.Count);
        Assert.All(testRuns, run => Assert.Equal(FeatureBuilderTestRunState.Passed, run.State));

        var applied = await service.ApplyProposalAsync(
            new ApplyFeatureBuilderProposalCommand(
                proposal.ProposalId,
                proposal.Revision,
                "draft_builder_mail_workflow",
                Now.AddMinutes(2)),
            CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, applied.Outcome);
        Assert.Equal(CapabilityDraftState.ReadyForReview, applied.Draft!.State);
        var catalog = Assert.IsType<CapabilityCatalogEntry>(
            await store.Capabilities.GetCatalogAsync(
                proposal.Candidate.Manifest.Id,
                CancellationToken.None));
        Assert.Null(catalog.ActiveVersion);
        Assert.Equal(0, catalog.CatalogRevision);
        Assert.Empty(await store.Connections.ListProfilesAsync(CancellationToken.None));
        Assert.Empty(await store.Connections.GetBindingsAsync(
            applied.Draft.ExactVersion,
            CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static WindowsToolHost MailOnlyHost() =>
        new(
            WindowsPlatformProfile.Classify(10, 0, 22_631, isWorkstation: true),
            [new MailCreateDraftTool()]);

    private static CapabilityInvocationContext CreateInvocationContext(
        ValidatedCapabilityDraft draft,
        CapabilityCatalogEntry catalog)
    {
        var candidate = new ApplicationCapabilityRuntimeCandidate(
            draft.ExactVersion,
            catalog.CatalogRevision,
            CapabilityRuntimeHasher.ComputeModelToolName(draft.ExactVersion),
            draft.Payload.Manifest.DisplayName,
            draft.Payload.Manifest.Description,
            CapabilityKind.Workflow,
            draft.Payload.Manifest.IntentExamples,
            draft.Payload.Instructions,
            draft.Payload.CanonicalInputSchemaJson,
            draft.Payload.Manifest.RequiredTools,
            $"sha256:{new string('e', 64)}",
            draft.Payload.Manifest.MaximumRisk);
        var snapshot = ApplicationCapabilityRuntimeSnapshot.Create([candidate]);
        return new CapabilityInvocationContext(
            snapshot.SnapshotHash.Value,
            candidate.ModelToolName,
            draft.ExactVersion.Id.Value,
            draft.ExactVersion.Version.Value,
            draft.ExactVersion.ContentHash.Value,
            candidate.CatalogRevision,
            candidate.ConnectionBindingHash);
    }

    private static ValidatedCapabilityDraft CreateMailWorkflowDraft(string draftId)
    {
        var id = CapabilityId.Parse("company.sample.mail-draft");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        var manifest = new CapabilityManifest(
            1,
            id,
            version,
            "Mail draft workflow",
            "Validates a bounded mail draft without sending it.",
            CapabilityKind.Workflow,
            ["prepare a mail draft without sending"],
            ["mail.create_draft.v1"],
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
                requiredTools = manifest.RequiredTools,
                schemaVersion = 1,
                version = version.Value,
            }),
            "Validate the declared mail draft through the registered local-only Tool.",
            MailInputSchema,
            CanonicalWorkflowJson: MailWorkflowJson);
        var hash = CapabilityContentHasher.Compute(payload);
        return new ValidatedCapabilityDraft(draftId, payload, hash, null, "local_import", hash);
    }

    private static IReadOnlyList<FeatureBuilderRawProposalArtifact> MailBuilderArtifacts() =>
    [
        new(
            "manifest.json",
            "application/json",
            """
            {
              "schemaVersion": 1,
              "id": "company.builder.mail-draft",
              "version": "1.0.0",
              "displayName": "메일 초안 준비",
              "description": "받는 사람, 제목, 본문을 검증해 전송하지 않는 로컬 메일 초안을 준비합니다.",
              "kind": "workflow",
              "intentExamples": ["이 내용으로 메일 초안을 준비해 줘"],
              "requiredTools": ["mail.create_draft.v1"],
              "requiredConnections": [],
              "maximumRisk": "R0",
              "entrypoint": "workflow.json"
            }
            """),
        new(
            "instructions.md",
            "text/markdown",
            "등록된 로컬 메일 초안 검증 도구만 사용하며 전송이나 외부 연결은 수행하지 않습니다."),
        new("input.schema.json", "application/json", MailInputSchema),
        new("workflow.json", "application/json", MailWorkflowJson),
        new(
            "tests.json",
            "application/json",
            """
            [
              {
                "name": "accept a bounded mail draft",
                "input": {
                  "to": ["owner@example.com"],
                  "cc": ["reviewer@example.com"],
                  "bcc": ["audit@example.com"],
                  "subject": "주간 보고",
                  "body": "검토할 초안입니다."
                },
                "expected": "accepted"
              },
              {
                "name": "reject an empty body",
                "input": {
                  "to": ["owner@example.com"],
                  "cc": ["reviewer@example.com"],
                  "bcc": ["audit@example.com"],
                  "subject": "주간 보고",
                  "body": ""
                },
                "expected": "rejected"
              },
              {
                "name": "reject a duplicate recipient",
                "input": {
                  "to": ["owner@example.com", "owner@example.com"],
                  "cc": ["reviewer@example.com"],
                  "bcc": ["audit@example.com"],
                  "subject": "주간 보고",
                  "body": "검토할 초안입니다."
                },
                "expected": "rejected"
              }
            ]
            """),
    ];

    private static string ToolCallId(string runId, int sequence, int attempt)
    {
        var hash = WorkflowPreparedEffectHash.Compute(
            "workflow-step-tool-call-v1",
            runId,
            sequence.ToString(CultureInfo.InvariantCulture),
            attempt.ToString(CultureInfo.InvariantCulture));
        return $"wfs-{hash[7..]}";
    }

    private const string MailInputSchema =
        "{\"additionalProperties\":false,\"properties\":{\"bcc\":{\"items\":{\"format\":\"email\",\"maxLength\":254,\"minLength\":3,\"type\":\"string\"},\"maxItems\":16,\"minItems\":1,\"type\":\"array\",\"uniqueItems\":true},\"body\":{\"maxLength\":4096,\"minLength\":1,\"type\":\"string\"},\"cc\":{\"items\":{\"format\":\"email\",\"maxLength\":254,\"minLength\":3,\"type\":\"string\"},\"maxItems\":16,\"minItems\":1,\"type\":\"array\",\"uniqueItems\":true},\"subject\":{\"maxLength\":200,\"minLength\":1,\"type\":\"string\"},\"to\":{\"items\":{\"format\":\"email\",\"maxLength\":254,\"minLength\":3,\"type\":\"string\"},\"maxItems\":16,\"minItems\":1,\"type\":\"array\",\"uniqueItems\":true}},\"required\":[\"to\",\"cc\",\"bcc\",\"subject\",\"body\"],\"type\":\"object\"}";

    private const string MailWorkflowJson =
        "{\"schemaVersion\":1,\"entryStepId\":\"draft\",\"steps\":[{\"id\":\"draft\",\"type\":\"tool.call\",\"tool\":\"mail.create_draft.v1\",\"arguments\":{\"to\":{\"$bind\":{\"source\":\"input\",\"path\":\"/to\"}},\"cc\":{\"$bind\":{\"source\":\"input\",\"path\":\"/cc\"}},\"bcc\":{\"$bind\":{\"source\":\"input\",\"path\":\"/bcc\"}},\"subject\":{\"$bind\":{\"source\":\"input\",\"path\":\"/subject\"}},\"body\":{\"$bind\":{\"source\":\"input\",\"path\":\"/body\"}}},\"next\":\"done\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{\"bodyLength\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"draft\",\"path\":\"/bodyLength\"}},\"draftHash\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"draft\",\"path\":\"/draftHash\"}},\"ready\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"draft\",\"path\":\"/ready\"}},\"recipientCount\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"draft\",\"path\":\"/recipientCount\"}},\"subjectLength\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"draft\",\"path\":\"/subjectLength\"}}}}]}";

    private sealed class FixedProposalRuntime(
        IReadOnlyList<FeatureBuilderRawProposalArtifact> artifacts) : IFeatureBuilderRuntimeClient
    {
        internal int CallCount { get; private set; }

        public Task<FeatureBuilderRuntimeResult> GenerateAsync(
            FeatureBuilderRuntimeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new FeatureBuilderRuntimeResult(
                request.SchemaVersion,
                request.GenerationId,
                request.RequestHash,
                request.SessionId,
                request.SessionRevision,
                request.InventoryHash,
                request.DeadlineUtc,
                FeatureBuilderRuntimeOutcome.Proposal,
                Artifacts: artifacts,
                Limitations: ["초안 검증만 수행하며 전송은 지원하지 않습니다."]));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }
}
