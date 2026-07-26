using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class ConversationViewportPolicyTests
{
    [Theory]
    [InlineData(100, 200, 0)]
    [InlineData(1000, 400, 600)]
    [InlineData(1000, 400, 568)]
    public void FollowsStreamingOutputWhenTheReaderIsAtTheEnd(
        double extent,
        double viewport,
        double offset) =>
        Assert.True(ConversationViewportPolicy.ShouldFollowOutput(extent, viewport, offset));

    [Theory]
    [InlineData(1000, 400, 0)]
    [InlineData(1000, 400, 500)]
    public void PreservesTheReadersPositionWhenTheyScrolledUp(
        double extent,
        double viewport,
        double offset) =>
        Assert.False(ConversationViewportPolicy.ShouldFollowOutput(extent, viewport, offset));

    [Fact]
    public void NewContentIndicatorAppearsOnlyWhenTheReaderRemainsAwayFromTheEnd()
    {
        Assert.True(ConversationViewportPolicy.ShouldShowNewContentIndicator(
            followOutput: false, preservedVerticalOffset: 120, extentHeight: 1_000, viewportHeight: 400, verticalOffset: 120));
        Assert.False(ConversationViewportPolicy.ShouldShowNewContentIndicator(
            followOutput: true, preservedVerticalOffset: null, extentHeight: 1_000, viewportHeight: 400, verticalOffset: 600));
        Assert.False(ConversationViewportPolicy.ShouldShowNewContentIndicator(
            followOutput: false, preservedVerticalOffset: 600, extentHeight: 1_000, viewportHeight: 400, verticalOffset: 600));
    }

    [Theory]
    [InlineData(1_000, 50)]
    [InlineData(32_768, 100)]
    [InlineData(131_072, 180)]
    public void LongTranscriptsUseABoundedLowerRenderFrequency(int length, int expectedMilliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds),
            ConversationViewportPolicy.RenderIntervalForLength(length));
}
