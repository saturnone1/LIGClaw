using System.Globalization;
using LIGClaw.Contracts.Generated;
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
internal sealed class ConversationRepository(ConversationDatabase database) : IConversationRepository
{
    private const int MaximumConversationSearchLength = 200;

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

    private static string QuoteFtsPhrase(string query) => $"\"{query.Replace("\"", "\"\"")}\"";

    private static string EscapeLikePattern(string query) => query
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? MapStatus(string eventType) => eventType switch
    {
        "run_completed" => "completed",
        "run_cancelled" => "cancelled",
        "run_failed" => "failed",
        _ => null,
    };

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
