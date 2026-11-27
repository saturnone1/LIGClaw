using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LIGClaw.Domain;

namespace LIGClaw.Application.Workflows;

public sealed record WorkflowDefinitionHash
{
    private WorkflowDefinitionHash(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static WorkflowDefinitionHash Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            value.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
            throw new FormatException("Workflow definition hash가 올바르지 않습니다.");
        return new WorkflowDefinitionHash(value);
    }
}

public sealed record WorkflowPlanHash
{
    private WorkflowPlanHash(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static WorkflowPlanHash Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            value.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
            throw new FormatException("Workflow plan hash가 올바르지 않습니다.");
        return new WorkflowPlanHash(value);
    }
}

public sealed record ValidatedWorkflowPlanStep
{
    private readonly IReadOnlyList<int> _successorOrdinals;

    public ValidatedWorkflowPlanStep(
        int ordinal,
        string id,
        WorkflowStepKind kind,
        IReadOnlyList<int> successorOrdinals,
        string? canonicalToolName = null,
        string? canonicalRisk = null,
        int maximumRetries = 0,
        WorkflowToolPreparationMode? preparationMode = null,
        WorkflowRetryClass? retryClass = null,
        WorkflowToolEffectClass? effectClass = null,
        string? inputContractHash = null,
        string? resultContractHash = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(successorOrdinals);
        Ordinal = ordinal;
        Id = id;
        Kind = kind;
        _successorOrdinals = Array.AsReadOnly(successorOrdinals.ToArray());
        CanonicalToolName = canonicalToolName;
        CanonicalRisk = canonicalRisk;
        MaximumRetries = maximumRetries;
        PreparationMode = preparationMode;
        RetryClass = retryClass;
        EffectClass = effectClass;
        InputContractHash = inputContractHash;
        ResultContractHash = resultContractHash;
    }

    public int Ordinal { get; }
    public string Id { get; }
    public WorkflowStepKind Kind { get; }
    public IReadOnlyList<int> SuccessorOrdinals => _successorOrdinals;
    public string? CanonicalToolName { get; }
    public string? CanonicalRisk { get; }
    public int MaximumRetries { get; }
    public WorkflowToolPreparationMode? PreparationMode { get; }
    public WorkflowRetryClass? RetryClass { get; }
    public WorkflowToolEffectClass? EffectClass { get; }
    public string? InputContractHash { get; }
    public string? ResultContractHash { get; }
}

public sealed record ValidatedWorkflowPlan
{
    private readonly IReadOnlyList<ValidatedWorkflowPlanStep> _steps;
    private readonly IReadOnlyList<string> _requiredTools;

    public ValidatedWorkflowPlan(
        WorkflowDefinition definition,
        WorkflowDefinitionHash definitionHash,
        string inputContractHash,
        IReadOnlyList<ValidatedWorkflowPlanStep> steps,
        IReadOnlyList<string> requiredTools,
        string maximumRisk,
        WorkflowPlanHash planHash)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definitionHash);
        ArgumentNullException.ThrowIfNull(inputContractHash);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(requiredTools);
        ArgumentNullException.ThrowIfNull(maximumRisk);
        ArgumentNullException.ThrowIfNull(planHash);
        Definition = definition;
        DefinitionHash = definitionHash;
        InputContractHash = inputContractHash;
        _steps = Array.AsReadOnly(steps.ToArray());
        _requiredTools = Array.AsReadOnly(requiredTools.ToArray());
        MaximumRisk = maximumRisk;
        PlanHash = planHash;
    }

    public WorkflowDefinition Definition { get; }
    public WorkflowDefinitionHash DefinitionHash { get; }
    public string InputContractHash { get; }
    public IReadOnlyList<ValidatedWorkflowPlanStep> Steps => _steps;
    public IReadOnlyList<string> RequiredTools => _requiredTools;
    public string MaximumRisk { get; }
    public WorkflowPlanHash PlanHash { get; }
}

public static class WorkflowPlanHasher
{
    public static WorkflowDefinitionHash ComputeDefinitionHash(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "format", "workflow-definition-v1");
        Append(hash, "schemaVersion", definition.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        Append(hash, "entryStepId", definition.EntryStepId);
        Append(hash, "stepCount", definition.Steps.Count.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < definition.Steps.Count; index++)
            AppendStep(hash, $"steps[{index}]", definition.Steps[index]);
        return WorkflowDefinitionHash.Parse(
            $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}");
    }

    public static WorkflowPlanHash ComputePlanHash(
        WorkflowDefinitionHash definitionHash,
        string inputContractHash,
        IReadOnlyList<ValidatedWorkflowPlanStep> steps,
        IReadOnlyList<string> requiredTools,
        string maximumRisk)
    {
        ArgumentNullException.ThrowIfNull(definitionHash);
        ArgumentNullException.ThrowIfNull(inputContractHash);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(requiredTools);
        ArgumentNullException.ThrowIfNull(maximumRisk);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "format", "workflow-static-plan-v1");
        Append(hash, "definitionHash", definitionHash.Value);
        Append(hash, "inputContractHash", inputContractHash);
        Append(hash, "maximumRisk", maximumRisk);
        Append(hash, "requiredToolCount", requiredTools.Count.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < requiredTools.Count; index++)
            Append(hash, $"requiredTools[{index}]", requiredTools[index]);
        Append(hash, "stepCount", steps.Count.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var prefix = $"steps[{index}]";
            Append(hash, $"{prefix}.ordinal", step.Ordinal.ToString(CultureInfo.InvariantCulture));
            Append(hash, $"{prefix}.id", step.Id);
            Append(hash, $"{prefix}.kind", step.Kind.ToString());
            Append(hash, $"{prefix}.tool", step.CanonicalToolName ?? string.Empty);
            Append(hash, $"{prefix}.risk", step.CanonicalRisk ?? string.Empty);
            Append(hash, $"{prefix}.maximumRetries", step.MaximumRetries.ToString(CultureInfo.InvariantCulture));
            Append(hash, $"{prefix}.preparationMode", step.PreparationMode?.ToString() ?? string.Empty);
            Append(hash, $"{prefix}.retryClass", step.RetryClass?.ToString() ?? string.Empty);
            Append(hash, $"{prefix}.effectClass", step.EffectClass?.ToString() ?? string.Empty);
            Append(hash, $"{prefix}.inputContractHash", step.InputContractHash ?? string.Empty);
            Append(hash, $"{prefix}.resultContractHash", step.ResultContractHash ?? string.Empty);
            Append(hash, $"{prefix}.successorCount", step.SuccessorOrdinals.Count.ToString(CultureInfo.InvariantCulture));
            for (var successor = 0; successor < step.SuccessorOrdinals.Count; successor++)
                Append(
                    hash,
                    $"{prefix}.successors[{successor}]",
                    step.SuccessorOrdinals[successor].ToString(CultureInfo.InvariantCulture));
        }
        return WorkflowPlanHash.Parse(
            $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}");
    }

    private static void AppendStep(IncrementalHash hash, string prefix, WorkflowStep step)
    {
        Append(hash, $"{prefix}.id", step.Id);
        Append(hash, $"{prefix}.kind", step.Kind.ToString());
        switch (step)
        {
            case WorkflowInputMapStep inputMap:
                AppendValue(hash, $"{prefix}.value", inputMap.Value);
                Append(hash, $"{prefix}.saveAs", inputMap.SaveAs);
                Append(hash, $"{prefix}.next", inputMap.Next);
                break;
            case WorkflowToolCallStep toolCall:
                Append(hash, $"{prefix}.tool", toolCall.Tool);
                AppendValue(hash, $"{prefix}.arguments", toolCall.Arguments);
                Append(hash, $"{prefix}.maximumRetries", toolCall.MaximumRetries.ToString(CultureInfo.InvariantCulture));
                Append(hash, $"{prefix}.next", toolCall.Next);
                break;
            case WorkflowConditionStep condition:
                AppendValue(hash, $"{prefix}.left", condition.Left);
                Append(hash, $"{prefix}.operator", condition.Operator);
                Append(hash, $"{prefix}.hasRight", (condition.Right is not null).ToString(CultureInfo.InvariantCulture));
                if (condition.Right is not null) AppendValue(hash, $"{prefix}.right", condition.Right);
                Append(hash, $"{prefix}.then", condition.ThenStepId);
                Append(hash, $"{prefix}.else", condition.ElseStepId);
                break;
            case WorkflowTransformStep transform:
                Append(hash, $"{prefix}.operation", transform.Operation);
                Append(hash, $"{prefix}.operandCount", transform.Operands.Count.ToString(CultureInfo.InvariantCulture));
                for (var index = 0; index < transform.Operands.Count; index++)
                    AppendValue(hash, $"{prefix}.operands[{index}]", transform.Operands[index]);
                Append(hash, $"{prefix}.saveAs", transform.SaveAs);
                Append(hash, $"{prefix}.next", transform.Next);
                break;
            case WorkflowNeedsInformationStep needsInformation:
                Append(
                    hash,
                    $"{prefix}.requiredInputCount",
                    needsInformation.RequiredInputs.Count.ToString(CultureInfo.InvariantCulture));
                for (var index = 0; index < needsInformation.RequiredInputs.Count; index++)
                    Append(hash, $"{prefix}.requiredInputs[{index}]", needsInformation.RequiredInputs[index]);
                break;
            case WorkflowOutputStep output:
                AppendValue(hash, $"{prefix}.value", output.Value);
                break;
            default:
                throw new InvalidOperationException("지원하지 않는 Workflow step입니다.");
        }
    }

    private static void AppendValue(IncrementalHash hash, string prefix, WorkflowValue value)
    {
        switch (value)
        {
            case WorkflowNullValue:
                Append(hash, $"{prefix}.kind", "null");
                break;
            case WorkflowBooleanValue boolean:
                Append(hash, $"{prefix}.kind", "boolean");
                Append(hash, $"{prefix}.value", boolean.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case WorkflowNumberValue number:
                Append(hash, $"{prefix}.kind", number.IsInteger ? "integer" : "number");
                Append(hash, $"{prefix}.value", number.CanonicalValue);
                break;
            case WorkflowStringValue text:
                Append(hash, $"{prefix}.kind", "string");
                Append(hash, $"{prefix}.value", text.Value);
                break;
            case WorkflowArrayValue array:
                Append(hash, $"{prefix}.kind", "array");
                Append(hash, $"{prefix}.count", array.Items.Count.ToString(CultureInfo.InvariantCulture));
                for (var index = 0; index < array.Items.Count; index++)
                    AppendValue(hash, $"{prefix}[{index}]", array.Items[index]);
                break;
            case WorkflowObjectValue valueObject:
                Append(hash, $"{prefix}.kind", "object");
                var properties = valueObject.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
                Append(hash, $"{prefix}.count", properties.Length.ToString(CultureInfo.InvariantCulture));
                for (var index = 0; index < properties.Length; index++)
                {
                    Append(hash, $"{prefix}.properties[{index}].name", properties[index].Key);
                    AppendValue(hash, $"{prefix}.properties[{index}].value", properties[index].Value);
                }
                break;
            case WorkflowBindingValue binding:
                Append(hash, $"{prefix}.kind", "binding");
                Append(hash, $"{prefix}.source", binding.Source.ToString());
                Append(hash, $"{prefix}.stepId", binding.StepId ?? string.Empty);
                Append(hash, $"{prefix}.variableName", binding.VariableName ?? string.Empty);
                Append(hash, $"{prefix}.jsonPointer", binding.JsonPointer);
                break;
            default:
                throw new InvalidOperationException("지원하지 않는 Workflow value입니다.");
        }
    }

    private static void Append(IncrementalHash hash, string name, string value)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        Span<byte> framing = stackalloc byte[12];
        BinaryPrimitives.WriteInt32BigEndian(framing, nameBytes.Length);
        BinaryPrimitives.WriteInt64BigEndian(framing[4..], valueBytes.LongLength);
        hash.AppendData(framing);
        hash.AppendData(nameBytes);
        hash.AppendData(valueBytes);
    }
}
