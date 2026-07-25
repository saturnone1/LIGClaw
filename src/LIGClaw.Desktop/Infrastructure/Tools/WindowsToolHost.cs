using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record WindowsToolExecutionResult(
    bool Success,
    IReadOnlyDictionary<string, object?> Output,
    string? Error = null,
    string? ActivitySummary = null);

internal interface IWindowsToolAdapter
{
    string Name { get; }
    string Risk { get; }
    WindowsCapability RequiredCapabilities { get; }
    int Priority { get; }
    Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken);
}

internal sealed class WindowsToolHost
{
    private readonly WindowsPlatformProfile _platform;
    private readonly IReadOnlyList<IWindowsToolAdapter> _adapters;

    public WindowsToolHost(WindowsPlatformProfile platform, IEnumerable<IWindowsToolAdapter>? adapters = null)
    {
        _platform = platform;
        _adapters = (adapters ?? [new SystemGetStatusTool(platform)]).ToArray();
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        ToolInvokeParams invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var adapter = _adapters
            .Where(candidate => StringComparer.Ordinal.Equals(candidate.Name, invocation.Name))
            .Where(candidate => _platform.Supports(candidate.RequiredCapabilities))
            .OrderByDescending(candidate => candidate.Priority)
            .FirstOrDefault();
        if (adapter is null)
            return Failure("이 Windows 버전에서 사용할 수 없는 기능이에요.");
        if (!StringComparer.Ordinal.Equals(adapter.Risk, invocation.Risk))
            return Failure("Tool 위험 등급이 Desktop 정책과 일치하지 않습니다.");
        if (!StringComparer.Ordinal.Equals(adapter.Risk, "R0"))
            return Failure("사용자 승인이 필요한 기능은 아직 실행할 수 없습니다.");

        try
        {
            return await adapter.ExecuteAsync(invocation.Input, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("Tool 실행을 중단했어요.");
        }
        catch (Exception)
        {
            return Failure("Windows 기능을 실행하지 못했어요.");
        }
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}
