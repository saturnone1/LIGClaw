using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Domain;

namespace LIGClaw.Application.Workflows;

public sealed record WorkflowValidationIssue(
    string Code,
    string Path,
    string? StepId = null);

public sealed record WorkflowStaticValidationResult(
    ValidatedWorkflowPlan? Plan,
    IReadOnlyList<WorkflowValidationIssue> Issues)
{
    public bool Succeeded => Plan is not null && Issues.Count == 0;
}

public sealed class WorkflowStaticValidator(IWorkflowToolMetadataCatalog tools)
{
    private const int MaximumBranchDepth = 5;
    private static readonly HashSet<string> ForbiddenTools =
    [
        "capability.select.v1",
        "workflow.invoke.v1",
        "subagent.run.v1",
        "mcp.read.v1",
        "agent_job.create.v1",
        "agent_job.list.v1",
        "agent_job.cancel.v1",
        "agent_job.control.v1",
    ];

    public WorkflowStaticValidationResult Validate(
        WorkflowDefinition definition,
        CapabilityManifest manifest,
        string canonicalInputSchemaJson)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(canonicalInputSchemaJson);
        if (definition.SchemaVersion != 1)
            return Failure("workflow.schema_version.unsupported", "/schemaVersion");
        if (definition.Steps.Count is < 1 or > WorkflowDefinitionParser.MaximumSteps)
            return Failure("workflow.step.count_out_of_bounds", "/steps");
        if (manifest.Kind != CapabilityKind.Workflow ||
            !StringComparer.Ordinal.Equals(manifest.Entrypoint, "workflow.json"))
            return Failure("workflow.manifest.kind_invalid", "/manifest");
        if (manifest.RequiredConnections.Any(requirement =>
                !CapabilityConnectionKindPolicy.IsSupported(requirement.Kind)))
            return Failure("workflow.connection.kind_unsupported", "/manifest/requiredConnections");
        if (!CapabilityRuntimeInputSchemaPolicy.HasBoundedObjectRoot(canonicalInputSchemaJson) ||
            !WorkflowContractSchema.TryParse(canonicalInputSchemaJson, out var workflowInputSchema) ||
            workflowInputSchema is null || workflowInputSchema.Type != "object")
            return Failure("workflow.input_schema.unsupported", "/inputSchema");

        var stepById = new Dictionary<string, int>(StringComparer.Ordinal);
        var insensitiveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedConnectionSlots = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < definition.Steps.Count; index++)
        {
            var step = definition.Steps[index];
            if (step is null || !HasValidAstInvariants(step))
                return Failure("workflow.ast.invalid", $"/steps/{index}");
            if (!IsIdentifier(step.Id))
                return Failure("workflow.step.id_invalid", $"/steps/{index}/id");
            if (!stepById.TryAdd(step.Id, index) || !insensitiveIds.Add(step.Id))
                return Failure("workflow.step.id_collision", $"/steps/{index}/id");
        }
        if (!IsIdentifier(definition.EntryStepId) || !stepById.TryGetValue(definition.EntryStepId, out var entryOrdinal))
            return Failure("workflow.entry.missing", "/entryStepId");

        var successorOrdinals = new IReadOnlyList<int>[definition.Steps.Count];
        var predecessors = Enumerable.Range(0, definition.Steps.Count)
            .Select(_ => new List<int>())
            .ToArray();
        var outputCount = 0;
        for (var index = 0; index < definition.Steps.Count; index++)
        {
            var step = definition.Steps[index];
            if (step is WorkflowOutputStep) outputCount++;
            if (step is WorkflowConditionStep condition &&
                StringComparer.Ordinal.Equals(condition.ThenStepId, condition.ElseStepId))
                return Failure("workflow.condition.branches_equal", $"/steps/{index}", step.Id);
            var successors = GetSuccessorIds(step);
            if ((step is WorkflowOutputStep or WorkflowNeedsInformationStep) && successors.Count != 0 ||
                step is not (WorkflowOutputStep or WorkflowNeedsInformationStep) && successors.Count == 0)
                return Failure("workflow.graph.terminal_invalid", $"/steps/{index}", step.Id);
            var resolved = new List<int>(successors.Count);
            foreach (var successorId in successors)
            {
                if (!stepById.TryGetValue(successorId, out var successorOrdinal))
                    return Failure("workflow.graph.dangling_edge", $"/steps/{index}", step.Id);
                if (successorOrdinal <= index)
                    return Failure("workflow.graph.backward_edge", $"/steps/{index}", step.Id);
                if (resolved.Contains(successorOrdinal))
                    return Failure("workflow.graph.duplicate_edge", $"/steps/{index}", step.Id);
                resolved.Add(successorOrdinal);
                predecessors[successorOrdinal].Add(index);
            }
            successorOrdinals[index] = resolved.AsReadOnly();
        }
        if (outputCount != 1)
            return Failure("workflow.output.count_invalid", "/steps");

        var reachable = FindReachable(entryOrdinal, successorOrdinals);
        if (reachable.Count != definition.Steps.Count)
        {
            var unreachable = Enumerable.Range(0, definition.Steps.Count).First(index => !reachable.Contains(index));
            return Failure("workflow.graph.unreachable_step", $"/steps/{unreachable}", definition.Steps[unreachable].Id);
        }
        var branchDepth = new int[definition.Steps.Count];
        branchDepth[entryOrdinal] = definition.Steps[entryOrdinal] is WorkflowConditionStep ? 1 : 0;
        for (var index = entryOrdinal; index < definition.Steps.Count; index++)
        {
            if (!reachable.Contains(index)) continue;
            if (branchDepth[index] > MaximumBranchDepth)
                return Failure("workflow.graph.branch_depth_exceeded", $"/steps/{index}", definition.Steps[index].Id);
            foreach (var successor in successorOrdinals[index])
            {
                var depth = branchDepth[index] + (definition.Steps[successor] is WorkflowConditionStep ? 1 : 0);
                branchDepth[successor] = Math.Max(branchDepth[successor], depth);
            }
        }

        var dominators = ComputeDominators(entryOrdinal, predecessors, reachable);
        var variables = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < definition.Steps.Count; index++)
        {
            var name = definition.Steps[index] switch
            {
                WorkflowInputMapStep inputMap => inputMap.SaveAs,
                WorkflowTransformStep transform => transform.SaveAs,
                _ => null,
            };
            if (name is null) continue;
            if (!IsIdentifier(name) || !variables.TryAdd(name, index))
                return Failure("workflow.variable.collision", $"/steps/{index}/saveAs", definition.Steps[index].Id);
        }

        var metadataByStep = new Dictionary<int, WorkflowToolMetadata>();
        var inputSchemaByStep = new Dictionary<int, WorkflowContractSchema>();
        var resultSchemaByStep = new Dictionary<int, WorkflowContractSchema>();
        var toolNames = new HashSet<string>(StringComparer.Ordinal);
        var actualMaximumRisk = "R0";
        for (var index = 0; index < definition.Steps.Count; index++)
        {
            var step = definition.Steps[index];
            if (step is WorkflowTransformStep transform && !HasValidTransformArity(transform))
                return Failure("workflow.transform.arity_invalid", $"/steps/{index}/operands", step.Id);
            if (step is WorkflowOutputStep { Value: not WorkflowObjectValue })
                return Failure("workflow.output.object_required", $"/steps/{index}/value", step.Id);
            if (step is WorkflowNeedsInformationStep needsInformation)
            {
                foreach (var pointer in needsInformation.RequiredInputs)
                {
                    if (!workflowInputSchema.TryResolvePointer(pointer, out _))
                        return Failure("workflow.input_pointer.unknown", $"/steps/{index}/requiredInputs", step.Id);
                }
            }
            if (step is not WorkflowToolCallStep toolCall) continue;
            if (IsForbiddenTool(toolCall.Tool))
                return Failure("workflow.tool.forbidden", $"/steps/{index}/tool", step.Id);
            if (!tools.TryGet(toolCall.Tool, out var metadata) || metadata is null ||
                !StringComparer.Ordinal.Equals(metadata.CanonicalName, toolCall.Tool))
                return Failure("workflow.tool.unknown", $"/steps/{index}/tool", step.Id);
            if (!metadata.IsAvailable)
                return Failure("workflow.tool.unavailable", $"/steps/{index}/tool", step.Id);
            if (!metadata.IsWorkflowAllowed)
                return Failure("workflow.tool.forbidden", $"/steps/{index}/tool", step.Id);
            if (!Enum.IsDefined(metadata.PreparationMode) ||
                !Enum.IsDefined(metadata.RetryClass) ||
                !Enum.IsDefined(metadata.EffectClass))
                return Failure("workflow.tool.metadata_invalid", $"/steps/{index}/tool", step.Id);
            if (!IsRisk(metadata.Risk))
                return Failure("workflow.tool.risk_invalid", $"/steps/{index}/tool", step.Id);
            if (metadata.EffectClass == WorkflowToolEffectClass.Effect &&
                metadata.PreparationMode != WorkflowToolPreparationMode.OneShot)
                return Failure("workflow.tool.prepared_required", $"/steps/{index}/tool", step.Id);
            if (metadata.EffectClass == WorkflowToolEffectClass.Observation &&
                metadata.PreparationMode != WorkflowToolPreparationMode.None)
                return Failure("workflow.tool.metadata_invalid", $"/steps/{index}/tool", step.Id);
            if (toolCall.MaximumRetries is < 0 or > 3 ||
                toolCall.MaximumRetries > 0 &&
                (metadata.EffectClass != WorkflowToolEffectClass.Observation ||
                 metadata.RetryClass != WorkflowRetryClass.ReadOnly))
                return Failure("workflow.tool.retry_forbidden", $"/steps/{index}/maxRetries", step.Id);
            if (!WorkflowContractSchema.TryParse(metadata.CanonicalInputSchemaJson, out var toolInputSchema) ||
                toolInputSchema is null || toolInputSchema.Type != "object")
                return Failure("workflow.tool.input_schema_unsupported", $"/steps/{index}/tool", step.Id);
            if (!WorkflowContractSchema.TryParse(metadata.CanonicalResultSchemaJson, out var toolResultSchema) ||
                toolResultSchema is null || toolResultSchema.Type != "object")
                return Failure("workflow.tool.result_schema_unsupported", $"/steps/{index}/tool", step.Id);
            metadataByStep.Add(index, metadata);
            inputSchemaByStep.Add(index, toolInputSchema);
            resultSchemaByStep.Add(index, toolResultSchema);
            toolNames.Add(toolCall.Tool);
            if (RiskOrdinal(metadata.Risk) > RiskOrdinal(actualMaximumRisk)) actualMaximumRisk = metadata.Risk;
        }

        var declaredTools = manifest.RequiredTools.ToHashSet(StringComparer.Ordinal);
        if (declaredTools.Count != manifest.RequiredTools.Count || !declaredTools.SetEquals(toolNames))
            return Failure("workflow.manifest.tools_mismatch", "/manifest/requiredTools");
        if (!IsRisk(manifest.MaximumRisk) || RiskOrdinal(actualMaximumRisk) > RiskOrdinal(manifest.MaximumRisk))
            return Failure("workflow.manifest.risk_underdeclared", "/manifest/maximumRisk");

        foreach (var needsInformationOrdinal in Enumerable.Range(0, definition.Steps.Count)
                     .Where(index => definition.Steps[index] is WorkflowNeedsInformationStep))
        {
            var hasPriorEffect = metadataByStep.Any(pair =>
                pair.Value.EffectClass == WorkflowToolEffectClass.Effect &&
                CanReach(pair.Key, needsInformationOrdinal, successorOrdinals));
            if (hasPriorEffect)
                return Failure(
                    "workflow.needs_information.after_effect",
                    $"/steps/{needsInformationOrdinal}",
                    definition.Steps[needsInformationOrdinal].Id);
        }

        var producedSchemas = new Dictionary<int, WorkflowContractSchema?>();

        (WorkflowContractSchema? Schema, WorkflowValidationIssue? Issue, bool Guaranteed) ResolveBinding(
            WorkflowBindingValue binding,
            int consumer)
        {
            WorkflowContractSchema? root;
            string pointerError;
            switch (binding.Source)
            {
                case WorkflowBindingSource.Input:
                    root = workflowInputSchema;
                    pointerError = "workflow.input_pointer.unknown";
                    break;
                case WorkflowBindingSource.Step:
                    if (binding.StepId is null || !stepById.TryGetValue(binding.StepId, out var producer) ||
                        producer >= consumer || !dominators[consumer].Contains(producer) ||
                        !ProducesBindableValue(definition.Steps[producer]))
                        return (null, new WorkflowValidationIssue(
                            "workflow.binding.step_not_dominating",
                            $"/steps/{consumer}",
                            definition.Steps[consumer].Id), false);
                    root = ResolveProducerSchema(producer);
                    pointerError = metadataByStep.ContainsKey(producer)
                        ? "workflow.binding.result_pointer_unknown"
                        : "workflow.binding.step_pointer_unknown";
                    break;
                case WorkflowBindingSource.Variable:
                    if (binding.VariableName is null || !variables.TryGetValue(binding.VariableName, out var variableProducer) ||
                        variableProducer >= consumer || !dominators[consumer].Contains(variableProducer))
                        return (null, new WorkflowValidationIssue(
                            "workflow.binding.variable_not_dominating",
                            $"/steps/{consumer}",
                            definition.Steps[consumer].Id), false);
                    root = ResolveProducerSchema(variableProducer);
                    pointerError = "workflow.binding.variable_pointer_unknown";
                    break;
                default:
                    return (null, new WorkflowValidationIssue(
                        "workflow.binding.source_unsupported",
                        $"/steps/{consumer}",
                        definition.Steps[consumer].Id), false);
            }
            return root is not null && root.TryResolvePointer(binding.JsonPointer, out var resolved, out var guaranteed)
                ? (resolved, null, guaranteed)
                : (null, new WorkflowValidationIssue(
                    pointerError,
                    $"/steps/{consumer}",
                    definition.Steps[consumer].Id), false);
        }

        WorkflowContractSchema? ResolveProducerSchema(int producer)
        {
            if (producedSchemas.TryGetValue(producer, out var cached)) return cached;
            var produced = definition.Steps[producer] switch
            {
                WorkflowToolCallStep => resultSchemaByStep.GetValueOrDefault(producer),
                WorkflowInputMapStep inputMap => WorkflowContractSchema.Infer(
                    inputMap.Value,
                    binding => ResolveBinding(binding, producer).Schema),
                WorkflowTransformStep transform => TransformResultSchema(transform),
                _ => null,
            };
            producedSchemas[producer] = produced;
            return produced;
        }

        for (var index = 0; index < definition.Steps.Count; index++)
        {
            foreach (var binding in EnumerateBindings(definition.Steps[index]))
            {
                var resolution = ResolveBinding(binding, index);
                if (resolution.Issue is not null)
                    return new WorkflowStaticValidationResult(null, [resolution.Issue]);
                var optionalExistsBinding = definition.Steps[index] is WorkflowConditionStep
                {
                    Operator: "exists",
                    Left: WorkflowBindingValue existsBinding,
                    Right: null,
                } && ReferenceEquals(binding, existsBinding);
                if (!resolution.Guaranteed && !optionalExistsBinding)
                    return Failure(
                        "workflow.binding.pointer_optional",
                        $"/steps/{index}",
                        definition.Steps[index].Id);
            }

            if (definition.Steps[index] is WorkflowTransformStep transform &&
                !TransformOperandsAreValid(transform, index, ResolveBinding))
                return Failure("workflow.transform.operand_type_mismatch", $"/steps/{index}/operands", transform.Id);
            if (definition.Steps[index] is WorkflowConditionStep condition &&
                !ConditionOperandsAreValid(condition, index, ResolveBinding))
                return Failure("workflow.condition.operand_type_mismatch", $"/steps/{index}", condition.Id);
            if (definition.Steps[index] is WorkflowToolCallStep toolCall &&
                !inputSchemaByStep[index].AcceptsValue(
                    toolCall.Arguments,
                    binding => ResolveBinding(binding, index).Schema))
                return Failure("workflow.tool.arguments_schema_mismatch", $"/steps/{index}/arguments", toolCall.Id);
            if (definition.Steps[index] is WorkflowToolCallStep
                {
                    Tool: "connection.http.request.v1",
                } httpCall)
            {
                var httpIssue = ValidateHttpConnectionCall(httpCall, manifest, index, usedConnectionSlots);
                if (httpIssue is not null)
                    return new WorkflowStaticValidationResult(null, [httpIssue]);
            }
            if (definition.Steps[index] is WorkflowToolCallStep
                {
                    Tool: "mail.send.v1",
                } mailSendCall)
            {
                var mailIssue = ValidateMailSendCall(
                    mailSendCall,
                    definition,
                    manifest,
                    index,
                    stepById,
                    dominators,
                    usedConnectionSlots);
                if (mailIssue is not null)
                    return new WorkflowStaticValidationResult(null, [mailIssue]);
            }
        }

        var declaredConnectionSlots = manifest.RequiredConnections
            .Select(requirement => requirement.Slot)
            .ToHashSet(StringComparer.Ordinal);
        if (declaredConnectionSlots.Count != manifest.RequiredConnections.Count ||
            !declaredConnectionSlots.SetEquals(usedConnectionSlots))
            return Failure("workflow.manifest.connections_mismatch", "/manifest/requiredConnections");

        var requiredTools = toolNames.Order(StringComparer.Ordinal).ToArray();
        var definitionHash = WorkflowPlanHasher.ComputeDefinitionHash(definition);
        var planSteps = definition.Steps.Select((step, index) =>
        {
            metadataByStep.TryGetValue(index, out var metadata);
            return new ValidatedWorkflowPlanStep(
                index,
                step.Id,
                step.Kind,
                successorOrdinals[index],
                metadata?.CanonicalName,
                metadata?.Risk,
                step is WorkflowToolCallStep toolCall ? toolCall.MaximumRetries : 0,
                metadata?.PreparationMode,
                metadata?.RetryClass,
                metadata?.EffectClass,
                metadata is null ? null : ComputeContractHash(metadata.CanonicalInputSchemaJson),
                metadata is null ? null : ComputeContractHash(metadata.CanonicalResultSchemaJson));
        }).ToArray();
        var inputContractHash = ComputeContractHash(canonicalInputSchemaJson);
        var planHash = WorkflowPlanHasher.ComputePlanHash(
            definitionHash,
            inputContractHash,
            planSteps,
            requiredTools,
            actualMaximumRisk);
        return new WorkflowStaticValidationResult(
            new ValidatedWorkflowPlan(
                definition,
                definitionHash,
                inputContractHash,
                planSteps,
                requiredTools,
                actualMaximumRisk,
                planHash),
            []);
    }

    private static WorkflowValidationIssue? ValidateHttpConnectionCall(
        WorkflowToolCallStep step,
        CapabilityManifest manifest,
        int ordinal,
        ISet<string> usedConnectionSlots)
    {
        var path = $"/steps/{ordinal}/arguments";
        if (!step.Arguments.Properties.TryGetValue("connectionSlot", out var slotValue) ||
            slotValue is not WorkflowStringValue { Value: var slot } ||
            !step.Arguments.Properties.TryGetValue("method", out var methodValue) ||
            methodValue is not WorkflowStringValue { Value: var method } ||
            !step.Arguments.Properties.TryGetValue("pathTemplate", out var templateValue) ||
            templateValue is not WorkflowStringValue { Value: var pathTemplate } ||
            !manifest.RequiredConnections.Any(requirement =>
                StringComparer.Ordinal.Equals(requirement.Slot, slot) &&
                StringComparer.Ordinal.Equals(requirement.Kind, CapabilityConnectionKindPolicy.Http)) ||
            !TryReadHttpParameters(step.Arguments, "pathParameters", out var pathParameters) ||
            !TryReadHttpParameters(step.Arguments, "queryParameters", out var queryParameters) ||
            !ConnectionHttpTemplatePolicy.TryBuild(
                method,
                pathTemplate,
                pathParameters,
                queryParameters,
                out _,
                out _))
            return new WorkflowValidationIssue("workflow.connection.http_binding_invalid", path, step.Id);
        usedConnectionSlots.Add(slot);
        return null;
    }

    private static WorkflowValidationIssue? ValidateMailSendCall(
        WorkflowToolCallStep step,
        WorkflowDefinition definition,
        CapabilityManifest manifest,
        int ordinal,
        IReadOnlyDictionary<string, int> stepById,
        IReadOnlyList<HashSet<int>> dominators,
        ISet<string> usedConnectionSlots)
    {
        var path = $"/steps/{ordinal}/arguments";
        if (!step.Arguments.Properties.TryGetValue("connectionSlot", out var slotValue) ||
            slotValue is not WorkflowStringValue { Value: var slot } ||
            !manifest.RequiredConnections.Any(requirement =>
                StringComparer.Ordinal.Equals(requirement.Slot, slot) &&
                requirement.Kind is CapabilityConnectionKindPolicy.MailApi or
                    CapabilityConnectionKindPolicy.Smtp) ||
            !step.Arguments.Properties.TryGetValue("draftHash", out var draftHashValue) ||
            draftHashValue is not WorkflowBindingValue
            {
                Source: WorkflowBindingSource.Step,
                StepId: { } draftStepId,
                VariableName: null,
                JsonPointer: "/draftHash",
            } ||
            !stepById.TryGetValue(draftStepId, out var draftOrdinal) ||
            draftOrdinal >= ordinal ||
            !dominators[ordinal].Contains(draftOrdinal) ||
            definition.Steps[draftOrdinal] is not WorkflowToolCallStep { Tool: "mail.create_draft.v1" })
            return new WorkflowValidationIssue("workflow.connection.mail_binding_invalid", path, step.Id);
        usedConnectionSlots.Add(slot);
        return null;
    }

    private static bool TryReadHttpParameters(
        WorkflowObjectValue arguments,
        string name,
        out IReadOnlyList<ConnectionHttpParameter> parameters)
    {
        parameters = [];
        if (!arguments.Properties.TryGetValue(name, out var value)) return true;
        if (value is not WorkflowArrayValue array) return false;
        var parsed = new List<ConnectionHttpParameter>(array.Items.Count);
        foreach (var item in array.Items)
        {
            if (item is not WorkflowObjectValue parameter ||
                parameter.Properties.Count != 2 ||
                !parameter.Properties.TryGetValue("name", out var nameValue) ||
                nameValue is not WorkflowStringValue { Value: var parameterName } ||
                !parameter.Properties.TryGetValue("value", out var parameterValue))
                return false;
            var valueText = parameterValue switch
            {
                WorkflowStringValue text => text.Value,
                WorkflowBindingValue => "binding-value",
                _ => null,
            };
            if (valueText is null) return false;
            parsed.Add(new ConnectionHttpParameter(parameterName, valueText));
        }
        parameters = parsed;
        return true;
    }

    private static IReadOnlyList<string> GetSuccessorIds(WorkflowStep step) => step switch
    {
        WorkflowInputMapStep inputMap => [inputMap.Next],
        WorkflowToolCallStep toolCall => [toolCall.Next],
        WorkflowConditionStep condition => [condition.ThenStepId, condition.ElseStepId],
        WorkflowTransformStep transform => [transform.Next],
        WorkflowNeedsInformationStep => [],
        WorkflowOutputStep => [],
        _ => [],
    };

    private static HashSet<int> FindReachable(
        int entryOrdinal,
        IReadOnlyList<int>[] successors)
    {
        var result = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(entryOrdinal);
        while (pending.TryPop(out var current))
        {
            if (!result.Add(current)) continue;
            foreach (var successor in successors[current]) pending.Push(successor);
        }
        return result;
    }

    private static bool CanReach(
        int start,
        int target,
        IReadOnlyList<int>[] successors)
    {
        if (start >= target) return false;
        var pending = new Stack<int>();
        var seen = new HashSet<int>();
        pending.Push(start);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue;
            foreach (var successor in successors[current])
            {
                if (successor == target) return true;
                if (successor < target) pending.Push(successor);
            }
        }
        return false;
    }

    private static IReadOnlyList<HashSet<int>> ComputeDominators(
        int entryOrdinal,
        IReadOnlyList<int>[] predecessors,
        IReadOnlySet<int> reachable)
    {
        var result = Enumerable.Range(0, predecessors.Length)
            .Select(_ => new HashSet<int>())
            .ToArray();
        result[entryOrdinal].Add(entryOrdinal);
        for (var index = entryOrdinal + 1; index < predecessors.Length; index++)
        {
            if (!reachable.Contains(index)) continue;
            var incoming = predecessors[index].Where(reachable.Contains).ToArray();
            if (incoming.Length == 0) continue;
            result[index].UnionWith(result[incoming[0]]);
            foreach (var predecessor in incoming.Skip(1)) result[index].IntersectWith(result[predecessor]);
            result[index].Add(index);
        }
        return result;
    }

    private static IEnumerable<WorkflowBindingValue> EnumerateBindings(WorkflowStep step)
    {
        var values = step switch
        {
            WorkflowInputMapStep inputMap => new[] { inputMap.Value },
            WorkflowToolCallStep toolCall => new WorkflowValue[] { toolCall.Arguments },
            WorkflowConditionStep condition when condition.Right is not null => [condition.Left, condition.Right],
            WorkflowConditionStep condition => [condition.Left],
            WorkflowTransformStep transform => transform.Operands,
            WorkflowOutputStep output => [output.Value],
            _ => [],
        };
        foreach (var value in values)
            foreach (var binding in EnumerateBindings(value))
                yield return binding;
    }

    private static IEnumerable<WorkflowBindingValue> EnumerateBindings(WorkflowValue value)
    {
        switch (value)
        {
            case WorkflowBindingValue binding:
                yield return binding;
                break;
            case WorkflowArrayValue array:
                foreach (var item in array.Items)
                    foreach (var binding in EnumerateBindings(item))
                        yield return binding;
                break;
            case WorkflowObjectValue valueObject:
                foreach (var item in valueObject.Properties.Values)
                    foreach (var binding in EnumerateBindings(item))
                        yield return binding;
                break;
        }
    }

    private static bool ProducesBindableValue(WorkflowStep step) =>
        step is WorkflowInputMapStep or WorkflowToolCallStep or WorkflowTransformStep;

    private static bool HasValidTransformArity(WorkflowTransformStep transform) =>
        transform.Operation == "concat"
            ? transform.Operands.Count is >= 1 and <= 16
            : transform.Operands.Count == 1;

    private static WorkflowContractSchema? TransformResultSchema(WorkflowTransformStep transform) =>
        transform.Operation switch
        {
            "concat" or "trim" or "lowerInvariant" or "upperInvariant" => WorkflowContractSchema.String(),
            "toNumber" => WorkflowContractSchema.Scalar("number"),
            "toDate" => WorkflowContractSchema.String("date-time"),
            _ => null,
        };

    private static bool TransformOperandsAreValid(
        WorkflowTransformStep transform,
        int consumer,
        Func<WorkflowBindingValue, int, (WorkflowContractSchema? Schema, WorkflowValidationIssue? Issue, bool Guaranteed)> resolveBinding)
    {
        if (TransformResultSchema(transform) is null) return false;
        var expected = WorkflowContractSchema.String();
        foreach (var operand in transform.Operands)
        {
            var shape = WorkflowContractSchema.Infer(
                operand,
                binding => resolveBinding(binding, consumer).Schema);
            if (shape is null || !expected.AcceptsSource(shape)) return false;
            if (operand is WorkflowStringValue literal && transform.Operation == "toNumber" &&
                !WorkflowDefinitionParser.TryCanonicalizeRuntimeNumber(literal.Value, out _))
                return false;
            if (operand is WorkflowStringValue date && transform.Operation == "toDate" &&
                !WorkflowInterpreter.TryNormalizeDate(date.Value, out _))
                return false;
        }
        return true;
    }

    private static bool ConditionOperandsAreValid(
        WorkflowConditionStep condition,
        int consumer,
        Func<WorkflowBindingValue, int, (WorkflowContractSchema? Schema, WorkflowValidationIssue? Issue, bool Guaranteed)> resolveBinding)
    {
        var left = WorkflowContractSchema.Infer(
            condition.Left,
            binding => resolveBinding(binding, consumer).Schema);
        if (left is null) return false;
        if (condition.Operator == "exists") return condition.Right is null;
        if (condition.Operator is "isTrue" or "isFalse")
            return condition.Right is null && left.Type == "boolean";
        if (condition.Operator is not ("equals" or "notEquals") || condition.Right is null) return false;
        var right = WorkflowContractSchema.Infer(
            condition.Right,
            binding => resolveBinding(binding, consumer).Schema);
        return right is not null && left.IsComparableWith(right);
    }

    private static bool IsForbiddenTool(string name) =>
        ForbiddenTools.Contains(name) ||
        name.StartsWith("capability.", StringComparison.Ordinal) ||
        name.StartsWith("workflow.", StringComparison.Ordinal) ||
        name.StartsWith("subagent.", StringComparison.Ordinal) ||
        name.StartsWith("mcp.", StringComparison.Ordinal) ||
        name.StartsWith("agent_job.", StringComparison.Ordinal) ||
        name.StartsWith("shell.", StringComparison.Ordinal) ||
        name.StartsWith("process.", StringComparison.Ordinal) ||
        name.StartsWith("command.", StringComparison.Ordinal);

    private static bool IsIdentifier(string value) =>
        value.Length is >= 1 and <= 64 && value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' ||
                               char.IsAsciiDigit(character) || character is '_' or '-');

    private static bool HasValidAstInvariants(WorkflowStep step)
    {
        var expectedKind = step switch
        {
            WorkflowInputMapStep => WorkflowStepKind.InputMap,
            WorkflowToolCallStep => WorkflowStepKind.ToolCall,
            WorkflowConditionStep => WorkflowStepKind.Condition,
            WorkflowTransformStep => WorkflowStepKind.Transform,
            WorkflowNeedsInformationStep => WorkflowStepKind.NeedsInformation,
            WorkflowOutputStep => WorkflowStepKind.Output,
            _ => (WorkflowStepKind)(-1),
        };
        if (step.Kind != expectedKind) return false;
        return step switch
        {
            WorkflowInputMapStep inputMap => HasValidValue(inputMap.Value),
            WorkflowToolCallStep toolCall => HasValidValue(toolCall.Arguments),
            WorkflowConditionStep condition => HasValidValue(condition.Left) &&
                                               (condition.Right is null || HasValidValue(condition.Right)),
            WorkflowTransformStep transform => transform.Operands.All(HasValidValue),
            WorkflowNeedsInformationStep needsInformation =>
                needsInformation.RequiredInputs.Count is >= 1 and <= 16,
            WorkflowOutputStep output => HasValidValue(output.Value),
            _ => false,
        };
    }

    private static bool HasValidValue(WorkflowValue value) => value switch
    {
        WorkflowNullValue => true,
        WorkflowBooleanValue => true,
        WorkflowNumberValue number => HasCanonicalNumber(number),
        WorkflowStringValue text => text.Value.Length <= WorkflowDefinitionParser.MaximumStringCharacters &&
                                    !text.Value.EnumerateRunes().Any(rune =>
                                        Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format ||
                                        Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control &&
                                        rune.Value is not ('\t' or '\n' or '\r')),
        WorkflowArrayValue array => array.Items.Count <= WorkflowDefinitionParser.MaximumArrayItems &&
                                    array.Items.All(HasValidValue),
        WorkflowObjectValue valueObject => valueObject.Properties.Count <= WorkflowDefinitionParser.MaximumObjectProperties &&
                                           valueObject.Properties.All(pair =>
                                               pair.Key.Length is >= 1 and <= 80 && pair.Key[0] != '$' &&
                                               HasValidValue(pair.Value)),
        WorkflowBindingValue binding => binding.Source switch
        {
            WorkflowBindingSource.Input => binding.StepId is null && binding.VariableName is null,
            WorkflowBindingSource.Step => binding.StepId is not null && binding.VariableName is null,
            WorkflowBindingSource.Variable => binding.StepId is null && binding.VariableName is not null,
            _ => false,
        },
        _ => false,
    };

    private static bool HasCanonicalNumber(WorkflowNumberValue number)
    {
        if (number.IsInteger)
        {
            return long.TryParse(number.CanonicalValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) &&
                   integer is >= -9_007_199_254_740_991 and <= 9_007_199_254_740_991 &&
                   StringComparer.Ordinal.Equals(integer.ToString(CultureInfo.InvariantCulture), number.CanonicalValue);
        }
        return double.TryParse(number.CanonicalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
               double.IsFinite(value) && value != 0 && Math.Truncate(value) != value &&
               StringComparer.Ordinal.Equals(value.ToString("R", CultureInfo.InvariantCulture), number.CanonicalValue);
    }

    private static bool IsRisk(string value) => value is "R0" or "R1" or "R2" or "R3" or "R4";

    private static int RiskOrdinal(string risk) => risk switch
    {
        "R0" => 0,
        "R1" => 1,
        "R2" => 2,
        "R3" => 3,
        "R4" => 4,
        _ => int.MaxValue,
    };

    private static string ComputeContractHash(string canonicalJson) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant()}";

    private static WorkflowStaticValidationResult Failure(
        string code,
        string path,
        string? stepId = null) =>
        new(null, [new WorkflowValidationIssue(code, path, stepId)]);

}
