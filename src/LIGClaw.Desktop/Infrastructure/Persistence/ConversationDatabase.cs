using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed class ConversationDatabase(string databasePath) : IDisposable
{
    internal const int CurrentSchemaVersion = 21;
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
            if (version < 12)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE capabilities (
                        id TEXT NOT NULL PRIMARY KEY,
                        display_name TEXT NOT NULL,
                        description TEXT NOT NULL,
                        kind TEXT NOT NULL,
                        maximum_risk TEXT NOT NULL,
                        active_version TEXT NULL,
                        active_content_hash TEXT NULL,
                        catalog_revision INTEGER NOT NULL DEFAULT 0 CHECK (catalog_revision >= 0),
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        CHECK ((active_version IS NULL) = (active_content_hash IS NULL)),
                        FOREIGN KEY (id, active_version, active_content_hash)
                            REFERENCES capability_versions(capability_id, version, content_hash)
                            DEFERRABLE INITIALLY DEFERRED
                    );
                    CREATE TABLE capability_versions (
                        capability_id TEXT NOT NULL,
                        version TEXT NOT NULL,
                        content_hash TEXT NOT NULL,
                        parent_version TEXT NULL,
                        parent_content_hash TEXT NULL,
                        manifest_json TEXT NOT NULL,
                        instructions_markdown TEXT NOT NULL,
                        input_schema_json TEXT NOT NULL,
                        settings_schema_json TEXT NULL,
                        workflow_json TEXT NULL,
                        tests_json TEXT NOT NULL,
                        source_kind TEXT NOT NULL,
                        source_package_hash TEXT NOT NULL,
                        created_at_utc TEXT NOT NULL,
                        CHECK ((parent_version IS NULL) = (parent_content_hash IS NULL)),
                        PRIMARY KEY (capability_id, version),
                        UNIQUE (capability_id, version, content_hash),
                        FOREIGN KEY (capability_id) REFERENCES capabilities(id) ON DELETE RESTRICT,
                        FOREIGN KEY (capability_id, parent_version, parent_content_hash)
                            REFERENCES capability_versions(capability_id, version, content_hash)
                    );
                    CREATE TABLE capability_drafts (
                        id TEXT NOT NULL PRIMARY KEY,
                        capability_id TEXT NOT NULL,
                        proposed_version TEXT NOT NULL,
                        content_hash TEXT NOT NULL,
                        parent_version TEXT NULL,
                        parent_content_hash TEXT NULL,
                        display_name TEXT NOT NULL,
                        kind TEXT NOT NULL,
                        manifest_json TEXT NOT NULL,
                        instructions_markdown TEXT NOT NULL,
                        input_schema_json TEXT NOT NULL,
                        settings_schema_json TEXT NULL,
                        workflow_json TEXT NULL,
                        tests_json TEXT NOT NULL,
                        status TEXT NOT NULL,
                        draft_revision INTEGER NOT NULL CHECK (draft_revision >= 0),
                        source_kind TEXT NOT NULL,
                        source_package_hash TEXT NOT NULL,
                        expected_catalog_revision INTEGER NOT NULL CHECK (expected_catalog_revision >= 0),
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        applied_at_utc TEXT NULL,
                        CHECK ((parent_version IS NULL) = (parent_content_hash IS NULL)),
                        UNIQUE (capability_id, proposed_version, content_hash),
                        FOREIGN KEY (capability_id) REFERENCES capabilities(id) ON DELETE RESTRICT,
                        FOREIGN KEY (capability_id, parent_version, parent_content_hash)
                            REFERENCES capability_versions(capability_id, version, content_hash)
                    );
                    CREATE INDEX ix_capability_drafts_catalog
                        ON capability_drafts(capability_id, status, updated_at_utc DESC);
                    CREATE TRIGGER capability_drafts_reject_payload_update
                    BEFORE UPDATE ON capability_drafts
                    WHEN OLD.id IS NOT NEW.id
                      OR OLD.capability_id IS NOT NEW.capability_id
                      OR OLD.proposed_version IS NOT NEW.proposed_version
                      OR OLD.content_hash IS NOT NEW.content_hash
                      OR OLD.parent_version IS NOT NEW.parent_version
                      OR OLD.parent_content_hash IS NOT NEW.parent_content_hash
                      OR OLD.display_name IS NOT NEW.display_name
                      OR OLD.kind IS NOT NEW.kind
                      OR OLD.manifest_json IS NOT NEW.manifest_json
                      OR OLD.instructions_markdown IS NOT NEW.instructions_markdown
                      OR OLD.input_schema_json IS NOT NEW.input_schema_json
                      OR OLD.settings_schema_json IS NOT NEW.settings_schema_json
                      OR OLD.workflow_json IS NOT NEW.workflow_json
                      OR OLD.tests_json IS NOT NEW.tests_json
                      OR OLD.source_kind IS NOT NEW.source_kind
                      OR OLD.source_package_hash IS NOT NEW.source_package_hash
                      OR OLD.expected_catalog_revision IS NOT NEW.expected_catalog_revision
                      OR OLD.created_at_utc IS NOT NEW.created_at_utc
                    BEGIN
                        SELECT RAISE(ABORT, 'capability draft payload is immutable');
                    END;
                    CREATE TRIGGER capability_drafts_reject_delete
                    BEFORE DELETE ON capability_drafts BEGIN
                        SELECT RAISE(ABORT, 'capability drafts cannot be deleted');
                    END;
                    CREATE TABLE capability_activations (
                        id TEXT NOT NULL PRIMARY KEY,
                        capability_id TEXT NOT NULL,
                        catalog_revision INTEGER NOT NULL CHECK (catalog_revision > 0),
                        action TEXT NOT NULL CHECK (action IN ('activate', 'rollback')),
                        source_draft_id TEXT NULL,
                        from_version TEXT NULL,
                        from_content_hash TEXT NULL,
                        target_version TEXT NOT NULL,
                        target_content_hash TEXT NOT NULL,
                        reason_code TEXT NOT NULL,
                        created_at_utc TEXT NOT NULL,
                        CHECK ((from_version IS NULL) = (from_content_hash IS NULL)),
                        UNIQUE (capability_id, catalog_revision),
                        FOREIGN KEY (capability_id) REFERENCES capabilities(id) ON DELETE RESTRICT,
                        FOREIGN KEY (source_draft_id) REFERENCES capability_drafts(id) ON DELETE RESTRICT,
                        FOREIGN KEY (capability_id, from_version, from_content_hash)
                            REFERENCES capability_versions(capability_id, version, content_hash),
                        FOREIGN KEY (capability_id, target_version, target_content_hash)
                            REFERENCES capability_versions(capability_id, version, content_hash)
                    );
                    CREATE INDEX ix_capability_activations_target
                        ON capability_activations(capability_id, target_version, target_content_hash, catalog_revision DESC);
                    CREATE TRIGGER capability_versions_reject_update
                    BEFORE UPDATE ON capability_versions BEGIN
                        SELECT RAISE(ABORT, 'capability versions are immutable');
                    END;
                    CREATE TRIGGER capability_versions_reject_delete
                    BEFORE DELETE ON capability_versions BEGIN
                        SELECT RAISE(ABORT, 'capability versions are immutable');
                    END;
                    CREATE TRIGGER capability_activations_reject_update
                    BEFORE UPDATE ON capability_activations BEGIN
                        SELECT RAISE(ABORT, 'capability activations are append-only');
                    END;
                    CREATE TRIGGER capability_activations_reject_delete
                    BEFORE DELETE ON capability_activations BEGIN
                        SELECT RAISE(ABORT, 'capability activations are append-only');
                    END;
                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (12, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 13)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE VIRTUAL TABLE capability_search USING fts5(
                        capability_id UNINDEXED,
                        version UNINDEXED,
                        content_hash UNINDEXED,
                        display_name,
                        description,
                        intent_examples,
                        tokenize='trigram'
                    );
                    INSERT INTO capability_search(
                        capability_id, version, content_hash,
                        display_name, description, intent_examples)
                    SELECT c.id, c.active_version, c.active_content_hash,
                           json_extract(v.manifest_json, '$.displayName'),
                           json_extract(v.manifest_json, '$.description'),
                           json_extract(v.manifest_json, '$.intentExamples')
                    FROM capabilities AS c
                    JOIN capability_versions AS v
                      ON v.capability_id = c.id
                     AND v.version = c.active_version
                     AND v.content_hash = c.active_content_hash
                    WHERE c.active_version IS NOT NULL;
                    CREATE TRIGGER capabilities_search_active_update
                    AFTER UPDATE OF active_version, active_content_hash ON capabilities BEGIN
                        DELETE FROM capability_search WHERE capability_id = old.id;
                        INSERT INTO capability_search(
                            capability_id, version, content_hash,
                            display_name, description, intent_examples)
                        SELECT new.id, new.active_version, new.active_content_hash,
                               json_extract(v.manifest_json, '$.displayName'),
                               json_extract(v.manifest_json, '$.description'),
                               json_extract(v.manifest_json, '$.intentExamples')
                        FROM capability_versions AS v
                        WHERE new.active_version IS NOT NULL
                          AND v.capability_id = new.id
                          AND v.version = new.active_version
                          AND v.content_hash = new.active_content_hash;
                    END;
                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (13, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 14)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE UNIQUE INDEX IF NOT EXISTS ux_conversation_runs_run_conversation
                        ON conversation_runs(run_id, conversation_id);
                    CREATE TABLE workflow_runs (
                        id TEXT NOT NULL PRIMARY KEY,
                        conversation_id TEXT NOT NULL,
                        parent_run_id TEXT NOT NULL,
                        parent_tool_call_id TEXT NOT NULL,
                        idempotency_key TEXT NOT NULL,
                        snapshot_hash TEXT NOT NULL
                            CHECK (length(snapshot_hash) = 71
                                AND substr(snapshot_hash, 1, 7) = 'sha256:'
                                AND substr(snapshot_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        model_tool_name TEXT NOT NULL,
                        capability_id TEXT NOT NULL,
                        capability_version TEXT NOT NULL,
                        catalog_revision INTEGER NOT NULL CHECK (catalog_revision > 0),
                        capability_content_hash TEXT NOT NULL
                            CHECK (length(capability_content_hash) = 71
                                AND substr(capability_content_hash, 1, 7) = 'sha256:'
                                AND substr(capability_content_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        workflow_hash TEXT NOT NULL
                            CHECK (length(workflow_hash) = 71
                                AND substr(workflow_hash, 1, 7) = 'sha256:'
                                AND substr(workflow_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        input_hash TEXT NOT NULL
                            CHECK (length(input_hash) = 71
                                AND substr(input_hash, 1, 7) = 'sha256:'
                                AND substr(input_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        plan_hash TEXT NOT NULL
                            CHECK (length(plan_hash) = 71
                                AND substr(plan_hash, 1, 7) = 'sha256:'
                                AND substr(plan_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        actual_maximum_risk TEXT NOT NULL
                            CHECK (actual_maximum_risk IN ('R0', 'R1', 'R2', 'R3', 'R4')),
                        status TEXT NOT NULL CHECK (status IN (
                            'planned', 'awaiting_approval', 'running', 'needs_information',
                            'succeeded', 'failed', 'cancelled', 'interrupted', 'attention_required')),
                        current_sequence INTEGER NOT NULL DEFAULT -1 CHECK (current_sequence BETWEEN -1 AND 19),
                        fixed_next_action TEXT NULL CHECK (fixed_next_action IS NULL OR (
                            length(fixed_next_action) BETWEEN 1 AND 128
                            AND fixed_next_action NOT GLOB '*[^a-z0-9._-]*')),
                        fixed_error_code TEXT NULL CHECK (fixed_error_code IS NULL OR (
                            length(fixed_error_code) BETWEEN 1 AND 128
                            AND fixed_error_code NOT GLOB '*[^a-z0-9._-]*')),
                        redacted_summary TEXT NULL CHECK (redacted_summary IS NULL OR length(redacted_summary) <= 512),
                        result_hash TEXT NULL CHECK (result_hash IS NULL OR (
                            length(result_hash) = 71
                            AND substr(result_hash, 1, 7) = 'sha256:'
                            AND substr(result_hash, 8) NOT GLOB '*[^0-9a-f]*')),
                        required_pointers_json TEXT NULL CHECK (required_pointers_json IS NULL OR (
                            length(required_pointers_json) <= 100000
                            AND json_valid(required_pointers_json)
                            AND json_type(required_pointers_json) = 'array')),
                        revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        completed_at_utc TEXT NULL,
                        UNIQUE (parent_run_id, idempotency_key),
                        UNIQUE (parent_run_id, parent_tool_call_id),
                        UNIQUE (id, conversation_id, parent_run_id),
                        FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
                        FOREIGN KEY (parent_run_id, conversation_id)
                            REFERENCES conversation_runs(run_id, conversation_id) ON DELETE CASCADE
                    );
                    CREATE INDEX ix_workflow_runs_status_updated
                        ON workflow_runs(status, updated_at_utc);

                    CREATE TABLE workflow_authorization_tickets (
                        id TEXT NOT NULL PRIMARY KEY,
                        conversation_id TEXT NOT NULL,
                        parent_run_id TEXT NOT NULL,
                        workflow_run_id TEXT NOT NULL,
                        snapshot_hash TEXT NOT NULL
                            CHECK (length(snapshot_hash) = 71
                                AND substr(snapshot_hash, 1, 7) = 'sha256:'
                                AND substr(snapshot_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        model_tool_name TEXT NOT NULL,
                        capability_id TEXT NOT NULL,
                        capability_version TEXT NOT NULL,
                        catalog_revision INTEGER NOT NULL CHECK (catalog_revision > 0),
                        capability_content_hash TEXT NOT NULL
                            CHECK (length(capability_content_hash) = 71
                                AND substr(capability_content_hash, 1, 7) = 'sha256:'
                                AND substr(capability_content_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        workflow_hash TEXT NOT NULL
                            CHECK (length(workflow_hash) = 71
                                AND substr(workflow_hash, 1, 7) = 'sha256:'
                                AND substr(workflow_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        input_hash TEXT NOT NULL
                            CHECK (length(input_hash) = 71
                                AND substr(input_hash, 1, 7) = 'sha256:'
                                AND substr(input_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        plan_hash TEXT NOT NULL
                            CHECK (length(plan_hash) = 71
                                AND substr(plan_hash, 1, 7) = 'sha256:'
                                AND substr(plan_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        actual_maximum_risk TEXT NOT NULL CHECK (actual_maximum_risk IN ('R0', 'R1', 'R2', 'R3', 'R4')),
                        approval_scope TEXT NOT NULL DEFAULT 'allow_once' CHECK (approval_scope = 'allow_once'),
                        status TEXT NOT NULL CHECK (status IN ('active', 'consumed', 'revoked', 'expired')),
                        approved_at_utc TEXT NOT NULL,
                        expires_at_utc TEXT NOT NULL,
                        consumed_at_utc TEXT NULL,
                        revoked_at_utc TEXT NULL,
                        revocation_code TEXT NULL CHECK (revocation_code IS NULL OR (
                            length(revocation_code) BETWEEN 1 AND 128
                            AND revocation_code NOT GLOB '*[^a-z0-9._-]*')),
                        revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
                        UNIQUE (id, conversation_id, parent_run_id, workflow_run_id),
                        FOREIGN KEY (workflow_run_id, conversation_id, parent_run_id)
                            REFERENCES workflow_runs(id, conversation_id, parent_run_id) ON DELETE CASCADE,
                        FOREIGN KEY (workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE,
                        FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
                        FOREIGN KEY (parent_run_id, conversation_id)
                            REFERENCES conversation_runs(run_id, conversation_id) ON DELETE CASCADE
                    );
                    CREATE INDEX ix_workflow_authorization_tickets_active
                        ON workflow_authorization_tickets(status, expires_at_utc, workflow_run_id);

                    CREATE TABLE workflow_authorization_ticket_items (
                        id TEXT NOT NULL PRIMARY KEY,
                        ticket_id TEXT NOT NULL,
                        conversation_id TEXT NOT NULL,
                        parent_run_id TEXT NOT NULL,
                        workflow_run_id TEXT NOT NULL,
                        step_sequence INTEGER NOT NULL CHECK (step_sequence BETWEEN 0 AND 19),
                        nonce_hash TEXT NOT NULL UNIQUE
                            CHECK (length(nonce_hash) = 71
                                AND substr(nonce_hash, 1, 7) = 'sha256:'
                                AND substr(nonce_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        tool_name TEXT NOT NULL,
                        risk TEXT NOT NULL CHECK (risk IN ('R0', 'R1', 'R2', 'R3', 'R4')),
                        payload_hash TEXT NOT NULL
                            CHECK (length(payload_hash) = 71
                                AND substr(payload_hash, 1, 7) = 'sha256:'
                                AND substr(payload_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        binding_hash TEXT NOT NULL
                            CHECK (length(binding_hash) = 71
                                AND substr(binding_hash, 1, 7) = 'sha256:'
                                AND substr(binding_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        status TEXT NOT NULL CHECK (status IN ('ready', 'consumed', 'revoked', 'expired')),
                        expires_at_utc TEXT NOT NULL,
                        consumed_at_utc TEXT NULL,
                        consumed_by_tool_call_id TEXT NULL,
                        revoked_at_utc TEXT NULL,
                        revocation_code TEXT NULL CHECK (revocation_code IS NULL OR (
                            length(revocation_code) BETWEEN 1 AND 128
                            AND revocation_code NOT GLOB '*[^a-z0-9._-]*')),
                        revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
                        UNIQUE (ticket_id, step_sequence),
                        UNIQUE (id, workflow_run_id, step_sequence, tool_name, risk, payload_hash, binding_hash),
                        FOREIGN KEY (ticket_id, conversation_id, parent_run_id, workflow_run_id)
                            REFERENCES workflow_authorization_tickets(
                                id, conversation_id, parent_run_id, workflow_run_id) ON DELETE CASCADE,
                        FOREIGN KEY (ticket_id) REFERENCES workflow_authorization_tickets(id) ON DELETE CASCADE,
                        FOREIGN KEY (workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE,
                        FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
                        FOREIGN KEY (parent_run_id, conversation_id)
                            REFERENCES conversation_runs(run_id, conversation_id) ON DELETE CASCADE
                    );
                    CREATE INDEX ix_workflow_authorization_ticket_items_ready
                        ON workflow_authorization_ticket_items(status, expires_at_utc, workflow_run_id, step_sequence);

                    CREATE TABLE workflow_step_runs (
                        tool_call_id TEXT NOT NULL PRIMARY KEY,
                        workflow_run_id TEXT NOT NULL,
                        step_sequence INTEGER NOT NULL CHECK (step_sequence BETWEEN 0 AND 19),
                        attempt INTEGER NOT NULL CHECK (attempt BETWEEN 1 AND 4),
                        step_id TEXT NOT NULL,
                        tool_name TEXT NOT NULL,
                        risk TEXT NOT NULL CHECK (risk IN ('R0', 'R1', 'R2', 'R3', 'R4')),
                        retry_class TEXT NOT NULL CHECK (retry_class IN ('never', 'read_only', 'idempotent')),
                        maximum_retries INTEGER NOT NULL CHECK (maximum_retries BETWEEN 0 AND 3),
                        effect_class TEXT NOT NULL CHECK (effect_class IN ('observation', 'effect')),
                        payload_hash TEXT NOT NULL
                            CHECK (length(payload_hash) = 71
                                AND substr(payload_hash, 1, 7) = 'sha256:'
                                AND substr(payload_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        binding_hash TEXT NOT NULL
                            CHECK (length(binding_hash) = 71
                                AND substr(binding_hash, 1, 7) = 'sha256:'
                                AND substr(binding_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        ticket_item_id TEXT NULL UNIQUE,
                        status TEXT NOT NULL CHECK (status IN (
                            'planned', 'executing', 'retryable_failed', 'not_started', 'succeeded', 'failed_known',
                            'interrupted', 'outcome_unknown')),
                        disposition TEXT NULL CHECK (disposition IS NULL OR disposition IN (
                            'retryable_failed', 'not_started', 'succeeded', 'failed_known', 'unknown')),
                        output_hash TEXT NULL CHECK (output_hash IS NULL OR (
                            length(output_hash) = 71
                            AND substr(output_hash, 1, 7) = 'sha256:'
                            AND substr(output_hash, 8) NOT GLOB '*[^0-9a-f]*')),
                        redacted_summary TEXT NULL CHECK (redacted_summary IS NULL OR length(redacted_summary) <= 512),
                        fixed_error_code TEXT NULL CHECK (fixed_error_code IS NULL OR (
                            length(fixed_error_code) BETWEEN 1 AND 128
                            AND fixed_error_code NOT GLOB '*[^a-z0-9._-]*')),
                        revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
                        created_at_utc TEXT NOT NULL,
                        started_at_utc TEXT NULL,
                        completed_at_utc TEXT NULL,
                        UNIQUE (workflow_run_id, step_sequence, attempt),
                        FOREIGN KEY (workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE,
                        FOREIGN KEY (
                            ticket_item_id, workflow_run_id, step_sequence,
                            tool_name, risk, payload_hash, binding_hash)
                            REFERENCES workflow_authorization_ticket_items(
                                id, workflow_run_id, step_sequence,
                                tool_name, risk, payload_hash, binding_hash) ON DELETE RESTRICT,
                        FOREIGN KEY (ticket_item_id) REFERENCES workflow_authorization_ticket_items(id) ON DELETE RESTRICT
                    );
                    CREATE INDEX ix_workflow_step_runs_run_sequence
                        ON workflow_step_runs(workflow_run_id, step_sequence, attempt);
                    CREATE INDEX ix_workflow_step_runs_recovery
                        ON workflow_step_runs(status, effect_class, workflow_run_id);
                    CREATE TRIGGER workflow_ticket_item_require_matching_step
                    BEFORE UPDATE OF status ON workflow_authorization_ticket_items
                    WHEN NEW.status = 'consumed'
                      AND NOT EXISTS (
                          SELECT 1 FROM workflow_step_runs AS step
                          WHERE step.tool_call_id = NEW.consumed_by_tool_call_id
                            AND step.workflow_run_id = NEW.workflow_run_id
                            AND step.step_sequence = NEW.step_sequence
                            AND step.tool_name = NEW.tool_name
                            AND step.risk = NEW.risk
                            AND step.payload_hash = NEW.payload_hash
                            AND step.binding_hash = NEW.binding_hash
                            AND step.effect_class = 'effect'
                            AND step.status = 'planned'
                      )
                    BEGIN
                        SELECT RAISE(ABORT, 'workflow ticket item does not match planned effect');
                    END;

                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (14, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 15)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE connection_profiles (
                        id TEXT NOT NULL PRIMARY KEY,
                        kind TEXT NOT NULL CHECK (kind = 'http'),
                        display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 80),
                        enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
                        state TEXT NOT NULL CHECK (state IN ('staged', 'updating', 'stable', 'error')),
                        revision INTEGER NOT NULL CHECK (revision > 0),
                        target_revision INTEGER NOT NULL CHECK (target_revision > 0),
                        credential_revision INTEGER NOT NULL CHECK (credential_revision >= 0),
                        target_hash TEXT NOT NULL CHECK (
                            length(target_hash) = 71
                            AND substr(target_hash, 1, 7) = 'sha256:'
                            AND substr(target_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        fixed_error_code TEXT NULL CHECK (fixed_error_code IS NULL OR length(fixed_error_code) BETWEEN 3 AND 128),
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        CHECK ((state = 'error' AND fixed_error_code IS NOT NULL) OR
                               (state <> 'error' AND fixed_error_code IS NULL))
                    );
                    CREATE TABLE http_connection_profiles (
                        profile_id TEXT NOT NULL PRIMARY KEY,
                        base_url TEXT NOT NULL CHECK (length(base_url) BETWEEN 8 AND 2048),
                        auth_kind TEXT NOT NULL CHECK (auth_kind IN ('none', 'bearer')),
                        FOREIGN KEY (profile_id) REFERENCES connection_profiles(id) ON DELETE RESTRICT
                    );
                    CREATE TABLE connection_probe_results (
                        profile_id TEXT NOT NULL PRIMARY KEY,
                        profile_target_revision INTEGER NOT NULL CHECK (profile_target_revision > 0),
                        profile_credential_revision INTEGER NOT NULL CHECK (profile_credential_revision >= 0),
                        profile_target_hash TEXT NOT NULL CHECK (
                            length(profile_target_hash) = 71
                            AND substr(profile_target_hash, 1, 7) = 'sha256:'
                            AND substr(profile_target_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        succeeded INTEGER NOT NULL CHECK (succeeded IN (0, 1)),
                        fixed_error_code TEXT NULL CHECK (fixed_error_code IS NULL OR length(fixed_error_code) BETWEEN 3 AND 128),
                        checked_at_utc TEXT NOT NULL,
                        CHECK ((succeeded = 1 AND fixed_error_code IS NULL) OR
                               (succeeded = 0 AND fixed_error_code IS NOT NULL)),
                        FOREIGN KEY (profile_id) REFERENCES connection_profiles(id) ON DELETE CASCADE
                    );
                    CREATE TABLE capability_connection_bindings (
                        capability_id TEXT NOT NULL,
                        capability_version TEXT NOT NULL,
                        capability_content_hash TEXT NOT NULL,
                        slot TEXT NOT NULL CHECK (length(slot) BETWEEN 1 AND 64),
                        kind TEXT NOT NULL CHECK (kind = 'http'),
                        profile_id TEXT NOT NULL,
                        revision INTEGER NOT NULL CHECK (revision > 0),
                        profile_target_revision INTEGER NOT NULL CHECK (profile_target_revision > 0),
                        profile_credential_revision INTEGER NOT NULL CHECK (profile_credential_revision >= 0),
                        profile_target_hash TEXT NOT NULL CHECK (
                            length(profile_target_hash) = 71
                            AND substr(profile_target_hash, 1, 7) = 'sha256:'
                            AND substr(profile_target_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        binding_hash TEXT NOT NULL CHECK (
                            length(binding_hash) = 71
                            AND substr(binding_hash, 1, 7) = 'sha256:'
                            AND substr(binding_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        PRIMARY KEY (capability_id, capability_version, capability_content_hash, slot),
                        FOREIGN KEY (capability_id, capability_version, capability_content_hash)
                            REFERENCES capability_drafts(capability_id, proposed_version, content_hash)
                            ON DELETE RESTRICT,
                        FOREIGN KEY (profile_id) REFERENCES connection_profiles(id) ON DELETE RESTRICT
                    );
                    CREATE INDEX ix_connection_profiles_management
                        ON connection_profiles(kind, state, enabled, display_name, id);
                    CREATE INDEX ix_capability_connection_bindings_profile
                        ON capability_connection_bindings(profile_id, capability_id, capability_version, slot);
                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (15, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 16)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE feature_builder_sessions (
                        id TEXT NOT NULL PRIMARY KEY CHECK (
                            length(id) = 36 AND substr(id, 1, 4) = 'fbs_' AND
                            substr(id, 5) NOT GLOB '*[^0-9a-f]*'),
                        mode TEXT NOT NULL CHECK (mode IN ('create', 'revise')),
                        state TEXT NOT NULL CHECK (state IN (
                            'collecting', 'awaiting_information', 'generating', 'proposal_ready',
                            'completed', 'cancelled', 'interrupted')),
                        revision INTEGER NOT NULL CHECK (revision >= 0),
                        requirement TEXT NOT NULL CHECK (length(requirement) BETWEEN 1 AND 8192),
                        requirement_hash TEXT NOT NULL CHECK (
                            length(requirement_hash) = 71 AND substr(requirement_hash, 1, 7) = 'sha256:' AND
                            substr(requirement_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        answers_json TEXT NOT NULL CHECK (
                            length(answers_json) BETWEEN 2 AND 131072 AND
                            json_valid(answers_json) AND json_type(answers_json) = 'array'),
                        questions_json TEXT NOT NULL CHECK (
                            length(questions_json) BETWEEN 2 AND 32768 AND
                            json_valid(questions_json) AND json_type(questions_json) = 'array'),
                        base_capability_id TEXT NULL,
                        base_version TEXT NULL,
                        base_content_hash TEXT NULL,
                        expected_catalog_revision INTEGER NOT NULL CHECK (expected_catalog_revision >= 0),
                        inventory_hash TEXT NOT NULL CHECK (
                            length(inventory_hash) = 71 AND substr(inventory_hash, 1, 7) = 'sha256:' AND
                            substr(inventory_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        active_generation_id TEXT NULL CHECK (
                            active_generation_id IS NULL OR
                            (length(active_generation_id) = 38 AND substr(active_generation_id, 1, 6) = 'fbgen_' AND
                             substr(active_generation_id, 7) NOT GLOB '*[^0-9a-f]*')),
                        fixed_error_code TEXT NULL CHECK (
                            fixed_error_code IS NULL OR length(fixed_error_code) BETWEEN 3 AND 128),
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        CHECK ((base_capability_id IS NULL) = (base_version IS NULL)),
                        CHECK ((base_capability_id IS NULL) = (base_content_hash IS NULL)),
                        CHECK ((mode = 'create' AND base_capability_id IS NULL) OR
                               (mode = 'revise' AND base_capability_id IS NOT NULL)),
                        CHECK ((mode = 'create' AND expected_catalog_revision = 0) OR
                               (mode = 'revise' AND expected_catalog_revision > 0)),
                        CHECK ((state = 'generating') = (active_generation_id IS NOT NULL)),
                        FOREIGN KEY (base_capability_id, base_version, base_content_hash)
                            REFERENCES capability_versions(capability_id, version, content_hash) ON DELETE RESTRICT
                    );
                    CREATE INDEX ix_feature_builder_sessions_state_updated
                        ON feature_builder_sessions(state, updated_at_utc DESC);

                    CREATE TABLE improvement_proposals (
                        id TEXT NOT NULL PRIMARY KEY CHECK (
                            length(id) = 57 AND substr(id, 1, 9) = 'proposal_' AND
                            substr(id, 10) NOT GLOB '*[^0-9a-f]*'),
                        session_id TEXT NOT NULL,
                        session_revision INTEGER NOT NULL CHECK (session_revision >= 0),
                        generation_id TEXT NOT NULL UNIQUE CHECK (
                            length(generation_id) = 38 AND substr(generation_id, 1, 6) = 'fbgen_' AND
                            substr(generation_id, 7) NOT GLOB '*[^0-9a-f]*'),
                        request_hash TEXT NOT NULL CHECK (
                            length(request_hash) = 71 AND substr(request_hash, 1, 7) = 'sha256:' AND
                            substr(request_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        response_hash TEXT NOT NULL CHECK (
                            length(response_hash) = 71 AND substr(response_hash, 1, 7) = 'sha256:' AND
                            substr(response_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        inventory_hash TEXT NOT NULL CHECK (
                            length(inventory_hash) = 71 AND substr(inventory_hash, 1, 7) = 'sha256:' AND
                            substr(inventory_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        base_capability_id TEXT NULL,
                        base_version TEXT NULL,
                        base_content_hash TEXT NULL,
                        expected_catalog_revision INTEGER NOT NULL CHECK (expected_catalog_revision >= 0),
                        candidate_content_hash TEXT NULL CHECK (
                            candidate_content_hash IS NULL OR
                            (length(candidate_content_hash) = 71 AND substr(candidate_content_hash, 1, 7) = 'sha256:' AND
                             substr(candidate_content_hash, 8) NOT GLOB '*[^0-9a-f]*')),
                        proposal_hash TEXT NOT NULL CHECK (
                            length(proposal_hash) = 71 AND substr(proposal_hash, 1, 7) = 'sha256:' AND
                            substr(proposal_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        manifest_json TEXT NULL CHECK (
                            manifest_json IS NULL OR
                            (json_valid(manifest_json) AND json_type(manifest_json) = 'object')),
                        instructions_markdown TEXT NULL,
                        input_schema_json TEXT NULL CHECK (
                            input_schema_json IS NULL OR
                            (json_valid(input_schema_json) AND json_type(input_schema_json) = 'object')),
                        settings_schema_json TEXT NULL CHECK (
                            settings_schema_json IS NULL OR
                            (json_valid(settings_schema_json) AND json_type(settings_schema_json) = 'object')),
                        workflow_json TEXT NULL CHECK (
                            workflow_json IS NULL OR
                            (json_valid(workflow_json) AND json_type(workflow_json) = 'object')),
                        tests_json TEXT NULL CHECK (
                            tests_json IS NULL OR
                            (json_valid(tests_json) AND json_type(tests_json) = 'array')),
                        known_limitations_json TEXT NOT NULL CHECK (
                            length(known_limitations_json) BETWEEN 2 AND 32768 AND
                            json_valid(known_limitations_json) AND json_type(known_limitations_json) = 'array'),
                        test_evidence_hash TEXT NULL CHECK (
                            test_evidence_hash IS NULL OR
                            (length(test_evidence_hash) = 71 AND substr(test_evidence_hash, 1, 7) = 'sha256:' AND
                             substr(test_evidence_hash, 8) NOT GLOB '*[^0-9a-f]*')),
                        status TEXT NOT NULL CHECK (status IN (
                            'pending', 'rejected', 'quarantined', 'stale', 'applied')),
                        revision INTEGER NOT NULL CHECK (revision >= 0),
                        fixed_issue_code TEXT NULL CHECK (
                            fixed_issue_code IS NULL OR length(fixed_issue_code) BETWEEN 3 AND 128),
                        applied_draft_id TEXT NULL UNIQUE,
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        terminal_at_utc TEXT NULL,
                        CHECK ((base_capability_id IS NULL) = (base_version IS NULL)),
                        CHECK ((base_capability_id IS NULL) = (base_content_hash IS NULL)),
                        CHECK ((base_capability_id IS NULL) = (expected_catalog_revision = 0)),
                        CHECK ((candidate_content_hash IS NULL) = (manifest_json IS NULL)),
                        CHECK ((manifest_json IS NULL) = (instructions_markdown IS NULL)),
                        CHECK ((manifest_json IS NULL) = (input_schema_json IS NULL)),
                        CHECK ((manifest_json IS NULL) = (tests_json IS NULL)),
                        CHECK ((status = 'quarantined') OR manifest_json IS NOT NULL),
                        CHECK ((status = 'pending' AND terminal_at_utc IS NULL AND applied_draft_id IS NULL) OR
                               (status <> 'pending' AND terminal_at_utc IS NOT NULL)),
                        CHECK ((status = 'applied') = (applied_draft_id IS NOT NULL)),
                        CHECK (status <> 'applied' OR test_evidence_hash IS NOT NULL),
                        FOREIGN KEY (session_id) REFERENCES feature_builder_sessions(id) ON DELETE RESTRICT,
                        FOREIGN KEY (base_capability_id, base_version, base_content_hash)
                            REFERENCES capability_versions(capability_id, version, content_hash) ON DELETE RESTRICT,
                        FOREIGN KEY (applied_draft_id) REFERENCES capability_drafts(id) ON DELETE RESTRICT
                    );
                    CREATE INDEX ix_improvement_proposals_session_status
                        ON improvement_proposals(session_id, status, updated_at_utc DESC);
                    CREATE TRIGGER improvement_proposals_reject_payload_update
                    BEFORE UPDATE ON improvement_proposals
                    WHEN OLD.id IS NOT NEW.id
                      OR OLD.session_id IS NOT NEW.session_id
                      OR OLD.session_revision IS NOT NEW.session_revision
                      OR OLD.generation_id IS NOT NEW.generation_id
                      OR OLD.request_hash IS NOT NEW.request_hash
                      OR OLD.response_hash IS NOT NEW.response_hash
                      OR OLD.inventory_hash IS NOT NEW.inventory_hash
                      OR OLD.base_capability_id IS NOT NEW.base_capability_id
                      OR OLD.base_version IS NOT NEW.base_version
                      OR OLD.base_content_hash IS NOT NEW.base_content_hash
                      OR OLD.expected_catalog_revision IS NOT NEW.expected_catalog_revision
                      OR OLD.candidate_content_hash IS NOT NEW.candidate_content_hash
                      OR OLD.proposal_hash IS NOT NEW.proposal_hash
                      OR OLD.manifest_json IS NOT NEW.manifest_json
                      OR OLD.instructions_markdown IS NOT NEW.instructions_markdown
                      OR OLD.input_schema_json IS NOT NEW.input_schema_json
                      OR OLD.settings_schema_json IS NOT NEW.settings_schema_json
                      OR OLD.workflow_json IS NOT NEW.workflow_json
                      OR OLD.tests_json IS NOT NEW.tests_json
                      OR OLD.known_limitations_json IS NOT NEW.known_limitations_json
                      OR OLD.created_at_utc IS NOT NEW.created_at_utc
                    BEGIN
                        SELECT RAISE(ABORT, 'capability proposal payload is immutable');
                    END;
                    CREATE TRIGGER improvement_proposals_reject_evidence_update
                    BEFORE UPDATE ON improvement_proposals
                    WHEN OLD.test_evidence_hash IS NOT NEW.test_evidence_hash
                     AND NOT (
                         OLD.test_evidence_hash IS NULL
                         AND NEW.test_evidence_hash IS NOT NULL
                         AND OLD.status = 'pending'
                         AND NEW.status = 'applied')
                    BEGIN
                        SELECT RAISE(ABORT, 'capability proposal evidence is immutable');
                    END;
                    CREATE TRIGGER improvement_proposals_reject_terminal_update
                    BEFORE UPDATE ON improvement_proposals
                    WHEN OLD.status <> 'pending' BEGIN
                        SELECT RAISE(ABORT, 'terminal capability proposals are immutable');
                    END;
                    CREATE TRIGGER improvement_proposals_reject_delete
                    BEFORE DELETE ON improvement_proposals BEGIN
                        SELECT RAISE(ABORT, 'capability proposals cannot be deleted');
                    END;

                    CREATE TABLE capability_test_runs (
                        id TEXT NOT NULL PRIMARY KEY CHECK (
                            length(id) = 39 AND substr(id, 1, 7) = 'fbtest_' AND
                            substr(id, 8) NOT GLOB '*[^0-9a-f]*'),
                        proposal_id TEXT NOT NULL,
                        kind TEXT NOT NULL CHECK (kind IN ('static_scan', 'dry_run')),
                        attempt INTEGER NOT NULL CHECK (attempt BETWEEN 1 AND 16),
                        candidate_content_hash TEXT NOT NULL CHECK (
                            length(candidate_content_hash) = 71 AND substr(candidate_content_hash, 1, 7) = 'sha256:' AND
                            substr(candidate_content_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        engine_version TEXT NOT NULL CHECK (
                            length(engine_version) BETWEEN 1 AND 64 AND
                            engine_version NOT GLOB '*[^A-Za-z0-9._-]*'),
                        status TEXT NOT NULL CHECK (status IN ('running', 'passed', 'failed', 'interrupted')),
                        revision INTEGER NOT NULL CHECK (revision >= 0),
                        result_json TEXT NOT NULL CHECK (
                            length(result_json) BETWEEN 2 AND 32768 AND
                            json_valid(result_json) AND json_type(result_json) IN ('object', 'array')),
                        fixed_error_code TEXT NULL CHECK (
                            fixed_error_code IS NULL OR length(fixed_error_code) BETWEEN 3 AND 128),
                        started_at_utc TEXT NOT NULL,
                        completed_at_utc TEXT NULL,
                        CHECK ((status = 'running' AND completed_at_utc IS NULL AND fixed_error_code IS NULL) OR
                               (status = 'passed' AND completed_at_utc IS NOT NULL AND fixed_error_code IS NULL) OR
                               (status IN ('failed', 'interrupted') AND completed_at_utc IS NOT NULL AND fixed_error_code IS NOT NULL)),
                        UNIQUE (proposal_id, kind, attempt),
                        FOREIGN KEY (proposal_id) REFERENCES improvement_proposals(id) ON DELETE RESTRICT
                    );
                    CREATE INDEX ix_capability_test_runs_proposal
                        ON capability_test_runs(proposal_id, kind, attempt DESC);
                    CREATE TRIGGER capability_test_runs_reject_identity_update
                    BEFORE UPDATE ON capability_test_runs
                    WHEN OLD.id IS NOT NEW.id
                      OR OLD.proposal_id IS NOT NEW.proposal_id
                      OR OLD.kind IS NOT NEW.kind
                      OR OLD.attempt IS NOT NEW.attempt
                      OR OLD.candidate_content_hash IS NOT NEW.candidate_content_hash
                      OR OLD.engine_version IS NOT NEW.engine_version
                      OR OLD.started_at_utc IS NOT NEW.started_at_utc
                    BEGIN
                        SELECT RAISE(ABORT, 'capability test run identity is immutable');
                    END;
                    CREATE TRIGGER capability_test_runs_reject_terminal_update
                    BEFORE UPDATE ON capability_test_runs
                    WHEN OLD.status <> 'running' BEGIN
                        SELECT RAISE(ABORT, 'terminal capability test runs are immutable');
                    END;
                    CREATE TRIGGER capability_test_runs_reject_delete
                    BEFORE DELETE ON capability_test_runs BEGIN
                        SELECT RAISE(ABORT, 'capability test runs cannot be deleted');
                    END;

                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (16, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 17)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE mail_outbox_operations (
                        operation_id TEXT NOT NULL PRIMARY KEY CHECK (
                            length(operation_id) = 68
                            AND substr(operation_id, 1, 4) = 'mgo_'
                            AND substr(operation_id, 5) NOT GLOB '*[^0-9a-f]*'),
                        idempotency_key TEXT NOT NULL UNIQUE CHECK (
                            length(idempotency_key) = 68
                            AND substr(idempotency_key, 1, 4) = 'mik_'
                            AND substr(idempotency_key, 5) NOT GLOB '*[^0-9a-f]*'),
                        conversation_id TEXT NOT NULL CHECK (length(conversation_id) BETWEEN 1 AND 256),
                        workflow_run_id TEXT NOT NULL CHECK (length(workflow_run_id) BETWEEN 1 AND 256),
                        tool_call_id TEXT NOT NULL UNIQUE CHECK (length(tool_call_id) BETWEEN 1 AND 256),
                        capability_id TEXT NOT NULL CHECK (length(capability_id) BETWEEN 3 AND 128),
                        capability_version TEXT NOT NULL CHECK (length(capability_version) BETWEEN 5 AND 64),
                        capability_content_hash TEXT NOT NULL CHECK (
                            length(capability_content_hash) = 71
                            AND substr(capability_content_hash, 1, 7) = 'sha256:'
                            AND substr(capability_content_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        catalog_revision INTEGER NOT NULL CHECK (
                            catalog_revision BETWEEN 1 AND 9007199254740991),
                        snapshot_hash TEXT NOT NULL CHECK (
                            length(snapshot_hash) = 71
                            AND substr(snapshot_hash, 1, 7) = 'sha256:'
                            AND substr(snapshot_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        connection_slot TEXT NOT NULL CHECK (
                            length(connection_slot) BETWEEN 1 AND 64
                            AND substr(connection_slot, 1, 1) GLOB '[a-z]'
                            AND connection_slot NOT GLOB '*[^a-z0-9._-]*'),
                        binding_hash TEXT NOT NULL CHECK (
                            length(binding_hash) = 71
                            AND substr(binding_hash, 1, 7) = 'sha256:'
                            AND substr(binding_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        draft_hash TEXT NOT NULL CHECK (
                            length(draft_hash) = 71
                            AND substr(draft_hash, 1, 7) = 'sha256:'
                            AND substr(draft_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        submission_state TEXT NOT NULL CHECK (submission_state IN (
                            'staged', 'submitting', 'accepted', 'failed_known',
                            'outcome_unknown', 'cancelled_not_started')),
                        delivery_state TEXT NOT NULL CHECK (delivery_state IN (
                            'unknown', 'queued', 'delivered', 'failed')),
                        attempt_count INTEGER NOT NULL CHECK (attempt_count BETWEEN 0 AND 2),
                        fixed_error_code TEXT NULL CHECK (fixed_error_code IS NULL OR (
                            length(fixed_error_code) BETWEEN 3 AND 128
                            AND substr(fixed_error_code, 1, 1) GLOB '[a-z]'
                            AND fixed_error_code NOT GLOB '*[^a-z0-9._-]*')),
                        revision INTEGER NOT NULL CHECK (revision >= 0),
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        CHECK (
                            (submission_state = 'staged'
                                AND delivery_state = 'unknown'
                                AND attempt_count = 0
                                AND fixed_error_code IS NULL)
                            OR (submission_state = 'submitting'
                                AND delivery_state = 'unknown'
                                AND attempt_count BETWEEN 1 AND 2
                                AND fixed_error_code IS NULL)
                            OR (submission_state = 'accepted'
                                AND delivery_state IN ('queued', 'delivered')
                                AND attempt_count BETWEEN 1 AND 2
                                AND fixed_error_code IS NULL)
                            OR (submission_state = 'accepted'
                                AND delivery_state = 'failed'
                                AND attempt_count BETWEEN 1 AND 2
                                AND fixed_error_code IS NOT NULL)
                            OR (submission_state = 'failed_known'
                                AND delivery_state = 'failed'
                                AND attempt_count BETWEEN 1 AND 2
                                AND fixed_error_code IS NOT NULL)
                            OR (submission_state = 'outcome_unknown'
                                AND delivery_state = 'unknown'
                                AND attempt_count BETWEEN 1 AND 2
                                AND fixed_error_code IS NOT NULL)
                            OR (submission_state = 'cancelled_not_started'
                                AND delivery_state = 'unknown'
                                AND attempt_count = 0
                                AND fixed_error_code IS NOT NULL)),
                        FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
                        FOREIGN KEY (workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE,
                        FOREIGN KEY (tool_call_id) REFERENCES workflow_step_runs(tool_call_id) ON DELETE CASCADE
                    );
                    CREATE INDEX ix_mail_outbox_recovery
                        ON mail_outbox_operations(submission_state, updated_at_utc, operation_id);
                    CREATE INDEX ix_mail_outbox_outcome_unknown
                        ON mail_outbox_operations(draft_hash, binding_hash, updated_at_utc DESC, operation_id)
                        WHERE submission_state = 'outcome_unknown';
                    CREATE TRIGGER mail_outbox_require_workflow_identity
                    BEFORE INSERT ON mail_outbox_operations
                    WHEN NOT EXISTS (
                        SELECT 1
                        FROM workflow_step_runs AS step
                        JOIN workflow_runs AS run ON run.id = step.workflow_run_id
                        WHERE step.tool_call_id = NEW.tool_call_id
                          AND step.workflow_run_id = NEW.workflow_run_id
                          AND step.tool_name = 'mail.send.v1'
                          AND step.risk = 'R3'
                          AND step.retry_class = 'never'
                          AND step.maximum_retries = 0
                          AND step.effect_class = 'effect'
                          AND run.conversation_id = NEW.conversation_id
                          AND run.capability_id = NEW.capability_id
                          AND run.capability_version = NEW.capability_version
                          AND run.capability_content_hash = NEW.capability_content_hash
                          AND run.catalog_revision = NEW.catalog_revision
                          AND run.snapshot_hash = NEW.snapshot_hash
                    )
                    BEGIN
                        SELECT RAISE(ABORT, 'mail outbox workflow identity mismatch');
                    END;
                    CREATE TRIGGER mail_outbox_reject_identity_update
                    BEFORE UPDATE ON mail_outbox_operations
                    WHEN OLD.operation_id IS NOT NEW.operation_id
                      OR OLD.idempotency_key IS NOT NEW.idempotency_key
                      OR OLD.conversation_id IS NOT NEW.conversation_id
                      OR OLD.workflow_run_id IS NOT NEW.workflow_run_id
                      OR OLD.tool_call_id IS NOT NEW.tool_call_id
                      OR OLD.capability_id IS NOT NEW.capability_id
                      OR OLD.capability_version IS NOT NEW.capability_version
                      OR OLD.capability_content_hash IS NOT NEW.capability_content_hash
                      OR OLD.catalog_revision IS NOT NEW.catalog_revision
                      OR OLD.snapshot_hash IS NOT NEW.snapshot_hash
                      OR OLD.connection_slot IS NOT NEW.connection_slot
                      OR OLD.binding_hash IS NOT NEW.binding_hash
                      OR OLD.draft_hash IS NOT NEW.draft_hash
                      OR OLD.created_at_utc IS NOT NEW.created_at_utc
                    BEGIN
                        SELECT RAISE(ABORT, 'mail outbox identity is immutable');
                    END;
                    CREATE TRIGGER mail_outbox_reject_terminal_update
                    BEFORE UPDATE ON mail_outbox_operations
                    WHEN OLD.submission_state IN (
                            'failed_known', 'cancelled_not_started')
                      OR (OLD.submission_state = 'accepted'
                          AND OLD.delivery_state IN ('delivered', 'failed'))
                    BEGIN
                        SELECT RAISE(ABORT, 'terminal mail outbox operation is immutable');
                    END;
                    CREATE TRIGGER mail_outbox_require_legal_transition
                    BEFORE UPDATE ON mail_outbox_operations
                    WHEN NEW.revision <> OLD.revision + 1
                      OR NEW.updated_at_utc < OLD.updated_at_utc
                      OR NOT (
                          (OLD.submission_state = 'staged'
                              AND OLD.delivery_state = 'unknown'
                              AND OLD.attempt_count = 0
                              AND NEW.submission_state = 'submitting'
                              AND NEW.delivery_state = 'unknown'
                              AND NEW.attempt_count = 1
                              AND NEW.fixed_error_code IS NULL)
                          OR (OLD.submission_state = 'staged'
                              AND OLD.delivery_state = 'unknown'
                              AND OLD.attempt_count = 0
                              AND NEW.submission_state = 'cancelled_not_started'
                              AND NEW.delivery_state = 'unknown'
                              AND NEW.attempt_count = 0
                              AND NEW.fixed_error_code IS NOT NULL)
                          OR (OLD.submission_state = 'submitting'
                              AND OLD.delivery_state = 'unknown'
                              AND NEW.submission_state = 'accepted'
                              AND NEW.delivery_state = 'queued'
                              AND NEW.attempt_count = OLD.attempt_count
                              AND NEW.fixed_error_code IS NULL)
                          OR (OLD.submission_state = 'submitting'
                              AND OLD.delivery_state = 'unknown'
                              AND NEW.submission_state = 'failed_known'
                              AND NEW.delivery_state = 'failed'
                              AND NEW.attempt_count = OLD.attempt_count
                              AND NEW.fixed_error_code IS NOT NULL)
                          OR (OLD.submission_state = 'submitting'
                              AND OLD.delivery_state = 'unknown'
                              AND NEW.submission_state = 'outcome_unknown'
                              AND NEW.delivery_state = 'unknown'
                              AND NEW.attempt_count = OLD.attempt_count
                              AND NEW.fixed_error_code IS NOT NULL)
                          OR (OLD.submission_state = 'outcome_unknown'
                              AND OLD.delivery_state = 'unknown'
                              AND NEW.submission_state = 'accepted'
                              AND NEW.delivery_state = 'queued'
                              AND NEW.attempt_count = OLD.attempt_count
                              AND NEW.fixed_error_code IS NULL)
                          OR (OLD.submission_state = 'outcome_unknown'
                              AND OLD.delivery_state = 'unknown'
                              AND NEW.submission_state = 'failed_known'
                              AND NEW.delivery_state = 'failed'
                              AND NEW.attempt_count = OLD.attempt_count
                              AND NEW.fixed_error_code IS NOT NULL)
                          OR (OLD.submission_state = 'outcome_unknown'
                              AND OLD.delivery_state = 'unknown'
                              AND OLD.attempt_count = 1
                              AND NEW.submission_state = 'submitting'
                              AND NEW.delivery_state = 'unknown'
                              AND NEW.attempt_count = 2
                              AND NEW.fixed_error_code IS NULL)
                          OR (OLD.submission_state = 'accepted'
                              AND OLD.delivery_state = 'queued'
                              AND NEW.submission_state = 'accepted'
                              AND NEW.delivery_state = 'delivered'
                              AND NEW.attempt_count = OLD.attempt_count
                              AND NEW.fixed_error_code IS NULL)
                          OR (OLD.submission_state = 'accepted'
                              AND OLD.delivery_state = 'queued'
                              AND NEW.submission_state = 'accepted'
                              AND NEW.delivery_state = 'failed'
                              AND NEW.attempt_count = OLD.attempt_count
                              AND NEW.fixed_error_code IS NOT NULL))
                    BEGIN
                        SELECT RAISE(ABORT, 'mail outbox transition is invalid');
                    END;
                    CREATE TRIGGER mail_outbox_reject_unresolved_delete
                    BEFORE DELETE ON mail_outbox_operations
                    WHEN OLD.submission_state IN ('staged', 'submitting', 'outcome_unknown')
                      OR (OLD.submission_state = 'accepted'
                          AND OLD.delivery_state = 'queued')
                    BEGIN
                        SELECT RAISE(ABORT, 'unresolved mail outbox operation cannot be deleted');
                    END;

                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (17, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 18)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE mail_gateway_connection_profiles (
                        id TEXT NOT NULL PRIMARY KEY CHECK (
                            length(id) BETWEEN 1 AND 64
                            AND substr(id, 1, 1) GLOB '[a-z]'
                            AND id NOT GLOB '*[^a-z0-9-]*'),
                        display_name TEXT NOT NULL CHECK (
                            length(display_name) BETWEEN 1 AND 80
                            AND trim(display_name) = display_name),
                        base_url TEXT NOT NULL CHECK (
                            length(base_url) BETWEEN 8 AND 2048
                            AND substr(base_url, 1, 8) = 'https://'),
                        sender TEXT NOT NULL CHECK (
                            length(sender) BETWEEN 3 AND 254
                            AND instr(sender, '@') BETWEEN 2 AND length(sender) - 1),
                        enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
                        state TEXT NOT NULL CHECK (state IN ('staged', 'updating', 'stable', 'error')),
                        revision INTEGER NOT NULL CHECK (revision > 0),
                        target_revision INTEGER NOT NULL CHECK (target_revision > 0),
                        credential_revision INTEGER NOT NULL CHECK (credential_revision >= 0),
                        target_hash TEXT NOT NULL CHECK (
                            length(target_hash) = 71
                            AND substr(target_hash, 1, 7) = 'sha256:'
                            AND substr(target_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        fixed_error_code TEXT NULL CHECK (fixed_error_code IS NULL OR (
                            length(fixed_error_code) BETWEEN 3 AND 128
                            AND substr(fixed_error_code, 1, 1) GLOB '[a-z]'
                            AND fixed_error_code NOT GLOB '*[^a-z0-9._]*')),
                        created_at_utc TEXT NOT NULL CHECK (
                            length(created_at_utc) = 33
                            AND substr(created_at_utc, 28, 6) = '+00:00'),
                        updated_at_utc TEXT NOT NULL CHECK (
                            length(updated_at_utc) = 33
                            AND substr(updated_at_utc, 28, 6) = '+00:00'),
                        CHECK (updated_at_utc >= created_at_utc),
                        CHECK ((state = 'error' AND fixed_error_code IS NOT NULL) OR
                               (state <> 'error' AND fixed_error_code IS NULL))
                    );
                    CREATE TABLE mail_gateway_probe_results (
                        profile_id TEXT NOT NULL PRIMARY KEY,
                        profile_target_revision INTEGER NOT NULL CHECK (profile_target_revision > 0),
                        profile_credential_revision INTEGER NOT NULL CHECK (profile_credential_revision >= 0),
                        profile_target_hash TEXT NOT NULL CHECK (
                            length(profile_target_hash) = 71
                            AND substr(profile_target_hash, 1, 7) = 'sha256:'
                            AND substr(profile_target_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        succeeded INTEGER NOT NULL CHECK (succeeded IN (0, 1)),
                        fixed_error_code TEXT NULL CHECK (fixed_error_code IS NULL OR (
                            length(fixed_error_code) BETWEEN 3 AND 128
                            AND substr(fixed_error_code, 1, 1) GLOB '[a-z]'
                            AND fixed_error_code NOT GLOB '*[^a-z0-9._]*')),
                        checked_at_utc TEXT NOT NULL CHECK (
                            length(checked_at_utc) = 33
                            AND substr(checked_at_utc, 28, 6) = '+00:00'),
                        CHECK ((succeeded = 1 AND fixed_error_code IS NULL) OR
                               (succeeded = 0 AND fixed_error_code IS NOT NULL)),
                        FOREIGN KEY (profile_id)
                            REFERENCES mail_gateway_connection_profiles(id) ON DELETE CASCADE
                    );
                    CREATE TABLE capability_mail_gateway_bindings (
                        capability_id TEXT NOT NULL CHECK (length(capability_id) BETWEEN 3 AND 128),
                        capability_version TEXT NOT NULL CHECK (length(capability_version) BETWEEN 5 AND 64),
                        capability_content_hash TEXT NOT NULL CHECK (
                            length(capability_content_hash) = 71
                            AND substr(capability_content_hash, 1, 7) = 'sha256:'
                            AND substr(capability_content_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        slot TEXT NOT NULL CHECK (
                            length(slot) BETWEEN 1 AND 64
                            AND substr(slot, 1, 1) GLOB '[a-z]'
                            AND slot NOT GLOB '*[^a-z0-9._-]*'),
                        kind TEXT NOT NULL CHECK (kind = 'mail-api'),
                        profile_id TEXT NOT NULL,
                        revision INTEGER NOT NULL CHECK (revision > 0),
                        profile_target_revision INTEGER NOT NULL CHECK (profile_target_revision > 0),
                        profile_credential_revision INTEGER NOT NULL CHECK (profile_credential_revision >= 0),
                        profile_target_hash TEXT NOT NULL CHECK (
                            length(profile_target_hash) = 71
                            AND substr(profile_target_hash, 1, 7) = 'sha256:'
                            AND substr(profile_target_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        binding_hash TEXT NOT NULL CHECK (
                            length(binding_hash) = 71
                            AND substr(binding_hash, 1, 7) = 'sha256:'
                            AND substr(binding_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        created_at_utc TEXT NOT NULL CHECK (
                            length(created_at_utc) = 33
                            AND substr(created_at_utc, 28, 6) = '+00:00'),
                        updated_at_utc TEXT NOT NULL CHECK (
                            length(updated_at_utc) = 33
                            AND substr(updated_at_utc, 28, 6) = '+00:00'),
                        CHECK (updated_at_utc >= created_at_utc),
                        PRIMARY KEY (capability_id, capability_version, capability_content_hash, slot),
                        FOREIGN KEY (capability_id, capability_version, capability_content_hash)
                            REFERENCES capability_drafts(capability_id, proposed_version, content_hash)
                            ON DELETE RESTRICT,
                        FOREIGN KEY (profile_id)
                            REFERENCES mail_gateway_connection_profiles(id) ON DELETE RESTRICT
                    );
                    CREATE INDEX ix_mail_gateway_profiles_management
                        ON mail_gateway_connection_profiles(
                            state, enabled, display_name COLLATE NOCASE, id);
                    CREATE INDEX ix_capability_mail_gateway_bindings_profile
                        ON capability_mail_gateway_bindings(
                            profile_id, capability_id, capability_version, slot);

                    CREATE TRIGGER mail_gateway_profiles_require_staged_insert
                    BEFORE INSERT ON mail_gateway_connection_profiles
                    WHEN NEW.state <> 'staged'
                      OR NEW.revision <> 1
                      OR NEW.target_revision <> 1
                      OR NEW.credential_revision <> 1
                    BEGIN
                        SELECT RAISE(ABORT, 'mail gateway profile must begin staged');
                    END;
                    CREATE TRIGGER mail_gateway_profiles_require_legal_update
                    BEFORE UPDATE ON mail_gateway_connection_profiles
                    WHEN OLD.id IS NOT NEW.id
                      OR OLD.created_at_utc IS NOT NEW.created_at_utc
                      OR NEW.revision <> OLD.revision + 1
                      OR NEW.target_revision NOT IN (OLD.target_revision, OLD.target_revision + 1)
                      OR NEW.credential_revision NOT IN (
                            OLD.credential_revision, OLD.credential_revision + 1)
                      OR NEW.updated_at_utc < OLD.updated_at_utc
                      OR NOT ((OLD.state = 'staged' AND NEW.state = 'updating')
                           OR (OLD.state = 'updating' AND NEW.state IN ('stable', 'error'))
                           OR (OLD.state = 'stable' AND NEW.state = 'updating')
                           OR (OLD.state = 'error' AND NEW.state = 'updating'))
                      OR ((OLD.target_revision <> NEW.target_revision
                            OR OLD.credential_revision <> NEW.credential_revision
                            OR OLD.base_url IS NOT NEW.base_url
                            OR OLD.sender IS NOT NEW.sender
                            OR OLD.enabled IS NOT NEW.enabled
                            OR OLD.target_hash IS NOT NEW.target_hash)
                          AND NEW.state <> 'updating')
                      OR ((OLD.base_url IS NOT NEW.base_url
                            OR OLD.sender IS NOT NEW.sender
                            OR OLD.enabled IS NOT NEW.enabled)
                          AND NEW.target_revision <> OLD.target_revision + 1)
                      OR (OLD.base_url IS NEW.base_url
                          AND OLD.sender IS NEW.sender
                          AND OLD.enabled IS NEW.enabled
                          AND NEW.target_revision <> OLD.target_revision)
                      OR ((OLD.base_url IS NOT NEW.base_url OR OLD.sender IS NOT NEW.sender)
                          AND OLD.target_hash IS NEW.target_hash)
                      OR (OLD.base_url IS NEW.base_url
                          AND OLD.sender IS NEW.sender
                          AND OLD.target_hash IS NOT NEW.target_hash)
                    BEGIN
                        SELECT RAISE(ABORT, 'mail gateway profile update is invalid');
                    END;
                    CREATE TRIGGER mail_gateway_profiles_invalidate_probe
                    AFTER UPDATE ON mail_gateway_connection_profiles
                    WHEN OLD.target_revision <> NEW.target_revision
                      OR OLD.credential_revision <> NEW.credential_revision
                      OR OLD.target_hash <> NEW.target_hash
                    BEGIN
                        DELETE FROM mail_gateway_probe_results WHERE profile_id = NEW.id;
                    END;
                    CREATE TRIGGER mail_gateway_probe_require_current_profile_insert
                    BEFORE INSERT ON mail_gateway_probe_results
                    WHEN NOT EXISTS (
                        SELECT 1 FROM mail_gateway_connection_profiles AS profile
                        WHERE profile.id = NEW.profile_id
                          AND profile.enabled = 1
                          AND profile.state = 'stable'
                          AND profile.target_revision = NEW.profile_target_revision
                          AND profile.credential_revision = NEW.profile_credential_revision
                          AND profile.target_hash = NEW.profile_target_hash)
                    BEGIN
                        SELECT RAISE(ABORT, 'mail gateway probe identity is stale');
                    END;
                    CREATE TRIGGER mail_gateway_probe_require_current_profile_update
                    BEFORE UPDATE ON mail_gateway_probe_results
                    WHEN OLD.profile_id IS NOT NEW.profile_id
                      OR NEW.checked_at_utc < OLD.checked_at_utc
                      OR NOT EXISTS (
                        SELECT 1 FROM mail_gateway_connection_profiles AS profile
                        WHERE profile.id = NEW.profile_id
                          AND profile.enabled = 1
                          AND profile.state = 'stable'
                          AND profile.target_revision = NEW.profile_target_revision
                          AND profile.credential_revision = NEW.profile_credential_revision
                          AND profile.target_hash = NEW.profile_target_hash)
                    BEGIN
                        SELECT RAISE(ABORT, 'mail gateway probe identity is stale');
                    END;
                    CREATE TRIGGER mail_gateway_binding_require_current_profile_insert
                    BEFORE INSERT ON capability_mail_gateway_bindings
                    WHEN NOT EXISTS (
                        SELECT 1 FROM mail_gateway_connection_profiles AS profile
                        WHERE profile.id = NEW.profile_id
                          AND profile.enabled = 1
                          AND profile.state = 'stable'
                          AND profile.target_revision = NEW.profile_target_revision
                          AND profile.credential_revision = NEW.profile_credential_revision
                          AND profile.target_hash = NEW.profile_target_hash)
                    BEGIN
                        SELECT RAISE(ABORT, 'mail gateway binding identity is stale');
                    END;
                    CREATE TRIGGER mail_gateway_binding_require_legal_update
                    BEFORE UPDATE ON capability_mail_gateway_bindings
                    WHEN OLD.capability_id IS NOT NEW.capability_id
                      OR OLD.capability_version IS NOT NEW.capability_version
                      OR OLD.capability_content_hash IS NOT NEW.capability_content_hash
                      OR OLD.slot IS NOT NEW.slot
                      OR OLD.created_at_utc IS NOT NEW.created_at_utc
                      OR NEW.revision <> OLD.revision + 1
                      OR OLD.binding_hash IS NEW.binding_hash
                      OR NEW.updated_at_utc < OLD.updated_at_utc
                      OR NOT EXISTS (
                        SELECT 1 FROM mail_gateway_connection_profiles AS profile
                        WHERE profile.id = NEW.profile_id
                          AND profile.enabled = 1
                          AND profile.state = 'stable'
                          AND profile.target_revision = NEW.profile_target_revision
                          AND profile.credential_revision = NEW.profile_credential_revision
                          AND profile.target_hash = NEW.profile_target_hash)
                    BEGIN
                        SELECT RAISE(ABORT, 'mail gateway binding update is invalid');
                    END;

                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (18, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 19)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE INDEX ix_mail_outbox_network_started_unresolved
                        ON mail_outbox_operations(
                            draft_hash, binding_hash, updated_at_utc DESC, operation_id)
                        WHERE submission_state IN ('submitting', 'outcome_unknown');
                    DROP INDEX ix_mail_outbox_outcome_unknown;

                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (19, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 20)
            {
                await ExecuteAsync(
                    connection,
                    """
                    DROP TRIGGER mail_outbox_require_workflow_identity;
                    DROP TRIGGER mail_outbox_reject_identity_update;
                    DROP TRIGGER mail_outbox_reject_terminal_update;
                    DROP TRIGGER mail_outbox_require_legal_transition;
                    DROP TRIGGER mail_outbox_reject_unresolved_delete;
                    DROP INDEX ix_mail_outbox_recovery;
                    DROP INDEX ix_mail_outbox_network_started_unresolved;
                    ALTER TABLE mail_outbox_operations RENAME TO mail_outbox_operations_schema19;

                    CREATE TABLE mail_outbox_operations (
                        operation_id TEXT NOT NULL PRIMARY KEY CHECK (
                            length(operation_id) = 68
                            AND substr(operation_id, 1, 4) = 'mgo_'
                            AND substr(operation_id, 5) NOT GLOB '*[^0-9a-f]*'),
                        idempotency_key TEXT NOT NULL UNIQUE CHECK (
                            length(idempotency_key) = 68
                            AND substr(idempotency_key, 1, 4) = 'mik_'
                            AND substr(idempotency_key, 5) NOT GLOB '*[^0-9a-f]*'),
                        conversation_id TEXT NOT NULL CHECK (length(conversation_id) BETWEEN 1 AND 256),
                        workflow_run_id TEXT NOT NULL CHECK (length(workflow_run_id) BETWEEN 1 AND 256),
                        tool_call_id TEXT NOT NULL UNIQUE CHECK (length(tool_call_id) BETWEEN 1 AND 256),
                        capability_id TEXT NOT NULL CHECK (length(capability_id) BETWEEN 3 AND 128),
                        capability_version TEXT NOT NULL CHECK (length(capability_version) BETWEEN 5 AND 64),
                        capability_content_hash TEXT NOT NULL CHECK (
                            length(capability_content_hash) = 71
                            AND substr(capability_content_hash, 1, 7) = 'sha256:'
                            AND substr(capability_content_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        catalog_revision INTEGER NOT NULL CHECK (
                            catalog_revision BETWEEN 1 AND 9007199254740991),
                        snapshot_hash TEXT NOT NULL CHECK (
                            length(snapshot_hash) = 71
                            AND substr(snapshot_hash, 1, 7) = 'sha256:'
                            AND substr(snapshot_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        connection_slot TEXT NOT NULL CHECK (
                            length(connection_slot) BETWEEN 1 AND 64
                            AND substr(connection_slot, 1, 1) GLOB '[a-z]'
                            AND connection_slot NOT GLOB '*[^a-z0-9._-]*'),
                        binding_hash TEXT NOT NULL CHECK (
                            length(binding_hash) = 71
                            AND substr(binding_hash, 1, 7) = 'sha256:'
                            AND substr(binding_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        draft_hash TEXT NOT NULL CHECK (
                            length(draft_hash) = 71
                            AND substr(draft_hash, 1, 7) = 'sha256:'
                            AND substr(draft_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        transport_kind TEXT NOT NULL CHECK (transport_kind IN ('mail-api', 'smtp')),
                        smtp_message_id TEXT NULL,
                        smtp_phase TEXT NULL CHECK (smtp_phase IS NULL OR smtp_phase IN ('pre_data', 'data_started')),
                        submission_state TEXT NOT NULL CHECK (submission_state IN (
                            'staged', 'submitting', 'accepted', 'failed_known',
                            'outcome_unknown', 'cancelled_not_started')),
                        delivery_state TEXT NOT NULL CHECK (delivery_state IN (
                            'unknown', 'queued', 'delivered', 'failed')),
                        attempt_count INTEGER NOT NULL CHECK (attempt_count BETWEEN 0 AND 2),
                        fixed_error_code TEXT NULL CHECK (fixed_error_code IS NULL OR (
                            length(fixed_error_code) BETWEEN 3 AND 128
                            AND substr(fixed_error_code, 1, 1) GLOB '[a-z]'
                            AND fixed_error_code NOT GLOB '*[^a-z0-9._-]*')),
                        revision INTEGER NOT NULL CHECK (revision >= 0),
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        CHECK (
                            (transport_kind = 'mail-api'
                                AND smtp_message_id IS NULL
                                AND smtp_phase IS NULL
                                AND (
                                    (submission_state = 'staged'
                                        AND delivery_state = 'unknown'
                                        AND attempt_count = 0
                                        AND fixed_error_code IS NULL)
                                    OR (submission_state = 'submitting'
                                        AND delivery_state = 'unknown'
                                        AND attempt_count BETWEEN 1 AND 2
                                        AND fixed_error_code IS NULL)
                                    OR (submission_state = 'accepted'
                                        AND delivery_state IN ('queued', 'delivered')
                                        AND attempt_count BETWEEN 1 AND 2
                                        AND fixed_error_code IS NULL)
                                    OR (submission_state = 'accepted'
                                        AND delivery_state = 'failed'
                                        AND attempt_count BETWEEN 1 AND 2
                                        AND fixed_error_code IS NOT NULL)
                                    OR (submission_state = 'failed_known'
                                        AND delivery_state = 'failed'
                                        AND attempt_count BETWEEN 1 AND 2
                                        AND fixed_error_code IS NOT NULL)
                                    OR (submission_state = 'outcome_unknown'
                                        AND delivery_state = 'unknown'
                                        AND attempt_count BETWEEN 1 AND 2
                                        AND fixed_error_code IS NOT NULL)
                                    OR (submission_state = 'cancelled_not_started'
                                        AND delivery_state = 'unknown'
                                        AND attempt_count = 0
                                        AND fixed_error_code IS NOT NULL)))
                            OR (transport_kind = 'smtp'
                                AND length(smtp_message_id) = 90
                                AND substr(smtp_message_id, 1, 9) = '<ligclaw.'
                                AND substr(smtp_message_id, 10, 64) = substr(operation_id, 5, 64)
                                AND substr(smtp_message_id, 10, 64) NOT GLOB '*[^0-9a-f]*'
                                AND substr(smtp_message_id, 74, 17) = '@ligclaw.invalid>'
                                AND smtp_phase IN ('pre_data', 'data_started')
                                AND attempt_count BETWEEN 0 AND 1
                                AND (
                                    (submission_state = 'staged'
                                        AND delivery_state = 'unknown'
                                        AND smtp_phase = 'pre_data'
                                        AND attempt_count = 0
                                        AND fixed_error_code IS NULL)
                                    OR (submission_state = 'submitting'
                                        AND delivery_state = 'unknown'
                                        AND attempt_count = 1
                                        AND fixed_error_code IS NULL)
                                    OR (submission_state = 'accepted'
                                        AND delivery_state = 'queued'
                                        AND smtp_phase = 'data_started'
                                        AND attempt_count = 1
                                        AND fixed_error_code IS NULL)
                                    OR (submission_state = 'failed_known'
                                        AND delivery_state = 'failed'
                                        AND attempt_count = 1
                                        AND fixed_error_code IS NOT NULL)
                                    OR (submission_state = 'outcome_unknown'
                                        AND delivery_state = 'unknown'
                                        AND smtp_phase = 'data_started'
                                        AND attempt_count = 1
                                        AND fixed_error_code IS NOT NULL)
                                    OR (submission_state = 'cancelled_not_started'
                                        AND delivery_state = 'unknown'
                                        AND smtp_phase = 'pre_data'
                                        AND attempt_count = 0
                                        AND fixed_error_code IS NOT NULL)))),
                        FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
                        FOREIGN KEY (workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE,
                        FOREIGN KEY (tool_call_id) REFERENCES workflow_step_runs(tool_call_id) ON DELETE CASCADE
                    );

                    INSERT INTO mail_outbox_operations(
                        operation_id, idempotency_key, conversation_id, workflow_run_id, tool_call_id,
                        capability_id, capability_version, capability_content_hash, catalog_revision,
                        snapshot_hash, connection_slot, binding_hash, draft_hash, transport_kind,
                        smtp_message_id, smtp_phase, submission_state, delivery_state, attempt_count,
                        fixed_error_code, revision, created_at_utc, updated_at_utc)
                    SELECT operation_id, idempotency_key, conversation_id, workflow_run_id, tool_call_id,
                           capability_id, capability_version, capability_content_hash, catalog_revision,
                           snapshot_hash, connection_slot, binding_hash, draft_hash, 'mail-api',
                           NULL, NULL, submission_state, delivery_state, attempt_count,
                           fixed_error_code, revision, created_at_utc, updated_at_utc
                    FROM mail_outbox_operations_schema19;
                    DROP TABLE mail_outbox_operations_schema19;

                    CREATE INDEX ix_mail_outbox_recovery
                        ON mail_outbox_operations(
                            transport_kind, submission_state, smtp_phase, updated_at_utc, operation_id);
                    CREATE INDEX ix_mail_outbox_mail_api_unresolved
                        ON mail_outbox_operations(
                            draft_hash, binding_hash, updated_at_utc DESC, operation_id)
                        WHERE transport_kind = 'mail-api'
                          AND submission_state IN ('submitting', 'outcome_unknown');
                    CREATE INDEX ix_mail_outbox_smtp_unresolved
                        ON mail_outbox_operations(draft_hash, updated_at_utc DESC, operation_id)
                        WHERE transport_kind = 'smtp'
                          AND submission_state IN ('submitting', 'outcome_unknown');

                    CREATE TRIGGER mail_outbox_require_workflow_identity
                    BEFORE INSERT ON mail_outbox_operations
                    WHEN NOT EXISTS (
                        SELECT 1
                        FROM workflow_step_runs AS step
                        JOIN workflow_runs AS run ON run.id = step.workflow_run_id
                        WHERE step.tool_call_id = NEW.tool_call_id
                          AND step.workflow_run_id = NEW.workflow_run_id
                          AND step.tool_name = 'mail.send.v1'
                          AND step.risk = 'R3'
                          AND step.retry_class = 'never'
                          AND step.maximum_retries = 0
                          AND step.effect_class = 'effect'
                          AND run.conversation_id = NEW.conversation_id
                          AND run.capability_id = NEW.capability_id
                          AND run.capability_version = NEW.capability_version
                          AND run.capability_content_hash = NEW.capability_content_hash
                          AND run.catalog_revision = NEW.catalog_revision
                          AND run.snapshot_hash = NEW.snapshot_hash)
                    BEGIN
                        SELECT RAISE(ABORT, 'mail outbox workflow identity mismatch');
                    END;
                    CREATE TRIGGER mail_outbox_reject_identity_update
                    BEFORE UPDATE ON mail_outbox_operations
                    WHEN OLD.operation_id IS NOT NEW.operation_id
                      OR OLD.idempotency_key IS NOT NEW.idempotency_key
                      OR OLD.conversation_id IS NOT NEW.conversation_id
                      OR OLD.workflow_run_id IS NOT NEW.workflow_run_id
                      OR OLD.tool_call_id IS NOT NEW.tool_call_id
                      OR OLD.capability_id IS NOT NEW.capability_id
                      OR OLD.capability_version IS NOT NEW.capability_version
                      OR OLD.capability_content_hash IS NOT NEW.capability_content_hash
                      OR OLD.catalog_revision IS NOT NEW.catalog_revision
                      OR OLD.snapshot_hash IS NOT NEW.snapshot_hash
                      OR OLD.connection_slot IS NOT NEW.connection_slot
                      OR OLD.binding_hash IS NOT NEW.binding_hash
                      OR OLD.draft_hash IS NOT NEW.draft_hash
                      OR OLD.transport_kind IS NOT NEW.transport_kind
                      OR OLD.smtp_message_id IS NOT NEW.smtp_message_id
                      OR OLD.created_at_utc IS NOT NEW.created_at_utc
                    BEGIN
                        SELECT RAISE(ABORT, 'mail outbox identity is immutable');
                    END;
                    CREATE TRIGGER mail_outbox_reject_terminal_update
                    BEFORE UPDATE ON mail_outbox_operations
                    WHEN OLD.submission_state IN ('failed_known', 'cancelled_not_started')
                      OR (OLD.transport_kind = 'mail-api'
                          AND OLD.submission_state = 'accepted'
                          AND OLD.delivery_state IN ('delivered', 'failed'))
                      OR (OLD.transport_kind = 'smtp'
                          AND OLD.submission_state IN ('accepted', 'outcome_unknown'))
                    BEGIN
                        SELECT RAISE(ABORT, 'terminal mail outbox operation is immutable');
                    END;
                    CREATE TRIGGER mail_outbox_require_legal_transition
                    BEFORE UPDATE ON mail_outbox_operations
                    WHEN NEW.revision <> OLD.revision + 1
                      OR NEW.updated_at_utc < OLD.updated_at_utc
                      OR NOT (
                          (OLD.transport_kind = 'mail-api' AND (
                              (OLD.submission_state = 'staged'
                                  AND OLD.delivery_state = 'unknown'
                                  AND OLD.attempt_count = 0
                                  AND NEW.submission_state = 'submitting'
                                  AND NEW.delivery_state = 'unknown'
                                  AND NEW.attempt_count = 1
                                  AND NEW.fixed_error_code IS NULL)
                              OR (OLD.submission_state = 'staged'
                                  AND OLD.delivery_state = 'unknown'
                                  AND OLD.attempt_count = 0
                                  AND NEW.submission_state = 'cancelled_not_started'
                                  AND NEW.delivery_state = 'unknown'
                                  AND NEW.attempt_count = 0
                                  AND NEW.fixed_error_code IS NOT NULL)
                              OR (OLD.submission_state = 'submitting'
                                  AND OLD.delivery_state = 'unknown'
                                  AND NEW.submission_state = 'accepted'
                                  AND NEW.delivery_state = 'queued'
                                  AND NEW.attempt_count = OLD.attempt_count
                                  AND NEW.fixed_error_code IS NULL)
                              OR (OLD.submission_state = 'submitting'
                                  AND OLD.delivery_state = 'unknown'
                                  AND NEW.submission_state = 'failed_known'
                                  AND NEW.delivery_state = 'failed'
                                  AND NEW.attempt_count = OLD.attempt_count
                                  AND NEW.fixed_error_code IS NOT NULL)
                              OR (OLD.submission_state = 'submitting'
                                  AND OLD.delivery_state = 'unknown'
                                  AND NEW.submission_state = 'outcome_unknown'
                                  AND NEW.delivery_state = 'unknown'
                                  AND NEW.attempt_count = OLD.attempt_count
                                  AND NEW.fixed_error_code IS NOT NULL)
                              OR (OLD.submission_state = 'outcome_unknown'
                                  AND OLD.delivery_state = 'unknown'
                                  AND NEW.submission_state = 'accepted'
                                  AND NEW.delivery_state = 'queued'
                                  AND NEW.attempt_count = OLD.attempt_count
                                  AND NEW.fixed_error_code IS NULL)
                              OR (OLD.submission_state = 'outcome_unknown'
                                  AND OLD.delivery_state = 'unknown'
                                  AND NEW.submission_state = 'failed_known'
                                  AND NEW.delivery_state = 'failed'
                                  AND NEW.attempt_count = OLD.attempt_count
                                  AND NEW.fixed_error_code IS NOT NULL)
                              OR (OLD.submission_state = 'outcome_unknown'
                                  AND OLD.delivery_state = 'unknown'
                                  AND OLD.attempt_count = 1
                                  AND NEW.submission_state = 'submitting'
                                  AND NEW.delivery_state = 'unknown'
                                  AND NEW.attempt_count = 2
                                  AND NEW.fixed_error_code IS NULL)
                              OR (OLD.submission_state = 'accepted'
                                  AND OLD.delivery_state = 'queued'
                                  AND NEW.submission_state = 'accepted'
                                  AND NEW.delivery_state = 'delivered'
                                  AND NEW.attempt_count = OLD.attempt_count
                                  AND NEW.fixed_error_code IS NULL)
                              OR (OLD.submission_state = 'accepted'
                                  AND OLD.delivery_state = 'queued'
                                  AND NEW.submission_state = 'accepted'
                                  AND NEW.delivery_state = 'failed'
                                  AND NEW.attempt_count = OLD.attempt_count
                                  AND NEW.fixed_error_code IS NOT NULL)))
                          OR (OLD.transport_kind = 'smtp' AND (
                              (OLD.submission_state = 'staged'
                                  AND OLD.smtp_phase = 'pre_data'
                                  AND NEW.submission_state = 'submitting'
                                  AND NEW.delivery_state = 'unknown'
                                  AND NEW.smtp_phase = 'pre_data'
                                  AND NEW.attempt_count = 1
                                  AND NEW.fixed_error_code IS NULL)
                              OR (OLD.submission_state = 'staged'
                                  AND OLD.smtp_phase = 'pre_data'
                                  AND NEW.submission_state = 'cancelled_not_started'
                                  AND NEW.delivery_state = 'unknown'
                                  AND NEW.smtp_phase = 'pre_data'
                                  AND NEW.attempt_count = 0
                                  AND NEW.fixed_error_code IS NOT NULL)
                              OR (OLD.submission_state = 'submitting'
                                  AND OLD.smtp_phase = 'pre_data'
                                  AND NEW.submission_state = 'submitting'
                                  AND NEW.delivery_state = 'unknown'
                                  AND NEW.smtp_phase = 'data_started'
                                  AND NEW.attempt_count = 1
                                  AND NEW.fixed_error_code IS NULL)
                              OR (OLD.submission_state = 'submitting'
                                  AND OLD.smtp_phase = 'pre_data'
                                  AND NEW.submission_state = 'failed_known'
                                  AND NEW.delivery_state = 'failed'
                                  AND NEW.smtp_phase = 'pre_data'
                                  AND NEW.attempt_count = 1
                                  AND NEW.fixed_error_code IS NOT NULL)
                              OR (OLD.submission_state = 'submitting'
                                  AND OLD.smtp_phase = 'data_started'
                                  AND NEW.submission_state = 'accepted'
                                  AND NEW.delivery_state = 'queued'
                                  AND NEW.smtp_phase = 'data_started'
                                  AND NEW.attempt_count = 1
                                  AND NEW.fixed_error_code IS NULL)
                              OR (OLD.submission_state = 'submitting'
                                  AND OLD.smtp_phase = 'data_started'
                                  AND NEW.submission_state = 'failed_known'
                                  AND NEW.delivery_state = 'failed'
                                  AND NEW.smtp_phase = 'data_started'
                                  AND NEW.attempt_count = 1
                                  AND NEW.fixed_error_code IS NOT NULL)
                              OR (OLD.submission_state = 'submitting'
                                  AND OLD.smtp_phase = 'data_started'
                                  AND NEW.submission_state = 'outcome_unknown'
                                  AND NEW.delivery_state = 'unknown'
                                  AND NEW.smtp_phase = 'data_started'
                                  AND NEW.attempt_count = 1
                                  AND NEW.fixed_error_code IS NOT NULL))))
                    BEGIN
                        SELECT RAISE(ABORT, 'mail outbox transition is invalid');
                    END;
                    CREATE TRIGGER mail_outbox_reject_unresolved_delete
                    BEFORE DELETE ON mail_outbox_operations
                    WHEN OLD.submission_state IN ('staged', 'submitting', 'outcome_unknown')
                      OR (OLD.submission_state = 'accepted' AND OLD.delivery_state = 'queued')
                    BEGIN
                        SELECT RAISE(ABORT, 'unresolved mail outbox operation cannot be deleted');
                    END;

                    CREATE TABLE smtp_connection_profiles (
                        id TEXT NOT NULL PRIMARY KEY CHECK (
                            length(id) BETWEEN 1 AND 64
                            AND substr(id, 1, 1) GLOB '[a-z]'
                            AND id NOT GLOB '*[^a-z0-9-]*'),
                        display_name TEXT NOT NULL CHECK (
                            length(display_name) BETWEEN 1 AND 80
                            AND trim(display_name) = display_name),
                        host TEXT NOT NULL CHECK (
                            length(host) BETWEEN 1 AND 253
                            AND host = lower(host)
                            AND host NOT GLOB '*[^a-z0-9.-]*'),
                        port INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
                        tls_mode TEXT NOT NULL CHECK (tls_mode IN (
                            'starttls-required', 'implicit-tls')),
                        authentication TEXT NOT NULL CHECK (authentication = 'plain'),
                        certificate_policy TEXT NOT NULL CHECK (certificate_policy = 'system-trust'),
                        sender TEXT NOT NULL CHECK (
                            length(sender) BETWEEN 3 AND 254
                            AND instr(sender, '@') BETWEEN 2 AND length(sender) - 1),
                        enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
                        state TEXT NOT NULL CHECK (state IN ('staged', 'updating', 'stable', 'error')),
                        revision INTEGER NOT NULL CHECK (revision > 0),
                        target_revision INTEGER NOT NULL CHECK (target_revision > 0),
                        credential_revision INTEGER NOT NULL CHECK (credential_revision >= 0),
                        target_hash TEXT NOT NULL CHECK (
                            length(target_hash) = 71
                            AND substr(target_hash, 1, 7) = 'sha256:'
                            AND substr(target_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        fixed_error_code TEXT NULL CHECK (fixed_error_code IS NULL OR (
                            length(fixed_error_code) BETWEEN 3 AND 128
                            AND substr(fixed_error_code, 1, 1) GLOB '[a-z]'
                            AND fixed_error_code NOT GLOB '*[^a-z0-9._]*')),
                        created_at_utc TEXT NOT NULL CHECK (
                            length(created_at_utc) = 33
                            AND substr(created_at_utc, 28, 6) = '+00:00'),
                        updated_at_utc TEXT NOT NULL CHECK (
                            length(updated_at_utc) = 33
                            AND substr(updated_at_utc, 28, 6) = '+00:00'),
                        CHECK (updated_at_utc >= created_at_utc),
                        CHECK ((state = 'error' AND fixed_error_code IS NOT NULL) OR
                               (state <> 'error' AND fixed_error_code IS NULL))
                    );
                    CREATE TABLE smtp_probe_results (
                        profile_id TEXT NOT NULL PRIMARY KEY,
                        profile_target_revision INTEGER NOT NULL CHECK (profile_target_revision > 0),
                        profile_credential_revision INTEGER NOT NULL CHECK (profile_credential_revision >= 0),
                        profile_target_hash TEXT NOT NULL CHECK (
                            length(profile_target_hash) = 71
                            AND substr(profile_target_hash, 1, 7) = 'sha256:'
                            AND substr(profile_target_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        succeeded INTEGER NOT NULL CHECK (succeeded IN (0, 1)),
                        fixed_error_code TEXT NULL CHECK (fixed_error_code IS NULL OR (
                            length(fixed_error_code) BETWEEN 3 AND 128
                            AND substr(fixed_error_code, 1, 1) GLOB '[a-z]'
                            AND fixed_error_code NOT GLOB '*[^a-z0-9._]*')),
                        checked_at_utc TEXT NOT NULL CHECK (
                            length(checked_at_utc) = 33
                            AND substr(checked_at_utc, 28, 6) = '+00:00'),
                        CHECK ((succeeded = 1 AND fixed_error_code IS NULL) OR
                               (succeeded = 0 AND fixed_error_code IS NOT NULL)),
                        FOREIGN KEY (profile_id)
                            REFERENCES smtp_connection_profiles(id) ON DELETE CASCADE
                    );
                    CREATE TABLE capability_smtp_bindings (
                        capability_id TEXT NOT NULL CHECK (length(capability_id) BETWEEN 3 AND 128),
                        capability_version TEXT NOT NULL CHECK (length(capability_version) BETWEEN 5 AND 64),
                        capability_content_hash TEXT NOT NULL CHECK (
                            length(capability_content_hash) = 71
                            AND substr(capability_content_hash, 1, 7) = 'sha256:'
                            AND substr(capability_content_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        slot TEXT NOT NULL CHECK (
                            length(slot) BETWEEN 1 AND 64
                            AND substr(slot, 1, 1) GLOB '[a-z]'
                            AND slot NOT GLOB '*[^a-z0-9._-]*'),
                        kind TEXT NOT NULL CHECK (kind = 'smtp'),
                        profile_id TEXT NOT NULL,
                        revision INTEGER NOT NULL CHECK (revision > 0),
                        profile_target_revision INTEGER NOT NULL CHECK (profile_target_revision > 0),
                        profile_credential_revision INTEGER NOT NULL CHECK (profile_credential_revision >= 0),
                        profile_target_hash TEXT NOT NULL CHECK (
                            length(profile_target_hash) = 71
                            AND substr(profile_target_hash, 1, 7) = 'sha256:'
                            AND substr(profile_target_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        binding_hash TEXT NOT NULL CHECK (
                            length(binding_hash) = 71
                            AND substr(binding_hash, 1, 7) = 'sha256:'
                            AND substr(binding_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        created_at_utc TEXT NOT NULL CHECK (
                            length(created_at_utc) = 33
                            AND substr(created_at_utc, 28, 6) = '+00:00'),
                        updated_at_utc TEXT NOT NULL CHECK (
                            length(updated_at_utc) = 33
                            AND substr(updated_at_utc, 28, 6) = '+00:00'),
                        CHECK (updated_at_utc >= created_at_utc),
                        PRIMARY KEY (capability_id, capability_version, capability_content_hash, slot),
                        FOREIGN KEY (capability_id, capability_version, capability_content_hash)
                            REFERENCES capability_drafts(capability_id, proposed_version, content_hash)
                            ON DELETE RESTRICT,
                        FOREIGN KEY (profile_id)
                            REFERENCES smtp_connection_profiles(id) ON DELETE RESTRICT
                    );
                    CREATE INDEX ix_smtp_profiles_management
                        ON smtp_connection_profiles(
                            state, enabled, display_name COLLATE NOCASE, id);
                    CREATE INDEX ix_capability_smtp_bindings_profile
                        ON capability_smtp_bindings(
                            profile_id, capability_id, capability_version, slot);

                    CREATE TRIGGER smtp_profiles_require_staged_insert
                    BEFORE INSERT ON smtp_connection_profiles
                    WHEN NEW.state <> 'staged'
                      OR NEW.revision <> 1
                      OR NEW.target_revision <> 1
                      OR NEW.credential_revision <> 1
                    BEGIN
                        SELECT RAISE(ABORT, 'smtp profile must begin staged');
                    END;
                    CREATE TRIGGER smtp_profiles_require_legal_update
                    BEFORE UPDATE ON smtp_connection_profiles
                    WHEN OLD.id IS NOT NEW.id
                      OR OLD.created_at_utc IS NOT NEW.created_at_utc
                      OR NEW.revision <> OLD.revision + 1
                      OR NEW.target_revision NOT IN (OLD.target_revision, OLD.target_revision + 1)
                      OR NEW.credential_revision NOT IN (
                            OLD.credential_revision, OLD.credential_revision + 1)
                      OR NEW.updated_at_utc < OLD.updated_at_utc
                      OR NOT ((OLD.state = 'staged' AND NEW.state = 'updating')
                           OR (OLD.state = 'updating' AND NEW.state IN ('stable', 'error'))
                           OR (OLD.state = 'stable' AND NEW.state = 'updating')
                           OR (OLD.state = 'error' AND NEW.state = 'updating'))
                      OR ((OLD.target_revision <> NEW.target_revision
                            OR OLD.credential_revision <> NEW.credential_revision
                            OR OLD.host IS NOT NEW.host
                            OR OLD.port IS NOT NEW.port
                            OR OLD.tls_mode IS NOT NEW.tls_mode
                            OR OLD.authentication IS NOT NEW.authentication
                            OR OLD.certificate_policy IS NOT NEW.certificate_policy
                            OR OLD.sender IS NOT NEW.sender
                            OR OLD.enabled IS NOT NEW.enabled
                            OR OLD.target_hash IS NOT NEW.target_hash)
                          AND NEW.state <> 'updating')
                      OR ((OLD.host IS NOT NEW.host
                            OR OLD.port IS NOT NEW.port
                            OR OLD.tls_mode IS NOT NEW.tls_mode
                            OR OLD.authentication IS NOT NEW.authentication
                            OR OLD.certificate_policy IS NOT NEW.certificate_policy
                            OR OLD.sender IS NOT NEW.sender
                            OR OLD.enabled IS NOT NEW.enabled)
                          AND NEW.target_revision <> OLD.target_revision + 1)
                      OR (OLD.host IS NEW.host
                          AND OLD.port IS NEW.port
                          AND OLD.tls_mode IS NEW.tls_mode
                          AND OLD.authentication IS NEW.authentication
                          AND OLD.certificate_policy IS NEW.certificate_policy
                          AND OLD.sender IS NEW.sender
                          AND OLD.enabled IS NEW.enabled
                          AND NEW.target_revision <> OLD.target_revision)
                      OR ((OLD.host IS NOT NEW.host
                            OR OLD.port IS NOT NEW.port
                            OR OLD.tls_mode IS NOT NEW.tls_mode
                            OR OLD.authentication IS NOT NEW.authentication
                            OR OLD.certificate_policy IS NOT NEW.certificate_policy
                            OR OLD.sender IS NOT NEW.sender)
                          AND OLD.target_hash IS NEW.target_hash)
                      OR (OLD.host IS NEW.host
                          AND OLD.port IS NEW.port
                          AND OLD.tls_mode IS NEW.tls_mode
                          AND OLD.authentication IS NEW.authentication
                          AND OLD.certificate_policy IS NEW.certificate_policy
                          AND OLD.sender IS NEW.sender
                          AND OLD.target_hash IS NOT NEW.target_hash)
                    BEGIN
                        SELECT RAISE(ABORT, 'smtp profile update is invalid');
                    END;
                    CREATE TRIGGER smtp_profiles_invalidate_probe
                    AFTER UPDATE ON smtp_connection_profiles
                    WHEN OLD.target_revision <> NEW.target_revision
                      OR OLD.credential_revision <> NEW.credential_revision
                      OR OLD.target_hash <> NEW.target_hash
                    BEGIN
                        DELETE FROM smtp_probe_results WHERE profile_id = NEW.id;
                    END;
                    CREATE TRIGGER smtp_profiles_reject_delete
                    BEFORE DELETE ON smtp_connection_profiles
                    BEGIN
                        SELECT RAISE(ABORT, 'smtp profiles cannot be deleted');
                    END;
                    CREATE TRIGGER smtp_probe_require_current_profile_insert
                    BEFORE INSERT ON smtp_probe_results
                    WHEN NOT EXISTS (
                        SELECT 1 FROM smtp_connection_profiles AS profile
                        WHERE profile.id = NEW.profile_id
                          AND profile.enabled = 1
                          AND profile.state = 'stable'
                          AND profile.target_revision = NEW.profile_target_revision
                          AND profile.credential_revision = NEW.profile_credential_revision
                          AND profile.target_hash = NEW.profile_target_hash)
                    BEGIN
                        SELECT RAISE(ABORT, 'smtp probe identity is stale');
                    END;
                    CREATE TRIGGER smtp_probe_require_current_profile_update
                    BEFORE UPDATE ON smtp_probe_results
                    WHEN OLD.profile_id IS NOT NEW.profile_id
                      OR NEW.checked_at_utc < OLD.checked_at_utc
                      OR NOT EXISTS (
                        SELECT 1 FROM smtp_connection_profiles AS profile
                        WHERE profile.id = NEW.profile_id
                          AND profile.enabled = 1
                          AND profile.state = 'stable'
                          AND profile.target_revision = NEW.profile_target_revision
                          AND profile.credential_revision = NEW.profile_credential_revision
                          AND profile.target_hash = NEW.profile_target_hash)
                    BEGIN
                        SELECT RAISE(ABORT, 'smtp probe identity is stale');
                    END;
                    CREATE TRIGGER smtp_binding_require_current_profile_insert
                    BEFORE INSERT ON capability_smtp_bindings
                    WHEN NOT EXISTS (
                        SELECT 1 FROM smtp_connection_profiles AS profile
                        WHERE profile.id = NEW.profile_id
                          AND profile.enabled = 1
                          AND profile.state = 'stable'
                          AND profile.target_revision = NEW.profile_target_revision
                          AND profile.credential_revision = NEW.profile_credential_revision
                          AND profile.target_hash = NEW.profile_target_hash)
                    BEGIN
                        SELECT RAISE(ABORT, 'smtp binding identity is stale');
                    END;
                    CREATE TRIGGER smtp_binding_require_legal_update
                    BEFORE UPDATE ON capability_smtp_bindings
                    WHEN OLD.capability_id IS NOT NEW.capability_id
                      OR OLD.capability_version IS NOT NEW.capability_version
                      OR OLD.capability_content_hash IS NOT NEW.capability_content_hash
                      OR OLD.slot IS NOT NEW.slot
                      OR OLD.created_at_utc IS NOT NEW.created_at_utc
                      OR NEW.revision <> OLD.revision + 1
                      OR OLD.binding_hash IS NEW.binding_hash
                      OR NEW.updated_at_utc < OLD.updated_at_utc
                      OR NOT EXISTS (
                        SELECT 1 FROM smtp_connection_profiles AS profile
                        WHERE profile.id = NEW.profile_id
                          AND profile.enabled = 1
                          AND profile.state = 'stable'
                          AND profile.target_revision = NEW.profile_target_revision
                          AND profile.credential_revision = NEW.profile_credential_revision
                          AND profile.target_hash = NEW.profile_target_hash)
                    BEGIN
                        SELECT RAISE(ABORT, 'smtp binding update is invalid');
                    END;
                    CREATE TRIGGER smtp_bindings_reject_delete
                    BEFORE DELETE ON capability_smtp_bindings
                    BEGIN
                        SELECT RAISE(ABORT, 'smtp bindings cannot be deleted');
                    END;
                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (20, $appliedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }
            if (version < 21)
            {
                await ExecuteAsync(
                    connection,
                    """
                    DROP TRIGGER capability_test_runs_reject_identity_update;
                    DROP TRIGGER capability_test_runs_reject_terminal_update;
                    DROP TRIGGER capability_test_runs_reject_delete;
                    DROP INDEX ix_capability_test_runs_proposal;
                    DROP TRIGGER improvement_proposals_reject_payload_update;
                    DROP TRIGGER improvement_proposals_reject_evidence_update;
                    DROP TRIGGER improvement_proposals_reject_terminal_update;
                    DROP TRIGGER improvement_proposals_reject_delete;
                    DROP INDEX ix_improvement_proposals_session_status;

                    ALTER TABLE capability_test_runs RENAME TO capability_test_runs_schema20;
                    ALTER TABLE improvement_proposals RENAME TO improvement_proposals_schema20;

                    CREATE TABLE improvement_proposals (
                        id TEXT NOT NULL PRIMARY KEY CHECK (
                            length(id) = 57 AND substr(id, 1, 9) = 'proposal_' AND
                            substr(id, 10) NOT GLOB '*[^0-9a-f]*'),
                        session_id TEXT NOT NULL,
                        session_revision INTEGER NOT NULL CHECK (session_revision >= 0),
                        generation_id TEXT NOT NULL UNIQUE CHECK (
                            length(generation_id) = 38 AND substr(generation_id, 1, 6) = 'fbgen_' AND
                            substr(generation_id, 7) NOT GLOB '*[^0-9a-f]*'),
                        request_hash TEXT NOT NULL CHECK (
                            length(request_hash) = 71 AND substr(request_hash, 1, 7) = 'sha256:' AND
                            substr(request_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        response_hash TEXT NOT NULL CHECK (
                            length(response_hash) = 71 AND substr(response_hash, 1, 7) = 'sha256:' AND
                            substr(response_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        inventory_hash TEXT NOT NULL CHECK (
                            length(inventory_hash) = 71 AND substr(inventory_hash, 1, 7) = 'sha256:' AND
                            substr(inventory_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        base_capability_id TEXT NULL,
                        base_version TEXT NULL,
                        base_content_hash TEXT NULL,
                        expected_catalog_revision INTEGER NOT NULL CHECK (expected_catalog_revision >= 0),
                        candidate_content_hash TEXT NULL CHECK (
                            candidate_content_hash IS NULL OR
                            (length(candidate_content_hash) = 71 AND substr(candidate_content_hash, 1, 7) = 'sha256:' AND
                             substr(candidate_content_hash, 8) NOT GLOB '*[^0-9a-f]*')),
                        proposal_hash TEXT NOT NULL CHECK (
                            length(proposal_hash) = 71 AND substr(proposal_hash, 1, 7) = 'sha256:' AND
                            substr(proposal_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        manifest_json TEXT NULL CHECK (
                            manifest_json IS NULL OR
                            (json_valid(manifest_json) AND json_type(manifest_json) = 'object')),
                        instructions_markdown TEXT NULL,
                        input_schema_json TEXT NULL CHECK (
                            input_schema_json IS NULL OR
                            (json_valid(input_schema_json) AND json_type(input_schema_json) = 'object')),
                        settings_schema_json TEXT NULL CHECK (
                            settings_schema_json IS NULL OR
                            (json_valid(settings_schema_json) AND json_type(settings_schema_json) = 'object')),
                        workflow_json TEXT NULL CHECK (
                            workflow_json IS NULL OR
                            (json_valid(workflow_json) AND json_type(workflow_json) = 'object')),
                        tests_json TEXT NULL CHECK (
                            tests_json IS NULL OR
                            (json_valid(tests_json) AND (
                                json_type(tests_json) = 'array' OR
                                (json_type(tests_json) = 'object' AND
                                 json_type(tests_json, '$.schemaVersion') IS 'integer' AND
                                 json_extract(tests_json, '$.schemaVersion') IS 2 AND
                                 json_type(tests_json, '$.cases') IS 'array' AND
                                 json_array_length(tests_json, '$.cases') BETWEEN 1 AND 16 AND
                                 json_remove(tests_json, '$.schemaVersion', '$.cases') = '{}')))),
                        known_limitations_json TEXT NOT NULL CHECK (
                            length(known_limitations_json) BETWEEN 2 AND 32768 AND
                            json_valid(known_limitations_json) AND json_type(known_limitations_json) = 'array'),
                        test_evidence_hash TEXT NULL CHECK (
                            test_evidence_hash IS NULL OR
                            (length(test_evidence_hash) = 71 AND substr(test_evidence_hash, 1, 7) = 'sha256:' AND
                             substr(test_evidence_hash, 8) NOT GLOB '*[^0-9a-f]*')),
                        status TEXT NOT NULL CHECK (status IN (
                            'pending', 'rejected', 'quarantined', 'stale', 'applied')),
                        revision INTEGER NOT NULL CHECK (revision >= 0),
                        fixed_issue_code TEXT NULL CHECK (
                            fixed_issue_code IS NULL OR length(fixed_issue_code) BETWEEN 3 AND 128),
                        applied_draft_id TEXT NULL UNIQUE,
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        terminal_at_utc TEXT NULL,
                        CHECK ((base_capability_id IS NULL) = (base_version IS NULL)),
                        CHECK ((base_capability_id IS NULL) = (base_content_hash IS NULL)),
                        CHECK ((base_capability_id IS NULL) = (expected_catalog_revision = 0)),
                        CHECK ((candidate_content_hash IS NULL) = (manifest_json IS NULL)),
                        CHECK ((manifest_json IS NULL) = (instructions_markdown IS NULL)),
                        CHECK ((manifest_json IS NULL) = (input_schema_json IS NULL)),
                        CHECK ((manifest_json IS NULL) = (tests_json IS NULL)),
                        CHECK ((status = 'quarantined') OR manifest_json IS NOT NULL),
                        CHECK ((status = 'pending' AND terminal_at_utc IS NULL AND applied_draft_id IS NULL) OR
                               (status <> 'pending' AND terminal_at_utc IS NOT NULL)),
                        CHECK ((status = 'applied') = (applied_draft_id IS NOT NULL)),
                        CHECK (status <> 'applied' OR test_evidence_hash IS NOT NULL),
                        FOREIGN KEY (session_id) REFERENCES feature_builder_sessions(id) ON DELETE RESTRICT,
                        FOREIGN KEY (base_capability_id, base_version, base_content_hash)
                            REFERENCES capability_versions(capability_id, version, content_hash) ON DELETE RESTRICT,
                        FOREIGN KEY (applied_draft_id) REFERENCES capability_drafts(id) ON DELETE RESTRICT
                    );
                    INSERT INTO improvement_proposals(
                        id, session_id, session_revision, generation_id, request_hash,
                        response_hash, inventory_hash, base_capability_id, base_version,
                        base_content_hash, expected_catalog_revision, candidate_content_hash,
                        proposal_hash, manifest_json, instructions_markdown, input_schema_json,
                        settings_schema_json, workflow_json, tests_json, known_limitations_json,
                        test_evidence_hash, status, revision, fixed_issue_code, applied_draft_id,
                        created_at_utc, updated_at_utc, terminal_at_utc)
                    SELECT
                        id, session_id, session_revision, generation_id, request_hash,
                        response_hash, inventory_hash, base_capability_id, base_version,
                        base_content_hash, expected_catalog_revision, candidate_content_hash,
                        proposal_hash, manifest_json, instructions_markdown, input_schema_json,
                        settings_schema_json, workflow_json, tests_json, known_limitations_json,
                        test_evidence_hash, status, revision, fixed_issue_code, applied_draft_id,
                        created_at_utc, updated_at_utc, terminal_at_utc
                    FROM improvement_proposals_schema20;

                    CREATE TABLE capability_test_runs (
                        id TEXT NOT NULL PRIMARY KEY CHECK (
                            length(id) = 39 AND substr(id, 1, 7) = 'fbtest_' AND
                            substr(id, 8) NOT GLOB '*[^0-9a-f]*'),
                        proposal_id TEXT NOT NULL,
                        kind TEXT NOT NULL CHECK (kind IN ('static_scan', 'dry_run')),
                        attempt INTEGER NOT NULL CHECK (attempt BETWEEN 1 AND 16),
                        candidate_content_hash TEXT NOT NULL CHECK (
                            length(candidate_content_hash) = 71 AND substr(candidate_content_hash, 1, 7) = 'sha256:' AND
                            substr(candidate_content_hash, 8) NOT GLOB '*[^0-9a-f]*'),
                        engine_version TEXT NOT NULL CHECK (
                            length(engine_version) BETWEEN 1 AND 64 AND
                            engine_version NOT GLOB '*[^A-Za-z0-9._-]*'),
                        status TEXT NOT NULL CHECK (status IN ('running', 'passed', 'failed', 'interrupted')),
                        revision INTEGER NOT NULL CHECK (revision >= 0),
                        result_json TEXT NOT NULL CHECK (
                            length(result_json) BETWEEN 2 AND 32768 AND
                            json_valid(result_json) AND json_type(result_json) IN ('object', 'array')),
                        fixed_error_code TEXT NULL CHECK (
                            fixed_error_code IS NULL OR length(fixed_error_code) BETWEEN 3 AND 128),
                        started_at_utc TEXT NOT NULL,
                        completed_at_utc TEXT NULL,
                        CHECK ((status = 'running' AND completed_at_utc IS NULL AND fixed_error_code IS NULL) OR
                               (status = 'passed' AND completed_at_utc IS NOT NULL AND fixed_error_code IS NULL) OR
                               (status IN ('failed', 'interrupted') AND completed_at_utc IS NOT NULL AND fixed_error_code IS NOT NULL)),
                        UNIQUE (proposal_id, kind, attempt),
                        FOREIGN KEY (proposal_id) REFERENCES improvement_proposals(id) ON DELETE RESTRICT
                    );
                    INSERT INTO capability_test_runs(
                        id, proposal_id, kind, attempt, candidate_content_hash, engine_version,
                        status, revision, result_json, fixed_error_code, started_at_utc, completed_at_utc)
                    SELECT
                        id, proposal_id, kind, attempt, candidate_content_hash, engine_version,
                        status, revision, result_json, fixed_error_code, started_at_utc, completed_at_utc
                    FROM capability_test_runs_schema20;

                    DROP TABLE capability_test_runs_schema20;
                    DROP TABLE improvement_proposals_schema20;

                    CREATE INDEX ix_improvement_proposals_session_status
                        ON improvement_proposals(session_id, status, updated_at_utc DESC);
                    CREATE TRIGGER improvement_proposals_reject_payload_update
                    BEFORE UPDATE ON improvement_proposals
                    WHEN OLD.id IS NOT NEW.id
                      OR OLD.session_id IS NOT NEW.session_id
                      OR OLD.session_revision IS NOT NEW.session_revision
                      OR OLD.generation_id IS NOT NEW.generation_id
                      OR OLD.request_hash IS NOT NEW.request_hash
                      OR OLD.response_hash IS NOT NEW.response_hash
                      OR OLD.inventory_hash IS NOT NEW.inventory_hash
                      OR OLD.base_capability_id IS NOT NEW.base_capability_id
                      OR OLD.base_version IS NOT NEW.base_version
                      OR OLD.base_content_hash IS NOT NEW.base_content_hash
                      OR OLD.expected_catalog_revision IS NOT NEW.expected_catalog_revision
                      OR OLD.candidate_content_hash IS NOT NEW.candidate_content_hash
                      OR OLD.proposal_hash IS NOT NEW.proposal_hash
                      OR OLD.manifest_json IS NOT NEW.manifest_json
                      OR OLD.instructions_markdown IS NOT NEW.instructions_markdown
                      OR OLD.input_schema_json IS NOT NEW.input_schema_json
                      OR OLD.settings_schema_json IS NOT NEW.settings_schema_json
                      OR OLD.workflow_json IS NOT NEW.workflow_json
                      OR OLD.tests_json IS NOT NEW.tests_json
                      OR OLD.known_limitations_json IS NOT NEW.known_limitations_json
                      OR OLD.created_at_utc IS NOT NEW.created_at_utc
                    BEGIN
                        SELECT RAISE(ABORT, 'capability proposal payload is immutable');
                    END;
                    CREATE TRIGGER improvement_proposals_reject_evidence_update
                    BEFORE UPDATE ON improvement_proposals
                    WHEN OLD.test_evidence_hash IS NOT NEW.test_evidence_hash
                     AND NOT (
                         OLD.test_evidence_hash IS NULL
                         AND NEW.test_evidence_hash IS NOT NULL
                         AND OLD.status = 'pending'
                         AND NEW.status = 'applied')
                    BEGIN
                        SELECT RAISE(ABORT, 'capability proposal evidence is immutable');
                    END;
                    CREATE TRIGGER improvement_proposals_reject_terminal_update
                    BEFORE UPDATE ON improvement_proposals
                    WHEN OLD.status <> 'pending' BEGIN
                        SELECT RAISE(ABORT, 'terminal capability proposals are immutable');
                    END;
                    CREATE TRIGGER improvement_proposals_reject_delete
                    BEFORE DELETE ON improvement_proposals BEGIN
                        SELECT RAISE(ABORT, 'capability proposals cannot be deleted');
                    END;
                    CREATE INDEX ix_capability_test_runs_proposal
                        ON capability_test_runs(proposal_id, kind, attempt DESC);
                    CREATE TRIGGER capability_test_runs_reject_identity_update
                    BEFORE UPDATE ON capability_test_runs
                    WHEN OLD.id IS NOT NEW.id
                      OR OLD.proposal_id IS NOT NEW.proposal_id
                      OR OLD.kind IS NOT NEW.kind
                      OR OLD.attempt IS NOT NEW.attempt
                      OR OLD.candidate_content_hash IS NOT NEW.candidate_content_hash
                      OR OLD.engine_version IS NOT NEW.engine_version
                      OR OLD.started_at_utc IS NOT NEW.started_at_utc
                    BEGIN
                        SELECT RAISE(ABORT, 'capability test run identity is immutable');
                    END;
                    CREATE TRIGGER capability_test_runs_reject_terminal_update
                    BEFORE UPDATE ON capability_test_runs
                    WHEN OLD.status <> 'running' BEGIN
                        SELECT RAISE(ABORT, 'terminal capability test runs are immutable');
                    END;
                    CREATE TRIGGER capability_test_runs_reject_delete
                    BEFORE DELETE ON capability_test_runs BEGIN
                        SELECT RAISE(ABORT, 'capability test runs cannot be deleted');
                    END;

                    INSERT INTO schema_migrations(version, applied_at_utc) VALUES (21, $appliedAtUtc);
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
