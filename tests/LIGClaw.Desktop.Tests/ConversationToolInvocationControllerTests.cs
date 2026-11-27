using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Tools;

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
            (_, _) =>
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
        var controller = new ConversationToolInvocationController(run);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;

        var first = controller.ExecuteAsync(
            Invocation(active.ConversationId, active.RunId, "first"),
            async (_, _) =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
                return Success();
            });
        await firstEntered.Task;
        var second = controller.ExecuteAsync(
            Invocation(active.ConversationId, active.RunId, "second"),
            (_, _) =>
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

    private static ToolInvokeParams Invocation(string conversationId, string runId, string toolCallId) =>
        new(toolCallId, conversationId, runId, "system.get_status.v1", "R0", new Dictionary<string, object?>());

    private static WindowsToolExecutionResult Success() =>
        new(true, new Dictionary<string, object?>());
}
