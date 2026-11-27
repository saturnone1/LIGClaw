using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class MailCreateDraftToolTests
{
    [Fact]
    public async Task CreatesOnlyBoundedMetadataForAPlainTextDraft()
    {
        const string subject = "주간 보고";
        const string body = "첫 줄\r\n둘째 줄";
        var result = await new MailCreateDraftTool().ExecuteAsync(
            Input(subject, body),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(true, result.Output["ready"]);
        Assert.Matches("^sha256:[0-9a-f]{64}$", Assert.IsType<string>(result.Output["draftHash"]));
        Assert.Equal(3L, result.Output["recipientCount"]);
        Assert.Equal(5L, result.Output["subjectLength"]);
        Assert.Equal(8L, result.Output["bodyLength"]);
        Assert.DoesNotContain(subject, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(body, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsTransportSenderAttachmentAndInvalidContentWithoutEchoingIt()
    {
        const string canary = "raw-mail-canary";
        var extra = new Dictionary<string, object?>(Input("제목", "본문"), StringComparer.Ordinal)
        {
            ["sender"] = "sender@example.test",
            ["endpoint"] = "https://mail.example.test",
            ["token"] = canary,
            ["attachments"] = Array.Empty<object>(),
        };
        var invalidBody = Input("제목", $"본문\0{canary}");

        var extraResult = await new MailCreateDraftTool().ExecuteAsync(extra, CancellationToken.None);
        var invalidResult = await new MailCreateDraftTool().ExecuteAsync(invalidBody, CancellationToken.None);

        Assert.False(extraResult.Success);
        Assert.Equal("mail.draft.input_invalid", extraResult.ErrorCode);
        Assert.False(invalidResult.Success);
        Assert.StartsWith("mail.draft.", invalidResult.ErrorCode, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, extraResult.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(canary, invalidResult.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryToolPathDeniesTheWorkflowOnlyDraftAdapter()
    {
        var host = Host();
        var invocation = Invocation(Input("제목", "본문"));

        var direct = await host.ExecuteAsync(invocation);
        var workflow = await host.ExecuteWorkflowObservationAsync(invocation);

        Assert.False(direct.Success);
        Assert.Contains("작업 흐름", direct.Error, StringComparison.Ordinal);
        Assert.True(workflow.Success, workflow.Error);
    }

    private static IReadOnlyDictionary<string, object?> Input(string subject, string body) =>
        new Dictionary<string, object?>
        {
            ["to"] = new[] { "owner@example.test" },
            ["cc"] = new[] { "reviewer@example.test" },
            ["bcc"] = new[] { "audit@example.test" },
            ["subject"] = subject,
            ["body"] = body,
        };

    private static ToolInvokeParams Invocation(IReadOnlyDictionary<string, object?> input) =>
        new("mail-draft-call", "conversation", "run", "mail.create_draft.v1", "R0", input);

    private static WindowsToolHost Host() =>
        new(
            WindowsPlatformProfile.Classify(10, 0, 22_631, isWorkstation: true),
            [new MailCreateDraftTool()]);
}
