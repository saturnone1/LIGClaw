using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LIGClaw.Application.Connections;
using LIGClaw.Application.Mail;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class SmtpWireClientTests
{
    private const string Host = "smtp.example.invalid";
    private const int Port = 587;
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task StartTlsSubmitUsesFixedHandshakeAndQueuesOnlyAfterFinal250()
    {
        var connection = StartTlsConnection(
            TlsReplies(
                "250 sender accepted",
                "250 recipient accepted",
                "250 hidden accepted",
                "354 send content",
                "250 queued"));
        var factory = new ScriptedFactory(connection);
        var draft = Draft(["to@example.invalid"], [], ["hidden@example.invalid"]);
        var dataStartedAtWrite = -1;

        var result = await new SmtpWireClient(factory).SubmitAsync(
            Host,
            Port,
            SmtpTlsMode.StartTlsRequired,
            Credential(),
            "sender@example.invalid",
            draft,
            Operation(),
            CreatedAt,
            _ =>
            {
                dataStartedAtWrite = connection.Writes.Count;
                return Task.FromResult(SmtpDataPhaseMarkOutcome.Recorded);
            },
            CancellationToken.None);

        Assert.Equal(SmtpWireOutcome.Accepted, result.Outcome);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(1, connection.UpgradeCount);
        Assert.Equal(Host, connection.UpgradeHost);
        Assert.Equal(8, dataStartedAtWrite);
        Assert.Collection(
            connection.Writes,
            write => AssertWrite(write, false, "EHLO ligclaw.local\r\n"),
            write => AssertWrite(write, false, "STARTTLS\r\n"),
            write => AssertWrite(write, true, "EHLO ligclaw.local\r\n"),
            write => Assert.StartsWith("AUTH PLAIN ", write.Text, StringComparison.Ordinal),
            write => AssertWrite(write, true, "MAIL FROM:<sender@example.invalid>\r\n"),
            write => AssertWrite(write, true, "RCPT TO:<to@example.invalid>\r\n"),
            write => AssertWrite(write, true, "RCPT TO:<hidden@example.invalid>\r\n"),
            write => AssertWrite(write, true, "DATA\r\n"),
            write =>
            {
                Assert.True(write.IsTls);
                Assert.StartsWith("Date: Sun, 02 Aug 2026", write.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("hidden@example.invalid", write.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("Bcc:", write.Text, StringComparison.OrdinalIgnoreCase);
            },
            write => AssertWrite(write, true, ".\r\n"),
            write => AssertWrite(write, true, "QUIT\r\n"));
    }

    [Fact]
    public async Task ImplicitTlsUpgradesBeforeGreetingAndProbeAuthenticatesWithoutMailCommands()
    {
        var connection = new ScriptedConnection(
            plainReplies: string.Empty,
            tlsReplies: "220 smtp ready\r\n250-smtp\r\n250 AUTH PLAIN\r\n235 authenticated\r\n");
        var factory = new ScriptedFactory(connection);

        var result = await new SmtpWireClient(factory).ProbeAsync(
            Host, 465, SmtpTlsMode.ImplicitTls, Credential(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, connection.UpgradeCount);
        Assert.All(connection.Writes, write => Assert.True(write.IsTls));
        Assert.Contains(connection.Writes, write => write.Text.StartsWith("AUTH PLAIN ", StringComparison.Ordinal));
        Assert.DoesNotContain(connection.Writes, write => write.Text.StartsWith("MAIL FROM", StringComparison.Ordinal));
        Assert.DoesNotContain(connection.Writes, write => write.Text == "DATA\r\n");
    }

    [Fact]
    public async Task StartTlsDowngradeAndPostTlsAuthOmissionFailClosed()
    {
        var noStartTls = new ScriptedConnection(
            "220 smtp\r\n250-smtp\r\n250 AUTH PLAIN\r\n",
            string.Empty);
        var noAuth = new ScriptedConnection(
            PlainStartTlsReplies,
            "250-smtp\r\n250 SIZE 65536\r\n");

        var downgrade = await new SmtpWireClient(new ScriptedFactory(noStartTls)).ProbeAsync(
            Host, Port, SmtpTlsMode.StartTlsRequired, Credential(), CancellationToken.None);
        var missingAuth = await new SmtpWireClient(new ScriptedFactory(noAuth)).ProbeAsync(
            Host, Port, SmtpTlsMode.StartTlsRequired, Credential(), CancellationToken.None);

        Assert.False(downgrade.Succeeded);
        Assert.Equal("smtp.probe.starttls_required", downgrade.ErrorCode);
        Assert.Equal(0, noStartTls.UpgradeCount);
        Assert.False(missingAuth.Succeeded);
        Assert.Equal("smtp.probe.auth_not_supported", missingAuth.ErrorCode);
        Assert.Equal(1, noAuth.UpgradeCount);
    }

    [Fact]
    public async Task RecipientRejectionIsKnownBeforeDataAndDoesNotSendContent()
    {
        var connection = StartTlsConnection(TlsReplies(
            "250 sender accepted",
            "250 first recipient accepted",
            "550 second recipient rejected"));

        var result = await new SmtpWireClient(new ScriptedFactory(connection)).SubmitAsync(
            Host, Port, SmtpTlsMode.StartTlsRequired, Credential(), "sender@example.invalid",
            Draft(["first@example.invalid", "second@example.invalid"], [], []),
            Operation(), CreatedAt, DataStarted, CancellationToken.None);

        Assert.Equal(SmtpWireOutcome.KnownFailed, result.Outcome);
        Assert.Equal("mail.smtp.envelope_rejected", result.ErrorCode);
        Assert.Equal(2, connection.Writes.Count(write => write.Text.StartsWith("RCPT TO:", StringComparison.Ordinal)));
        Assert.DoesNotContain(connection.Writes, write => write.Text == "DATA\r\n");
        Assert.DoesNotContain(connection.Writes, write => write.Text.StartsWith("Date: ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("greeting", "mail.smtp.greeting_rejected")]
    [InlineData("ehlo", "mail.smtp.ehlo_rejected")]
    [InlineData("auth-advertisement", "mail.smtp.auth_not_supported")]
    [InlineData("auth", "mail.smtp.auth_rejected")]
    [InlineData("mail", "mail.smtp.envelope_rejected")]
    [InlineData("data", "mail.smtp.data_rejected")]
    public async Task EveryNegativeReplyBeforeDurableDataBoundaryIsKnownAndWritesNoMessage(
        string phase,
        string expectedErrorCode)
    {
        var tlsReplies = phase switch
        {
            "greeting" => "554 greeting rejected\r\n",
            "ehlo" => "220 smtp ready\r\n554 ehlo rejected\r\n",
            "auth-advertisement" => "220 smtp ready\r\n250-smtp\r\n250 SIZE 65536\r\n",
            "auth" => "220 smtp ready\r\n250-smtp\r\n250 AUTH PLAIN\r\n535 auth rejected\r\n",
            "mail" => "220 smtp ready\r\n250-smtp\r\n250 AUTH PLAIN\r\n235 authenticated\r\n550 sender rejected\r\n",
            "data" => "220 smtp ready\r\n250-smtp\r\n250 AUTH PLAIN\r\n235 authenticated\r\n" +
                      "250 sender accepted\r\n250 recipient accepted\r\n554 data rejected\r\n",
            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };
        var connection = new ScriptedConnection(string.Empty, tlsReplies);
        var factory = new ScriptedFactory(connection);
        var dataStarted = 0;
        using var credential = Credential();

        var result = await new SmtpWireClient(factory).SubmitAsync(
            Host,
            465,
            SmtpTlsMode.ImplicitTls,
            credential,
            "sender@example.invalid",
            Draft(["to@example.invalid"], [], []),
            Operation(),
            CreatedAt,
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                dataStarted++;
                return Task.FromResult(SmtpDataPhaseMarkOutcome.Recorded);
            },
            CancellationToken.None);

        Assert.Equal(SmtpWireOutcome.KnownFailed, result.Outcome);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(0, dataStarted);
        Assert.DoesNotContain(connection.Writes, write =>
            write.Text.StartsWith("Date: ", StringComparison.Ordinal));
        Assert.DoesNotContain(connection.Writes, write => write.Text == ".\r\n");
    }

    [Theory]
    [InlineData("EHLO ")]
    [InlineData("AUTH PLAIN ")]
    [InlineData("MAIL FROM:")]
    [InlineData("RCPT TO:")]
    [InlineData("DATA\r\n")]
    public async Task UserCancellationAtEveryCommandBeforeDurableDataIsKnownAndWritesNoMessage(
        string commandPrefix)
    {
        var connection = new ScriptedConnection(
            string.Empty,
            "220 smtp ready\r\n250-smtp\r\n250 AUTH PLAIN\r\n235 authenticated\r\n" +
            "250 sender accepted\r\n250 recipient accepted\r\n354 send content\r\n250 queued\r\n");
        using var cancellation = new CancellationTokenSource();
        connection.ThrowOnWrite = bytes =>
        {
            var command = Encoding.ASCII.GetString(bytes.Span);
            if (!command.StartsWith(commandPrefix, StringComparison.Ordinal)) return null;
            cancellation.Cancel();
            return new OperationCanceledException(cancellation.Token);
        };
        var dataStarted = 0;
        using var credential = Credential();

        var result = await new SmtpWireClient(new ScriptedFactory(connection)).SubmitAsync(
            Host,
            465,
            SmtpTlsMode.ImplicitTls,
            credential,
            "sender@example.invalid",
            Draft(["to@example.invalid"], [], []),
            Operation(),
            CreatedAt,
            cancellationToken =>
            {
                dataStarted++;
                return Task.FromResult(SmtpDataPhaseMarkOutcome.Recorded);
            },
            cancellation.Token);

        Assert.Equal(SmtpWireOutcome.KnownFailed, result.Outcome);
        Assert.Equal("mail.smtp.submit_failed_known", result.ErrorCode);
        Assert.Equal(0, dataStarted);
        Assert.DoesNotContain(connection.Writes, write =>
            write.Text.StartsWith("Date: ", StringComparison.Ordinal));
        Assert.DoesNotContain(connection.Writes, write => write.Text == ".\r\n");
    }

    [Fact]
    public async Task UserCancellationWhileGreetingIsBlockedIsKnownBeforeAnyCommand()
    {
        var connection = new ScriptedConnection(
            string.Empty,
            string.Empty,
            blockWhenExhausted: true);
        using var cancellation = new CancellationTokenSource();
        using var credential = Credential();
        var pending = new SmtpWireClient(new ScriptedFactory(connection)).SubmitAsync(
            Host,
            465,
            SmtpTlsMode.ImplicitTls,
            credential,
            "sender@example.invalid",
            Draft(["to@example.invalid"], [], []),
            Operation(),
            CreatedAt,
            DataStarted,
            cancellation.Token);

        await connection.ReadBlocked.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SmtpWireOutcome.KnownFailed, result.Outcome);
        Assert.Equal("mail.smtp.submit_failed_known", result.ErrorCode);
        Assert.Empty(connection.Writes);
    }

    [Fact]
    public async Task GlobalOperationDeadlineWhileDataReplyIsBlockedIsKnownBeforeDurableDataBoundary()
    {
        var connection = new ScriptedConnection(
            PlainStartTlsReplies,
            TlsReplies(
                "250 sender accepted",
                "250 recipient accepted"),
            blockWhenExhausted: true);
        var factory = new ScriptedFactory(connection);
        using var credential = Credential();
        var dataStarted = 0;

        var submission = new SmtpWireClient(factory, TimeSpan.FromMilliseconds(250)).SubmitAsync(
            Host,
            Port,
            SmtpTlsMode.StartTlsRequired,
            credential,
            "sender@example.invalid",
            Draft(["to@example.invalid"], [], []),
            Operation(),
            CreatedAt,
            _ =>
            {
                dataStarted++;
                return Task.FromResult(SmtpDataPhaseMarkOutcome.Recorded);
            },
            CancellationToken.None);

        await connection.ReadBlocked.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await submission.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SmtpWireOutcome.KnownFailed, result.Outcome);
        Assert.Equal("mail.smtp.submit_failed_known", result.ErrorCode);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(0, dataStarted);
        AssertWrite(connection.Writes[^1], isTls: true, "DATA\r\n");
        Assert.DoesNotContain(connection.Writes, write =>
            write.Text.StartsWith("Date: ", StringComparison.Ordinal));
        Assert.DoesNotContain(connection.Writes, write => write.Text == ".\r\n");
    }

    [Fact]
    public async Task AnyFailureAfterDurableDataStartedBoundaryIsUnknown()
    {
        var before = StartTlsConnection(TlsReplies(
            "250 sender accepted", "250 recipient accepted", "354 send content"));
        before.ThrowOnWrite = bytes => Encoding.ASCII.GetString(bytes.Span).StartsWith("Date: ", StringComparison.Ordinal)
            ? new IOException("raw body write failure")
            : null;
        var atTerminator = StartTlsConnection(TlsReplies(
            "250 sender accepted", "250 recipient accepted", "354 send content"));
        atTerminator.ThrowOnWrite = bytes => bytes.Span.SequenceEqual(".\r\n"u8)
            ? new IOException("ambiguous terminator write")
            : null;

        var beforeTerminator = await SubmitAsync(before);
        var unknown = await SubmitAsync(atTerminator);

        Assert.Equal(SmtpWireOutcome.OutcomeUnknown, beforeTerminator.Outcome);
        Assert.Equal("mail.smtp.outcome_unknown", beforeTerminator.ErrorCode);
        Assert.Equal(SmtpWireOutcome.OutcomeUnknown, unknown.Outcome);
        Assert.Equal("mail.smtp.outcome_unknown", unknown.ErrorCode);
    }

    [Fact]
    public async Task DataPhaseJournalFailureSendsNoMessageBytesAndIsKnown()
    {
        var connection = StartTlsConnection(TlsReplies(
            "250 sender accepted", "250 recipient accepted", "354 send content"));

        var result = await new SmtpWireClient(new ScriptedFactory(connection)).SubmitAsync(
            Host, Port, SmtpTlsMode.StartTlsRequired, Credential(), "sender@example.invalid",
            Draft(["to@example.invalid"], [], []), Operation(), CreatedAt,
            _ => Task.FromResult(SmtpDataPhaseMarkOutcome.NotRecorded), CancellationToken.None);

        Assert.Equal(SmtpWireOutcome.KnownFailed, result.Outcome);
        Assert.Equal("mail.smtp.data_phase_not_recorded", result.ErrorCode);
        Assert.DoesNotContain(connection.Writes, write => write.Text.StartsWith("Date: ", StringComparison.Ordinal));
        Assert.DoesNotContain(connection.Writes, write => write.Text == ".\r\n");
        Assert.Equal("DATA\r\n", connection.Writes[^1].Text);
    }

    [Fact]
    public async Task IndeterminateOrThrowingDataPhaseMarkIsUnknownAndSendsNoMessageBytes()
    {
        static ScriptedConnection Connection() => StartTlsConnection(TlsReplies(
            "250 sender accepted", "250 recipient accepted", "354 send content"));
        var indeterminateConnection = Connection();
        var throwingConnection = Connection();

        var indeterminate = await new SmtpWireClient(new ScriptedFactory(indeterminateConnection)).SubmitAsync(
            Host, Port, SmtpTlsMode.StartTlsRequired, Credential(), "sender@example.invalid",
            Draft(["to@example.invalid"], [], []), Operation(), CreatedAt,
            _ => Task.FromResult(SmtpDataPhaseMarkOutcome.Indeterminate), CancellationToken.None);
        var throwing = await new SmtpWireClient(new ScriptedFactory(throwingConnection)).SubmitAsync(
            Host, Port, SmtpTlsMode.StartTlsRequired, Credential(), "sender@example.invalid",
            Draft(["to@example.invalid"], [], []), Operation(), CreatedAt,
            _ => throw new IOException("commit result unavailable"), CancellationToken.None);

        Assert.Equal(SmtpWireOutcome.OutcomeUnknown, indeterminate.Outcome);
        Assert.Equal(SmtpWireOutcome.OutcomeUnknown, throwing.Outcome);
        Assert.All(
            new[] { indeterminateConnection, throwingConnection },
            connection =>
            {
                Assert.DoesNotContain(connection.Writes, write => write.Text.StartsWith("Date: ", StringComparison.Ordinal));
                Assert.DoesNotContain(connection.Writes, write => write.Text == ".\r\n");
                Assert.Equal("DATA\r\n", connection.Writes[^1].Text);
            });
    }

    [Fact]
    public async Task MissingFinalReplyIsUnknownAndExactFinalFailureIsKnown()
    {
        var missing = StartTlsConnection(TlsReplies(
            "250 sender accepted", "250 recipient accepted", "354 send content"));
        var rejected = StartTlsConnection(TlsReplies(
            "250 sender accepted", "250 recipient accepted", "354 send content", "554 transaction failed"));

        var unknown = await SubmitAsync(missing);
        var known = await SubmitAsync(rejected);

        Assert.Equal(SmtpWireOutcome.OutcomeUnknown, unknown.Outcome);
        Assert.Equal(SmtpWireOutcome.KnownFailed, known.Outcome);
        Assert.Equal("mail.smtp.remote_rejected", known.ErrorCode);
    }

    [Fact]
    public async Task ConnectionDisposalFailureCannotChangeTerminalServerResult()
    {
        var acceptedConnection = StartTlsConnection(TlsReplies(
            "250 sender accepted", "250 recipient accepted", "354 send content", "250 queued"));
        acceptedConnection.DisposeFailure = new IOException("dispose failed after final 250");
        var rejectedConnection = StartTlsConnection(TlsReplies(
            "250 sender accepted", "250 recipient accepted", "354 send content", "554 rejected"));
        rejectedConnection.DisposeFailure = new IOException("dispose failed after final 554");

        var accepted = await SubmitAsync(acceptedConnection);
        var rejected = await SubmitAsync(rejectedConnection);

        Assert.Equal(SmtpWireOutcome.Accepted, accepted.Outcome);
        Assert.Equal(SmtpWireOutcome.KnownFailed, rejected.Outcome);
        Assert.Equal("mail.smtp.remote_rejected", rejected.ErrorCode);
    }

    [Fact]
    public async Task MalformedOrOversizedReplyNeverLeaksRemoteTextAndNeverReconnects()
    {
        const string remoteCanary = "RAW-SMTP-REMOTE-CANARY";
        var oversized = new ScriptedConnection(
            "220 " + remoteCanary + new string('x', 510) + "\r\n",
            string.Empty);
        var factory = new ScriptedFactory(oversized);

        var result = await new SmtpWireClient(factory).SubmitAsync(
            Host, Port, SmtpTlsMode.StartTlsRequired, Credential(), "sender@example.invalid",
            Draft(["to@example.invalid"], [], []), Operation(), CreatedAt, DataStarted, CancellationToken.None);

        Assert.Equal(SmtpWireOutcome.KnownFailed, result.Outcome);
        Assert.Equal(1, factory.ConnectCount);
        Assert.DoesNotContain(remoteCanary, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptsCodeOnlyRepliesButRejectsInteriorCarriageReturn()
    {
        var codeOnly = new ScriptedConnection(
            "220\r\n250-smtp\r\n250-STARTTLS\r\n250 SIZE 1\r\n220\r\n",
            "250-smtp\r\n250 AUTH PLAIN\r\n235\r\n");
        var malformed = new ScriptedConnection(
            "220 smtp\rjunk\r\n",
            string.Empty);

        var accepted = await new SmtpWireClient(new ScriptedFactory(codeOnly)).ProbeAsync(
            Host, Port, SmtpTlsMode.StartTlsRequired, Credential(), CancellationToken.None);
        var rejected = await new SmtpWireClient(new ScriptedFactory(malformed)).ProbeAsync(
            Host, Port, SmtpTlsMode.StartTlsRequired, Credential(), CancellationToken.None);

        Assert.True(accepted.Succeeded, accepted.ErrorCode);
        Assert.False(rejected.Succeeded);
        Assert.Equal("smtp.probe.failed", rejected.ErrorCode);
    }

    [Fact]
    public async Task ReadsEveryReplyWhenTransportFragmentsItOneByteAtATime()
    {
        var connection = new ScriptedConnection(
            PlainStartTlsReplies,
            TlsReplies(
                "250 sender accepted",
                "250 recipient accepted",
                "354 send content",
                "250 queued"),
            maximumReadChunk: 1);
        var factory = new ScriptedFactory(connection);
        var dataStarted = 0;
        using var credential = Credential();

        var result = await new SmtpWireClient(factory).SubmitAsync(
            Host, Port, SmtpTlsMode.StartTlsRequired, credential, "sender@example.invalid",
            Draft(["to@example.invalid"], [], []), Operation(), CreatedAt,
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                dataStarted++;
                return Task.FromResult(SmtpDataPhaseMarkOutcome.Recorded);
            },
            CancellationToken.None);

        Assert.Equal(SmtpWireOutcome.Accepted, result.Outcome);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(1, dataStarted);
        Assert.True(connection.ReadCount > 100, $"Expected one-byte reads, observed {connection.ReadCount}.");
    }

    [Fact]
    public async Task UserCancellationDuringBlockedFinalReplyReadIsOutcomeUnknown()
    {
        var connection = new ScriptedConnection(
            PlainStartTlsReplies,
            TlsReplies(
                "250 sender accepted",
                "250 recipient accepted",
                "354 send content"),
            blockWhenExhausted: true);
        var factory = new ScriptedFactory(connection);
        var dataStarted = 0;
        using var credential = Credential();
        using var cancellation = new CancellationTokenSource();

        var submission = new SmtpWireClient(factory).SubmitAsync(
            Host, Port, SmtpTlsMode.StartTlsRequired, credential, "sender@example.invalid",
            Draft(["to@example.invalid"], [], []), Operation(), CreatedAt,
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                dataStarted++;
                return Task.FromResult(SmtpDataPhaseMarkOutcome.Recorded);
            },
            cancellation.Token);

        await connection.ReadBlocked.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await submission.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SmtpWireOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(1, dataStarted);
        AssertWrite(connection.Writes[^1], isTls: true, ".\r\n");
    }

    [Fact]
    public async Task GlobalOperationDeadlineDuringBlockedFinalReplyIsUnknownAfterDurableDataStarted()
    {
        var connection = new ScriptedConnection(
            PlainStartTlsReplies,
            TlsReplies(
                "250 sender accepted",
                "250 recipient accepted",
                "354 send content"),
            blockWhenExhausted: true);
        var factory = new ScriptedFactory(connection);
        var dataStarted = 0;
        using var credential = Credential();

        var submission = new SmtpWireClient(factory, TimeSpan.FromMilliseconds(250)).SubmitAsync(
            Host, Port, SmtpTlsMode.StartTlsRequired, credential, "sender@example.invalid",
            Draft(["to@example.invalid"], [], []), Operation(), CreatedAt,
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                dataStarted++;
                return Task.FromResult(SmtpDataPhaseMarkOutcome.Recorded);
            },
            CancellationToken.None);

        await connection.ReadBlocked.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await submission.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SmtpWireOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal("mail.smtp.outcome_unknown", result.ErrorCode);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(1, dataStarted);
        AssertWrite(connection.Writes[^1], isTls: true, ".\r\n");
    }

    [Fact]
    public async Task UntrustedTlsCertificateFailsClosedBeforeAnySmtpCommand()
    {
        var now = DateTimeOffset.UtcNow;
        using var certificateAuthority = CreateCertificateAuthority(
            $"ligclaw-smtp-test-{Guid.NewGuid():N}", now);
        using var untrusted = CreateServerCertificate(
            certificateAuthority, "localhost", now.AddMinutes(-5), now.AddDays(1));

        await AssertCertificateFailsClosedAsync("untrusted", untrusted);
    }

    [Fact]
    public async Task InvalidEndpointOrCredentialFailsBeforeSocket()
    {
        var factory = new ScriptedFactory(StartTlsConnection(string.Empty));
        var client = new SmtpWireClient(factory);

        var result = await client.SubmitAsync(
            "SMTP.EXAMPLE.INVALID", Port, SmtpTlsMode.StartTlsRequired, Credential(),
            "sender@example.invalid", Draft(["to@example.invalid"], [], []), Operation(), CreatedAt,
            DataStarted, CancellationToken.None);

        Assert.Equal(SmtpWireOutcome.NotStarted, result.Outcome);
        Assert.Equal(0, factory.ConnectCount);
        Assert.Throws<ArgumentException>(() => new SmtpWireCredential("user name", "password"));
        Assert.Throws<ArgumentException>(() => new SmtpWireCredential("username", "bad\0password"));
    }

    [Fact]
    public void InjectedGlobalOperationDeadlineRejectsNonPositiveOrAboveProductionThirtySeconds()
    {
        var factory = new ScriptedFactory(StartTlsConnection(string.Empty));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SmtpWireClient(factory, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SmtpWireClient(factory, TimeSpan.FromMilliseconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SmtpWireClient(factory, TimeSpan.FromSeconds(30).Add(TimeSpan.FromTicks(1))));
        _ = new SmtpWireClient(factory, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void TlsOptionsRequireSystemValidationRevocationAndTls12OrLater()
    {
        var options = SmtpWireClient.CreateTlsOptions(Host);

        Assert.Equal(Host, options.TargetHost);
        Assert.Equal(SslProtocols.Tls12 | SslProtocols.Tls13, options.EnabledSslProtocols);
        Assert.Equal(X509RevocationMode.Online, options.CertificateRevocationCheckMode);
        Assert.Equal(EncryptionPolicy.RequireEncryption, options.EncryptionPolicy);
        Assert.False(options.AllowRenegotiation);
        Assert.Null(options.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void AuthPlainCommandIsBoundedAndDiagnosticsAreRedacted()
    {
        const string username = "RAW-SMTP-USER-CANARY";
        const string password = "RAW-SMTP-PASSWORD-CANARY";
        using var credential = new SmtpWireCredential(username, password);
        var command = credential.CreateAuthPlainCommand();
        var commandText = Encoding.ASCII.GetString(command);
        var decoded = Convert.FromBase64String(commandText[11..^2]);

        Assert.InRange(command.Length, 1, 512);
        Assert.Equal($"\0{username}\0{password}", Encoding.UTF8.GetString(decoded));
        Assert.DoesNotContain(username, credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(password, credential.ToString(), StringComparison.Ordinal);
        credential.Dispose();
        Assert.Throws<ObjectDisposedException>(() => credential.CreateAuthPlainCommand());
    }

    private static Task<SmtpWireResult> SubmitAsync(ScriptedConnection connection) =>
        new SmtpWireClient(new ScriptedFactory(connection)).SubmitAsync(
            Host, Port, SmtpTlsMode.StartTlsRequired, Credential(), "sender@example.invalid",
            Draft(["to@example.invalid"], [], []), Operation(), CreatedAt, DataStarted, CancellationToken.None);

    private static Task<SmtpDataPhaseMarkOutcome> DataStarted(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SmtpDataPhaseMarkOutcome.Recorded);
    }

    private static ScriptedConnection StartTlsConnection(string tlsReplies) =>
        new(PlainStartTlsReplies, tlsReplies);

    private static string TlsReplies(params string[] afterAuthReplies) =>
        "250-smtp\r\n250 AUTH PLAIN\r\n235 authenticated\r\n" +
        string.Concat(afterAuthReplies.Select(reply => reply + "\r\n"));

    private const string PlainStartTlsReplies =
        "220 smtp ready\r\n250-smtp\r\n250-STARTTLS\r\n250 SIZE 65536\r\n220 start tls\r\n";

    private static SmtpWireCredential Credential() => new("smtp-user", "smtp-password");

    private static MailDraft Draft(
        IReadOnlyList<string> to,
        IReadOnlyList<string> cc,
        IReadOnlyList<string> bcc) =>
        Assert.IsType<MailDraft>(MailDraftPolicy.Validate(
            new MailDraftInput(to, cc, bcc, "Subject", "Body")).Draft);

    private static MailOutboxOperationId Operation() =>
        MailOutboxOperationId.Parse($"mgo_{new string('a', 64)}");

    private static void AssertWrite(ScriptedWrite write, bool isTls, string expected)
    {
        Assert.Equal(isTls, write.IsTls);
        Assert.Equal(expected, write.Text);
    }

    private static async Task AssertCertificateFailsClosedAsync(
        string scenario,
        X509Certificate2 serverCertificate)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observationTask = ObserveTlsSmtpAsync(
            listener, serverCertificate, serverCancellation.Token);
        SmtpWireResult result;
        TlsServerObservation observation;
        var dataStarted = 0;
        var clientTimedOut = false;

        try
        {
            using var credential = Credential();
            using var clientCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            result = await new SmtpWireClient().SubmitAsync(
                "localhost", port, SmtpTlsMode.ImplicitTls, credential, "sender@example.invalid",
                Draft(["to@example.invalid"], [], []), Operation(), CreatedAt,
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    dataStarted++;
                    return Task.FromResult(SmtpDataPhaseMarkOutcome.Recorded);
                },
                clientCancellation.Token);
            clientTimedOut = clientCancellation.IsCancellationRequested;
            observation = await observationTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            serverCancellation.Cancel();
            listener.Stop();
            if (!observationTask.IsCompleted)
            {
                try
                {
                    await observationTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception exception) when (
                    exception is OperationCanceledException or TimeoutException or SocketException)
                {
                    // Best-effort teardown for a failed assertion or handshake.
                }
            }
        }

        Assert.False(clientTimedOut, $"{scenario}: client timed out instead of rejecting the certificate.");
        Assert.False(observation.TimedOut, $"{scenario}: loopback TLS server timed out.");
        Assert.Equal(SmtpWireOutcome.KnownFailed, result.Outcome);
        Assert.Equal(0, dataStarted);
        Assert.Equal(0, observation.Commands.Count(command =>
            command.StartsWith("AUTH ", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(0, observation.Commands.Count(command =>
            command.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(0, observation.Commands.Count(command =>
            command.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(0, observation.Commands.Count(command =>
            string.Equals(command, "DATA", StringComparison.OrdinalIgnoreCase)));
        Assert.Empty(observation.Commands);
    }

    private static async Task<TlsServerObservation> ObserveTlsSmtpAsync(
        TcpListener listener,
        X509Certificate2 serverCertificate,
        CancellationToken cancellationToken)
    {
        var commands = new List<string>();
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await stream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCertificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    EncryptionPolicy = EncryptionPolicy.RequireEncryption,
                    AllowRenegotiation = false,
                },
                cancellationToken);

            await WriteTlsReplyAsync(stream, "220 loopback ready\r\n", cancellationToken);
            var readingData = false;
            while (await ReadTlsLineAsync(stream, cancellationToken) is { } line)
            {
                if (readingData)
                {
                    if (line == ".")
                    {
                        readingData = false;
                        await WriteTlsReplyAsync(stream, "250 queued\r\n", cancellationToken);
                    }

                    continue;
                }

                commands.Add(line);
                if (line.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTlsReplyAsync(
                        stream, "250-loopback\r\n250 AUTH PLAIN\r\n", cancellationToken);
                }
                else if (line.StartsWith("AUTH PLAIN ", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTlsReplyAsync(stream, "235 authenticated\r\n", cancellationToken);
                }
                else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTlsReplyAsync(stream, "250 accepted\r\n", cancellationToken);
                }
                else if (string.Equals(line, "DATA", StringComparison.OrdinalIgnoreCase))
                {
                    readingData = true;
                    await WriteTlsReplyAsync(stream, "354 send content\r\n", cancellationToken);
                }
                else if (string.Equals(line, "QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTlsReplyAsync(stream, "221 goodbye\r\n", cancellationToken);
                    break;
                }
                else
                {
                    await WriteTlsReplyAsync(stream, "500 unsupported\r\n", cancellationToken);
                }
            }

            return new TlsServerObservation(commands, TimedOut: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TlsServerObservation(commands, TimedOut: true);
        }
        catch (Exception exception) when (
            exception is AuthenticationException or IOException or SocketException)
        {
            return new TlsServerObservation(commands, TimedOut: false);
        }
    }

    private static async Task<string?> ReadTlsLineAsync(
        SslStream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(128);
        var oneByte = new byte[1];
        while (bytes.Count <= 4 * 1024)
        {
            var read = await stream.ReadAsync(oneByte, cancellationToken);
            if (read == 0)
            {
                if (bytes.Count == 0) return null;
                throw new IOException("SMTP command ended before CRLF.");
            }

            if (oneByte[0] == (byte)'\r')
            {
                read = await stream.ReadAsync(oneByte, cancellationToken);
                if (read != 1 || oneByte[0] != (byte)'\n')
                    throw new IOException("SMTP command contained an invalid line ending.");
                return Encoding.ASCII.GetString(bytes.ToArray());
            }

            if (oneByte[0] is (byte)'\n' or > 0x7f)
                throw new IOException("SMTP command contained an invalid byte.");
            bytes.Add(oneByte[0]);
        }

        throw new IOException("SMTP command exceeded the test server limit.");
    }

    private static async Task WriteTlsReplyAsync(
        SslStream stream,
        string reply,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static X509Certificate2 CreateCertificateAuthority(
        string commonName,
        DateTimeOffset now)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        return request.CreateSelfSigned(now.AddYears(-1), now.AddYears(1));
    }

    private static X509Certificate2 CreateServerCertificate(
        X509Certificate2 issuer,
        string dnsName,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));
        var usages = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1"),
        };
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(usages, critical: true));
        var subjectAlternativeName = new SubjectAlternativeNameBuilder();
        subjectAlternativeName.AddDnsName(dnsName);
        request.CertificateExtensions.Add(subjectAlternativeName.Build());
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var serialNumber = RandomNumberGenerator.GetBytes(16);
        serialNumber[0] &= 0x7f;
        serialNumber[^1] |= 1;
        using var publicCertificate = request.Create(
            issuer, notBefore, notAfter, serialNumber);
        return publicCertificate.CopyWithPrivateKey(key);
    }

    private sealed record TlsServerObservation(IReadOnlyList<string> Commands, bool TimedOut);

    private sealed class ScriptedFactory(ScriptedConnection connection) : ISmtpWireConnectionFactory
    {
        public int ConnectCount { get; private set; }

        public Task<ISmtpWireConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            return Task.FromResult<ISmtpWireConnection>(connection);
        }
    }

    private sealed record ScriptedWrite(bool IsTls, byte[] Bytes)
    {
        public string Text => Encoding.UTF8.GetString(Bytes);
    }

    private sealed class ScriptedConnection(
        string plainReplies,
        string tlsReplies,
        int maximumReadChunk = int.MaxValue,
        bool blockWhenExhausted = false) : ISmtpWireConnection
    {
        private readonly byte[] plain = Encoding.ASCII.GetBytes(plainReplies);
        private readonly byte[] tls = Encoding.ASCII.GetBytes(tlsReplies);
        private readonly int maximumReadChunk = maximumReadChunk > 0
            ? maximumReadChunk
            : throw new ArgumentOutOfRangeException(nameof(maximumReadChunk));
        private readonly TaskCompletionSource<bool> readBlocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int plainOffset;
        private int tlsOffset;

        public bool IsTls { get; private set; }
        public int ReadCount { get; private set; }
        public int UpgradeCount { get; private set; }
        public string? UpgradeHost { get; private set; }
        public Task ReadBlocked => readBlocked.Task;
        public List<ScriptedWrite> Writes { get; } = [];
        public Func<ReadOnlyMemory<byte>, Exception?>? ThrowOnWrite { get; set; }
        public Exception? DisposeFailure { get; set; }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            var source = IsTls ? tls : plain;
            ref var offset = ref IsTls ? ref tlsOffset : ref plainOffset;
            if (offset == source.Length)
            {
                if (!blockWhenExhausted) return ValueTask.FromResult(0);
                readBlocked.TrySetResult(true);
                return new ValueTask<int>(WaitUntilCancelledAsync(cancellationToken));
            }

            var length = Math.Min(
                maximumReadChunk,
                Math.Min(buffer.Length, source.Length - offset));
            source.AsMemory(offset, length).CopyTo(buffer);
            offset += length;
            return ValueTask.FromResult(length);
        }

        private static async Task<int> WaitUntilCancelledAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnWrite?.Invoke(buffer) is { } failure) return ValueTask.FromException(failure);
            Writes.Add(new ScriptedWrite(IsTls, buffer.ToArray()));
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public Task UpgradeToTlsAsync(string targetHost, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsTls) throw new InvalidOperationException("duplicate TLS");
            IsTls = true;
            UpgradeCount++;
            UpgradeHost = targetHost;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => DisposeFailure is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(DisposeFailure);
    }
}
