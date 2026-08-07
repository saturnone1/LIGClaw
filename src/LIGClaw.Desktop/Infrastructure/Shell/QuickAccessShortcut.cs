namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record QuickAccessShortcut(string Id, string DisplayName, uint Modifiers, uint VirtualKey);

internal static class QuickAccessShortcutCatalog
{
    private const uint Control = 0x0002;
    private const uint Alt = 0x0001;
    private const uint Shift = 0x0004;
    private const uint NoRepeat = 0x4000;
    private const uint Space = 0x20;

    public static IReadOnlyList<QuickAccessShortcut> All { get; } =
    [
        new("ctrl-alt-space", "Ctrl + Alt + Space", Control | Alt | NoRepeat, Space),
        new("ctrl-shift-space", "Ctrl + Shift + Space", Control | Shift | NoRepeat, Space),
        new("ctrl-alt-l", "Ctrl + Alt + L", Control | Alt | NoRepeat, 0x4C),
    ];

    public static QuickAccessShortcut Default => All[0];

    public static QuickAccessShortcut Find(string? id) =>
        All.FirstOrDefault(shortcut => StringComparer.Ordinal.Equals(shortcut.Id, id)) ?? Default;
}
