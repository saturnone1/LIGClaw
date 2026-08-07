namespace LIGClaw.Desktop.Infrastructure.Platform;

[Flags]
internal enum WindowsCapability
{
    None = 0,
    Win32DesktopShell = 1 << 0,
    CredentialManager = 1 << 1,
    GlobalHotkey = 1 << 2,
    Windows11Shell = 1 << 3,
    UserNotification = 1 << 4,
    AppLaunch = 1 << 5,
    FileOperations = 1 << 6,
    Clipboard = 1 << 7,
    ExplorerContext = 1 << 8,
    PersonalMemory = 1 << 9,
    DurableScheduler = 1 << 10,
    UiAutomation = 1 << 11,
    WebAccess = 1 << 12,
}

internal enum WindowsClientRelease
{
    Unsupported,
    Windows10,
    Windows11OrLater,
}

internal sealed record WindowsPlatformProfile(
    WindowsClientRelease Release,
    int MajorVersion,
    int MinorVersion,
    int BuildNumber,
    bool IsWorkstation,
    WindowsCapability Capabilities)
{
    public const int MinimumSupportedBuild = 17_763;
    public const int Windows11FirstBuild = 22_000;

    public bool IsSupported => Release is not WindowsClientRelease.Unsupported;

    public string DisplayName => Release switch
    {
        WindowsClientRelease.Windows10 => $"Windows 10 (build {BuildNumber})",
        WindowsClientRelease.Windows11OrLater => $"Windows 11 이상 (build {BuildNumber})",
        _ => $"지원되지 않는 Windows (build {BuildNumber})",
    };

    public bool Supports(WindowsCapability capability) =>
        (Capabilities & capability) == capability;

    public static WindowsPlatformProfile Classify(
        int majorVersion,
        int minorVersion,
        int buildNumber,
        bool isWorkstation)
    {
        var supportedClient = isWorkstation && majorVersion == 10 && buildNumber >= MinimumSupportedBuild;
        var release = !supportedClient
            ? WindowsClientRelease.Unsupported
            : buildNumber >= Windows11FirstBuild
                ? WindowsClientRelease.Windows11OrLater
                : WindowsClientRelease.Windows10;
        var capabilities = release is WindowsClientRelease.Unsupported
            ? WindowsCapability.None
            : WindowsCapability.Win32DesktopShell |
              WindowsCapability.CredentialManager |
              WindowsCapability.GlobalHotkey |
              WindowsCapability.UserNotification |
              WindowsCapability.AppLaunch |
              WindowsCapability.FileOperations |
              WindowsCapability.Clipboard |
              WindowsCapability.ExplorerContext |
              WindowsCapability.PersonalMemory |
              WindowsCapability.DurableScheduler |
              WindowsCapability.UiAutomation |
              WindowsCapability.WebAccess;
        if (release is WindowsClientRelease.Windows11OrLater)
            capabilities |= WindowsCapability.Windows11Shell;

        return new WindowsPlatformProfile(
            release,
            majorVersion,
            minorVersion,
            buildNumber,
            isWorkstation,
            capabilities);
    }
}
