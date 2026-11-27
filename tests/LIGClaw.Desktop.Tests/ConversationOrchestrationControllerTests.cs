using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Sidecar;
using LIGClaw.Domain;
using ApplicationCapabilityRuntimeSnapshot = LIGClaw.Application.Capabilities.CapabilityRuntimeSnapshot;
using ContractCapabilityRuntimeSnapshot = LIGClaw.Contracts.Generated.CapabilityRuntimeSnapshot;

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
        Assert.NotNull(sidecar.CapabilitySnapshot);
        Assert.Empty(sidecar.CapabilitySnapshot.Candidates);
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
    public async Task FreezesAndProjectsTheCapabilitySnapshotBeforeStartingTheSidecar()
    {
        var calls = new List<string>();
        var store = new FakeStore(calls);
        var sidecar = new FakeSidecar(calls);
        var ids = new Queue<string>(["conversation", "run"]);
        var snapshot = Snapshot();
        var controller = Create(store, sidecar, ids.Dequeue);
        controller.ConfigureCapabilitySnapshotFactory((input, cancellationToken) =>
        {
            Assert.Equal("기능 요청", input);
            Assert.False(cancellationToken.IsCancellationRequested);
            calls.Add("snapshot.create");
            return Task.FromResult(snapshot);
        });

        var outcome = await controller.StartAsync(
            controller.BeginRun(),
            "기능 요청",
            persistenceAvailable: true);

        Assert.Equal(["store.start", "store.context", "snapshot.create", "sidecar.start"], calls);
        Assert.Null(outcome.Diagnostic);
        var projected = Assert.Single(sidecar.CapabilitySnapshot!.Candidates);
        Assert.Equal(snapshot.SnapshotHash.Value, sidecar.CapabilitySnapshot.SnapshotHash);
        Assert.Equal(snapshot.Candidates[0].ExactVersion.Id.Value, projected.Id);
        Assert.Equal("prompt", projected.Kind);
        Assert.Equal(snapshot.Candidates[0].Instructions, projected.Instructions);
    }

    [Fact]
    public async Task SnapshotFailureStartsGeneralChatWithAnEmptySnapshotAndFixedDiagnostic()
    {
        var sidecar = new FakeSidecar([]);
        var ids = new Queue<string>(["conversation", "run"]);
        var controller = Create(new FakeStore([]), sidecar, ids.Dequeue);
        controller.ConfigureCapabilitySnapshotFactory((_, _) =>
            Task.FromException<ApplicationCapabilityRuntimeSnapshot>(
                new InvalidOperationException("sensitive candidate identity")));

        var outcome = await controller.StartAsync(
            controller.BeginRun(),
            "사용자 입력 원문",
            persistenceAvailable: false);

        Assert.Equal(
            ConversationOrchestrationController.CapabilitySnapshotFallbackDiagnostic,
            outcome.Diagnostic);
        Assert.DoesNotContain("sensitive", outcome.Diagnostic, StringComparison.Ordinal);
        Assert.NotNull(sidecar.CapabilitySnapshot);
        Assert.Empty(sidecar.CapabilitySnapshot.Candidates);
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

    private static ApplicationCapabilityRuntimeSnapshot Snapshot()
    {
        var exactVersion = new CapabilityVersionRef(
            CapabilityId.Parse("desktop.status"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            CapabilityContentHash.Parse($"sha256:{new string('a', 64)}"));
        var candidate = new LIGClaw.Application.Capabilities.CapabilityRuntimeCandidate(
            exactVersion,
            7,
            CapabilityRuntimeHasher.ComputeModelToolName(exactVersion),
            "상태 확인",
            "현재 상태를 확인합니다.",
            CapabilityKind.Prompt,
            ["상태 알려 줘"],
            "필요한 상태 도구만 사용합니다.",
            "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
            ["system.get_status.v1"],
            $"sha256:{new string('b', 64)}",
            "R0");
        return ApplicationCapabilityRuntimeSnapshot.Create([candidate]);
    }

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
        public ContractCapabilityRuntimeSnapshot? CapabilitySnapshot { get; private set; }

        public Task<ConversationStartResult> StartConversationAsync(string conversationId, string runId, string input, string runtime, IReadOnlyList<IReadOnlyDictionary<string, object?>>? history = null, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, object?>? providerRouting = null, ContractCapabilityRuntimeSnapshot? capabilitySnapshot = null)
        {
            calls.Add("sidecar.start");
            History = history;
            CapabilitySnapshot = capabilitySnapshot;
            return Task.FromResult(new ConversationStartResult(true, runId));
        }

        public Task<ConversationCancelResult> CancelConversationAsync(string conversationId, CancellationToken cancellationToken = default) =>
            FailCancel
                ? Task.FromException<ConversationCancelResult>(new InvalidOperationException())
                : Task.FromResult(new ConversationCancelResult(true));
    }
}
