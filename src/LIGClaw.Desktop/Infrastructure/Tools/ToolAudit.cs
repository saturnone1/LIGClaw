namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record ToolApprovalAuditRecord(
    string ConversationId,
    string RunId,
    string ToolCallId,
    string ToolName,
    string Risk,
    bool Approved,
    string Scope,
    string Summary,
    DateTimeOffset CreatedAtUtc);

internal sealed record ToolExecutionAuditRecord(
    string ConversationId,
    string RunId,
    string ToolCallId,
    string ToolName,
    string Risk,
    string Status,
    string Summary,
    DateTimeOffset CreatedAtUtc);

internal enum ToolApprovalChoice
{
    Deny,
    AllowOnce,
    AllowConversation,
    AllowAlways,
}

internal sealed record CapabilityGrant(
    string Id,
    string ToolName,
    string Risk,
    string ScopeHash,
    string ScopeSummary,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public string DisplayText => $"{ScopeSummary} · {ExpiresAtUtc.ToLocalTime():yyyy.MM.dd}까지";
}

internal sealed record ToolActivitySummary(
    long Id,
    string ToolName,
    string Risk,
    string Status,
    string Summary,
    bool? Approved,
    DateTimeOffset CreatedAtUtc)
{
    public string StatusDisplay => Status switch
    {
        "succeeded" => "완료",
        "failed" => "실패",
        "denied" => "허용 안 함",
        "cancelled" => "중단",
        _ => Status,
    };

    public string CreatedAtDisplay => CreatedAtUtc.ToLocalTime().ToString("MM.dd HH:mm");
}

internal sealed record UndoActivitySummary(
    string UndoId,
    string Kind,
    string DisplayName,
    DateTimeOffset CreatedAtUtc)
{
    public string Description => Kind switch
    {
        "copy" => $"{DisplayName} 복사 되돌리기",
        "move" => $"{DisplayName} 이동 되돌리기",
        "rename" => $"{DisplayName} 이름 변경 되돌리기",
        _ => $"{DisplayName} 작업 되돌리기",
    };

    public string CreatedAtDisplay => CreatedAtUtc.ToLocalTime().ToString("MM.dd HH:mm");
}

internal interface IToolAuditSink
{
    Task RecordApprovalAsync(ToolApprovalAuditRecord record, CancellationToken cancellationToken);
    Task RecordExecutionAsync(ToolExecutionAuditRecord record, CancellationToken cancellationToken);
}

internal interface ICapabilityGrantStore
{
    Task<CapabilityGrant?> FindActiveGrantAsync(
        string toolName,
        string risk,
        string scopeHash,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task SaveGrantAsync(CapabilityGrant grant, CancellationToken cancellationToken);

    Task<IReadOnlyList<CapabilityGrant>> GetActiveGrantsAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task RevokeGrantAsync(string grantId, DateTimeOffset revokedAtUtc, CancellationToken cancellationToken = default);
}
