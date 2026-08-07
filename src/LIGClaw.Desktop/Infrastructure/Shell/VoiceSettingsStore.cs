using Microsoft.Win32;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record VoiceSettings(
    bool PushToTalkEnabled,
    bool AutoReadEnabled,
    int QuietStartMinute = 22 * 60,
    int QuietEndMinute = 7 * 60);

internal interface IVoiceSettingsStore
{
    VoiceSettings Load();
    void Save(VoiceSettings settings);
}

internal sealed class VoiceSettingsStore : IVoiceSettingsStore
{
    private const string SettingsKeyPath = @"Software\LIGClaw\VoiceInput";

    public VoiceSettings Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
        return new VoiceSettings(
            ReadBool(key, "Enabled"),
            ReadBool(key, "AutoReadEnabled"),
            ReadMinute(key, "QuietStartMinute", 22 * 60),
            ReadMinute(key, "QuietEndMinute", 7 * 60));
    }

    public void Save(VoiceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        VoiceAutoReadPolicy.Validate(settings);
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
        key.SetValue("Enabled", settings.PushToTalkEnabled ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("AutoReadEnabled", settings.AutoReadEnabled ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("QuietStartMinute", settings.QuietStartMinute, RegistryValueKind.DWord);
        key.SetValue("QuietEndMinute", settings.QuietEndMinute, RegistryValueKind.DWord);
    }

    private static bool ReadBool(RegistryKey? key, string name) =>
        key is not null && Convert.ToInt32(key.GetValue(name, 0), System.Globalization.CultureInfo.InvariantCulture) == 1;

    private static int ReadMinute(RegistryKey? key, string name, int fallback)
    {
        if (key is null) return fallback;
        var value = Convert.ToInt32(key.GetValue(name, fallback), System.Globalization.CultureInfo.InvariantCulture);
        return value is >= 0 and < 1_440 ? value : fallback;
    }
}

internal static class VoiceAutoReadPolicy
{
    internal static void Validate(VoiceSettings settings)
    {
        if (settings.QuietStartMinute is < 0 or >= 1_440 || settings.QuietEndMinute is < 0 or >= 1_440)
            throw new ArgumentOutOfRangeException(nameof(settings));
        if (settings.QuietStartMinute == settings.QuietEndMinute)
            throw new ArgumentException("방해 금지 시작과 종료 시간을 다르게 선택해 주세요.", nameof(settings));
    }

    internal static bool ShouldRead(
        VoiceSettings settings,
        DateTimeOffset localNow,
        bool isWindowActive,
        bool isVoiceInputActive,
        bool isAlreadySpeaking,
        string? response)
    {
        Validate(settings);
        if (!settings.AutoReadEnabled || !isWindowActive || isVoiceInputActive || isAlreadySpeaking ||
            string.IsNullOrWhiteSpace(response)) return false;
        var minute = localNow.Hour * 60 + localNow.Minute;
        var quiet = settings.QuietStartMinute < settings.QuietEndMinute
            ? minute >= settings.QuietStartMinute && minute < settings.QuietEndMinute
            : minute >= settings.QuietStartMinute || minute < settings.QuietEndMinute;
        return !quiet;
    }
}

internal sealed record VoiceTimeOption(int Minute, string DisplayName)
{
    internal static IReadOnlyList<VoiceTimeOption> All { get; } = Enumerable.Range(0, 48)
        .Select(index => new VoiceTimeOption(index * 30, $"{index / 2:00}:{index % 2 * 30:00}"))
        .ToArray();
}
