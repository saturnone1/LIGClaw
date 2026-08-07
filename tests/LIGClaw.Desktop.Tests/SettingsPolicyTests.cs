using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class SettingsPolicyTests
{
    [Fact]
    public void EmptyModelFieldsDoNotBlockShellOnlySettings()
    {
        Assert.False(ModelConnectionInputPolicy.HasAnyInput("", " ", null, hasStoredSettings: false));
        Assert.True(ModelConnectionInputPolicy.HasAnyInput("https://models.example/v1", "", "", hasStoredSettings: false));
        Assert.True(ModelConnectionInputPolicy.HasAnyInput("", "", "", hasStoredSettings: true));
    }

    [Theory]
    [InlineData("http://models.example/v1", true)]
    [InlineData("http://10.0.0.5/v1", true)]
    [InlineData("http://localhost:8080/v1", false)]
    [InlineData("http://127.0.0.1:8080/v1", false)]
    [InlineData("https://models.example/v1", false)]
    [InlineData("not-a-url", false)]
    public void PlaintextWarningExemptsOnlyLocalHttp(string endpoint, bool expected)
    {
        Assert.Equal(expected, ModelConnectionInputPolicy.ShouldWarnAboutPlaintextHttp(endpoint));
    }

    [Fact]
    public void ChangedConnectionMustMatchTheLastSuccessfulTestBeforeSaving()
    {
        var stored = new ModelConnectionSettings("https://old.example/v1", "old-key", "old-model");
        var changed = new ModelConnectionSettings("https://new.example/v1", "new-key", "new-model");

        Assert.True(ModelConnectionInputPolicy.RequiresSuccessfulTest(changed, stored, lastSuccessfulTest: null));
        Assert.True(ModelConnectionInputPolicy.RequiresSuccessfulTest(changed, stored, stored));
        Assert.False(ModelConnectionInputPolicy.RequiresSuccessfulTest(changed, stored, changed));
        Assert.False(ModelConnectionInputPolicy.RequiresSuccessfulTest(stored, stored, lastSuccessfulTest: null));
    }

    [Fact]
    public void ShortcutCatalogHasStableUniqueChoices()
    {
        Assert.Equal("ctrl-alt-space", QuickAccessShortcutCatalog.Default.Id);
        Assert.Equal(
            QuickAccessShortcutCatalog.All.Count,
            QuickAccessShortcutCatalog.All.Select(shortcut => shortcut.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Same(QuickAccessShortcutCatalog.Default, QuickAccessShortcutCatalog.Find("unknown"));
    }

    [Theory]
    [InlineData("https://knowledge.example/mcp", true)]
    [InlineData("http://localhost:3000/mcp", true)]
    [InlineData("http://127.0.0.1:3000/mcp", true)]
    [InlineData("http://[::1]:3000/mcp", true)]
    [InlineData("http://knowledge.example/mcp", false)]
    [InlineData("https://user:password@knowledge.example/mcp", false)]
    [InlineData("https://knowledge.example/mcp#fragment", false)]
    [InlineData("not-a-url", false)]
    public void McpEndpointAllowsOnlyHttpsOrLocalHttp(string endpoint, bool expected)
    {
        Assert.Equal(expected, McpConnectionPolicy.TryValidate(endpoint, out _, out _));
    }

    [Theory]
    [InlineData("stdio", "stale-token", false)]
    [InlineData("streamable_http", null, false)]
    [InlineData("streamable_http", "", false)]
    [InlineData("streamable_http", "active-token", true)]
    public void McpCredentialsExistOnlyForAuthenticatedHttp(string transport, string? token, bool shouldWrite)
    {
        string? written = null;
        var deleted = false;

        McpCredentialPolicy.Apply(transport, token, value => written = value, () => deleted = true);

        Assert.Equal(shouldWrite ? token : null, written);
        Assert.Equal(!shouldWrite, deleted);
    }
}
