using LIGClaw.Application.Memory;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal interface IPersonalMemoryRepository : IMemoryRepository
{
    Task<IReadOnlyList<SemanticMemoryCandidate>> GetSemanticMemoryCandidatesAsync(
        string modelKey,
        DateTimeOffset nowUtc,
        int limit = 500,
        CancellationToken cancellationToken = default);

    Task UpsertMemoryEmbeddingAsync(
        string memoryId,
        string modelKey,
        string contentHash,
        IReadOnlyList<float> vector,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken);
}
