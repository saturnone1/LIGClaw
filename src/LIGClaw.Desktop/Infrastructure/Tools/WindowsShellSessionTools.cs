using System.Diagnostics;
using System.Runtime.InteropServices;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal interface IWindowsSettingsLauncher { bool Open(string page); }
internal sealed class WindowsSettingsLauncher : IWindowsSettingsLauncher
{
    private static readonly IReadOnlyDictionary<string, string> Pages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["display"] = "display",
        ["sound"] = "sound",
        ["notifications"] = "notifications",
        ["bluetooth"] = "bluetooth",
        ["network"] = "network-status",
        ["apps"] = "appsfeatures",
        ["storage"] = "storagesense",
        ["windows_update"] = "windowsupdate",
        ["privacy"] = "privacy",
    };
    public bool Open(string page)
    {
        if (!Pages.TryGetValue(page, out var route)) return false;
        return Process.Start(new ProcessStartInfo($"ms-settings:{route}") { UseShellExecute = true }) is not null;
    }
}

internal sealed class SystemOpenSettingsTool(IWindowsSettingsLauncher? launcher = null) : IWindowsToolAdapter
{
    private readonly IWindowsSettingsLauncher _launcher = launcher ?? new WindowsSettingsLauncher();
    public string Name => "system.open_settings.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.Win32DesktopShell;
    public int Priority => 0;
    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var value = Read(input); return value is null ? null : new WindowsToolApprovalPrompt("Windows 설정을 열까요?", $"{Label(value.Value.Page)} 설정 페이지를 엽니다.", $"요청 이유: {value.Value.Reason}\n\n미리 등록된 Windows 설정 페이지만 열 수 있습니다.", GrantScope: $"settings:{value.Value.Page}");
    }
    public Task<WindowsToolExecutionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var value = Read(input);
        if (value is null || !_launcher.Open(value.Value.Page)) return Task.FromResult(Failure("Windows 설정 페이지를 열지 못했어요."));
        return Task.FromResult(new WindowsToolExecutionResult(true, new Dictionary<string, object?> { ["opened"] = true, ["page"] = value.Value.Page }, ActivitySummary: $"{Label(value.Value.Page)} 설정을 열었어요."));
    }
    private static (string Page, string Reason)? Read(IReadOnlyDictionary<string, object?> input) => ToolInputReader.HasOnlyKeys(input, "page", "reason") && ToolInputReader.TryGetRequiredString(input, "page", 32, out var page) && page is "display" or "sound" or "notifications" or "bluetooth" or "network" or "apps" or "storage" or "windows_update" or "privacy" && ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason) ? (page, reason) : null;
    private static string Label(string page) => page switch { "display" => "디스플레이", "sound" => "소리", "notifications" => "알림", "bluetooth" => "Bluetooth", "network" => "네트워크", "apps" => "앱", "storage" => "저장소", "windows_update" => "Windows 업데이트", _ => "개인정보" };
    private static WindowsToolExecutionResult Failure(string error) => new(false, new Dictionary<string, object?>(), error);
}

internal interface IWindowsSessionActionService { bool Request(string action); }
internal sealed class WindowsSessionActionService : IWindowsSessionActionService
{
    public bool Request(string action) => action switch { "lock" => LockWorkStation(), "sleep" => SetSuspendState(false, false, false), _ => false };
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}

internal sealed class SystemSessionActionTool(IWindowsSessionActionService? sessions = null) : IWindowsToolAdapter
{
    private readonly IWindowsSessionActionService _sessions = sessions ?? new WindowsSessionActionService();
    public string Name => "system.session_action.v1";
    public string Risk => "R2";
    public WindowsCapability RequiredCapabilities => WindowsCapability.Win32DesktopShell;
    public int Priority => 0;
    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var value = Read(input); if (value is null) return null; var label = value.Value.Action == "lock" ? "잠금" : "절전";
        return new WindowsToolApprovalPrompt($"PC를 {label}할까요?", $"현재 Windows 세션에 {label}을 요청합니다.", $"요청 이유: {value.Value.Reason}\n\n실행 즉시 화면 또는 작업이 중단될 수 있습니다.", Risk: "R2");
    }
    public Task<WindowsToolExecutionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var value = Read(input);
        if (value is null || !_sessions.Request(value.Value.Action)) return Task.FromResult(Failure("Windows가 세션 동작을 수행하지 못했어요."));
        return Task.FromResult(new WindowsToolExecutionResult(true, new Dictionary<string, object?> { ["requested"] = true, ["action"] = value.Value.Action }, ActivitySummary: value.Value.Action == "lock" ? "PC 잠금을 요청했어요." : "PC 절전을 요청했어요."));
    }
    private static (string Action, string Reason)? Read(IReadOnlyDictionary<string, object?> input) => ToolInputReader.HasOnlyKeys(input, "action", "reason") && ToolInputReader.TryGetRequiredString(input, "action", 8, out var action) && action is "lock" or "sleep" && ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason) ? (action, reason) : null;
    private static WindowsToolExecutionResult Failure(string error) => new(false, new Dictionary<string, object?>(), error);
}
