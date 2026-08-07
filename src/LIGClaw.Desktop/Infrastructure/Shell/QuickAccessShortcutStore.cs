using Microsoft.Win32;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed class QuickAccessShortcutStore
{
    private const string SettingsKeyPath = @"Software\LIGClaw\Shell";
    private const string ValueName = "QuickAccessShortcut";

    public QuickAccessShortcut Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
        return QuickAccessShortcutCatalog.Find(key?.GetValue(ValueName) as string);
    }

    public void Save(QuickAccessShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
        key.SetValue(ValueName, shortcut.Id, RegistryValueKind.String);
    }
}
