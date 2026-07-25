using System.Windows;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Tray;

namespace LIGClaw.Desktop;

public partial class App : System.Windows.Application
{
    private TrayIconService? _trayIcon;
    private SingleInstanceCoordinator? _singleInstance;

    internal bool IsExitRequested { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.IsPrimary)
        {
            _singleInstance.SignalPrimary();
            Shutdown();
            return;
        }
        _singleInstance.ActivationRequested += SingleInstance_ActivationRequested;

        var window = new MainWindow();
        MainWindow = window;
        _trayIcon = new TrayIconService(ShowQuickInput, ExitApplication);
        window.InitializeBackgroundServices();
        if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
        {
            window.Show();
        }
    }

    private void SingleInstance_ActivationRequested(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(ShowQuickInput);

    internal void ShowMainWindow()
    {
        if (MainWindow is null) return;

        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }

        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Focus();
    }

    internal void NotifyWindowHidden() => _trayIcon?.ShowWindowHiddenMessage();

    internal void ShowQuickInput()
    {
        ShowMainWindow();
        (MainWindow as MainWindow)?.FocusRequestInput();
    }

    internal void ExitApplication()
    {
        if (IsExitRequested) return;
        IsExitRequested = true;
        if (MainWindow is { } window)
        {
            window.Close();
        }
        else
        {
            Shutdown();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExitRequested = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance is not null)
        {
            _singleInstance.ActivationRequested -= SingleInstance_ActivationRequested;
            _singleInstance.Dispose();
            _singleInstance = null;
        }
        _trayIcon?.Dispose();
        _trayIcon = null;
        base.OnExit(e);
    }
}
