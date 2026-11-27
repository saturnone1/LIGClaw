using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class AgentFailurePolicyTests
{
    [Theory]
    [InlineData("runtime.provider_authentication", "API 키 인증")]
    [InlineData("runtime.provider_rate_limited", "요청 한도")]
    [InlineData("runtime.network_unavailable", "네트워크")]
    [InlineData("runtime.context_limit", "컨텍스트")]
    public void Maps_fixed_runtime_failure_codes(string code, string expected)
    {
        Assert.Contains(expected, AgentFailurePolicy.ToUserMessage(code));
    }

    [Fact]
    public void Does_not_echo_untrusted_sidecar_error_text()
    {
        const string untrusted = "api_key=secret prompt=private-content";

        var message = AgentFailurePolicy.ToUserMessage(untrusted);

        Assert.DoesNotContain("secret", message);
        Assert.DoesNotContain("private-content", message);
        Assert.Equal("요청을 처리하지 못했어요. 다시 시도해 주세요.", message);
    }
}
