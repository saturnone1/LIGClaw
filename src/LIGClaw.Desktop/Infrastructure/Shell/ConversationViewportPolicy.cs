namespace LIGClaw.Desktop.Infrastructure.Shell;

internal static class ConversationViewportPolicy
{
    internal const double FollowThreshold = 32;
    private const int MediumTranscriptLength = 32 * 1024;
    private const int LargeTranscriptLength = 128 * 1024;
    private static readonly TimeSpan NormalRenderInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MediumRenderInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LargeRenderInterval = TimeSpan.FromMilliseconds(180);

    internal static bool ShouldFollowOutput(double extentHeight, double viewportHeight, double verticalOffset)
    {
        if (extentHeight <= viewportHeight) return true;
        var remaining = extentHeight - viewportHeight - verticalOffset;
        return remaining <= FollowThreshold;
    }

    internal static bool ShouldShowNewContentIndicator(
        bool followOutput,
        double? preservedVerticalOffset,
        double extentHeight,
        double viewportHeight,
        double verticalOffset) =>
        !followOutput &&
        preservedVerticalOffset.HasValue &&
        !ShouldFollowOutput(extentHeight, viewportHeight, verticalOffset);

    internal static TimeSpan RenderIntervalForLength(int characterCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(characterCount);
        if (characterCount >= LargeTranscriptLength) return LargeRenderInterval;
        if (characterCount >= MediumTranscriptLength) return MediumRenderInterval;
        return NormalRenderInterval;
    }
}
