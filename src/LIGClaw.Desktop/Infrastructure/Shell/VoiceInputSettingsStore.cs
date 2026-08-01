using Microsoft.Win32;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record VoiceInputSettings(bool Enabled);

internal interface IVoiceInputSettingsStore
{
    VoiceInputSettings Load();
    void Save(VoiceInputSettings settings);
}

internal sealed class VoiceInputSettingsStore : IVoiceInputSettingsStore
{
    private const string SettingsKeyPath = @"Software\LIGClaw\VoiceInput";

    public VoiceInputSettings Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
        return new VoiceInputSettings(
            key is not null &&
            Convert.ToInt32(key.GetValue("Enabled", 0), System.Globalization.CultureInfo.InvariantCulture) == 1);
    }

    public void Save(VoiceInputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
        key.SetValue("Enabled", settings.Enabled ? 1 : 0, RegistryValueKind.DWord);
    }
}
