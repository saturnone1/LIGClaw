using System.IO;
using LIGClaw.Desktop.Infrastructure.Tools;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LIGClaw.Desktop.Infrastructure.Tray;

internal sealed class TrayIconService : IUserNotificationService, IDisposable
{
    private readonly Drawing.Icon _icon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _windowHiddenMessageShown;

    public TrayIconService(Action showWindow, Action exitApplication)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ligclaw-tray.ico");
        _icon = new Drawing.Icon(iconPath);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("LIGClaw 열기", null, (_, _) => showWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => exitApplication());

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = _icon,
            Text = "LIGClaw · Windows 개인 비서",
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => showWindow();
    }

    public void ShowWindowHiddenMessage()
    {
        if (_windowHiddenMessageShown) return;
        _windowHiddenMessageShown = true;
        _notifyIcon.ShowBalloonTip(
            3_000,
            "LIGClaw는 계속 실행 중이에요",
            "다시 열려면 작업 표시줄의 LIGClaw 아이콘을 두 번 클릭하세요.",
            Forms.ToolTipIcon.Info);
    }

    public async Task ShowAsync(string title, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        await dispatcher.InvokeAsync(() =>
            _notifyIcon.ShowBalloonTip(5_000, title, message, Forms.ToolTipIcon.Info),
            System.Windows.Threading.DispatcherPriority.Normal,
            cancellationToken);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
