using LIGClaw.Application.Connections;
using LIGClaw.Application.Workflows;
using LIGClaw.Domain;

namespace LIGClaw.Application.Capabilities;

public enum CapabilityMutationOutcome
{
    Succeeded,
    Invalid,
    Duplicate,
    NotFound,
    NotReady,
    Stale,
    Unsupported,
}

public sealed record ValidatedCapabilityDraft(
    string DraftId,
    CapabilityVersionPayload Payload,
    CapabilityContentHash ContentHash,
    CapabilityVersionRef? Parent,
    string SourceKind,
    CapabilityContentHash SourcePackageHash)
{
    public CapabilityVersionRef ExactVersion =>
        new(Payload.Manifest.Id, Payload.Manifest.Version, ContentHash);
}

public sealed record CapabilityDraftCreateResult(
    CapabilityMutationOutcome Outcome,
    CapabilityDraft? Draft = null,
    string? ErrorCode = null);

public sealed record CapabilityActivationResult(
    CapabilityMutationOutcome Outcome,
    CapabilityCatalogEntry? Catalog = null,
    CapabilityActivation? Activation = null,
    string? ErrorCode = null);

public sealed record CapabilityApprovalCommand(
    string DraftId,
    CapabilityVersionRef ExpectedVersion,
    long ExpectedDraftRevision,
    long ExpectedCatalogRevision,
    CapabilityVersionRef? ExpectedActiveVersion,
    string ReasonCode,
    DateTimeOffset NowUtc);

public sealed record CapabilityRollbackCommand(
    CapabilityId Id,
    long ExpectedCatalogRevision,
    CapabilityVersionRef ExpectedActiveVersion,
    CapabilityVersionRef TargetVersion,
    string ReasonCode,
    DateTimeOffset NowUtc);

public sealed record CapabilityCatalogSummary(
    CapabilityId Id,
    string DisplayName,
    string Description,
    CapabilityKind Kind,
    string MaximumRisk,
    CapabilityVersionRef? ActiveVersion,
    CapabilityVersionRef LatestVersion,
    CapabilityDraftState LatestState,
    long CatalogRevision,
    string SourceKind,
    DateTimeOffset UpdatedAtUtc);

public interface ICapabilityCatalogRepository
{
    Task<CapabilityCatalogEntry?> GetCatalogAsync(
        CapabilityId id,
        CancellationToken cancellationToken);

    Task<CapabilityVersion?> GetVersionAsync(
        CapabilityVersionRef exactVersion,
        CancellationToken cancellationToken);

    Task<CapabilityDraft?> GetDraftAsync(
        string draftId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CapabilityCatalogSummary>> ListAsync(
        string? query,
        int offset,
        int limit,
        CancellationToken cancellationToken);

    Task<CapabilityDraftCreateResult> CreateValidatedDraftAsync(
        ValidatedCapabilityDraft draft,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task<CapabilityActivationResult> ApproveAndActivateAsync(
        CapabilityApprovalCommand command,
        CancellationToken cancellationToken);

    Task<CapabilityActivationResult> RollbackAsync(
        CapabilityRollbackCommand command,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CapabilityActivation>> ListActivationsAsync(
        CapabilityId id,
        int limit,
        CancellationToken cancellationToken);
}

public interface ICapabilityCandidateRepository
{
    Task<IReadOnlyList<CapabilityVersionRef>> SearchActiveAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);
}

public sealed record CapabilityToolDependency(string Name, string Risk);

public interface ICapabilityDependencyCatalog
{
    bool TryGetTool(string name, out CapabilityToolDependency? dependency);
}

public interface ICapabilityCatalogService
{
    Task<CapabilityCatalogEntry?> GetCatalogAsync(
        CapabilityId id,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CapabilityCatalogSummary>> ListAsync(
        string? query,
        int offset,
        int limit,
        CancellationToken cancellationToken);

    Task<CapabilityDraft?> GetDraftAsync(string draftId, CancellationToken cancellationToken);

    Task<CapabilityVersion?> GetVersionAsync(
        CapabilityVersionRef exactVersion,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CapabilityActivation>> ListActivationsAsync(
        CapabilityId id,
        int limit,
        CancellationToken cancellationToken);

    Task<CapabilityDraftCreateResult> SubmitValidatedDraftAsync(
        ValidatedCapabilityDraft draft,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task<CapabilityActivationResult> ApproveAndActivateAsync(
        CapabilityApprovalCommand command,
        CancellationToken cancellationToken);

    Task<CapabilityActivationResult> RollbackAsync(
        CapabilityRollbackCommand command,
        CancellationToken cancellationToken);
}

public sealed class CapabilityCatalogService(
    ICapabilityCatalogRepository repository,
    ICapabilityDependencyCatalog dependencies,
    IWorkflowToolMetadataCatalog? workflowTools = null,
    ICapabilityConnectionReadinessResolver? connections = null) : ICapabilityCatalogService
{
    public Task<CapabilityCatalogEntry?> GetCatalogAsync(
        CapabilityId id,
        CancellationToken cancellationToken) =>
        repository.GetCatalogAsync(id, cancellationToken);

    public Task<IReadOnlyList<CapabilityCatalogSummary>> ListAsync(
        string? query,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (query?.Length > 256) throw new ArgumentOutOfRangeException(nameof(query));
        return repository.ListAsync(query, offset, limit, cancellationToken);
    }

    public Task<CapabilityDraft?> GetDraftAsync(string draftId, CancellationToken cancellationToken) =>
        repository.GetDraftAsync(draftId, cancellationToken);

    public Task<CapabilityVersion?> GetVersionAsync(
        CapabilityVersionRef exactVersion,
        CancellationToken cancellationToken) =>
        repository.GetVersionAsync(exactVersion, cancellationToken);

    public Task<IReadOnlyList<CapabilityActivation>> ListActivationsAsync(
        CapabilityId id,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return repository.ListActivationsAsync(id, limit, cancellationToken);
    }

    public async Task<CapabilityDraftCreateResult> SubmitValidatedDraftAsync(
        ValidatedCapabilityDraft draft,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (nowUtc.Offset != TimeSpan.Zero)
            return Invalid("capability.timestamp.utc_required");
        var validation = CapabilityDefinitionPolicy.Validate(draft.Payload);
        if (!validation.IsValid) return Invalid(validation.ErrorCode!);
        if (draft.DraftId.Length is < 1 or > 64 ||
            draft.DraftId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) ||
            draft.SourceKind is not ("local_import" or "builder"))
            return Invalid("capability.draft.identity_invalid");
        if (draft.SourceKind == "builder")
            return new(CapabilityMutationOutcome.Unsupported, ErrorCode: "capability.d1.local_import_only");
        if (draft.Payload.Manifest.Kind is not (CapabilityKind.Prompt or CapabilityKind.Workflow))
            return new(CapabilityMutationOutcome.Unsupported, ErrorCode: "capability.d3.kind_unsupported");
        if (draft.Payload.Manifest.RequiredConnections.Count != 0 &&
            (draft.Payload.Manifest.Kind != CapabilityKind.Workflow ||
             draft.Payload.Manifest.RequiredConnections.Any(requirement =>
                 !CapabilityConnectionKindPolicy.IsSupported(requirement.Kind))))
            return new(CapabilityMutationOutcome.Unsupported, ErrorCode: "capability.d4.connection_kind_unsupported");
        if (!CapabilityContentHasher.Compute(draft.Payload).Equals(draft.ContentHash))
            return Invalid("capability.content_hash.mismatch");
        if (draft.Parent is not null &&
            (!draft.Parent.Id.Equals(draft.ExactVersion.Id) ||
             draft.ExactVersion.Version.CompareTo(draft.Parent.Version) <= 0 ||
             SamePrecedence(draft.ExactVersion.Version, draft.Parent.Version)))
            return Invalid("capability.parent.invalid");
        var ingressViolation = CapabilityIngressPolicy.GetViolation(draft.Payload);
        if (ingressViolation == CapabilityIngressViolation.SecretLiteral)
            return Invalid("capability.secret_literal.detected");
        if (ingressViolation == CapabilityIngressViolation.ConnectionLiteral)
            return Invalid("capability.connection_literal.detected");
        foreach (var toolName in draft.Payload.Manifest.RequiredTools)
        {
            if (!dependencies.TryGetTool(toolName, out var dependency) || dependency is null)
                return Invalid("capability.tool.unknown");
            if (RiskOrdinal(dependency.Risk) > RiskOrdinal(draft.Payload.Manifest.MaximumRisk))
                return Invalid("capability.risk.underdeclared");
        }
        if (ValidateWorkflowDefinition(draft.Payload) is { } workflowError)
            return Invalid(workflowError);

        return await repository.CreateValidatedDraftAsync(draft, nowUtc, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CapabilityActivationResult> ApproveAndActivateAsync(
        CapabilityApprovalCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ExpectedVersion is null ||
            command.ExpectedDraftRevision < 0 || command.ExpectedCatalogRevision < 0 ||
            (command.ExpectedActiveVersion is not null &&
             !command.ExpectedVersion.Id.Equals(command.ExpectedActiveVersion.Id)) ||
            command.NowUtc.Offset != TimeSpan.Zero ||
            !IsReasonCode(command.ReasonCode))
            return InvalidActivation("capability.activation.command_invalid");

        var draft = await repository.GetDraftAsync(command.DraftId, cancellationToken).ConfigureAwait(false);
        if (draft is null)
            return new(CapabilityMutationOutcome.NotFound, ErrorCode: "capability.draft.not_found");
        if (!draft.ExactVersion.Equals(command.ExpectedVersion))
            return new(CapabilityMutationOutcome.Stale, ErrorCode: "capability.approval.version_changed");
        var readiness = await ValidateActivationReadinessAsync(
            draft.Payload,
            draft.ExactVersion,
            cancellationToken).ConfigureAwait(false);
        if (readiness is not null) return readiness;

        return await repository.ApproveAndActivateAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CapabilityActivationResult> RollbackAsync(
        CapabilityRollbackCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ExpectedCatalogRevision < 1 ||
            !command.Id.Equals(command.ExpectedActiveVersion.Id) ||
            !command.Id.Equals(command.TargetVersion.Id) ||
            command.ExpectedActiveVersion.Equals(command.TargetVersion) ||
            command.NowUtc.Offset != TimeSpan.Zero ||
            !IsReasonCode(command.ReasonCode))
            return InvalidActivation("capability.rollback.command_invalid");

        var target = await repository.GetVersionAsync(command.TargetVersion, cancellationToken).ConfigureAwait(false);
        if (target is null)
            return new(CapabilityMutationOutcome.NotFound, ErrorCode: "capability.rollback.target_not_found");
        var readiness = await ValidateActivationReadinessAsync(
            target.Payload,
            target.ExactVersion,
            cancellationToken).ConfigureAwait(false);
        if (readiness is not null) return readiness;

        return await repository.RollbackAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CapabilityActivationResult?> ValidateActivationReadinessAsync(
        CapabilityVersionPayload payload,
        CapabilityVersionRef exactVersion,
        CancellationToken cancellationToken)
    {
        var validation = CapabilityDefinitionPolicy.Validate(payload);
        if (!validation.IsValid)
            return InvalidActivation(validation.ErrorCode ?? "capability.definition.invalid");
        if (!payload.Manifest.Id.Equals(exactVersion.Id) ||
            !payload.Manifest.Version.Equals(exactVersion.Version) ||
            !CapabilityContentHasher.Compute(payload).Equals(exactVersion.ContentHash))
            return InvalidActivation("capability.content_hash.mismatch");
        if (payload.Manifest.Kind is not (CapabilityKind.Prompt or CapabilityKind.Workflow))
            return new(CapabilityMutationOutcome.Unsupported, ErrorCode: "capability.d3.kind_unsupported");
        if (payload.Manifest.RequiredConnections.Count != 0 &&
            (payload.Manifest.Kind != CapabilityKind.Workflow ||
             payload.Manifest.RequiredConnections.Any(requirement =>
                 !CapabilityConnectionKindPolicy.IsSupported(requirement.Kind))))
            return new(CapabilityMutationOutcome.Unsupported, ErrorCode: "capability.d4.connection_kind_unsupported");
        var ingressViolation = CapabilityIngressPolicy.GetViolation(payload);
        if (ingressViolation == CapabilityIngressViolation.SecretLiteral)
            return InvalidActivation("capability.secret_literal.detected");
        if (ingressViolation == CapabilityIngressViolation.ConnectionLiteral)
            return InvalidActivation("capability.connection_literal.detected");
        if (payload.Manifest.RequiredConnections.Count != 0)
        {
            if (connections is null)
                return new(CapabilityMutationOutcome.NotReady, ErrorCode: "capability.connection.resolver_unavailable");
            ConnectionReadinessResolution resolved;
            try
            {
                resolved = await connections.ResolveAsync(
                    exactVersion,
                    payload.Manifest.RequiredConnections,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new(CapabilityMutationOutcome.NotReady, ErrorCode: "capability.connection.resolution_failed");
            }
            if (!resolved.IsReady)
                return new(
                    CapabilityMutationOutcome.NotReady,
                    ErrorCode: resolved.Bindings.FirstOrDefault(binding => !binding.IsReady)?.ErrorCode ??
                               "capability.connection.not_ready");
            if (!IsHash(resolved.AggregateHash))
                return InvalidActivation("capability.connection.binding_hash_invalid");
        }
        foreach (var toolName in payload.Manifest.RequiredTools)
        {
            if (!dependencies.TryGetTool(toolName, out var dependency) || dependency is null)
                return new(CapabilityMutationOutcome.NotReady, ErrorCode: "capability.tool.unavailable");
            if (RiskOrdinal(dependency.Risk) > RiskOrdinal(payload.Manifest.MaximumRisk))
                return new(CapabilityMutationOutcome.NotReady, ErrorCode: "capability.risk.changed");
        }
        if (ValidateWorkflowDefinition(payload) is { } workflowError)
        {
            var outcome = StringComparer.Ordinal.Equals(
                workflowError,
                "capability.workflow.validator_unavailable")
                ? CapabilityMutationOutcome.NotReady
                : CapabilityMutationOutcome.Invalid;
            return new(outcome, ErrorCode: workflowError);
        }
        return null;
    }

    private static bool IsHash(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef");

    private string? ValidateWorkflowDefinition(CapabilityVersionPayload payload)
    {
        if (payload.Manifest.Kind != CapabilityKind.Workflow) return null;
        if (workflowTools is null) return "capability.workflow.validator_unavailable";
        if (payload.CanonicalWorkflowJson is null) return "capability.workflow.entrypoint_invalid";
        var parsed = new WorkflowDefinitionParser().Parse(payload.CanonicalWorkflowJson);
        if (!parsed.Succeeded || parsed.Definition is null)
            return parsed.Issues.FirstOrDefault()?.Code ?? "workflow.definition.invalid";
        var validation = new WorkflowStaticValidator(workflowTools).Validate(
            parsed.Definition,
            payload.Manifest,
            payload.CanonicalInputSchemaJson);
        return validation.Succeeded
            ? null
            : validation.Issues.FirstOrDefault()?.Code ?? "workflow.definition.invalid";
    }

    private static CapabilityDraftCreateResult Invalid(string errorCode) =>
        new(CapabilityMutationOutcome.Invalid, ErrorCode: errorCode);

    private static CapabilityActivationResult InvalidActivation(string errorCode) =>
        new(CapabilityMutationOutcome.Invalid, ErrorCode: errorCode);

    private static bool SamePrecedence(CapabilitySemanticVersion left, CapabilitySemanticVersion right) =>
        left.CompareTo(right) == 0;

    private static int RiskOrdinal(string risk) => risk switch
    {
        "R0" => 0,
        "R1" => 1,
        "R2" => 2,
        "R3" => 3,
        "R4" => 4,
        _ => int.MaxValue,
    };

    private static bool IsReasonCode(string value) =>
        value.Length is >= 1 and <= 64 &&
        value.All(character => character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character is '.' or '_');
}
