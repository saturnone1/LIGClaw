namespace LIGClaw.Desktop.Tests;

public sealed class SettingsOperationGuardTests
{
    [Fact]
    public void AllowsOnlyOneOperationAndCanBeReusedAfterEnd()
    {
        using var guard = new SettingsOperationGuard();

        Assert.True(guard.TryBegin(out var first));
        Assert.True(guard.IsBusy);
        Assert.False(first.IsCancellationRequested);
        Assert.False(guard.TryBegin(out _));

        guard.End();

        Assert.False(guard.IsBusy);
        Assert.True(guard.TryBegin(out var second));
        Assert.False(second.IsCancellationRequested);
    }

    [Fact]
    public void CancelSignalsOnlyTheActiveOperation()
    {
        using var guard = new SettingsOperationGuard();
        Assert.True(guard.TryBegin(out var first));

        guard.Cancel();

        Assert.True(first.IsCancellationRequested);
        Assert.False(guard.TryBegin(out _));
        guard.End();
        Assert.True(guard.TryBegin(out var second));
        Assert.False(second.IsCancellationRequested);
    }
}
