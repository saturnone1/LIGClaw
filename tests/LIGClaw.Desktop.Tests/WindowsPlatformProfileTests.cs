using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Tests;

public sealed class WindowsPlatformProfileTests
{
    [Fact]
    public void DetectsTheCurrentSupportedWindowsClient()
    {
        var profile = WindowsPlatformDetector.Detect();

        Assert.True(profile.IsWorkstation);
        Assert.True(profile.IsSupported, profile.DisplayName);
    }

    [Theory]
    [InlineData(17763)]
    [InlineData(19045)]
    [InlineData(21999)]
    public void ClassifiesSupportedWindows10Builds(int build)
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, build, isWorkstation: true);

        Assert.Equal(WindowsClientRelease.Windows10, profile.Release);
        Assert.True(profile.Supports(WindowsCapability.Win32DesktopShell));
        Assert.False(profile.Supports(WindowsCapability.Windows11Shell));
    }

    [Theory]
    [InlineData(22000)]
    [InlineData(22631)]
    [InlineData(26100)]
    [InlineData(30000)]
    public void ClassifiesWindows11AndFutureBuildsByCapability(int build)
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, build, isWorkstation: true);

        Assert.Equal(WindowsClientRelease.Windows11OrLater, profile.Release);
        Assert.True(profile.Supports(WindowsCapability.Windows11Shell));
    }

    [Theory]
    [InlineData(17134, true)]
    [InlineData(20348, false)]
    public void RejectsUnsupportedOrServerPlatforms(int build, bool isWorkstation)
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, build, isWorkstation);

        Assert.False(profile.IsSupported);
        Assert.Equal(WindowsCapability.None, profile.Capabilities);
    }
}
