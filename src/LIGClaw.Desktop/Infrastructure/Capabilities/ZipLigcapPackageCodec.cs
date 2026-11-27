using System.IO;
using System.Text;
using LIGClaw.Application.Capabilities;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Capabilities;

internal sealed class ZipLigcapPackageCodec : ILigcapPackageCodec
{
    public async Task<CapabilityPackageReadResult> ReadAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            var archive = await LigcapPackageSecurity.ReadArchiveAsync(source, cancellationToken).ConfigureAwait(false);
            var prepared = CapabilityPayloadMaterializer.Prepare(archive.Entries);
            var contentHash = CapabilityContentHasher.Compute(prepared.Payload);
            var sourcePackageHash = CapabilityContentHash.Parse($"sha256:{archive.SourcePackageSha256}");
            return new CapabilityPackageReadResult(
                new CapabilityPackageDocument(prepared.Manifest, prepared.Payload, Parent: null),
                [],
                contentHash,
                sourcePackageHash);
        }
        catch (LigcapValidationException exception)
        {
            return Invalid(exception);
        }
    }

    public async Task WriteAsync(
        Stream destination,
        CapabilityPackageDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceEntries = CapabilityPayloadMaterializer.CreateSourceEntries(document.Payload);
        var prepared = CapabilityPayloadMaterializer.Prepare(sourceEntries);
        if (!CapabilityPayloadMaterializer.Equivalent(prepared.Manifest, document.Manifest) ||
            !CapabilityPayloadMaterializer.Equivalent(prepared.Manifest, document.Payload.Manifest))
        {
            throw new InvalidDataException("package.manifest.payload_mismatch");
        }

        var canonicalEntries = CapabilityPayloadMaterializer.CreateCanonicalEntries(prepared.Payload);
        var archive = await LigcapPackageSecurity.WriteArchiveAsync(canonicalEntries, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(archive, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CapabilityPackageReadResult Invalid(LigcapValidationException exception)
    {
        var safePath = exception.EntryName is LigcapPackageSecurity.ManifestEntry or
            LigcapPackageSecurity.InstructionsEntry or
            LigcapPackageSecurity.InputSchemaEntry or
            LigcapPackageSecurity.SettingsSchemaEntry or
            LigcapPackageSecurity.WorkflowEntry or
            LigcapPackageSecurity.TestsEntry
            ? exception.EntryName
            : null;
        return new CapabilityPackageReadResult(
            Document: null,
            [new CapabilityPackageIssue(exception.Code, safePath)],
            ContentHash: null,
            SourcePackageHash: null);
    }
}
