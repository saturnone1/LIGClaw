namespace LIGClaw.Desktop.Infrastructure.Shell;

internal readonly record struct ShellLayoutMetrics(
    bool IsCompact,
    double AppRailWidth,
    double HorizontalPageMargin,
    bool ShowRailLabels);

internal static class ShellLayoutPolicy
{
    internal const double CompactBreakpoint = 900;

    internal static ShellLayoutMetrics ForWidth(double width)
    {
        var compact = width < CompactBreakpoint;
        return compact
            ? new ShellLayoutMetrics(true, 72, 16, false)
            : new ShellLayoutMetrics(false, 216, 32, true);
    }
}
