using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class VoiceAutoReadPolicyTests
{
    private static readonly VoiceSettings Enabled = new(false, true, 22 * 60, 7 * 60);

    [Theory]
    [InlineData(21, 59, true)]
    [InlineData(22, 0, false)]
    [InlineData(0, 0, false)]
    [InlineData(6, 59, false)]
    [InlineData(7, 0, true)]
    public void OvernightQuietHoursUseInclusiveStartAndExclusiveEnd(int hour, int minute, bool expected)
    {
        Assert.Equal(expected, ShouldRead(Enabled, hour, minute));
    }

    [Theory]
    [InlineData(8, 59, true)]
    [InlineData(9, 0, false)]
    [InlineData(16, 59, false)]
    [InlineData(17, 0, true)]
    public void SameDayQuietHoursAreSupported(int hour, int minute, bool expected)
    {
        Assert.Equal(expected, ShouldRead(new(false, true, 9 * 60, 17 * 60), hour, minute));
    }

    [Fact]
    public void AutomaticReadingRequiresOptInActiveWindowAndIdleAudio()
    {
        Assert.False(ShouldRead(Enabled with { AutoReadEnabled = false }, 12, 0));
        Assert.False(VoiceAutoReadPolicy.ShouldRead(Enabled, At(12, 0), false, false, false, "답변"));
        Assert.False(VoiceAutoReadPolicy.ShouldRead(Enabled, At(12, 0), true, true, false, "답변"));
        Assert.False(VoiceAutoReadPolicy.ShouldRead(Enabled, At(12, 0), true, false, true, "답변"));
        Assert.False(VoiceAutoReadPolicy.ShouldRead(Enabled, At(12, 0), true, false, false, "  "));
    }

    [Fact]
    public void EqualQuietHourBoundariesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => VoiceAutoReadPolicy.Validate(new(false, true, 600, 600)));
    }

    private static bool ShouldRead(VoiceSettings settings, int hour, int minute) =>
        VoiceAutoReadPolicy.ShouldRead(settings, At(hour, minute), true, false, false, "답변");

    private static DateTimeOffset At(int hour, int minute) =>
        new(2026, 8, 2, hour, minute, 0, TimeSpan.FromHours(9));
}
