using System.Buffers.Text;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LIGClaw.Application.Connections;
using LIGClaw.Application.Mail;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal enum SmtpWireOutcome
{
    NotStarted,
    Accepted,
    KnownFailed,
    OutcomeUnknown,
}

internal enum SmtpDataPhaseMarkOutcome
{
    NotRecorded,
    Recorded,
    Indeterminate,
}

internal sealed record SmtpWireResult(SmtpWireOutcome Outcome, string? ErrorCode = null)
{
    public override string ToString() => $"SmtpWireResult(Outcome={Outcome}, ErrorCode={ErrorCode})";
}

internal sealed record SmtpWireProbeResult(bool Succeeded, string? ErrorCode = null)
{
    public override string ToString() =>
        $"SmtpWireProbeResult(Succeeded={Succeeded}, ErrorCode={ErrorCode})";
}

internal sealed class SmtpWireCredential : IDisposable
{
    public const int MaximumUsernameBytes = 128;
    public const int MaximumPasswordBytes = 224;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object gate = new();
    private byte[]? usernameBytes;
    private byte[]? passwordBytes;

    public SmtpWireCredential(string username, string password)
    {
        if (!IsValidUsername(username) || !IsValidPassword(password))
            throw new ArgumentException("SMTP credential 형식이 올바르지 않습니다.");
        usernameBytes = Encoding.ASCII.GetBytes(username);
        passwordBytes = StrictUtf8.GetBytes(password);
    }

    internal byte[] CreateAuthPlainCommand()
    {
        byte[] sasl;
        lock (gate)
        {
            var currentUsername = usernameBytes ??
                                  throw new ObjectDisposedException(nameof(SmtpWireCredential));
            var currentPassword = passwordBytes ??
                                  throw new ObjectDisposedException(nameof(SmtpWireCredential));
            sasl = new byte[checked(2 + currentUsername.Length + currentPassword.Length)];
            currentUsername.CopyTo(sasl, 1);
            currentPassword.CopyTo(sasl, 2 + currentUsername.Length);
        }
        var encodedLength = Base64.GetMaxEncodedToUtf8Length(sasl.Length);
        var command = new byte[checked(11 + encodedLength + 2)];
        "AUTH PLAIN "u8.CopyTo(command);
        System.Buffers.OperationStatus status;
        int consumed;
        int written;
        try
        {
            status = Base64.EncodeToUtf8(
                sasl,
                command.AsSpan(11, encodedLength),
                out consumed,
                out written);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(command);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sasl);
        }
        if (status != System.Buffers.OperationStatus.Done ||
            consumed != sasl.Length)
        {
            CryptographicOperations.ZeroMemory(command);
            throw new InvalidOperationException("SMTP AUTH payload를 만들지 못했습니다.");
        }
        command[11 + written] = (byte)'\r';
        command[12 + written] = (byte)'\n';
        if (11 + written + 2 != command.Length)
        {
            CryptographicOperations.ZeroMemory(command);
            throw new InvalidOperationException("SMTP AUTH payload 길이가 올바르지 않습니다.");
        }
        return command;
    }

    public override string ToString() => "[redacted SMTP credential]";

    public void Dispose()
    {
        lock (gate)
        {
            if (usernameBytes is not null) CryptographicOperations.ZeroMemory(usernameBytes);
            if (passwordBytes is not null) CryptographicOperations.ZeroMemory(passwordBytes);
            usernameBytes = null;
            passwordBytes = null;
        }
    }

    internal static bool IsValidUsername(string? value) =>
        value is { Length: >= 1 and <= MaximumUsernameBytes } &&
        value.All(character => character is >= (char)0x21 and <= (char)0x7e);

    internal static bool IsValidPassword(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\0')) return false;
        try
        {
            return StrictUtf8.GetByteCount(value) <= MaximumPasswordBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}

internal interface ISmtpWireConnection : IAsyncDisposable
{
    bool IsTls { get; }
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
    ValueTask FlushAsync(CancellationToken cancellationToken);
    Task UpgradeToTlsAsync(string targetHost, CancellationToken cancellationToken);
}

internal interface ISmtpWireConnectionFactory
{
    Task<ISmtpWireConnection> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken);
}

internal sealed class SmtpWireClient
{
    private const int MaximumReplyLineBytes = 512;
    private const int MaximumReplyLines = 32;
    private const int MaximumReplyBytes = 16 * 1024;
    private const int MaximumMessageBytes = 64 * 1024;
    private static readonly TimeSpan ProductionOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(1);
    private static readonly byte[] EhloCommand = "EHLO ligclaw.local\r\n"u8.ToArray();
    private static readonly byte[] StartTlsCommand = "STARTTLS\r\n"u8.ToArray();
    private static readonly byte[] DataCommand = "DATA\r\n"u8.ToArray();
    private static readonly byte[] DataTerminator = ".\r\n"u8.ToArray();
    private static readonly byte[] QuitCommand = "QUIT\r\n"u8.ToArray();
    private readonly ISmtpWireConnectionFactory connectionFactory;
    private readonly TimeSpan operationTimeout;

    public SmtpWireClient() : this(new DefaultSmtpWireConnectionFactory(), ProductionOperationTimeout)
    {
    }

    internal SmtpWireClient(ISmtpWireConnectionFactory connectionFactory) :
        this(connectionFactory, ProductionOperationTimeout)
    {
    }

    internal SmtpWireClient(
        ISmtpWireConnectionFactory connectionFactory,
        TimeSpan operationTimeout)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        if (operationTimeout <= TimeSpan.Zero || operationTimeout > ProductionOperationTimeout)
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        this.operationTimeout = operationTimeout;
    }

    public async Task<SmtpWireProbeResult> ProbeAsync(
        string canonicalHost,
        int port,
        SmtpTlsMode tlsMode,
        SmtpWireCredential credential,
        CancellationToken cancellationToken)
    {
        if (!HasValidEndpoint(canonicalHost, port, tlsMode) || credential is null)
            return new SmtpWireProbeResult(false, "smtp.probe.input_invalid");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(operationTimeout);
        ISmtpWireConnection? connection = null;
        try
        {
            connection = await connectionFactory.ConnectAsync(
                canonicalHost, port, timeout.Token).ConfigureAwait(false);
            _ = await AuthenticateAsync(
                connection, canonicalHost, tlsMode, credential, timeout.Token).ConfigureAwait(false);
            await TryQuitAsync(connection).ConfigureAwait(false);
            return new SmtpWireProbeResult(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SmtpWireProbeResult(false, "smtp.probe.cancelled");
        }
        catch (SmtpProtocolException exception)
        {
            return new SmtpWireProbeResult(false, ProbeError(exception.ErrorCode));
        }
        catch
        {
            return new SmtpWireProbeResult(false, "smtp.probe.failed");
        }
        finally
        {
            await DisposeIgnoringNetworkErrorsAsync(connection).ConfigureAwait(false);
        }
    }

    public async Task<SmtpWireResult> SubmitAsync(
        string canonicalHost,
        int port,
        SmtpTlsMode tlsMode,
        SmtpWireCredential credential,
        string sender,
        MailDraft draft,
        MailOutboxOperationId operationId,
        DateTimeOffset createdAtUtc,
        Func<CancellationToken, Task<SmtpDataPhaseMarkOutcome>> markDataStarted,
        CancellationToken cancellationToken)
    {
        if (!HasValidEndpoint(canonicalHost, port, tlsMode) || credential is null ||
            !MailAddressPolicy.IsValidAsciiMailbox(sender) || draft is null || operationId is null ||
            createdAtUtc.Offset != TimeSpan.Zero || markDataStarted is null)
            return NotStarted("mail.smtp.input_invalid");

        SmtpCanonicalMessage message;
        try
        {
            message = SmtpMessageFormatter.Create(sender, draft, operationId, createdAtUtc);
        }
        catch
        {
            return NotStarted("mail.smtp.input_invalid");
        }
        using (message)
        {
            if (message.Bytes.Length is < 1 or > MaximumMessageBytes)
                return NotStarted("mail.smtp.message_too_large");

            var commitUncertain = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(operationTimeout);
            ISmtpWireConnection? connection = null;
            try
            {
                connection = await connectionFactory.ConnectAsync(
                    canonicalHost, port, timeout.Token).ConfigureAwait(false);
                var reader = await AuthenticateAsync(
                    connection, canonicalHost, tlsMode, credential, timeout.Token).ConfigureAwait(false);

                await SendAsciiCommandAsync(
                    connection, $"MAIL FROM:<{sender}>\r\n", timeout.Token).ConfigureAwait(false);
                RequireCode(await reader.ReadReplyAsync(timeout.Token).ConfigureAwait(false), 250, "mail.smtp.envelope_rejected");
                foreach (var recipient in draft.To.Concat(draft.Cc).Concat(draft.Bcc))
                {
                    await SendAsciiCommandAsync(
                        connection, $"RCPT TO:<{recipient}>\r\n", timeout.Token).ConfigureAwait(false);
                    var recipientReply = await reader.ReadReplyAsync(timeout.Token).ConfigureAwait(false);
                    if (recipientReply.Code is not (250 or 251))
                        throw new SmtpProtocolException("mail.smtp.envelope_rejected");
                }

                await WriteAsync(connection, DataCommand, timeout.Token).ConfigureAwait(false);
                RequireCode(await reader.ReadReplyAsync(timeout.Token).ConfigureAwait(false), 354, "mail.smtp.data_rejected");
                SmtpDataPhaseMarkOutcome phaseMarkOutcome;
                try
                {
                    phaseMarkOutcome = await markDataStarted(timeout.Token).ConfigureAwait(false);
                }
                catch
                {
                    phaseMarkOutcome = SmtpDataPhaseMarkOutcome.Indeterminate;
                }
                if (phaseMarkOutcome == SmtpDataPhaseMarkOutcome.NotRecorded)
                    return KnownFailed("mail.smtp.data_phase_not_recorded");
                if (phaseMarkOutcome != SmtpDataPhaseMarkOutcome.Recorded)
                    return Unknown();
                commitUncertain = true;
                await WriteAsync(connection, message.Bytes, timeout.Token).ConfigureAwait(false);
                await WriteAsync(connection, DataTerminator, timeout.Token).ConfigureAwait(false);
                await connection.FlushAsync(timeout.Token).ConfigureAwait(false);

                SmtpReply finalReply;
                try
                {
                    finalReply = await reader.ReadReplyAsync(timeout.Token).ConfigureAwait(false);
                }
                catch
                {
                    return Unknown();
                }
                if (finalReply.Code == 250)
                {
                    await TryQuitAsync(connection).ConfigureAwait(false);
                    return new SmtpWireResult(SmtpWireOutcome.Accepted);
                }
                if (finalReply.Code is >= 400 and <= 599)
                    return KnownFailed("mail.smtp.remote_rejected");
                return Unknown();
            }
            catch (OperationCanceledException)
            {
                return commitUncertain ? Unknown() : KnownFailed("mail.smtp.submit_failed_known");
            }
            catch (SmtpProtocolException exception)
            {
                return commitUncertain ? Unknown() : KnownFailed(exception.ErrorCode);
            }
            catch
            {
                return commitUncertain ? Unknown() : KnownFailed("mail.smtp.submit_failed_known");
            }
            finally
            {
                await DisposeIgnoringNetworkErrorsAsync(connection).ConfigureAwait(false);
            }
        }
    }

    internal static SslClientAuthenticationOptions CreateTlsOptions(string targetHost)
    {
        if (!SmtpConnectionProfilePolicy.TryNormalizeHost(targetHost, out var normalized) ||
            !StringComparer.Ordinal.Equals(targetHost, normalized))
            throw new ArgumentException("SMTP TLS target host가 canonical 형식이 아닙니다.", nameof(targetHost));
        return new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.Online,
            EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            AllowRenegotiation = false,
        };
    }

    private static bool HasValidEndpoint(string host, int port, SmtpTlsMode tlsMode) =>
        SmtpConnectionProfilePolicy.TryNormalizeHost(host, out var normalized) &&
        StringComparer.Ordinal.Equals(host, normalized) &&
        port is >= SmtpConnectionProfilePolicy.MinimumPort and <= SmtpConnectionProfilePolicy.MaximumPort &&
        Enum.IsDefined(tlsMode);

    private static async Task<SmtpReplyReader> AuthenticateAsync(
        ISmtpWireConnection connection,
        string host,
        SmtpTlsMode tlsMode,
        SmtpWireCredential credential,
        CancellationToken cancellationToken)
    {
        if (tlsMode == SmtpTlsMode.ImplicitTls)
            await connection.UpgradeToTlsAsync(host, cancellationToken).ConfigureAwait(false);
        var reader = new SmtpReplyReader(connection);
        RequireCode(await reader.ReadReplyAsync(cancellationToken).ConfigureAwait(false), 220, "mail.smtp.greeting_rejected");

        await WriteAsync(connection, EhloCommand, cancellationToken).ConfigureAwait(false);
        var ehlo = await reader.ReadReplyAsync(cancellationToken).ConfigureAwait(false);
        RequireCode(ehlo, 250, "mail.smtp.ehlo_rejected");
        if (tlsMode == SmtpTlsMode.StartTlsRequired)
        {
            if (!HasExtension(ehlo, "STARTTLS"))
                throw new SmtpProtocolException("mail.smtp.starttls_required");
            await WriteAsync(connection, StartTlsCommand, cancellationToken).ConfigureAwait(false);
            RequireCode(
                await reader.ReadReplyAsync(cancellationToken).ConfigureAwait(false),
                220,
                "mail.smtp.starttls_rejected");
            await connection.UpgradeToTlsAsync(host, cancellationToken).ConfigureAwait(false);
            reader = new SmtpReplyReader(connection);
            await WriteAsync(connection, EhloCommand, cancellationToken).ConfigureAwait(false);
            ehlo = await reader.ReadReplyAsync(cancellationToken).ConfigureAwait(false);
            RequireCode(ehlo, 250, "mail.smtp.ehlo_rejected");
        }
        if (!connection.IsTls)
            throw new SmtpProtocolException("mail.smtp.tls_required");
        if (!HasAuthPlain(ehlo))
            throw new SmtpProtocolException("mail.smtp.auth_not_supported");

        var auth = credential.CreateAuthPlainCommand();
        try
        {
            await WriteAsync(connection, auth, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(auth);
        }
        RequireCode(await reader.ReadReplyAsync(cancellationToken).ConfigureAwait(false), 235, "mail.smtp.auth_rejected");
        return reader;
    }

    private static bool HasExtension(SmtpReply reply, string extension) =>
        reply.TextLines.Skip(1).Any(line => StringComparer.OrdinalIgnoreCase.Equals(line, extension));

    private static bool HasAuthPlain(SmtpReply reply)
    {
        foreach (var line in reply.TextLines.Skip(1))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length >= 2 && StringComparer.OrdinalIgnoreCase.Equals(tokens[0], "AUTH") &&
                tokens.Skip(1).Any(token => StringComparer.OrdinalIgnoreCase.Equals(token, "PLAIN")))
                return true;
        }
        return false;
    }

    private static void RequireCode(SmtpReply reply, int expected, string errorCode)
    {
        if (reply.Code != expected) throw new SmtpProtocolException(errorCode);
    }

    private static async Task SendAsciiCommandAsync(
        ISmtpWireConnection connection,
        string command,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(command);
        try
        {
            if (bytes.Length > MaximumReplyLineBytes || bytes.Any(value => value > 0x7f))
                throw new SmtpProtocolException("mail.smtp.command_invalid");
            await WriteAsync(connection, bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async Task WriteAsync(
        ISmtpWireConnection connection,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await connection.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await connection.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryQuitAsync(ISmtpWireConnection connection)
    {
        using var timeout = new CancellationTokenSource(QuitTimeout);
        try
        {
            await WriteAsync(connection, QuitCommand, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // QUIT is best effort and cannot change a previously established result.
        }
    }

    private static async Task DisposeIgnoringNetworkErrorsAsync(ISmtpWireConnection? connection)
    {
        if (connection is null) return;
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // SMTP 결과는 서버 응답/지속 경계로 결정한다. 정리 실패가 결과를 뒤집으면 안 된다.
        }
    }

    private static string ProbeError(string submitError) => submitError switch
    {
        "mail.smtp.starttls_required" => "smtp.probe.starttls_required",
        "mail.smtp.auth_not_supported" => "smtp.probe.auth_not_supported",
        "mail.smtp.auth_rejected" => "smtp.probe.auth_rejected",
        "mail.smtp.tls_required" => "smtp.probe.tls_required",
        _ => "smtp.probe.failed",
    };

    private static SmtpWireResult NotStarted(string code) => new(SmtpWireOutcome.NotStarted, code);
    private static SmtpWireResult KnownFailed(string code) => new(SmtpWireOutcome.KnownFailed, code);
    private static SmtpWireResult Unknown() => new(SmtpWireOutcome.OutcomeUnknown, "mail.smtp.outcome_unknown");

    private sealed class SmtpProtocolException(string errorCode) : Exception
    {
        public string ErrorCode { get; } = errorCode;
    }

    private sealed class SmtpReply
    {
        public SmtpReply(int code, IReadOnlyList<string> textLines)
        {
            Code = code;
            TextLines = textLines;
        }

        public int Code { get; }
        public IReadOnlyList<string> TextLines { get; }
        public override string ToString() => $"SmtpReply(Code={Code}, LineCount={TextLines.Count})";
    }

    private sealed class SmtpReplyReader(ISmtpWireConnection connection)
    {
        private readonly byte[] readBuffer = new byte[1_024];
        private int readOffset;
        private int readCount;

        public async Task<SmtpReply> ReadReplyAsync(CancellationToken cancellationToken)
        {
            var lines = new List<string>();
            var totalBytes = 0;
            int? replyCode = null;
            for (var lineIndex = 0; lineIndex < MaximumReplyLines; lineIndex++)
            {
                var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                totalBytes = checked(totalBytes + line.Length + 2);
                if (totalBytes > MaximumReplyBytes || line.Length < 3 ||
                    line[0] is < '2' or > '5' || line[1] is < '0' or > '9' || line[2] is < '0' or > '9' ||
                    line.Length > 3 && line[3] is not ('-' or ' '))
                    throw new SmtpProtocolException("mail.smtp.reply_invalid");
                var code = (line[0] - '0') * 100 + (line[1] - '0') * 10 + line[2] - '0';
                if (replyCode is not null && replyCode != code)
                    throw new SmtpProtocolException("mail.smtp.reply_invalid");
                replyCode = code;
                var separator = line.Length == 3 ? ' ' : line[3];
                lines.Add(line.Length > 4 ? line[4..] : string.Empty);
                if (separator == ' ')
                    return new SmtpReply(code, Array.AsReadOnly(lines.ToArray()));
            }
            throw new SmtpProtocolException("mail.smtp.reply_invalid");
        }

        private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new byte[MaximumReplyLineBytes - 2];
            var length = 0;
            while (true)
            {
                var current = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (current < 0) throw new EndOfStreamException("SMTP reply가 끝났습니다.");
                if (current == '\r')
                {
                    if (await ReadByteAsync(cancellationToken).ConfigureAwait(false) != '\n')
                        throw new SmtpProtocolException("mail.smtp.reply_invalid");
                    return Encoding.ASCII.GetString(line, 0, length);
                }
                if (current is < 0x20 or > 0x7e || length == line.Length)
                    throw new SmtpProtocolException("mail.smtp.reply_invalid");
                line[length++] = (byte)current;
            }
        }

        private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (readOffset < readCount) return readBuffer[readOffset++];
            readCount = await connection.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
            readOffset = 0;
            return readCount == 0 ? -1 : readBuffer[readOffset++];
        }
    }
}

internal sealed class DefaultSmtpWireConnectionFactory : ISmtpWireConnectionFactory
{
    public async Task<ISmtpWireConnection> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new DefaultSmtpWireConnection(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

internal sealed class DefaultSmtpWireConnection(TcpClient client) : ISmtpWireConnection
{
    private Stream stream = client.GetStream();

    public bool IsTls { get; private set; }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        stream.ReadAsync(buffer, cancellationToken);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
        stream.WriteAsync(buffer, cancellationToken);

    public ValueTask FlushAsync(CancellationToken cancellationToken) =>
        new(stream.FlushAsync(cancellationToken));

    public async Task UpgradeToTlsAsync(string targetHost, CancellationToken cancellationToken)
    {
        if (IsTls) throw new InvalidOperationException("SMTP TLS는 한 번만 시작할 수 있습니다.");
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(
                SmtpWireClient.CreateTlsOptions(targetHost), cancellationToken).ConfigureAwait(false);
            if (ssl.SslProtocol is not (SslProtocols.Tls12 or SslProtocols.Tls13))
                throw new AuthenticationException("SMTP TLS 1.2 이상이 필요합니다.");
            stream = ssl;
            IsTls = true;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            client.Dispose();
        }
    }
}
