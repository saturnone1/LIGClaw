using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed class QuickAccessHotkey : IDisposable
{
    private const int HotkeyId = 0x4C49;
    private const int WmHotkey = 0x0312;
    private HwndSource? _source;
    private QuickAccessShortcut? _shortcut;

    public bool IsRegistered { get; private set; }

    public event EventHandler? Pressed;

    public QuickAccessShortcut? Shortcut => _shortcut;

    public bool Register(nint windowHandle, QuickAccessShortcut shortcut)
    {
        if (IsRegistered) return true;
        if (_source is null)
        {
            _source = HwndSource.FromHwnd(windowHandle);
            if (_source is null) return false;
            _source.AddHook(WndProc);
        }
        IsRegistered = RegisterHotKey(windowHandle, HotkeyId, shortcut.Modifiers, shortcut.VirtualKey);
        if (IsRegistered)
        {
            _shortcut = shortcut;
        }
        return IsRegistered;
    }

    public bool Change(QuickAccessShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        if (_source is null) return false;
        if (StringComparer.Ordinal.Equals(_shortcut?.Id, shortcut.Id)) return IsRegistered;

        var previous = _shortcut;
        if (IsRegistered) UnregisterHotKey(_source.Handle, HotkeyId);
        IsRegistered = RegisterHotKey(_source.Handle, HotkeyId, shortcut.Modifiers, shortcut.VirtualKey);
        if (IsRegistered)
        {
            _shortcut = shortcut;
            return true;
        }

        if (previous is not null)
        {
            IsRegistered = RegisterHotKey(_source.Handle, HotkeyId, previous.Modifiers, previous.VirtualKey);
            _shortcut = IsRegistered ? previous : null;
        }
        else
        {
            _shortcut = null;
        }
        return false;
    }

    public void Unregister()
    {
        if (_source is not null && IsRegistered) UnregisterHotKey(_source.Handle, HotkeyId);
        IsRegistered = false;
        _shortcut = null;
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
        Unregister();
        _source.RemoveHook(WndProc);
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);
}
