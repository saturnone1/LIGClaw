using System.Text;
using System.Text.Json;
using LIGClaw.Domain;

namespace LIGClaw.Application.Capabilities;

public sealed class FeatureBuilderManagementService(
    IFeatureBuilderRepository repository,
    IFeatureBuilderRuntimeClient runtime,
    IFeatureBuilderProposalScanner scanner) : IFeatureBuilderManagementService
{
    private static readonly HashSet<string> RefusalCodes = new(StringComparer.Ordinal)
    {
        "capability.requirement_unsafe",
        "capability.requirement_unsupported",
        "capability.requirement_conflict",
    };

    public Task<FeatureBuilderSession?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken) =>
        repository.GetSessionAsync(sessionId, cancellationToken);

    public Task<FeatureBuilderProposal?> GetProposalAsync(
        string proposalId,
        CancellationToken cancellationToken) =>
        repository.GetProposalAsync(proposalId, cancellationToken);

    public async Task<FeatureBuilderManagementResult> StartSessionAsync(
        StartFeatureBuilderSessionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var requirement = FeatureBuilderPolicy.ValidateRequirement(command.Requirement);
        var inventory = FeatureBuilderHashPolicy.ValidateInventory(command.Inventory);
        if (!requirement.IsValid) return Invalid(requirement.ErrorCode!);
        if (!inventory.IsValid) return Invalid(inventory.ErrorCode!);
        if (!FeatureBuilderPolicy.IsSessionId(command.SessionId) ||
            !IsUtc(command.NowUtc) ||
            command.ExpectedCatalogRevision < 0 ||
            command.RevisionTarget is null && command.ExpectedCatalogRevision != 0 ||
            command.RevisionTarget is not null &&
            (command.RevisionTarget.ExpectedCatalogRevision != command.ExpectedCatalogRevision ||
             command.ExpectedCatalogRevision < 1))
            return Invalid("feature_builder.session.command_invalid");

        var inventoryHash = FeatureBuilderHashPolicy.ComputeInventoryHash(command.Inventory);
        var session = new FeatureBuilderSession(
            command.SessionId,
            command.RevisionTarget is null ? FeatureBuilderMode.Create : FeatureBuilderMode.Revise,
            FeatureBuilderSessionState.Collecting,
            0,
            command.Requirement,
            FeatureBuilderHashPolicy.ComputeRequirementHash(command.Requirement),
            [],
            [],
            command.RevisionTarget,
            command.ExpectedCatalogRevision,
            inventoryHash,
            null,
            null,
            command.NowUtc,
            command.NowUtc);
        var created = await repository.CreateSessionAsync(session, cancellationToken).ConfigureAwait(false);
        return From(created);
    }

    public async Task<FeatureBuilderManagementResult> GenerateAsync(
        GenerateFeatureBuilderProposalCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!FeatureBuilderPolicy.IsSessionId(command.SessionId) ||
            !FeatureBuilderPolicy.IsGenerationId(command.GenerationId) ||
            command.ExpectedSessionRevision is < 0 or > int.MaxValue - 2 ||
            !IsUtc(command.NowUtc) ||
            !IsUtc(command.DeadlineUtc) ||
            command.DeadlineUtc <= command.NowUtc ||
            command.DeadlineUtc - command.NowUtc > TimeSpan.FromSeconds(60))
            return Invalid("feature_builder.generation.command_invalid");
        var inventoryValidation = FeatureBuilderHashPolicy.ValidateInventory(command.Inventory);
        if (!inventoryValidation.IsValid) return Invalid(inventoryValidation.ErrorCode!);

        var session = await repository.GetSessionAsync(command.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return NotFound("feature_builder.session.not_found");
        if (session.Revision != command.ExpectedSessionRevision)
            return Stale("feature_builder.session.revision_changed");
        if (session.State is not (FeatureBuilderSessionState.Collecting or
            FeatureBuilderSessionState.AwaitingInformation or
            FeatureBuilderSessionState.Interrupted) ||
            !FeatureBuilderPolicy.CanTransition(session.State, FeatureBuilderSessionState.Generating))
            return NotReady("feature_builder.session.not_collecting");
        var inventoryHash = FeatureBuilderHashPolicy.ComputeInventoryHash(command.Inventory);
        if (!inventoryHash.Equals(session.InventoryHash))
            return Stale("feature_builder.inventory.changed");

        IReadOnlyList<FeatureBuilderAnswer> answers;
        if (session.State == FeatureBuilderSessionState.AwaitingInformation)
        {
            var answerValidation = FeatureBuilderPolicy.ValidateSubmittedAnswers(
                session.Answers,
                session.Questions,
                command.Answers);
            if (!answerValidation.IsValid) return Invalid(answerValidation.ErrorCode!);
            answers = Array.AsReadOnly(session.Answers.Concat(command.Answers).ToArray());
        }
        else
        {
            if (command.Answers.Count != 0)
                return Invalid("feature_builder.answers.unexpected");
            answers = session.Answers;
        }

        var generating = session with
        {
            State = FeatureBuilderSessionState.Generating,
            Revision = session.Revision + 1,
            Answers = answers,
            Questions = [],
            ActiveGenerationId = command.GenerationId,
            FixedErrorCode = null,
            UpdatedAtUtc = command.NowUtc,
        };
        var began = await repository.ReplaceSessionAsync(
            session.Revision,
            generating,
            cancellationToken).ConfigureAwait(false);
        if (began.Outcome != FeatureBuilderManagementOutcome.Succeeded)
            return From(began);

        var requestHash = FeatureBuilderHashPolicy.ComputeRequestHash(
            command.GenerationId,
            generating,
            command.DeadlineUtc,
            command.Inventory);
        var request = new FeatureBuilderRuntimeRequest(
            1,
            command.GenerationId,
            requestHash,
            generating.SessionId,
            generating.Revision,
            inventoryHash,
            command.DeadlineUtc,
            generating.Requirement,
            generating.Answers,
            command.Inventory);
        FeatureBuilderRuntimeResult response;
        try
        {
            response = await runtime.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await MarkInterruptedAsync(
                generating,
                "feature_builder.generation.interrupted",
                command.NowUtc).ConfigureAwait(false);
        }
        catch
        {
            return await MarkInterruptedAsync(
                generating,
                "feature_builder.runtime.failed",
                command.NowUtc).ConfigureAwait(false);
        }

        var responseValidation = ValidateResponse(request, response);
        if (!responseValidation.IsValid)
            return await MarkInterruptedAsync(
                generating,
                responseValidation.ErrorCode!,
                command.NowUtc).ConfigureAwait(false);

        return response.Outcome switch
        {
            FeatureBuilderRuntimeOutcome.NeedsInformation => await CompleteQuestionsAsync(
                generating, response, command.NowUtc, cancellationToken).ConfigureAwait(false),
            FeatureBuilderRuntimeOutcome.Refused => await CompleteRefusalAsync(
                generating, response, command.NowUtc, cancellationToken).ConfigureAwait(false),
            FeatureBuilderRuntimeOutcome.Proposal => await CompleteProposalAsync(
                generating, response, command.NowUtc, cancellationToken).ConfigureAwait(false),
            _ => await MarkInterruptedAsync(
                generating,
                "feature_builder.runtime.response_invalid",
                command.NowUtc).ConfigureAwait(false),
        };
    }

    public async Task<FeatureBuilderManagementResult> ApplyProposalAsync(
        ApplyFeatureBuilderProposalCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!FeatureBuilderPolicy.IsProposalId(command.ProposalId) ||
            command.ExpectedProposalRevision < 0 ||
            !IsDraftId(command.DraftId) ||
            !IsUtc(command.NowUtc))
            return Invalid("feature_builder.apply.command_invalid");
        var proposal = await repository.GetProposalAsync(command.ProposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null) return NotFound("feature_builder.proposal.not_found");
        if (proposal.Revision != command.ExpectedProposalRevision)
            return Stale("feature_builder.proposal.revision_changed");
        if (proposal.State != FeatureBuilderProposalState.Pending ||
            proposal.Candidate is null || proposal.CandidateContentHash is null)
            return NotReady("feature_builder.proposal.not_pending");
        var session = await repository.GetSessionAsync(proposal.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return NotFound("feature_builder.session.not_found");
        if (session.State != FeatureBuilderSessionState.ProposalReady)
            return NotReady("feature_builder.session.not_proposal_ready");

        var runs = await repository.ListTestRunsAsync(proposal.ProposalId, cancellationToken).ConfigureAwait(false);
        var evidence = SelectPassingEvidence(runs, proposal.CandidateContentHash);
        if (evidence is null) return NotReady("feature_builder.proposal.tests_not_ready");
        var evidenceHash = FeatureBuilderHashPolicy.ComputeTestEvidenceHash(evidence);
        var draft = new ValidatedCapabilityDraft(
            command.DraftId,
            proposal.Candidate,
            proposal.CandidateContentHash,
            proposal.BaseVersion,
            "builder",
            proposal.ProposalHash);
        var applied = await repository.ApplyProposalAsReadyDraftAsync(
            new FeatureBuilderRepositoryApplyCommand(
                proposal.ProposalId,
                proposal.Revision,
                session.Revision,
                draft,
                evidenceHash,
                command.NowUtc),
            cancellationToken).ConfigureAwait(false);
        if (applied.Outcome == FeatureBuilderManagementOutcome.Succeeded &&
            (applied.Draft is null || applied.Draft.State != CapabilityDraftState.ReadyForReview ||
             !applied.Draft.ExactVersion.Equals(draft.ExactVersion) ||
             !Nullable.Equals(applied.Draft.Parent, proposal.BaseVersion)))
            return Invalid("feature_builder.apply.repository_contract_invalid");
        return From(applied);
    }

    public async Task<FeatureBuilderManagementResult> SetProposalDispositionAsync(
        SetFeatureBuilderProposalDispositionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!FeatureBuilderPolicy.IsProposalId(command.ProposalId) ||
            command.ExpectedProposalRevision < 0 ||
            command.State is FeatureBuilderProposalState.Pending or FeatureBuilderProposalState.Applied ||
            !FeatureBuilderPolicy.IsFixedCode(command.ReasonCode) ||
            !IsUtc(command.NowUtc))
            return Invalid("feature_builder.proposal.disposition_invalid");
        var proposal = await repository.GetProposalAsync(command.ProposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null) return NotFound("feature_builder.proposal.not_found");
        if (proposal.Revision != command.ExpectedProposalRevision)
            return Stale("feature_builder.proposal.revision_changed");
        if (!FeatureBuilderPolicy.CanTransition(proposal.State, command.State))
            return NotReady("feature_builder.proposal.terminal");
        var session = await repository.GetSessionAsync(proposal.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return NotFound("feature_builder.session.not_found");
        if (session.State != FeatureBuilderSessionState.ProposalReady ||
            !FeatureBuilderPolicy.CanTransition(session.State, FeatureBuilderSessionState.Completed))
            return NotReady("feature_builder.session.not_proposal_ready");
        var replacementProposal = proposal with
        {
            State = command.State,
            Revision = proposal.Revision + 1,
            FixedIssueCode = command.ReasonCode,
            UpdatedAtUtc = command.NowUtc,
            TerminalAtUtc = command.NowUtc,
        };
        var replacementSession = session with
        {
            State = FeatureBuilderSessionState.Completed,
            Revision = session.Revision + 1,
            ActiveGenerationId = null,
            FixedErrorCode = command.ReasonCode,
            UpdatedAtUtc = command.NowUtc,
        };
        var result = await repository.ReplaceProposalAndSessionAsync(
            proposal.Revision,
            replacementProposal,
            session.Revision,
            replacementSession,
            cancellationToken).ConfigureAwait(false);
        return From(result);
    }

    public async Task<FeatureBuilderManagementResult> CancelSessionAsync(
        CancelFeatureBuilderSessionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!FeatureBuilderPolicy.IsSessionId(command.SessionId) ||
            command.ExpectedSessionRevision < 0 || !IsUtc(command.NowUtc))
            return Invalid("feature_builder.cancel.command_invalid");
        var session = await repository.GetSessionAsync(command.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return NotFound("feature_builder.session.not_found");
        if (session.Revision != command.ExpectedSessionRevision)
            return Stale("feature_builder.session.revision_changed");
        if (!FeatureBuilderPolicy.CanTransition(session.State, FeatureBuilderSessionState.Cancelled))
            return NotReady("feature_builder.session.terminal");
        var replacement = session with
        {
            State = FeatureBuilderSessionState.Cancelled,
            Revision = session.Revision + 1,
            Questions = [],
            ActiveGenerationId = null,
            FixedErrorCode = "feature_builder.session.cancelled",
            UpdatedAtUtc = command.NowUtc,
        };
        return From(await repository.ReplaceSessionAsync(
            session.Revision, replacement, cancellationToken).ConfigureAwait(false));
    }

    public async Task<FeatureBuilderManagementResult> StartTestRunAsync(
        StartFeatureBuilderTestRunCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!FeatureBuilderPolicy.IsTestRunId(command.TestRunId) ||
            !FeatureBuilderPolicy.IsProposalId(command.ProposalId) ||
            !Enum.IsDefined(command.Kind) ||
            command.Attempt is < 1 or > 16 ||
            command.EngineVersion.Length is < 1 or > 64 ||
            !command.EngineVersion.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_') ||
            !IsUtc(command.NowUtc))
            return Invalid("feature_builder.test.command_invalid");
        var proposal = await repository.GetProposalAsync(command.ProposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null) return NotFound("feature_builder.proposal.not_found");
        if (proposal.State != FeatureBuilderProposalState.Pending || proposal.CandidateContentHash is null)
            return NotReady("feature_builder.proposal.not_pending");
        var run = new FeatureBuilderTestRun(
            command.TestRunId,
            proposal.ProposalId,
            command.Kind,
            command.Attempt,
            proposal.CandidateContentHash,
            command.EngineVersion,
            FeatureBuilderTestRunState.Running,
            0,
            "{}",
            null,
            command.NowUtc,
            null);
        return From(await repository.CreateTestRunAsync(run, cancellationToken).ConfigureAwait(false));
    }

    public async Task<FeatureBuilderManagementResult> CompleteTestRunAsync(
        CompleteFeatureBuilderTestRunCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!FeatureBuilderPolicy.IsTestRunId(command.TestRunId) ||
            command.ExpectedRevision < 0 ||
            command.State is FeatureBuilderTestRunState.Running ||
            !IsTestResult(command.CanonicalResultJson) ||
            command.State == FeatureBuilderTestRunState.Passed && command.FixedErrorCode is not null ||
            command.State != FeatureBuilderTestRunState.Passed && !FeatureBuilderPolicy.IsFixedCode(command.FixedErrorCode) ||
            !IsUtc(command.NowUtc))
            return Invalid("feature_builder.test.completion_invalid");
        var run = await repository.GetTestRunAsync(command.TestRunId, cancellationToken).ConfigureAwait(false);
        if (run is null) return NotFound("feature_builder.test.not_found");
        if (run.Revision != command.ExpectedRevision)
            return Stale("feature_builder.test.revision_changed");
        if (!FeatureBuilderPolicy.CanTransition(run.State, command.State))
            return NotReady("feature_builder.test.terminal");
        var replacement = run with
        {
            State = command.State,
            Revision = run.Revision + 1,
            CanonicalResultJson = command.CanonicalResultJson,
            FixedErrorCode = command.FixedErrorCode,
            CompletedAtUtc = command.NowUtc,
        };
        return From(await repository.ReplaceTestRunAsync(
            run.Revision, replacement, cancellationToken).ConfigureAwait(false));
    }

    private async Task<FeatureBuilderManagementResult> CompleteQuestionsAsync(
        FeatureBuilderSession generating,
        FeatureBuilderRuntimeResult response,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var replacement = generating with
        {
            State = FeatureBuilderSessionState.AwaitingInformation,
            Revision = generating.Revision + 1,
            Questions = response.Questions!,
            ActiveGenerationId = null,
            UpdatedAtUtc = nowUtc,
        };
        return From(await repository.ReplaceSessionAsync(
            generating.Revision, replacement, cancellationToken).ConfigureAwait(false));
    }

    private async Task<FeatureBuilderManagementResult> CompleteRefusalAsync(
        FeatureBuilderSession generating,
        FeatureBuilderRuntimeResult response,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var replacement = generating with
        {
            State = FeatureBuilderSessionState.Completed,
            Revision = generating.Revision + 1,
            ActiveGenerationId = null,
            FixedErrorCode = response.RefusalCode,
            UpdatedAtUtc = nowUtc,
        };
        var persisted = await repository.ReplaceSessionAsync(
            generating.Revision, replacement, cancellationToken).ConfigureAwait(false);
        return persisted.Outcome == FeatureBuilderManagementOutcome.Succeeded
            ? new(FeatureBuilderManagementOutcome.Refused, persisted.Session ?? replacement, ErrorCode: response.RefusalCode)
            : From(persisted);
    }

    private async Task<FeatureBuilderManagementResult> CompleteProposalAsync(
        FeatureBuilderSession generating,
        FeatureBuilderRuntimeResult response,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var scanned = scanner.Scan(response.Artifacts!, generating.RevisionTarget);
        var responseHash = scanned.IsValid
            ? FeatureBuilderHashPolicy.ComputeResponseHash(response)
            : FeatureBuilderHashPolicy.ComputeQuarantinedResponseHash(
                response,
                scanned.ErrorCode ?? "feature_builder.proposal.invalid");
        var proposalHash = FeatureBuilderHashPolicy.ComputeProposalHash(
            generating.ActiveGenerationId!,
            response.RequestHash,
            responseHash,
            scanned.CandidateContentHash,
            generating.RevisionTarget?.BaseVersion,
            generating.ExpectedCatalogRevision);
        var quarantined = !scanned.IsValid;
        var proposal = new FeatureBuilderProposal(
            FeatureBuilderHashPolicy.CreateProposalId(proposalHash),
            generating.SessionId,
            generating.Revision,
            generating.ActiveGenerationId!,
            response.RequestHash,
            responseHash,
            response.InventoryHash,
            generating.RevisionTarget?.BaseVersion,
            generating.ExpectedCatalogRevision,
            scanned.CandidateContentHash,
            proposalHash,
            scanned.Candidate,
            response.Limitations!,
            null,
            quarantined ? FeatureBuilderProposalState.Quarantined : FeatureBuilderProposalState.Pending,
            0,
            scanned.ErrorCode,
            null,
            nowUtc,
            nowUtc,
            quarantined ? nowUtc : null);
        var replacementSession = generating with
        {
            State = quarantined ? FeatureBuilderSessionState.Completed : FeatureBuilderSessionState.ProposalReady,
            Revision = generating.Revision + 1,
            ActiveGenerationId = null,
            FixedErrorCode = scanned.ErrorCode,
            UpdatedAtUtc = nowUtc,
        };
        var persisted = await repository.CreateProposalAsync(
            proposal,
            generating.Revision,
            replacementSession,
            cancellationToken).ConfigureAwait(false);
        if (persisted.Outcome != FeatureBuilderManagementOutcome.Succeeded) return From(persisted);
        return new(
            quarantined ? FeatureBuilderManagementOutcome.Quarantined : FeatureBuilderManagementOutcome.Succeeded,
            persisted.Session ?? replacementSession,
            persisted.Proposal ?? proposal,
            ErrorCode: scanned.ErrorCode);
    }

    private async Task<FeatureBuilderManagementResult> MarkInterruptedAsync(
        FeatureBuilderSession generating,
        string errorCode,
        DateTimeOffset nowUtc)
    {
        var replacement = generating with
        {
            State = FeatureBuilderSessionState.Interrupted,
            Revision = generating.Revision + 1,
            ActiveGenerationId = null,
            FixedErrorCode = errorCode,
            UpdatedAtUtc = nowUtc,
        };
        var persisted = await repository.ReplaceSessionAsync(
            generating.Revision,
            replacement,
            CancellationToken.None).ConfigureAwait(false);
        return persisted.Outcome == FeatureBuilderManagementOutcome.Succeeded
            ? new(FeatureBuilderManagementOutcome.Interrupted, persisted.Session ?? replacement, ErrorCode: errorCode)
            : From(persisted);
    }

    private static FeatureBuilderValidationResult ValidateResponse(
        FeatureBuilderRuntimeRequest request,
        FeatureBuilderRuntimeResult response)
    {
        if (response is null || response.SchemaVersion != 1 ||
            !StringComparer.Ordinal.Equals(response.GenerationId, request.GenerationId) ||
            !response.RequestHash.Equals(request.RequestHash) ||
            !StringComparer.Ordinal.Equals(response.SessionId, request.SessionId) ||
            response.SessionRevision != request.SessionRevision ||
            !response.InventoryHash.Equals(request.InventoryHash) ||
            !response.DeadlineUtc.EqualsExact(request.DeadlineUtc))
            return FeatureBuilderValidationResult.Invalid("feature_builder.runtime.response_mismatch");

        var limitationValidation = response.Limitations is null
            ? FeatureBuilderValidationResult.Valid
            : FeatureBuilderPolicy.ValidateLimitations(response.Limitations);
        if (!limitationValidation.IsValid || response.Limitations?.Any(value =>
                FeatureBuilderPolicy.ContainsDirectSecret(value, sensitiveQuestion: false)) == true)
            return FeatureBuilderValidationResult.Invalid("feature_builder.runtime.response_invalid");
        return response.Outcome switch
        {
            FeatureBuilderRuntimeOutcome.NeedsInformation
                when response.Artifacts is null && response.RefusalCode is null &&
                     response.Questions is not null &&
                     AreFollowUpQuestionsValid(request.Answers, response.Questions) =>
                FeatureBuilderValidationResult.Valid,
            FeatureBuilderRuntimeOutcome.Proposal
                when response.Questions is null && response.RefusalCode is null &&
                     response.Artifacts is { Count: >= 1 and <= 6 } && response.Limitations is not null =>
                FeatureBuilderValidationResult.Valid,
            FeatureBuilderRuntimeOutcome.Refused
                when response.Questions is null && response.Artifacts is null &&
                     response.Limitations is { Count: > 0 } && response.RefusalCode is not null &&
                     RefusalCodes.Contains(response.RefusalCode) =>
                FeatureBuilderValidationResult.Valid,
            _ => FeatureBuilderValidationResult.Invalid("feature_builder.runtime.response_invalid"),
        };
    }

    private static bool AreFollowUpQuestionsValid(
        IReadOnlyList<FeatureBuilderAnswer> existingAnswers,
        IReadOnlyList<FeatureBuilderQuestion> questions)
    {
        if (!FeatureBuilderPolicy.ValidateQuestions(questions).IsValid ||
            existingAnswers.Count + questions.Count > FeatureBuilderPolicy.MaximumAnswers)
            return false;

        var answeredQuestionIds = existingAnswers
            .Select(answer => answer.QuestionId)
            .ToHashSet(StringComparer.Ordinal);
        return questions.All(question => !answeredQuestionIds.Contains(question.QuestionId));
    }

    private static IReadOnlyList<FeatureBuilderTestRun>? SelectPassingEvidence(
        IReadOnlyList<FeatureBuilderTestRun> runs,
        CapabilityContentHash candidateContentHash)
    {
        var selected = new List<FeatureBuilderTestRun>(2);
        foreach (var kind in new[] { FeatureBuilderTestRunKind.StaticScan, FeatureBuilderTestRunKind.DryRun })
        {
            var latest = runs.Where(run => run.Kind == kind).OrderByDescending(run => run.Attempt).FirstOrDefault();
            if (latest is null || latest.State != FeatureBuilderTestRunState.Passed ||
                !latest.CandidateContentHash.Equals(candidateContentHash))
                return null;
            selected.Add(latest);
        }
        return selected.AsReadOnly();
    }

    private static bool IsTestResult(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32_768 || Encoding.UTF8.GetByteCount(value) > 32_768 ||
            FeatureBuilderPolicy.ContainsDirectSecret(value, sensitiveQuestion: false))
            return false;
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array &&
                   !CapabilityIngressPolicy.ContainsSensitiveJsonLiteral(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsDraftId(string value) =>
        value.Length is >= 1 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    private static FeatureBuilderManagementResult Invalid(string errorCode) =>
        new(FeatureBuilderManagementOutcome.Invalid, ErrorCode: errorCode);

    private static FeatureBuilderManagementResult NotFound(string errorCode) =>
        new(FeatureBuilderManagementOutcome.NotFound, ErrorCode: errorCode);

    private static FeatureBuilderManagementResult NotReady(string errorCode) =>
        new(FeatureBuilderManagementOutcome.NotReady, ErrorCode: errorCode);

    private static FeatureBuilderManagementResult Stale(string errorCode) =>
        new(FeatureBuilderManagementOutcome.Stale, ErrorCode: errorCode);

    private static FeatureBuilderManagementResult From(FeatureBuilderSessionWriteResult result) =>
        new(result.Outcome, result.Session, ErrorCode: result.ErrorCode);

    private static FeatureBuilderManagementResult From(FeatureBuilderProposalWriteResult result) =>
        new(result.Outcome, result.Session, result.Proposal, ErrorCode: result.ErrorCode);

    private static FeatureBuilderManagementResult From(FeatureBuilderTestRunWriteResult result) =>
        new(result.Outcome, TestRun: result.TestRun, ErrorCode: result.ErrorCode);

    private static FeatureBuilderManagementResult From(FeatureBuilderApplyWriteResult result) =>
        new(result.Outcome, result.Session, result.Proposal, result.Draft, ErrorCode: result.ErrorCode);
}
