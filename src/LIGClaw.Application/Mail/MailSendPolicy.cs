namespace LIGClaw.Application.Mail;

public sealed class MailSendInput
{
    public MailSendInput(
        string? connectionSlot,
        string? draftHash,
        IReadOnlyList<string>? to,
        IReadOnlyList<string>? cc,
        IReadOnlyList<string>? bcc,
        string? subject,
        string? body)
    {
        ConnectionSlot = connectionSlot;
        DraftHash = draftHash;
        To = Copy(to);
        Cc = Copy(cc);
        Bcc = Copy(bcc);
        Subject = subject;
        Body = body;
    }

    public string? ConnectionSlot { get; }
    public string? DraftHash { get; }
    public IReadOnlyList<string> To { get; }
    public IReadOnlyList<string> Cc { get; }
    public IReadOnlyList<string> Bcc { get; }
    public string? Subject { get; }
    public string? Body { get; }

    public override string ToString() =>
        $"MailSendInput(ToCount={To.Count}, CcCount={Cc.Count}, BccCount={Bcc.Count}, " +
        $"HasConnectionSlot={ConnectionSlot is not null}, HasDraftHash={DraftHash is not null}, " +
        $"HasSubject={Subject is not null}, HasBody={Body is not null})";

    private static IReadOnlyList<string> Copy(IReadOnlyList<string>? source) =>
        Array.AsReadOnly((source ?? []).ToArray());
}

public sealed class ValidatedMailSend
{
    internal ValidatedMailSend(string connectionSlot, string draftHash, MailDraft draft)
    {
        ConnectionSlot = connectionSlot;
        DraftHash = draftHash;
        Draft = new MailDraft(
            draft.To,
            draft.Cc,
            draft.Bcc,
            draft.Subject,
            draft.Body,
            draft.CanonicalHash);
    }

    public string ConnectionSlot { get; }
    public string DraftHash { get; }
    public MailDraft Draft { get; }

    public override string ToString() =>
        $"ValidatedMailSend(ToCount={Draft.To.Count}, CcCount={Draft.Cc.Count}, " +
        $"BccCount={Draft.Bcc.Count})";
}

public sealed class MailSendValidationResult
{
    private MailSendValidationResult(ValidatedMailSend? send, string? errorCode)
    {
        Send = send;
        ErrorCode = errorCode;
    }

    public bool IsValid => Send is not null && ErrorCode is null;
    public ValidatedMailSend? Send { get; }
    public string? ErrorCode { get; }

    internal static MailSendValidationResult Valid(ValidatedMailSend send) => new(send, null);

    internal static MailSendValidationResult Invalid(string errorCode) => new(null, errorCode);

    public override string ToString() => IsValid
        ? "MailSendValidationResult(IsValid=True)"
        : $"MailSendValidationResult(IsValid=False, ErrorCode={ErrorCode})";
}

public static class MailSendPolicy
{
    public const int MaximumConnectionSlotCharacters = 64;

    public const string InputInvalid = "mail.send.input_invalid";
    public const string ConnectionSlotInvalid = "mail.send.connection_slot_invalid";
    public const string DraftHashInvalid = "mail.send.draft_hash_invalid";
    public const string DraftInvalid = "mail.send.draft_invalid";
    public const string DraftHashMismatch = "mail.send.draft_hash_mismatch";

    public static MailSendValidationResult Validate(MailSendInput? input)
    {
        if (input is null) return MailSendValidationResult.Invalid(InputInvalid);
        if (!IsCanonicalConnectionSlot(input.ConnectionSlot))
            return MailSendValidationResult.Invalid(ConnectionSlotInvalid);
        if (!IsCanonicalHash(input.DraftHash))
            return MailSendValidationResult.Invalid(DraftHashInvalid);

        var draftValidation = MailDraftPolicy.Validate(new MailDraftInput(
            input.To,
            input.Cc,
            input.Bcc,
            input.Subject,
            input.Body));
        if (!draftValidation.IsValid || draftValidation.Draft is not { } draft)
            return MailSendValidationResult.Invalid(DraftInvalid);
        if (!StringComparer.Ordinal.Equals(input.DraftHash, draft.CanonicalHash))
            return MailSendValidationResult.Invalid(DraftHashMismatch);

        return MailSendValidationResult.Valid(new ValidatedMailSend(
            input.ConnectionSlot!,
            input.DraftHash!,
            draft));
    }

    private static bool IsCanonicalConnectionSlot(string? value) =>
        value is { Length: >= 1 and <= MaximumConnectionSlotCharacters } &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' ||
                               char.IsAsciiDigit(character) || character is '.' or '_' or '-');

    private static bool IsCanonicalHash(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef");
}
