using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class UiaInspectTool(IUiAutomationService automation) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _preparedWindows = new(StringComparer.Ordinal);

    public string Name => "uia.inspect.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.UiAutomation;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return null;
        var target = automation.ResolveWindowAsync(parameters.WindowId, CancellationToken.None).GetAwaiter().GetResult();
        if (target is null) return null;
        lock (_sync) _preparedWindows[Fingerprint(input)] = target.Identity;
        return new WindowsToolApprovalPrompt(
            "이 창의 화면 요소를 확인할까요?",
            $"{target.Title} 창의 UI 요소 이름과 지원 동작을 조회합니다.",
            $"앱: {target.ProcessName}\n검색: {parameters.Query ?? "전체"}\n최대 결과: {parameters.MaximumElements}개\n요청 이유: {parameters.Reason}\n\n입력값 자체는 읽지 않지만 화면에 표시된 텍스트가 모델에 전달될 수 있습니다.");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var parameters = Read(input);
        if (parameters is null) return Failure("창 또는 UI 요소 조회 조건이 올바르지 않습니다.");
        string? preparedIdentity;
        lock (_sync) _preparedWindows.Remove(Fingerprint(input), out preparedIdentity);
        if (preparedIdentity is null) return Failure("승인한 UI 조회 정보를 찾을 수 없어요.");
        var window = await automation.ResolveWindowAsync(parameters.WindowId, cancellationToken).ConfigureAwait(false);
        if (window is null || !StringComparer.Ordinal.Equals(window.Identity, preparedIdentity))
            return Failure("승인 후 대상 창이 변경되어 조회하지 않았어요.");
        var inspection = await automation.InspectAsync(
            parameters.WindowId,
            parameters.Query,
            parameters.MaximumElements,
            cancellationToken).ConfigureAwait(false);
        if (inspection is null) return Failure("대상 창의 UI 요소를 확인하지 못했어요.");
        var elements = inspection.Elements.Select(element =>
            (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["elementId"] = element.ElementId,
                ["name"] = element.Name,
                ["automationId"] = element.AutomationId,
                ["controlType"] = element.ControlType,
                ["isEnabled"] = element.IsEnabled,
                ["isOffscreen"] = element.IsOffscreen,
                ["supportsInvoke"] = element.SupportsInvoke,
                ["supportsValue"] = element.SupportsValue,
                ["supportsTextInput"] = element.SupportsTextInput,
            }).ToArray();
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?>
            {
                ["windowId"] = inspection.WindowId,
                ["title"] = inspection.Title,
                ["processName"] = inspection.ProcessName,
                ["elements"] = elements,
                ["truncated"] = inspection.Truncated,
            },
            ActivitySummary: $"{inspection.ProcessName} 창의 UI 요소 {elements.Length}개를 조회했어요.");
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input)
    {
        lock (_sync) _preparedWindows.Remove(Fingerprint(input));
    }

    private static InspectInput? Read(IReadOnlyDictionary<string, object?> input)
    {
        if (!ToolInputReader.HasOnlyKeys(input, "windowId", "query", "maxElements", "reason") ||
            !ToolInputReader.TryGetRequiredString(input, "windowId", 18, out var windowId) ||
            !windowId.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason)) return null;
        string? query = null;
        if (input.ContainsKey("query") && !ToolInputReader.TryGetRequiredString(input, "query", 128, out query)) return null;
        var maximumElements = ReadOptionalInteger(input, "maxElements") ?? 30;
        return maximumElements is < 1 or > 50
            ? null
            : new InspectInput(windowId, query, checked((int)maximumElements), reason);
    }

    private static long? ReadOptionalInteger(IReadOnlyDictionary<string, object?> input, string key)
    {
        if (!input.TryGetValue(key, out var raw)) return null;
        return raw switch
        {
            int value => value,
            long value => value,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var value) => value,
            _ => -1,
        };
    }

    private static string Fingerprint(IReadOnlyDictionary<string, object?> input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', input
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}={Convert.ToString(entry.Value, CultureInfo.InvariantCulture)}")))));

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);

    private sealed record InspectInput(string WindowId, string? Query, int MaximumElements, string Reason);
}

internal abstract class UiaElementActionTool(IUiAutomationService automation) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _preparedTargets = new(StringComparer.Ordinal);

    public abstract string Name { get; }
    public string Risk => "R2";
    public WindowsCapability RequiredCapabilities => WindowsCapability.UiAutomation;
    public int Priority => 0;
    protected abstract string Heading { get; }
    protected abstract string ActionDescription(UiAutomationElementTarget target, ElementActionInput input);
    protected abstract string DetailDescription(ElementActionInput input);
    protected abstract bool Supports(UiAutomationElementTarget target);
    protected IUiAutomationService Automation { get; } = automation;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return null;
        var target = Automation.ResolveAsync(parameters.ElementId, CancellationToken.None).GetAwaiter().GetResult();
        if (target is null || target.IsPassword || !Supports(target)) return null;
        lock (_sync) _preparedTargets[Fingerprint(parameters)] = target.Identity;
        return new WindowsToolApprovalPrompt(
            Heading,
            ActionDescription(target, parameters),
            $"창: {target.WindowTitle}\n앱: {target.ProcessName}\n요소: {DisplayName(target)}\n{DetailDescription(parameters)}요청 이유: {parameters.Reason}\n\n승인 직후 동일한 창과 UI 요소를 다시 확인하고 focus가 일치할 때만 실행합니다.");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var parameters = Read(input);
        if (parameters is null) return Failure("UI 요소 또는 요청 값이 올바르지 않습니다.");
        string? preparedIdentity;
        lock (_sync) _preparedTargets.Remove(Fingerprint(parameters), out preparedIdentity);
        if (preparedIdentity is null) return Failure("승인한 UI 조작 정보를 찾을 수 없어요.");
        var target = await Automation.ResolveAsync(parameters.ElementId, cancellationToken).ConfigureAwait(false);
        if (target is null) return Failure("UI 요소의 유효기간이 지났거나 더 이상 존재하지 않아요. 다시 조회해 주세요.");
        if (!StringComparer.Ordinal.Equals(target.Identity, preparedIdentity))
            return Failure("승인 후 대상 창이나 UI 요소가 변경되어 실행하지 않았어요.");
        if (target.IsPassword) return Failure("비밀번호 입력 요소는 자동 조작하지 않습니다.");
        if (!Supports(target)) return Failure("이 UI 요소는 요청한 동작을 지원하지 않습니다.");
        var status = await ExecuteActionAsync(target, parameters, cancellationToken).ConfigureAwait(false);
        return status == UiAutomationActionStatus.Succeeded
            ? Success()
            : Failure(StatusMessage(status));
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        if (parameters is null) return;
        lock (_sync) _preparedTargets.Remove(Fingerprint(parameters));
    }

    protected abstract ElementActionInput? Read(IReadOnlyDictionary<string, object?> input);
    protected abstract Task<UiAutomationActionStatus> ExecuteActionAsync(
        UiAutomationElementTarget target,
        ElementActionInput input,
        CancellationToken cancellationToken);
    protected abstract WindowsToolExecutionResult Success();

    protected static ElementActionInput? ReadCore(
        IReadOnlyDictionary<string, object?> input,
        string? payloadKey,
        int payloadMaximumLength,
        bool payloadCanBeEmpty)
    {
        var allowed = payloadKey is null ? new[] { "elementId", "reason" } : ["elementId", payloadKey, "reason"];
        if (!ToolInputReader.HasOnlyKeys(input, allowed) ||
            !ToolInputReader.TryGetRequiredString(input, "elementId", 32, out var elementId) ||
            elementId.Length != 32 || elementId.Any(character => !char.IsAsciiHexDigitLower(character)) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason)) return null;
        if (payloadKey is null) return new ElementActionInput(elementId, null, reason);
        var valid = payloadCanBeEmpty
            ? ToolInputReader.TryGetStringExact(input, payloadKey, payloadMaximumLength, out var payload)
            : ToolInputReader.TryGetRequiredString(input, payloadKey, payloadMaximumLength, out payload);
        return valid ? new ElementActionInput(elementId, payload, reason) : null;
    }

    private static string Fingerprint(ElementActionInput input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{input.ElementId}\n{input.Payload}\n{input.Reason}")));

    private static string DisplayName(UiAutomationElementTarget target) =>
        string.IsNullOrWhiteSpace(target.Name)
            ? string.IsNullOrWhiteSpace(target.AutomationId) ? target.ControlType : target.AutomationId
            : target.Name;

    private static string StatusMessage(UiAutomationActionStatus status) => status switch
    {
        UiAutomationActionStatus.TargetUnavailable => "대상 창이나 UI 요소를 더 이상 찾을 수 없어요.",
        UiAutomationActionStatus.IdentityChanged => "대상 창이나 UI 요소가 바뀌어 실행하지 않았어요.",
        UiAutomationActionStatus.Unsupported => "이 UI 요소는 요청한 동작을 지원하지 않습니다.",
        UiAutomationActionStatus.PasswordField => "비밀번호 입력 요소는 자동 조작하지 않습니다.",
        UiAutomationActionStatus.FocusFailed => "다른 창에 잘못 입력하지 않도록 focus 확인에 실패한 동작을 중단했어요.",
        UiAutomationActionStatus.UserInputDetected => "사용자 입력이 감지되어 자동 입력을 중단했어요.",
        _ => "UI Automation 동작을 완료하지 못했어요.",
    };

    protected static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);

    protected sealed record ElementActionInput(string ElementId, string? Payload, string Reason);
}

internal sealed class UiaInvokeTool(IUiAutomationService automation) : UiaElementActionTool(automation)
{
    public override string Name => "uia.invoke.v1";
    protected override string Heading => "이 화면 요소를 실행할까요?";
    protected override string ActionDescription(UiAutomationElementTarget target, ElementActionInput input) =>
        $"{target.Name} 요소를 클릭과 동등한 UIA Invoke로 실행합니다.";
    protected override string DetailDescription(ElementActionInput input) => string.Empty;
    protected override bool Supports(UiAutomationElementTarget target) => target.SupportsInvoke;
    protected override ElementActionInput? Read(IReadOnlyDictionary<string, object?> input) =>
        ReadCore(input, null, 0, false);
    protected override Task<UiAutomationActionStatus> ExecuteActionAsync(
        UiAutomationElementTarget target,
        ElementActionInput input,
        CancellationToken cancellationToken) => Automation.InvokeAsync(target, cancellationToken);
    protected override WindowsToolExecutionResult Success() => new(
        true,
        new Dictionary<string, object?> { ["invoked"] = true },
        ActivitySummary: "승인한 UI 요소를 실행했어요.");
}

internal sealed class UiaSetValueTool(IUiAutomationService automation) : UiaElementActionTool(automation)
{
    public override string Name => "uia.set_value.v1";
    protected override string Heading => "이 입력값을 설정할까요?";
    protected override string ActionDescription(UiAutomationElementTarget target, ElementActionInput input) =>
        $"{target.Name} 요소의 값을 UIA Value로 설정합니다.";
    protected override string DetailDescription(ElementActionInput input) => $"설정할 값: {input.Payload}\n";
    protected override bool Supports(UiAutomationElementTarget target) => target.SupportsValue;
    protected override ElementActionInput? Read(IReadOnlyDictionary<string, object?> input) =>
        ReadCore(input, "value", 2_000, true);
    protected override Task<UiAutomationActionStatus> ExecuteActionAsync(
        UiAutomationElementTarget target,
        ElementActionInput input,
        CancellationToken cancellationToken) => Automation.SetValueAsync(target, input.Payload!, cancellationToken);
    protected override WindowsToolExecutionResult Success() => new(
        true,
        new Dictionary<string, object?> { ["valueSet"] = true },
        ActivitySummary: "승인한 UI 요소의 값을 설정했어요.");
}

internal sealed class UiaSendTextTool(IUiAutomationService automation) : UiaElementActionTool(automation)
{
    public override string Name => "uia.send_text.v1";
    protected override string Heading => "이 텍스트를 입력할까요?";
    protected override string ActionDescription(UiAutomationElementTarget target, ElementActionInput input) =>
        $"{target.Name} 입력 요소에 일반 텍스트를 입력합니다.";
    protected override string DetailDescription(ElementActionInput input) => $"입력할 텍스트: {input.Payload}\n";
    protected override bool Supports(UiAutomationElementTarget target) =>
        target.IsKeyboardFocusable && target.ControlType == "Edit";
    protected override ElementActionInput? Read(IReadOnlyDictionary<string, object?> input) =>
        ReadCore(input, "text", 500, false);
    protected override Task<UiAutomationActionStatus> ExecuteActionAsync(
        UiAutomationElementTarget target,
        ElementActionInput input,
        CancellationToken cancellationToken) => Automation.SendTextAsync(target, input.Payload!, cancellationToken);
    protected override WindowsToolExecutionResult Success() => new(
        true,
        new Dictionary<string, object?> { ["textSent"] = true },
        ActivitySummary: "승인한 UI 요소에 텍스트를 입력했어요.");
}
