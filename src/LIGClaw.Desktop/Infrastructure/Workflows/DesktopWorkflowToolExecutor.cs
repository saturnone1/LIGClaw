using System.Globalization;
using System.Security.Cryptography;
using LIGClaw.Application.Workflows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Workflows;

internal sealed class DesktopWorkflowToolExecutor : IWorkflowToolExecutor
{
    private const string DefaultEffectRedactedSummary = "작업 흐름의 실행 단계를 완료했습니다.";
    private const string SmtpOutcomeUnknownActivityPrefix =
        "메일 서버의 접수 여부를 확인해야 해요. 메시지 식별자: ";
    private const string SmtpMessageIdPrefix = "<ligclaw.";
    private const string SmtpMessageIdSuffix = "@ligclaw.invalid>";
    private const int SmtpMessageIdHexCharacters = 64;
    private const int SmtpMessageIdCharacters = 90;
    private static readonly IReadOnlySet<string> PreservedConnectionErrorCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "connection.http.context_required",
            "connection.http.input_invalid",
            "connection.http.binding_unavailable",
            "connection.http.binding_mismatch",
            "connection.http.binding_invalid",
            "connection.http.credential_unavailable",
            "connection.http.expired",
            "connection.http.already_consumed",
            "connection.http.cancelled",
            "connection.http.redirect_denied",
            "connection.http.remote_failed",
            "connection.http.response_too_large",
            "connection.http.content_unsupported",
            "connection.http.credential_reflected",
            "connection.http.timeout",
            "connection.http.transport_failed",
            "mail.send.context_required",
            "mail.send.input_invalid",
            "mail.send.connection_slot_invalid",
            "mail.send.draft_hash_invalid",
            "mail.send.draft_invalid",
            "mail.send.draft_hash_mismatch",
            "mail.send.binding_invalid",
            "mail.send.expired",
            "mail.send.already_consumed",
            "mail.send.cancelled",
            "mail.send.outbox_unavailable",
            "mail.send.outbox_conflict",
            "mail.send.reconciliation_required",
            "mail.send.submit_not_started",
            "mail.send.submit_failed",
            "mail.send.outcome_unknown",
            "mail.gateway.binding_unavailable",
            "mail.gateway.binding_mismatch",
            "mail.gateway.credential_unavailable",
        };
    private readonly WorkflowRunIdentity expectedRun;
    private readonly ValidatedWorkflowPlan expectedPlan;
    private readonly CapabilityInvocationContext capabilityContext;
    private readonly IWorkflowToolMetadataCatalog catalog;
    private readonly WindowsToolHost toolHost;
    private readonly ToolInvocationCoordinator coordinator;
    private readonly IWorkflowExecutionLedger ledger;
    private readonly TimeProvider timeProvider;

    public DesktopWorkflowToolExecutor(
        WorkflowRunIdentity expectedRun,
        ValidatedWorkflowPlan expectedPlan,
        CapabilityInvocationContext capabilityContext,
        IWorkflowToolMetadataCatalog catalog,
        WindowsToolHost toolHost,
        ToolInvocationCoordinator coordinator,
        IWorkflowExecutionLedger ledger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(expectedRun);
        ArgumentNullException.ThrowIfNull(expectedPlan);
        ArgumentNullException.ThrowIfNull(capabilityContext);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(toolHost);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(ledger);
        if (!StringComparer.Ordinal.Equals(capabilityContext.CapabilityId, expectedRun.ExactCapabilityVersion.Id.Value) ||
            !StringComparer.Ordinal.Equals(capabilityContext.Version, expectedRun.ExactCapabilityVersion.Version.Value) ||
            !StringComparer.Ordinal.Equals(
                capabilityContext.ContentHash,
                expectedRun.ExactCapabilityVersion.ContentHash.Value) ||
            capabilityContext.CatalogRevision != expectedRun.CatalogRevision)
            throw new ArgumentException("Capability context does not match the workflow run identity.", nameof(capabilityContext));
        this.expectedRun = expectedRun;
        this.expectedPlan = expectedPlan;
        this.capabilityContext = capabilityContext;
        this.catalog = catalog;
        this.toolHost = toolHost;
        this.coordinator = coordinator;
        this.ledger = ledger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkflowToolExecutionResponse> ExecuteAsync(
        WorkflowToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested) return Cancelled();
        if (!TryValidateRequest(request, out var metadata, out var planStep, out var validationError))
            return NotStarted(validationError);
        if (!WorkflowRuntimeContractValidator.TryComputeCanonicalHash(
                request.Payload,
                WorkflowInterpreter.MaximumInputBytes,
                out var canonicalPayloadHash) ||
            !WorkflowToolValueConverter.TryToToolInput(request.Payload, out var toolInput))
            return NotStarted("workflow.tool.payload_invalid");

        WorkflowRunRecord? persistedRun;
        try
        {
            persistedRun = await ledger.GetRunAsync(request.Run.WorkflowRunId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (Exception)
        {
            return NotStarted("workflow.ledger.unavailable");
        }
        if (persistedRun is null || !HasExactLedgerIdentity(persistedRun, request))
            return NotStarted("workflow.ledger.run_mismatch");

        var toolCallId = ComputeToolCallId(request);
        var invocation = new ToolInvokeParams(
            toolCallId,
            request.Run.ConversationId,
            request.Run.WorkflowRunId,
            request.ToolName,
            request.Risk,
            toolInput,
            capabilityContext);
        return metadata!.EffectClass == WorkflowToolEffectClass.Observation
            ? await ExecuteObservationAsync(
                request,
                planStep!,
                metadata,
                invocation,
                canonicalPayloadHash,
                cancellationToken).ConfigureAwait(false)
            : await ExecuteEffectAsync(
                request,
                planStep!,
                metadata,
                invocation,
                cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkflowToolExecutionResponse> ExecuteObservationAsync(
        WorkflowToolExecutionRequest request,
        ValidatedWorkflowPlanStep planStep,
        WorkflowToolMetadata metadata,
        ToolInvokeParams invocation,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        var descriptor = toolHost.Descriptors
            .Where(candidate => StringComparer.Ordinal.Equals(candidate.Name, request.ToolName))
            .OrderByDescending(candidate => candidate.Priority)
            .FirstOrDefault();
        if (descriptor is null) return NotStarted("workflow.tool.unavailable");
        var bindingHash = WorkflowPreparedEffectHash.Compute(
            "workflow-observation-binding-v1",
            descriptor.Name,
            descriptor.Risk,
            Convert.ToInt64(descriptor.RequiredCapabilities, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture),
            descriptor.Priority.ToString(CultureInfo.InvariantCulture),
            descriptor.Timeout.Ticks.ToString(CultureInfo.InvariantCulture));
        var stepPlan = CreateStepPlan(
            request,
            planStep,
            invocation.ToolCallId,
            payloadHash,
            bindingHash,
            WorkflowLedgerEffectClass.Observation);
        if (!await TryAddPlanAsync(stepPlan, cancellationToken).ConfigureAwait(false))
            return cancellationToken.IsCancellationRequested
                ? Cancelled()
                : NotStarted("workflow.ledger.plan_failed");

        var beforeReservation = await TryReloadExactRunAsync(request, cancellationToken).ConfigureAwait(false);
        if (beforeReservation is null)
        {
            await TryAbandonAsync(request, invocation.ToolCallId, "workflow.ledger.run_mismatch").ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested
                ? Cancelled()
                : NotStarted("workflow.ledger.run_mismatch");
        }
        bool started;
        try
        {
            started = await ledger.TryStartObservationStepAsync(
                new WorkflowObservationReservation(
                    request.Run.WorkflowRunId,
                    invocation.ToolCallId,
                    request.StepOrdinal,
                    request.ToolName,
                    request.Risk,
                    RetryClassValue(metadata.RetryClass),
                    payloadHash,
                    bindingHash,
                    beforeReservation.Revision,
                    beforeReservation.CurrentSequence,
                    timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryAbandonAsync(request, invocation.ToolCallId, "workflow.cancelled").ConfigureAwait(false);
            return Cancelled();
        }
        catch (Exception)
        {
            await TryAbandonAsync(request, invocation.ToolCallId, "workflow.ledger.reservation_failed").ConfigureAwait(false);
            return NotStarted("workflow.ledger.reservation_failed");
        }
        if (!started)
        {
            await TryAbandonAsync(request, invocation.ToolCallId, "workflow.ledger.reservation_failed").ConfigureAwait(false);
            return NotStarted("workflow.ledger.reservation_failed");
        }

        var afterReservation = await TryReloadExactRunAsync(request, CancellationToken.None).ConfigureAwait(false);
        if (afterReservation is null) return Unknown("workflow.ledger.reservation_unconfirmed");

        WindowsToolExecutionResult execution;
        try
        {
            execution = await coordinator.ExecuteWorkflowObservationAsync(invocation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CompleteObservationAsync(
                request,
                invocation.ToolCallId,
                WorkflowStepCompletionDisposition.NotStarted,
                null,
                "workflow.cancelled",
                afterReservation.Revision,
                Cancelled()).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await CompleteObservationFailureAsync(
                request,
                invocation.ToolCallId,
                afterReservation.Revision,
                "workflow.tool.executor_failed").ConfigureAwait(false);
        }

        if (!execution.Success)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return await CompleteObservationAsync(
                    request,
                    invocation.ToolCallId,
                    WorkflowStepCompletionDisposition.NotStarted,
                    null,
                    "workflow.cancelled",
                    afterReservation.Revision,
                    Cancelled()).ConfigureAwait(false);
            }
            return await CompleteObservationFailureAsync(
                request,
                invocation.ToolCallId,
                afterReservation.Revision,
                "workflow.tool.failed_known").ConfigureAwait(false);
        }
        if (!TryValidateOutput(request, metadata, execution.Output, out var output, out var outputHash))
        {
            return await CompleteObservationAsync(
                request,
                invocation.ToolCallId,
                WorkflowStepCompletionDisposition.FailedKnown,
                null,
                "workflow.tool.result_contract_mismatch",
                afterReservation.Revision,
                FailedKnown("workflow.tool.result_contract_mismatch")).ConfigureAwait(false);
        }
        return await CompleteObservationAsync(
            request,
            invocation.ToolCallId,
            WorkflowStepCompletionDisposition.Succeeded,
            outputHash,
            null,
            afterReservation.Revision,
            new WorkflowToolExecutionResponse(WorkflowToolExecutionDisposition.Succeeded, output)).ConfigureAwait(false);
    }

    private async Task<WorkflowToolExecutionResponse> ExecuteEffectAsync(
        WorkflowToolExecutionRequest request,
        ValidatedWorkflowPlanStep planStep,
        WorkflowToolMetadata metadata,
        ToolInvokeParams invocation,
        CancellationToken cancellationToken)
    {
        if (request.Attempt != 1 || request.MaximumRetries != 0)
            return NotStarted("workflow.effect.retry_forbidden");
        var prepared = await toolHost.PrepareWorkflowEffectAsync(
            invocation,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (!prepared.Success || prepared.Effect is null)
            return cancellationToken.IsCancellationRequested
                ? Cancelled()
                : NotStarted(PreserveConnectionErrorCode(
                    prepared.ErrorCode,
                    "workflow.effect.prepare_failed"));
        await using var effect = prepared.Effect;
        if (!StringComparer.Ordinal.Equals(effect.ToolName, request.ToolName) ||
            !StringComparer.Ordinal.Equals(effect.Risk, request.Risk) ||
            !IsHash(effect.CanonicalPayloadHash) || !IsHash(effect.CanonicalBindingHash) ||
            effect.ExpiresAtUtc <= timeProvider.GetUtcNow())
            return NotStarted("workflow.effect.prepared_mismatch");

        var stepPlan = CreateStepPlan(
            request,
            planStep,
            invocation.ToolCallId,
            effect.CanonicalPayloadHash,
            effect.CanonicalBindingHash,
            WorkflowLedgerEffectClass.Effect);
        if (!await TryAddPlanAsync(stepPlan, cancellationToken).ConfigureAwait(false))
            return cancellationToken.IsCancellationRequested
                ? Cancelled()
                : NotStarted("workflow.ledger.plan_failed");
        var beforeApproval = await TryReloadExactRunAsync(request, cancellationToken).ConfigureAwait(false);
        if (beforeApproval is null)
        {
            await TryAbandonAsync(request, invocation.ToolCallId, "workflow.ledger.run_mismatch").ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested
                ? Cancelled()
                : NotStarted("workflow.ledger.run_mismatch");
        }

        WorkflowPreparedAuthorization authorization;
        try
        {
            authorization = await coordinator.AuthorizePreparedWorkflowEffectAsync(
                invocation,
                effect,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryAbandonAsync(request, invocation.ToolCallId, "workflow.cancelled").ConfigureAwait(false);
            return Cancelled();
        }
        catch (Exception)
        {
            await TryAbandonAsync(request, invocation.ToolCallId, "workflow.effect.authorization_failed").ConfigureAwait(false);
            return NotStarted("workflow.effect.authorization_failed");
        }
        if (!authorization.Allowed || cancellationToken.IsCancellationRequested)
        {
            var code = cancellationToken.IsCancellationRequested
                ? "workflow.cancelled"
                : "workflow.effect.authorization_denied";
            await TryAbandonAsync(request, invocation.ToolCallId, code).ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested ? Cancelled() : NotStarted(code);
        }
        var approvedAt = timeProvider.GetUtcNow();
        if (effect.ExpiresAtUtc <= approvedAt)
        {
            await TryAbandonAsync(request, invocation.ToolCallId, "workflow.effect.prepared_expired").ConfigureAwait(false);
            return NotStarted("workflow.effect.prepared_expired");
        }

        var ticketId = $"wft-{Guid.NewGuid():N}";
        var itemId = $"wfi-{Guid.NewGuid():N}";
        var nonceHash = HashRandomNonce();
        var ticket = new WorkflowAuthorizationTicketDraft(
            ticketId,
            request.Run.WorkflowRunId,
            expectedPlan.MaximumRisk,
            approvedAt,
            effect.ExpiresAtUtc,
            [
                new WorkflowAuthorizationTicketItemDraft(
                    itemId,
                    request.StepOrdinal,
                    nonceHash,
                    request.ToolName,
                    request.Risk,
                    effect.CanonicalPayloadHash,
                    effect.CanonicalBindingHash),
            ],
            beforeApproval.Revision,
            beforeApproval.CurrentSequence);
        bool ticketSaved;
        try
        {
            ticketSaved = await ledger.SaveAuthorizationTicketAsync(ticket, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryAbandonAsync(request, invocation.ToolCallId, "workflow.cancelled").ConfigureAwait(false);
            return Cancelled();
        }
        catch (Exception)
        {
            await TryAbandonAsync(request, invocation.ToolCallId, "workflow.ledger.ticket_failed").ConfigureAwait(false);
            return NotStarted("workflow.ledger.ticket_failed");
        }
        if (!ticketSaved)
        {
            await TryAbandonAsync(request, invocation.ToolCallId, "workflow.ledger.ticket_failed").ConfigureAwait(false);
            return NotStarted("workflow.ledger.ticket_failed");
        }

        var beforeReservation = await TryReloadExactRunAsync(request, CancellationToken.None).ConfigureAwait(false);
        if (beforeReservation is null)
        {
            await TryAbandonAsync(request, invocation.ToolCallId, "workflow.ledger.run_mismatch").ConfigureAwait(false);
            return NotStarted("workflow.ledger.run_mismatch");
        }
        if (cancellationToken.IsCancellationRequested)
        {
            await TryAbandonAsync(request, invocation.ToolCallId, "workflow.cancelled").ConfigureAwait(false);
            return Cancelled();
        }

        bool reserved;
        try
        {
            reserved = await ledger.TryReserveEffectStepAsync(
                new WorkflowEffectReservation(
                    request.Run.WorkflowRunId,
                    invocation.ToolCallId,
                    ticketId,
                    itemId,
                    request.StepOrdinal,
                    nonceHash,
                    request.ToolName,
                    request.Risk,
                    effect.CanonicalPayloadHash,
                    effect.CanonicalBindingHash,
                    beforeReservation.Revision,
                    beforeReservation.CurrentSequence,
                    timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await ReconcileEffectNotStartedAsync(request, invocation.ToolCallId).ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested
                ? Cancelled()
                : NotStarted("workflow.ledger.reservation_failed");
        }
        if (!reserved)
        {
            await ReconcileEffectNotStartedAsync(request, invocation.ToolCallId).ConfigureAwait(false);
            return NotStarted("workflow.ledger.reservation_failed");
        }

        var afterReservation = await TryReloadExactRunAsync(request, CancellationToken.None).ConfigureAwait(false);
        if (afterReservation is null) return Unknown("workflow.ledger.reservation_unconfirmed");
        if (cancellationToken.IsCancellationRequested)
        {
            return await CompleteEffectAsync(
                request,
                invocation.ToolCallId,
                WorkflowStepCompletionDisposition.NotStarted,
                null,
                "workflow.cancelled",
                afterReservation.Revision,
                Cancelled()).ConfigureAwait(false);
        }

        WorkflowPreparedEffectExecution execution;
        try
        {
            execution = await effect.ExecuteOnceAsync(
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            execution = new WorkflowPreparedEffectExecution(
                WorkflowEffectDisposition.Unknown,
                new WindowsToolExecutionResult(false, new Dictionary<string, object?>()));
        }
        return await CompletePreparedEffectResultAsync(
            request,
            metadata,
            invocation.ToolCallId,
            execution,
            afterReservation.Revision).ConfigureAwait(false);
    }

    private async Task<WorkflowToolExecutionResponse> CompletePreparedEffectResultAsync(
        WorkflowToolExecutionRequest request,
        WorkflowToolMetadata metadata,
        string toolCallId,
        WorkflowPreparedEffectExecution execution,
        long expectedRunRevision)
    {
        WorkflowStepCompletionDisposition ledgerDisposition;
        WorkflowToolExecutionResponse response;
        string? outputHash = null;
        string? errorCode;
        switch (execution.Disposition)
        {
            case WorkflowEffectDisposition.Succeeded when execution.Result.Success &&
                                                          TryValidateOutput(
                                                              request,
                                                              metadata,
                                                              execution.Result.Output,
                                                              out var output,
                                                              out outputHash):
                ledgerDisposition = WorkflowStepCompletionDisposition.Succeeded;
                response = new WorkflowToolExecutionResponse(WorkflowToolExecutionDisposition.Succeeded, output);
                errorCode = null;
                break;
            case WorkflowEffectDisposition.NotStarted:
                ledgerDisposition = WorkflowStepCompletionDisposition.NotStarted;
                errorCode = PreserveConnectionErrorCode(
                    execution.Result.ErrorCode,
                    "workflow.effect.not_started");
                response = NotStarted(errorCode);
                break;
            case WorkflowEffectDisposition.FailedKnown:
                ledgerDisposition = WorkflowStepCompletionDisposition.FailedKnown;
                errorCode = PreserveConnectionErrorCode(
                    execution.Result.ErrorCode,
                    "workflow.effect.failed_known");
                response = FailedKnown(errorCode);
                break;
            default:
                ledgerDisposition = WorkflowStepCompletionDisposition.Unknown;
                errorCode = execution.Disposition == WorkflowEffectDisposition.Succeeded
                    ? "workflow.tool.result_contract_mismatch"
                    : PreserveConnectionErrorCode(
                        execution.Result.ErrorCode,
                        "workflow.effect.outcome_unknown");
                response = Unknown(errorCode);
                outputHash = null;
                break;
        }
        var redactedSummary = SelectEffectRedactedSummary(request.ToolName, execution);
        return await CompleteEffectAsync(
            request,
            toolCallId,
            ledgerDisposition,
            outputHash,
            errorCode,
            expectedRunRevision,
            response,
            redactedSummary).ConfigureAwait(false);
    }

    internal static string SelectEffectRedactedSummary(
        string toolName,
        WorkflowPreparedEffectExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (!StringComparer.Ordinal.Equals(toolName, "mail.send.v1") ||
            execution.Disposition != WorkflowEffectDisposition.Unknown ||
            !StringComparer.Ordinal.Equals(execution.Result.ErrorCode, "mail.send.outcome_unknown") ||
            execution.Result.ActivitySummary is not { } candidate ||
            candidate.Length != SmtpOutcomeUnknownActivityPrefix.Length + SmtpMessageIdCharacters ||
            !candidate.StartsWith(SmtpOutcomeUnknownActivityPrefix, StringComparison.Ordinal) ||
            !IsCanonicalSmtpMessageId(candidate.AsSpan(SmtpOutcomeUnknownActivityPrefix.Length)))
            return DefaultEffectRedactedSummary;
        return candidate;
    }

    private static bool IsCanonicalSmtpMessageId(ReadOnlySpan<char> candidate)
    {
        if (candidate.Length != SmtpMessageIdCharacters ||
            !candidate.StartsWith(SmtpMessageIdPrefix, StringComparison.Ordinal) ||
            !candidate.EndsWith(SmtpMessageIdSuffix, StringComparison.Ordinal))
            return false;
        var hex = candidate.Slice(SmtpMessageIdPrefix.Length, SmtpMessageIdHexCharacters);
        foreach (var character in hex)
        {
            if (character is not (>= '0' and <= '9') && character is not (>= 'a' and <= 'f')) return false;
        }
        return true;
    }

    private static string PreserveConnectionErrorCode(string? candidate, string fallback)
        => candidate is not null && PreservedConnectionErrorCodes.Contains(candidate)
            ? candidate
            : fallback;

    private async Task<WorkflowToolExecutionResponse> CompleteObservationFailureAsync(
        WorkflowToolExecutionRequest request,
        string toolCallId,
        long expectedRunRevision,
        string errorCode)
    {
        var retryable = request.Attempt <= request.MaximumRetries;
        return await CompleteObservationAsync(
            request,
            toolCallId,
            retryable
                ? WorkflowStepCompletionDisposition.RetryableFailed
                : WorkflowStepCompletionDisposition.FailedKnown,
            null,
            errorCode,
            expectedRunRevision,
            retryable
                ? new WorkflowToolExecutionResponse(
                    WorkflowToolExecutionDisposition.RetryableFailed,
                    ErrorCode: errorCode)
                : FailedKnown(errorCode)).ConfigureAwait(false);
    }

    private Task<WorkflowToolExecutionResponse> CompleteObservationAsync(
        WorkflowToolExecutionRequest request,
        string toolCallId,
        WorkflowStepCompletionDisposition disposition,
        string? outputHash,
        string? errorCode,
        long expectedRunRevision,
        WorkflowToolExecutionResponse response) =>
        CompleteStepAsync(
            request,
            toolCallId,
            disposition,
            outputHash,
            errorCode,
            expectedRunRevision,
            "작업 흐름의 확인 단계를 완료했습니다.",
            response);

    private Task<WorkflowToolExecutionResponse> CompleteEffectAsync(
        WorkflowToolExecutionRequest request,
        string toolCallId,
        WorkflowStepCompletionDisposition disposition,
        string? outputHash,
        string? errorCode,
        long expectedRunRevision,
        WorkflowToolExecutionResponse response,
        string redactedSummary = DefaultEffectRedactedSummary) =>
        CompleteStepAsync(
            request,
            toolCallId,
            disposition,
            outputHash,
            errorCode,
            expectedRunRevision,
            redactedSummary,
            response);

    private async Task<WorkflowToolExecutionResponse> CompleteStepAsync(
        WorkflowToolExecutionRequest request,
        string toolCallId,
        WorkflowStepCompletionDisposition disposition,
        string? outputHash,
        string? errorCode,
        long expectedRunRevision,
        string redactedSummary,
        WorkflowToolExecutionResponse response)
    {
        try
        {
            var completed = await ledger.CompleteStepAsync(
                new WorkflowStepCompletion(
                    request.Run.WorkflowRunId,
                    toolCallId,
                    disposition,
                    outputHash,
                    redactedSummary,
                    errorCode,
                    expectedRunRevision,
                    timeProvider.GetUtcNow()),
                CancellationToken.None).ConfigureAwait(false);
            return completed ? response : Unknown("workflow.ledger.completion_failed");
        }
        catch (Exception)
        {
            return Unknown("workflow.ledger.completion_failed");
        }
    }

    private async Task<bool> TryAddPlanAsync(WorkflowStepPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            return await ledger.AddPlannedStepAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<WorkflowRunRecord?> TryReloadExactRunAsync(
        WorkflowToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var run = await ledger.GetRunAsync(request.Run.WorkflowRunId, cancellationToken).ConfigureAwait(false);
            return run is not null && HasExactLedgerIdentity(run, request) ? run : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task TryAbandonAsync(
        WorkflowToolExecutionRequest request,
        string toolCallId,
        string errorCode)
    {
        try
        {
            var run = await ledger.GetRunAsync(request.Run.WorkflowRunId, CancellationToken.None).ConfigureAwait(false);
            if (run is null || !HasExactLedgerIdentity(run, request)) return;
            _ = await ledger.AbandonPlannedStepAsync(
                new WorkflowPlannedStepAbandonment(
                    request.Run.WorkflowRunId,
                    toolCallId,
                    errorCode,
                    run.Revision,
                    run.CurrentSequence,
                    timeProvider.GetUtcNow(),
                    "작업 흐름 단계가 시작되지 않았습니다."),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The effect is still discarded; startup recovery remains conservative if the ledger is unavailable.
        }
    }

    private async Task ReconcileEffectNotStartedAsync(
        WorkflowToolExecutionRequest request,
        string toolCallId)
    {
        try
        {
            var step = await ledger.GetStepAsync(toolCallId, CancellationToken.None).ConfigureAwait(false);
            var run = await ledger.GetRunAsync(request.Run.WorkflowRunId, CancellationToken.None).ConfigureAwait(false);
            if (step is null || run is null || !HasExactLedgerIdentity(run, request)) return;
            if (StringComparer.Ordinal.Equals(step.Status, "planned"))
            {
                await TryAbandonAsync(request, toolCallId, "workflow.ledger.reservation_failed").ConfigureAwait(false);
                return;
            }
            if (!StringComparer.Ordinal.Equals(step.Status, "executing")) return;
            _ = await ledger.CompleteStepAsync(
                new WorkflowStepCompletion(
                    request.Run.WorkflowRunId,
                    toolCallId,
                    WorkflowStepCompletionDisposition.NotStarted,
                    null,
                    "작업 흐름의 실행 단계가 시작되지 않았습니다.",
                    "workflow.ledger.reservation_failed",
                    run.Revision,
                    timeProvider.GetUtcNow()),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // No prepared effect is executed on this path.
        }
    }

    private bool TryValidateRequest(
        WorkflowToolExecutionRequest request,
        out WorkflowToolMetadata? metadata,
        out ValidatedWorkflowPlanStep? planStep,
        out string errorCode)
    {
        metadata = null;
        planStep = null;
        errorCode = "workflow.request.invalid";
        if (request.Run is null || request.DefinitionHash is null || request.PlanHash is null ||
            request.Payload is null || string.IsNullOrEmpty(request.InputHash) ||
            string.IsNullOrEmpty(request.AuthorizationHash) || string.IsNullOrEmpty(request.StepId) ||
            string.IsNullOrEmpty(request.ToolName) || string.IsNullOrEmpty(request.Risk) ||
            string.IsNullOrEmpty(request.InputContractHash) || string.IsNullOrEmpty(request.ResultContractHash) ||
            !HasExactRunIdentity(request.Run) ||
            !StringComparer.Ordinal.Equals(request.DefinitionHash.Value, expectedPlan.DefinitionHash.Value) ||
            !StringComparer.Ordinal.Equals(request.PlanHash.Value, expectedPlan.PlanHash.Value) ||
            request.StepOrdinal < 0 || request.StepOrdinal >= expectedPlan.Steps.Count ||
            !WorkflowRuntimeContractValidator.ValidateAuthorizationHash(
                expectedRun,
                expectedPlan,
                request.InputHash,
                request.AuthorizationHash))
            return false;
        planStep = expectedPlan.Steps[request.StepOrdinal];
        if (!StringComparer.Ordinal.Equals(planStep.Id, request.StepId) ||
            request.Attempt is < 1 or > 4 ||
            request.MaximumRetries != planStep.MaximumRetries ||
            request.Attempt > request.MaximumRetries + 1 ||
            !StringComparer.Ordinal.Equals(planStep.CanonicalToolName, request.ToolName) ||
            !StringComparer.Ordinal.Equals(planStep.CanonicalRisk, request.Risk) ||
            planStep.PreparationMode != request.PreparationMode ||
            !StringComparer.Ordinal.Equals(planStep.InputContractHash, request.InputContractHash) ||
            !StringComparer.Ordinal.Equals(planStep.ResultContractHash, request.ResultContractHash))
            return false;
        if (!catalog.TryGet(request.ToolName, out metadata) || metadata is null ||
            !metadata.IsWorkflowAllowed || !metadata.IsAvailable ||
            !StringComparer.Ordinal.Equals(metadata.CanonicalName, request.ToolName) ||
            !StringComparer.Ordinal.Equals(metadata.Risk, request.Risk) ||
            metadata.PreparationMode != request.PreparationMode ||
            metadata.RetryClass != planStep.RetryClass || metadata.EffectClass != planStep.EffectClass ||
            !ToolSchemaRegistry.All.TryGetValue(request.ToolName, out var schemas) ||
            !StringComparer.Ordinal.Equals(schemas.CanonicalName, request.ToolName) ||
            !StringComparer.Ordinal.Equals(schemas.CanonicalInputSchemaJson, metadata.CanonicalInputSchemaJson) ||
            !StringComparer.Ordinal.Equals(schemas.CanonicalResultSchemaJson, metadata.CanonicalResultSchemaJson))
        {
            errorCode = "workflow.catalog.drift";
            return false;
        }
        if (!WorkflowRuntimeContractValidator.ValidateObject(
                schemas.CanonicalInputSchemaJson,
                request.InputContractHash,
                request.Payload))
        {
            errorCode = "workflow.tool.input_contract_mismatch";
            return false;
        }
        return true;
    }

    private bool TryValidateOutput(
        WorkflowToolExecutionRequest request,
        WorkflowToolMetadata metadata,
        IReadOnlyDictionary<string, object?> rawOutput,
        out WorkflowObjectValue? output,
        out string? outputHash)
    {
        output = null;
        outputHash = null;
        if (!WorkflowToolValueConverter.TryFromToolOutput(rawOutput, out var converted) || converted is null ||
            !WorkflowRuntimeContractValidator.ValidateObject(
                metadata.CanonicalResultSchemaJson,
                request.ResultContractHash,
                converted) ||
            !WorkflowRuntimeContractValidator.TryComputeCanonicalHash(
                converted,
                WorkflowInterpreter.MaximumStepOutputBytes,
                out var hash))
            return false;
        output = converted;
        outputHash = hash;
        return true;
    }

    private WorkflowStepPlan CreateStepPlan(
        WorkflowToolExecutionRequest request,
        ValidatedWorkflowPlanStep planStep,
        string toolCallId,
        string payloadHash,
        string bindingHash,
        WorkflowLedgerEffectClass effectClass) =>
        new(
            request.Run.WorkflowRunId,
            request.StepOrdinal,
            request.Attempt,
            request.StepId,
            toolCallId,
            request.ToolName,
            request.Risk,
            RetryClassValue(planStep.RetryClass!.Value),
            effectClass,
            payloadHash,
            bindingHash,
            timeProvider.GetUtcNow(),
            request.MaximumRetries);

    private bool HasExactRunIdentity(WorkflowRunIdentity actual) =>
        actual.ExactCapabilityVersion is not null &&
        StringComparer.Ordinal.Equals(actual.WorkflowRunId, expectedRun.WorkflowRunId) &&
        StringComparer.Ordinal.Equals(actual.ConversationId, expectedRun.ConversationId) &&
        StringComparer.Ordinal.Equals(actual.ParentRunId, expectedRun.ParentRunId) &&
        StringComparer.Ordinal.Equals(actual.ParentToolCallId, expectedRun.ParentToolCallId) &&
        actual.CatalogRevision == expectedRun.CatalogRevision &&
        StringComparer.Ordinal.Equals(actual.ExactCapabilityVersion.Id.Value, expectedRun.ExactCapabilityVersion.Id.Value) &&
        StringComparer.Ordinal.Equals(actual.ExactCapabilityVersion.Version.Value, expectedRun.ExactCapabilityVersion.Version.Value) &&
        StringComparer.Ordinal.Equals(
            actual.ExactCapabilityVersion.ContentHash.Value,
            expectedRun.ExactCapabilityVersion.ContentHash.Value);

    private bool HasExactLedgerIdentity(
        WorkflowRunRecord persisted,
        WorkflowToolExecutionRequest request) =>
        persisted.Status is "planned" or "awaiting_approval" or "running" &&
        StringComparer.Ordinal.Equals(persisted.RunId, expectedRun.WorkflowRunId) &&
        StringComparer.Ordinal.Equals(persisted.ConversationId, expectedRun.ConversationId) &&
        StringComparer.Ordinal.Equals(persisted.ParentRunId, expectedRun.ParentRunId) &&
        StringComparer.Ordinal.Equals(persisted.ParentToolCallId, expectedRun.ParentToolCallId) &&
        StringComparer.Ordinal.Equals(persisted.SnapshotHash, capabilityContext.SnapshotHash) &&
        StringComparer.Ordinal.Equals(persisted.ModelToolName, capabilityContext.ModelToolName) &&
        StringComparer.Ordinal.Equals(persisted.CapabilityId, expectedRun.ExactCapabilityVersion.Id.Value) &&
        StringComparer.Ordinal.Equals(persisted.CapabilityVersion, expectedRun.ExactCapabilityVersion.Version.Value) &&
        persisted.CatalogRevision == expectedRun.CatalogRevision &&
        StringComparer.Ordinal.Equals(
            persisted.CapabilityContentHash,
            expectedRun.ExactCapabilityVersion.ContentHash.Value) &&
        StringComparer.Ordinal.Equals(persisted.WorkflowHash, expectedPlan.DefinitionHash.Value) &&
        StringComparer.Ordinal.Equals(persisted.InputHash, request.InputHash) &&
        StringComparer.Ordinal.Equals(persisted.PlanHash, expectedPlan.PlanHash.Value) &&
        StringComparer.Ordinal.Equals(persisted.ActualMaximumRisk, expectedPlan.MaximumRisk);

    private static string ComputeToolCallId(WorkflowToolExecutionRequest request)
    {
        var hash = WorkflowPreparedEffectHash.Compute(
            "workflow-step-tool-call-v1",
            request.Run.WorkflowRunId,
            request.StepOrdinal.ToString(CultureInfo.InvariantCulture),
            request.Attempt.ToString(CultureInfo.InvariantCulture));
        return $"wfs-{hash[7..]}";
    }

    private static string HashRandomNonce() =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(RandomNumberGenerator.GetBytes(32))).ToLowerInvariant()}";

    private static bool IsHash(string value) =>
        value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).ContainsAnyExcept("0123456789abcdef") == false;

    private static string RetryClassValue(WorkflowRetryClass retryClass) => retryClass switch
    {
        WorkflowRetryClass.Never => "never",
        WorkflowRetryClass.ReadOnly => "read_only",
        WorkflowRetryClass.Idempotent => "idempotent",
        _ => throw new ArgumentOutOfRangeException(nameof(retryClass)),
    };

    private static WorkflowToolExecutionResponse NotStarted(string errorCode) =>
        new(WorkflowToolExecutionDisposition.NotStarted, ErrorCode: errorCode);

    private static WorkflowToolExecutionResponse FailedKnown(string errorCode) =>
        new(WorkflowToolExecutionDisposition.FailedKnown, ErrorCode: errorCode);

    private static WorkflowToolExecutionResponse Cancelled() =>
        new(WorkflowToolExecutionDisposition.Cancelled, ErrorCode: "workflow.cancelled");

    private static WorkflowToolExecutionResponse Unknown(string errorCode) =>
        new(WorkflowToolExecutionDisposition.OutcomeUnknown, ErrorCode: errorCode);
}
