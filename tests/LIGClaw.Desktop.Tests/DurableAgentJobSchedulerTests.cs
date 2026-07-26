using LIGClaw.Application.Scheduling;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Scheduling;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class DurableAgentJobSchedulerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ligclaw-agent-scheduler-{Guid.NewGuid():N}");

    [Fact]
    public async Task ClaimsDueJobAndPersistsExecutorResult()
    {
        using var store = new ConversationStore(Path.Combine(_directory, "jobs.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var job = await store.CreateAgentJobAsync(Draft(now.AddSeconds(1)), now.AddMinutes(-1), CancellationToken.None);
        var executor = new RecordingExecutor();
        await using var scheduler = new DurableAgentJobScheduler(store, executor);

        Assert.Equal(1, await scheduler.RunDueOnceAsync(now.AddSeconds(2), CancellationToken.None));
        await executor.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(async () => (await store.ListAgentJobRunsAsync(job.Id, 10, CancellationToken.None)).Single().Status == "succeeded");

        var run = Assert.Single(await store.ListAgentJobRunsAsync(job.Id, 10, CancellationToken.None));
        Assert.Equal("완료된 분석", run.ResultText);
        Assert.Equal(job.Id, Assert.Single(executor.Claims).Job.Id);
    }

    [Fact]
    public async Task NeverRunsMoreThanTwoJobsConcurrently()
    {
        using var store = new ConversationStore(Path.Combine(_directory, "jobs.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 3; index++)
            await store.CreateAgentJobAsync(Draft(now.AddSeconds(1)) with { Title = $"작업 {index}" }, now.AddMinutes(-1), CancellationToken.None);
        var executor = new BlockingExecutor();
        await using var scheduler = new DurableAgentJobScheduler(store, executor);

        Assert.Equal(2, await scheduler.RunDueOnceAsync(now.AddSeconds(2), CancellationToken.None));
        await executor.TwoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, executor.MaximumConcurrent);
        Assert.Equal(0, await scheduler.RunDueOnceAsync(now.AddSeconds(2), CancellationToken.None));
        executor.Release.TrySetResult();
    }

    [Fact]
    public async Task CompletionNotificationFailureDoesNotChangeDurableResult()
    {
        using var store = new ConversationStore(Path.Combine(_directory, "notification-failure.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var job = await store.CreateAgentJobAsync(Draft(now.AddSeconds(1)), now.AddMinutes(-1), CancellationToken.None);
        var executor = new RecordingExecutor();
        await using var scheduler = new DurableAgentJobScheduler(
            store,
            executor,
            (_, _, _) => Task.FromException(new InvalidOperationException("notification unavailable")));

        Assert.Equal(1, await scheduler.RunDueOnceAsync(now.AddSeconds(2), CancellationToken.None));
        await executor.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(async () => (await store.ListAgentJobRunsAsync(job.Id, 10, CancellationToken.None)).Single().Status == "succeeded");

        Assert.Equal("완료된 분석", Assert.Single(await store.ListAgentJobRunsAsync(job.Id, 10, CancellationToken.None)).ResultText);
    }

    private static AgentJobDraft Draft(DateTimeOffset utc) => new(
        "분석", "로컬 문서를 분석해 줘",
        TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time")).DateTime,
        "Korea Standard Time", ScheduleValues.Once, 1, ScheduleValues.RunOnceOnResume,
        null, 30, 2, 10_000, "test");

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class RecordingExecutor : IAgentJobExecutor
    {
        public List<AgentJobClaim> Claims { get; } = [];
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AgentJobRunResult> ExecuteAsync(AgentJobClaim claim, CancellationToken cancellationToken)
        {
            Claims.Add(claim);
            Completed.TrySetResult();
            return Task.FromResult(new AgentJobRunResult(true, "완료된 분석"));
        }
    }

    private sealed class BlockingExecutor : IAgentJobExecutor
    {
        private int _concurrent;
        private int _started;
        public int MaximumConcurrent { get; private set; }
        public TaskCompletionSource TwoStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AgentJobRunResult> ExecuteAsync(AgentJobClaim claim, CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref _concurrent);
            MaximumConcurrent = Math.Max(MaximumConcurrent, concurrent);
            if (Interlocked.Increment(ref _started) == 2) TwoStarted.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                return new AgentJobRunResult(true, "done");
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }
}
