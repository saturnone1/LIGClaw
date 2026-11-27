using LIGClaw.Application.Connections;

namespace LIGClaw.Application.Tests;

public sealed class CapabilityConnectionKindPolicyTests
{
    [Fact]
    public void ExposesTheSameExactKindsUsedByValidation()
    {
        Assert.Equal(["http", "mail-api", "smtp"], CapabilityConnectionKindPolicy.SupportedKinds);
        Assert.All(CapabilityConnectionKindPolicy.SupportedKinds, kind =>
            Assert.True(CapabilityConnectionKindPolicy.IsSupported(kind)));
    }

    [Theory]
    [InlineData("http", true)]
    [InlineData("mail-api", true)]
    [InlineData("smtp", true)]
    [InlineData("HTTP", false)]
    [InlineData("mail_api", false)]
    [InlineData("SMTP", false)]
    [InlineData("sql", false)]
    [InlineData(null, false)]
    public void AllowsOnlyTheThreeExactApplicationConnectionKinds(string? kind, bool expected)
    {
        Assert.Equal(expected, CapabilityConnectionKindPolicy.IsSupported(kind));
    }
}
