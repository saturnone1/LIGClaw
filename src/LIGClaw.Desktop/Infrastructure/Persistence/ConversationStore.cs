using System.Globalization;
using System.IO;
using LIGClaw.Contracts.Generated;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed record ConversationSummary(
    string Id,
    string UserInput,
    string Status,
    DateTimeOffset UpdatedAtUtc)
{
    public string Title => UserInput.Length <= 34 ? UserInput : string.Concat(UserInput.AsSpan(0, 34), "…");
    public string UpdatedAtDisplay => UpdatedAtUtc.ToLocalTime().ToString("MM.dd HH:mm", CultureInfo.CurrentCulture);
}

internal sealed class ConversationStore(string databasePath) : IDisposable
{
    private const int CurrentSchemaVersion = 1;
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

    public async Task StartConversationAsync(
        string conversationId,
        string userInput,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);
        await WithConnectionAsync(async connection =>
        {
            await ExecuteAsync(
                connection,
                """
                INSERT INTO conversations(id, user_input, status, created_at_utc, updated_at_utc)
                VALUES ($id, $userInput, 'running', $startedAtUtc, $startedAtUtc);
                """,
                cancellationToken,
                parameters:
                [
                    ("$id", conversationId),
                    ("$userInput", userInput),
                    ("$startedAtUtc", startedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                ]).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
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

            var status = MapStatus(agentEvent.Type);
            if (status is not null)
            {
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE conversations
                    SET status = $status, updated_at_utc = $updatedAtUtc
                    WHERE id = $conversationId;
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

    public Task MarkConversationAsync(
        string conversationId,
        string status,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        WithConnectionAsync(
            connection => ExecuteAsync(
                connection,
                """
                UPDATE conversations
                SET status = $status, updated_at_utc = $updatedAtUtc
                WHERE id = $conversationId;
                """,
                cancellationToken,
                parameters:
                [
                    ("$status", status),
                    ("$updatedAtUtc", updatedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                    ("$conversationId", conversationId),
                ]),
            cancellationToken);

    public Task MarkRunningConversationsInterruptedAsync(CancellationToken cancellationToken = default) =>
        WithConnectionAsync(
            connection => ExecuteAsync(
                connection,
                """
                UPDATE conversations
                SET status = 'interrupted', updated_at_utc = $updatedAtUtc
                WHERE status = 'running';
                """,
                cancellationToken,
                parameters: [("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))]),
            cancellationToken);

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
                SELECT id, user_input, status, updated_at_utc
                FROM conversations
                ORDER BY updated_at_utc DESC
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
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }
        }, cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<string> GetTranscriptAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        var transcript = new System.Text.StringBuilder();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT text
                FROM agent_events
                WHERE conversation_id = $conversationId AND type = 'text_delta' AND text IS NOT NULL
                ORDER BY id;
                """;
            command.Parameters.AddWithValue("$conversationId", conversationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                transcript.Append(reader.GetString(0));
        }, cancellationToken).ConfigureAwait(false);
        return transcript.ToString();
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

    private static async Task ExecuteAsync(
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
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
