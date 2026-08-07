using System.Globalization;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed partial class ConversationStore
{
    public Task CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default) =>
        _database.CreateBackupAsync(destinationPath, cancellationToken);

    public Task StageRestoreAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        _database.StageRestoreAsync(sourcePath, cancellationToken);

    public async Task<int> CleanupOperationalHistoryAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var sql in new[]
            {
                "DELETE FROM approval_decisions WHERE created_at_utc < $cutoff;",
                "DELETE FROM tool_executions WHERE created_at_utc < $cutoff;",
                "DELETE FROM capability_grants WHERE expires_at_utc < $cutoff OR revoked_at_utc IS NOT NULL AND revoked_at_utc < $cutoff;",
                "DELETE FROM undo_entries WHERE undone_at_utc IS NOT NULL AND undone_at_utc < $cutoff;",
                "DELETE FROM agent_job_runs WHERE completed_at_utc IS NOT NULL AND completed_at_utc < $cutoff;",
                "DELETE FROM subagent_tasks WHERE status IN ('succeeded', 'failed', 'cancelled') AND updated_at_utc < $cutoff;",
                "DELETE FROM subagent_batches WHERE status IN ('succeeded', 'failed', 'cancelled') AND updated_at_utc < $cutoff;",
            })
                removed += await ExecuteAsync(connection, sql, cancellationToken, transaction, ("$cutoff", cutoffUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return removed;
    }

}
