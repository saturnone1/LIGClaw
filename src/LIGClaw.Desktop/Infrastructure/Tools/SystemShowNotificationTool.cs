using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal interface IUserNotificationService
{
    Task ShowAsync(string title, string message, CancellationToken cancellationToken);
}

internal sealed class SystemShowNotificationTool(IUserNotificationService notifications) : IWorkflowOneShotToolAdapter
{
    public string Name => "system.show_notification.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.UserNotification;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        return parameters is null ? null : CreatePrompt(parameters.Value);
    }

    public Task<WorkflowEffectPreparationResult> PrepareWorkflowEffectAsync(
        LIGClaw.Contracts.Generated.ToolInvokeParams invocation,
        DateTimeOffset nowUtc,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parameters = Read(invocation.Input);
        return Task.FromResult(parameters is null || lifetime <= TimeSpan.Zero
            ? new WorkflowEffectPreparationResult(false, Error: "알림 입력을 one-shot 동작으로 준비하지 못했어요.")
            : new WorkflowEffectPreparationResult(
                true,
                new PreparedNotificationEffect(
                    notifications,
                    parameters.Value.Title,
                    parameters.Value.Message,
                    parameters.Value.Reason,
                    nowUtc.Add(lifetime))));
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

    private static WindowsToolApprovalPrompt CreatePrompt((string Title, string Message, string Reason) parameters) =>
        new(
            "Windows 알림을 표시할까요?",
            $"“{parameters.Title}” 알림을 표시합니다.",
            $"알림 내용: {parameters.Message}\n\n요청 이유: {parameters.Reason}");

    private sealed class PreparedNotificationEffect : IPreparedWorkflowEffect
    {
        private readonly IUserNotificationService notifications;
        private readonly string title;
        private readonly string message;
        private int state;

        public PreparedNotificationEffect(
            IUserNotificationService notifications,
            string title,
            string message,
            string reason,
            DateTimeOffset expiresAtUtc)
        {
            this.notifications = notifications;
            this.title = string.Concat(title);
            this.message = string.Concat(message);
            ExpiresAtUtc = expiresAtUtc;
            ApprovalPrompt = CreatePrompt((this.title, this.message, string.Concat(reason)));
            CanonicalPayloadHash = WorkflowPreparedEffectHash.Compute(
                ToolName,
                Risk,
                this.title,
                this.message,
                reason);
            CanonicalBindingHash = WorkflowPreparedEffectHash.Compute(
                ToolName,
                "current-windows-user",
                "local-notification-service");
        }

        public string ToolName => "system.show_notification.v1";
        public string Risk => "R1";
        public string CanonicalPayloadHash { get; }
        public string CanonicalBindingHash { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public WindowsToolApprovalPrompt ApprovalPrompt { get; }

        public async Task<WorkflowPreparedEffectExecution> ExecuteOnceAsync(
            DateTimeOffset trustedNowUtc,
            CancellationToken cancellationToken)
        {
            if (trustedNowUtc.Offset != TimeSpan.Zero || trustedNowUtc >= ExpiresAtUtc)
            {
                _ = Interlocked.CompareExchange(ref state, 2, 0);
                return NotStarted("준비된 알림 동작이 만료됐어요.");
            }
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
                return NotStarted("준비된 알림 동작은 이미 소비됐어요.");
            try
            {
                await notifications.ShowAsync(title, message, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref state, 2);
                return new WorkflowPreparedEffectExecution(
                    WorkflowEffectDisposition.Succeeded,
                    new WindowsToolExecutionResult(
                        true,
                        new Dictionary<string, object?> { ["shown"] = true },
                        ActivitySummary: "승인한 Windows 알림을 표시했어요."));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref state, 2);
                return Unknown();
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref state, 2);
                return Unknown();
            }
        }

        public ValueTask DisposeAsync()
        {
            _ = Interlocked.CompareExchange(ref state, 2, 0);
            return ValueTask.CompletedTask;
        }

        private static WorkflowPreparedEffectExecution NotStarted(string error) =>
            new(
                WorkflowEffectDisposition.NotStarted,
                new WindowsToolExecutionResult(false, new Dictionary<string, object?>(), error));

        private static WorkflowPreparedEffectExecution Unknown() =>
            new(
                WorkflowEffectDisposition.Unknown,
                new WindowsToolExecutionResult(
                    false,
                    new Dictionary<string, object?>(),
                    "알림 실행 결과를 확정하지 못했어요."));
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}
