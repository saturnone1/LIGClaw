using LIGClaw.Application.Mail;

namespace LIGClaw.Application.Tests;

public sealed class MailSendPolicyTests
{
    [Fact]
    public void ReturnsOnlyCanonicalDraftSlotAndExactHash()
    {
        var slot = "a" + new string('-', MailSendPolicy.MaximumConnectionSlotCharacters - 1);
        var input = ValidInput(
            connectionSlot: slot,
            to: ["owner@example.com"],
            cc: ["reviewer@example.com"],
            bcc: ["archive@example.com"],
            subject: "주간 보고서",
            body: "첫 줄\r\n둘째 줄");

        var result = MailSendPolicy.Validate(input);

        Assert.True(result.IsValid, result.ErrorCode);
        Assert.Null(result.ErrorCode);
        var send = Assert.IsType<ValidatedMailSend>(result.Send);
        Assert.Equal(slot, send.ConnectionSlot);
        Assert.Equal(input.DraftHash, send.DraftHash);
        Assert.Equal("첫 줄\n둘째 줄", send.Draft.Body);
        Assert.Equal(send.Draft.CanonicalHash, send.DraftHash);
        Assert.Equal(
            ["ConnectionSlot", "Draft", "DraftHash"],
            typeof(ValidatedMailSend).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1mail")]
    [InlineData("Mail")]
    [InlineData("mail/api")]
    [InlineData("mail api")]
    public void RejectsNonCanonicalConnectionSlots(string? connectionSlot)
    {
        var result = MailSendPolicy.Validate(ValidInput(connectionSlot: connectionSlot));

        Assert.False(result.IsValid);
        Assert.Equal(MailSendPolicy.ConnectionSlotInvalid, result.ErrorCode);
        Assert.Null(result.Send);
    }

    [Fact]
    public void EnforcesConnectionSlotLengthBoundary()
    {
        var exact = "a" + new string('0', MailSendPolicy.MaximumConnectionSlotCharacters - 1);
        var tooLong = exact + "0";

        Assert.True(MailSendPolicy.Validate(ValidInput(connectionSlot: exact)).IsValid);
        Assert.Equal(
            MailSendPolicy.ConnectionSlotInvalid,
            MailSendPolicy.Validate(ValidInput(connectionSlot: tooLong)).ErrorCode);
    }

    [Fact]
    public void RevalidatesTheExistingDraftContentBoundaries()
    {
        var recipients = Enumerable.Range(0, MailDraftPolicy.MaximumRecipients)
            .Select(index => $"owner{index}@example.com")
            .ToArray();
        var subject = string.Concat(Enumerable.Repeat("😀", MailDraftPolicy.MaximumSubjectScalars));
        var body = new string('한', MailDraftPolicy.MaximumBodyScalars);

        var accepted = MailSendPolicy.Validate(ValidInput(
            to: [recipients[0]],
            cc: recipients[1..],
            subject: subject,
            body: body));
        var rejected = MailSendPolicy.Validate(new MailSendInput(
            "mail",
            ValidInput().DraftHash,
            recipients.Append("overflow@example.com").ToArray(),
            [],
            [],
            "Subject",
            "Body"));

        Assert.True(accepted.IsValid, accepted.ErrorCode);
        Assert.Equal(MailDraftPolicy.MaximumSubjectUtf8Bytes,
            System.Text.Encoding.UTF8.GetByteCount(accepted.Send!.Draft.Subject));
        Assert.Equal(MailDraftPolicy.MaximumBodyUtf8Bytes,
            System.Text.Encoding.UTF8.GetByteCount(accepted.Send.Draft.Body));
        Assert.Equal(MailSendPolicy.DraftInvalid, rejected.ErrorCode);
    }

    [Theory]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("sha257:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaag")]
    public void RejectsHashesOutsideTheExactLowercaseSha256Shape(string draftHash)
    {
        var result = MailSendPolicy.Validate(ValidInput(draftHash: draftHash));

        Assert.False(result.IsValid);
        Assert.Equal(MailSendPolicy.DraftHashInvalid, result.ErrorCode);
    }

    [Fact]
    public void RejectsAWellFormedHashThatDoesNotMatchTheRevalidatedDraft()
    {
        var input = ValidInput(draftHash: "sha256:" + new string('f', 64));

        var result = MailSendPolicy.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal(MailSendPolicy.DraftHashMismatch, result.ErrorCode);
        Assert.Null(result.Send);
    }

    [Fact]
    public void ReusesDraftValidationWithoutLeakingItsInput()
    {
        var validHash = ValidInput().DraftHash;
        var result = MailSendPolicy.Validate(new MailSendInput(
            "mail",
            validHash,
            ["owner@example.com"],
            [],
            [],
            "unsafe\nsubject",
            "raw body"));

        Assert.False(result.IsValid);
        Assert.Equal(MailSendPolicy.DraftInvalid, result.ErrorCode);
        Assert.Null(result.Send);
    }

    [Fact]
    public void InputAndValidatedDraftDefensivelyCopyRecipientCollections()
    {
        var to = new List<string> { "owner@example.com" };
        var input = ValidInput(to: to);
        to[0] = "mutated@example.com";

        var result = MailSendPolicy.Validate(input);
        var send = Assert.IsType<ValidatedMailSend>(result.Send);

        Assert.Equal(["owner@example.com"], input.To);
        Assert.Equal(["owner@example.com"], send.Draft.To);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)input.To)[0] = "blocked@example.com");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)send.Draft.To)[0] = "blocked@example.com");
        Assert.NotSame(input.To, send.Draft.To);
    }

    [Fact]
    public void ErrorsAndToStringNeverEchoRawMailContent()
    {
        const string marker = "raw-secret-marker";
        var validInput = ValidInput(
            connectionSlot: marker,
            to: [$"{marker}@example.com"],
            subject: marker,
            body: marker);
        var valid = MailSendPolicy.Validate(validInput);
        var send = Assert.IsType<ValidatedMailSend>(valid.Send);
        var invalidInput = new MailSendInput(
            marker,
            validInput.DraftHash,
            [$"{marker}@example.com"],
            [],
            [],
            marker + "\ninvalid",
            marker);
        var invalid = MailSendPolicy.Validate(invalidInput);

        Assert.DoesNotContain(marker, validInput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, send.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, valid.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, invalidInput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, invalid.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, invalid.ErrorCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullInputReturnsOnlyTheFixedInputError()
    {
        var result = MailSendPolicy.Validate(null);

        Assert.False(result.IsValid);
        Assert.Equal(MailSendPolicy.InputInvalid, result.ErrorCode);
        Assert.Null(result.Send);
    }

    private static MailSendInput ValidInput(
        string? connectionSlot = "mail",
        string? draftHash = null,
        IReadOnlyList<string>? to = null,
        IReadOnlyList<string>? cc = null,
        IReadOnlyList<string>? bcc = null,
        string? subject = "업무 안내",
        string? body = "확인 부탁드립니다.")
    {
        to ??= ["owner@example.com"];
        cc ??= [];
        bcc ??= [];
        draftHash ??= ValidDraft(to, cc, bcc, subject, body).CanonicalHash;
        return new MailSendInput(connectionSlot, draftHash, to, cc, bcc, subject, body);
    }

    private static MailDraft ValidDraft(
        IReadOnlyList<string> to,
        IReadOnlyList<string> cc,
        IReadOnlyList<string> bcc,
        string? subject,
        string? body)
    {
        var result = MailDraftPolicy.Validate(new MailDraftInput(to, cc, bcc, subject, body));
        Assert.True(result.IsValid, result.ErrorCode);
        return Assert.IsType<MailDraft>(result.Draft);
    }
}
