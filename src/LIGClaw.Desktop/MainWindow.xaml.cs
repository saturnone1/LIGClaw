using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LIGClaw.Application.Agents;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Application.Scheduling;
using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Capabilities;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Scheduling;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Sidecar;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Desktop.Infrastructure.Workflows;
using LIGClaw.Domain;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace LIGClaw.Desktop;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly SidecarSupervisor _sidecar = new();
    private readonly QuickAccessHotkey _quickAccessHotkey = new();
    private readonly QuickAccessShortcutStore _quickAccessShortcutStore = new();
    private readonly ModelConnectionSettingsStore _modelSettingsStore = new();
    private readonly McpConnectionSettingsStore _mcpSettingsStore = new();
    private readonly SemanticMemorySettingsStore _semanticMemorySettingsStore = new();
    private readonly HttpClient _semanticMemoryHttpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly DiagnosticBundleService _diagnosticBundleService = new();
    private readonly IScreenCapturePicker _screenCapturePicker = new WindowsScreenCapturePicker();
    private readonly IScreenTextRecognizer _screenTextRecognizer = new WindowsOcrScreenTextRecognizer();
    private readonly PreparedSensitiveContextStore _sensitiveContextStore = new();
    private readonly WindowsConnectionCredentialStore _connectionCredentials = new();
    private readonly IVoiceSettingsStore _voiceSettingsStore = new VoiceSettingsStore();
    private readonly IRoutineSuggestionSettingsStore _routineSuggestionSettingsStore = new RoutineSuggestionSettingsStore();
    private readonly IPushToTalkRecognizer _pushToTalkRecognizer = new WindowsPushToTalkRecognizer();
    private readonly PushToTalkInteractionController _pushToTalk;
    private readonly ITextToSpeechPlayer _textToSpeech = new WindowsTextToSpeechPlayer();
    private readonly ObservableCollection<string> _diagnostics = [];
    private readonly ConversationStore _conversationStore = ConversationStore.CreateDefault();
    private readonly IConversationRepository _conversationRepository;
    private readonly IOperationalAuditRepository _operationalRepository;
    private readonly IPersonalMemoryRepository _personalMemoryRepository;
    private readonly IScheduleRepository _scheduleRepository;
    private readonly IAgentJobRepository _agentJobRepository;
    private readonly ILocalSubagentRepository _subagentRepository;
    private readonly SemanticMemoryRepository _memories;
    private readonly ToolInvocationPolicy _toolInvocationPolicy = new();
    private readonly ConversationRunController _conversationRun;
    private readonly CapabilityBuilderOpenController _capabilityBuilderOpenController;
    private readonly ConversationOrchestrationController _conversationOrchestration;
    private readonly ConversationToolInvocationController _conversationTools;
    private ConversationWorkflowInvocationController? _conversationWorkflows;
    private readonly IUserNotificationService _notifications;
    private ICapabilityManagementService? _capabilityManagement;
    private IFeatureBuilderManagementService? _featureBuilderManagement;
    private DesktopFeatureBuilderTestRunner? _featureBuilderTestRunner;
    private ConnectionManagementService? _connectionManagement;
    private MailGatewayConnectionManagementService? _mailGatewayManagement;
    private SmtpConnectionManagementService? _smtpManagement;
    private ICapabilityConnectionReadinessResolver? _connectionReadiness;
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
    private bool _capabilityMcpReadAvailable;
    private bool _refreshingConversations;
    private bool _isPreparingScreenContext;
    private bool _isOpeningFeatureBuilder;
    private bool _voiceInputEnabled;
    private VoiceSettings _voiceSettings = new(false, false);
    private readonly StringBuilder _currentAssistantResponse = new();
    private bool _isSpeaking;
    private CancellationTokenSource? _textToSpeechStartCancellation;
    private readonly LatestRequestController _conversationListRequests = new();
    private readonly StringBuilder _transcriptMarkdown = new();
    private readonly DispatcherTimer _transcriptRenderTimer;
    private readonly DispatcherTimer _voiceLimitTimer;
    private bool _pendingTranscriptFollowOutput;
    private double? _pendingTranscriptVerticalOffset;
    private ActivityView? _activityView;
    private CapabilitiesPage? _capabilitiesPage;
    private FeatureBuilderPage? _featureBuilderPage;
    private ConnectionsPage? _connectionsPage;
    private MemoryPage? _memoryPage;
    private SchedulePage? _schedulePage;
    private AgentJobsPage? _agentJobsPage;
    private SettingsPage? _settingsPage;
    private DiagnosticsWindow? _diagnosticsWindow;
    private RoutineSuggestionCandidate? _routineSuggestionCandidate;

    internal MainWindow(IUserNotificationService notifications)
    {
        _notifications = notifications;
        _pushToTalk = new PushToTalkInteractionController(_pushToTalkRecognizer);
        _textToSpeech.PlaybackEnded += TextToSpeech_PlaybackEnded;
        _conversationRepository = _conversationStore.Conversations;
        _operationalRepository = _conversationStore.Operations;
        _personalMemoryRepository = _conversationStore.Memories;
        _scheduleRepository = _conversationStore.Schedules;
        _agentJobRepository = _conversationStore.AgentJobs;
        _subagentRepository = _conversationStore.Subagents;
        _conversationRun = new ConversationRunController(_toolInvocationPolicy);
        _capabilityBuilderOpenController = new CapabilityBuilderOpenController(
            _conversationRun,
            _conversationStore.Conversations);
        _conversationOrchestration = new ConversationOrchestrationController(
            _conversationRun,
            _sidecar,
            _conversationRepository,
            () => _modelSettingsStore.LoadRouting() is { } routing
                ? ModelRoutingPayload.Create(routing)
                : null);
        _conversationTools = new ConversationToolInvocationController(_conversationRun);
        var semanticSearch = new SemanticMemorySearchService(
            _personalMemoryRepository,
            new OpenAiSemanticMemoryEmbeddingClient(_semanticMemoryHttpClient),
            _semanticMemorySettingsStore.Load);
        _memories = new SemanticMemoryRepository(
            _personalMemoryRepository, semanticSearch, _semanticMemorySettingsStore.Load);
        InitializeComponent();
        _transcriptRenderTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(50),
            DispatcherPriority.Render,
            (_, _) => RenderPendingTranscript(),
            Dispatcher)
        {
            IsEnabled = false,
        };
        _voiceLimitTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(60),
            DispatcherPriority.Normal,
            (_, _) => _ = EndVoiceInputAsync(timedOut: true),
            Dispatcher)
        {
            IsEnabled = false,
        };
        _sidecar.StatusChanged += Sidecar_StatusChanged;
        _sidecar.DiagnosticMessage += Sidecar_DiagnosticMessage;
        _sidecar.AgentEventReceived += Sidecar_AgentEventReceived;
        _sidecar.ToolInvocationReceived += Sidecar_ToolInvocationReceived;
        _sidecar.WorkflowInvocationReceived += Sidecar_WorkflowInvocationReceived;
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
            ApplyVoiceSettings(_voiceSettingsStore.Load());
        }
        catch (Exception exception)
        {
            _voiceInputEnabled = false;
            AddDiagnostic($"음성 입력 설정을 불러오지 못했습니다: {exception.GetType().Name}");
        }
        try
        {
            _platformProfile = WindowsPlatformDetector.Detect();
            AddDiagnostic($"플랫폼: {_platformProfile.DisplayName}");
            if (!_platformProfile.IsSupported)
                AddDiagnostic($"이 윈도우 버전은 공식 지원 기준 {WindowsPlatformProfile.MinimumSupportedBuild}보다 낮거나 일반 PC용 운영체제가 아닙니다.");
        }
        catch (Exception exception)
        {
            AddDiagnostic($"윈도우 환경 확인 실패: {exception.GetType().Name}");
        }
        DesktopWorkflowToolMetadataCatalog? workflowTools = null;
        var httpConnectionReadiness = new ConnectionReadinessResolver(
            _conversationStore.Connections,
            _connectionCredentials);
        var mailGatewayReadiness = new MailGatewayConnectionReadinessResolver(
            _conversationStore.MailGatewayConnections,
            _connectionCredentials);
        var smtpReadiness = new SmtpConnectionReadinessResolver(
            _conversationStore.SmtpConnections,
            _connectionCredentials);
        _connectionReadiness = new CompositeConnectionReadinessResolver(
            httpConnectionReadiness,
            mailGatewayReadiness,
            smtpReadiness);
        var connectionProbe = new ConnectionHttpProbe(_connectionCredentials.ReadAsync);
        var connectionExecutionLeases = new ConnectionExecutionLeaseCoordinator();
        _connectionManagement = new ConnectionManagementService(
            _conversationStore.Connections,
            _connectionCredentials,
            httpProbe: connectionProbe,
            executionLeases: connectionExecutionLeases);
        var connectionHttpResolver = new DesktopConnectionHttpExecutionResolver(
            _conversationStore.Capabilities,
            _conversationStore.Connections,
            _connectionReadiness,
            _connectionCredentials.ReadAsync,
            connectionExecutionLeases);
        var mailGatewayWireClient = new MailGatewayWireClient();
        var mailGatewayProbe = new MailGatewayProbeAdapter(
            _connectionCredentials.ReadAsync,
            mailGatewayWireClient);
        _mailGatewayManagement = new MailGatewayConnectionManagementService(
            _conversationStore.MailGatewayConnections,
            _connectionCredentials,
            probe: mailGatewayProbe,
            executionLeaseCoordinator: connectionExecutionLeases);
        var mailGatewayResolver = new DesktopMailGatewayExecutionResolver(
            _conversationStore.Capabilities,
            _conversationStore.MailGatewayConnections,
            _connectionReadiness,
            _connectionCredentials.ReadAsync,
            connectionExecutionLeases);
        var smtpWireClient = new SmtpWireClient();
        var smtpProbe = new SmtpConnectionProbeAdapter(
            _connectionCredentials.ReadSmtpAsync,
            smtpWireClient.ProbeAsync);
        _smtpManagement = new SmtpConnectionManagementService(
            _conversationStore.SmtpConnections,
            _connectionCredentials,
            probe: smtpProbe,
            executionLeaseCoordinator: connectionExecutionLeases);
        var smtpResolver = new DesktopSmtpExecutionResolver(
            _conversationStore.Capabilities,
            _conversationStore.SmtpConnections,
            _connectionReadiness,
            _connectionCredentials.ReadSmtpAsync,
            connectionExecutionLeases);
        if (_platformProfile is not null)
        {
            _windowsToolHost = new WindowsToolHost(
                _platformProfile,
                notifications: _notifications,
                undoJournal: _operationalRepository,
                memories: _memories,
                schedules: _scheduleRepository,
                agentJobs: _agentJobRepository,
                getSubagentOrchestrator: () => _subagentOrchestrator,
                loadMcpSettings: _mcpSettingsStore.Load,
                callMcp: _sidecar.CallMcpAsync,
                connectionHttpResolver: connectionHttpResolver,
                mailGatewayResolver: mailGatewayResolver,
                mailOutbox: _conversationStore.MailOutbox,
                mailGatewayWireClient: mailGatewayWireClient,
                smtpResolver: smtpResolver,
                smtpWireClient: smtpWireClient);
            _toolInvocationCoordinator = new ToolInvocationCoordinator(
                _toolInvocationPolicy,
                _windowsToolHost,
                RequestToolApprovalAsync,
                _operationalRepository,
                _operationalRepository);
            workflowTools = new DesktopWorkflowToolMetadataCatalog(_windowsToolHost);
            _conversationWorkflows = new ConversationWorkflowInvocationController(
                _conversationRun,
                new DesktopWorkflowOrchestrator(
                    _conversationStore.Capabilities,
                    _conversationStore.WorkflowLedger,
                    workflowTools,
                    _windowsToolHost,
                    policy => new ToolInvocationCoordinator(
                        policy,
                        _windowsToolHost,
                        RequestToolApprovalAsync,
                        _operationalRepository,
                        _operationalRepository)));
        }
        var capabilityTools = _windowsToolHost is null
            ? []
            : _windowsToolHost.Descriptors.Select(descriptor =>
                new CapabilityToolDependency(descriptor.Name, descriptor.Risk)).ToArray();
        var capabilityDependencies = new CapabilityToolDependencyCatalog(
            capabilityTools,
            name => !StringComparer.Ordinal.Equals(name, "mcp.read.v1") ||
                    Volatile.Read(ref _capabilityMcpReadAvailable));
        _capabilityManagement = new CapabilityManagementService(
            new CapabilityCatalogService(
                _conversationStore.Capabilities,
                capabilityDependencies,
                workflowTools,
                _connectionReadiness),
            new ZipLigcapPackageCodec());
        var featureBuilderScanner = new DesktopFeatureBuilderProposalScanner(
            capabilityDependencies,
            workflowTools);
        _featureBuilderManagement = new FeatureBuilderManagementService(
            _conversationStore.FeatureBuilder,
            _sidecar,
            featureBuilderScanner);
        _featureBuilderTestRunner = new DesktopFeatureBuilderTestRunner(
            _featureBuilderManagement,
            _conversationStore.FeatureBuilder,
            featureBuilderScanner);
        var capabilityRuntime = new CapabilityRuntimeSnapshotBuilder(
            _conversationStore.CapabilityRuntimeCandidates,
            capabilityDependencies,
            _connectionReadiness);
        _conversationOrchestration.ConfigureCapabilitySnapshotFactory(capabilityRuntime.BuildAsync);

        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (_platformProfile is not null && !WindowsWindowAppearance.Apply(this, _platformProfile))
            AddDiagnostic("현재 윈도우의 창 모양 최적화를 적용하지 못했습니다.");
        QuickAccessShortcut preferredShortcut;
        try
        {
            preferredShortcut = _quickAccessShortcutStore.Load();
        }
        catch (Exception exception)
        {
            preferredShortcut = QuickAccessShortcutCatalog.Default;
            AddDiagnostic($"빠른 호출 설정을 불러오지 못해 기본값을 사용합니다: {exception.GetType().Name}");
        }
        _configuredQuickAccessShortcut = preferredShortcut;
        var canUseGlobalHotkey = _platformProfile?.Supports(WindowsCapability.GlobalHotkey) ?? true;
        if (canUseGlobalHotkey && !_quickAccessHotkey.Register(handle, preferredShortcut))
        {
            AddDiagnostic("빠른 호출 단축키를 등록하지 못했습니다.");
        }
        else if (!canUseGlobalHotkey)
        {
            AddDiagnostic("현재 윈도우에서는 전체 화면에서 동작하는 단축키 기능을 사용할 수 없습니다.");
        }
        _persistenceInitialization = InitializePersistenceAsync();
        _sidecar.Start();
    }

    private async Task InitializePersistenceAsync()
    {
        try
        {
            await _conversationStore.InitializeForDesktopStartupAsync();
            await _conversationRepository.MarkRunningConversationsInterruptedAsync();
            await _subagentRepository.ReconcileSubagentTasksOnStartupAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            _persistenceAvailable = true;
            try
            {
                _scheduler = new DurableNotificationScheduler(_scheduleRepository, _notifications);
                await _scheduler.StartAsync();
                if (_windowsToolHost is not null)
                {
                    _subagentOrchestrator = new LocalSubagentOrchestrator(
                        _sidecar,
                        _windowsToolHost,
                        _subagentRepository,
                        RequestBackgroundToolApprovalAsync,
                        _modelSettingsStore.LoadRouting,
                        _operationalRepository,
                        _operationalRepository);
                    _agentJobExecutor = new SidecarAgentJobExecutor(
                        _sidecar,
                        _windowsToolHost,
                        RequestBackgroundToolApprovalAsync,
                        _modelSettingsStore.LoadRouting,
                        _operationalRepository,
                        _operationalRepository);
                    _agentJobScheduler = new DurableAgentJobScheduler(
                        _agentJobRepository,
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
                AddDiagnostic($"예약 실행 서비스 초기화 실패: {exception.GetType().Name}");
            }
            await RefreshRecentConversationsAsync();
        }
        catch (Exception exception)
        {
            _persistenceAvailable = false;
            AddDiagnostic($"대화 기록 저장소 초기화 실패: {exception.GetType().Name}");
        }
    }

    internal void FocusRequestInput()
    {
        ConversationInput.Focus();
        Keyboard.Focus(ConversationInput);
    }

    internal void ApplyExplorerActivation(ExplorerActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        var text = ExplorerActivationPolicy.ToComposerText(activation);
        var separator = string.IsNullOrWhiteSpace(ConversationInput.Text) ? string.Empty : "\n\n";
        if (ConversationInput.Text.Length + separator.Length + text.Length > ConversationInput.MaxLength)
        {
            SetRunStatus("선택한 항목이 많아 요청 입력에 추가하지 못했어요.");
            return;
        }
        ConversationInput.AppendText(separator + text);
        ConversationInput.CaretIndex = ConversationInput.Text.Length;
        SetRunStatus($"Explorer에서 선택한 항목 {activation.Paths.Count}개를 요청에 추가했어요.");
        FocusRequestInput();
    }

    internal void ApplyVoiceSettings(VoiceSettings settings)
    {
        VoiceAutoReadPolicy.Validate(settings);
        _voiceSettings = settings;
        _voiceInputEnabled = settings.PushToTalkEnabled;
        if (!settings.PushToTalkEnabled && _pushToTalk.IsActive) _ = CancelVoiceInputAsync();
        UpdateCommandState();
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
        Volatile.Write(ref _capabilityMcpReadAvailable, false);
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
        var status = result.Connections.Count == 0
            ? new McpConnectionStatus("disabled", 0, [], [])
            : ReadMcpStatus(result.Connections[0]);
        Volatile.Write(
            ref _capabilityMcpReadAvailable,
            status.IsConnected && status.AllowedTools.Count > 0);
        return status;
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
                SetRunStatus("설정에서 인공지능 모델 서버를 연결해 주세요.");
                ShowConnectionBanner(
                    "대화를 시작하려면 먼저 인공지능 모델 서버를 연결해 주세요.",
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
            AddDiagnostic($"모델 설정 적용 실패: {exception.GetType().Name}");
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
                ? $"외부 지식 도구 연결됨: {settings.DisplayName}, 도구 {status.ToolCount}개"
                : $"외부 지식 도구 연결 실패: {settings.DisplayName}");
        }
        catch (Exception exception)
        {
            AddDiagnostic($"외부 지식 도구 설정 적용 실패: {exception.GetType().Name}");
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
        if (_isSpeaking) StopReadingSelection();
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
                     CapabilitiesNavigationButton,
                     ConnectionsNavigationButton,
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
            AddDiagnostic($"설정 화면 오류: {exception.GetType().Name}");
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
            var activity = await _operationalRepository.GetRecentToolActivityAsync();
            var pendingUndo = await _operationalRepository.GetPendingUndoActivityAsync();
            _activityView ??= CreateActivityView();
            _activityView.SetItems(activity, pendingUndo);
            ShowShellPage(_activityView, ActivityNavigationButton);
        }
        catch (Exception exception)
        {
            AddDiagnostic($"실행 활동 불러오기 실패: {exception.GetType().Name}");
            System.Windows.MessageBox.Show(this, "실행 활동 화면을 열지 못했습니다.", "LIGClaw", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Capabilities_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _persistenceInitialization;
            if (!_persistenceAvailable || _capabilityManagement is null)
            {
                AddDiagnostic("기능 저장소를 사용할 수 없습니다.");
                return;
            }
            _capabilitiesPage ??= CreateCapabilitiesPage();
            ShowShellPage(_capabilitiesPage, CapabilitiesNavigationButton);
            await _capabilitiesPage.RefreshAsync();
        }
        catch (Exception exception)
        {
            AddDiagnostic($"Capabilities 관리 화면 오류: {exception.GetType().Name}");
            System.Windows.MessageBox.Show(
                this,
                "Capabilities 관리 화면을 열지 못했습니다.",
                "LIGClaw",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private CapabilitiesPage CreateCapabilitiesPage()
    {
        if (_capabilityManagement is null)
            throw new InvalidOperationException("Capability management is unavailable.");
        var page = new CapabilitiesPage(
            _capabilityManagement,
            _connectionManagement,
            _connectionManagement is null ? null : _conversationStore.Connections,
            _connectionReadiness,
            _mailGatewayManagement,
            _mailGatewayManagement is null ? null : _conversationStore.MailGatewayConnections,
            _smtpManagement,
            _smtpManagement is null ? null : _conversationStore.SmtpConnections);
        page.BuilderRequested += CapabilitiesPage_BuilderRequested;
        return page;
    }

    private async void CapabilitiesPage_BuilderRequested(object? sender, EventArgs e)
    {
        if (!await OpenFeatureBuilderAsync())
            AddDiagnostic("기능 만들기 서비스를 사용할 수 없습니다.");
    }

    private async Task<bool> OpenFeatureBuilderAsync(
        string? initialRequirement = null,
        Func<bool>? stillAuthorized = null)
    {
        try
        {
            await _persistenceInitialization;
            if (stillAuthorized is not null && !stillAuthorized()) return false;
            if (!_persistenceAvailable || _capabilityManagement is null ||
                _featureBuilderManagement is null || _featureBuilderTestRunner is null)
                return false;

            _capabilitiesPage ??= CreateCapabilitiesPage();
            _featureBuilderPage = new FeatureBuilderPage(
                _featureBuilderManagement,
                BuildFeatureBuilderInventoryAsync,
                _featureBuilderTestRunner.RunRequiredTestsAsync,
                initialRequirement);
            _featureBuilderPage.BackRequested += FeatureBuilderPage_BackRequested;
            _featureBuilderPage.DraftReadyForReview += FeatureBuilderPage_DraftReadyForReview;
            ShowShellPage(_featureBuilderPage, CapabilitiesNavigationButton);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            AddDiagnostic($"기능 만들기 화면 오류: {exception.GetType().Name}");
            return false;
        }
    }

    private async void FeatureBuilderPage_BackRequested(object? sender, EventArgs e)
    {
        await ReturnToCapabilitiesFromBuilderAsync();
    }

    private async void FeatureBuilderPage_DraftReadyForReview(
        object? sender,
        CapabilityVersionRef exactVersion)
    {
        await ReturnToCapabilitiesFromBuilderAsync(
            "검토 초안이 준비됐습니다. 내용을 확인한 뒤 활성화하세요.",
            exactVersion);
    }

    private async Task ReturnToCapabilitiesFromBuilderAsync(
        string? completionMessage = null,
        CapabilityVersionRef? focusVersion = null)
    {
        if (_capabilitiesPage is null) return;
        ShowShellPage(_capabilitiesPage, CapabilitiesNavigationButton);
        try
        {
            await _capabilitiesPage.RefreshAsync(completionMessage, focusVersion);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AddDiagnostic($"Capabilities 목록 복귀 실패: {exception.GetType().Name}");
        }
    }

    private async Task<FeatureBuilderInventory> BuildFeatureBuilderInventoryAsync(
        CancellationToken cancellationToken)
    {
        var tools = _windowsToolHost is null
            ? []
            : _windowsToolHost.Descriptors
                .Where(descriptor =>
                    !StringComparer.Ordinal.Equals(descriptor.Name, "mcp.read.v1") ||
                    Volatile.Read(ref _capabilityMcpReadAvailable))
                .Select(descriptor => descriptor.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
        var summaries = await _conversationStore.Capabilities.ListAsync(
            null,
            0,
            100,
            cancellationToken);
        var capabilityIds = summaries
            .Select(summary => summary.Id.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new FeatureBuilderInventory(
            tools,
            CapabilityConnectionKindPolicy.SupportedKinds,
            capabilityIds);
    }

    private async void Connections_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _persistenceInitialization;
            if (!_persistenceAvailable || _connectionManagement is null ||
                _mailGatewayManagement is null || _smtpManagement is null)
            {
                AddDiagnostic("연결 설정 저장소를 사용할 수 없습니다.");
                return;
            }
            _connectionsPage ??= new ConnectionsPage(
                _connectionManagement,
                _conversationStore.Connections,
                _connectionCredentials,
                _mailGatewayManagement,
                _conversationStore.MailGatewayConnections,
                _smtpManagement,
                _conversationStore.SmtpConnections);
            ShowShellPage(_connectionsPage, ConnectionsNavigationButton);
            await _connectionsPage.RefreshAsync();
        }
        catch (Exception exception)
        {
            AddDiagnostic($"연결 설정 관리 화면 오류: {exception.GetType().Name}");
            System.Windows.MessageBox.Show(
                this,
                "연결 설정 관리 화면을 열지 못했습니다.",
                "LIGClaw",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
            var activity = await _operationalRepository.GetRecentToolActivityAsync();
            var pendingUndo = await _operationalRepository.GetPendingUndoActivityAsync();
            _activityView.SetItems(activity, pendingUndo);
            _activityView.SetFeedback(
                succeeded ? "파일 작업을 되돌렸어요." : "파일 작업을 되돌리지 못했어요.",
                isError: !succeeded);
        }
        catch (Exception exception)
        {
            AddDiagnostic($"파일 되돌리기 화면 갱신 실패: {exception.GetType().Name}");
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
            AddDiagnostic($"기억 관리 화면 오류: {exception.GetType().Name}");
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
            _schedulePage ??= new SchedulePage(_scheduleRepository);
            ShowShellPage(_schedulePage, ScheduleNavigationButton);
            await _schedulePage.RefreshAsync();
        }
        catch (Exception exception)
        {
            AddDiagnostic($"예약 관리 화면 오류: {exception.GetType().Name}");
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

    private async void MakeCapability_Click(object sender, RoutedEventArgs e)
    {
        if (_isOpeningFeatureBuilder) return;
        var requirement = ConversationInput.Text.Trim();
        if (requirement.Length == 0)
        {
            SetRunStatus("기능으로 만들 요청을 먼저 입력해 주세요.");
            ConversationInput.Focus();
            return;
        }

        var validation = FeatureBuilderPolicy.ValidateRequirement(requirement);
        if (!validation.IsValid)
        {
            SetRunStatus(DescribeFeatureBuilderHandoffFailure(validation.ErrorCode));
            ConversationInput.Focus();
            return;
        }

        _isOpeningFeatureBuilder = true;
        UpdateCommandState();
        try
        {
            if (!await OpenFeatureBuilderAsync(requirement))
                SetRunStatus("기능 만들기를 준비하지 못했습니다. 잠시 후 다시 시도해 주세요.");
        }
        finally
        {
            _isOpeningFeatureBuilder = false;
            UpdateCommandState();
        }
    }

    private static string DescribeFeatureBuilderHandoffFailure(string? errorCode) =>
        errorCode == "feature_builder.requirement.secret_detected"
            ? "비밀값처럼 보이는 내용을 제거한 뒤 기능으로 만들어 주세요."
            : "기능 요구사항은 공백이 아닌 8,192자 이내의 텍스트여야 합니다.";

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

    private void Transcript_SelectionChanged(object sender, RoutedEventArgs e) => UpdateReadSelectionState();

    private async Task TryAutoReadResponseAsync(string response)
    {
        if (!VoiceAutoReadPolicy.ShouldRead(
                _voiceSettings,
                DateTimeOffset.Now,
                IsVisible && IsActive,
                _pushToTalk.IsActive,
                _isSpeaking,
                response)) return;

        _textToSpeechStartCancellation?.Dispose();
        _textToSpeechStartCancellation = new CancellationTokenSource();
        _isSpeaking = true;
        ReadSelectionButton.Content = "읽기 중지";
        ReadSelectionButton.IsEnabled = true;
        SetRunStatus("새 답변을 읽을 준비를 하고 있어요.");
        try
        {
            var result = await _textToSpeech.PlayAsync(response, _textToSpeechStartCancellation.Token);
            if (!_isSpeaking) return;
            if (result.Status == TextToSpeechStatus.Playing)
            {
                SetRunStatus("새 답변을 읽고 있어요. 멈추려면 읽기 중지를 누르세요.");
                return;
            }
            ResetReadSelectionState();
            SetRunStatus(result.Error ?? "새 답변을 자동으로 읽지 못했어요.");
        }
        catch (OperationCanceledException)
        {
            ResetReadSelectionState();
        }
        catch (Exception exception)
        {
            AddDiagnostic($"답변 자동 읽기 실패: {exception.GetType().Name}");
            ResetReadSelectionState();
            SetRunStatus("윈도우에서 새 답변을 자동으로 읽지 못했어요.");
        }
    }

    private async void ReadSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_isSpeaking)
        {
            StopReadingSelection("읽기를 멈췄어요.");
            return;
        }

        var selectedText = new System.Windows.Documents.TextRange(
            Transcript.Selection.Start,
            Transcript.Selection.End).Text;
        var validation = TextToSpeechTextPolicy.Prepare(selectedText, out _);
        if (validation.Status != TextToSpeechStatus.Playing)
        {
            SetRunStatus(validation.Error ?? "읽을 답변을 선택해 주세요.");
            return;
        }

        _textToSpeechStartCancellation?.Dispose();
        _textToSpeechStartCancellation = new CancellationTokenSource();
        _isSpeaking = true;
        ReadSelectionButton.Content = "읽기 중지";
        ReadSelectionButton.IsEnabled = true;
        SetRunStatus("선택한 답변을 읽을 준비를 하고 있어요.");
        try
        {
            var result = await _textToSpeech.PlayAsync(selectedText, _textToSpeechStartCancellation.Token);
            if (!_isSpeaking) return;
            if (result.Status == TextToSpeechStatus.Playing)
            {
                SetRunStatus("선택한 답변을 읽고 있어요. 멈추려면 읽기 중지를 누르세요.");
                return;
            }
            ResetReadSelectionState();
            SetRunStatus(result.Error ?? "선택한 답변을 읽지 못했어요.");
        }
        catch (OperationCanceledException)
        {
            ResetReadSelectionState();
        }
        catch (Exception exception)
        {
            AddDiagnostic($"답변 읽기 시작 실패: {exception.GetType().Name}");
            ResetReadSelectionState();
            SetRunStatus("윈도우에서 선택한 답변을 읽지 못했어요.");
        }
    }

    private void TextToSpeech_PlaybackEnded(object? sender, TextToSpeechPlaybackEndedEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isSpeaking) return;
            ResetReadSelectionState();
            SetRunStatus(e.Failed
                ? "윈도우에서 선택한 답변을 끝까지 읽지 못했어요."
                : "선택한 답변을 모두 읽었어요.");
        });

    private void StopReadingSelection(string? status = null)
    {
        _textToSpeechStartCancellation?.Cancel();
        _textToSpeech.Stop();
        ResetReadSelectionState();
        if (status is not null) SetRunStatus(status);
    }

    private void ResetReadSelectionState()
    {
        _isSpeaking = false;
        _textToSpeechStartCancellation?.Dispose();
        _textToSpeechStartCancellation = null;
        ReadSelectionButton.Content = "선택 영역 읽기";
        UpdateReadSelectionState();
    }

    private void UpdateReadSelectionState()
    {
        if (!IsInitialized || _isSpeaking) return;
        ReadSelectionButton.IsEnabled = !Transcript.Selection.IsEmpty;
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
        _currentAssistantResponse.Clear();
        HideRoutineSuggestion();
        if (input.Length == 0) return;
        if (!_isConnected)
        {
            SetRunStatus("아직 준비 중이에요. 잠시 후 다시 시도해 주세요.");
            return;
        }

        var identity = _conversationOrchestration.BeginRun();
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
            await _persistenceInitialization;
            var outcome = await _conversationOrchestration.StartAsync(identity, input, _persistenceAvailable);
            if (outcome.Diagnostic is not null) AddDiagnostic(outcome.Diagnostic);
            if (outcome.RefreshConversations) await RefreshRecentConversationsAsync();
            if (_conversationRun.IsRunning && StringComparer.Ordinal.Equals(_conversationRun.ActiveConversationId, conversationId))
            {
                SetRunStatus("답변을 준비하고 있어요…");
            }
        }
        catch (OperationCanceledException) when (identity.CancellationToken.IsCancellationRequested)
        {
            AddDiagnostic("요청 시작을 사용자 취소 또는 연결 종료에 따라 중단했습니다.");
            AppendTranscriptStatus("요청을 중단했습니다.");
            CompleteRun("요청을 중단했어요.");
            _ = MarkRunAndRefreshAsync(conversationId, runId, "cancelled");
        }
        catch (Exception exception)
        {
            AddDiagnostic($"요청 시작 실패: {exception.GetType().Name}");
            ShowRunFailure("요청을 시작하지 못했어요. 잠시 후 다시 시도해 주세요.");
        }
    }

    private async void ScreenContext_Click(object sender, RoutedEventArgs e)
    {
        if (_isPreparingScreenContext || _conversationRun.IsRunning) return;
        _isPreparingScreenContext = true;
        ScreenCapturePickerResult? capture = null;
        ScreenTextRecognitionResult? recognition = null;
        SetRunStatus("윈도우에서 공유할 화면을 선택해 주세요.");
        UpdateCommandState();
        try
        {
            capture = await _screenCapturePicker.PickSingleFrameAsync(this);
            if (capture.Status == ScreenCapturePickerStatus.Cancelled)
            {
                SetRunStatus("화면 가져오기를 취소했어요.");
                return;
            }

            if (capture.Status != ScreenCapturePickerStatus.Captured || capture.EncodedImage is null)
            {
                ShowScreenContextProblem(capture.Error ?? "이 PC에서는 화면을 가져올 수 없어요.");
                return;
            }

            SetRunStatus("선택한 화면에서 글자를 읽고 있어요…");
            recognition = await _screenTextRecognizer.RecognizeAsync(capture.EncodedImage);
            if (recognition.Status != ScreenTextRecognitionStatus.Recognized || recognition.TextUtf8 is null)
            {
                capture.ClearImage();
                ShowScreenContextProblem(recognition.Error ?? "선택한 화면에서 글자를 읽지 못했어요.");
                return;
            }

            var identity = new PreparedSensitiveContextIdentity(
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"));
            var prepared = _sensitiveContextStore.Prepare(
                identity,
                new PreparedSensitiveContextDraft(
                    "selection",
                    capture.WidthPixels,
                    capture.HeightPixels,
                    capture.EncodedImage,
                    recognition.TextUtf8,
                    0));
            if (!prepared.Success || prepared.Token is null)
            {
                ShowScreenContextProblem(prepared.Error ?? "화면 내용을 안전하게 준비하지 못했어요.");
                return;
            }

            var taken = _sensitiveContextStore.Take(identity, prepared.Token);
            if (!taken.Success || taken.Context is null)
            {
                ShowScreenContextProblem(taken.Error ?? "준비한 화면 내용을 찾지 못했어요.");
                return;
            }

            var preview = new SensitiveContextPreviewWindow(taken.Context, _screenTextRecognizer) { Owner = this };
            if (preview.ShowDialog() != true)
            {
                SetRunStatus("화면 내용을 요청에 추가하지 않았어요.");
                return;
            }

            var approvedText = preview.TakeApprovedOcrTextUtf8();
            if (approvedText is null) return;
            try
            {
                if (!AppendScreenContextToInput(Encoding.UTF8.GetString(approvedText))) return;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(approvedText);
            }

            SetRunStatus(recognition.WasTruncated
                ? "화면 글자가 길어 안전 한도까지만 요청에 추가됐어요."
                : "확인한 화면 글자를 요청에 추가했어요.");
            FocusRequestInput();
        }
        catch (OperationCanceledException)
        {
            SetRunStatus("화면 가져오기를 취소했어요.");
        }
        catch (Exception exception)
        {
            AddDiagnostic($"화면 내용 준비 실패: {exception.GetType().Name}");
            ShowScreenContextProblem("화면 내용을 준비하는 중 문제가 생겼어요.");
        }
        finally
        {
            capture?.ClearImage();
            recognition?.ClearText();
            _isPreparingScreenContext = false;
            UpdateCommandState();
        }
    }

    private async void VoiceInputButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pushToTalk.IsActive) return;
        e.Handled = true;
        _ = VoiceInputButton.CaptureMouse();
        await BeginVoiceInputAsync();
    }

    private async void VoiceInputButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pushToTalk.IsActive) return;
        e.Handled = true;
        if (VoiceInputButton.IsMouseCaptured) VoiceInputButton.ReleaseMouseCapture();
        await EndVoiceInputAsync();
    }

    private async void VoiceInputButton_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_pushToTalk.IsActive) return;
        await EndVoiceInputAsync();
    }

    private async void VoiceInputButton_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter) || e.IsRepeat || _pushToTalk.IsActive) return;
        e.Handled = true;
        await BeginVoiceInputAsync();
    }

    private async void VoiceInputButton_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter) || !_pushToTalk.IsActive) return;
        e.Handled = true;
        await EndVoiceInputAsync();
    }

    private void VoiceInputButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!_pushToTalk.IsActive)
            SetRunStatus("음성 입력 버튼은 마우스나 Space 키를 누른 채 사용해 주세요.");
    }

    private async Task BeginVoiceInputAsync()
    {
        if (!_voiceInputEnabled || _pushToTalk.IsActive || _conversationRun.IsRunning) return;
        if (_isSpeaking) StopReadingSelection();
        VoiceInputButton.Content = "마이크 준비 중…";
        SetRunStatus("마이크를 준비하고 있어요. 버튼을 누른 채 기다려 주세요…");
        _voiceLimitTimer.Start();
        UpdateCommandState();
        try
        {
            var result = await _pushToTalk.PressAsync();
            if (_pushToTalk.IsEnding) return;
            if (result.Status != PushToTalkStatus.Listening)
            {
                SetRunStatus(result.Error ?? "음성 입력을 시작하지 못했어요.");
                ResetVoiceInputState();
                return;
            }

            VoiceInputButton.Content = "듣는 중…";
            SetRunStatus("듣고 있어요. 말을 마치면 버튼에서 손을 떼세요.");
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ObjectDisposedException)
        {
            AddDiagnostic($"음성 입력 시작 실패: {exception.GetType().Name}");
            SetRunStatus("윈도우 음성 입력을 시작하지 못했어요.");
            ResetVoiceInputState();
        }
    }

    private async Task EndVoiceInputAsync(bool timedOut = false)
    {
        if (!_pushToTalk.IsActive || _pushToTalk.IsEnding) return;
        _voiceLimitTimer.Stop();
        VoiceInputButton.Content = "글자로 바꾸는 중…";
        SetRunStatus(timedOut
            ? "60초 제한에 도달해 음성 입력을 마치고 있어요…"
            : "말한 내용을 글자로 바꾸고 있어요…");
        try
        {
            var result = await _pushToTalk.ReleaseAsync();
            if (result is null) return;
            if (result.Status == PushToTalkStatus.Recognized && !string.IsNullOrWhiteSpace(result.Text))
            {
                if (AppendVoiceInput(result.Text))
                    SetRunStatus(result.WasTruncated
                        ? "말한 내용이 길어 안전 한도까지만 요청에 추가했어요."
                        : "말한 내용을 요청에 추가했어요.");
            }
            else
            {
                SetRunStatus(result.Error ?? "인식된 음성이 없어요.");
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ObjectDisposedException)
        {
            AddDiagnostic($"음성 입력 종료 실패: {exception.GetType().Name}");
            SetRunStatus("음성 입력을 안전하게 마치지 못했어요. 다시 시도해 주세요.");
            await _pushToTalk.CancelAsync();
        }
        finally
        {
            ResetVoiceInputState();
        }
    }

    private async Task CancelVoiceInputAsync()
    {
        if (!_pushToTalk.IsActive) return;
        _voiceLimitTimer.Stop();
        try
        {
            await _pushToTalk.CancelAsync();
        }
        finally
        {
            SetRunStatus("음성 입력을 취소했어요.");
            ResetVoiceInputState();
        }
    }

    private bool AppendVoiceInput(string text)
    {
        var separator = string.IsNullOrWhiteSpace(ConversationInput.Text) ? string.Empty : " ";
        var available = ConversationInput.MaxLength - ConversationInput.Text.Length - separator.Length;
        if (available <= 0)
        {
            SetRunStatus("요청 입력란이 가득 차 말한 내용을 추가하지 못했어요.");
            return false;
        }

        var end = Math.Min(text.Length, available);
        if (end < text.Length && end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
        ConversationInput.AppendText(separator + text[..end]);
        ConversationInput.CaretIndex = ConversationInput.Text.Length;
        FocusRequestInput();
        return true;
    }

    private void ResetVoiceInputState()
    {
        _voiceLimitTimer.Stop();
        VoiceInputButton.Content = "누르고 말하기";
        if (VoiceInputButton.IsMouseCaptured) VoiceInputButton.ReleaseMouseCapture();
        UpdateCommandState();
    }

    private bool AppendScreenContextToInput(string text)
    {
        const string heading = "[선택한 화면에서 확인한 글자]\n";
        var separator = string.IsNullOrWhiteSpace(ConversationInput.Text) ? string.Empty : "\n\n";
        var available = ConversationInput.MaxLength - ConversationInput.Text.Length - separator.Length - heading.Length;
        if (available <= 0)
        {
            ShowScreenContextProblem("요청 입력란이 가득 차 화면 글자를 추가하지 못했어요.");
            return false;
        }

        var end = Math.Min(text.Length, available);
        if (end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
        var bounded = text[..end];
        ConversationInput.AppendText($"{separator}{heading}{bounded}");
        ConversationInput.CaretIndex = ConversationInput.Text.Length;
        return true;
    }

    private void ShowScreenContextProblem(string message)
    {
        SetRunStatus(message);
        System.Windows.MessageBox.Show(
            this,
            message,
            "화면 가져오기",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_conversationOrchestration.TryRequestCancellation(out var activeRun) || activeRun is null) return;
        SetRunStatus("요청을 중단하고 있어요…");
        UpdateCommandState();
        var outcome = await _conversationOrchestration.CancelAsync(activeRun);
        if (outcome.Diagnostic is not null) AddDiagnostic(outcome.Diagnostic);
        if (_conversationRun.IsRunning)
        {
            SetRunStatus(outcome.Status);
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
                AddDiagnostic($"인공지능 도우미 이벤트 처리 실패: {exception.GetType().Name}");
                if (_conversationRun.IsRunning)
                    ShowRunFailure("응답을 표시하는 중 문제가 발생했어요. 다시 시도해 주세요.");
            });
        }
    }

    private async Task HandleAgentEventAsync(AgentEvent agentEvent)
    {
        if (!_conversationRun.IsActiveRun(agentEvent.ConversationId, agentEvent.RunId))
        {
            AddDiagnostic("현재 요청과 일치하지 않는 인공지능 도우미 이벤트를 무시했습니다.");
            return;
        }
        var decision = ConversationAgentEventPolicy.Evaluate(agentEvent);
        if (decision.Diagnostic is not null) AddDiagnostic(decision.Diagnostic);
        if (decision.RunStatus is not null) SetRunStatus(decision.RunStatus);
        switch (decision.Action)
        {
            case ConversationAgentEventAction.AppendText:
                AppendTranscript(decision.Transcript);
                if (decision.Transcript is not null)
                    _currentAssistantResponse.Append(decision.Transcript);
                break;
            case ConversationAgentEventAction.AppendStatus:
                if (decision.Transcript is not null) AppendTranscriptStatus(decision.Transcript);
                if (decision.IsTerminal) CompleteRun(decision.RunStatus!);
                break;
            case ConversationAgentEventAction.Complete:
                CompleteRun(decision.RunStatus!);
                await TryAutoReadResponseAsync(_currentAssistantResponse.ToString());
                break;
            case ConversationAgentEventAction.Fail:
                ShowRunFailure(decision.RunStatus!);
                break;
        }
        await _persistenceInitialization;
        var persistenceDiagnostic = await _conversationOrchestration.PersistEventAsync(
            agentEvent,
            _persistenceAvailable);
        if (persistenceDiagnostic is not null) AddDiagnostic(persistenceDiagnostic);
        if (decision.IsTerminal)
            await RefreshRecentConversationsAsync();
        if (decision.Action == ConversationAgentEventAction.Complete)
            await TryShowRoutineSuggestionAsync(agentEvent.RunId);
    }

    private async Task TryShowRoutineSuggestionAsync(string runId)
    {
        if (!_persistenceAvailable || _conversationRun.IsRunning) return;
        try
        {
            var preferences = _routineSuggestionSettingsStore.Load();
            if (!preferences.Enabled) return;
            var nowUtc = DateTimeOffset.UtcNow;
            var history = await _conversationRepository.GetRecentCompletedUserInputsAsync(
                nowUtc - RoutineSuggestionPolicy.HistoryWindow,
                RoutineSuggestionPolicy.MaximumHistoryItems);
            var candidate = RoutineSuggestionPolicy.Evaluate(
                preferences,
                runId,
                history,
                nowUtc,
                TimeZoneInfo.Local);
            if (candidate is null || _conversationRun.IsRunning) return;
            _routineSuggestionSettingsStore.RecordOffer(candidate.Fingerprint, nowUtc);
            _routineSuggestionCandidate = candidate;
            RoutineSuggestionText.Text =
                $"서로 다른 날짜에 같은 요청을 {candidate.OccurrenceCount}번 완료했습니다. " +
                $"{(candidate.Recurrence == ScheduleValues.Daily ? "매일" : "매주")} 실행하도록 미리 채워 드릴게요. 시간과 권한은 저장 전에 확인할 수 있습니다.";
            RoutineSuggestionCard.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            AddDiagnostic($"반복 작업 제안 준비 실패: {exception.GetType().Name}");
        }
    }

    private void AcceptRoutineSuggestion_Click(object sender, RoutedEventArgs e)
    {
        var candidate = _routineSuggestionCandidate;
        HideRoutineSuggestion();
        if (candidate is null || !_persistenceAvailable) return;
        var editor = new AgentJobEditorWindow(_agentJobRepository, candidate) { Owner = this };
        if (editor.ShowDialog() == true)
            SetRunStatus("반복 자동 작업을 예약했어요. 자동 작업 화면에서 확인할 수 있습니다.");
    }

    private void DismissRoutineSuggestion_Click(object sender, RoutedEventArgs e) => HideRoutineSuggestion();

    private void IgnoreRoutineSuggestion_Click(object sender, RoutedEventArgs e)
    {
        var candidate = _routineSuggestionCandidate;
        HideRoutineSuggestion();
        if (candidate is null) return;
        try
        {
            _routineSuggestionSettingsStore.Ignore(candidate.Fingerprint);
            SetRunStatus("이 요청은 반복 작업으로 다시 제안하지 않을게요.");
        }
        catch (Exception exception)
        {
            AddDiagnostic($"반복 작업 제안 무시 저장 실패: {exception.GetType().Name}");
            SetRunStatus("제안 숨김 설정을 저장하지 못했어요.");
        }
    }

    private void HideRoutineSuggestion()
    {
        _routineSuggestionCandidate = null;
        if (IsInitialized) RoutineSuggestionCard.Visibility = Visibility.Collapsed;
    }

    private async void Sidecar_ToolInvocationReceived(object? sender, ToolInvokeParams invocation)
    {
        if (_agentJobExecutor is not null &&
            await _agentJobExecutor.TryHandleToolInvocationAsync(invocation)) return;
        if (_subagentOrchestrator is not null &&
            await _subagentOrchestrator.TryHandleToolInvocationAsync(invocation)) return;
        if (StringComparer.Ordinal.Equals(
                invocation.Name,
                CapabilityBuilderOpenController.CanonicalToolName))
        {
            await HandleCapabilityBuilderOpenInvocationAsync(invocation);
            return;
        }

        await Dispatcher.InvokeAsync(() => SetRunStatus(_conversationTools.PreparingStatus));
        var outcome = await _conversationTools.ExecuteAsync(
            invocation,
            _toolInvocationCoordinator is null
                ? null
                : _toolInvocationCoordinator.ExecutePreparedAsync);
        var execution = outcome.Execution;
        await Dispatcher.InvokeAsync(() =>
        {
            SetRunStatus(outcome.RunStatus);
            if (outcome.Diagnostic is not null) AddDiagnostic(outcome.Diagnostic);
        });

        await SubmitToolExecutionResultAsync(invocation, execution);
    }

    private async Task HandleCapabilityBuilderOpenInvocationAsync(ToolInvokeParams invocation)
    {
        await Dispatcher.InvokeAsync(() =>
            SetRunStatus("기능 만들기 요청을 안전하게 확인하고 있어요…"));

        CapabilityBuilderOpenControllerOutcome outcome;
        try
        {
            outcome = await _capabilityBuilderOpenController.ExecuteAsync(invocation);
        }
        catch (Exception exception)
        {
            outcome = new CapabilityBuilderOpenControllerOutcome(
                new WindowsToolExecutionResult(
                    false,
                    new Dictionary<string, object?>(),
                    "기능 만들기 요청을 안전하게 처리하지 못했어요.",
                    ErrorCode: "capability.builder.unavailable"));
            await Dispatcher.InvokeAsync(() =>
                AddDiagnostic($"기능 만들기 제어 요청 실패: {exception.GetType().Name}"));
        }

        var execution = outcome.Execution;
        var acknowledged = await SubmitToolExecutionResultAsync(invocation, execution);
        if (acknowledged is null || !acknowledged.Accepted || !execution.Success ||
            outcome.RequestAfterAcknowledgement is not { } request)
            return;
        if (!_capabilityBuilderOpenController.CanOpenAfterAcknowledgement(request)) return;

        var opened = await Dispatcher
            .InvokeAsync(() => OpenFeatureBuilderAsync(
                request.Requirement,
                () => _capabilityBuilderOpenController.CanOpenAfterAcknowledgement(request)))
            .Task
            .Unwrap();
        if (opened || !_capabilityBuilderOpenController.CanOpenAfterAcknowledgement(request)) return;

        await Dispatcher.InvokeAsync(() =>
        {
            SetRunStatus("기능 만들기를 열지 못했습니다. 기능 화면에서 다시 시도해 주세요.");
            AddDiagnostic("요청 확인 후 기능 만들기 화면을 열지 못했습니다.");
        });
    }

    private async Task<ToolResultResult?> SubmitToolExecutionResultAsync(
        ToolInvokeParams invocation,
        WindowsToolExecutionResult execution)
    {
        try
        {
            var acknowledged = await _sidecar.SubmitToolResultAsync(new ToolResultParams(
                invocation.ToolCallId,
                execution.Success,
                execution.Output,
                execution.Error));
            if (!acknowledged.Accepted)
                await Dispatcher.InvokeAsync(() => AddDiagnostic("보조 실행기가 만료된 작업 도구 결과를 거부했습니다."));
            else if (execution.ActivitySummary is not null)
                await Dispatcher.InvokeAsync(() => AddDiagnostic(execution.ActivitySummary));
            return acknowledged;
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() =>
                AddDiagnostic($"작업 도구 결과 전달 실패: {exception.GetType().Name}"));
            return null;
        }
    }

    private async void Sidecar_WorkflowInvocationReceived(object? sender, WorkflowInvokeParams invocation)
    {
        await Dispatcher.InvokeAsync(() => SetRunStatus("작업 흐름을 안전하게 준비하고 있어요…"));
        WorkflowResultParams result;
        try
        {
            result = _conversationWorkflows is null
                ? new WorkflowResultParams(
                    invocation.WorkflowCallId,
                    false,
                    Error: "workflow.runtime.unavailable")
                : await _conversationWorkflows.ExecuteAsync(invocation);
        }
        catch (Exception exception)
        {
            result = new WorkflowResultParams(
                invocation.WorkflowCallId,
                false,
                Error: "workflow.runtime.unavailable");
            await Dispatcher.InvokeAsync(() =>
                AddDiagnostic($"작업 흐름 실행 경계 오류: {exception.GetType().Name}"));
        }

        await Dispatcher.InvokeAsync(() => SetRunStatus(result.Output?.Status switch
        {
            "succeeded" => "작업 흐름을 완료했어요.",
            "needs_information" => "작업 흐름에 추가 정보가 필요해요.",
            "cancelled" => "작업 흐름 실행을 중단했어요.",
            "attention_required" => "작업 흐름 결과를 확인해 주세요.",
            "failed" => "작업 흐름을 완료하지 못했어요.",
            _ => result.Success ? "작업 흐름 결과를 확인하고 있어요…" : "작업 흐름 요청을 실행하지 않았어요.",
        }));

        try
        {
            var acknowledged = await _sidecar.SubmitWorkflowResultAsync(result);
            if (!acknowledged.Accepted)
                await Dispatcher.InvokeAsync(() => AddDiagnostic("보조 실행기가 만료된 작업 흐름 결과를 거부했습니다."));
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() =>
                AddDiagnostic($"작업 흐름 결과 전달 실패: {exception.GetType().Name}"));
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
                AddDiagnostic("자동 작업 저장소를 사용할 수 없습니다.");
                return;
            }
            _agentJobsPage ??= new AgentJobsPage(_agentJobRepository);
            ShowShellPage(_agentJobsPage, AgentJobsNavigationButton);
            await _agentJobsPage.RefreshAsync();
        }
        catch (Exception exception)
        {
            AddDiagnostic($"자동 작업 관리 화면 오류: {exception.GetType().Name}");
            System.Windows.MessageBox.Show(this, "자동 작업 관리 화면을 열지 못했습니다.", "LIGClaw", MessageBoxButton.OK, MessageBoxImage.Error);
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
            ? "작업이 완료되었습니다. LIGClaw의 자동 작업 화면에서 결과를 확인하세요."
            : "작업을 완료하지 못했습니다. LIGClaw의 자동 작업 화면에서 오류와 재시도를 확인하세요.";
        await _notifications.ShowAsync($"자동 작업 · {claim.Job.Title}", message, cancellationToken).ConfigureAwait(false);
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
        return await _operationalRepository.GetActiveGrantsAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(true);
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
        await _operationalRepository.RevokeGrantAsync(grantId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(true);
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
            _ = MarkRunAndRefreshAsync(failedConversationId, failedRunId, "failed");
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
        if (_isSpeaking) StopReadingSelection();
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

    private async Task MarkRunAndRefreshAsync(string conversationId, string runId, string status)
    {
        await _persistenceInitialization;
        var diagnostic = await _conversationOrchestration.MarkRunAsync(
            conversationId,
            runId,
            status,
            _persistenceAvailable);
        if (diagnostic is not null) AddDiagnostic(diagnostic);
        else if (_persistenceAvailable) await RefreshRecentConversationsAsync();
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
                ? await _conversationRepository.GetRecentConversationsAsync(cancellationToken: request.CancellationToken)
                : await _conversationRepository.SearchConversationsAsync(
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
            AddDiagnostic($"최근 대화 불러오기 실패: {exception.GetType().Name}");
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
            SetTranscriptMarkdown(await _conversationRepository.GetTranscriptAsync(conversation.Id), scrollToEnd: true);
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
            AddDiagnostic($"대화 내용 불러오기 실패: {exception.GetType().Name}");
        }
    }

    private void UpdateCommandState()
    {
        if (!IsInitialized) return;
        SendButton.IsEnabled = _isConnected && _isModelConfigured && !_conversationRun.IsRunning && !_pushToTalk.IsActive && !string.IsNullOrWhiteSpace(ConversationInput.Text);
        CancelButton.Visibility = _conversationRun.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = _conversationRun.IsRunning && !_conversationRun.IsCancelling;
        TimelineProgressCard.Visibility = _conversationRun.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        NewConversationButton.IsEnabled = !_conversationRun.IsRunning && !_pushToTalk.IsActive;
        ScreenContextButton.IsEnabled = !_conversationRun.IsRunning && !_isPreparingScreenContext && !_pushToTalk.IsActive;
        VoiceInputButton.IsEnabled = _voiceInputEnabled && !_conversationRun.IsRunning && !_isPreparingScreenContext;
        MakeCapabilityButton.IsEnabled = !_conversationRun.IsRunning && !_isPreparingScreenContext &&
                                         !_pushToTalk.IsActive && !_isOpeningFeatureBuilder;
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
        CapabilitiesNavigationLabel.Visibility = labelVisibility;
        ConnectionsNavigationLabel.Visibility = labelVisibility;
        MemoryNavigationLabel.Visibility = labelVisibility;
        ScheduleNavigationLabel.Visibility = labelVisibility;
        AgentJobsNavigationLabel.Visibility = labelVisibility;
        SettingsNavigationLabel.Visibility = labelVisibility;
        SidebarFooter.Visibility = labelVisibility;
        PrimaryNavigation.Margin = layout.IsCompact
            ? new Thickness(0, 20, 0, 0)
            : new Thickness(0, 38, 0, 0);
        foreach (var button in new[]
                 {
                     HomeNavigationButton,
                     ActivityNavigationButton,
                     CapabilitiesNavigationButton,
                     ConnectionsNavigationButton,
                     MemoryNavigationButton,
                     ScheduleNavigationButton,
                     AgentJobsNavigationButton,
                     SettingsNavigationButton,
                 })
        {
            button.Height = layout.IsCompact ? 42 : 48;
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
            if (_isSpeaking) StopReadingSelection();
            Hide();
            (System.Windows.Application.Current as App)?.NotifyWindowHidden();
        }
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _transcriptRenderTimer.Stop();
        _voiceLimitTimer.Stop();
        _conversationListRequests.Dispose();
        _diagnosticsWindow?.Close();
        _sidecar.StatusChanged -= Sidecar_StatusChanged;
        _sidecar.DiagnosticMessage -= Sidecar_DiagnosticMessage;
        _sidecar.AgentEventReceived -= Sidecar_AgentEventReceived;
        _sidecar.ToolInvocationReceived -= Sidecar_ToolInvocationReceived;
        _sidecar.WorkflowInvocationReceived -= Sidecar_WorkflowInvocationReceived;
        _quickAccessHotkey.Pressed -= QuickAccessHotkey_Pressed;
        _textToSpeech.PlaybackEnded -= TextToSpeech_PlaybackEnded;
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
            await _pushToTalkRecognizer.DisposeAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"음성 입력 서비스 종료 실패: {exception.GetType().Name}");
        }
        try
        {
            await _textToSpeech.DisposeAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"답변 읽기 서비스 종료 실패: {exception.GetType().Name}");
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
            _sensitiveContextStore.Dispose();
            _semanticMemoryHttpClient.Dispose();
            System.Windows.Application.Current.Shutdown();
        }
    }
}
