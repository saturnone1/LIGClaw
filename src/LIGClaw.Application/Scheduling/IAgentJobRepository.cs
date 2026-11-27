using LIGClaw.Domain;

namespace LIGClaw.Application.Scheduling;

public sealed record AgentJobClaim(
    ScheduledAgentJob Job,
    string RunId,
    DateTimeOffset ScheduledAtUtc,
    int Attempt);

public sealed record AgentJobRunResult(
    bool Succeeded,
    string? ResultText = null,
    string? ErrorCode = null);

public sealed record AgentJobRunRecord(
    string Id,
    string JobId,
    DateTimeOffset ScheduledAtUtc,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string Status,
    int Attempt,
    string? ResultText,
    string? ErrorCode);

public interface IAgentJobRepository
{
    Task<ScheduledAgentJob> CreateAgentJobAsync(
        AgentJobDraft draft,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ScheduledAgentJob>> ListAgentJobsAsync(
        bool includeInactive,
        int offset,
        int limit,
        CancellationToken cancellationToken);
    Task<ScheduledAgentJob?> GetAgentJobAsync(string jobId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentJobRunRecord>> ListAgentJobRunsAsync(
        string jobId,
        int limit,
        CancellationToken cancellationToken);
    Task<bool> CancelAgentJobAsync(string jobId, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<bool> SetAgentJobPausedAsync(string jobId, bool paused, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<bool> RetryAgentJobNowAsync(string jobId, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<ScheduleRecoveryResult> ReconcileAgentJobsOnStartupAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<ScheduleRecoveryResult> ReconcileOverdueAgentJobsAsync(
        DateTimeOffset nowUtc,
        DateTimeOffset dueBeforeUtc,
        CancellationToken cancellationToken);
    Task<AgentJobClaim?> TryClaimDueAgentJobAsync(
        DateTimeOffset nowUtc,
        DateTimeOffset leaseUntilUtc,
        CancellationToken cancellationToken);
    Task CompleteAgentJobRunAsync(
        string runId,
        DateTimeOffset completedAtUtc,
        AgentJobRunResult result,
        CancellationToken cancellationToken);
    Task<bool> ResolveAgentJobMisfireAsync(
        string jobId,
        bool runNow,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
}
