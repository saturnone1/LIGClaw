using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed class QuickAccessHotkey : IDisposable
{
    private const int HotkeyId = 0x4C49;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeySpace = 0x20;
    private HwndSource? _source;

    public bool IsRegistered { get; private set; }

    public event EventHandler? Pressed;

    public bool Register(nint windowHandle)
    {
        if (IsRegistered) return true;
        _source = HwndSource.FromHwnd(windowHandle);
        if (_source is null) return false;
        _source.AddHook(WndProc);
        IsRegistered = RegisterHotKey(windowHandle, HotkeyId, ModControl | ModAlt | ModNoRepeat, VirtualKeySpace);
        if (!IsRegistered)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
        return IsRegistered;
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        return nint.Zero;
    }

    public void Dispose()
    {
        if (_source is null) return;
        if (IsRegistered) UnregisterHotKey(_source.Handle, HotkeyId);
        _source.RemoveHook(WndProc);
        _source = null;
        IsRegistered = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);
}
