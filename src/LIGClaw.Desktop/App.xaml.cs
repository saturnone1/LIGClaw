using System.Windows;
using System.Windows.Threading;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Tray;

namespace LIGClaw.Desktop;

public partial class App : System.Windows.Application
{
    private TrayIconService? _trayIcon;
    private SingleInstanceCoordinator? _singleInstance;
    private AccessibilityThemeService? _accessibilityTheme;

    internal bool IsExitRequested { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        _accessibilityTheme = new AccessibilityThemeService(Resources);

        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.IsPrimary)
        {
            _singleInstance.SignalPrimary();
            Shutdown();
            return;
        }
        _singleInstance.ActivationRequested += SingleInstance_ActivationRequested;

        _trayIcon = new TrayIconService(ShowQuickInput, ExitApplication);
        var window = new MainWindow(_trayIcon);
        MainWindow = window;
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

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (!UiExceptionPolicy.CanRecover(e.Exception)) return;
        e.Handled = true;
        try
        {
            if (MainWindow is MainWindow window)
                window.ShowUnexpectedUiFailure();
        }
        catch (Exception)
        {
            // 예외 복구 UI 자체의 실패는 원래 입력이나 예외 메시지를 로그로 남기지 않는다.
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExitRequested = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        if (_singleInstance is not null)
        {
            _singleInstance.ActivationRequested -= SingleInstance_ActivationRequested;
            _singleInstance.Dispose();
            _singleInstance = null;
        }
        _trayIcon?.Dispose();
        _trayIcon = null;
        _accessibilityTheme?.Dispose();
        _accessibilityTheme = null;
        base.OnExit(e);
    }
}
