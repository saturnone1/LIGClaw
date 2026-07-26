using LIGClaw.Domain;

namespace LIGClaw.Application.Scheduling;

public sealed record ScheduledJobClaim(ScheduledNotification Job, string RunId, DateTimeOffset ScheduledAtUtc);

public sealed record ScheduleRecoveryResult(int Skipped, int AwaitingDecision, int ReadyToRun);

public interface IScheduleRepository
{
    Task<ScheduledNotification> CreateAsync(
        NotificationScheduleDraft draft,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
    Task<ScheduledNotification?> UpdateAsync(
        string jobId,
        NotificationScheduleDraft draft,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ScheduledNotification>> ListAsync(
        bool includeInactive,
        int offset,
        int limit,
        CancellationToken cancellationToken);
    Task<ScheduledNotification?> GetAsync(string jobId, CancellationToken cancellationToken);
    Task<bool> CancelAsync(string jobId, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<ScheduleRecoveryResult> ReconcileOnStartupAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<ScheduleRecoveryResult> ReconcileOverdueAsync(
        DateTimeOffset nowUtc,
        DateTimeOffset dueBeforeUtc,
        CancellationToken cancellationToken);
    Task<ScheduledJobClaim?> TryClaimDueAsync(
        DateTimeOffset nowUtc,
        DateTimeOffset leaseUntilUtc,
        CancellationToken cancellationToken);
    Task CompleteRunAsync(
        string runId,
        DateTimeOffset completedAtUtc,
        bool succeeded,
        string? errorCode,
        CancellationToken cancellationToken);
    Task<bool> ResolveMisfireAsync(
        string jobId,
        bool runNow,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
}
