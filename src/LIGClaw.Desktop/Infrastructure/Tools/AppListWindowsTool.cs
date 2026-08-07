using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class AppListWindowsTool(IWindowCatalog windowCatalog) : IWindowsToolAdapter
{
    private const int MaximumWindowCount = 100;

    public string Name => "app.list_windows.v1";
    public string Risk => "R0";
    public WindowsCapability RequiredCapabilities => WindowsCapability.Win32DesktopShell;
    public int Priority => 0;

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        if (input.Count != 0)
            return Failure("열린 창 확인에는 입력값이 필요하지 않습니다.");

        var entries = await windowCatalog.ListWindowsAsync(cancellationToken).ConfigureAwait(false);
        var windows = entries
            .OrderByDescending(entry => entry.IsForeground)
            .ThenBy(entry => entry.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumWindowCount)
            .Select(entry => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["windowId"] = entry.WindowId,
                ["title"] = entry.Title,
                ["processName"] = entry.ProcessName,
                ["isForeground"] = entry.IsForeground,
                ["isMinimized"] = entry.IsMinimized,
            })
            .ToArray();
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?> { ["windows"] = windows },
            ActivitySummary: $"열린 앱 창 {windows.Length}개를 확인했어요.");
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}
