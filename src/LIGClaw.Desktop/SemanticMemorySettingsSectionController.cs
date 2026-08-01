using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop;

internal sealed record SemanticMemorySettingsInput(
    bool Enabled,
    string BaseUrl,
    string Model,
    string? ApiKey);

internal sealed class SemanticMemorySettingsSectionController(ISemanticMemorySettingsStore store)
{
    public SemanticMemorySettings Load() => store.Load();

    public SemanticMemorySettings Save(
        SemanticMemorySettingsInput input,
        SemanticMemorySettings? existing)
    {
        var settings = new SemanticMemorySettings(
            input.Enabled,
            input.BaseUrl,
            input.Model,
            string.IsNullOrWhiteSpace(input.ApiKey) ? existing?.ApiKey : input.ApiKey);
        if (!SemanticMemorySettingsPolicy.TryValidate(settings, out var normalized, out var error))
            throw new SettingsSectionValidationException(error);
        store.Save(normalized);
        return normalized;
    }
}
