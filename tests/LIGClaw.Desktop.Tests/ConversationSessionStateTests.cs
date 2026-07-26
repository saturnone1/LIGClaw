using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class ConversationSessionStateTests
{
    [Fact]
    public void FollowUpRunsKeepTheConversationAndRotateOnlyTheRunIdentity()
    {
        var ids = new Queue<string>(["conversation-1", "run-1", "run-2"]);
        var state = new ConversationSessionState(ids.Dequeue);

        var first = state.BeginRun();
        Assert.True(first.IsNewConversation);
        Assert.True(state.EndRun(first.ConversationId, first.RunId));
        var followUp = state.BeginRun();

        Assert.False(followUp.IsNewConversation);
        Assert.Equal(first.ConversationId, followUp.ConversationId);
        Assert.NotEqual(first.RunId, followUp.RunId);
    }

    [Fact]
    public void ExplicitNewConversationOrSelectionControlsTheThreadBoundary()
    {
        var ids = new Queue<string>(["created-thread", "created-run", "new-thread", "new-run", "selected-run"]);
        var state = new ConversationSessionState(ids.Dequeue);
        var first = state.BeginRun();
        Assert.False(state.StartNewConversation());
        Assert.False(state.SelectConversation("other-thread"));
        Assert.True(state.EndRun(first.ConversationId, first.RunId));

        Assert.True(state.StartNewConversation());
        var created = state.BeginRun();
        Assert.Equal("new-thread", created.ConversationId);
        Assert.True(state.EndRun(created.ConversationId, created.RunId));
        Assert.True(state.SelectConversation("saved-thread"));
        var resumed = state.BeginRun();

        Assert.Equal("saved-thread", resumed.ConversationId);
        Assert.Equal("selected-run", resumed.RunId);
        Assert.False(resumed.IsNewConversation);
    }

    [Fact]
    public void LateTerminalEventCannotEndAnotherRun()
    {
        var ids = new Queue<string>(["thread", "run"]);
        var state = new ConversationSessionState(ids.Dequeue);
        var active = state.BeginRun();

        Assert.False(state.EndRun(active.ConversationId, "late-run"));
        Assert.True(state.IsRunActive);
        Assert.True(state.EndRun(active.ConversationId, active.RunId));
        Assert.False(state.IsRunActive);
    }

    [Fact]
    public void CancellingAnActiveRunPropagatesToDesktopWorkOnlyForThatRun()
    {
        var ids = new Queue<string>(["thread", "run"]);
        var state = new ConversationSessionState(ids.Dequeue);
        var active = state.BeginRun();

        Assert.False(state.CancelRun(active.ConversationId, "another-run"));
        Assert.False(active.CancellationToken.IsCancellationRequested);
        Assert.True(state.TryGetCancellationToken(active.ConversationId, active.RunId, out var token));
        Assert.Equal(active.CancellationToken, token);

        Assert.True(state.CancelRun(active.ConversationId, active.RunId));
        Assert.True(active.CancellationToken.IsCancellationRequested);
        Assert.True(state.IsRunActive);
    }

    [Fact]
    public void EndingARunCancelsItsLifetimeAndDoesNotLeakItToTheNextRun()
    {
        var ids = new Queue<string>(["thread", "run-1", "run-2"]);
        var state = new ConversationSessionState(ids.Dequeue);
        var first = state.BeginRun();

        Assert.True(state.EndRun(first.ConversationId, first.RunId));
        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(state.TryGetCancellationToken(first.ConversationId, first.RunId, out _));

        var second = state.BeginRun();
        Assert.False(second.CancellationToken.IsCancellationRequested);
    }
}
