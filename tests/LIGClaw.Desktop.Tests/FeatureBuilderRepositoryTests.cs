using LIGClaw.Application.Capabilities;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Tests;

public sealed class FeatureBuilderRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public FeatureBuilderRepositoryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task RejectsCreateAndReviseSessionsWithInconsistentCatalogSnapshots()
    {
        using var store = CreateStore("session-snapshot.db");
        await store.InitializeAsync();
        var inventoryHash = FeatureBuilderHashPolicy.ComputeInventoryHash(Inventory());
        var requirement = "새 워크플로를 만들어 줘.";
        var createWithRevision = new FeatureBuilderSession(
            SessionId('e'),
            FeatureBuilderMode.Create,
            FeatureBuilderSessionState.Collecting,
            revision: 0,
            requirement,
            FeatureBuilderHashPolicy.ComputeRequirementHash(requirement),
            answers: [],
            questions: [],
            revisionTarget: null,
            expectedCatalogRevision: 1,
            inventoryHash,
            activeGenerationId: null,
            fixedErrorCode: null,
            createdAtUtc: Timestamp(0),
            updatedAtUtc: Timestamp(0));
        var payload = WorkflowPayload();
        var baseVersion = new CapabilityVersionRef(
            payload.Manifest.Id,
            payload.Manifest.Version,
            CapabilityContentHasher.Compute(payload));
        var reviseWithoutRevision = createWithRevision with
        {
            SessionId = SessionId('f'),
            Mode = FeatureBuilderMode.Revise,
            RevisionTarget = new FeatureBuilderRevisionTarget(baseVersion, ExpectedCatalogRevision: 0),
            ExpectedCatalogRevision = 0,
        };

        Assert.Equal(FeatureBuilderManagementOutcome.Invalid, (
            await store.FeatureBuilder.CreateSessionAsync(
                createWithRevision,
                CancellationToken.None)).Outcome);
        Assert.Equal(FeatureBuilderManagementOutcome.Invalid, (
            await store.FeatureBuilder.CreateSessionAsync(
                reviseWithoutRevision,
                CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task RejectsSessionTransitionThatRewritesPreviouslyCollectedAnswers()
    {
        using var store = CreateStore("session-answer-mutation.db");
        await store.InitializeAsync();
        var generating = await CreateGeneratingSessionAsync(store, '1', Inventory());
        var interrupted = generating with
        {
            State = FeatureBuilderSessionState.Interrupted,
            Revision = generating.Revision + 1,
            Answers = [new FeatureBuilderAnswer("q_context", "새로 끼워 넣은 답변")],
            ActiveGenerationId = null,
            FixedErrorCode = "feature_builder.runtime.interrupted",
            UpdatedAtUtc = Timestamp(2),
        };

        var result = await store.FeatureBuilder.ReplaceSessionAsync(
            generating.Revision,
            interrupted,
            CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Stale, result.Outcome);
        var stored = Assert.IsType<FeatureBuilderSession>(
            await store.FeatureBuilder.GetSessionAsync(generating.SessionId, CancellationToken.None));
        Assert.Equal(FeatureBuilderSessionState.Generating, stored.State);
        Assert.Empty(stored.Answers);
    }

    [Fact]
    public async Task CreatesPendingProposalAndMovesGeneratingSessionAtomically()
    {
        using var store = CreateStore("pending.db");
        await store.InitializeAsync();

        var pending = await CreatePendingProposalAsync(store, '1');
        var storedSession = Assert.IsType<FeatureBuilderSession>(
            await store.FeatureBuilder.GetSessionAsync(pending.Session.SessionId, CancellationToken.None));
        var storedProposal = Assert.IsType<FeatureBuilderProposal>(
            await store.FeatureBuilder.GetProposalAsync(pending.Proposal.ProposalId, CancellationToken.None));

        Assert.Equal(FeatureBuilderSessionState.ProposalReady, storedSession.State);
        Assert.Equal(2, storedSession.Revision);
        Assert.Null(storedSession.ActiveGenerationId);
        Assert.Equal(FeatureBuilderProposalState.Pending, storedProposal.State);
        Assert.Equal(0, storedProposal.Revision);
        Assert.Equal(storedSession.SessionId, storedProposal.SessionId);
        Assert.Equal(1, storedProposal.SessionRevision);
        Assert.Equal(pending.Proposal.RequestHash, storedProposal.RequestHash);
        Assert.Equal(pending.Proposal.ResponseHash, storedProposal.ResponseHash);
        Assert.Equal(pending.Proposal.ProposalHash, storedProposal.ProposalHash);
        Assert.Equal(pending.Proposal.CandidateContentHash, storedProposal.CandidateContentHash);
        Assert.Equal(pending.Proposal.Candidate!.CanonicalWorkflowJson, storedProposal.Candidate!.CanonicalWorkflowJson);

        await using var connection = await OpenAsync(store.DatabasePath);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM improvement_proposals WHERE id = $id;",
            ("$id", pending.Proposal.ProposalId)));
        Assert.Equal("proposal_ready", await ScalarStringAsync(
            connection,
            "SELECT state FROM feature_builder_sessions WHERE id = $id;",
            ("$id", pending.Session.SessionId)));

        var duplicate = await store.FeatureBuilder.CreateProposalAsync(
            storedProposal,
            expectedSessionRevision: 1,
            storedSession,
            CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Duplicate, duplicate.Outcome);
        Assert.Equal("feature_builder.proposal.duplicate", duplicate.ErrorCode);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM improvement_proposals WHERE id = $id;",
            ("$id", pending.Proposal.ProposalId)));
    }

    [Fact]
    public async Task RejectsForgedProposalIdentityManifestAndTestKindAtTheRepositoryBoundary()
    {
        using var store = CreateStore("forged-boundary.db");
        await store.InitializeAsync();
        var pending = await CreatePendingProposalAsync(store, 'a');

        var forgedIdentity = pending.Proposal with
        {
            ProposalId = $"proposal_{new string('f', 48)}",
        };
        var identityResult = await store.FeatureBuilder.CreateProposalAsync(
            forgedIdentity,
            expectedSessionRevision: 1,
            pending.Session,
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Invalid, identityResult.Outcome);

        var candidate = pending.Proposal.Candidate!;
        var forgedManifest = pending.Proposal with
        {
            Candidate = candidate with
            {
                Manifest = candidate.Manifest with { DisplayName = "canonical JSON과 다른 이름" },
            },
        };
        var manifestResult = await store.FeatureBuilder.CreateProposalAsync(
            forgedManifest,
            expectedSessionRevision: 1,
            pending.Session,
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Invalid, manifestResult.Outcome);

        var invalidKind = RunningTestRun(
            pending,
            (FeatureBuilderTestRunKind)999,
            attempt: 1,
            'b',
            Timestamp(4));
        var testResult = await store.FeatureBuilder.CreateTestRunAsync(invalidKind, CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Invalid, testResult.Outcome);
    }

    [Fact]
    public async Task RejectsProposalThatDoesNotMatchTheGeneratingSessionSnapshotOrTerminalState()
    {
        using var store = CreateStore("proposal-correlation.db");
        await store.InitializeAsync();
        var inventory = Inventory();
        var generating = await CreateGeneratingSessionAsync(store, 'b', inventory);
        var pending = BuildPendingFixture(generating, inventory);

        var candidateWithUnknownManifestField = pending.Proposal.Candidate! with
        {
            CanonicalManifestJson = pending.Proposal.Candidate!.CanonicalManifestJson.Insert(
                pending.Proposal.Candidate.CanonicalManifestJson.Length - 1,
                ",\"unexpected\":true"),
        };
        var candidateWithUnknownManifestFieldHash = CapabilityContentHasher.Compute(candidateWithUnknownManifestField);
        var proposalWithUnknownManifestFieldHash = FeatureBuilderHashPolicy.ComputeProposalHash(
            pending.Proposal.GenerationId,
            pending.Proposal.RequestHash,
            pending.Proposal.ResponseHash,
            candidateWithUnknownManifestFieldHash,
            pending.Proposal.BaseVersion,
            pending.Proposal.ExpectedCatalogRevision);
        var proposalWithUnknownManifestField = pending.Proposal with
        {
            ProposalId = FeatureBuilderHashPolicy.CreateProposalId(proposalWithUnknownManifestFieldHash),
            Candidate = candidateWithUnknownManifestField,
            CandidateContentHash = candidateWithUnknownManifestFieldHash,
            ProposalHash = proposalWithUnknownManifestFieldHash,
        };
        var unknownManifestField = await store.FeatureBuilder.CreateProposalAsync(
            proposalWithUnknownManifestField,
            expectedSessionRevision: 1,
            pending.Session,
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Invalid, unknownManifestField.Outcome);

        var wrongInventory = pending.Proposal with
        {
            InventoryHash = FeatureBuilderHashPolicy.ComputeRequirementHash("different inventory"),
        };
        var mismatch = await store.FeatureBuilder.CreateProposalAsync(
            wrongInventory,
            expectedSessionRevision: 1,
            pending.Session,
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Stale, mismatch.Outcome);

        var wrongTerminalState = pending.Session with
        {
            State = FeatureBuilderSessionState.Completed,
        };
        var stateMismatch = await store.FeatureBuilder.CreateProposalAsync(
            pending.Proposal,
            expectedSessionRevision: 1,
            wrongTerminalState,
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Stale, stateMismatch.Outcome);
        Assert.Null(await store.FeatureBuilder.GetProposalAsync(
            pending.Proposal.ProposalId,
            CancellationToken.None));
        Assert.Equal(FeatureBuilderSessionState.Generating, (await store.FeatureBuilder.GetSessionAsync(
            generating.SessionId,
            CancellationToken.None))!.State);
    }

    [Fact]
    public async Task QuarantinedProposalRoundTripsWithoutGeneratedPayload()
    {
        using var store = CreateStore("quarantine.db");
        await store.InitializeAsync();
        var inventory = Inventory();
        var generating = await CreateGeneratingSessionAsync(store, '2', inventory);
        var deadline = Timestamp(10);
        var requestHash = FeatureBuilderHashPolicy.ComputeRequestHash(
            generating.ActiveGenerationId!,
            generating,
            deadline,
            inventory);
        var unsafeRuntimeResult = new FeatureBuilderRuntimeResult(
            1,
            generating.ActiveGenerationId!,
            requestHash,
            generating.SessionId,
            generating.Revision,
            generating.InventoryHash,
            deadline,
            FeatureBuilderRuntimeOutcome.Proposal,
            Artifacts:
            [
                new FeatureBuilderRawProposalArtifact(
                    "instructions.md",
                    "text/markdown",
                    "token=ordinary-secret-value"),
            ],
            Limitations: []);
        var responseHash = FeatureBuilderHashPolicy.ComputeResponseHash(unsafeRuntimeResult);
        var proposalHash = FeatureBuilderHashPolicy.ComputeProposalHash(
            generating.ActiveGenerationId!,
            requestHash,
            responseHash,
            candidateContentHash: null,
            baseVersion: null,
            expectedCatalogRevision: 0);
        var proposal = new FeatureBuilderProposal(
            FeatureBuilderHashPolicy.CreateProposalId(proposalHash),
            generating.SessionId,
            generating.Revision,
            generating.ActiveGenerationId!,
            requestHash,
            responseHash,
            generating.InventoryHash,
            baseVersion: null,
            expectedCatalogRevision: 0,
            candidateContentHash: null,
            proposalHash,
            candidate: null,
            knownLimitations: [],
            testEvidenceHash: null,
            FeatureBuilderProposalState.Quarantined,
            revision: 0,
            fixedIssueCode: "feature_builder.proposal.secret_literal_detected",
            appliedDraftId: null,
            createdAtUtc: Timestamp(2),
            updatedAtUtc: Timestamp(3),
            terminalAtUtc: Timestamp(3));
        var completed = generating with
        {
            State = FeatureBuilderSessionState.Completed,
            Revision = 2,
            ActiveGenerationId = null,
            FixedErrorCode = "feature_builder.proposal.secret_literal_detected",
            UpdatedAtUtc = Timestamp(3),
        };

        var created = await store.FeatureBuilder.CreateProposalAsync(
            proposal,
            expectedSessionRevision: 1,
            completed,
            CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, created.Outcome);
        var stored = Assert.IsType<FeatureBuilderProposal>(
            await store.FeatureBuilder.GetProposalAsync(proposal.ProposalId, CancellationToken.None));
        Assert.Equal(FeatureBuilderProposalState.Quarantined, stored.State);
        Assert.Equal(responseHash, stored.ResponseHash);
        Assert.Equal("feature_builder.proposal.secret_literal_detected", stored.FixedIssueCode);
        Assert.Null(stored.Candidate);
        Assert.Null(stored.CandidateContentHash);
        Assert.NotNull(stored.TerminalAtUtc);

        await using var connection = await OpenAsync(store.DatabasePath);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            """
            SELECT COUNT(*)
            FROM improvement_proposals
            WHERE id = $id
              AND candidate_content_hash IS NULL
              AND manifest_json IS NULL
              AND instructions_markdown IS NULL
              AND input_schema_json IS NULL
              AND settings_schema_json IS NULL
              AND workflow_json IS NULL
              AND tests_json IS NULL;
            """,
            ("$id", proposal.ProposalId)));
        Assert.Equal(0L, await ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM improvement_proposals
            WHERE id = $id
              AND (COALESCE(manifest_json, '') || COALESCE(instructions_markdown, '') ||
                   COALESCE(input_schema_json, '') || COALESCE(settings_schema_json, '') ||
                   COALESCE(workflow_json, '') || COALESCE(tests_json, '') ||
                   COALESCE(known_limitations_json, '')) LIKE '%ordinary-secret-value%';
            """,
            ("$id", proposal.ProposalId)));
    }

    [Fact]
    public async Task PassingEvidenceAppliesOnlyAReadyForReviewDraft()
    {
        using var store = CreateStore("apply.db");
        await store.InitializeAsync();
        var pending = await CreatePendingProposalAsync(store, '3');
        var evidence = await CreatePassingEvidenceAsync(store, pending, '3');

        var apply = await store.FeatureBuilder.ApplyProposalAsReadyDraftAsync(
            ApplyCommand(pending, evidence.Hash, "draft-builder-ready", Timestamp(9)),
            CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, apply.Outcome);
        Assert.Equal(FeatureBuilderSessionState.Completed, apply.Session!.State);
        Assert.Equal(3, apply.Session.Revision);
        Assert.Equal(FeatureBuilderProposalState.Applied, apply.Proposal!.State);
        Assert.Equal(1, apply.Proposal.Revision);
        Assert.Equal(evidence.Hash, apply.Proposal.TestEvidenceHash);
        Assert.Equal("draft-builder-ready", apply.Proposal.AppliedDraftId);
        Assert.Equal(CapabilityDraftState.ReadyForReview, apply.Draft!.State);
        Assert.Equal("builder", apply.Draft.SourceKind);
        Assert.Equal(pending.Proposal.ProposalHash, apply.Draft.SourcePackageHash);
        Assert.Equal(pending.Draft.ExactVersion, apply.Draft.ExactVersion);

        var runs = await store.FeatureBuilder.ListTestRunsAsync(
            pending.Proposal.ProposalId,
            CancellationToken.None);
        Assert.Equal(2, runs.Count);
        Assert.All(runs, run => Assert.Equal(FeatureBuilderTestRunState.Passed, run.State));
        Assert.Equal(FeatureBuilderHashPolicy.ComputeTestEvidenceHash(runs), evidence.Hash);
        Assert.Equal(
            [FeatureBuilderTestRunKind.StaticScan, FeatureBuilderTestRunKind.DryRun],
            runs.OrderBy(run => run.Kind).Select(run => run.Kind));

        var catalog = Assert.IsType<CapabilityCatalogEntry>(
            await store.Capabilities.GetCatalogAsync(pending.Draft.ExactVersion.Id, CancellationToken.None));
        Assert.Null(catalog.ActiveVersion);
        Assert.Equal(0, catalog.CatalogRevision);
        Assert.Null(await store.Capabilities.GetVersionAsync(
            pending.Draft.ExactVersion,
            CancellationToken.None));
        Assert.Empty(await store.Capabilities.ListActivationsAsync(
            pending.Draft.ExactVersion.Id,
            10,
            CancellationToken.None));
        Assert.Equal(CapabilityDraftState.ReadyForReview, (await store.Capabilities.GetDraftAsync(
            "draft-builder-ready",
            CancellationToken.None))!.State);

        await using var connection = await OpenAsync(store.DatabasePath);
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM capability_activations;"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM capability_versions;"));
    }

    [Fact]
    public async Task ApplyRejectsDraftWhoseManifestIdentityDiffersFromThePersistedCandidate()
    {
        using var store = CreateStore("apply-forged-manifest.db");
        await store.InitializeAsync();
        var pending = await CreatePendingProposalAsync(store, 'c');
        var evidence = await CreatePassingEvidenceAsync(store, pending, 'c');
        var candidate = pending.Draft.Payload;
        var forgedDraft = pending.Draft with
        {
            DraftId = "draft-forged-manifest",
            Payload = candidate with
            {
                Manifest = candidate.Manifest with
                {
                    Version = CapabilitySemanticVersion.Parse("2.0.0"),
                },
            },
        };

        var result = await store.FeatureBuilder.ApplyProposalAsReadyDraftAsync(
            ApplyCommand(pending, evidence.Hash, forgedDraft.DraftId, Timestamp(9)) with
            {
                Draft = forgedDraft,
            },
            CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Stale, result.Outcome);
        Assert.Equal("feature_builder.apply.compare_failed", result.ErrorCode);
        await AssertPendingApplyStateAsync(store, pending, forgedDraft.DraftId);
    }

    [Fact]
    public async Task CatalogDriftAtomicallyMarksProposalStaleAndCompletesSession()
    {
        using var store = CreateStore("apply-catalog-drift.db");
        await store.InitializeAsync();
        var pending = await CreatePendingProposalAsync(store, 'd');
        var evidence = await CreatePassingEvidenceAsync(store, pending, 'd');
        var imported = pending.Draft with
        {
            DraftId = "draft-imported-before-builder-apply",
            SourceKind = "local_import",
            SourcePackageHash = pending.Draft.ContentHash,
        };
        Assert.Equal(CapabilityMutationOutcome.Succeeded, (
            await store.Capabilities.CreateValidatedDraftAsync(
                imported,
                Timestamp(8),
                CancellationToken.None)).Outcome);
        var activated = await store.Capabilities.ApproveAndActivateAsync(
            new CapabilityApprovalCommand(
                imported.DraftId,
                imported.ExactVersion,
                ExpectedDraftRevision: 0,
                ExpectedCatalogRevision: 0,
                ExpectedActiveVersion: null,
                ReasonCode: "review.approved",
                NowUtc: Timestamp(9)),
            CancellationToken.None);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, activated.Outcome);

        var apply = await store.FeatureBuilder.ApplyProposalAsReadyDraftAsync(
            ApplyCommand(pending, evidence.Hash, "draft-builder-after-drift", Timestamp(10)),
            CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Stale, apply.Outcome);
        Assert.Equal("feature_builder.proposal.base_changed", apply.ErrorCode);
        Assert.Equal(FeatureBuilderProposalState.Stale, apply.Proposal!.State);
        Assert.Equal(1, apply.Proposal.Revision);
        Assert.Equal("feature_builder.proposal.base_changed", apply.Proposal.FixedIssueCode);
        Assert.Equal(Timestamp(10), apply.Proposal.TerminalAtUtc);
        Assert.Null(apply.Proposal.TestEvidenceHash);
        Assert.Null(apply.Proposal.AppliedDraftId);
        Assert.Equal(FeatureBuilderSessionState.Completed, apply.Session!.State);
        Assert.Equal(3, apply.Session.Revision);
        Assert.Equal("feature_builder.proposal.base_changed", apply.Session.FixedErrorCode);
        Assert.Null(await store.Capabilities.GetDraftAsync(
            "draft-builder-after-drift",
            CancellationToken.None));

        var storedProposal = Assert.IsType<FeatureBuilderProposal>(
            await store.FeatureBuilder.GetProposalAsync(pending.Proposal.ProposalId, CancellationToken.None));
        var storedSession = Assert.IsType<FeatureBuilderSession>(
            await store.FeatureBuilder.GetSessionAsync(pending.Session.SessionId, CancellationToken.None));
        Assert.Equal(apply.Proposal.State, storedProposal.State);
        Assert.Equal(apply.Proposal.Revision, storedProposal.Revision);
        Assert.Equal(apply.Proposal.FixedIssueCode, storedProposal.FixedIssueCode);
        Assert.Equal(apply.Proposal.TerminalAtUtc, storedProposal.TerminalAtUtc);
        Assert.Equal(apply.Session.State, storedSession.State);
        Assert.Equal(apply.Session.Revision, storedSession.Revision);
        Assert.Equal(apply.Session.FixedErrorCode, storedSession.FixedErrorCode);
        var catalog = Assert.IsType<CapabilityCatalogEntry>(
            await store.Capabilities.GetCatalogAsync(imported.ExactVersion.Id, CancellationToken.None));
        Assert.Equal(imported.ExactVersion, catalog.ActiveVersion);
        Assert.Equal(1, catalog.CatalogRevision);
    }

    [Fact]
    public async Task ExistingInactiveCatalogMakesParentlessCreateProposalPermanentlyStale()
    {
        using var store = CreateStore("apply-inactive-catalog.db");
        await store.InitializeAsync();
        var pending = await CreatePendingProposalAsync(store, 'e');
        var evidence = await CreatePassingEvidenceAsync(store, pending, 'e');
        var otherPayload = pending.Draft.Payload with
        {
            Manifest = pending.Draft.Payload.Manifest with
            {
                Version = CapabilitySemanticVersion.Parse("2.0.0"),
            },
            CanonicalManifestJson = pending.Draft.Payload.CanonicalManifestJson.Replace(
                "\"version\":\"1.0.0\"",
                "\"version\":\"2.0.0\"",
                StringComparison.Ordinal),
        };
        var otherHash = CapabilityContentHasher.Compute(otherPayload);
        var reservedDraft = new ValidatedCapabilityDraft(
            "draft-reserving-inactive-catalog",
            otherPayload,
            otherHash,
            Parent: null,
            SourceKind: "local_import",
            SourcePackageHash: otherHash);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, (
            await store.Capabilities.CreateValidatedDraftAsync(
                reservedDraft,
                Timestamp(8),
                CancellationToken.None)).Outcome);

        var apply = await store.FeatureBuilder.ApplyProposalAsReadyDraftAsync(
            ApplyCommand(pending, evidence.Hash, "draft-builder-second-root", Timestamp(9)),
            CancellationToken.None);

        Assert.Equal(FeatureBuilderManagementOutcome.Stale, apply.Outcome);
        Assert.Equal("feature_builder.proposal.base_changed", apply.ErrorCode);
        Assert.Equal(FeatureBuilderProposalState.Stale, apply.Proposal!.State);
        Assert.Equal(FeatureBuilderSessionState.Completed, apply.Session!.State);
        Assert.Null(await store.Capabilities.GetDraftAsync(
            "draft-builder-second-root",
            CancellationToken.None));
        Assert.NotNull(await store.Capabilities.GetDraftAsync(
            reservedDraft.DraftId,
            CancellationToken.None));
        var catalog = Assert.IsType<CapabilityCatalogEntry>(
            await store.Capabilities.GetCatalogAsync(reservedDraft.ExactVersion.Id, CancellationToken.None));
        Assert.Null(catalog.ActiveVersion);
        Assert.Equal(0, catalog.CatalogRevision);
    }

    [Fact]
    public async Task CompareEvidenceAndDuplicateFailuresRollBackApplyState()
    {
        using var store = CreateStore("apply-failures.db");
        await store.InitializeAsync();
        var pending = await CreatePendingProposalAsync(store, '4');
        var evidence = await CreatePassingEvidenceAsync(store, pending, '4');

        var stale = await store.FeatureBuilder.ApplyProposalAsReadyDraftAsync(
            ApplyCommand(pending, evidence.Hash, "draft-stale", Timestamp(9)) with
            {
                ExpectedProposalRevision = 1,
            },
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Stale, stale.Outcome);
        Assert.Equal("feature_builder.apply.compare_failed", stale.ErrorCode);
        await AssertPendingApplyStateAsync(store, pending, "draft-stale");

        var wrongEvidence = FeatureBuilderHashPolicy.ComputeRequirementHash("different test evidence");
        var mismatch = await store.FeatureBuilder.ApplyProposalAsReadyDraftAsync(
            ApplyCommand(pending, wrongEvidence, "draft-evidence", Timestamp(10)),
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.NotReady, mismatch.Outcome);
        Assert.Equal("feature_builder.tests.not_passed", mismatch.ErrorCode);
        await AssertPendingApplyStateAsync(store, pending, "draft-evidence");

        var unrelatedPayload = pending.Draft.Payload with
        {
            Manifest = pending.Draft.Payload.Manifest with
            {
                Id = CapabilityId.Parse("company.builder.unrelated-workflow"),
            },
            CanonicalManifestJson = pending.Draft.Payload.CanonicalManifestJson.Replace(
                "company.builder.status-workflow",
                "company.builder.unrelated-workflow",
                StringComparison.Ordinal),
        };
        var unrelatedHash = CapabilityContentHasher.Compute(unrelatedPayload);
        var localImport = new ValidatedCapabilityDraft(
            "draft-builder-duplicate",
            unrelatedPayload,
            unrelatedHash,
            Parent: null,
            SourceKind: "local_import",
            SourcePackageHash: unrelatedHash);
        var imported = await store.Capabilities.CreateValidatedDraftAsync(
            localImport,
            Timestamp(11),
            CancellationToken.None);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, imported.Outcome);

        var duplicate = await store.FeatureBuilder.ApplyProposalAsReadyDraftAsync(
            ApplyCommand(pending, evidence.Hash, "draft-builder-duplicate", Timestamp(12)),
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Duplicate, duplicate.Outcome);
        Assert.Equal("feature_builder.draft.duplicate", duplicate.ErrorCode);
        Assert.Equal("draft-builder-duplicate", duplicate.Draft!.DraftId);
        await AssertPendingApplyStateAsync(store, pending, "draft-not-created-after-duplicate");

        Assert.Null(await store.Capabilities.GetCatalogAsync(
            pending.Draft.ExactVersion.Id,
            CancellationToken.None));
        Assert.Empty(await store.Capabilities.ListActivationsAsync(
            pending.Draft.ExactVersion.Id,
            10,
            CancellationToken.None));
    }

    [Fact]
    public async Task TerminalRecordsAndProposalPayloadAreImmutable()
    {
        using var store = CreateStore("immutability.db");
        await store.InitializeAsync();
        var pending = await CreatePendingProposalAsync(store, '5');
        var evidence = await CreatePassingEvidenceAsync(store, pending, '5');

        await using var connection = await OpenAsync(store.DatabasePath);
        var payloadUpdate = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "UPDATE improvement_proposals SET instructions_markdown = 'changed' WHERE id = $id;",
            ("$id", pending.Proposal.ProposalId)));
        Assert.Contains("payload is immutable", payloadUpdate.Message, StringComparison.Ordinal);

        var applied = await store.FeatureBuilder.ApplyProposalAsReadyDraftAsync(
            ApplyCommand(pending, evidence.Hash, "draft-immutable", Timestamp(9)),
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, applied.Outcome);

        var terminalProposalUpdate = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            """
            UPDATE improvement_proposals
            SET status = 'rejected', applied_draft_id = NULL,
                fixed_issue_code = 'feature_builder.proposal.rejected', revision = revision + 1
            WHERE id = $id;
            """,
            ("$id", pending.Proposal.ProposalId)));
        Assert.Contains("terminal capability proposals are immutable", terminalProposalUpdate.Message, StringComparison.Ordinal);

        var terminalTestUpdate = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "UPDATE capability_test_runs SET revision = revision + 1 WHERE id = $id;",
            ("$id", evidence.StaticScan.TestRunId)));
        Assert.Contains("terminal capability test runs are immutable", terminalTestUpdate.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "DELETE FROM improvement_proposals WHERE id = $id;",
            ("$id", pending.Proposal.ProposalId)));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "DELETE FROM capability_test_runs WHERE id = $id;",
            ("$id", evidence.DryRun.TestRunId)));
    }

    [Fact]
    public async Task DesktopStartupInterruptsGeneratingSessionsAndRunningTestsOnce()
    {
        var path = DatabasePath("recovery.db");
        var inventory = Inventory();
        FeatureBuilderSession generating;
        PendingFixture pending;
        FeatureBuilderTestRun running;
        using (var store = new ConversationStore(path))
        {
            await store.InitializeAsync();
            generating = await CreateGeneratingSessionAsync(store, '6', inventory);
            pending = await CreatePendingProposalAsync(store, '7');
            running = RunningTestRun(
                pending,
                FeatureBuilderTestRunKind.StaticScan,
                attempt: 1,
                '7',
                Timestamp(6));
            var created = await store.FeatureBuilder.CreateTestRunAsync(running, CancellationToken.None);
            Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, created.Outcome);
        }

        var recoveredAt = Timestamp(20);
        using (var reopened = new ConversationStore(path, new FixedTimeProvider(recoveredAt)))
        {
            await reopened.InitializeForDesktopStartupAsync();
            var recoveredSession = Assert.IsType<FeatureBuilderSession>(
                await reopened.FeatureBuilder.GetSessionAsync(generating.SessionId, CancellationToken.None));
            var recoveredRun = Assert.IsType<FeatureBuilderTestRun>(
                await reopened.FeatureBuilder.GetTestRunAsync(running.TestRunId, CancellationToken.None));

            Assert.Equal(FeatureBuilderSessionState.Interrupted, recoveredSession.State);
            Assert.Equal(2, recoveredSession.Revision);
            Assert.Null(recoveredSession.ActiveGenerationId);
            Assert.Equal("feature_builder.runtime.interrupted", recoveredSession.FixedErrorCode);
            Assert.Equal(recoveredAt, recoveredSession.UpdatedAtUtc);
            Assert.Equal(FeatureBuilderTestRunState.Interrupted, recoveredRun.State);
            Assert.Equal(1, recoveredRun.Revision);
            Assert.Equal("feature_builder.test.interrupted", recoveredRun.FixedErrorCode);
            Assert.Equal("{\"code\":\"feature_builder.test.interrupted\"}", recoveredRun.CanonicalResultJson);
            Assert.Equal(recoveredAt, recoveredRun.CompletedAtUtc);

            await reopened.InitializeForDesktopStartupAsync();
            Assert.Equal(2, (await reopened.FeatureBuilder.GetSessionAsync(
                generating.SessionId,
                CancellationToken.None))!.Revision);
            Assert.Equal(1, (await reopened.FeatureBuilder.GetTestRunAsync(
                running.TestRunId,
                CancellationToken.None))!.Revision);
            Assert.Equal(FeatureBuilderSessionState.ProposalReady, (await reopened.FeatureBuilder.GetSessionAsync(
                pending.Session.SessionId,
                CancellationToken.None))!.State);
            Assert.Equal(FeatureBuilderProposalState.Pending, (await reopened.FeatureBuilder.GetProposalAsync(
                pending.Proposal.ProposalId,
                CancellationToken.None))!.State);
        }

        using var restartedAgain = new ConversationStore(path, new FixedTimeProvider(Timestamp(30)));
        await restartedAgain.InitializeForDesktopStartupAsync();
        Assert.Equal(2, (await restartedAgain.FeatureBuilder.GetSessionAsync(
            generating.SessionId,
            CancellationToken.None))!.Revision);
        Assert.Equal(1, (await restartedAgain.FeatureBuilder.GetTestRunAsync(
            running.TestRunId,
            CancellationToken.None))!.Revision);
    }

    private ConversationStore CreateStore(string name) => new(DatabasePath(name));

    private string DatabasePath(string name) => Path.Combine(_directory, name);

    private static FeatureBuilderInventory Inventory() =>
        new(["system.get_status.v1"], [], []);

    private static async Task<FeatureBuilderSession> CreateGeneratingSessionAsync(
        ConversationStore store,
        char idSeed,
        FeatureBuilderInventory inventory)
    {
        var createdAt = Timestamp(0);
        var session = new FeatureBuilderSession(
            SessionId(idSeed),
            FeatureBuilderMode.Create,
            FeatureBuilderSessionState.Collecting,
            revision: 0,
            requirement: "현재 시스템 상태를 확인하고 정해진 결과로 끝나는 워크플로를 만들어 줘.",
            requirementHash: FeatureBuilderHashPolicy.ComputeRequirementHash(
                "현재 시스템 상태를 확인하고 정해진 결과로 끝나는 워크플로를 만들어 줘."),
            answers: [],
            questions: [],
            revisionTarget: null,
            expectedCatalogRevision: 0,
            inventoryHash: FeatureBuilderHashPolicy.ComputeInventoryHash(inventory),
            activeGenerationId: null,
            fixedErrorCode: null,
            createdAtUtc: createdAt,
            updatedAtUtc: createdAt);
        var create = await store.FeatureBuilder.CreateSessionAsync(session, CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, create.Outcome);

        var generating = session with
        {
            State = FeatureBuilderSessionState.Generating,
            Revision = 1,
            ActiveGenerationId = GenerationId(idSeed),
            UpdatedAtUtc = Timestamp(1),
        };
        var replace = await store.FeatureBuilder.ReplaceSessionAsync(
            expectedRevision: 0,
            generating,
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, replace.Outcome);
        return generating;
    }

    private static async Task<PendingFixture> CreatePendingProposalAsync(
        ConversationStore store,
        char idSeed)
    {
        var inventory = Inventory();
        var generating = await CreateGeneratingSessionAsync(store, idSeed, inventory);
        var pending = BuildPendingFixture(generating, inventory);
        var created = await store.FeatureBuilder.CreateProposalAsync(
            pending.Proposal,
            expectedSessionRevision: 1,
            pending.Session,
            CancellationToken.None);
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, created.Outcome);
        return pending;
    }

    private static PendingFixture BuildPendingFixture(
        FeatureBuilderSession generating,
        FeatureBuilderInventory inventory)
    {
        var payload = WorkflowPayload();
        var candidateHash = CapabilityContentHasher.Compute(payload);
        var deadline = Timestamp(10);
        var requestHash = FeatureBuilderHashPolicy.ComputeRequestHash(
            generating.ActiveGenerationId!,
            generating,
            deadline,
            inventory);
        var runtimeResult = new FeatureBuilderRuntimeResult(
            1,
            generating.ActiveGenerationId!,
            requestHash,
            generating.SessionId,
            generating.Revision,
            generating.InventoryHash,
            deadline,
            FeatureBuilderRuntimeOutcome.Proposal,
            Artifacts: Artifacts(payload),
            Limitations: []);
        var responseHash = FeatureBuilderHashPolicy.ComputeResponseHash(runtimeResult);
        var proposalHash = FeatureBuilderHashPolicy.ComputeProposalHash(
            generating.ActiveGenerationId!,
            requestHash,
            responseHash,
            candidateHash,
            baseVersion: null,
            expectedCatalogRevision: 0);
        var proposal = new FeatureBuilderProposal(
            FeatureBuilderHashPolicy.CreateProposalId(proposalHash),
            generating.SessionId,
            generating.Revision,
            generating.ActiveGenerationId!,
            requestHash,
            responseHash,
            generating.InventoryHash,
            baseVersion: null,
            expectedCatalogRevision: 0,
            candidateHash,
            proposalHash,
            payload,
            knownLimitations: [],
            testEvidenceHash: null,
            FeatureBuilderProposalState.Pending,
            revision: 0,
            fixedIssueCode: null,
            appliedDraftId: null,
            createdAtUtc: Timestamp(2),
            updatedAtUtc: Timestamp(2),
            terminalAtUtc: null);
        var proposalReady = generating with
        {
            State = FeatureBuilderSessionState.ProposalReady,
            Revision = 2,
            ActiveGenerationId = null,
            UpdatedAtUtc = Timestamp(3),
        };
        var draft = new ValidatedCapabilityDraft(
            $"draft-builder-{generating.SessionId[4]}",
            payload,
            candidateHash,
            Parent: null,
            SourceKind: "builder",
            SourcePackageHash: proposalHash);
        return new PendingFixture(proposalReady, proposal, draft);
    }

    private static async Task<PassingEvidence> CreatePassingEvidenceAsync(
        ConversationStore store,
        PendingFixture pending,
        char idSeed)
    {
        var staticRun = RunningTestRun(
            pending,
            FeatureBuilderTestRunKind.StaticScan,
            attempt: 1,
            idSeed,
            Timestamp(4));
        var dryRun = RunningTestRun(
            pending,
            FeatureBuilderTestRunKind.DryRun,
            attempt: 1,
            NextHex(idSeed),
            Timestamp(5));
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, (
            await store.FeatureBuilder.CreateTestRunAsync(staticRun, CancellationToken.None)).Outcome);
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, (
            await store.FeatureBuilder.CreateTestRunAsync(dryRun, CancellationToken.None)).Outcome);

        var passedStatic = staticRun with
        {
            State = FeatureBuilderTestRunState.Passed,
            Revision = 1,
            CanonicalResultJson = "{\"issues\":[],\"passed\":true}",
            CompletedAtUtc = Timestamp(6),
        };
        var passedDry = dryRun with
        {
            State = FeatureBuilderTestRunState.Passed,
            Revision = 1,
            CanonicalResultJson = "{\"cases\":1,\"passed\":true}",
            CompletedAtUtc = Timestamp(7),
        };
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, (
            await store.FeatureBuilder.ReplaceTestRunAsync(0, passedStatic, CancellationToken.None)).Outcome);
        Assert.Equal(FeatureBuilderManagementOutcome.Succeeded, (
            await store.FeatureBuilder.ReplaceTestRunAsync(0, passedDry, CancellationToken.None)).Outcome);

        var persisted = await store.FeatureBuilder.ListTestRunsAsync(
            pending.Proposal.ProposalId,
            CancellationToken.None);
        var evidenceHash = FeatureBuilderHashPolicy.ComputeTestEvidenceHash(persisted);
        return new PassingEvidence(passedStatic, passedDry, evidenceHash);
    }

    private static FeatureBuilderTestRun RunningTestRun(
        PendingFixture pending,
        FeatureBuilderTestRunKind kind,
        int attempt,
        char idSeed,
        DateTimeOffset startedAtUtc) =>
        new(
            TestRunId(idSeed),
            pending.Proposal.ProposalId,
            kind,
            attempt,
            pending.Draft.ContentHash,
            kind == FeatureBuilderTestRunKind.StaticScan ? "d5-static-v1" : "d5-dry-v1",
            FeatureBuilderTestRunState.Running,
            Revision: 0,
            CanonicalResultJson: "{}",
            FixedErrorCode: null,
            startedAtUtc,
            CompletedAtUtc: null);

    private static FeatureBuilderRepositoryApplyCommand ApplyCommand(
        PendingFixture pending,
        CapabilityContentHash evidenceHash,
        string draftId,
        DateTimeOffset nowUtc) =>
        new(
            pending.Proposal.ProposalId,
            ExpectedProposalRevision: 0,
            ExpectedSessionRevision: 2,
            pending.Draft with { DraftId = draftId },
            evidenceHash,
            nowUtc);

    private static async Task AssertPendingApplyStateAsync(
        ConversationStore store,
        PendingFixture pending,
        string absentDraftId)
    {
        var proposal = Assert.IsType<FeatureBuilderProposal>(
            await store.FeatureBuilder.GetProposalAsync(pending.Proposal.ProposalId, CancellationToken.None));
        var session = Assert.IsType<FeatureBuilderSession>(
            await store.FeatureBuilder.GetSessionAsync(pending.Session.SessionId, CancellationToken.None));
        Assert.Equal(FeatureBuilderProposalState.Pending, proposal.State);
        Assert.Equal(0, proposal.Revision);
        Assert.Null(proposal.TestEvidenceHash);
        Assert.Null(proposal.AppliedDraftId);
        Assert.Equal(FeatureBuilderSessionState.ProposalReady, session.State);
        Assert.Equal(2, session.Revision);
        Assert.Null(await store.Capabilities.GetDraftAsync(absentDraftId, CancellationToken.None));
    }

    private static CapabilityVersionPayload WorkflowPayload()
    {
        var id = CapabilityId.Parse("company.builder.status-workflow");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        var manifest = new CapabilityManifest(
            1,
            id,
            version,
            "상태 확인 워크플로",
            "등록된 읽기 전용 도구로 현재 시스템 상태를 확인합니다.",
            CapabilityKind.Workflow,
            ["현재 시스템 상태를 확인해 줘"],
            ["system.get_status.v1"],
            [],
            "R0",
            "workflow.json");
        return new CapabilityVersionPayload(
            manifest,
            "{\"description\":\"등록된 읽기 전용 도구로 현재 시스템 상태를 확인합니다.\",\"displayName\":\"상태 확인 워크플로\",\"entrypoint\":\"workflow.json\",\"id\":\"company.builder.status-workflow\",\"intentExamples\":[\"현재 시스템 상태를 확인해 줘\"],\"kind\":\"workflow\",\"maximumRisk\":\"R0\",\"requiredConnections\":[],\"requiredTools\":[\"system.get_status.v1\"],\"schemaVersion\":1,\"version\":\"1.0.0\"}",
            "등록된 읽기 전용 도구만 사용해 상태를 확인합니다.",
            "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
            CanonicalSettingsSchemaJson: null,
            CanonicalWorkflowJson: "{\"entryStepId\":\"status\",\"schemaVersion\":1,\"steps\":[{\"arguments\":{},\"id\":\"status\",\"next\":\"done\",\"tool\":\"system.get_status.v1\",\"type\":\"tool.call\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{}}]}",
            CanonicalTestsJson: "[{\"expected\":\"accepted\",\"input\":{},\"name\":\"status succeeds\"}]");
    }

    private static IReadOnlyList<FeatureBuilderRawProposalArtifact> Artifacts(CapabilityVersionPayload payload) =>
    [
        new("manifest.json", "application/json", payload.CanonicalManifestJson),
        new("instructions.md", "text/markdown", payload.Instructions),
        new("input.schema.json", "application/json", payload.CanonicalInputSchemaJson),
        new("workflow.json", "application/json", payload.CanonicalWorkflowJson!),
        new("tests.json", "application/json", payload.CanonicalTestsJson),
    ];

    private static string SessionId(char seed) => $"fbs_{new string(seed, 32)}";

    private static string GenerationId(char seed) => $"fbgen_{new string(seed, 32)}";

    private static string TestRunId(char seed) => $"fbtest_{new string(seed, 32)}";

    private static char NextHex(char value) => value switch
    {
        >= '0' and < '9' => (char)(value + 1),
        '9' => 'a',
        >= 'a' and < 'f' => (char)(value + 1),
        _ => '0',
    };

    private static DateTimeOffset Timestamp(int minute) =>
        DateTimeOffset.Parse("2026-08-02T00:00:00Z").AddMinutes(minute);

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private sealed record PendingFixture(
        FeatureBuilderSession Session,
        FeatureBuilderProposal Proposal,
        ValidatedCapabilityDraft Draft);

    private sealed record PassingEvidence(
        FeatureBuilderTestRun StaticScan,
        FeatureBuilderTestRun DryRun,
        CapabilityContentHash Hash);

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup for SQLite handles retained by a failed test.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup for SQLite handles retained by a failed test.
        }
    }
}
