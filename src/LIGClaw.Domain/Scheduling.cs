using System.Globalization;

namespace LIGClaw.Domain;

public sealed record NotificationScheduleDraft(
    string Title,
    string Message,
    DateTime StartLocal,
    string TimeZoneId,
    string Recurrence,
    int Interval,
    string MisfirePolicy,
    string Source);

public sealed record ScheduledNotification(
    string Id,
    string Title,
    string Message,
    DateTime StartLocal,
    string TimeZoneId,
    string Recurrence,
    int Interval,
    string MisfirePolicy,
    string Source,
    string Status,
    DateTimeOffset? NextRunAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public static class ScheduleValues
{
    public const string Once = "once";
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Skip = "skip";
    public const string RunOnceOnResume = "run_once_on_resume";
    public const string Ask = "ask";
    public const string Pending = "pending";
    public const string Running = "running";
    public const string AwaitingDecision = "awaiting_decision";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Paused = "paused";
}

public static class NotificationSchedulePolicy
{
    private static readonly HashSet<string> Recurrences =
        [ScheduleValues.Once, ScheduleValues.Daily, ScheduleValues.Weekly];
    private static readonly HashSet<string> MisfirePolicies =
        [ScheduleValues.Skip, ScheduleValues.RunOnceOnResume, ScheduleValues.Ask];

    public static bool IsValid(NotificationScheduleDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft.Title.Length is >= 1 and <= 63 &&
               draft.Message.Length is >= 1 and <= 255 &&
               draft.StartLocal.Kind == DateTimeKind.Unspecified &&
               draft.TimeZoneId.Length is >= 1 and <= 128 &&
               Recurrences.Contains(draft.Recurrence) &&
               draft.Interval is >= 1 and <= 365 &&
               MisfirePolicies.Contains(draft.MisfirePolicy) &&
               draft.Source.Length is >= 1 and <= 160 &&
               TryFindTimeZone(draft.TimeZoneId, out _);
    }

    public static bool TryParseLocal(string value, out DateTime local)
    {
        if (!DateTime.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out local)) return false;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return true;
    }

    public static DateTimeOffset ResolveLocal(DateTime local, TimeZoneInfo timeZone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var adjusted = local;
        for (var minute = 0; timeZone.IsInvalidTime(adjusted) && minute < 180; minute++)
            adjusted = adjusted.AddMinutes(1);
        if (timeZone.IsInvalidTime(adjusted))
            throw new ArgumentOutOfRangeException(nameof(local), "현지 시각을 유효한 시각으로 조정할 수 없습니다.");

        var offset = timeZone.IsAmbiguousTime(adjusted)
            ? timeZone.GetAmbiguousTimeOffsets(adjusted).Max()
            : timeZone.GetUtcOffset(adjusted);
        return new DateTimeOffset(adjusted, offset).ToUniversalTime();
    }

    public static DateTimeOffset FirstOccurrenceUtc(NotificationScheduleDraft draft)
    {
        if (!IsValid(draft)) throw new ArgumentException("예약 정보가 올바르지 않습니다.", nameof(draft));
        _ = TryFindTimeZone(draft.TimeZoneId, out var timeZone);
        return ResolveLocal(draft.StartLocal, timeZone!);
    }

    public static DateTimeOffset? NextOccurrenceAfter(ScheduledNotification job, DateTimeOffset afterUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Recurrence == ScheduleValues.Once) return null;
        if (!TryFindTimeZone(job.TimeZoneId, out var timeZone))
            throw new TimeZoneNotFoundException($"예약 시간대를 찾을 수 없습니다: {job.TimeZoneId}");

        var resolvedTimeZone = timeZone!;
        var localAfter = TimeZoneInfo.ConvertTime(afterUtc, resolvedTimeZone).DateTime;
        var unitDays = job.Recurrence == ScheduleValues.Daily ? job.Interval : checked(job.Interval * 7);
        var elapsedDays = Math.Max(0, (localAfter.Date - job.StartLocal.Date).Days);
        var steps = Math.Max(0L, elapsedDays / unitDays);
        var candidate = job.StartLocal.AddDays(checked(steps * unitDays));
        var candidateUtc = ResolveLocal(candidate, resolvedTimeZone);
        if (candidateUtc <= afterUtc)
        {
            candidate = candidate.AddDays(unitDays);
            candidateUtc = ResolveLocal(candidate, resolvedTimeZone);
        }
        return candidateUtc;
    }

    public static bool TryFindTimeZone(string id, out TimeZoneInfo? timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = null;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            timeZone = null;
            return false;
        }
    }
}
