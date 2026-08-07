using Microsoft.Win32;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record ModelConnectionSettings(
    string BaseUrl,
    string ApiKey,
    string Model,
    string ProfileId = "default",
    string DisplayName = "기본 모델");

internal sealed record ModelRoutingSettings(
    ModelConnectionSettings Primary,
    IReadOnlyList<ModelConnectionSettings> Fallbacks,
    bool AllowFallback,
    bool ExplicitSelection);

internal static class ModelRoutingPayload
{
    public static IReadOnlyDictionary<string, object?> Create(ModelRoutingSettings routing) =>
        new Dictionary<string, object?>
        {
            ["primary"] = Provider(routing.Primary),
            ["fallbacks"] = routing.Fallbacks.Select(Provider).ToArray(),
            ["allowFallback"] = routing.AllowFallback,
            ["explicitSelection"] = routing.ExplicitSelection,
        };

    private static IReadOnlyDictionary<string, object?> Provider(ModelConnectionSettings profile) =>
        new Dictionary<string, object?>
        {
            ["profileId"] = profile.ProfileId,
            ["baseUrl"] = profile.BaseUrl,
            ["apiKey"] = profile.ApiKey,
            ["model"] = profile.Model,
        };
}

internal interface IModelConnectionSettingsStore
{
    ModelConnectionSettings? Load();
    IReadOnlyList<ModelConnectionSettings> ListProfiles();
    void Save(ModelConnectionSettings settings);
    void SaveRouting(string defaultProfileId, IReadOnlyList<string> fallbackProfileIds);
    ModelRoutingSettings? LoadRouting(string? explicitProfileId = null);
}

internal sealed class ModelConnectionSettingsStore : IModelConnectionSettingsStore
{
    private const string SettingsKeyPath = @"Software\LIGClaw\ModelConnection";
    private const string ProfilesKeyPath = @"Software\LIGClaw\ModelProfiles";
    private const string CredentialTarget = "LIGClaw/OpenAICompatibleApiKey";

    public ModelConnectionSettings? Load()
    {
        using var profiles = Registry.CurrentUser.OpenSubKey(ProfilesKeyPath);
        var defaultId = profiles?.GetValue("DefaultProfileId") as string;
        var profile = LoadProfile(string.IsNullOrWhiteSpace(defaultId) ? "default" : defaultId);
        if (profile is not null) return profile;
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
        var baseUrl = key?.GetValue("BaseUrl") as string;
        var model = key?.GetValue("Model") as string;
        var apiKey = ReadCredential();
        return string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new ModelConnectionSettings(baseUrl, apiKey, model);
    }

    public void Save(ModelConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SaveProfile(settings);
        using var profiles = Registry.CurrentUser.CreateSubKey(ProfilesKeyPath, writable: true);
        profiles.SetValue("DefaultProfileId", settings.ProfileId, RegistryValueKind.String);
    }

    public IReadOnlyList<ModelConnectionSettings> ListProfiles()
    {
        using var root = Registry.CurrentUser.OpenSubKey(ProfilesKeyPath);
        if (root is null)
        {
            var legacy = LoadLegacy();
            return legacy is null ? [] : [legacy];
        }
        return root.GetSubKeyNames()
            .Where(IsValidProfileId)
            .Select(LoadProfile)
            .OfType<ModelConnectionSettings>()
            .OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public ModelConnectionSettings? LoadProfile(string profileId)
    {
        if (!IsValidProfileId(profileId)) return null;
        using var key = Registry.CurrentUser.OpenSubKey($@"{ProfilesKeyPath}\{profileId}");
        var baseUrl = key?.GetValue("BaseUrl") as string;
        var model = key?.GetValue("Model") as string;
        var displayName = key?.GetValue("DisplayName") as string;
        var apiKey = WindowsCredentialStore.Read(ProfileCredentialTarget(profileId));
        return string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new ModelConnectionSettings(baseUrl, apiKey, model, profileId,
                string.IsNullOrWhiteSpace(displayName) ? profileId : displayName);
    }

    public void SaveProfile(ModelConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsValidProfileId(settings.ProfileId))
            throw new ArgumentException("모델 프로필 ID가 올바르지 않습니다.", nameof(settings));
        using var key = Registry.CurrentUser.CreateSubKey($@"{ProfilesKeyPath}\{settings.ProfileId}", writable: true);
        var previousName = key.GetValue("DisplayName") as string;
        var previousBaseUrl = key.GetValue("BaseUrl") as string;
        var previousModel = key.GetValue("Model") as string;
        var target = ProfileCredentialTarget(settings.ProfileId);
        var previousSecret = WindowsCredentialStore.Read(target);
        try
        {
            WindowsCredentialStore.Write(target, settings.ApiKey);
            key.SetValue("DisplayName", settings.DisplayName, RegistryValueKind.String);
            key.SetValue("BaseUrl", settings.BaseUrl, RegistryValueKind.String);
            key.SetValue("Model", settings.Model, RegistryValueKind.String);
        }
        catch (Exception saveException)
        {
            var rollbackFailures = new List<Exception>();
            try { RestoreValue(key, "DisplayName", previousName); } catch (Exception exception) { rollbackFailures.Add(exception); }
            try { RestoreValue(key, "BaseUrl", previousBaseUrl); } catch (Exception exception) { rollbackFailures.Add(exception); }
            try { RestoreValue(key, "Model", previousModel); } catch (Exception exception) { rollbackFailures.Add(exception); }
            try
            {
                if (previousSecret is null) WindowsCredentialStore.Delete(target);
                else WindowsCredentialStore.Write(target, previousSecret);
            }
            catch (Exception exception) { rollbackFailures.Add(exception); }
            if (rollbackFailures.Count > 0)
                throw new AggregateException("모델 프로필 저장과 이전 상태 복원에 실패했습니다.", [saveException, .. rollbackFailures]);
            throw;
        }
    }

    public void SaveRouting(string defaultProfileId, IReadOnlyList<string> fallbackProfileIds)
    {
        if (!IsValidProfileId(defaultProfileId)) throw new ArgumentException("기본 프로필 ID가 올바르지 않습니다.");
        if (fallbackProfileIds.Count > 4 || fallbackProfileIds.Any(id => !IsValidProfileId(id)) ||
            fallbackProfileIds.Distinct(StringComparer.Ordinal).Count() != fallbackProfileIds.Count ||
            fallbackProfileIds.Contains(defaultProfileId, StringComparer.Ordinal))
            throw new ArgumentException("Fallback 프로필 순서가 올바르지 않습니다.");
        if (LoadProfile(defaultProfileId) is null &&
            StringComparer.Ordinal.Equals(defaultProfileId, "default") && LoadLegacy() is { } legacy)
            SaveProfile(legacy);
        if (LoadProfile(defaultProfileId) is null || fallbackProfileIds.Any(id => LoadProfile(id) is null))
            throw new ArgumentException("저장되지 않은 모델 프로필은 routing에 사용할 수 없습니다.");
        using var root = Registry.CurrentUser.CreateSubKey(ProfilesKeyPath, writable: true);
        root.SetValue("DefaultProfileId", defaultProfileId, RegistryValueKind.String);
        root.SetValue("FallbackProfileIds", fallbackProfileIds.ToArray(), RegistryValueKind.MultiString);
    }

    public ModelRoutingSettings? LoadRouting(string? explicitProfileId = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitProfileId))
        {
            var selected = LoadProfile(explicitProfileId);
            return selected is null ? null : new ModelRoutingSettings(selected, [], false, true);
        }
        using var root = Registry.CurrentUser.OpenSubKey(ProfilesKeyPath);
        var defaultId = root?.GetValue("DefaultProfileId") as string ?? "default";
        var primary = LoadProfile(defaultId) ?? Load();
        if (primary is null) return null;
        var fallbackIds = root?.GetValue("FallbackProfileIds") as string[] ?? [];
        var fallbacks = fallbackIds.Take(4)
            .Where(id => !StringComparer.Ordinal.Equals(id, primary.ProfileId))
            .Select(LoadProfile)
            .OfType<ModelConnectionSettings>()
            .ToArray();
        return new ModelRoutingSettings(primary, fallbacks, fallbacks.Length > 0, false);
    }

    private static bool IsValidProfileId(string? value) =>
        value is { Length: >= 1 and <= 64 } && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string ProfileCredentialTarget(string profileId) =>
        $"LIGClaw/OpenAICompatibleApiKey/{profileId}";

    private static ModelConnectionSettings? LoadLegacy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
        var baseUrl = key?.GetValue("BaseUrl") as string;
        var model = key?.GetValue("Model") as string;
        var apiKey = ReadCredential();
        return string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new ModelConnectionSettings(baseUrl, apiKey, model);
    }

    private static void RestoreValue(RegistryKey key, string name, string? value)
    {
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    private static string? ReadCredential() => WindowsCredentialStore.Read(CredentialTarget);
}
