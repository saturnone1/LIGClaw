using Microsoft.Win32;
using Windows.ApplicationModel;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LIGClaw";

    public async Task<bool> IsEnabledAsync()
    {
        if (IsPackaged())
        {
            var task = await StartupTask.GetAsync("LIGClawStartup");
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        if (IsPackaged())
        {
            var task = await StartupTask.GetAsync("LIGClawStartup");
            if (enabled)
            {
                var state = await task.RequestEnableAsync();
                if (state is not (StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy))
                    throw new InvalidOperationException("Windows 시작 앱 사용을 허용하지 않았습니다.");
            }
            else task.Disable();
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("실행 파일 경로를 확인할 수 없습니다.");
        key.SetValue(ValueName, $"\"{executablePath}\" --background", RegistryValueKind.String);
    }

    private static bool IsPackaged()
    {
        try
        {
            _ = Package.Current.Id.Name;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
