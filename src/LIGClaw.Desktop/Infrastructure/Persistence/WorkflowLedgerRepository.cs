using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal enum WorkflowRunStartOutcome
{
    Created,
    Coalesced,
    Conflict,
}

internal enum WorkflowLedgerEffectClass
{
    Observation,
    Effect,
}

internal enum WorkflowStepCompletionDisposition
{
    RetryableFailed,
    NotStarted,
    Succeeded,
    FailedKnown,
    Unknown,
}

internal enum WorkflowRunTerminalStatus
{
    Completed,
    NeedsInformation,
    Failed,
    Cancelled,
    AttentionRequired,
}

internal enum WorkflowRunNextAction
{
    None,
    ProvideInformation,
    RetryFromStart,
    ReviewOutcome,
}

internal sealed record WorkflowRunStart(
    string RunId,
    string ConversationId,
    string ParentRunId,
    string ParentToolCallId,
    string IdempotencyKey,
    string SnapshotHash,
    string ModelToolName,
    string CapabilityId,
    string CapabilityVersion,
    long CatalogRevision,
    string CapabilityContentHash,
    string WorkflowHash,
    string InputHash,
    string PlanHash,
    string ActualMaximumRisk,
    DateTimeOffset CreatedAtUtc);

internal sealed record WorkflowRunRecord(
    string RunId,
    string ConversationId,
    string ParentRunId,
    string ParentToolCallId,
    string IdempotencyKey,
    string SnapshotHash,
    string ModelToolName,
    string CapabilityId,
    string CapabilityVersion,
    long CatalogRevision,
    string CapabilityContentHash,
    string WorkflowHash,
    string InputHash,
    string PlanHash,
    string ActualMaximumRisk,
    string Status,
    int CurrentSequence,
    string? FixedNextAction,
    string? FixedErrorCode,
    string? RedactedSummary,
    string? ResultHash,
    IReadOnlyList<string>? RequiredInputs,
    long Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

internal sealed record WorkflowRunStartResult(
    WorkflowRunStartOutcome Outcome,
    WorkflowRunRecord Run,
    string? ErrorCode = null);

internal sealed record WorkflowStepPlan(
    string WorkflowRunId,
    int Sequence,
    int Attempt,
    string StepId,
    string ToolCallId,
    string ToolName,
    string Risk,
    string RetryClass,
    WorkflowLedgerEffectClass EffectClass,
    string PayloadHash,
    string BindingHash,
    DateTimeOffset CreatedAtUtc,
    int MaximumRetries = 0);

internal sealed record WorkflowAuthorizationTicketItemDraft(
    string ItemId,
    int Sequence,
    string NonceHash,
    string ToolName,
    string Risk,
    string PayloadHash,
    string BindingHash);

internal sealed record WorkflowAuthorizationTicketDraft(
    string TicketId,
    string WorkflowRunId,
    string ActualMaximumRisk,
    DateTimeOffset ApprovedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<WorkflowAuthorizationTicketItemDraft> Items,
    long ExpectedRunRevision = 0,
    int ExpectedCurrentSequence = -1);

internal sealed record WorkflowEffectReservation(
    string WorkflowRunId,
    string ToolCallId,
    string TicketId,
    string TicketItemId,
    int Sequence,
    string NonceHash,
    string ToolName,
    string Risk,
    string PayloadHash,
    string BindingHash,
    long ExpectedRunRevision,
    int ExpectedCurrentSequence,
    DateTimeOffset StartedAtUtc);

internal sealed record WorkflowObservationReservation(
    string WorkflowRunId,
    string ToolCallId,
    int Sequence,
    string ToolName,
    string Risk,
    string RetryClass,
    string PayloadHash,
    string BindingHash,
    long ExpectedRunRevision,
    int ExpectedCurrentSequence,
    DateTimeOffset StartedAtUtc);

internal sealed record WorkflowStepCompletion(
    string WorkflowRunId,
    string ToolCallId,
    WorkflowStepCompletionDisposition Disposition,
    string? OutputHash,
    string? RedactedSummary,
    string? FixedErrorCode,
    long ExpectedRunRevision,
    DateTimeOffset CompletedAtUtc);

internal sealed record WorkflowPlannedStepAbandonment(
    string WorkflowRunId,
    string ToolCallId,
    string FixedErrorCode,
    long ExpectedRunRevision,
    int ExpectedCurrentSequence,
    DateTimeOffset AbandonedAtUtc,
    string? RedactedSummary = null);

internal sealed record WorkflowRunCompletion(
    string WorkflowRunId,
    WorkflowRunTerminalStatus Status,
    WorkflowRunNextAction NextAction,
    string? FixedErrorCode,
    string? RedactedSummary,
    string? ResultHash,
    long ExpectedRunRevision,
    int ExpectedCurrentSequence,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<string>? RequiredInputs = null);

internal sealed record WorkflowStepRunRecord(
    string ToolCallId,
    string WorkflowRunId,
    int Sequence,
    int Attempt,
    string StepId,
    string ToolName,
    string Risk,
    string RetryClass,
    int MaximumRetries,
    string EffectClass,
    string PayloadHash,
    string BindingHash,
    string? TicketItemId,
    string Status,
    string? Disposition,
    string? OutputHash,
    string? RedactedSummary,
    string? FixedErrorCode,
    long Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

internal sealed record WorkflowAuthorizationTicketRecord(
    string TicketId,
    string WorkflowRunId,
    string Status,
    string ActualMaximumRisk,
    DateTimeOffset ApprovedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? ConsumedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string? RevocationCode,
    long Revision);

internal sealed record WorkflowAuthorizationTicketItemRecord(
    string ItemId,
    string TicketId,
    string WorkflowRunId,
    int Sequence,
    string NonceHash,
    string ToolName,
    string Risk,
    string PayloadHash,
    string BindingHash,
    string Status,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? ConsumedAtUtc,
    string? ConsumedByToolCallId,
    DateTimeOffset? RevokedAtUtc,
    string? RevocationCode,
    long Revision);

internal sealed record WorkflowStartupRecoveryResult(
    int RevokedTickets,
    int RevokedTicketItems,
    int InterruptedObservations,
    int UnknownEffects,
    int RecoveredRuns);

internal sealed class WorkflowLedgerRepository
{
    private const int MaximumTokenLength = 256;
    private const int MaximumSummaryLength = 512;
    private const int MaximumTicketItems = 20;
    private static readonly TimeSpan MaximumTicketLifetime = TimeSpan.FromMinutes(9);
    private readonly ConversationDatabase database;
    private readonly TimeProvider timeProvider;

    public WorkflowLedgerRepository(ConversationDatabase database, TimeProvider? timeProvider = null)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkflowRunStartResult> StartOrCoalesceRunAsync(
        WorkflowRunStart request,
        CancellationToken cancellationToken)
    {
        ValidateRunStart(request);
        WorkflowRunStartResult? result = null;
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var existing = await ReadRunByIdempotencyAsync(
                connection,
                (SqliteTransaction)transaction,
                request.ParentRunId,
                request.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                result = ExactIdentityMatches(existing, request)
                    ? new WorkflowRunStartResult(WorkflowRunStartOutcome.Coalesced, existing)
                    : new WorkflowRunStartResult(
                        WorkflowRunStartOutcome.Conflict,
                        existing,
                        "workflow.idempotency_conflict");
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var existingParentToolCall = await ReadRunByParentToolCallAsync(
                connection,
                (SqliteTransaction)transaction,
                request.ParentRunId,
                request.ParentToolCallId,
                cancellationToken).ConfigureAwait(false);
            if (existingParentToolCall is not null)
            {
                result = new WorkflowRunStartResult(
                    WorkflowRunStartOutcome.Conflict,
                    existingParentToolCall,
                    "workflow.idempotency_conflict");
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var runIdCollision = await ReadRunAsync(
                connection,
                (SqliteTransaction)transaction,
                request.RunId,
                cancellationToken).ConfigureAwait(false);
            if (runIdCollision is not null)
            {
                result = new WorkflowRunStartResult(
                    WorkflowRunStartOutcome.Conflict,
                    runIdCollision,
                    "workflow.run_id_conflict");
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await ExecuteAsync(
                connection,
                """
                INSERT INTO workflow_runs(
                    id, conversation_id, parent_run_id, parent_tool_call_id, idempotency_key,
                    snapshot_hash, model_tool_name,
                    capability_id, capability_version, catalog_revision, capability_content_hash,
                    workflow_hash, input_hash, plan_hash, actual_maximum_risk, status, current_sequence,
                    revision, created_at_utc, updated_at_utc)
                VALUES (
                    $id, $conversationId, $parentRunId, $parentToolCallId, $idempotencyKey,
                    $snapshotHash, $modelToolName,
                    $capabilityId, $capabilityVersion, $catalogRevision, $capabilityContentHash,
                    $workflowHash, $inputHash, $planHash, $actualMaximumRisk, 'planned', -1,
                    0, $createdAtUtc, $createdAtUtc);
                """,
                cancellationToken,
                transaction,
                ("$id", request.RunId),
                ("$conversationId", request.ConversationId),
                ("$parentRunId", request.ParentRunId),
                ("$parentToolCallId", request.ParentToolCallId),
                ("$idempotencyKey", request.IdempotencyKey),
                ("$snapshotHash", request.SnapshotHash),
                ("$modelToolName", request.ModelToolName),
                ("$capabilityId", request.CapabilityId),
                ("$capabilityVersion", request.CapabilityVersion),
                ("$catalogRevision", request.CatalogRevision),
                ("$capabilityContentHash", request.CapabilityContentHash),
                ("$workflowHash", request.WorkflowHash),
                ("$inputHash", request.InputHash),
                ("$planHash", request.PlanHash),
                ("$actualMaximumRisk", request.ActualMaximumRisk),
                ("$createdAtUtc", Format(request.CreatedAtUtc))).ConfigureAwait(false);
            var created = await ReadRunAsync(
                connection,
                (SqliteTransaction)transaction,
                request.RunId,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Workflow run 저장 결과를 읽을 수 없습니다.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            result = new WorkflowRunStartResult(WorkflowRunStartOutcome.Created, created);
        }, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("Workflow run 시작 결과가 없습니다.");
    }

    public async Task<bool> AddPlannedStepAsync(
        WorkflowStepPlan step,
        CancellationToken cancellationToken)
    {
        ValidateStepPlan(step);
        var inserted = false;
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var run = await ReadRunAsync(
                connection,
                (SqliteTransaction)transaction,
                step.WorkflowRunId,
                cancellationToken).ConfigureAwait(false);
            if (run is null || run.Status is not ("planned" or "awaiting_approval" or "running"))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            if (RiskOrdinal(step.Risk) > RiskOrdinal(run.ActualMaximumRisk))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            var existingToolCall = await ReadStepAsync(
                connection,
                (SqliteTransaction)transaction,
                step.ToolCallId,
                cancellationToken).ConfigureAwait(false);
            if (existingToolCall is not null)
            {
                inserted = ExactStepPlanMatches(existingToolCall, step);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!await CanAddStepAttemptAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    step,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            inserted = await ExecuteCountAsync(
                connection,
                """
                INSERT INTO workflow_step_runs(
                    tool_call_id, workflow_run_id, step_sequence, attempt, step_id,
                    tool_name, risk, retry_class, maximum_retries, effect_class, payload_hash, binding_hash,
                    status, revision, created_at_utc)
                VALUES (
                    $toolCallId, $workflowRunId, $sequence, $attempt, $stepId,
                    $toolName, $risk, $retryClass, $maximumRetries, $effectClass, $payloadHash, $bindingHash,
                    'planned', 0, $createdAtUtc)
                ON CONFLICT DO NOTHING;
                """,
                cancellationToken,
                transaction,
                parameters:
                [
                    ("$toolCallId", step.ToolCallId),
                    ("$workflowRunId", step.WorkflowRunId),
                    ("$sequence", step.Sequence),
                    ("$attempt", step.Attempt),
                    ("$stepId", step.StepId),
                    ("$toolName", step.ToolName),
                    ("$risk", step.Risk),
                    ("$retryClass", step.RetryClass),
                    ("$maximumRetries", step.MaximumRetries),
                    ("$effectClass", EffectClassValue(step.EffectClass)),
                    ("$payloadHash", step.PayloadHash),
                    ("$bindingHash", step.BindingHash),
                    ("$createdAtUtc", Format(step.CreatedAtUtc)),
                ]).ConfigureAwait(false) == 1;
            if (!inserted)
            {
                var existing = await ReadStepAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    step.ToolCallId,
                    cancellationToken).ConfigureAwait(false);
                inserted = existing is not null && ExactStepPlanMatches(existing, step);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    public async Task<bool> SaveAuthorizationTicketAsync(
        WorkflowAuthorizationTicketDraft ticket,
        CancellationToken cancellationToken)
    {
        ValidateTicket(ticket);
        var saved = false;
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (await ReadTicketAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    ticket.TicketId,
                    cancellationToken).ConfigureAwait(false) is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var run = await ReadRunAsync(
                connection,
                (SqliteTransaction)transaction,
                ticket.WorkflowRunId,
                cancellationToken).ConfigureAwait(false);
            if (run is null || run.Status is not ("planned" or "awaiting_approval" or "running") ||
                run.Revision != ticket.ExpectedRunRevision ||
                run.CurrentSequence != ticket.ExpectedCurrentSequence)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!StringComparer.Ordinal.Equals(ticket.ActualMaximumRisk, run.ActualMaximumRisk))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await ExecuteAsync(
                connection,
                """
                INSERT INTO workflow_authorization_tickets(
                    id, conversation_id, parent_run_id, workflow_run_id,
                    snapshot_hash, model_tool_name,
                    capability_id, capability_version, catalog_revision, capability_content_hash,
                    workflow_hash, input_hash, plan_hash, actual_maximum_risk,
                    approval_scope, status, approved_at_utc, expires_at_utc, revision)
                VALUES (
                    $id, $conversationId, $parentRunId, $workflowRunId,
                    $snapshotHash, $modelToolName,
                    $capabilityId, $capabilityVersion, $catalogRevision, $capabilityContentHash,
                    $workflowHash, $inputHash, $planHash, $actualMaximumRisk,
                    'allow_once', 'active', $approvedAtUtc, $expiresAtUtc, 0);
                """,
                cancellationToken,
                transaction,
                ("$id", ticket.TicketId),
                ("$conversationId", run.ConversationId),
                ("$parentRunId", run.ParentRunId),
                ("$workflowRunId", run.RunId),
                ("$snapshotHash", run.SnapshotHash),
                ("$modelToolName", run.ModelToolName),
                ("$capabilityId", run.CapabilityId),
                ("$capabilityVersion", run.CapabilityVersion),
                ("$catalogRevision", run.CatalogRevision),
                ("$capabilityContentHash", run.CapabilityContentHash),
                ("$workflowHash", run.WorkflowHash),
                ("$inputHash", run.InputHash),
                ("$planHash", run.PlanHash),
                ("$actualMaximumRisk", ticket.ActualMaximumRisk),
                ("$approvedAtUtc", Format(ticket.ApprovedAtUtc)),
                ("$expiresAtUtc", Format(ticket.ExpiresAtUtc))).ConfigureAwait(false);

            foreach (var item in ticket.Items)
            {
                if (RiskOrdinal(item.Risk) > RiskOrdinal(run.ActualMaximumRisk) ||
                    !await HasMatchingPlannedEffectAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        run.RunId,
                        item,
                        cancellationToken).ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO workflow_authorization_ticket_items(
                        id, ticket_id, conversation_id, parent_run_id, workflow_run_id,
                        step_sequence, nonce_hash, tool_name, risk, payload_hash, binding_hash,
                        status, expires_at_utc, revision)
                    VALUES (
                        $id, $ticketId, $conversationId, $parentRunId, $workflowRunId,
                        $sequence, $nonceHash, $toolName, $risk, $payloadHash, $bindingHash,
                        'ready', $expiresAtUtc, 0);
                    """,
                    cancellationToken,
                    transaction,
                    ("$id", item.ItemId),
                    ("$ticketId", ticket.TicketId),
                    ("$conversationId", run.ConversationId),
                    ("$parentRunId", run.ParentRunId),
                    ("$workflowRunId", run.RunId),
                    ("$sequence", item.Sequence),
                    ("$nonceHash", item.NonceHash),
                    ("$toolName", item.ToolName),
                    ("$risk", item.Risk),
                    ("$payloadHash", item.PayloadHash),
                    ("$bindingHash", item.BindingHash),
                    ("$expiresAtUtc", Format(ticket.ExpiresAtUtc))).ConfigureAwait(false);
            }

            var changedRun = await ExecuteCountAsync(
                connection,
                """
                UPDATE workflow_runs
                SET status = 'running', revision = revision + 1, updated_at_utc = $approvedAtUtc
                WHERE id = $workflowRunId
                  AND status IN ('planned', 'awaiting_approval', 'running')
                  AND revision = $expectedRunRevision
                  AND current_sequence = $expectedCurrentSequence;
                """,
                cancellationToken,
                transaction,
                ("$approvedAtUtc", Format(ticket.ApprovedAtUtc)),
                ("$workflowRunId", run.RunId),
                ("$expectedRunRevision", ticket.ExpectedRunRevision),
                ("$expectedCurrentSequence", ticket.ExpectedCurrentSequence)).ConfigureAwait(false);
            if (changedRun != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            saved = true;
        }, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public async Task<bool> TryStartObservationStepAsync(
        WorkflowObservationReservation reservation,
        CancellationToken cancellationToken)
    {
        ValidateObservationReservation(reservation);
        var started = false;
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var changedStep = await ExecuteCountAsync(
                connection,
                """
                UPDATE workflow_step_runs
                SET status = 'executing', started_at_utc = $startedAtUtc, revision = revision + 1
                WHERE tool_call_id = $toolCallId
                  AND workflow_run_id = $workflowRunId
                  AND step_sequence = $sequence
                  AND tool_name = $toolName
                  AND risk = $risk
                  AND retry_class = $retryClass
                  AND payload_hash = $payloadHash
                  AND binding_hash = $bindingHash
                  AND effect_class = 'observation'
                  AND status = 'planned';
                """,
                cancellationToken,
                transaction,
                ("$startedAtUtc", Format(reservation.StartedAtUtc)),
                ("$toolCallId", reservation.ToolCallId),
                ("$workflowRunId", reservation.WorkflowRunId),
                ("$sequence", reservation.Sequence),
                ("$toolName", reservation.ToolName),
                ("$risk", reservation.Risk),
                ("$retryClass", reservation.RetryClass),
                ("$payloadHash", reservation.PayloadHash),
                ("$bindingHash", reservation.BindingHash)).ConfigureAwait(false);
            if (changedStep != 1 ||
                await UpdateRunningRunAsync(
                    connection,
                    transaction,
                    reservation.WorkflowRunId,
                    reservation.ToolCallId,
                    reservation.Sequence,
                    reservation.ExpectedRunRevision,
                    reservation.ExpectedCurrentSequence,
                    reservation.StartedAtUtc,
                    cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            started = true;
        }, cancellationToken).ConfigureAwait(false);
        return started;
    }

    public async Task<bool> TryReserveEffectStepAsync(
        WorkflowEffectReservation reservation,
        CancellationToken cancellationToken)
    {
        ValidateReservation(reservation);
        var reserved = false;
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var trustedStartedAtUtc = timeProvider.GetUtcNow();
            var consumedItem = await ExecuteCountAsync(
                connection,
                """
                UPDATE workflow_authorization_ticket_items
                SET status = 'consumed', consumed_at_utc = $startedAtUtc,
                    consumed_by_tool_call_id = $toolCallId, revision = revision + 1
                WHERE id = $ticketItemId
                  AND ticket_id = $ticketId
                  AND workflow_run_id = $workflowRunId
                  AND step_sequence = $sequence
                  AND nonce_hash = $nonceHash
                  AND tool_name = $toolName
                  AND risk = $risk
                  AND payload_hash = $payloadHash
                  AND binding_hash = $bindingHash
                  AND status = 'ready'
                  AND expires_at_utc > $startedAtUtc
                  AND EXISTS (
                      SELECT 1
                      FROM workflow_step_runs AS current
                      WHERE current.tool_call_id = $toolCallId
                        AND current.workflow_run_id = $workflowRunId
                        AND current.step_sequence = $sequence
                        AND current.tool_name = $toolName
                        AND current.risk = $risk
                        AND current.payload_hash = $payloadHash
                        AND current.binding_hash = $bindingHash
                        AND current.effect_class = 'effect'
                        AND current.status = 'planned'
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM workflow_authorization_ticket_items AS prior
                      WHERE prior.ticket_id = $ticketId
                        AND prior.step_sequence < $sequence
                        AND (
                            prior.status <> 'consumed'
                            OR NOT EXISTS (
                                SELECT 1
                                FROM workflow_step_runs AS prior_step
                                WHERE prior_step.ticket_item_id = prior.id
                                  AND prior_step.workflow_run_id = $workflowRunId
                                  AND prior_step.status = 'succeeded'
                            )
                        )
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM workflow_step_runs AS prior
                      WHERE prior.workflow_run_id = $workflowRunId
                        AND prior.step_sequence = $sequence
                        AND prior.effect_class = 'effect'
                        AND prior.attempt < (
                            SELECT current.attempt
                            FROM workflow_step_runs AS current
                            WHERE current.tool_call_id = $toolCallId
                              AND current.workflow_run_id = $workflowRunId
                        )
                        AND prior.status <> 'not_started'
                  )
                  AND EXISTS (
                      SELECT 1
                      FROM workflow_authorization_tickets AS ticket
                      JOIN workflow_runs AS run ON run.id = ticket.workflow_run_id
                      WHERE ticket.id = $ticketId
                        AND ticket.workflow_run_id = $workflowRunId
                        AND ticket.status = 'active'
                        AND ticket.approved_at_utc <= $startedAtUtc
                        AND ticket.expires_at_utc > $startedAtUtc
                        AND ticket.actual_maximum_risk = run.actual_maximum_risk
                        AND ticket.snapshot_hash = run.snapshot_hash
                        AND ticket.model_tool_name = run.model_tool_name
                        AND ticket.capability_id = run.capability_id
                        AND ticket.capability_version = run.capability_version
                        AND ticket.catalog_revision = run.catalog_revision
                        AND ticket.capability_content_hash = run.capability_content_hash
                        AND ticket.workflow_hash = run.workflow_hash
                        AND ticket.input_hash = run.input_hash
                        AND ticket.plan_hash = run.plan_hash
                  );
                """,
                cancellationToken,
                transaction,
                ("$startedAtUtc", Format(trustedStartedAtUtc)),
                ("$toolCallId", reservation.ToolCallId),
                ("$ticketItemId", reservation.TicketItemId),
                ("$ticketId", reservation.TicketId),
                ("$workflowRunId", reservation.WorkflowRunId),
                ("$sequence", reservation.Sequence),
                ("$nonceHash", reservation.NonceHash),
                ("$toolName", reservation.ToolName),
                ("$risk", reservation.Risk),
                ("$payloadHash", reservation.PayloadHash),
                ("$bindingHash", reservation.BindingHash)).ConfigureAwait(false);
            if (consumedItem != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var startedStep = await ExecuteCountAsync(
                connection,
                """
                UPDATE workflow_step_runs
                SET status = 'executing', ticket_item_id = $ticketItemId,
                    started_at_utc = $startedAtUtc, revision = revision + 1
                WHERE tool_call_id = $toolCallId
                  AND workflow_run_id = $workflowRunId
                  AND step_sequence = $sequence
                  AND tool_name = $toolName
                  AND risk = $risk
                  AND payload_hash = $payloadHash
                  AND binding_hash = $bindingHash
                  AND effect_class = 'effect'
                  AND ticket_item_id IS NULL
                  AND status = 'planned';
                """,
                cancellationToken,
                transaction,
                ("$ticketItemId", reservation.TicketItemId),
                ("$startedAtUtc", Format(trustedStartedAtUtc)),
                ("$toolCallId", reservation.ToolCallId),
                ("$workflowRunId", reservation.WorkflowRunId),
                ("$sequence", reservation.Sequence),
                ("$toolName", reservation.ToolName),
                ("$risk", reservation.Risk),
                ("$payloadHash", reservation.PayloadHash),
                ("$bindingHash", reservation.BindingHash)).ConfigureAwait(false);
            if (startedStep != 1 ||
                await UpdateRunningRunAsync(
                    connection,
                    transaction,
                    reservation.WorkflowRunId,
                    reservation.ToolCallId,
                    reservation.Sequence,
                    reservation.ExpectedRunRevision,
                    reservation.ExpectedCurrentSequence,
                    trustedStartedAtUtc,
                    cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await ExecuteAsync(
                connection,
                """
                UPDATE workflow_authorization_tickets
                SET status = 'consumed', consumed_at_utc = $startedAtUtc, revision = revision + 1
                WHERE id = $ticketId
                  AND status = 'active'
                  AND NOT EXISTS (
                      SELECT 1 FROM workflow_authorization_ticket_items
                      WHERE ticket_id = $ticketId AND status = 'ready'
                  );
                """,
                cancellationToken,
                transaction,
                ("$startedAtUtc", Format(trustedStartedAtUtc)),
                ("$ticketId", reservation.TicketId)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            reserved = true;
        }, cancellationToken).ConfigureAwait(false);
        return reserved;
    }

    public async Task<bool> AbandonPlannedStepAsync(
        WorkflowPlannedStepAbandonment abandonment,
        CancellationToken cancellationToken)
    {
        ValidatePlannedStepAbandonment(abandonment);
        var abandoned = false;
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var changedStep = await ExecuteCountAsync(
                connection,
                """
                UPDATE workflow_step_runs
                SET status = 'not_started', disposition = 'not_started',
                    redacted_summary = $redactedSummary, fixed_error_code = $fixedErrorCode,
                    completed_at_utc = $abandonedAtUtc, revision = revision + 1
                WHERE tool_call_id = $toolCallId
                  AND workflow_run_id = $workflowRunId
                  AND status = 'planned'
                  AND ticket_item_id IS NULL;
                """,
                cancellationToken,
                transaction,
                ("$redactedSummary", abandonment.RedactedSummary),
                ("$fixedErrorCode", abandonment.FixedErrorCode),
                ("$abandonedAtUtc", Format(abandonment.AbandonedAtUtc)),
                ("$toolCallId", abandonment.ToolCallId),
                ("$workflowRunId", abandonment.WorkflowRunId)).ConfigureAwait(false);
            if (changedStep != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await ExecuteAsync(
                connection,
                """
                UPDATE workflow_authorization_tickets
                SET status = 'revoked', revoked_at_utc = $abandonedAtUtc,
                    revocation_code = 'workflow_step_abandoned', revision = revision + 1
                WHERE workflow_run_id = $workflowRunId
                  AND status = 'active'
                  AND EXISTS (
                      SELECT 1
                      FROM workflow_authorization_ticket_items AS item
                      JOIN workflow_step_runs AS step
                        ON step.tool_call_id = $toolCallId
                       AND step.workflow_run_id = $workflowRunId
                       AND step.step_sequence = item.step_sequence
                       AND step.tool_name = item.tool_name
                       AND step.risk = item.risk
                       AND step.payload_hash = item.payload_hash
                       AND step.binding_hash = item.binding_hash
                      WHERE item.ticket_id = workflow_authorization_tickets.id
                        AND item.status = 'ready'
                  );
                """,
                cancellationToken,
                transaction,
                ("$abandonedAtUtc", Format(abandonment.AbandonedAtUtc)),
                ("$workflowRunId", abandonment.WorkflowRunId),
                ("$toolCallId", abandonment.ToolCallId)).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                UPDATE workflow_authorization_ticket_items
                SET status = 'revoked', revoked_at_utc = $abandonedAtUtc,
                    revocation_code = 'workflow_step_abandoned', revision = revision + 1
                WHERE status = 'ready'
                  AND ticket_id IN (
                      SELECT id
                      FROM workflow_authorization_tickets
                      WHERE workflow_run_id = $workflowRunId
                        AND status = 'revoked'
                        AND revocation_code = 'workflow_step_abandoned'
                        AND revoked_at_utc = $abandonedAtUtc
                  );
                """,
                cancellationToken,
                transaction,
                ("$abandonedAtUtc", Format(abandonment.AbandonedAtUtc)),
                ("$workflowRunId", abandonment.WorkflowRunId)).ConfigureAwait(false);

            var changedRun = await ExecuteCountAsync(
                connection,
                """
                UPDATE workflow_runs
                SET updated_at_utc = $abandonedAtUtc, revision = revision + 1
                WHERE id = $workflowRunId
                  AND status IN ('planned', 'awaiting_approval', 'running')
                  AND revision = $expectedRunRevision
                  AND current_sequence = $expectedCurrentSequence
                  AND current_sequence <= (
                      SELECT step_sequence
                      FROM workflow_step_runs
                      WHERE tool_call_id = $toolCallId AND workflow_run_id = $workflowRunId
                  );
                """,
                cancellationToken,
                transaction,
                ("$abandonedAtUtc", Format(abandonment.AbandonedAtUtc)),
                ("$workflowRunId", abandonment.WorkflowRunId),
                ("$expectedRunRevision", abandonment.ExpectedRunRevision),
                ("$expectedCurrentSequence", abandonment.ExpectedCurrentSequence),
                ("$toolCallId", abandonment.ToolCallId)).ConfigureAwait(false);
            if (changedRun != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            abandoned = true;
        }, cancellationToken).ConfigureAwait(false);
        return abandoned;
    }

    public async Task<bool> CompleteStepAsync(
        WorkflowStepCompletion completion,
        CancellationToken cancellationToken)
    {
        ValidateCompletion(completion);
        var completed = false;
        var disposition = DispositionValue(completion.Disposition);
        var stepStatus = completion.Disposition switch
        {
            WorkflowStepCompletionDisposition.RetryableFailed => "retryable_failed",
            WorkflowStepCompletionDisposition.NotStarted => "not_started",
            WorkflowStepCompletionDisposition.Succeeded => "succeeded",
            WorkflowStepCompletionDisposition.FailedKnown => "failed_known",
            WorkflowStepCompletionDisposition.Unknown => "outcome_unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(completion)),
        };
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var changedStep = await ExecuteCountAsync(
                connection,
                """
                UPDATE workflow_step_runs
                SET status = $status, disposition = $disposition, output_hash = $outputHash,
                    redacted_summary = $redactedSummary, fixed_error_code = $fixedErrorCode,
                    completed_at_utc = $completedAtUtc, revision = revision + 1
                WHERE tool_call_id = $toolCallId
                  AND workflow_run_id = $workflowRunId
                  AND status = 'executing'
                  AND (
                      $status <> 'retryable_failed'
                      OR (effect_class = 'observation' AND retry_class = 'read_only'
                          AND attempt <= maximum_retries)
                  );
                """,
                cancellationToken,
                transaction,
                ("$status", stepStatus),
                ("$disposition", disposition),
                ("$outputHash", completion.OutputHash),
                ("$redactedSummary", completion.RedactedSummary),
                ("$fixedErrorCode", completion.FixedErrorCode),
                ("$completedAtUtc", Format(completion.CompletedAtUtc)),
                ("$toolCallId", completion.ToolCallId),
                ("$workflowRunId", completion.WorkflowRunId)).ConfigureAwait(false);
            if (changedStep != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var runStatus = completion.Disposition switch
            {
                WorkflowStepCompletionDisposition.Unknown => "attention_required",
                WorkflowStepCompletionDisposition.FailedKnown => "failed",
                _ => null,
            };
            var nextAction = completion.Disposition switch
            {
                WorkflowStepCompletionDisposition.Unknown => "review_outcome",
                WorkflowStepCompletionDisposition.FailedKnown => "retry_from_start",
                _ => null,
            };
            var changedRun = await ExecuteCountAsync(
                connection,
                """
                UPDATE workflow_runs
                SET status = COALESCE($runStatus, status),
                    fixed_next_action = COALESCE($nextAction, fixed_next_action),
                    fixed_error_code = COALESCE($fixedErrorCode, fixed_error_code),
                    updated_at_utc = $completedAtUtc,
                    revision = revision + 1
                WHERE id = $workflowRunId
                  AND status IN ('planned', 'awaiting_approval', 'running')
                  AND revision = $expectedRunRevision
                  AND current_sequence = (
                      SELECT step_sequence FROM workflow_step_runs
                      WHERE tool_call_id = $toolCallId AND workflow_run_id = $workflowRunId
                  );
                """,
                cancellationToken,
                transaction,
                ("$runStatus", runStatus),
                ("$nextAction", nextAction),
                ("$fixedErrorCode", completion.FixedErrorCode),
                ("$completedAtUtc", Format(completion.CompletedAtUtc)),
                ("$workflowRunId", completion.WorkflowRunId),
                ("$expectedRunRevision", completion.ExpectedRunRevision),
                ("$toolCallId", completion.ToolCallId)).ConfigureAwait(false);
            if (changedRun != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            completed = true;
        }, cancellationToken).ConfigureAwait(false);
        return completed;
    }

    public async Task<bool> CompleteRunAsync(
        WorkflowRunCompletion completion,
        CancellationToken cancellationToken)
    {
        ValidateRunCompletion(completion);
        var completed = false;
        var status = RunTerminalStatusValue(completion.Status);
        var nextAction = RunNextActionValue(completion.NextAction);
        var requiredPointersJson = completion.RequiredInputs is null
            ? null
            : JsonSerializer.Serialize(completion.RequiredInputs);
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var changedRun = await ExecuteCountAsync(
                connection,
                """
                UPDATE workflow_runs
                SET status = $status,
                    fixed_next_action = $nextAction,
                    fixed_error_code = $fixedErrorCode,
                    redacted_summary = $redactedSummary,
                    result_hash = $resultHash,
                    required_pointers_json = $requiredPointersJson,
                    updated_at_utc = $completedAtUtc,
                    completed_at_utc = $completedAtUtc,
                    revision = revision + 1
                WHERE id = $workflowRunId
                  AND revision = $expectedRunRevision
                  AND current_sequence = $expectedCurrentSequence
                  AND (
                      status IN ('planned', 'awaiting_approval', 'running')
                      OR (status = $status AND completed_at_utc IS NULL)
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM workflow_step_runs AS step
                      WHERE step.workflow_run_id = workflow_runs.id
                        AND step.status IN ('planned', 'executing')
                  )
                  AND (
                      $status <> 'succeeded'
                      OR NOT EXISTS (
                          SELECT 1
                          FROM workflow_step_runs AS latest_step
                          WHERE latest_step.workflow_run_id = workflow_runs.id
                            AND latest_step.attempt = (
                                SELECT MAX(candidate.attempt)
                                FROM workflow_step_runs AS candidate
                                WHERE candidate.workflow_run_id = latest_step.workflow_run_id
                                  AND candidate.step_sequence = latest_step.step_sequence
                            )
                            AND latest_step.status <> 'succeeded'
                      )
                  );
                """,
                cancellationToken,
                transaction,
                ("$status", status),
                ("$nextAction", nextAction),
                ("$fixedErrorCode", completion.FixedErrorCode),
                ("$redactedSummary", completion.RedactedSummary),
                ("$resultHash", completion.ResultHash),
                ("$requiredPointersJson", requiredPointersJson),
                ("$completedAtUtc", Format(completion.CompletedAtUtc)),
                ("$workflowRunId", completion.WorkflowRunId),
                ("$expectedRunRevision", completion.ExpectedRunRevision),
                ("$expectedCurrentSequence", completion.ExpectedCurrentSequence)).ConfigureAwait(false);
            if (changedRun != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await ExecuteAsync(
                connection,
                """
                UPDATE workflow_authorization_tickets
                SET status = 'revoked', revoked_at_utc = $completedAtUtc,
                    revocation_code = 'workflow_terminal', revision = revision + 1
                WHERE workflow_run_id = $workflowRunId AND status = 'active';
                """,
                cancellationToken,
                transaction,
                ("$completedAtUtc", Format(completion.CompletedAtUtc)),
                ("$workflowRunId", completion.WorkflowRunId)).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                UPDATE workflow_authorization_ticket_items
                SET status = 'revoked', revoked_at_utc = $completedAtUtc,
                    revocation_code = 'workflow_terminal', revision = revision + 1
                WHERE workflow_run_id = $workflowRunId AND status = 'ready';
                """,
                cancellationToken,
                transaction,
                ("$completedAtUtc", Format(completion.CompletedAtUtc)),
                ("$workflowRunId", completion.WorkflowRunId)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            completed = true;
        }, cancellationToken).ConfigureAwait(false);
        return completed;
    }

    public async Task<WorkflowStartupRecoveryResult> RecoverOnStartupAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken)
    {
        WorkflowStartupRecoveryResult? result = null;
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var revokedTickets = await ExecuteCountAsync(
                connection,
                """
                UPDATE workflow_authorization_tickets
                SET status = 'revoked', revoked_at_utc = $recoveredAtUtc,
                    revocation_code = 'desktop_restart', revision = revision + 1
                WHERE status = 'active';
                """,
                cancellationToken,
                transaction,
                ("$recoveredAtUtc", Format(recoveredAtUtc))).ConfigureAwait(false);
            var revokedItems = await ExecuteCountAsync(
                connection,
                """
                UPDATE workflow_authorization_ticket_items
                SET status = 'revoked', revoked_at_utc = $recoveredAtUtc,
                    revocation_code = 'desktop_restart', revision = revision + 1
                WHERE status = 'ready'
                  AND ticket_id IN (
                      SELECT id FROM workflow_authorization_tickets
                      WHERE status = 'revoked' AND revocation_code = 'desktop_restart'
                  );
                """,
                cancellationToken,
                transaction,
                ("$recoveredAtUtc", Format(recoveredAtUtc))).ConfigureAwait(false);
            var unknownEffects = await ExecuteCountAsync(
                connection,
                """
                UPDATE workflow_step_runs
                SET status = 'outcome_unknown', disposition = 'unknown',
                    fixed_error_code = 'workflow.desktop_restart.effect_outcome_unknown',
                    completed_at_utc = $recoveredAtUtc, revision = revision + 1
                WHERE status = 'executing' AND effect_class = 'effect';
                """,
                cancellationToken,
                transaction,
                ("$recoveredAtUtc", Format(recoveredAtUtc))).ConfigureAwait(false);
            var interruptedObservations = await ExecuteCountAsync(
                connection,
                """
                UPDATE workflow_step_runs
                SET status = 'interrupted', disposition = 'unknown',
                    fixed_error_code = 'workflow.desktop_restart.observation_interrupted',
                    completed_at_utc = $recoveredAtUtc, revision = revision + 1
                WHERE status = 'executing' AND effect_class = 'observation';
                """,
                cancellationToken,
                transaction,
                ("$recoveredAtUtc", Format(recoveredAtUtc))).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                UPDATE workflow_step_runs
                SET status = 'not_started', disposition = 'not_started',
                    fixed_error_code = 'workflow.desktop_restart.step_not_started',
                    completed_at_utc = $recoveredAtUtc, revision = revision + 1
                WHERE status = 'planned'
                  AND workflow_run_id IN (
                      SELECT id
                      FROM workflow_runs
                      WHERE status IN (
                          'planned', 'awaiting_approval', 'running', 'failed', 'attention_required')
                        AND completed_at_utc IS NULL
                  );
                """,
                cancellationToken,
                transaction,
                ("$recoveredAtUtc", Format(recoveredAtUtc))).ConfigureAwait(false);
            var recoveredLiveRuns = await ExecuteCountAsync(
                connection,
                """
                UPDATE workflow_runs
                SET status = CASE WHEN EXISTS (
                        SELECT 1 FROM workflow_step_runs AS step
                        WHERE step.workflow_run_id = workflow_runs.id
                          AND step.status = 'outcome_unknown'
                          AND step.effect_class = 'effect'
                    ) THEN 'attention_required' ELSE 'interrupted' END,
                    fixed_next_action = CASE WHEN EXISTS (
                        SELECT 1 FROM workflow_step_runs AS step
                        WHERE step.workflow_run_id = workflow_runs.id
                          AND step.status = 'outcome_unknown'
                          AND step.effect_class = 'effect'
                    ) THEN 'review_outcome' ELSE 'retry_from_start' END,
                    fixed_error_code = CASE WHEN EXISTS (
                        SELECT 1 FROM workflow_step_runs AS step
                        WHERE step.workflow_run_id = workflow_runs.id
                          AND step.status = 'outcome_unknown'
                          AND step.effect_class = 'effect'
                    ) THEN 'workflow.desktop_restart.effect_outcome_unknown'
                      ELSE 'workflow.desktop_restart.interrupted' END,
                    updated_at_utc = $recoveredAtUtc,
                    completed_at_utc = $recoveredAtUtc,
                    revision = revision + 1
                WHERE status IN ('planned', 'awaiting_approval', 'running');
                """,
                cancellationToken,
                transaction,
                ("$recoveredAtUtc", Format(recoveredAtUtc))).ConfigureAwait(false);
            var recoveredHalfTerminalRuns = await ExecuteCountAsync(
                connection,
                """
                UPDATE workflow_runs
                SET fixed_next_action = CASE status
                        WHEN 'attention_required' THEN 'review_outcome'
                        ELSE 'retry_from_start'
                    END,
                    fixed_error_code = COALESCE(
                        fixed_error_code,
                        CASE status
                            WHEN 'attention_required' THEN 'workflow.outcome_unknown'
                            ELSE 'workflow.failed'
                        END),
                    updated_at_utc = $recoveredAtUtc,
                    completed_at_utc = $recoveredAtUtc,
                    revision = revision + 1
                WHERE status IN ('failed', 'attention_required')
                  AND completed_at_utc IS NULL;
                """,
                cancellationToken,
                transaction,
                ("$recoveredAtUtc", Format(recoveredAtUtc))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            result = new WorkflowStartupRecoveryResult(
                revokedTickets,
                revokedItems,
                interruptedObservations,
                unknownEffects,
                recoveredLiveRuns + recoveredHalfTerminalRuns);
        }, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("Workflow startup 복구 결과가 없습니다.");
    }

    public async Task<WorkflowRunRecord?> GetRunAsync(
        string workflowRunId,
        CancellationToken cancellationToken)
    {
        ValidateToken(workflowRunId, nameof(workflowRunId));
        WorkflowRunRecord? result = null;
        await WithConnectionAsync(async connection =>
        {
            result = await ReadRunAsync(connection, null, workflowRunId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<WorkflowStepRunRecord?> GetStepAsync(
        string toolCallId,
        CancellationToken cancellationToken)
    {
        ValidateToken(toolCallId, nameof(toolCallId));
        WorkflowStepRunRecord? result = null;
        await WithConnectionAsync(async connection =>
        {
            result = await ReadStepAsync(connection, null, toolCallId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<WorkflowAuthorizationTicketRecord?> GetTicketAsync(
        string ticketId,
        CancellationToken cancellationToken)
    {
        ValidateToken(ticketId, nameof(ticketId));
        WorkflowAuthorizationTicketRecord? result = null;
        await WithConnectionAsync(async connection =>
        {
            result = await ReadTicketAsync(connection, null, ticketId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<WorkflowAuthorizationTicketItemRecord?> GetTicketItemAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        ValidateToken(itemId, nameof(itemId));
        WorkflowAuthorizationTicketItemRecord? result = null;
        await WithConnectionAsync(async connection =>
        {
            result = await ReadTicketItemAsync(connection, null, itemId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<int> UpdateRunningRunAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string workflowRunId,
        string toolCallId,
        int sequence,
        long expectedRevision,
        int expectedCurrentSequence,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken) =>
        await ExecuteCountAsync(
            connection,
            """
            UPDATE workflow_runs
            SET status = 'running',
                current_sequence = CASE WHEN current_sequence < $sequence THEN $sequence ELSE current_sequence END,
                updated_at_utc = $updatedAtUtc,
                revision = revision + 1
            WHERE id = $workflowRunId
              AND status IN ('planned', 'awaiting_approval', 'running')
              AND revision = $expectedRevision
              AND current_sequence = $expectedCurrentSequence
              AND NOT EXISTS (
                  SELECT 1
                  FROM workflow_step_runs AS other
                  WHERE other.workflow_run_id = $workflowRunId
                    AND other.status = 'executing'
                    AND other.tool_call_id <> $toolCallId
              )
              AND EXISTS (
                  SELECT 1
                  FROM workflow_step_runs AS current_step
                  WHERE current_step.tool_call_id = $toolCallId
                    AND current_step.workflow_run_id = $workflowRunId
                    AND current_step.step_sequence = $sequence
                    AND current_step.status = 'executing'
                    AND (
                        (current_step.attempt = 1 AND current_sequence < $sequence)
                        OR (
                            current_step.attempt > 1
                            AND (
                                current_sequence = $sequence
                                OR (
                                    current_sequence < $sequence
                                    AND NOT EXISTS (
                                        SELECT 1
                                        FROM workflow_step_runs AS abandoned_prior
                                        WHERE abandoned_prior.workflow_run_id = $workflowRunId
                                          AND abandoned_prior.step_sequence = $sequence
                                          AND abandoned_prior.attempt < current_step.attempt
                                          AND (
                                              abandoned_prior.status <> 'not_started'
                                              OR abandoned_prior.started_at_utc IS NOT NULL
                                          )
                                    )
                                )
                            )
                        )
                    )
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM workflow_step_runs AS predecessor
                  WHERE predecessor.workflow_run_id = $workflowRunId
                    AND predecessor.step_sequence < $sequence
                    AND predecessor.attempt = (
                        SELECT MAX(latest.attempt)
                        FROM workflow_step_runs AS latest
                        WHERE latest.workflow_run_id = predecessor.workflow_run_id
                          AND latest.step_sequence = predecessor.step_sequence
                    )
                    AND predecessor.status <> 'succeeded'
              );
            """,
            cancellationToken,
            transaction,
            ("$toolCallId", toolCallId),
            ("$sequence", sequence),
            ("$expectedRevision", expectedRevision),
            ("$expectedCurrentSequence", expectedCurrentSequence),
            ("$updatedAtUtc", Format(updatedAtUtc)),
            ("$workflowRunId", workflowRunId)).ConfigureAwait(false);

    private static bool ExactIdentityMatches(WorkflowRunRecord existing, WorkflowRunStart request) =>
        StringComparer.Ordinal.Equals(existing.ConversationId, request.ConversationId) &&
        StringComparer.Ordinal.Equals(existing.ParentRunId, request.ParentRunId) &&
        StringComparer.Ordinal.Equals(existing.ParentToolCallId, request.ParentToolCallId) &&
        StringComparer.Ordinal.Equals(existing.IdempotencyKey, request.IdempotencyKey) &&
        StringComparer.Ordinal.Equals(existing.SnapshotHash, request.SnapshotHash) &&
        StringComparer.Ordinal.Equals(existing.ModelToolName, request.ModelToolName) &&
        StringComparer.Ordinal.Equals(existing.CapabilityId, request.CapabilityId) &&
        StringComparer.Ordinal.Equals(existing.CapabilityVersion, request.CapabilityVersion) &&
        existing.CatalogRevision == request.CatalogRevision &&
        StringComparer.Ordinal.Equals(existing.CapabilityContentHash, request.CapabilityContentHash) &&
        StringComparer.Ordinal.Equals(existing.WorkflowHash, request.WorkflowHash) &&
        StringComparer.Ordinal.Equals(existing.InputHash, request.InputHash) &&
        StringComparer.Ordinal.Equals(existing.PlanHash, request.PlanHash) &&
        StringComparer.Ordinal.Equals(existing.ActualMaximumRisk, request.ActualMaximumRisk);

    private static bool ExactStepPlanMatches(WorkflowStepRunRecord existing, WorkflowStepPlan step) =>
        StringComparer.Ordinal.Equals(existing.WorkflowRunId, step.WorkflowRunId) &&
        existing.Sequence == step.Sequence &&
        existing.Attempt == step.Attempt &&
        StringComparer.Ordinal.Equals(existing.StepId, step.StepId) &&
        StringComparer.Ordinal.Equals(existing.ToolCallId, step.ToolCallId) &&
        StringComparer.Ordinal.Equals(existing.ToolName, step.ToolName) &&
        StringComparer.Ordinal.Equals(existing.Risk, step.Risk) &&
        StringComparer.Ordinal.Equals(existing.RetryClass, step.RetryClass) &&
        existing.MaximumRetries == step.MaximumRetries &&
        StringComparer.Ordinal.Equals(existing.EffectClass, EffectClassValue(step.EffectClass)) &&
        StringComparer.Ordinal.Equals(existing.PayloadHash, step.PayloadHash) &&
        StringComparer.Ordinal.Equals(existing.BindingHash, step.BindingHash) &&
        existing.Status == "planned";

    private static async Task<bool> CanAddStepAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkflowStepPlan step,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM workflow_step_runs AS existing
            WHERE existing.workflow_run_id = $workflowRunId
              AND existing.step_sequence = $sequence;
            """;
        command.Parameters.AddWithValue("$workflowRunId", step.WorkflowRunId);
        command.Parameters.AddWithValue("$sequence", step.Sequence);
        var existingAttempts = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (step.Attempt == 1) return existingAttempts == 0;
        if (existingAttempts != step.Attempt - 1) return false;

        command.Parameters.Clear();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM workflow_step_runs AS previous
            WHERE previous.workflow_run_id = $workflowRunId
              AND previous.step_sequence = $sequence
              AND previous.attempt = $previousAttempt
              AND previous.step_id = $stepId
              AND previous.tool_name = $toolName
              AND previous.risk = $risk
              AND previous.retry_class = $retryClass
              AND previous.maximum_retries = $maximumRetries
              AND previous.effect_class = $effectClass
              AND previous.payload_hash = $payloadHash
              AND previous.binding_hash = $bindingHash
              AND (
                    ($effectClass = 'effect' AND previous.status = 'not_started')
                 OR ($effectClass = 'observation' AND $retryClass = 'read_only'
                     AND $attempt <= $maximumRetries + 1
                     AND previous.status = 'retryable_failed')
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM workflow_step_runs AS unsafe
                  WHERE unsafe.workflow_run_id = $workflowRunId
                    AND unsafe.step_sequence = $sequence
                    AND unsafe.effect_class = 'effect'
                    AND unsafe.status <> 'not_started'
              );
            """;
        command.Parameters.AddWithValue("$workflowRunId", step.WorkflowRunId);
        command.Parameters.AddWithValue("$sequence", step.Sequence);
        command.Parameters.AddWithValue("$previousAttempt", step.Attempt - 1);
        command.Parameters.AddWithValue("$stepId", step.StepId);
        command.Parameters.AddWithValue("$toolName", step.ToolName);
        command.Parameters.AddWithValue("$risk", step.Risk);
        command.Parameters.AddWithValue("$retryClass", step.RetryClass);
        command.Parameters.AddWithValue("$maximumRetries", step.MaximumRetries);
        command.Parameters.AddWithValue("$attempt", step.Attempt);
        command.Parameters.AddWithValue("$effectClass", EffectClassValue(step.EffectClass));
        command.Parameters.AddWithValue("$payloadHash", step.PayloadHash);
        command.Parameters.AddWithValue("$bindingHash", step.BindingHash);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> HasMatchingPlannedEffectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workflowRunId,
        WorkflowAuthorizationTicketItemDraft item,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM workflow_step_runs
            WHERE workflow_run_id = $workflowRunId
              AND step_sequence = $sequence
              AND tool_name = $toolName
              AND risk = $risk
              AND payload_hash = $payloadHash
              AND binding_hash = $bindingHash
              AND effect_class = 'effect'
              AND status = 'planned';
            """;
        command.Parameters.AddWithValue("$workflowRunId", workflowRunId);
        command.Parameters.AddWithValue("$sequence", item.Sequence);
        command.Parameters.AddWithValue("$toolName", item.ToolName);
        command.Parameters.AddWithValue("$risk", item.Risk);
        command.Parameters.AddWithValue("$payloadHash", item.PayloadHash);
        command.Parameters.AddWithValue("$bindingHash", item.BindingHash);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<WorkflowRunRecord?> ReadRunByIdempotencyAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string parentRunId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{RunSelect} WHERE parent_run_id = $parentRunId AND idempotency_key = $idempotencyKey;";
        command.Parameters.AddWithValue("$parentRunId", parentRunId);
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRun(reader) : null;
    }

    private static async Task<WorkflowRunRecord?> ReadRunByParentToolCallAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string parentRunId,
        string parentToolCallId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"{RunSelect} WHERE parent_run_id = $parentRunId AND parent_tool_call_id = $parentToolCallId;";
        command.Parameters.AddWithValue("$parentRunId", parentRunId);
        command.Parameters.AddWithValue("$parentToolCallId", parentToolCallId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRun(reader) : null;
    }

    private static async Task<WorkflowRunRecord?> ReadRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string workflowRunId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{RunSelect} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", workflowRunId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRun(reader) : null;
    }

    private static WorkflowRunRecord ReadRun(System.Data.Common.DbDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetInt64(9),
        reader.GetString(10), reader.GetString(11), reader.GetString(12), reader.GetString(13), reader.GetString(14),
        reader.GetString(15), reader.GetInt32(16),
        reader.IsDBNull(17) ? null : reader.GetString(17),
        reader.IsDBNull(18) ? null : reader.GetString(18),
        reader.IsDBNull(19) ? null : reader.GetString(19),
        reader.IsDBNull(20) ? null : reader.GetString(20),
        reader.IsDBNull(21) ? null : ReadRequiredPointers(reader.GetString(21)),
        reader.GetInt64(22), ParseTimestamp(reader.GetString(23)), ParseTimestamp(reader.GetString(24)),
        reader.IsDBNull(25) ? null : ParseTimestamp(reader.GetString(25)));

    private static IReadOnlyList<string> ReadRequiredPointers(string json)
    {
        var pointers = JsonSerializer.Deserialize<string[]>(json)
            ?? throw new InvalidDataException("Workflow required pointer metadata was invalid.");
        if (!AreValidRequiredPointers(pointers))
            throw new InvalidDataException("Workflow required pointer metadata was invalid.");
        return Array.AsReadOnly(pointers);
    }

    private static async Task<WorkflowStepRunRecord?> ReadStepAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string toolCallId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{StepSelect} WHERE tool_call_id = $toolCallId;";
        command.Parameters.AddWithValue("$toolCallId", toolCallId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadStep(reader) : null;
    }

    private static WorkflowStepRunRecord ReadStep(System.Data.Common.DbDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt32(8), reader.GetString(9),
        reader.GetString(10), reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.IsDBNull(16) ? null : reader.GetString(16),
        reader.IsDBNull(17) ? null : reader.GetString(17),
        reader.GetInt64(18), ParseTimestamp(reader.GetString(19)),
        reader.IsDBNull(20) ? null : ParseTimestamp(reader.GetString(20)),
        reader.IsDBNull(21) ? null : ParseTimestamp(reader.GetString(21)));

    private static async Task<WorkflowAuthorizationTicketRecord?> ReadTicketAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ticketId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, workflow_run_id, status, actual_maximum_risk,
                   approved_at_utc, expires_at_utc, consumed_at_utc, revoked_at_utc,
                   revocation_code, revision
            FROM workflow_authorization_tickets
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", ticketId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new WorkflowAuthorizationTicketRecord(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            ParseTimestamp(reader.GetString(4)), ParseTimestamp(reader.GetString(5)),
            reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt64(9));
    }

    private static async Task<WorkflowAuthorizationTicketItemRecord?> ReadTicketItemAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string itemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, ticket_id, workflow_run_id, step_sequence, nonce_hash,
                   tool_name, risk, payload_hash, binding_hash, status, expires_at_utc,
                   consumed_at_utc, consumed_by_tool_call_id, revoked_at_utc,
                   revocation_code, revision
            FROM workflow_authorization_ticket_items
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new WorkflowAuthorizationTicketItemRecord(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), ParseTimestamp(reader.GetString(10)),
            reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11)),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : ParseTimestamp(reader.GetString(13)),
            reader.IsDBNull(14) ? null : reader.GetString(14), reader.GetInt64(15));
    }

    private static void ValidateRunStart(WorkflowRunStart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateToken(request.RunId, nameof(request.RunId));
        ValidateToken(request.ConversationId, nameof(request.ConversationId));
        ValidateToken(request.ParentRunId, nameof(request.ParentRunId));
        ValidateToken(request.ParentToolCallId, nameof(request.ParentToolCallId));
        ValidateToken(request.IdempotencyKey, nameof(request.IdempotencyKey));
        ValidateHash(request.SnapshotHash, nameof(request.SnapshotHash));
        ValidateToken(request.ModelToolName, nameof(request.ModelToolName));
        ValidateToken(request.CapabilityId, nameof(request.CapabilityId));
        ValidateToken(request.CapabilityVersion, nameof(request.CapabilityVersion));
        if (request.CatalogRevision < 1) throw new ArgumentOutOfRangeException(nameof(request.CatalogRevision));
        ValidateHash(request.CapabilityContentHash, nameof(request.CapabilityContentHash));
        ValidateHash(request.WorkflowHash, nameof(request.WorkflowHash));
        ValidateHash(request.InputHash, nameof(request.InputHash));
        ValidateHash(request.PlanHash, nameof(request.PlanHash));
        ValidateRisk(request.ActualMaximumRisk, nameof(request.ActualMaximumRisk));
    }

    private static void ValidateStepPlan(WorkflowStepPlan step)
    {
        ArgumentNullException.ThrowIfNull(step);
        ValidateToken(step.WorkflowRunId, nameof(step.WorkflowRunId));
        ValidateToken(step.StepId, nameof(step.StepId));
        ValidateToken(step.ToolCallId, nameof(step.ToolCallId));
        ValidateToken(step.ToolName, nameof(step.ToolName));
        ValidateRisk(step.Risk, nameof(step.Risk));
        if (step.RetryClass is not ("never" or "read_only" or "idempotent"))
            throw new ArgumentException("Workflow retry class가 올바르지 않습니다.", nameof(step));
        if (step.MaximumRetries is < 0 or > 3 ||
            step.MaximumRetries > 0 &&
            (step.EffectClass != WorkflowLedgerEffectClass.Observation || step.RetryClass != "read_only"))
            throw new ArgumentException("Workflow retry metadata가 올바르지 않습니다.", nameof(step));
        if (step.Sequence is < 0 or > 19) throw new ArgumentOutOfRangeException(nameof(step.Sequence));
        if (step.Attempt is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(step.Attempt));
        ValidateHash(step.PayloadHash, nameof(step.PayloadHash));
        ValidateHash(step.BindingHash, nameof(step.BindingHash));
    }

    private static void ValidateTicket(WorkflowAuthorizationTicketDraft ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(ticket.Items);
        ValidateToken(ticket.TicketId, nameof(ticket.TicketId));
        ValidateToken(ticket.WorkflowRunId, nameof(ticket.WorkflowRunId));
        ValidateRisk(ticket.ActualMaximumRisk, nameof(ticket.ActualMaximumRisk));
        if (ticket.ExpiresAtUtc <= ticket.ApprovedAtUtc ||
            ticket.ExpiresAtUtc - ticket.ApprovedAtUtc >= MaximumTicketLifetime)
            throw new ArgumentOutOfRangeException(nameof(ticket.ExpiresAtUtc));
        if (ticket.Items.Count is < 1 or > MaximumTicketItems)
            throw new ArgumentOutOfRangeException(nameof(ticket.Items));
        if (ticket.ExpectedRunRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(ticket.ExpectedRunRevision));
        if (ticket.ExpectedCurrentSequence is < -1 or > 19)
            throw new ArgumentOutOfRangeException(nameof(ticket.ExpectedCurrentSequence));
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        var sequences = new HashSet<int>();
        var nonces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in ticket.Items)
        {
            ArgumentNullException.ThrowIfNull(item);
            ValidateToken(item.ItemId, nameof(item.ItemId));
            ValidateToken(item.ToolName, nameof(item.ToolName));
            ValidateRisk(item.Risk, nameof(item.Risk));
            ValidateHash(item.NonceHash, nameof(item.NonceHash));
            ValidateHash(item.PayloadHash, nameof(item.PayloadHash));
            ValidateHash(item.BindingHash, nameof(item.BindingHash));
            if (item.Sequence is < 0 or > 19 || !itemIds.Add(item.ItemId) || !sequences.Add(item.Sequence) ||
                !nonces.Add(item.NonceHash))
                throw new ArgumentException("Workflow ticket item이 중복되었거나 올바르지 않습니다.", nameof(ticket));
        }
    }

    private static void ValidateReservation(WorkflowEffectReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ValidateToken(reservation.WorkflowRunId, nameof(reservation.WorkflowRunId));
        ValidateToken(reservation.ToolCallId, nameof(reservation.ToolCallId));
        ValidateToken(reservation.TicketId, nameof(reservation.TicketId));
        ValidateToken(reservation.TicketItemId, nameof(reservation.TicketItemId));
        ValidateToken(reservation.ToolName, nameof(reservation.ToolName));
        ValidateRisk(reservation.Risk, nameof(reservation.Risk));
        if (reservation.Sequence is < 0 or > 19) throw new ArgumentOutOfRangeException(nameof(reservation.Sequence));
        ValidateHash(reservation.NonceHash, nameof(reservation.NonceHash));
        ValidateHash(reservation.PayloadHash, nameof(reservation.PayloadHash));
        ValidateHash(reservation.BindingHash, nameof(reservation.BindingHash));
        if (reservation.ExpectedRunRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(reservation.ExpectedRunRevision));
        if (reservation.ExpectedCurrentSequence < -1)
            throw new ArgumentOutOfRangeException(nameof(reservation.ExpectedCurrentSequence));
    }

    private static void ValidateObservationReservation(WorkflowObservationReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ValidateToken(reservation.WorkflowRunId, nameof(reservation.WorkflowRunId));
        ValidateToken(reservation.ToolCallId, nameof(reservation.ToolCallId));
        ValidateToken(reservation.ToolName, nameof(reservation.ToolName));
        ValidateRisk(reservation.Risk, nameof(reservation.Risk));
        if (reservation.RetryClass is not ("never" or "read_only" or "idempotent"))
            throw new ArgumentException("Workflow retry class가 올바르지 않습니다.", nameof(reservation));
        if (reservation.Sequence is < 0 or > 19) throw new ArgumentOutOfRangeException(nameof(reservation.Sequence));
        ValidateHash(reservation.PayloadHash, nameof(reservation.PayloadHash));
        ValidateHash(reservation.BindingHash, nameof(reservation.BindingHash));
        if (reservation.ExpectedRunRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(reservation.ExpectedRunRevision));
        if (reservation.ExpectedCurrentSequence < -1)
            throw new ArgumentOutOfRangeException(nameof(reservation.ExpectedCurrentSequence));
    }

    private static void ValidateCompletion(WorkflowStepCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ValidateToken(completion.WorkflowRunId, nameof(completion.WorkflowRunId));
        ValidateToken(completion.ToolCallId, nameof(completion.ToolCallId));
        if (completion.OutputHash is not null) ValidateHash(completion.OutputHash, nameof(completion.OutputHash));
        if (completion.RedactedSummary?.Length > MaximumSummaryLength)
            throw new ArgumentOutOfRangeException(nameof(completion.RedactedSummary));
        if (completion.FixedErrorCode is not null) ValidateFixedCode(completion.FixedErrorCode, nameof(completion.FixedErrorCode));
        if (completion.ExpectedRunRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(completion.ExpectedRunRevision));
        if (completion.Disposition == WorkflowStepCompletionDisposition.Succeeded)
        {
            if (completion.OutputHash is null || completion.FixedErrorCode is not null)
                throw new ArgumentException("성공한 Workflow step에는 output hash만 기록해야 합니다.", nameof(completion));
        }
        else if (completion.OutputHash is not null)
        {
            throw new ArgumentException("성공하지 않은 Workflow step에는 output hash를 기록할 수 없습니다.", nameof(completion));
        }
    }

    private static void ValidatePlannedStepAbandonment(WorkflowPlannedStepAbandonment abandonment)
    {
        ArgumentNullException.ThrowIfNull(abandonment);
        ValidateToken(abandonment.WorkflowRunId, nameof(abandonment.WorkflowRunId));
        ValidateToken(abandonment.ToolCallId, nameof(abandonment.ToolCallId));
        ValidateFixedCode(abandonment.FixedErrorCode, nameof(abandonment.FixedErrorCode));
        if (abandonment.RedactedSummary?.Length > MaximumSummaryLength)
            throw new ArgumentOutOfRangeException(nameof(abandonment.RedactedSummary));
        if (abandonment.ExpectedRunRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(abandonment.ExpectedRunRevision));
        if (abandonment.ExpectedCurrentSequence is < -1 or > 19)
            throw new ArgumentOutOfRangeException(nameof(abandonment.ExpectedCurrentSequence));
    }

    private static void ValidateRunCompletion(WorkflowRunCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ValidateToken(completion.WorkflowRunId, nameof(completion.WorkflowRunId));
        if (completion.FixedErrorCode is not null)
            ValidateFixedCode(completion.FixedErrorCode, nameof(completion.FixedErrorCode));
        if (completion.RedactedSummary?.Length > MaximumSummaryLength)
            throw new ArgumentOutOfRangeException(nameof(completion.RedactedSummary));
        if (completion.ResultHash is not null) ValidateHash(completion.ResultHash, nameof(completion.ResultHash));
        if (completion.ExpectedRunRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(completion.ExpectedRunRevision));
        if (completion.ExpectedCurrentSequence is < -1 or > 19)
            throw new ArgumentOutOfRangeException(nameof(completion.ExpectedCurrentSequence));

        var expectedNextAction = completion.Status switch
        {
            WorkflowRunTerminalStatus.Completed => WorkflowRunNextAction.None,
            WorkflowRunTerminalStatus.NeedsInformation => WorkflowRunNextAction.ProvideInformation,
            WorkflowRunTerminalStatus.Failed => WorkflowRunNextAction.RetryFromStart,
            WorkflowRunTerminalStatus.Cancelled => WorkflowRunNextAction.RetryFromStart,
            WorkflowRunTerminalStatus.AttentionRequired => WorkflowRunNextAction.ReviewOutcome,
            _ => throw new ArgumentOutOfRangeException(nameof(completion.Status)),
        };
        if (completion.NextAction != expectedNextAction)
            throw new ArgumentException("Workflow terminal next action이 status와 일치하지 않습니다.", nameof(completion));
        if (completion.Status == WorkflowRunTerminalStatus.Completed)
        {
            if (completion.ResultHash is null || completion.FixedErrorCode is not null)
                throw new ArgumentException("완료 Workflow에는 result hash만 기록해야 합니다.", nameof(completion));
        }
        else if (completion.ResultHash is not null)
        {
            throw new ArgumentException("완료되지 않은 Workflow에는 result hash를 기록할 수 없습니다.", nameof(completion));
        }
        if (completion.Status is WorkflowRunTerminalStatus.Failed or WorkflowRunTerminalStatus.AttentionRequired &&
            completion.FixedErrorCode is null)
            throw new ArgumentException("실패 또는 검토 필요 Workflow에는 고정 오류 코드가 필요합니다.", nameof(completion));
        if (completion.Status == WorkflowRunTerminalStatus.NeedsInformation)
        {
            if (completion.RequiredInputs is null || !AreValidRequiredPointers(completion.RequiredInputs))
                throw new ArgumentException("추가 정보가 필요한 Workflow에는 안전한 입력 pointer가 필요합니다.", nameof(completion));
        }
        else if (completion.RequiredInputs is not null)
        {
            throw new ArgumentException("추가 정보 상태가 아닌 Workflow에는 입력 pointer를 기록할 수 없습니다.", nameof(completion));
        }
    }

    private static bool AreValidRequiredPointers(IReadOnlyList<string> pointers)
    {
        if (pointers.Count is < 1 or > 16 || pointers.Distinct(StringComparer.Ordinal).Count() != pointers.Count)
            return false;
        return pointers.All(pointer => pointer is { Length: >= 1 and <= 512 } && IsNonRootJsonPointer(pointer));
    }

    private static bool IsNonRootJsonPointer(string pointer)
    {
        if (pointer[0] != '/') return false;
        for (var index = 0; index < pointer.Length; index++)
        {
            if (pointer[index] != '~') continue;
            if (++index >= pointer.Length || pointer[index] is not ('0' or '1')) return false;
        }
        return true;
    }

    private static void ValidateToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumTokenLength ||
            value.Any(char.IsControl))
            throw new ArgumentException("Workflow ledger 식별자가 올바르지 않습니다.", parameterName);
    }

    private static void ValidateFixedCode(string value, string parameterName)
    {
        if (value.Length is < 1 or > 128 ||
            value.Any(character => character is not (>= 'a' and <= 'z') &&
                                   !char.IsAsciiDigit(character) && character is not ('.' or '_' or '-')))
            throw new ArgumentException("Workflow ledger 고정 코드가 올바르지 않습니다.", parameterName);
    }

    private static void ValidateRisk(string value, string parameterName)
    {
        if (value is not ("R0" or "R1" or "R2" or "R3" or "R4"))
            throw new ArgumentException("Workflow risk가 올바르지 않습니다.", parameterName);
    }

    private static int RiskOrdinal(string value) => value switch
    {
        "R0" => 0,
        "R1" => 1,
        "R2" => 2,
        "R3" => 3,
        "R4" => 4,
        _ => int.MaxValue,
    };

    private static void ValidateHash(string value, string parameterName)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            value.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException("Workflow ledger hash가 올바르지 않습니다.", parameterName);
    }

    private static string EffectClassValue(WorkflowLedgerEffectClass value) => value switch
    {
        WorkflowLedgerEffectClass.Observation => "observation",
        WorkflowLedgerEffectClass.Effect => "effect",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string DispositionValue(WorkflowStepCompletionDisposition value) => value switch
    {
        WorkflowStepCompletionDisposition.RetryableFailed => "retryable_failed",
        WorkflowStepCompletionDisposition.NotStarted => "not_started",
        WorkflowStepCompletionDisposition.Succeeded => "succeeded",
        WorkflowStepCompletionDisposition.FailedKnown => "failed_known",
        WorkflowStepCompletionDisposition.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string RunTerminalStatusValue(WorkflowRunTerminalStatus value) => value switch
    {
        WorkflowRunTerminalStatus.Completed => "succeeded",
        WorkflowRunTerminalStatus.NeedsInformation => "needs_information",
        WorkflowRunTerminalStatus.Failed => "failed",
        WorkflowRunTerminalStatus.Cancelled => "cancelled",
        WorkflowRunTerminalStatus.AttentionRequired => "attention_required",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string? RunNextActionValue(WorkflowRunNextAction value) => value switch
    {
        WorkflowRunNextAction.None => null,
        WorkflowRunNextAction.ProvideInformation => "provide_information",
        WorkflowRunNextAction.RetryFromStart => "retry_from_start",
        WorkflowRunNextAction.ReviewOutcome => "review_outcome",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private const string RunSelect =
        """
        SELECT id, conversation_id, parent_run_id, parent_tool_call_id, idempotency_key,
               snapshot_hash, model_tool_name,
               capability_id, capability_version, catalog_revision, capability_content_hash,
               workflow_hash, input_hash, plan_hash, actual_maximum_risk, status, current_sequence,
               fixed_next_action, fixed_error_code, redacted_summary, result_hash, required_pointers_json,
               revision, created_at_utc, updated_at_utc, completed_at_utc
        FROM workflow_runs
        """;

    private const string StepSelect =
        """
        SELECT tool_call_id, workflow_run_id, step_sequence, attempt, step_id,
               tool_name, risk, retry_class, maximum_retries, effect_class, payload_hash, binding_hash,
               ticket_item_id, status, disposition, output_hash, redacted_summary,
               fixed_error_code, revision, created_at_utc, started_at_utc, completed_at_utc
        FROM workflow_step_runs
        """;

    private Task WithConnectionAsync(Func<SqliteConnection, Task> operation, CancellationToken cancellationToken) =>
        database.WithConnectionAsync(operation, cancellationToken);

    private static Task<int> ExecuteAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        System.Data.Common.DbTransaction? transaction = null,
        params (string Name, object? Value)[] parameters) =>
        ConversationDatabase.ExecuteAsync(connection, commandText, cancellationToken, transaction, parameters);

    private static async Task<int> ExecuteCountAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        System.Data.Common.DbTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = commandText;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
