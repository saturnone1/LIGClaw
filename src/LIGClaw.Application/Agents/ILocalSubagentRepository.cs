using LIGClaw.Domain;

namespace LIGClaw.Application.Agents;

public sealed record SubagentTaskRecord(
    string BatchId,
    string ChildRunId,
    int Ordinal,
    string Title,
    string Prompt,
    string Status,
    string? ResultText,
    string? ErrorCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SubagentTaskResult(
    string ChildRunId,
    string Title,
    bool Succeeded,
    string? ResultText,
    string? ErrorCode);

public sealed record SubagentBatchResult(
    string BatchId,
    IReadOnlyList<SubagentTaskResult> Tasks);

public interface ILocalSubagentRepository
{
    Task<IReadOnlyList<SubagentTaskRecord>> CreateSubagentBatchAsync(
        string batchId,
        SubagentBatchDraft draft,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
    Task CompleteSubagentTaskAsync(
        string childRunId,
        bool succeeded,
        string? resultText,
        string? errorCode,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);
    Task ReconcileSubagentTasksOnStartupAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<SubagentTaskRecord>> ListSubagentTasksAsync(
        string batchId,
        CancellationToken cancellationToken);
}

public interface ILocalSubagentOrchestrator
{
    Task<SubagentBatchResult> ExecuteAsync(SubagentBatchDraft draft, CancellationToken cancellationToken);
}
