using System.Globalization;
using LIGClaw.Application.Scheduling;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed partial class ConversationStore
{
    public async Task<ScheduledAgentJob> CreateAgentJobAsync(
        AgentJobDraft draft,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (!AgentJobPolicy.IsValid(draft))
            throw new ArgumentException("Agent job 정보가 올바르지 않습니다.", nameof(draft));
        var first = AgentJobPolicy.FirstOccurrenceUtc(draft);
        if (first <= nowUtc && draft.Recurrence == ScheduleValues.Once)
            throw new ArgumentOutOfRangeException(nameof(draft), "단발 Agent job 시각은 미래여야 합니다.");
        var job = new ScheduledAgentJob(
            Guid.NewGuid().ToString("N"), draft.Title, draft.Prompt, draft.StartLocal, draft.TimeZoneId,
            draft.Recurrence, draft.Interval, draft.MisfirePolicy, draft.ModelProfileId,
            draft.MaxRuntimeSeconds, draft.MaxAttempts, draft.ResultMaxCharacters, draft.Source,
            ScheduleValues.Pending, 0, first > nowUtc ? first : null, nowUtc, nowUtc);
        if (job.NextRunAtUtc is null) job = job with { NextRunAtUtc = AgentJobPolicy.NextOccurrenceAfter(job, nowUtc) };
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO agent_jobs(
                    id, title, prompt, start_local, time_zone_id, recurrence, recurrence_interval,
                    misfire_policy, model_profile_id, max_runtime_seconds, max_attempts,
                    result_max_characters, source, status, attempt_count, next_run_at_utc,
                    created_at_utc, updated_at_utc)
                VALUES ($id, $title, $prompt, $startLocal, $timeZoneId, $recurrence, $interval,
                    $misfirePolicy, $modelProfileId, $maxRuntimeSeconds, $maxAttempts,
                    $resultMaxCharacters, $source, $status, 0, $nextRunAtUtc, $createdAtUtc, $updatedAtUtc);
                """;
            AddAgentJobParameters(command, job);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return job;
    }

    public async Task<IReadOnlyList<ScheduledAgentJob>> ListAgentJobsAsync(
        bool includeInactive,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        var results = new List<ScheduledAgentJob>();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                {AgentJobSelect}
                {(includeInactive ? string.Empty : "WHERE status IN ('pending', 'running', 'awaiting_decision')")}
                ORDER BY CASE WHEN next_run_at_utc IS NULL THEN 1 ELSE 0 END, next_run_at_utc, updated_at_utc DESC
                LIMIT $limit OFFSET $offset;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadAgentJob(reader));
        }, cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<ScheduledAgentJob?> GetAgentJobAsync(string jobId, CancellationToken cancellationToken)
    {
        ScheduledAgentJob? result = null;
        await WithConnectionAsync(async connection =>
        {
            result = await ReadAgentJobAsync(connection, null, jobId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<AgentJobRunRecord>> ListAgentJobRunsAsync(
        string jobId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var results = new List<AgentJobRunRecord>();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, job_id, scheduled_at_utc, started_at_utc, completed_at_utc,
                       status, attempt, result_text, error_code
                FROM agent_job_runs
                WHERE job_id = $jobId
                ORDER BY started_at_utc DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$jobId", jobId);
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new AgentJobRunRecord(
                    reader.GetString(0), reader.GetString(1), ParseTimestamp(reader.GetString(2)),
                    ParseTimestamp(reader.GetString(3)), reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
                    reader.GetString(5), reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }, cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<bool> CancelAgentJobAsync(
        string jobId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var cancelled = false;
        await WithConnectionAsync(async connection =>
        {
            cancelled = await ExecuteCountAsync(
                connection,
                """
                UPDATE agent_jobs
                SET status = 'cancelled', next_run_at_utc = NULL, lease_until_utc = NULL,
                    active_run_id = NULL, updated_at_utc = $nowUtc
                WHERE id = $id AND status IN ('pending', 'awaiting_decision', 'paused');
                """,
                cancellationToken,
                parameters: [("$nowUtc", Format(nowUtc)), ("$id", jobId)]).ConfigureAwait(false) == 1;
        }, cancellationToken).ConfigureAwait(false);
        return cancelled;
    }

    public async Task<bool> SetAgentJobPausedAsync(string jobId, bool paused, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var changed = false;
        await WithConnectionAsync(async connection =>
        {
            changed = await ExecuteCountAsync(
                connection,
                paused
                    ? "UPDATE agent_jobs SET status = 'paused', updated_at_utc = $nowUtc WHERE id = $id AND status IN ('pending', 'awaiting_decision');"
                    : "UPDATE agent_jobs SET status = 'pending', next_run_at_utc = CASE WHEN next_run_at_utc IS NULL OR next_run_at_utc < $nowUtc THEN $nowUtc ELSE next_run_at_utc END, updated_at_utc = $nowUtc WHERE id = $id AND status = 'paused';",
                cancellationToken,
                parameters: [("$nowUtc", Format(nowUtc)), ("$id", jobId)]).ConfigureAwait(false) == 1;
        }, cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<bool> RetryAgentJobNowAsync(string jobId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var changed = false;
        await WithConnectionAsync(async connection =>
        {
            changed = await ExecuteCountAsync(
                connection,
                """
                UPDATE agent_jobs
                SET status = 'pending', next_run_at_utc = $nowUtc, attempt_count = 0,
                    lease_until_utc = NULL, active_run_id = NULL, updated_at_utc = $nowUtc
                WHERE id = $id AND status IN ('failed', 'completed', 'cancelled');
                """,
                cancellationToken,
                parameters: [("$nowUtc", Format(nowUtc)), ("$id", jobId)]).ConfigureAwait(false) == 1;
        }, cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<ScheduleRecoveryResult> ReconcileAgentJobsOnStartupAsync(
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
                UPDATE agent_job_runs
                SET status = 'interrupted', completed_at_utc = $nowUtc, error_code = 'desktop_restart'
                WHERE status = 'running';
                """,
                cancellationToken,
                transaction,
                ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                UPDATE agent_jobs
                SET status = CASE WHEN attempt_count < max_attempts THEN 'pending' ELSE 'failed' END,
                    next_run_at_utc = CASE WHEN attempt_count < max_attempts THEN $nowUtc ELSE NULL END,
                    lease_until_utc = NULL, active_run_id = NULL, updated_at_utc = $nowUtc
                WHERE status = 'running';
                """,
                cancellationToken,
                transaction,
                ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);
            result = await ReconcilePendingAgentJobsAsync(
                connection, transaction, nowUtc, nowUtc.AddMinutes(-1), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<ScheduleRecoveryResult> ReconcileOverdueAgentJobsAsync(
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
                UPDATE agent_job_runs
                SET status = 'interrupted', completed_at_utc = $nowUtc, error_code = 'lease_expired'
                WHERE status = 'running'
                  AND id IN (
                      SELECT active_run_id FROM agent_jobs
                      WHERE status = 'running' AND lease_until_utc <= $nowUtc
                  );
                """,
                cancellationToken,
                transaction,
                ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                UPDATE agent_jobs
                SET status = CASE WHEN attempt_count < max_attempts THEN 'pending' ELSE 'failed' END,
                    next_run_at_utc = CASE WHEN attempt_count < max_attempts THEN $nowUtc ELSE NULL END,
                    lease_until_utc = NULL, active_run_id = NULL, updated_at_utc = $nowUtc
                WHERE status = 'running' AND lease_until_utc <= $nowUtc;
                """,
                cancellationToken,
                transaction,
                ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);
            result = await ReconcilePendingAgentJobsAsync(
                connection, transaction, nowUtc, dueBeforeUtc, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<AgentJobClaim?> TryClaimDueAgentJobAsync(
        DateTimeOffset nowUtc,
        DateTimeOffset leaseUntilUtc,
        CancellationToken cancellationToken)
    {
        if (leaseUntilUtc <= nowUtc) throw new ArgumentOutOfRangeException(nameof(leaseUntilUtc));
        AgentJobClaim? result = null;
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            ScheduledAgentJob? job = null;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = $"{AgentJobSelect} WHERE status = 'pending' AND next_run_at_utc <= $nowUtc ORDER BY next_run_at_utc LIMIT 1;";
                command.Parameters.AddWithValue("$nowUtc", Format(nowUtc));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) job = ReadAgentJob(reader);
            }
            if (job?.NextRunAtUtc is not null)
            {
                var runId = Guid.NewGuid().ToString("N");
                var attempt = job.AttemptCount + 1;
                var changed = await ExecuteCountAsync(
                    connection,
                    """
                    UPDATE agent_jobs
                    SET status = 'running', attempt_count = $attempt, lease_until_utc = $leaseUntilUtc,
                        active_run_id = $runId, updated_at_utc = $nowUtc
                    WHERE id = $id AND status = 'pending';
                    """,
                    cancellationToken,
                    transaction,
                    ("$attempt", attempt), ("$leaseUntilUtc", Format(leaseUntilUtc)),
                    ("$runId", runId), ("$nowUtc", Format(nowUtc)), ("$id", job.Id)).ConfigureAwait(false);
                if (changed == 1)
                {
                    await ExecuteAsync(
                        connection,
                        """
                        INSERT INTO agent_job_runs(id, job_id, scheduled_at_utc, started_at_utc, status, attempt)
                        VALUES ($runId, $jobId, $scheduledAtUtc, $nowUtc, 'running', $attempt);
                        """,
                        cancellationToken,
                        transaction,
                        ("$runId", runId), ("$jobId", job.Id),
                        ("$scheduledAtUtc", Format(job.NextRunAtUtc.Value)), ("$nowUtc", Format(nowUtc)),
                        ("$attempt", attempt)).ConfigureAwait(false);
                    result = new AgentJobClaim(job with { Status = ScheduleValues.Running, AttemptCount = attempt },
                        runId, job.NextRunAtUtc.Value, attempt);
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task CompleteAgentJobRunAsync(
        string runId,
        DateTimeOffset completedAtUtc,
        AgentJobRunResult result,
        CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            ScheduledAgentJob? job = null;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = $"{AgentJobSelect} WHERE active_run_id = $runId AND status = 'running';";
                command.Parameters.AddWithValue("$runId", runId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) job = ReadAgentJob(reader);
            }
            if (job is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            var boundedResult = result.ResultText;
            if (boundedResult?.Length > job.ResultMaxCharacters)
                boundedResult = string.Concat(boundedResult.AsSpan(0, job.ResultMaxCharacters), "…");
            await ExecuteAsync(
                connection,
                """
                UPDATE agent_job_runs
                SET status = $status, completed_at_utc = $completedAtUtc,
                    result_text = $resultText, error_code = $errorCode
                WHERE id = $runId AND status = 'running';
                """,
                cancellationToken,
                transaction,
                ("$status", result.Succeeded ? "succeeded" : "failed"),
                ("$completedAtUtc", Format(completedAtUtc)), ("$resultText", boundedResult),
                ("$errorCode", result.ErrorCode), ("$runId", runId)).ConfigureAwait(false);

            string status;
            DateTimeOffset? next;
            var attemptCount = job.AttemptCount;
            if (result.Succeeded)
            {
                next = AgentJobPolicy.NextOccurrenceAfter(job, completedAtUtc);
                status = next is null ? ScheduleValues.Completed : ScheduleValues.Pending;
                attemptCount = 0;
            }
            else if (job.AttemptCount < job.MaxAttempts)
            {
                next = completedAtUtc.AddMinutes(Math.Min(30, 5 * job.AttemptCount));
                status = ScheduleValues.Pending;
            }
            else
            {
                next = AgentJobPolicy.NextOccurrenceAfter(job, completedAtUtc);
                status = next is null ? ScheduleValues.Failed : ScheduleValues.Pending;
                attemptCount = 0;
            }
            await ExecuteAsync(
                connection,
                """
                UPDATE agent_jobs
                SET status = $status, attempt_count = $attemptCount, next_run_at_utc = $nextRunAtUtc,
                    lease_until_utc = NULL, active_run_id = NULL, updated_at_utc = $updatedAtUtc
                WHERE id = $id AND active_run_id = $runId;
                """,
                cancellationToken,
                transaction,
                ("$status", status), ("$attemptCount", attemptCount),
                ("$nextRunAtUtc", next is null ? null : Format(next.Value)),
                ("$updatedAtUtc", Format(completedAtUtc)), ("$id", job.Id), ("$runId", runId)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public async Task<bool> ResolveAgentJobMisfireAsync(
        string jobId,
        bool runNow,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var resolved = false;
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var job = await ReadAgentJobAsync(connection, (SqliteTransaction)transaction, jobId, cancellationToken)
                .ConfigureAwait(false);
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
                await RecordSkippedAgentJobRunAsync(connection, transaction, job, nowUtc, cancellationToken)
                    .ConfigureAwait(false);
                next = AgentJobPolicy.NextOccurrenceAfter(job, nowUtc);
            }
            await UpdateAgentJobStateAsync(
                connection, transaction, job.Id,
                next is null ? ScheduleValues.Completed : ScheduleValues.Pending,
                next, nowUtc, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            resolved = true;
        }, cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    private const string AgentJobSelect =
        """
        SELECT id, title, prompt, start_local, time_zone_id, recurrence, recurrence_interval,
               misfire_policy, model_profile_id, max_runtime_seconds, max_attempts,
               result_max_characters, source, status, attempt_count, next_run_at_utc,
               created_at_utc, updated_at_utc
        FROM agent_jobs
        """;

    private static async Task<ScheduledAgentJob?> ReadAgentJobAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{AgentJobSelect} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAgentJob(reader) : null;
    }

    private static ScheduledAgentJob ReadAgentJob(System.Data.Common.DbDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        DateTime.SpecifyKind(DateTime.ParseExact(reader.GetString(3), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Unspecified),
        reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt32(9), reader.GetInt32(10),
        reader.GetInt32(11), reader.GetString(12), reader.GetString(13), reader.GetInt32(14),
        reader.IsDBNull(15) ? null : ParseTimestamp(reader.GetString(15)),
        ParseTimestamp(reader.GetString(16)), ParseTimestamp(reader.GetString(17)));

    private static void AddAgentJobParameters(SqliteCommand command, ScheduledAgentJob job)
    {
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$title", job.Title);
        command.Parameters.AddWithValue("$prompt", job.Prompt);
        command.Parameters.AddWithValue("$startLocal", job.StartLocal.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$timeZoneId", job.TimeZoneId);
        command.Parameters.AddWithValue("$recurrence", job.Recurrence);
        command.Parameters.AddWithValue("$interval", job.Interval);
        command.Parameters.AddWithValue("$misfirePolicy", job.MisfirePolicy);
        command.Parameters.AddWithValue("$modelProfileId", (object?)job.ModelProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$maxRuntimeSeconds", job.MaxRuntimeSeconds);
        command.Parameters.AddWithValue("$maxAttempts", job.MaxAttempts);
        command.Parameters.AddWithValue("$resultMaxCharacters", job.ResultMaxCharacters);
        command.Parameters.AddWithValue("$source", job.Source);
        command.Parameters.AddWithValue("$status", job.Status);
        command.Parameters.AddWithValue("$nextRunAtUtc", job.NextRunAtUtc is null ? DBNull.Value : Format(job.NextRunAtUtc.Value));
        command.Parameters.AddWithValue("$createdAtUtc", Format(job.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedAtUtc", Format(job.UpdatedAtUtc));
    }

    private static async Task<ScheduleRecoveryResult> ReconcilePendingAgentJobsAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        DateTimeOffset nowUtc,
        DateTimeOffset dueBeforeUtc,
        CancellationToken cancellationToken)
    {
        var due = new List<ScheduledAgentJob>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = $"{AgentJobSelect} WHERE status = 'pending' AND next_run_at_utc <= $dueBeforeUtc ORDER BY next_run_at_utc;";
            command.Parameters.AddWithValue("$dueBeforeUtc", Format(dueBeforeUtc));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) due.Add(ReadAgentJob(reader));
        }
        var skipped = 0;
        var awaiting = 0;
        var ready = 0;
        foreach (var job in due)
        {
            if (job.MisfirePolicy == ScheduleValues.RunOnceOnResume)
            {
                ready++;
                continue;
            }
            if (job.MisfirePolicy == ScheduleValues.Ask)
            {
                await UpdateAgentJobStateAsync(
                    connection, transaction, job.Id, ScheduleValues.AwaitingDecision,
                    job.NextRunAtUtc, nowUtc, cancellationToken).ConfigureAwait(false);
                awaiting++;
                continue;
            }
            await RecordSkippedAgentJobRunAsync(connection, transaction, job, nowUtc, cancellationToken)
                .ConfigureAwait(false);
            var next = AgentJobPolicy.NextOccurrenceAfter(job, nowUtc);
            await UpdateAgentJobStateAsync(
                connection, transaction, job.Id,
                next is null ? ScheduleValues.Completed : ScheduleValues.Pending,
                next, nowUtc, cancellationToken).ConfigureAwait(false);
            skipped++;
        }
        return new ScheduleRecoveryResult(skipped, awaiting, ready);
    }

    private static Task UpdateAgentJobStateAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string jobId,
        string status,
        DateTimeOffset? nextRunAtUtc,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            """
            UPDATE agent_jobs
            SET status = $status, next_run_at_utc = $nextRunAtUtc, lease_until_utc = NULL,
                active_run_id = NULL, updated_at_utc = $updatedAtUtc
            WHERE id = $id;
            """,
            cancellationToken,
            transaction,
            ("$status", status), ("$nextRunAtUtc", nextRunAtUtc is null ? null : Format(nextRunAtUtc.Value)),
            ("$updatedAtUtc", Format(updatedAtUtc)), ("$id", jobId));

    private static Task RecordSkippedAgentJobRunAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        ScheduledAgentJob job,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            """
            INSERT INTO agent_job_runs(
                id, job_id, scheduled_at_utc, started_at_utc, completed_at_utc, status, attempt)
            VALUES ($id, $jobId, $scheduledAtUtc, $nowUtc, $nowUtc, 'skipped', 0);
            """,
            cancellationToken,
            transaction,
            ("$id", Guid.NewGuid().ToString("N")), ("$jobId", job.Id),
            ("$scheduledAtUtc", Format(job.NextRunAtUtc ?? nowUtc)), ("$nowUtc", Format(nowUtc)));
}
