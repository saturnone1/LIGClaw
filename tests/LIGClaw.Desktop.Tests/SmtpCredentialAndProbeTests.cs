using System.Buffers.Binary;
using System.Text;
using LIGClaw.Application.Connections;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class SmtpCredentialAndProbeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(7);

    [Fact]
    public void VersionedLengthFramedCodecRoundTripsOneAtomicCredentialWithoutRawDiagnostics()
    {
        const string username = "RAW-SMTP-USERNAME-CANARY";
        const string password = "RAW-SMTP-PASSWORD-암호-CANARY";

        var encoded = SmtpCredentialCodec.Encode(username, password);

        Assert.StartsWith(SmtpCredentialCodec.Prefix, encoded, StringComparison.Ordinal);
        Assert.True(encoded.Length <= SmtpCredentialCodec.MaximumEncodedCharacters);
        Assert.DoesNotContain(username, encoded, StringComparison.Ordinal);
        Assert.DoesNotContain(password, encoded, StringComparison.Ordinal);
        Assert.True(SmtpCredentialCodec.TryDecode(encoded, out var credential));
        using (credential)
        {
            var command = Encoding.ASCII.GetString(credential!.CreateAuthPlainCommand());
            var expectedPayload = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"\0{username}\0{password}"));
            Assert.Equal($"AUTH PLAIN {expectedPayload}\r\n", command);
            Assert.DoesNotContain(username, credential.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(password, credential.ToString(), StringComparison.Ordinal);
        }
        Assert.Throws<ObjectDisposedException>(() => credential!.CreateAuthPlainCommand());

        var id = ConnectionProfileId.Parse("shared-profile");
        Assert.Equal(
            "LIGClaw/SmtpConnections/shared-profile/AuthV1",
            WindowsConnectionCredentialStore.Target(id, ConnectionCredentialKind.SmtpAuth));
        Assert.NotEqual(
            WindowsConnectionCredentialStore.Target(id, ConnectionCredentialKind.BearerToken),
            WindowsConnectionCredentialStore.Target(id, ConnectionCredentialKind.SmtpAuth));
        Assert.NotEqual(
            WindowsConnectionCredentialStore.Target(id, ConnectionCredentialKind.MailGatewayBearerToken),
            WindowsConnectionCredentialStore.Target(id, ConnectionCredentialKind.SmtpAuth));
    }

    [Fact]
    public void CodecUsesWireBoundsAndRejectsWrongVersionMalformedBase64OrLengthFrames()
    {
        var maximum = SmtpCredentialCodec.Encode(
            new string('u', SmtpWireCredential.MaximumUsernameBytes),
            new string('p', SmtpWireCredential.MaximumPasswordBytes));
        Assert.True(SmtpCredentialCodec.IsValid(maximum));

        const string rawCanary = "RAW-INVALID-SMTP-CREDENTIAL-CANARY";
        var usernameError = Assert.Throws<ArgumentException>(() => SmtpCredentialCodec.Encode(
            new string('u', SmtpWireCredential.MaximumUsernameBytes + 1),
            rawCanary));
        var passwordError = Assert.Throws<ArgumentException>(() => SmtpCredentialCodec.Encode(
            "username",
            new string('p', SmtpWireCredential.MaximumPasswordBytes + 1)));
        Assert.DoesNotContain(rawCanary, usernameError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(rawCanary, passwordError.Message, StringComparison.Ordinal);

        Assert.False(SmtpCredentialCodec.TryDecode(null, out _));
        Assert.False(SmtpCredentialCodec.TryDecode("ligclaw-smtp-auth-v2:AAAA", out _));
        Assert.False(SmtpCredentialCodec.TryDecode(SmtpCredentialCodec.Prefix + "!!!!", out _));

        var malformedLength = new byte[7];
        BinaryPrimitives.WriteUInt16BigEndian(malformedLength, 1);
        BinaryPrimitives.WriteUInt16BigEndian(malformedLength.AsSpan(2), 1);
        malformedLength[4] = (byte)'u';
        malformedLength[5] = (byte)'p';
        malformedLength[6] = (byte)'x';
        Assert.False(SmtpCredentialCodec.TryDecode(
            SmtpCredentialCodec.Prefix + Convert.ToBase64String(malformedLength),
            out _));
    }

    [Fact]
    public async Task TypedProbePassesOnlyCanonicalProfileAndDisposesCredentialAfterWireCall()
    {
        var profile = Profile();
        var credential = new SmtpWireCredential("smtp-user", "smtp-password");
        var credentialReads = 0;
        var wireCalls = 0;
        var adapter = new SmtpConnectionProbeAdapter(
            (id, _) =>
            {
                credentialReads++;
                Assert.Equal(profile.Id, id);
                return Task.FromResult<SmtpWireCredential?>(credential);
            },
            (host, port, tlsMode, actualCredential, cancellationToken) =>
            {
                wireCalls++;
                Assert.False(cancellationToken.IsCancellationRequested);
                Assert.Equal(profile.Host, host);
                Assert.Equal(profile.Port, port);
                Assert.Equal(profile.TlsMode, tlsMode);
                Assert.Same(credential, actualCredential);
                var command = actualCredential.CreateAuthPlainCommand();
                Assert.NotEmpty(command);
                return Task.FromResult(new SmtpWireProbeResult(true));
            });

        var result = await adapter.ProbeAsync(profile, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.Equal(1, credentialReads);
        Assert.Equal(1, wireCalls);
        Assert.Throws<ObjectDisposedException>(() => credential.CreateAuthPlainCommand());
    }

    [Fact]
    public async Task ProbeFailuresUseFixedCodesAndNeverEchoCredentialOrTransportExceptions()
    {
        const string credentialCanary = "RAW-CREDENTIAL-PROVIDER-CANARY";
        const string transportCanary = "RAW-SMTP-TRANSPORT-CANARY";
        var profile = Profile();
        var unavailable = new SmtpConnectionProbeAdapter(
            (_, _) => throw new InvalidOperationException(credentialCanary),
            (_, _, _, _, _) => throw new InvalidOperationException("must not run"));
        var transport = new SmtpConnectionProbeAdapter(
            (_, _) => Task.FromResult<SmtpWireCredential?>(new("smtp-user", "smtp-password")),
            (_, _, _, _, _) => throw new InvalidOperationException(transportCanary));

        var unavailableResult = await unavailable.ProbeAsync(profile, CancellationToken.None);
        var transportResult = await transport.ProbeAsync(profile, CancellationToken.None);
        var invalidResult = await transport.ProbeAsync(
            profile with { Authentication = (SmtpAuthenticationMechanism)99 },
            CancellationToken.None);

        Assert.Equal("smtp.probe.credential_unavailable", unavailableResult.ErrorCode);
        Assert.Equal("smtp.probe.transport_failed", transportResult.ErrorCode);
        Assert.Equal("smtp.probe.profile_invalid", invalidResult.ErrorCode);
        var diagnostics = unavailableResult + transportResult.ToString() + invalidResult +
                          unavailable + transport;
        Assert.DoesNotContain(credentialCanary, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(transportCanary, diagnostics, StringComparison.Ordinal);
    }

    private static SmtpConnectionProfile Profile()
    {
        const string host = "smtp.example.test";
        const int port = 587;
        const string sender = "sender@example.test";
        var targetHash = SmtpConnectionProfilePolicy.ComputeTargetHash(
            host,
            port,
            SmtpTlsMode.StartTlsRequired,
            SmtpAuthenticationMechanism.Plain,
            SmtpCertificatePolicy.SystemTrust,
            sender);
        return new SmtpConnectionProfile(
            ConnectionProfileId.Parse("smtp-profile"),
            "SMTP Profile",
            host,
            port,
            SmtpTlsMode.StartTlsRequired,
            SmtpAuthenticationMechanism.Plain,
            SmtpCertificatePolicy.SystemTrust,
            sender,
            true,
            ConnectionProfileLifecycleState.Stable,
            3,
            1,
            1,
            targetHash,
            null,
            Now,
            Now);
    }
}
