using System.Collections.Concurrent;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Tools;
using LIGClaw.Application.Workflows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Workflows;

internal sealed class DesktopWorkflowOrchestrator
{
    private static readonly TimeSpan MaximumDeadline = TimeSpan.FromMinutes(9);
    private readonly ICapabilityCatalogRepository capabilityCatalog;
    private readonly WorkflowLedgerRepository ledger;
    private readonly IWorkflowToolMetadataCatalog workflowTools;
    private readonly WindowsToolHost toolHost;
    private readonly Func<ToolInvocationPolicy, ToolInvocationCoordinator> createCoordinator;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<WorkflowInFlightKey, Lazy<Task<WorkflowResultParams>>> inFlight = new();

    public DesktopWorkflowOrchestrator(
        ICapabilityCatalogRepository capabilityCatalog,
        WorkflowLedgerRepository ledger,
        IWorkflowToolMetadataCatalog workflowTools,
        WindowsToolHost toolHost,
        Func<ToolInvocationPolicy, ToolInvocationCoordinator> createCoordinator,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(capabilityCatalog);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(workflowTools);
        ArgumentNullException.ThrowIfNull(toolHost);
        ArgumentNullException.ThrowIfNull(createCoordinator);
        this.capabilityCatalog = capabilityCatalog;
        this.ledger = ledger;
        this.workflowTools = workflowTools;
        this.toolHost = toolHost;
        this.createCoordinator = createCoordinator;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkflowResultParams> InvokeAsync(
        WorkflowInvokeParams invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!HasValidEnvelope(invocation, timeProvider.GetUtcNow()))
            return AdmissionFailure(invocation.WorkflowCallId, "workflow.invocation.invalid");

        PreparedInvocation? prepared;
        try
        {
            prepared = await PrepareAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AdmissionFailure(invocation.WorkflowCallId, "workflow.parent.cancelled");
        }
        catch (Exception)
        {
            return AdmissionFailure(invocation.WorkflowCallId, "workflow.catalog.unavailable");
        }
        if (prepared is null)
            return AdmissionFailure(invocation.WorkflowCallId, "workflow.capability.stale");
        if (prepared.ErrorCode is not null)
            return AdmissionFailure(invocation.WorkflowCallId, prepared.ErrorCode);

        var workflowRunId = CreateWorkflowRunId(invocation.RunId, invocation.IdempotencyKey);
        var inFlightKey = WorkflowInFlightKey.Create(workflowRunId, invocation, prepared);
        var owned = new Lazy<Task<WorkflowResultParams>>(
            () => InvokePreparedAsync(invocation, workflowRunId, prepared, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var active = inFlight.GetOrAdd(inFlightKey, owned);
        try
        {
            return await active.Value.ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(active, owned)) _ = inFlight.TryRemove(inFlightKey, out _);
        }
    }

    private async Task<WorkflowResultParams> InvokePreparedAsync(
        WorkflowInvokeParams invocation,
        string workflowRunId,
        PreparedInvocation prepared,
        CancellationToken cancellationToken)
    {
        WorkflowRunStartResult start;
        try
        {
            start = await ledger.StartOrCoalesceRunAsync(
                new WorkflowRunStart(
                    workflowRunId,
                    invocation.ConversationId,
                    invocation.RunId,
                    invocation.WorkflowCallId,
                    invocation.IdempotencyKey,
                    invocation.CapabilityContext.SnapshotHash,
                    invocation.CapabilityContext.ModelToolName,
                    prepared.ExactVersion!.Id.Value,
                    prepared.ExactVersion.Version.Value,
                    invocation.CapabilityContext.CatalogRevision,
                    prepared.ExactVersion.ContentHash.Value,
                    prepared.Plan!.DefinitionHash.Value,
                    prepared.InputHash!,
                    prepared.Plan.PlanHash.Value,
                    prepared.Plan.MaximumRisk,
                    timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AdmissionFailure(invocation.WorkflowCallId, "workflow.parent.cancelled");
        }
        catch (Exception)
        {
            return AdmissionFailure(invocation.WorkflowCallId, "workflow.ledger.unavailable");
        }

        if (start.Outcome == WorkflowRunStartOutcome.Conflict)
            return AdmissionFailure(
                invocation.WorkflowCallId,
                start.ErrorCode ?? "workflow.idempotency_conflict");
        if (start.Outcome == WorkflowRunStartOutcome.Coalesced)
            return FromCoalesced(invocation.WorkflowCallId, start.Run);

        var remaining = invocation.DeadlineUtc - timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            return await CompleteStartedWithoutExecutionAsync(
                invocation.WorkflowCallId,
                start.Run,
                WorkflowRunTerminalStatus.Cancelled,
                WorkflowRunNextAction.RetryFromStart,
                "workflow.deadline.exceeded").ConfigureAwait(false);

        using var workflowCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        workflowCancellation.CancelAfter(remaining);
        return await ExecuteCreatedAsync(
            invocation,
            workflowRunId,
            prepared,
            workflowCancellation.Token).ConfigureAwait(false);
    }

    private async Task<PreparedInvocation?> PrepareAsync(
        WorkflowInvokeParams invocation,
        CancellationToken cancellationToken)
    {
        if (!TryParseExactVersion(invocation.CapabilityContext, out var exactVersion) ||
            exactVersion is null ||
            !StringComparer.Ordinal.Equals(
                invocation.CapabilityContext.ModelToolName,
                CapabilityRuntimeHasher.ComputeModelToolName(exactVersion)))
            return PreparedInvocation.Failure("workflow.capability.context_invalid");

        var version = await capabilityCatalog.GetVersionAsync(exactVersion, cancellationToken)
            .ConfigureAwait(false);
        if (version is null) return null;
        if (!version.ExactVersion.Equals(exactVersion) ||
            !version.Payload.Manifest.Id.Equals(exactVersion.Id) ||
            !version.Payload.Manifest.Version.Equals(exactVersion.Version) ||
            !CapabilityContentHasher.Compute(version.Payload).Equals(exactVersion.ContentHash) ||
            CapabilityIngressPolicy.GetViolation(version.Payload) != CapabilityIngressViolation.None)
            return PreparedInvocation.Failure("workflow.capability.integrity_invalid");
        var definitionValidation = CapabilityDefinitionPolicy.Validate(version.Payload);
        if (!definitionValidation.IsValid || version.Payload.Manifest.Kind != CapabilityKind.Workflow ||
            version.Payload.CanonicalWorkflowJson is null)
            return PreparedInvocation.Failure(
                definitionValidation.ErrorCode ?? "workflow.capability.kind_invalid");

        var parsed = new WorkflowDefinitionParser().Parse(version.Payload.CanonicalWorkflowJson);
        if (!parsed.Succeeded || parsed.Definition is null)
            return PreparedInvocation.Failure(
                parsed.Issues.FirstOrDefault()?.Code ?? "workflow.definition.invalid");
        var validation = new WorkflowStaticValidator(workflowTools).Validate(
            parsed.Definition,
            version.Payload.Manifest,
            version.Payload.CanonicalInputSchemaJson);
        if (!validation.Succeeded || validation.Plan is null)
            return PreparedInvocation.Failure(
                validation.Issues.FirstOrDefault()?.Code ?? "workflow.definition.invalid");
        if (!WorkflowToolValueConverter.TryFromToolOutput(invocation.Input, out var input) || input is null ||
            !WorkflowRuntimeContractValidator.TryComputeCanonicalHash(
                input,
                WorkflowInterpreter.MaximumInputBytes,
                out var inputHash))
            return PreparedInvocation.Failure("workflow.input.invalid");
        if (!WorkflowRuntimeContractValidator.ValidateObject(
                version.Payload.CanonicalInputSchemaJson,
                validation.Plan.InputContractHash,
                input))
            return PreparedInvocation.Failure("workflow.input.schema_mismatch");
        return new PreparedInvocation(
            exactVersion,
            version.Payload,
            validation.Plan,
            input,
            inputHash,
            null);
    }

    private async Task<WorkflowResultParams> ExecuteCreatedAsync(
        WorkflowInvokeParams invocation,
        string workflowRunId,
        PreparedInvocation prepared,
        CancellationToken cancellationToken)
    {
        var run = new WorkflowRunIdentity(
            workflowRunId,
            invocation.ConversationId,
            invocation.RunId,
            invocation.WorkflowCallId,
            prepared.ExactVersion!,
            invocation.CapabilityContext.CatalogRevision);
        var policy = new ToolInvocationPolicy();
        var beganPolicy = false;
        WorkflowRunResult result;
        try
        {
            policy.BeginRun(invocation.ConversationId, workflowRunId);
            beganPolicy = true;
            var coordinator = createCoordinator(policy) ??
                throw new InvalidOperationException("Workflow Tool coordinator was not created.");
            var executor = new DesktopWorkflowToolExecutor(
                run,
                prepared.Plan!,
                invocation.CapabilityContext,
                workflowTools,
                toolHost,
                coordinator,
                new WorkflowExecutionLedger(ledger),
                timeProvider);
            result = await new WorkflowInterpreter().ExecuteAsync(
                prepared.Plan!,
                prepared.Payload!.CanonicalInputSchemaJson,
                prepared.Input!,
                run,
                executor,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new WorkflowRunResult(
                WorkflowRunStatus.AttentionRequired,
                WorkflowNextAction.ReviewOutcome,
                prepared.InputHash!,
                string.Empty,
                null,
                null,
                [],
                "workflow.cancellation.unconfirmed");
        }
        catch (Exception)
        {
            result = new WorkflowRunResult(
                WorkflowRunStatus.AttentionRequired,
                WorkflowNextAction.ReviewOutcome,
                prepared.InputHash!,
                string.Empty,
                null,
                null,
                [],
                "workflow.runtime.unavailable");
        }
        finally
        {
            if (beganPolicy) policy.EndRun(invocation.ConversationId, workflowRunId);
        }

        return await PersistTerminalResultAsync(
            invocation.WorkflowCallId,
            run,
            prepared.Plan!,
            result).ConfigureAwait(false);
    }

    private async Task<WorkflowResultParams> PersistTerminalResultAsync(
        string workflowCallId,
        WorkflowRunIdentity run,
        ValidatedWorkflowPlan plan,
        WorkflowRunResult result)
    {
        var normalized = NormalizeResult(run, plan, result);
        WorkflowRunRecord? current;
        try
        {
            current = await ledger.GetRunAsync(run.WorkflowRunId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return StartedAttention(workflowCallId, run.WorkflowRunId, "workflow.ledger.completion_failed");
        }
        if (current is null)
            return StartedAttention(workflowCallId, run.WorkflowRunId, "workflow.ledger.completion_failed");

        bool completed;
        try
        {
            completed = await ledger.CompleteRunAsync(
                new WorkflowRunCompletion(
                    run.WorkflowRunId,
                    normalized.LedgerStatus,
                    normalized.LedgerNextAction,
                    normalized.ErrorCode,
                    normalized.Summary,
                    normalized.ResultHash,
                    current.Revision,
                    current.CurrentSequence,
                    timeProvider.GetUtcNow(),
                    normalized.ContractResult.RequiredInputs),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            completed = false;
        }
        if (!completed)
        {
            try
            {
                var persisted = await ledger.GetRunAsync(run.WorkflowRunId, CancellationToken.None)
                    .ConfigureAwait(false);
                if (persisted is null || !PersistedTerminalMatches(persisted, normalized))
                    return StartedAttention(
                        workflowCallId,
                        run.WorkflowRunId,
                        "workflow.ledger.completion_failed");
            }
            catch (Exception)
            {
                return StartedAttention(
                    workflowCallId,
                    run.WorkflowRunId,
                    "workflow.ledger.completion_failed");
            }
        }
        return new WorkflowResultParams(workflowCallId, true, normalized.ContractResult);
    }

    private static bool PersistedTerminalMatches(WorkflowRunRecord persisted, NormalizedResult normalized)
    {
        var expectedNextAction = normalized.ContractResult.NextAction == "none"
            ? null
            : normalized.ContractResult.NextAction;
        return persisted.CompletedAtUtc is not null &&
               StringComparer.Ordinal.Equals(persisted.Status, normalized.ContractResult.Status) &&
               StringComparer.Ordinal.Equals(persisted.FixedNextAction, expectedNextAction) &&
               StringComparer.Ordinal.Equals(persisted.FixedErrorCode, normalized.ErrorCode) &&
               StringComparer.Ordinal.Equals(persisted.RedactedSummary, normalized.Summary) &&
               StringComparer.Ordinal.Equals(persisted.ResultHash, normalized.ResultHash) &&
               RequiredInputsEqual(persisted.RequiredInputs, normalized.ContractResult.RequiredInputs);
    }

    private static bool RequiredInputsEqual(
        IReadOnlyList<string>? left,
        IReadOnlyList<string>? right) =>
        left is null && right is null ||
        left is not null && right is not null && left.SequenceEqual(right, StringComparer.Ordinal);

    private async Task<WorkflowResultParams> CompleteStartedWithoutExecutionAsync(
        string workflowCallId,
        WorkflowRunRecord run,
        WorkflowRunTerminalStatus status,
        WorkflowRunNextAction nextAction,
        string errorCode)
    {
        bool completed;
        try
        {
            completed = await ledger.CompleteRunAsync(
                new WorkflowRunCompletion(
                    run.RunId,
                    status,
                    nextAction,
                    errorCode,
                    "작업 흐름이 제한 시간 안에 시작되지 않았습니다.",
                    null,
                    run.Revision,
                    run.CurrentSequence,
                    timeProvider.GetUtcNow()),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return StartedAttention(workflowCallId, run.RunId, "workflow.ledger.completion_failed");
        }
        if (!completed)
            return StartedAttention(workflowCallId, run.RunId, "workflow.ledger.completion_failed");
        return new WorkflowResultParams(
            workflowCallId,
            true,
            new WorkflowInvokeV1Result(
                run.RunId,
                "cancelled",
                "retry_from_start",
                "작업 흐름이 제한 시간 안에 시작되지 않았습니다.",
                ErrorCode: errorCode));
    }

    private static NormalizedResult NormalizeResult(
        WorkflowRunIdentity run,
        ValidatedWorkflowPlan plan,
        WorkflowRunResult result)
    {
        if (!StringComparer.Ordinal.Equals(result.InputHash, string.Empty) &&
            (!WorkflowRuntimeContractValidator.ValidateAuthorizationHash(
                 run,
                 plan,
                 result.InputHash,
                 result.AuthorizationHash) ||
             result.Status == WorkflowRunStatus.Completed && result.Output is null))
            return Attention(run.WorkflowRunId, "workflow.result.integrity_invalid");

        IReadOnlyDictionary<string, object?>? output = null;
        string? resultHash = null;
        if (result.Status == WorkflowRunStatus.Completed &&
            (!WorkflowToolValueConverter.TryToToolInput(result.Output!, out output) ||
             !WorkflowRuntimeContractValidator.TryComputeCanonicalHash(
                 result.Output!,
                 WorkflowInterpreter.MaximumFinalOutputBytes,
                 out resultHash)))
            return Attention(run.WorkflowRunId, "workflow.result.serialization_failed");

        var status = result.Status switch
        {
            WorkflowRunStatus.Completed => "succeeded",
            WorkflowRunStatus.NeedsInformation => "needs_information",
            WorkflowRunStatus.Failed => "failed",
            WorkflowRunStatus.Cancelled => "cancelled",
            WorkflowRunStatus.AttentionRequired => "attention_required",
            _ => throw new ArgumentOutOfRangeException(nameof(result.Status)),
        };
        var nextAction = result.NextAction switch
        {
            WorkflowNextAction.None => "none",
            WorkflowNextAction.ProvideInformation => "provide_information",
            WorkflowNextAction.RetryFromStart => "retry_from_start",
            WorkflowNextAction.ReviewOutcome => "review_outcome",
            _ => throw new ArgumentOutOfRangeException(nameof(result.NextAction)),
        };
        var summary = result.Status switch
        {
            WorkflowRunStatus.Completed => "작업 흐름을 완료했습니다.",
            WorkflowRunStatus.NeedsInformation => "작업 흐름에 추가 정보가 필요합니다.",
            WorkflowRunStatus.Failed => "작업 흐름을 안전하게 완료하지 못했습니다.",
            WorkflowRunStatus.Cancelled => "작업 흐름을 완료하기 전에 취소했습니다.",
            WorkflowRunStatus.AttentionRequired => "작업 흐름 결과를 확인해 주세요.",
            _ => "작업 흐름을 마쳤습니다.",
        };
        var ledgerStatus = result.Status switch
        {
            WorkflowRunStatus.Completed => WorkflowRunTerminalStatus.Completed,
            WorkflowRunStatus.NeedsInformation => WorkflowRunTerminalStatus.NeedsInformation,
            WorkflowRunStatus.Failed => WorkflowRunTerminalStatus.Failed,
            WorkflowRunStatus.Cancelled => WorkflowRunTerminalStatus.Cancelled,
            WorkflowRunStatus.AttentionRequired => WorkflowRunTerminalStatus.AttentionRequired,
            _ => throw new ArgumentOutOfRangeException(nameof(result.Status)),
        };
        var ledgerNextAction = result.NextAction switch
        {
            WorkflowNextAction.None => WorkflowRunNextAction.None,
            WorkflowNextAction.ProvideInformation => WorkflowRunNextAction.ProvideInformation,
            WorkflowNextAction.RetryFromStart => WorkflowRunNextAction.RetryFromStart,
            WorkflowNextAction.ReviewOutcome => WorkflowRunNextAction.ReviewOutcome,
            _ => throw new ArgumentOutOfRangeException(nameof(result.NextAction)),
        };
        return new NormalizedResult(
            new WorkflowInvokeV1Result(
                run.WorkflowRunId,
                status,
                nextAction,
                summary,
                result.RequiredInputs.Count == 0 ? null : result.RequiredInputs,
                output,
                result.ErrorCode),
            ledgerStatus,
            ledgerNextAction,
            result.ErrorCode,
            summary,
            resultHash);
    }

    private static NormalizedResult Attention(string workflowRunId, string errorCode)
    {
        const string summary = "작업 흐름 결과를 확인해 주세요.";
        return new NormalizedResult(
            new WorkflowInvokeV1Result(
                workflowRunId,
                "attention_required",
                "review_outcome",
                summary,
                ErrorCode: errorCode),
            WorkflowRunTerminalStatus.AttentionRequired,
            WorkflowRunNextAction.ReviewOutcome,
            errorCode,
            summary,
            null);
    }

    private static WorkflowResultParams FromCoalesced(string workflowCallId, WorkflowRunRecord run) =>
        run.Status switch
        {
            "succeeded" => new WorkflowResultParams(
                workflowCallId,
                true,
                new WorkflowInvokeV1Result(
                    run.RunId,
                    "succeeded",
                    "none",
                    run.RedactedSummary)),
            "failed" => new WorkflowResultParams(
                workflowCallId,
                true,
                new WorkflowInvokeV1Result(
                    run.RunId,
                    "failed",
                    "retry_from_start",
                    run.RedactedSummary,
                    ErrorCode: run.FixedErrorCode)),
            "cancelled" => new WorkflowResultParams(
                workflowCallId,
                true,
                new WorkflowInvokeV1Result(
                    run.RunId,
                    "cancelled",
                    "retry_from_start",
                    run.RedactedSummary,
                    ErrorCode: run.FixedErrorCode)),
            "attention_required" => new WorkflowResultParams(
                workflowCallId,
                true,
                new WorkflowInvokeV1Result(
                    run.RunId,
                    "attention_required",
                    "review_outcome",
                    run.RedactedSummary,
                    ErrorCode: run.FixedErrorCode)),
            "needs_information" when run.RequiredInputs is { Count: > 0 } => new WorkflowResultParams(
                workflowCallId,
                true,
                new WorkflowInvokeV1Result(
                    run.RunId,
                    "needs_information",
                    "provide_information",
                    run.RedactedSummary,
                    run.RequiredInputs,
                    ErrorCode: run.FixedErrorCode)),
            "needs_information" => StartedAttention(
                workflowCallId,
                run.RunId,
                "workflow.coalesced_result_incomplete"),
            _ => AdmissionFailure(workflowCallId, "workflow.run.in_progress"),
        };

    private static bool HasValidEnvelope(WorkflowInvokeParams invocation, DateTimeOffset now) =>
        invocation.WorkflowCallId is not null && invocation.IdempotencyKey is not null &&
        invocation.Name is not null && invocation.ConversationId is not null && invocation.RunId is not null &&
        IsExactId(invocation.WorkflowCallId, "wfinv_", 48) &&
        IsExactId(invocation.IdempotencyKey, "wfid_", 64) &&
        invocation.Name == "workflow.invoke.v1" &&
        invocation.ConversationId.Length is >= 1 and <= 128 &&
        invocation.RunId.Length is >= 1 and <= 128 &&
        !invocation.ConversationId.Any(char.IsControl) &&
        !invocation.RunId.Any(char.IsControl) &&
        invocation.CapabilityContext is not null &&
        invocation.Input is not null &&
        invocation.DeadlineUtc.Offset == TimeSpan.Zero &&
        invocation.DeadlineUtc > now &&
        invocation.DeadlineUtc - now <= MaximumDeadline;

    private static bool TryParseExactVersion(
        CapabilityInvocationContext context,
        out CapabilityVersionRef? exactVersion)
    {
        exactVersion = null;
        try
        {
            if (!CapabilityRuntimeSnapshotHash.TryParse(context.SnapshotHash, out _) ||
                string.IsNullOrEmpty(context.ModelToolName) ||
                context.ModelToolName.Length != 28 ||
                !context.ModelToolName.StartsWith("cap_", StringComparison.Ordinal) ||
                context.ModelToolName.AsSpan(4).ContainsAnyExcept("0123456789abcdef") ||
                context.ConnectionBindingHash.Length != 71 ||
                !context.ConnectionBindingHash.StartsWith("sha256:", StringComparison.Ordinal) ||
                context.ConnectionBindingHash.AsSpan(7).ContainsAnyExcept("0123456789abcdef") ||
                context.CatalogRevision is < 1 or > 9_007_199_254_740_991)
                return false;
            exactVersion = new CapabilityVersionRef(
                CapabilityId.Parse(context.CapabilityId),
                CapabilitySemanticVersion.Parse(context.Version),
                CapabilityContentHash.Parse(context.ContentHash));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static bool IsExactId(string value, string prefix, int hexLength) =>
        value.Length == prefix.Length + hexLength &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef") == false;

    private static string CreateWorkflowRunId(string parentRunId, string idempotencyKey)
    {
        var hash = WorkflowPreparedEffectHash.Compute("workflow-run-v1", parentRunId, idempotencyKey);
        return $"wfr_{hash.AsSpan(7, 48)}";
    }

    private static WorkflowResultParams AdmissionFailure(string workflowCallId, string errorCode) =>
        new(workflowCallId, false, Error: errorCode);

    private static WorkflowResultParams StartedAttention(
        string workflowCallId,
        string workflowRunId,
        string errorCode) =>
        new(
            workflowCallId,
            true,
            new WorkflowInvokeV1Result(
                workflowRunId,
                "attention_required",
                "review_outcome",
                "작업 흐름 결과를 확인해 주세요.",
                ErrorCode: errorCode));

    private sealed record PreparedInvocation(
        CapabilityVersionRef? ExactVersion,
        CapabilityVersionPayload? Payload,
        ValidatedWorkflowPlan? Plan,
        WorkflowObjectValue? Input,
        string? InputHash,
        string? ErrorCode)
    {
        public static PreparedInvocation Failure(string errorCode) =>
            new(null, null, null, null, null, errorCode);
    }

    private sealed record NormalizedResult(
        WorkflowInvokeV1Result ContractResult,
        WorkflowRunTerminalStatus LedgerStatus,
        WorkflowRunNextAction LedgerNextAction,
        string? ErrorCode,
        string Summary,
        string? ResultHash);

    private sealed record WorkflowInFlightKey(
        string WorkflowRunId,
        string ConversationId,
        string ParentRunId,
        string ParentToolCallId,
        string IdempotencyKey,
        string SnapshotHash,
        string ModelToolName,
        CapabilityVersionRef ExactVersion,
        long CatalogRevision,
        string WorkflowHash,
        string InputHash,
        string PlanHash,
        string ActualMaximumRisk)
    {
        public static WorkflowInFlightKey Create(
            string workflowRunId,
            WorkflowInvokeParams invocation,
            PreparedInvocation prepared) =>
            new(
                workflowRunId,
                invocation.ConversationId,
                invocation.RunId,
                invocation.WorkflowCallId,
                invocation.IdempotencyKey,
                invocation.CapabilityContext.SnapshotHash,
                invocation.CapabilityContext.ModelToolName,
                prepared.ExactVersion!,
                invocation.CapabilityContext.CatalogRevision,
                prepared.Plan!.DefinitionHash.Value,
                prepared.InputHash!,
                prepared.Plan.PlanHash.Value,
                prepared.Plan.MaximumRisk);
    }
}
