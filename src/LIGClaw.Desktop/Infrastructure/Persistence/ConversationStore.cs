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

internal sealed record ConversationSummary(
    string Id,
    string UserInput,
    string Status,
    DateTimeOffset UpdatedAtUtc,
    int TurnCount)
{
    public string Title => UserInput.Length <= 34 ? UserInput : string.Concat(UserInput.AsSpan(0, 34), "…");
    public string UpdatedAtDisplay => UpdatedAtUtc.ToLocalTime().ToString("MM.dd HH:mm", CultureInfo.CurrentCulture);
    public string TurnCountDisplay => $"{TurnCount}턴 · {UpdatedAtDisplay}";
}

internal sealed record ConversationContextMessage(string Role, string Content);
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

internal sealed partial class ConversationStore(string databasePath) : IConversationRunStore, IToolAuditSink, ICapabilityGrantStore, IUndoJournal, IMemoryRepository, IScheduleRepository, IAgentJobRepository, ILocalSubagentRepository, IDisposable
{
    private const int CurrentSchemaVersion = 11;
    private const int MaximumConversationSearchLength = 200;
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

    public static ConversationStore CreateDefault()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LIGClaw",
            "Data");
        return new ConversationStore(Path.Combine(directory, "ligclaw.db"));
    }

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

    public async Task StartRunAsync(
        string conversationId,
        string runId,
        string userInput,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                INSERT INTO conversations(id, user_input, status, created_at_utc, updated_at_utc)
                VALUES ($id, $userInput, 'running', $startedAtUtc, $startedAtUtc)
                ON CONFLICT(id) DO UPDATE SET
                    status = 'running',
                    updated_at_utc = excluded.updated_at_utc;
                """,
                cancellationToken,
                transaction,
                ("$id", conversationId),
                ("$userInput", userInput),
                ("$startedAtUtc", startedAtUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                INSERT INTO conversation_runs(
                    run_id, conversation_id, user_input, assistant_text, status, created_at_utc, updated_at_utc)
                VALUES ($runId, $conversationId, $userInput, '', 'running', $startedAtUtc, $startedAtUtc);
                """,
                cancellationToken,
                transaction,
                ("$runId", runId),
                ("$conversationId", conversationId),
                ("$userInput", userInput),
                ("$startedAtUtc", startedAtUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var inserted = await ExecuteAsync(
                connection,
                """
                INSERT OR IGNORE INTO agent_events(
                    conversation_id, run_id, sequence, type, timestamp_utc, text, message)
                VALUES ($conversationId, $runId, $sequence, $type, $timestampUtc, $text, $message);
                """,
                cancellationToken,
                transaction,
                ("$conversationId", agentEvent.ConversationId),
                ("$runId", agentEvent.RunId),
                ("$sequence", agentEvent.Sequence),
                ("$type", agentEvent.Type),
                ("$timestampUtc", agentEvent.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
                ("$text", agentEvent.Text),
                ("$message", agentEvent.Message)).ConfigureAwait(false);

            if (inserted == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (agentEvent.Type == "text_delta" && !string.IsNullOrEmpty(agentEvent.Text))
            {
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE conversation_runs
                    SET assistant_text = assistant_text || $text, updated_at_utc = $updatedAtUtc
                    WHERE run_id = $runId AND conversation_id = $conversationId;
                    """,
                    cancellationToken,
                    transaction,
                    ("$text", agentEvent.Text),
                    ("$updatedAtUtc", agentEvent.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
                    ("$runId", agentEvent.RunId),
                    ("$conversationId", agentEvent.ConversationId)).ConfigureAwait(false);
            }

            var status = MapStatus(agentEvent.Type);
            if (status is not null)
            {
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE conversation_runs
                    SET status = $status, updated_at_utc = $updatedAtUtc
                    WHERE run_id = $runId AND conversation_id = $conversationId;
                    """,
                    cancellationToken,
                    transaction,
                    ("$status", status),
                    ("$updatedAtUtc", agentEvent.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
                    ("$runId", agentEvent.RunId),
                    ("$conversationId", agentEvent.ConversationId)).ConfigureAwait(false);
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE conversations
                    SET status = $status, updated_at_utc = $updatedAtUtc
                    WHERE id = $conversationId
                      AND NOT EXISTS (
                          SELECT 1 FROM conversation_runs
                          WHERE conversation_id = $conversationId AND status = 'running');
                    """,
                    cancellationToken,
                    transaction,
                    ("$status", status),
                    ("$updatedAtUtc", agentEvent.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
                    ("$conversationId", agentEvent.ConversationId)).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task MarkRunAsync(
        string conversationId,
        string runId,
        string status,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var timestamp = updatedAtUtc.ToString("O", CultureInfo.InvariantCulture);
            await ExecuteAsync(
                connection,
                """
                UPDATE conversation_runs
                SET status = $status, updated_at_utc = $updatedAtUtc
                WHERE conversation_id = $conversationId AND run_id = $runId AND status = 'running';
                """,
                cancellationToken,
                transaction,
                ("$status", status), ("$updatedAtUtc", timestamp),
                ("$conversationId", conversationId), ("$runId", runId)).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                UPDATE conversations
                SET status = $status, updated_at_utc = $updatedAtUtc
                WHERE id = $conversationId
                  AND NOT EXISTS (
                      SELECT 1 FROM conversation_runs
                      WHERE conversation_id = $conversationId AND status = 'running');
                """,
                cancellationToken,
                transaction,
                ("$status", status), ("$updatedAtUtc", timestamp), ("$conversationId", conversationId)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task MarkRunningConversationsInterruptedAsync(CancellationToken cancellationToken = default) =>
        WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            await ExecuteAsync(
                connection,
                "UPDATE conversation_runs SET status = 'interrupted', updated_at_utc = $updatedAtUtc WHERE status = 'running';",
                cancellationToken,
                transaction,
                ("$updatedAtUtc", timestamp)).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                "UPDATE conversations SET status = 'interrupted', updated_at_utc = $updatedAtUtc WHERE status = 'running';",
                cancellationToken,
                transaction,
                ("$updatedAtUtc", timestamp)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

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

    public async Task<IReadOnlyList<ConversationSummary>> GetRecentConversationsAsync(
        int limit = 12,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var results = new List<ConversationSummary>();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT c.id, c.user_input, c.status, c.updated_at_utc, COUNT(r.run_id)
                FROM conversations c
                LEFT JOIN conversation_runs r ON r.conversation_id = c.id
                GROUP BY c.id, c.user_input, c.status, c.updated_at_utc
                ORDER BY c.updated_at_utc DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new ConversationSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetInt32(4)));
            }
        }, cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<IReadOnlyList<ConversationSummary>> SearchConversationsAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length > MaximumConversationSearchLength || normalizedQuery.Contains('\0'))
            throw new ArgumentOutOfRangeException(nameof(query));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));

        var results = new List<ConversationSummary>();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            if (normalizedQuery.Length >= 3)
            {
                command.CommandText =
                    """
                    WITH matches AS (
                        SELECT DISTINCT conversation_id
                        FROM conversation_search
                        WHERE conversation_search MATCH $query
                        LIMIT $scanLimit
                    )
                    SELECT c.id, c.user_input, c.status, c.updated_at_utc,
                           (SELECT COUNT(*) FROM conversation_runs count_runs WHERE count_runs.conversation_id = c.id)
                    FROM matches
                    JOIN conversations c ON c.id = matches.conversation_id
                    ORDER BY c.updated_at_utc DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$query", QuoteFtsPhrase(normalizedQuery));
                command.Parameters.AddWithValue("$scanLimit", Math.Min(limit * 20, 2_000));
            }
            else
            {
                command.CommandText =
                    """
                    SELECT c.id, c.user_input, c.status, c.updated_at_utc, COUNT(r.run_id)
                    FROM conversations c
                    JOIN conversation_runs r ON r.conversation_id = c.id
                    WHERE r.user_input LIKE $pattern ESCAPE '\'
                       OR r.assistant_text LIKE $pattern ESCAPE '\'
                    GROUP BY c.id, c.user_input, c.status, c.updated_at_utc
                    ORDER BY c.updated_at_utc DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$pattern", $"%{EscapeLikePattern(normalizedQuery)}%");
            }
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new ConversationSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetInt32(4)));
            }
        }, cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<string> GetTranscriptAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var turns = await GetConversationTurnsAsync(conversationId, cancellationToken).ConfigureAwait(false);
        return ConversationTranscriptFormatter.Format(turns);
    }

    public async Task<IReadOnlyList<ConversationTurn>> GetConversationTurnsAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        var turns = new List<ConversationTurn>();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT run_id, user_input, assistant_text, status, created_at_utc
                FROM conversation_runs
                WHERE conversation_id = $conversationId
                ORDER BY created_at_utc, run_id;
                """;
            command.Parameters.AddWithValue("$conversationId", conversationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                turns.Add(new ConversationTurn(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }
        }, cancellationToken).ConfigureAwait(false);
        return turns;
    }

    public async Task<IReadOnlyList<ConversationContextMessage>> GetConversationContextAsync(
        string conversationId,
        int maximumMessages = 40,
        int maximumCharacters = 64_000,
        CancellationToken cancellationToken = default)
    {
        if (maximumMessages is < 2 or > 40 || maximumMessages % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(maximumMessages));
        if (maximumCharacters is < 1 or > 64_000)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        var completed = (await GetConversationTurnsAsync(conversationId, cancellationToken).ConfigureAwait(false))
            .Where(turn => turn.Status == "completed" && !string.IsNullOrWhiteSpace(turn.AssistantText))
            .ToArray();
        var selected = new List<ConversationTurn>();
        var characters = 0;
        for (var index = completed.Length - 1; index >= 0 && selected.Count * 2 < maximumMessages; index--)
        {
            var turn = completed[index];
            var bounded = turn with
            {
                UserInput = BoundContextContent(turn.UserInput),
                AssistantText = BoundContextContent(turn.AssistantText),
            };
            var nextCharacters = characters + bounded.UserInput.Length + bounded.AssistantText.Length;
            if (nextCharacters > maximumCharacters) break;
            selected.Add(bounded);
            characters = nextCharacters;
        }
        selected.Reverse();
        return selected
            .SelectMany(turn => new[]
            {
                new ConversationContextMessage("user", turn.UserInput),
                new ConversationContextMessage("assistant", turn.AssistantText),
            })
            .ToArray();
    }

    private static string BoundContextContent(string content)
    {
        const int maximumLength = 20_000;
        const string omission = "\n…[긴 대화 내용 중략]…\n";
        if (content.Length <= maximumLength) return content;
        var retained = maximumLength - omission.Length;
        var prefixLength = retained / 2;
        return string.Concat(content.AsSpan(0, prefixLength), omission, content.AsSpan(content.Length - (retained - prefixLength)));
    }

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

    private static string QuoteFtsPhrase(string query) => $"\"{query.Replace("\"", "\"\"")}\"";

    private static string EscapeLikePattern(string query) => query
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

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

    private async Task WithConnectionAsync(
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

    private static string? MapStatus(string eventType) => eventType switch
    {
        "run_completed" => "completed",
        "run_cancelled" => "cancelled",
        "run_failed" => "failed",
        _ => null,
    };

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
