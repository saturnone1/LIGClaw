using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class WindowsToolApprovalPromptTests
{
    [Fact]
    public void ToStringDoesNotExposeDisplayedContentOrGrantScope()
    {
        const string detailSentinel = "MAIL_BODY_SHOULD_ONLY_BE_SHOWN_IN_THE_DIALOG";
        const string scopeSentinel = "mail:one-shot-secret-scope";
        var prompt = new WindowsToolApprovalPrompt(
            "메일을 보낼까요?",
            "승인할 메일",
            detailSentinel,
            "R3",
            GrantScope: scopeSentinel);

        var rendered = prompt.ToString();

        Assert.DoesNotContain(detailSentinel, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(scopeSentinel, rendered, StringComparison.Ordinal);
        Assert.Contains("Risk = R3", rendered, StringComparison.Ordinal);
        Assert.Contains("HasGrantScope = True", rendered, StringComparison.Ordinal);
    }
}
