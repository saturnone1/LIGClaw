using Microsoft.Win32;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record McpConnectionSettings(
    string DisplayName,
    string Url,
    bool Enabled,
    IReadOnlyList<string> AllowedTools,
    string? AuthorizationToken = null,
    string Transport = "streamable_http");

internal sealed record McpConnectionStatus(
    string State,
    int ToolCount,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> AllowedTools)
{
    public bool IsConnected => StringComparer.Ordinal.Equals(State, "connected");
}

internal static class McpConnectionPolicy
{
    public static bool TryValidate(string url, out string normalizedUrl, out string error)
    {
        normalizedUrl = url.Trim().TrimEnd('/');
        error = string.Empty;
        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            error = "MCP 주소를 http:// 또는 https://로 시작해 입력해 주세요.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "MCP 주소에는 사용자 정보나 # 조각을 넣을 수 없어요.";
            return false;
        }

        var isLocal = uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme == Uri.UriSchemeHttp && !isLocal)
        {
            error = "원격 MCP 서버는 HTTPS만 사용할 수 있어요. HTTP는 이 PC의 localhost 연결만 허용됩니다.";
            return false;
        }

        return true;
    }
}

internal sealed class McpConnectionSettingsStore
{
    private const string SettingsKeyPath = @"Software\LIGClaw\Mcp\Knowledge";
    private const string CredentialTarget = "LIGClaw/Mcp/Knowledge/BearerToken";

    public McpConnectionSettings? Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
        var url = key?.GetValue("Url") as string;
        if (string.IsNullOrWhiteSpace(url)) return null;
        var displayName = key?.GetValue("DisplayName") as string;
        var enabled = key?.GetValue("Enabled") is not int value || value != 0;
        return new McpConnectionSettings(
            string.IsNullOrWhiteSpace(displayName) ? "지식 검색" : displayName,
            url,
            enabled,
            ParseAllowedTools(key?.GetValue("AllowedTools") as string),
            WindowsCredentialStore.Read(CredentialTarget),
            key?.GetValue("Transport") as string == "stdio" ? "stdio" : "streamable_http");
    }

    public void Save(McpConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
        var previousValues = new Dictionary<string, object?>
        {
            ["DisplayName"] = key.GetValue("DisplayName"),
            ["Url"] = key.GetValue("Url"),
            ["Enabled"] = key.GetValue("Enabled"),
            ["AllowedTools"] = key.GetValue("AllowedTools"),
            ["Transport"] = key.GetValue("Transport"),
        };
        var previousCredential = WindowsCredentialStore.Read(CredentialTarget);
        try
        {
            McpCredentialPolicy.Apply(
                settings.Transport,
                settings.AuthorizationToken,
                secret => WindowsCredentialStore.Write(CredentialTarget, secret),
                () => WindowsCredentialStore.Delete(CredentialTarget));
            key.SetValue("DisplayName", settings.DisplayName, RegistryValueKind.String);
            key.SetValue("Url", settings.Url, RegistryValueKind.String);
            key.SetValue("Enabled", settings.Enabled ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("AllowedTools", string.Join("\n", settings.AllowedTools), RegistryValueKind.String);
            key.SetValue("Transport", settings.Transport, RegistryValueKind.String);
        }
        catch (Exception saveException)
        {
            var rollbackFailures = new List<Exception>();
            foreach (var (name, value) in previousValues)
            {
                try { RestoreValue(key, name, value); }
                catch (Exception exception) { rollbackFailures.Add(exception); }
            }
            try
            {
                if (previousCredential is null) WindowsCredentialStore.Delete(CredentialTarget);
                else WindowsCredentialStore.Write(CredentialTarget, previousCredential);
            }
            catch (Exception exception)
            {
                rollbackFailures.Add(exception);
            }
            if (rollbackFailures.Count > 0)
                throw new AggregateException("MCP 연결 설정 저장과 이전 상태 복원에 실패했습니다.", [saveException, .. rollbackFailures]);
            throw;
        }
    }

    private static void RestoreValue(RegistryKey key, string name, object? value)
    {
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }
        key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
    }

    private static IReadOnlyList<string> ParseAllowedTools(string? value) =>
        (value ?? string.Empty)
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Take(32)
            .ToArray();
}

internal static class McpCredentialPolicy
{
    public static void Apply(string transport, string? token, Action<string> write, Action delete)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(delete);
        if (StringComparer.Ordinal.Equals(transport, "streamable_http") && !string.IsNullOrWhiteSpace(token))
            write(token);
        else
            delete();
    }
}
