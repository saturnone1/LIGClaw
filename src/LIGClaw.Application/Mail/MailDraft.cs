namespace LIGClaw.Application.Mail;

public sealed class MailDraftInput
{
    public MailDraftInput(
        IReadOnlyList<string>? to,
        IReadOnlyList<string>? cc,
        IReadOnlyList<string>? bcc,
        string? subject,
        string? body)
    {
        To = Copy(to);
        Cc = Copy(cc);
        Bcc = Copy(bcc);
        Subject = subject;
        Body = body;
    }

    public IReadOnlyList<string> To { get; }
    public IReadOnlyList<string> Cc { get; }
    public IReadOnlyList<string> Bcc { get; }
    public string? Subject { get; }
    public string? Body { get; }

    public override string ToString() =>
        $"MailDraftInput(ToCount={To.Count}, CcCount={Cc.Count}, BccCount={Bcc.Count}, " +
        $"HasSubject={Subject is not null}, HasBody={Body is not null})";

    private static IReadOnlyList<string> Copy(IReadOnlyList<string>? source) =>
        Array.AsReadOnly((source ?? []).ToArray());
}

public sealed class MailDraft
{
    internal MailDraft(
        IReadOnlyList<string> to,
        IReadOnlyList<string> cc,
        IReadOnlyList<string> bcc,
        string subject,
        string body,
        string canonicalHash)
    {
        To = Array.AsReadOnly(to.ToArray());
        Cc = Array.AsReadOnly(cc.ToArray());
        Bcc = Array.AsReadOnly(bcc.ToArray());
        Subject = subject;
        Body = body;
        CanonicalHash = canonicalHash;
    }

    public IReadOnlyList<string> To { get; }
    public IReadOnlyList<string> Cc { get; }
    public IReadOnlyList<string> Bcc { get; }
    public string Subject { get; }
    public string Body { get; }
    public string CanonicalHash { get; }

    public override string ToString() =>
        $"MailDraft(ToCount={To.Count}, CcCount={Cc.Count}, BccCount={Bcc.Count}, " +
        $"CanonicalHash={CanonicalHash})";
}

public sealed class MailDraftValidationResult
{
    private MailDraftValidationResult(MailDraft? draft, string? errorCode)
    {
        Draft = draft;
        ErrorCode = errorCode;
    }

    public bool IsValid => Draft is not null && ErrorCode is null;
    public MailDraft? Draft { get; }
    public string? ErrorCode { get; }

    internal static MailDraftValidationResult Valid(MailDraft draft) => new(draft, null);

    internal static MailDraftValidationResult Invalid(string errorCode) => new(null, errorCode);

    public override string ToString() => IsValid
        ? "MailDraftValidationResult(IsValid=True)"
        : $"MailDraftValidationResult(IsValid=False, ErrorCode={ErrorCode})";
}
