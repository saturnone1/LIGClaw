using System.Collections.ObjectModel;

namespace LIGClaw.Domain;

public enum WorkflowStepKind
{
    InputMap,
    ToolCall,
    Condition,
    Transform,
    NeedsInformation,
    Output,
}

public enum WorkflowBindingSource
{
    Input,
    Step,
    Variable,
}

public sealed record WorkflowDefinition
{
    private readonly IReadOnlyList<WorkflowStep> _steps;

    public WorkflowDefinition(
        int schemaVersion,
        string entryStepId,
        IReadOnlyList<WorkflowStep> steps)
    {
        ArgumentNullException.ThrowIfNull(entryStepId);
        ArgumentNullException.ThrowIfNull(steps);
        SchemaVersion = schemaVersion;
        EntryStepId = entryStepId;
        _steps = Array.AsReadOnly(steps.ToArray());
    }

    public int SchemaVersion { get; }
    public string EntryStepId { get; }
    public IReadOnlyList<WorkflowStep> Steps => _steps;
}

public abstract record WorkflowStep(string Id, WorkflowStepKind Kind);

public sealed record WorkflowInputMapStep : WorkflowStep
{
    public WorkflowInputMapStep(
        string id,
        WorkflowValue value,
        string saveAs,
        string next)
        : base(id, WorkflowStepKind.InputMap)
    {
        Value = value;
        SaveAs = saveAs;
        Next = next;
    }

    public WorkflowValue Value { get; }
    public string SaveAs { get; }
    public string Next { get; }
}

public sealed record WorkflowToolCallStep : WorkflowStep
{
    public WorkflowToolCallStep(
        string id,
        string tool,
        WorkflowObjectValue arguments,
        int maximumRetries,
        string next)
        : base(id, WorkflowStepKind.ToolCall)
    {
        Tool = tool;
        Arguments = arguments;
        MaximumRetries = maximumRetries;
        Next = next;
    }

    public string Tool { get; }
    public WorkflowObjectValue Arguments { get; }
    public int MaximumRetries { get; }
    public string Next { get; }
}

public sealed record WorkflowConditionStep : WorkflowStep
{
    public WorkflowConditionStep(
        string id,
        WorkflowValue left,
        string @operator,
        WorkflowValue? right,
        string thenStepId,
        string elseStepId)
        : base(id, WorkflowStepKind.Condition)
    {
        Left = left;
        Operator = @operator;
        Right = right;
        ThenStepId = thenStepId;
        ElseStepId = elseStepId;
    }

    public WorkflowValue Left { get; }
    public string Operator { get; }
    public WorkflowValue? Right { get; }
    public string ThenStepId { get; }
    public string ElseStepId { get; }
}

public sealed record WorkflowTransformStep : WorkflowStep
{
    private readonly IReadOnlyList<WorkflowValue> _operands;

    public WorkflowTransformStep(
        string id,
        string operation,
        IReadOnlyList<WorkflowValue> operands,
        string saveAs,
        string next)
        : base(id, WorkflowStepKind.Transform)
    {
        ArgumentNullException.ThrowIfNull(operands);
        Operation = operation;
        _operands = Array.AsReadOnly(operands.ToArray());
        SaveAs = saveAs;
        Next = next;
    }

    public string Operation { get; }
    public IReadOnlyList<WorkflowValue> Operands => _operands;
    public string SaveAs { get; }
    public string Next { get; }
}

public sealed record WorkflowNeedsInformationStep : WorkflowStep
{
    private readonly IReadOnlyList<string> _requiredInputs;

    public WorkflowNeedsInformationStep(
        string id,
        IReadOnlyList<string> requiredInputs)
        : base(id, WorkflowStepKind.NeedsInformation)
    {
        ArgumentNullException.ThrowIfNull(requiredInputs);
        _requiredInputs = Array.AsReadOnly(requiredInputs.ToArray());
    }

    public IReadOnlyList<string> RequiredInputs => _requiredInputs;
}

public sealed record WorkflowOutputStep(
    string Id,
    WorkflowValue Value) : WorkflowStep(Id, WorkflowStepKind.Output);

public abstract record WorkflowValue;

public sealed record WorkflowNullValue : WorkflowValue
{
    private WorkflowNullValue()
    {
    }

    public static WorkflowNullValue Instance { get; } = new();
}

public sealed record WorkflowBooleanValue(bool Value) : WorkflowValue;

public sealed record WorkflowNumberValue(
    string CanonicalValue,
    bool IsInteger) : WorkflowValue;

public sealed record WorkflowStringValue(string Value) : WorkflowValue;

public sealed record WorkflowArrayValue : WorkflowValue
{
    private readonly IReadOnlyList<WorkflowValue> _items;

    public WorkflowArrayValue(IReadOnlyList<WorkflowValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = Array.AsReadOnly(items.ToArray());
    }

    public IReadOnlyList<WorkflowValue> Items => _items;
}

public sealed record WorkflowObjectValue : WorkflowValue
{
    private readonly IReadOnlyDictionary<string, WorkflowValue> _properties;

    public WorkflowObjectValue(IReadOnlyDictionary<string, WorkflowValue> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        _properties = new ReadOnlyDictionary<string, WorkflowValue>(
            properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, WorkflowValue> Properties => _properties;
}

public sealed record WorkflowBindingValue(
    WorkflowBindingSource Source,
    string? StepId,
    string? VariableName,
    string JsonPointer) : WorkflowValue;
