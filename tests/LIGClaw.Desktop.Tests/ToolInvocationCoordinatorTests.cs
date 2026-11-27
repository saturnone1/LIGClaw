using LIGClaw.Application.Tools;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class ToolInvocationCoordinatorTests
{
    [Fact]
    public async Task R2ToolNeverExecutesWhenTheUserDoesNotApprove()
    {
        var policy = new ToolInvocationPolicy();
        policy.BeginRun("conversation", "run");
        var tool = new FakeR2Tool();
        var audit = new FakeAuditSink();
        var coordinator = new ToolInvocationCoordinator(
            policy,
            new WindowsToolHost(
                WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true),
                [tool]),
            (_, _) => Task.FromResult(false),
            audit);

        var result = await coordinator.ExecuteAsync(Invocation());

        Assert.False(result.Success);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.False(Assert.Single(audit.Approvals).Approved);
        Assert.Equal("denied", Assert.Single(audit.Executions).Status);
    }

    [Fact]
    public async Task ExactR1ActionCanBeAllowedForThirtyDaysAndRevokedByScope()
    {
        var policy = new ToolInvocationPolicy();
        var tool = new FakeR1Tool();
        var grants = new FakeGrantStore();
        var promptCount = 0;
        var coordinator = new ToolInvocationCoordinator(
            policy,
            new WindowsToolHost(
                WindowsPlatformProfile.Classify(10, 0, 22631, isWorkstation: true),
                [tool]),
            (_, allowAlways, _) =>
            {
                promptCount++;
                Assert.True(allowAlways);
                return Task.FromResult(ToolApprovalChoice.AllowAlways);
            },
            grantStore: grants);

        policy.BeginRun("conversation", "run-1");
        Assert.True((await coordinator.ExecuteAsync(R1Invocation("call-1", "run-1", "계산기"))).Success);
        policy.EndRun("conversation", "run-1");
        policy.BeginRun("conversation", "run-2");
        Assert.True((await coordinator.ExecuteAsync(R1Invocation("call-2", "run-2", "계산기"))).Success);
        policy.EndRun("conversation", "run-2");
        policy.BeginRun("conversation", "run-3");
        Assert.True((await coordinator.ExecuteAsync(R1Invocation("call-3", "run-3", "메모장"))).Success);

        Assert.Equal(2, promptCount);
        Assert.Equal(3, tool.ExecutionCount);
        Assert.Equal(2, grants.Items.Count);
        Assert.All(grants.Items, grant => Assert.InRange(
            grant.ExpiresAtUtc - grant.CreatedAtUtc, TimeSpan.FromDays(29.99), TimeSpan.FromDays(30.01)));
    }

    [Fact]
    public async Task ExactR1ActionCanBeAllowedOnlyWithinTheSameConversation()
    {
        var policy = new ToolInvocationPolicy();
        var tool = new FakeR1Tool();
        var promptCount = 0;
        var coordinator = new ToolInvocationCoordinator(
            policy,
            new WindowsToolHost(WindowsPlatformProfile.Classify(10, 0, 22631, true), [tool]),
            (_, allowReusable, _) =>
            {
                promptCount++;
                Assert.True(allowReusable);
                return Task.FromResult(ToolApprovalChoice.AllowConversation);
            },
            grantStore: new FakeGrantStore());

        policy.BeginRun("conversation", "run-1");
        Assert.True((await coordinator.ExecuteAsync(R1Invocation("call-1", "run-1", "계산기"))).Success);
        policy.EndRun("conversation", "run-1");
        policy.BeginRun("conversation", "run-2");
        Assert.True((await coordinator.ExecuteAsync(R1Invocation("call-2", "run-2", "계산기"))).Success);
        policy.EndRun("conversation", "run-2");
        policy.BeginRun("other-conversation", "run-3");
        var other = R1Invocation("call-3", "run-3", "계산기") with { ConversationId = "other-conversation" };
        Assert.True((await coordinator.ExecuteAsync(other)).Success);

        Assert.Equal(2, promptCount);
        Assert.Equal(3, tool.ExecutionCount);
    }

    [Fact]
    public async Task CapabilityPreflightDenialIsAuditedWithoutApprovalOrExecution()
    {
        var policy = new ToolInvocationPolicy();
        policy.BeginRun("conversation", "run");
        var tool = new FakeR1Tool();
        var audit = new FakeAuditSink();
        var approvalCount = 0;
        var coordinator = new ToolInvocationCoordinator(
            policy,
            new WindowsToolHost(WindowsPlatformProfile.Classify(10, 0, 22631, true), [tool]),
            (_, _) =>
            {
                approvalCount++;
                return Task.FromResult(true);
            },
            audit);
        var invocation = R1Invocation("call", "run", "계산기");
        Assert.True(policy.Prepare(
            invocation.ConversationId,
            invocation.RunId,
            invocation.ToolCallId,
            invocation.Risk).RequiresApproval);

        var result = await coordinator.ExecutePreparedAsync(
            invocation,
            ToolInvocationPreflight.Deny(
                "Capability 정책에 따라 실행하지 않았어요.",
                "capability.context.stale"));

        Assert.False(result.Success);
        Assert.Equal(0, approvalCount);
        Assert.Equal(0, tool.ExecutionCount);
        var denial = Assert.Single(audit.Executions);
        Assert.Equal("denied", denial.Status);
        Assert.Equal("capability.context.stale", denial.Summary);
        Assert.False((await coordinator.ExecuteAsync(invocation)).Success);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task ReusableGrantIsBoundToTheExactCapabilityContext()
    {
        var policy = new ToolInvocationPolicy();
        var tool = new FakeR1Tool();
        var grants = new FakeGrantStore();
        var promptCount = 0;
        var coordinator = new ToolInvocationCoordinator(
            policy,
            new WindowsToolHost(WindowsPlatformProfile.Classify(10, 0, 22631, true), [tool]),
            (_, allowAlways, _) =>
            {
                promptCount++;
                Assert.True(allowAlways);
                return Task.FromResult(ToolApprovalChoice.AllowAlways);
            },
            grantStore: grants);
        var v1 = CapabilityContext("1.0.0", 'a', 'b', 1);
        var v2 = CapabilityContext("2.0.0", 'c', 'd', 2);

        await ExecuteRunAsync("run-1", "call-1", v1);
        await ExecuteRunAsync("run-2", "call-2", v1);
        await ExecuteRunAsync("run-3", "call-3", v2);
        await ExecuteRunAsync("run-4", "call-4", null);

        Assert.Equal(3, promptCount);
        Assert.Equal(4, tool.ExecutionCount);
        Assert.Equal(3, grants.Items.Count);

        async Task ExecuteRunAsync(
            string runId,
            string toolCallId,
            CapabilityInvocationContext? context)
        {
            policy.BeginRun("conversation", runId);
            var invocation = R1Invocation(toolCallId, runId, "계산기") with
            {
                CapabilityContext = context,
            };
            Assert.True((await coordinator.ExecuteAsync(invocation)).Success);
            policy.EndRun("conversation", runId);
        }
    }

    [Fact]
    public async Task PreparedWorkflowEffectAcceptsOnlyAllowOnceAndDoesNotExecuteDuringApproval()
    {
        var policy = new ToolInvocationPolicy();
        policy.BeginRun("conversation", "run");
        var notifications = new FakeNotificationService();
        var host = new WindowsToolHost(
            WindowsPlatformProfile.Classify(10, 0, 22631, true),
            [new SystemShowNotificationTool(notifications)]);
        var coordinator = new ToolInvocationCoordinator(
            policy,
            host,
            (_, allowReusable, _) =>
            {
                Assert.False(allowReusable);
                return Task.FromResult(ToolApprovalChoice.AllowOnce);
            });
        var invocation = NotificationInvocation("workflow-call", "run");
        var effect = Assert.IsAssignableFrom<IPreparedWorkflowEffect>(
            (await host.PrepareWorkflowEffectAsync(invocation, DateTimeOffset.UtcNow)).Effect);

        var authorization = await coordinator.AuthorizePreparedWorkflowEffectAsync(invocation, effect);

        Assert.True(authorization.Allowed);
        Assert.Empty(notifications.Shown);
        Assert.Equal(
            WorkflowEffectDisposition.Succeeded,
            (await effect.ExecuteOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None)).Disposition);
        Assert.Single(notifications.Shown);
    }

    [Theory]
    [InlineData("conversation")]
    [InlineData("always")]
    public async Task PreparedWorkflowEffectRejectsReusableApprovalChoices(string scope)
    {
        var choice = scope == "conversation"
            ? ToolApprovalChoice.AllowConversation
            : ToolApprovalChoice.AllowAlways;
        var policy = new ToolInvocationPolicy();
        policy.BeginRun("conversation", "run");
        var host = new WindowsToolHost(
            WindowsPlatformProfile.Classify(10, 0, 22631, true),
            [new SystemShowNotificationTool(new FakeNotificationService())]);
        var coordinator = new ToolInvocationCoordinator(
            policy,
            host,
            (_, _, _) => Task.FromResult(choice));
        var invocation = NotificationInvocation("workflow-call", "run");
        var effect = Assert.IsAssignableFrom<IPreparedWorkflowEffect>(
            (await host.PrepareWorkflowEffectAsync(invocation, DateTimeOffset.UtcNow)).Effect);

        var authorization = await coordinator.AuthorizePreparedWorkflowEffectAsync(invocation, effect);

        Assert.False(authorization.Allowed);
        await effect.DisposeAsync();
    }

    private static ToolInvokeParams Invocation() =>
        new(
            "call", "conversation", "run", "fixture.r2.v1", "R2",
            new Dictionary<string, object?> { ["reason"] = "테스트" });

    private static ToolInvokeParams R1Invocation(string callId, string runId, string target) =>
        new(
            callId, "conversation", runId, "fixture.r1.v1", "R1",
            new Dictionary<string, object?> { ["target"] = target });

    private static ToolInvokeParams NotificationInvocation(string callId, string runId) =>
        new(
            callId,
            "conversation",
            runId,
            "system.show_notification.v1",
            "R1",
            new Dictionary<string, object?>
            {
                ["title"] = "Workflow",
                ["message"] = "완료",
                ["reason"] = "테스트",
            });

    private static CapabilityInvocationContext CapabilityContext(
        string version,
        char contentHash,
        char snapshotHash,
        long revision) =>
        new(
            $"sha256:{new string(snapshotHash, 64)}",
            $"cap_{new string('e', 24)}",
            "company.fixture.action",
            version,
            $"sha256:{new string(contentHash, 64)}",
            revision,
            $"sha256:{new string('f', 64)}");

    private sealed class FakeR1Tool : IWindowsToolAdapter
    {
        public int ExecutionCount { get; private set; }
        public string Name => "fixture.r1.v1";
        public string Risk => "R1";
        public WindowsCapability RequiredCapabilities => WindowsCapability.UserNotification;
        public int Priority => 0;
        public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input) =>
            input.TryGetValue("target", out var target) && target is string text
                ? new("앱을 실행할까요?", $"{text} 실행", $"대상: {text}", "R1", GrantScope: $"app:{text}")
                : null;
        public Task<WindowsToolExecutionResult> ExecuteAsync(
            IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new WindowsToolExecutionResult(true, new Dictionary<string, object?>()));
        }
    }

    private sealed class FakeNotificationService : IUserNotificationService
    {
        public List<(string Title, string Message)> Shown { get; } = [];

        public Task ShowAsync(string title, string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Shown.Add((title, message));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeR2Tool : IWindowsToolAdapter
    {
        public int ExecutionCount { get; private set; }
        public string Name => "fixture.r2.v1";
        public string Risk => "R2";
        public WindowsCapability RequiredCapabilities => WindowsCapability.FileOperations;
        public int Priority => 0;
        public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input) =>
            new("테스트 작업을 할까요?", "테스트 파일을 변경합니다.", "R2 승인 테스트입니다.");
        public Task<WindowsToolExecutionResult> ExecuteAsync(
            IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new WindowsToolExecutionResult(true, new Dictionary<string, object?>()));
        }
    }

    private sealed class FakeAuditSink : IToolAuditSink
    {
        public List<ToolApprovalAuditRecord> Approvals { get; } = [];
        public List<ToolExecutionAuditRecord> Executions { get; } = [];
        public Task RecordApprovalAsync(ToolApprovalAuditRecord record, CancellationToken cancellationToken)
        {
            Approvals.Add(record);
            return Task.CompletedTask;
        }
        public Task RecordExecutionAsync(ToolExecutionAuditRecord record, CancellationToken cancellationToken)
        {
            Executions.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGrantStore : ICapabilityGrantStore
    {
        public List<CapabilityGrant> Items { get; } = [];
        public Task<CapabilityGrant?> FindActiveGrantAsync(
            string toolName, string risk, string scopeHash, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult(Items.FirstOrDefault(item =>
                item.ToolName == toolName && item.Risk == risk && item.ScopeHash == scopeHash && item.ExpiresAtUtc > nowUtc));
        public Task SaveGrantAsync(CapabilityGrant grant, CancellationToken cancellationToken)
        {
            Items.RemoveAll(item => item.ToolName == grant.ToolName && item.Risk == grant.Risk && item.ScopeHash == grant.ScopeHash);
            Items.Add(grant);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<CapabilityGrant>> GetActiveGrantsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CapabilityGrant>>(Items.Where(item => item.ExpiresAtUtc > nowUtc).ToArray());
        public Task RevokeGrantAsync(string grantId, DateTimeOffset revokedAtUtc, CancellationToken cancellationToken = default)
        {
            Items.RemoveAll(item => item.Id == grantId);
            return Task.CompletedTask;
        }
    }
}
