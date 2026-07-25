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
    public void ShortcutCatalogHasStableUniqueChoices()
    {
        Assert.Equal("ctrl-alt-space", QuickAccessShortcutCatalog.Default.Id);
        Assert.Equal(
            QuickAccessShortcutCatalog.All.Count,
            QuickAccessShortcutCatalog.All.Select(shortcut => shortcut.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Same(QuickAccessShortcutCatalog.Default, QuickAccessShortcutCatalog.Find("unknown"));
    }
}
