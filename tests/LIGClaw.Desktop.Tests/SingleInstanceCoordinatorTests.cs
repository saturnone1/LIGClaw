using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact(Timeout = 10_000)]
    public async Task SecondaryInstanceSignalsThePrimaryInstance()
    {
        var instanceId = $"LIGClaw-Test-{Guid.NewGuid():N}";
        using var primary = new SingleInstanceCoordinator(instanceId);
        using var secondary = new SingleInstanceCoordinator(instanceId);
        var activation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationRequested += (_, _) => activation.TrySetResult();

        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);

        secondary.SignalPrimary();

        await activation.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RejectsAnEmptyInstanceIdentifier()
    {
        Assert.Throws<ArgumentException>(() => new SingleInstanceCoordinator(" "));
    }
}
