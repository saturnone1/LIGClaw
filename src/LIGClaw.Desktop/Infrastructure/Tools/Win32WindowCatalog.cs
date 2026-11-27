using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record WindowCatalogEntry(
    string WindowId,
    string Title,
    string ProcessName,
    bool IsForeground,
    bool IsMinimized);

internal interface IWindowCatalog
{
    Task<IReadOnlyList<WindowCatalogEntry>> ListWindowsAsync(CancellationToken cancellationToken);
}

internal sealed class Win32WindowCatalog : IWindowCatalog
{
    private const int MaximumTitleLength = 512;
    private const int MaximumProcessNameLength = 128;
    private const int DwmWindowAttributeCloaked = 14;

    public Task<IReadOnlyList<WindowCatalogEntry>> ListWindowsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var foreground = GetForegroundWindow();
        var currentProcessId = Environment.ProcessId;
        var entries = new List<WindowCatalogEntry>();
        _ = EnumWindows((window, parameter) =>
        {
            _ = parameter;
            if (cancellationToken.IsCancellationRequested) return false;
            if (!IsWindowVisible(window) || IsCloaked(window)) return true;
            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId == 0 || processId == currentProcessId) return true;

            var titleLength = GetWindowTextLength(window);
            if (titleLength <= 0) return true;
            var titleBuffer = new StringBuilder(Math.Min(titleLength, MaximumTitleLength) + 1);
            if (GetWindowText(window, titleBuffer, titleBuffer.Capacity) <= 0) return true;
            var title = titleBuffer.ToString().Trim();
            if (title.Length == 0) return true;

            string processName;
            try
            {
                using var process = Process.GetProcessById(checked((int)processId));
                processName = process.ProcessName;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return true;
            }

            entries.Add(new WindowCatalogEntry(
                $"0x{window.ToInt64():X}",
                Limit(title, MaximumTitleLength),
                Limit(processName, MaximumProcessNameLength),
                window == foreground,
                IsIconic(window)));
            return true;
        }, IntPtr.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<WindowCatalogEntry>>(entries);
    }

    private static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttribute(window, DwmWindowAttributeCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int valueSize);
}
