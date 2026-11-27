using LIGClaw.Application.Workflows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Infrastructure.Workflows;

internal sealed class DesktopWorkflowToolMetadataCatalog : IWorkflowToolMetadataCatalog
{
    private static readonly IReadOnlyDictionary<string, WorkflowAdmissionProfile> AdmissionProfiles =
        new Dictionary<string, WorkflowAdmissionProfile>(StringComparer.Ordinal)
        {
            ["system.get_status.v1"] = new(
                WorkflowToolPreparationMode.None,
                WorkflowRetryClass.ReadOnly,
                WorkflowToolEffectClass.Observation),
            ["file.get_metadata.v1"] = new(
                WorkflowToolPreparationMode.None,
                WorkflowRetryClass.ReadOnly,
                WorkflowToolEffectClass.Observation),
            ["mail.create_draft.v1"] = new(
                WorkflowToolPreparationMode.None,
                WorkflowRetryClass.ReadOnly,
                WorkflowToolEffectClass.Observation,
                RequiresWorkflowOnlyObservation: true),
            ["system.show_notification.v1"] = new(
                WorkflowToolPreparationMode.OneShot,
                WorkflowRetryClass.Never,
                WorkflowToolEffectClass.Effect),
            ["connection.http.request.v1"] = new(
                WorkflowToolPreparationMode.OneShot,
                WorkflowRetryClass.Never,
                WorkflowToolEffectClass.Effect),
            ["mail.send.v1"] = new(
                WorkflowToolPreparationMode.OneShot,
                WorkflowRetryClass.Never,
                WorkflowToolEffectClass.Effect),
        };

    private readonly IReadOnlyDictionary<string, WindowsToolDescriptor> descriptors;

    public DesktopWorkflowToolMetadataCatalog(WindowsToolHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        descriptors = host.Descriptors
            .GroupBy(descriptor => descriptor.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(descriptor => descriptor.Priority).First(),
                StringComparer.Ordinal);
    }

    public bool TryGet(string canonicalName, out WorkflowToolMetadata? metadata)
    {
        metadata = null;
        if (!AdmissionProfiles.TryGetValue(canonicalName, out var profile) ||
            !ToolSchemaRegistry.All.TryGetValue(canonicalName, out var schemas))
            return false;
        var available = descriptors.TryGetValue(canonicalName, out var descriptor) &&
                        StringComparer.Ordinal.Equals(descriptor.Risk, ExpectedRisk(canonicalName)) &&
                        (!profile.RequiresWorkflowOnlyObservation || descriptor.WorkflowOnlyObservation) &&
                        (profile.PreparationMode != WorkflowToolPreparationMode.OneShot ||
                         descriptor.SupportsWorkflowOneShot);
        metadata = new WorkflowToolMetadata(
            canonicalName,
            descriptor?.Risk ?? ExpectedRisk(canonicalName),
            schemas.CanonicalInputSchemaJson,
            schemas.CanonicalResultSchemaJson,
            true,
            profile.PreparationMode,
            available,
            profile.RetryClass,
            profile.EffectClass);
        return true;
    }

    private static string ExpectedRisk(string canonicalName) => canonicalName switch
    {
        "system.get_status.v1" or "file.get_metadata.v1" or "mail.create_draft.v1" => "R0",
        "system.show_notification.v1" => "R1",
        "connection.http.request.v1" or "mail.send.v1" => "R3",
        _ => throw new InvalidOperationException("Workflow allowlist 밖의 Tool입니다."),
    };

    private sealed record WorkflowAdmissionProfile(
        WorkflowToolPreparationMode PreparationMode,
        WorkflowRetryClass RetryClass,
        WorkflowToolEffectClass EffectClass,
        bool RequiresWorkflowOnlyObservation = false);
}
