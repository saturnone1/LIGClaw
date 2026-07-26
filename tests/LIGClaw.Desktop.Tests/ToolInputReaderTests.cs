using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class ToolInputReaderTests
{
    [Fact]
    public void HasOnlyKeysAllowsOmittedOptionalKeys()
    {
        var input = new Dictionary<string, object?> { ["reason"] = "조회가 필요해서" };

        Assert.True(ToolInputReader.HasOnlyKeys(input, "includeInactive", "reason"));
    }

    [Fact]
    public void HasOnlyKeysRejectsUnknownKeys()
    {
        var input = new Dictionary<string, object?>
        {
            ["reason"] = "조회가 필요해서",
            ["unexpected"] = true,
        };

        Assert.False(ToolInputReader.HasOnlyKeys(input, "includeInactive", "reason"));
    }
}
