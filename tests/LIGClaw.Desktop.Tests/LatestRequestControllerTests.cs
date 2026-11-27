using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class LatestRequestControllerTests
{
    [Fact]
    public void BeginningANewRequestCancelsAndInvalidatesThePreviousRequest()
    {
        using var controller = new LatestRequestController();
        var first = controller.Begin();
        var second = controller.Begin();

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(first.IsCurrent);
        Assert.False(second.CancellationToken.IsCancellationRequested);
        Assert.True(second.IsCurrent);
    }

    [Fact]
    public void DisposalCancelsTheCurrentRequestAndRejectsFurtherWork()
    {
        var controller = new LatestRequestController();
        var request = controller.Begin();

        controller.Dispose();

        Assert.True(request.CancellationToken.IsCancellationRequested);
        Assert.False(request.IsCurrent);
        Assert.Throws<ObjectDisposedException>(() => controller.Begin());
    }
}
