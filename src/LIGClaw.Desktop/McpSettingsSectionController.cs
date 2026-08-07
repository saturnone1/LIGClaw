using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop;

internal sealed record McpSettingsInput(
    string DisplayName,
    string Url,
    bool Enabled,
    string AllowedTools,
    string? AuthorizationToken,
    bool UseSampleRag);

internal sealed record McpSettingsSaveResult(
    McpConnectionSettings Settings,
    McpConnectionStatus Status);

internal sealed class McpSettingsSectionController(
    IMcpConnectionSettingsStore store,
    Func<McpConnectionSettings, CancellationToken, Task<McpConnectionStatus>> apply)
{
    public McpConnectionSettings? Load() => store.Load();

    public async Task<McpSettingsSaveResult?> SaveAsync(
        McpSettingsInput input,
        CancellationToken cancellationToken)
    {
        if (!input.UseSampleRag && string.IsNullOrWhiteSpace(input.Url))
        {
            if (input.Enabled)
                throw new SettingsSectionValidationException("사용할 MCP 서버 주소를 입력해 주세요.");
            return null;
        }

        var settings = Validate(input, store.Load());
        store.Save(settings);
        var status = await apply(settings, cancellationToken).ConfigureAwait(false);
        return new McpSettingsSaveResult(settings, status);
    }

    public static McpConnectionSettings Validate(McpSettingsInput input, McpConnectionSettings? existing)
    {
        var displayName = input.DisplayName.Trim();
        if (string.IsNullOrWhiteSpace(displayName)) displayName = "지식 검색";
        var url = input.UseSampleRag ? "stdio://ligclaw-sample-rag" : NormalizeUrl(input.Url);
        var allowedTools = input.AllowedTools
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (input.UseSampleRag && allowedTools.Length == 0)
            allowedTools = ["search_knowledge", "get_document"];
        if (allowedTools.Length > 32 || allowedTools.Any(name => name.Length > 128))
            throw new SettingsSectionValidationException("허용할 MCP 도구는 이름 32개까지 입력할 수 있어요.");
        var token = string.IsNullOrWhiteSpace(input.AuthorizationToken)
            ? existing?.AuthorizationToken
            : input.AuthorizationToken;
        return new McpConnectionSettings(
            displayName,
            url,
            input.Enabled,
            allowedTools,
            input.UseSampleRag ? null : token,
            input.UseSampleRag ? "stdio" : "streamable_http");
    }

    private static string NormalizeUrl(string value)
    {
        if (!McpConnectionPolicy.TryValidate(value, out var url, out var error))
            throw new SettingsSectionValidationException(error);
        return url;
    }
}
