namespace LIGClaw.Application.Workflows;

public enum WorkflowToolPreparationMode
{
    None,
    OneShot,
}

public enum WorkflowRetryClass
{
    Never,
    ReadOnly,
    Idempotent,
}

public enum WorkflowToolEffectClass
{
    Observation,
    Effect,
}

public sealed record WorkflowToolMetadata(
    string CanonicalName,
    string Risk,
    string CanonicalInputSchemaJson,
    string CanonicalResultSchemaJson,
    bool IsWorkflowAllowed,
    WorkflowToolPreparationMode PreparationMode,
    bool IsAvailable,
    WorkflowRetryClass RetryClass,
    WorkflowToolEffectClass EffectClass);

public interface IWorkflowToolMetadataCatalog
{
    bool TryGet(string canonicalName, out WorkflowToolMetadata? metadata);
}
