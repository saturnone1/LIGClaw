using LIGClaw.Application.Connections;

namespace LIGClaw.Application.Tests;

public sealed class SmtpConnectionProfilePolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(4);

    [Theory]
    [InlineData(" SMTP.BÜCHER.Example. ", "smtp.xn--bcher-kva.example")]
    [InlineData("MAIL.INTERNAL", "mail.internal")]
    public void NormalizesCanonicalAsciiDnsHostsWithoutAcceptingUriSyntax(string input, string expected)
    {
        Assert.True(SmtpConnectionProfilePolicy.TryNormalizeHost(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("smtp://mail.example.test")]
    [InlineData("user@mail.example.test")]
    [InlineData("mail_example.test")]
    [InlineData("-mail.example.test")]
    [InlineData("mail-.example.test")]
    [InlineData("mail..example.test")]
    [InlineData("192.168.10.15")]
    [InlineData("2001:0DB8::1")]
    [InlineData("[2001:db8::1]")]
    [InlineData("fe80::1%12")]
    [InlineData("127.1")]
    [InlineData("192.168.001.1")]
    [InlineData("192.168.1.999")]
    public void RejectsNonHostOrAmbiguousHostInputs(string? input)
    {
        Assert.False(SmtpConnectionProfilePolicy.TryNormalizeHost(input, out var normalized));
        Assert.Empty(normalized);
    }

    [Fact]
    public void EnforcesDnsLabelAndTotalHostBounds()
    {
        var label63 = new string('a', 63);
        var exact253 = $"{label63}.{label63}.{label63}.{new string('b', 61)}";
        var oversized254 = $"{label63}.{label63}.{label63}.{new string('b', 62)}";

        Assert.Equal(253, exact253.Length);
        Assert.True(SmtpConnectionProfilePolicy.TryNormalizeHost(exact253, out var normalized));
        Assert.Equal(exact253, normalized);
        Assert.Equal(254, oversized254.Length);
        Assert.False(SmtpConnectionProfilePolicy.TryNormalizeHost(oversized254, out _));
        Assert.False(SmtpConnectionProfilePolicy.TryNormalizeHost(
            $"{new string('a', 64)}.example",
            out _));
    }

    [Fact]
    public void TargetHashIsDeterministicAndChangesWithEveryMutableTargetField()
    {
        var baseline = Hash(
            "smtp.example.test",
            587,
            SmtpTlsMode.StartTlsRequired,
            "sender@example.test");

        Assert.Matches("^sha256:[0-9a-f]{64}$", baseline);
        Assert.Equal(
            "sha256:bc09369b96cd149f27d8ce52ee8b23cb85220e6bf5f9ebe7d7409e13196fbe40",
            baseline);
        Assert.Equal(
            baseline,
            Hash(
                "smtp.example.test",
                587,
                SmtpTlsMode.StartTlsRequired,
                "sender@example.test"));
        Assert.NotEqual(baseline, Hash(
            "other.example.test",
            587,
            SmtpTlsMode.StartTlsRequired,
            "sender@example.test"));
        Assert.NotEqual(baseline, Hash(
            "smtp.example.test",
            465,
            SmtpTlsMode.StartTlsRequired,
            "sender@example.test"));
        Assert.NotEqual(baseline, Hash(
            "smtp.example.test",
            587,
            SmtpTlsMode.ImplicitTls,
            "sender@example.test"));
        Assert.NotEqual(baseline, Hash(
            "smtp.example.test",
            587,
            SmtpTlsMode.StartTlsRequired,
            "Sender@example.test"));
    }

    [Theory]
    [InlineData("SMTP.EXAMPLE.TEST", 587)]
    [InlineData("smtp.example.test.", 587)]
    [InlineData("smtp.example.test", 0)]
    [InlineData("smtp.example.test", 65536)]
    public void TargetHashRejectsNonCanonicalHostOrPortWithoutEchoingRawValues(string host, int port)
    {
        var exception = Assert.Throws<ArgumentException>(() => Hash(
            host,
            port,
            SmtpTlsMode.StartTlsRequired,
            "sender@example.test"));

        Assert.DoesNotContain(host, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V1AllowsOnlyTlsAuthPlainAndSystemTrustTypedSemantics()
    {
        Assert.Equal([SmtpTlsMode.StartTlsRequired, SmtpTlsMode.ImplicitTls], Enum.GetValues<SmtpTlsMode>());
        Assert.Equal([SmtpAuthenticationMechanism.Plain], Enum.GetValues<SmtpAuthenticationMechanism>());
        Assert.Equal([SmtpCertificatePolicy.SystemTrust], Enum.GetValues<SmtpCertificatePolicy>());

        var profile = ValidProfile();
        Assert.True(SmtpConnectionProfilePolicy.IsValid(profile));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(profile with { TlsMode = (SmtpTlsMode)99 }));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(
            profile with { Authentication = (SmtpAuthenticationMechanism)99 }));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(
            profile with { CertificatePolicy = (SmtpCertificatePolicy)99 }));
    }

    [Fact]
    public void ValidatesCanonicalProfileAndAllStateBoundaries()
    {
        var profile = ValidProfile(fixedErrorCode: "smtp.probe_failed");

        Assert.True(SmtpConnectionProfilePolicy.IsValid(profile));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(null));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(profile with { Id = null! }));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(profile with { DisplayName = " SMTP" }));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(profile with { DisplayName = "SMTP\n" }));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(profile with { Host = "SMTP.EXAMPLE.TEST" }));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(profile with { Port = 0 }));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(profile with { Port = 65_536 }));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(profile with { Sender = "sender with space@example.test" }));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(profile with { State = (ConnectionProfileLifecycleState)99 }));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(profile with { Revision = 0 }));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(profile with { TargetRevision = 0 }));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(profile with { CredentialRevision = -1 }));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(profile with { TargetHash = HashValue('f') }));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(profile with { FixedErrorCode = "Smtp.invalid" }));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(
            profile with { CreatedAtUtc = Now.ToOffset(TimeSpan.FromHours(9)) }));
        Assert.False(SmtpConnectionProfilePolicy.IsValid(
            profile with { UpdatedAtUtc = Now.AddTicks(-1) }));
    }

    [Fact]
    public void ProfileHasNoUsernamePasswordOrSecretPropertyAndToStringRedactsTargetIdentity()
    {
        const string marker = "smtp-raw-canary";
        var profile = ValidProfile(
            id: "smtp-raw-canary",
            displayName: $"{marker} display",
            host: $"{marker}.example.test",
            sender: $"{marker}@example.test",
            fixedErrorCode: "smtp.raw_canary");
        var propertyNames = typeof(SmtpConnectionProfile).GetProperties().Select(property => property.Name);

        Assert.DoesNotContain("Username", propertyNames);
        Assert.DoesNotContain("Password", propertyNames);
        Assert.DoesNotContain("Secret", propertyNames);
        var text = profile.ToString();
        Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
        Assert.DoesNotContain(profile.TargetHash, text, StringComparison.Ordinal);
        Assert.Contains("Authentication=Plain", text, StringComparison.Ordinal);
        Assert.Contains("CertificatePolicy=SystemTrust", text, StringComparison.Ordinal);
    }

    private static string Hash(string host, int port, SmtpTlsMode tlsMode, string sender) =>
        SmtpConnectionProfilePolicy.ComputeTargetHash(
            host,
            port,
            tlsMode,
            SmtpAuthenticationMechanism.Plain,
            SmtpCertificatePolicy.SystemTrust,
            sender);

    private static SmtpConnectionProfile ValidProfile(
        string id = "smtp-profile",
        string displayName = "SMTP Profile",
        string host = "smtp.example.test",
        int port = 587,
        SmtpTlsMode tlsMode = SmtpTlsMode.StartTlsRequired,
        string sender = "sender@example.test",
        string? fixedErrorCode = null)
    {
        var targetHash = Hash(host, port, tlsMode, sender);
        return new SmtpConnectionProfile(
            ConnectionProfileId.Parse(id),
            displayName,
            host,
            port,
            tlsMode,
            SmtpAuthenticationMechanism.Plain,
            SmtpCertificatePolicy.SystemTrust,
            sender,
            Enabled: true,
            ConnectionProfileLifecycleState.Stable,
            Revision: 3,
            TargetRevision: 2,
            CredentialRevision: 4,
            targetHash,
            fixedErrorCode,
            Now,
            Now);
    }

    private static string HashValue(char value) => $"sha256:{new string(value, 64)}";
}
