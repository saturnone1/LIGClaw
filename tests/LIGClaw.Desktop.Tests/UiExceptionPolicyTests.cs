using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class UiExceptionPolicyTests
{
    [Fact]
    public void RecoversOrdinaryUiEventFailuresButNotFatalRuntimeFailures()
    {
        Assert.True(UiExceptionPolicy.CanRecover(new InvalidOperationException("event failed")));
        Assert.True(UiExceptionPolicy.CanRecover(new IOException("resource unavailable")));
        Assert.False(UiExceptionPolicy.CanRecover(new OutOfMemoryException()));
        Assert.False(UiExceptionPolicy.CanRecover(new AccessViolationException()));
        Assert.False(UiExceptionPolicy.CanRecover(new BadImageFormatException()));
    }
}
