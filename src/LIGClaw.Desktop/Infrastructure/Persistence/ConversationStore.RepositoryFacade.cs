using LIGClaw.Application.Agents;
using LIGClaw.Application.Scheduling;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed partial class ConversationStore
{
    public Task<ScheduledAgentJob> CreateAgentJobAsync(AgentJobDraft draft, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _agentJobs.CreateAgentJobAsync(draft, nowUtc, cancellationToken);

    public Task<IReadOnlyList<ScheduledAgentJob>> ListAgentJobsAsync(bool includeInactive, int offset, int limit, CancellationToken cancellationToken) =>
        _agentJobs.ListAgentJobsAsync(includeInactive, offset, limit, cancellationToken);

    public Task<ScheduledAgentJob?> GetAgentJobAsync(string jobId, CancellationToken cancellationToken) =>
        _agentJobs.GetAgentJobAsync(jobId, cancellationToken);

    public Task<IReadOnlyList<AgentJobRunRecord>> ListAgentJobRunsAsync(string jobId, int limit, CancellationToken cancellationToken) =>
        _agentJobs.ListAgentJobRunsAsync(jobId, limit, cancellationToken);

    public Task<bool> CancelAgentJobAsync(string jobId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _agentJobs.CancelAgentJobAsync(jobId, nowUtc, cancellationToken);

    public Task<bool> SetAgentJobPausedAsync(string jobId, bool paused, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _agentJobs.SetAgentJobPausedAsync(jobId, paused, nowUtc, cancellationToken);

    public Task<bool> RetryAgentJobNowAsync(string jobId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _agentJobs.RetryAgentJobNowAsync(jobId, nowUtc, cancellationToken);

    public Task<ScheduleRecoveryResult> ReconcileAgentJobsOnStartupAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _agentJobs.ReconcileAgentJobsOnStartupAsync(nowUtc, cancellationToken);

    public Task<ScheduleRecoveryResult> ReconcileOverdueAgentJobsAsync(DateTimeOffset nowUtc, DateTimeOffset dueBeforeUtc, CancellationToken cancellationToken) =>
        _agentJobs.ReconcileOverdueAgentJobsAsync(nowUtc, dueBeforeUtc, cancellationToken);

    public Task<AgentJobClaim?> TryClaimDueAgentJobAsync(DateTimeOffset nowUtc, DateTimeOffset leaseUntilUtc, CancellationToken cancellationToken) =>
        _agentJobs.TryClaimDueAgentJobAsync(nowUtc, leaseUntilUtc, cancellationToken);

    public Task CompleteAgentJobRunAsync(string runId, DateTimeOffset completedAtUtc, AgentJobRunResult result, CancellationToken cancellationToken) =>
        _agentJobs.CompleteAgentJobRunAsync(runId, completedAtUtc, result, cancellationToken);

    public Task<bool> ResolveAgentJobMisfireAsync(string jobId, bool runNow, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _agentJobs.ResolveAgentJobMisfireAsync(jobId, runNow, nowUtc, cancellationToken);

    public Task<IReadOnlyList<SubagentTaskRecord>> CreateSubagentBatchAsync(string batchId, SubagentBatchDraft draft, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _subagents.CreateSubagentBatchAsync(batchId, draft, nowUtc, cancellationToken);

    public Task CompleteSubagentTaskAsync(string childRunId, bool succeeded, string? resultText, string? errorCode, DateTimeOffset completedAtUtc, CancellationToken cancellationToken) =>
        _subagents.CompleteSubagentTaskAsync(childRunId, succeeded, resultText, errorCode, completedAtUtc, cancellationToken);

    public Task ReconcileSubagentTasksOnStartupAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        _subagents.ReconcileSubagentTasksOnStartupAsync(nowUtc, cancellationToken);

    public Task<IReadOnlyList<SubagentTaskRecord>> ListSubagentTasksAsync(string batchId, CancellationToken cancellationToken) =>
        _subagents.ListSubagentTasksAsync(batchId, cancellationToken);
}
