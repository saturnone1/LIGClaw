namespace LIGClaw.Domain;

public sealed record AgentJobDraft(
    string Title,
    string Prompt,
    DateTime StartLocal,
    string TimeZoneId,
    string Recurrence,
    int Interval,
    string MisfirePolicy,
    string? ModelProfileId,
    int MaxRuntimeSeconds,
    int MaxAttempts,
    int ResultMaxCharacters,
    string Source);

public sealed record ScheduledAgentJob(
    string Id,
    string Title,
    string Prompt,
    DateTime StartLocal,
    string TimeZoneId,
    string Recurrence,
    int Interval,
    string MisfirePolicy,
    string? ModelProfileId,
    int MaxRuntimeSeconds,
    int MaxAttempts,
    int ResultMaxCharacters,
    string Source,
    string Status,
    int AttemptCount,
    DateTimeOffset? NextRunAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public static class AgentJobPolicy
{
    public static bool IsValid(AgentJobDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft.Title.Length is >= 1 and <= 80 &&
               draft.Prompt.Length is >= 1 and <= 8_000 &&
               draft.StartLocal.Kind == DateTimeKind.Unspecified &&
               draft.TimeZoneId.Length is >= 1 and <= 128 &&
               draft.Recurrence is ScheduleValues.Once or ScheduleValues.Daily or ScheduleValues.Weekly &&
               draft.Interval is >= 1 and <= 365 &&
               draft.MisfirePolicy is ScheduleValues.Skip or ScheduleValues.RunOnceOnResume or ScheduleValues.Ask &&
               (draft.ModelProfileId is null || draft.ModelProfileId.Length is >= 1 and <= 64) &&
               draft.MaxRuntimeSeconds is >= 30 and <= 3_600 &&
               draft.MaxAttempts is >= 1 and <= 5 &&
               draft.ResultMaxCharacters is >= 1_000 and <= 100_000 &&
               draft.Source.Length is >= 1 and <= 160 &&
               NotificationSchedulePolicy.TryFindTimeZone(draft.TimeZoneId, out _);
    }

    public static DateTimeOffset FirstOccurrenceUtc(AgentJobDraft draft)
    {
        if (!IsValid(draft)) throw new ArgumentException("Agent job 정보가 올바르지 않습니다.", nameof(draft));
        _ = NotificationSchedulePolicy.TryFindTimeZone(draft.TimeZoneId, out var timeZone);
        return NotificationSchedulePolicy.ResolveLocal(draft.StartLocal, timeZone!);
    }

    public static DateTimeOffset? NextOccurrenceAfter(ScheduledAgentJob job, DateTimeOffset afterUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Recurrence == ScheduleValues.Once) return null;
        if (!NotificationSchedulePolicy.TryFindTimeZone(job.TimeZoneId, out var timeZone))
            throw new TimeZoneNotFoundException($"Agent job 시간대를 찾을 수 없습니다: {job.TimeZoneId}");
        var resolvedTimeZone = timeZone!;
        var localAfter = TimeZoneInfo.ConvertTime(afterUtc, resolvedTimeZone).DateTime;
        var unitDays = job.Recurrence == ScheduleValues.Daily ? job.Interval : checked(job.Interval * 7);
        var elapsedDays = Math.Max(0, (localAfter.Date - job.StartLocal.Date).Days);
        var steps = Math.Max(0L, elapsedDays / unitDays);
        var candidate = job.StartLocal.AddDays(checked(steps * unitDays));
        var candidateUtc = NotificationSchedulePolicy.ResolveLocal(candidate, resolvedTimeZone);
        if (candidateUtc <= afterUtc)
            candidateUtc = NotificationSchedulePolicy.ResolveLocal(candidate.AddDays(unitDays), resolvedTimeZone);
        return candidateUtc;
    }
}
