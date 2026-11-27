using LIGClaw.Application.Capabilities;
using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class FeatureBuilderManagementServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly string SessionId = "fbs_" + new string('a', 32);
    private static readonly FeatureBuilderInventory Inventory = new(
        ["system.get_status.v1"],
        ["http"],
        ["company.existing.status"]);

    [Fact]
    public async Task StartSessionComputesDesktopHashesAndKeepsCreateTargetEmpty()
    {
        var repository = new RecordingRepository();
        var service = Service(repository, _ => throw new InvalidOperationException());

        var result = await service.StartSessionAsync(Start(), CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, result.Outcome);
        Assert.Equal(FeatureBuilderMode.Create, result.Session!.Mode);
        Assert.Equal(FeatureBuilderSessionState.Collecting, result.Session.State);
        Assert.Null(result.Session.RevisionTarget);
        Assert.Equal(
            FeatureBuilderHashPolicy.ComputeRequirementHash(result.Session.Requirement),
            result.Session.RequirementHash);
        Assert.Equal(FeatureBuilderHashPolicy.ComputeInventoryHash(Inventory), result.Session.InventoryHash);
    }

    [Fact]
    public async Task ReviseSessionRequiresAnExactBaseAndCatalogRevision()
    {
        var repository = new RecordingRepository();
        var service = Service(repository, _ => throw new InvalidOperationException());
        var target = new FeatureBuilderRevisionTarget(
            VersionRef("company.builder.status", "1.0.0", 'a'),
            4);

        var invalid = await service.StartSessionAsync(
            Start() with { RevisionTarget = target, ExpectedCatalogRevision = 3 },
            CancellationToken.None);
        var valid = await service.StartSessionAsync(
            Start() with { RevisionTarget = target, ExpectedCatalogRevision = 4 },
            CancellationToken.None);

        Assert.Equal("feature_builder.session.command_invalid", invalid.ErrorCode);
        Assert.Equal(FeatureBuilderMode.Revise, valid.Session!.Mode);
        Assert.Equal(target, valid.Session.RevisionTarget);
    }

    [Fact]
    public async Task MissingInformationIsPersistedAndSecretLikeAnswerNeverReachesRuntime()
    {
        var repository = new RecordingRepository();
        var runtimeCalls = 0;
        var service = Service(repository, request =>
        {
            runtimeCalls++;
            return Echo(
                request,
                FeatureBuilderRuntimeOutcome.NeedsInformation,
                questions: [new FeatureBuilderQuestion(
                    "q_target",
                    "대상 폴더를 알려 주세요.",
                    FeatureBuilderAnswerKind.Text)]);
        });
        await service.StartSessionAsync(Start(), CancellationToken.None);

        var first = await service.GenerateAsync(Generate(0, 'b'), CancellationToken.None);
        var second = await service.GenerateAsync(
            Generate(first.Session!.Revision, 'c') with
            {
                Answers = [new FeatureBuilderAnswer(
                    "q_target",
                    "Authorization: Bearer abcdefghijklmnop")],
            },
            CancellationToken.None);

        Assert.Equal(FeatureBuilderSessionState.AwaitingInformation, first.Session.State);
        Assert.Equal("q_target", Assert.Single(first.Session.Questions).QuestionId);
        Assert.Equal("feature_builder.answer.secret_detected", second.ErrorCode);
        Assert.Equal(1, runtimeCalls);
    }

    [Fact]
    public async Task CancellingWhileAwaitingInformationClearsPendingQuestions()
    {
        var repository = new RecordingRepository();
        var service = Service(repository, request => Echo(
            request,
            FeatureBuilderRuntimeOutcome.NeedsInformation,
            questions: [new FeatureBuilderQuestion(
                "q_target",
                "대상 폴더를 알려 주세요.",
                FeatureBuilderAnswerKind.Text)]));
        await service.StartSessionAsync(Start(), CancellationToken.None);
        var awaiting = await service.GenerateAsync(Generate(0, 'b'), CancellationToken.None);

        var cancelled = await service.CancelSessionAsync(
            new(SessionId, awaiting.Session!.Revision, Now),
            CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, cancelled.Outcome);
        Assert.Equal(FeatureBuilderSessionState.Cancelled, cancelled.Session!.State);
        Assert.Empty(cancelled.Session.Questions);
    }

    [Fact]
    public async Task RepeatingAnAnsweredQuestionInterruptsTheGeneration()
    {
        var repository = new RecordingRepository();
        var runtimeCalls = 0;
        var service = Service(repository, request =>
        {
            runtimeCalls++;
            return Echo(
                request,
                FeatureBuilderRuntimeOutcome.NeedsInformation,
                questions: [new FeatureBuilderQuestion(
                    "q_target",
                    "대상 폴더를 알려 주세요.",
                    FeatureBuilderAnswerKind.Text)]);
        });
        await service.StartSessionAsync(Start(), CancellationToken.None);
        var awaiting = await service.GenerateAsync(Generate(0, 'b'), CancellationToken.None);

        var result = await service.GenerateAsync(
            Generate(awaiting.Session!.Revision, 'c') with
            {
                Answers = [new FeatureBuilderAnswer("q_target", "C:\\work")],
            },
            CancellationToken.None);

        Assert.Equal(2, runtimeCalls);
        Assert.Equal(FeatureBuilderManagementOutcome.Interrupted, result.Outcome);
        Assert.Equal(FeatureBuilderSessionState.Interrupted, result.Session!.State);
        Assert.Equal("feature_builder.runtime.response_invalid", result.ErrorCode);
        Assert.Empty(result.Session.Questions);
    }

    [Fact]
    public async Task FollowUpQuestionsThatExceedTheCumulativeAnswerLimitInterruptTheGeneration()
    {
        var repository = new RecordingRepository();
        var priorAnswers = Enumerable.Range(0, FeatureBuilderPolicy.MaximumAnswers - 2)
            .Select(index => new FeatureBuilderAnswer($"q_prior_{index}", $"answer-{index}"))
            .ToArray();
        repository.Sessions[SessionId] = Session(Inventory, revision: 0) with
        {
            State = FeatureBuilderSessionState.AwaitingInformation,
            Answers = priorAnswers,
            Questions = [new FeatureBuilderQuestion(
                "q_pending",
                "현재 대상은 무엇인가요?",
                FeatureBuilderAnswerKind.Text)],
            ActiveGenerationId = null,
        };
        var service = Service(repository, request => Echo(
            request,
            FeatureBuilderRuntimeOutcome.NeedsInformation,
            questions:
            [
                new FeatureBuilderQuestion(
                    "q_follow_up_one",
                    "첫 번째 추가 정보는 무엇인가요?",
                    FeatureBuilderAnswerKind.Text),
                new FeatureBuilderQuestion(
                    "q_follow_up_two",
                    "두 번째 추가 정보는 무엇인가요?",
                    FeatureBuilderAnswerKind.Text),
            ]));

        var result = await service.GenerateAsync(
            Generate(0, 'b') with
            {
                Answers = [new FeatureBuilderAnswer("q_pending", "target")],
            },
            CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Interrupted, result.Outcome);
        Assert.Equal(FeatureBuilderSessionState.Interrupted, result.Session!.State);
        Assert.Equal("feature_builder.runtime.response_invalid", result.ErrorCode);
        Assert.Equal(FeatureBuilderPolicy.MaximumAnswers - 1, result.Session.Answers.Count);
        Assert.Empty(result.Session.Questions);
    }

    [Fact]
    public async Task CorrelationMismatchInterruptsTheDurableGenerationWithoutCreatingProposal()
    {
        var repository = new RecordingRepository();
        var service = Service(repository, request => Echo(
            request,
            FeatureBuilderRuntimeOutcome.Proposal,
            artifacts: FeatureBuilderProposalScannerTests.Artifacts(),
            limitations: []) with
        {
            SessionRevision = request.SessionRevision + 1,
        });
        await service.StartSessionAsync(Start(), CancellationToken.None);

        var result = await service.GenerateAsync(Generate(0, 'b'), CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Interrupted, result.Outcome);
        Assert.Equal(FeatureBuilderSessionState.Interrupted, result.Session!.State);
        Assert.Equal("feature_builder.runtime.response_mismatch", result.ErrorCode);
        Assert.Empty(repository.Proposals);
    }

    [Fact]
    public async Task ValidProposalGetsOnlyDesktopIdentityHashParentAndPendingStatus()
    {
        var repository = new RecordingRepository();
        var target = new FeatureBuilderRevisionTarget(
            VersionRef("company.builder.status", "1.0.0", 'a'),
            4);
        var service = Service(repository, request => Echo(
            request,
            FeatureBuilderRuntimeOutcome.Proposal,
            artifacts: FeatureBuilderProposalScannerTests.Artifacts(version: "1.1.0"),
            limitations: ["사용자 승인 후에만 적용됩니다."]));
        await service.StartSessionAsync(
            Start() with { RevisionTarget = target, ExpectedCatalogRevision = 4 },
            CancellationToken.None);

        var result = await service.GenerateAsync(Generate(0, 'b'), CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, result.Outcome);
        Assert.Equal(FeatureBuilderSessionState.ProposalReady, result.Session!.State);
        Assert.Equal(FeatureBuilderProposalState.Pending, result.Proposal!.State);
        Assert.Matches("^proposal_[0-9a-f]{48}$", result.Proposal.ProposalId);
        Assert.Equal(target.BaseVersion, result.Proposal.BaseVersion);
        Assert.Equal(4, result.Proposal.ExpectedCatalogRevision);
        Assert.Equal(result.Proposal.CandidateContentHash, result.Proposal.Candidate!.Manifest.Version is not null
            ? CapabilityContentHasher.Compute(result.Proposal.Candidate)
            : null);
        Assert.Null(result.Proposal.TestEvidenceHash);
        Assert.Null(result.Proposal.AppliedDraftId);
    }

    [Fact]
    public async Task UnsafeRawProposalIsQuarantinedWithoutPersistingItsArtifactPayload()
    {
        var repository = new RecordingRepository();
        var unsafeArtifacts = FeatureBuilderProposalScannerTests.Artifacts(
            inputSchema: "{\"type\":\"object\",\"proposalId\":\"sidecar-owned\"}");
        var service = Service(repository, request => Echo(
            request,
            FeatureBuilderRuntimeOutcome.Proposal,
            artifacts: unsafeArtifacts,
            limitations: []));
        await service.StartSessionAsync(Start(), CancellationToken.None);

        var result = await service.GenerateAsync(Generate(0, 'b'), CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Quarantined, result.Outcome);
        Assert.Equal(FeatureBuilderProposalState.Quarantined, result.Proposal!.State);
        Assert.Equal("feature_builder.proposal.artifact_invalid", result.Proposal.FixedIssueCode);
        Assert.Null(result.Proposal.Candidate);
        Assert.Null(result.Proposal.CandidateContentHash);
        Assert.Equal(FeatureBuilderSessionState.Completed, result.Session!.State);
    }

    [Fact]
    public async Task RuntimeRefusalCompletesSessionWithOnlyAFixedCode()
    {
        var repository = new RecordingRepository();
        var service = Service(repository, request => Echo(
            request,
            FeatureBuilderRuntimeOutcome.Refused,
            limitations: ["임의 shell 실행은 지원하지 않습니다."],
            refusalCode: "capability.requirement_unsafe"));
        await service.StartSessionAsync(Start(), CancellationToken.None);

        var result = await service.GenerateAsync(Generate(0, 'b'), CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Refused, result.Outcome);
        Assert.Equal(FeatureBuilderSessionState.Completed, result.Session!.State);
        Assert.Equal("capability.requirement_unsafe", result.ErrorCode);
        Assert.Empty(repository.Proposals);
    }

    [Fact]
    public async Task RuntimeFailureProducesInterruptedStateAndNoRawExceptionDetail()
    {
        var repository = new RecordingRepository();
        var service = Service(repository, _ => throw new InvalidOperationException("known sensitive detail"));
        await service.StartSessionAsync(Start(), CancellationToken.None);

        var result = await service.GenerateAsync(Generate(0, 'b'), CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Interrupted, result.Outcome);
        Assert.Equal("feature_builder.runtime.failed", result.ErrorCode);
        Assert.DoesNotContain("sensitive", result.ErrorCode, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(repository.Proposals);
    }

    [Fact]
    public async Task ApplyRequiresLatestStaticAndDryRunPassesForTheExactCandidate()
    {
        var repository = new RecordingRepository
        {
            ActiveVersion = VersionRef("company.existing.status", "2.0.0", 'e'),
        };
        var service = Service(repository, request => Echo(
            request,
            FeatureBuilderRuntimeOutcome.Proposal,
            artifacts: FeatureBuilderProposalScannerTests.Artifacts(),
            limitations: []));
        await service.StartSessionAsync(Start(), CancellationToken.None);
        var generated = await service.GenerateAsync(Generate(0, 'b'), CancellationToken.None);
        var proposal = generated.Proposal!;

        var blocked = await service.ApplyProposalAsync(
            new(proposal.ProposalId, 0, "builder_ready_draft", Now),
            CancellationToken.None);
        await PassAsync(service, proposal, FeatureBuilderTestRunKind.StaticScan, '1');
        await PassAsync(service, proposal, FeatureBuilderTestRunKind.DryRun, '2');
        var applied = await service.ApplyProposalAsync(
            new(proposal.ProposalId, 0, "builder_ready_draft", Now),
            CancellationToken.None);

        Assert.Equal("feature_builder.proposal.tests_not_ready", blocked.ErrorCode);
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, applied.Outcome);
        Assert.Equal(CapabilityDraftState.ReadyForReview, applied.Draft!.State);
        Assert.Equal("builder", applied.Draft.SourceKind);
        Assert.Equal(proposal.ProposalHash, applied.Draft.SourcePackageHash);
        Assert.Equal(FeatureBuilderProposalState.Applied, applied.Proposal!.State);
        Assert.NotNull(applied.Proposal.TestEvidenceHash);
        Assert.Equal(1, repository.ApplyCalls);
        Assert.Equal(VersionRef("company.existing.status", "2.0.0", 'e'), repository.ActiveVersion);
    }

    [Fact]
    public async Task AFailedNewerTestAttemptMakesEarlierPassInsufficient()
    {
        var repository = new RecordingRepository();
        var service = Service(repository, request => Echo(
            request,
            FeatureBuilderRuntimeOutcome.Proposal,
            artifacts: FeatureBuilderProposalScannerTests.Artifacts(),
            limitations: []));
        await service.StartSessionAsync(Start(), CancellationToken.None);
        var proposal = (await service.GenerateAsync(Generate(0, 'b'), CancellationToken.None)).Proposal!;
        await PassAsync(service, proposal, FeatureBuilderTestRunKind.StaticScan, '1', attempt: 1);
        await PassAsync(service, proposal, FeatureBuilderTestRunKind.DryRun, '2', attempt: 1);
        var failedRunId = "fbtest_" + new string('3', 32);
        await service.StartTestRunAsync(
            new(failedRunId, proposal.ProposalId, FeatureBuilderTestRunKind.DryRun, 2, "engine_1", Now),
            CancellationToken.None);
        await service.CompleteTestRunAsync(
            new(failedRunId, 0, FeatureBuilderTestRunState.Failed, "{}", "feature_builder.test.failed", Now),
            CancellationToken.None);

        var result = await service.ApplyProposalAsync(
            new(proposal.ProposalId, 0, "builder_ready_draft", Now),
            CancellationToken.None);

        Assert.Equal("feature_builder.proposal.tests_not_ready", result.ErrorCode);
        Assert.Equal(0, repository.ApplyCalls);
    }

    [Fact]
    public async Task RejectCompletesSessionAndMakesProposalTerminal()
    {
        var repository = new RecordingRepository();
        var service = Service(repository, request => Echo(
            request,
            FeatureBuilderRuntimeOutcome.Proposal,
            artifacts: FeatureBuilderProposalScannerTests.Artifacts(),
            limitations: []));
        await service.StartSessionAsync(Start(), CancellationToken.None);
        var proposal = (await service.GenerateAsync(Generate(0, 'b'), CancellationToken.None)).Proposal!;

        var rejected = await service.SetProposalDispositionAsync(
            new(proposal.ProposalId, 0, FeatureBuilderProposalState.Rejected,
                "feature_builder.proposal.user_rejected", Now),
            CancellationToken.None);
        var retry = await service.SetProposalDispositionAsync(
            new(proposal.ProposalId, 1, FeatureBuilderProposalState.Stale,
                "feature_builder.proposal.stale", Now),
            CancellationToken.None);

        Assert.Equal(FeatureBuilderProposalState.Rejected, rejected.Proposal!.State);
        Assert.Equal(FeatureBuilderSessionState.Completed, rejected.Session!.State);
        Assert.Equal("feature_builder.proposal.terminal", retry.ErrorCode);
    }

    [Fact]
    public void RequestAndInventoryHashesAreDeterministicButBindSessionRevision()
    {
        var reverseInventory = new FeatureBuilderInventory(
            ["system.get_status.v1", "system.get_resource_status.v1"],
            ["sql", "http"],
            ["company.zed", "company.alpha"]);
        var sortedInventory = new FeatureBuilderInventory(
            ["system.get_resource_status.v1", "system.get_status.v1"],
            ["http", "sql"],
            ["company.alpha", "company.zed"]);
        var session = Session(reverseInventory, revision: 1);

        Assert.Equal(
            FeatureBuilderHashPolicy.ComputeInventoryHash(reverseInventory),
            FeatureBuilderHashPolicy.ComputeInventoryHash(sortedInventory));
        Assert.Equal(
            FeatureBuilderHashPolicy.ComputeRequestHash(
                "fbgen_" + new string('a', 32), session, Now.AddSeconds(30), reverseInventory),
            FeatureBuilderHashPolicy.ComputeRequestHash(
                "fbgen_" + new string('a', 32), session, Now.AddSeconds(30), sortedInventory));
        Assert.NotEqual(
            FeatureBuilderHashPolicy.ComputeRequestHash(
                "fbgen_" + new string('a', 32), session, Now.AddSeconds(30), reverseInventory),
            FeatureBuilderHashPolicy.ComputeRequestHash(
                "fbgen_" + new string('a', 32), session with { Revision = 2 }, Now.AddSeconds(30), reverseInventory));
    }

    private static async Task PassAsync(
        FeatureBuilderManagementService service,
        FeatureBuilderProposal proposal,
        FeatureBuilderTestRunKind kind,
        char id,
        int attempt = 1)
    {
        var testRunId = "fbtest_" + new string(id, 32);
        var started = await service.StartTestRunAsync(
            new(testRunId, proposal.ProposalId, kind, attempt, "engine_1", Now),
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, started.Outcome);
        var completed = await service.CompleteTestRunAsync(
            new(testRunId, 0, FeatureBuilderTestRunState.Passed, "{}", null, Now),
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, completed.Outcome);
    }

    private static FeatureBuilderManagementService Service(
        RecordingRepository repository,
        Func<FeatureBuilderRuntimeRequest, FeatureBuilderRuntimeResult> generate) =>
        new(
            repository,
            new Runtime(generate),
            new FeatureBuilderProposalScanner(new Dependencies()));

    private static StartFeatureBuilderSessionCommand Start() =>
        new(SessionId, "현재 상태를 설명하는 기능을 추가해 줘.", Inventory, 0, null, Now);

    private static GenerateFeatureBuilderProposalCommand Generate(long revision, char generation) =>
        new(
            SessionId,
            revision,
            "fbgen_" + new string(generation, 32),
            [],
            Inventory,
            Now.AddSeconds(30),
            Now);

    private static FeatureBuilderRuntimeResult Echo(
        FeatureBuilderRuntimeRequest request,
        FeatureBuilderRuntimeOutcome outcome,
        IReadOnlyList<FeatureBuilderQuestion>? questions = null,
        IReadOnlyList<FeatureBuilderRawProposalArtifact>? artifacts = null,
        IReadOnlyList<string>? limitations = null,
        string? refusalCode = null) =>
        new(
            1,
            request.GenerationId,
            request.RequestHash,
            request.SessionId,
            request.SessionRevision,
            request.InventoryHash,
            request.DeadlineUtc,
            outcome,
            questions,
            artifacts,
            limitations,
            refusalCode);

    private static FeatureBuilderSession Session(FeatureBuilderInventory inventory, long revision) =>
        new(
            SessionId,
            FeatureBuilderMode.Create,
            FeatureBuilderSessionState.Generating,
            revision,
            "requirement",
            FeatureBuilderHashPolicy.ComputeRequirementHash("requirement"),
            [],
            [],
            null,
            0,
            FeatureBuilderHashPolicy.ComputeInventoryHash(inventory),
            "fbgen_" + new string('a', 32),
            null,
            Now,
            Now);

    private static CapabilityVersionRef VersionRef(string id, string version, char hash) =>
        new(
            CapabilityId.Parse(id),
            CapabilitySemanticVersion.Parse(version),
            CapabilityContentHash.Parse("sha256:" + new string(hash, 64)));

    private sealed class Runtime(Func<FeatureBuilderRuntimeRequest, FeatureBuilderRuntimeResult> generate)
        : IFeatureBuilderRuntimeClient
    {
        public Task<FeatureBuilderRuntimeResult> GenerateAsync(
            FeatureBuilderRuntimeRequest request,
            CancellationToken cancellationToken) => Task.FromResult(generate(request));
    }

    private sealed class Dependencies : ICapabilityDependencyCatalog
    {
        public bool TryGetTool(string name, out CapabilityToolDependency? dependency)
        {
            if (name == "system.get_status.v1")
            {
                dependency = new CapabilityToolDependency(name, "R0");
                return true;
            }
            dependency = null;
            return false;
        }
    }

    private sealed class RecordingRepository : IFeatureBuilderRepository
    {
        internal Dictionary<string, FeatureBuilderSession> Sessions { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, FeatureBuilderProposal> Proposals { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, FeatureBuilderTestRun> TestRuns { get; } = new(StringComparer.Ordinal);
        internal int ApplyCalls { get; private set; }
        internal CapabilityVersionRef? ActiveVersion { get; set; }

        public Task<FeatureBuilderSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(Sessions.GetValueOrDefault(sessionId));

        public Task<FeatureBuilderProposal?> GetProposalAsync(string proposalId, CancellationToken cancellationToken) =>
            Task.FromResult(Proposals.GetValueOrDefault(proposalId));

        public Task<FeatureBuilderTestRun?> GetTestRunAsync(string testRunId, CancellationToken cancellationToken) =>
            Task.FromResult(TestRuns.GetValueOrDefault(testRunId));

        public Task<IReadOnlyList<FeatureBuilderTestRun>> ListTestRunsAsync(
            string proposalId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FeatureBuilderTestRun>>(
                TestRuns.Values.Where(run => run.ProposalId == proposalId).ToArray());

        public Task<FeatureBuilderSessionWriteResult> CreateSessionAsync(
            FeatureBuilderSession session,
            CancellationToken cancellationToken)
        {
            if (!Sessions.TryAdd(session.SessionId, session))
                return Task.FromResult(new FeatureBuilderSessionWriteResult(
                    FeatureBuilderManagementOutcome.Duplicate,
                    Sessions[session.SessionId],
                    "feature_builder.session.duplicate"));
            return Task.FromResult(new FeatureBuilderSessionWriteResult(
                FeatureBuilderManagementOutcome.Succeeded,
                session));
        }

        public Task<FeatureBuilderSessionWriteResult> ReplaceSessionAsync(
            long expectedRevision,
            FeatureBuilderSession replacement,
            CancellationToken cancellationToken)
        {
            if (!Sessions.TryGetValue(replacement.SessionId, out var current))
                return Task.FromResult(new FeatureBuilderSessionWriteResult(
                    FeatureBuilderManagementOutcome.NotFound,
                    ErrorCode: "feature_builder.session.not_found"));
            if (current.Revision != expectedRevision)
                return Task.FromResult(new FeatureBuilderSessionWriteResult(
                    FeatureBuilderManagementOutcome.Stale,
                    current,
                    "feature_builder.session.revision_changed"));
            Sessions[replacement.SessionId] = replacement;
            return Task.FromResult(new FeatureBuilderSessionWriteResult(
                FeatureBuilderManagementOutcome.Succeeded,
                replacement));
        }

        public Task<FeatureBuilderProposalWriteResult> CreateProposalAsync(
            FeatureBuilderProposal proposal,
            long expectedSessionRevision,
            FeatureBuilderSession replacementSession,
            CancellationToken cancellationToken)
        {
            if (Sessions[proposal.SessionId].Revision != expectedSessionRevision)
                return Task.FromResult(new FeatureBuilderProposalWriteResult(
                    FeatureBuilderManagementOutcome.Stale,
                    ErrorCode: "feature_builder.session.revision_changed"));
            if (!Proposals.TryAdd(proposal.ProposalId, proposal))
                return Task.FromResult(new FeatureBuilderProposalWriteResult(
                    FeatureBuilderManagementOutcome.Duplicate,
                    ErrorCode: "feature_builder.proposal.duplicate"));
            Sessions[proposal.SessionId] = replacementSession;
            return Task.FromResult(new FeatureBuilderProposalWriteResult(
                FeatureBuilderManagementOutcome.Succeeded,
                replacementSession,
                proposal));
        }

        public Task<FeatureBuilderProposalWriteResult> ReplaceProposalAndSessionAsync(
            long expectedProposalRevision,
            FeatureBuilderProposal replacementProposal,
            long expectedSessionRevision,
            FeatureBuilderSession replacementSession,
            CancellationToken cancellationToken)
        {
            var proposal = Proposals[replacementProposal.ProposalId];
            var session = Sessions[replacementSession.SessionId];
            if (proposal.Revision != expectedProposalRevision || session.Revision != expectedSessionRevision)
                return Task.FromResult(new FeatureBuilderProposalWriteResult(
                    FeatureBuilderManagementOutcome.Stale,
                    session,
                    proposal,
                    "feature_builder.proposal.revision_changed"));
            Proposals[replacementProposal.ProposalId] = replacementProposal;
            Sessions[replacementSession.SessionId] = replacementSession;
            return Task.FromResult(new FeatureBuilderProposalWriteResult(
                FeatureBuilderManagementOutcome.Succeeded,
                replacementSession,
                replacementProposal));
        }

        public Task<FeatureBuilderApplyWriteResult> ApplyProposalAsReadyDraftAsync(
            FeatureBuilderRepositoryApplyCommand command,
            CancellationToken cancellationToken)
        {
            ApplyCalls++;
            var proposal = Proposals[command.ProposalId];
            var session = Sessions[proposal.SessionId];
            var draft = new CapabilityDraft(
                command.Draft.DraftId,
                command.Draft.ExactVersion,
                command.Draft.Parent,
                command.Draft.Payload,
                CapabilityDraftState.ReadyForReview,
                0,
                command.Draft.SourceKind,
                command.Draft.SourcePackageHash,
                command.NowUtc,
                command.NowUtc);
            var appliedProposal = proposal with
            {
                State = FeatureBuilderProposalState.Applied,
                Revision = proposal.Revision + 1,
                TestEvidenceHash = command.TestEvidenceHash,
                AppliedDraftId = draft.DraftId,
                UpdatedAtUtc = command.NowUtc,
                TerminalAtUtc = command.NowUtc,
            };
            var completedSession = session with
            {
                State = FeatureBuilderSessionState.Completed,
                Revision = session.Revision + 1,
                UpdatedAtUtc = command.NowUtc,
            };
            Proposals[proposal.ProposalId] = appliedProposal;
            Sessions[session.SessionId] = completedSession;
            return Task.FromResult(new FeatureBuilderApplyWriteResult(
                FeatureBuilderManagementOutcome.Succeeded,
                completedSession,
                appliedProposal,
                draft));
        }

        public Task<FeatureBuilderTestRunWriteResult> CreateTestRunAsync(
            FeatureBuilderTestRun testRun,
            CancellationToken cancellationToken)
        {
            if (!TestRuns.TryAdd(testRun.TestRunId, testRun))
                return Task.FromResult(new FeatureBuilderTestRunWriteResult(
                    FeatureBuilderManagementOutcome.Duplicate,
                    ErrorCode: "feature_builder.test.duplicate"));
            return Task.FromResult(new FeatureBuilderTestRunWriteResult(
                FeatureBuilderManagementOutcome.Succeeded,
                testRun));
        }

        public Task<FeatureBuilderTestRunWriteResult> ReplaceTestRunAsync(
            long expectedRevision,
            FeatureBuilderTestRun replacement,
            CancellationToken cancellationToken)
        {
            var current = TestRuns[replacement.TestRunId];
            if (current.Revision != expectedRevision)
                return Task.FromResult(new FeatureBuilderTestRunWriteResult(
                    FeatureBuilderManagementOutcome.Stale,
                    current,
                    "feature_builder.test.revision_changed"));
            TestRuns[replacement.TestRunId] = replacement;
            return Task.FromResult(new FeatureBuilderTestRunWriteResult(
                FeatureBuilderManagementOutcome.Succeeded,
                replacement));
        }
    }
}
