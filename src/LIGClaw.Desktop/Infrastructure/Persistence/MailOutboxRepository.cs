using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using LIGClaw.Application.Mail;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed class MailOutboxRepository(ConversationDatabase database) : IMailOutboxRepository
{
    private const string SelectColumns =
        """
        operation_id, idempotency_key, conversation_id, workflow_run_id, tool_call_id,
        capability_id, capability_version, capability_content_hash, catalog_revision,
        snapshot_hash, connection_slot, binding_hash, draft_hash, transport_kind,
        smtp_message_id, smtp_phase, submission_state, delivery_state, attempt_count,
        fixed_error_code, revision, created_at_utc, updated_at_utc
        """;

    internal static string NetworkStartedUnresolvedSql { get; } =
        $"""
        SELECT {SelectColumns}
        FROM mail_outbox_operations INDEXED BY ix_mail_outbox_mail_api_unresolved
        WHERE draft_hash = $draftHash
          AND binding_hash = $bindingHash
          AND transport_kind = 'mail-api'
          AND submission_state IN ('submitting', 'outcome_unknown')
        ORDER BY updated_at_utc DESC, operation_id
        LIMIT $limit;
        """;

    internal static string SmtpNetworkStartedUnresolvedSql { get; } =
        $"""
        SELECT {SelectColumns}
        FROM mail_outbox_operations INDEXED BY ix_mail_outbox_smtp_unresolved
        WHERE draft_hash = $draftHash
          AND transport_kind = 'smtp'
          AND submission_state IN ('submitting', 'outcome_unknown')
        ORDER BY updated_at_utc DESC, operation_id
        LIMIT $limit;
        """;

    public async Task<MailOutboxCreateResult> CreateOrGetAsync(
        MailOutboxCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        MailOutboxCreateResult? result = null;
        await database.WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var existing = await ReadByToolCallAsync(
                connection,
                (SqliteTransaction)transaction,
                request.ToolCallId,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                result = ExactIdentityMatches(existing, request)
                    ? new MailOutboxCreateResult(MailOutboxCreateOutcome.Existing, existing)
                    : new MailOutboxCreateResult(
                        MailOutboxCreateOutcome.Conflict,
                        existing,
                        MailOutboxPolicy.IdentityConflict);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            for (var generationAttempt = 0; generationAttempt < 4; generationAttempt++)
            {
                var operation = MailOutboxOperation.CreateStaged(
                    CreateOperationId(),
                    CreateIdempotencyKey(),
                    request);
                var inserted = await InsertAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    operation,
                    cancellationToken).ConfigureAwait(false);
                if (inserted == 1)
                {
                    result = new MailOutboxCreateResult(MailOutboxCreateOutcome.Created, operation);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                existing = await ReadByToolCallAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    request.ToolCallId,
                    cancellationToken).ConfigureAwait(false);
                if (existing is null) continue;
                result = ExactIdentityMatches(existing, request)
                    ? new MailOutboxCreateResult(MailOutboxCreateOutcome.Existing, existing)
                    : new MailOutboxCreateResult(
                        MailOutboxCreateOutcome.Conflict,
                        existing,
                        MailOutboxPolicy.IdentityConflict);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException("Mail outbox identity를 생성하지 못했습니다.");
        }, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("Mail outbox create 결과가 없습니다.");
    }

    public async Task<MailOutboxOperation?> GetAsync(
        MailOutboxOperationId operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        MailOutboxOperation? result = null;
        await database.WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {SelectColumns} FROM mail_outbox_operations WHERE operation_id = $operationId;";
            command.Parameters.AddWithValue("$operationId", operationId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadOperation(reader)
                : null;
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<MailOutboxTransitionResult> TryTransitionAsync(
        MailOutboxTransitionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.OperationId);
        MailOutboxTransitionResult? result = null;
        await database.WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var current = await ReadByOperationIdAsync(
                connection,
                (SqliteTransaction)transaction,
                request.OperationId,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                result = new MailOutboxTransitionResult(
                    MailOutboxTransitionOutcome.NotFound,
                    ErrorCode: MailOutboxPolicy.NotFound);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!MailOutboxPolicy.TryApplyTransition(current, request, out var updated, out var errorCode) ||
                updated is null)
            {
                result = new MailOutboxTransitionResult(
                    StringComparer.Ordinal.Equals(errorCode, MailOutboxPolicy.TransitionStale)
                        ? MailOutboxTransitionOutcome.Stale
                        : MailOutboxTransitionOutcome.Invalid,
                    current,
                    errorCode ?? MailOutboxPolicy.TransitionInvalid);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText =
                """
                UPDATE mail_outbox_operations
                SET submission_state = $nextSubmissionState,
                    delivery_state = $nextDeliveryState,
                    smtp_phase = $nextSmtpPhase,
                    attempt_count = $nextAttemptCount,
                    fixed_error_code = $fixedErrorCode,
                    revision = revision + 1,
                    updated_at_utc = $updatedAtUtc
                WHERE operation_id = $operationId
                  AND revision = $expectedRevision
                  AND submission_state = $expectedSubmissionState
                  AND delivery_state = $expectedDeliveryState;
                """;
            update.Parameters.AddWithValue("$nextSubmissionState", SubmissionValue(updated.SubmissionState));
            update.Parameters.AddWithValue("$nextDeliveryState", DeliveryValue(updated.DeliveryState));
            update.Parameters.AddWithValue(
                "$nextSmtpPhase",
                updated.SmtpPhase is null ? DBNull.Value : SmtpPhaseValue(updated.SmtpPhase.Value));
            update.Parameters.AddWithValue("$nextAttemptCount", updated.AttemptCount);
            update.Parameters.AddWithValue("$fixedErrorCode", (object?)updated.FixedErrorCode ?? DBNull.Value);
            update.Parameters.AddWithValue("$updatedAtUtc", Format(updated.UpdatedAtUtc));
            update.Parameters.AddWithValue("$operationId", request.OperationId.Value);
            update.Parameters.AddWithValue("$expectedRevision", request.ExpectedRevision);
            update.Parameters.AddWithValue("$expectedSubmissionState", SubmissionValue(request.ExpectedSubmissionState));
            update.Parameters.AddWithValue("$expectedDeliveryState", DeliveryValue(request.ExpectedDeliveryState));
            var changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed != 1)
            {
                result = new MailOutboxTransitionResult(
                    MailOutboxTransitionOutcome.Stale,
                    current,
                    MailOutboxPolicy.TransitionStale);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            result = new MailOutboxTransitionResult(MailOutboxTransitionOutcome.Succeeded, updated);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("Mail outbox transition 결과가 없습니다.");
    }

    public async Task<IReadOnlyList<MailOutboxOperation>> ListNetworkStartedUnresolvedAsync(
        MailOutboxHash draftHash,
        MailOutboxHash bindingHash,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draftHash);
        ArgumentNullException.ThrowIfNull(bindingHash);
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = new List<MailOutboxOperation>();
        await database.WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = NetworkStartedUnresolvedSql;
            command.Parameters.AddWithValue("$draftHash", draftHash.Value);
            command.Parameters.AddWithValue("$bindingHash", bindingHash.Value);
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                result.Add(ReadOperation(reader));
        }, cancellationToken).ConfigureAwait(false);
        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<MailOutboxOperation>> ListSmtpNetworkStartedUnresolvedAsync(
        MailOutboxHash draftHash,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draftHash);
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = new List<MailOutboxOperation>();
        await database.WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = SmtpNetworkStartedUnresolvedSql;
            command.Parameters.AddWithValue("$draftHash", draftHash.Value);
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                result.Add(ReadOperation(reader));
        }, cancellationToken).ConfigureAwait(false);
        return result.AsReadOnly();
    }

    public async Task<MailOutboxStartupRecoveryResult> RecoverOnStartupAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken)
    {
        if (recoveredAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("UTC recovery timestamp가 필요합니다.", nameof(recoveredAtUtc));
        MailOutboxStartupRecoveryResult? result = null;
        await database.WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var cancelled = await ExecuteRecoveryAsync(
                connection,
                (SqliteTransaction)transaction,
                "staged",
                "cancelled_not_started",
                "unknown",
                "mail.outbox.desktop_restart.not_started",
                null,
                null,
                recoveredAtUtc,
                cancellationToken).ConfigureAwait(false);
            var mailApiUnknown = await ExecuteRecoveryAsync(
                connection,
                (SqliteTransaction)transaction,
                "submitting",
                "outcome_unknown",
                "unknown",
                "mail.outbox.desktop_restart.outcome_unknown",
                "mail-api",
                null,
                recoveredAtUtc,
                cancellationToken).ConfigureAwait(false);
            var smtpFailedKnown = await ExecuteRecoveryAsync(
                connection,
                (SqliteTransaction)transaction,
                "submitting",
                "failed_known",
                "failed",
                "mail.outbox.desktop_restart.smtp_not_submitted",
                "smtp",
                "pre_data",
                recoveredAtUtc,
                cancellationToken).ConfigureAwait(false);
            var smtpUnknown = await ExecuteRecoveryAsync(
                connection,
                (SqliteTransaction)transaction,
                "submitting",
                "outcome_unknown",
                "unknown",
                "mail.outbox.desktop_restart.smtp_outcome_unknown",
                "smtp",
                "data_started",
                recoveredAtUtc,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            result = new MailOutboxStartupRecoveryResult(
                cancelled,
                checked(mailApiUnknown + smtpUnknown),
                smtpFailedKnown);
        }, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("Mail outbox recovery 결과가 없습니다.");
    }

    private static async Task<int> InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MailOutboxOperation operation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO mail_outbox_operations(
                operation_id, idempotency_key, conversation_id, workflow_run_id, tool_call_id,
                capability_id, capability_version, capability_content_hash, catalog_revision,
                snapshot_hash, connection_slot, binding_hash, draft_hash, transport_kind,
                smtp_message_id, smtp_phase, submission_state, delivery_state, attempt_count,
                fixed_error_code, revision, created_at_utc, updated_at_utc)
            VALUES (
                $operationId, $idempotencyKey, $conversationId, $workflowRunId, $toolCallId,
                $capabilityId, $capabilityVersion, $capabilityContentHash, $catalogRevision,
                $snapshotHash, $connectionSlot, $bindingHash, $draftHash, $transportKind,
                $smtpMessageId, $smtpPhase, $submissionState, $deliveryState, $attemptCount,
                $fixedErrorCode, $revision, $createdAtUtc, $updatedAtUtc);
            """;
        AddOperationParameters(command, operation);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MailOutboxOperation?> ReadByToolCallAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string toolCallId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM mail_outbox_operations WHERE tool_call_id = $toolCallId;";
        command.Parameters.AddWithValue("$toolCallId", toolCallId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadOperation(reader)
            : null;
    }

    private static async Task<MailOutboxOperation?> ReadByOperationIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MailOutboxOperationId operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM mail_outbox_operations WHERE operation_id = $operationId;";
        command.Parameters.AddWithValue("$operationId", operationId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadOperation(reader)
            : null;
    }

    private static MailOutboxOperation ReadOperation(SqliteDataReader reader)
    {
        try
        {
            return MailOutboxOperation.Restore(
                MailOutboxOperationId.Parse(reader.GetString(0)),
                MailOutboxIdempotencyKey.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                MailOutboxHash.Parse(reader.GetString(7)),
                reader.GetInt64(8),
                MailOutboxHash.Parse(reader.GetString(9)),
                reader.GetString(10),
                MailOutboxHash.Parse(reader.GetString(11)),
                MailOutboxHash.Parse(reader.GetString(12)),
                ParseSubmission(reader.GetString(16)),
                ParseDelivery(reader.GetString(17)),
                reader.GetInt32(18),
                reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.GetInt64(20),
                ParseTimestamp(reader.GetString(21)),
                ParseTimestamp(reader.GetString(22)),
                ParseTransport(reader.GetString(13)),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : ParseSmtpPhase(reader.GetString(15)));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Mail outbox metadata가 손상되었습니다.", exception);
        }
    }

    private static async Task<int> ExecuteRecoveryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string currentSubmissionState,
        string nextSubmissionState,
        string nextDeliveryState,
        string fixedErrorCode,
        string? transportKind,
        string? smtpPhase,
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE mail_outbox_operations
            SET submission_state = $nextSubmissionState,
                delivery_state = $nextDeliveryState,
                fixed_error_code = $fixedErrorCode,
                revision = revision + 1,
                updated_at_utc = CASE
                    WHEN updated_at_utc > $recoveredAtUtc THEN updated_at_utc
                    ELSE $recoveredAtUtc
                END
            WHERE submission_state = $currentSubmissionState
              AND ($transportKind IS NULL OR transport_kind = $transportKind)
              AND ($smtpPhase IS NULL OR smtp_phase = $smtpPhase);
            """;
        command.Parameters.AddWithValue("$nextSubmissionState", nextSubmissionState);
        command.Parameters.AddWithValue("$nextDeliveryState", nextDeliveryState);
        command.Parameters.AddWithValue("$fixedErrorCode", fixedErrorCode);
        command.Parameters.AddWithValue("$transportKind", (object?)transportKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$smtpPhase", (object?)smtpPhase ?? DBNull.Value);
        command.Parameters.AddWithValue("$recoveredAtUtc", Format(recoveredAtUtc));
        command.Parameters.AddWithValue("$currentSubmissionState", currentSubmissionState);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddOperationParameters(SqliteCommand command, MailOutboxOperation operation)
    {
        command.Parameters.AddWithValue("$operationId", operation.OperationId.Value);
        command.Parameters.AddWithValue("$idempotencyKey", operation.IdempotencyKey.Value);
        command.Parameters.AddWithValue("$conversationId", operation.ConversationId);
        command.Parameters.AddWithValue("$workflowRunId", operation.WorkflowRunId);
        command.Parameters.AddWithValue("$toolCallId", operation.ToolCallId);
        command.Parameters.AddWithValue("$capabilityId", operation.CapabilityId);
        command.Parameters.AddWithValue("$capabilityVersion", operation.CapabilityVersion);
        command.Parameters.AddWithValue("$capabilityContentHash", operation.CapabilityContentHash.Value);
        command.Parameters.AddWithValue("$catalogRevision", operation.CatalogRevision);
        command.Parameters.AddWithValue("$snapshotHash", operation.SnapshotHash.Value);
        command.Parameters.AddWithValue("$connectionSlot", operation.ConnectionSlot);
        command.Parameters.AddWithValue("$bindingHash", operation.BindingHash.Value);
        command.Parameters.AddWithValue("$draftHash", operation.DraftHash.Value);
        command.Parameters.AddWithValue("$transportKind", TransportValue(operation.TransportKind));
        command.Parameters.AddWithValue("$smtpMessageId", (object?)operation.SmtpMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$smtpPhase",
            operation.SmtpPhase is null ? DBNull.Value : SmtpPhaseValue(operation.SmtpPhase.Value));
        command.Parameters.AddWithValue("$submissionState", SubmissionValue(operation.SubmissionState));
        command.Parameters.AddWithValue("$deliveryState", DeliveryValue(operation.DeliveryState));
        command.Parameters.AddWithValue("$attemptCount", operation.AttemptCount);
        command.Parameters.AddWithValue("$fixedErrorCode", (object?)operation.FixedErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", operation.Revision);
        command.Parameters.AddWithValue("$createdAtUtc", Format(operation.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedAtUtc", Format(operation.UpdatedAtUtc));
    }

    private static bool ExactIdentityMatches(
        MailOutboxOperation operation,
        MailOutboxCreateRequest request) =>
        StringComparer.Ordinal.Equals(operation.ConversationId, request.ConversationId) &&
        StringComparer.Ordinal.Equals(operation.WorkflowRunId, request.WorkflowRunId) &&
        StringComparer.Ordinal.Equals(operation.ToolCallId, request.ToolCallId) &&
        StringComparer.Ordinal.Equals(operation.CapabilityId, request.CapabilityId) &&
        StringComparer.Ordinal.Equals(operation.CapabilityVersion, request.CapabilityVersion) &&
        operation.CapabilityContentHash.Equals(request.CapabilityContentHash) &&
        operation.CatalogRevision == request.CatalogRevision &&
        operation.SnapshotHash.Equals(request.SnapshotHash) &&
        StringComparer.Ordinal.Equals(operation.ConnectionSlot, request.ConnectionSlot) &&
        operation.BindingHash.Equals(request.BindingHash) &&
        operation.DraftHash.Equals(request.DraftHash) &&
        operation.TransportKind == request.TransportKind;

    private static MailOutboxOperationId CreateOperationId() =>
        MailOutboxOperationId.Parse(
            $"mgo_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}");

    private static MailOutboxIdempotencyKey CreateIdempotencyKey() =>
        MailOutboxIdempotencyKey.Parse(
            $"mik_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}");

    private static string SubmissionValue(MailOutboxSubmissionState state) => state switch
    {
        MailOutboxSubmissionState.Staged => "staged",
        MailOutboxSubmissionState.Submitting => "submitting",
        MailOutboxSubmissionState.Accepted => "accepted",
        MailOutboxSubmissionState.FailedKnown => "failed_known",
        MailOutboxSubmissionState.OutcomeUnknown => "outcome_unknown",
        MailOutboxSubmissionState.CancelledNotStarted => "cancelled_not_started",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static MailOutboxSubmissionState ParseSubmission(string value) => value switch
    {
        "staged" => MailOutboxSubmissionState.Staged,
        "submitting" => MailOutboxSubmissionState.Submitting,
        "accepted" => MailOutboxSubmissionState.Accepted,
        "failed_known" => MailOutboxSubmissionState.FailedKnown,
        "outcome_unknown" => MailOutboxSubmissionState.OutcomeUnknown,
        "cancelled_not_started" => MailOutboxSubmissionState.CancelledNotStarted,
        _ => throw new InvalidDataException("지원하지 않는 Mail outbox submission 상태입니다."),
    };

    private static string DeliveryValue(MailOutboxDeliveryState state) => state switch
    {
        MailOutboxDeliveryState.Unknown => "unknown",
        MailOutboxDeliveryState.Queued => "queued",
        MailOutboxDeliveryState.Delivered => "delivered",
        MailOutboxDeliveryState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static MailOutboxDeliveryState ParseDelivery(string value) => value switch
    {
        "unknown" => MailOutboxDeliveryState.Unknown,
        "queued" => MailOutboxDeliveryState.Queued,
        "delivered" => MailOutboxDeliveryState.Delivered,
        "failed" => MailOutboxDeliveryState.Failed,
        _ => throw new InvalidDataException("지원하지 않는 Mail outbox delivery 상태입니다."),
    };

    private static string TransportValue(MailOutboxTransportKind transport) => transport switch
    {
        MailOutboxTransportKind.MailApi => "mail-api",
        MailOutboxTransportKind.Smtp => "smtp",
        _ => throw new ArgumentOutOfRangeException(nameof(transport)),
    };

    private static MailOutboxTransportKind ParseTransport(string value) => value switch
    {
        "mail-api" => MailOutboxTransportKind.MailApi,
        "smtp" => MailOutboxTransportKind.Smtp,
        _ => throw new InvalidDataException("지원하지 않는 Mail outbox transport입니다."),
    };

    private static string SmtpPhaseValue(MailOutboxSmtpPhase phase) => phase switch
    {
        MailOutboxSmtpPhase.PreData => "pre_data",
        MailOutboxSmtpPhase.DataStarted => "data_started",
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    private static MailOutboxSmtpPhase ParseSmtpPhase(string value) => value switch
    {
        "pre_data" => MailOutboxSmtpPhase.PreData,
        "data_started" => MailOutboxSmtpPhase.DataStarted,
        _ => throw new InvalidDataException("지원하지 않는 SMTP outbox phase입니다."),
    };

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
