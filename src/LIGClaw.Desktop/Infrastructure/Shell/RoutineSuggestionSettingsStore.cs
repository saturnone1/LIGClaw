using System.Globalization;
using Microsoft.Win32;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record RoutineSuggestionPreferences(
    bool Enabled,
    IReadOnlySet<string> IgnoredFingerprints,
    IReadOnlyDictionary<string, DateTimeOffset> LastOfferedUtc);

internal interface IRoutineSuggestionSettingsStore
{
    RoutineSuggestionPreferences Load();
    void SetEnabled(bool enabled);
    void RecordOffer(string fingerprint, DateTimeOffset offeredAtUtc);
    void Ignore(string fingerprint);
    void ClearHistory();
}

internal sealed class RoutineSuggestionSettingsStore : IRoutineSuggestionSettingsStore
{
    private const string SettingsKeyPath = @"Software\LIGClaw\RoutineSuggestions";
    private const int MaximumEntries = 100;

    public RoutineSuggestionPreferences Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
        var enabled = key is not null && Convert.ToInt32(
            key.GetValue("Enabled", 0), CultureInfo.InvariantCulture) == 1;
        var ignored = ReadFingerprints(key?.GetValue("IgnoredFingerprints") as string[]);
        var offers = ReadOffers(key?.GetValue("RecentOffers") as string[]);
        return new RoutineSuggestionPreferences(enabled, ignored, offers);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
        key.SetValue("Enabled", enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    public void RecordOffer(string fingerprint, DateTimeOffset offeredAtUtc)
    {
        ValidateFingerprint(fingerprint);
        var preferences = Load();
        var offers = preferences.LastOfferedUtc.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        offers[fingerprint] = offeredAtUtc.ToUniversalTime();
        WriteOffers(offers);
    }

    public void Ignore(string fingerprint)
    {
        ValidateFingerprint(fingerprint);
        var ignored = Load().IgnoredFingerprints.ToHashSet(StringComparer.Ordinal);
        ignored.Remove(fingerprint);
        var values = new[] { fingerprint }
            .Concat(ignored.Order())
            .Take(MaximumEntries)
            .ToArray();
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
        key.SetValue("IgnoredFingerprints", values, RegistryValueKind.MultiString);
    }

    public void ClearHistory()
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
        key.DeleteValue("IgnoredFingerprints", throwOnMissingValue: false);
        key.DeleteValue("RecentOffers", throwOnMissingValue: false);
    }

    private static HashSet<string> ReadFingerprints(string[]? values) =>
        (values ?? []).Where(IsFingerprint).Take(MaximumEntries).ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, DateTimeOffset> ReadOffers(string[]? values)
    {
        var offers = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var value in (values ?? []).Take(MaximumEntries))
        {
            var separator = value.IndexOf('|');
            if (separator <= 0 || !IsFingerprint(value[..separator]) ||
                !DateTimeOffset.TryParseExact(value[(separator + 1)..], "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var timestamp)) continue;
            offers[value[..separator]] = timestamp.ToUniversalTime();
        }
        return offers;
    }

    private static void WriteOffers(IReadOnlyDictionary<string, DateTimeOffset> offers)
    {
        var values = offers
            .OrderByDescending(pair => pair.Value)
            .Take(MaximumEntries)
            .Select(pair => $"{pair.Key}|{pair.Value.ToUniversalTime():O}")
            .ToArray();
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
        key.SetValue("RecentOffers", values, RegistryValueKind.MultiString);
    }

    private static void ValidateFingerprint(string fingerprint)
    {
        if (!IsFingerprint(fingerprint)) throw new ArgumentException("Invalid routine fingerprint.", nameof(fingerprint));
    }

    private static bool IsFingerprint(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
