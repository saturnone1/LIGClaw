using LIGClaw.Domain;

namespace LIGClaw.Application.Capabilities;

public enum CapabilityManagementFilter
{
    All,
    ReadyForReview,
    Active,
    Inactive,
}

public enum CapabilityManagementSort
{
    RecentlyUpdated,
    Name,
}

public enum CapabilityManagementOutcome
{
    Succeeded,
    Duplicate,
    Invalid,
    NotFound,
    NotReady,
    Stale,
    Unsupported,
    IoFailure,
}

public sealed record CapabilityManagementItem(
    CapabilityCatalogSummary Summary,
    string DraftId,
    bool CanReview,
    bool CanExport,
    bool CanRollback);

public sealed record CapabilityActivationReview(
    CapabilityDraft Draft,
    CapabilityCatalogEntry Catalog)
{
    public bool CanActivate =>
        Draft.State == CapabilityDraftState.ReadyForReview &&
        Equals(Draft.Parent, Catalog.ActiveVersion);
}

public sealed record CapabilityRollbackReview(
    CapabilityCatalogEntry Catalog,
    CapabilityVersion CurrentVersion,
    CapabilityVersion TargetVersion);

public sealed record CapabilityImportResult(
    CapabilityManagementOutcome Outcome,
    CapabilityDraft? Draft = null,
    string? ErrorCode = null);

public sealed record CapabilityReviewResult(
    CapabilityManagementOutcome Outcome,
    CapabilityActivationReview? Review = null,
    string? ErrorCode = null);

public sealed record CapabilityRollbackReviewResult(
    CapabilityManagementOutcome Outcome,
    CapabilityRollbackReview? Review = null,
    string? ErrorCode = null);

public sealed record CapabilityManagementMutationResult(
    CapabilityManagementOutcome Outcome,
    CapabilityCatalogEntry? Catalog = null,
    string? ErrorCode = null);

public interface ICapabilityManagementService
{
    Task<IReadOnlyList<CapabilityManagementItem>> ListAsync(
        CapabilityManagementFilter filter,
        CapabilityManagementSort sort,
        CancellationToken cancellationToken);

    Task<CapabilityImportResult> ImportPackageAsync(
        string packagePath,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task<CapabilityReviewResult> GetActivationReviewAsync(
        string draftId,
        CapabilityVersionRef expectedVersion,
        long expectedCatalogRevision,
        CancellationToken cancellationToken);

    Task<CapabilityManagementMutationResult> ActivateAsync(
        CapabilityActivationReview review,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task<CapabilityRollbackReviewResult> GetRollbackReviewAsync(
        CapabilityId id,
        CapabilityVersionRef expectedActiveVersion,
        long expectedCatalogRevision,
        CancellationToken cancellationToken);

    Task<CapabilityManagementMutationResult> RollbackAsync(
        CapabilityRollbackReview review,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task<CapabilityManagementMutationResult> ExportVersionAsync(
        CapabilityVersionRef exactVersion,
        string destinationPath,
        CancellationToken cancellationToken);
}
