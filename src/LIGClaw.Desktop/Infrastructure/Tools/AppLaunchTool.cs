using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class AppLaunchTool(
    IRegisteredAppCatalog appCatalog,
    IRegisteredAppLauncher appLauncher) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _preparedAppIds = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "app.launch.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.AppLaunch;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return null;
        var app = appCatalog.Resolve(parameters.Value.AppName);
        if (app is null) return null;
        lock (_sync) _preparedAppIds[parameters.Value.AppName] = app.AppId;
        return new WindowsToolApprovalPrompt(
            "앱을 실행할까요?",
            $"{app.DisplayName} 앱을 엽니다.",
            $"요청 이유: {parameters.Value.Reason}\n\n시작 메뉴에 등록된 앱만 실행합니다.",
            GrantScope: $"registered-app:{app.AppId}");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var parameters = Read(input);
        if (parameters is null) return Failure("앱 이름 또는 요청 이유가 올바르지 않습니다.");
        string? preparedAppId;
        lock (_sync)
        {
            _preparedAppIds.Remove(parameters.Value.AppName, out preparedAppId);
        }
        if (preparedAppId is null) return Failure("승인한 앱 실행 정보를 찾을 수 없어요.");

        var app = appCatalog.Resolve(parameters.Value.AppName);
        if (app is null || !StringComparer.OrdinalIgnoreCase.Equals(app.AppId, preparedAppId))
            return Failure("승인 후 앱 등록 정보가 변경되어 실행하지 않았어요.");
        await appLauncher.LaunchAsync(app, cancellationToken).ConfigureAwait(false);
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?>
            {
                ["launched"] = true,
                ["displayName"] = app.DisplayName,
            },
            ActivitySummary: $"{app.DisplayName} 앱을 실행했어요.");
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return;
        lock (_sync) _preparedAppIds.Remove(parameters.Value.AppName);
    }

    private static (string AppName, string Reason)? Read(IReadOnlyDictionary<string, object?> input)
    {
        if (!ToolInputReader.HasOnlyKeys(input, "appName", "reason") ||
            !ToolInputReader.TryGetRequiredString(input, "appName", 128, out var appName) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason))
            return null;
        return (appName, reason);
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}
