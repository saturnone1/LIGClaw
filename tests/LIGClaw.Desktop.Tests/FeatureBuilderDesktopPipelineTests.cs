using System.Reflection;
using System.Text;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Workflows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Capabilities;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Tests;

public sealed class FeatureBuilderDesktopPipelineTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-02T01:00:00Z");

    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public FeatureBuilderDesktopPipelineTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task CanonicalPromptFixturesPassDesktopTestsAndApplyAsReviewOnlyDraft()
    {
        using var store = CreateStore("passing.db");
        await store.InitializeAsync();
        var generated = await GenerateAsync(store, CanonicalArtifacts(), '1');
        var proposal = Assert.IsType<FeatureBuilderProposal>(generated.Result.Proposal);

        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, generated.Result.Outcome);
        Assert.Equal(FeatureBuilderProposalState.Pending, proposal.State);
        Assert.Equal(
            "[{\"expected\":\"accepted\",\"input\":{\"text\":\"ready\"},\"name\":\"accept bounded text\"}," +
            "{\"expected\":\"rejected\",\"input\":{\"text\":\"\"},\"name\":\"reject empty text\"}]",
            proposal.Candidate!.CanonicalTestsJson);

        var runner = new DesktopFeatureBuilderTestRunner(
            generated.Service,
            store.FeatureBuilder,
            generated.Scanner,
            new FixedTimeProvider(Now.AddMinutes(1)));
        var tested = await runner.RunRequiredTestsAsync(proposal, CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, tested.Outcome);
        Assert.Equal(FeatureBuilderTestRunState.Passed, tested.TestRun!.State);
        var runs = await store.FeatureBuilder.ListTestRunsAsync(
            proposal.ProposalId,
            CancellationToken.None);
        Assert.Collection(
            runs.OrderBy(run => run.Kind),
            run =>
            {
                Assert.Equal(FeatureBuilderTestRunKind.StaticScan, run.Kind);
                Assert.Equal(FeatureBuilderTestRunState.Passed, run.State);
                Assert.Equal(proposal.CandidateContentHash, run.CandidateContentHash);
            },
            run =>
            {
                Assert.Equal(FeatureBuilderTestRunKind.DryRun, run.Kind);
                Assert.Equal(FeatureBuilderTestRunState.Passed, run.State);
                Assert.Equal(proposal.CandidateContentHash, run.CandidateContentHash);
            });

        var applied = await generated.Service.ApplyProposalAsync(
            new ApplyFeatureBuilderProposalCommand(
                proposal.ProposalId,
                proposal.Revision,
                "draft_builder_pipeline_pass",
                Now.AddMinutes(2)),
            CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, applied.Outcome);
        Assert.Equal(FeatureBuilderProposalState.Applied, applied.Proposal!.State);
        Assert.NotNull(applied.Proposal.TestEvidenceHash);
        Assert.Equal(CapabilityDraftState.ReadyForReview, applied.Draft!.State);
        Assert.Equal("builder", applied.Draft.SourceKind);
        var catalog = Assert.IsType<CapabilityCatalogEntry>(
            await store.Capabilities.GetCatalogAsync(
                proposal.Candidate.Manifest.Id,
                CancellationToken.None));
        Assert.Null(catalog.ActiveVersion);
        Assert.Equal(0, catalog.CatalogRevision);
        Assert.Equal(1, generated.Runtime.CallCount);
    }

    [Fact]
    public async Task MissingInformationThenTypedWorkflowBecomesAReviewOnlyDraft()
    {
        using var store = CreateStore("workflow-questions.db");
        await store.InitializeAsync();
        var inventory = new FeatureBuilderInventory(
            ["system.get_status.v1", "system.show_notification.v1"],
            [],
            []);
        var scanner = new DesktopFeatureBuilderProposalScanner(
            new Dependencies(),
            new WorkflowCatalog());
        var runtime = new QuestionThenWorkflowRuntime(WorkflowArtifacts());
        var service = new FeatureBuilderManagementService(store.FeatureBuilder, runtime, scanner);
        var started = await service.StartSessionAsync(
            new StartFeatureBuilderSessionCommand(
                $"fbs_{new string('9', 32)}",
                "시스템 상태를 확인한 뒤 사용자가 정한 제목으로 로컬 알림을 보여 주는 기능을 만들어 줘.",
                inventory,
                ExpectedCatalogRevision: 0,
                RevisionTarget: null,
                Now),
            CancellationToken.None);

        var questions = await service.GenerateAsync(
            new GenerateFeatureBuilderProposalCommand(
                started.Session!.SessionId,
                started.Session.Revision,
                $"fbgen_{new string('9', 32)}",
                Answers: [],
                inventory,
                DeadlineUtc: Now.AddSeconds(30),
                NowUtc: Now),
            CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, questions.Outcome);
        Assert.Equal(FeatureBuilderSessionState.AwaitingInformation, questions.Session!.State);
        Assert.Collection(
            questions.Session.Questions,
            question => Assert.Equal("q_title", question.QuestionId),
            question => Assert.Equal("q_notice_style", question.QuestionId));

        var generated = await service.GenerateAsync(
            new GenerateFeatureBuilderProposalCommand(
                questions.Session.SessionId,
                questions.Session.Revision,
                $"fbgen_{new string('a', 32)}",
                [
                    new FeatureBuilderAnswer("q_title", "업무 PC 상태"),
                    new FeatureBuilderAnswer("q_notice_style", "간단한 알림"),
                ],
                inventory,
                DeadlineUtc: Now.AddSeconds(40),
                NowUtc: Now.AddSeconds(1)),
            CancellationToken.None);
        var proposal = Assert.IsType<FeatureBuilderProposal>(generated.Proposal);

        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, generated.Outcome);
        Assert.Equal(CapabilityKind.Workflow, proposal.Candidate!.Manifest.Kind);
        Assert.Equal(
            ["system.get_status.v1", "system.show_notification.v1"],
            proposal.Candidate.Manifest.RequiredTools);
        Assert.Equal(2, runtime.CallCount);
        Assert.Equal(
            ["q_title", "q_notice_style"],
            runtime.Requests[1].Answers.Select(answer => answer.QuestionId));

        var runner = new DesktopFeatureBuilderTestRunner(
            service,
            store.FeatureBuilder,
            scanner,
            new FixedTimeProvider(Now.AddMinutes(1)));
        var tested = await runner.RunRequiredTestsAsync(proposal, CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, tested.Outcome);
        Assert.Equal(FeatureBuilderTestRunState.Passed, tested.TestRun!.State);

        var applied = await service.ApplyProposalAsync(
            new ApplyFeatureBuilderProposalCommand(
                proposal.ProposalId,
                proposal.Revision,
                "draft_builder_typed_workflow",
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
    }

    [Fact]
    public async Task FixtureExpectationMismatchFailsDryRunAndCannotBecomeADraft()
    {
        using var store = CreateStore("mismatch.db");
        await store.InitializeAsync();
        var generated = await GenerateAsync(
            store,
            CanonicalArtifacts(
                testsJson:
                "[{\"name\":\"incorrectly accept empty text\",\"input\":{\"text\":\"\"},\"expected\":\"accepted\"}]"),
            '2');
        var proposal = Assert.IsType<FeatureBuilderProposal>(generated.Result.Proposal);
        var runner = new DesktopFeatureBuilderTestRunner(
            generated.Service,
            store.FeatureBuilder,
            generated.Scanner,
            new FixedTimeProvider(Now.AddMinutes(1)));

        var tested = await runner.RunRequiredTestsAsync(proposal, CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, tested.Outcome);
        Assert.Equal(FeatureBuilderTestRunState.Failed, tested.TestRun!.State);
        Assert.Equal("feature_builder.dry_run.fixture_mismatch", tested.TestRun.FixedErrorCode);
        var runs = await store.FeatureBuilder.ListTestRunsAsync(
            proposal.ProposalId,
            CancellationToken.None);
        Assert.Equal(2, runs.Count);
        Assert.Equal(
            FeatureBuilderTestRunState.Passed,
            Assert.Single(runs, run => run.Kind == FeatureBuilderTestRunKind.StaticScan).State);
        Assert.Equal(
            FeatureBuilderTestRunState.Failed,
            Assert.Single(runs, run => run.Kind == FeatureBuilderTestRunKind.DryRun).State);

        var apply = await generated.Service.ApplyProposalAsync(
            new ApplyFeatureBuilderProposalCommand(
                proposal.ProposalId,
                proposal.Revision,
                "draft_builder_pipeline_mismatch",
                Now.AddMinutes(2)),
            CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.NotReady, apply.Outcome);
        Assert.Equal("feature_builder.proposal.tests_not_ready", apply.ErrorCode);
        Assert.Null(await store.Capabilities.GetDraftAsync(
            "draft_builder_pipeline_mismatch",
            CancellationToken.None));
        Assert.Equal(1, generated.Runtime.CallCount);
    }

    [Fact]
    public async Task WorkflowSemanticV2ExecutesTheInterpreterAndPersistsOnlyHashedEvidence()
    {
        using var store = CreateStore("workflow-semantic-pass.db");
        await store.InitializeAsync();
        var generated = await GenerateAsync(
            store,
            SemanticWorkflowArtifacts(),
            'a',
            new WorkflowCatalog(),
            SemanticInventory());
        Assert.True(
            generated.Result.Proposal is not null,
            $"{generated.Result.Outcome}:{generated.Result.ErrorCode}");
        var proposal = Assert.IsType<FeatureBuilderProposal>(generated.Result.Proposal);
        Assert.True(
            generated.Result.Outcome == FeatureBuilderManagementOutcome.Succeeded,
            generated.Result.ErrorCode ?? proposal.FixedIssueCode);
        Assert.Equal(FeatureBuilderProposalState.Pending, proposal.State);
        var runner = new DesktopFeatureBuilderTestRunner(
            generated.Service,
            store.FeatureBuilder,
            generated.Scanner,
            new FixedTimeProvider(Now.AddMinutes(1)));

        var tested = await runner.RunRequiredTestsAsync(proposal, CancellationToken.None);

        Assert.True(
            tested.Outcome == FeatureBuilderManagementOutcome.Succeeded,
            tested.ErrorCode ?? tested.TestRun?.FixedErrorCode);
        Assert.Equal(FeatureBuilderTestRunState.Passed, tested.TestRun!.State);
        var runs = await store.FeatureBuilder.ListTestRunsAsync(
            proposal.ProposalId,
            CancellationToken.None);
        var dryRun = Assert.Single(runs, run => run.Kind == FeatureBuilderTestRunKind.DryRun);
        Assert.Equal("workflow-semantic-v2", dryRun.EngineVersion);
        Assert.Equal(FeatureBuilderTestRunState.Passed, dryRun.State);
        using (var result = System.Text.Json.JsonDocument.Parse(dryRun.CanonicalResultJson))
        {
            Assert.True(result.RootElement.GetProperty("passed").GetBoolean());
            Assert.Equal(2, result.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(1, result.RootElement.GetProperty("caseCount").GetInt32());
            Assert.Equal(1, result.RootElement.GetProperty("matchedCount").GetInt32());
            var item = Assert.Single(result.RootElement.GetProperty("cases").EnumerateArray());
            Assert.Matches("^sha256:[0-9a-f]{64}$", item.GetProperty("caseHash").GetString());
            Assert.True(item.GetProperty("passed").GetBoolean());
        }
        Assert.DoesNotContain("metadata detail branch", dryRun.CanonicalResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("report.txt", dryRun.CanonicalResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\fixture", dryRun.CanonicalResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-08-02T00:00:00Z", dryRun.CanonicalResultJson, StringComparison.Ordinal);

        var applied = await generated.Service.ApplyProposalAsync(
            new ApplyFeatureBuilderProposalCommand(
                proposal.ProposalId,
                proposal.Revision,
                "draft_builder_semantic_workflow",
                Now.AddMinutes(2)),
            CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, applied.Outcome);
        Assert.Equal(CapabilityDraftState.ReadyForReview, applied.Draft!.State);
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("binding")]
    [InlineData("tool")]
    [InlineData("argument")]
    [InlineData("terminal")]
    [InlineData("output")]
    public async Task WorkflowSemanticV2FailsClosedOnSemanticMismatch(string mismatch)
    {
        using var store = CreateStore($"workflow-semantic-{mismatch}.db");
        await store.InitializeAsync();
        var generated = await GenerateAsync(
            store,
            SemanticWorkflowArtifacts(mismatch),
            'b',
            new WorkflowCatalog(),
            SemanticInventory());
        Assert.True(
            generated.Result.Proposal is not null,
            $"{generated.Result.Outcome}:{generated.Result.ErrorCode}");
        var proposal = Assert.IsType<FeatureBuilderProposal>(generated.Result.Proposal);
        Assert.True(
            generated.Result.Outcome == FeatureBuilderManagementOutcome.Succeeded,
            generated.Result.ErrorCode ?? proposal.FixedIssueCode);
        Assert.Equal(FeatureBuilderProposalState.Pending, proposal.State);
        var runner = new DesktopFeatureBuilderTestRunner(
            generated.Service,
            store.FeatureBuilder,
            generated.Scanner,
            new FixedTimeProvider(Now.AddMinutes(1)));

        var tested = await runner.RunRequiredTestsAsync(proposal, CancellationToken.None);

        Assert.True(
            tested.Outcome == FeatureBuilderManagementOutcome.Succeeded,
            tested.ErrorCode ?? tested.TestRun?.FixedErrorCode);
        Assert.Equal(FeatureBuilderTestRunState.Failed, tested.TestRun!.State);
        Assert.Equal("feature_builder.semantic.fixture_mismatch", tested.TestRun.FixedErrorCode);
        Assert.DoesNotContain("report.txt", tested.TestRun.CanonicalResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\fixture", tested.TestRun.CanonicalResultJson, StringComparison.Ordinal);
        var apply = await generated.Service.ApplyProposalAsync(
            new ApplyFeatureBuilderProposalCommand(
                proposal.ProposalId,
                proposal.Revision,
                $"draft_builder_semantic_{mismatch}",
                Now.AddMinutes(2)),
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.NotReady, apply.Outcome);
        Assert.Equal("feature_builder.proposal.tests_not_ready", apply.ErrorCode);
    }

    [Fact]
    public async Task WorkflowSemanticV2RejectsEffectOrPreparedToolsBeforeSimulation()
    {
        using var store = CreateStore("workflow-semantic-effect.db");
        await store.InitializeAsync();
        var generated = await GenerateAsync(
            store,
            EffectWorkflowSemanticArtifacts(),
            'c',
            new WorkflowCatalog(),
            new FeatureBuilderInventory(
                ["system.get_status.v1", "system.show_notification.v1"],
                [],
                []));
        Assert.True(
            generated.Result.Proposal is not null,
            $"{generated.Result.Outcome}:{generated.Result.ErrorCode}");
        var proposal = Assert.IsType<FeatureBuilderProposal>(generated.Result.Proposal);
        var runner = new DesktopFeatureBuilderTestRunner(
            generated.Service,
            store.FeatureBuilder,
            generated.Scanner,
            new FixedTimeProvider(Now.AddMinutes(1)));

        var tested = await runner.RunRequiredTestsAsync(proposal, CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, tested.Outcome);
        Assert.Equal(FeatureBuilderTestRunState.Failed, tested.TestRun!.State);
        Assert.Equal("feature_builder.semantic.effect_forbidden", tested.TestRun.FixedErrorCode);
        Assert.DoesNotContain("effect semantic canary", tested.TestRun.CanonicalResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("notification", tested.TestRun.CanonicalResultJson, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("media", '3')]
    [InlineData("oversize", '4')]
    [InlineData("secret", '5')]
    [InlineData("connection", '6')]
    [InlineData("shell", '7')]
    [InlineData("missing_tests", '8')]
    [InlineData("semantic_secret", '9')]
    public async Task UnsafeOrIncompleteArtifactsAreMetadataOnlyQuarantinedWithoutRawPersistence(
        string caseName,
        char idSeed)
    {
        var databasePath = DatabasePath($"quarantine-{caseName}.db");
        var canary = $"RAW_CANARY_{caseName}_d5f39a";
        FeatureBuilderProposal proposal;
        string fixedIssueCode;
        using (var store = new ConversationStore(databasePath))
        {
            await store.InitializeAsync();
            var generated = await GenerateAsync(
                store,
                QuarantineArtifacts(caseName, canary),
                idSeed);

            Assert.Equal(FeatureBuilderManagementOutcome.Quarantined, generated.Result.Outcome);
            proposal = Assert.IsType<FeatureBuilderProposal>(generated.Result.Proposal);
            Assert.Equal(FeatureBuilderProposalState.Quarantined, proposal.State);
            Assert.Null(proposal.Candidate);
            Assert.Null(proposal.CandidateContentHash);
            Assert.Null(proposal.TestEvidenceHash);
            Assert.Null(proposal.AppliedDraftId);
            Assert.NotNull(proposal.TerminalAtUtc);
            fixedIssueCode = Assert.IsType<string>(proposal.FixedIssueCode);
            Assert.StartsWith("feature_builder.proposal.", fixedIssueCode, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, fixedIssueCode, StringComparison.Ordinal);
            Assert.Equal(1, generated.Runtime.CallCount);

            await AssertMetadataOnlyRowAsync(store.DatabasePath, proposal.ProposalId, fixedIssueCode);
        }

        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            var count = File.Exists(path)
                ? CountOccurrences(await File.ReadAllBytesAsync(path), Encoding.UTF8.GetBytes(canary))
                : 0;
            Assert.Equal(0, count);
        }
    }

    [Fact]
    public void DesktopBuilderValidationCompositionHasNoWindowsHttpOrEffectAdapterDependency()
    {
        Assert.Equal(
            [
                typeof(IFeatureBuilderManagementService),
                typeof(IFeatureBuilderRepository),
                typeof(IFeatureBuilderProposalScanner),
                typeof(TimeProvider),
            ],
            ConstructorParameterTypes(typeof(DesktopFeatureBuilderTestRunner)));
        Assert.Equal(
            [typeof(ICapabilityDependencyCatalog), typeof(IWorkflowToolMetadataCatalog)],
            ConstructorParameterTypes(typeof(DesktopFeatureBuilderProposalScanner)));
        Assert.Equal(
            [typeof(IWorkflowToolMetadataCatalog)],
            ConstructorParameterTypes(typeof(FeatureBuilderWorkflowSemanticSimulator)));

        var runnerFieldTypes = typeof(DesktopFeatureBuilderTestRunner)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();
        Assert.All(
            runnerFieldTypes,
            type => Assert.Contains(
                type,
                new[]
                {
                    typeof(IFeatureBuilderManagementService),
                    typeof(IFeatureBuilderRepository),
                    typeof(IFeatureBuilderProposalScanner),
                    typeof(TimeProvider),
                }));
        Assert.DoesNotContain(
            runnerFieldTypes,
            type => type == typeof(HttpClient) ||
                    type.FullName?.Contains("WindowsToolHost", StringComparison.Ordinal) == true ||
                    type.FullName?.Contains("ExecutionResolver", StringComparison.Ordinal) == true ||
                    type.FullName?.Contains("ToolExecutor", StringComparison.Ordinal) == true);
        var semanticDependencyTypes = typeof(FeatureBuilderWorkflowSemanticSimulator)
            .GetNestedTypes(BindingFlags.NonPublic)
            .Append(typeof(FeatureBuilderWorkflowSemanticSimulator))
            .SelectMany(type =>
                type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .Select(field => field.FieldType)
                    .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic |
                                                 BindingFlags.Public)
                        .SelectMany(constructor => constructor.GetParameters())
                        .Select(parameter => parameter.ParameterType)))
            .ToArray();
        Assert.DoesNotContain(
            semanticDependencyTypes,
            type => type == typeof(HttpClient) ||
                    type.FullName?.Contains("WindowsToolHost", StringComparison.Ordinal) == true ||
                    type.FullName?.Contains("Credential", StringComparison.Ordinal) == true ||
                    type.FullName?.Contains("ExecutionResolver", StringComparison.Ordinal) == true ||
                    type.FullName?.Contains("Process", StringComparison.Ordinal) == true);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private ConversationStore CreateStore(string name) => new(DatabasePath(name));

    private string DatabasePath(string name) => Path.Combine(directory, name);

    private static async Task<GeneratedProposal> GenerateAsync(
        ConversationStore store,
        IReadOnlyList<FeatureBuilderRawProposalArtifact> artifacts,
        char idSeed,
        IWorkflowToolMetadataCatalog? workflowTools = null,
        FeatureBuilderInventory? inventory = null)
    {
        var scanner = new DesktopFeatureBuilderProposalScanner(new Dependencies(), workflowTools);
        var runtime = new DeterministicRuntime(artifacts);
        var service = new FeatureBuilderManagementService(store.FeatureBuilder, runtime, scanner);
        inventory ??= Inventory();
        var sessionId = $"fbs_{new string(idSeed, 32)}";
        var started = await service.StartSessionAsync(
            new StartFeatureBuilderSessionCommand(
                sessionId,
                "등록된 상태 조회 도구를 설명하는 기능을 만들어 줘.",
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
                $"fbgen_{new string(idSeed, 32)}",
                Answers: [],
                inventory,
                DeadlineUtc: Now.AddSeconds(30),
                NowUtc: Now),
            CancellationToken.None);
        return new GeneratedProposal(service, scanner, runtime, generated);
    }

    private static IReadOnlyList<FeatureBuilderRawProposalArtifact> CanonicalArtifacts(
        string? instructions = null,
        string? testsJson = null) =>
    [
        new(
            "manifest.json",
            "application/json",
            """
            {
              "schemaVersion": 1,
              "id": "company.builder.desktop-pipeline",
              "version": "1.0.0",
              "displayName": "상태 설명",
              "description": "등록된 읽기 전용 도구로 현재 상태를 설명합니다.",
              "kind": "prompt",
              "intentExamples": ["현재 상태를 설명해 줘"],
              "requiredTools": ["system.get_status.v1"],
              "requiredConnections": [],
              "maximumRisk": "R0",
              "entrypoint": "instructions.md"
            }
            """),
        new(
            "instructions.md",
            "text/markdown",
            instructions ?? "등록된 읽기 전용 도구만 사용해 현재 상태를 설명합니다."),
        new(
            "input.schema.json",
            "application/json",
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": {
                "text": { "type": "string", "minLength": 1, "maxLength": 64 }
              },
              "required": ["text"],
              "additionalProperties": false
            }
            """),
        new(
            "tests.json",
            "application/json",
            testsJson ??
            """
            [
              { "name": "accept bounded text", "input": { "text": "ready" }, "expected": "accepted" },
              { "name": "reject empty text", "input": { "text": "" }, "expected": "rejected" }
            ]
            """),
    ];

    private static IReadOnlyList<FeatureBuilderRawProposalArtifact> QuarantineArtifacts(
        string caseName,
        string canary)
    {
        var artifacts = CanonicalArtifacts(instructions: $"Generated note {canary}").ToList();
        switch (caseName)
        {
            case "media":
                artifacts[1] = artifacts[1] with { MediaType = "application/json" };
                break;
            case "oversize":
                artifacts[1] = artifacts[1] with
                {
                    Content = $"Generated note {canary} {new string('x', 128 * 1024)}",
                };
                break;
            case "secret":
                artifacts[1] = artifacts[1] with { Content = $"api_key={canary}" };
                break;
            case "connection":
                artifacts[1] = artifacts[1] with
                {
                    Content = $"endpoint=https://internal.invalid/{canary}",
                };
                break;
            case "shell":
                artifacts[1] = artifacts[1] with { Content = $"Run powershell.exe for {canary}" };
                break;
            case "missing_tests":
                artifacts.RemoveAt(3);
                break;
            case "semantic_secret":
                artifacts[3] = artifacts[3] with
                {
                    Content = $$"""
                        {
                          "schemaVersion": 2,
                          "cases": [
                            {
                              "name": "secret semantic input",
                              "input": { "api_key": "{{canary}}" },
                              "toolResults": [],
                              "expected": {
                                "status": "completed",
                                "output": {},
                                "visitedSteps": ["done"]
                              }
                            }
                          ]
                        }
                        """,
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName));
        }
        return artifacts.AsReadOnly();
    }

    private static FeatureBuilderInventory SemanticInventory() =>
        new(["file.get_metadata.v1"], [], []);

    private static IReadOnlyList<FeatureBuilderRawProposalArtifact> SemanticWorkflowArtifacts(
        string? mismatch = null)
    {
        const string primaryPath = "C:\\fixture\\report.txt";
        const string alternatePath = "C:\\fixture\\alternate.txt";
        const string wrongPath = "C:\\fixture\\wrong.txt";
        var bindingPath = mismatch == "binding" ? "/alternatePath" : "/path";
        var finalValue = mismatch == "output"
            ? """{ "metadata": { "$bind": { "source": "step", "stepId": "metadata", "path": "/path" } } }"""
            : """{ "metadata": { "$bind": { "source": "step", "stepId": "metadata", "path": "" } } }""";
        var workflow = $$"""
            {
              "schemaVersion": 1,
              "entryStepId": "metadata",
              "steps": [
                {
                  "id": "metadata",
                  "type": "tool.call",
                  "tool": "file.get_metadata.v1",
                  "arguments": {
                    "path": { "$bind": { "source": "input", "path": "{{bindingPath}}" } }
                  },
                  "next": "choose"
                },
                {
                  "id": "choose",
                  "type": "condition",
                  "left": { "$bind": { "source": "input", "path": "/includeDetail" } },
                  "operator": "isTrue",
                  "then": "detail",
                  "else": "summary"
                },
                {
                  "id": "detail",
                  "type": "input.map",
                  "value": "detail",
                  "saveAs": "detail_mode",
                  "next": "done"
                },
                {
                  "id": "summary",
                  "type": "input.map",
                  "value": "summary",
                  "saveAs": "summary_mode",
                  "next": "done"
                },
                {
                  "id": "done",
                  "type": "output",
                  "value": {{finalValue}}
                }
              ]
            }
            """;
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = primaryPath,
            ["name"] = "report.txt",
            ["isDirectory"] = false,
            ["sizeBytes"] = 42,
            ["lastWriteTimeUtc"] = "2026-08-02T00:00:00Z",
            ["readOnly"] = true,
        };
        var expectedMetadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["readOnly"] = true,
            ["lastWriteTimeUtc"] = "2026-08-02T00:00:00Z",
            ["sizeBytes"] = 42,
            ["isDirectory"] = false,
            ["name"] = "report.txt",
            ["path"] = primaryPath,
        };
        var completed = mismatch != "terminal";
        var tests = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = 2,
            ["cases"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "metadata detail branch",
                    ["input"] = new Dictionary<string, object?>
                    {
                        ["path"] = primaryPath,
                        ["alternatePath"] = alternatePath,
                        ["includeDetail"] = mismatch != "branch",
                    },
                    ["toolResults"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["stepId"] = "metadata",
                            ["tool"] = mismatch == "tool"
                                ? "system.get_status.v1"
                                : "file.get_metadata.v1",
                            ["arguments"] = new Dictionary<string, object?>
                            {
                                ["path"] = mismatch == "argument" ? wrongPath : primaryPath,
                            },
                            ["output"] = metadata,
                        },
                    },
                    ["expected"] = new Dictionary<string, object?>
                    {
                        ["status"] = completed ? "completed" : "failed",
                        ["output"] = completed
                            ? new Dictionary<string, object?> { ["metadata"] = expectedMetadata }
                            : null,
                        ["visitedSteps"] = new[] { "metadata", "choose", "detail", "done" },
                    },
                },
            },
        });
        return
        [
            new(
                "manifest.json",
                "application/json",
                """
                {
                  "schemaVersion": 1,
                  "id": "company.builder.metadata-semantic",
                  "version": "1.0.0",
                  "displayName": "파일 메타데이터 확인",
                  "description": "등록된 읽기 전용 도구로 파일 메타데이터를 확인합니다.",
                  "kind": "workflow",
                  "intentExamples": ["파일 메타데이터를 확인해 줘"],
                  "requiredTools": ["file.get_metadata.v1"],
                  "requiredConnections": [],
                  "maximumRisk": "R0",
                  "entrypoint": "workflow.json"
                }
                """),
            new(
                "instructions.md",
                "text/markdown",
                "지정된 파일의 메타데이터를 읽기 전용 도구로 확인합니다."),
            new(
                "input.schema.json",
                "application/json",
                """
                {
                  "$schema": "https://json-schema.org/draft/2020-12/schema",
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "minLength": 3, "maxLength": 4096 },
                    "alternatePath": { "type": "string", "minLength": 3, "maxLength": 4096 },
                    "includeDetail": { "type": "boolean" }
                  },
                  "required": ["path", "alternatePath", "includeDetail"],
                  "additionalProperties": false
                }
                """),
            new("workflow.json", "application/json", workflow),
            new("tests.json", "application/json", tests),
        ];
    }

    private static IReadOnlyList<FeatureBuilderRawProposalArtifact> EffectWorkflowSemanticArtifacts()
    {
        var artifacts = WorkflowArtifacts().ToList();
        artifacts[^1] = new FeatureBuilderRawProposalArtifact(
            "tests.json",
            "application/json",
            """
            {
              "schemaVersion": 2,
              "cases": [
                {
                  "name": "effect semantic canary",
                  "input": { "title": "업무 PC 상태" },
                  "toolResults": [],
                  "expected": {
                    "status": "completed",
                    "output": {},
                    "visitedSteps": ["status", "notify", "done"]
                  }
                }
              ]
            }
            """);
        return artifacts.AsReadOnly();
    }

    private static IReadOnlyList<FeatureBuilderRawProposalArtifact> WorkflowArtifacts() =>
    [
        new(
            "manifest.json",
            "application/json",
            """
            {
              "schemaVersion": 1,
              "id": "company.builder.status-notification",
              "version": "1.0.0",
              "displayName": "상태 확인 알림",
              "description": "등록된 상태 조회 도구의 실행 완료를 로컬 알림으로 알려 줍니다.",
              "kind": "workflow",
              "intentExamples": ["PC 상태를 확인하고 알림으로 알려 줘"],
              "requiredTools": ["system.get_status.v1", "system.show_notification.v1"],
              "requiredConnections": [],
              "maximumRisk": "R1",
              "entrypoint": "workflow.json"
            }
            """),
        new(
            "instructions.md",
            "text/markdown",
            "등록된 상태 조회 도구를 먼저 실행하고, 사용자가 제공한 제목으로 완료 알림을 표시합니다."),
        new(
            "input.schema.json",
            "application/json",
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": {
                "title": { "type": "string", "minLength": 1, "maxLength": 63 }
              },
              "required": ["title"],
              "additionalProperties": false
            }
            """),
        new(
            "workflow.json",
            "application/json",
            """
            {
              "schemaVersion": 1,
              "entryStepId": "status",
              "steps": [
                {
                  "id": "status",
                  "type": "tool.call",
                  "tool": "system.get_status.v1",
                  "arguments": {},
                  "next": "notify"
                },
                {
                  "id": "notify",
                  "type": "tool.call",
                  "tool": "system.show_notification.v1",
                  "arguments": {
                    "title": { "$bind": { "source": "input", "path": "/title" } },
                    "message": "시스템 상태 확인을 마쳤습니다.",
                    "reason": "사용자가 만든 상태 확인 알림 기능"
                  },
                  "next": "done"
                },
                {
                  "id": "done",
                  "type": "output",
                  "value": {
                    "status": { "$bind": { "source": "step", "stepId": "status", "path": "" } }
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
              { "name": "accept bounded title", "input": { "title": "업무 PC 상태" }, "expected": "accepted" },
              { "name": "reject empty title", "input": { "title": "" }, "expected": "rejected" }
            ]
            """),
    ];

    private static FeatureBuilderInventory Inventory() =>
        new(["system.get_status.v1"], [], []);

    private static async Task AssertMetadataOnlyRowAsync(
        string databasePath,
        string proposalId,
        string expectedIssueCode)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT candidate_content_hash, manifest_json, instructions_markdown,
                   input_schema_json, settings_schema_json, workflow_json, tests_json,
                   test_evidence_hash, applied_draft_id, fixed_issue_code
            FROM improvement_proposals
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", proposalId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < 9; index++) Assert.True(reader.IsDBNull(index));
        Assert.Equal(expectedIssueCode, reader.GetString(9));
        Assert.False(await reader.ReadAsync());
    }

    private static int CountOccurrences(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
    {
        var count = 0;
        while (source.IndexOf(value) is var index and >= 0)
        {
            count++;
            source = source[(index + value.Length)..];
        }
        return count;
    }

    private static Type[] ConstructorParameterTypes(Type type)
    {
        var constructor = Assert.Single(type.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        return constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
    }

    private sealed record GeneratedProposal(
        FeatureBuilderManagementService Service,
        DesktopFeatureBuilderProposalScanner Scanner,
        DeterministicRuntime Runtime,
        FeatureBuilderManagementResult Result);

    private sealed class DeterministicRuntime(
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
                Limitations: []));
        }
    }

    private sealed class QuestionThenWorkflowRuntime(
        IReadOnlyList<FeatureBuilderRawProposalArtifact> artifacts) : IFeatureBuilderRuntimeClient
    {
        private readonly List<FeatureBuilderRuntimeRequest> requests = [];

        internal int CallCount => requests.Count;
        internal IReadOnlyList<FeatureBuilderRuntimeRequest> Requests => requests.AsReadOnly();

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
                            "q_title",
                            "완료 알림에 표시할 제목은 무엇인가요?",
                            FeatureBuilderAnswerKind.Text,
                            []),
                        new FeatureBuilderQuestion(
                            "q_notice_style",
                            "알림 표현 방식을 선택해 주세요.",
                            FeatureBuilderAnswerKind.SingleChoice,
                            ["간단한 알림", "상세한 알림"]),
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
                    Artifacts: artifacts,
                    Limitations: ["알림 본문은 현재 고정된 안전 문구를 사용합니다."]));
        }
    }

    private sealed class Dependencies : ICapabilityDependencyCatalog
    {
        public bool TryGetTool(string name, out CapabilityToolDependency? dependency)
        {
            if (name is "system.get_status.v1" or "file.get_metadata.v1" or
                "system.show_notification.v1")
            {
                dependency = new CapabilityToolDependency(
                    name,
                    name == "system.show_notification.v1" ? "R1" : "R0");
                return true;
            }
            dependency = null;
            return false;
        }
    }

    private sealed class WorkflowCatalog : IWorkflowToolMetadataCatalog
    {
        public bool TryGet(string canonicalName, out WorkflowToolMetadata? metadata)
        {
            metadata = null;
            if (canonicalName is not ("system.get_status.v1" or "file.get_metadata.v1" or
                    "system.show_notification.v1") ||
                !ToolSchemaRegistry.All.TryGetValue(canonicalName, out var schemas))
                return false;
            var observation = canonicalName != "system.show_notification.v1";
            metadata = new WorkflowToolMetadata(
                canonicalName,
                observation ? "R0" : "R1",
                schemas.CanonicalInputSchemaJson,
                schemas.CanonicalResultSchemaJson,
                true,
                observation ? WorkflowToolPreparationMode.None : WorkflowToolPreparationMode.OneShot,
                true,
                observation ? WorkflowRetryClass.ReadOnly : WorkflowRetryClass.Never,
                observation ? WorkflowToolEffectClass.Observation : WorkflowToolEffectClass.Effect);
            return true;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }
}
