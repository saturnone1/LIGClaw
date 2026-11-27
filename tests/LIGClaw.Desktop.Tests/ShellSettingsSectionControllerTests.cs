using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class ShellSettingsSectionControllerTests
{
    [Fact]
    public async Task AppliesChangedShortcutAndStartupSetting()
    {
        QuickAccessShortcut? applied = null;
        bool? startup = null;
        var controller = new ShellSettingsSectionController(
            () => Task.FromResult(false),
            enabled => { startup = enabled; return Task.CompletedTask; },
            shortcut => { applied = shortcut; return true; });
        var selected = QuickAccessShortcutCatalog.All.First(shortcut =>
            !StringComparer.Ordinal.Equals(shortcut.Id, QuickAccessShortcutCatalog.Default.Id));

        var result = await controller.SaveAsync(
            selected, QuickAccessShortcutCatalog.Default, isShortcutAvailable: true, startWithWindows: true);

        Assert.Same(selected, applied);
        Assert.True(startup);
        Assert.Same(selected, result.Shortcut);
        Assert.True(result.IsShortcutAvailable);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task ReportsIndependentShortcutAndStartupFailuresWithoutLosingCurrentState()
    {
        var controller = new ShellSettingsSectionController(
            () => Task.FromResult(false),
            _ => Task.FromException(new InvalidOperationException("denied")),
            _ => false);
        var selected = QuickAccessShortcutCatalog.All.First(shortcut =>
            !StringComparer.Ordinal.Equals(shortcut.Id, QuickAccessShortcutCatalog.Default.Id));

        var result = await controller.SaveAsync(
            selected, QuickAccessShortcutCatalog.Default, isShortcutAvailable: true, startWithWindows: true);

        Assert.Same(QuickAccessShortcutCatalog.Default, result.Shortcut);
        Assert.True(result.IsShortcutAvailable);
        Assert.Equal(2, result.Failures.Count);
        Assert.Contains(result.Failures, message => message.Contains("사용 중", StringComparison.Ordinal));
        Assert.Contains(result.Failures, message => message.Contains("자동 실행", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingShortcutDoesNotPreventStartupUpdate()
    {
        var startupCalls = 0;
        var controller = new ShellSettingsSectionController(
            () => Task.FromResult(true),
            _ => { startupCalls++; return Task.CompletedTask; },
            _ => throw new InvalidOperationException());

        var result = await controller.SaveAsync(
            null, QuickAccessShortcutCatalog.Default, isShortcutAvailable: false, startWithWindows: false);

        Assert.Equal(1, startupCalls);
        Assert.Single(result.Failures);
    }
}
