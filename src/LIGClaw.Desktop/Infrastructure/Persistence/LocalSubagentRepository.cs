using System.Globalization;
using LIGClaw.Application.Agents;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed class LocalSubagentRepository(ConversationDatabase database) : ILocalSubagentRepository
{
    public async Task<IReadOnlyList<SubagentTaskRecord>> CreateSubagentBatchAsync(
        string batchId,
        SubagentBatchDraft draft,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (batchId.Length != 32 || !batchId.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException("Batch ID가 올바르지 않습니다.", nameof(batchId));
        if (!SubagentPolicy.IsValid(draft)) throw new ArgumentException("하위 Agent 작업이 올바르지 않습니다.", nameof(draft));
        var records = draft.Tasks.Select((task, ordinal) => new SubagentTaskRecord(
            batchId, Guid.NewGuid().ToString("N"), ordinal, task.Title, task.Prompt,
            "running", null, null, nowUtc, nowUtc)).ToArray();
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                INSERT INTO subagent_batches(
                    id, parent_conversation_id, parent_run_id, parent_tool_call_id, model_profile_id,
                    max_risk, max_runtime_seconds, result_max_characters, reason, status,
                    created_at_utc, updated_at_utc)
                VALUES ($id, $parentConversationId, $parentRunId, $parentToolCallId, $modelProfileId,
                    $maxRisk, $maxRuntimeSeconds, $resultMaxCharacters, $reason, 'running',
                    $nowUtc, $nowUtc);
                """,
                cancellationToken,
                transaction,
                ("$id", batchId), ("$parentConversationId", draft.ParentConversationId),
                ("$parentRunId", draft.ParentRunId), ("$parentToolCallId", draft.ParentToolCallId),
                ("$modelProfileId", draft.ModelProfileId), ("$maxRisk", draft.MaxRisk),
                ("$maxRuntimeSeconds", draft.MaxRuntimeSeconds),
                ("$resultMaxCharacters", draft.ResultMaxCharacters), ("$reason", draft.Reason),
                ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);
            foreach (var record in records)
            {
                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO subagent_tasks(
                        child_run_id, batch_id, ordinal, title, prompt, status, created_at_utc, updated_at_utc)
                    VALUES ($childRunId, $batchId, $ordinal, $title, $prompt, 'running', $nowUtc, $nowUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$childRunId", record.ChildRunId), ("$batchId", batchId),
                    ("$ordinal", record.Ordinal), ("$title", record.Title), ("$prompt", record.Prompt),
                    ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return records;
    }

    public Task CompleteSubagentTaskAsync(
        string childRunId,
        bool succeeded,
        string? resultText,
        string? errorCode,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            string? batchId = null;
            var maximum = 0;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                command.CommandText =
                    """
                    SELECT t.batch_id, b.result_max_characters
                    FROM subagent_tasks t JOIN subagent_batches b ON b.id = t.batch_id
                    WHERE t.child_run_id = $childRunId AND t.status = 'running';
                    """;
                command.Parameters.AddWithValue("$childRunId", childRunId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    batchId = reader.GetString(0);
                    maximum = reader.GetInt32(1);
                }
            }
            if (batchId is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            var bounded = resultText;
            if (bounded?.Length > maximum) bounded = string.Concat(bounded.AsSpan(0, maximum), "…");
            await ExecuteAsync(
                connection,
                """
                UPDATE subagent_tasks
                SET status = $status, result_text = $resultText, error_code = $errorCode, updated_at_utc = $nowUtc
                WHERE child_run_id = $childRunId AND status = 'running';
                """,
                cancellationToken,
                transaction,
                ("$status", succeeded ? "succeeded" : "failed"), ("$resultText", bounded),
                ("$errorCode", errorCode), ("$nowUtc", Format(completedAtUtc)), ("$childRunId", childRunId))
                .ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                UPDATE subagent_batches
                SET status = CASE
                        WHEN EXISTS(SELECT 1 FROM subagent_tasks WHERE batch_id = $batchId AND status = 'running') THEN 'running'
                        WHEN EXISTS(SELECT 1 FROM subagent_tasks WHERE batch_id = $batchId AND status != 'succeeded') THEN 'failed'
                        ELSE 'succeeded'
                    END,
                    updated_at_utc = $nowUtc
                WHERE id = $batchId;
                """,
                cancellationToken,
                transaction,
                ("$batchId", batchId), ("$nowUtc", Format(completedAtUtc))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task ReconcileSubagentTasksOnStartupAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                UPDATE subagent_tasks
                SET status = 'interrupted', error_code = 'desktop_restart', updated_at_utc = $nowUtc
                WHERE status = 'running';
                """,
                cancellationToken,
                transaction,
                ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                UPDATE subagent_batches
                SET status = 'interrupted', updated_at_utc = $nowUtc
                WHERE status = 'running';
                """,
                cancellationToken,
                transaction,
                ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public async Task<IReadOnlyList<SubagentTaskRecord>> ListSubagentTasksAsync(
        string batchId,
        CancellationToken cancellationToken)
    {
        var results = new List<SubagentTaskRecord>();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT batch_id, child_run_id, ordinal, title, prompt, status, result_text, error_code,
                       created_at_utc, updated_at_utc
                FROM subagent_tasks WHERE batch_id = $batchId ORDER BY ordinal;
                """;
            command.Parameters.AddWithValue("$batchId", batchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                results.Add(new SubagentTaskRecord(
                    reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7), ParseTimestamp(reader.GetString(8)), ParseTimestamp(reader.GetString(9))));
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
