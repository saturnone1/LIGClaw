using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class TextToSpeechTextPolicyTests
{
    [Fact]
    public void PrepareCollapsesWhitespaceWithoutChangingWords()
    {
        var result = TextToSpeechTextPolicy.Prepare("  첫째\r\n 둘째\t셋째  ", out var prepared);

        Assert.Equal(TextToSpeechStatus.Playing, result.Status);
        Assert.Equal("첫째 둘째 셋째", prepared);
    }

    [Fact]
    public void PrepareRejectsEmptySelection()
    {
        var result = TextToSpeechTextPolicy.Prepare(" \r\n ", out var prepared);

        Assert.Equal(TextToSpeechStatus.Empty, result.Status);
        Assert.Empty(prepared);
    }

    [Fact]
    public void PrepareRejectsOversizedSelectionInsteadOfReadingPartialText()
    {
        var result = TextToSpeechTextPolicy.Prepare(
            new string('가', TextToSpeechTextPolicy.MaximumCharacters + 1),
            out var prepared);

        Assert.Equal(TextToSpeechStatus.TooLong, result.Status);
        Assert.Empty(prepared);
    }

    [Fact]
    public void PrepareAcceptsTheDocumentedMaximum()
    {
        var text = new string('가', TextToSpeechTextPolicy.MaximumCharacters);

        var result = TextToSpeechTextPolicy.Prepare(text, out var prepared);

        Assert.Equal(TextToSpeechStatus.Playing, result.Status);
        Assert.Equal(text, prepared);
    }
}
