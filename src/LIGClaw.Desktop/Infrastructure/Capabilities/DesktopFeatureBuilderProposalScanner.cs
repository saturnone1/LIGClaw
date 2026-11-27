using System.Text;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Workflows;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Capabilities;

internal sealed class DesktopFeatureBuilderProposalScanner(
    ICapabilityDependencyCatalog dependencies,
    IWorkflowToolMetadataCatalog? workflowTools = null) : IFeatureBuilderProposalScanner
{
    private readonly FeatureBuilderProposalScanner _scanner = new(dependencies, workflowTools);
    private readonly IWorkflowToolMetadataCatalog? _workflowTools = workflowTools;

    public FeatureBuilderProposalScanResult Scan(
        IReadOnlyList<FeatureBuilderRawProposalArtifact> artifacts,
        FeatureBuilderRevisionTarget? revisionTarget)
    {
        try
        {
            var prepared = FeatureBuilderProposalMaterializer.Prepare(artifacts);
            var canonicalArtifacts = CapabilityPayloadMaterializer
                .CreateCanonicalEntries(prepared.Payload)
                .Select(entry => new FeatureBuilderRawProposalArtifact(
                    entry.Key,
                    entry.Key == LigcapPackageSecurity.InstructionsEntry
                        ? "text/markdown"
                        : "application/json",
                    Encoding.UTF8.GetString(entry.Value)))
                .ToArray();
            var scanned = _scanner.Scan(canonicalArtifacts, revisionTarget);
            if (!scanned.IsValid || scanned.Candidate is null || scanned.CandidateContentHash is null)
                return scanned;
            if (!scanned.CandidateContentHash.Equals(CapabilityContentHasher.Compute(prepared.Payload)) ||
                !Equivalent(scanned.Candidate, prepared.Payload))
                return FeatureBuilderProposalScanResult.Invalid(
                    "feature_builder.proposal.materializer_mismatch");
            return new FeatureBuilderProposalScanResult(
                true,
                prepared.Payload,
                scanned.CandidateContentHash);
        }
        catch (LigcapValidationException exception)
        {
            var code = exception.Code.StartsWith("feature_builder.", StringComparison.Ordinal)
                ? exception.Code
                : $"feature_builder.proposal.{exception.Code.Replace(".", "_", StringComparison.Ordinal)}";
            return FeatureBuilderProposalScanResult.Invalid(code);
        }
        catch (Exception exception) when (exception is ArgumentException or EncoderFallbackException)
        {
            return FeatureBuilderProposalScanResult.Invalid(
                "feature_builder.proposal.artifact_invalid");
        }
    }

    internal bool TryPrepareNoEffectWorkflowSimulation(
        CapabilityVersionPayload payload,
        out ValidatedWorkflowPlan? plan,
        out IWorkflowToolMetadataCatalog? toolCatalog,
        out string errorCode)
    {
        plan = null;
        toolCatalog = null;
        errorCode = "feature_builder.semantic.workflow_required";
        if (_workflowTools is null || payload.Manifest.Kind != CapabilityKind.Workflow ||
            string.IsNullOrWhiteSpace(payload.CanonicalWorkflowJson))
            return false;
        var parsed = new WorkflowDefinitionParser().Parse(payload.CanonicalWorkflowJson);
        if (!parsed.Succeeded || parsed.Definition is null)
        {
            errorCode = "feature_builder.semantic.plan_invalid";
            return false;
        }
        var validated = new WorkflowStaticValidator(_workflowTools).Validate(
            parsed.Definition,
            payload.Manifest,
            payload.CanonicalInputSchemaJson);
        if (!validated.Succeeded || validated.Plan is null)
        {
            errorCode = "feature_builder.semantic.plan_invalid";
            return false;
        }
        if (validated.Plan.Steps.Any(step => step.CanonicalToolName is not null &&
                (step.EffectClass != WorkflowToolEffectClass.Observation ||
                 step.PreparationMode != WorkflowToolPreparationMode.None)))
        {
            errorCode = "feature_builder.semantic.effect_forbidden";
            return false;
        }
        plan = validated.Plan;
        toolCatalog = _workflowTools;
        errorCode = string.Empty;
        return true;
    }

    private static bool Equivalent(
        LIGClaw.Domain.CapabilityVersionPayload left,
        LIGClaw.Domain.CapabilityVersionPayload right) =>
        CapabilityPayloadMaterializer.Equivalent(left.Manifest, right.Manifest) &&
        StringComparer.Ordinal.Equals(left.CanonicalManifestJson, right.CanonicalManifestJson) &&
        StringComparer.Ordinal.Equals(left.Instructions, right.Instructions) &&
        StringComparer.Ordinal.Equals(left.CanonicalInputSchemaJson, right.CanonicalInputSchemaJson) &&
        StringComparer.Ordinal.Equals(left.CanonicalSettingsSchemaJson, right.CanonicalSettingsSchemaJson) &&
        StringComparer.Ordinal.Equals(left.CanonicalWorkflowJson, right.CanonicalWorkflowJson) &&
        StringComparer.Ordinal.Equals(left.CanonicalTestsJson, right.CanonicalTestsJson);
}
