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

    private static ToolInvokeParams Invocation() =>
        new(
            "call", "conversation", "run", "fixture.r2.v1", "R2",
            new Dictionary<string, object?> { ["reason"] = "테스트" });

    private static ToolInvokeParams R1Invocation(string callId, string runId, string target) =>
        new(
            callId, "conversation", runId, "fixture.r1.v1", "R1",
            new Dictionary<string, object?> { ["target"] = target });

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
