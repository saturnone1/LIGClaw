using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class KnowledgeSettingsSectionControllerTests
{
    [Fact]
    public void SemanticMemoryNormalizesInputAndReusesExistingSecret()
    {
        var existing = new SemanticMemorySettings(
            true, "https://old.example/v1", "old-model", "stored-key");
        var store = new FakeSemanticStore(existing);
        var controller = new SemanticMemorySettingsSectionController(store);

        var saved = controller.Save(
            new SemanticMemorySettingsInput(true, "https://new.example/v1/", "new-model", ""),
            existing);

        Assert.Equal("https://new.example/v1", saved.BaseUrl);
        Assert.Equal("stored-key", saved.ApiKey);
        Assert.Equal(saved, store.Saved);
    }

    [Fact]
    public void InvalidSemanticMemoryNeverReachesStore()
    {
        var store = new FakeSemanticStore(new SemanticMemorySettings(false, "http://127.0.0.1:11434", "model"));
        var controller = new SemanticMemorySettingsSectionController(store);

        Assert.Throws<SettingsSectionValidationException>(() => controller.Save(
            new SemanticMemorySettingsInput(true, "not-a-url", "model", null),
            existing: null));

        Assert.Null(store.Saved);
    }

    [Fact]
    public void WebSearchValidationAndDeletionAreOwnedBySectionController()
    {
        var store = new FakeWebStore();
        var controller = new WebSearchSettingsSectionController(store);

        var saved = controller.Save("https://search.example/?q={query}");
        Assert.Equal(saved, store.Saved);
        Assert.Throws<SettingsSectionValidationException>(() => controller.Save("https://search.example/"));
        Assert.Equal(saved, store.Saved);

        Assert.Null(controller.Save(""));
        Assert.Null(store.Saved);
    }

    private sealed class FakeSemanticStore(SemanticMemorySettings current) : ISemanticMemorySettingsStore
    {
        public SemanticMemorySettings? Saved { get; private set; }

        public SemanticMemorySettings Load() => current;

        public void Save(SemanticMemorySettings settings) => Saved = settings;
    }

    private sealed class FakeWebStore : IWebSearchSettingsStore
    {
        public WebSearchSettings? Saved { get; private set; }

        public WebSearchSettings? Load() => Saved;

        public void Save(WebSearchSettings? settings) => Saved = settings;
    }
}
