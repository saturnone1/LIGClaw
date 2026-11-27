using System.Globalization;
using System.IO;
using LIGClaw.Desktop.Infrastructure.Tools;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed class OperationalAuditRepository(ConversationDatabase database) : IOperationalAuditRepository
{
    public Task RecordApprovalAsync(ToolApprovalAuditRecord record, CancellationToken cancellationToken) =>
        WithConnectionAsync(
            connection => ExecuteAsync(
                connection,
                """
                INSERT OR IGNORE INTO approval_decisions(
                    conversation_id, run_id, tool_call_id, tool_name, risk, approved, scope, summary, created_at_utc)
                VALUES ($conversationId, $runId, $toolCallId, $toolName, $risk, $approved, $scope, $summary, $createdAtUtc);
                """,
                cancellationToken,
                parameters:
                [
                    ("$conversationId", record.ConversationId),
                    ("$runId", record.RunId),
                    ("$toolCallId", record.ToolCallId),
                    ("$toolName", record.ToolName),
                    ("$risk", record.Risk),
                    ("$approved", record.Approved ? 1 : 0),
                    ("$scope", record.Scope),
                    ("$summary", record.Summary),
                    ("$createdAtUtc", record.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                ]),
            cancellationToken);

    public Task RecordExecutionAsync(ToolExecutionAuditRecord record, CancellationToken cancellationToken) =>
        WithConnectionAsync(
            connection => ExecuteAsync(
                connection,
                """
                INSERT OR IGNORE INTO tool_executions(
                    conversation_id, run_id, tool_call_id, tool_name, risk, status, summary, created_at_utc)
                VALUES ($conversationId, $runId, $toolCallId, $toolName, $risk, $status, $summary, $createdAtUtc);
                """,
                cancellationToken,
                parameters:
                [
                    ("$conversationId", record.ConversationId),
                    ("$runId", record.RunId),
                    ("$toolCallId", record.ToolCallId),
                    ("$toolName", record.ToolName),
                    ("$risk", record.Risk),
                    ("$status", record.Status),
                    ("$summary", record.Summary),
                    ("$createdAtUtc", record.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                ]),
            cancellationToken);

    public async Task<CapabilityGrant?> FindActiveGrantAsync(
        string toolName,
        string risk,
        string scopeHash,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        CapabilityGrant? result = null;
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, tool_name, risk, scope_hash, scope_summary, created_at_utc, expires_at_utc
                FROM capability_grants
                WHERE tool_name = $toolName AND risk = $risk AND scope_hash = $scopeHash
                  AND revoked_at_utc IS NULL AND expires_at_utc > $nowUtc
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$toolName", toolName);
            command.Parameters.AddWithValue("$risk", risk);
            command.Parameters.AddWithValue("$scopeHash", scopeHash);
            command.Parameters.AddWithValue("$nowUtc", Format(nowUtc));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result = ReadCapabilityGrant(reader);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task SaveGrantAsync(CapabilityGrant grant, CancellationToken cancellationToken) =>
        WithConnectionAsync(
            connection => ExecuteAsync(
                connection,
                """
                INSERT INTO capability_grants(
                    id, tool_name, risk, scope_hash, scope_summary, created_at_utc, expires_at_utc, revoked_at_utc)
                VALUES ($id, $toolName, $risk, $scopeHash, $scopeSummary, $createdAtUtc, $expiresAtUtc, NULL)
                ON CONFLICT(tool_name, risk, scope_hash) DO UPDATE SET
                    id = excluded.id,
                    scope_summary = excluded.scope_summary,
                    created_at_utc = excluded.created_at_utc,
                    expires_at_utc = excluded.expires_at_utc,
                    revoked_at_utc = NULL;
                """,
                cancellationToken,
                parameters:
                [
                    ("$id", grant.Id),
                    ("$toolName", grant.ToolName),
                    ("$risk", grant.Risk),
                    ("$scopeHash", grant.ScopeHash),
                    ("$scopeSummary", grant.ScopeSummary),
                    ("$createdAtUtc", Format(grant.CreatedAtUtc)),
                    ("$expiresAtUtc", Format(grant.ExpiresAtUtc)),
                ]),
            cancellationToken);

    public async Task<IReadOnlyList<CapabilityGrant>> GetActiveGrantsAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CapabilityGrant>();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, tool_name, risk, scope_hash, scope_summary, created_at_utc, expires_at_utc
                FROM capability_grants
                WHERE revoked_at_utc IS NULL AND expires_at_utc > $nowUtc
                ORDER BY expires_at_utc, scope_summary;
                """;
            command.Parameters.AddWithValue("$nowUtc", Format(nowUtc));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadCapabilityGrant(reader));
        }, cancellationToken).ConfigureAwait(false);
        return results;
    }

    public Task RevokeGrantAsync(
        string grantId,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(grantId)) throw new ArgumentException("권한 ID가 필요합니다.", nameof(grantId));
        return WithConnectionAsync(
            connection => ExecuteAsync(
                connection,
                "UPDATE capability_grants SET revoked_at_utc = $revokedAtUtc WHERE id = $id AND revoked_at_utc IS NULL;",
                cancellationToken,
                parameters: [("$revokedAtUtc", Format(revokedAtUtc)), ("$id", grantId)]),
            cancellationToken);
    }

    private static CapabilityGrant ReadCapabilityGrant(System.Data.Common.DbDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        ParseTimestamp(reader.GetString(5)),
        ParseTimestamp(reader.GetString(6)));

    public async Task<IReadOnlyList<ToolActivitySummary>> GetRecentToolActivityAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        var results = new List<ToolActivitySummary>();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                WITH activity AS (
                    SELECT
                        e.id,
                        e.tool_name,
                        e.risk,
                        e.status,
                        e.summary,
                        a.approved,
                        e.created_at_utc AS occurred_at_utc,
                        1 AS source_order
                    FROM tool_executions AS e
                    LEFT JOIN approval_decisions AS a ON a.tool_call_id = e.tool_call_id

                    UNION ALL

                    SELECT
                        -w.rowid AS id,
                        'Workflow · ' || w.capability_id || '@' || w.capability_version AS tool_name,
                        w.actual_maximum_risk AS risk,
                        CASE w.status
                            WHEN 'succeeded' THEN 'succeeded'
                            WHEN 'failed' THEN 'failed'
                            WHEN 'attention_required' THEN 'attention_required'
                            WHEN 'interrupted' THEN 'interrupted'
                            WHEN 'cancelled' THEN 'cancelled'
                            WHEN 'needs_information' THEN 'needs_information'
                            ELSE 'running'
                        END AS status,
                        COALESCE(NULLIF(w.redacted_summary, ''), '작업 흐름 실행')
                        || CASE WHEN w.status IN ('failed', 'attention_required', 'interrupted') THEN
                            ' · 실패 단계: '
                            || COALESCE(failed_step.step_id || ' (' || failed_step.tool_name || ')', '실행 전')
                           ELSE '' END
                        || CASE WHEN w.status = 'attention_required'
                            AND failed_step.tool_name = 'mail.send.v1'
                            AND failed_step.status = 'outcome_unknown'
                            AND failed_step.disposition = 'unknown'
                            AND failed_step.fixed_error_code = 'mail.send.outcome_unknown'
                            AND length(failed_step.redacted_summary) =
                                length('메일 서버의 접수 여부를 확인해야 해요. 메시지 식별자: ') + 90
                            AND substr(
                                failed_step.redacted_summary,
                                1,
                                length('메일 서버의 접수 여부를 확인해야 해요. 메시지 식별자: ')) =
                                '메일 서버의 접수 여부를 확인해야 해요. 메시지 식별자: '
                            AND substr(
                                failed_step.redacted_summary,
                                length('메일 서버의 접수 여부를 확인해야 해요. 메시지 식별자: ') + 1,
                                9) = '<ligclaw.'
                            AND substr(
                                failed_step.redacted_summary,
                                length('메일 서버의 접수 여부를 확인해야 해요. 메시지 식별자: ') + 10,
                                64) NOT GLOB '*[^0-9a-f]*'
                            AND substr(
                                failed_step.redacted_summary,
                                length('메일 서버의 접수 여부를 확인해야 해요. 메시지 식별자: ') + 74,
                                17) = '@ligclaw.invalid>' THEN
                            ' · ' || failed_step.redacted_summary
                           ELSE '' END
                        || CASE WHEN w.status = 'attention_required'
                            AND w.fixed_next_action = 'review_outcome'
                            AND w.fixed_error_code = 'workflow.desktop_restart.effect_outcome_unknown'
                            THEN COALESCE((
                                SELECT
                                    ' · 메일 서버의 접수 여부를 확인해야 해요. 메시지 식별자: '
                                    || restart_outbox.smtp_message_id
                                FROM mail_outbox_operations AS restart_outbox
                                JOIN workflow_step_runs AS restart_step
                                  ON restart_step.workflow_run_id = restart_outbox.workflow_run_id
                                 AND restart_step.tool_call_id = restart_outbox.tool_call_id
                                WHERE restart_outbox.workflow_run_id = w.id
                                  AND restart_step.tool_name = 'mail.send.v1'
                                  AND restart_step.effect_class = 'effect'
                                  AND restart_step.status = 'outcome_unknown'
                                  AND restart_step.disposition = 'unknown'
                                  AND restart_step.fixed_error_code =
                                      'workflow.desktop_restart.effect_outcome_unknown'
                                  AND restart_outbox.transport_kind = 'smtp'
                                  AND restart_outbox.smtp_phase = 'data_started'
                                  AND restart_outbox.submission_state = 'outcome_unknown'
                                  AND restart_outbox.delivery_state = 'unknown'
                                  AND length(restart_outbox.smtp_message_id) = 90
                                  AND substr(restart_outbox.smtp_message_id, 1, 9) = '<ligclaw.'
                                  AND substr(restart_outbox.smtp_message_id, 10, 64)
                                      NOT GLOB '*[^0-9a-f]*'
                                  AND substr(restart_outbox.smtp_message_id, 74, 17) =
                                      '@ligclaw.invalid>'
                                  AND substr(restart_outbox.smtp_message_id, 10, 64) =
                                      substr(restart_outbox.operation_id, 5, 64)
                                ORDER BY restart_step.step_sequence DESC,
                                         restart_step.attempt DESC,
                                         restart_outbox.operation_id
                                LIMIT 1
                            ), '')
                           ELSE '' END
                        || ' · 다음 행동: '
                        || CASE w.fixed_next_action
                            WHEN 'provide_information' THEN '추가 정보 제공'
                            WHEN 'retry_from_start' THEN '처음부터 다시 실행'
                            WHEN 'review_outcome' THEN '실행 결과 검토'
                            ELSE COALESCE(w.fixed_next_action, '없음')
                        END AS summary,
                        CAST(NULL AS INTEGER) AS approved,
                        w.updated_at_utc AS occurred_at_utc,
                        0 AS source_order
                    FROM workflow_runs AS w
                    LEFT JOIN workflow_step_runs AS failed_step
                      ON failed_step.tool_call_id = (
                          SELECT step.tool_call_id
                          FROM workflow_step_runs AS step
                          WHERE step.workflow_run_id = w.id
                            AND step.status IN (
                                'retryable_failed', 'not_started', 'failed_known',
                                'interrupted', 'outcome_unknown')
                            AND NOT EXISTS (
                                SELECT 1
                                FROM workflow_step_runs AS later_attempt
                                WHERE later_attempt.workflow_run_id = step.workflow_run_id
                                  AND later_attempt.step_sequence = step.step_sequence
                                  AND later_attempt.attempt > step.attempt
                            )
                          ORDER BY step.step_sequence DESC, step.attempt DESC
                          LIMIT 1
                      )
                )
                SELECT id, tool_name, risk, status, summary, approved, occurred_at_utc
                FROM activity
                ORDER BY occurred_at_utc DESC, source_order, id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new ToolActivitySummary(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5) != 0,
                    DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }
        }, cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<string> CreateUndoAsync(
        string kind,
        string originalPath,
        string currentPath,
        string currentIdentity,
        CancellationToken cancellationToken)
    {
        var undoId = Guid.NewGuid().ToString("N");
        await WithConnectionAsync(
            connection => ExecuteAsync(
                connection,
                """
                INSERT INTO undo_entries(
                    undo_id, kind, original_path, current_path, current_identity, created_at_utc, undone_at_utc)
                VALUES ($undoId, $kind, $originalPath, $currentPath, $currentIdentity, $createdAtUtc, NULL);
                """,
                cancellationToken,
                parameters:
                [
                    ("$undoId", undoId),
                    ("$kind", kind),
                    ("$originalPath", originalPath),
                    ("$currentPath", currentPath),
                    ("$currentIdentity", currentIdentity),
                    ("$createdAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                ]),
            cancellationToken).ConfigureAwait(false);
        return undoId;
    }

    public async Task<FileUndoEntry?> GetPendingUndoAsync(string undoId, CancellationToken cancellationToken)
    {
        FileUndoEntry? result = null;
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT undo_id, kind, original_path, current_path, current_identity, created_at_utc
                FROM undo_entries
                WHERE undo_id = $undoId AND undone_at_utc IS NULL;
                """;
            command.Parameters.AddWithValue("$undoId", undoId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result = new FileUndoEntry(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            }
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task MarkUndoneAsync(string undoId, DateTimeOffset undoneAtUtc, CancellationToken cancellationToken) =>
        WithConnectionAsync(
            connection => ExecuteAsync(
                connection,
                "UPDATE undo_entries SET undone_at_utc = $undoneAtUtc WHERE undo_id = $undoId AND undone_at_utc IS NULL;",
                cancellationToken,
                parameters:
                [
                    ("$undoId", undoId),
                    ("$undoneAtUtc", undoneAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                ]),
            cancellationToken);

    public async Task<IReadOnlyList<UndoActivitySummary>> GetPendingUndoActivityAsync(
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var results = new List<UndoActivitySummary>();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT undo_id, kind, current_path, created_at_utc
                FROM undo_entries
                WHERE undone_at_utc IS NULL
                ORDER BY created_at_utc DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new UndoActivitySummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    Path.GetFileName(reader.GetString(2)),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }
        }, cancellationToken).ConfigureAwait(false);
        return results;
    }

    private Task WithConnectionAsync(Func<SqliteConnection, Task> operation, CancellationToken cancellationToken) =>
        database.WithConnectionAsync(operation, cancellationToken);

    private static Task<int> ExecuteAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        System.Data.Common.DbTransaction? transaction = null,
        params (string Name, object? Value)[] parameters) =>
        ConversationDatabase.ExecuteAsync(connection, commandText, cancellationToken, transaction, parameters);

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
