using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class PersonalMemoryPolicyTests
{
    [Fact]
    public void AcceptsExplicitBoundedMemoryWithFutureExpiry()
    {
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        var draft = new PersonalMemoryDraft(
            "alias", "프로젝트 별칭", "LIGClaw", "general", "conversation:test", now.AddDays(30));

        Assert.True(PersonalMemoryPolicy.IsValid(draft, now));
    }

    [Theory]
    [InlineData("API key")]
    [InlineData("비밀번호")]
    [InlineData("refresh token")]
    public void RejectsCredentialMemoryKeys(string key)
    {
        var now = DateTimeOffset.UtcNow;
        var draft = new PersonalMemoryDraft("note", key, "do-not-store", "sensitive", "conversation:test", null);

        Assert.False(PersonalMemoryPolicy.IsValid(draft, now));
    }

    [Theory]
    [InlineData("nvapi-example-token")]
    [InlineData("Authorization: Bearer example")]
    [InlineData("password=secret")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    public void RejectsCredentialLikeMemoryValues(string value)
    {
        var now = DateTimeOffset.UtcNow;
        var draft = new PersonalMemoryDraft("note", "서버 설정", value, "sensitive", "conversation:test", null);

        Assert.False(PersonalMemoryPolicy.IsValid(draft, now));
    }
}
