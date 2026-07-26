using System.Security.Cryptography;
using System.Text;
using LIGClaw.Application.Memory;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record SemanticMemoryMatch(PersonalMemory Memory, double Score);

internal sealed class SemanticMemorySearchService(
    ConversationStore store,
    ISemanticMemoryEmbeddingClient embeddingClient,
    Func<SemanticMemorySettings> loadSettings)
{
    private const int MaximumIndexedMemories = 500;
    private const int BatchSize = 32;
    private readonly SemaphoreSlim _indexGate = new(1, 1);

    public async Task<IReadOnlyList<SemanticMemoryMatch>> SearchAsync(
        string query,
        int limit,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > 200) throw new ArgumentOutOfRangeException(nameof(query));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var settings = loadSettings();
        if (!SemanticMemorySettingsPolicy.TryValidate(settings, out settings, out var error) || !settings.Enabled)
            throw new InvalidOperationException(error.Length == 0 ? "의미 기억이 비활성화되어 있습니다." : error);

        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var modelKey = Hash($"{settings.BaseUrl}\n{settings.Model}");
            var candidates = await store.GetSemanticMemoryCandidatesAsync(
                modelKey, nowUtc, MaximumIndexedMemories, cancellationToken).ConfigureAwait(false);
            var pending = candidates
                .Select(candidate => (Candidate: candidate, Content: CreateContent(candidate.Memory)))
                .Where(item => item.Candidate.Vector is null ||
                               !StringComparer.Ordinal.Equals(item.Candidate.ContentHash, Hash(item.Content)))
                .ToArray();
            for (var offset = 0; offset < pending.Length; offset += BatchSize)
            {
                var batch = pending.Skip(offset).Take(BatchSize).ToArray();
                var vectors = await embeddingClient.EmbedAsync(
                    settings, batch.Select(item => item.Content).ToArray(), cancellationToken).ConfigureAwait(false);
                for (var index = 0; index < batch.Length; index++)
                {
                    await store.UpsertMemoryEmbeddingAsync(
                        batch[index].Candidate.Memory.Id,
                        modelKey,
                        Hash(batch[index].Content),
                        vectors[index],
                        nowUtc,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            if (pending.Length > 0)
                candidates = await store.GetSemanticMemoryCandidatesAsync(
                    modelKey, nowUtc, MaximumIndexedMemories, cancellationToken).ConfigureAwait(false);
            var queryVector = (await embeddingClient.EmbedAsync(settings, [query], cancellationToken)
                .ConfigureAwait(false))[0];
            return candidates
                .Where(candidate => candidate.Vector?.Length == queryVector.Length)
                .Select(candidate => new SemanticMemoryMatch(
                    candidate.Memory,
                    CosineSimilarity(queryVector, candidate.Vector!)))
                .Where(match => double.IsFinite(match.Score))
                .OrderByDescending(match => match.Score)
                .ThenByDescending(match => match.Memory.UpdatedAtUtc)
                .Take(limit)
                .ToArray();
        }
        finally
        {
            _indexGate.Release();
        }
    }

    internal static string CreateContent(PersonalMemory memory) =>
        $"종류: {memory.Kind}\n키: {memory.Key}\n내용: {memory.Value}";

    internal static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count) return double.NaN;
        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var index = 0; index < left.Count; index++)
        {
            var leftValue = (double)left[index];
            var rightValue = (double)right[index];
            dot += leftValue * rightValue;
            leftMagnitude += leftValue * leftValue;
            rightMagnitude += rightValue * rightValue;
        }
        if (leftMagnitude <= 0 || rightMagnitude <= 0) return double.NaN;
        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }
}

internal sealed class SemanticMemoryRepository(
    IMemoryRepository inner,
    SemanticMemorySearchService semanticSearch,
    Func<SemanticMemorySettings> loadSettings) : IMemoryRepository
{
    public Task<MemoryUpsertResult> UpsertAsync(
        PersonalMemoryDraft draft, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        inner.UpsertAsync(draft, nowUtc, cancellationToken);

    public async Task<IReadOnlyList<PersonalMemory>> ListAsync(
        string? query, int limit, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var keyword = await inner.ListAsync(query, limit, nowUtc, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(query) || !loadSettings().Enabled) return keyword;
        try
        {
            var semantic = await semanticSearch.SearchAsync(query, limit, nowUtc, cancellationToken).ConfigureAwait(false);
            var scores = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var index = 0; index < keyword.Count; index++)
                scores[keyword[index].Id] = 1d / (60 + index + 1);
            for (var index = 0; index < semantic.Count; index++)
                scores[semantic[index].Memory.Id] = scores.GetValueOrDefault(semantic[index].Memory.Id) +
                                                    1d / (60 + index + 1);
            return keyword.Concat(semantic.Select(match => match.Memory))
                .DistinctBy(memory => memory.Id)
                .OrderByDescending(memory => scores.GetValueOrDefault(memory.Id))
                .ThenByDescending(memory => memory.UpdatedAtUtc)
                .Take(limit)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return keyword;
        }
    }

    public Task<PersonalMemory?> GetAsync(string memoryId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        inner.GetAsync(memoryId, nowUtc, cancellationToken);

    public Task<IReadOnlyList<PersonalMemory>> ListForManagementAsync(
        string? query, int offset, int limit, CancellationToken cancellationToken) =>
        inner.ListForManagementAsync(query, offset, limit, cancellationToken);

    public Task<PersonalMemory?> GetForManagementAsync(string memoryId, CancellationToken cancellationToken) =>
        inner.GetForManagementAsync(memoryId, cancellationToken);

    public Task<string?> ResolveValueAsync(
        string kind, string key, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        inner.ResolveValueAsync(kind, key, nowUtc, cancellationToken);

    public Task<bool> DeleteAsync(string memoryId, CancellationToken cancellationToken) =>
        inner.DeleteAsync(memoryId, cancellationToken);
}
