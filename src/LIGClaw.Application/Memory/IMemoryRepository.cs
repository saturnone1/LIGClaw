using LIGClaw.Domain;

namespace LIGClaw.Application.Memory;

public sealed record MemoryUpsertResult(PersonalMemory Memory, bool Created);

public interface IMemoryRepository
{
    Task<MemoryUpsertResult> UpsertAsync(PersonalMemoryDraft draft, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<PersonalMemory>> ListAsync(string? query, int limit, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<PersonalMemory?> GetAsync(string memoryId, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<PersonalMemory>> ListForManagementAsync(
        string? query,
        int offset,
        int limit,
        CancellationToken cancellationToken);
    Task<PersonalMemory?> GetForManagementAsync(string memoryId, CancellationToken cancellationToken);
    Task<string?> ResolveValueAsync(
        string kind,
        string key,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string memoryId, CancellationToken cancellationToken);
}
