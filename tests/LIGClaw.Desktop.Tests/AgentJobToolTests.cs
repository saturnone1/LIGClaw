using System.Globalization;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class AgentJobToolTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LIGClaw.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateShowsEffectBoundaryAndPersistsBoundedDefaults()
    {
        using var store = await CreateStoreAsync();
        var input = FutureInput();
        var tool = new AgentJobCreateTool(store);

        var preview = Assert.IsType<WindowsToolApprovalPrompt>(tool.CreateApprovalPrompt(input));
        var result = await tool.ExecuteAsync(input, CancellationToken.None);

        Assert.Contains("실행 시점", preview.Details, StringComparison.Ordinal);
        Assert.NotNull(preview.GrantScope);
        Assert.True(result.Success);
        var job = Assert.Single(await store.ListAgentJobsAsync(false, 0, 10, CancellationToken.None));
        Assert.Equal(300, job.MaxRuntimeSeconds);
        Assert.Equal(3, job.MaxAttempts);
        Assert.Equal(20_000, job.ResultMaxCharacters);
    }

    [Fact]
    public async Task CancelExecutesOnlyForTheJobPreparedForApproval()
    {
        using var store = await CreateStoreAsync();
        var created = await new AgentJobCreateTool(store).ExecuteAsync(FutureInput(), CancellationToken.None);
        var id = Assert.IsType<string>(created.Output["jobId"]);
        var cancel = new AgentJobCancelTool(store);
        var input = new Dictionary<string, object?> { ["jobId"] = id, ["reason"] = "사용자가 취소를 요청했기 때문에" };

        Assert.False((await cancel.ExecuteAsync(input, CancellationToken.None)).Success);
        Assert.NotNull(cancel.CreateApprovalPrompt(input));
        Assert.True((await cancel.ExecuteAsync(input, CancellationToken.None)).Success);
        Assert.Equal(ScheduleValues.Cancelled, (await store.GetAgentJobAsync(id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ControlToolPausesAndResumesOnlyThePreparedJobAction()
    {
        using var store = await CreateStoreAsync();
        var created = await new AgentJobCreateTool(store).ExecuteAsync(FutureInput(), CancellationToken.None);
        var id = Assert.IsType<string>(created.Output["jobId"]);
        var tool = new AgentJobControlTool(store);
        var pause = new Dictionary<string, object?> { ["jobId"] = id, ["action"] = "pause", ["reason"] = "잠시 중단" };

        Assert.NotNull(tool.CreateApprovalPrompt(pause));
        Assert.True((await tool.ExecuteAsync(pause, CancellationToken.None)).Success);
        Assert.Equal(ScheduleValues.Paused, (await store.GetAgentJobAsync(id, CancellationToken.None))!.Status);

        var resume = new Dictionary<string, object?> { ["jobId"] = id, ["action"] = "resume", ["reason"] = "다시 진행" };
        Assert.NotNull(tool.CreateApprovalPrompt(resume));
        Assert.True((await tool.ExecuteAsync(resume, CancellationToken.None)).Success);
        Assert.Equal(ScheduleValues.Pending, (await store.GetAgentJobAsync(id, CancellationToken.None))!.Status);
    }

    private async Task<ConversationStore> CreateStoreAsync()
    {
        var store = new ConversationStore(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"));
        await store.InitializeAsync();
        return store;
    }

    private static Dictionary<string, object?> FutureInput()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddHours(1), zone).DateTime;
        return new Dictionary<string, object?>
        {
            ["title"] = "시스템 요약",
            ["prompt"] = "현재 시스템 리소스를 확인하고 요약해 줘.",
            ["startLocal"] = local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            ["timeZoneId"] = zone.Id,
            ["recurrence"] = "once",
            ["misfirePolicy"] = "run_once_on_resume",
            ["reason"] = "사용자가 백그라운드 시스템 요약을 요청했기 때문에",
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
