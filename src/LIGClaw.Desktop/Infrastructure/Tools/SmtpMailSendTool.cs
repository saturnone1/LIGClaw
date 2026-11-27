using System.Collections;
using System.Globalization;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Application.Mail;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class SmtpMailSendTool : IWorkflowOneShotToolAdapter
{
    private readonly ISmtpExecutionResolver resolver;
    private readonly IMailOutboxRepository outbox;
    private readonly SmtpWireClient wireClient;

    public SmtpMailSendTool(
        ISmtpExecutionResolver resolver,
        IMailOutboxRepository outbox,
        SmtpWireClient wireClient)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        this.wireClient = wireClient ?? throw new ArgumentNullException(nameof(wireClient));
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
                "고정된 Workflow SMTP 메일 컨텍스트를 확인하지 못했어요.",
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

        SmtpExecutionResolution resolution;
        try
        {
            resolution = await resolver.ResolveAsync(
                context,
                send.ConnectionSlot,
                SmtpResolutionPurpose.Preparation,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PreparationFailure(
                "등록된 SMTP 연결을 확인하지 못했어요.",
                "mail.smtp.binding_unavailable");
        }

        await using var unexpectedLease = resolution.ExecutionLease;
        using var binding = resolution.Binding;
        if (!resolution.Success || binding is null || unexpectedLease is not null)
            return PreparationFailure(
                "등록된 SMTP 연결을 확인하지 못했어요.",
                NormalizeResolverError(resolution.ErrorCode));
        if (!TryValidateBinding(binding, requireCredential: false) ||
            !StringComparer.Ordinal.Equals(binding.FrozenBindingHash, context.ConnectionBindingHash))
            return PreparationFailure(
                "Workflow에 고정된 SMTP 연결이 현재 연결과 일치하지 않아요.",
                "mail.smtp.binding_mismatch");

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
            string.Concat(binding.Host),
            binding.Port,
            binding.TlsMode,
            binding.Authentication,
            binding.CertificatePolicy,
            string.Concat(binding.Sender),
            string.Concat(binding.FrozenBindingHash),
            string.Concat(binding.LocalBindingHash),
            string.Concat(binding.ExecutionFingerprint));
        return new WorkflowEffectPreparationResult(
            true,
            new PreparedSmtpMailSendEffect(
                resolver,
                outbox,
                wireClient,
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

    private static bool TryValidateBinding(SmtpRuntimeBinding binding, bool requireCredential)
    {
        try
        {
            return binding.DisplayName is { Length: >= 1 and <= 80 } &&
                   binding.DisplayName.Trim() == binding.DisplayName &&
                   !binding.DisplayName.Any(char.IsControl) &&
                   SmtpConnectionProfilePolicy.TryNormalizeHost(binding.Host, out var canonicalHost) &&
                   StringComparer.Ordinal.Equals(binding.Host, canonicalHost) &&
                   binding.Port is >= SmtpConnectionProfilePolicy.MinimumPort and <= SmtpConnectionProfilePolicy.MaximumPort &&
                   binding.TlsMode is SmtpTlsMode.StartTlsRequired or SmtpTlsMode.ImplicitTls &&
                   binding.Authentication == SmtpAuthenticationMechanism.Plain &&
                   binding.CertificatePolicy == SmtpCertificatePolicy.SystemTrust &&
                   MailAddressPolicy.IsValidAsciiMailbox(binding.Sender) &&
                   IsHash(binding.FrozenBindingHash) && IsHash(binding.LocalBindingHash) &&
                   IsHash(binding.ExecutionFingerprint) &&
                   (requireCredential ? binding.Credential is not null : binding.Credential is null);
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
        "mail.smtp.binding_unavailable" => errorCode,
        "mail.smtp.binding_mismatch" => errorCode,
        "mail.smtp.credential_unavailable" => errorCode,
        _ => "mail.smtp.binding_unavailable",
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
        string Host,
        int Port,
        SmtpTlsMode TlsMode,
        SmtpAuthenticationMechanism Authentication,
        SmtpCertificatePolicy CertificatePolicy,
        string Sender,
        string FrozenBindingHash,
        string LocalBindingHash,
        string ExecutionFingerprint);

    private sealed class PreparedSmtpMailSendEffect : IPreparedWorkflowEffect
    {
        private const int UnresolvedLookupLimit = 2;
        private static readonly WindowsToolApprovalPrompt RedactedApprovalPrompt = new(
            "메일 발송 승인이 종료됐어요.",
            "메일 원문 preview는 메모리에서 해제됐습니다.",
            "이 one-shot 동작은 다시 사용할 수 없습니다.",
            "R3");
        private readonly ISmtpExecutionResolver resolver;
        private readonly IMailOutboxRepository outbox;
        private readonly SmtpWireClient wireClient;
        private readonly CapabilityInvocationContext context;
        private readonly string conversationId;
        private readonly string workflowRunId;
        private readonly string toolCallId;
        private readonly string connectionSlot;
        private readonly PreparedBinding preparedBinding;
        private MailDraft? draft;
        private WindowsToolApprovalPrompt approvalPrompt;
        private int state;

        public PreparedSmtpMailSendEffect(
            ISmtpExecutionResolver resolver,
            IMailOutboxRepository outbox,
            SmtpWireClient wireClient,
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
                "smtp",
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
                preparedBinding.Host,
                preparedBinding.Port.ToString(CultureInfo.InvariantCulture),
                ((int)preparedBinding.TlsMode).ToString(CultureInfo.InvariantCulture),
                ((int)preparedBinding.Authentication).ToString(CultureInfo.InvariantCulture),
                ((int)preparedBinding.CertificatePolicy).ToString(CultureInfo.InvariantCulture),
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

        public override string ToString() => "Prepared SMTP Mail Send effect [redacted]";

        private async Task<WorkflowPreparedEffectExecution> ExecuteCoreAsync(
            MailDraft executionDraft,
            DateTimeOffset trustedNowUtc,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return NotStarted("메일을 보내기 전에 실행이 중단됐어요.", "mail.send.cancelled");

            SmtpExecutionResolution resolution;
            try
            {
                resolution = await resolver.ResolveAsync(
                    context,
                    connectionSlot,
                    SmtpResolutionPurpose.Execution,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return NotStarted("메일을 보내기 전에 실행이 중단됐어요.", "mail.send.cancelled");
            }
            catch
            {
                return NotStarted(
                    "등록된 SMTP 연결을 다시 확인하지 못했어요.",
                    "mail.smtp.binding_unavailable");
            }

            await using var executionLease = resolution.ExecutionLease;
            using var currentBinding = resolution.Binding;
            if (!resolution.Success || currentBinding is null || executionLease is null)
                return NotStarted(
                    "등록된 SMTP 연결을 다시 확인하지 못했어요.",
                    NormalizeResolverError(resolution.ErrorCode));
            if (!TryValidateBinding(currentBinding, requireCredential: true))
                return NotStarted(
                    "현재 등록된 SMTP 연결 설정을 안전하게 사용할 수 없어요.",
                    "mail.send.binding_invalid");
            if (!BindingMatches(currentBinding))
                return NotStarted(
                    "승인 후 등록된 SMTP 연결이 변경되어 보내지 않았어요.",
                    "mail.smtp.binding_mismatch");

            var draftHash = MailOutboxHash.Parse(executionDraft.CanonicalHash);
            var localBindingHash = MailOutboxHash.Parse(preparedBinding.LocalBindingHash);
            var unresolved = await CheckUnresolvedAsync(draftHash, cancellationToken).ConfigureAwait(false);
            if (unresolved is not null) return unresolved;

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
                    trustedNowUtc,
                    MailOutboxTransportKind.Smtp);
                created = await outbox.CreateOrGetAsync(createRequest, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return NotStarted("메일을 보내기 전에 실행이 중단됐어요.", "mail.send.cancelled");
            }
            catch
            {
                return NotStarted(
                    "SMTP 제출 기록을 준비하지 못했어요.",
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
                    "SMTP 제출 기록의 고정 정보가 현재 승인과 일치하지 않아요.",
                    "mail.send.outbox_unavailable");
            if (created.Outcome == MailOutboxCreateOutcome.Existing)
            {
                var existing = ExistingResult(operation);
                if (existing is not null) return existing;
            }
            if (!IsStagedSmtp(operation))
                return NotStarted(
                    "기존 SMTP 제출 상태를 안전하게 이어갈 수 없어요.",
                    "mail.send.outbox_unavailable");

            if (cancellationToken.IsCancellationRequested)
                return await CancelStagedAsync(operation, trustedNowUtc).ConfigureAwait(false);

            var submitting = await TransitionAsync(
                operation,
                MailOutboxSubmissionState.Submitting,
                MailOutboxDeliveryState.Unknown,
                1,
                null,
                trustedNowUtc,
                MailOutboxSmtpPhase.PreData,
                cancellationToken).ConfigureAwait(false);
            if (submitting is null)
                return NotStarted(
                    "SMTP 제출 상태를 확정하지 못해 보내지 않았어요.",
                    "mail.send.outbox_unavailable");

            if (cancellationToken.IsCancellationRequested)
                return await FailBeforeDataAsync(
                    submitting,
                    "mail.send.cancelled",
                    trustedNowUtc,
                    asCancellation: true).ConfigureAwait(false);

            MailOutboxOperation? durableDataStarted = null;
            async Task<SmtpDataPhaseMarkOutcome> MarkDataStartedAsync(CancellationToken markCancellation)
            {
                try
                {
                    var transition = await outbox.TryTransitionAsync(
                        new MailOutboxTransitionRequest(
                            submitting.OperationId,
                            submitting.Revision,
                            submitting.SubmissionState,
                            submitting.DeliveryState,
                            MailOutboxSubmissionState.Submitting,
                            MailOutboxDeliveryState.Unknown,
                            submitting.AttemptCount,
                            null,
                            trustedNowUtc >= submitting.UpdatedAtUtc
                                ? trustedNowUtc
                                : submitting.UpdatedAtUtc,
                            MailOutboxSmtpPhase.DataStarted),
                        markCancellation).ConfigureAwait(false);
                    if (transition.Outcome == MailOutboxTransitionOutcome.Succeeded &&
                        transition.Operation is { } transitioned)
                    {
                        durableDataStarted = transitioned;
                        return SmtpDataPhaseMarkOutcome.Recorded;
                    }
                }
                catch
                {
                }

                MailOutboxOperation? current;
                try
                {
                    current = await outbox.GetAsync(submitting.OperationId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    return SmtpDataPhaseMarkOutcome.Indeterminate;
                }
                if (!SameOperationIdentity(current, submitting))
                    return SmtpDataPhaseMarkOutcome.Indeterminate;
                if (current!.SubmissionState == MailOutboxSubmissionState.Submitting &&
                    current.DeliveryState == MailOutboxDeliveryState.Unknown &&
                    current.AttemptCount == 1 &&
                    current.SmtpPhase == MailOutboxSmtpPhase.DataStarted)
                {
                    durableDataStarted = current;
                    return SmtpDataPhaseMarkOutcome.Recorded;
                }
                if (current.SmtpPhase == MailOutboxSmtpPhase.PreData &&
                    current.SubmissionState is MailOutboxSubmissionState.Submitting or
                        MailOutboxSubmissionState.FailedKnown)
                    return SmtpDataPhaseMarkOutcome.NotRecorded;
                return SmtpDataPhaseMarkOutcome.Indeterminate;
            }

            SmtpWireResult wireResult;
            try
            {
                wireResult = await wireClient.SubmitAsync(
                    currentBinding.Host,
                    currentBinding.Port,
                    currentBinding.TlsMode,
                    currentBinding.Credential!,
                    currentBinding.Sender,
                    executionDraft,
                    submitting.OperationId,
                    submitting.CreatedAtUtc,
                    MarkDataStartedAsync,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                wireResult = new SmtpWireResult(
                    SmtpWireOutcome.OutcomeUnknown,
                    "mail.smtp.outcome_unknown");
            }

            return await CompleteSubmissionAsync(
                submitting,
                durableDataStarted,
                wireResult,
                trustedNowUtc).ConfigureAwait(false);
        }

        private async Task<WorkflowPreparedEffectExecution?> CheckUnresolvedAsync(
            MailOutboxHash draftHash,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<MailOutboxOperation> unresolved;
            try
            {
                unresolved = await outbox.ListSmtpNetworkStartedUnresolvedAsync(
                    draftHash,
                    UnresolvedLookupLimit,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return NotStarted("메일을 보내기 전에 실행이 중단됐어요.", "mail.send.cancelled");
            }
            catch
            {
                return NotStarted(
                    "이전 SMTP 제출 상태를 확인하지 못했어요.",
                    "mail.send.outbox_unavailable");
            }
            if (unresolved is null || unresolved.Count > UnresolvedLookupLimit ||
                unresolved.Any(operation => operation is null ||
                    operation.TransportKind != MailOutboxTransportKind.Smtp ||
                    !operation.DraftHash.Equals(draftHash) ||
                    operation.SubmissionState is not (
                        MailOutboxSubmissionState.Submitting or
                        MailOutboxSubmissionState.OutcomeUnknown) ||
                    operation.DeliveryState != MailOutboxDeliveryState.Unknown))
                return NotStarted(
                    "이전 SMTP 제출 기록을 안전하게 확인하지 못했어요.",
                    "mail.send.outbox_unavailable");
            return unresolved.Count == 0
                ? null
                : UnknownWithMessageId(
                    unresolved[0],
                    "접수 여부가 확인되지 않은 이전 SMTP 메일이 있어 새로 보내지 않았어요.",
                    "mail.send.outcome_unknown");
        }

        private async Task<WorkflowPreparedEffectExecution> CompleteSubmissionAsync(
            MailOutboxOperation submitting,
            MailOutboxOperation? durableDataStarted,
            SmtpWireResult wireResult,
            DateTimeOffset trustedNowUtc)
        {
            var latest = await ReadLatestAsync(submitting.OperationId).ConfigureAwait(false) ??
                         durableDataStarted ?? submitting;
            var terminal = ExistingResult(latest);
            if (terminal is not null) return terminal;

            if (wireResult.Outcome == SmtpWireOutcome.Accepted)
            {
                if (!IsDataStartedSubmitting(latest))
                    return UnknownWithMessageId(
                        latest,
                        "SMTP 접수 결과를 저널에 연결하지 못했어요.",
                        "mail.send.outcome_unknown");
                var accepted = await TransitionAsync(
                    latest,
                    MailOutboxSubmissionState.Accepted,
                    MailOutboxDeliveryState.Queued,
                    latest.AttemptCount,
                    null,
                    trustedNowUtc,
                    MailOutboxSmtpPhase.DataStarted,
                    CancellationToken.None).ConfigureAwait(false);
                return accepted is null
                    ? UnknownWithMessageId(
                        latest,
                        "SMTP 접수 결과를 기록하지 못했어요.",
                        "mail.send.outcome_unknown")
                    : Success(accepted);
            }

            if (wireResult.Outcome == SmtpWireOutcome.OutcomeUnknown)
            {
                if (IsPreDataSubmitting(latest))
                {
                    var known = await TransitionAsync(
                        latest,
                        MailOutboxSubmissionState.FailedKnown,
                        MailOutboxDeliveryState.Failed,
                        latest.AttemptCount,
                        "mail.smtp.data_phase_not_recorded",
                        trustedNowUtc,
                        MailOutboxSmtpPhase.PreData,
                        CancellationToken.None).ConfigureAwait(false);
                    return known is null
                        ? UnknownWithMessageId(
                            latest,
                            "SMTP DATA 시작 여부를 확정하지 못했어요.",
                            "mail.send.outcome_unknown")
                        : FailedKnown(
                            "SMTP DATA를 시작하지 못해 메일을 보내지 않았어요.",
                            "mail.send.submit_failed");
                }
                if (!IsDataStartedSubmitting(latest))
                    return UnknownWithMessageId(
                        latest,
                        "SMTP 접수 여부를 확정하지 못했어요.",
                        "mail.send.outcome_unknown");
                var unknown = await TransitionAsync(
                    latest,
                    MailOutboxSubmissionState.OutcomeUnknown,
                    MailOutboxDeliveryState.Unknown,
                    latest.AttemptCount,
                    "mail.smtp.submit.outcome_unknown",
                    trustedNowUtc,
                    MailOutboxSmtpPhase.DataStarted,
                    CancellationToken.None).ConfigureAwait(false);
                return UnknownWithMessageId(
                    unknown ?? latest,
                    "SMTP 서버의 메일 접수 여부를 확인해야 해요.",
                    "mail.send.outcome_unknown");
            }

            if (!IsPreDataSubmitting(latest) && !IsDataStartedSubmitting(latest))
                return UnknownWithMessageId(
                    latest,
                    "SMTP 실패 결과를 저널에 연결하지 못했어요.",
                    "mail.send.outcome_unknown");
            var failed = await TransitionAsync(
                latest,
                MailOutboxSubmissionState.FailedKnown,
                MailOutboxDeliveryState.Failed,
                latest.AttemptCount,
                wireResult.ErrorCode is { Length: >= 3 and <= 128 } code
                    ? code
                    : "mail.smtp.submit.failed_known",
                trustedNowUtc,
                latest.SmtpPhase,
                CancellationToken.None).ConfigureAwait(false);
            if (failed is null)
                return UnknownWithMessageId(
                    latest,
                    "SMTP 실패 결과를 기록하지 못했어요.",
                    "mail.send.outcome_unknown");
            return wireResult.Outcome == SmtpWireOutcome.NotStarted
                ? NotStarted("SMTP 제출을 시작하지 못했어요.", "mail.send.submit_not_started")
                : FailedKnown("SMTP 서버가 메일 제출을 완료하지 않았어요.", "mail.send.submit_failed");
        }

        private async Task<WorkflowPreparedEffectExecution> CancelStagedAsync(
            MailOutboxOperation operation,
            DateTimeOffset trustedNowUtc)
        {
            var cancelled = await TransitionAsync(
                operation,
                MailOutboxSubmissionState.CancelledNotStarted,
                MailOutboxDeliveryState.Unknown,
                0,
                "mail.send.cancelled",
                trustedNowUtc,
                MailOutboxSmtpPhase.PreData,
                CancellationToken.None).ConfigureAwait(false);
            return cancelled is null
                ? NotStarted(
                    "취소된 SMTP 제출 상태를 기록하지 못했어요.",
                    "mail.send.outbox_unavailable")
                : NotStarted("메일을 보내기 전에 실행이 중단됐어요.", "mail.send.cancelled");
        }

        private async Task<WorkflowPreparedEffectExecution> FailBeforeDataAsync(
            MailOutboxOperation operation,
            string fixedErrorCode,
            DateTimeOffset trustedNowUtc,
            bool asCancellation)
        {
            var failed = await TransitionAsync(
                operation,
                MailOutboxSubmissionState.FailedKnown,
                MailOutboxDeliveryState.Failed,
                operation.AttemptCount,
                fixedErrorCode,
                trustedNowUtc,
                MailOutboxSmtpPhase.PreData,
                CancellationToken.None).ConfigureAwait(false);
            if (failed is null)
                return UnknownWithMessageId(
                    operation,
                    "SMTP 제출 상태를 기록하지 못했어요.",
                    "mail.send.outcome_unknown");
            return asCancellation
                ? NotStarted("메일을 보내기 전에 실행이 중단됐어요.", "mail.send.cancelled")
                : FailedKnown("SMTP DATA를 시작하지 못했어요.", "mail.send.submit_failed");
        }

        private async Task<MailOutboxOperation?> ReadLatestAsync(
            MailOutboxOperationId operationId)
        {
            try
            {
                return await outbox.GetAsync(operationId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        private async Task<MailOutboxOperation?> TransitionAsync(
            MailOutboxOperation operation,
            MailOutboxSubmissionState nextSubmission,
            MailOutboxDeliveryState nextDelivery,
            int nextAttemptCount,
            string? fixedErrorCode,
            DateTimeOffset trustedNowUtc,
            MailOutboxSmtpPhase? nextSmtpPhase,
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
                            : operation.UpdatedAtUtc,
                        nextSmtpPhase),
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
            operation.TransportKind != MailOutboxTransportKind.Smtp
                ? NotStarted(
                    "메일 제출 transport가 현재 승인과 일치하지 않아요.",
                    "mail.send.outbox_conflict")
                : (operation.SubmissionState, operation.DeliveryState, operation.SmtpPhase) switch
                {
                    (MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Queued,
                        MailOutboxSmtpPhase.DataStarted) => Success(operation),
                    (MailOutboxSubmissionState.FailedKnown, MailOutboxDeliveryState.Failed, _) =>
                        FailedKnown(
                            "이 SMTP 메일 제출은 이미 실패로 확정됐어요.",
                            "mail.send.submit_failed"),
                    (MailOutboxSubmissionState.OutcomeUnknown, MailOutboxDeliveryState.Unknown,
                        MailOutboxSmtpPhase.DataStarted) =>
                        UnknownWithMessageId(
                            operation,
                            "이 SMTP 메일의 접수 여부를 확인해야 해요.",
                            "mail.send.outcome_unknown"),
                    (MailOutboxSubmissionState.CancelledNotStarted, MailOutboxDeliveryState.Unknown,
                        MailOutboxSmtpPhase.PreData) =>
                        NotStarted(
                            "이 SMTP 메일 제출은 보내기 전에 취소됐어요.",
                            "mail.send.cancelled"),
                    _ => null,
                };

        private bool BindingMatches(SmtpRuntimeBinding current) =>
            StringComparer.Ordinal.Equals(current.DisplayName, preparedBinding.DisplayName) &&
            StringComparer.Ordinal.Equals(current.Host, preparedBinding.Host) &&
            current.Port == preparedBinding.Port && current.TlsMode == preparedBinding.TlsMode &&
            current.Authentication == preparedBinding.Authentication &&
            current.CertificatePolicy == preparedBinding.CertificatePolicy &&
            StringComparer.Ordinal.Equals(current.Sender, preparedBinding.Sender) &&
            StringComparer.Ordinal.Equals(current.FrozenBindingHash, context.ConnectionBindingHash) &&
            StringComparer.Ordinal.Equals(current.FrozenBindingHash, preparedBinding.FrozenBindingHash) &&
            StringComparer.Ordinal.Equals(current.LocalBindingHash, preparedBinding.LocalBindingHash) &&
            StringComparer.Ordinal.Equals(current.ExecutionFingerprint, preparedBinding.ExecutionFingerprint);

        private static bool OperationMatchesRequest(
            MailOutboxOperation? operation,
            MailOutboxCreateRequest request) =>
            operation is not null &&
            operation.TransportKind == MailOutboxTransportKind.Smtp &&
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
            operation.DraftHash.Equals(request.DraftHash) &&
            StringComparer.Ordinal.Equals(
                operation.SmtpMessageId,
                MailOutboxPolicy.ComputeSmtpMessageId(operation.OperationId));

        private static bool SameOperationIdentity(
            MailOutboxOperation? current,
            MailOutboxOperation expected) =>
            current is not null && current.OperationId.Equals(expected.OperationId) &&
            current.TransportKind == MailOutboxTransportKind.Smtp &&
            current.DraftHash.Equals(expected.DraftHash) &&
            current.BindingHash.Equals(expected.BindingHash) &&
            StringComparer.Ordinal.Equals(current.ToolCallId, expected.ToolCallId) &&
            StringComparer.Ordinal.Equals(
                current.SmtpMessageId,
                MailOutboxPolicy.ComputeSmtpMessageId(current.OperationId));

        private static bool IsStagedSmtp(MailOutboxOperation operation) =>
            operation.TransportKind == MailOutboxTransportKind.Smtp &&
            operation.SubmissionState == MailOutboxSubmissionState.Staged &&
            operation.DeliveryState == MailOutboxDeliveryState.Unknown &&
            operation.SmtpPhase == MailOutboxSmtpPhase.PreData &&
            operation.AttemptCount == 0;

        private static bool IsPreDataSubmitting(MailOutboxOperation operation) =>
            operation.TransportKind == MailOutboxTransportKind.Smtp &&
            operation.SubmissionState == MailOutboxSubmissionState.Submitting &&
            operation.DeliveryState == MailOutboxDeliveryState.Unknown &&
            operation.SmtpPhase == MailOutboxSmtpPhase.PreData &&
            operation.AttemptCount == 1;

        private static bool IsDataStartedSubmitting(MailOutboxOperation operation) =>
            operation.TransportKind == MailOutboxTransportKind.Smtp &&
            operation.SubmissionState == MailOutboxSubmissionState.Submitting &&
            operation.DeliveryState == MailOutboxDeliveryState.Unknown &&
            operation.SmtpPhase == MailOutboxSmtpPhase.DataStarted &&
            operation.AttemptCount == 1;

        private static WindowsToolApprovalPrompt CreatePrompt(
            PreparedBinding binding,
            MailDraft draft) => new(
                "다음 메일을 SMTP로 보낼까요?",
                $"{binding.DisplayName} ({binding.Host}:{binding.Port}) 연결에서 " +
                $"{binding.Sender} 발신자로 메일을 제출합니다.",
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

        private static WorkflowPreparedEffectExecution Success(MailOutboxOperation operation) =>
            new(
                WorkflowEffectDisposition.Succeeded,
                new WindowsToolExecutionResult(
                    true,
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operation.OperationId.Value,
                        ["status"] = "queued",
                        ["draftHash"] = operation.DraftHash.Value,
                    },
                    ActivitySummary: "승인된 메일을 SMTP 서버 대기열에 제출했어요."));

        private static WorkflowPreparedEffectExecution NotStarted(
            string message,
            string errorCode) =>
            new(WorkflowEffectDisposition.NotStarted, Failure(message, errorCode));

        private static WorkflowPreparedEffectExecution FailedKnown(
            string message,
            string errorCode) =>
            new(WorkflowEffectDisposition.FailedKnown, Failure(message, errorCode));

        private static WorkflowPreparedEffectExecution UnknownWithMessageId(
            MailOutboxOperation operation,
            string message,
            string errorCode) =>
            new(
                WorkflowEffectDisposition.Unknown,
                new WindowsToolExecutionResult(
                    false,
                    new Dictionary<string, object?>(),
                    message,
                    ActivitySummary: operation.SmtpMessageId is null
                        ? "메일 서버의 접수 여부를 확인해야 해요."
                        : $"메일 서버의 접수 여부를 확인해야 해요. 메시지 식별자: {operation.SmtpMessageId}",
                    ErrorCode: errorCode));
    }
}
