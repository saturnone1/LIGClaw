using System.Globalization;
using LIGClaw.Application.Scheduling;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed class ScheduleRepository(ConversationDatabase database) : IScheduleRepository
{
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

    private Task WithConnectionAsync(Func<SqliteConnection, Task> operation, CancellationToken cancellationToken) =>
        database.WithConnectionAsync(operation, cancellationToken);

    private static Task<int> ExecuteAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        System.Data.Common.DbTransaction? transaction = null,
        params (string Name, object? Value)[] parameters) =>
        ConversationDatabase.ExecuteAsync(connection, commandText, cancellationToken, transaction, parameters);
}
