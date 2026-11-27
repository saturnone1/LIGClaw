using LIGClaw.Application.Tools;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class ConversationRunControllerTests
{
    [Fact]
    public void BeginAndCompleteKeepSessionAndToolPolicyInOneLifecycle()
    {
        var ids = new Queue<string>(["conversation", "run"]);
        var policy = new ToolInvocationPolicy();
        var controller = new ConversationRunController(policy, ids.Dequeue);

        var identity = controller.BeginRun();

        Assert.True(controller.IsRunning);
        Assert.False(controller.IsCancelling);
        Assert.True(controller.IsActiveRun(identity.ConversationId, identity.RunId));
        Assert.True(controller.CompleteActiveRun());
        Assert.False(controller.IsRunning);
        Assert.False(controller.IsActiveRun(identity.ConversationId, identity.RunId));
        Assert.True(identity.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void CancellationChangesPresentationStateAndCancelsOnlyTheActiveRun()
    {
        var ids = new Queue<string>(["conversation", "run"]);
        var controller = new ConversationRunController(new ToolInvocationPolicy(), ids.Dequeue);
        var identity = controller.BeginRun();

        Assert.True(controller.TryRequestCancellation(out var active));
        Assert.NotNull(active);
        Assert.Equal(identity.ConversationId, active.ConversationId);
        Assert.True(controller.IsRunning);
        Assert.True(controller.IsCancelling);
        Assert.True(identity.CancellationToken.IsCancellationRequested);
        Assert.False(controller.TryGetCancellationToken("other", identity.RunId, out _));

        Assert.True(controller.CompleteActiveRun());
        Assert.False(controller.IsCancelling);
    }

    [Fact]
    public void CompletingWithoutAnActiveRunCannotMutatePolicyState()
    {
        var controller = new ConversationRunController(new ToolInvocationPolicy());

        Assert.False(controller.CompleteActiveRun());
        Assert.False(controller.IsRunning);
        Assert.False(controller.IsCancelling);
    }
}
