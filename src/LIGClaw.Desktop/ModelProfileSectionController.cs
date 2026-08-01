using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop;

internal enum ModelProfileField
{
    None,
    BaseUrl,
    Model,
    ApiKey,
}

internal sealed class SettingsSectionValidationException(
    string message,
    ModelProfileField field = ModelProfileField.None) : Exception(message)
{
    public ModelProfileField Field { get; } = field;
}

internal sealed record ModelProfileInput(
    string BaseUrl,
    string Model,
    string? ApiKey,
    string ProfileId,
    string DisplayName);

internal sealed record ModelProfileSectionState(
    IReadOnlyList<ModelConnectionSettings> Profiles,
    ModelConnectionSettings? Current,
    ModelRoutingSettings? Routing);

internal sealed record ModelProfileSaveResult(
    ModelConnectionSettings Settings,
    IReadOnlyList<ModelConnectionSettings> Profiles,
    bool RequiresApply);

internal sealed class ModelProfileSectionController(IModelConnectionSettingsStore store)
{
    public ModelProfileSectionState Load()
    {
        var profiles = store.ListProfiles();
        var current = store.Load();
        return new ModelProfileSectionState(profiles, current, store.LoadRouting());
    }

    public ModelConnectionSettings Validate(ModelProfileInput input, ModelConnectionSettings? existing)
    {
        var baseUrl = input.BaseUrl.Trim().TrimEnd('/');
        var model = input.Model.Trim();
        var profileId = input.ProfileId.Trim();
        var displayName = input.DisplayName.Trim();
        var apiKey = string.IsNullOrEmpty(input.ApiKey) ? existing?.ApiKey : input.ApiKey;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new SettingsSectionValidationException(
                "Base URL을 http:// 또는 https://로 시작하는 주소로 입력해 주세요.",
                ModelProfileField.BaseUrl);
        if (string.IsNullOrWhiteSpace(model))
            throw new SettingsSectionValidationException("사용할 모델 이름을 입력해 주세요.", ModelProfileField.Model);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new SettingsSectionValidationException("API Key를 입력해 주세요.", ModelProfileField.ApiKey);
        if (profileId.Length is < 1 or > 64 || profileId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new SettingsSectionValidationException("프로필 ID는 영문, 숫자, -, _만 사용해 64자 이내로 입력해 주세요.");
        if (displayName.Length is < 1 or > 80)
            throw new SettingsSectionValidationException("프로필 표시 이름을 80자 이내로 입력해 주세요.");
        return new ModelConnectionSettings(baseUrl, apiKey, model, profileId, displayName);
    }

    public ModelProfileSaveResult Save(
        ModelConnectionSettings candidate,
        string fallbackProfileIds,
        ModelConnectionSettings? existing,
        ModelConnectionSettings? lastSuccessfulTest)
    {
        if (ModelConnectionInputPolicy.RequiresSuccessfulTest(candidate, existing, lastSuccessfulTest))
            throw new SettingsSectionValidationException("변경한 모델 연결을 먼저 테스트해 주세요.");
        var fallbackIds = ParseFallbackProfileIds(candidate.ProfileId, fallbackProfileIds);
        var changed = !Equals(candidate, existing);
        if (changed) store.Save(candidate);
        store.SaveRouting(candidate.ProfileId, fallbackIds);
        return new ModelProfileSaveResult(candidate, store.ListProfiles(), changed);
    }

    private string[] ParseFallbackProfileIds(string primaryId, string value)
    {
        var ids = value
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length > 4 || ids.Contains(primaryId, StringComparer.Ordinal))
            throw new SettingsSectionValidationException("Fallback은 기본 프로필과 다른 프로필 ID를 최대 4개까지 입력해 주세요.");
        var known = store.ListProfiles().Select(profile => profile.ProfileId).ToHashSet(StringComparer.Ordinal);
        var missing = ids.FirstOrDefault(id => !known.Contains(id));
        if (missing is not null)
            throw new SettingsSectionValidationException($"저장되지 않은 fallback 프로필입니다: {missing}");
        return ids;
    }
}
