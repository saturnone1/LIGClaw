using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;
using ApplicationCapabilityRuntimeCandidate = LIGClaw.Application.Capabilities.CapabilityRuntimeCandidate;
using ApplicationCapabilityRuntimeSnapshot = LIGClaw.Application.Capabilities.CapabilityRuntimeSnapshot;

namespace LIGClaw.Desktop.Tests;

public sealed class ConversationToolInvocationControllerTests
{
    [Fact]
    public async Task RejectsAStaleToolInvocationBeforeCallingTheExecutor()
    {
        var run = new ConversationRunController(new ToolInvocationPolicy(), () => "unused");
        var controller = new ConversationToolInvocationController(run);
        var called = false;

        var outcome = await controller.ExecuteAsync(
            Invocation("conversation", "stale", "tool"),
            (_, _, _) =>
            {
                called = true;
                return Task.FromResult(Success());
            });

        Assert.False(called);
        Assert.False(outcome.Execution.Success);
        Assert.Contains("일치하지 않아", outcome.Execution.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SerializesToolExecutionsWithinTheActiveRun()
    {
        var ids = new Queue<string>(["conversation", "run"]);
        var run = new ConversationRunController(new ToolInvocationPolicy(), ids.Dequeue);
        var active = run.BeginRun();
        run.BindCapabilitySnapshot(
            active.ConversationId,
            active.RunId,
            ApplicationCapabilityRuntimeSnapshot.Empty);
        var controller = new ConversationToolInvocationController(run);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;

        var first = controller.ExecuteAsync(
            Invocation(active.ConversationId, active.RunId, "first"),
            async (_, _, _) =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
                return Success();
            });
        await firstEntered.Task;
        var second = controller.ExecuteAsync(
            Invocation(active.ConversationId, active.RunId, "second"),
            (_, _, _) =>
            {
                secondEntered = true;
                return Task.FromResult(Success());
            });

        await Task.Yield();
        Assert.False(secondEntered);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.True(secondEntered);
    }

    [Fact]
    public async Task SelectsAnExactFrozenCandidateWithoutCallingTheWindowsExecutor()
    {
        var (run, active, snapshot, candidate) = ActiveRunWithCandidate();
        var controller = new ConversationToolInvocationController(run);
        var called = false;

        var outcome = await controller.ExecuteAsync(
            SelectorInvocation(active, snapshot, candidate, "selector-call"),
            (_, _, _) =>
            {
                called = true;
                return Task.FromResult(Success());
            });

        Assert.False(called);
        Assert.True(outcome.Execution.Success);
        Assert.True(Assert.IsType<bool>(outcome.Execution.Output["selected"]));
        var context = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            outcome.Execution.Output["context"]);
        Assert.Equal(snapshot.SnapshotHash.Value, context["snapshotHash"]);
        Assert.Equal(candidate.ModelToolName, context["modelToolName"]);
        Assert.Equal(candidate.CatalogRevision, context["catalogRevision"]);
    }

    [Fact]
    public async Task SelectorRejectsNonR0WithoutOpeningTheWindowsPath()
    {
        var (run, active, snapshot, candidate) = ActiveRunWithCandidate();
        var controller = new ConversationToolInvocationController(run);
        var invocation = SelectorInvocation(active, snapshot, candidate, "selector-call") with
        {
            Risk = "R1",
        };
        var called = false;

        var outcome = await controller.ExecuteAsync(invocation, (_, _, _) =>
        {
            called = true;
            return Task.FromResult(Success());
        });
        var retried = await controller.ExecuteAsync(
            SelectorInvocation(active, snapshot, candidate, "selector-call"),
            null);

        Assert.False(called);
        Assert.False(outcome.Execution.Success);
        Assert.Contains("위험 수준", outcome.Execution.Error, StringComparison.Ordinal);
        Assert.False(retried.Execution.Success);
        Assert.Equal("작업 도구 실행 예약 정책이 요청을 거부했습니다.", retried.Diagnostic);
    }

    [Fact]
    public async Task SelectorRejectsCatalogRevisionOutsideTheJavascriptSafeIntegerRange()
    {
        var (run, active, snapshot, candidate) = ActiveRunWithCandidate();
        var controller = new ConversationToolInvocationController(run);
        var input = SelectorInvocation(active, snapshot, candidate, "selector-call").Input
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        input["catalogRevision"] = 9_007_199_254_740_992L;

        var outcome = await controller.ExecuteAsync(
            SelectorInvocation(active, snapshot, candidate, "selector-call") with { Input = input },
            null);

        Assert.False(outcome.Execution.Success);
        Assert.Equal("기능 선택기 입력 해석을 거부했습니다.", outcome.Diagnostic);
    }

    [Fact]
    public async Task MalformedSelectorIsConsumedAndCannotBeRetriedWithTheSameToolCallId()
    {
        var (run, active, snapshot, candidate) = ActiveRunWithCandidate();
        var controller = new ConversationToolInvocationController(run);
        var malformed = SelectorInvocation(active, snapshot, candidate, "selector-call") with
        {
            Input = new Dictionary<string, object?>
            {
                ["snapshotHash"] = snapshot.SnapshotHash.Value,
                ["modelToolName"] = candidate.ModelToolName,
                ["capabilityId"] = candidate.ExactVersion.Id.Value,
                ["version"] = candidate.ExactVersion.Version.Value,
                ["contentHash"] = candidate.ExactVersion.ContentHash.Value,
                ["catalogRevision"] = candidate.CatalogRevision,
                ["arguments"] = new Dictionary<string, object?> { ["secret"] = "must-not-cross" },
            },
        };

        var first = await controller.ExecuteAsync(malformed, null);
        var second = await controller.ExecuteAsync(
            SelectorInvocation(active, snapshot, candidate, "selector-call"),
            null);

        Assert.False(first.Execution.Success);
        Assert.Equal("기능 선택기 입력 해석을 거부했습니다.", first.Diagnostic);
        Assert.DoesNotContain("must-not-cross", first.Diagnostic, StringComparison.Ordinal);
        Assert.False(second.Execution.Success);
        Assert.Equal("작업 도구 실행 예약 정책이 요청을 거부했습니다.", second.Diagnostic);
    }

    [Fact]
    public async Task SelectorSharesAtMostOnceIdentityWithRegularToolInvocations()
    {
        var (run, active, snapshot, candidate) = ActiveRunWithCandidate();
        var controller = new ConversationToolInvocationController(run);
        Assert.True(run.PrepareControlPlaneInvocation(
            active.ConversationId,
            active.RunId,
            "shared-call",
            "R0").Allowed);

        var outcome = await controller.ExecuteAsync(
            SelectorInvocation(active, snapshot, candidate, "shared-call"),
            null);

        Assert.False(outcome.Execution.Success);
        Assert.Equal("작업 도구 실행 예약 정책이 요청을 거부했습니다.", outcome.Diagnostic);
    }

    [Fact]
    public async Task SelectedCapabilityRequiresExactContextAndRequiredToolAllowlist()
    {
        var (run, active, snapshot, candidate) = ActiveRunWithCandidate();
        var controller = new ConversationToolInvocationController(run);
        var selected = await controller.ExecuteAsync(
            SelectorInvocation(active, snapshot, candidate, "selector-call"),
            null);
        Assert.True(selected.Execution.Success);
        var context = ContractContext(snapshot, candidate);
        var called = false;

        var allowed = await controller.ExecuteAsync(
            Invocation(active.ConversationId, active.RunId, "allowed-call") with
            {
                CapabilityContext = context,
            },
            (_, _, _) =>
            {
                called = true;
                return Task.FromResult(Success());
            });
        var missingContext = await controller.ExecuteAsync(
            Invocation(active.ConversationId, active.RunId, "missing-context"),
            (_, _, _) => Task.FromResult(Success()));
        var retriedWithContextCalled = false;
        var retriedWithContext = await controller.ExecuteAsync(
            Invocation(active.ConversationId, active.RunId, "missing-context") with
            {
                CapabilityContext = context,
            },
            (_, _, _) =>
            {
                retriedWithContextCalled = true;
                return Task.FromResult(Success());
            });
        var outsideAllowlist = await controller.ExecuteAsync(
            Invocation(active.ConversationId, active.RunId, "outside") with
            {
                Name = "app.list_windows.v1",
                CapabilityContext = context,
            },
            (_, _, _) => Task.FromResult(Success()));

        Assert.True(called);
        Assert.True(allowed.Execution.Success);
        Assert.False(missingContext.Execution.Success);
        Assert.Contains("기능 정책", missingContext.Execution.Error, StringComparison.Ordinal);
        Assert.False(retriedWithContextCalled);
        Assert.False(retriedWithContext.Execution.Success);
        Assert.False(outsideAllowlist.Execution.Success);
    }

    private static ToolInvokeParams Invocation(string conversationId, string runId, string toolCallId) =>
        new(toolCallId, conversationId, runId, "system.get_status.v1", "R0", new Dictionary<string, object?>());

    private static ToolInvokeParams SelectorInvocation(
        ConversationRunIdentity active,
        ApplicationCapabilityRuntimeSnapshot snapshot,
        ApplicationCapabilityRuntimeCandidate candidate,
        string toolCallId) =>
        new(
            toolCallId,
            active.ConversationId,
            active.RunId,
            "capability.select.v1",
            "R0",
            new Dictionary<string, object?>
            {
                ["snapshotHash"] = snapshot.SnapshotHash.Value,
                ["modelToolName"] = candidate.ModelToolName,
                ["capabilityId"] = candidate.ExactVersion.Id.Value,
                ["version"] = candidate.ExactVersion.Version.Value,
                ["contentHash"] = candidate.ExactVersion.ContentHash.Value,
                ["catalogRevision"] = candidate.CatalogRevision,
                ["connectionBindingHash"] = candidate.ConnectionBindingHash,
            });

    private static CapabilityInvocationContext ContractContext(
        ApplicationCapabilityRuntimeSnapshot snapshot,
        ApplicationCapabilityRuntimeCandidate candidate) =>
        new(
            snapshot.SnapshotHash.Value,
            candidate.ModelToolName,
            candidate.ExactVersion.Id.Value,
            candidate.ExactVersion.Version.Value,
            candidate.ExactVersion.ContentHash.Value,
            candidate.CatalogRevision,
            candidate.ConnectionBindingHash);

    private static (
        ConversationRunController Run,
        ConversationRunIdentity Active,
        ApplicationCapabilityRuntimeSnapshot Snapshot,
        ApplicationCapabilityRuntimeCandidate Candidate) ActiveRunWithCandidate()
    {
        var ids = new Queue<string>(["conversation", "run"]);
        var run = new ConversationRunController(new ToolInvocationPolicy(), ids.Dequeue);
        var active = run.BeginRun();
        var exactVersion = new CapabilityVersionRef(
            CapabilityId.Parse("desktop.status"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            CapabilityContentHash.Parse($"sha256:{new string('a', 64)}"));
        var candidate = new ApplicationCapabilityRuntimeCandidate(
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
        var snapshot = ApplicationCapabilityRuntimeSnapshot.Create([candidate]);
        run.BindCapabilitySnapshot(active.ConversationId, active.RunId, snapshot);
        return (run, active, snapshot, candidate);
    }

    private static WindowsToolExecutionResult Success() =>
        new(true, new Dictionary<string, object?>());
}
