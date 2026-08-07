using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class ModelProfileSectionControllerTests
{
    [Fact]
    public void ValidatePreservesStoredSecretAndIdentifiesInvalidField()
    {
        var existing = Settings("https://old.example/v1", "stored-secret", "old-model");
        var controller = new ModelProfileSectionController(new FakeStore(existing));

        var candidate = controller.Validate(
            new ModelProfileInput("https://new.example/v1/", "new-model", "", "default", "기본 모델"),
            existing);

        Assert.Equal("https://new.example/v1", candidate.BaseUrl);
        Assert.Equal("stored-secret", candidate.ApiKey);
        var exception = Assert.Throws<SettingsSectionValidationException>(() => controller.Validate(
            new ModelProfileInput("not-a-url", "model", "key", "default", "기본 모델"), existing));
        Assert.Equal(ModelProfileField.BaseUrl, exception.Field);
    }

    [Fact]
    public void ChangedProfileRequiresMatchingConnectionTestBeforePersistence()
    {
        var existing = Settings("https://old.example/v1", "old-key", "old-model");
        var changed = Settings("https://new.example/v1", "new-key", "new-model");
        var store = new FakeStore(existing);
        var controller = new ModelProfileSectionController(store);

        Assert.Throws<SettingsSectionValidationException>(() =>
            controller.Save(changed, "", existing, lastSuccessfulTest: null));

        Assert.Empty(store.Saved);
        var result = controller.Save(changed, "", existing, changed);
        Assert.True(result.RequiresApply);
        Assert.Equal(changed, Assert.Single(store.Saved));
        Assert.Equal("default", store.Routing?.Primary);
        Assert.Empty(store.Routing?.Fallbacks ?? []);
    }

    [Fact]
    public void RejectsUnknownFallbackBeforeChangingStoredProfile()
    {
        var existing = Settings("https://old.example/v1", "old-key", "old-model");
        var store = new FakeStore(existing);
        var controller = new ModelProfileSectionController(store);

        var exception = Assert.Throws<SettingsSectionValidationException>(() =>
            controller.Save(existing, "missing", existing, lastSuccessfulTest: null));

        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
        Assert.Empty(store.Saved);
        Assert.Null(store.Routing);
    }

    private static ModelConnectionSettings Settings(string url, string key, string model) =>
        new(url, key, model);

    private sealed class FakeStore(ModelConnectionSettings? current) : IModelConnectionSettingsStore
    {
        public List<ModelConnectionSettings> Saved { get; } = [];
        public (string Primary, string[] Fallbacks)? Routing { get; private set; }

        public ModelConnectionSettings? Load() => current;

        public IReadOnlyList<ModelConnectionSettings> ListProfiles() =>
            current is null ? Saved : [current, .. Saved.Where(profile => !Equals(profile, current))];

        public void Save(ModelConnectionSettings settings) => Saved.Add(settings);

        public void SaveRouting(string defaultProfileId, IReadOnlyList<string> fallbackProfileIds) =>
            Routing = (defaultProfileId, fallbackProfileIds.ToArray());

        public ModelRoutingSettings? LoadRouting(string? explicitProfileId = null) => null;
    }
}
