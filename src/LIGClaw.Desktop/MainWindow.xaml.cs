using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Sidecar;

namespace LIGClaw.Desktop;

public partial class MainWindow : Window
{
    private readonly SidecarSupervisor _sidecar = new();
    private readonly QuickAccessHotkey _quickAccessHotkey = new();
    private string? _activeConversationId;
    private bool _isConnected;
    private bool _isRunning;

    public MainWindow()
    {
        InitializeComponent();
        _sidecar.StatusChanged += Sidecar_StatusChanged;
        _sidecar.DiagnosticMessage += Sidecar_DiagnosticMessage;
        _sidecar.AgentEventReceived += Sidecar_AgentEventReceived;
        _quickAccessHotkey.Pressed += QuickAccessHotkey_Pressed;
        Loaded += (_, _) =>
        {
            FocusRequestInput();
        };
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    internal bool IsQuickAccessAvailable => _quickAccessHotkey.IsRegistered;

    internal void InitializeBackgroundServices()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (!_quickAccessHotkey.Register(handle))
        {
            AddDiagnostic("빠른 호출 단축키 Ctrl+Alt+Space를 등록하지 못했습니다.");
        }
        _sidecar.Start();
    }

    internal void FocusRequestInput()
    {
        ConversationInput.Focus();
        Keyboard.Focus(ConversationInput);
    }

    private void QuickAccessHotkey_Pressed(object? sender, EventArgs e) =>
        (System.Windows.Application.Current as App)?.ShowQuickInput();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(IsQuickAccessAvailable) { Owner = this };
        settings.ShowDialog();
    }

    private void RestartSidecar_Click(object sender, RoutedEventArgs e)
    {
        _isConnected = false;
        RestartButton.Visibility = Visibility.Collapsed;
        SetConnectionCopy("다시 연결하고 있어요", "잠시만 기다려 주세요.", "연결 중", SidecarState.Restarting);
        UpdateCommandState();
        _sidecar.RequestRestart();
    }

    private void ConversationInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        InputPlaceholder.Visibility = string.IsNullOrEmpty(ConversationInput.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateCommandState();
    }

    private void ConversationInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && SendButton.IsEnabled)
        {
            e.Handled = true;
            Send_Click(SendButton, new RoutedEventArgs());
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var input = ConversationInput.Text.Trim();
        if (input.Length == 0) return;
        if (!_isConnected)
        {
            RunStatus.Text = "아직 준비 중이에요. 잠시 후 다시 시도해 주세요.";
            return;
        }

        _activeConversationId = Guid.NewGuid().ToString("N");
        var conversationId = _activeConversationId;
        _isRunning = true;
        Transcript.Clear();
        TranscriptPlaceholder.Visibility = Visibility.Collapsed;
        RunStatus.Text = "요청을 확인하고 있어요…";
        UpdateCommandState();
        try
        {
            _ = await _sidecar.StartConversationAsync(_activeConversationId, input, "cline");
            if (_isRunning && StringComparer.Ordinal.Equals(_activeConversationId, conversationId))
            {
                RunStatus.Text = "답변을 준비하고 있어요…";
            }
        }
        catch (Exception exception)
        {
            AddDiagnostic($"요청 시작 실패: {exception.Message}");
            ShowRunFailure("요청을 시작하지 못했어요. 잠시 후 다시 시도해 주세요.");
        }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_activeConversationId is null) return;
        var conversationId = _activeConversationId;
        try
        {
            var result = await _sidecar.CancelConversationAsync(_activeConversationId);
            if (_isRunning && StringComparer.Ordinal.Equals(_activeConversationId, conversationId))
            {
                RunStatus.Text = result.Cancelled ? "요청을 중단하고 있어요…" : "이미 처리가 끝났어요.";
            }
        }
        catch (Exception exception)
        {
            AddDiagnostic($"요청 중단 실패: {exception.Message}");
            ShowRunFailure("요청을 중단하지 못했어요. 연결 상태를 확인해 주세요.");
        }
    }

    private void Sidecar_AgentEventReceived(object? sender, AgentEvent agentEvent) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (!StringComparer.Ordinal.Equals(agentEvent.ConversationId, _activeConversationId)) return;
            switch (agentEvent.Type)
            {
                case "run_started":
                    RunStatus.Text = "답변을 작성하고 있어요…";
                    break;
                case "text_delta":
                    Transcript.AppendText(agentEvent.Text ?? string.Empty);
                    Transcript.ScrollToEnd();
                    break;
                case "run_completed":
                    CompleteRun("답변을 마쳤어요.");
                    break;
                case "run_cancelled":
                    CompleteRun("요청을 중단했어요.");
                    break;
                case "run_failed":
                    AddDiagnostic($"요청 처리 실패: {agentEvent.Message}");
                    ShowRunFailure("요청을 처리하지 못했어요. 다시 시도해 주세요.");
                    break;
            }
        });

    private void CompleteRun(string status)
    {
        RunStatus.Text = status;
        _activeConversationId = null;
        _isRunning = false;
        UpdateCommandState();
    }

    private void ShowRunFailure(string message)
    {
        Transcript.Text = message;
        TranscriptPlaceholder.Visibility = Visibility.Collapsed;
        CompleteRun(message);
    }

    private void UpdateCommandState()
    {
        if (!IsInitialized) return;
        SendButton.IsEnabled = _isConnected && !_isRunning && !string.IsNullOrWhiteSpace(ConversationInput.Text);
        CancelButton.Visibility = _isRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Sidecar_StatusChanged(object? sender, SidecarStatus status) =>
        Dispatcher.InvokeAsync(() =>
        {
            _isConnected = status.State == SidecarState.Connected;
            if (!_isConnected && _isRunning)
            {
                ShowRunFailure("연결이 끊어져 요청이 중단됐어요. 다시 연결되면 재시도해 주세요.");
            }

            var statusBrush = new SolidColorBrush(status.State switch
            {
                SidecarState.Connected => System.Windows.Media.Color.FromRgb(22, 132, 91),
                SidecarState.Faulted => System.Windows.Media.Color.FromRgb(196, 61, 75),
                SidecarState.Starting or SidecarState.Restarting => System.Windows.Media.Color.FromRgb(182, 106, 10),
                _ => System.Windows.Media.Color.FromRgb(135, 150, 168),
            });
            StatusIndicator.Fill = statusBrush;
            HeaderStatusIndicator.Fill = statusBrush;
            var copy = status.State switch
            {
                SidecarState.Connected => ("사용할 준비가 됐어요", "요청을 입력하면 바로 시작할게요.", "준비됨"),
                SidecarState.Faulted => ("연결에 문제가 있어요", "자동으로 다시 시도하고 있어요.", "연결 문제"),
                SidecarState.Starting => ("도우미를 준비하고 있어요", "잠시만 기다려 주세요.", "준비 중"),
                SidecarState.Restarting => ("다시 연결하고 있어요", "잠시만 기다려 주세요.", "연결 중"),
                _ => ("도우미가 멈췄어요", "다시 연결을 눌러 주세요.", "연결 안 됨"),
            };
            StatusTitle.Text = copy.Item1;
            StatusMessage.Text = copy.Item2;
            HeaderStatusText.Text = copy.Item3;
            RestartButton.Visibility = status.State is SidecarState.Faulted or SidecarState.Stopped
                ? Visibility.Visible
                : Visibility.Collapsed;
            RunStatus.Text = status.State switch
            {
                SidecarState.Connected when !_isRunning => "요청을 입력해 주세요.",
                SidecarState.Faulted when !_isRunning => "연결을 복구하고 있어요…",
                SidecarState.Stopped when !_isRunning => "다시 연결해 주세요.",
                _ when !_isRunning => "준비하고 있어요…",
                _ => RunStatus.Text,
            };
            UpdateCommandState();
            AddDiagnostic($"{status.ChangedAtUtc:HH:mm:ss}  {status.State}: {status.Message}");
        });

    private void SetConnectionCopy(
        string title,
        string message,
        string header,
        SidecarState state)
    {
        StatusTitle.Text = title;
        StatusMessage.Text = message;
        HeaderStatusText.Text = header;
        var brush = new SolidColorBrush(state == SidecarState.Faulted
            ? System.Windows.Media.Color.FromRgb(196, 61, 75)
            : System.Windows.Media.Color.FromRgb(182, 106, 10));
        StatusIndicator.Fill = brush;
        HeaderStatusIndicator.Fill = brush;
    }

    private void Sidecar_DiagnosticMessage(object? sender, string message) =>
        Dispatcher.InvokeAsync(() => AddDiagnostic($"{DateTimeOffset.UtcNow:HH:mm:ss}  {message}"));

    private void AddDiagnostic(string message)
    {
        DiagnosticsList.Items.Add(message);
        while (DiagnosticsList.Items.Count > 100) DiagnosticsList.Items.RemoveAt(0);
        DiagnosticsList.ScrollIntoView(DiagnosticsList.Items[^1]);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (System.Windows.Application.Current is not App { IsExitRequested: true })
        {
            e.Cancel = true;
            Hide();
            (System.Windows.Application.Current as App)?.NotifyWindowHidden();
        }
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _sidecar.StatusChanged -= Sidecar_StatusChanged;
        _sidecar.DiagnosticMessage -= Sidecar_DiagnosticMessage;
        _sidecar.AgentEventReceived -= Sidecar_AgentEventReceived;
        _quickAccessHotkey.Pressed -= QuickAccessHotkey_Pressed;
        _quickAccessHotkey.Dispose();
        await _sidecar.DisposeAsync();
        System.Windows.Application.Current.Shutdown();
    }
}
