using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LIGClaw.Application.Mail;

public static class MailDraftPolicy
{
    public const int MaximumAddressCharacters = MailAddressPolicy.MaximumAddressCharacters;
    public const int MaximumRecipients = 16;
    public const int MaximumSubjectScalars = 200;
    public const int MaximumSubjectUtf8Bytes = 800;
    public const int MaximumBodyScalars = 8_192;
    public const int MaximumBodyUtf8Bytes = 24_576;

    public const string InputInvalid = "mail.draft.input_invalid";
    public const string ToRequired = "mail.draft.to_required";
    public const string RecipientCountInvalid = "mail.draft.recipient_count_invalid";
    public const string RecipientInvalid = "mail.draft.recipient_invalid";
    public const string RecipientDuplicate = "mail.draft.recipient_duplicate";
    public const string SubjectInvalid = "mail.draft.subject_invalid";
    public const string BodyInvalid = "mail.draft.body_invalid";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static MailDraftValidationResult Validate(MailDraftInput? input)
    {
        if (input is null) return MailDraftValidationResult.Invalid(InputInvalid);
        if (input.To.Count == 0) return MailDraftValidationResult.Invalid(ToRequired);

        var recipientCount = (long)input.To.Count + input.Cc.Count + input.Bcc.Count;
        if (recipientCount is < 1 or > MaximumRecipients)
            return MailDraftValidationResult.Invalid(RecipientCountInvalid);

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipients in new[] { input.To, input.Cc, input.Bcc })
        {
            foreach (var address in recipients)
            {
                if (!MailAddressPolicy.IsValidAsciiMailbox(address))
                    return MailDraftValidationResult.Invalid(RecipientInvalid);
                if (!unique.Add(address))
                    return MailDraftValidationResult.Invalid(RecipientDuplicate);
            }
        }

        if (!IsValidSubject(input.Subject))
            return MailDraftValidationResult.Invalid(SubjectInvalid);

        if (input.Body is null) return MailDraftValidationResult.Invalid(BodyInvalid);
        var normalizedBody = NormalizeBody(input.Body);
        if (!IsValidBody(normalizedBody))
            return MailDraftValidationResult.Invalid(BodyInvalid);

        var to = Array.AsReadOnly(input.To.ToArray());
        var cc = Array.AsReadOnly(input.Cc.ToArray());
        var bcc = Array.AsReadOnly(input.Bcc.ToArray());
        var canonicalHash = ComputeCanonicalHash(to, cc, bcc, input.Subject!, normalizedBody);
        return MailDraftValidationResult.Valid(new MailDraft(
            to,
            cc,
            bcc,
            input.Subject!,
            normalizedBody,
            canonicalHash));
    }

    public static string ComputeCanonicalHash(MailDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return ComputeCanonicalHash(draft.To, draft.Cc, draft.Bcc, draft.Subject, draft.Body);
    }

    private static string ComputeCanonicalHash(
        IReadOnlyList<string> to,
        IReadOnlyList<string> cc,
        IReadOnlyList<string> bcc,
        string subject,
        string normalizedBody)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "domain", "mail-draft-v1");
        AppendRecipients(hash, "to", to);
        AppendRecipients(hash, "cc", cc);
        AppendRecipients(hash, "bcc", bcc);
        Append(hash, "subject", subject);
        Append(hash, "body", normalizedBody);
        return $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private static void AppendRecipients(
        IncrementalHash hash,
        string category,
        IReadOnlyList<string> recipients)
    {
        Append(hash, $"{category}.count", recipients.Count.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < recipients.Count; index++)
            Append(hash, $"{category}.{index}.address", recipients[index]);
    }

    private static void Append(IncrementalHash hash, string name, string value)
    {
        var nameBytes = StrictUtf8.GetBytes(name);
        var valueBytes = StrictUtf8.GetBytes(value);
        Span<byte> frame = stackalloc byte[12];
        BinaryPrimitives.WriteInt32BigEndian(frame, nameBytes.Length);
        BinaryPrimitives.WriteInt64BigEndian(frame[4..], valueBytes.LongLength);
        hash.AppendData(frame);
        hash.AppendData(nameBytes);
        hash.AppendData(valueBytes);
    }

    private static bool IsValidSubject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !TryMeasureUtf8(value, MaximumSubjectScalars, MaximumSubjectUtf8Bytes))
            return false;
        return value.EnumerateRunes().All(rune => Rune.GetUnicodeCategory(rune) is not (
            UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator));
    }

    private static bool IsValidBody(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !TryMeasureUtf8(value, MaximumBodyScalars, MaximumBodyUtf8Bytes))
            return false;
        return value.EnumerateRunes().All(rune =>
            Rune.GetUnicodeCategory(rune) != UnicodeCategory.Control || rune.Value is '\t' or '\n');
    }

    private static bool TryMeasureUtf8(string value, int maximumScalars, int maximumBytes)
    {
        try
        {
            if (StrictUtf8.GetByteCount(value) > maximumBytes) return false;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        var scalarCount = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            if (++scalarCount > maximumScalars) return false;
        }
        return scalarCount > 0;
    }

    private static string NormalizeBody(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
