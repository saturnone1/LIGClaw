using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class ShellLayoutPolicyTests
{
    [Theory]
    [InlineData(760)]
    [InlineData(899)]
    public void CompactWidthsPreserveThePrimaryContentArea(double width)
    {
        var layout = ShellLayoutPolicy.ForWidth(width);

        Assert.True(layout.IsCompact);
        Assert.Equal(72, layout.AppRailWidth);
        Assert.Equal(16, layout.HorizontalPageMargin);
        Assert.False(layout.ShowRailLabels);
        Assert.True(width - layout.AppRailWidth - (layout.HorizontalPageMargin * 2) >= 656);
    }

    [Theory]
    [InlineData(900)]
    [InlineData(1060)]
    public void WideWidthsShowTheFullNavigationAndStatusPanel(double width)
    {
        var layout = ShellLayoutPolicy.ForWidth(width);

        Assert.False(layout.IsCompact);
        Assert.Equal(216, layout.AppRailWidth);
        Assert.Equal(32, layout.HorizontalPageMargin);
        Assert.True(layout.ShowRailLabels);
    }
}
