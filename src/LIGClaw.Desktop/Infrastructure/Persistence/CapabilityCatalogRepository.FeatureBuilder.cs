using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed partial class CapabilityCatalogRepository
{
    private static readonly JsonDocumentOptions StrictFeatureBuilderJsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    private const string FeatureBuilderSessionSelect =
        """
        SELECT id, mode, state, revision, requirement, requirement_hash,
               answers_json, questions_json, base_capability_id, base_version,
               base_content_hash, expected_catalog_revision, inventory_hash,
               active_generation_id, fixed_error_code, created_at_utc, updated_at_utc
        FROM feature_builder_sessions
        """;

    private const string FeatureBuilderProposalSelect =
        """
        SELECT id, session_id, session_revision, generation_id, request_hash,
               response_hash, inventory_hash, base_capability_id, base_version,
               base_content_hash, expected_catalog_revision, candidate_content_hash,
               proposal_hash, manifest_json, instructions_markdown, input_schema_json,
               settings_schema_json, workflow_json, tests_json, known_limitations_json,
               test_evidence_hash, status, revision, fixed_issue_code, applied_draft_id,
               created_at_utc, updated_at_utc, terminal_at_utc
        FROM improvement_proposals
        """;

    private const string FeatureBuilderTestRunSelect =
        """
        SELECT id, proposal_id, kind, attempt, candidate_content_hash, engine_version,
               status, revision, result_json, fixed_error_code, started_at_utc, completed_at_utc
        FROM capability_test_runs
        """;

    public async Task<FeatureBuilderSession?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        FeatureBuilderSession? result = null;
        await WithConnectionAsync(async connection =>
        {
            result = await ReadFeatureBuilderSessionAsync(
                connection, null, sessionId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<FeatureBuilderProposal?> GetProposalAsync(
        string proposalId,
        CancellationToken cancellationToken)
    {
        ValidateProposalId(proposalId);
        FeatureBuilderProposal? result = null;
        await WithConnectionAsync(async connection =>
        {
            result = await ReadFeatureBuilderProposalAsync(
                connection, null, proposalId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<FeatureBuilderTestRun?> GetTestRunAsync(
        string testRunId,
        CancellationToken cancellationToken)
    {
        ValidateTestRunId(testRunId);
        FeatureBuilderTestRun? result = null;
        await WithConnectionAsync(async connection =>
        {
            result = await ReadFeatureBuilderTestRunAsync(
                connection, null, testRunId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<FeatureBuilderTestRun>> ListTestRunsAsync(
        string proposalId,
        CancellationToken cancellationToken)
    {
        ValidateProposalId(proposalId);
        var results = new List<FeatureBuilderTestRun>();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"{FeatureBuilderTestRunSelect} WHERE proposal_id = $proposalId ORDER BY kind, attempt, id;";
            command.Parameters.AddWithValue("$proposalId", proposalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                results.Add(ReadFeatureBuilderTestRun(reader));
        }, cancellationToken).ConfigureAwait(false);
        return results.AsReadOnly();
    }

    public async Task<FeatureBuilderSessionWriteResult> CreateSessionAsync(
        FeatureBuilderSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!IsValidNewSession(session))
            return new(FeatureBuilderManagementOutcome.Invalid, ErrorCode: "feature_builder.session.invalid");

        FeatureBuilderSessionWriteResult result = new(
            FeatureBuilderManagementOutcome.Invalid,
            ErrorCode: "feature_builder.session.not_created");
        try
        {
            await WithConnectionAsync(async connection =>
            {
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var existing = await ReadFeatureBuilderSessionAsync(
                    connection, transaction, session.SessionId, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    result = Equivalent(existing, session)
                        ? new(FeatureBuilderManagementOutcome.Duplicate, existing, "feature_builder.session.duplicate")
                        : new(FeatureBuilderManagementOutcome.Invalid, ErrorCode: "feature_builder.session.id_conflict");
                    return;
                }
                await InsertFeatureBuilderSessionAsync(
                    connection, transaction, session, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                result = new(FeatureBuilderManagementOutcome.Succeeded, session);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return new(FeatureBuilderManagementOutcome.Stale, ErrorCode: "feature_builder.session.constraint_failed");
        }
        return result;
    }

    public async Task<FeatureBuilderSessionWriteResult> ReplaceSessionAsync(
        long expectedRevision,
        FeatureBuilderSession replacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (expectedRevision < 0 || replacement.Revision != expectedRevision + 1 ||
            !IsValidSession(replacement))
            return new(FeatureBuilderManagementOutcome.Invalid, ErrorCode: "feature_builder.session.invalid");

        FeatureBuilderSessionWriteResult result = new(
            FeatureBuilderManagementOutcome.Invalid,
            ErrorCode: "feature_builder.session.not_updated");
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var current = await ReadFeatureBuilderSessionAsync(
                connection, transaction, replacement.SessionId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                result = new(FeatureBuilderManagementOutcome.NotFound, ErrorCode: "feature_builder.session.not_found");
                return;
            }
            if (current.Revision != expectedRevision || !IsValidSessionTransition(current, replacement))
            {
                result = new(FeatureBuilderManagementOutcome.Stale, current, "feature_builder.session.compare_failed");
                return;
            }
            if (await UpdateFeatureBuilderSessionAsync(
                    connection, transaction, expectedRevision, replacement, cancellationToken).ConfigureAwait(false) != 1)
            {
                result = new(FeatureBuilderManagementOutcome.Stale, current, "feature_builder.session.compare_failed");
                return;
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            result = new(FeatureBuilderManagementOutcome.Succeeded, replacement);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<FeatureBuilderProposalWriteResult> CreateProposalAsync(
        FeatureBuilderProposal proposal,
        long expectedSessionRevision,
        FeatureBuilderSession replacementSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(replacementSession);
        if (!IsValidNewProposal(proposal) || expectedSessionRevision < 0 ||
            replacementSession.Revision != expectedSessionRevision + 1 ||
            !IsValidSession(replacementSession))
            return new(FeatureBuilderManagementOutcome.Invalid, ErrorCode: "feature_builder.proposal.invalid");

        FeatureBuilderProposalWriteResult result = new(
            FeatureBuilderManagementOutcome.Invalid,
            ErrorCode: "feature_builder.proposal.not_created");
        try
        {
            await WithConnectionAsync(async connection =>
            {
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var existing = await ReadFeatureBuilderProposalAsync(
                    connection, transaction, proposal.ProposalId, cancellationToken).ConfigureAwait(false);
                var sameGeneration = await ReadFeatureBuilderProposalByGenerationAsync(
                    connection, transaction, proposal.GenerationId, cancellationToken).ConfigureAwait(false);
                if (existing is not null || sameGeneration is not null)
                {
                    var duplicate = existing ?? sameGeneration!;
                    result = Equivalent(duplicate, proposal)
                        ? new(FeatureBuilderManagementOutcome.Duplicate, replacementSession, duplicate,
                            "feature_builder.proposal.duplicate")
                        : new(FeatureBuilderManagementOutcome.Invalid, ErrorCode: "feature_builder.proposal.identity_conflict");
                    return;
                }
                var currentSession = await ReadFeatureBuilderSessionAsync(
                    connection, transaction, proposal.SessionId, cancellationToken).ConfigureAwait(false);
                if (currentSession is null)
                {
                    result = new(FeatureBuilderManagementOutcome.NotFound, ErrorCode: "feature_builder.session.not_found");
                    return;
                }
                if (currentSession.Revision != expectedSessionRevision ||
                    currentSession.State != FeatureBuilderSessionState.Generating ||
                    !StringComparer.Ordinal.Equals(currentSession.ActiveGenerationId, proposal.GenerationId) ||
                    !IsValidSessionTransition(currentSession, replacementSession) ||
                    proposal.SessionRevision != currentSession.Revision ||
                    !proposal.InventoryHash.Equals(currentSession.InventoryHash) ||
                    !Equals(proposal.BaseVersion, currentSession.RevisionTarget?.BaseVersion) ||
                    proposal.ExpectedCatalogRevision != currentSession.ExpectedCatalogRevision ||
                    !StringComparer.Ordinal.Equals(proposal.FixedIssueCode, replacementSession.FixedErrorCode) ||
                    proposal.State == FeatureBuilderProposalState.Pending &&
                    replacementSession.State != FeatureBuilderSessionState.ProposalReady ||
                    proposal.State == FeatureBuilderProposalState.Quarantined &&
                    replacementSession.State != FeatureBuilderSessionState.Completed)
                {
                    result = new(FeatureBuilderManagementOutcome.Stale, currentSession, ErrorCode: "feature_builder.session.compare_failed");
                    return;
                }
                await InsertFeatureBuilderProposalAsync(
                    connection, transaction, proposal, cancellationToken).ConfigureAwait(false);
                if (await UpdateFeatureBuilderSessionAsync(
                        connection, transaction, expectedSessionRevision, replacementSession, cancellationToken)
                    .ConfigureAwait(false) != 1)
                {
                    result = new(FeatureBuilderManagementOutcome.Stale, currentSession, ErrorCode: "feature_builder.session.compare_failed");
                    return;
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                result = new(FeatureBuilderManagementOutcome.Succeeded, replacementSession, proposal);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return new(FeatureBuilderManagementOutcome.Stale, ErrorCode: "feature_builder.proposal.constraint_failed");
        }
        return result;
    }

    public async Task<FeatureBuilderProposalWriteResult> ReplaceProposalAndSessionAsync(
        long expectedProposalRevision,
        FeatureBuilderProposal replacementProposal,
        long expectedSessionRevision,
        FeatureBuilderSession replacementSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacementProposal);
        ArgumentNullException.ThrowIfNull(replacementSession);
        if (expectedProposalRevision < 0 || expectedSessionRevision < 0 ||
            replacementProposal.Revision != expectedProposalRevision + 1 ||
            replacementSession.Revision != expectedSessionRevision + 1 ||
            !IsValidProposal(replacementProposal) || !IsValidSession(replacementSession))
            return new(FeatureBuilderManagementOutcome.Invalid, ErrorCode: "feature_builder.proposal.invalid");

        FeatureBuilderProposalWriteResult result = new(
            FeatureBuilderManagementOutcome.Invalid,
            ErrorCode: "feature_builder.proposal.not_updated");
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var currentProposal = await ReadFeatureBuilderProposalAsync(
                connection, transaction, replacementProposal.ProposalId, cancellationToken).ConfigureAwait(false);
            if (currentProposal is null)
            {
                result = new(FeatureBuilderManagementOutcome.NotFound, ErrorCode: "feature_builder.proposal.not_found");
                return;
            }
            var currentSession = await ReadFeatureBuilderSessionAsync(
                connection, transaction, replacementSession.SessionId, cancellationToken).ConfigureAwait(false);
            if (currentSession is null)
            {
                result = new(FeatureBuilderManagementOutcome.NotFound, ErrorCode: "feature_builder.session.not_found");
                return;
            }
            if (currentProposal.Revision != expectedProposalRevision ||
                currentSession.Revision != expectedSessionRevision ||
                !SameProposalPayload(currentProposal, replacementProposal) ||
                !IsValidSessionTransition(currentSession, replacementSession) ||
                !FeatureBuilderPolicy.CanTransition(currentProposal.State, replacementProposal.State) ||
                replacementProposal.SessionId != replacementSession.SessionId ||
                !StringComparer.Ordinal.Equals(
                    replacementProposal.FixedIssueCode,
                    replacementSession.FixedErrorCode) ||
                replacementProposal.State is not (FeatureBuilderProposalState.Rejected or
                    FeatureBuilderProposalState.Stale) ||
                replacementSession.State != FeatureBuilderSessionState.Completed)
            {
                result = new(FeatureBuilderManagementOutcome.Stale, currentSession, currentProposal,
                    "feature_builder.proposal.compare_failed");
                return;
            }
            if (await UpdateFeatureBuilderProposalAsync(
                    connection, transaction, expectedProposalRevision, replacementProposal, cancellationToken)
                .ConfigureAwait(false) != 1 ||
                await UpdateFeatureBuilderSessionAsync(
                    connection, transaction, expectedSessionRevision, replacementSession, cancellationToken)
                .ConfigureAwait(false) != 1)
            {
                result = new(FeatureBuilderManagementOutcome.Stale, currentSession, currentProposal,
                    "feature_builder.proposal.compare_failed");
                return;
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            result = new(FeatureBuilderManagementOutcome.Succeeded, replacementSession, replacementProposal);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<FeatureBuilderApplyWriteResult> ApplyProposalAsReadyDraftAsync(
        FeatureBuilderRepositoryApplyCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ExpectedProposalRevision < 0 || command.ExpectedSessionRevision < 0 ||
            command.NowUtc.Offset != TimeSpan.Zero || command.Draft.SourceKind != "builder" ||
            !IsSafeDraftId(command.Draft.DraftId))
            return new(FeatureBuilderManagementOutcome.Invalid, ErrorCode: "feature_builder.apply.command_invalid");

        FeatureBuilderApplyWriteResult result = new(
            FeatureBuilderManagementOutcome.Invalid,
            ErrorCode: "feature_builder.apply.not_applied");
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var proposal = await ReadFeatureBuilderProposalAsync(
                connection, transaction, command.ProposalId, cancellationToken).ConfigureAwait(false);
            if (proposal is null)
            {
                result = new(FeatureBuilderManagementOutcome.NotFound, ErrorCode: "feature_builder.proposal.not_found");
                return;
            }
            var session = await ReadFeatureBuilderSessionAsync(
                connection, transaction, proposal.SessionId, cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                result = new(FeatureBuilderManagementOutcome.NotFound, Proposal: proposal,
                    ErrorCode: "feature_builder.session.not_found");
                return;
            }
            if (proposal.Revision != command.ExpectedProposalRevision ||
                session.Revision != command.ExpectedSessionRevision ||
                proposal.State != FeatureBuilderProposalState.Pending ||
                session.State != FeatureBuilderSessionState.ProposalReady ||
                proposal.Candidate is null || proposal.CandidateContentHash is null ||
                proposal.TestEvidenceHash is not null ||
                command.NowUtc < proposal.UpdatedAtUtc || command.NowUtc < session.UpdatedAtUtc ||
                !proposal.CandidateContentHash.Equals(command.Draft.ContentHash) ||
                !EquivalentPayload(proposal.Candidate, command.Draft.Payload) ||
                !command.Draft.ExactVersion.Id.Equals(proposal.Candidate.Manifest.Id) ||
                !command.Draft.ExactVersion.Version.Equals(proposal.Candidate.Manifest.Version) ||
                !Equals(proposal.BaseVersion, command.Draft.Parent) ||
                !proposal.ProposalHash.Equals(command.Draft.SourcePackageHash))
            {
                result = new(FeatureBuilderManagementOutcome.Stale, session, proposal,
                    ErrorCode: "feature_builder.apply.compare_failed");
                return;
            }
            var evidence = await ReadPassingBuilderEvidenceAsync(
                connection, transaction, proposal, cancellationToken).ConfigureAwait(false);
            if (evidence is null ||
                !FeatureBuilderHashPolicy.ComputeTestEvidenceHash(evidence).Equals(command.TestEvidenceHash))
            {
                result = new(FeatureBuilderManagementOutcome.NotReady, session, proposal,
                    ErrorCode: "feature_builder.tests.not_passed");
                return;
            }

            var exactVersion = command.Draft.ExactVersion;
            var catalog = await ReadCatalogAsync(
                connection, transaction, exactVersion.Id, cancellationToken).ConfigureAwait(false);
            if (catalog is null)
            {
                if (proposal.BaseVersion is not null || proposal.ExpectedCatalogRevision != 0)
                {
                    result = await MarkProposalStaleAsync(
                        connection,
                        transaction,
                        proposal,
                        session,
                        "feature_builder.proposal.base_changed",
                        command.NowUtc,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
                await InsertCatalogAsync(
                    connection, transaction, proposal.Candidate.Manifest, command.NowUtc, cancellationToken)
                    .ConfigureAwait(false);
                catalog = new CapabilityCatalogEntry(exactVersion.Id, null, 0, command.NowUtc, command.NowUtc);
            }
            else if (proposal.BaseVersion is null)
            {
                result = await MarkProposalStaleAsync(
                    connection,
                    transaction,
                    proposal,
                    session,
                    "feature_builder.proposal.base_changed",
                    command.NowUtc,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            if (catalog.CatalogRevision != proposal.ExpectedCatalogRevision ||
                !Equals(catalog.ActiveVersion, proposal.BaseVersion))
            {
                result = await MarkProposalStaleAsync(
                    connection,
                    transaction,
                    proposal,
                    session,
                    "feature_builder.proposal.base_changed",
                    command.NowUtc,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var existingDraft = await ReadDraftAsync(
                connection, transaction, command.Draft.DraftId, cancellationToken).ConfigureAwait(false);
            var exactDraft = await ReadDraftByExactVersionAsync(
                connection, transaction, exactVersion, cancellationToken).ConfigureAwait(false);
            if (existingDraft is not null || exactDraft is not null)
            {
                result = new(FeatureBuilderManagementOutcome.Duplicate, session, proposal,
                    existingDraft?.Draft ?? exactDraft!.Draft, "feature_builder.draft.duplicate");
                return;
            }
            var identityVersion = await ReadVersionByIdentityAsync(
                connection, transaction, exactVersion.Id, exactVersion.Version, cancellationToken).ConfigureAwait(false);
            if (identityVersion is not null)
            {
                result = await MarkProposalStaleAsync(
                    connection,
                    transaction,
                    proposal,
                    session,
                    "feature_builder.version.conflict",
                    command.NowUtc,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var readyDraft = new CapabilityDraft(
                command.Draft.DraftId,
                exactVersion,
                proposal.BaseVersion,
                proposal.Candidate,
                CapabilityDraftState.ReadyForReview,
                0,
                "builder",
                proposal.ProposalHash,
                command.NowUtc,
                command.NowUtc);
            await InsertDraftAsync(
                connection, transaction, readyDraft, proposal.ExpectedCatalogRevision, cancellationToken)
                .ConfigureAwait(false);
            await UpdateCatalogProposalMetadataAsync(
                connection, transaction, proposal.Candidate.Manifest, command.NowUtc, cancellationToken)
                .ConfigureAwait(false);

            var appliedProposal = proposal with
            {
                State = FeatureBuilderProposalState.Applied,
                Revision = proposal.Revision + 1,
                TestEvidenceHash = command.TestEvidenceHash,
                AppliedDraftId = readyDraft.DraftId,
                UpdatedAtUtc = command.NowUtc,
                TerminalAtUtc = command.NowUtc,
            };
            var completedSession = session with
            {
                State = FeatureBuilderSessionState.Completed,
                Revision = session.Revision + 1,
                Questions = [],
                ActiveGenerationId = null,
                FixedErrorCode = null,
                UpdatedAtUtc = command.NowUtc,
            };
            if (await UpdateFeatureBuilderProposalAsync(
                    connection, transaction, command.ExpectedProposalRevision, appliedProposal, cancellationToken)
                .ConfigureAwait(false) != 1 ||
                await UpdateFeatureBuilderSessionAsync(
                    connection, transaction, command.ExpectedSessionRevision, completedSession, cancellationToken)
                .ConfigureAwait(false) != 1)
            {
                result = new(FeatureBuilderManagementOutcome.Stale, session, proposal,
                    ErrorCode: "feature_builder.apply.compare_failed");
                return;
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            result = new(FeatureBuilderManagementOutcome.Succeeded, completedSession, appliedProposal, readyDraft);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<FeatureBuilderApplyWriteResult> MarkProposalStaleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FeatureBuilderProposal proposal,
        FeatureBuilderSession session,
        string errorCode,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var staleProposal = proposal with
        {
            State = FeatureBuilderProposalState.Stale,
            Revision = proposal.Revision + 1,
            FixedIssueCode = errorCode,
            UpdatedAtUtc = nowUtc,
            TerminalAtUtc = nowUtc,
        };
        var completedSession = session with
        {
            State = FeatureBuilderSessionState.Completed,
            Revision = session.Revision + 1,
            Questions = [],
            ActiveGenerationId = null,
            FixedErrorCode = errorCode,
            UpdatedAtUtc = nowUtc,
        };
        if (!IsValidProposal(staleProposal) || !IsValidSession(completedSession))
            return new(FeatureBuilderManagementOutcome.Invalid, session, proposal,
                ErrorCode: "feature_builder.apply.stale_transition_invalid");
        if (await UpdateFeatureBuilderProposalAsync(
                connection, transaction, proposal.Revision, staleProposal, cancellationToken)
            .ConfigureAwait(false) != 1 ||
            await UpdateFeatureBuilderSessionAsync(
                connection, transaction, session.Revision, completedSession, cancellationToken)
            .ConfigureAwait(false) != 1)
            return new(FeatureBuilderManagementOutcome.Stale, session, proposal,
                ErrorCode: "feature_builder.apply.compare_failed");

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(FeatureBuilderManagementOutcome.Stale, completedSession, staleProposal, ErrorCode: errorCode);
    }

    public async Task<FeatureBuilderTestRunWriteResult> CreateTestRunAsync(
        FeatureBuilderTestRun testRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(testRun);
        if (!IsValidNewTestRun(testRun))
            return new(FeatureBuilderManagementOutcome.Invalid, ErrorCode: "feature_builder.test.invalid");
        FeatureBuilderTestRunWriteResult result = new(
            FeatureBuilderManagementOutcome.Invalid,
            ErrorCode: "feature_builder.test.not_created");
        try
        {
            await WithConnectionAsync(async connection =>
            {
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var proposal = await ReadFeatureBuilderProposalAsync(
                    connection, transaction, testRun.ProposalId, cancellationToken).ConfigureAwait(false);
                if (proposal is null)
                {
                    result = new(FeatureBuilderManagementOutcome.NotFound, ErrorCode: "feature_builder.proposal.not_found");
                    return;
                }
                if (proposal.State != FeatureBuilderProposalState.Pending || proposal.CandidateContentHash is null ||
                    !proposal.CandidateContentHash.Equals(testRun.CandidateContentHash))
                {
                    result = new(FeatureBuilderManagementOutcome.Stale, ErrorCode: "feature_builder.test.proposal_changed");
                    return;
                }
                var existing = await ReadFeatureBuilderTestRunAsync(
                    connection, transaction, testRun.TestRunId, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    result = Equivalent(existing, testRun)
                        ? new(FeatureBuilderManagementOutcome.Duplicate, existing, "feature_builder.test.duplicate")
                        : new(FeatureBuilderManagementOutcome.Invalid, ErrorCode: "feature_builder.test.id_conflict");
                    return;
                }
                await InsertFeatureBuilderTestRunAsync(
                    connection, transaction, testRun, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                result = new(FeatureBuilderManagementOutcome.Succeeded, testRun);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return new(FeatureBuilderManagementOutcome.Stale, ErrorCode: "feature_builder.test.constraint_failed");
        }
        return result;
    }

    public async Task<FeatureBuilderTestRunWriteResult> ReplaceTestRunAsync(
        long expectedRevision,
        FeatureBuilderTestRun replacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (expectedRevision < 0 || replacement.Revision != expectedRevision + 1 ||
            !IsValidTestRun(replacement))
            return new(FeatureBuilderManagementOutcome.Invalid, ErrorCode: "feature_builder.test.invalid");
        FeatureBuilderTestRunWriteResult result = new(
            FeatureBuilderManagementOutcome.Invalid,
            ErrorCode: "feature_builder.test.not_updated");
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var current = await ReadFeatureBuilderTestRunAsync(
                connection, transaction, replacement.TestRunId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                result = new(FeatureBuilderManagementOutcome.NotFound, ErrorCode: "feature_builder.test.not_found");
                return;
            }
            if (current.Revision != expectedRevision || !SameTestIdentity(current, replacement) ||
                !FeatureBuilderPolicy.CanTransition(current.State, replacement.State))
            {
                result = new(FeatureBuilderManagementOutcome.Stale, current, "feature_builder.test.compare_failed");
                return;
            }
            if (await UpdateFeatureBuilderTestRunAsync(
                    connection, transaction, expectedRevision, replacement, cancellationToken).ConfigureAwait(false) != 1)
            {
                result = new(FeatureBuilderManagementOutcome.Stale, current, "feature_builder.test.compare_failed");
                return;
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            result = new(FeatureBuilderManagementOutcome.Succeeded, replacement);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal async Task RecoverFeatureBuilderOnStartupAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                UPDATE feature_builder_sessions
                SET state = 'interrupted', revision = revision + 1,
                    active_generation_id = NULL,
                    fixed_error_code = 'feature_builder.runtime.interrupted',
                    updated_at_utc = $nowUtc
                WHERE state = 'generating';
                """,
                cancellationToken,
                transaction,
                ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                UPDATE capability_test_runs
                SET status = 'interrupted', revision = revision + 1,
                    result_json = '{"code":"feature_builder.test.interrupted"}',
                    fixed_error_code = 'feature_builder.test.interrupted',
                    completed_at_utc = $nowUtc
                WHERE status = 'running';
                """,
                cancellationToken,
                transaction,
                ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FeatureBuilderSession?> ReadFeatureBuilderSessionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{FeatureBuilderSessionSelect} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadFeatureBuilderSession(reader)
            : null;
    }

    private static async Task<FeatureBuilderProposal?> ReadFeatureBuilderProposalAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string proposalId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{FeatureBuilderProposalSelect} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", proposalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadFeatureBuilderProposal(reader)
            : null;
    }

    private static async Task<FeatureBuilderProposal?> ReadFeatureBuilderProposalByGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{FeatureBuilderProposalSelect} WHERE generation_id = $generationId;";
        command.Parameters.AddWithValue("$generationId", generationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadFeatureBuilderProposal(reader)
            : null;
    }

    private static async Task<FeatureBuilderTestRun?> ReadFeatureBuilderTestRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string testRunId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{FeatureBuilderTestRunSelect} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", testRunId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadFeatureBuilderTestRun(reader)
            : null;
    }

    private static Task InsertFeatureBuilderSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FeatureBuilderSession session,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            """
            INSERT INTO feature_builder_sessions(
                id, mode, state, revision, requirement, requirement_hash,
                answers_json, questions_json, base_capability_id, base_version,
                base_content_hash, expected_catalog_revision, inventory_hash,
                active_generation_id, fixed_error_code, created_at_utc, updated_at_utc)
            VALUES (
                $id, $mode, $state, $revision, $requirement, $requirementHash,
                $answersJson, $questionsJson, $baseCapabilityId, $baseVersion,
                $baseContentHash, $expectedCatalogRevision, $inventoryHash,
                $activeGenerationId, $fixedErrorCode, $createdAtUtc, $updatedAtUtc);
            """,
            cancellationToken,
            transaction,
            SessionParameters(session));

    private static Task<int> UpdateFeatureBuilderSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long expectedRevision,
        FeatureBuilderSession session,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            """
            UPDATE feature_builder_sessions
            SET state = $state, revision = $revision, answers_json = $answersJson,
                questions_json = $questionsJson, active_generation_id = $activeGenerationId,
                fixed_error_code = $fixedErrorCode, updated_at_utc = $updatedAtUtc
            WHERE id = $id AND revision = $expectedRevision;
            """,
            cancellationToken,
            transaction,
            ("$state", FormatSessionState(session.State)),
            ("$revision", session.Revision),
            ("$answersJson", SerializeAnswers(session.Answers)),
            ("$questionsJson", SerializeQuestions(session.Questions)),
            ("$activeGenerationId", session.ActiveGenerationId),
            ("$fixedErrorCode", session.FixedErrorCode),
            ("$updatedAtUtc", Format(session.UpdatedAtUtc)),
            ("$id", session.SessionId),
            ("$expectedRevision", expectedRevision));

    private static Task InsertFeatureBuilderProposalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FeatureBuilderProposal proposal,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            """
            INSERT INTO improvement_proposals(
                id, session_id, session_revision, generation_id, request_hash,
                response_hash, inventory_hash, base_capability_id, base_version,
                base_content_hash, expected_catalog_revision, candidate_content_hash,
                proposal_hash, manifest_json, instructions_markdown, input_schema_json,
                settings_schema_json, workflow_json, tests_json, known_limitations_json,
                test_evidence_hash, status, revision, fixed_issue_code, applied_draft_id,
                created_at_utc, updated_at_utc, terminal_at_utc)
            VALUES (
                $id, $sessionId, $sessionRevision, $generationId, $requestHash,
                $responseHash, $inventoryHash, $baseCapabilityId, $baseVersion,
                $baseContentHash, $expectedCatalogRevision, $candidateContentHash,
                $proposalHash, $manifestJson, $instructions, $inputSchemaJson,
                $settingsSchemaJson, $workflowJson, $testsJson, $limitationsJson,
                $testEvidenceHash, $status, $revision, $fixedIssueCode, $appliedDraftId,
                $createdAtUtc, $updatedAtUtc, $terminalAtUtc);
            """,
            cancellationToken,
            transaction,
            ProposalParameters(proposal));

    private static Task<int> UpdateFeatureBuilderProposalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long expectedRevision,
        FeatureBuilderProposal proposal,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            """
            UPDATE improvement_proposals
            SET status = $status, revision = $revision, fixed_issue_code = $fixedIssueCode,
                test_evidence_hash = $testEvidenceHash,
                applied_draft_id = $appliedDraftId, updated_at_utc = $updatedAtUtc,
                terminal_at_utc = $terminalAtUtc
            WHERE id = $id AND revision = $expectedRevision;
            """,
            cancellationToken,
            transaction,
            ("$status", FormatProposalState(proposal.State)),
            ("$revision", proposal.Revision),
            ("$fixedIssueCode", proposal.FixedIssueCode),
            ("$testEvidenceHash", proposal.TestEvidenceHash?.Value),
            ("$appliedDraftId", proposal.AppliedDraftId),
            ("$updatedAtUtc", Format(proposal.UpdatedAtUtc)),
            ("$terminalAtUtc", proposal.TerminalAtUtc is null ? null : Format(proposal.TerminalAtUtc.Value)),
            ("$id", proposal.ProposalId),
            ("$expectedRevision", expectedRevision));

    private static Task InsertFeatureBuilderTestRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FeatureBuilderTestRun testRun,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            """
            INSERT INTO capability_test_runs(
                id, proposal_id, kind, attempt, candidate_content_hash, engine_version,
                status, revision, result_json, fixed_error_code, started_at_utc, completed_at_utc)
            VALUES (
                $id, $proposalId, $kind, $attempt, $candidateContentHash, $engineVersion,
                $status, $revision, $resultJson, $fixedErrorCode, $startedAtUtc, $completedAtUtc);
            """,
            cancellationToken,
            transaction,
            ("$id", testRun.TestRunId),
            ("$proposalId", testRun.ProposalId),
            ("$kind", FormatTestRunKind(testRun.Kind)),
            ("$attempt", testRun.Attempt),
            ("$candidateContentHash", testRun.CandidateContentHash.Value),
            ("$engineVersion", testRun.EngineVersion),
            ("$status", FormatTestRunState(testRun.State)),
            ("$revision", testRun.Revision),
            ("$resultJson", testRun.CanonicalResultJson),
            ("$fixedErrorCode", testRun.FixedErrorCode),
            ("$startedAtUtc", Format(testRun.StartedAtUtc)),
            ("$completedAtUtc", testRun.CompletedAtUtc is null ? null : Format(testRun.CompletedAtUtc.Value)));

    private static Task<int> UpdateFeatureBuilderTestRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long expectedRevision,
        FeatureBuilderTestRun testRun,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            """
            UPDATE capability_test_runs
            SET status = $status, revision = $revision, result_json = $resultJson,
                fixed_error_code = $fixedErrorCode, completed_at_utc = $completedAtUtc
            WHERE id = $id AND revision = $expectedRevision;
            """,
            cancellationToken,
            transaction,
            ("$status", FormatTestRunState(testRun.State)),
            ("$revision", testRun.Revision),
            ("$resultJson", testRun.CanonicalResultJson),
            ("$fixedErrorCode", testRun.FixedErrorCode),
            ("$completedAtUtc", testRun.CompletedAtUtc is null ? null : Format(testRun.CompletedAtUtc.Value)),
            ("$id", testRun.TestRunId),
            ("$expectedRevision", expectedRevision));

    private static async Task<IReadOnlyList<FeatureBuilderTestRun>?> ReadPassingBuilderEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FeatureBuilderProposal proposal,
        CancellationToken cancellationToken)
    {
        var selected = new List<FeatureBuilderTestRun>(2);
        foreach (var kind in new[] { FeatureBuilderTestRunKind.StaticScan, FeatureBuilderTestRunKind.DryRun })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"""
                {FeatureBuilderTestRunSelect}
                WHERE proposal_id = $proposalId AND kind = $kind
                ORDER BY attempt DESC LIMIT 1;
                """;
            command.Parameters.AddWithValue("$proposalId", proposal.ProposalId);
            command.Parameters.AddWithValue("$kind", FormatTestRunKind(kind));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            var run = ReadFeatureBuilderTestRun(reader);
            if (run.State != FeatureBuilderTestRunState.Passed ||
                !run.CandidateContentHash.Equals(proposal.CandidateContentHash))
                return null;
            selected.Add(run);
        }
        return selected.AsReadOnly();
    }

    private static FeatureBuilderSession ReadFeatureBuilderSession(DbDataReader reader)
    {
        var target = reader.IsDBNull(8)
            ? null
            : new FeatureBuilderRevisionTarget(
                new CapabilityVersionRef(
                    CapabilityId.Parse(reader.GetString(8)),
                    CapabilitySemanticVersion.Parse(reader.GetString(9)),
                    CapabilityContentHash.Parse(reader.GetString(10))),
                reader.GetInt64(11));
        var session = new FeatureBuilderSession(
            reader.GetString(0),
            ParseSessionMode(reader.GetString(1)),
            ParseSessionState(reader.GetString(2)),
            reader.GetInt64(3),
            reader.GetString(4),
            CapabilityContentHash.Parse(reader.GetString(5)),
            DeserializeAnswers(reader.GetString(6)),
            DeserializeQuestions(reader.GetString(7)),
            target,
            reader.GetInt64(11),
            CapabilityContentHash.Parse(reader.GetString(12)),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            ParseTimestamp(reader.GetString(15)),
            ParseTimestamp(reader.GetString(16)));
        if (!IsValidSession(session)) throw new InvalidDataException("Feature Builder session이 손상되었습니다.");
        return session;
    }

    private static FeatureBuilderProposal ReadFeatureBuilderProposal(DbDataReader reader)
    {
        var baseVersion = reader.IsDBNull(7)
            ? null
            : new CapabilityVersionRef(
                CapabilityId.Parse(reader.GetString(7)),
                CapabilitySemanticVersion.Parse(reader.GetString(8)),
                CapabilityContentHash.Parse(reader.GetString(9)));
        CapabilityVersionPayload? candidate = null;
        if (!reader.IsDBNull(13)) candidate = ReadPayload(reader, 13, 14, 15, 16, 17, 18);
        var proposal = new FeatureBuilderProposal(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            CapabilityContentHash.Parse(reader.GetString(4)),
            CapabilityContentHash.Parse(reader.GetString(5)),
            CapabilityContentHash.Parse(reader.GetString(6)),
            baseVersion,
            reader.GetInt64(10),
            reader.IsDBNull(11) ? null : CapabilityContentHash.Parse(reader.GetString(11)),
            CapabilityContentHash.Parse(reader.GetString(12)),
            candidate,
            DeserializeLimitations(reader.IsDBNull(19)
                ? throw new InvalidDataException("Feature Builder limitations가 누락되었습니다.")
                : reader.GetString(19)),
            reader.IsDBNull(20) ? null : CapabilityContentHash.Parse(reader.GetString(20)),
            ParseProposalState(reader.GetString(21)),
            reader.GetInt64(22),
            reader.IsDBNull(23) ? null : reader.GetString(23),
            reader.IsDBNull(24) ? null : reader.GetString(24),
            ParseTimestamp(reader.GetString(25)),
            ParseTimestamp(reader.GetString(26)),
            reader.IsDBNull(27) ? null : ParseTimestamp(reader.GetString(27)));
        if (!IsValidProposal(proposal)) throw new InvalidDataException("Feature Builder proposal이 손상되었습니다.");
        return proposal;
    }

    private static FeatureBuilderTestRun ReadFeatureBuilderTestRun(DbDataReader reader)
    {
        var testRun = new FeatureBuilderTestRun(
            reader.GetString(0),
            reader.GetString(1),
            ParseTestRunKind(reader.GetString(2)),
            reader.GetInt32(3),
            CapabilityContentHash.Parse(reader.GetString(4)),
            reader.GetString(5),
            ParseTestRunState(reader.GetString(6)),
            reader.GetInt64(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            ParseTimestamp(reader.GetString(10)),
            reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11)));
        if (!IsValidTestRun(testRun)) throw new InvalidDataException("Feature Builder test run이 손상되었습니다.");
        return testRun;
    }

    private static (string Name, object? Value)[] SessionParameters(FeatureBuilderSession session) =>
    [
        ("$id", session.SessionId),
        ("$mode", FormatSessionMode(session.Mode)),
        ("$state", FormatSessionState(session.State)),
        ("$revision", session.Revision),
        ("$requirement", session.Requirement),
        ("$requirementHash", session.RequirementHash.Value),
        ("$answersJson", SerializeAnswers(session.Answers)),
        ("$questionsJson", SerializeQuestions(session.Questions)),
        ("$baseCapabilityId", session.RevisionTarget?.BaseVersion.Id.Value),
        ("$baseVersion", session.RevisionTarget?.BaseVersion.Version.Value),
        ("$baseContentHash", session.RevisionTarget?.BaseVersion.ContentHash.Value),
        ("$expectedCatalogRevision", session.ExpectedCatalogRevision),
        ("$inventoryHash", session.InventoryHash.Value),
        ("$activeGenerationId", session.ActiveGenerationId),
        ("$fixedErrorCode", session.FixedErrorCode),
        ("$createdAtUtc", Format(session.CreatedAtUtc)),
        ("$updatedAtUtc", Format(session.UpdatedAtUtc)),
    ];

    private static (string Name, object? Value)[] ProposalParameters(FeatureBuilderProposal proposal) =>
    [
        ("$id", proposal.ProposalId),
        ("$sessionId", proposal.SessionId),
        ("$sessionRevision", proposal.SessionRevision),
        ("$generationId", proposal.GenerationId),
        ("$requestHash", proposal.RequestHash.Value),
        ("$responseHash", proposal.ResponseHash.Value),
        ("$inventoryHash", proposal.InventoryHash.Value),
        ("$baseCapabilityId", proposal.BaseVersion?.Id.Value),
        ("$baseVersion", proposal.BaseVersion?.Version.Value),
        ("$baseContentHash", proposal.BaseVersion?.ContentHash.Value),
        ("$expectedCatalogRevision", proposal.ExpectedCatalogRevision),
        ("$candidateContentHash", proposal.CandidateContentHash?.Value),
        ("$proposalHash", proposal.ProposalHash.Value),
        ("$manifestJson", proposal.Candidate?.CanonicalManifestJson),
        ("$instructions", proposal.Candidate?.Instructions),
        ("$inputSchemaJson", proposal.Candidate?.CanonicalInputSchemaJson),
        ("$settingsSchemaJson", proposal.Candidate?.CanonicalSettingsSchemaJson),
        ("$workflowJson", proposal.Candidate?.CanonicalWorkflowJson),
        ("$testsJson", proposal.Candidate?.CanonicalTestsJson),
        ("$limitationsJson", SerializeLimitations(proposal.KnownLimitations)),
        ("$testEvidenceHash", proposal.TestEvidenceHash?.Value),
        ("$status", FormatProposalState(proposal.State)),
        ("$revision", proposal.Revision),
        ("$fixedIssueCode", proposal.FixedIssueCode),
        ("$appliedDraftId", proposal.AppliedDraftId),
        ("$createdAtUtc", Format(proposal.CreatedAtUtc)),
        ("$updatedAtUtc", Format(proposal.UpdatedAtUtc)),
        ("$terminalAtUtc", proposal.TerminalAtUtc is null ? null : Format(proposal.TerminalAtUtc.Value)),
    ];

    private static bool IsValidNewSession(FeatureBuilderSession session) =>
        session.Revision == 0 && session.State == FeatureBuilderSessionState.Collecting &&
        session.Answers.Count == 0 && session.Questions.Count == 0 && session.ActiveGenerationId is null &&
        session.FixedErrorCode is null && IsValidSession(session);

    private static bool IsValidSession(FeatureBuilderSession session)
    {
        if (!Enum.IsDefined(session.Mode) || !Enum.IsDefined(session.State) ||
            !FeatureBuilderPolicy.IsSessionId(session.SessionId) || session.Revision < 0 ||
            session.RequirementHash is null || session.InventoryHash is null ||
            !FeatureBuilderPolicy.ValidateRequirement(session.Requirement).IsValid ||
            !session.RequirementHash.Equals(FeatureBuilderHashPolicy.ComputeRequirementHash(session.Requirement)) ||
            !IsValidAnswers(session.Answers) ||
            session.CreatedAtUtc.Offset != TimeSpan.Zero || session.UpdatedAtUtc.Offset != TimeSpan.Zero ||
            session.UpdatedAtUtc < session.CreatedAtUtc)
            return false;

        var hasValidTarget = session.Mode switch
        {
            FeatureBuilderMode.Create => session.RevisionTarget is null && session.ExpectedCatalogRevision == 0,
            FeatureBuilderMode.Revise => session.RevisionTarget is
            {
                BaseVersion: not null,
                ExpectedCatalogRevision: > 0,
            } target &&
                session.ExpectedCatalogRevision == target.ExpectedCatalogRevision,
            _ => false,
        };
        return hasValidTarget &&
               (session.State == FeatureBuilderSessionState.Generating) ==
               (session.ActiveGenerationId is not null) &&
               (session.ActiveGenerationId is null ||
                FeatureBuilderPolicy.IsGenerationId(session.ActiveGenerationId)) &&
               (session.FixedErrorCode is null || FeatureBuilderPolicy.IsFixedCode(session.FixedErrorCode)) &&
               (session.State == FeatureBuilderSessionState.AwaitingInformation
                   ? FeatureBuilderPolicy.ValidateQuestions(session.Questions).IsValid
                   : session.Questions.Count == 0) &&
               session.State switch
               {
                   FeatureBuilderSessionState.Interrupted or FeatureBuilderSessionState.Cancelled =>
                       session.FixedErrorCode is not null,
                   FeatureBuilderSessionState.Completed => true,
                   _ => session.FixedErrorCode is null,
               };
    }

    private static bool IsValidNewProposal(FeatureBuilderProposal proposal) =>
        proposal.Revision == 0 &&
        proposal.State is (FeatureBuilderProposalState.Pending or FeatureBuilderProposalState.Quarantined) &&
        IsValidProposal(proposal);

    private static bool IsValidProposal(FeatureBuilderProposal proposal)
    {
        if (!Enum.IsDefined(proposal.State) ||
            !FeatureBuilderPolicy.IsProposalId(proposal.ProposalId) ||
            !FeatureBuilderPolicy.IsSessionId(proposal.SessionId) || proposal.SessionRevision < 0 ||
            !FeatureBuilderPolicy.IsGenerationId(proposal.GenerationId) || proposal.ExpectedCatalogRevision < 0 ||
            proposal.RequestHash is null || proposal.ResponseHash is null || proposal.InventoryHash is null ||
            proposal.ProposalHash is null ||
            !FeatureBuilderPolicy.ValidateLimitations(proposal.KnownLimitations).IsValid ||
            proposal.KnownLimitations.Any(value => FeatureBuilderPolicy.ContainsDirectSecret(value, false)) ||
            proposal.Revision < 0 || proposal.CreatedAtUtc.Offset != TimeSpan.Zero ||
            proposal.UpdatedAtUtc.Offset != TimeSpan.Zero || proposal.UpdatedAtUtc < proposal.CreatedAtUtc ||
            proposal.TerminalAtUtc is not null &&
            (proposal.TerminalAtUtc.Value.Offset != TimeSpan.Zero ||
             proposal.TerminalAtUtc.Value < proposal.CreatedAtUtc) ||
            (proposal.FixedIssueCode is not null && !FeatureBuilderPolicy.IsFixedCode(proposal.FixedIssueCode)))
            return false;
        if (!StringComparer.Ordinal.Equals(
                proposal.ProposalId,
                FeatureBuilderHashPolicy.CreateProposalId(proposal.ProposalHash)) ||
            (proposal.Candidate is null) != (proposal.CandidateContentHash is null) ||
            proposal.Candidate is not null &&
            !CapabilityContentHasher.Compute(proposal.Candidate).Equals(proposal.CandidateContentHash))
            return false;
        if ((proposal.BaseVersion is null) != (proposal.ExpectedCatalogRevision == 0) ||
            proposal.BaseVersion is not null && proposal.Candidate is not null &&
            (!proposal.BaseVersion.Id.Equals(proposal.Candidate.Manifest.Id) ||
             proposal.Candidate.Manifest.Version.CompareTo(proposal.BaseVersion.Version) <= 0))
            return false;
        if (proposal.Candidate is not null)
        {
            var definition = CapabilityDefinitionPolicy.Validate(proposal.Candidate);
            var fixtureTests = CapabilityFixtureTestPolicy.Validate(proposal.Candidate.CanonicalTestsJson);
            if (!definition.IsValid || !fixtureTests.IsValid || fixtureTests.CaseCount == 0 ||
                !ManifestMatchesPayload(proposal.Candidate) ||
                !HasStrictBuilderManifestShape(proposal.Candidate.CanonicalManifestJson) ||
                CapabilityIngressPolicy.GetViolation(proposal.Candidate) != CapabilityIngressViolation.None)
                return false;
        }
        var expectedProposalHash = FeatureBuilderHashPolicy.ComputeProposalHash(
            proposal.GenerationId,
            proposal.RequestHash,
            proposal.ResponseHash,
            proposal.CandidateContentHash,
            proposal.BaseVersion,
            proposal.ExpectedCatalogRevision);
        if (!proposal.ProposalHash.Equals(expectedProposalHash)) return false;
        return proposal.State switch
        {
            FeatureBuilderProposalState.Pending => proposal.Candidate is not null &&
                                                   proposal.TestEvidenceHash is null &&
                                                   proposal.FixedIssueCode is null &&
                                                   proposal.TerminalAtUtc is null &&
                                                   proposal.AppliedDraftId is null,
            FeatureBuilderProposalState.Applied => proposal.Candidate is not null &&
                                                   proposal.TestEvidenceHash is not null &&
                                                   proposal.FixedIssueCode is null &&
                                                   proposal.TerminalAtUtc is not null &&
                                                   proposal.AppliedDraftId is not null,
            FeatureBuilderProposalState.Quarantined => proposal.Candidate is null &&
                                                       proposal.CandidateContentHash is null &&
                                                       proposal.TestEvidenceHash is null &&
                                                       proposal.FixedIssueCode is not null &&
                                                       proposal.TerminalAtUtc is not null &&
                                                       proposal.AppliedDraftId is null,
            FeatureBuilderProposalState.Rejected or FeatureBuilderProposalState.Stale =>
                proposal.Candidate is not null && proposal.TestEvidenceHash is null &&
                proposal.FixedIssueCode is not null && proposal.TerminalAtUtc is not null &&
                proposal.AppliedDraftId is null,
            _ => false,
        };
    }

    private static bool IsValidNewTestRun(FeatureBuilderTestRun testRun) =>
        testRun.Revision == 0 && testRun.State == FeatureBuilderTestRunState.Running &&
        testRun.CanonicalResultJson == "{}" && testRun.FixedErrorCode is null &&
        testRun.CompletedAtUtc is null && IsValidTestRun(testRun);

    private static bool IsValidTestRun(FeatureBuilderTestRun testRun) =>
        Enum.IsDefined(testRun.Kind) && Enum.IsDefined(testRun.State) &&
        FeatureBuilderPolicy.IsTestRunId(testRun.TestRunId) &&
        FeatureBuilderPolicy.IsProposalId(testRun.ProposalId) && testRun.Attempt is >= 1 and <= 16 &&
        testRun.CandidateContentHash is not null &&
        testRun.EngineVersion is { Length: >= 1 and <= 64 } &&
        testRun.EngineVersion.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_') &&
        testRun.Revision >= 0 &&
        testRun.CanonicalResultJson is { Length: >= 2 and <= 32768 } &&
        IsJsonContainer(testRun.CanonicalResultJson) && testRun.StartedAtUtc.Offset == TimeSpan.Zero &&
        (testRun.CompletedAtUtc is null ||
         testRun.CompletedAtUtc.Value.Offset == TimeSpan.Zero && testRun.CompletedAtUtc >= testRun.StartedAtUtc) &&
        (testRun.FixedErrorCode is null || FeatureBuilderPolicy.IsFixedCode(testRun.FixedErrorCode)) &&
        testRun.State switch
        {
            FeatureBuilderTestRunState.Running => testRun.CompletedAtUtc is null && testRun.FixedErrorCode is null,
            FeatureBuilderTestRunState.Passed => testRun.CompletedAtUtc is not null && testRun.FixedErrorCode is null,
            FeatureBuilderTestRunState.Failed or FeatureBuilderTestRunState.Interrupted =>
                testRun.CompletedAtUtc is not null && testRun.FixedErrorCode is not null,
            _ => false,
        };

    private static bool IsJsonContainer(string json)
    {
        if (FeatureBuilderPolicy.ContainsDirectSecret(json, false)) return false;
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(json) > 32_768) return false;
            using var document = JsonDocument.Parse(json, StrictFeatureBuilderJsonOptions);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array &&
                   !CapabilityIngressPolicy.ContainsSensitiveJsonLiteral(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool SameSessionIdentity(FeatureBuilderSession left, FeatureBuilderSession right) =>
        left.SessionId == right.SessionId && left.Mode == right.Mode &&
        left.Requirement == right.Requirement && left.RequirementHash.Equals(right.RequirementHash) &&
        Equals(left.RevisionTarget, right.RevisionTarget) &&
        left.ExpectedCatalogRevision == right.ExpectedCatalogRevision &&
        left.InventoryHash.Equals(right.InventoryHash) && left.CreatedAtUtc == right.CreatedAtUtc;

    private static bool IsValidSessionTransition(FeatureBuilderSession current, FeatureBuilderSession replacement)
    {
        if (!SameSessionIdentity(current, replacement) ||
            !FeatureBuilderPolicy.CanTransition(current.State, replacement.State) ||
            replacement.UpdatedAtUtc < current.UpdatedAtUtc)
            return false;
        if (current.State == FeatureBuilderSessionState.Generating &&
            replacement.State == FeatureBuilderSessionState.AwaitingInformation &&
            !AreFollowUpQuestionsCompatible(replacement.Answers, replacement.Questions))
            return false;
        if (current.State != FeatureBuilderSessionState.AwaitingInformation ||
            replacement.State != FeatureBuilderSessionState.Generating)
            return current.Answers.SequenceEqual(replacement.Answers);
        if (replacement.Answers.Count <= current.Answers.Count ||
            !current.Answers.SequenceEqual(replacement.Answers.Take(current.Answers.Count)))
            return false;
        return FeatureBuilderPolicy.ValidateSubmittedAnswers(
            current.Answers,
            current.Questions,
            replacement.Answers.Skip(current.Answers.Count).ToArray()).IsValid;
    }

    private static bool AreFollowUpQuestionsCompatible(
        IReadOnlyList<FeatureBuilderAnswer> answers,
        IReadOnlyList<FeatureBuilderQuestion> questions)
    {
        if (answers.Count + questions.Count > FeatureBuilderPolicy.MaximumAnswers) return false;
        var answeredQuestionIds = answers.Select(answer => answer.QuestionId).ToHashSet(StringComparer.Ordinal);
        return questions.All(question => !answeredQuestionIds.Contains(question.QuestionId));
    }

    private static bool SameProposalPayload(FeatureBuilderProposal left, FeatureBuilderProposal right) =>
        left.ProposalId == right.ProposalId && left.SessionId == right.SessionId &&
        left.SessionRevision == right.SessionRevision && left.GenerationId == right.GenerationId &&
        left.RequestHash.Equals(right.RequestHash) && left.ResponseHash.Equals(right.ResponseHash) &&
        left.InventoryHash.Equals(right.InventoryHash) && Equals(left.BaseVersion, right.BaseVersion) &&
        left.ExpectedCatalogRevision == right.ExpectedCatalogRevision &&
        Equals(left.CandidateContentHash, right.CandidateContentHash) &&
        left.ProposalHash.Equals(right.ProposalHash) && EquivalentPayload(left.Candidate, right.Candidate) &&
        left.KnownLimitations.SequenceEqual(right.KnownLimitations, StringComparer.Ordinal) &&
        Equals(left.TestEvidenceHash, right.TestEvidenceHash) && left.CreatedAtUtc == right.CreatedAtUtc;

    private static bool SameTestIdentity(FeatureBuilderTestRun left, FeatureBuilderTestRun right) =>
        left.TestRunId == right.TestRunId && left.ProposalId == right.ProposalId && left.Kind == right.Kind &&
        left.Attempt == right.Attempt && left.CandidateContentHash.Equals(right.CandidateContentHash) &&
        left.EngineVersion == right.EngineVersion && left.StartedAtUtc == right.StartedAtUtc;

    private static bool Equivalent(FeatureBuilderSession left, FeatureBuilderSession right) =>
        SameSessionIdentity(left, right) && left.State == right.State && left.Revision == right.Revision &&
        left.Answers.SequenceEqual(right.Answers) && QuestionsEqual(left.Questions, right.Questions) &&
        left.ActiveGenerationId == right.ActiveGenerationId && left.FixedErrorCode == right.FixedErrorCode &&
        left.UpdatedAtUtc == right.UpdatedAtUtc;

    private static bool Equivalent(FeatureBuilderProposal left, FeatureBuilderProposal right) =>
        SameProposalPayload(left, right) && left.State == right.State && left.Revision == right.Revision &&
        left.FixedIssueCode == right.FixedIssueCode && left.AppliedDraftId == right.AppliedDraftId &&
        left.UpdatedAtUtc == right.UpdatedAtUtc && left.TerminalAtUtc == right.TerminalAtUtc;

    private static bool Equivalent(FeatureBuilderTestRun left, FeatureBuilderTestRun right) => left == right;

    private static bool IsValidAnswers(IReadOnlyList<FeatureBuilderAnswer> answers)
    {
        if (answers.Count > FeatureBuilderPolicy.MaximumAnswers) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var answer in answers)
        {
            if (!FeatureBuilderPolicy.IsQuestionId(answer.QuestionId) || !ids.Add(answer.QuestionId) ||
                string.IsNullOrWhiteSpace(answer.Value) ||
                answer.Value.Length > FeatureBuilderPolicy.MaximumAnswerCharacters ||
                FeatureBuilderPolicy.ContainsDirectSecret(
                    answer.Value,
                    FeatureBuilderPolicy.IsSensitiveQuestionId(answer.QuestionId)))
                return false;
            try
            {
                bytes += new System.Text.UTF8Encoding(false, true).GetByteCount(answer.Value);
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }
        return bytes <= FeatureBuilderPolicy.MaximumAnswersUtf8Bytes;
    }

    private static bool QuestionsEqual(
        IReadOnlyList<FeatureBuilderQuestion> left,
        IReadOnlyList<FeatureBuilderQuestion> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.QuestionId == pair.Second.QuestionId &&
            pair.First.Prompt == pair.Second.Prompt &&
            pair.First.AnswerKind == pair.Second.AnswerKind &&
            pair.First.Choices.SequenceEqual(pair.Second.Choices, StringComparer.Ordinal));

    private static bool HasStrictBuilderManifestShape(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, StrictFeatureBuilderJsonOptions);
            var fields = ReadExactJsonFields(
                document.RootElement,
                "schemaVersion",
                "id",
                "version",
                "displayName",
                "description",
                "kind",
                "intentExamples",
                "requiredTools",
                "requiredConnections",
                "maximumRisk",
                "entrypoint");
            if (fields["schemaVersion"].ValueKind != JsonValueKind.Number ||
                !fields["schemaVersion"].TryGetInt32(out _) ||
                fields["intentExamples"].ValueKind != JsonValueKind.Array ||
                fields["requiredTools"].ValueKind != JsonValueKind.Array ||
                fields["requiredConnections"].ValueKind != JsonValueKind.Array)
                return false;
            foreach (var name in new[]
                     {
                         "id", "version", "displayName", "description", "kind", "maximumRisk", "entrypoint",
                     })
            {
                if (fields[name].ValueKind != JsonValueKind.String) return false;
            }
            if (fields["intentExamples"].EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String) ||
                fields["requiredTools"].EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                return false;
            foreach (var requirement in fields["requiredConnections"].EnumerateArray())
            {
                var requirementFields = ReadExactJsonFields(requirement, "slot", "kind");
                if (requirementFields.Values.Any(value => value.ValueKind != JsonValueKind.String)) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool EquivalentPayload(
        CapabilityVersionPayload? left,
        CapabilityVersionPayload? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        return ManifestMatchesPayload(left) && ManifestMatchesPayload(right) &&
               CapabilityContentHasher.Compute(left).Equals(CapabilityContentHasher.Compute(right)) &&
               left.CanonicalManifestJson == right.CanonicalManifestJson &&
               left.Instructions == right.Instructions &&
               left.CanonicalInputSchemaJson == right.CanonicalInputSchemaJson &&
               left.CanonicalSettingsSchemaJson == right.CanonicalSettingsSchemaJson &&
               left.CanonicalWorkflowJson == right.CanonicalWorkflowJson &&
               left.CanonicalTestsJson == right.CanonicalTestsJson;
    }

    private static bool IsSafeDraftId(string value) =>
        value.Length is >= 1 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string SerializeAnswers(IReadOnlyList<FeatureBuilderAnswer> answers) =>
        JsonSerializer.Serialize(answers.Select(answer => new
        {
            questionId = answer.QuestionId,
            value = answer.Value,
        }));

    private static IReadOnlyList<FeatureBuilderAnswer> DeserializeAnswers(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, StrictFeatureBuilderJsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Feature Builder answers가 손상되었습니다.");
            return document.RootElement.EnumerateArray().Select(item =>
            {
                var fields = ReadExactJsonFields(item, "questionId", "value");
                return new FeatureBuilderAnswer(
                    RequireJsonString(fields["questionId"]),
                    RequireJsonString(fields["value"]));
            }).ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException("Feature Builder answers가 손상되었습니다.", exception);
        }
    }

    private static string SerializeQuestions(IReadOnlyList<FeatureBuilderQuestion> questions) =>
        JsonSerializer.Serialize(questions.Select(question => new
        {
            questionId = question.QuestionId,
            prompt = question.Prompt,
            answerKind = question.AnswerKind == FeatureBuilderAnswerKind.Text ? "text" : "single_choice",
            choices = question.Choices,
        }));

    private static IReadOnlyList<FeatureBuilderQuestion> DeserializeQuestions(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, StrictFeatureBuilderJsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Feature Builder questions가 손상되었습니다.");
            return document.RootElement.EnumerateArray().Select(item =>
            {
                var fields = ReadExactJsonFields(
                    item, "questionId", "prompt", "answerKind", "choices");
                if (fields["choices"].ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Feature Builder choices가 손상되었습니다.");
                return new FeatureBuilderQuestion(
                RequireJsonString(fields["questionId"]),
                RequireJsonString(fields["prompt"]),
                RequireJsonString(fields["answerKind"]) switch
                {
                    "text" => FeatureBuilderAnswerKind.Text,
                    "single_choice" => FeatureBuilderAnswerKind.SingleChoice,
                    _ => throw new InvalidDataException("Feature Builder answer kind가 손상되었습니다."),
                },
                fields["choices"].EnumerateArray().Select(RequireJsonString).ToArray());
            }).ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException("Feature Builder questions가 손상되었습니다.", exception);
        }
    }

    private static string SerializeLimitations(IReadOnlyList<string> limitations) =>
        JsonSerializer.Serialize(limitations);

    private static IReadOnlyList<string> DeserializeLimitations(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, StrictFeatureBuilderJsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Feature Builder limitations가 손상되었습니다.");
            return document.RootElement.EnumerateArray().Select(RequireJsonString).ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("Feature Builder limitations가 손상되었습니다.", exception);
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadExactJsonFields(
        JsonElement value,
        params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Feature Builder JSON object가 손상되었습니다.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (!fields.TryAdd(property.Name, property.Value) || !insensitive.Add(property.Name))
                throw new InvalidDataException("Feature Builder JSON property가 중복되었습니다.");
        }
        if (fields.Count != names.Length || names.Any(name => !fields.ContainsKey(name)))
            throw new InvalidDataException("Feature Builder JSON fields가 손상되었습니다.");
        return fields;
    }

    private static string RequireJsonString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { } result
            ? result
            : throw new InvalidDataException("Feature Builder JSON string이 손상되었습니다.");

    private static string FormatSessionMode(FeatureBuilderMode mode) => mode switch
    {
        FeatureBuilderMode.Create => "create",
        FeatureBuilderMode.Revise => "revise",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static FeatureBuilderMode ParseSessionMode(string value) => value switch
    {
        "create" => FeatureBuilderMode.Create,
        "revise" => FeatureBuilderMode.Revise,
        _ => throw new InvalidDataException("Feature Builder mode가 손상되었습니다."),
    };

    private static string FormatSessionState(FeatureBuilderSessionState state) => state switch
    {
        FeatureBuilderSessionState.Collecting => "collecting",
        FeatureBuilderSessionState.AwaitingInformation => "awaiting_information",
        FeatureBuilderSessionState.Generating => "generating",
        FeatureBuilderSessionState.ProposalReady => "proposal_ready",
        FeatureBuilderSessionState.Completed => "completed",
        FeatureBuilderSessionState.Cancelled => "cancelled",
        FeatureBuilderSessionState.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static FeatureBuilderSessionState ParseSessionState(string value) => value switch
    {
        "collecting" => FeatureBuilderSessionState.Collecting,
        "awaiting_information" => FeatureBuilderSessionState.AwaitingInformation,
        "generating" => FeatureBuilderSessionState.Generating,
        "proposal_ready" => FeatureBuilderSessionState.ProposalReady,
        "completed" => FeatureBuilderSessionState.Completed,
        "cancelled" => FeatureBuilderSessionState.Cancelled,
        "interrupted" => FeatureBuilderSessionState.Interrupted,
        _ => throw new InvalidDataException("Feature Builder session state가 손상되었습니다."),
    };

    private static string FormatProposalState(FeatureBuilderProposalState state) => state switch
    {
        FeatureBuilderProposalState.Pending => "pending",
        FeatureBuilderProposalState.Applied => "applied",
        FeatureBuilderProposalState.Rejected => "rejected",
        FeatureBuilderProposalState.Quarantined => "quarantined",
        FeatureBuilderProposalState.Stale => "stale",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static FeatureBuilderProposalState ParseProposalState(string value) => value switch
    {
        "pending" => FeatureBuilderProposalState.Pending,
        "applied" => FeatureBuilderProposalState.Applied,
        "rejected" => FeatureBuilderProposalState.Rejected,
        "quarantined" => FeatureBuilderProposalState.Quarantined,
        "stale" => FeatureBuilderProposalState.Stale,
        _ => throw new InvalidDataException("Feature Builder proposal state가 손상되었습니다."),
    };

    private static string FormatTestRunKind(FeatureBuilderTestRunKind kind) => kind switch
    {
        FeatureBuilderTestRunKind.StaticScan => "static_scan",
        FeatureBuilderTestRunKind.DryRun => "dry_run",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static FeatureBuilderTestRunKind ParseTestRunKind(string value) => value switch
    {
        "static_scan" => FeatureBuilderTestRunKind.StaticScan,
        "dry_run" => FeatureBuilderTestRunKind.DryRun,
        _ => throw new InvalidDataException("Feature Builder test kind가 손상되었습니다."),
    };

    private static string FormatTestRunState(FeatureBuilderTestRunState state) => state switch
    {
        FeatureBuilderTestRunState.Running => "running",
        FeatureBuilderTestRunState.Passed => "passed",
        FeatureBuilderTestRunState.Failed => "failed",
        FeatureBuilderTestRunState.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static FeatureBuilderTestRunState ParseTestRunState(string value) => value switch
    {
        "running" => FeatureBuilderTestRunState.Running,
        "passed" => FeatureBuilderTestRunState.Passed,
        "failed" => FeatureBuilderTestRunState.Failed,
        "interrupted" => FeatureBuilderTestRunState.Interrupted,
        _ => throw new InvalidDataException("Feature Builder test state가 손상되었습니다."),
    };

    private static void ValidateSessionId(string value)
    {
        if (!FeatureBuilderPolicy.IsSessionId(value))
            throw new ArgumentException("Feature Builder session ID가 올바르지 않습니다.", nameof(value));
    }

    private static void ValidateProposalId(string value)
    {
        if (!FeatureBuilderPolicy.IsProposalId(value))
            throw new ArgumentException("Feature Builder proposal ID가 올바르지 않습니다.", nameof(value));
    }

    private static void ValidateTestRunId(string value)
    {
        if (!FeatureBuilderPolicy.IsTestRunId(value))
            throw new ArgumentException("Feature Builder test run ID가 올바르지 않습니다.", nameof(value));
    }
}
