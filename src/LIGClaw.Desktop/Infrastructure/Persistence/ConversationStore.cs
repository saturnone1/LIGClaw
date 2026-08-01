using System.Globalization;
using System.IO;
using LIGClaw.Application.Agents;
using LIGClaw.Application.Memory;
using LIGClaw.Application.Scheduling;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed record SemanticMemoryCandidate(
    PersonalMemory Memory,
    string? ContentHash,
    float[]? Vector);
internal sealed record ConversationTurn(
    string RunId,
    string UserInput,
    string AssistantText,
    string Status,
    DateTimeOffset CreatedAtUtc);

internal sealed partial class ConversationStore : IConversationRepository, IToolAuditSink, ICapabilityGrantStore, IUndoJournal, IMemoryRepository, IScheduleRepository, IAgentJobRepository, ILocalSubagentRepository, IDisposable
{
    private readonly ConversationDatabase _database;
    private readonly ConversationRepository _conversations;

    public ConversationStore(string databasePath)
    {
        _database = new ConversationDatabase(databasePath);
        _conversations = new ConversationRepository(_database);
    }

    public string DatabasePath => _database.DatabasePath;
    public IConversationRepository Conversations => _conversations;

    public static ConversationStore CreateDefault()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LIGClaw",
            "Data");
        return new ConversationStore(Path.Combine(directory, "ligclaw.db"));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _database.InitializeAsync(cancellationToken);

    public Task StartRunAsync(string conversationId, string runId, string userInput, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default) =>
        _conversations.StartRunAsync(conversationId, runId, userInput, startedAtUtc, cancellationToken);

    public Task AppendEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken = default) =>
        _conversations.AppendEventAsync(agentEvent, cancellationToken);

    public Task MarkRunAsync(string conversationId, string runId, string status, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) =>
        _conversations.MarkRunAsync(conversationId, runId, status, updatedAtUtc, cancellationToken);

    public Task MarkRunningConversationsInterruptedAsync(CancellationToken cancellationToken = default) =>
        _conversations.MarkRunningConversationsInterruptedAsync(cancellationToken);

    public async Task<MemoryUpsertResult> UpsertAsync(
        PersonalMemoryDraft draft,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (!PersonalMemoryPolicy.IsValid(draft, nowUtc))
            throw new ArgumentException("기억 값 또는 정책이 올바르지 않습니다.", nameof(draft));
        PersonalMemory? memory = null;
        var created = false;
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var existing = await ReadMemoryByKeyAsync(connection, transaction, draft.Kind, draft.Key, cancellationToken)
                .ConfigureAwait(false);
            created = existing is null;
            var id = existing?.Id ?? Guid.NewGuid().ToString("N");
            var createdAt = existing?.CreatedAtUtc ?? nowUtc;
            await ExecuteAsync(
                connection,
                """
                INSERT INTO memories(id, kind, key, value, sensitivity, source, created_at_utc, updated_at_utc, expires_at_utc)
                VALUES ($id, $kind, $key, $value, $sensitivity, $source, $createdAtUtc, $updatedAtUtc, $expiresAtUtc)
                ON CONFLICT(kind, key) DO UPDATE SET
                    value = excluded.value,
                    sensitivity = excluded.sensitivity,
                    source = excluded.source,
                    updated_at_utc = excluded.updated_at_utc,
                    expires_at_utc = excluded.expires_at_utc;
                """,
                cancellationToken,
                transaction,
                ("$id", id),
                ("$kind", draft.Kind),
                ("$key", draft.Key),
                ("$value", draft.Value),
                ("$sensitivity", draft.Sensitivity),
                ("$source", draft.Source),
                ("$createdAtUtc", createdAt.ToString("O", CultureInfo.InvariantCulture)),
                ("$updatedAtUtc", nowUtc.ToString("O", CultureInfo.InvariantCulture)),
                ("$expiresAtUtc", draft.ExpiresAtUtc?.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            memory = new PersonalMemory(
                id, draft.Kind, draft.Key, draft.Value, draft.Sensitivity, draft.Source,
                createdAt, nowUtc, draft.ExpiresAtUtc);
        }, cancellationToken).ConfigureAwait(false);
        return new MemoryUpsertResult(memory!, created);
    }

    public async Task<IReadOnlyList<PersonalMemory>> ListAsync(
        string? query,
        int limit,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        var results = new List<PersonalMemory>();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, kind, key, value, sensitivity, source, created_at_utc, updated_at_utc, expires_at_utc
                FROM memories
                WHERE (expires_at_utc IS NULL OR expires_at_utc > $nowUtc)
                  AND ($query = '' OR key LIKE $pattern ESCAPE '\' OR value LIKE $pattern ESCAPE '\')
                ORDER BY updated_at_utc DESC
                LIMIT $limit;
                """;
            var normalizedQuery = query?.Trim() ?? string.Empty;
            var escaped = normalizedQuery.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            command.Parameters.AddWithValue("$nowUtc", nowUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$query", normalizedQuery);
            command.Parameters.AddWithValue("$pattern", $"%{escaped}%");
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadMemory(reader));
        }, cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<PersonalMemory?> GetAsync(
        string memoryId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        PersonalMemory? result = null;
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, kind, key, value, sensitivity, source, created_at_utc, updated_at_utc, expires_at_utc
                FROM memories
                WHERE id = $id AND (expires_at_utc IS NULL OR expires_at_utc > $nowUtc);
                """;
            command.Parameters.AddWithValue("$id", memoryId);
            command.Parameters.AddWithValue("$nowUtc", nowUtc.ToString("O", CultureInfo.InvariantCulture));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result = ReadMemory(reader);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<PersonalMemory>> ListForManagementAsync(
        string? query,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        var results = new List<PersonalMemory>();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, kind, key, value, sensitivity, source, created_at_utc, updated_at_utc, expires_at_utc
                FROM memories
                WHERE $query = '' OR key LIKE $pattern ESCAPE '\' OR value LIKE $pattern ESCAPE '\'
                ORDER BY updated_at_utc DESC
                LIMIT $limit OFFSET $offset;
                """;
            var normalizedQuery = query?.Trim() ?? string.Empty;
            var escaped = normalizedQuery.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            command.Parameters.AddWithValue("$query", normalizedQuery);
            command.Parameters.AddWithValue("$pattern", $"%{escaped}%");
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadMemory(reader));
        }, cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<PersonalMemory?> GetForManagementAsync(
        string memoryId,
        CancellationToken cancellationToken)
    {
        PersonalMemory? result = null;
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, kind, key, value, sensitivity, source, created_at_utc, updated_at_utc, expires_at_utc
                FROM memories
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", memoryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result = ReadMemory(reader);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<bool> DeleteAsync(string memoryId, CancellationToken cancellationToken)
    {
        var deleted = false;
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM memories WHERE id = $id;";
            command.Parameters.AddWithValue("$id", memoryId);
            deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }, cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    public async Task<IReadOnlyList<SemanticMemoryCandidate>> GetSemanticMemoryCandidatesAsync(
        string modelKey,
        DateTimeOffset nowUtc,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelKey);
        if (modelKey.Length > 128) throw new ArgumentOutOfRangeException(nameof(modelKey));
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        var results = new List<SemanticMemoryCandidate>();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT m.id, m.kind, m.key, m.value, m.sensitivity, m.source,
                       m.created_at_utc, m.updated_at_utc, m.expires_at_utc,
                       e.content_hash, e.dimensions, e.vector
                FROM memories m
                LEFT JOIN memory_embeddings e
                  ON e.memory_id = m.id AND e.model_key = $modelKey
                WHERE m.expires_at_utc IS NULL OR m.expires_at_utc > $nowUtc
                ORDER BY m.updated_at_utc DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$modelKey", modelKey);
            command.Parameters.AddWithValue("$nowUtc", Format(nowUtc));
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var vector = reader.IsDBNull(10) || reader.IsDBNull(11)
                    ? null
                    : TryDeserializeVector(reader.GetInt32(10), (byte[])reader.GetValue(11));
                results.Add(new SemanticMemoryCandidate(
                    ReadMemory(reader),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    vector));
            }
        }, cancellationToken).ConfigureAwait(false);
        return results;
    }

    public Task UpsertMemoryEmbeddingAsync(
        string memoryId,
        string modelKey,
        string contentHash,
        IReadOnlyList<float> vector,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        if (memoryId.Length > 128 || modelKey.Length > 128 || contentHash.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(memoryId));
        if (vector.Count is < 1 or > 8_192 || vector.Any(value => !float.IsFinite(value)))
            throw new ArgumentOutOfRangeException(nameof(vector));
        var bytes = new byte[checked(vector.Count * sizeof(float))];
        var values = vector as float[] ?? vector.ToArray();
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO memory_embeddings(memory_id, model_key, content_hash, dimensions, vector, updated_at_utc)
                VALUES ($memoryId, $modelKey, $contentHash, $dimensions, $vector, $updatedAtUtc)
                ON CONFLICT(memory_id, model_key) DO UPDATE SET
                    content_hash = excluded.content_hash,
                    dimensions = excluded.dimensions,
                    vector = excluded.vector,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            command.Parameters.AddWithValue("$memoryId", memoryId);
            command.Parameters.AddWithValue("$modelKey", modelKey);
            command.Parameters.AddWithValue("$contentHash", contentHash);
            command.Parameters.AddWithValue("$dimensions", vector.Count);
            command.Parameters.AddWithValue("$vector", bytes);
            command.Parameters.AddWithValue("$updatedAtUtc", Format(updatedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public async Task<string?> ResolveValueAsync(
        string kind,
        string key,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        string? result = null;
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT value
                FROM memories
                WHERE kind = $kind AND key = $key
                  AND (expires_at_utc IS NULL OR expires_at_utc > $nowUtc);
                """;
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$nowUtc", Format(nowUtc));
            result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<ScheduledNotification> CreateAsync(
        NotificationScheduleDraft draft,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (!NotificationSchedulePolicy.IsValid(draft))
            throw new ArgumentException("예약 정보가 올바르지 않습니다.", nameof(draft));
        var first = NotificationSchedulePolicy.FirstOccurrenceUtc(draft);
        var id = Guid.NewGuid().ToString("N");
        var template = CreateScheduledNotification(id, draft, first, nowUtc);
        var next = first > nowUtc ? first : NotificationSchedulePolicy.NextOccurrenceAfter(template, nowUtc);
        if (next is null) throw new ArgumentOutOfRangeException(nameof(draft), "단발 예약 시각은 미래여야 합니다.");
        var created = template with { NextRunAtUtc = next };
        await WithConnectionAsync(
            connection => InsertScheduledJobAsync(connection, created, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task<ScheduledNotification?> UpdateAsync(
        string jobId,
        NotificationScheduleDraft draft,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (!NotificationSchedulePolicy.IsValid(draft))
            throw new ArgumentException("예약 정보가 올바르지 않습니다.", nameof(draft));
        ScheduledNotification? updated = null;
        await WithConnectionAsync(async connection =>
        {
            var existing = await GetScheduledJobAsync(connection, null, jobId, cancellationToken).ConfigureAwait(false);
            if (existing is null || existing.Status == ScheduleValues.Running) return;
            var first = NotificationSchedulePolicy.FirstOccurrenceUtc(draft);
            var template = CreateScheduledNotification(jobId, draft, first, existing.CreatedAtUtc);
            var next = first > nowUtc ? first : NotificationSchedulePolicy.NextOccurrenceAfter(template, nowUtc);
            if (next is null) throw new ArgumentOutOfRangeException(nameof(draft), "단발 예약 시각은 미래여야 합니다.");
            updated = template with { NextRunAtUtc = next, UpdatedAtUtc = nowUtc };
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE scheduled_jobs
                SET title = $title, message = $message, start_local = $startLocal,
                    time_zone_id = $timeZoneId, recurrence = $recurrence,
                    recurrence_interval = $interval, misfire_policy = $misfirePolicy,
                    source = $source, status = 'pending', next_run_at_utc = $nextRunAtUtc,
                    lease_until_utc = NULL, active_run_id = NULL, updated_at_utc = $updatedAtUtc
                WHERE id = $id AND status <> 'running';
                """;
            AddScheduledJobParameters(command, updated);
            command.Parameters.AddWithValue("$id", jobId);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<IReadOnlyList<ScheduledNotification>> ListAsync(
        bool includeInactive,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        var results = new List<ScheduledNotification>();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT id, title, message, start_local, time_zone_id, recurrence, recurrence_interval,
                       misfire_policy, source, status, next_run_at_utc, created_at_utc, updated_at_utc
                FROM scheduled_jobs
                {(includeInactive ? string.Empty : "WHERE status IN ('pending', 'running', 'awaiting_decision')")}
                ORDER BY CASE WHEN next_run_at_utc IS NULL THEN 1 ELSE 0 END, next_run_at_utc, updated_at_utc DESC
                LIMIT $limit OFFSET $offset;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadScheduledJob(reader));
        }, cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<ScheduledNotification?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        ScheduledNotification? result = null;
        await WithConnectionAsync(async connection =>
        {
            result = await GetScheduledJobAsync(connection, null, jobId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<bool> CancelAsync(string jobId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var cancelled = false;
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE scheduled_jobs
                SET status = 'cancelled', next_run_at_utc = NULL, lease_until_utc = NULL,
                    active_run_id = NULL, updated_at_utc = $nowUtc
                WHERE id = $id AND status IN ('pending', 'awaiting_decision');
                """;
            command.Parameters.AddWithValue("$id", jobId);
            command.Parameters.AddWithValue("$nowUtc", Format(nowUtc));
            cancelled = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }, cancellationToken).ConfigureAwait(false);
        return cancelled;
    }

    public async Task<ScheduleRecoveryResult> ReconcileOnStartupAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ScheduleRecoveryResult result = new(0, 0, 0);
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                UPDATE job_runs
                SET status = 'interrupted', completed_at_utc = $nowUtc, error_code = 'desktop_restart'
                WHERE status = 'running';
                """,
                cancellationToken,
                transaction,
                ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                UPDATE scheduled_jobs
                SET status = 'pending', lease_until_utc = NULL, active_run_id = NULL, updated_at_utc = $nowUtc
                WHERE status = 'running';
                """,
                cancellationToken,
                transaction,
                ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);

            result = await ReconcilePendingJobsAsync(
                connection, transaction, nowUtc, nowUtc.AddMinutes(-1), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<ScheduleRecoveryResult> ReconcileOverdueAsync(
        DateTimeOffset nowUtc,
        DateTimeOffset dueBeforeUtc,
        CancellationToken cancellationToken)
    {
        if (dueBeforeUtc > nowUtc) throw new ArgumentOutOfRangeException(nameof(dueBeforeUtc));
        ScheduleRecoveryResult result = new(0, 0, 0);
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                UPDATE job_runs
                SET status = 'interrupted', completed_at_utc = $nowUtc, error_code = 'lease_expired'
                WHERE status = 'running'
                  AND id IN (
                      SELECT active_run_id FROM scheduled_jobs
                      WHERE status = 'running' AND lease_until_utc <= $nowUtc
                  );
                """,
                cancellationToken,
                transaction,
                ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                UPDATE scheduled_jobs
                SET status = 'pending', lease_until_utc = NULL, active_run_id = NULL, updated_at_utc = $nowUtc
                WHERE status = 'running' AND lease_until_utc <= $nowUtc;
                """,
                cancellationToken,
                transaction,
                ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);
            result = await ReconcilePendingJobsAsync(
                connection, transaction, nowUtc, dueBeforeUtc, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<ScheduledJobClaim?> TryClaimDueAsync(
        DateTimeOffset nowUtc,
        DateTimeOffset leaseUntilUtc,
        CancellationToken cancellationToken)
    {
        ScheduledJobClaim? result = null;
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            ScheduledNotification? job;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText =
                    """
                    SELECT id, title, message, start_local, time_zone_id, recurrence, recurrence_interval,
                           misfire_policy, source, status, next_run_at_utc, created_at_utc, updated_at_utc
                    FROM scheduled_jobs
                    WHERE status = 'pending' AND next_run_at_utc <= $nowUtc
                    ORDER BY next_run_at_utc
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$nowUtc", Format(nowUtc));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                job = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadScheduledJob(reader) : null;
            }
            if (job is not null && job.NextRunAtUtc is not null)
            {
                var runId = Guid.NewGuid().ToString("N");
                var changed = await ExecuteCountAsync(
                    connection,
                    """
                    UPDATE scheduled_jobs
                    SET status = 'running', lease_until_utc = $leaseUntilUtc,
                        active_run_id = $runId, updated_at_utc = $nowUtc
                    WHERE id = $id AND status = 'pending';
                    """,
                    cancellationToken,
                    transaction,
                    ("$leaseUntilUtc", Format(leaseUntilUtc)), ("$runId", runId),
                    ("$nowUtc", Format(nowUtc)), ("$id", job.Id)).ConfigureAwait(false);
                if (changed == 1)
                {
                    await ExecuteAsync(
                        connection,
                        """
                        INSERT INTO job_runs(id, job_id, scheduled_at_utc, started_at_utc, status)
                        VALUES ($runId, $jobId, $scheduledAtUtc, $nowUtc, 'running');
                        """,
                        cancellationToken,
                        transaction,
                        ("$runId", runId), ("$jobId", job.Id),
                        ("$scheduledAtUtc", Format(job.NextRunAtUtc.Value)), ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);
                    result = new ScheduledJobClaim(job with { Status = ScheduleValues.Running }, runId, job.NextRunAtUtc.Value);
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task CompleteRunAsync(
        string runId,
        DateTimeOffset completedAtUtc,
        bool succeeded,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            ScheduledNotification? job = null;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText =
                    """
                    SELECT j.id, j.title, j.message, j.start_local, j.time_zone_id, j.recurrence,
                           j.recurrence_interval, j.misfire_policy, j.source, j.status,
                           j.next_run_at_utc, j.created_at_utc, j.updated_at_utc
                    FROM scheduled_jobs j
                    JOIN job_runs r ON r.job_id = j.id
                    WHERE r.id = $runId AND r.status = 'running' AND j.active_run_id = $runId;
                    """;
                command.Parameters.AddWithValue("$runId", runId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) job = ReadScheduledJob(reader);
            }
            if (job is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            await ExecuteAsync(
                connection,
                """
                UPDATE job_runs
                SET status = $status, completed_at_utc = $completedAtUtc, error_code = $errorCode
                WHERE id = $runId AND status = 'running';
                """,
                cancellationToken,
                transaction,
                ("$status", succeeded ? "succeeded" : "failed"), ("$completedAtUtc", Format(completedAtUtc)),
                ("$errorCode", errorCode), ("$runId", runId)).ConfigureAwait(false);
            var next = NotificationSchedulePolicy.NextOccurrenceAfter(job, completedAtUtc);
            var status = next is null ? (succeeded ? ScheduleValues.Completed : ScheduleValues.Failed) : ScheduleValues.Pending;
            await UpdateScheduleStateAsync(connection, transaction, job.Id, status, next, completedAtUtc, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ResolveMisfireAsync(
        string jobId,
        bool runNow,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var resolved = false;
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var job = await GetScheduledJobAsync(connection, transaction, jobId, cancellationToken).ConfigureAwait(false);
            if (job?.Status != ScheduleValues.AwaitingDecision)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            DateTimeOffset? next;
            if (runNow)
            {
                next = nowUtc;
            }
            else
            {
                await RecordSkippedRunAsync(connection, transaction, job, nowUtc, cancellationToken).ConfigureAwait(false);
                next = NotificationSchedulePolicy.NextOccurrenceAfter(job, nowUtc);
            }
            await UpdateScheduleStateAsync(
                connection, transaction, job.Id, next is null ? ScheduleValues.Completed : ScheduleValues.Pending,
                next, nowUtc, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            resolved = true;
        }, cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    public Task<IReadOnlyList<ConversationSummary>> GetRecentConversationsAsync(int limit = 12, CancellationToken cancellationToken = default) =>
        _conversations.GetRecentConversationsAsync(limit, cancellationToken);

    public Task<IReadOnlyList<ConversationSummary>> SearchConversationsAsync(string query, int limit = 50, CancellationToken cancellationToken = default) =>
        _conversations.SearchConversationsAsync(query, limit, cancellationToken);

    public Task<string> GetTranscriptAsync(string conversationId, CancellationToken cancellationToken = default) =>
        _conversations.GetTranscriptAsync(conversationId, cancellationToken);

    public Task<IReadOnlyList<ConversationTurn>> GetConversationTurnsAsync(string conversationId, CancellationToken cancellationToken = default) =>
        _conversations.GetConversationTurnsAsync(conversationId, cancellationToken);

    public Task<IReadOnlyList<ConversationContextMessage>> GetConversationContextAsync(string conversationId, int maximumMessages = 40, int maximumCharacters = 64_000, CancellationToken cancellationToken = default) =>
        _conversations.GetConversationContextAsync(conversationId, maximumMessages, maximumCharacters, cancellationToken);

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
                SELECT e.id, e.tool_name, e.risk, e.status, e.summary, a.approved, e.created_at_utc
                FROM tool_executions e
                LEFT JOIN approval_decisions a ON a.tool_call_id = e.tool_call_id
                ORDER BY e.created_at_utc DESC, e.id DESC
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

    private static ScheduledNotification CreateScheduledNotification(
        string id,
        NotificationScheduleDraft draft,
        DateTimeOffset firstOccurrenceUtc,
        DateTimeOffset createdAtUtc) => new(
            id,
            draft.Title,
            draft.Message,
            draft.StartLocal,
            draft.TimeZoneId,
            draft.Recurrence,
            draft.Interval,
            draft.MisfirePolicy,
            draft.Source,
            ScheduleValues.Pending,
            firstOccurrenceUtc,
            createdAtUtc,
            createdAtUtc);

    private static async Task InsertScheduledJobAsync(
        SqliteConnection connection,
        ScheduledNotification job,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO scheduled_jobs(
                id, title, message, start_local, time_zone_id, recurrence, recurrence_interval,
                misfire_policy, source, status, next_run_at_utc, created_at_utc, updated_at_utc)
            VALUES (
                $id, $title, $message, $startLocal, $timeZoneId, $recurrence, $interval,
                $misfirePolicy, $source, $status, $nextRunAtUtc, $createdAtUtc, $updatedAtUtc);
            """;
        command.Parameters.AddWithValue("$id", job.Id);
        AddScheduledJobParameters(command, job);
        command.Parameters.AddWithValue("$status", job.Status);
        command.Parameters.AddWithValue("$createdAtUtc", Format(job.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddScheduledJobParameters(SqliteCommand command, ScheduledNotification job)
    {
        command.Parameters.AddWithValue("$title", job.Title);
        command.Parameters.AddWithValue("$message", job.Message);
        command.Parameters.AddWithValue("$startLocal", job.StartLocal.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$timeZoneId", job.TimeZoneId);
        command.Parameters.AddWithValue("$recurrence", job.Recurrence);
        command.Parameters.AddWithValue("$interval", job.Interval);
        command.Parameters.AddWithValue("$misfirePolicy", job.MisfirePolicy);
        command.Parameters.AddWithValue("$source", job.Source);
        command.Parameters.AddWithValue("$nextRunAtUtc", job.NextRunAtUtc is null ? DBNull.Value : Format(job.NextRunAtUtc.Value));
        command.Parameters.AddWithValue("$updatedAtUtc", Format(job.UpdatedAtUtc));
    }

    private static async Task<ScheduledNotification?> GetScheduledJobAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        string jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText =
            """
            SELECT id, title, message, start_local, time_zone_id, recurrence, recurrence_interval,
                   misfire_policy, source, status, next_run_at_utc, created_at_utc, updated_at_utc
            FROM scheduled_jobs
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadScheduledJob(reader) : null;
    }

    private static ScheduledNotification ReadScheduledJob(System.Data.Common.DbDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        DateTime.SpecifyKind(
            DateTime.ParseExact(reader.GetString(3), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeKind.Unspecified),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetInt32(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10)),
        ParseTimestamp(reader.GetString(11)),
        ParseTimestamp(reader.GetString(12)));

    private static async Task<ScheduleRecoveryResult> ReconcilePendingJobsAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        DateTimeOffset nowUtc,
        DateTimeOffset dueBeforeUtc,
        CancellationToken cancellationToken)
    {
        var skipped = 0;
        var awaiting = 0;
        var ready = 0;
        var due = new List<ScheduledNotification>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                SELECT id, title, message, start_local, time_zone_id, recurrence, recurrence_interval,
                       misfire_policy, source, status, next_run_at_utc, created_at_utc, updated_at_utc
                FROM scheduled_jobs
                WHERE status = 'pending' AND next_run_at_utc <= $dueBeforeUtc
                ORDER BY next_run_at_utc;
                """;
            command.Parameters.AddWithValue("$dueBeforeUtc", Format(dueBeforeUtc));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) due.Add(ReadScheduledJob(reader));
        }
        foreach (var job in due)
        {
            if (job.MisfirePolicy == ScheduleValues.RunOnceOnResume)
            {
                ready++;
                continue;
            }
            if (job.MisfirePolicy == ScheduleValues.Ask)
            {
                await UpdateScheduleStateAsync(
                    connection, transaction, job.Id, ScheduleValues.AwaitingDecision, job.NextRunAtUtc, nowUtc,
                    cancellationToken).ConfigureAwait(false);
                awaiting++;
                continue;
            }
            await RecordSkippedRunAsync(connection, transaction, job, nowUtc, cancellationToken).ConfigureAwait(false);
            var next = NotificationSchedulePolicy.NextOccurrenceAfter(job, nowUtc);
            await UpdateScheduleStateAsync(
                connection, transaction, job.Id, next is null ? ScheduleValues.Completed : ScheduleValues.Pending,
                next, nowUtc, cancellationToken).ConfigureAwait(false);
            skipped++;
        }
        return new ScheduleRecoveryResult(skipped, awaiting, ready);
    }

    private static async Task UpdateScheduleStateAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string jobId,
        string status,
        DateTimeOffset? nextRunAtUtc,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            connection,
            """
            UPDATE scheduled_jobs
            SET status = $status, next_run_at_utc = $nextRunAtUtc, lease_until_utc = NULL,
                active_run_id = NULL, updated_at_utc = $updatedAtUtc
            WHERE id = $id;
            """,
            cancellationToken,
            transaction,
            ("$status", status), ("$nextRunAtUtc", nextRunAtUtc is null ? null : Format(nextRunAtUtc.Value)),
            ("$updatedAtUtc", Format(updatedAtUtc)), ("$id", jobId)).ConfigureAwait(false);

    private static async Task RecordSkippedRunAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        ScheduledNotification job,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            connection,
            """
            INSERT INTO job_runs(id, job_id, scheduled_at_utc, completed_at_utc, status)
            VALUES ($id, $jobId, $scheduledAtUtc, $nowUtc, 'skipped');
            """,
            cancellationToken,
            transaction,
            ("$id", Guid.NewGuid().ToString("N")), ("$jobId", job.Id),
            ("$scheduledAtUtc", Format(job.NextRunAtUtc ?? nowUtc)), ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);

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

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static async Task<PersonalMemory?> ReadMemoryByKeyAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string kind,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT id, kind, key, value, sensitivity, source, created_at_utc, updated_at_utc, expires_at_utc
            FROM memories
            WHERE kind = $kind AND key = $key;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMemory(reader) : null;
    }

    private static PersonalMemory ReadMemory(System.Data.Common.DbDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(8)
            ? null
            : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static float[]? TryDeserializeVector(int dimensions, byte[] bytes)
    {
        if (dimensions is < 1 or > 8_192 || bytes.Length != dimensions * sizeof(float)) return null;
        var vector = new float[dimensions];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector.All(float.IsFinite) ? vector : null;
    }

    private Task WithConnectionAsync(
        Func<SqliteConnection, Task> operation,
        CancellationToken cancellationToken) =>
        _database.WithConnectionAsync(operation, cancellationToken);

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        System.Data.Common.DbTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _database.Dispose();
}
