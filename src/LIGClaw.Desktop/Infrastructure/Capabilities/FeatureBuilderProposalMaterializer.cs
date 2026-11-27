using System.Text;
using LIGClaw.Application.Capabilities;

namespace LIGClaw.Desktop.Infrastructure.Capabilities;

internal static class FeatureBuilderProposalMaterializer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static PreparedCapabilityPayload Prepare(
        IReadOnlyList<FeatureBuilderRawProposalArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (artifacts.Count is < 1 or > LigcapPackageSecurity.MaximumEntries)
            throw new LigcapValidationException("feature_builder.proposal.artifact_count_invalid");

        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            var expectedMediaType = artifact.Path == LigcapPackageSecurity.InstructionsEntry
                ? "text/markdown"
                : "application/json";
            if (!IsKnownPath(artifact.Path) ||
                !StringComparer.Ordinal.Equals(artifact.MediaType, expectedMediaType) ||
                string.IsNullOrEmpty(artifact.Content))
            {
                throw new LigcapValidationException(
                    "feature_builder.proposal.artifact_invalid",
                    IsKnownPath(artifact.Path) ? artifact.Path : null);
            }
            if (!entries.TryAdd(artifact.Path, StrictUtf8.GetBytes(artifact.Content)))
                throw new LigcapValidationException(
                    "feature_builder.proposal.artifact_duplicate",
                    artifact.Path);
        }

        return CapabilityPayloadMaterializer.Prepare(entries);
    }

    private static bool IsKnownPath(string path) => path is
        LigcapPackageSecurity.ManifestEntry or
        LigcapPackageSecurity.InstructionsEntry or
        LigcapPackageSecurity.InputSchemaEntry or
        LigcapPackageSecurity.SettingsSchemaEntry or
        LigcapPackageSecurity.WorkflowEntry or
        LigcapPackageSecurity.TestsEntry;
}
