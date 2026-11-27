using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class McpSettingsSectionControllerTests
{
    [Fact]
    public void SampleRagUsesFixedTransportToolsAndNoStaleCredential()
    {
        var existing = new McpConnectionSettings(
            "기존", "https://old.example/mcp", true, ["old"], "stale-token");

        var settings = McpSettingsSectionController.Validate(
            new McpSettingsInput("", "", true, "", "new-token", UseSampleRag: true), existing);

        Assert.Equal("지식 검색", settings.DisplayName);
        Assert.Equal("stdio://ligclaw-sample-rag", settings.Url);
        Assert.Equal("stdio", settings.Transport);
        Assert.Null(settings.AuthorizationToken);
        Assert.Equal(["search_knowledge", "get_document"], settings.AllowedTools);
    }

    [Fact]
    public async Task SavesBeforeApplyingAndPreservesStoredTokenWhenInputIsBlank()
    {
        var existing = new McpConnectionSettings(
            "기존", "https://old.example/mcp", true, ["old"], "stored-token");
        var store = new FakeStore(existing);
        var appliedAfterSave = false;
        var controller = new McpSettingsSectionController(store, (settings, _) =>
        {
            appliedAfterSave = store.Saved is not null;
            return Task.FromResult(new McpConnectionStatus("connected", 1, ["search"], ["search"]));
        });

        var result = await controller.SaveAsync(
            new McpSettingsInput("지식", "https://new.example/mcp/", true, "search\nsearch", "", false),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(appliedAfterSave);
        Assert.Equal("stored-token", result.Settings.AuthorizationToken);
        Assert.Equal("https://new.example/mcp", result.Settings.Url);
        Assert.Equal(["search"], result.Settings.AllowedTools);
    }

    [Fact]
    public async Task EmptyDisabledSectionIsNoOpButEnabledSectionRequiresAddress()
    {
        var store = new FakeStore(null);
        var applyCount = 0;
        var controller = new McpSettingsSectionController(store, (_, _) =>
        {
            applyCount++;
            return Task.FromResult(new McpConnectionStatus("disabled", 0, [], []));
        });

        var empty = new McpSettingsInput("", "", false, "", "", false);
        Assert.Null(await controller.SaveAsync(empty, CancellationToken.None));
        await Assert.ThrowsAsync<SettingsSectionValidationException>(() =>
            controller.SaveAsync(empty with { Enabled = true }, CancellationToken.None));
        Assert.Null(store.Saved);
        Assert.Equal(0, applyCount);
    }

    private sealed class FakeStore(McpConnectionSettings? current) : IMcpConnectionSettingsStore
    {
        public McpConnectionSettings? Saved { get; private set; }

        public McpConnectionSettings? Load() => current;

        public void Save(McpConnectionSettings settings) => Saved = settings;
    }
}
