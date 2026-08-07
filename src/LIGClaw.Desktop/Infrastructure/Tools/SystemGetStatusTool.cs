using System.Runtime.InteropServices;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using Forms = System.Windows.Forms;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class SystemGetStatusTool(WindowsPlatformProfile platform) : IWindowsToolAdapter
{
    public string Name => "system.get_status.v1";
    public string Risk => "R0";
    public WindowsCapability RequiredCapabilities => WindowsCapability.Win32DesktopShell;
    public int Priority => 0;

    public Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Count != 0)
            return Task.FromResult(new WindowsToolExecutionResult(
                false,
                new Dictionary<string, object?>(),
                "시스템 상태 확인에는 입력값이 필요하지 않습니다."));

        var release = platform.Release switch
        {
            WindowsClientRelease.Windows10 => "Windows 10",
            WindowsClientRelease.Windows11OrLater => "Windows 11 이상",
            _ => "지원되지 않는 Windows",
        };
        var powerSource = Forms.SystemInformation.PowerStatus.PowerLineStatus switch
        {
            Forms.PowerLineStatus.Online => "ac",
            Forms.PowerLineStatus.Offline => "battery",
            _ => "unknown",
        };
        var status = new SystemGetStatusV1Result(
            release,
            platform.BuildNumber,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            DateTimeOffset.Now,
            TimeZoneInfo.Local.Id,
            powerSource);
        IReadOnlyDictionary<string, object?> output = new Dictionary<string, object?>
        {
            ["windowsRelease"] = status.WindowsRelease,
            ["buildNumber"] = status.BuildNumber,
            ["architecture"] = status.Architecture,
            ["localTime"] = status.LocalTime,
            ["timeZone"] = status.TimeZone,
            ["powerSource"] = status.PowerSource,
        };
        return Task.FromResult(new WindowsToolExecutionResult(
            true,
            output,
            ActivitySummary: "시스템 상태를 확인했어요."));
    }
}
