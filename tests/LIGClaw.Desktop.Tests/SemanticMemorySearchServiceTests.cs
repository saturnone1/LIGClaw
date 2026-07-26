using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class SemanticMemorySearchServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ligclaw-semantic-{Guid.NewGuid():N}");

    [Fact]
    public async Task IndexesMemoriesAndFindsMeaningBeyondLiteralKeywords()
    {
        using var store = new ConversationStore(Path.Combine(_directory, "memory.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        var editor = await store.UpsertAsync(
            new PersonalMemoryDraft("preference", "선호 에디터", "VS Code", "general", "test", null),
            now,
            CancellationToken.None);
        await store.UpsertAsync(
            new PersonalMemoryDraft("preference", "선호 음료", "커피", "general", "test", null),
            now,
            CancellationToken.None);
        var client = new DeterministicEmbeddingClient();
        var service = new SemanticMemorySearchService(
            store,
            client,
            () => new SemanticMemorySettings(true, "http://127.0.0.1:11434", "test-model"));

        var results = await service.SearchAsync("코딩할 때 쓰는 도구", 2, now, CancellationToken.None);

        Assert.Equal(editor.Memory.Id, results[0].Memory.Id);
        Assert.True(results[0].Score > results[1].Score);
        Assert.Equal(3, client.InputCount);
    }

    [Fact]
    public async Task ReindexesChangedMemoryAndRemovesVectorWithMemory()
    {
        using var store = new ConversationStore(Path.Combine(_directory, "memory.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        var created = await store.UpsertAsync(
            new PersonalMemoryDraft("note", "도구", "VS Code", "general", "test", null),
            now,
            CancellationToken.None);
        var client = new DeterministicEmbeddingClient();
        var settings = new SemanticMemorySettings(true, "http://127.0.0.1:11434", "test-model");
        var service = new SemanticMemorySearchService(store, client, () => settings);
        await service.SearchAsync("코딩 도구", 1, now, CancellationToken.None);
        var firstInputCount = client.InputCount;

        await store.UpsertAsync(
            new PersonalMemoryDraft("note", "도구", "Visual Studio", "general", "test", null),
            now.AddMinutes(1),
            CancellationToken.None);
        await service.SearchAsync("코딩 도구", 1, now.AddMinutes(1), CancellationToken.None);
        Assert.Equal(firstInputCount + 2, client.InputCount);

        Assert.True(await store.DeleteAsync(created.Memory.Id, CancellationToken.None));
        var candidates = await store.GetSemanticMemoryCandidatesAsync(
            SemanticMemorySearchService.Hash($"{settings.BaseUrl}\n{settings.Model}"),
            now,
            cancellationToken: CancellationToken.None);
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task RepositoryFallsBackToKeywordSearchWhenEmbeddingEndpointFails()
    {
        using var store = new ConversationStore(Path.Combine(_directory, "memory.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        await store.UpsertAsync(
            new PersonalMemoryDraft("note", "업무 도구", "VS Code", "general", "test", null),
            now,
            CancellationToken.None);
        var settings = new SemanticMemorySettings(true, "https://embedding.example", "test-model");
        var service = new SemanticMemorySearchService(store, new FailingEmbeddingClient(), () => settings);
        var repository = new SemanticMemoryRepository(store, service, () => settings);

        var results = await repository.ListAsync("VS Code", 10, now, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("업무 도구", results[0].Key);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class DeterministicEmbeddingClient : ISemanticMemoryEmbeddingClient
    {
        public int InputCount { get; private set; }

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            SemanticMemorySettings settings,
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken)
        {
            InputCount += inputs.Count;
            return Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(input =>
                input.Contains("VS Code", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("코딩", StringComparison.OrdinalIgnoreCase)
                    ? new[] { 1f, 0f }
                    : new[] { 0f, 1f }).ToArray());
        }
    }

    private sealed class FailingEmbeddingClient : ISemanticMemoryEmbeddingClient
    {
        public Task<IReadOnlyList<float[]>> EmbedAsync(
            SemanticMemorySettings settings,
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }
}
