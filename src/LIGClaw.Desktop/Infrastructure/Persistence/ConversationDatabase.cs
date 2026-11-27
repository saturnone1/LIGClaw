using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed class ConversationDatabase(string databasePath) : IDisposable
{
    internal const int CurrentSchemaVersion = 11;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = false,
    }.ToString();

    public string DatabasePath { get; } = Path.GetFullPath(databasePath);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            if (_connection is not null) return;
            await ApplyPendingRestoreAsync(cancellationToken).ConfigureAwait(false);
            var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            _connection = connection;
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER NOT NULL PRIMARY KEY,
                    applied_at_utc TEXT NOT NULL
                );
                """,
                cancellationToken,
                transaction).ConfigureAwait(false);
            var version = await ReadSchemaVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (version < 1)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE conversations (
                        id TEXT NOT NULL PRIMARY KEY,
                        user_input TEXT NOT NULL,
                        status TEXT NOT NULL,
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL
                    );
                    CREATE TABLE agent_events (
                        id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        conversation_id TEXT NOT NULL,
                        run_id TEXT NOT NULL,
                        sequence INTEGER NOT NULL,
                        type TEXT NOT NULL,
                        timestamp_utc TEXT NOT NULL,
                        text TEXT NULL,
                        message TEXT NULL,
                        FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
                        UNIQUE (run_id, sequence)
                    );
                    CREATE INDEX ix_conversations_updated_at ON conversations(updated_at_utc DESC);
                    CREATE INDEX ix_agent_events_conversation ON agent_events(conversation_id, id);
                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (1, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 2)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE tool_executions (
                        id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        conversation_id TEXT NOT NULL,
                        run_id TEXT NOT NULL,
                        tool_call_id TEXT NOT NULL UNIQUE,
                        tool_name TEXT NOT NULL,
                        risk TEXT NOT NULL,
                        status TEXT NOT NULL,
                        summary TEXT NOT NULL,
                        created_at_utc TEXT NOT NULL,
                        FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
                    );
                    CREATE TABLE approval_decisions (
                        id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        conversation_id TEXT NOT NULL,
                        run_id TEXT NOT NULL,
                        tool_call_id TEXT NOT NULL UNIQUE,
                        tool_name TEXT NOT NULL,
                        risk TEXT NOT NULL,
                        approved INTEGER NOT NULL,
                        scope TEXT NOT NULL,
                        summary TEXT NOT NULL,
                        created_at_utc TEXT NOT NULL,
                        FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
                    );
                    CREATE INDEX ix_tool_executions_created_at ON tool_executions(created_at_utc DESC);
                    CREATE INDEX ix_approval_decisions_tool_call ON approval_decisions(tool_call_id);
                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (2, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 3)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE undo_entries (
                        undo_id TEXT NOT NULL PRIMARY KEY,
                        kind TEXT NOT NULL,
                        original_path TEXT NOT NULL,
                        current_path TEXT NOT NULL,
                        current_identity TEXT NOT NULL,
                        created_at_utc TEXT NOT NULL,
                        undone_at_utc TEXT NULL
                    );
                    CREATE INDEX ix_undo_entries_pending ON undo_entries(undone_at_utc, created_at_utc DESC);
                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (3, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 4)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE memories (
                        id TEXT NOT NULL PRIMARY KEY,
                        kind TEXT NOT NULL COLLATE NOCASE,
                        key TEXT NOT NULL COLLATE NOCASE,
                        value TEXT NOT NULL,
                        sensitivity TEXT NOT NULL,
                        source TEXT NOT NULL,
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        expires_at_utc TEXT NULL,
                        UNIQUE(kind, key)
                    );
                    CREATE INDEX ix_memories_expiry_updated ON memories(expires_at_utc, updated_at_utc DESC);
                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (4, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 5)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE scheduled_jobs (
                        id TEXT NOT NULL PRIMARY KEY,
                        title TEXT NOT NULL,
                        message TEXT NOT NULL,
                        start_local TEXT NOT NULL,
                        time_zone_id TEXT NOT NULL,
                        recurrence TEXT NOT NULL,
                        recurrence_interval INTEGER NOT NULL,
                        misfire_policy TEXT NOT NULL,
                        source TEXT NOT NULL,
                        status TEXT NOT NULL,
                        next_run_at_utc TEXT NULL,
                        lease_until_utc TEXT NULL,
                        active_run_id TEXT NULL,
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL
                    );
                    CREATE INDEX ix_scheduled_jobs_due ON scheduled_jobs(status, next_run_at_utc);
                    CREATE TABLE job_runs (
                        id TEXT NOT NULL PRIMARY KEY,
                        job_id TEXT NOT NULL,
                        scheduled_at_utc TEXT NOT NULL,
                        started_at_utc TEXT NULL,
                        completed_at_utc TEXT NULL,
                        status TEXT NOT NULL,
                        error_code TEXT NULL,
                        FOREIGN KEY (job_id) REFERENCES scheduled_jobs(id) ON DELETE CASCADE
                    );
                    CREATE INDEX ix_job_runs_job_started ON job_runs(job_id, started_at_utc DESC);
                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (5, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 6)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE conversation_runs (
                        run_id TEXT NOT NULL PRIMARY KEY,
                        conversation_id TEXT NOT NULL,
                        user_input TEXT NOT NULL,
                        assistant_text TEXT NOT NULL DEFAULT '',
                        status TEXT NOT NULL,
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
                    );
                    CREATE INDEX ix_conversation_runs_thread ON conversation_runs(conversation_id, created_at_utc, run_id);
                    INSERT INTO conversation_runs(
                        run_id, conversation_id, user_input, assistant_text, status, created_at_utc, updated_at_utc)
                    SELECT
                        COALESCE(
                            (SELECT ae.run_id FROM agent_events ae WHERE ae.conversation_id = c.id ORDER BY ae.id LIMIT 1),
                            'legacy-' || c.id),
                        c.id,
                        c.user_input,
                        COALESCE((
                            SELECT group_concat(ordered.text, '')
                            FROM (
                                SELECT ae2.text AS text
                                FROM agent_events ae2
                                WHERE ae2.conversation_id = c.id
                                  AND ae2.type = 'text_delta'
                                  AND ae2.text IS NOT NULL
                                ORDER BY ae2.id
                            ) ordered
                        ), ''),
                        c.status,
                        c.created_at_utc,
                        c.updated_at_utc
                    FROM conversations c;
                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (6, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 7)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE capability_grants (
                        id TEXT NOT NULL PRIMARY KEY,
                        tool_name TEXT NOT NULL,
                        risk TEXT NOT NULL,
                        scope_hash TEXT NOT NULL,
                        scope_summary TEXT NOT NULL,
                        created_at_utc TEXT NOT NULL,
                        expires_at_utc TEXT NOT NULL,
                        revoked_at_utc TEXT NULL,
                        UNIQUE(tool_name, risk, scope_hash)
                    );
                    CREATE INDEX ix_capability_grants_active
                        ON capability_grants(revoked_at_utc, expires_at_utc, tool_name, risk, scope_hash);
                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (7, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 8)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE VIRTUAL TABLE conversation_search USING fts5(
                        conversation_id UNINDEXED,
                        run_id UNINDEXED,
                        user_input,
                        assistant_text,
                        tokenize='trigram'
                    );
                    INSERT INTO conversation_search(conversation_id, run_id, user_input, assistant_text)
                    SELECT conversation_id, run_id, user_input, assistant_text
                    FROM conversation_runs;
                    CREATE TRIGGER conversation_runs_search_insert
                    AFTER INSERT ON conversation_runs BEGIN
                        INSERT INTO conversation_search(conversation_id, run_id, user_input, assistant_text)
                        VALUES (new.conversation_id, new.run_id, new.user_input, new.assistant_text);
                    END;
                    CREATE TRIGGER conversation_runs_search_update
                    AFTER UPDATE OF conversation_id, user_input, assistant_text ON conversation_runs BEGIN
                        DELETE FROM conversation_search WHERE run_id = old.run_id;
                        INSERT INTO conversation_search(conversation_id, run_id, user_input, assistant_text)
                        VALUES (new.conversation_id, new.run_id, new.user_input, new.assistant_text);
                    END;
                    CREATE TRIGGER conversation_runs_search_delete
                    AFTER DELETE ON conversation_runs BEGIN
                        DELETE FROM conversation_search WHERE run_id = old.run_id;
                    END;
                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (8, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 9)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE memory_embeddings (
                        memory_id TEXT NOT NULL,
                        model_key TEXT NOT NULL,
                        content_hash TEXT NOT NULL,
                        dimensions INTEGER NOT NULL,
                        vector BLOB NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        PRIMARY KEY(memory_id, model_key),
                        FOREIGN KEY (memory_id) REFERENCES memories(id) ON DELETE CASCADE
                    );
                    CREATE INDEX ix_memory_embeddings_model ON memory_embeddings(model_key, updated_at_utc DESC);
                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (9, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 10)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE agent_jobs (
                        id TEXT NOT NULL PRIMARY KEY,
                        title TEXT NOT NULL,
                        prompt TEXT NOT NULL,
                        start_local TEXT NOT NULL,
                        time_zone_id TEXT NOT NULL,
                        recurrence TEXT NOT NULL,
                        recurrence_interval INTEGER NOT NULL,
                        misfire_policy TEXT NOT NULL,
                        model_profile_id TEXT NULL,
                        max_runtime_seconds INTEGER NOT NULL,
                        max_attempts INTEGER NOT NULL,
                        result_max_characters INTEGER NOT NULL,
                        source TEXT NOT NULL,
                        status TEXT NOT NULL,
                        attempt_count INTEGER NOT NULL DEFAULT 0,
                        next_run_at_utc TEXT NULL,
                        lease_until_utc TEXT NULL,
                        active_run_id TEXT NULL,
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL
                    );
                    CREATE INDEX ix_agent_jobs_due ON agent_jobs(status, next_run_at_utc);
                    CREATE TABLE agent_job_runs (
                        id TEXT NOT NULL PRIMARY KEY,
                        job_id TEXT NOT NULL,
                        scheduled_at_utc TEXT NOT NULL,
                        started_at_utc TEXT NOT NULL,
                        completed_at_utc TEXT NULL,
                        status TEXT NOT NULL,
                        attempt INTEGER NOT NULL,
                        result_text TEXT NULL,
                        error_code TEXT NULL,
                        FOREIGN KEY (job_id) REFERENCES agent_jobs(id) ON DELETE CASCADE
                    );
                    CREATE INDEX ix_agent_job_runs_job_started ON agent_job_runs(job_id, started_at_utc DESC);
                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (10, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 11)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE subagent_batches (
                        id TEXT NOT NULL PRIMARY KEY,
                        parent_conversation_id TEXT NOT NULL,
                        parent_run_id TEXT NOT NULL,
                        parent_tool_call_id TEXT NOT NULL,
                        model_profile_id TEXT NULL,
                        max_risk TEXT NOT NULL,
                        max_runtime_seconds INTEGER NOT NULL,
                        result_max_characters INTEGER NOT NULL,
                        reason TEXT NOT NULL,
                        status TEXT NOT NULL,
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL
                    );
                    CREATE INDEX ix_subagent_batches_parent ON subagent_batches(parent_run_id, created_at_utc DESC);
                    CREATE TABLE subagent_tasks (
                        child_run_id TEXT NOT NULL PRIMARY KEY,
                        batch_id TEXT NOT NULL,
                        ordinal INTEGER NOT NULL,
                        title TEXT NOT NULL,
                        prompt TEXT NOT NULL,
                        status TEXT NOT NULL,
                        result_text TEXT NULL,
                        error_code TEXT NULL,
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        FOREIGN KEY (batch_id) REFERENCES subagent_batches(id) ON DELETE CASCADE,
                        UNIQUE(batch_id, ordinal)
                    );
                    CREATE INDEX ix_subagent_tasks_batch ON subagent_tasks(batch_id, ordinal);
                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (11, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version > CurrentSchemaVersion)
                throw new InvalidDataException($"대화 데이터베이스 버전 {version}은 이 앱에서 지원하지 않습니다.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _connection?.Dispose();
            _connection = null;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

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
    internal async Task WithConnectionAsync(
        Func<SqliteConnection, Task> operation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = _connection ?? throw new InvalidOperationException("대화 저장소가 초기화되지 않았습니다.");
            await operation(connection).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    internal static async Task<int> ExecuteAsync(
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

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _connection?.Dispose();
            _connection = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
