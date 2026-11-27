using System.Text.Json;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal enum CapabilityBuilderOpenIntent
{
    Create,
    Revise,
}

internal sealed record CapabilityBuilderOpenRequest(
    string ConversationId,
    string RunId,
    string ToolCallId,
    CapabilityBuilderOpenIntent Intent,
    string Requirement)
{
    public override string ToString() =>
        $"{nameof(CapabilityBuilderOpenRequest)} {{ Intent = {Intent}, HasRequirement = True }}";
}

internal sealed record CapabilityBuilderOpenControllerOutcome(
    WindowsToolExecutionResult Execution,
    CapabilityBuilderOpenRequest? RequestAfterAcknowledgement = null);

internal sealed class CapabilityBuilderOpenController(
    ConversationRunController conversationRun,
    IConversationRunInputSource persistedRuns)
{
    internal const string CanonicalToolName = "capability.builder.open.v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object acceptedIdentityGate = new();
    private string? acceptedConversationId;
    private string? acceptedRunId;
    private string? acceptedToolCallId;
    private long acceptedRunSequence;

    public async Task<CapabilityBuilderOpenControllerOutcome> ExecuteAsync(ToolInvokeParams invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!StringComparer.Ordinal.Equals(invocation.Name, CanonicalToolName))
            return Failure("capability.builder.invocation_invalid");
        if (!conversationRun.TryGetCancellationToken(
                invocation.ConversationId,
                invocation.RunId,
                out var cancellationToken))
            return Failure("capability.builder.run_not_active");
        if (cancellationToken.IsCancellationRequested)
            return Failure("capability.builder.run_cancelled");

        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("capability.builder.run_cancelled");
        }

        try
        {
            if (!conversationRun.IsActiveRun(invocation.ConversationId, invocation.RunId) ||
                cancellationToken.IsCancellationRequested)
                return Failure("capability.builder.run_cancelled");

            var prepared = conversationRun.PrepareControlPlaneInvocation(
                invocation.ConversationId,
                invocation.RunId,
                invocation.ToolCallId,
                invocation.Risk);
            if (!prepared.Allowed && !prepared.RequiresApproval)
                return Failure("capability.builder.duplicate_or_invalid");
            var claimed = conversationRun.ClaimPreparedInvocation(
                invocation.ConversationId,
                invocation.RunId,
                invocation.ToolCallId,
                invocation.Risk);
            if (!StringComparer.Ordinal.Equals(invocation.Risk, "R0") || !claimed.Allowed)
                return Failure("capability.builder.risk_denied");
            if (invocation.CapabilityContext is not null || invocation.Input is null ||
                !TryReadIntent(invocation.Input, out var intent))
                return Failure("capability.builder.payload_invalid");
            if (intent == CapabilityBuilderOpenIntent.Revise)
                return Failure("capability.builder.revise_target_required");
            if (StringComparer.Ordinal.Equals(acceptedConversationId, invocation.ConversationId) &&
                StringComparer.Ordinal.Equals(acceptedRunId, invocation.RunId))
                return Failure("capability.builder.already_opened");

            PersistedConversationRunInput? persisted;
            try
            {
                persisted = await persistedRuns.GetPersistedRunInputAsync(
                    invocation.ConversationId,
                    invocation.RunId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure("capability.builder.run_cancelled");
            }
            catch
            {
                return Failure("capability.builder.prompt_unavailable");
            }

            if (cancellationToken.IsCancellationRequested ||
                !conversationRun.IsActiveRun(invocation.ConversationId, invocation.RunId))
                return Failure("capability.builder.run_cancelled");
            if (persisted is null ||
                !StringComparer.Ordinal.Equals(persisted.ConversationId, invocation.ConversationId) ||
                !StringComparer.Ordinal.Equals(persisted.RunId, invocation.RunId) ||
                !StringComparer.Ordinal.Equals(persisted.Status, "running") ||
                !FeatureBuilderPolicy.ValidateRequirement(persisted.UserInput).IsValid)
                return Failure("capability.builder.prompt_unavailable");

            lock (acceptedIdentityGate)
            {
                acceptedConversationId = invocation.ConversationId;
                acceptedRunId = invocation.RunId;
                acceptedToolCallId = invocation.ToolCallId;
                acceptedRunSequence = conversationRun.RunSequence;
            }
            var deferred = new CapabilityBuilderOpenRequest(
                invocation.ConversationId,
                invocation.RunId,
                invocation.ToolCallId,
                CapabilityBuilderOpenIntent.Create,
                persisted.UserInput);
            return new CapabilityBuilderOpenControllerOutcome(
                new WindowsToolExecutionResult(
                    true,
                    new Dictionary<string, object?>
                    {
                        ["accepted"] = true,
                        ["intent"] = "create",
                    }),
                deferred);
        }
        finally
        {
            gate.Release();
        }
    }

    internal bool CanOpenAfterAcknowledgement(CapabilityBuilderOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        long runSequence;
        lock (acceptedIdentityGate)
        {
            if (!StringComparer.Ordinal.Equals(acceptedConversationId, request.ConversationId) ||
                !StringComparer.Ordinal.Equals(acceptedRunId, request.RunId) ||
                !StringComparer.Ordinal.Equals(acceptedToolCallId, request.ToolCallId))
                return false;
            runSequence = acceptedRunSequence;
        }

        if (request.Intent != CapabilityBuilderOpenIntent.Create ||
            !FeatureBuilderPolicy.ValidateRequirement(request.Requirement).IsValid ||
            conversationRun.RunSequence != runSequence ||
            conversationRun.WasCancellationRequested(request.ConversationId, request.RunId))
            return false;

        if (!conversationRun.IsRunning)
            return StringComparer.Ordinal.Equals(
                conversationRun.CurrentConversationId,
                request.ConversationId);

        return StringComparer.Ordinal.Equals(
                   conversationRun.ActiveConversationId,
                   request.ConversationId) &&
               StringComparer.Ordinal.Equals(conversationRun.ActiveRunId, request.RunId) &&
               conversationRun.IsActiveRun(request.ConversationId, request.RunId);
    }

    private static bool TryReadIntent(
        IReadOnlyDictionary<string, object?> input,
        out CapabilityBuilderOpenIntent intent)
    {
        intent = default;
        if (input.Count != 1 || !input.TryGetValue("intent", out var value)) return false;
        var text = value switch
        {
            string raw => raw,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
        intent = text switch
        {
            "create" => CapabilityBuilderOpenIntent.Create,
            "revise" => CapabilityBuilderOpenIntent.Revise,
            _ => default,
        };
        return text is "create" or "revise";
    }

    private static CapabilityBuilderOpenControllerOutcome Failure(string errorCode) =>
        new(new WindowsToolExecutionResult(
            false,
            new Dictionary<string, object?>(),
            "Feature Builder 요청을 안전하게 처리하지 못했어요.",
            ErrorCode: errorCode));
}
