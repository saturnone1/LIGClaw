using LIGClaw.Application.Capabilities;
using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class CapabilityManagementTests
{
    [Fact]
    public void ActivationReviewRequiresReadyDraftBoundToTheExactActiveParent()
    {
        var id = CapabilityId.Parse("company.summary");
        var parent = VersionRef(id, "1.0.0", 'a');
        var exact = VersionRef(id, "2.0.0", 'b');
        var draft = new CapabilityDraft(
            "draft_1",
            exact,
            parent,
            Payload(id, exact.Version),
            CapabilityDraftState.ReadyForReview,
            0,
            "local_import",
            Hash('c'),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = new CapabilityCatalogEntry(
            id,
            parent,
            1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        Assert.True(new CapabilityActivationReview(draft, catalog).CanActivate);
        Assert.False(new CapabilityActivationReview(
            draft,
            catalog with { ActiveVersion = VersionRef(id, "1.1.0", 'd') }).CanActivate);
        Assert.False(new CapabilityActivationReview(
            draft with { State = CapabilityDraftState.Stale },
            catalog).CanActivate);
    }

    private static CapabilityVersionPayload Payload(CapabilityId id, CapabilitySemanticVersion version)
    {
        var manifest = new CapabilityManifest(
            1,
            id,
            version,
            "문서 요약",
            "문서를 요약합니다.",
            CapabilityKind.Prompt,
            ["요약해 줘"],
            [],
            [],
            "R0",
            "instructions.md");
        return new CapabilityVersionPayload(
            manifest,
            "{}",
            "핵심만 요약합니다.",
            "{\"type\":\"object\"}");
    }

    private static CapabilityVersionRef VersionRef(
        CapabilityId id,
        string version,
        char hash) => new(id, CapabilitySemanticVersion.Parse(version), Hash(hash));

    private static CapabilityContentHash Hash(char value) =>
        CapabilityContentHash.Parse($"sha256:{new string(value, 64)}");
}
