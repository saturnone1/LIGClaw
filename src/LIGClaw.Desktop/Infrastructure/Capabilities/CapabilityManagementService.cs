using System.IO;
using LIGClaw.Application.Capabilities;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Capabilities;

internal sealed class CapabilityManagementService(
    ICapabilityCatalogService catalog,
    ILigcapPackageCodec codec) : ICapabilityManagementService
{
    private const string ImportSourceKind = "local_import";

    public async Task<IReadOnlyList<CapabilityManagementItem>> ListAsync(
        CapabilityManagementFilter filter,
        CapabilityManagementSort sort,
        CancellationToken cancellationToken)
    {
        var summaries = await catalog.ListAsync(null, 0, 100, cancellationToken).ConfigureAwait(false);
        IEnumerable<CapabilityCatalogSummary> filtered = filter switch
        {
            CapabilityManagementFilter.ReadyForReview => summaries.Where(item =>
                item.LatestState == CapabilityDraftState.ReadyForReview),
            CapabilityManagementFilter.Active => summaries.Where(item => item.ActiveVersion is not null),
            CapabilityManagementFilter.Inactive => summaries.Where(item => item.ActiveVersion is null),
            _ => summaries,
        };
        filtered = sort switch
        {
            CapabilityManagementSort.Name => filtered
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Id.Value, StringComparer.Ordinal),
            _ => filtered
                .OrderByDescending(item => item.UpdatedAtUtc)
                .ThenBy(item => item.Id.Value, StringComparer.Ordinal),
        };
        return filtered.Select(summary => new CapabilityManagementItem(
                summary,
                CreateDraftId(summary.LatestVersion.ContentHash),
                CanReview: true,
                CanExport: summary.ActiveVersion is not null,
                CanRollback: summary.ActiveVersion is not null && summary.CatalogRevision > 1))
            .ToArray();
    }

    public async Task<CapabilityImportResult> ImportPackageAsync(
        string packagePath,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (!TryResolveImportPath(packagePath, out var fullPath))
            return InvalidImport("capability.import.path_invalid");

        try
        {
            if (HasReparsePoint(fullPath!, includeTarget: true))
                return InvalidImport("capability.import.reparse_not_allowed");

            await using var source = new FileStream(
                fullPath!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length is < 1 or > LigcapPackageSecurity.MaximumArchiveBytes)
                return InvalidImport("capability.import.size_invalid");
            if ((File.GetAttributes(fullPath!) & FileAttributes.ReparsePoint) != 0)
                return InvalidImport("capability.import.reparse_not_allowed");

            var package = await codec.ReadAsync(source, cancellationToken).ConfigureAwait(false);
            if (!package.Succeeded || package.Document is null ||
                package.ContentHash is null || package.SourcePackageHash is null)
            {
                var issue = package.Issues.FirstOrDefault()?.Code ?? "capability.import.package_invalid";
                return InvalidImport(issue);
            }

            var exactVersion = new CapabilityVersionRef(
                package.Document.Payload.Manifest.Id,
                package.Document.Payload.Manifest.Version,
                package.ContentHash);
            var draftId = CreateDraftId(package.ContentHash);
            var existingDraft = await catalog.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
            if (existingDraft is not null && existingDraft.ExactVersion.Equals(exactVersion))
                return new(CapabilityManagementOutcome.Duplicate, existingDraft, "capability.draft.duplicate");

            var current = await catalog.GetCatalogAsync(exactVersion.Id, cancellationToken).ConfigureAwait(false);
            var created = await catalog.SubmitValidatedDraftAsync(
                new ValidatedCapabilityDraft(
                    draftId,
                    package.Document.Payload,
                    package.ContentHash,
                    current?.ActiveVersion,
                    ImportSourceKind,
                    package.SourcePackageHash),
                nowUtc,
                cancellationToken).ConfigureAwait(false);
            return new CapabilityImportResult(
                Map(created.Outcome),
                created.Draft,
                created.ErrorCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return new(CapabilityManagementOutcome.IoFailure, ErrorCode: "capability.import.io_failed");
        }
    }

    public async Task<CapabilityReviewResult> GetActivationReviewAsync(
        string draftId,
        CapabilityVersionRef expectedVersion,
        long expectedCatalogRevision,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(draftId) || expectedCatalogRevision < 0)
            return InvalidReview("capability.review.identity_invalid");
        var draft = await catalog.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (draft is null)
            return new(CapabilityManagementOutcome.NotFound, ErrorCode: "capability.draft.not_found");
        var current = await catalog.GetCatalogAsync(draft.ExactVersion.Id, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return new(CapabilityManagementOutcome.NotFound, ErrorCode: "capability.catalog.not_found");
        if (!draft.ExactVersion.Equals(expectedVersion) || current.CatalogRevision != expectedCatalogRevision)
            return new(CapabilityManagementOutcome.Stale, ErrorCode: "capability.review.stale");
        return new(CapabilityManagementOutcome.Succeeded, new CapabilityActivationReview(draft, current));
    }

    public async Task<CapabilityManagementMutationResult> ActivateAsync(
        CapabilityActivationReview review,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(review);
        var draft = await catalog.GetDraftAsync(review.Draft.DraftId, cancellationToken).ConfigureAwait(false);
        var current = await catalog.GetCatalogAsync(review.Draft.ExactVersion.Id, cancellationToken)
            .ConfigureAwait(false);
        if (draft is null || current is null)
            return new(CapabilityManagementOutcome.NotFound, ErrorCode: "capability.activation.review_not_found");
        if (!draft.ExactVersion.Equals(review.Draft.ExactVersion) ||
            draft.Revision != review.Draft.Revision ||
            current.CatalogRevision != review.Catalog.CatalogRevision ||
            !Equals(current.ActiveVersion, review.Catalog.ActiveVersion) ||
            draft.State != CapabilityDraftState.ReadyForReview ||
            !Equals(draft.Parent, current.ActiveVersion))
            return new(CapabilityManagementOutcome.Stale, current, "capability.activation.review_stale");

        var result = await catalog.ApproveAndActivateAsync(
            new CapabilityApprovalCommand(
                draft.DraftId,
                review.Draft.ExactVersion,
                draft.Revision,
                current.CatalogRevision,
                current.ActiveVersion,
                "ui.review.activate",
                nowUtc),
            cancellationToken).ConfigureAwait(false);
        return new(Map(result.Outcome), result.Catalog, result.ErrorCode);
    }

    public async Task<CapabilityRollbackReviewResult> GetRollbackReviewAsync(
        CapabilityId id,
        CapabilityVersionRef expectedActiveVersion,
        long expectedCatalogRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(expectedActiveVersion);
        var current = await catalog.GetCatalogAsync(id, cancellationToken).ConfigureAwait(false);
        if (current?.ActiveVersion is null)
            return new(CapabilityManagementOutcome.NotFound, ErrorCode: "capability.rollback.active_not_found");
        if (current.CatalogRevision != expectedCatalogRevision ||
            !current.ActiveVersion.Equals(expectedActiveVersion))
            return new(CapabilityManagementOutcome.Stale, ErrorCode: "capability.rollback.review_stale");

        var activations = await catalog.ListActivationsAsync(id, 100, cancellationToken).ConfigureAwait(false);
        var previous = activations.FirstOrDefault(activation => activation.To.Equals(current.ActiveVersion))?.From;
        if (previous is null)
            return new(CapabilityManagementOutcome.NotFound, ErrorCode: "capability.rollback.target_unavailable");
        var currentVersion = await catalog.GetVersionAsync(current.ActiveVersion, cancellationToken).ConfigureAwait(false);
        var targetVersion = await catalog.GetVersionAsync(previous, cancellationToken).ConfigureAwait(false);
        if (currentVersion is null || targetVersion is null)
            return new(CapabilityManagementOutcome.NotFound, ErrorCode: "capability.rollback.target_unavailable");
        return new(
            CapabilityManagementOutcome.Succeeded,
            new CapabilityRollbackReview(current, currentVersion, targetVersion));
    }

    public async Task<CapabilityManagementMutationResult> RollbackAsync(
        CapabilityRollbackReview review,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(review);
        var current = await catalog.GetCatalogAsync(review.Catalog.Id, cancellationToken).ConfigureAwait(false);
        if (current?.ActiveVersion is null)
            return new(CapabilityManagementOutcome.NotFound, ErrorCode: "capability.rollback.active_not_found");
        if (current.CatalogRevision != review.Catalog.CatalogRevision ||
            !current.ActiveVersion.Equals(review.CurrentVersion.ExactVersion))
            return new(CapabilityManagementOutcome.Stale, current, "capability.rollback.review_stale");

        var result = await catalog.RollbackAsync(
            new CapabilityRollbackCommand(
                current.Id,
                current.CatalogRevision,
                current.ActiveVersion,
                review.TargetVersion.ExactVersion,
                "ui.review.rollback",
                nowUtc),
            cancellationToken).ConfigureAwait(false);
        return new(Map(result.Outcome), result.Catalog, result.ErrorCode);
    }

    public async Task<CapabilityManagementMutationResult> ExportVersionAsync(
        CapabilityVersionRef exactVersion,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exactVersion);
        if (!TryResolveExportPath(destinationPath, out var fullPath))
            return new(CapabilityManagementOutcome.Invalid, ErrorCode: "capability.export.path_invalid");

        string? temporaryPath = null;
        try
        {
            var version = await catalog.GetVersionAsync(exactVersion, cancellationToken).ConfigureAwait(false);
            if (version is null)
                return new(CapabilityManagementOutcome.NotFound, ErrorCode: "capability.export.version_not_found");
            if (HasReparsePoint(fullPath!, includeTarget: File.Exists(fullPath)))
                return new(CapabilityManagementOutcome.Invalid, ErrorCode: "capability.export.reparse_not_allowed");

            var directory = Path.GetDirectoryName(fullPath!)!;
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await codec.WriteAsync(
                    destination,
                    new CapabilityPackageDocument(version.Payload.Manifest, version.Payload, version.Parent),
                    cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath) && (File.GetAttributes(fullPath!) & FileAttributes.ReparsePoint) != 0)
                return new(CapabilityManagementOutcome.Invalid, ErrorCode: "capability.export.reparse_not_allowed");
            File.Move(temporaryPath, fullPath!, overwrite: true);
            temporaryPath = null;
            return new(CapabilityManagementOutcome.Succeeded);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return new(CapabilityManagementOutcome.IoFailure, ErrorCode: "capability.export.io_failed");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static CapabilityImportResult InvalidImport(string errorCode) =>
        new(CapabilityManagementOutcome.Invalid, ErrorCode: errorCode);

    private static CapabilityReviewResult InvalidReview(string errorCode) =>
        new(CapabilityManagementOutcome.Invalid, ErrorCode: errorCode);

    private static CapabilityManagementOutcome Map(CapabilityMutationOutcome outcome) => outcome switch
    {
        CapabilityMutationOutcome.Succeeded => CapabilityManagementOutcome.Succeeded,
        CapabilityMutationOutcome.Duplicate => CapabilityManagementOutcome.Duplicate,
        CapabilityMutationOutcome.NotFound => CapabilityManagementOutcome.NotFound,
        CapabilityMutationOutcome.NotReady => CapabilityManagementOutcome.NotReady,
        CapabilityMutationOutcome.Stale => CapabilityManagementOutcome.Stale,
        CapabilityMutationOutcome.Unsupported => CapabilityManagementOutcome.Unsupported,
        _ => CapabilityManagementOutcome.Invalid,
    };

    private static string CreateDraftId(CapabilityContentHash contentHash)
    {
        const string prefix = "sha256:";
        var hash = contentHash.Value.StartsWith(prefix, StringComparison.Ordinal)
            ? contentHash.Value[prefix.Length..]
            : contentHash.Value;
        return $"ligcap_{hash[..Math.Min(56, hash.Length)]}";
    }

    private static bool TryResolveImportPath(string value, out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(Path.GetExtension(value), ".ligcap", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            fullPath = Path.GetFullPath(value);
            return File.Exists(fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryResolveExportPath(string value, out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(Path.GetExtension(value), ".ligcap", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            fullPath = Path.GetFullPath(value);
            var directory = Path.GetDirectoryName(fullPath);
            return !string.IsNullOrEmpty(directory) && Directory.Exists(directory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool HasReparsePoint(string path, bool includeTarget)
    {
        FileSystemInfo? current = includeTarget
            ? new FileInfo(path)
            : Directory.GetParent(path);
        while (current is not null)
        {
            current.Refresh();
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        }
        return false;
    }

    private static bool IsFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}

internal sealed class CapabilityToolDependencyCatalog(
    IEnumerable<CapabilityToolDependency> dependencies,
    Func<string, bool>? isAvailable = null) : ICapabilityDependencyCatalog
{
    private readonly Func<string, bool> _isAvailable = isAvailable ?? (_ => true);
    private readonly IReadOnlyDictionary<string, CapabilityToolDependency> _dependencies = dependencies
        .GroupBy(item => item.Name, StringComparer.Ordinal)
        .ToDictionary(
            group => group.Key,
            group => group.OrderByDescending(item => item.Risk, StringComparer.Ordinal).First(),
            StringComparer.Ordinal);

    public bool TryGetTool(string name, out CapabilityToolDependency? dependency)
    {
        if (_isAvailable(name)) return _dependencies.TryGetValue(name, out dependency);
        dependency = null;
        return false;
    }
}
