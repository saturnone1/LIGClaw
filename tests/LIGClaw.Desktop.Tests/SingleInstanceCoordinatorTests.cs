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
        var activation = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationRequested += (_, args) => activation.TrySetResult(args.Payload);

        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);

        secondary.SignalPrimary("bounded activation");

        Assert.Equal("bounded activation", await activation.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void RejectsAnEmptyInstanceIdentifier()
    {
        Assert.Throws<ArgumentException>(() => new SingleInstanceCoordinator(" "));
    }

    [Fact]
    public void SecondaryRejectsAnOversizedActivationPayload()
    {
        var instanceId = $"LIGClaw-Test-{Guid.NewGuid():N}";
        using var primary = new SingleInstanceCoordinator(instanceId);
        using var secondary = new SingleInstanceCoordinator(instanceId);

        Assert.Throws<ArgumentOutOfRangeException>(() => secondary.SignalPrimary(new string('x', 32 * 1024 + 1)));
    }
}
