using LIGClaw.Application.Agents;
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
    string? ActivitySummary = null);

internal sealed record WindowsToolApprovalPrompt(
    string Heading,
    string Action,
    string Details,
    string Risk = "R1",
    bool CanUndo = false,
    string? GrantScope = null);

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
    int Priority);

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
                adapter.Priority))
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
        Func<McpCallParams, CancellationToken, Task<McpCallResult>>? callMcp = null)
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
        _registry = new WindowsToolRegistry(adapters ?? defaultAdapters);
    }

    public IReadOnlyList<WindowsToolDescriptor> Descriptors => _registry.Descriptors;

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        ToolInvokeParams invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var adapter = FindAdapter(invocation);
        if (adapter is null)
            return Failure("이 Windows 버전에서 사용할 수 없는 기능이에요.");
        if (!StringComparer.Ordinal.Equals(adapter.Risk, invocation.Risk))
            return Failure("Tool 위험 등급이 Desktop 정책과 일치하지 않습니다.");

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
            return Failure("Tool 실행을 중단했어요.");
        }
        catch (OperationCanceledException)
        {
            return Failure("Tool 실행 제한 시간을 초과했어요.");
        }
        catch (Exception)
        {
            return Failure("Windows 기능을 실행하지 못했어요.");
        }
    }

    public WindowsToolApprovalPromptResult CreateApprovalPrompt(ToolInvokeParams invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var adapter = FindAdapter(invocation);
        if (adapter is null)
            return new WindowsToolApprovalPromptResult(false, Error: "이 Windows 버전에서 사용할 수 없는 기능이에요.");
        if (!StringComparer.Ordinal.Equals(adapter.Risk, invocation.Risk))
            return new WindowsToolApprovalPromptResult(false, Error: "Tool 위험 등급이 Desktop 정책과 일치하지 않습니다.");
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
