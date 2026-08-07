using System.Globalization;
using System.Runtime.InteropServices;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record WindowActionTarget(string WindowId, string Title, string ProcessName)
{
    public string Identity => $"{WindowId}\n{ProcessName}\n{Title}";
}

internal interface IWindowActionService
{
    Task<WindowActionTarget?> ResolveAsync(string windowId, CancellationToken cancellationToken);
    Task<bool> ActivateAsync(WindowActionTarget target, CancellationToken cancellationToken);
    Task<bool> CloseAsync(WindowActionTarget target, CancellationToken cancellationToken);
    Task<bool> SetStateAsync(WindowActionTarget target, string state, CancellationToken cancellationToken) => Task.FromResult(false);
}

internal sealed class Win32WindowActionService(IWindowCatalog catalog) : IWindowActionService
{
    private const int RestoreWindow = 9;
    private const uint CloseWindow = 0x0010;
    private const uint AbortIfHung = 0x0002;

    public async Task<WindowActionTarget?> ResolveAsync(string windowId, CancellationToken cancellationToken)
    {
        if (!TryParseWindowId(windowId, out _)) return null;
        var entry = (await catalog.ListWindowsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(candidate => StringComparer.OrdinalIgnoreCase.Equals(candidate.WindowId, windowId));
        return entry is null ? null : new WindowActionTarget(entry.WindowId, entry.Title, entry.ProcessName);
    }

    public Task<bool> ActivateAsync(WindowActionTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParseWindowId(target.WindowId, out var window) || !IsWindow(window)) return Task.FromResult(false);
        if (IsIconic(window)) _ = ShowWindow(window, RestoreWindow);
        return Task.FromResult(SetForegroundWindow(window));
    }

    public Task<bool> CloseAsync(WindowActionTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParseWindowId(target.WindowId, out var window) || !IsWindow(window)) return Task.FromResult(false);
        var sent = SendMessageTimeout(window, CloseWindow, UIntPtr.Zero, IntPtr.Zero, AbortIfHung, 2_000, out _);
        return Task.FromResult(sent != IntPtr.Zero);
    }

    public Task<bool> SetStateAsync(WindowActionTarget target, string state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParseWindowId(target.WindowId, out var window) || !IsWindow(window)) return Task.FromResult(false);
        var command = state switch { "minimize" => 6, "maximize" => 3, "restore" => RestoreWindow, _ => -1 };
        if (command < 0) return Task.FromResult(false);
        _ = ShowWindow(window, command);
        return Task.FromResult(true);
    }

    private static bool TryParseWindowId(string value, out IntPtr window)
    {
        window = IntPtr.Zero;
        if (value.Length is < 3 or > 18 || !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
            return false;
        window = new IntPtr(parsed);
        return true;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out UIntPtr result);
}

internal abstract class WindowActionTool(IWindowActionService windows) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _preparedTargets = new(StringComparer.OrdinalIgnoreCase);

    public abstract string Name { get; }
    public abstract string Risk { get; }
    public WindowsCapability RequiredCapabilities => WindowsCapability.Win32DesktopShell;
    public int Priority => 0;
    protected abstract string Heading { get; }
    protected abstract string ActionVerb { get; }
    protected virtual string SafetyNote => "승인 직전에 확인한 동일한 창만 대상으로 합니다.";
    protected IWindowActionService Windows { get; } = windows;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return null;
        var target = Windows.ResolveAsync(parameters.Value.WindowId, CancellationToken.None).GetAwaiter().GetResult();
        if (target is null) return null;
        lock (_sync) _preparedTargets[parameters.Value.WindowId] = target.Identity;
        return new WindowsToolApprovalPrompt(
            Heading,
            $"{target.Title} 창을 {ActionVerb}.",
            $"앱: {target.ProcessName}\n요청 이유: {parameters.Value.Reason}\n\n{SafetyNote}");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var parameters = Read(input);
        if (parameters is null) return Failure("창 ID 또는 요청 이유가 올바르지 않습니다.");
        string? preparedIdentity;
        lock (_sync) _preparedTargets.Remove(parameters.Value.WindowId, out preparedIdentity);
        if (preparedIdentity is null) return Failure("승인한 창 정보를 찾을 수 없어요.");
        var target = await Windows.ResolveAsync(parameters.Value.WindowId, cancellationToken).ConfigureAwait(false);
        if (target is null || !StringComparer.Ordinal.Equals(target.Identity, preparedIdentity))
            return Failure("승인 후 대상 창이 변경되어 실행하지 않았어요.");
        return await ExecuteTargetAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return;
        lock (_sync) _preparedTargets.Remove(parameters.Value.WindowId);
    }

    protected abstract Task<WindowsToolExecutionResult> ExecuteTargetAsync(
        WindowActionTarget target,
        CancellationToken cancellationToken);

    protected static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);

    private static (string WindowId, string Reason)? Read(IReadOnlyDictionary<string, object?> input)
    {
        if (!ToolInputReader.HasOnlyKeys(input, "windowId", "reason") ||
            !ToolInputReader.TryGetRequiredString(input, "windowId", 18, out var windowId) ||
            !windowId.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason))
            return null;
        return (windowId, reason);
    }
}

internal sealed class AppActivateTool(IWindowActionService windows) : WindowActionTool(windows)
{
    public override string Name => "app.activate.v1";
    public override string Risk => "R1";
    protected override string Heading => "창을 앞으로 가져올까요?";
    protected override string ActionVerb => "앞으로 가져옵니다";

    protected override async Task<WindowsToolExecutionResult> ExecuteTargetAsync(
        WindowActionTarget target,
        CancellationToken cancellationToken) =>
        await Windows.ActivateAsync(target, cancellationToken).ConfigureAwait(false)
            ? new WindowsToolExecutionResult(
                true,
                new Dictionary<string, object?>
                {
                    ["activated"] = true,
                    ["title"] = target.Title,
                    ["processName"] = target.ProcessName,
                },
                ActivitySummary: $"{target.Title} 창을 앞으로 가져왔어요.")
            : Failure("창을 앞으로 가져오지 못했어요.");
}

internal sealed class AppCloseTool(IWindowActionService windows) : WindowActionTool(windows)
{
    public override string Name => "app.close.v1";
    public override string Risk => "R2";
    protected override string Heading => "창을 닫을까요?";
    protected override string ActionVerb => "닫습니다";
    protected override string SafetyNote =>
        "저장하지 않은 작업이 있으면 앱이 확인을 요청할 수 있습니다. 강제 종료하지 않으며 승인 직전에 확인한 동일한 창만 대상으로 합니다.";

    protected override async Task<WindowsToolExecutionResult> ExecuteTargetAsync(
        WindowActionTarget target,
        CancellationToken cancellationToken) =>
        await Windows.CloseAsync(target, cancellationToken).ConfigureAwait(false)
            ? new WindowsToolExecutionResult(
                true,
                new Dictionary<string, object?>
                {
                    ["closeRequested"] = true,
                    ["title"] = target.Title,
                    ["processName"] = target.ProcessName,
                },
                ActivitySummary: $"{target.Title} 창에 닫기를 요청했어요.")
            : Failure("창 닫기를 요청하지 못했어요.");
}

internal sealed class AppSetWindowStateTool(IWindowActionService windows) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (string Identity, string State)> _prepared = new(StringComparer.OrdinalIgnoreCase);
    public string Name => "app.set_window_state.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.Win32DesktopShell;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input); if (parameters is null) return null;
        var target = windows.ResolveAsync(parameters.Value.WindowId, CancellationToken.None).GetAwaiter().GetResult();
        if (target is null) return null;
        lock (_sync) _prepared[parameters.Value.WindowId] = (target.Identity, parameters.Value.State);
        return new WindowsToolApprovalPrompt(
            "창 상태를 바꿀까요?", $"{target.Title} 창을 {StateLabel(parameters.Value.State)}합니다.",
            $"앱: {target.ProcessName}\n요청 이유: {parameters.Value.Reason}\n\n승인 직전에 확인한 동일한 창만 대상으로 합니다.",
            GrantScope: $"window:{target.Identity}:{parameters.Value.State}");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken)
    {
        var parameters = Read(input); if (parameters is null) return Failure("창 ID, 상태 또는 요청 이유가 올바르지 않습니다.");
        (string Identity, string State) prepared;
        lock (_sync) { if (!_prepared.Remove(parameters.Value.WindowId, out prepared)) return Failure("승인한 창 정보를 찾을 수 없어요."); }
        var target = await windows.ResolveAsync(parameters.Value.WindowId, cancellationToken).ConfigureAwait(false);
        if (target is null || prepared.State != parameters.Value.State || !StringComparer.Ordinal.Equals(target.Identity, prepared.Identity)) return Failure("승인 후 대상 창이 변경되어 실행하지 않았어요.");
        if (!await windows.SetStateAsync(target, parameters.Value.State, cancellationToken).ConfigureAwait(false)) return Failure("창 상태를 바꾸지 못했어요.");
        return new WindowsToolExecutionResult(true, new Dictionary<string, object?> { ["changed"] = true, ["state"] = parameters.Value.State, ["title"] = target.Title, ["processName"] = target.ProcessName }, ActivitySummary: $"{target.Title} 창을 {StateLabel(parameters.Value.State)}했어요.");
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input) { var value = Read(input); if (value is not null) lock (_sync) _prepared.Remove(value.Value.WindowId); }
    private static (string WindowId, string State, string Reason)? Read(IReadOnlyDictionary<string, object?> input) => ToolInputReader.HasOnlyKeys(input, "windowId", "state", "reason") && ToolInputReader.TryGetRequiredString(input, "windowId", 18, out var windowId) && windowId.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ToolInputReader.TryGetRequiredString(input, "state", 8, out var state) && state is "minimize" or "maximize" or "restore" && ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason) ? (windowId, state, reason) : null;
    private static string StateLabel(string state) => state switch { "minimize" => "최소화", "maximize" => "최대화", _ => "복원" };
    private static WindowsToolExecutionResult Failure(string error) => new(false, new Dictionary<string, object?>(), error);
}
