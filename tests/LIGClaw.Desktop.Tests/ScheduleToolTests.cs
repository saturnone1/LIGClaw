using System.Globalization;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class ScheduleToolTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LIGClaw.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateRequiresPreviewAndPersistsOnlyTheApprovedNotification()
    {
        using var store = await CreateStoreAsync();
        var tool = new ScheduleCreateTool(store);
        var input = FutureInput();

        var preview = Assert.IsType<WindowsToolApprovalPrompt>(tool.CreateApprovalPrompt(input));
        var result = await tool.ExecuteAsync(input, CancellationToken.None);

        Assert.Contains("회의 알림", preview.Details, StringComparison.Ordinal);
        Assert.NotNull(preview.GrantScope);
        Assert.True(result.Success);
        Assert.Equal("로컬 알림 예약 하나를 저장했어요.", result.ActivitySummary);
        Assert.Equal("pending", result.Output["status"]);
        Assert.Single(await store.ListAsync(false, 0, 10, CancellationToken.None));
    }

    [Fact]
    public async Task CreateRejectsUnknownTimeZonesBeforeApproval()
    {
        using var store = await CreateStoreAsync();
        var input = FutureInput();
        input["timeZoneId"] = "Not/A-Time-Zone";

        Assert.Null(new ScheduleCreateTool(store).CreateApprovalPrompt(input));
    }

    [Fact]
    public async Task CreateDefaultsAnOmittedIntervalAndListDefaultsToActiveOnly()
    {
        using var store = await CreateStoreAsync();
        var input = FutureInput();
        input.Remove("interval");

        Assert.True((await new ScheduleCreateTool(store).ExecuteAsync(input, CancellationToken.None)).Success);
        var list = new ScheduleListTool(store);
        var listInput = new Dictionary<string, object?> { ["reason"] = "활성 예약을 확인하기 위해" };

        Assert.NotNull(list.CreateApprovalPrompt(listInput));
        Assert.True((await list.ExecuteAsync(listInput, CancellationToken.None)).Success);
        Assert.Equal(1, Assert.Single(await store.ListAsync(false, 0, 10, CancellationToken.None)).Interval);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task CreateTreatsNullOrZeroOptionalIntervalAsTheDefault(object? interval)
    {
        using var store = await CreateStoreAsync();
        var input = FutureInput();
        input["interval"] = interval;

        Assert.NotNull(new ScheduleCreateTool(store).CreateApprovalPrompt(input));
        Assert.True((await new ScheduleCreateTool(store).ExecuteAsync(input, CancellationToken.None)).Success);
        Assert.Equal(1, Assert.Single(await store.ListAsync(false, 0, 10, CancellationToken.None)).Interval);
    }

    [Fact]
    public async Task CreateComputesRelativeDelayOnDesktopWithoutModelTimeArithmetic()
    {
        using var store = await CreateStoreAsync();
        var before = DateTimeOffset.UtcNow;
        var input = FutureInput();
        input.Remove("startLocal");
        input.Remove("timeZoneId");
        input.Remove("interval");
        input["delayMinutes"] = 2;
        input["recurrence"] = "once";

        var tool = new ScheduleCreateTool(store);
        var preview = tool.CreateApprovalPrompt(input);
        var repeatedPreview = tool.CreateApprovalPrompt(input);
        var result = await tool.ExecuteAsync(input, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.Equal(preview.GrantScope, repeatedPreview?.GrantScope);
        Assert.True(result.Success);
        var scheduled = Assert.Single(await store.ListAsync(false, 0, 10, CancellationToken.None));
        Assert.InRange(scheduled.NextRunAtUtc!.Value, before.AddMinutes(1.9), before.AddMinutes(2.1));
        Assert.Equal(TimeZoneInfo.Local.Id, scheduled.TimeZoneId);
    }

    [Fact]
    public async Task DesktopHostStoresTheActualConversationAsScheduleSource()
    {
        using var store = await CreateStoreAsync();
        var host = new WindowsToolHost(
            WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true),
            [new ScheduleCreateTool(store)]);
        var invocation = new LIGClaw.Contracts.Generated.ToolInvokeParams(
            "call", "conversation-42", "run", "schedule.create.v1", "R1", FutureInput());

        Assert.True((await host.ExecuteAsync(invocation)).Success);

        Assert.Equal("conversation:conversation-42", Assert.Single(await store.ListAsync(false, 0, 10, CancellationToken.None)).Source);
    }

    [Fact]
    public async Task CancelCanOnlyDeleteTheJobPreparedForApproval()
    {
        using var store = await CreateStoreAsync();
        var create = await new ScheduleCreateTool(store).ExecuteAsync(FutureInput(), CancellationToken.None);
        var id = Assert.IsType<string>(create.Output["jobId"]);
        var cancel = new ScheduleCancelTool(store);
        var input = new Dictionary<string, object?> { ["jobId"] = id, ["reason"] = "사용자가 삭제를 요청했기 때문에" };

        Assert.False((await cancel.ExecuteAsync(input, CancellationToken.None)).Success);
        Assert.NotNull(cancel.CreateApprovalPrompt(input));
        Assert.True((await cancel.ExecuteAsync(input, CancellationToken.None)).Success);
        Assert.Equal(ScheduleValues.Cancelled, (await store.GetAsync(id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task CancelPreviewUsesTheSchedulesTimeZone()
    {
        using var store = await CreateStoreAsync();
        Assert.True(NotificationSchedulePolicy.TryParseLocal("2030-01-15T09:00:00", out var local));
        var created = await store.CreateAsync(
            new NotificationScheduleDraft(
                "뉴욕 알림", "현지 시각 확인", local, "Eastern Standard Time",
                ScheduleValues.Once, 1, ScheduleValues.Skip, "test"),
            DateTimeOffset.Parse("2029-01-01T00:00:00Z"),
            CancellationToken.None);
        var input = new Dictionary<string, object?>
        {
            ["jobId"] = created.Id,
            ["reason"] = "표시 시각을 확인하기 위해",
        };

        var prompt = Assert.IsType<WindowsToolApprovalPrompt>(new ScheduleCancelTool(store).CreateApprovalPrompt(input));

        Assert.Contains("2030-01-15 09:00:00", prompt.Details, StringComparison.Ordinal);
        Assert.Contains("(Eastern Standard Time)", prompt.Details, StringComparison.Ordinal);
    }

    private async Task<ConversationStore> CreateStoreAsync()
    {
        var store = new ConversationStore(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"));
        await store.InitializeAsync();
        return store;
    }

    private static Dictionary<string, object?> FutureInput()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        var futureLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddDays(2), timeZone).DateTime;
        return new Dictionary<string, object?>
        {
            ["title"] = "회의 알림",
            ["message"] = "주간 회의가 시작됩니다.",
            ["startLocal"] = futureLocal.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            ["timeZoneId"] = timeZone.Id,
            ["recurrence"] = "weekly",
            ["interval"] = 1,
            ["misfirePolicy"] = "run_once_on_resume",
            ["reason"] = "사용자가 반복 알림을 요청했기 때문에",
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
