using Microsoft.Win32;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record WebSearchSettings(string UrlTemplate);

internal static class WebSearchSettingsPolicy
{
    internal static bool TryValidate(string? value, out WebSearchSettings? settings, out string error)
    {
        var template = value?.Trim() ?? string.Empty;
        settings = null;
        error = string.Empty;
        if (template.Length == 0) return true;
        if (template.Length > 2_048 || !template.Contains("{query}", StringComparison.Ordinal))
        {
            error = "검색 URL에는 {query} 자리표시자가 필요합니다.";
            return false;
        }
        var sample = template.Replace("{query}", "test", StringComparison.Ordinal);
        if (!Uri.TryCreate(sample, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            error = "검색 URL을 HTTP 또는 HTTPS 주소로 입력해 주세요.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "검색 URL에는 사용자 정보나 # 조각을 넣을 수 없습니다.";
            return false;
        }
        settings = new WebSearchSettings(template);
        return true;
    }

    internal static Uri BuildUri(WebSearchSettings settings, string query)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new Uri(settings.UrlTemplate.Replace(
            "{query}", Uri.EscapeDataString(query), StringComparison.Ordinal));
    }
}

internal sealed class WebSearchSettingsStore
{
    private const string SettingsKeyPath = @"Software\LIGClaw\WebSearch";

    public WebSearchSettings? Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
        var value = key?.GetValue("UrlTemplate") as string;
        return WebSearchSettingsPolicy.TryValidate(value, out var settings, out _) ? settings : null;
    }

    public void Save(WebSearchSettings? settings)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
        if (settings is null)
        {
            key.DeleteValue("UrlTemplate", throwOnMissingValue: false);
            return;
        }
        if (!WebSearchSettingsPolicy.TryValidate(settings.UrlTemplate, out var normalized, out var error))
            throw new ArgumentException(error, nameof(settings));
        key.SetValue("UrlTemplate", normalized!.UrlTemplate, RegistryValueKind.String);
    }
}
