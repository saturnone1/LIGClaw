using LIGClaw.Application.Capabilities;
using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class CapabilityRunAuthorizationPolicyTests
{
    [Fact]
    public void SelectsOnlyAnExactCandidateFromTheFrozenRunSnapshot()
    {
        var policy = Begin(Snapshot(out var candidate));
        var context = Context(candidate);

        var selected = policy.Select(new(
            "conversation",
            "run",
            "select-1",
            context));
        var stale = policy.Select(new(
            "conversation",
            "run",
            "select-2",
            context with { CatalogRevision = context.CatalogRevision + 1 }));

        Assert.True(selected.Allowed);
        Assert.Equal(context, selected.Context);
        Assert.False(stale.Allowed);
        Assert.Equal("capability.selection.conflict", stale.ErrorCode);
    }

    [Fact]
    public void RejectsNonCandidateStaleAndDuplicateSelectorCallsBeforeSelection()
    {
        var snapshot = Snapshot(out var candidate);
        var context = Context(candidate);

        var nonCandidatePolicy = Begin(snapshot);
        var nonCandidate = nonCandidatePolicy.Select(new(
            "conversation",
            "run",
            "select-1",
            context with { ModelToolName = "cap_ffffffffffffffffffffffff" }));
        Assert.Equal("capability.candidate.not_found", nonCandidate.ErrorCode);

        var stalePolicy = Begin(snapshot);
        var stale = stalePolicy.Select(new(
            "conversation",
            "run",
            "select-1",
            context with { SnapshotHash = Hash('f') }));
        var duplicate = stalePolicy.Select(new(
            "conversation",
            "run",
            "select-1",
            context));
        Assert.Equal("capability.snapshot.stale", stale.ErrorCode);
        Assert.Equal("capability.selector.duplicate_tool_call", duplicate.ErrorCode);

        var staleCandidatePolicy = Begin(snapshot);
        var staleCandidate = staleCandidatePolicy.Select(new(
            "conversation",
            "run",
            "select-2",
            context with { CatalogRevision = context.CatalogRevision + 1 }));
        Assert.Equal("capability.candidate.stale", staleCandidate.ErrorCode);

        var staleConnectionPolicy = Begin(snapshot);
        var staleConnection = staleConnectionPolicy.Select(new(
            "conversation",
            "run",
            "select-3",
            context with { ConnectionBindingHash = Hash('c') }));
        Assert.Equal("capability.candidate.stale", staleConnection.ErrorCode);
    }

    [Fact]
    public void KeepsLegacyToolsBeforeSelectionButRequiresExactContextAfterSelection()
    {
        var policy = Begin(Snapshot(out var candidate));
        var context = Context(candidate);

        Assert.True(policy.AuthorizeTool(
            "conversation", "run", "system.get_status.v1", "R0", null).Allowed);
        Assert.Equal(
            "capability.selection.required",
            policy.AuthorizeTool(
                "conversation", "run", "system.get_status.v1", "R0", context).ErrorCode);
        Assert.True(policy.Select(new("conversation", "run", "select-1", context)).Allowed);
        Assert.True(policy.AuthorizeTool(
            "conversation", "run", "system.get_status.v1", "R0", context).Allowed);
        Assert.Equal(
            "capability.context.required",
            policy.AuthorizeTool(
                "conversation", "run", "system.get_status.v1", "R0", null).ErrorCode);
        Assert.Equal(
            "capability.tool.not_allowed",
            policy.AuthorizeTool(
                "conversation", "run", "file.write_text.v1", "R1", context).ErrorCode);
        Assert.Equal(
            "capability.risk.exceeded",
            policy.AuthorizeTool(
                "conversation", "run", "system.get_status.v1", "R1", context).ErrorCode);
        Assert.Equal(
            "capability.context.stale",
            policy.AuthorizeTool(
                "conversation",
                "run",
                "system.get_status.v1",
                "R0",
                context with { CatalogRevision = 2 }).ErrorCode);
        Assert.Equal(
            "capability.context.stale",
            policy.AuthorizeTool(
                "conversation",
                "run",
                "system.get_status.v1",
                "R0",
                context with { ConnectionBindingHash = Hash('c') }).ErrorCode);
    }

    [Fact]
    public void RejectsTamperedSnapshotAndClearsAuthorizationAtRunEnd()
    {
        var snapshot = Snapshot(out var candidate);
        var policy = new CapabilityRunAuthorizationPolicy();
        policy.BeginRun("conversation", "run");

        Assert.Throws<InvalidDataException>(() => policy.BindSnapshot(
            "conversation",
            "run",
            snapshot with { SnapshotHash = CapabilityRuntimeSnapshotHash.Parse(Hash('e')) }));

        policy.BindSnapshot("conversation", "run", snapshot);
        policy.EndRun("conversation", "run");
        var rejected = policy.Select(new(
            "conversation",
            "run",
            "select-1",
            Context(candidate)));
        Assert.Equal("capability.run.not_active", rejected.ErrorCode);
    }

    [Fact]
    public void RejectsSnapshotMetadataThatCouldWeakenRunAuthorization()
    {
        var snapshot = Snapshot(out var candidate);
        var invalidRisk = CapabilityRuntimeSnapshot.Create([
            candidate with { MaximumRisk = "RX" },
        ]);
        var invalidSchema = CapabilityRuntimeSnapshot.Create([
            candidate with { CanonicalInputSchemaJson = "{\"type\":\"object\"}" },
        ]);
        var policy = new CapabilityRunAuthorizationPolicy();
        policy.BeginRun("conversation", "run");

        Assert.Throws<InvalidDataException>(() =>
            policy.BindSnapshot("conversation", "run", invalidRisk));
        Assert.Throws<InvalidDataException>(() =>
            policy.BindSnapshot("conversation", "run", invalidSchema));
    }

    [Fact]
    public void WorkflowSelectionAllowsOnlyTheDedicatedWorkflowControlPlane()
    {
        var promptSnapshot = Snapshot(out var promptCandidate);
        var workflowCandidate = promptCandidate with { Kind = CapabilityKind.Workflow };
        var snapshot = CapabilityRuntimeSnapshot.Create([workflowCandidate]);
        var context = Context(workflowCandidate);
        var policy = Begin(snapshot);

        Assert.True(policy.Select(new("conversation", "run", "select-workflow", context)).Allowed);
        Assert.Equal(
            "capability.workflow.direct_tool_denied",
            policy.AuthorizeTool(
                "conversation", "run", "system.get_status.v1", "R0", context).ErrorCode);
        Assert.True(policy.AuthorizeWorkflow("conversation", "run", "workflow-1", context).Allowed);
        Assert.True(policy.AuthorizeWorkflow("conversation", "run", "workflow-1", context).Allowed);
        Assert.Equal(
            "capability.workflow.invocation_conflict",
            policy.AuthorizeWorkflow("conversation", "run", "workflow-2", context).ErrorCode);
        Assert.Equal(
            "capability.context.stale",
            policy.AuthorizeWorkflow(
                "conversation", "run", "workflow-1", context with { CatalogRevision = 2 }).ErrorCode);

        var promptPolicy = Begin(promptSnapshot);
        var promptContext = Context(promptCandidate);
        Assert.True(promptPolicy.Select(new("conversation", "run", "select-prompt", promptContext)).Allowed);
        Assert.Equal(
            "capability.workflow.kind_required",
            promptPolicy.AuthorizeWorkflow("conversation", "run", "workflow-prompt", promptContext).ErrorCode);
    }

    private static CapabilityRunAuthorizationPolicy Begin(CapabilityRuntimeSnapshot snapshot)
    {
        var policy = new CapabilityRunAuthorizationPolicy();
        policy.BeginRun("conversation", "run");
        policy.BindSnapshot("conversation", "run", snapshot);
        return policy;
    }

    private static CapabilityRuntimeSnapshot Snapshot(out CapabilityRuntimeCandidate candidate)
    {
        var id = CapabilityId.Parse("company.status");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        var exact = new CapabilityVersionRef(id, version, ContentHash('a'));
        candidate = new CapabilityRuntimeCandidate(
            exact,
            1,
            CapabilityRuntimeHasher.ComputeModelToolName(exact),
            "상태 확인",
            "현재 상태를 확인합니다.",
            CapabilityKind.Prompt,
            ["상태 알려줘"],
            "등록된 도구로 상태를 확인합니다.",
            "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
            ["system.get_status.v1"],
            Hash('b'),
            "R0");
        return CapabilityRuntimeSnapshot.Create([candidate]);
    }

    private static CapabilityRunInvocationContext Context(CapabilityRuntimeCandidate candidate)
    {
        var snapshot = CapabilityRuntimeSnapshot.Create([candidate]);
        return new(
            snapshot.SnapshotHash.Value,
            candidate.ModelToolName,
            candidate.ExactVersion,
            candidate.CatalogRevision,
            candidate.ConnectionBindingHash);
    }

    private static CapabilityContentHash ContentHash(char value) =>
        CapabilityContentHash.Parse(Hash(value));

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";
}
