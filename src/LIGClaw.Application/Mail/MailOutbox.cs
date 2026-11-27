using LIGClaw.Domain;

namespace LIGClaw.Application.Mail;

public sealed record MailOutboxOperationId
{
    private const string Prefix = "mgo_";

    private MailOutboxOperationId(string value) => Value = value;

    public string Value { get; }

    public static MailOutboxOperationId Parse(string value)
    {
        if (!TryParse(value, out var parsed))
            throw new FormatException("Mail outbox operation ID 형식이 올바르지 않습니다.");
        return parsed!;
    }

    public static bool TryParse(string? value, out MailOutboxOperationId? parsed)
    {
        parsed = null;
        if (!MailOutboxValuePolicy.IsPrefixedHex(value, Prefix)) return false;
        parsed = new MailOutboxOperationId(value!);
        return true;
    }

    public override string ToString() => Value;
}

public sealed record MailOutboxIdempotencyKey
{
    private const string Prefix = "mik_";

    private MailOutboxIdempotencyKey(string value) => Value = value;

    public string Value { get; }

    public static MailOutboxIdempotencyKey Parse(string value)
    {
        if (!TryParse(value, out var parsed))
            throw new FormatException("Mail outbox idempotency key 형식이 올바르지 않습니다.");
        return parsed!;
    }

    public static bool TryParse(string? value, out MailOutboxIdempotencyKey? parsed)
    {
        parsed = null;
        if (!MailOutboxValuePolicy.IsPrefixedHex(value, Prefix)) return false;
        parsed = new MailOutboxIdempotencyKey(value!);
        return true;
    }

    public override string ToString() => "[mail-outbox-idempotency-key]";
}

public sealed record MailOutboxHash
{
    private MailOutboxHash(string value) => Value = value;

    public string Value { get; }

    public static MailOutboxHash Parse(string value)
    {
        if (!TryParse(value, out var parsed))
            throw new FormatException("Mail outbox hash 형식이 올바르지 않습니다.");
        return parsed!;
    }

    public static bool TryParse(string? value, out MailOutboxHash? parsed)
    {
        parsed = null;
        if (!MailOutboxValuePolicy.IsSha256(value)) return false;
        parsed = new MailOutboxHash(value!);
        return true;
    }

    public override string ToString() => Value;
}

public enum MailOutboxSubmissionState
{
    Staged,
    Submitting,
    Accepted,
    FailedKnown,
    OutcomeUnknown,
    CancelledNotStarted,
}

public enum MailOutboxDeliveryState
{
    Unknown,
    Queued,
    Delivered,
    Failed,
}

public enum MailOutboxTransportKind
{
    MailApi,
    Smtp,
}

public enum MailOutboxSmtpPhase
{
    PreData,
    DataStarted,
}

public sealed class MailOutboxCreateRequest
{
    public MailOutboxCreateRequest(
        string conversationId,
        string workflowRunId,
        string toolCallId,
        string capabilityId,
        string capabilityVersion,
        MailOutboxHash capabilityContentHash,
        long catalogRevision,
        MailOutboxHash snapshotHash,
        string connectionSlot,
        MailOutboxHash bindingHash,
        MailOutboxHash draftHash,
        DateTimeOffset createdAtUtc,
        MailOutboxTransportKind transportKind = MailOutboxTransportKind.MailApi)
    {
        ConversationId = Copy(conversationId);
        WorkflowRunId = Copy(workflowRunId);
        ToolCallId = Copy(toolCallId);
        CapabilityId = Copy(capabilityId);
        CapabilityVersion = Copy(capabilityVersion);
        CapabilityContentHash = capabilityContentHash;
        CatalogRevision = catalogRevision;
        SnapshotHash = snapshotHash;
        ConnectionSlot = Copy(connectionSlot);
        BindingHash = bindingHash;
        DraftHash = draftHash;
        CreatedAtUtc = createdAtUtc;
        TransportKind = transportKind;
        MailOutboxPolicy.ValidateCreateRequest(this);
    }

    public string ConversationId { get; }
    public string WorkflowRunId { get; }
    public string ToolCallId { get; }
    public string CapabilityId { get; }
    public string CapabilityVersion { get; }
    public MailOutboxHash CapabilityContentHash { get; }
    public long CatalogRevision { get; }
    public MailOutboxHash SnapshotHash { get; }
    public string ConnectionSlot { get; }
    public MailOutboxHash BindingHash { get; }
    public MailOutboxHash DraftHash { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public MailOutboxTransportKind TransportKind { get; }

    public override string ToString() => "MailOutboxCreateRequest [metadata only]";

    private static string Copy(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Concat(value);
    }
}

public sealed class MailOutboxOperation
{
    private MailOutboxOperation(
        MailOutboxOperationId operationId,
        MailOutboxIdempotencyKey idempotencyKey,
        string conversationId,
        string workflowRunId,
        string toolCallId,
        string capabilityId,
        string capabilityVersion,
        MailOutboxHash capabilityContentHash,
        long catalogRevision,
        MailOutboxHash snapshotHash,
        string connectionSlot,
        MailOutboxHash bindingHash,
        MailOutboxHash draftHash,
        MailOutboxTransportKind transportKind,
        string? smtpMessageId,
        MailOutboxSmtpPhase? smtpPhase,
        MailOutboxSubmissionState submissionState,
        MailOutboxDeliveryState deliveryState,
        int attemptCount,
        string? fixedErrorCode,
        long revision,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        OperationId = operationId;
        IdempotencyKey = idempotencyKey;
        ConversationId = string.Concat(conversationId);
        WorkflowRunId = string.Concat(workflowRunId);
        ToolCallId = string.Concat(toolCallId);
        CapabilityId = string.Concat(capabilityId);
        CapabilityVersion = string.Concat(capabilityVersion);
        CapabilityContentHash = capabilityContentHash;
        CatalogRevision = catalogRevision;
        SnapshotHash = snapshotHash;
        ConnectionSlot = string.Concat(connectionSlot);
        BindingHash = bindingHash;
        DraftHash = draftHash;
        TransportKind = transportKind;
        SmtpMessageId = smtpMessageId is null ? null : string.Concat(smtpMessageId);
        SmtpPhase = smtpPhase;
        SubmissionState = submissionState;
        DeliveryState = deliveryState;
        AttemptCount = attemptCount;
        FixedErrorCode = fixedErrorCode is null ? null : string.Concat(fixedErrorCode);
        Revision = revision;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public MailOutboxOperationId OperationId { get; }
    public MailOutboxIdempotencyKey IdempotencyKey { get; }
    public string ConversationId { get; }
    public string WorkflowRunId { get; }
    public string ToolCallId { get; }
    public string CapabilityId { get; }
    public string CapabilityVersion { get; }
    public MailOutboxHash CapabilityContentHash { get; }
    public long CatalogRevision { get; }
    public MailOutboxHash SnapshotHash { get; }
    public string ConnectionSlot { get; }
    public MailOutboxHash BindingHash { get; }
    public MailOutboxHash DraftHash { get; }
    public MailOutboxTransportKind TransportKind { get; }
    public string? SmtpMessageId { get; }
    public MailOutboxSmtpPhase? SmtpPhase { get; }
    public MailOutboxSubmissionState SubmissionState { get; }
    public MailOutboxDeliveryState DeliveryState { get; }
    public int AttemptCount { get; }
    public string? FixedErrorCode { get; }
    public long Revision { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; }

    public static MailOutboxOperation CreateStaged(
        MailOutboxOperationId operationId,
        MailOutboxIdempotencyKey idempotencyKey,
        MailOutboxCreateRequest request) =>
        Restore(
            operationId,
            idempotencyKey,
            request.ConversationId,
            request.WorkflowRunId,
            request.ToolCallId,
            request.CapabilityId,
            request.CapabilityVersion,
            request.CapabilityContentHash,
            request.CatalogRevision,
            request.SnapshotHash,
            request.ConnectionSlot,
            request.BindingHash,
            request.DraftHash,
            MailOutboxSubmissionState.Staged,
            MailOutboxDeliveryState.Unknown,
            0,
            null,
            0,
            request.CreatedAtUtc,
            request.CreatedAtUtc,
            request.TransportKind,
            request.TransportKind == MailOutboxTransportKind.Smtp
                ? MailOutboxPolicy.ComputeSmtpMessageId(operationId)
                : null,
            request.TransportKind == MailOutboxTransportKind.Smtp
                ? MailOutboxSmtpPhase.PreData
                : null);

    public static MailOutboxOperation Restore(
        MailOutboxOperationId operationId,
        MailOutboxIdempotencyKey idempotencyKey,
        string conversationId,
        string workflowRunId,
        string toolCallId,
        string capabilityId,
        string capabilityVersion,
        MailOutboxHash capabilityContentHash,
        long catalogRevision,
        MailOutboxHash snapshotHash,
        string connectionSlot,
        MailOutboxHash bindingHash,
        MailOutboxHash draftHash,
        MailOutboxSubmissionState submissionState,
        MailOutboxDeliveryState deliveryState,
        int attemptCount,
        string? fixedErrorCode,
        long revision,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        MailOutboxTransportKind transportKind = MailOutboxTransportKind.MailApi,
        string? smtpMessageId = null,
        MailOutboxSmtpPhase? smtpPhase = null)
    {
        var operation = new MailOutboxOperation(
            operationId,
            idempotencyKey,
            conversationId,
            workflowRunId,
            toolCallId,
            capabilityId,
            capabilityVersion,
            capabilityContentHash,
            catalogRevision,
            snapshotHash,
            connectionSlot,
            bindingHash,
            draftHash,
            transportKind,
            smtpMessageId,
            smtpPhase,
            submissionState,
            deliveryState,
            attemptCount,
            fixedErrorCode,
            revision,
            createdAtUtc,
            updatedAtUtc);
        MailOutboxPolicy.ValidateOperation(operation);
        return operation;
    }

    internal MailOutboxOperation WithTransition(
        MailOutboxSubmissionState submissionState,
        MailOutboxDeliveryState deliveryState,
        int attemptCount,
        string? fixedErrorCode,
        DateTimeOffset updatedAtUtc,
        MailOutboxSmtpPhase? smtpPhase) =>
        Restore(
            OperationId,
            IdempotencyKey,
            ConversationId,
            WorkflowRunId,
            ToolCallId,
            CapabilityId,
            CapabilityVersion,
            CapabilityContentHash,
            CatalogRevision,
            SnapshotHash,
            ConnectionSlot,
            BindingHash,
            DraftHash,
            submissionState,
            deliveryState,
            attemptCount,
            fixedErrorCode,
            checked(Revision + 1),
            CreatedAtUtc,
            updatedAtUtc,
            TransportKind,
            SmtpMessageId,
            smtpPhase);

    public override string ToString() =>
        $"MailOutboxOperation(Transport={TransportKind}, Submission={SubmissionState}, " +
        $"Delivery={DeliveryState}, SmtpPhase={SmtpPhase}, " +
        $"AttemptCount={AttemptCount}, Revision={Revision})";
}

public sealed record MailOutboxTransitionRequest(
    MailOutboxOperationId OperationId,
    long ExpectedRevision,
    MailOutboxSubmissionState ExpectedSubmissionState,
    MailOutboxDeliveryState ExpectedDeliveryState,
    MailOutboxSubmissionState NextSubmissionState,
    MailOutboxDeliveryState NextDeliveryState,
    int NextAttemptCount,
    string? FixedErrorCode,
    DateTimeOffset UpdatedAtUtc,
    MailOutboxSmtpPhase? NextSmtpPhase = null)
{
    public override string ToString() =>
        $"MailOutboxTransitionRequest(NextSubmission={NextSubmissionState}, " +
        $"NextDelivery={NextDeliveryState}, NextAttemptCount={NextAttemptCount})";
}

public enum MailOutboxCreateOutcome
{
    Created,
    Existing,
    Conflict,
}

public sealed record MailOutboxCreateResult(
    MailOutboxCreateOutcome Outcome,
    MailOutboxOperation Operation,
    string? ErrorCode = null)
{
    public override string ToString() =>
        $"MailOutboxCreateResult(Outcome={Outcome}, ErrorCode={ErrorCode})";
}

public enum MailOutboxTransitionOutcome
{
    Succeeded,
    NotFound,
    Stale,
    Invalid,
}

public sealed record MailOutboxTransitionResult(
    MailOutboxTransitionOutcome Outcome,
    MailOutboxOperation? Operation = null,
    string? ErrorCode = null)
{
    public override string ToString() =>
        $"MailOutboxTransitionResult(Outcome={Outcome}, ErrorCode={ErrorCode})";
}

public sealed record MailOutboxStartupRecoveryResult(
    int CancelledNotStarted,
    int MarkedOutcomeUnknown,
    int MarkedFailedKnown = 0);

public interface IMailOutboxRepository
{
    Task<MailOutboxCreateResult> CreateOrGetAsync(
        MailOutboxCreateRequest request,
        CancellationToken cancellationToken);

    Task<MailOutboxOperation?> GetAsync(
        MailOutboxOperationId operationId,
        CancellationToken cancellationToken);

    Task<MailOutboxTransitionResult> TryTransitionAsync(
        MailOutboxTransitionRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MailOutboxOperation>> ListNetworkStartedUnresolvedAsync(
        MailOutboxHash draftHash,
        MailOutboxHash bindingHash,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MailOutboxOperation>> ListSmtpNetworkStartedUnresolvedAsync(
        MailOutboxHash draftHash,
        int limit,
        CancellationToken cancellationToken);

    Task<MailOutboxStartupRecoveryResult> RecoverOnStartupAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken);
}

public static class MailOutboxPolicy
{
    public const string IdentityConflict = "mail.outbox.identity_conflict";
    public const string TransitionInvalid = "mail.outbox.transition_invalid";
    public const string TransitionStale = "mail.outbox.transition_stale";
    public const string NotFound = "mail.outbox.not_found";

    public static string ComputeSmtpMessageId(MailOutboxOperationId operationId)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        return $"<ligclaw.{operationId.Value[4..]}@ligclaw.invalid>";
    }

    public static bool TryApplyTransition(
        MailOutboxOperation current,
        MailOutboxTransitionRequest request,
        out MailOutboxOperation? updated,
        out string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(request);
        updated = null;
        errorCode = null;
        if (!current.OperationId.Equals(request.OperationId) ||
            current.Revision != request.ExpectedRevision ||
            current.SubmissionState != request.ExpectedSubmissionState ||
            current.DeliveryState != request.ExpectedDeliveryState)
        {
            errorCode = TransitionStale;
            return false;
        }
        var nextSmtpPhase = current.TransportKind == MailOutboxTransportKind.Smtp
            ? request.NextSmtpPhase ?? current.SmtpPhase
            : null;
        if (request.UpdatedAtUtc.Offset != TimeSpan.Zero ||
            request.UpdatedAtUtc < current.UpdatedAtUtc ||
            !IsValidFixedErrorCode(request.FixedErrorCode, allowNull: true) ||
            current.TransportKind == MailOutboxTransportKind.MailApi && request.NextSmtpPhase is not null ||
            !IsLegalTransition(current, request, nextSmtpPhase))
        {
            errorCode = TransitionInvalid;
            return false;
        }

        try
        {
            updated = current.WithTransition(
                request.NextSubmissionState,
                request.NextDeliveryState,
                request.NextAttemptCount,
                request.FixedErrorCode,
                request.UpdatedAtUtc,
                nextSmtpPhase);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            updated = null;
            errorCode = TransitionInvalid;
            return false;
        }
    }

    internal static void ValidateCreateRequest(MailOutboxCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!MailOutboxValuePolicy.IsToken(request.ConversationId) ||
            !MailOutboxValuePolicy.IsToken(request.WorkflowRunId) ||
            !MailOutboxValuePolicy.IsToken(request.ToolCallId) ||
            !CapabilityId.TryParse(request.CapabilityId, out _) ||
            !CapabilitySemanticVersion.TryParse(request.CapabilityVersion, out _) ||
            request.CapabilityContentHash is null || request.SnapshotHash is null ||
            request.BindingHash is null || request.DraftHash is null ||
            !Enum.IsDefined(request.TransportKind) ||
            request.CatalogRevision is < 1 or > 9_007_199_254_740_991L ||
            !MailOutboxValuePolicy.IsName(request.ConnectionSlot, 64) ||
            request.CreatedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Mail outbox create metadata가 올바르지 않습니다.", nameof(request));
    }

    internal static void ValidateOperation(MailOutboxOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.OperationId is null || operation.IdempotencyKey is null ||
            !MailOutboxValuePolicy.IsToken(operation.ConversationId) ||
            !MailOutboxValuePolicy.IsToken(operation.WorkflowRunId) ||
            !MailOutboxValuePolicy.IsToken(operation.ToolCallId) ||
            !CapabilityId.TryParse(operation.CapabilityId, out _) ||
            !CapabilitySemanticVersion.TryParse(operation.CapabilityVersion, out _) ||
            operation.CapabilityContentHash is null || operation.SnapshotHash is null ||
            operation.BindingHash is null || operation.DraftHash is null ||
            operation.CatalogRevision is < 1 or > 9_007_199_254_740_991L ||
            !MailOutboxValuePolicy.IsName(operation.ConnectionSlot, 64) ||
            !Enum.IsDefined(operation.TransportKind) ||
            !Enum.IsDefined(operation.SubmissionState) || !Enum.IsDefined(operation.DeliveryState) ||
            operation.AttemptCount is < 0 or > 2 || operation.Revision < 0 ||
            operation.CreatedAtUtc.Offset != TimeSpan.Zero || operation.UpdatedAtUtc.Offset != TimeSpan.Zero ||
            operation.UpdatedAtUtc < operation.CreatedAtUtc ||
            !HasValidTransportIdentity(operation) || !HasValidStateCombination(operation))
            throw new ArgumentException("Mail outbox operation metadata가 올바르지 않습니다.", nameof(operation));
    }

    private static bool IsLegalTransition(
        MailOutboxOperation current,
        MailOutboxTransitionRequest request,
        MailOutboxSmtpPhase? nextSmtpPhase) =>
        current.TransportKind switch
        {
            MailOutboxTransportKind.MailApi => IsLegalMailApiTransition(current, request),
            MailOutboxTransportKind.Smtp => IsLegalSmtpTransition(current, request, nextSmtpPhase),
            _ => false,
        };

    private static bool IsLegalMailApiTransition(
        MailOutboxOperation current,
        MailOutboxTransitionRequest request) =>
        (current.SubmissionState, current.DeliveryState, request.NextSubmissionState, request.NextDeliveryState) switch
        {
            (MailOutboxSubmissionState.Staged, MailOutboxDeliveryState.Unknown,
                MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown) =>
                current.AttemptCount == 0 && request.NextAttemptCount == 1 && request.FixedErrorCode is null,
            (MailOutboxSubmissionState.Staged, MailOutboxDeliveryState.Unknown,
                MailOutboxSubmissionState.CancelledNotStarted, MailOutboxDeliveryState.Unknown) =>
                request.NextAttemptCount == 0 && request.FixedErrorCode is not null,
            (MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown,
                MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Queued) =>
                request.NextAttemptCount == current.AttemptCount && request.FixedErrorCode is null,
            (MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown,
                MailOutboxSubmissionState.FailedKnown, MailOutboxDeliveryState.Failed) =>
                request.NextAttemptCount == current.AttemptCount && request.FixedErrorCode is not null,
            (MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown,
                MailOutboxSubmissionState.OutcomeUnknown, MailOutboxDeliveryState.Unknown) =>
                request.NextAttemptCount == current.AttemptCount && request.FixedErrorCode is not null,
            (MailOutboxSubmissionState.OutcomeUnknown, MailOutboxDeliveryState.Unknown,
                MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Queued) =>
                request.NextAttemptCount == current.AttemptCount && request.FixedErrorCode is null,
            (MailOutboxSubmissionState.OutcomeUnknown, MailOutboxDeliveryState.Unknown,
                MailOutboxSubmissionState.FailedKnown, MailOutboxDeliveryState.Failed) =>
                request.NextAttemptCount == current.AttemptCount && request.FixedErrorCode is not null,
            (MailOutboxSubmissionState.OutcomeUnknown, MailOutboxDeliveryState.Unknown,
                MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown) =>
                current.AttemptCount == 1 && request.NextAttemptCount == 2 && request.FixedErrorCode is null,
            (MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Queued,
                MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Delivered) =>
                request.NextAttemptCount == current.AttemptCount && request.FixedErrorCode is null,
            (MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Queued,
                MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Failed) =>
                request.NextAttemptCount == current.AttemptCount && request.FixedErrorCode is not null,
            _ => false,
        };

    private static bool IsLegalSmtpTransition(
        MailOutboxOperation current,
        MailOutboxTransitionRequest request,
        MailOutboxSmtpPhase? nextSmtpPhase) =>
        (current.SubmissionState, current.DeliveryState, current.SmtpPhase,
            request.NextSubmissionState, request.NextDeliveryState, nextSmtpPhase) switch
        {
            (MailOutboxSubmissionState.Staged, MailOutboxDeliveryState.Unknown,
                MailOutboxSmtpPhase.PreData,
                MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown,
                MailOutboxSmtpPhase.PreData) =>
                current.AttemptCount == 0 && request.NextAttemptCount == 1 &&
                request.FixedErrorCode is null,
            (MailOutboxSubmissionState.Staged, MailOutboxDeliveryState.Unknown,
                MailOutboxSmtpPhase.PreData,
                MailOutboxSubmissionState.CancelledNotStarted, MailOutboxDeliveryState.Unknown,
                MailOutboxSmtpPhase.PreData) =>
                request.NextAttemptCount == 0 && request.FixedErrorCode is not null,
            (MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown,
                MailOutboxSmtpPhase.PreData,
                MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown,
                MailOutboxSmtpPhase.DataStarted) =>
                current.AttemptCount == 1 && request.NextAttemptCount == 1 &&
                request.FixedErrorCode is null,
            (MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown,
                MailOutboxSmtpPhase.PreData,
                MailOutboxSubmissionState.FailedKnown, MailOutboxDeliveryState.Failed,
                MailOutboxSmtpPhase.PreData) =>
                request.NextAttemptCount == 1 && request.FixedErrorCode is not null,
            (MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown,
                MailOutboxSmtpPhase.DataStarted,
                MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Queued,
                MailOutboxSmtpPhase.DataStarted) =>
                request.NextAttemptCount == 1 && request.FixedErrorCode is null,
            (MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown,
                MailOutboxSmtpPhase.DataStarted,
                MailOutboxSubmissionState.FailedKnown, MailOutboxDeliveryState.Failed,
                MailOutboxSmtpPhase.DataStarted) =>
                request.NextAttemptCount == 1 && request.FixedErrorCode is not null,
            (MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown,
                MailOutboxSmtpPhase.DataStarted,
                MailOutboxSubmissionState.OutcomeUnknown, MailOutboxDeliveryState.Unknown,
                MailOutboxSmtpPhase.DataStarted) =>
                request.NextAttemptCount == 1 && request.FixedErrorCode is not null,
            _ => false,
        };

    private static bool HasValidStateCombination(MailOutboxOperation operation) =>
        operation.TransportKind switch
        {
            MailOutboxTransportKind.MailApi => HasValidMailApiStateCombination(operation),
            MailOutboxTransportKind.Smtp => HasValidSmtpStateCombination(operation),
            _ => false,
        };

    private static bool HasValidMailApiStateCombination(MailOutboxOperation operation) =>
        (operation.SubmissionState, operation.DeliveryState) switch
        {
            (MailOutboxSubmissionState.Staged, MailOutboxDeliveryState.Unknown) =>
                operation.AttemptCount == 0 && operation.FixedErrorCode is null,
            (MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown) =>
                operation.AttemptCount is >= 1 and <= 2 && operation.FixedErrorCode is null,
            (MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Queued or MailOutboxDeliveryState.Delivered) =>
                operation.AttemptCount is >= 1 and <= 2 && operation.FixedErrorCode is null,
            (MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Failed) =>
                operation.AttemptCount is >= 1 and <= 2 &&
                IsValidFixedErrorCode(operation.FixedErrorCode, allowNull: false),
            (MailOutboxSubmissionState.FailedKnown, MailOutboxDeliveryState.Failed) =>
                operation.AttemptCount is >= 1 and <= 2 &&
                IsValidFixedErrorCode(operation.FixedErrorCode, allowNull: false),
            (MailOutboxSubmissionState.OutcomeUnknown, MailOutboxDeliveryState.Unknown) =>
                operation.AttemptCount is >= 1 and <= 2 &&
                IsValidFixedErrorCode(operation.FixedErrorCode, allowNull: false),
            (MailOutboxSubmissionState.CancelledNotStarted, MailOutboxDeliveryState.Unknown) =>
                operation.AttemptCount == 0 &&
                IsValidFixedErrorCode(operation.FixedErrorCode, allowNull: false),
            _ => false,
        };

    private static bool HasValidSmtpStateCombination(MailOutboxOperation operation) =>
        (operation.SubmissionState, operation.DeliveryState, operation.SmtpPhase) switch
        {
            (MailOutboxSubmissionState.Staged, MailOutboxDeliveryState.Unknown,
                MailOutboxSmtpPhase.PreData) =>
                operation.AttemptCount == 0 && operation.FixedErrorCode is null,
            (MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown,
                MailOutboxSmtpPhase.PreData or MailOutboxSmtpPhase.DataStarted) =>
                operation.AttemptCount == 1 && operation.FixedErrorCode is null,
            (MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Queued,
                MailOutboxSmtpPhase.DataStarted) =>
                operation.AttemptCount == 1 && operation.FixedErrorCode is null,
            (MailOutboxSubmissionState.FailedKnown, MailOutboxDeliveryState.Failed,
                MailOutboxSmtpPhase.PreData or MailOutboxSmtpPhase.DataStarted) =>
                operation.AttemptCount == 1 &&
                IsValidFixedErrorCode(operation.FixedErrorCode, allowNull: false),
            (MailOutboxSubmissionState.OutcomeUnknown, MailOutboxDeliveryState.Unknown,
                MailOutboxSmtpPhase.DataStarted) =>
                operation.AttemptCount == 1 &&
                IsValidFixedErrorCode(operation.FixedErrorCode, allowNull: false),
            (MailOutboxSubmissionState.CancelledNotStarted, MailOutboxDeliveryState.Unknown,
                MailOutboxSmtpPhase.PreData) =>
                operation.AttemptCount == 0 &&
                IsValidFixedErrorCode(operation.FixedErrorCode, allowNull: false),
            _ => false,
        };

    private static bool HasValidTransportIdentity(MailOutboxOperation operation) =>
        operation.TransportKind switch
        {
            MailOutboxTransportKind.MailApi =>
                operation.SmtpMessageId is null && operation.SmtpPhase is null,
            MailOutboxTransportKind.Smtp =>
                operation.SmtpPhase is not null &&
                StringComparer.Ordinal.Equals(
                    operation.SmtpMessageId,
                    ComputeSmtpMessageId(operation.OperationId)),
            _ => false,
        };

    private static bool IsValidFixedErrorCode(string? value, bool allowNull)
    {
        if (value is null) return allowNull;
        return value.Length is >= 3 and <= 128 && value[0] is >= 'a' and <= 'z' &&
               value.All(character => character is >= 'a' and <= 'z' ||
                                      char.IsAsciiDigit(character) || character is '.' or '_' or '-');
    }
}

internal static class MailOutboxValuePolicy
{
    internal static bool IsPrefixedHex(string? value, string prefix) =>
        value is { Length: 68 } && value.StartsWith(prefix, StringComparison.Ordinal) &&
        !value.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef");

    internal static bool IsSha256(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef");

    internal static bool IsToken(string? value) =>
        value is { Length: >= 1 and <= 256 } && !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl);

    internal static bool IsName(string? value, int maximumLength) =>
        value is { Length: >= 1 } && value.Length <= maximumLength && value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) ||
                               character is '.' or '_' or '-');
}
