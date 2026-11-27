using System.Text;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class VoiceInputTextPolicyTests
{
    [Fact]
    public void AppendBoundedCollapsesWhitespaceBetweenRecognizedSegments()
    {
        var target = new StringBuilder("첫 문장");

        var truncated = VoiceInputTextPolicy.AppendBounded(target, "  둘째\r\n  문장  ");

        Assert.False(truncated);
        Assert.Equal("첫 문장 둘째 문장", target.ToString());
    }

    [Fact]
    public void AppendBoundedDoesNotSplitASurrogatePairAtTheLimit()
    {
        var target = new StringBuilder(new string('가', VoiceInputTextPolicy.MaximumCharacters - 2));

        var truncated = VoiceInputTextPolicy.AppendBounded(target, "😀뒤");

        Assert.True(truncated);
        Assert.Equal(VoiceInputTextPolicy.MaximumCharacters - 2, target.Length);
        Assert.False(char.IsHighSurrogate(target[^1]));
    }

    [Fact]
    public void AppendBoundedReportsTruncationWhenTargetIsAlreadyFull()
    {
        var target = new StringBuilder(new string('가', VoiceInputTextPolicy.MaximumCharacters));

        Assert.True(VoiceInputTextPolicy.AppendBounded(target, "추가"));
        Assert.Equal(VoiceInputTextPolicy.MaximumCharacters, target.Length);
    }
}
