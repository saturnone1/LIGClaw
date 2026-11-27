using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop;

internal sealed class WebSearchSettingsSectionController(IWebSearchSettingsStore store)
{
    public WebSearchSettings? Load() => store.Load();

    public WebSearchSettings? Save(string? urlTemplate)
    {
        if (!WebSearchSettingsPolicy.TryValidate(urlTemplate, out var settings, out var error))
            throw new SettingsSectionValidationException(error);
        store.Save(settings);
        return settings;
    }
}
