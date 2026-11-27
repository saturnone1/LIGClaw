using System.Collections;
using System.Globalization;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Mail;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class MailSendTool : IWorkflowOneShotToolAdapter
{
    private readonly IMailGatewayExecutionResolver resolver;
    private readonly IMailOutboxRepository outbox;
    private readonly MailGatewayWireClient wireClient;
    private readonly TimeProvider timeProvider;

    public MailSendTool(
        IMailGatewayExecutionResolver resolver,
        IMailOutboxRepository outbox,
        MailGatewayWireClient wireClient,
        TimeProvider? timeProvider = null)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        this.wireClient = wireClient ?? throw new ArgumentNullException(nameof(wireClient));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Name => "mail.send.v1";
    public string Risk => "R3";
    public WindowsCapability RequiredCapabilities =>
        WindowsCapability.WebAccess | WindowsCapability.CredentialManager;
    public int Priority => 0;
    public TimeSpan Timeout => TimeSpan.FromSeconds(50);

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(
        IReadOnlyDictionary<string, object?> input) => null;

    public Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken) =>
        Task.FromResult(Failure(
            "메일 발송은 고정된 Workflow 컨텍스트에서만 실행할 수 있어요.",
            "mail.send.context_required"));

    public async Task<WorkflowEffectPreparationResult> PrepareWorkflowEffectAsync(
        ToolInvokeParams invocation,
        DateTimeOffset nowUtc,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.CapabilityContext is not { } context ||
            nowUtc.Offset != TimeSpan.Zero || lifetime <= TimeSpan.Zero ||
            !StringComparer.Ordinal.Equals(invocation.Name, Name) ||
            !StringComparer.Ordinal.Equals(invocation.Risk, Risk) ||
            !IsInvocationToken(invocation.ConversationId) ||
            !IsInvocationToken(invocation.RunId) ||
            !IsInvocationToken(invocation.ToolCallId) ||
            !IsExactCapabilityContext(context))
            return PreparationFailure(
                "고정된 Workflow 메일 컨텍스트를 확인하지 못했어요.",
                "mail.send.context_required");

        MailSendValidationResult validation;
        try
        {
            validation = TryReadInput(invocation.Input, out var input)
                ? MailSendPolicy.Validate(input)
                : MailSendPolicy.Validate(null);
        }
        catch
        {
            validation = MailSendPolicy.Validate(null);
        }
        if (!validation.IsValid || validation.Send is not { } send)
            return PreparationFailure(
                "메일 발송 입력이 허용된 형식과 일치하지 않아요.",
                NormalizeInputError(validation.ErrorCode));

        MailGatewayExecutionResolution resolution;
        try
        {
            resolution = await resolver.ResolveAsync(
                context,
                send.ConnectionSlot,
                MailGatewayResolutionPurpose.Preparation,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PreparationFailure(
                "등록된 메일 연결을 확인하지 못했어요.",
                "mail.gateway.binding_unavailable");
        }

        if (!resolution.Success || resolution.Binding is null || resolution.ExecutionLease is not null)
        {
            if (resolution.ExecutionLease is not null)
                await resolution.ExecutionLease.DisposeAsync().ConfigureAwait(false);
            return PreparationFailure(
                "등록된 메일 연결을 확인하지 못했어요.",
                NormalizeResolverError(resolution.ErrorCode));
        }

        var binding = resolution.Binding;
        if (!TryValidateBinding(binding, requireCredential: false) ||
            !StringComparer.Ordinal.Equals(binding.FrozenBindingHash, context.ConnectionBindingHash))
            return PreparationFailure(
                "Workflow에 고정된 메일 연결이 현재 연결과 일치하지 않아요.",
                "mail.gateway.binding_mismatch");

        DateTimeOffset expiresAtUtc;
        try
        {
            expiresAtUtc = nowUtc.Add(lifetime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return PreparationFailure(
                "메일 발송 승인 시간을 안전하게 계산하지 못했어요.",
                "mail.send.input_invalid");
        }

        var preparedBinding = new PreparedBinding(
            string.Concat(binding.DisplayName),
            new Uri(binding.BaseUri.AbsoluteUri, UriKind.Absolute),
            string.Concat(binding.Sender),
            string.Concat(binding.FrozenBindingHash),
            string.Concat(binding.LocalBindingHash),
            string.Concat(binding.ExecutionFingerprint));
        return new WorkflowEffectPreparationResult(
            true,
            new PreparedMailSendEffect(
                resolver,
                outbox,
                wireClient,
                timeProvider,
                CopyContext(context),
                string.Concat(invocation.ConversationId),
                string.Concat(invocation.RunId),
                string.Concat(invocation.ToolCallId),
                string.Concat(send.ConnectionSlot),
                send.Draft,
                preparedBinding,
                expiresAtUtc));
    }

    private static bool TryReadInput(
        IReadOnlyDictionary<string, object?> source,
        out MailSendInput input)
    {
        input = default!;
        if (source is null || source.Count != 7 ||
            !ToolInputReader.HasOnlyKeys(
                source,
                "connectionSlot",
                "draftHash",
                "to",
                "cc",
                "bcc",
                "subject",
                "body") ||
            !TryReadString(source, "connectionSlot", MailSendPolicy.MaximumConnectionSlotCharacters, out var slot) ||
            !TryReadString(source, "draftHash", 71, out var draftHash) || draftHash.Length != 71 ||
            !TryReadStrings(source, "to", MailDraftPolicy.MaximumRecipients, out var to) ||
            !TryReadStrings(source, "cc", MailDraftPolicy.MaximumRecipients, out var cc) ||
            !TryReadStrings(source, "bcc", MailDraftPolicy.MaximumRecipients, out var bcc) ||
            !TryReadString(source, "subject", MailDraftPolicy.MaximumSubjectScalars * 2, out var subject) ||
            !TryReadString(source, "body", MailDraftPolicy.MaximumBodyScalars * 2, out var body))
            return false;

        input = new MailSendInput(slot, draftHash, to, cc, bcc, subject, body);
        return true;
    }

    private static bool TryReadString(
        IReadOnlyDictionary<string, object?> source,
        string key,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (!source.TryGetValue(key, out var raw)) return false;
        var candidate = raw switch
        {
            string direct => direct,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
        if (candidate is null || candidate.Length > maximumLength) return false;
        value = string.Concat(candidate);
        return true;
    }

    private static bool TryReadStrings(
        IReadOnlyDictionary<string, object?> source,
        string key,
        int maximumItems,
        out IReadOnlyList<string> values)
    {
        values = [];
        if (!source.TryGetValue(key, out var raw)) return false;
        if (raw is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maximumItems)
                return false;
            var parsed = new List<string>(element.GetArrayLength());
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } value ||
                    value.Length > MailDraftPolicy.MaximumAddressCharacters)
                    return false;
                parsed.Add(string.Concat(value));
            }
            values = parsed.AsReadOnly();
            return true;
        }

        if (raw is not IEnumerable candidates || raw is string) return false;

        var result = new List<string>();
        foreach (var candidate in candidates)
        {
            if (candidate is not string value || value.Length > MailDraftPolicy.MaximumAddressCharacters ||
                result.Count == maximumItems)
                return false;
            result.Add(string.Concat(value));
        }
        values = result.AsReadOnly();
        return true;
    }

    private static bool IsExactCapabilityContext(CapabilityInvocationContext context)
    {
        if (!CapabilityRuntimeSnapshotHash.TryParse(context.SnapshotHash, out _) ||
            !IsHash(context.ContentHash) || !IsHash(context.ConnectionBindingHash) ||
            context.CatalogRevision is < 1 or > 9_007_199_254_740_991L)
            return false;
        try
        {
            var exactVersion = new CapabilityVersionRef(
                CapabilityId.Parse(context.CapabilityId),
                CapabilitySemanticVersion.Parse(context.Version),
                CapabilityContentHash.Parse(context.ContentHash));
            return StringComparer.Ordinal.Equals(
                context.ModelToolName,
                CapabilityRuntimeHasher.ComputeModelToolName(exactVersion));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static bool TryValidateBinding(
        MailGatewayRuntimeBinding binding,
        bool requireCredential)
    {
        try
        {
            return binding.DisplayName is { Length: >= 1 and <= 80 } &&
                   binding.DisplayName.Trim() == binding.DisplayName &&
                   !binding.DisplayName.Any(char.IsControl) &&
                   MailGatewayWireClient.IsValidRootHttpsBaseUri(binding.BaseUri) &&
                   MailAddressPolicy.IsValidAsciiMailbox(binding.Sender) &&
                   IsHash(binding.FrozenBindingHash) &&
                   IsHash(binding.LocalBindingHash) &&
                   IsHash(binding.ExecutionFingerprint) &&
                   (requireCredential
                       ? binding.Credential is not null &&
                         MailGatewayBearerTokenPolicy.IsValid(binding.Credential.Reveal())
                       : binding.Credential is null);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeInputError(string? errorCode) => errorCode switch
    {
        MailSendPolicy.ConnectionSlotInvalid => errorCode,
        MailSendPolicy.DraftHashInvalid => errorCode,
        MailSendPolicy.DraftInvalid => errorCode,
        MailSendPolicy.DraftHashMismatch => errorCode,
        _ => MailSendPolicy.InputInvalid,
    };

    private static string NormalizeResolverError(string? errorCode) => errorCode switch
    {
        "mail.gateway.binding_unavailable" => errorCode,
        "mail.gateway.binding_mismatch" => errorCode,
        "mail.gateway.credential_unavailable" => errorCode,
        _ => "mail.gateway.binding_unavailable",
    };

    private static CapabilityInvocationContext CopyContext(CapabilityInvocationContext context) =>
        new(
            string.Concat(context.SnapshotHash),
            string.Concat(context.ModelToolName),
            string.Concat(context.CapabilityId),
            string.Concat(context.Version),
            string.Concat(context.ContentHash),
            context.CatalogRevision,
            string.Concat(context.ConnectionBindingHash));

    private static bool IsHash(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef");

    private static bool IsInvocationToken(string? value) =>
        value is { Length: >= 1 and <= 256 } && !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl);

    private static WorkflowEffectPreparationResult PreparationFailure(
        string message,
        string errorCode) => new(false, Error: message, ErrorCode: errorCode);

    private static WindowsToolExecutionResult Failure(string message, string errorCode) =>
        new(false, new Dictionary<string, object?>(), message, ErrorCode: errorCode);

    private sealed record PreparedBinding(
        string DisplayName,
        Uri BaseUri,
        string Sender,
        string FrozenBindingHash,
        string LocalBindingHash,
        string ExecutionFingerprint);

    private sealed class PreparedMailSendEffect : IPreparedWorkflowEffect
    {
        private const int UnresolvedLookupLimit = 2;
        private static readonly TimeSpan GatewayAuthoritativeLookupWindow = TimeSpan.FromSeconds(300);
        private static readonly TimeSpan HistoricalLookupSafetyMargin = TimeSpan.FromSeconds(60);
        private static readonly WindowsToolApprovalPrompt RedactedApprovalPrompt = new(
            "메일 발송 승인이 종료됐어요.",
            "메일 원문 preview는 메모리에서 해제됐습니다.",
            "이 one-shot 동작은 다시 사용할 수 없습니다.",
            "R3");
        private readonly IMailGatewayExecutionResolver resolver;
        private readonly IMailOutboxRepository outbox;
        private readonly MailGatewayWireClient wireClient;
        private readonly TimeProvider timeProvider;
        private readonly CapabilityInvocationContext context;
        private readonly string conversationId;
        private readonly string workflowRunId;
        private readonly string toolCallId;
        private readonly string connectionSlot;
        private readonly PreparedBinding preparedBinding;
        private MailDraft? draft;
        private WindowsToolApprovalPrompt approvalPrompt;
        private int state;

        public PreparedMailSendEffect(
            IMailGatewayExecutionResolver resolver,
            IMailOutboxRepository outbox,
            MailGatewayWireClient wireClient,
            TimeProvider timeProvider,
            CapabilityInvocationContext context,
            string conversationId,
            string workflowRunId,
            string toolCallId,
            string connectionSlot,
            MailDraft draft,
            PreparedBinding preparedBinding,
            DateTimeOffset expiresAtUtc)
        {
            this.resolver = resolver;
            this.outbox = outbox;
            this.wireClient = wireClient;
            this.timeProvider = timeProvider;
            this.context = context;
            this.conversationId = conversationId;
            this.workflowRunId = workflowRunId;
            this.toolCallId = toolCallId;
            this.connectionSlot = connectionSlot;
            this.draft = draft;
            this.preparedBinding = preparedBinding;
            ExpiresAtUtc = expiresAtUtc;
            approvalPrompt = CreatePrompt(preparedBinding, draft);
            CanonicalPayloadHash = WorkflowPreparedEffectHash.Compute(
                ToolName,
                Risk,
                connectionSlot,
                draft.CanonicalHash);
            CanonicalBindingHash = WorkflowPreparedEffectHash.Compute(
                ToolName,
                context.SnapshotHash,
                context.ModelToolName,
                context.CapabilityId,
                context.Version,
                context.ContentHash,
                context.CatalogRevision.ToString(CultureInfo.InvariantCulture),
                context.ConnectionBindingHash,
                connectionSlot,
                preparedBinding.FrozenBindingHash,
                preparedBinding.LocalBindingHash,
                preparedBinding.ExecutionFingerprint,
                preparedBinding.BaseUri.AbsoluteUri,
                preparedBinding.Sender,
                preparedBinding.DisplayName);
        }

        public string ToolName => "mail.send.v1";
        public string Risk => "R3";
        public string CanonicalPayloadHash { get; }
        public string CanonicalBindingHash { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public WindowsToolApprovalPrompt ApprovalPrompt => Volatile.Read(ref approvalPrompt);

        public async Task<WorkflowPreparedEffectExecution> ExecuteOnceAsync(
            DateTimeOffset trustedNowUtc,
            CancellationToken cancellationToken)
        {
            if (trustedNowUtc.Offset != TimeSpan.Zero || trustedNowUtc >= ExpiresAtUtc)
            {
                if (Interlocked.CompareExchange(ref state, 2, 0) == 0) ClearSensitive();
                return NotStarted("준비된 메일 발송이 만료됐어요.", "mail.send.expired");
            }
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
                return NotStarted(
                    "준비된 메일 발송은 이미 소비됐어요.",
                    "mail.send.already_consumed");

            var executionDraft = Interlocked.Exchange(ref draft, null);
            Interlocked.Exchange(ref approvalPrompt, RedactedApprovalPrompt);
            if (executionDraft is null)
            {
                Volatile.Write(ref state, 2);
                return NotStarted(
                    "준비된 메일 원문을 사용할 수 없어요.",
                    "mail.send.already_consumed");
            }

            try
            {
                return await ExecuteCoreAsync(
                    executionDraft,
                    trustedNowUtc,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                executionDraft = null;
                Volatile.Write(ref state, 2);
                ClearSensitive();
            }
        }

        public ValueTask DisposeAsync()
        {
            _ = Interlocked.CompareExchange(ref state, 2, 0);
            ClearSensitive();
            return ValueTask.CompletedTask;
        }

        public override string ToString() => "Prepared Mail Send effect [redacted]";

        private async Task<WorkflowPreparedEffectExecution> ExecuteCoreAsync(
            MailDraft executionDraft,
            DateTimeOffset trustedNowUtc,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return NotStarted(
                    "메일을 보내기 전에 실행이 중단됐어요.",
                    "mail.send.cancelled");

            MailGatewayExecutionResolution resolution;
            try
            {
                resolution = await resolver.ResolveAsync(
                    context,
                    connectionSlot,
                    MailGatewayResolutionPurpose.Execution,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return NotStarted(
                    "메일을 보내기 전에 실행이 중단됐어요.",
                    "mail.send.cancelled");
            }
            catch
            {
                return NotStarted(
                    "등록된 메일 연결을 다시 확인하지 못했어요.",
                    "mail.gateway.binding_unavailable");
            }

            await using var executionLease = resolution.ExecutionLease;
            if (!resolution.Success || resolution.Binding is null || executionLease is null)
                return NotStarted(
                    "등록된 메일 연결을 다시 확인하지 못했어요.",
                    NormalizeResolverError(resolution.ErrorCode));

            var currentBinding = resolution.Binding;
            if (!TryValidateBinding(currentBinding, requireCredential: true))
                return NotStarted(
                    "현재 등록된 메일 연결 설정을 안전하게 사용할 수 없어요.",
                    "mail.send.binding_invalid");
            if (!BindingMatches(currentBinding))
                return NotStarted(
                    "승인 후 등록된 메일 연결이 변경되어 보내지 않았어요.",
                    "mail.gateway.binding_mismatch");

            var draftHash = MailOutboxHash.Parse(executionDraft.CanonicalHash);
            var localBindingHash = MailOutboxHash.Parse(preparedBinding.LocalBindingHash);
            var reconciliation = await ReconcileOutstandingAsync(
                currentBinding,
                draftHash,
                localBindingHash,
                trustedNowUtc,
                cancellationToken).ConfigureAwait(false);
            if (reconciliation is not null) return reconciliation;

            if (cancellationToken.IsCancellationRequested)
                return NotStarted(
                    "메일을 보내기 전에 실행이 중단됐어요.",
                    "mail.send.cancelled");

            MailOutboxCreateResult created;
            MailOutboxCreateRequest createRequest;
            try
            {
                createRequest = new MailOutboxCreateRequest(
                    conversationId,
                    workflowRunId,
                    toolCallId,
                    context.CapabilityId,
                    context.Version,
                    MailOutboxHash.Parse(context.ContentHash),
                    context.CatalogRevision,
                    MailOutboxHash.Parse(context.SnapshotHash),
                    connectionSlot,
                    localBindingHash,
                    draftHash,
                    trustedNowUtc);
                created = await outbox.CreateOrGetAsync(
                    createRequest,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return NotStarted(
                    "메일을 보내기 전에 실행이 중단됐어요.",
                    "mail.send.cancelled");
            }
            catch
            {
                return NotStarted(
                    "메일 제출 기록을 준비하지 못했어요.",
                    "mail.send.outbox_unavailable");
            }

            if (created.Outcome == MailOutboxCreateOutcome.Conflict)
                return NotStarted(
                    "같은 Workflow 단계의 메일 제출 정보가 일치하지 않아요.",
                    "mail.send.outbox_conflict");
            var operation = created.Operation;
            if (created.Outcome is not (MailOutboxCreateOutcome.Created or MailOutboxCreateOutcome.Existing) ||
                created.ErrorCode is not null || !OperationMatchesRequest(operation, createRequest))
                return NotStarted(
                    "메일 제출 기록의 고정 정보가 현재 승인과 일치하지 않아요.",
                    "mail.send.outbox_unavailable");
            if (created.Outcome == MailOutboxCreateOutcome.Existing)
            {
                var existing = ExistingResult(operation);
                if (existing is not null) return existing;
            }
            if (operation.SubmissionState != MailOutboxSubmissionState.Staged ||
                operation.DeliveryState != MailOutboxDeliveryState.Unknown ||
                operation.AttemptCount != 0)
                return NotStarted(
                    "기존 메일 제출 상태를 안전하게 이어갈 수 없어요.",
                    "mail.send.outbox_unavailable");

            if (cancellationToken.IsCancellationRequested)
            {
                var cancelled = await TransitionAsync(
                    operation,
                    MailOutboxSubmissionState.CancelledNotStarted,
                    MailOutboxDeliveryState.Unknown,
                    0,
                    "mail.send.cancelled",
                    trustedNowUtc,
                    CancellationToken.None).ConfigureAwait(false);
                if (cancelled is null)
                    return NotStarted(
                        "취소된 메일 제출 상태를 기록하지 못했어요.",
                        "mail.send.outbox_unavailable");
                return NotStarted(
                    "메일을 보내기 전에 실행이 중단됐어요.",
                    "mail.send.cancelled");
            }

            MailOutboxOperation? submitting;
            try
            {
                submitting = await TransitionAsync(
                    operation,
                    MailOutboxSubmissionState.Submitting,
                    MailOutboxDeliveryState.Unknown,
                    1,
                    null,
                    trustedNowUtc,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var cancelled = await TransitionAsync(
                    operation,
                    MailOutboxSubmissionState.CancelledNotStarted,
                    MailOutboxDeliveryState.Unknown,
                    0,
                    "mail.send.cancelled",
                    trustedNowUtc,
                    CancellationToken.None).ConfigureAwait(false);
                if (cancelled is null)
                    return NotStarted(
                        "취소된 메일 제출 상태를 기록하지 못했어요.",
                        "mail.send.outbox_unavailable");
                return NotStarted(
                    "메일을 보내기 전에 실행이 중단됐어요.",
                    "mail.send.cancelled");
            }
            if (submitting is null)
                return NotStarted(
                    "메일 제출 상태를 확정하지 못해 보내지 않았어요.",
                    "mail.send.outbox_unavailable");

            if (cancellationToken.IsCancellationRequested)
            {
                var cancelled = await TransitionAsync(
                    submitting,
                    MailOutboxSubmissionState.FailedKnown,
                    MailOutboxDeliveryState.Failed,
                    submitting.AttemptCount,
                    "mail.send.cancelled",
                    trustedNowUtc,
                    CancellationToken.None).ConfigureAwait(false);
                if (cancelled is null)
                    return NotStarted(
                        "취소된 메일 제출 상태를 기록하지 못했어요.",
                        "mail.send.outbox_unavailable");
                return NotStarted(
                    "메일을 보내기 전에 실행이 중단됐어요.",
                    "mail.send.cancelled");
            }

            MailGatewayWireResult submitted;
            try
            {
                submitted = await wireClient.SubmitAsync(
                    currentBinding.BaseUri,
                    currentBinding.Credential!.Reveal(),
                    currentBinding.Sender,
                    executionDraft,
                    submitting.OperationId,
                    submitting.IdempotencyKey,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                submitted = new MailGatewayWireResult(
                    MailGatewayWireOutcome.OutcomeUnknown,
                    ErrorCode: "mail.send.outcome_unknown");
            }
            catch
            {
                submitted = new MailGatewayWireResult(
                    MailGatewayWireOutcome.OutcomeUnknown,
                    ErrorCode: "mail.send.outcome_unknown");
            }

            return await CompleteSubmissionAsync(
                currentBinding,
                submitting,
                draftHash,
                submitted,
                trustedNowUtc).ConfigureAwait(false);
        }

        private async Task<WorkflowPreparedEffectExecution?> ReconcileOutstandingAsync(
            MailGatewayRuntimeBinding binding,
            MailOutboxHash draftHash,
            MailOutboxHash localBindingHash,
            DateTimeOffset trustedNowUtc,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<MailOutboxOperation> unresolved;
            try
            {
                unresolved = await outbox.ListNetworkStartedUnresolvedAsync(
                    draftHash,
                    localBindingHash,
                    UnresolvedLookupLimit,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return NotStarted(
                    "메일을 보내기 전에 실행이 중단됐어요.",
                    "mail.send.cancelled");
            }
            catch
            {
                return NotStarted(
                    "이전 메일 제출 상태를 확인하지 못했어요.",
                    "mail.send.outbox_unavailable");
            }

            if (unresolved is null || unresolved.Count > UnresolvedLookupLimit)
                return NotStarted(
                    "확인되지 않은 이전 메일 제출이 남아 있어 새로 보내지 않았어요.",
                    "mail.send.reconciliation_required");

            if (unresolved.Count == 0) return null;

            // One newly approved execution performs at most one historical lookup. This keeps
            // aggregate execution time bounded; additional unknown rows require another explicit
            // approval before any new submission may start.
            var operation = unresolved[0];
            if (operation is null ||
                operation.SubmissionState is not (
                    MailOutboxSubmissionState.Submitting or
                    MailOutboxSubmissionState.OutcomeUnknown) ||
                operation.DeliveryState != MailOutboxDeliveryState.Unknown ||
                !operation.DraftHash.Equals(draftHash) ||
                !operation.BindingHash.Equals(localBindingHash))
                return NotStarted(
                    "이전 메일 제출 기록이 현재 승인과 일치하지 않아요.",
                    "mail.send.outbox_unavailable");

            MailGatewayWireResult result;
            try
            {
                result = await wireClient.ReconcileAsync(
                    binding.BaseUri,
                    binding.Credential!.Reveal(),
                    operation.DraftHash,
                    operation.OperationId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return NotStarted(
                    "이전 메일 제출 확인을 중단했어요.",
                    "mail.send.cancelled");
            }
            catch
            {
                return NotStarted(
                    "확인되지 않은 이전 메일 제출이 남아 있어 새로 보내지 않았어요.",
                    "mail.send.reconciliation_required");
            }

            var lookupObservedAtUtc = ReadCurrentUtc();

            if (result.Outcome == MailGatewayWireOutcome.Accepted && result.DeliveryStatus is not null)
            {
                var accepted = await RecordAcceptedAsync(
                    operation,
                    result.DeliveryStatus.Value,
                    trustedNowUtc).ConfigureAwait(false);
                return accepted ?? Unknown(
                    "이전 메일 제출 결과를 기록하지 못했어요.",
                    "mail.send.outcome_unknown");
            }

            if (result.Outcome == MailGatewayWireOutcome.NotFound &&
                !IsAuthoritativeNotFoundWindow(operation, trustedNowUtc, lookupObservedAtUtc))
                return NotStarted(
                    "이전 메일 제출의 중복 억제 조회 시간이 지나 새로 보내지 않았어요.",
                    "mail.send.reconciliation_required");

            if (result.Outcome is MailGatewayWireOutcome.KnownFailed or MailGatewayWireOutcome.NotFound)
            {
                var failed = await TransitionAsync(
                    operation,
                    MailOutboxSubmissionState.FailedKnown,
                    MailOutboxDeliveryState.Failed,
                    operation.AttemptCount,
                    result.Outcome == MailGatewayWireOutcome.NotFound
                        ? "mail.gateway.reconcile.not_found"
                        : "mail.gateway.reconcile.remote_failed",
                    trustedNowUtc,
                    CancellationToken.None).ConfigureAwait(false);
                if (failed is null)
                    return NotStarted(
                        "이전 메일 제출 결과를 기록하지 못했어요.",
                        "mail.send.outbox_unavailable");
                return unresolved.Count == 1
                    ? null
                    : NotStarted(
                        "확인되지 않은 이전 메일 제출이 더 남아 있어 새로 보내지 않았어요.",
                        "mail.send.reconciliation_required");
            }

            return NotStarted(
                "확인되지 않은 이전 메일 제출이 남아 있어 새로 보내지 않았어요.",
                "mail.send.reconciliation_required");
        }

        private async Task<WorkflowPreparedEffectExecution> CompleteSubmissionAsync(
            MailGatewayRuntimeBinding binding,
            MailOutboxOperation submitting,
            MailOutboxHash draftHash,
            MailGatewayWireResult submitted,
            DateTimeOffset trustedNowUtc)
        {
            if (submitted.Outcome == MailGatewayWireOutcome.NotStarted)
            {
                var failed = await TransitionAsync(
                    submitting,
                    MailOutboxSubmissionState.FailedKnown,
                    MailOutboxDeliveryState.Failed,
                    submitting.AttemptCount,
                    "mail.gateway.submit.not_started",
                    trustedNowUtc,
                    CancellationToken.None).ConfigureAwait(false);
                return failed is null
                    ? Unknown("메일 제출 상태를 기록하지 못했어요.", "mail.send.outcome_unknown")
                    : NotStarted("메일 제출을 시작하지 못했어요.", "mail.send.submit_not_started");
            }

            if (submitted.Outcome == MailGatewayWireOutcome.KnownFailed)
            {
                var failed = await TransitionAsync(
                    submitting,
                    MailOutboxSubmissionState.FailedKnown,
                    MailOutboxDeliveryState.Failed,
                    submitting.AttemptCount,
                    "mail.gateway.submit.remote_failed",
                    trustedNowUtc,
                    CancellationToken.None).ConfigureAwait(false);
                return failed is null
                    ? Unknown("메일 제출 상태를 기록하지 못했어요.", "mail.send.outcome_unknown")
                    : FailedKnown("메일 Gateway가 제출을 거부했어요.", "mail.send.submit_failed");
            }

            if (submitted.Outcome == MailGatewayWireOutcome.Accepted &&
                submitted.DeliveryStatus is not null)
                return await RecordAcceptedAsync(
                           submitting,
                           submitted.DeliveryStatus.Value,
                           trustedNowUtc).ConfigureAwait(false)
                       ?? Unknown("메일 제출 결과를 기록하지 못했어요.", "mail.send.outcome_unknown");

            var unknown = await TransitionAsync(
                submitting,
                MailOutboxSubmissionState.OutcomeUnknown,
                MailOutboxDeliveryState.Unknown,
                submitting.AttemptCount,
                "mail.gateway.submit.outcome_unknown",
                trustedNowUtc,
                CancellationToken.None).ConfigureAwait(false);
            if (unknown is null)
                return Unknown("메일 제출 결과를 확정하지 못했어요.", "mail.send.outcome_unknown");

            MailGatewayWireResult reconciled;
            try
            {
                reconciled = await wireClient.ReconcileAsync(
                    binding.BaseUri,
                    binding.Credential!.Reveal(),
                    draftHash,
                    unknown.OperationId,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                return Unknown("메일 제출 결과를 확정하지 못했어요.", "mail.send.outcome_unknown");
            }

            var lookupObservedAtUtc = ReadCurrentUtc();

            if (reconciled.Outcome == MailGatewayWireOutcome.Accepted &&
                reconciled.DeliveryStatus is not null)
                return await RecordAcceptedAsync(
                           unknown,
                           reconciled.DeliveryStatus.Value,
                           trustedNowUtc).ConfigureAwait(false)
                       ?? Unknown("메일 제출 결과를 기록하지 못했어요.", "mail.send.outcome_unknown");

            if (reconciled.Outcome == MailGatewayWireOutcome.NotFound &&
                !IsAuthoritativeNotFoundWindow(unknown, trustedNowUtc, lookupObservedAtUtc))
                return Unknown(
                    "메일 제출의 중복 억제 조회 시간을 확인하지 못했어요.",
                    "mail.send.outcome_unknown");

            if (reconciled.Outcome is MailGatewayWireOutcome.KnownFailed or MailGatewayWireOutcome.NotFound)
            {
                var failed = await TransitionAsync(
                    unknown,
                    MailOutboxSubmissionState.FailedKnown,
                    MailOutboxDeliveryState.Failed,
                    unknown.AttemptCount,
                    reconciled.Outcome == MailGatewayWireOutcome.NotFound
                        ? "mail.gateway.reconcile.not_found"
                        : "mail.gateway.reconcile.remote_failed",
                    trustedNowUtc,
                    CancellationToken.None).ConfigureAwait(false);
                return failed is null
                    ? Unknown("메일 제출 결과를 기록하지 못했어요.", "mail.send.outcome_unknown")
                    : FailedKnown("메일 제출이 완료되지 않았어요.", "mail.send.submit_failed");
            }

            return Unknown("메일 제출 결과를 확정하지 못했어요.", "mail.send.outcome_unknown");
        }

        private static bool IsAuthoritativeNotFoundWindow(
            MailOutboxOperation operation,
            DateTimeOffset executionStartedAtUtc,
            DateTimeOffset? lookupObservedAtUtc)
        {
            // created_at_utc is a durable lower bound for the first network attempt because the
            // outbox row is committed before the staged -> submitting CAS and POST. updated_at_utc
            // is not used for age, but any later transition/recovery timestamp is an additional
            // durable lower bound that detects a wall-clock rollback before this execution.
            var maximumLookupObservedAge = GatewayAuthoritativeLookupWindow - HistoricalLookupSafetyMargin;
            return operation.AttemptCount == 1 &&
                   operation.CreatedAtUtc.Offset == TimeSpan.Zero &&
                   executionStartedAtUtc.Offset == TimeSpan.Zero &&
                   lookupObservedAtUtc is { Offset: var offset } observedAtUtc &&
                   offset == TimeSpan.Zero &&
                   executionStartedAtUtc >= operation.CreatedAtUtc &&
                   executionStartedAtUtc >= operation.UpdatedAtUtc &&
                   observedAtUtc >= executionStartedAtUtc &&
                   observedAtUtc - operation.CreatedAtUtc <= maximumLookupObservedAge;
        }

        private DateTimeOffset? ReadCurrentUtc()
        {
            try
            {
                return timeProvider.GetUtcNow();
            }
            catch
            {
                return null;
            }
        }

        private async Task<WorkflowPreparedEffectExecution?> RecordAcceptedAsync(
            MailOutboxOperation operation,
            MailGatewayWireDeliveryStatus deliveryStatus,
            DateTimeOffset trustedNowUtc)
        {
            var queued = await TransitionAsync(
                operation,
                MailOutboxSubmissionState.Accepted,
                MailOutboxDeliveryState.Queued,
                operation.AttemptCount,
                null,
                trustedNowUtc,
                CancellationToken.None).ConfigureAwait(false);
            if (queued is null) return null;
            if (deliveryStatus == MailGatewayWireDeliveryStatus.Delivered)
            {
                var delivered = await TransitionAsync(
                    queued,
                    MailOutboxSubmissionState.Accepted,
                    MailOutboxDeliveryState.Delivered,
                    queued.AttemptCount,
                    null,
                    trustedNowUtc,
                    CancellationToken.None).ConfigureAwait(false);
                return delivered is null
                    ? Success(queued, MailGatewayWireDeliveryStatus.Queued)
                    : Success(delivered, MailGatewayWireDeliveryStatus.Delivered);
            }
            return Success(queued, deliveryStatus);
        }

        private async Task<MailOutboxOperation?> TransitionAsync(
            MailOutboxOperation operation,
            MailOutboxSubmissionState nextSubmission,
            MailOutboxDeliveryState nextDelivery,
            int nextAttemptCount,
            string? fixedErrorCode,
            DateTimeOffset trustedNowUtc,
            CancellationToken cancellationToken)
        {
            try
            {
                var transition = await outbox.TryTransitionAsync(
                    new MailOutboxTransitionRequest(
                        operation.OperationId,
                        operation.Revision,
                        operation.SubmissionState,
                        operation.DeliveryState,
                        nextSubmission,
                        nextDelivery,
                        nextAttemptCount,
                        fixedErrorCode,
                        trustedNowUtc >= operation.UpdatedAtUtc
                            ? trustedNowUtc
                            : operation.UpdatedAtUtc),
                    cancellationToken).ConfigureAwait(false);
                return transition.Outcome == MailOutboxTransitionOutcome.Succeeded
                    ? transition.Operation
                    : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private WorkflowPreparedEffectExecution? ExistingResult(MailOutboxOperation operation) =>
            (operation.SubmissionState, operation.DeliveryState) switch
            {
                (MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Queued) =>
                    Success(operation, MailGatewayWireDeliveryStatus.Queued),
                (MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Delivered) =>
                    Success(operation, MailGatewayWireDeliveryStatus.Delivered),
                (MailOutboxSubmissionState.FailedKnown, MailOutboxDeliveryState.Failed) or
                (MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Failed) =>
                    FailedKnown("이 메일 제출은 이미 실패로 확정됐어요.", "mail.send.submit_failed"),
                (MailOutboxSubmissionState.OutcomeUnknown, MailOutboxDeliveryState.Unknown) or
                (MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown) =>
                    Unknown("이 메일 제출 결과를 아직 확정하지 못했어요.", "mail.send.outcome_unknown"),
                (MailOutboxSubmissionState.CancelledNotStarted, MailOutboxDeliveryState.Unknown) =>
                    NotStarted("이 메일 제출은 보내기 전에 취소됐어요.", "mail.send.cancelled"),
                _ => null,
            };

        private bool BindingMatches(MailGatewayRuntimeBinding current) =>
            StringComparer.Ordinal.Equals(current.DisplayName, preparedBinding.DisplayName) &&
            StringComparer.Ordinal.Equals(current.BaseUri.AbsoluteUri, preparedBinding.BaseUri.AbsoluteUri) &&
            StringComparer.Ordinal.Equals(current.Sender, preparedBinding.Sender) &&
            StringComparer.Ordinal.Equals(current.FrozenBindingHash, context.ConnectionBindingHash) &&
            StringComparer.Ordinal.Equals(current.FrozenBindingHash, preparedBinding.FrozenBindingHash) &&
            StringComparer.Ordinal.Equals(current.LocalBindingHash, preparedBinding.LocalBindingHash) &&
            StringComparer.Ordinal.Equals(current.ExecutionFingerprint, preparedBinding.ExecutionFingerprint);

        private static bool OperationMatchesRequest(
            MailOutboxOperation? operation,
            MailOutboxCreateRequest request) =>
            operation is not null &&
            StringComparer.Ordinal.Equals(operation.ConversationId, request.ConversationId) &&
            StringComparer.Ordinal.Equals(operation.WorkflowRunId, request.WorkflowRunId) &&
            StringComparer.Ordinal.Equals(operation.ToolCallId, request.ToolCallId) &&
            StringComparer.Ordinal.Equals(operation.CapabilityId, request.CapabilityId) &&
            StringComparer.Ordinal.Equals(operation.CapabilityVersion, request.CapabilityVersion) &&
            operation.CapabilityContentHash.Equals(request.CapabilityContentHash) &&
            operation.CatalogRevision == request.CatalogRevision &&
            operation.SnapshotHash.Equals(request.SnapshotHash) &&
            StringComparer.Ordinal.Equals(operation.ConnectionSlot, request.ConnectionSlot) &&
            operation.BindingHash.Equals(request.BindingHash) &&
            operation.DraftHash.Equals(request.DraftHash);

        private static WindowsToolApprovalPrompt CreatePrompt(
            PreparedBinding binding,
            MailDraft draft) => new(
                "다음 메일을 보낼까요?",
                $"{binding.DisplayName} 연결에서 {binding.Sender} 발신자로 메일을 제출합니다.",
                $"To:\n{FormatRecipients(draft.To)}\n\n" +
                $"Cc:\n{FormatRecipients(draft.Cc)}\n\n" +
                $"Bcc:\n{FormatRecipients(draft.Bcc)}\n\n" +
                $"제목:\n{draft.Subject}\n\n본문:\n{draft.Body}",
                "R3");

        private static string FormatRecipients(IReadOnlyList<string> recipients) =>
            recipients.Count == 0 ? "(없음)" : string.Join("\n", recipients);

        private void ClearSensitive()
        {
            _ = Interlocked.Exchange(ref draft, null);
            Interlocked.Exchange(ref approvalPrompt, RedactedApprovalPrompt);
        }

        private static WorkflowPreparedEffectExecution Success(
            MailOutboxOperation operation,
            MailGatewayWireDeliveryStatus status) => new(
                WorkflowEffectDisposition.Succeeded,
                new WindowsToolExecutionResult(
                    true,
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operation.OperationId.Value,
                        ["status"] = status == MailGatewayWireDeliveryStatus.Delivered
                            ? "delivered"
                            : "queued",
                        ["draftHash"] = operation.DraftHash.Value,
                    },
                    ActivitySummary: status == MailGatewayWireDeliveryStatus.Delivered
                        ? "승인된 메일 제출의 전달 완료를 확인했어요."
                        : "승인된 메일을 Gateway 대기열에 제출했어요."));

        private static WorkflowPreparedEffectExecution NotStarted(string message, string errorCode) =>
            new(
                WorkflowEffectDisposition.NotStarted,
                Failure(message, errorCode));

        private static WorkflowPreparedEffectExecution FailedKnown(string message, string errorCode) =>
            new(
                WorkflowEffectDisposition.FailedKnown,
                Failure(message, errorCode));

        private static WorkflowPreparedEffectExecution Unknown(string message, string errorCode) =>
            new(
                WorkflowEffectDisposition.Unknown,
                Failure(message, errorCode));
    }
}
