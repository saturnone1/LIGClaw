using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Scheduling;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class DurableNotificationSchedulerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LIGClaw.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ClaimsPersistsAndCompletesADueNotification()
    {
        using var store = new ConversationStore(Path.Combine(_directory, "scheduler.db"));
        await store.InitializeAsync();
        var dueAt = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        Assert.True(NotificationSchedulePolicy.TryParseLocal("2026-07-26T09:00:00", out var local));
        var job = await store.Schedules.CreateAsync(
            new NotificationScheduleDraft(
                "회의 알림", "회의가 시작됩니다.", local, "Korea Standard Time",
                ScheduleValues.Once, 1, ScheduleValues.RunOnceOnResume, "test"),
            dueAt.AddDays(-1),
            CancellationToken.None);
        var notifications = new FakeNotificationService();
        await using var scheduler = new DurableNotificationScheduler(store.Schedules, notifications);

        var count = await scheduler.RunDueOnceAsync(dueAt, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(("회의 알림", "회의가 시작됩니다."), Assert.Single(notifications.Shown));
        Assert.Equal(ScheduleValues.Completed, (await store.Schedules.GetAsync(job.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task NotificationFailureIsPersistedWithoutStoppingTheSchedulerPass()
    {
        using var store = new ConversationStore(Path.Combine(_directory, "scheduler-failure.db"));
        await store.InitializeAsync();
        var dueAt = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        Assert.True(NotificationSchedulePolicy.TryParseLocal("2026-07-26T09:00:00", out var local));
        var job = await store.Schedules.CreateAsync(
            new NotificationScheduleDraft(
                "회의 알림", "회의가 시작됩니다.", local, "Korea Standard Time",
                ScheduleValues.Once, 1, ScheduleValues.RunOnceOnResume, "test"),
            dueAt.AddDays(-1),
            CancellationToken.None);
        await using var scheduler = new DurableNotificationScheduler(store.Schedules, new ThrowingNotificationService());

        var count = await scheduler.RunDueOnceAsync(dueAt, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(ScheduleValues.Failed, (await store.Schedules.GetAsync(job.Id, CancellationToken.None))!.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeNotificationService : IUserNotificationService
    {
        public List<(string Title, string Message)> Shown { get; } = [];

        public Task ShowAsync(string title, string message, CancellationToken cancellationToken)
        {
            Shown.Add((title, message));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingNotificationService : IUserNotificationService
    {
        public Task ShowAsync(string title, string message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("notification unavailable");
    }
}
