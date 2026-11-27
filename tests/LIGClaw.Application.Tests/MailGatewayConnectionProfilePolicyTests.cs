using LIGClaw.Application.Connections;
using LIGClaw.Application.Mail;

namespace LIGClaw.Application.Tests;

public sealed class MailGatewayConnectionProfilePolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddHours(1);

    [Theory]
    [InlineData(" HTTPS://BÜCHER.Example:443 ", "https://xn--bcher-kva.example/")]
    [InlineData("https://Gateway.Example:8443", "https://gateway.example:8443/")]
    [InlineData("https://gateway.example/", "https://gateway.example/")]
    public void NormalizesHttpsOriginsToCanonicalRootUrls(string input, string expected)
    {
        var accepted = MailGatewayConnectionProfilePolicy.TryNormalizeBaseUrl(input, out var normalized);

        Assert.True(accepted);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://")]
    [InlineData("gateway.example")]
    [InlineData("http://gateway.example/")]
    [InlineData("https://user:password@gateway.example/")]
    [InlineData("https://gateway.example/api")]
    [InlineData("https://gateway.example/?mode=probe")]
    [InlineData("https://gateway.example/#status")]
    public void RejectsAnythingOtherThanAnAbsoluteHttpsOriginRoot(string? input)
    {
        var accepted = MailGatewayConnectionProfilePolicy.TryNormalizeBaseUrl(input, out var normalized);

        Assert.False(accepted);
        Assert.Empty(normalized);
    }

    [Fact]
    public void RejectsOversizedBaseUrlInputWithoutEchoingIt()
    {
        var marker = $"raw-url-marker-{new string('x', 2_049)}";

        Assert.False(MailGatewayConnectionProfilePolicy.TryNormalizeBaseUrl(marker, out var normalized));
        Assert.Empty(normalized);
    }

    [Fact]
    public void SharedSenderPolicyAcceptsExactly254AsciiCharactersAndRejects255()
    {
        var local = new string('a', 64);
        var exactDomain = $"{new string('b', 63)}.{new string('c', 63)}.{new string('d', 61)}";
        var oversizedDomain = $"{new string('b', 63)}.{new string('c', 63)}.{new string('d', 62)}";
        var exact = $"{local}@{exactDomain}";
        var oversized = $"{local}@{oversizedDomain}";

        Assert.Equal(MailAddressPolicy.MaximumAddressCharacters, exact.Length);
        Assert.Equal(MailAddressPolicy.MaximumAddressCharacters + 1, oversized.Length);
        Assert.True(MailAddressPolicy.IsValidAsciiMailbox(exact));
        Assert.False(MailAddressPolicy.IsValidAsciiMailbox(oversized));

        Assert.True(MailGatewayConnectionProfilePolicy.IsValid(ValidProfile(sender: exact)));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(ValidProfile() with { Sender = oversized }));
    }

    [Theory]
    [InlineData("plain-address")]
    [InlineData("two@@example.com")]
    [InlineData(".sender@example.com")]
    [InlineData("sender.@example.com")]
    [InlineData("sender@example..com")]
    [InlineData("sender@-example.com")]
    [InlineData("sender@example-.com")]
    [InlineData("sender@example.com.")]
    [InlineData("send er@example.com")]
    [InlineData("보내는이@example.com")]
    public void RejectsNonCanonicalOrNonAsciiSenders(string sender)
    {
        Assert.False(MailAddressPolicy.IsValidAsciiMailbox(sender));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(ValidProfile() with { Sender = sender }));
    }

    [Fact]
    public void TargetHashUsesTheFixedLengthFramedMailGatewayDomain()
    {
        const string baseUrl = "https://gateway.example.test/";
        const string sender = "sender@example.com";

        var hash = MailGatewayConnectionProfilePolicy.ComputeTargetHash(baseUrl, sender);
        var changedUrl = MailGatewayConnectionProfilePolicy.ComputeTargetHash(
            "https://other.example.test/",
            sender);
        var changedSender = MailGatewayConnectionProfilePolicy.ComputeTargetHash(
            baseUrl,
            "Sender@example.com");

        Assert.Equal(
            "sha256:f3ed68f30be4c58453b21926acd63d9e8f3c48bbe5ef39fae2b9e175e723aeb8",
            hash);
        Assert.NotEqual(hash, changedUrl);
        Assert.NotEqual(hash, changedSender);
    }

    [Theory]
    [InlineData("HTTPS://gateway.example.test/", "sender@example.com")]
    [InlineData("https://gateway.example.test:443/", "sender@example.com")]
    [InlineData("https://gateway.example.test/api", "sender@example.com")]
    [InlineData("https://gateway.example.test/", "invalid sender@example.com")]
    public void TargetHashRejectsNonCanonicalInputsWithoutEchoingRawValues(
        string baseUrl,
        string sender)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            MailGatewayConnectionProfilePolicy.ComputeTargetHash(baseUrl, sender));

        Assert.DoesNotContain(baseUrl, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sender, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatesCanonicalProfileAndRejectsIdentityDisplayAndTargetBoundaries()
    {
        var profile = ValidProfile(fixedErrorCode: "mail.gateway_unavailable");

        Assert.True(MailGatewayConnectionProfilePolicy.IsValid(profile));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(null));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(profile with { Id = null! }));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(profile with { DisplayName = string.Empty }));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(profile with { DisplayName = " gateway" }));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(profile with { DisplayName = "gateway\n" }));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(
            profile with { DisplayName = new string('g', 81) }));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(
            profile with { BaseUrl = "https://GATEWAY.example.test/" }));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(
            profile with { BaseUrl = "https://gateway.example.test/api" }));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(
            profile with { TargetHash = $"sha256:{new string('0', 64)}" }));
    }

    [Fact]
    public void RejectsInvalidLifecycleRevisionErrorAndUtcBoundaries()
    {
        var profile = ValidProfile();

        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(
            profile with { State = (ConnectionProfileLifecycleState)999 }));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(profile with { Revision = 0 }));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(profile with { TargetRevision = 0 }));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(profile with { CredentialRevision = -1 }));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(profile with { FixedErrorCode = "Mail.invalid" }));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(profile with { FixedErrorCode = "mail-invalid" }));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(
            profile with { FixedErrorCode = new string('a', 129) }));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(
            profile with { CreatedAtUtc = Now.ToOffset(TimeSpan.FromHours(9)) }));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(
            profile with { UpdatedAtUtc = Now.ToOffset(TimeSpan.FromHours(-5)) }));
        Assert.False(MailGatewayConnectionProfilePolicy.IsValid(
            profile with { UpdatedAtUtc = Now.AddTicks(-1) }));
    }

    [Fact]
    public void ProfileToStringDoesNotExposeIdentityEndpointSenderHashOrError()
    {
        const string marker = "raw-marker";
        var profile = ValidProfile(
            id: "raw-marker-profile",
            displayName: $"{marker} display",
            baseUrl: $"https://{marker}.example.test/",
            sender: $"{marker}@example.test",
            fixedErrorCode: "mail.raw_marker");

        var text = profile.ToString();

        Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
        Assert.DoesNotContain(profile.TargetHash, text, StringComparison.Ordinal);
        Assert.Contains("Enabled=True", text, StringComparison.Ordinal);
        Assert.Contains("HasFixedError=True", text, StringComparison.Ordinal);
    }

    private static MailGatewayConnectionProfile ValidProfile(
        string id = "mail-gateway",
        string displayName = "Mail Gateway",
        string baseUrl = "https://gateway.example.test/",
        string sender = "sender@example.com",
        string? fixedErrorCode = null)
    {
        var targetHash = MailGatewayConnectionProfilePolicy.ComputeTargetHash(baseUrl, sender);
        return new MailGatewayConnectionProfile(
            ConnectionProfileId.Parse(id),
            displayName,
            baseUrl,
            sender,
            Enabled: true,
            ConnectionProfileLifecycleState.Stable,
            Revision: 3,
            TargetRevision: 2,
            CredentialRevision: 1,
            targetHash,
            fixedErrorCode,
            Now,
            Now);
    }
}
