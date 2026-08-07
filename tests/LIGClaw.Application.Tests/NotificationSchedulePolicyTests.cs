using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class NotificationSchedulePolicyTests
{
    private const string EasternTime = "Eastern Standard Time";

    [Fact]
    public void SpringGapMovesToFirstValidLocalMinute()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(EasternTime);
        var invalid = new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified);

        var resolved = NotificationSchedulePolicy.ResolveLocal(invalid, timeZone);

        Assert.Equal(DateTimeOffset.Parse("2026-03-08T07:00:00Z"), resolved);
    }

    [Fact]
    public void FallOverlapChoosesTheEarlierUtcOccurrence()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(EasternTime);
        var ambiguous = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified);

        var resolved = NotificationSchedulePolicy.ResolveLocal(ambiguous, timeZone);

        Assert.Equal(DateTimeOffset.Parse("2026-11-01T05:30:00Z"), resolved);
    }

    [Fact]
    public void DailyRecurrenceKeepsWallClockAcrossDst()
    {
        var job = Job(new DateTime(2026, 3, 7, 9, 0, 0, DateTimeKind.Unspecified), ScheduleValues.Daily);

        var next = NotificationSchedulePolicy.NextOccurrenceAfter(
            job,
            DateTimeOffset.Parse("2026-03-07T14:00:00Z"));

        Assert.Equal(DateTimeOffset.Parse("2026-03-08T13:00:00Z"), next);
    }

    [Fact]
    public void WeeklyIntervalUsesTheAnchoredLocalWeekday()
    {
        var job = Job(new DateTime(2026, 7, 6, 10, 0, 0, DateTimeKind.Unspecified), ScheduleValues.Weekly, 2);

        var next = NotificationSchedulePolicy.NextOccurrenceAfter(
            job,
            DateTimeOffset.Parse("2026-07-06T14:00:00Z"));

        Assert.Equal(DateTimeOffset.Parse("2026-07-20T14:00:00Z"), next);
    }

    private static ScheduledNotification Job(DateTime startLocal, string recurrence, int interval = 1) => new(
        "job", "title", "message", startLocal, EasternTime, recurrence, interval, ScheduleValues.Skip,
        "test", ScheduleValues.Pending, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
