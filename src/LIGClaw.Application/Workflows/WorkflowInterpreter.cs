using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LIGClaw.Domain;

namespace LIGClaw.Application.Workflows;

public sealed class WorkflowInterpreter
{
    public const int MaximumInputBytes = 32 * 1_024;
    public const int MaximumStepOutputBytes = 64 * 1_024;
    public const int MaximumFinalOutputBytes = 100 * 1_024;
    private const string EmptyHash = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    public async Task<WorkflowRunResult> ExecuteAsync(
        ValidatedWorkflowPlan plan,
        string canonicalInputSchemaJson,
        WorkflowObjectValue input,
        WorkflowRunIdentity run,
        IWorkflowToolExecutor tools,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(canonicalInputSchemaJson);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(tools);

        var trace = new List<WorkflowStepTrace>();
        if (!HasValidRunIdentity(run))
            return Failed(EmptyHash, EmptyHash, trace, "workflow.run.identity_invalid");
        if (!HasCoherentPlan(plan))
            return Failed(EmptyHash, EmptyHash, trace, "workflow.plan.integrity_invalid");
        if (!WorkflowRuntimeValue.TryCanonicalize(input, MaximumInputBytes, out var canonicalInput))
            return Failed(EmptyHash, EmptyHash, trace, "workflow.input.too_large");
        var inputHash = WorkflowRuntimeValue.Hash(canonicalInput);
        var inputContractHash = ComputeContractHash(canonicalInputSchemaJson);
        if (!StringComparer.Ordinal.Equals(inputContractHash, plan.InputContractHash) ||
            !WorkflowContractSchema.TryParse(canonicalInputSchemaJson, out var inputSchema) ||
            inputSchema is null || inputSchema.Type != "object")
            return Failed(inputHash, EmptyHash, trace, "workflow.input.contract_drift");
        if (!inputSchema.AcceptsValue(input, _ => null))
            return Failed(inputHash, EmptyHash, trace, "workflow.input.schema_mismatch");

        var authorizationHash = WorkflowRuntimeValue.ComputeAuthorizationHash(run, plan, inputHash);
        var stepById = plan.Steps.ToDictionary(step => step.Id, step => step.Ordinal, StringComparer.Ordinal);
        if (!stepById.TryGetValue(plan.Definition.EntryStepId, out var currentOrdinal))
            return Failed(inputHash, authorizationHash, trace, "workflow.plan.entry_missing");

        var stepOutputs = new Dictionary<int, WorkflowValue>();
        var variables = new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);
        var visited = new HashSet<int>();
        var effectMayHaveOccurred = false;
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                return CancelledOrAttention(inputHash, authorizationHash, trace, effectMayHaveOccurred);
            if (currentOrdinal < 0 || currentOrdinal >= plan.Definition.Steps.Count || !visited.Add(currentOrdinal))
                return FailureOrAttention(inputHash, authorizationHash, trace, "workflow.plan.execution_order_invalid", effectMayHaveOccurred);
            var definitionStep = plan.Definition.Steps[currentOrdinal];
            var planStep = plan.Steps[currentOrdinal];
            if (planStep.Ordinal != currentOrdinal || !StringComparer.Ordinal.Equals(planStep.Id, definitionStep.Id))
                return FailureOrAttention(inputHash, authorizationHash, trace, "workflow.plan.step_drift", effectMayHaveOccurred);

            switch (definitionStep)
            {
                case WorkflowInputMapStep inputMap:
                    {
                        var evaluated = Evaluate(inputMap.Value, currentOrdinal, input, stepById, stepOutputs, variables);
                        if (evaluated is null ||
                            !WorkflowRuntimeValue.TryCanonicalize(evaluated, MaximumStepOutputBytes, out _))
                            return StepFailure(inputHash, authorizationHash, trace, planStep, 1, "workflow.input_map.output_invalid", effectMayHaveOccurred);
                        variables[inputMap.SaveAs] = evaluated;
                        stepOutputs[currentOrdinal] = evaluated;
                        trace.Add(new WorkflowStepTrace(currentOrdinal, planStep.Id, 1, "succeeded"));
                        if (!TrySingleSuccessor(planStep, out currentOrdinal))
                            return FailureOrAttention(inputHash, authorizationHash, trace, "workflow.plan.successor_invalid", effectMayHaveOccurred);
                        break;
                    }
                case WorkflowTransformStep transform:
                    {
                        var operands = new List<WorkflowValue>(transform.Operands.Count);
                        foreach (var operand in transform.Operands)
                        {
                            var evaluated = Evaluate(operand, currentOrdinal, input, stepById, stepOutputs, variables);
                            if (evaluated is null)
                                return StepFailure(inputHash, authorizationHash, trace, planStep, 1, "workflow.binding.runtime_invalid", effectMayHaveOccurred);
                            operands.Add(evaluated);
                        }
                        var transformed = Transform(transform.Operation, operands);
                        if (transformed is null ||
                            !WorkflowRuntimeValue.TryCanonicalize(transformed, MaximumStepOutputBytes, out _))
                            return StepFailure(inputHash, authorizationHash, trace, planStep, 1, "workflow.transform.runtime_invalid", effectMayHaveOccurred);
                        variables[transform.SaveAs] = transformed;
                        stepOutputs[currentOrdinal] = transformed;
                        trace.Add(new WorkflowStepTrace(currentOrdinal, planStep.Id, 1, "succeeded"));
                        if (!TrySingleSuccessor(planStep, out currentOrdinal))
                            return FailureOrAttention(inputHash, authorizationHash, trace, "workflow.plan.successor_invalid", effectMayHaveOccurred);
                        break;
                    }
                case WorkflowConditionStep condition:
                    {
                        var left = Evaluate(condition.Left, currentOrdinal, input, stepById, stepOutputs, variables);
                        var right = condition.Right is null
                            ? null
                            : Evaluate(condition.Right, currentOrdinal, input, stepById, stepOutputs, variables);
                        bool branch;
                        if (condition.Operator == "exists" && condition.Right is null)
                        {
                            branch = left is not null and not WorkflowNullValue;
                        }
                        else if (left is null || condition.Right is not null && right is null ||
                                 !TryEvaluateCondition(condition.Operator, left, right, out branch))
                        {
                            return StepFailure(inputHash, authorizationHash, trace, planStep, 1, "workflow.condition.runtime_invalid", effectMayHaveOccurred);
                        }
                        var successorIndex = branch ? 0 : 1;
                        if (planStep.SuccessorOrdinals.Count != 2)
                            return FailureOrAttention(inputHash, authorizationHash, trace, "workflow.plan.successor_invalid", effectMayHaveOccurred);
                        currentOrdinal = planStep.SuccessorOrdinals[successorIndex];
                        trace.Add(new WorkflowStepTrace(planStep.Ordinal, planStep.Id, 1, "succeeded"));
                        break;
                    }
                case WorkflowToolCallStep toolCall:
                    {
                        var evaluated = Evaluate(toolCall.Arguments, currentOrdinal, input, stepById, stepOutputs, variables);
                        if (evaluated is not WorkflowObjectValue payload ||
                            !WorkflowRuntimeValue.TryCanonicalize(payload, MaximumInputBytes, out _))
                            return StepFailure(inputHash, authorizationHash, trace, planStep, 1, "workflow.tool.payload_invalid", effectMayHaveOccurred);
                        var execution = await ExecuteToolAsync(
                            run,
                            plan,
                            planStep,
                            payload,
                            inputHash,
                            authorizationHash,
                            tools,
                            cancellationToken).ConfigureAwait(false);
                        trace.AddRange(execution.Trace);
                        effectMayHaveOccurred |= execution.EffectMayHaveOccurred;
                        if (execution.TerminalStatus is not null)
                        {
                            var terminalStatus = execution.TerminalStatus.Value;
                            var nextAction = execution.NextAction;
                            if (effectMayHaveOccurred && terminalStatus is WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled)
                            {
                                terminalStatus = WorkflowRunStatus.AttentionRequired;
                                nextAction = WorkflowNextAction.ReviewOutcome;
                            }
                            return new WorkflowRunResult(
                                terminalStatus,
                                nextAction,
                                inputHash,
                                authorizationHash,
                                null,
                                null,
                                trace,
                                execution.ErrorCode);
                        }
                        var output = execution.Output!;
                        stepOutputs[currentOrdinal] = output;
                        if (!TrySingleSuccessor(planStep, out currentOrdinal))
                            return FailureOrAttention(inputHash, authorizationHash, trace, "workflow.plan.successor_invalid", effectMayHaveOccurred);
                        break;
                    }
                case WorkflowNeedsInformationStep needsInformation:
                    trace.Add(new WorkflowStepTrace(currentOrdinal, planStep.Id, 1, "needs_information"));
                    return new WorkflowRunResult(
                        WorkflowRunStatus.NeedsInformation,
                        WorkflowNextAction.ProvideInformation,
                        inputHash,
                        authorizationHash,
                        null,
                        needsInformation.RequiredInputs,
                        trace);
                case WorkflowOutputStep output:
                    {
                        var evaluated = Evaluate(output.Value, currentOrdinal, input, stepById, stepOutputs, variables);
                        if (evaluated is not WorkflowObjectValue finalOutput ||
                            !WorkflowRuntimeValue.TryCanonicalize(finalOutput, MaximumFinalOutputBytes, out _))
                            return StepFailure(inputHash, authorizationHash, trace, planStep, 1, "workflow.output.runtime_invalid", effectMayHaveOccurred);
                        trace.Add(new WorkflowStepTrace(currentOrdinal, planStep.Id, 1, "succeeded"));
                        return new WorkflowRunResult(
                            WorkflowRunStatus.Completed,
                            WorkflowNextAction.None,
                            inputHash,
                            authorizationHash,
                            finalOutput,
                            null,
                            trace);
                    }
                default:
                    return FailureOrAttention(inputHash, authorizationHash, trace, "workflow.step.unsupported", effectMayHaveOccurred);
            }
        }
    }

    private static async Task<ToolAttemptResult> ExecuteToolAsync(
        WorkflowRunIdentity run,
        ValidatedWorkflowPlan plan,
        ValidatedWorkflowPlanStep step,
        WorkflowObjectValue payload,
        string inputHash,
        string authorizationHash,
        IWorkflowToolExecutor tools,
        CancellationToken cancellationToken)
    {
        var trace = new List<WorkflowStepTrace>();
        if (step.CanonicalToolName is null || step.CanonicalRisk is null ||
            step.PreparationMode is null || step.RetryClass is null || step.EffectClass is null ||
            step.InputContractHash is null || step.ResultContractHash is null)
            return ToolAttemptResult.Failed(trace, "workflow.plan.tool_metadata_missing");
        var isEffect = step.EffectClass == WorkflowToolEffectClass.Effect;
        var retriesAllowed = step.EffectClass == WorkflowToolEffectClass.Observation &&
                             step.RetryClass == WorkflowRetryClass.ReadOnly;
        for (var attempt = 1; attempt <= step.MaximumRetries + 1; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
                return ToolAttemptResult.Cancelled(trace);
            WorkflowToolExecutionResponse response;
            try
            {
                response = await tools.ExecuteAsync(
                    new WorkflowToolExecutionRequest(
                        run,
                        plan.DefinitionHash,
                        plan.PlanHash,
                        inputHash,
                        authorizationHash,
                        step.Ordinal,
                        step.Id,
                        attempt,
                        step.MaximumRetries,
                        step.CanonicalToolName,
                        step.CanonicalRisk,
                        step.PreparationMode.Value,
                        step.InputContractHash,
                        step.ResultContractHash,
                        payload),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (step.EffectClass == WorkflowToolEffectClass.Effect)
                {
                    trace.Add(new WorkflowStepTrace(step.Ordinal, step.Id, attempt, "outcome_unknown", "workflow.tool.outcome_unknown"));
                    return ToolAttemptResult.Attention(trace, "workflow.tool.outcome_unknown", effectMayHaveOccurred: true);
                }
                return ToolAttemptResult.Cancelled(trace);
            }
            catch (Exception)
            {
                if (step.EffectClass == WorkflowToolEffectClass.Effect)
                {
                    trace.Add(new WorkflowStepTrace(step.Ordinal, step.Id, attempt, "outcome_unknown", "workflow.tool.outcome_unknown"));
                    return ToolAttemptResult.Attention(trace, "workflow.tool.outcome_unknown", effectMayHaveOccurred: true);
                }
                trace.Add(new WorkflowStepTrace(step.Ordinal, step.Id, attempt, "failed", "workflow.tool.executor_failed"));
                return ToolAttemptResult.Failed(trace, "workflow.tool.executor_failed");
            }

            if (!HasValidResponse(response))
            {
                trace.Add(new WorkflowStepTrace(step.Ordinal, step.Id, attempt, "failed", "workflow.tool.response_invalid"));
                return isEffect
                    ? ToolAttemptResult.Attention(trace, "workflow.tool.response_invalid", effectMayHaveOccurred: true)
                    : ToolAttemptResult.Failed(trace, "workflow.tool.response_invalid");
            }
            switch (response.Disposition)
            {
                case WorkflowToolExecutionDisposition.Succeeded:
                    if (response.Output is null ||
                        !WorkflowRuntimeValue.TryCanonicalize(response.Output, MaximumStepOutputBytes, out _))
                    {
                        trace.Add(new WorkflowStepTrace(step.Ordinal, step.Id, attempt, "failed", "workflow.tool.output_invalid"));
                        return isEffect
                            ? ToolAttemptResult.Attention(trace, "workflow.tool.output_invalid", effectMayHaveOccurred: true)
                            : ToolAttemptResult.Failed(trace, "workflow.tool.output_invalid");
                    }
                    trace.Add(new WorkflowStepTrace(step.Ordinal, step.Id, attempt, "succeeded"));
                    return ToolAttemptResult.Succeeded(response.Output, trace, isEffect);
                case WorkflowToolExecutionDisposition.RetryableFailed when retriesAllowed && attempt <= step.MaximumRetries:
                    trace.Add(new WorkflowStepTrace(step.Ordinal, step.Id, attempt, "retryable_failed", response.ErrorCode));
                    continue;
                case WorkflowToolExecutionDisposition.OutcomeUnknown:
                    trace.Add(new WorkflowStepTrace(step.Ordinal, step.Id, attempt, "outcome_unknown", response.ErrorCode));
                    return ToolAttemptResult.Attention(
                        trace,
                        response.ErrorCode ?? "workflow.tool.outcome_unknown",
                        effectMayHaveOccurred: isEffect);
                case WorkflowToolExecutionDisposition.Cancelled:
                    trace.Add(new WorkflowStepTrace(step.Ordinal, step.Id, attempt, "cancelled", response.ErrorCode));
                    return ToolAttemptResult.Cancelled(trace);
                case WorkflowToolExecutionDisposition.NotStarted:
                    trace.Add(new WorkflowStepTrace(step.Ordinal, step.Id, attempt, "not_started", response.ErrorCode));
                    return ToolAttemptResult.Failed(trace, response.ErrorCode ?? "workflow.tool.not_started");
                default:
                    trace.Add(new WorkflowStepTrace(step.Ordinal, step.Id, attempt, "failed", response.ErrorCode));
                    return ToolAttemptResult.Failed(
                        trace,
                        response.ErrorCode ?? "workflow.tool.failed_known",
                        effectMayHaveOccurred: isEffect);
            }
        }
        return ToolAttemptResult.Failed(trace, "workflow.tool.retry_exhausted");
    }

    private static WorkflowValue? Evaluate(
        WorkflowValue template,
        int consumerOrdinal,
        WorkflowObjectValue input,
        IReadOnlyDictionary<string, int> stepById,
        IReadOnlyDictionary<int, WorkflowValue> stepOutputs,
        IReadOnlyDictionary<string, WorkflowValue> variables)
    {
        switch (template)
        {
            case WorkflowBindingValue binding:
                {
                    WorkflowValue? root = binding.Source switch
                    {
                        WorkflowBindingSource.Input => input,
                        WorkflowBindingSource.Step when binding.StepId is not null &&
                                                        stepById.TryGetValue(binding.StepId, out var producer) &&
                                                        producer < consumerOrdinal &&
                                                        stepOutputs.TryGetValue(producer, out var stepValue) => stepValue,
                        WorkflowBindingSource.Variable when binding.VariableName is not null &&
                                                            variables.TryGetValue(binding.VariableName, out var variable) => variable,
                        _ => null,
                    };
                    return root is not null && WorkflowRuntimeValue.TryResolvePointer(root, binding.JsonPointer, out var resolved)
                        ? resolved
                        : null;
                }
            case WorkflowArrayValue array:
                {
                    var items = new List<WorkflowValue>(array.Items.Count);
                    foreach (var item in array.Items)
                    {
                        var evaluated = Evaluate(item, consumerOrdinal, input, stepById, stepOutputs, variables);
                        if (evaluated is null) return null;
                        items.Add(evaluated);
                    }
                    return new WorkflowArrayValue(items);
                }
            case WorkflowObjectValue valueObject:
                {
                    var properties = new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);
                    foreach (var pair in valueObject.Properties)
                    {
                        var evaluated = Evaluate(pair.Value, consumerOrdinal, input, stepById, stepOutputs, variables);
                        if (evaluated is null) return null;
                        properties.Add(pair.Key, evaluated);
                    }
                    return new WorkflowObjectValue(properties);
                }
            case WorkflowNullValue or WorkflowBooleanValue or WorkflowNumberValue or WorkflowStringValue:
                return template;
            default:
                return null;
        }
    }

    private static WorkflowValue? Transform(string operation, IReadOnlyList<WorkflowValue> operands)
    {
        if (operands.Count == 0 || operands.Any(operand => operand is not WorkflowStringValue)) return null;
        var strings = operands.Cast<WorkflowStringValue>().Select(value => value.Value).ToArray();
        try
        {
            return operation switch
            {
                "concat" => BoundedString(string.Concat(strings)),
                "trim" when strings.Length == 1 => BoundedString(strings[0].Trim()),
                "lowerInvariant" when strings.Length == 1 => BoundedString(strings[0].ToLowerInvariant()),
                "upperInvariant" when strings.Length == 1 => BoundedString(strings[0].ToUpperInvariant()),
                "toNumber" when strings.Length == 1 &&
                                WorkflowDefinitionParser.TryCanonicalizeRuntimeNumber(strings[0], out var number) => number,
                "toDate" when strings.Length == 1 && TryNormalizeDate(strings[0], out var date) =>
                    new WorkflowStringValue(date),
                _ => null,
            };
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static WorkflowStringValue? BoundedString(string value) =>
        value.Length <= WorkflowDefinitionParser.MaximumStringCharacters
            ? new WorkflowStringValue(value)
            : null;

    internal static bool TryNormalizeDate(string value, out string normalized)
    {
        normalized = string.Empty;
        if (value.Length is < 20 or > 35 || value[10] != 'T' ||
            !(value.EndsWith('Z') || value.Length >= 6 && value[^6] is '+' or '-' && value[^3] == ':'))
            return false;
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];
        if (!DateTimeOffset.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
            return false;
        normalized = parsed.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryEvaluateCondition(
        string operation,
        WorkflowValue left,
        WorkflowValue? right,
        out bool result)
    {
        result = operation switch
        {
            "equals" when right is not null => WorkflowRuntimeValue.StructuralEquals(left, right),
            "notEquals" when right is not null => !WorkflowRuntimeValue.StructuralEquals(left, right),
            "exists" when right is null => left is not WorkflowNullValue,
            "isTrue" when right is null && left is WorkflowBooleanValue boolean => boolean.Value,
            "isFalse" when right is null && left is WorkflowBooleanValue boolean => !boolean.Value,
            _ => false,
        };
        return operation switch
        {
            "equals" or "notEquals" => right is not null,
            "exists" => right is null,
            "isTrue" or "isFalse" => right is null && left is WorkflowBooleanValue,
            _ => false,
        };
    }

    private static bool TrySingleSuccessor(ValidatedWorkflowPlanStep step, out int successor)
    {
        successor = -1;
        if (step.SuccessorOrdinals.Count != 1) return false;
        successor = step.SuccessorOrdinals[0];
        return successor > step.Ordinal;
    }

    private static bool HasValidRunIdentity(WorkflowRunIdentity run) =>
        IsRunIdentifier(run.WorkflowRunId) &&
        IsRunIdentifier(run.ConversationId) &&
        IsRunIdentifier(run.ParentRunId) &&
        IsRunIdentifier(run.ParentToolCallId) &&
        run.ExactCapabilityVersion is not null &&
        run.ExactCapabilityVersion.Id is not null &&
        run.ExactCapabilityVersion.Version is not null &&
        run.ExactCapabilityVersion.ContentHash is not null &&
        run.CatalogRevision is >= 1 and <= 9_007_199_254_740_991;

    private static bool IsRunIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => character is >= '!' and <= '~');

    private static bool HasCoherentPlan(ValidatedWorkflowPlan plan)
    {
        try
        {
            if (plan.Definition is null || plan.DefinitionHash is null || plan.PlanHash is null ||
                plan.Steps.Count != plan.Definition.Steps.Count ||
                plan.Steps.Select((step, ordinal) => step is not null && step.Ordinal == ordinal &&
                    StringComparer.Ordinal.Equals(step.Id, plan.Definition.Steps[ordinal].Id)).Any(valid => !valid) ||
                plan.Steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() != plan.Steps.Count)
                return false;
            var definitionHash = WorkflowPlanHasher.ComputeDefinitionHash(plan.Definition);
            if (!definitionHash.Equals(plan.DefinitionHash)) return false;
            var planHash = WorkflowPlanHasher.ComputePlanHash(
                definitionHash,
                plan.InputContractHash,
                plan.Steps,
                plan.RequiredTools,
                plan.MaximumRisk);
            return planHash.Equals(plan.PlanHash);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          FormatException or OverflowException)
        {
            return false;
        }
    }

    private static bool HasValidResponse(WorkflowToolExecutionResponse response) =>
        response is not null && Enum.IsDefined(response.Disposition) &&
        IsCode(response.ErrorCode, allowNull: true) && IsCode(response.SummaryCode, allowNull: true) &&
        (response.Disposition == WorkflowToolExecutionDisposition.Succeeded
            ? response.Output is not null && response.ErrorCode is null
            : response.Output is null);

    private static bool IsCode(string? value, bool allowNull) =>
        value is null ? allowNull : value.Length is >= 1 and <= 96 &&
        value[0] is >= 'a' and <= 'z' && value.All(character =>
            character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character is '.' or '_');

    private static string ComputeContractHash(string canonicalJson) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant()}";

    private static WorkflowRunResult StepFailure(
        string inputHash,
        string authorizationHash,
        List<WorkflowStepTrace> trace,
        ValidatedWorkflowPlanStep step,
        int attempt,
        string errorCode,
        bool effectMayHaveOccurred)
    {
        trace.Add(new WorkflowStepTrace(step.Ordinal, step.Id, attempt, "failed", errorCode));
        return FailureOrAttention(inputHash, authorizationHash, trace, errorCode, effectMayHaveOccurred);
    }

    private static WorkflowRunResult FailureOrAttention(
        string inputHash,
        string authorizationHash,
        IReadOnlyList<WorkflowStepTrace> trace,
        string errorCode,
        bool effectMayHaveOccurred) =>
        effectMayHaveOccurred
            ? new WorkflowRunResult(
                WorkflowRunStatus.AttentionRequired,
                WorkflowNextAction.ReviewOutcome,
                inputHash,
                authorizationHash,
                null,
                null,
                trace,
                errorCode)
            : Failed(inputHash, authorizationHash, trace, errorCode);

    private static WorkflowRunResult CancelledOrAttention(
        string inputHash,
        string authorizationHash,
        IReadOnlyList<WorkflowStepTrace> trace,
        bool effectMayHaveOccurred) =>
        effectMayHaveOccurred
            ? new WorkflowRunResult(
                WorkflowRunStatus.AttentionRequired,
                WorkflowNextAction.ReviewOutcome,
                inputHash,
                authorizationHash,
                null,
                null,
                trace,
                "workflow.cancelled_after_effect")
            : Cancelled(inputHash, authorizationHash, trace);

    private static WorkflowRunResult Failed(
        string inputHash,
        string authorizationHash,
        IReadOnlyList<WorkflowStepTrace> trace,
        string errorCode) =>
        new(
            WorkflowRunStatus.Failed,
            WorkflowNextAction.RetryFromStart,
            inputHash,
            authorizationHash,
            null,
            null,
            trace,
            errorCode);

    private static WorkflowRunResult Cancelled(
        string inputHash,
        string authorizationHash,
        IReadOnlyList<WorkflowStepTrace> trace) =>
        new(
            WorkflowRunStatus.Cancelled,
            WorkflowNextAction.RetryFromStart,
            inputHash,
            authorizationHash,
            null,
            null,
            trace,
            "workflow.cancelled");

    private sealed record ToolAttemptResult(
        WorkflowObjectValue? Output,
        IReadOnlyList<WorkflowStepTrace> Trace,
        WorkflowRunStatus? TerminalStatus = null,
        WorkflowNextAction NextAction = WorkflowNextAction.None,
        string? ErrorCode = null,
        bool EffectMayHaveOccurred = false)
    {
        public static ToolAttemptResult Succeeded(
            WorkflowObjectValue output,
            IReadOnlyList<WorkflowStepTrace> trace,
            bool effectMayHaveOccurred = false) =>
            new(output, trace, EffectMayHaveOccurred: effectMayHaveOccurred);

        public static ToolAttemptResult Failed(
            IReadOnlyList<WorkflowStepTrace> trace,
            string errorCode,
            bool effectMayHaveOccurred = false) =>
            new(
                null,
                trace,
                WorkflowRunStatus.Failed,
                WorkflowNextAction.RetryFromStart,
                errorCode,
                effectMayHaveOccurred);

        public static ToolAttemptResult Cancelled(IReadOnlyList<WorkflowStepTrace> trace) =>
            new(null, trace, WorkflowRunStatus.Cancelled, WorkflowNextAction.RetryFromStart, "workflow.cancelled");

        public static ToolAttemptResult Attention(
            IReadOnlyList<WorkflowStepTrace> trace,
            string errorCode,
            bool effectMayHaveOccurred = true) =>
            new(
                null,
                trace,
                WorkflowRunStatus.AttentionRequired,
                WorkflowNextAction.ReviewOutcome,
                errorCode,
                effectMayHaveOccurred);
    }
}
