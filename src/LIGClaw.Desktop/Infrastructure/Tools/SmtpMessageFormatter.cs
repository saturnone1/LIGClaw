using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LIGClaw.Application.Mail;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class SmtpCanonicalMessage : IDisposable
{
    private byte[]? bytes;

    internal SmtpCanonicalMessage(string messageId, byte[] bytes)
    {
        MessageId = messageId;
        this.bytes = bytes;
    }

    public string MessageId { get; }
    public ReadOnlyMemory<byte> Bytes => Volatile.Read(ref bytes) ?? ReadOnlyMemory<byte>.Empty;

    public override string ToString() =>
        $"SmtpCanonicalMessage(MessageId={MessageId}, ByteLength={Bytes.Length})";

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref bytes, null);
        if (value is not null) CryptographicOperations.ZeroMemory(value);
    }
}

internal static class SmtpMessageFormatter
{
    private const int SubjectWordMaximumUtf8Bytes = 30;
    private const int Base64LineLength = 76;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static SmtpCanonicalMessage Create(
        string sender,
        MailDraft draft,
        MailOutboxOperationId operationId,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(operationId);
        if (!MailAddressPolicy.IsValidAsciiMailbox(sender))
            throw new ArgumentException("SMTP sender 형식이 올바르지 않습니다.", nameof(sender));
        if (createdAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("SMTP message timestamp는 UTC여야 합니다.", nameof(createdAtUtc));
        if (!StringComparer.Ordinal.Equals(MailDraftPolicy.ComputeCanonicalHash(draft), draft.CanonicalHash))
            throw new ArgumentException("SMTP draft identity가 올바르지 않습니다.", nameof(draft));

        var operationHex = operationId.Value.AsSpan(4);
        var messageId = $"<ligclaw.{operationHex}@ligclaw.invalid>";
        var headers = new StringBuilder(2_048);
        headers.Append("Date: ")
            .Append(createdAtUtc.ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture))
            .Append("\r\nMessage-ID: ")
            .Append(messageId)
            .Append("\r\nFrom: ")
            .Append(sender)
            .Append("\r\n");
        AppendAddressHeader(headers, "To", draft.To);
        if (draft.Cc.Count > 0) AppendAddressHeader(headers, "Cc", draft.Cc);
        AppendSubjectHeader(headers, draft.Subject);
        headers.Append("MIME-Version: 1.0\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append("Content-Transfer-Encoding: base64\r\n\r\n");

        var headerBytes = StrictUtf8.GetBytes(headers.ToString());
        var bodyBytes = EncodeBodyWithCrlf(draft.Body);
        byte[]? messageBytes = null;
        try
        {
            const int inputBytesPerLine = Base64LineLength / 4 * 3;
            var encodedLength = checked(((bodyBytes.Length + 2) / 3) * 4);
            var lineCount = Math.Max(1, (bodyBytes.Length + inputBytesPerLine - 1) / inputBytesPerLine);
            messageBytes = new byte[checked(headerBytes.Length + encodedLength + lineCount * 2)];
            headerBytes.CopyTo(messageBytes, 0);
            var sourceOffset = 0;
            var destinationOffset = headerBytes.Length;
            while (sourceOffset < bodyBytes.Length)
            {
                var sourceLength = Math.Min(inputBytesPerLine, bodyBytes.Length - sourceOffset);
                var expectedWritten = checked(((sourceLength + 2) / 3) * 4);
                var status = Base64.EncodeToUtf8(
                    bodyBytes.AsSpan(sourceOffset, sourceLength),
                    messageBytes.AsSpan(destinationOffset, expectedWritten),
                    out var consumed,
                    out var written,
                    isFinalBlock: true);
                if (status != OperationStatus.Done || consumed != sourceLength || written != expectedWritten)
                    throw new InvalidOperationException("SMTP body를 base64로 인코딩하지 못했습니다.");
                sourceOffset += consumed;
                destinationOffset += written;
                messageBytes[destinationOffset++] = (byte)'\r';
                messageBytes[destinationOffset++] = (byte)'\n';
            }
            if (bodyBytes.Length == 0)
            {
                messageBytes[destinationOffset++] = (byte)'\r';
                messageBytes[destinationOffset++] = (byte)'\n';
            }
            if (destinationOffset != messageBytes.Length)
                throw new InvalidOperationException("SMTP message 길이가 일치하지 않습니다.");
            var result = new SmtpCanonicalMessage(messageId, messageBytes);
            messageBytes = null;
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(headerBytes);
            CryptographicOperations.ZeroMemory(bodyBytes);
            if (messageBytes is not null) CryptographicOperations.ZeroMemory(messageBytes);
        }
    }

    private static void AppendAddressHeader(
        StringBuilder builder,
        string name,
        IReadOnlyList<string> addresses)
    {
        if (addresses.Count == 0 || addresses.Any(address => !MailAddressPolicy.IsValidAsciiMailbox(address)))
            throw new ArgumentException("SMTP recipient 형식이 올바르지 않습니다.", nameof(addresses));
        builder.Append(name).Append(": ");
        for (var index = 0; index < addresses.Count; index++)
        {
            if (index > 0) builder.Append(",\r\n ");
            builder.Append(addresses[index]);
        }
        builder.Append("\r\n");
    }

    private static byte[] EncodeBodyWithCrlf(string body)
    {
        var newlineCount = body.Count(character => character == '\n');
        var result = new byte[checked(StrictUtf8.GetByteCount(body) + newlineCount)];
        var sourceOffset = 0;
        var destinationOffset = 0;
        for (var index = 0; index < body.Length; index++)
        {
            if (body[index] != '\n') continue;
            destinationOffset += StrictUtf8.GetBytes(
                body.AsSpan(sourceOffset, index - sourceOffset),
                result.AsSpan(destinationOffset));
            result[destinationOffset++] = (byte)'\r';
            result[destinationOffset++] = (byte)'\n';
            sourceOffset = index + 1;
        }
        destinationOffset += StrictUtf8.GetBytes(
            body.AsSpan(sourceOffset),
            result.AsSpan(destinationOffset));
        if (destinationOffset != result.Length)
        {
            CryptographicOperations.ZeroMemory(result);
            throw new InvalidOperationException("SMTP body CRLF 정규화 길이가 일치하지 않습니다.");
        }
        return result;
    }

    private static void AppendSubjectHeader(StringBuilder builder, string subject)
    {
        builder.Append("Subject: ");
        Span<byte> chunk = stackalloc byte[SubjectWordMaximumUtf8Bytes];
        var chunkLength = 0;
        var first = true;
        Span<byte> encoded = stackalloc byte[4];
        try
        {
            foreach (var rune in subject.EnumerateRunes())
            {
                if (!rune.TryEncodeToUtf8(encoded, out var count))
                    throw new EncoderFallbackException("SMTP subject를 UTF-8로 인코딩할 수 없습니다.");
                if (chunkLength > 0 && chunkLength + count > SubjectWordMaximumUtf8Bytes)
                {
                    AppendEncodedWord(builder, chunk[..chunkLength], first);
                    first = false;
                    CryptographicOperations.ZeroMemory(chunk);
                    chunkLength = 0;
                }
                encoded[..count].CopyTo(chunk[chunkLength..]);
                chunkLength += count;
            }
            if (chunkLength == 0)
                throw new ArgumentException("SMTP subject가 비어 있습니다.", nameof(subject));
            AppendEncodedWord(builder, chunk[..chunkLength], first);
            builder.Append("\r\n");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
            CryptographicOperations.ZeroMemory(chunk);
        }
    }

    private static void AppendEncodedWord(StringBuilder builder, ReadOnlySpan<byte> bytes, bool first)
    {
        if (!first) builder.Append("\r\n ");
        builder.Append("=?UTF-8?B?")
            .Append(Convert.ToBase64String([.. bytes]))
            .Append("?=");
    }
}
