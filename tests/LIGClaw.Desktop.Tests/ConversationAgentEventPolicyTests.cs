using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class ConversationAgentEventPolicyTests
{
    [Fact]
    public void RoutingChangeProducesBoundedUserAndDiagnosticEffects()
    {
        var decision = ConversationAgentEventPolicy.Evaluate(Event("routing_changed", message: "backup|provider_unavailable"));

        Assert.Equal(ConversationAgentEventAction.AppendStatus, decision.Action);
        Assert.Equal("대체 모델로 답변을 계속하고 있어요…", decision.RunStatus);
        Assert.Contains("backup", decision.Transcript, StringComparison.Ordinal);
        Assert.Equal("모델 fallback 전환: backup (provider_unavailable)", decision.Diagnostic);
        Assert.False(decision.IsTerminal);
    }

    [Theory]
    [InlineData("run_completed", "Complete", "답변을 마쳤어요.")]
    [InlineData("run_cancelled", "AppendStatus", "요청을 중단했어요.")]
    [InlineData("run_failed", "Fail", "요청을 처리하지 못했어요. 다시 시도해 주세요.")]
    public void TerminalEventsHaveDeterministicPresentation(
        string type,
        string action,
        string status)
    {
        var decision = ConversationAgentEventPolicy.Evaluate(Event(type, message: "provider_auth_failed"));

        Assert.Equal(Enum.Parse<ConversationAgentEventAction>(action), decision.Action);
        Assert.Equal(status, decision.RunStatus);
        Assert.True(decision.IsTerminal);
    }

    private static AgentEvent Event(string type, string? text = null, string? message = null) =>
        new("conversation", "run", 0, type, DateTimeOffset.UnixEpoch, text, message);
}
