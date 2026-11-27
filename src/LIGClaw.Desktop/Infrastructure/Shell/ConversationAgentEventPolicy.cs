using LIGClaw.Contracts.Generated;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal enum ConversationAgentEventAction
{
    None,
    AppendText,
    AppendStatus,
    Complete,
    Fail,
}

internal sealed record ConversationAgentEventDecision(
    ConversationAgentEventAction Action,
    string? RunStatus = null,
    string? Transcript = null,
    string? Diagnostic = null,
    bool IsTerminal = false);

internal static class ConversationAgentEventPolicy
{
    public static ConversationAgentEventDecision Evaluate(AgentEvent agentEvent) =>
        agentEvent.Type switch
        {
            "run_started" => new(
                ConversationAgentEventAction.None,
                RunStatus: "답변을 작성하고 있어요…"),
            "routing_changed" => RoutingChanged(agentEvent.Message),
            "text_delta" => new(
                ConversationAgentEventAction.AppendText,
                Transcript: agentEvent.Text),
            "run_completed" => new(
                ConversationAgentEventAction.Complete,
                RunStatus: "답변을 마쳤어요.",
                IsTerminal: true),
            "run_cancelled" => new(
                ConversationAgentEventAction.AppendStatus,
                RunStatus: "요청을 중단했어요.",
                Transcript: "요청을 중단했습니다.",
                IsTerminal: true),
            "run_failed" => Failed(agentEvent.Message),
            _ => new(ConversationAgentEventAction.None),
        };

    private static ConversationAgentEventDecision RoutingChanged(string? message)
    {
        var routing = message?.Split('|', 2);
        var profile = routing is { Length: 2 } ? routing[0] : "fallback";
        var reason = routing is { Length: 2 } ? routing[1] : "provider_unavailable";
        return new ConversationAgentEventDecision(
            ConversationAgentEventAction.AppendStatus,
            RunStatus: "대체 모델로 답변을 계속하고 있어요…",
            Transcript: $"모델 연결 문제로 ‘{profile}’ 프로필로 전환했습니다. 사유: {reason}",
            Diagnostic: $"모델 fallback 전환: {profile} ({reason})");
    }

    private static ConversationAgentEventDecision Failed(string? failureCode)
    {
        var message = AgentFailurePolicy.ToUserMessage(failureCode);
        return new ConversationAgentEventDecision(
            ConversationAgentEventAction.Fail,
            RunStatus: message,
            Diagnostic: $"요청 처리 실패: {message}",
            IsTerminal: true);
    }
}
