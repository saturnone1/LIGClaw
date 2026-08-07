using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class AppSearchInstalledTool(IInstalledAppSearch apps) : IWindowsToolAdapter
{
    private const int MaximumResults = 20;

    public string Name => "app.search_installed.v1";
    public string Risk => "R0";
    public WindowsCapability RequiredCapabilities => WindowsCapability.AppLaunch;
    public int Priority => 0;

    public Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ToolInputReader.HasOnlyKeys(input, "query") ||
            !ToolInputReader.TryGetRequiredString(input, "query", 128, out var query))
            return Task.FromResult(Failure("검색할 앱 이름이 올바르지 않습니다."));
        var matches = apps.Search(query, MaximumResults, out var truncated)
            .Select(app => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["displayName"] = app.DisplayName,
            })
            .ToArray();
        return Task.FromResult(new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?>
            {
                ["apps"] = matches,
                ["truncated"] = truncated,
            },
            ActivitySummary: $"시작 메뉴에서 앱 {matches.Length}개를 찾았어요."));
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}
