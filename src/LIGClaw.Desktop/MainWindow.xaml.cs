using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Sidecar;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop;

public partial class MainWindow : Window
{
    private readonly SidecarSupervisor _sidecar = new();
    private readonly QuickAccessHotkey _quickAccessHotkey = new();
    private readonly QuickAccessShortcutStore _quickAccessShortcutStore = new();
    private readonly ModelConnectionSettingsStore _modelSettingsStore = new();
    private readonly ConversationStore _conversationStore = ConversationStore.CreateDefault();
    private QuickAccessShortcut _configuredQuickAccessShortcut = QuickAccessShortcutCatalog.Default;
    private WindowsPlatformProfile? _platformProfile;
    private WindowsToolHost? _windowsToolHost;
    private Task _persistenceInitialization = Task.CompletedTask;
    private bool _persistenceAvailable;
    private string? _activeConversationId;
    private bool _isConnected;
    private bool _isModelConfigured;
    private bool _isRunning;

    public MainWindow()
    {
        InitializeComponent();
        _sidecar.StatusChanged += Sidecar_StatusChanged;
        _sidecar.DiagnosticMessage += Sidecar_DiagnosticMessage;
        _sidecar.AgentEventReceived += Sidecar_AgentEventReceived;
        _sidecar.ToolInvocationReceived += Sidecar_ToolInvocationReceived;
        _quickAccessHotkey.Pressed += QuickAccessHotkey_Pressed;
        Loaded += (_, _) =>
        {
            FocusRequestInput();
        };
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    internal bool IsQuickAccessAvailable => _quickAccessHotkey.IsRegistered;
    internal WindowsPlatformProfile? PlatformProfile => _platformProfile;
    internal QuickAccessShortcut CurrentQuickAccessShortcut =>
        _quickAccessHotkey.Shortcut ?? _configuredQuickAccessShortcut;

    internal void InitializeBackgroundServices()
    {
        try
        {
            _platformProfile = WindowsPlatformDetector.Detect();
            AddDiagnostic($"플랫폼: {_platformProfile.DisplayName}");
            if (!_platformProfile.IsSupported)
                AddDiagnostic($"이 Windows 빌드는 공식 지원 기준 {WindowsPlatformProfile.MinimumSupportedBuild}보다 낮거나 클라이언트 OS가 아닙니다.");
        }
        catch (Exception exception)
        {
            AddDiagnostic($"Windows 플랫폼 감지 실패: {exception.Message}");
        }
        if (_platformProfile is not null) _windowsToolHost = new WindowsToolHost(_platformProfile);

        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (_platformProfile is not null && !WindowsWindowAppearance.Apply(this, _platformProfile))
            AddDiagnostic("현재 Windows의 창 모양 최적화를 적용하지 못했습니다.");
        QuickAccessShortcut preferredShortcut;
        try
        {
            preferredShortcut = _quickAccessShortcutStore.Load();
        }
        catch (Exception exception)
        {
            preferredShortcut = QuickAccessShortcutCatalog.Default;
            AddDiagnostic($"빠른 호출 설정을 불러오지 못해 기본값을 사용합니다: {exception.Message}");
        }
        _configuredQuickAccessShortcut = preferredShortcut;
        var canUseGlobalHotkey = _platformProfile?.Supports(WindowsCapability.GlobalHotkey) ?? true;
        if (canUseGlobalHotkey && !_quickAccessHotkey.Register(handle, preferredShortcut))
        {
            AddDiagnostic("빠른 호출 단축키를 등록하지 못했습니다.");
        }
        else if (!canUseGlobalHotkey)
        {
            AddDiagnostic("현재 Windows에서는 전역 단축키 capability를 사용할 수 없습니다.");
        }
        _persistenceInitialization = InitializePersistenceAsync();
        _sidecar.Start();
    }

    private async Task InitializePersistenceAsync()
    {
        try
        {
            await _conversationStore.InitializeAsync();
            await _conversationStore.MarkRunningConversationsInterruptedAsync();
            _persistenceAvailable = true;
            await RefreshRecentConversationsAsync();
        }
        catch (Exception exception)
        {
            _persistenceAvailable = false;
            AddDiagnostic($"대화 기록 저장소 초기화 실패: {exception.Message}");
        }
    }

    internal void FocusRequestInput()
    {
        ConversationInput.Focus();
        Keyboard.Focus(ConversationInput);
    }

    internal async Task<ProviderTestResult> TestProviderAsync(
        ModelConnectionSettings settings,
        CancellationToken cancellationToken = default) =>
        await _sidecar.TestProviderAsync(
            new ProviderConfigureParams(settings.BaseUrl, settings.ApiKey, settings.Model),
            cancellationToken);

    internal async Task ApplyProviderAsync(
        ModelConnectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        _ = await _sidecar.ConfigureProviderAsync(
            new ProviderConfigureParams(settings.BaseUrl, settings.ApiKey, settings.Model),
            cancellationToken);
        _isModelConfigured = true;
        UpdateCommandState();
    }

    internal bool ApplyQuickAccessShortcut(QuickAccessShortcut shortcut)
    {
        var previous = _quickAccessHotkey.Shortcut;
        if (!_quickAccessHotkey.Change(shortcut)) return false;
        try
        {
            _quickAccessShortcutStore.Save(shortcut);
            _configuredQuickAccessShortcut = shortcut;
            return true;
        }
        catch
        {
            if (previous is not null) _quickAccessHotkey.Change(previous);
            else _quickAccessHotkey.Unregister();
            throw;
        }
    }

    private async Task ConfigureStoredProviderAsync()
    {
        var settings = _modelSettingsStore.Load();
        if (settings is null)
        {
            RunStatus.Text = "설정에서 모델 API를 연결해 주세요.";
            return;
        }
        try
        {
            await ApplyProviderAsync(settings);
            RunStatus.Text = "요청을 입력해 주세요.";
        }
        catch (Exception exception)
        {
            AddDiagnostic($"모델 설정 적용 실패: {exception.Message}");
            RunStatus.Text = "모델 연결을 확인해 주세요.";
        }
    }

    private void QuickAccessHotkey_Pressed(object? sender, EventArgs e) =>
        (System.Windows.Application.Current as App)?.ShowQuickInput();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(IsQuickAccessAvailable, CurrentQuickAccessShortcut, PlatformProfile) { Owner = this };
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
        RecentConversationsList.SelectedItem = null;
        RecentConversationsList.IsEnabled = false;
        Transcript.Clear();
        TranscriptPlaceholder.Visibility = Visibility.Collapsed;
        RunStatus.Text = "요청을 확인하고 있어요…";
        UpdateCommandState();
        try
        {
            await PersistConversationStartAsync(conversationId, input);
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
        _ = Dispatcher.InvokeAsync(() => HandleAgentEventAsync(agentEvent));

    private async Task HandleAgentEventAsync(AgentEvent agentEvent)
    {
        if (!StringComparer.Ordinal.Equals(agentEvent.ConversationId, _activeConversationId))
        {
            await PersistAgentEventAsync(agentEvent);
            return;
        }
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
        await PersistAgentEventAsync(agentEvent);
        if (agentEvent.Type is "run_completed" or "run_cancelled" or "run_failed")
            await RefreshRecentConversationsAsync();
    }

    private async void Sidecar_ToolInvocationReceived(object? sender, ToolInvokeParams invocation)
    {
        WindowsToolExecutionResult execution;
        var belongsToActiveRun = await Dispatcher.InvokeAsync(() =>
            _isRunning && StringComparer.Ordinal.Equals(_activeConversationId, invocation.ConversationId));
        if (!belongsToActiveRun)
        {
            execution = new WindowsToolExecutionResult(
                false,
                new Dictionary<string, object?>(),
                "현재 요청에 속하지 않은 Tool 호출을 거부했어요.");
        }
        else if (_windowsToolHost is null)
        {
            execution = new WindowsToolExecutionResult(
                false,
                new Dictionary<string, object?>(),
                "Windows 플랫폼을 확인할 수 없어 기능을 실행하지 못했어요.");
        }
        else
        {
            execution = await _windowsToolHost.ExecuteAsync(invocation);
        }

        try
        {
            var acknowledged = await _sidecar.SubmitToolResultAsync(new ToolResultParams(
                invocation.ToolCallId,
                execution.Success,
                execution.Output,
                execution.Error));
            if (!acknowledged.Accepted)
                await Dispatcher.InvokeAsync(() => AddDiagnostic("Sidecar가 만료된 Tool 결과를 거부했습니다."));
            else if (execution.ActivitySummary is not null)
                await Dispatcher.InvokeAsync(() => AddDiagnostic(execution.ActivitySummary));
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() => AddDiagnostic($"Tool 결과 전달 실패: {exception.Message}"));
        }
    }

    private void CompleteRun(string status)
    {
        RunStatus.Text = status;
        _activeConversationId = null;
        _isRunning = false;
        RecentConversationsList.IsEnabled = true;
        UpdateCommandState();
    }

    private void ShowRunFailure(string message)
    {
        var failedConversationId = _activeConversationId;
        Transcript.Text = message;
        TranscriptPlaceholder.Visibility = Visibility.Collapsed;
        CompleteRun(message);
        if (failedConversationId is not null)
            _ = MarkConversationAsync(failedConversationId, "failed");
    }

    private async Task PersistConversationStartAsync(string conversationId, string input)
    {
        await _persistenceInitialization;
        if (!_persistenceAvailable) return;
        try
        {
            await _conversationStore.StartConversationAsync(conversationId, input, DateTimeOffset.UtcNow);
            await RefreshRecentConversationsAsync();
        }
        catch (Exception exception)
        {
            AddDiagnostic($"대화 시작 기록 실패: {exception.Message}");
        }
    }

    private async Task PersistAgentEventAsync(AgentEvent agentEvent)
    {
        await _persistenceInitialization;
        if (!_persistenceAvailable) return;
        try
        {
            await _conversationStore.AppendEventAsync(agentEvent);
        }
        catch (Exception exception)
        {
            AddDiagnostic($"대화 이벤트 기록 실패: {exception.Message}");
        }
    }

    private async Task MarkConversationAsync(string conversationId, string status)
    {
        await _persistenceInitialization;
        if (!_persistenceAvailable) return;
        try
        {
            await _conversationStore.MarkConversationAsync(conversationId, status, DateTimeOffset.UtcNow);
            await RefreshRecentConversationsAsync();
        }
        catch (Exception exception)
        {
            AddDiagnostic($"대화 상태 기록 실패: {exception.Message}");
        }
    }

    private async Task RefreshRecentConversationsAsync()
    {
        if (!_persistenceAvailable) return;
        try
        {
            var selectedId = (RecentConversationsList.SelectedItem as ConversationSummary)?.Id;
            var conversations = await _conversationStore.GetRecentConversationsAsync();
            RecentConversationsList.ItemsSource = conversations;
            if (selectedId is not null)
                RecentConversationsList.SelectedItem = conversations.FirstOrDefault(item => item.Id == selectedId);
        }
        catch (Exception exception)
        {
            AddDiagnostic($"최근 대화 불러오기 실패: {exception.Message}");
        }
    }

    private async void RecentConversationsList_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isRunning || RecentConversationsList.SelectedItem is not ConversationSummary conversation) return;
        await _persistenceInitialization;
        if (!_persistenceAvailable) return;
        try
        {
            Transcript.Text = await _conversationStore.GetTranscriptAsync(conversation.Id);
            TranscriptPlaceholder.Visibility = string.IsNullOrEmpty(Transcript.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
            RunStatus.Text = conversation.Status switch
            {
                "completed" => "지난 대화를 불러왔어요.",
                "cancelled" => "중단한 대화를 불러왔어요.",
                "interrupted" => "앱 종료로 중단된 대화예요.",
                _ => "지난 대화를 불러왔어요.",
            };
        }
        catch (Exception exception)
        {
            AddDiagnostic($"대화 내용 불러오기 실패: {exception.Message}");
        }
    }

    private void UpdateCommandState()
    {
        if (!IsInitialized) return;
        SendButton.IsEnabled = _isConnected && _isModelConfigured && !_isRunning && !string.IsNullOrWhiteSpace(ConversationInput.Text);
        CancelButton.Visibility = _isRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Sidecar_StatusChanged(object? sender, SidecarStatus status) =>
        Dispatcher.InvokeAsync(() =>
        {
            _isConnected = status.State == SidecarState.Connected;
            if (!_isConnected) _isModelConfigured = false;
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
            if (status.State == SidecarState.Connected) _ = ConfigureStoredProviderAsync();
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
        _sidecar.ToolInvocationReceived -= Sidecar_ToolInvocationReceived;
        _quickAccessHotkey.Pressed -= QuickAccessHotkey_Pressed;
        _quickAccessHotkey.Dispose();
        await _sidecar.DisposeAsync();
        await _persistenceInitialization;
        _conversationStore.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
