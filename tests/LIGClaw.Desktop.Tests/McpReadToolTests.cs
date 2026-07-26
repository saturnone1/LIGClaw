using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class McpReadToolTests
{
    private static readonly McpConnectionSettings Settings = new(
        "지식 검색",
        "https://knowledge.example/mcp",
        true,
        ["search"]);

    [Fact]
    public async Task AllowlistedCallShowsExternalDestinationAndReturnsBoundedResult()
    {
        McpCallParams? sent = null;
        var tool = new McpReadTool(
            () => Settings,
            (request, _) =>
            {
                sent = request;
                return Task.FromResult(new McpCallResult("정책 문서", false, false));
            });
        var input = Input("search");

        var preview = tool.CreateApprovalPrompt(input);
        var result = await tool.ExecuteAsync(input, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.Contains("https://knowledge.example/mcp", preview.Details, StringComparison.Ordinal);
        Assert.Equal("R3", tool.Risk);
        Assert.True(result.Success);
        Assert.Equal("search", sent?.ToolName);
        Assert.Equal("정책 문서", result.Output["text"]);
        Assert.Equal("지식 검색", result.Output["source"]);
    }

    [Fact]
    public async Task ToolThatWasOnlyDiscoveredButNotAllowlistedIsRejected()
    {
        var called = false;
        var tool = new McpReadTool(
            () => Settings,
            (_, _) =>
            {
                called = true;
                return Task.FromResult(new McpCallResult("", false, false));
            });

        Assert.Null(tool.CreateApprovalPrompt(Input("delete-all")));
        var result = await tool.ExecuteAsync(Input("delete-all"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(called);
    }

    private static IReadOnlyDictionary<string, object?> Input(string toolName) =>
        new Dictionary<string, object?>
        {
            ["connectionId"] = "knowledge",
            ["toolName"] = toolName,
            ["arguments"] = new Dictionary<string, object?> { ["query"] = "policy" },
            ["reason"] = "사용자가 정책 검색을 요청했기 때문에",
        };
}
