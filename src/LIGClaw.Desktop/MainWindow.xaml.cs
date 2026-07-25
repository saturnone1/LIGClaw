using System.Windows;
using System.Windows.Media;
using LIGClaw.Desktop.Infrastructure.Sidecar;

namespace LIGClaw.Desktop;

public partial class MainWindow : Window
{
    private readonly SidecarSupervisor _sidecar = new();

    public MainWindow()
    {
        InitializeComponent();
        _sidecar.StatusChanged += Sidecar_StatusChanged;
        _sidecar.DiagnosticMessage += Sidecar_DiagnosticMessage;
        Loaded += (_, _) => _sidecar.Start();
        Closed += MainWindow_Closed;
    }

    private void RestartSidecar_Click(object sender, RoutedEventArgs e) => _sidecar.RequestRestart();

    private void Sidecar_StatusChanged(object? sender, SidecarStatus status) =>
        Dispatcher.InvokeAsync(() =>
        {
            StatusTitle.Text = status.State.ToString();
            StatusMessage.Text = status.Message;
            StatusIndicator.Fill = new SolidColorBrush(status.State switch
            {
                SidecarState.Connected => Color.FromRgb(34, 197, 94),
                SidecarState.Faulted => Color.FromRgb(239, 68, 68),
                SidecarState.Starting or SidecarState.Restarting => Color.FromRgb(245, 158, 11),
                _ => Color.FromRgb(148, 163, 184),
            });
            AddDiagnostic($"{status.ChangedAtUtc:HH:mm:ss}  {status.State}: {status.Message}");
        });

    private void Sidecar_DiagnosticMessage(object? sender, string message) =>
        Dispatcher.InvokeAsync(() => AddDiagnostic($"{DateTimeOffset.UtcNow:HH:mm:ss}  {message}"));

    private void AddDiagnostic(string message)
    {
        DiagnosticsList.Items.Add(message);
        while (DiagnosticsList.Items.Count > 100) DiagnosticsList.Items.RemoveAt(0);
        DiagnosticsList.ScrollIntoView(DiagnosticsList.Items[^1]);
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _sidecar.StatusChanged -= Sidecar_StatusChanged;
        _sidecar.DiagnosticMessage -= Sidecar_DiagnosticMessage;
        await _sidecar.DisposeAsync();
    }
}
