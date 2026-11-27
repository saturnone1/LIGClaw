using LIGClaw.Application.Capabilities;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Workflows;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed class ConversationWorkflowInvocationController(
    ConversationRunController conversationRun,
    DesktopWorkflowOrchestrator workflows)
{
    public async Task<WorkflowResultParams> ExecuteAsync(WorkflowInvokeParams invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!IsExactId(invocation.WorkflowCallId, "wfinv_", 48) ||
            !IsExactId(invocation.IdempotencyKey, "wfid_", 64) ||
            !StringComparer.Ordinal.Equals(invocation.Name, "workflow.invoke.v1"))
            return Failure(invocation.WorkflowCallId, "workflow.invocation.invalid");
        if (!conversationRun.TryGetCancellationToken(
                invocation.ConversationId,
                invocation.RunId,
                out var cancellationToken))
            return Failure(invocation.WorkflowCallId, "capability.run.not_active");

        CapabilityRunInvocationContext context;
        try
        {
            context = ToApplicationContext(invocation.CapabilityContext);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            return Failure(invocation.WorkflowCallId, "capability.context.parse_denied");
        }

        var authorization = conversationRun.AuthorizeCapabilityWorkflow(
            invocation.ConversationId,
            invocation.RunId,
            invocation.WorkflowCallId,
            context);
        if (!authorization.Allowed)
            return Failure(
                invocation.WorkflowCallId,
                authorization.ErrorCode ?? "capability.workflow.denied");
        return await workflows.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private static CapabilityRunInvocationContext ToApplicationContext(
        CapabilityInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ModelToolName.Length != 28 ||
            !context.ModelToolName.StartsWith("cap_", StringComparison.Ordinal) ||
            context.ModelToolName.AsSpan(4).ContainsAnyExcept("0123456789abcdef") ||
            context.CatalogRevision is < 1 or > 9_007_199_254_740_991 ||
            context.ConnectionBindingHash.Length != 71 ||
            !context.ConnectionBindingHash.StartsWith("sha256:", StringComparison.Ordinal) ||
            context.ConnectionBindingHash.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
            throw new FormatException();
        return new CapabilityRunInvocationContext(
            CapabilityRuntimeSnapshotHash.Parse(context.SnapshotHash).Value,
            context.ModelToolName,
            new CapabilityVersionRef(
                CapabilityId.Parse(context.CapabilityId),
                CapabilitySemanticVersion.Parse(context.Version),
                CapabilityContentHash.Parse(context.ContentHash)),
            context.CatalogRevision,
            context.ConnectionBindingHash);
    }

    private static WorkflowResultParams Failure(string workflowCallId, string errorCode) =>
        new(workflowCallId, false, Error: errorCode);

    private static bool IsExactId(string value, string prefix, int hexLength) =>
        value is not null && value.Length == prefix.Length + hexLength &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef") == false;
}
