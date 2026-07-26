using Microsoft.Win32;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed class SemanticMemorySettingsStore
{
    private const string SettingsKeyPath = @"Software\LIGClaw\SemanticMemory";
    private const string CredentialTarget = "LIGClaw/SemanticMemoryApiKey";
    private static readonly SemanticMemorySettings DisabledDefault = new(
        false,
        "http://127.0.0.1:11434",
        "nomic-embed-text");

    public SemanticMemorySettings Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
        if (key is null) return DisabledDefault;
        return new SemanticMemorySettings(
            Convert.ToInt32(key.GetValue("Enabled", 0), System.Globalization.CultureInfo.InvariantCulture) == 1,
            key.GetValue("BaseUrl") as string ?? DisabledDefault.BaseUrl,
            key.GetValue("Model") as string ?? DisabledDefault.Model,
            WindowsCredentialStore.Read(CredentialTarget));
    }

    public void Save(SemanticMemorySettings settings)
    {
        if (!SemanticMemorySettingsPolicy.TryValidate(settings, out var normalized, out var error))
            throw new ArgumentException(error, nameof(settings));
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
        key.SetValue("Enabled", normalized.Enabled ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("BaseUrl", normalized.BaseUrl, RegistryValueKind.String);
        key.SetValue("Model", normalized.Model, RegistryValueKind.String);
        if (string.IsNullOrWhiteSpace(normalized.ApiKey)) WindowsCredentialStore.Delete(CredentialTarget);
        else WindowsCredentialStore.Write(CredentialTarget, normalized.ApiKey);
    }
}
