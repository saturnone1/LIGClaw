using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Scheduling;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Sidecar;
using LIGClaw.Desktop.Infrastructure.Tools;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;

namespace LIGClaw.Desktop;

public partial class MainWindow : Window
{
    private readonly SidecarSupervisor _sidecar = new();
    private readonly QuickAccessHotkey _quickAccessHotkey = new();
    private readonly QuickAccessShortcutStore _quickAccessShortcutStore = new();
    private readonly ModelConnectionSettingsStore _modelSettingsStore = new();
    private readonly McpConnectionSettingsStore _mcpSettingsStore = new();
    private readonly SemanticMemorySettingsStore _semanticMemorySettingsStore = new();
    private readonly HttpClient _semanticMemoryHttpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly DiagnosticBundleService _diagnosticBundleService = new();
    private readonly ObservableCollection<string> _diagnostics = [];
    private readonly ConversationStore _conversationStore = ConversationStore.CreateDefault();
    private readonly SemanticMemoryRepository _memories;
    private readonly ToolInvocationPolicy _toolInvocationPolicy = new();
    private readonly ConversationRunController _conversationRun;
    private readonly ConversationToolInvocationController _conversationTools;
    private readonly IUserNotificationService _notifications;
    private QuickAccessShortcut _configuredQuickAccessShortcut = QuickAccessShortcutCatalog.Default;
    private WindowsPlatformProfile? _platformProfile;
    private WindowsToolHost? _windowsToolHost;
    private ToolInvocationCoordinator? _toolInvocationCoordinator;
    private DurableNotificationScheduler? _scheduler;
    private DurableAgentJobScheduler? _agentJobScheduler;
    private SidecarAgentJobExecutor? _agentJobExecutor;
    private LocalSubagentOrchestrator? _subagentOrchestrator;
    private Task _persistenceInitialization = Task.CompletedTask;
    private bool _persistenceAvailable;
    private bool _isConnected;
    private bool _isModelConfigured;
    private bool _refreshingConversations;
    private readonly LatestRequestController _conversationListRequests = new();
    private readonly StringBuilder _transcriptMarkdown = new();
    private readonly DispatcherTimer _transcriptRenderTimer;
    private bool _pendingTranscriptFollowOutput;
    private double? _pendingTranscriptVerticalOffset;
    private ActivityView? _activityView;
    private MemoryPage? _memoryPage;
    private SchedulePage? _schedulePage;
    private AgentJobsPage? _agentJobsPage;
    private SettingsPage? _settingsPage;
    private DiagnosticsWindow? _diagnosticsWindow;

    internal MainWindow(IUserNotificationService notifications)
    {
        _notifications = notifications;
        _conversationRun = new ConversationRunController(_toolInvocationPolicy);
        _conversationTools = new ConversationToolInvocationController(_conversationRun);
        var semanticSearch = new SemanticMemorySearchService(
            _conversationStore,
            new OpenAiSemanticMemoryEmbeddingClient(_semanticMemoryHttpClient),
            _semanticMemorySettingsStore.Load);
        _memories = new SemanticMemoryRepository(
            _conversationStore, semanticSearch, _semanticMemorySettingsStore.Load);
        InitializeComponent();
        _transcriptRenderTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(50),
            DispatcherPriority.Render,
            (_, _) => RenderPendingTranscript(),
            Dispatcher)
        {
            IsEnabled = false,
        };
        _sidecar.StatusChanged += Sidecar_StatusChanged;
        _sidecar.DiagnosticMessage += Sidecar_DiagnosticMessage;
        _sidecar.AgentEventReceived += Sidecar_AgentEventReceived;
        _sidecar.ToolInvocationReceived += Sidecar_ToolInvocationReceived;
        _quickAccessHotkey.Pressed += QuickAccessHotkey_Pressed;
        Loaded += (_, _) =>
        {
            ApplyResponsiveLayout();
            UpdateConversationMode(hasContent: false);
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
        if (_platformProfile is not null)
        {
            _windowsToolHost = new WindowsToolHost(
                _platformProfile,
                notifications: _notifications,
                undoJournal: _conversationStore,
                memories: _memories,
                schedules: _conversationStore,
                agentJobs: _conversationStore,
                getSubagentOrchestrator: () => _subagentOrchestrator,
                loadMcpSettings: _mcpSettingsStore.Load,
                callMcp: _sidecar.CallMcpAsync);
            _toolInvocationCoordinator = new ToolInvocationCoordinator(
                _toolInvocationPolicy,
                _windowsToolHost,
                RequestToolApprovalAsync,
                _conversationStore,
                _conversationStore);
        }

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
            await _conversationStore.ReconcileSubagentTasksOnStartupAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            _persistenceAvailable = true;
            try
            {
                _scheduler = new DurableNotificationScheduler(_conversationStore, _notifications);
                await _scheduler.StartAsync();
                if (_windowsToolHost is not null)
                {
                    _subagentOrchestrator = new LocalSubagentOrchestrator(
                        _sidecar,
                        _windowsToolHost,
                        _conversationStore,
                        RequestBackgroundToolApprovalAsync,
                        _modelSettingsStore.LoadRouting,
                        _conversationStore,
                        _conversationStore);
                    _agentJobExecutor = new SidecarAgentJobExecutor(
                        _sidecar,
                        _windowsToolHost,
                        RequestBackgroundToolApprovalAsync,
                        _modelSettingsStore.LoadRouting,
                        _conversationStore,
                        _conversationStore);
                    _agentJobScheduler = new DurableAgentJobScheduler(
                        _conversationStore,
                        _agentJobExecutor,
                        NotifyAgentJobCompletedAsync);
                    await _agentJobScheduler.StartAsync();
                }
            }
            catch (Exception exception)
            {
                if (_scheduler is not null)
                {
                    try
                    {
                        await _scheduler.DisposeAsync();
                    }
                    catch
                    {
                        // 시작 실패 원인을 보존하고 대화·기억 저장소는 계속 사용한다.
                    }
                }
                _scheduler = null;
                AddDiagnostic($"예약 실행 서비스 초기화 실패: {exception.Message}");
            }
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

    internal void ShowUnexpectedUiFailure()
    {
        ShowHome();
        ShowRunFailure("화면을 처리하는 중 문제가 생겼어요. 같은 작업을 다시 시도해 주세요. 문제가 계속되면 앱을 다시 시작해 주세요.");
        _ = Dispatcher.BeginInvoke(FocusRequestInput, DispatcherPriority.Input);
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
        var result = await _sidecar.ConfigureProviderAsync(
            new ProviderConfigureParams(settings.BaseUrl, settings.ApiKey, settings.Model),
            cancellationToken);
        if (!result.Configured)
            throw new InvalidDataException("Sidecar가 모델 설정 적용을 확인하지 못했습니다.");
        _isModelConfigured = true;
        HideConnectionBanner();
        UpdateCommandState();
    }

    internal async Task<McpConnectionStatus> ApplyMcpAsync(
        McpConnectionSettings? settings,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> connections = settings is null
            ? []
            : [new Dictionary<string, object?>
            {
                ["id"] = "knowledge",
                ["displayName"] = settings.DisplayName,
                ["transport"] = settings.Transport,
                ["url"] = settings.Url,
                ["enabled"] = settings.Enabled,
                ["readOnly"] = true,
                ["allowedTools"] = settings.AllowedTools,
            }];
        if (settings?.Transport == "stdio")
            ((Dictionary<string, object?>)connections[0])["executableId"] = "ligclaw-sample-rag";
        if (settings?.Transport == "streamable_http" && !string.IsNullOrWhiteSpace(settings.AuthorizationToken))
            ((Dictionary<string, object?>)connections[0])["authorization"] = settings.AuthorizationToken;
        var result = await _sidecar.ConfigureMcpAsync(connections, cancellationToken);
        if (result.Connections.Count == 0) return new McpConnectionStatus("disabled", 0, [], []);
        return ReadMcpStatus(result.Connections[0]);
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

    internal Task CreateDiagnosticBundleAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        return _diagnosticBundleService.CreateAsync(
            destinationPath,
            _platformProfile?.DisplayName ?? "unknown",
            _diagnostics.ToArray(),
            cancellationToken);
    }

    private async Task ConfigureStoredProviderAsync()
    {
        try
        {
            var settings = _modelSettingsStore.Load();
            if (settings is null)
            {
                SetRunStatus("설정에서 모델 API를 연결해 주세요.");
                ShowConnectionBanner(
                    "대화를 시작하려면 먼저 모델 API를 연결해 주세요.",
                    "Settings",
                    "설정 열기",
                    isDanger: false);
                return;
            }
            await ApplyProviderAsync(settings);
            SetRunStatus("요청을 입력해 주세요.");
        }
        catch (Exception exception)
        {
            AddDiagnostic($"모델 설정 적용 실패: {exception.Message}");
            SetRunStatus("모델 연결을 확인해 주세요.");
            ShowConnectionBanner(
                "저장된 모델 연결을 적용하지 못했어요. 설정을 확인해 주세요.",
                "Settings",
                "설정 열기",
                isDanger: true);
        }
    }

    private async Task ConfigureStoredMcpAsync()
    {
        try
        {
            var settings = _mcpSettingsStore.Load();
            if (settings is null) return;
            var status = await ApplyMcpAsync(settings);
            AddDiagnostic(status.IsConnected
                ? $"MCP 연결됨: {settings.DisplayName}, 도구 {status.ToolCount}개"
                : $"MCP 연결 실패: {settings.DisplayName}");
        }
        catch (Exception exception)
        {
            AddDiagnostic($"MCP 설정 적용 실패: {exception.Message}");
        }
    }

    private static McpConnectionStatus ReadMcpStatus(IReadOnlyDictionary<string, object?> connection)
    {
        static string ReadString(object? value) => value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "failed",
            string text => text,
            _ => "failed",
        };
        static int ReadInt(object? value) => value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var number) => number,
            int number => number,
            long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
            _ => 0,
        };
        static IReadOnlyList<string> ReadStrings(object? value)
        {
            if (value is JsonElement { ValueKind: JsonValueKind.Array } element)
                return element.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .OfType<string>()
                    .ToArray();
            if (value is IEnumerable<string> strings) return strings.ToArray();
            return [];
        }
        connection.TryGetValue("state", out var state);
        connection.TryGetValue("toolCount", out var toolCount);
        connection.TryGetValue("tools", out var tools);
        connection.TryGetValue("allowedTools", out var allowedTools);
        return new McpConnectionStatus(ReadString(state), ReadInt(toolCount), ReadStrings(tools), ReadStrings(allowedTools));
    }

    private void QuickAccessHotkey_Pressed(object? sender, EventArgs e) =>
        (System.Windows.Application.Current as App)?.ShowQuickInput();

    private void Home_Click(object sender, RoutedEventArgs e) => NavigateHome();

    private void NewConversation_Click(object sender, RoutedEventArgs e)
    {
        if (_conversationRun.IsRunning)
        {
            SetRunStatus("현재 요청을 마친 뒤 새 대화를 시작할 수 있어요.");
            return;
        }
        ConversationSwitcherPopup.IsOpen = false;
        _ = _conversationRun.StartNewConversation();
        RecentConversationsList.SelectedItem = null;
        UserMessageBubble.Visibility = Visibility.Collapsed;
        SetTranscriptMarkdown(string.Empty);
        TranscriptPlaceholder.Visibility = Visibility.Visible;
        ConversationSubtitle.Text = "새 대화입니다. 첫 요청을 보내면 하나의 대화 흐름으로 계속 이어집니다.";
        ShowHome();
        SetRunStatus("새 대화를 시작할 준비가 됐어요.");
        FocusRequestInput();
    }

    internal void NavigateHome()
    {
        ShowHome();
        _ = Dispatcher.BeginInvoke(FocusRequestInput, DispatcherPriority.Input);
    }

    private void ShowHome()
    {
        ShellPageHost.Visibility = Visibility.Collapsed;
        ConversationPage.Visibility = Visibility.Visible;
        SetSelectedDestination(HomeNavigationButton);
    }

    private void ShowShellPage(FrameworkElement page, Button selectedNavigation)
    {
        ConversationSwitcherPopup.IsOpen = false;
        ShellPageHost.Content = page;
        ConversationPage.Visibility = Visibility.Collapsed;
        ShellPageHost.Visibility = Visibility.Visible;
        SetSelectedDestination(selectedNavigation);
        page.Focus();
    }

    private void ConversationSwitcher_Click(object sender, RoutedEventArgs e)
    {
        ConversationSwitcherPopup.IsOpen = !ConversationSwitcherPopup.IsOpen;
        if (ConversationSwitcherPopup.IsOpen)
        {
            _ = RefreshConversationListAsync(ConversationSearchBox.Text);
            _ = Dispatcher.BeginInvoke(() =>
            {
                ConversationSearchBox.Focus();
                ConversationSearchBox.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    private async void ConversationSearchBox_TextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        var request = _conversationListRequests.Begin();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(180), request.CancellationToken);
            await RefreshConversationListAsync(ConversationSearchBox.Text, request);
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ClearConversationSearch_Click(object sender, RoutedEventArgs e)
    {
        ConversationSearchBox.Clear();
        ConversationSearchBox.Focus();
    }

    private void SetSelectedDestination(Button selected)
    {
        foreach (var button in new[]
                 {
                     HomeNavigationButton,
                     SettingsNavigationButton,
                     ActivityNavigationButton,
                     MemoryNavigationButton,
                     ScheduleNavigationButton,
                     AgentJobsNavigationButton,
                 })
            button.Tag = ReferenceEquals(button, selected) ? "Selected" : null;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settingsPage ??= new SettingsPage(this);
            ShowShellPage(_settingsPage, SettingsNavigationButton);
        }
        catch (Exception exception)
        {
            AddDiagnostic($"설정 화면 오류: {exception.Message}");
            System.Windows.MessageBox.Show(this, "설정 화면을 열지 못했습니다.", "LIGClaw", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Activity_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _persistenceInitialization;
            if (!_persistenceAvailable)
            {
                AddDiagnostic("실행 활동 저장소를 사용할 수 없습니다.");
                return;
            }
            var activity = await _conversationStore.GetRecentToolActivityAsync();
            var pendingUndo = await _conversationStore.GetPendingUndoActivityAsync();
            _activityView ??= CreateActivityView();
            _activityView.SetItems(activity, pendingUndo);
            ShowShellPage(_activityView, ActivityNavigationButton);
        }
        catch (Exception exception)
        {
            AddDiagnostic($"실행 활동 불러오기 실패: {exception.Message}");
            System.Windows.MessageBox.Show(this, "실행 활동 화면을 열지 못했습니다.", "LIGClaw", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private ActivityView CreateActivityView()
    {
        var view = new ActivityView();
        view.UndoRequested += ActivityView_UndoRequested;
        return view;
    }

    private async void ActivityView_UndoRequested(object? sender, string undoId)
    {
        try
        {
            var succeeded = await ExecuteManualUndoAsync(undoId);
            if (_activityView is null) return;
            var activity = await _conversationStore.GetRecentToolActivityAsync();
            var pendingUndo = await _conversationStore.GetPendingUndoActivityAsync();
            _activityView.SetItems(activity, pendingUndo);
            _activityView.SetFeedback(
                succeeded ? "파일 작업을 되돌렸어요." : "파일 작업을 되돌리지 못했어요.",
                isError: !succeeded);
        }
        catch (Exception exception)
        {
            AddDiagnostic($"파일 되돌리기 화면 갱신 실패: {exception.Message}");
            _activityView?.SetFeedback("파일 작업을 되돌리지 못했습니다.", isError: true);
        }
    }

    private async void Memory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _persistenceInitialization;
            if (!_persistenceAvailable)
            {
                AddDiagnostic("개인 기억 저장소를 사용할 수 없습니다.");
                return;
            }
            _memoryPage ??= new MemoryPage(_memories);
            ShowShellPage(_memoryPage, MemoryNavigationButton);
            await _memoryPage.RefreshAsync();
        }
        catch (Exception exception)
        {
            AddDiagnostic($"기억 관리 화면 오류: {exception.Message}");
            System.Windows.MessageBox.Show(this, "기억 관리 화면을 열지 못했습니다.", "LIGClaw", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Schedule_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _persistenceInitialization;
            if (!_persistenceAvailable)
            {
                AddDiagnostic("예약 저장소를 사용할 수 없습니다.");
                return;
            }
            _schedulePage ??= new SchedulePage(_conversationStore);
            ShowShellPage(_schedulePage, ScheduleNavigationButton);
            await _schedulePage.RefreshAsync();
        }
        catch (Exception exception)
        {
            AddDiagnostic($"예약 관리 화면 오류: {exception.Message}");
            System.Windows.MessageBox.Show(this, "예약 관리 화면을 열지 못했습니다.", "LIGClaw", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<bool> ExecuteManualUndoAsync(string undoId)
    {
        if (_conversationRun.IsRunning || _toolInvocationCoordinator is null)
        {
            AddDiagnostic("다른 요청이 실행 중일 때는 파일 작업을 되돌릴 수 없습니다.");
            return false;
        }
        var conversationId = $"activity-{Guid.NewGuid():N}";
        var runId = Guid.NewGuid().ToString("N");
        _toolInvocationPolicy.BeginRun(conversationId, runId);
        var execution = new WindowsToolExecutionResult(
            false,
            new Dictionary<string, object?>(),
            "파일 되돌리기를 실행하지 못했어요.");
        try
        {
            execution = await _toolInvocationCoordinator.ExecuteAsync(new ToolInvokeParams(
                Guid.NewGuid().ToString("N"),
                conversationId,
                runId,
                "file.undo.v1",
                "R2",
                new Dictionary<string, object?>
                {
                    ["undoId"] = undoId,
                    ["reason"] = "실행 활동에서 사용자가 되돌리기를 선택했기 때문에",
                }));
        }
        finally
        {
            _toolInvocationPolicy.EndRun(conversationId, runId);
        }
        AddDiagnostic(execution.ActivitySummary ?? execution.Error ?? "파일 되돌리기를 처리했어요.");
        return execution.Success;
    }

    private void RestartSidecar_Click(object sender, RoutedEventArgs e)
    {
        _isConnected = false;
        RestartButton.Visibility = Visibility.Collapsed;
        SetConnectionCopy("다시 연결하고 있어요", "잠시만 기다려 주세요.", "연결 중", SidecarState.Restarting);
        UpdateCommandState();
        _sidecar.RequestRestart();
    }

    private void ConnectionBannerAction_Click(object sender, RoutedEventArgs e)
    {
        if (StringComparer.Ordinal.Equals(ConnectionBannerActionButton.Tag as string, "Reconnect"))
            RestartSidecar_Click(sender, e);
        else
            Settings_Click(sender, e);
    }

    private void ShowConnectionBanner(string message, string action, string actionText, bool isDanger)
    {
        ConnectionBanner.Style = (Style)FindResource(isDanger ? "DangerBannerStyle" : "WarningBannerStyle");
        ConnectionBannerText.Text = message;
        ConnectionBannerActionButton.Tag = action;
        ConnectionBannerActionButton.Content = actionText;
        ConnectionBanner.Visibility = Visibility.Visible;
    }

    private void HideConnectionBanner() => ConnectionBanner.Visibility = Visibility.Collapsed;

    private void ConversationInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        InputPlaceholder.Visibility = string.IsNullOrEmpty(ConversationInput.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateCommandState();
    }

    private void ExamplePrompt_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string prompt } || string.IsNullOrWhiteSpace(prompt)) return;
        ConversationInput.Text = prompt;
        ConversationInput.CaretIndex = ConversationInput.Text.Length;
        FocusRequestInput();
    }

    private void NewContentIndicator_Click(object sender, RoutedEventArgs e)
    {
        Transcript.ScrollToEnd();
        NewContentIndicator.Visibility = Visibility.Collapsed;
    }

    private void Transcript_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (ConversationViewportPolicy.ShouldFollowOutput(
                Transcript.ExtentHeight,
                Transcript.ViewportHeight,
                Transcript.VerticalOffset))
            NewContentIndicator.Visibility = Visibility.Collapsed;
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
            SetRunStatus("아직 준비 중이에요. 잠시 후 다시 시도해 주세요.");
            return;
        }

        var identity = _conversationRun.BeginRun();
        var conversationId = identity.ConversationId;
        var runId = identity.RunId;
        RecentConversationsList.IsEnabled = false;
        UserMessageText.Text = input;
        UserMessageBubble.Visibility = Visibility.Collapsed;
        ConversationSubtitle.Text = identity.IsNewConversation
            ? "새 대화를 시작했습니다. 다음 요청도 이 대화에 이어집니다."
            : "현재 대화에 후속 요청을 이어서 보냈습니다.";
        ConversationInput.Clear();
        if (identity.IsNewConversation) SetTranscriptMarkdown(string.Empty);
        ConversationTranscriptFormatter.AppendTurnStart(_transcriptMarkdown, input);
        UpdateConversationMode(hasContent: true);
        RenderTranscript(followOutput: true);
        TranscriptPlaceholder.Visibility = Visibility.Collapsed;
        SetRunStatus("요청을 확인하고 있어요…");
        UpdateCommandState();
        try
        {
            var history = await PersistConversationRunStartAsync(conversationId, runId, input);
            var started = await _sidecar.StartConversationAsync(
                conversationId,
                runId,
                input,
                "cline",
                history,
                identity.CancellationToken,
                _modelSettingsStore.LoadRouting() is { } routing
                    ? ModelRoutingPayload.Create(routing)
                    : null);
            if (!started.Accepted || !StringComparer.Ordinal.Equals(started.RunId, runId))
                throw new InvalidDataException("Sidecar가 요청 실행 ID를 확인하지 못했습니다.");
            if (_conversationRun.IsRunning && StringComparer.Ordinal.Equals(_conversationRun.ActiveConversationId, conversationId))
            {
                SetRunStatus("답변을 준비하고 있어요…");
            }
        }
        catch (OperationCanceledException) when (identity.CancellationToken.IsCancellationRequested)
        {
            AddDiagnostic("요청 시작을 사용자 취소 또는 연결 종료에 따라 중단했습니다.");
        }
        catch (Exception exception)
        {
            AddDiagnostic($"요청 시작 실패: {exception.Message}");
            ShowRunFailure("요청을 시작하지 못했어요. 잠시 후 다시 시도해 주세요.");
        }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_conversationRun.TryRequestCancellation(out var activeRun) || activeRun is null) return;
        var conversationId = activeRun.ConversationId;
        SetRunStatus("요청을 중단하고 있어요…");
        UpdateCommandState();
        try
        {
            var result = await _sidecar.CancelConversationAsync(conversationId);
            if (_conversationRun.IsRunning && StringComparer.Ordinal.Equals(_conversationRun.ActiveConversationId, conversationId))
            {
                SetRunStatus(result.Cancelled ? "요청을 중단하고 있어요…" : "이미 처리가 끝났어요.");
            }
        }
        catch (Exception exception)
        {
            AddDiagnostic($"요청 중단 실패: {exception.Message}");
            SetRunStatus("중단 요청을 전달하지 못했어요. 현재 요청 상태를 확인하고 있어요…");
        }
    }

    private async void Sidecar_AgentEventReceived(object? sender, AgentEvent agentEvent)
    {
        if (_agentJobExecutor?.TryHandleAgentEvent(agentEvent) == true) return;
        if (_subagentOrchestrator?.TryHandleAgentEvent(agentEvent) == true) return;
        try
        {
            await Dispatcher.InvokeAsync(() => HandleAgentEventAsync(agentEvent)).Task.Unwrap();
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                AddDiagnostic($"Agent 이벤트 처리 실패: {exception.Message}");
                if (_conversationRun.IsRunning)
                    ShowRunFailure("응답을 표시하는 중 문제가 발생했어요. 다시 시도해 주세요.");
            });
        }
    }

    private async Task HandleAgentEventAsync(AgentEvent agentEvent)
    {
        if (!_conversationRun.IsActiveRun(agentEvent.ConversationId, agentEvent.RunId))
        {
            AddDiagnostic("현재 요청과 일치하지 않는 Agent 이벤트를 무시했습니다.");
            return;
        }
        var decision = ConversationAgentEventPolicy.Evaluate(agentEvent);
        if (decision.Diagnostic is not null) AddDiagnostic(decision.Diagnostic);
        if (decision.RunStatus is not null) SetRunStatus(decision.RunStatus);
        switch (decision.Action)
        {
            case ConversationAgentEventAction.AppendText:
                AppendTranscript(decision.Transcript);
                break;
            case ConversationAgentEventAction.AppendStatus:
                if (decision.Transcript is not null) AppendTranscriptStatus(decision.Transcript);
                if (decision.IsTerminal) CompleteRun(decision.RunStatus!);
                break;
            case ConversationAgentEventAction.Complete:
                CompleteRun(decision.RunStatus!);
                break;
            case ConversationAgentEventAction.Fail:
                ShowRunFailure(decision.RunStatus!);
                break;
        }
        await PersistAgentEventAsync(agentEvent);
        if (decision.IsTerminal)
            await RefreshRecentConversationsAsync();
    }

    private async void Sidecar_ToolInvocationReceived(object? sender, ToolInvokeParams invocation)
    {
        if (_agentJobExecutor is not null &&
            await _agentJobExecutor.TryHandleToolInvocationAsync(invocation)) return;
        if (_subagentOrchestrator is not null &&
            await _subagentOrchestrator.TryHandleToolInvocationAsync(invocation)) return;
        await Dispatcher.InvokeAsync(() => SetRunStatus(_conversationTools.PreparingStatus));
        var outcome = await _conversationTools.ExecuteAsync(
            invocation,
            _toolInvocationCoordinator is null
                ? null
                : _toolInvocationCoordinator.ExecuteAsync);
        var execution = outcome.Execution;
        await Dispatcher.InvokeAsync(() =>
        {
            SetRunStatus(outcome.RunStatus);
            if (outcome.Diagnostic is not null) AddDiagnostic(outcome.Diagnostic);
        });

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

    private async Task<ToolApprovalChoice> RequestToolApprovalAsync(
        WindowsToolApprovalPrompt prompt,
        bool allowAlways,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.InvokeAsync(() =>
        {
            SetRunStatus("실행 승인을 기다리고 있어요…");
            var approvalWindow = new ToolApprovalWindow(prompt, allowAlways) { Owner = this };
            using var cancellationRegistration = cancellationToken.Register(() =>
                approvalWindow.Dispatcher.BeginInvoke(() =>
                {
                    if (approvalWindow.IsVisible) approvalWindow.Close();
                }));
            _ = approvalWindow.ShowDialog();
            if (!cancellationToken.IsCancellationRequested)
            {
                SetRunStatus(approvalWindow.Choice != ToolApprovalChoice.Deny
                    ? "승인한 동작을 실행하고 있어요…"
                    : "승인하지 않아 실행을 건너뛰었어요.");
            }
            return approvalWindow.Choice;
        });
    }

    private async void AgentJobs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _persistenceInitialization;
            if (!_persistenceAvailable)
            {
                AddDiagnostic("Agent 작업 저장소를 사용할 수 없습니다.");
                return;
            }
            _agentJobsPage ??= new AgentJobsPage(_conversationStore);
            ShowShellPage(_agentJobsPage, AgentJobsNavigationButton);
            await _agentJobsPage.RefreshAsync();
        }
        catch (Exception exception)
        {
            AddDiagnostic($"Agent 작업 관리 화면 오류: {exception.Message}");
            System.Windows.MessageBox.Show(this, "Agent 작업 관리 화면을 열지 못했습니다.", "LIGClaw", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<ToolApprovalChoice> RequestBackgroundToolApprovalAsync(
        WindowsToolApprovalPrompt prompt,
        bool allowAlways,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.InvokeAsync(() =>
        {
            var approvalWindow = new ToolApprovalWindow(prompt, allowAlways) { Owner = this };
            using var cancellationRegistration = cancellationToken.Register(() =>
                approvalWindow.Dispatcher.BeginInvoke(() =>
                {
                    if (approvalWindow.IsVisible) approvalWindow.Close();
                }));
            _ = approvalWindow.ShowDialog();
            return approvalWindow.Choice;
        });
    }

    private async Task NotifyAgentJobCompletedAsync(
        LIGClaw.Application.Scheduling.AgentJobClaim claim,
        LIGClaw.Application.Scheduling.AgentJobRunResult result,
        CancellationToken cancellationToken)
    {
        var message = result.Succeeded
            ? "작업이 완료되었습니다. LIGClaw의 Agent 작업에서 결과를 확인하세요."
            : "작업을 완료하지 못했습니다. LIGClaw의 Agent 작업에서 오류와 재시도를 확인하세요.";
        await _notifications.ShowAsync($"Agent 작업 · {claim.Job.Title}", message, cancellationToken).ConfigureAwait(false);
        Task? refresh = null;
        await Dispatcher.InvokeAsync(() =>
        {
            if (_agentJobsPage is not null && _agentJobsPage.IsLoaded) refresh = _agentJobsPage.RefreshAsync(message);
        });
        if (refresh is not null) await refresh.ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<CapabilityGrant>> GetCapabilityGrantsAsync(
        CancellationToken cancellationToken = default)
    {
        await _persistenceInitialization.ConfigureAwait(true);
        if (!_persistenceAvailable) return [];
        return await _conversationStore.GetActiveGrantsAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(true);
    }

    internal async Task CreateDataBackupAsync(string path, CancellationToken cancellationToken = default)
    {
        await _persistenceInitialization.ConfigureAwait(true);
        await _conversationStore.CreateBackupAsync(path, cancellationToken).ConfigureAwait(true);
    }

    internal async Task StageDataRestoreAsync(string path, CancellationToken cancellationToken = default)
    {
        await _persistenceInitialization.ConfigureAwait(true);
        await _conversationStore.StageRestoreAsync(path, cancellationToken).ConfigureAwait(true);
    }

    internal async Task<int> CleanupOperationalHistoryAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
    {
        await _persistenceInitialization.ConfigureAwait(true);
        return await _conversationStore.CleanupOperationalHistoryAsync(cutoffUtc, cancellationToken).ConfigureAwait(true);
    }

    internal async Task RevokeCapabilityGrantAsync(string grantId, CancellationToken cancellationToken = default)
    {
        await _persistenceInitialization.ConfigureAwait(true);
        if (!_persistenceAvailable) return;
        await _conversationStore.RevokeGrantAsync(grantId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(true);
    }

    private void CompleteRun(string status)
    {
        _ = _conversationRun.CompleteActiveRun();
        SetRunStatus(status);
        RecentConversationsList.IsEnabled = true;
        UpdateCommandState();
    }

    private void ShowRunFailure(string message)
    {
        var failedConversationId = _conversationRun.ActiveConversationId;
        var failedRunId = _conversationRun.ActiveRunId;
        AppendTranscriptStatus(message);
        TranscriptPlaceholder.Visibility = Visibility.Collapsed;
        CompleteRun(message);
        if (failedConversationId is not null && failedRunId is not null)
            _ = MarkRunAsync(failedConversationId, failedRunId, "failed");
    }

    private void AppendTranscriptStatus(string message)
    {
        var viewport = CaptureTranscriptViewport();
        if (_transcriptMarkdown.Length > 0) _transcriptMarkdown.AppendLine().AppendLine();
        _transcriptMarkdown.Append("> ").AppendLine(message);
        UpdateConversationMode(hasContent: true);
        RenderTranscript(viewport.FollowOutput, viewport.VerticalOffset);
    }

    private void SetTranscriptMarkdown(string markdown, bool scrollToEnd = false)
    {
        _transcriptMarkdown.Clear();
        _transcriptMarkdown.Append(markdown);
        TranscriptPlaceholder.Visibility = string.IsNullOrWhiteSpace(markdown)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateConversationMode(!string.IsNullOrWhiteSpace(markdown));
        RenderTranscript(scrollToEnd);
    }

    private void AppendTranscript(string? text)
    {
        var viewport = CaptureTranscriptViewport();
        _transcriptMarkdown.Append(text);
        if (!string.IsNullOrWhiteSpace(text))
        {
            TranscriptPlaceholder.Visibility = Visibility.Collapsed;
            UpdateConversationMode(hasContent: true);
        }
        RenderTranscript(viewport.FollowOutput, viewport.VerticalOffset);
    }

    private (bool FollowOutput, double VerticalOffset) CaptureTranscriptViewport() =>
        (ConversationViewportPolicy.ShouldFollowOutput(
            Transcript.ExtentHeight,
            Transcript.ViewportHeight,
            Transcript.VerticalOffset), Transcript.VerticalOffset);

    private void RenderTranscript(bool followOutput = false, double? preserveVerticalOffset = null)
    {
        _pendingTranscriptFollowOutput = followOutput;
        _pendingTranscriptVerticalOffset = followOutput ? null : preserveVerticalOffset;
        _transcriptRenderTimer.Interval = ConversationViewportPolicy.RenderIntervalForLength(_transcriptMarkdown.Length);
        if (!_transcriptRenderTimer.IsEnabled) _transcriptRenderTimer.Start();
    }

    private void UpdateConversationMode(bool hasContent)
    {
        if (!IsInitialized) return;
        var layout = ConversationLayoutPolicy.ForContent(hasContent);
        ConversationSurface.Visibility = layout.ShowConversationSurface ? Visibility.Visible : Visibility.Collapsed;
        ComposerExamples.Visibility = layout.ShowExamples ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Controls.Grid.SetRow(ComposerHost, layout.ComposerRow);
        ComposerHost.VerticalAlignment = layout.Mode == ConversationLayoutMode.Docked
            ? VerticalAlignment.Stretch
            : VerticalAlignment.Center;
        ComposerHost.MaxWidth = (double)FindResource(layout.ComposerWidthResourceKey);
        ComposerHost.Margin = layout.Mode == ConversationLayoutMode.Docked
            ? new Thickness(0, 16, 0, 0)
            : new Thickness(0, 20, 0, 0);
    }

    private void RenderPendingTranscript()
    {
        _transcriptRenderTimer.Stop();
        var followOutput = _pendingTranscriptFollowOutput;
        var preserveVerticalOffset = _pendingTranscriptVerticalOffset;
        _pendingTranscriptFollowOutput = false;
        _pendingTranscriptVerticalOffset = null;
        var secondary = TryFindResource("TextSecondaryBrush") as Brush ?? Transcript.Foreground;
        var border = TryFindResource("BorderBrush") as Brush ?? Brushes.LightGray;
        var mutedSurface = TryFindResource("SurfaceMutedBrush") as Brush ?? Brushes.Gainsboro;
        var mono = TryFindResource("MonoFontFamily") as FontFamily ?? new FontFamily("Consolas");
        Transcript.Document = MarkdownDocumentRenderer.Render(
            _transcriptMarkdown.ToString(),
            Transcript.FontFamily,
            Transcript.Foreground,
            secondary,
            border,
            mutedSurface,
            mono);
        Transcript.UpdateLayout();
        if (followOutput) Transcript.ScrollToEnd();
        else if (preserveVerticalOffset is { } offset) Transcript.ScrollToVerticalOffset(offset);
        else Transcript.ScrollToHome();
        NewContentIndicator.Visibility = ConversationViewportPolicy.ShouldShowNewContentIndicator(
            followOutput,
            preserveVerticalOffset,
            Transcript.ExtentHeight,
            Transcript.ViewportHeight,
            Transcript.VerticalOffset)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>?> PersistConversationRunStartAsync(
        string conversationId,
        string runId,
        string input)
    {
        await _persistenceInitialization;
        if (!_persistenceAvailable) return null;
        try
        {
            await _conversationStore.StartRunAsync(conversationId, runId, input, DateTimeOffset.UtcNow);
            var context = await _conversationStore.GetConversationContextAsync(conversationId);
            await RefreshRecentConversationsAsync();
            return context
                .Select(message => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["role"] = message.Role,
                    ["content"] = message.Content,
                })
                .ToArray();
        }
        catch (Exception exception)
        {
            AddDiagnostic($"대화 시작 기록 실패: {exception.Message}");
            return null;
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

    private async Task MarkRunAsync(string conversationId, string runId, string status)
    {
        await _persistenceInitialization;
        if (!_persistenceAvailable) return;
        try
        {
            await _conversationStore.MarkRunAsync(conversationId, runId, status, DateTimeOffset.UtcNow);
            await RefreshRecentConversationsAsync();
        }
        catch (Exception exception)
        {
            AddDiagnostic($"대화 상태 기록 실패: {exception.Message}");
        }
    }

    private async Task RefreshRecentConversationsAsync()
    {
        await RefreshConversationListAsync(ConversationSearchBox.Text);
    }

    private Task RefreshConversationListAsync(string? query) =>
        RefreshConversationListAsync(query, _conversationListRequests.Begin());

    private async Task RefreshConversationListAsync(string? query, LatestRequest request)
    {
        if (!_persistenceAvailable) return;
        try
        {
            var selectedId = _conversationRun.CurrentConversationId;
            var normalizedQuery = query?.Trim() ?? string.Empty;
            var conversations = normalizedQuery.Length == 0
                ? await _conversationStore.GetRecentConversationsAsync(cancellationToken: request.CancellationToken)
                : await _conversationStore.SearchConversationsAsync(
                    normalizedQuery,
                    cancellationToken: request.CancellationToken);
            if (!request.IsCurrent) return;
            _refreshingConversations = true;
            try
            {
                RecentConversationsList.ItemsSource = conversations;
                if (selectedId is not null)
                    RecentConversationsList.SelectedItem = conversations.FirstOrDefault(item => item.Id == selectedId);
                ConversationSearchStatus.Text = normalizedQuery.Length == 0
                    ? $"최근 대화 {conversations.Count}개"
                    : conversations.Count == 0
                        ? "일치하는 대화가 없습니다."
                        : $"검색 결과 {conversations.Count}개";
            }
            finally
            {
                _refreshingConversations = false;
            }
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!request.IsCurrent) return;
            AddDiagnostic($"최근 대화 불러오기 실패: {exception.Message}");
            ConversationSearchStatus.Text = "대화를 불러오지 못했습니다.";
        }
    }

    private async void RecentConversationsList_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_refreshingConversations || _conversationRun.IsRunning ||
            RecentConversationsList.SelectedItem is not ConversationSummary conversation) return;
        ConversationSwitcherPopup.IsOpen = false;
        ShowHome();
        await _persistenceInitialization;
        if (!_persistenceAvailable) return;
        try
        {
            if (!_conversationRun.SelectConversation(conversation.Id)) return;
            UserMessageBubble.Visibility = Visibility.Collapsed;
            ConversationSubtitle.Text = $"{conversation.TurnCount}턴 대화를 이어서 진행합니다.";
            SetTranscriptMarkdown(await _conversationStore.GetTranscriptAsync(conversation.Id), scrollToEnd: true);
            TranscriptPlaceholder.Visibility = _transcriptMarkdown.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            SetRunStatus(conversation.Status switch
            {
                "completed" => "지난 대화를 불러왔어요.",
                "cancelled" => "중단한 대화를 불러왔어요.",
                "interrupted" => "앱 종료로 중단된 대화예요.",
                "failed" => "실패한 요청을 확인하고 이어서 다시 시도할 수 있어요.",
                "running" => "이전 실행 상태를 확인하고 있어요.",
                _ => "지난 대화를 불러왔어요.",
            });
        }
        catch (Exception exception)
        {
            AddDiagnostic($"대화 내용 불러오기 실패: {exception.Message}");
        }
    }

    private void UpdateCommandState()
    {
        if (!IsInitialized) return;
        SendButton.IsEnabled = _isConnected && _isModelConfigured && !_conversationRun.IsRunning && !string.IsNullOrWhiteSpace(ConversationInput.Text);
        CancelButton.Visibility = _conversationRun.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = _conversationRun.IsRunning && !_conversationRun.IsCancelling;
        TimelineProgressCard.Visibility = _conversationRun.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        NewConversationButton.IsEnabled = !_conversationRun.IsRunning;
    }

    private void SetRunStatus(string status)
    {
        RunStatus.Text = status;
        TimelineProgressText.Text = status;
    }

    private void Sidecar_StatusChanged(object? sender, SidecarStatus status) =>
        Dispatcher.InvokeAsync(() =>
        {
            _isConnected = status.State == SidecarState.Connected;
            if (!_isConnected) _isModelConfigured = false;
            if (!_isConnected && _conversationRun.IsRunning)
            {
                ShowRunFailure("연결이 끊어져 요청이 중단됐어요. 다시 연결되면 재시도해 주세요.");
            }

            var statusBrush = FindStatusBrush(status.State);
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
            SupportStatusCard.Visibility = status.State is SidecarState.Faulted or SidecarState.Stopped
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (status.State is SidecarState.Faulted or SidecarState.Stopped)
            {
                ShowConnectionBanner(
                    "도우미 연결이 끊겼어요. 로컬 관리 기능은 계속 사용할 수 있습니다.",
                    "Reconnect",
                    "다시 연결",
                    isDanger: true);
            }
            else if (status.State is SidecarState.Starting or SidecarState.Restarting)
            {
                HideConnectionBanner();
            }
            SetRunStatus(status.State switch
            {
                SidecarState.Connected when !_conversationRun.IsRunning => "요청을 입력해 주세요.",
                SidecarState.Faulted when !_conversationRun.IsRunning => "연결을 복구하고 있어요…",
                SidecarState.Stopped when !_conversationRun.IsRunning => "다시 연결해 주세요.",
                _ when !_conversationRun.IsRunning => "준비하고 있어요…",
                _ => RunStatus.Text,
            });
            UpdateCommandState();
            if (status.State == SidecarState.Connected)
            {
                _ = ConfigureStoredProviderAsync();
                _ = ConfigureStoredMcpAsync();
            }
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
        var brush = FindStatusBrush(state);
        StatusIndicator.Fill = brush;
        HeaderStatusIndicator.Fill = brush;
        SupportStatusCard.Visibility = state == SidecarState.Faulted
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private Brush FindStatusBrush(SidecarState state)
    {
        var resourceKey = state switch
        {
            SidecarState.Connected => "SuccessBrush",
            SidecarState.Faulted => "DangerBrush",
            SidecarState.Starting or SidecarState.Restarting => "WarningBrush",
            _ => "InactiveBrush",
        };
        return TryFindResource(resourceKey) as Brush ?? Brushes.Gray;
    }

    private void Sidecar_DiagnosticMessage(object? sender, string message) =>
        Dispatcher.InvokeAsync(() => AddDiagnostic($"{DateTimeOffset.UtcNow:HH:mm:ss}  {message}"));

    private void AddDiagnostic(string message)
    {
        _diagnostics.Add(message);
        while (_diagnostics.Count > 100) _diagnostics.RemoveAt(0);
    }

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosticsWindow is { IsLoaded: true })
        {
            _diagnosticsWindow.Activate();
            return;
        }
        _diagnosticsWindow = new DiagnosticsWindow(_diagnostics) { Owner = this };
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show();
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout();

    private void ApplyResponsiveLayout()
    {
        if (!IsInitialized) return;
        var layout = ShellLayoutPolicy.ForWidth(ActualWidth);
        SidebarColumn.Width = new GridLength(layout.AppRailWidth);
        SidebarLayout.Margin = layout.IsCompact ? new Thickness(0, 18, 0, 16) : new Thickness(8, 18, 8, 16);
        var labelVisibility = layout.ShowRailLabels ? Visibility.Visible : Visibility.Collapsed;
        BrandCopy.Visibility = labelVisibility;
        HomeNavigationLabel.Visibility = labelVisibility;
        ActivityNavigationLabel.Visibility = labelVisibility;
        MemoryNavigationLabel.Visibility = labelVisibility;
        ScheduleNavigationLabel.Visibility = labelVisibility;
        AgentJobsNavigationLabel.Visibility = labelVisibility;
        SettingsNavigationLabel.Visibility = labelVisibility;
        SidebarFooter.Visibility = labelVisibility;
        foreach (var button in new[]
                 {
                     HomeNavigationButton,
                     ActivityNavigationButton,
                     MemoryNavigationButton,
                     ScheduleNavigationButton,
                     AgentJobsNavigationButton,
                     SettingsNavigationButton,
                 })
        {
            button.HorizontalContentAlignment = layout.ShowRailLabels
                ? System.Windows.HorizontalAlignment.Left
                : System.Windows.HorizontalAlignment.Center;
            button.Padding = layout.ShowRailLabels ? new Thickness(16, 0, 0, 0) : new Thickness(0);
        }
        ConversationPage.Margin = new Thickness(layout.HorizontalPageMargin, layout.IsCompact ? 18 : 24,
            layout.HorizontalPageMargin, layout.IsCompact ? 18 : 24);
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
        _transcriptRenderTimer.Stop();
        _conversationListRequests.Dispose();
        _diagnosticsWindow?.Close();
        _sidecar.StatusChanged -= Sidecar_StatusChanged;
        _sidecar.DiagnosticMessage -= Sidecar_DiagnosticMessage;
        _sidecar.AgentEventReceived -= Sidecar_AgentEventReceived;
        _sidecar.ToolInvocationReceived -= Sidecar_ToolInvocationReceived;
        _quickAccessHotkey.Pressed -= QuickAccessHotkey_Pressed;
        _quickAccessHotkey.Dispose();
        try
        {
            await _persistenceInitialization;
            if (_agentJobScheduler is not null) await _agentJobScheduler.DisposeAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Agent job 서비스 종료 실패: {exception.GetType().Name}");
        }
        try
        {
            await _sidecar.DisposeAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Sidecar 종료 실패: {exception.GetType().Name}");
        }
        try
        {
            await _persistenceInitialization;
            if (_scheduler is not null) await _scheduler.DisposeAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"백그라운드 서비스 종료 실패: {exception.GetType().Name}");
        }
        finally
        {
            _conversationStore.Dispose();
            _semanticMemoryHttpClient.Dispose();
            System.Windows.Application.Current.Shutdown();
        }
    }
}
