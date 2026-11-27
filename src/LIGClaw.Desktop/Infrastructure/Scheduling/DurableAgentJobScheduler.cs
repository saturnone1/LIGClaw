using LIGClaw.Application.Scheduling;

namespace LIGClaw.Desktop.Infrastructure.Scheduling;

internal interface IAgentJobExecutor
{
    Task<AgentJobRunResult> ExecuteAsync(AgentJobClaim claim, CancellationToken cancellationToken);
}

internal sealed class DurableAgentJobScheduler(
    IAgentJobRepository jobs,
    IAgentJobExecutor executor,
    Func<AgentJobClaim, AgentJobRunResult, CancellationToken, Task>? onCompleted = null) : IAsyncDisposable
{
    private const int MaximumClaimsPerTick = 10;
    private const int MaximumConcurrentRuns = 2;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _concurrency = new(MaximumConcurrentRuns, MaximumConcurrentRuns);
    private readonly object _sync = new();
    private readonly HashSet<Task> _activeRuns = [];
    private Task? _loop;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null) return;
        await jobs.ReconcileAgentJobsOnStartupAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        _ = await RunDueOnceAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        _loop = RunLoopAsync(_lifetime.Token);
    }

    internal async Task<int> RunDueOnceAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        _ = await jobs.ReconcileOverdueAgentJobsAsync(
            nowUtc, nowUtc.AddMinutes(-1), cancellationToken).ConfigureAwait(false);
        var claimed = 0;
        while (claimed < MaximumClaimsPerTick && _concurrency.Wait(0))
        {
            AgentJobClaim? claim;
            try
            {
                claim = await jobs.TryClaimDueAgentJobAsync(
                    nowUtc, nowUtc.AddHours(1), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _concurrency.Release();
                throw;
            }
            if (claim is null)
            {
                _concurrency.Release();
                break;
            }
            var run = ExecuteClaimAsync(claim, cancellationToken);
            lock (_sync) _activeRuns.Add(run);
            _ = run.ContinueWith(
                completed =>
                {
                    lock (_sync) _activeRuns.Remove(completed);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            claimed++;
        }
        return claimed;
    }

    private async Task ExecuteClaimAsync(AgentJobClaim claim, CancellationToken schedulerCancellation)
    {
        try
        {
            AgentJobRunResult result;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(schedulerCancellation, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(claim.Job.MaxRuntimeSeconds));
            try
            {
                result = await executor.ExecuteAsync(claim, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                result = new AgentJobRunResult(false, ErrorCode: "runtime_timeout");
            }
            catch
            {
                result = new AgentJobRunResult(false, ErrorCode: "runtime_error");
            }
            await jobs.CompleteAgentJobRunAsync(
                claim.RunId, DateTimeOffset.UtcNow, result, CancellationToken.None).ConfigureAwait(false);
            if (onCompleted is not null)
            {
                try { await onCompleted(claim, result, CancellationToken.None).ConfigureAwait(false); }
                catch { /* 완료 알림 실패는 durable 실행 결과를 바꾸지 않는다. */ }
            }
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try { _ = await RunDueOnceAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { /* 다음 tick에서 durable ledger를 다시 확인한다. */ }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        Task[] active;
        lock (_sync) active = [.. _activeRuns];
        if (active.Length > 0) await Task.WhenAll(active).ConfigureAwait(false);
        _concurrency.Dispose();
        _lifetime.Dispose();
    }
}
