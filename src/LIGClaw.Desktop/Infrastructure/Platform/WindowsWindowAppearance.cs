using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LIGClaw.Desktop.Infrastructure.Platform;

internal static class WindowsWindowAppearance
{
    private const int WindowCornerPreferenceAttribute = 33;
    private const int RoundCornerPreference = 2;

    public static bool Apply(Window window, WindowsPlatformProfile profile)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.Supports(WindowsCapability.Windows11Shell)) return true;

        var handle = new WindowInteropHelper(window).EnsureHandle();
        var preference = RoundCornerPreference;
        return DwmSetWindowAttribute(
            handle,
            WindowCornerPreferenceAttribute,
            ref preference,
            sizeof(int)) == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
