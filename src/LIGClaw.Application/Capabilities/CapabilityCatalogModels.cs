using LIGClaw.Domain;

namespace LIGClaw.Application.Capabilities;

public sealed record CapabilityPackageDocument(
    CapabilityManifest Manifest,
    CapabilityVersionPayload Payload,
    CapabilityVersionRef? Parent);

public sealed record CapabilityPackageIssue(string Code, string? Path = null);

public sealed record CapabilityPackageReadResult(
    CapabilityPackageDocument? Document,
    IReadOnlyList<CapabilityPackageIssue> Issues,
    CapabilityContentHash? ContentHash = null,
    CapabilityContentHash? SourcePackageHash = null)
{
    public bool Succeeded => Document is not null && Issues.Count == 0;
}
