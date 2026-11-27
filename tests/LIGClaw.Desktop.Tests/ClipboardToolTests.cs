using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class ClipboardToolTests
{
    [Fact]
    public async Task ReadRequiresApprovalAndBoundsSensitiveText()
    {
        var clipboard = new FakeClipboardTextService { Text = new string('x', 40_000) };
        var host = Host(new ClipboardReadTextTool(clipboard));
        var invocation = new ToolInvokeParams(
            "read", "conversation", "run", "clipboard.read_text.v1", "R1",
            new Dictionary<string, object?> { ["reason"] = "사용자가 붙여넣은 내용을 요청했기 때문에" });

        var preview = host.CreateApprovalPrompt(invocation);
        var result = await host.ExecuteAsync(invocation);

        Assert.True(preview.Success);
        Assert.Contains("모델 요청의 컨텍스트", preview.Prompt!.Details, StringComparison.Ordinal);
        Assert.True(result.Success);
        Assert.Equal(32_768, result.Output["length"]);
        Assert.Equal(true, result.Output["truncated"]);
    }

    [Fact]
    public async Task WriteShowsABoundedPreviewAndRevalidatesApprovedText()
    {
        var clipboard = new FakeClipboardTextService();
        var host = Host(new ClipboardWriteTextTool(clipboard));
        var approved = new string('a', 300);
        var invocation = WriteInvocation(approved);

        var preview = host.CreateApprovalPrompt(invocation);
        var changed = WriteInvocation("changed");
        var result = await host.ExecuteAsync(changed);

        Assert.True(preview.Success);
        Assert.Contains("…", preview.Prompt!.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(approved, preview.Prompt.Details, StringComparison.Ordinal);
        Assert.False(result.Success);
        Assert.Equal(string.Empty, clipboard.Text);
    }

    [Fact]
    public async Task WritePreservesLeadingAndTrailingWhitespace()
    {
        var clipboard = new FakeClipboardTextService();
        var host = Host(new ClipboardWriteTextTool(clipboard));
        var invocation = WriteInvocation("  exact text  \r\n");

        Assert.True(host.CreateApprovalPrompt(invocation).Success);
        Assert.True((await host.ExecuteAsync(invocation)).Success);

        Assert.Equal("  exact text  \r\n", clipboard.Text);
    }

    private static ToolInvokeParams WriteInvocation(string text) =>
        new(
            "write", "conversation", "run", "clipboard.write_text.v1", "R1",
            new Dictionary<string, object?> { ["text"] = text, ["reason"] = "사용자가 복사를 요청했기 때문에" });

    private static WindowsToolHost Host(IWindowsToolAdapter adapter) =>
        new(WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true), [adapter]);

    private sealed class FakeClipboardTextService : IClipboardTextService
    {
        public string Text { get; set; } = string.Empty;
        public Task<string> ReadTextAsync(CancellationToken cancellationToken) => Task.FromResult(Text);
        public Task WriteTextAsync(string text, CancellationToken cancellationToken)
        {
            Text = text;
            return Task.CompletedTask;
        }
    }
}
