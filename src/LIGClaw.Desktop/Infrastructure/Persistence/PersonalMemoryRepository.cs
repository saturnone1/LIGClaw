using System.Globalization;
using LIGClaw.Application.Memory;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed record SemanticMemoryCandidate(
    PersonalMemory Memory,
    string? ContentHash,
    float[]? Vector);
internal sealed class PersonalMemoryRepository(ConversationDatabase database) : IPersonalMemoryRepository
{
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

    private Task WithConnectionAsync(Func<SqliteConnection, Task> operation, CancellationToken cancellationToken) =>
        database.WithConnectionAsync(operation, cancellationToken);

    private static Task<int> ExecuteAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        System.Data.Common.DbTransaction? transaction = null,
        params (string Name, object? Value)[] parameters) =>
        ConversationDatabase.ExecuteAsync(connection, commandText, cancellationToken, transaction, parameters);

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
