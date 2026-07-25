using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;

namespace LIGClaw.Desktop.Tests;

public sealed class ConversationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LIGClaw.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PersistsAndReplaysConversationEventsIdempotently()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        var startedAt = DateTimeOffset.Parse("2026-07-25T01:00:00Z");
        await store.StartConversationAsync("conversation-1", "오늘 할 일을 정리해 줘", startedAt);
        var started = Event(0, "run_started", startedAt);
        var first = Event(1, "text_delta", startedAt.AddSeconds(1), "첫 번째 ");
        var second = Event(2, "text_delta", startedAt.AddSeconds(2), "답변");
        var completed = Event(3, "run_completed", startedAt.AddSeconds(3));

        await store.AppendEventAsync(started);
        await store.AppendEventAsync(first);
        await store.AppendEventAsync(first);
        await store.AppendEventAsync(second);
        await store.AppendEventAsync(completed);

        var recent = Assert.Single(await store.GetRecentConversationsAsync());
        Assert.Equal("completed", recent.Status);
        Assert.Equal("오늘 할 일을 정리해 줘", recent.Title);
        Assert.Equal("첫 번째 답변", await store.GetTranscriptAsync("conversation-1"));
    }

    [Fact]
    public async Task MarksRunsInterruptedWhenTheDesktopRestarts()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        await store.StartConversationAsync("running", "중단된 요청", DateTimeOffset.UtcNow);

        await store.MarkRunningConversationsInterruptedAsync();

        Assert.Equal("interrupted", Assert.Single(await store.GetRecentConversationsAsync()).Status);
    }

    [Fact]
    public async Task ReopensAnExistingSchemaWithoutLosingHistory()
    {
        var path = Path.Combine(_directory, "history.db");
        using (var first = new ConversationStore(path))
        {
            await first.InitializeAsync();
            await first.StartConversationAsync("saved", "저장된 요청", DateTimeOffset.UtcNow);
        }

        using var reopened = new ConversationStore(path);
        await reopened.InitializeAsync();

        Assert.Equal("saved", Assert.Single(await reopened.GetRecentConversationsAsync()).Id);
    }

    private ConversationStore CreateStore() => new(Path.Combine(_directory, "history.db"));

    private static AgentEvent Event(long sequence, string type, DateTimeOffset timestamp, string? text = null) =>
        new("conversation-1", "run-1", sequence, type, timestamp, text, null);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
