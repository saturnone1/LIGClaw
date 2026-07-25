namespace LIGClaw.Application.Tools;

public sealed record ToolAuthorizationDecision(bool Allowed, string? Error = null);

public sealed class ToolInvocationPolicy
{
    private static readonly HashSet<string> KnownRisks = ["R0", "R1", "R2", "R3", "R4"];
    private readonly object _sync = new();
    private readonly HashSet<string> _acceptedToolCallIds = new(StringComparer.Ordinal);
    private string? _conversationId;
    private string? _runId;

    public void BeginRun(string conversationId, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        lock (_sync)
        {
            if (_conversationId is not null)
                throw new InvalidOperationException("다른 요청이 이미 실행 중입니다.");
            _conversationId = conversationId;
            _runId = runId;
            _acceptedToolCallIds.Clear();
        }
    }

    public bool IsActiveRun(string conversationId, string runId)
    {
        lock (_sync)
        {
            return StringComparer.Ordinal.Equals(_conversationId, conversationId) &&
                   StringComparer.Ordinal.Equals(_runId, runId);
        }
    }

    public ToolAuthorizationDecision Authorize(
        string conversationId,
        string runId,
        string toolCallId,
        string risk,
        bool userApproved = false)
    {
        if (string.IsNullOrWhiteSpace(toolCallId) || !KnownRisks.Contains(risk))
            return Deny("Tool 호출 정보가 올바르지 않습니다.");

        lock (_sync)
        {
            if (!StringComparer.Ordinal.Equals(_conversationId, conversationId) ||
                !StringComparer.Ordinal.Equals(_runId, runId))
                return Deny("현재 요청에 속하지 않은 Tool 호출을 거부했어요.");
            if (_acceptedToolCallIds.Contains(toolCallId))
                return Deny("이미 처리한 Tool 호출을 다시 실행하지 않았어요.");
            if (!StringComparer.Ordinal.Equals(risk, "R0") && !userApproved)
                return Deny("사용자 승인이 필요한 기능이에요.");

            _acceptedToolCallIds.Add(toolCallId);
            return new ToolAuthorizationDecision(true);
        }
    }

    public void EndRun(string conversationId, string runId)
    {
        lock (_sync)
        {
            if (!StringComparer.Ordinal.Equals(_conversationId, conversationId) ||
                !StringComparer.Ordinal.Equals(_runId, runId))
                return;
            _conversationId = null;
            _runId = null;
            _acceptedToolCallIds.Clear();
        }
    }

    private static ToolAuthorizationDecision Deny(string error) => new(false, error);
}
