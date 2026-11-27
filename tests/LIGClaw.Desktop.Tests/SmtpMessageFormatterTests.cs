using System.Runtime.InteropServices;
using System.Text;
using LIGClaw.Application.Mail;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class SmtpMessageFormatterTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void CreatesStableBoundedMimeWithoutBccHeader()
    {
        var draft = Draft(
            ["to@example.invalid", "second@example.invalid"],
            ["cc@example.invalid"],
            ["hidden@example.invalid"],
            "Subject",
            "Body");

        using var first = SmtpMessageFormatter.Create(
            "sender@example.invalid", draft, Operation('a'), CreatedAt);
        using var second = SmtpMessageFormatter.Create(
            "sender@example.invalid", draft, Operation('a'), CreatedAt);
        var text = Encoding.UTF8.GetString(first.Bytes.Span);

        Assert.Equal(first.Bytes.ToArray(), second.Bytes.ToArray());
        Assert.Equal("<ligclaw." + new string('a', 64) + "@ligclaw.invalid>", first.MessageId);
        Assert.StartsWith("Date: Sun, 02 Aug 2026 03:04:05 +0000\r\n", text, StringComparison.Ordinal);
        Assert.Contains($"Message-ID: {first.MessageId}\r\n", text, StringComparison.Ordinal);
        Assert.Contains("From: sender@example.invalid\r\n", text, StringComparison.Ordinal);
        Assert.Contains("To: to@example.invalid,\r\n second@example.invalid\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Cc: cc@example.invalid\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Bcc:", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hidden@example.invalid", text, StringComparison.Ordinal);
        Assert.Contains("Subject: =?UTF-8?B?U3ViamVjdA==?=\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("Qm9keQ==\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnicodeSubjectAndBodyUseDeterministicSafeFolding()
    {
        var subject = string.Concat(Enumerable.Repeat("제목😀", 60));
        var body = string.Concat(Enumerable.Repeat("본문😀\n", 900));
        using var message = SmtpMessageFormatter.Create(
            "sender@example.invalid",
            Draft(["to@example.invalid"], [], [], subject, body),
            Operation('b'),
            CreatedAt);
        var text = Encoding.UTF8.GetString(message.Bytes.Span);
        var lines = text.Split("\r\n", StringSplitOptions.None);

        Assert.All(lines.TakeWhile(line => line.Length > 0), line => Assert.InRange(line.Length, 1, 998));
        var separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(separator > 0);
        var bodyLines = text[(separator + 4)..].Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.All(bodyLines, line => Assert.InRange(line.Length, 1, 76));
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(bodyLines)));
        Assert.Equal(body.Replace("\n", "\r\n", StringComparison.Ordinal), decoded);
        Assert.DoesNotContain(subject, text, StringComparison.Ordinal);
    }

    [Fact]
    public void MessageIdentityChangesOnlyWithOperationAndTimestampInputs()
    {
        var draft = Draft(["to@example.invalid"], [], [], "Subject", "Body");
        using var original = SmtpMessageFormatter.Create("sender@example.invalid", draft, Operation('a'), CreatedAt);
        using var otherOperation = SmtpMessageFormatter.Create("sender@example.invalid", draft, Operation('b'), CreatedAt);
        using var otherTimestamp = SmtpMessageFormatter.Create(
            "sender@example.invalid", draft, Operation('a'), CreatedAt.AddSeconds(1));

        Assert.NotEqual(original.MessageId, otherOperation.MessageId);
        Assert.False(original.Bytes.Span.SequenceEqual(otherOperation.Bytes.Span));
        Assert.Equal(original.MessageId, otherTimestamp.MessageId);
        Assert.False(original.Bytes.Span.SequenceEqual(otherTimestamp.Bytes.Span));
    }

    [Fact]
    public void DiagnosticsDoNotContainRawMailContent()
    {
        const string subject = "RAW-SMTP-SUBJECT-CANARY";
        const string body = "RAW-SMTP-BODY-CANARY";
        using var message = SmtpMessageFormatter.Create(
            "sender@example.invalid",
            Draft(["raw-recipient@example.invalid"], [], [], subject, body),
            Operation('c'),
            CreatedAt);

        var diagnostic = message.ToString();
        Assert.DoesNotContain(subject, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(body, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-recipient", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsInvalidSenderAndNonUtcTimestampBeforeFormatting()
    {
        var draft = Draft(["to@example.invalid"], [], [], "Subject", "Body");

        Assert.Throws<ArgumentException>(() => SmtpMessageFormatter.Create(
            "sender\r\n@example.invalid", draft, Operation('d'), CreatedAt));
        Assert.Throws<ArgumentException>(() => SmtpMessageFormatter.Create(
            "sender@example.invalid", draft, Operation('d'), CreatedAt.ToOffset(TimeSpan.FromHours(9))));
    }

    [Fact]
    public void DisposeZeroesOwnedMailBytes()
    {
        var message = SmtpMessageFormatter.Create(
            "sender@example.invalid",
            Draft(["to@example.invalid"], [], [], "RAW-SUBJECT-CANARY", "RAW-BODY-CANARY"),
            Operation('e'),
            CreatedAt);
        var owned = message.Bytes;
        Assert.True(MemoryMarshal.TryGetArray(owned, out var segment));
        Assert.NotNull(segment.Array);
        Assert.Contains(segment.Array!, value => value != 0);

        message.Dispose();

        Assert.Empty(message.Bytes.ToArray());
        Assert.All(segment.Array!.Skip(segment.Offset).Take(segment.Count), value => Assert.Equal(0, value));
    }

    private static MailDraft Draft(
        IReadOnlyList<string> to,
        IReadOnlyList<string> cc,
        IReadOnlyList<string> bcc,
        string subject,
        string body) =>
        Assert.IsType<MailDraft>(MailDraftPolicy.Validate(new MailDraftInput(to, cc, bcc, subject, body)).Draft);

    private static MailOutboxOperationId Operation(char value) =>
        MailOutboxOperationId.Parse($"mgo_{new string(value, 64)}");
}
