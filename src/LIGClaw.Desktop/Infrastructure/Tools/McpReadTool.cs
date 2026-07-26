using System.Text.Json;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class McpReadTool(
    Func<McpConnectionSettings?> loadSettings,
    Func<McpCallParams, CancellationToken, Task<McpCallResult>> call) : IWindowsToolAdapter
{
    public string Name => "mcp.read.v1";
    public string Risk => "R3";
    public WindowsCapability RequiredCapabilities => WindowsCapability.None;
    public int Priority => 0;
    public TimeSpan Timeout => TimeSpan.FromSeconds(35);

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        if (!TryRead(input, out var connectionId, out var toolName, out var arguments, out var reason) ||
            !TryAuthorize(connectionId, toolName, out var settings)) return null;
        var argumentPreview = JsonSerializer.Serialize(arguments);
        if (argumentPreview.Length > 4_000) argumentPreview = string.Concat(argumentPreview.AsSpan(0, 4_000), "…");
        return new WindowsToolApprovalPrompt(
            "외부 MCP 서버에 요청할까요?",
            $"{settings.DisplayName}의 {toolName} 도구를 호출합니다.",
            $"서버: {settings.Url}\n도구: {toolName}\n요청 이유: {reason}\n전송 인자: {argumentPreview}\n\n외부 서버의 결과는 신뢰할 수 없는 콘텐츠로 처리됩니다.",
            "R3");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        if (!TryRead(input, out var connectionId, out var toolName, out var arguments, out _) ||
            !TryAuthorize(connectionId, toolName, out var settings))
            return Failure("허용된 읽기 전용 MCP 도구가 아니에요.");
        var result = await call(new McpCallParams(connectionId, toolName, arguments), cancellationToken)
            .ConfigureAwait(false);
        if (result.IsError) return Failure("MCP 서버가 요청 실패를 반환했어요.");
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?>
            {
                ["text"] = result.Text,
                ["source"] = settings.DisplayName,
                ["truncated"] = result.Truncated,
            },
            ActivitySummary: $"{settings.DisplayName}에서 읽기 전용 정보를 조회했어요.");
    }

    private bool TryAuthorize(string connectionId, string toolName, out McpConnectionSettings settings)
    {
        settings = loadSettings()!;
        return settings is not null && settings.Enabled &&
               StringComparer.Ordinal.Equals(connectionId, "knowledge") &&
               settings.AllowedTools.Contains(toolName, StringComparer.Ordinal);
    }

    private static bool TryRead(
        IReadOnlyDictionary<string, object?> input,
        out string connectionId,
        out string toolName,
        out IReadOnlyDictionary<string, object?> arguments,
        out string reason)
    {
        connectionId = string.Empty;
        toolName = string.Empty;
        reason = string.Empty;
        arguments = new Dictionary<string, object?>();
        if (!ToolInputReader.HasOnlyKeys(input, "connectionId", "toolName", "arguments", "reason") ||
            !ToolInputReader.TryGetRequiredString(input, "connectionId", 32, out connectionId) ||
            !ToolInputReader.TryGetRequiredString(input, "toolName", 128, out toolName) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out reason) ||
            !input.TryGetValue("arguments", out var raw)) return false;
        arguments = raw switch
        {
            IReadOnlyDictionary<string, object?> dictionary => dictionary,
            JsonElement { ValueKind: JsonValueKind.Object } element =>
                JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText()) ?? new Dictionary<string, object?>(),
            _ => new Dictionary<string, object?>(),
        };
        try
        {
            return raw is IReadOnlyDictionary<string, object?> or JsonElement &&
                   JsonSerializer.SerializeToUtf8Bytes(arguments).Length <= 32_768;
        }
        catch
        {
            return false;
        }
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}
