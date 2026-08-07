namespace LIGClaw.Application.Tools;

public sealed record ToolAuthorizationDecision(
    bool Allowed,
    bool RequiresApproval = false,
    string? Error = null);

public sealed class ToolInvocationPolicy
{
    private static readonly HashSet<string> KnownRisks = ["R0", "R1", "R2", "R3", "R4"];
    private readonly object _sync = new();
    private readonly HashSet<string> _seenToolCallIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingApprovalIds = new(StringComparer.Ordinal);
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
            _seenToolCallIds.Clear();
            _pendingApprovalIds.Clear();
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

    public ToolAuthorizationDecision Prepare(
        string conversationId,
        string runId,
        string toolCallId,
        string risk)
    {
        if (string.IsNullOrWhiteSpace(toolCallId) || !KnownRisks.Contains(risk))
            return Deny("Tool 호출 정보가 올바르지 않습니다.");

        lock (_sync)
        {
            if (!StringComparer.Ordinal.Equals(_conversationId, conversationId) ||
                !StringComparer.Ordinal.Equals(_runId, runId))
                return Deny("현재 요청에 속하지 않은 Tool 호출을 거부했어요.");
            if (!_seenToolCallIds.Add(toolCallId))
                return Deny("이미 처리한 Tool 호출을 다시 실행하지 않았어요.");
            if (!StringComparer.Ordinal.Equals(risk, "R0"))
            {
                _pendingApprovalIds.Add(toolCallId);
                return new ToolAuthorizationDecision(false, RequiresApproval: true);
            }
            return new ToolAuthorizationDecision(true);
        }
    }

    public ToolAuthorizationDecision ResolveApproval(
        string conversationId,
        string runId,
        string toolCallId,
        bool approved)
    {
        lock (_sync)
        {
            if (!StringComparer.Ordinal.Equals(_conversationId, conversationId) ||
                !StringComparer.Ordinal.Equals(_runId, runId))
                return Deny("현재 요청에 속하지 않은 승인을 거부했어요.");
            if (!_pendingApprovalIds.Remove(toolCallId))
                return Deny("대기 중인 승인을 찾을 수 없어요.");
            return approved
                ? new ToolAuthorizationDecision(true)
                : Deny("사용자가 이 동작을 허용하지 않았어요.");
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
            _seenToolCallIds.Clear();
            _pendingApprovalIds.Clear();
        }
    }

    private static ToolAuthorizationDecision Deny(string error) => new(false, Error: error);
}
