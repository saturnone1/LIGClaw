using LIGClaw.Application.Agents;
using LIGClaw.Application.Mail;
using LIGClaw.Application.Memory;
using LIGClaw.Application.Scheduling;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record WindowsToolExecutionResult(
    bool Success,
    IReadOnlyDictionary<string, object?> Output,
    string? Error = null,
    string? ActivitySummary = null,
    string? ErrorCode = null);

internal sealed record WindowsToolApprovalPrompt(
    string Heading,
    string Action,
    string Details,
    string Risk = "R1",
    bool CanUndo = false,
    string? GrantScope = null)
{
    public override string ToString() =>
        $"{nameof(WindowsToolApprovalPrompt)} {{ Risk = {Risk}, CanUndo = {CanUndo}, " +
        $"HasGrantScope = {GrantScope is not null} }}";
}

internal sealed record WindowsToolApprovalPromptResult(
    bool Success,
    WindowsToolApprovalPrompt? Prompt = null,
    string? Error = null);

internal interface IWindowsToolAdapter
{
    string Name { get; }
    string Risk { get; }
    WindowsCapability RequiredCapabilities { get; }
    int Priority { get; }
    TimeSpan Timeout => TimeSpan.FromSeconds(20);
    Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken);
    WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input) => null;
    void DiscardApproval(IReadOnlyDictionary<string, object?> input) { }
}

internal sealed record WindowsToolDescriptor(
    string Name,
    string Risk,
    WindowsCapability RequiredCapabilities,
    TimeSpan Timeout,
    int Priority,
    bool SupportsWorkflowOneShot,
    bool WorkflowOnlyObservation);

internal sealed class WindowsToolRegistry
{
    private readonly IReadOnlyList<IWindowsToolAdapter> _adapters;

    public WindowsToolRegistry(IEnumerable<IWindowsToolAdapter> adapters)
    {
        _adapters = adapters.ToArray();
        var duplicate = _adapters
            .GroupBy(adapter => (adapter.Name, adapter.RequiredCapabilities, adapter.Priority))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"중복 Windows Tool adapter가 등록됐습니다: {duplicate.Key.Name}");
        Descriptors = _adapters
            .Select(adapter => new WindowsToolDescriptor(
                adapter.Name,
                adapter.Risk,
                adapter.RequiredCapabilities,
                adapter.Timeout,
                adapter.Priority,
                adapter is IWorkflowOneShotToolAdapter,
                adapter is IWorkflowOnlyObservationToolAdapter))
            .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal)
            .ThenByDescending(descriptor => descriptor.Priority)
            .ToArray();
    }

    public IReadOnlyList<WindowsToolDescriptor> Descriptors { get; }

    public IWindowsToolAdapter? Resolve(string name, WindowsPlatformProfile platform) =>
        _adapters
            .Where(candidate => StringComparer.Ordinal.Equals(candidate.Name, name))
            .Where(candidate => platform.Supports(candidate.RequiredCapabilities))
            .OrderByDescending(candidate => candidate.Priority)
            .FirstOrDefault();
}

internal sealed class WindowsToolHost
{
    private static readonly TimeSpan WorkflowPreparationLifetime = TimeSpan.FromMinutes(2);
    private readonly WindowsPlatformProfile _platform;
    private readonly WindowsToolRegistry _registry;
    private readonly MemoryArgumentResolver? _memoryArguments;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, object?>> _approvedInputs = new(StringComparer.Ordinal);

    public WindowsToolHost(
        WindowsPlatformProfile platform,
        IEnumerable<IWindowsToolAdapter>? adapters = null,
        IUserNotificationService? notifications = null,
        IUndoJournal? undoJournal = null,
        IMemoryRepository? memories = null,
        IScheduleRepository? schedules = null,
        IAgentJobRepository? agentJobs = null,
        Func<ILocalSubagentOrchestrator?>? getSubagentOrchestrator = null,
        Func<McpConnectionSettings?>? loadMcpSettings = null,
        Func<McpCallParams, CancellationToken, Task<McpCallResult>>? callMcp = null,
        IConnectionHttpExecutionResolver? connectionHttpResolver = null,
        IMailGatewayExecutionResolver? mailGatewayResolver = null,
        IMailOutboxRepository? mailOutbox = null,
        MailGatewayWireClient? mailGatewayWireClient = null,
        ISmtpExecutionResolver? smtpResolver = null,
        SmtpWireClient? smtpWireClient = null)
    {
        _platform = platform;
        _memoryArguments = memories is null ? null : new MemoryArgumentResolver(memories);
        var windowCatalog = new Win32WindowCatalog();
        var windowActions = new Win32WindowActionService(windowCatalog);
        var uiAutomation = new WindowsUiAutomationService();
        var edgeSessions = new EdgeBrowserSessionService(
            windowCatalog, uiAutomation, new EdgeProcessLauncher());
        var appCatalog = new StartMenuAppCatalog();
        var webClient = new BoundedWebContentClient(BoundedWebContentClient.CreateDefaultHttpClient());
        var webSearchSettings = new WebSearchSettingsStore();
        var defaultAdapters = new List<IWindowsToolAdapter>
        {
            new SystemGetStatusTool(platform),
            new SystemGetPowerStatusTool(),
            new SystemGetStorageStatusTool(),
            new SystemGetDiskHealthTool(),
            new SystemGetSecurityStatusTool(),
            new SystemGetResourceStatusTool(),
            new SystemGetProcessResourceStatusTool(),
            new SystemGetNetworkStatusTool(),
            new SystemGetNetworkDetailsTool(),
            new SystemGetDeviceStatusTool(),
            new AppListWindowsTool(windowCatalog),
            new AppSearchInstalledTool(appCatalog),
            new AppLaunchTool(appCatalog, new ShellRegisteredAppLauncher()),
            new AppActivateTool(windowActions),
            new AppCloseTool(windowActions),
            new AppSetWindowStateTool(windowActions),
            new SystemOpenSettingsTool(),
            new SystemSessionActionTool(),
            new UiaInspectTool(uiAutomation),
            new UiaInvokeTool(uiAutomation),
            new UiaSetValueTool(uiAutomation),
            new UiaSendTextTool(uiAutomation),
            new ExplorerGetContextTool(new WindowsExplorerContextReader()),
            new FileSearchTool(new SafeFileOperationService()),
            new FileGetMetadataTool(new SafeFileOperationService()),
            new FileReadTextTool(new SafeFileOperationService(), new BoundedFileTextReader()),
            new FileOpenTool(new SafeFileOperationService()),
            new ClipboardReadTextTool(new WindowsClipboardTextService()),
            new ClipboardWriteTextTool(new WindowsClipboardTextService()),
            new WebFetchTool(webClient),
            new WebSearchTool(webClient, webSearchSettings.Load),
            new BrowserOpenTool(edgeSessions),
            new BrowserSnapshotTool(edgeSessions),
            new MailCreateDraftTool(),
        };
        if (undoJournal is not null)
        {
            var fileOperations = new SafeFileOperationService();
            defaultAdapters.Add(new FileCopyTool(fileOperations, undoJournal));
            defaultAdapters.Add(new FileMoveTool(fileOperations, undoJournal));
            defaultAdapters.Add(new FileRenameTool(fileOperations, undoJournal));
            defaultAdapters.Add(new FileRecycleTool(fileOperations));
            defaultAdapters.Add(new FileUndoTool(fileOperations, undoJournal));
            defaultAdapters.Add(new FileCreateDirectoryTool(fileOperations, undoJournal));
            defaultAdapters.Add(new FileWriteTextTool(fileOperations, undoJournal));
            defaultAdapters.Add(new FileZipCreateTool(fileOperations, undoJournal));
            defaultAdapters.Add(new FileZipExtractTool(fileOperations, undoJournal));
        }
        if (notifications is not null) defaultAdapters.Add(new SystemShowNotificationTool(notifications));
        if (memories is not null)
        {
            defaultAdapters.Add(new MemoryRememberTool(memories));
            defaultAdapters.Add(new MemoryListTool(memories));
            defaultAdapters.Add(new MemoryForgetTool(memories));
        }
        if (schedules is not null)
        {
            defaultAdapters.Add(new ScheduleCreateTool(schedules));
            defaultAdapters.Add(new ScheduleListTool(schedules));
            defaultAdapters.Add(new ScheduleCancelTool(schedules));
        }
        if (agentJobs is not null)
        {
            defaultAdapters.Add(new AgentJobCreateTool(agentJobs));
            defaultAdapters.Add(new AgentJobListTool(agentJobs));
            defaultAdapters.Add(new AgentJobCancelTool(agentJobs));
            defaultAdapters.Add(new AgentJobControlTool(agentJobs));
        }
        if (getSubagentOrchestrator is not null)
            defaultAdapters.Add(new SubagentRunTool(getSubagentOrchestrator));
        if (loadMcpSettings is not null && callMcp is not null)
            defaultAdapters.Add(new McpReadTool(loadMcpSettings, callMcp));
        if (connectionHttpResolver is not null)
            defaultAdapters.Add(new ConnectionHttpRequestTool(connectionHttpResolver));
        IWorkflowOneShotToolAdapter? mailGatewaySend =
            mailGatewayResolver is not null && mailOutbox is not null && mailGatewayWireClient is not null
                ? new MailSendTool(mailGatewayResolver, mailOutbox, mailGatewayWireClient)
                : null;
        IWorkflowOneShotToolAdapter? smtpSend =
            smtpResolver is not null && mailOutbox is not null && smtpWireClient is not null
                ? new SmtpMailSendTool(smtpResolver, mailOutbox, smtpWireClient)
                : null;
        if (mailGatewaySend is not null && smtpSend is not null)
            defaultAdapters.Add(new CompositeMailSendTool(mailGatewaySend, smtpSend));
        else if (mailGatewaySend is not null)
            defaultAdapters.Add(mailGatewaySend);
        else if (smtpSend is not null)
            defaultAdapters.Add(smtpSend);
        _registry = new WindowsToolRegistry(adapters ?? defaultAdapters);
    }

    public IReadOnlyList<WindowsToolDescriptor> Descriptors => _registry.Descriptors
        .Where(descriptor => _platform.Supports(descriptor.RequiredCapabilities))
        .ToArray();

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        ToolInvokeParams invocation,
        CancellationToken cancellationToken = default) =>
        await ExecuteCoreAsync(invocation, allowWorkflowOnlyObservation: false, cancellationToken)
            .ConfigureAwait(false);

    internal async Task<WindowsToolExecutionResult> ExecuteWorkflowObservationAsync(
        ToolInvokeParams invocation,
        CancellationToken cancellationToken = default) =>
        await ExecuteCoreAsync(invocation, allowWorkflowOnlyObservation: true, cancellationToken)
            .ConfigureAwait(false);

    private async Task<WindowsToolExecutionResult> ExecuteCoreAsync(
        ToolInvokeParams invocation,
        bool allowWorkflowOnlyObservation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var adapter = FindAdapter(invocation);
        if (adapter is null)
            return Failure("이 윈도우 버전에서 사용할 수 없는 기능이에요.");
        if (adapter is IWorkflowOnlyObservationToolAdapter && !allowWorkflowOnlyObservation)
            return Failure("이 기능은 검증된 작업 흐름 안에서만 사용할 수 있어요.");
        if (!StringComparer.Ordinal.Equals(adapter.Risk, invocation.Risk))
            return Failure("작업 도구의 위험 수준이 앱 정책과 일치하지 않습니다.");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(adapter.Timeout);
            var resolution = _approvedInputs.TryRemove(invocation.ToolCallId, out var approvedInput)
                ? new MemoryArgumentResolution(true, approvedInput)
                : _memoryArguments is null
                    ? new MemoryArgumentResolution(true, invocation.Input)
                    : await _memoryArguments.ResolveAsync(invocation.Input, timeout.Token).ConfigureAwait(false);
            if (!resolution.Success) return Failure(resolution.Error!);
            var executionInput = AttachDesktopContext(invocation, resolution.Input);
            return await adapter.ExecuteAsync(executionInput, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("작업 도구 실행을 중단했어요.");
        }
        catch (OperationCanceledException)
        {
            return Failure("작업 도구 실행 제한 시간을 초과했어요.");
        }
        catch (Exception)
        {
            return Failure("윈도우 기능을 실행하지 못했어요.");
        }
    }

    public WindowsToolApprovalPromptResult CreateApprovalPrompt(ToolInvokeParams invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var adapter = FindAdapter(invocation);
        if (adapter is null)
            return new WindowsToolApprovalPromptResult(false, Error: "이 윈도우 버전에서 사용할 수 없는 기능이에요.");
        if (!StringComparer.Ordinal.Equals(adapter.Risk, invocation.Risk))
            return new WindowsToolApprovalPromptResult(false, Error: "작업 도구의 위험 수준이 앱 정책과 일치하지 않습니다.");
        try
        {
            var resolution = _memoryArguments?.ResolveAsync(invocation.Input, CancellationToken.None).GetAwaiter().GetResult()
                ?? new MemoryArgumentResolution(true, invocation.Input);
            if (!resolution.Success) return new WindowsToolApprovalPromptResult(false, Error: resolution.Error);
            var prompt = adapter.CreateApprovalPrompt(resolution.Input);
            if (prompt is not null)
            {
                prompt = prompt with
                {
                    Risk = adapter.Risk,
                    CanUndo = invocation.Name is "file.copy.v1" or "file.move.v1" or "file.rename.v1" or
                        "file.create_directory.v1" or "file.write_text.v1" or "file.zip_create.v1" or "file.zip_extract.v1",
                };
            }
            if (prompt is not null && resolution.ContainsReference)
                _approvedInputs[invocation.ToolCallId] = resolution.Input;
            return prompt is null
                ? new WindowsToolApprovalPromptResult(false, Error: "사용자에게 보여줄 실행 정보를 만들지 못했어요.")
                : new WindowsToolApprovalPromptResult(true, prompt);
        }
        catch (Exception)
        {
            return new WindowsToolApprovalPromptResult(false, Error: "실행할 동작을 확인하지 못했어요.");
        }
    }

    public async Task<WorkflowEffectPreparationResult> PrepareWorkflowEffectAsync(
        ToolInvokeParams invocation,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var adapter = FindAdapter(invocation);
        if (adapter is not IWorkflowOneShotToolAdapter workflowAdapter)
            return new WorkflowEffectPreparationResult(false, Error: "작업 흐름의 일회성 실행 준비를 지원하지 않는 작업 도구예요.");
        if (!StringComparer.Ordinal.Equals(adapter.Risk, invocation.Risk))
            return new WorkflowEffectPreparationResult(false, Error: "작업 도구의 위험 수준이 앱 정책과 일치하지 않습니다.");
        try
        {
            return await workflowAdapter.PrepareWorkflowEffectAsync(
                invocation,
                nowUtc,
                WorkflowPreparationLifetime,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new WorkflowEffectPreparationResult(
                false,
                Error: "작업 흐름 동작 준비를 중단했어요.",
                ErrorCode: "workflow.cancelled");
        }
        catch (Exception)
        {
            return new WorkflowEffectPreparationResult(false, Error: "작업 흐름 동작을 안전하게 준비하지 못했어요.");
        }
    }

    public void DiscardApproval(ToolInvokeParams invocation)
    {
        var adapter = FindAdapter(invocation);
        if (adapter is null) return;
        var input = _approvedInputs.TryRemove(invocation.ToolCallId, out var approvedInput)
            ? approvedInput
            : invocation.Input;
        adapter.DiscardApproval(input);
    }

    private IWindowsToolAdapter? FindAdapter(ToolInvokeParams invocation) =>
        _registry.Resolve(invocation.Name, _platform);

    private static IReadOnlyDictionary<string, object?> AttachDesktopContext(
        ToolInvokeParams invocation,
        IReadOnlyDictionary<string, object?> input)
    {
        if (invocation.Name == "subagent.run.v1")
        {
            var subagentContext = new Dictionary<string, object?>(input, StringComparer.Ordinal)
            {
                ["_conversationId"] = invocation.ConversationId,
                ["_runId"] = invocation.RunId,
                ["_toolCallId"] = invocation.ToolCallId,
            };
            return subagentContext;
        }
        if (invocation.Name is not ("memory.remember.v1" or "schedule.create.v1" or "agent_job.create.v1")) return input;
        var contextual = new Dictionary<string, object?>(input, StringComparer.Ordinal)
        {
            ["_source"] = $"conversation:{invocation.ConversationId}",
        };
        return contextual;
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}
