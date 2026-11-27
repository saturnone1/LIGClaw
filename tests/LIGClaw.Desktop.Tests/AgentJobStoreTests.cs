using LIGClaw.Application.Scheduling;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class AgentJobStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ligclaw-agent-job-{Guid.NewGuid():N}");

    [Fact]
    public async Task ClaimsCompletesAndBoundsDurableAgentJobResult()
    {
        using var store = Store();
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        var created = await store.CreateAgentJobAsync(Draft(now.AddMinutes(1), resultMax: 1_000), now, CancellationToken.None);

        Assert.Null(await store.TryClaimDueAgentJobAsync(now, now.AddMinutes(2), CancellationToken.None));
        var claim = Assert.IsType<AgentJobClaim>(await store.TryClaimDueAgentJobAsync(
            now.AddMinutes(1), now.AddMinutes(6), CancellationToken.None));
        Assert.Equal(created.Id, claim.Job.Id);
        Assert.Equal(1, claim.Attempt);
        await store.CompleteAgentJobRunAsync(
            claim.RunId, now.AddMinutes(2), new AgentJobRunResult(true, new string('x', 1_200)), CancellationToken.None);

        var completed = Assert.IsType<ScheduledAgentJob>(await store.GetAgentJobAsync(created.Id, CancellationToken.None));
        Assert.Equal(ScheduleValues.Completed, completed.Status);
        var run = Assert.Single(await store.ListAgentJobRunsAsync(created.Id, 10, CancellationToken.None));
        Assert.Equal("succeeded", run.Status);
        Assert.Equal(1_001, run.ResultText!.Length);
        Assert.EndsWith("…", run.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRunRetriesWithinConfiguredAttemptLimit()
    {
        using var store = Store();
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        var job = await store.CreateAgentJobAsync(Draft(now.AddMinutes(1), maxAttempts: 2), now, CancellationToken.None);
        var first = Assert.IsType<AgentJobClaim>(await store.TryClaimDueAgentJobAsync(
            now.AddMinutes(1), now.AddMinutes(6), CancellationToken.None));

        await store.CompleteAgentJobRunAsync(
            first.RunId, now.AddMinutes(2), new AgentJobRunResult(false, ErrorCode: "runtime_error"), CancellationToken.None);
        var retrying = Assert.IsType<ScheduledAgentJob>(await store.GetAgentJobAsync(job.Id, CancellationToken.None));
        Assert.Equal(ScheduleValues.Pending, retrying.Status);
        Assert.Equal(now.AddMinutes(7), retrying.NextRunAtUtc);
        var second = Assert.IsType<AgentJobClaim>(await store.TryClaimDueAgentJobAsync(
            now.AddMinutes(7), now.AddMinutes(12), CancellationToken.None));

        await store.CompleteAgentJobRunAsync(
            second.RunId, now.AddMinutes(8), new AgentJobRunResult(false, ErrorCode: "runtime_error"), CancellationToken.None);
        Assert.Equal(ScheduleValues.Failed,
            (await store.GetAgentJobAsync(job.Id, CancellationToken.None))!.Status);
        Assert.Equal(2, (await store.ListAgentJobRunsAsync(job.Id, 10, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task StartupRecoveryMarksRunInterruptedAndRequeuesWithinAttemptLimit()
    {
        var path = Path.Combine(_directory, "jobs.db");
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        string jobId;
        using (var firstStore = new ConversationStore(path))
        {
            await firstStore.InitializeAsync();
            var job = await firstStore.CreateAgentJobAsync(Draft(now.AddMinutes(1)), now, CancellationToken.None);
            jobId = job.Id;
            _ = Assert.IsType<AgentJobClaim>(await firstStore.TryClaimDueAgentJobAsync(
                now.AddMinutes(1), now.AddMinutes(6), CancellationToken.None));
        }
        using var reopened = new ConversationStore(path);
        await reopened.InitializeAsync();

        await reopened.ReconcileAgentJobsOnStartupAsync(now.AddMinutes(3), CancellationToken.None);

        var recovered = Assert.IsType<ScheduledAgentJob>(await reopened.GetAgentJobAsync(jobId, CancellationToken.None));
        Assert.Equal(ScheduleValues.Pending, recovered.Status);
        Assert.Equal(now.AddMinutes(3), recovered.NextRunAtUtc);
        Assert.Equal("interrupted", Assert.Single(await reopened.ListAgentJobRunsAsync(jobId, 10, CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData("skip", "completed")]
    [InlineData("ask", "awaiting_decision")]
    [InlineData("run_once_on_resume", "pending")]
    public async Task ReconcilesOverdueJobsByDeclaredMisfirePolicy(string policy, string expectedStatus)
    {
        using var store = Store();
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        var job = await store.CreateAgentJobAsync(
            Draft(now.AddMinutes(1)) with { MisfirePolicy = policy }, now, CancellationToken.None);

        var recovery = await store.ReconcileOverdueAgentJobsAsync(
            now.AddMinutes(5), now.AddMinutes(4), CancellationToken.None);

        Assert.Equal(expectedStatus, (await store.GetAgentJobAsync(job.Id, CancellationToken.None))!.Status);
        Assert.Equal(policy == "skip" ? 1 : 0, recovery.Skipped);
        Assert.Equal(policy == "ask" ? 1 : 0, recovery.AwaitingDecision);
        Assert.Equal(policy == "run_once_on_resume" ? 1 : 0, recovery.ReadyToRun);
    }

    [Fact]
    public async Task UserCanResolveAnAskMisfireWithoutExecutingIt()
    {
        using var store = Store();
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        var job = await store.CreateAgentJobAsync(
            Draft(now.AddMinutes(1)) with { MisfirePolicy = ScheduleValues.Ask }, now, CancellationToken.None);
        _ = await store.ReconcileOverdueAgentJobsAsync(now.AddMinutes(5), now.AddMinutes(4), CancellationToken.None);

        Assert.True(await store.ResolveAgentJobMisfireAsync(job.Id, false, now.AddMinutes(5), CancellationToken.None));
        Assert.Equal(ScheduleValues.Completed, (await store.GetAgentJobAsync(job.Id, CancellationToken.None))!.Status);
        Assert.Equal("skipped", Assert.Single(await store.ListAgentJobRunsAsync(job.Id, 10, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Paused_job_is_not_claimed_and_can_resume_or_retry_after_completion()
    {
        using var store = Store();
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        var job = await store.CreateAgentJobAsync(Draft(now.AddMinutes(1)), now, CancellationToken.None);

        Assert.True(await store.SetAgentJobPausedAsync(job.Id, true, now, CancellationToken.None));
        Assert.Equal(ScheduleValues.Paused, (await store.GetAgentJobAsync(job.Id, CancellationToken.None))!.Status);
        Assert.Null(await store.TryClaimDueAgentJobAsync(now.AddHours(1), now.AddHours(2), CancellationToken.None));
        Assert.True(await store.SetAgentJobPausedAsync(job.Id, false, now.AddHours(1), CancellationToken.None));
        var claim = Assert.IsType<AgentJobClaim>(await store.TryClaimDueAgentJobAsync(now.AddHours(1), now.AddHours(2), CancellationToken.None));
        await store.CompleteAgentJobRunAsync(claim.RunId, now.AddHours(1), new AgentJobRunResult(true, "done"), CancellationToken.None);
        Assert.True(await store.RetryAgentJobNowAsync(job.Id, now.AddHours(2), CancellationToken.None));
        Assert.Equal(ScheduleValues.Pending, (await store.GetAgentJobAsync(job.Id, CancellationToken.None))!.Status);
    }

    private ConversationStore Store() => new(Path.Combine(_directory, "jobs.db"));

    private static AgentJobDraft Draft(DateTimeOffset runAtUtc, int maxAttempts = 3, int resultMax = 10_000) => new(
        "문서 요약", "승인된 로컬 문서를 읽고 요약해 줘",
        TimeZoneInfo.ConvertTime(runAtUtc, TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time")).DateTime,
        "Korea Standard Time", ScheduleValues.Once, 1, ScheduleValues.RunOnceOnResume,
        null, 300, maxAttempts, resultMax, "test");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
