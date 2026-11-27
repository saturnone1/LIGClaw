using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Sidecar;

namespace LIGClaw.Desktop.Tests;

public sealed class ConversationOrchestrationControllerTests
{
    [Fact]
    public async Task StartsPersistenceBeforeSidecarAndPassesBoundedHistory()
    {
        var calls = new List<string>();
        var store = new FakeStore(calls);
        var sidecar = new FakeSidecar(calls);
        var ids = new Queue<string>(["conversation", "run"]);
        var controller = Create(store, sidecar, ids.Dequeue);
        var identity = controller.BeginRun();

        var outcome = await controller.StartAsync(identity, "요청", persistenceAvailable: true);

        Assert.Equal(["store.start", "store.context", "sidecar.start"], calls);
        Assert.True(outcome.RefreshConversations);
        Assert.Null(outcome.Diagnostic);
        Assert.Equal("user", sidecar.History![0]["role"]);
        Assert.Equal("이전 요청", sidecar.History[0]["content"]);
    }

    [Fact]
    public async Task PersistenceFailureDoesNotPreventTheSidecarRun()
    {
        var calls = new List<string>();
        var store = new FakeStore(calls) { FailStart = true };
        var sidecar = new FakeSidecar(calls);
        var ids = new Queue<string>(["conversation", "run"]);
        var controller = Create(store, sidecar, ids.Dequeue);

        var outcome = await controller.StartAsync(controller.BeginRun(), "요청", persistenceAvailable: true);

        Assert.Equal(["store.start", "sidecar.start"], calls);
        Assert.False(outcome.RefreshConversations);
        Assert.Contains(nameof(InvalidOperationException), outcome.Diagnostic, StringComparison.Ordinal);
        Assert.Null(sidecar.History);
    }

    [Fact]
    public async Task CancellationUpdatesRunStateEvenWhenSidecarCancellationFails()
    {
        var ids = new Queue<string>(["conversation", "run"]);
        var sidecar = new FakeSidecar([]) { FailCancel = true };
        var controller = Create(new FakeStore([]), sidecar, ids.Dequeue);
        var identity = controller.BeginRun();

        Assert.True(controller.TryRequestCancellation(out var active));
        var outcome = await controller.CancelAsync(active!);

        Assert.True(outcome.Requested);
        Assert.True(identity.CancellationToken.IsCancellationRequested);
        Assert.Contains(nameof(InvalidOperationException), outcome.Diagnostic, StringComparison.Ordinal);
    }

    private static ConversationOrchestrationController Create(
        IConversationRunStore store,
        IConversationSidecar sidecar,
        Func<string> createId) =>
        new(
            new ConversationRunController(new ToolInvocationPolicy(), createId),
            sidecar,
            store,
            () => null,
            () => DateTimeOffset.UnixEpoch);

    private sealed class FakeStore(List<string> calls) : IConversationRunStore
    {
        public bool FailStart { get; init; }

        public Task StartRunAsync(string conversationId, string runId, string userInput, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default)
        {
            calls.Add("store.start");
            return FailStart ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConversationContextMessage>> GetConversationContextAsync(string conversationId, int maximumMessages = 40, int maximumCharacters = 64_000, CancellationToken cancellationToken = default)
        {
            calls.Add("store.context");
            return Task.FromResult<IReadOnlyList<ConversationContextMessage>>([new("user", "이전 요청")]);
        }

        public Task AppendEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkRunAsync(string conversationId, string runId, string status, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSidecar(List<string> calls) : IConversationSidecar
    {
        public bool FailCancel { get; init; }
        public IReadOnlyList<IReadOnlyDictionary<string, object?>>? History { get; private set; }

        public Task<ConversationStartResult> StartConversationAsync(string conversationId, string runId, string input, string runtime, IReadOnlyList<IReadOnlyDictionary<string, object?>>? history = null, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, object?>? providerRouting = null)
        {
            calls.Add("sidecar.start");
            History = history;
            return Task.FromResult(new ConversationStartResult(true, runId));
        }

        public Task<ConversationCancelResult> CancelConversationAsync(string conversationId, CancellationToken cancellationToken = default) =>
            FailCancel
                ? Task.FromException<ConversationCancelResult>(new InvalidOperationException())
                : Task.FromResult(new ConversationCancelResult(true));
    }
}
