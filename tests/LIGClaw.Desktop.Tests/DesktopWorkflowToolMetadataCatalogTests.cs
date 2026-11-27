using LIGClaw.Application.Workflows;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Desktop.Infrastructure.Workflows;

namespace LIGClaw.Desktop.Tests;

public sealed class DesktopWorkflowToolMetadataCatalogTests
{
    [Fact]
    public void JoinsGeneratedContractsWithActualDesktopAdapters()
    {
        var platform = WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);
        var host = new WindowsToolHost(
            platform,
            [
                new SystemGetStatusTool(platform),
                new FileGetMetadataTool(new SafeFileOperationService()),
                new MailCreateDraftTool(),
                new SystemShowNotificationTool(new NoOpNotificationService()),
                new ConnectionHttpRequestTool(new UnavailableConnectionResolver()),
                new PreparedMailSendAdapter(),
            ]);
        var catalog = new DesktopWorkflowToolMetadataCatalog(host);

        Assert.True(catalog.TryGet("system.get_status.v1", out var status));
        Assert.True(catalog.TryGet("file.get_metadata.v1", out var metadata));
        Assert.True(catalog.TryGet("mail.create_draft.v1", out var mailDraft));
        Assert.True(catalog.TryGet("system.show_notification.v1", out var notification));
        Assert.True(catalog.TryGet("connection.http.request.v1", out var connection));
        Assert.True(catalog.TryGet("mail.send.v1", out var mailSend));
        Assert.False(catalog.TryGet("app.launch.v1", out _));
        Assert.Equal("R0", status!.Risk);
        Assert.True(status.IsAvailable);
        Assert.Equal(WorkflowToolPreparationMode.None, status.PreparationMode);
        Assert.Contains("system.get_status.v1.schema.json", status.CanonicalInputSchemaJson, StringComparison.Ordinal);
        Assert.Equal("R0", metadata!.Risk);
        Assert.True(metadata.IsAvailable);
        Assert.Contains("file.get_metadata.v1.result.schema.json", metadata.CanonicalResultSchemaJson, StringComparison.Ordinal);
        Assert.Equal("R0", mailDraft!.Risk);
        Assert.True(mailDraft.IsAvailable);
        Assert.Equal(WorkflowToolPreparationMode.None, mailDraft.PreparationMode);
        Assert.Equal(WorkflowRetryClass.ReadOnly, mailDraft.RetryClass);
        Assert.Equal(WorkflowToolEffectClass.Observation, mailDraft.EffectClass);
        Assert.Contains("mail.create_draft.v1.schema.json", mailDraft.CanonicalInputSchemaJson, StringComparison.Ordinal);
        Assert.Equal("R1", notification!.Risk);
        Assert.True(notification.IsAvailable);
        Assert.Equal(WorkflowToolPreparationMode.OneShot, notification.PreparationMode);
        Assert.Equal(WorkflowToolEffectClass.Effect, notification.EffectClass);
        Assert.Equal("R3", connection!.Risk);
        Assert.True(connection.IsAvailable);
        Assert.Equal(WorkflowToolPreparationMode.OneShot, connection.PreparationMode);
        Assert.Equal(WorkflowRetryClass.Never, connection.RetryClass);
        Assert.Equal(WorkflowToolEffectClass.Effect, connection.EffectClass);
        Assert.Equal("R3", mailSend!.Risk);
        Assert.True(mailSend.IsAvailable);
        Assert.Equal(WorkflowToolPreparationMode.OneShot, mailSend.PreparationMode);
        Assert.Equal(WorkflowRetryClass.Never, mailSend.RetryClass);
        Assert.Equal(WorkflowToolEffectClass.Effect, mailSend.EffectClass);
        Assert.Contains("mail.send.v1.schema.json", mailSend.CanonicalInputSchemaJson, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsMissingOrNonPreparedAdaptersUnavailable()
    {
        var platform = WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true);
        var noNotification = new DesktopWorkflowToolMetadataCatalog(
            new WindowsToolHost(platform, [new SystemGetStatusTool(platform)]));
        var fakeNotification = new DesktopWorkflowToolMetadataCatalog(
            new WindowsToolHost(platform, [new NonPreparedNotificationAdapter()]));

        Assert.True(noNotification.TryGet("system.show_notification.v1", out var missing));
        Assert.False(missing!.IsAvailable);
        Assert.True(fakeNotification.TryGet("system.show_notification.v1", out var nonPrepared));
        Assert.False(nonPrepared!.IsAvailable);

        var directMailDraft = new DesktopWorkflowToolMetadataCatalog(
            new WindowsToolHost(platform, [new NonWorkflowMailDraftAdapter()]));
        Assert.True(directMailDraft.TryGet("mail.create_draft.v1", out var nonWorkflowDraft));
        Assert.False(nonWorkflowDraft!.IsAvailable);

        var directMailSend = new DesktopWorkflowToolMetadataCatalog(
            new WindowsToolHost(platform, [new NonPreparedMailSendAdapter()]));
        Assert.True(directMailSend.TryGet("mail.send.v1", out var nonPreparedMailSend));
        Assert.False(nonPreparedMailSend!.IsAvailable);
    }

    private sealed class NoOpNotificationService : IUserNotificationService
    {
        public Task ShowAsync(string title, string message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NonPreparedNotificationAdapter : IWindowsToolAdapter
    {
        public string Name => "system.show_notification.v1";
        public string Risk => "R1";
        public WindowsCapability RequiredCapabilities => WindowsCapability.UserNotification;
        public int Priority => 0;

        public Task<WindowsToolExecutionResult> ExecuteAsync(
            IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WindowsToolExecutionResult(true, new Dictionary<string, object?>()));
    }

    private sealed class UnavailableConnectionResolver : IConnectionHttpExecutionResolver
    {
        public Task<ConnectionHttpExecutionResolution> ResolveAsync(
            LIGClaw.Contracts.Generated.CapabilityInvocationContext capabilityContext,
            string connectionSlot,
            ConnectionHttpResolutionPurpose purpose,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionHttpExecutionResolution(
                false,
                ErrorCode: "connection.http.binding_unavailable"));
    }

    private sealed class NonWorkflowMailDraftAdapter : IWindowsToolAdapter
    {
        public string Name => "mail.create_draft.v1";
        public string Risk => "R0";
        public WindowsCapability RequiredCapabilities => WindowsCapability.None;
        public int Priority => 0;

        public Task<WindowsToolExecutionResult> ExecuteAsync(
            IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WindowsToolExecutionResult(true, new Dictionary<string, object?>()));
    }

    private sealed class PreparedMailSendAdapter : IWorkflowOneShotToolAdapter
    {
        public string Name => "mail.send.v1";
        public string Risk => "R3";
        public WindowsCapability RequiredCapabilities => WindowsCapability.WebAccess;
        public int Priority => 0;

        public Task<WindowsToolExecutionResult> ExecuteAsync(
            IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WindowsToolExecutionResult(false, new Dictionary<string, object?>()));

        public Task<WorkflowEffectPreparationResult> PrepareWorkflowEffectAsync(
            LIGClaw.Contracts.Generated.ToolInvokeParams invocation,
            DateTimeOffset nowUtc,
            TimeSpan lifetime,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WorkflowEffectPreparationResult(false, ErrorCode: "mail.send.test"));
    }

    private sealed class NonPreparedMailSendAdapter : IWindowsToolAdapter
    {
        public string Name => "mail.send.v1";
        public string Risk => "R3";
        public WindowsCapability RequiredCapabilities => WindowsCapability.WebAccess;
        public int Priority => 0;

        public Task<WindowsToolExecutionResult> ExecuteAsync(
            IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WindowsToolExecutionResult(false, new Dictionary<string, object?>()));
    }
}
