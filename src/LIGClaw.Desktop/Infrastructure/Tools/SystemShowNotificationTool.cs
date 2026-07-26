using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal interface IUserNotificationService
{
    Task ShowAsync(string title, string message, CancellationToken cancellationToken);
}

internal sealed class SystemShowNotificationTool(IUserNotificationService notifications) : IWindowsToolAdapter
{
    public string Name => "system.show_notification.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.UserNotification;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        return parameters is null
            ? null
            : new WindowsToolApprovalPrompt(
                "Windows 알림을 표시할까요?",
                $"“{parameters.Value.Title}” 알림을 표시합니다.",
                $"알림 내용: {parameters.Value.Message}\n\n요청 이유: {parameters.Value.Reason}");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var parameters = Read(input);
        if (parameters is null) return Failure("알림 제목, 내용 또는 요청 이유가 올바르지 않습니다.");
        await notifications.ShowAsync(parameters.Value.Title, parameters.Value.Message, cancellationToken).ConfigureAwait(false);
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?> { ["shown"] = true },
            ActivitySummary: "승인한 Windows 알림을 표시했어요.");
    }

    private static (string Title, string Message, string Reason)? Read(IReadOnlyDictionary<string, object?> input)
    {
        if (!ToolInputReader.HasOnlyKeys(input, "title", "message", "reason") ||
            !ToolInputReader.TryGetRequiredString(input, "title", 63, out var title) ||
            !ToolInputReader.TryGetRequiredString(input, "message", 255, out var message) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason))
            return null;
        return (title, message, reason);
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}
