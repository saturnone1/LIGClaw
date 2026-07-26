using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed partial class ConversationStore
{
    public async Task CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        var destination = ValidateMaintenancePath(destinationPath);
        if (StringComparer.OrdinalIgnoreCase.Equals(destination, DatabasePath)) throw new ArgumentException("현재 데이터 파일과 다른 위치를 선택해야 합니다.", nameof(destinationPath));
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(destination)) throw new IOException("같은 이름의 백업 파일이 이미 있습니다.");
            var source = _connection ?? throw new InvalidOperationException("대화 저장소가 초기화되지 않았습니다.");
            await using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
            await target.OpenAsync(cancellationToken).ConfigureAwait(false);
            source.BackupDatabase(target);
            await target.CloseAsync().ConfigureAwait(false);
            await ValidateBackupAsync(temporary, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            _gate.Release();
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { }
        }
    }

    public async Task StageRestoreAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var source = ValidateMaintenancePath(sourcePath);
        if (!File.Exists(source) || StringComparer.OrdinalIgnoreCase.Equals(source, DatabasePath)) throw new FileNotFoundException("복원할 백업 파일을 찾을 수 없습니다.", source);
        await ValidateBackupAsync(source, cancellationToken).ConfigureAwait(false);
        var pending = PendingRestorePath;
        var temporary = pending + $".{Guid.NewGuid():N}.tmp";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            await ValidateBackupAsync(temporary, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, pending, overwrite: true);
        }
        finally
        {
            _gate.Release();
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { }
        }
    }

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

    private string PendingRestorePath => DatabasePath + ".restore.pending";

    private async Task ApplyPendingRestoreAsync(CancellationToken cancellationToken)
    {
        var pending = PendingRestorePath;
        if (!File.Exists(pending)) return;
        await ValidateBackupAsync(pending, cancellationToken).ConfigureAwait(false);
        var backupSuffix = $".before-restore-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.bak";
        if (File.Exists(DatabasePath)) File.Move(DatabasePath, DatabasePath + backupSuffix, overwrite: false);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = DatabasePath + suffix;
            if (File.Exists(sidecar)) File.Move(sidecar, sidecar + backupSuffix, overwrite: false);
        }
        File.Move(pending, DatabasePath, overwrite: false);
    }

    private static string ValidateMaintenancePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("절대 경로가 필요합니다.", nameof(path));
        var full = Path.GetFullPath(path.Trim());
        if (!full.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(".db 백업 파일을 선택해야 합니다.", nameof(path));
        return full;
    }

    private static async Task ValidateBackupAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (!StringComparer.OrdinalIgnoreCase.Equals(result, "ok")) throw new InvalidDataException("SQLite 무결성 검사를 통과하지 못했습니다.");
        }
        await using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        var version = Convert.ToInt32(await schema.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (version is < 1 or > CurrentSchemaVersion) throw new InvalidDataException("지원하지 않는 LIGClaw 데이터 버전입니다.");
    }
}
