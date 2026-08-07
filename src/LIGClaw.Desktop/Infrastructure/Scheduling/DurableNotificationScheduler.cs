using LIGClaw.Application.Scheduling;

namespace LIGClaw.Desktop.Infrastructure.Scheduling;

internal sealed class DurableNotificationScheduler(
    IScheduleRepository schedules,
    Tools.IUserNotificationService notifications) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null) return;
        var recovery = await schedules.ReconcileOnStartupAsync(DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        if (recovery.AwaitingDecision > 0)
        {
            await TryShowAttentionAsync(recovery.AwaitingDecision, cancellationToken).ConfigureAwait(false);
        }
        await RunDueOnceAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        _loop = RunLoopAsync(_lifetime.Token);
    }

    private async Task TryShowAttentionAsync(int count, CancellationToken cancellationToken)
    {
        try
        {
            await notifications.ShowAsync(
                "놓친 예약 확인 필요",
                $"놓친 예약 {count}개의 실행 여부를 예약 관리에서 선택해 주세요.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 관리 화면에 awaiting_decision 상태가 유지되므로 알림 실패가 scheduler를 중단하지 않는다.
        }
    }

    internal async Task<int> RunDueOnceAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var recovery = await schedules.ReconcileOverdueAsync(
            nowUtc, nowUtc.AddMinutes(-1), cancellationToken).ConfigureAwait(false);
        if (recovery.AwaitingDecision > 0)
            await TryShowAttentionAsync(recovery.AwaitingDecision, cancellationToken).ConfigureAwait(false);
        var executed = 0;
        while (executed < 20)
        {
            var claim = await schedules.TryClaimDueAsync(
                nowUtc,
                nowUtc.AddMinutes(2),
                cancellationToken).ConfigureAwait(false);
            if (claim is null) break;
            try
            {
                using var executionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                executionTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                await notifications.ShowAsync(claim.Job.Title, claim.Job.Message, executionTimeout.Token)
                    .WaitAsync(TimeSpan.FromSeconds(35), cancellationToken).ConfigureAwait(false);
                await schedules.CompleteRunAsync(
                    claim.RunId, DateTimeOffset.UtcNow, true, null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await schedules.CompleteRunAsync(
                    claim.RunId, DateTimeOffset.UtcNow, false, "notification_error", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            executed++;
        }
        return executed;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    _ = await RunDueOnceAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // 한 번의 저장소·알림 장애가 이후 예약 실행을 영구 중단하지 않게 다음 tick에서 재시도한다.
                }
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
        _lifetime.Dispose();
    }
}
