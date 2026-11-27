using LIGClaw.Domain;

namespace LIGClaw.Application.Capabilities;

public enum FeatureBuilderRuntimeOutcome
{
    NeedsInformation,
    Proposal,
    Refused,
}

public enum FeatureBuilderManagementOutcome
{
    Succeeded,
    Invalid,
    Duplicate,
    NotFound,
    NotReady,
    Stale,
    Refused,
    Quarantined,
    Interrupted,
}

public sealed record FeatureBuilderInventory
{
    private IReadOnlyList<string> _tools = [];
    private IReadOnlyList<string> _connectionKinds = [];
    private IReadOnlyList<string> _capabilityIds = [];

    public FeatureBuilderInventory(
        IReadOnlyList<string> tools,
        IReadOnlyList<string> connectionKinds,
        IReadOnlyList<string> capabilityIds)
    {
        Tools = tools;
        ConnectionKinds = connectionKinds;
        CapabilityIds = capabilityIds;
    }

    public IReadOnlyList<string> Tools
    {
        get => _tools;
        init => _tools = Copy(value);
    }
    public IReadOnlyList<string> ConnectionKinds
    {
        get => _connectionKinds;
        init => _connectionKinds = Copy(value);
    }
    public IReadOnlyList<string> CapabilityIds
    {
        get => _capabilityIds;
        init => _capabilityIds = Copy(value);
    }

    private static IReadOnlyList<string> Copy(IReadOnlyList<string> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Array.AsReadOnly(source.ToArray());
    }
}

public sealed record FeatureBuilderRawProposalArtifact(string Path, string MediaType, string Content);

public sealed record FeatureBuilderRuntimeRequest(
    int SchemaVersion,
    string GenerationId,
    CapabilityContentHash RequestHash,
    string SessionId,
    long SessionRevision,
    CapabilityContentHash InventoryHash,
    DateTimeOffset DeadlineUtc,
    string Requirement,
    IReadOnlyList<FeatureBuilderAnswer> Answers,
    FeatureBuilderInventory Inventory);

public sealed record FeatureBuilderRuntimeResult(
    int SchemaVersion,
    string GenerationId,
    CapabilityContentHash RequestHash,
    string SessionId,
    long SessionRevision,
    CapabilityContentHash InventoryHash,
    DateTimeOffset DeadlineUtc,
    FeatureBuilderRuntimeOutcome Outcome,
    IReadOnlyList<FeatureBuilderQuestion>? Questions = null,
    IReadOnlyList<FeatureBuilderRawProposalArtifact>? Artifacts = null,
    IReadOnlyList<string>? Limitations = null,
    string? RefusalCode = null);

public interface IFeatureBuilderRuntimeClient
{
    Task<FeatureBuilderRuntimeResult> GenerateAsync(
        FeatureBuilderRuntimeRequest request,
        CancellationToken cancellationToken);
}

public sealed record StartFeatureBuilderSessionCommand(
    string SessionId,
    string Requirement,
    FeatureBuilderInventory Inventory,
    long ExpectedCatalogRevision,
    FeatureBuilderRevisionTarget? RevisionTarget,
    DateTimeOffset NowUtc);

public sealed record GenerateFeatureBuilderProposalCommand(
    string SessionId,
    long ExpectedSessionRevision,
    string GenerationId,
    IReadOnlyList<FeatureBuilderAnswer> Answers,
    FeatureBuilderInventory Inventory,
    DateTimeOffset DeadlineUtc,
    DateTimeOffset NowUtc);

public sealed record ApplyFeatureBuilderProposalCommand(
    string ProposalId,
    long ExpectedProposalRevision,
    string DraftId,
    DateTimeOffset NowUtc);

public sealed record SetFeatureBuilderProposalDispositionCommand(
    string ProposalId,
    long ExpectedProposalRevision,
    FeatureBuilderProposalState State,
    string ReasonCode,
    DateTimeOffset NowUtc);

public sealed record CancelFeatureBuilderSessionCommand(
    string SessionId,
    long ExpectedSessionRevision,
    DateTimeOffset NowUtc);

public sealed record StartFeatureBuilderTestRunCommand(
    string TestRunId,
    string ProposalId,
    FeatureBuilderTestRunKind Kind,
    int Attempt,
    string EngineVersion,
    DateTimeOffset NowUtc);

public sealed record CompleteFeatureBuilderTestRunCommand(
    string TestRunId,
    long ExpectedRevision,
    FeatureBuilderTestRunState State,
    string CanonicalResultJson,
    string? FixedErrorCode,
    DateTimeOffset NowUtc);

public sealed record FeatureBuilderManagementResult(
    FeatureBuilderManagementOutcome Outcome,
    FeatureBuilderSession? Session = null,
    FeatureBuilderProposal? Proposal = null,
    CapabilityDraft? Draft = null,
    FeatureBuilderTestRun? TestRun = null,
    string? ErrorCode = null);

public sealed record FeatureBuilderSessionWriteResult(
    FeatureBuilderManagementOutcome Outcome,
    FeatureBuilderSession? Session = null,
    string? ErrorCode = null);

public sealed record FeatureBuilderProposalWriteResult(
    FeatureBuilderManagementOutcome Outcome,
    FeatureBuilderSession? Session = null,
    FeatureBuilderProposal? Proposal = null,
    string? ErrorCode = null);

public sealed record FeatureBuilderTestRunWriteResult(
    FeatureBuilderManagementOutcome Outcome,
    FeatureBuilderTestRun? TestRun = null,
    string? ErrorCode = null);

public sealed record FeatureBuilderRepositoryApplyCommand(
    string ProposalId,
    long ExpectedProposalRevision,
    long ExpectedSessionRevision,
    ValidatedCapabilityDraft Draft,
    CapabilityContentHash TestEvidenceHash,
    DateTimeOffset NowUtc);

public sealed record FeatureBuilderApplyWriteResult(
    FeatureBuilderManagementOutcome Outcome,
    FeatureBuilderSession? Session = null,
    FeatureBuilderProposal? Proposal = null,
    CapabilityDraft? Draft = null,
    string? ErrorCode = null);

public interface IFeatureBuilderRepository
{
    Task<FeatureBuilderSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task<FeatureBuilderProposal?> GetProposalAsync(string proposalId, CancellationToken cancellationToken);
    Task<FeatureBuilderTestRun?> GetTestRunAsync(string testRunId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FeatureBuilderTestRun>> ListTestRunsAsync(
        string proposalId,
        CancellationToken cancellationToken);

    Task<FeatureBuilderSessionWriteResult> CreateSessionAsync(
        FeatureBuilderSession session,
        CancellationToken cancellationToken);

    Task<FeatureBuilderSessionWriteResult> ReplaceSessionAsync(
        long expectedRevision,
        FeatureBuilderSession replacement,
        CancellationToken cancellationToken);

    Task<FeatureBuilderProposalWriteResult> CreateProposalAsync(
        FeatureBuilderProposal proposal,
        long expectedSessionRevision,
        FeatureBuilderSession replacementSession,
        CancellationToken cancellationToken);

    Task<FeatureBuilderProposalWriteResult> ReplaceProposalAndSessionAsync(
        long expectedProposalRevision,
        FeatureBuilderProposal replacementProposal,
        long expectedSessionRevision,
        FeatureBuilderSession replacementSession,
        CancellationToken cancellationToken);

    // This is one Desktop-owned transaction: it may create only a ReadyForReview draft.
    // It must not change capability activation or the active catalog version.
    Task<FeatureBuilderApplyWriteResult> ApplyProposalAsReadyDraftAsync(
        FeatureBuilderRepositoryApplyCommand command,
        CancellationToken cancellationToken);

    Task<FeatureBuilderTestRunWriteResult> CreateTestRunAsync(
        FeatureBuilderTestRun testRun,
        CancellationToken cancellationToken);

    Task<FeatureBuilderTestRunWriteResult> ReplaceTestRunAsync(
        long expectedRevision,
        FeatureBuilderTestRun replacement,
        CancellationToken cancellationToken);
}

public interface IFeatureBuilderManagementService
{
    Task<FeatureBuilderSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task<FeatureBuilderProposal?> GetProposalAsync(string proposalId, CancellationToken cancellationToken);

    Task<FeatureBuilderManagementResult> StartSessionAsync(
        StartFeatureBuilderSessionCommand command,
        CancellationToken cancellationToken);

    Task<FeatureBuilderManagementResult> GenerateAsync(
        GenerateFeatureBuilderProposalCommand command,
        CancellationToken cancellationToken);

    Task<FeatureBuilderManagementResult> ApplyProposalAsync(
        ApplyFeatureBuilderProposalCommand command,
        CancellationToken cancellationToken);

    Task<FeatureBuilderManagementResult> SetProposalDispositionAsync(
        SetFeatureBuilderProposalDispositionCommand command,
        CancellationToken cancellationToken);

    Task<FeatureBuilderManagementResult> CancelSessionAsync(
        CancelFeatureBuilderSessionCommand command,
        CancellationToken cancellationToken);

    Task<FeatureBuilderManagementResult> StartTestRunAsync(
        StartFeatureBuilderTestRunCommand command,
        CancellationToken cancellationToken);

    Task<FeatureBuilderManagementResult> CompleteTestRunAsync(
        CompleteFeatureBuilderTestRunCommand command,
        CancellationToken cancellationToken);
}
