using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop;

internal sealed record ShellSettingsSaveResult(
    QuickAccessShortcut Shortcut,
    bool IsShortcutAvailable,
    IReadOnlyList<string> Failures);

internal sealed class ShellSettingsSectionController(
    Func<Task<bool>> loadStartupEnabled,
    Func<bool, Task> saveStartupEnabled,
    Func<QuickAccessShortcut, bool> applyShortcut)
{
    public Task<bool> LoadStartupEnabledAsync() => loadStartupEnabled();

    public async Task<ShellSettingsSaveResult> SaveAsync(
        QuickAccessShortcut? selectedShortcut,
        QuickAccessShortcut currentShortcut,
        bool isShortcutAvailable,
        bool startWithWindows)
    {
        var failures = new List<string>();
        var shortcut = currentShortcut;
        var available = isShortcutAvailable;
        if (selectedShortcut is null)
        {
            failures.Add("빠른 호출 단축키를 선택해 주세요.");
        }
        else if (!available || !StringComparer.Ordinal.Equals(selectedShortcut.Id, shortcut.Id))
        {
            try
            {
                if (!applyShortcut(selectedShortcut))
                    failures.Add($"{selectedShortcut.DisplayName}은 다른 프로그램에서 사용 중이에요.");
                else
                {
                    shortcut = selectedShortcut;
                    available = true;
                }
            }
            catch (Exception)
            {
                failures.Add("빠른 호출 단축키 설정을 저장하지 못했어요.");
            }
        }

        try
        {
            await saveStartupEnabled(startWithWindows).ConfigureAwait(false);
        }
        catch (Exception)
        {
            failures.Add("Windows 자동 실행 설정을 저장하지 못했어요.");
        }
        return new ShellSettingsSaveResult(shortcut, available, failures);
    }
}
