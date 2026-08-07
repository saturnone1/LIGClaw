using System.Windows;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal interface IClipboardTextService
{
    Task<string> ReadTextAsync(CancellationToken cancellationToken);
    Task WriteTextAsync(string text, CancellationToken cancellationToken);
}

internal sealed class WindowsClipboardTextService : IClipboardTextService
{
    public async Task<string> ReadTextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("Windows UI dispatcher를 사용할 수 없습니다.");
        return await dispatcher.InvokeAsync(() =>
            System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty);
    }

    public async Task WriteTextAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("Windows UI dispatcher를 사용할 수 없습니다.");
        await dispatcher.InvokeAsync(() => System.Windows.Clipboard.SetText(text));
    }
}

internal sealed class ClipboardReadTextTool(IClipboardTextService clipboard) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly HashSet<string> _preparedReasons = new(StringComparer.Ordinal);

    public string Name => "clipboard.read_text.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.Clipboard;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var reason = ReadReason(input);
        if (reason is null) return null;
        lock (_sync) _preparedReasons.Add(reason);
        return new WindowsToolApprovalPrompt(
            "클립보드 텍스트를 읽을까요?",
            "현재 클립보드의 텍스트를 최대 32,768자까지 읽습니다.",
            $"요청 이유: {reason}\n\n읽은 텍스트는 현재 모델 요청의 컨텍스트로 전달될 수 있습니다. 이미지와 파일은 읽지 않습니다.");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var reason = ReadReason(input);
        if (reason is null) return Failure("요청 이유가 올바르지 않습니다.");
        lock (_sync)
        {
            if (!_preparedReasons.Remove(reason)) return Failure("승인한 클립보드 읽기 정보를 찾을 수 없어요.");
        }
        var raw = await clipboard.ReadTextAsync(cancellationToken).ConfigureAwait(false);
        var truncated = raw.Length > 32_768;
        var text = truncated ? raw[..32_768] : raw;
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?> { ["text"] = text, ["length"] = text.Length, ["truncated"] = truncated },
            ActivitySummary: $"클립보드 텍스트 {text.Length}자를 읽었어요.");
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input)
    {
        var reason = ReadReason(input);
        if (reason is null) return;
        lock (_sync) _preparedReasons.Remove(reason);
    }

    private static string? ReadReason(IReadOnlyDictionary<string, object?> input) =>
        ToolInputReader.HasOnlyKeys(input, "reason") &&
        ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason) ? reason : null;

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}

internal sealed class ClipboardWriteTextTool(IClipboardTextService clipboard) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _prepared = new(StringComparer.Ordinal);

    public string Name => "clipboard.write_text.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.Clipboard;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return null;
        lock (_sync) _prepared[parameters.Value.Reason] = parameters.Value.Text;
        var preview = parameters.Value.Text.Length <= 200
            ? parameters.Value.Text
            : string.Concat(parameters.Value.Text.AsSpan(0, 200), "…");
        return new WindowsToolApprovalPrompt(
            "클립보드에 쓸까요?",
            $"텍스트 {parameters.Value.Text.Length}자를 클립보드에 씁니다.",
            $"미리보기:\n{preview}\n\n요청 이유: {parameters.Value.Reason}\n현재 클립보드 내용은 교체됩니다.");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var parameters = Read(input);
        if (parameters is null) return Failure("텍스트 또는 요청 이유가 올바르지 않습니다.");
        string? approvedText;
        lock (_sync) _prepared.Remove(parameters.Value.Reason, out approvedText);
        if (!StringComparer.Ordinal.Equals(approvedText, parameters.Value.Text))
            return Failure("승인 후 클립보드 텍스트가 변경되어 쓰지 않았어요.");
        await clipboard.WriteTextAsync(parameters.Value.Text, cancellationToken).ConfigureAwait(false);
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?> { ["written"] = true, ["length"] = parameters.Value.Text.Length },
            ActivitySummary: $"클립보드에 텍스트 {parameters.Value.Text.Length}자를 썼어요.");
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return;
        lock (_sync) _prepared.Remove(parameters.Value.Reason);
    }

    private static (string Text, string Reason)? Read(IReadOnlyDictionary<string, object?> input)
    {
        if (!ToolInputReader.HasOnlyKeys(input, "text", "reason") ||
            !ToolInputReader.TryGetRequiredStringExact(input, "text", 32_768, out var text) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason))
            return null;
        return (text, reason);
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}
