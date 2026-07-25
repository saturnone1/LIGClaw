using LIGClaw.Application.Tools;

namespace LIGClaw.Application.Tests;

public sealed class ToolInvocationPolicyTests
{
    [Fact]
    public void AllowsOneReadOnlyCallForTheExactActiveRun()
    {
        var policy = new ToolInvocationPolicy();
        policy.BeginRun("conversation", "run");

        Assert.True(policy.Authorize("conversation", "run", "call", "R0").Allowed);
        Assert.False(policy.Authorize("conversation", "run", "call", "R0").Allowed);
    }

    [Theory]
    [InlineData("other-conversation", "run")]
    [InlineData("conversation", "other-run")]
    public void RejectsCallsOutsideTheExactActiveRun(string conversationId, string runId)
    {
        var policy = new ToolInvocationPolicy();
        policy.BeginRun("conversation", "run");

        Assert.False(policy.Authorize(conversationId, runId, "call", "R0").Allowed);
    }

    [Fact]
    public void RequiresApprovalForHigherRiskButAllowsAnExplicitApproval()
    {
        var policy = new ToolInvocationPolicy();
        policy.BeginRun("conversation", "run");

        Assert.False(policy.Authorize("conversation", "run", "call", "R1").Allowed);
        Assert.True(policy.Authorize("conversation", "run", "call", "R1", userApproved: true).Allowed);
    }

    [Fact]
    public void EndingARunPreventsLateCallsAndAllowsTheNextRun()
    {
        var policy = new ToolInvocationPolicy();
        policy.BeginRun("conversation", "run-1");
        policy.EndRun("conversation", "run-1");

        Assert.False(policy.Authorize("conversation", "run-1", "late", "R0").Allowed);
        policy.BeginRun("conversation-2", "run-2");
        Assert.True(policy.Authorize("conversation-2", "run-2", "call", "R0").Allowed);
    }
}
