using LIGClaw.Application.Tools;

namespace LIGClaw.Application.Tests;

public sealed class ToolInvocationPolicyTests
{
    [Fact]
    public void AllowsOneReadOnlyCallForTheExactActiveRun()
    {
        var policy = new ToolInvocationPolicy();
        policy.BeginRun("conversation", "run");

        Assert.True(policy.Prepare("conversation", "run", "call", "R0").Allowed);
        Assert.False(policy.Prepare("conversation", "run", "call", "R0").Allowed);
    }

    [Theory]
    [InlineData("other-conversation", "run")]
    [InlineData("conversation", "other-run")]
    public void RejectsCallsOutsideTheExactActiveRun(string conversationId, string runId)
    {
        var policy = new ToolInvocationPolicy();
        policy.BeginRun("conversation", "run");

        Assert.False(policy.Prepare(conversationId, runId, "call", "R0").Allowed);
    }

    [Fact]
    public void ReservesHigherRiskCallsUntilTheUserApproves()
    {
        var policy = new ToolInvocationPolicy();
        policy.BeginRun("conversation", "run");

        var prepared = policy.Prepare("conversation", "run", "call", "R1");

        Assert.False(prepared.Allowed);
        Assert.True(prepared.RequiresApproval);
        Assert.True(policy.ResolveApproval("conversation", "run", "call", approved: true).Allowed);
        Assert.False(policy.Prepare("conversation", "run", "call", "R1").Allowed);
    }

    [Fact]
    public void ARejectedApprovalStillConsumesTheToolCallId()
    {
        var policy = new ToolInvocationPolicy();
        policy.BeginRun("conversation", "run");

        Assert.True(policy.Prepare("conversation", "run", "call", "R2").RequiresApproval);
        Assert.False(policy.ResolveApproval("conversation", "run", "call", approved: false).Allowed);
        Assert.False(policy.Prepare("conversation", "run", "call", "R2").Allowed);
    }

    [Fact]
    public void EndingARunPreventsLateCallsAndAllowsTheNextRun()
    {
        var policy = new ToolInvocationPolicy();
        policy.BeginRun("conversation", "run-1");
        policy.EndRun("conversation", "run-1");

        Assert.False(policy.Prepare("conversation", "run-1", "late", "R0").Allowed);
        policy.BeginRun("conversation-2", "run-2");
        Assert.True(policy.Prepare("conversation-2", "run-2", "call", "R0").Allowed);
    }
}
