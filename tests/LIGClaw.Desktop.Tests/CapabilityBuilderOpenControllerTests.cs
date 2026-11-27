using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class CapabilityBuilderOpenControllerTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-02T08:00:00Z");
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public CapabilityBuilderOpenControllerTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task BindsOnlyTheExactPersistedActiveRunPromptAndDefersTheInMemoryRequest()
    {
        const string exactPrompt = "  이 요청 원문 그대로 새 기능을 만들어 줘.\r\n두 번째 줄도 유지해.  ";
        const string decoyPrompt = "DECOY_LATEST_CONVERSATION_PROMPT_MUST_NOT_BIND";
        using var store = new ConversationStore(Path.Combine(directory, "builder-open.db"));
        await store.InitializeAsync();
        var (run, active) = BeginRun("builder-conversation", "builder-run");
        await store.StartRunAsync(active.ConversationId, active.RunId, exactPrompt, Now);
        await store.StartRunAsync(active.ConversationId, "decoy-run", decoyPrompt, Now.AddSeconds(1));
        var controller = new CapabilityBuilderOpenController(run, store.Conversations);

        var outcome = await controller.ExecuteAsync(Invocation(active, "builder-call", "create"));

        Assert.True(outcome.Execution.Success, outcome.Execution.Error);
        Assert.Equal(
            ["accepted", "intent"],
            outcome.Execution.Output.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(true, outcome.Execution.Output["accepted"]);
        Assert.Equal("create", outcome.Execution.Output["intent"]);
        var request = Assert.IsType<CapabilityBuilderOpenRequest>(outcome.RequestAfterAcknowledgement);
        Assert.Equal(active.ConversationId, request.ConversationId);
        Assert.Equal(active.RunId, request.RunId);
        Assert.Equal("builder-call", request.ToolCallId);
        Assert.Equal(CapabilityBuilderOpenIntent.Create, request.Intent);
        Assert.Equal(exactPrompt, request.Requirement);
        Assert.DoesNotContain(decoyPrompt, request.Requirement, StringComparison.Ordinal);
        Assert.DoesNotContain(exactPrompt, outcome.Execution.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(exactPrompt, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            exactPrompt,
            (await store.Conversations.GetPersistedRunInputAsync(
                active.ConversationId,
                active.RunId))!.ToString(),
            StringComparison.Ordinal);
        Assert.Empty(await store.GetRecentToolActivityAsync(cancellationToken: CancellationToken.None));
        Assert.True(run.CompleteActiveRun());
    }

    [Fact]
    public async Task RejectsBothSameAndDifferentCallIdDuplicatesWithinOneRun()
    {
        var (run, active) = BeginRun("duplicate-conversation", "duplicate-run");
        var source = new RecordingInputSource(Input(active, "새 기능을 만들어 줘."));
        var controller = new CapabilityBuilderOpenController(run, source);

        var first = await controller.ExecuteAsync(Invocation(active, "same-call", "create"));
        var sameCall = await controller.ExecuteAsync(Invocation(active, "same-call", "create"));
        var differentCall = await controller.ExecuteAsync(Invocation(active, "different-call", "create"));

        Assert.True(first.Execution.Success);
        Assert.False(sameCall.Execution.Success);
        Assert.Equal("capability.builder.duplicate_or_invalid", sameCall.Execution.ErrorCode);
        Assert.Null(sameCall.RequestAfterAcknowledgement);
        Assert.False(differentCall.Execution.Success);
        Assert.Equal("capability.builder.already_opened", differentCall.Execution.ErrorCode);
        Assert.Null(differentCall.RequestAfterAcknowledgement);
        Assert.Equal(1, source.CallCount);
        Assert.True(run.CompleteActiveRun());
    }

    [Fact]
    public async Task RechecksExactActiveIdentityAndCancellationAfterAcknowledgement()
    {
        var (run, active) = BeginRun("ack-conversation", "ack-run");
        var source = new RecordingInputSource(Input(active, "새 범용 기능을 만들어 줘."));
        var controller = new CapabilityBuilderOpenController(run, source);

        var outcome = await controller.ExecuteAsync(Invocation(active, "ack-call", "create"));
        var request = Assert.IsType<CapabilityBuilderOpenRequest>(outcome.RequestAfterAcknowledgement);

        Assert.True(controller.CanOpenAfterAcknowledgement(request));
        Assert.False(controller.CanOpenAfterAcknowledgement(request with { ToolCallId = "other-call" }));
        Assert.False(controller.CanOpenAfterAcknowledgement(request with { RunId = "other-run" }));
        Assert.True(run.TryRequestCancellation(out _));
        Assert.False(controller.CanOpenAfterAcknowledgement(request));
        Assert.True(run.CompleteActiveRun());
    }

    [Fact]
    public async Task AllowsAcknowledgedHandoffAfterNormalRunCompletionButNotAfterContextChanges()
    {
        var (run, active) = BeginRun("completed-conversation", "completed-run");
        var controller = new CapabilityBuilderOpenController(
            run,
            new RecordingInputSource(Input(active, "완료 뒤에도 이 기능 빌더를 열어 줘.")));

        var outcome = await controller.ExecuteAsync(Invocation(active, "completed-call", "create"));
        var request = Assert.IsType<CapabilityBuilderOpenRequest>(outcome.RequestAfterAcknowledgement);

        Assert.True(run.CompleteActiveRun());
        Assert.True(controller.CanOpenAfterAcknowledgement(request));
        Assert.True(run.StartNewConversation());
        Assert.False(controller.CanOpenAfterAcknowledgement(request));
    }

    [Fact]
    public async Task RejectsStaleAndCancelledRunsBeforeReadingThePersistedPrompt()
    {
        var (run, active) = BeginRun("active-conversation", "active-run");
        var source = new RecordingInputSource(Input(active, "읽으면 안 되는 원문"));
        var controller = new CapabilityBuilderOpenController(run, source);

        var stale = await controller.ExecuteAsync(new ToolInvokeParams(
            "stale-call",
            active.ConversationId,
            "stale-run",
            CapabilityBuilderOpenController.CanonicalToolName,
            "R0",
            new Dictionary<string, object?> { ["intent"] = "create" }));
        Assert.True(run.TryRequestCancellation(out _));
        var cancelled = await controller.ExecuteAsync(Invocation(active, "cancelled-call", "create"));

        Assert.False(stale.Execution.Success);
        Assert.Equal("capability.builder.run_not_active", stale.Execution.ErrorCode);
        Assert.Null(stale.RequestAfterAcknowledgement);
        Assert.False(cancelled.Execution.Success);
        Assert.Equal("capability.builder.run_cancelled", cancelled.Execution.ErrorCode);
        Assert.Null(cancelled.RequestAfterAcknowledgement);
        Assert.Equal(0, source.CallCount);
        Assert.True(run.CompleteActiveRun());
    }

    [Fact]
    public async Task ModelRequirementInjectionIsConsumedAndCannotBeRetriedWithTheSameIdentity()
    {
        const string injected = "MODEL_REQUIREMENT_INJECTION_CANARY";
        var (run, active) = BeginRun("injection-conversation", "injection-run");
        var source = new RecordingInputSource(Input(active, "저장된 원문만 사용해."));
        var controller = new CapabilityBuilderOpenController(run, source);
        var malformed = Invocation(active, "injected-call", "create") with
        {
            Input = new Dictionary<string, object?>
            {
                ["intent"] = "create",
                ["requirement"] = injected,
            },
        };

        var rejected = await controller.ExecuteAsync(malformed);
        var retry = await controller.ExecuteAsync(Invocation(active, "injected-call", "create"));

        Assert.False(rejected.Execution.Success);
        Assert.Equal("capability.builder.payload_invalid", rejected.Execution.ErrorCode);
        Assert.Null(rejected.RequestAfterAcknowledgement);
        Assert.DoesNotContain(injected, rejected.Execution.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain(injected, rejected.Execution.ToString(), StringComparison.Ordinal);
        Assert.False(retry.Execution.Success);
        Assert.Equal("capability.builder.duplicate_or_invalid", retry.Execution.ErrorCode);
        Assert.Equal(0, source.CallCount);
        Assert.True(run.CompleteActiveRun());
    }

    [Fact]
    public async Task RejectsCapabilityContextNonR0AndReviseWithoutAnExactUserSelectedTarget()
    {
        var (run, active) = BeginRun("denied-conversation", "denied-run");
        var source = new RecordingInputSource(Input(active, "현재 기능을 수정해 줘."));
        var controller = new CapabilityBuilderOpenController(run, source);
        var context = new CapabilityInvocationContext(
            Hash('a'),
            $"cap_{new string('b', 24)}",
            "company.existing-capability",
            "1.0.0",
            Hash('c'),
            1,
            Hash('d'));

        var contextRejected = await controller.ExecuteAsync(
            Invocation(active, "context-call", "create") with { CapabilityContext = context });
        var riskRejected = await controller.ExecuteAsync(
            Invocation(active, "risk-call", "create") with { Risk = "R1" });
        var reviseRejected = await controller.ExecuteAsync(Invocation(active, "revise-call", "revise"));

        Assert.Equal("capability.builder.payload_invalid", contextRejected.Execution.ErrorCode);
        Assert.Equal("capability.builder.risk_denied", riskRejected.Execution.ErrorCode);
        Assert.Equal("capability.builder.revise_target_required", reviseRejected.Execution.ErrorCode);
        Assert.Null(contextRejected.RequestAfterAcknowledgement);
        Assert.Null(riskRejected.RequestAfterAcknowledgement);
        Assert.Null(reviseRejected.RequestAfterAcknowledgement);
        Assert.Equal(0, source.CallCount);
        Assert.True(run.CompleteActiveRun());
    }

    [Theory]
    [InlineData("completed", "정상 범위지만 더는 실행 중이 아닌 요청")]
    [InlineData("running", "api_key=BUILDER_SECRET_PROMPT_CANARY")]
    public async Task RejectsNonRunningOrBuilderUnsafePersistedPromptsWithoutEchoingThem(
        string status,
        string persistedPrompt)
    {
        var (run, active) = BeginRun("unsafe-conversation", "unsafe-run");
        var source = new RecordingInputSource(Input(active, persistedPrompt, status));
        var controller = new CapabilityBuilderOpenController(run, source);

        var outcome = await controller.ExecuteAsync(Invocation(active, "unsafe-call", "create"));

        Assert.False(outcome.Execution.Success);
        Assert.Equal("capability.builder.prompt_unavailable", outcome.Execution.ErrorCode);
        Assert.Null(outcome.RequestAfterAcknowledgement);
        Assert.DoesNotContain(persistedPrompt, outcome.Execution.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain(persistedPrompt, outcome.Execution.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, source.CallCount);
        Assert.True(run.CompleteActiveRun());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static (ConversationRunController Run, ConversationRunIdentity Active) BeginRun(
        string conversationId,
        string runId)
    {
        var ids = new Queue<string>([conversationId, runId]);
        var run = new ConversationRunController(new ToolInvocationPolicy(), ids.Dequeue);
        return (run, run.BeginRun());
    }

    private static ToolInvokeParams Invocation(
        ConversationRunIdentity active,
        string toolCallId,
        string intent) =>
        new(
            toolCallId,
            active.ConversationId,
            active.RunId,
            CapabilityBuilderOpenController.CanonicalToolName,
            "R0",
            new Dictionary<string, object?> { ["intent"] = intent });

    private static PersistedConversationRunInput Input(
        ConversationRunIdentity active,
        string userInput,
        string status = "running") =>
        new(active.ConversationId, active.RunId, userInput, status);

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    private sealed class RecordingInputSource(PersistedConversationRunInput? input)
        : IConversationRunInputSource
    {
        public int CallCount { get; private set; }

        public Task<PersistedConversationRunInput?> GetPersistedRunInputAsync(
            string conversationId,
            string runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(input);
        }
    }
}
