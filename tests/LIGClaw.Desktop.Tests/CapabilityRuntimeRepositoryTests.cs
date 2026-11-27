using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class CapabilityRuntimeRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public CapabilityRuntimeRepositoryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task LoadsOnlyActiveExactImmutablePayloadAndKeepsMaterializedCandidateFrozen()
    {
        using var store = await CreateStoreAsync("runtime-active.db");
        Assert.Same(store.Capabilities, store.CapabilityRuntimeCandidates);
        var v1 = Draft(
            "draft-runtime-v1",
            "1.0.0",
            displayName: "Atomic Runtime Helper",
            instructions: "v1 frozen instructions",
            inputSchema: "{\"type\":\"object\",\"properties\":{\"v1\":{\"type\":\"string\"}},\"additionalProperties\":false}");
        await CreateAndActivateAsync(store, v1, 0, null, Timestamp(0));
        var v2 = Draft(
            "draft-runtime-v2",
            "2.0.0",
            v1.ExactVersion,
            displayName: "Future Runtime Helper",
            instructions: "v2 proposed instructions",
            inputSchema: "{\"type\":\"object\",\"properties\":{\"v2\":{\"type\":\"string\"}},\"additionalProperties\":false}");
        Assert.Equal(CapabilityMutationOutcome.Succeeded, (
            await store.Capabilities.CreateValidatedDraftAsync(
                v2, Timestamp(2), CancellationToken.None)).Outcome);

        var frozen = Assert.Single(await store.CapabilityRuntimeCandidates.LoadActiveRuntimeCandidatesAsync(
            "Runtime", 8, CancellationToken.None));
        Assert.Equal(v1.ExactVersion, frozen.ExactVersion);
        Assert.Equal(1, frozen.CatalogRevision);
        Assert.Equal("v1 frozen instructions", frozen.Payload.Instructions);
        Assert.Contains("\"v1\"", frozen.Payload.CanonicalInputSchemaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("v2 proposed", frozen.Payload.Instructions, StringComparison.Ordinal);

        await ActivateExistingAsync(store, v2, 1, v1.ExactVersion, Timestamp(3));
        var current = Assert.Single(await store.CapabilityRuntimeCandidates.LoadActiveRuntimeCandidatesAsync(
            "Runtime", 8, CancellationToken.None));
        Assert.Equal(v2.ExactVersion, current.ExactVersion);
        Assert.Equal(2, current.CatalogRevision);
        Assert.Equal("v2 proposed instructions", current.Payload.Instructions);
        Assert.Equal(v1.ExactVersion, frozen.ExactVersion);
        Assert.Equal(1, frozen.CatalogRevision);
        Assert.Equal("v1 frozen instructions", frozen.Payload.Instructions);
    }

    [Fact]
    public async Task RollbackLoadsHistoricalExactPayloadWithNewCatalogRevision()
    {
        using var store = await CreateStoreAsync("runtime-rollback.db");
        var v1 = Draft(
            "draft-rollback-v1",
            "1.0.0",
            displayName: "Rollback Runtime",
            instructions: "historical instructions");
        await CreateAndActivateAsync(store, v1, 0, null, Timestamp(0));
        var v2 = Draft(
            "draft-rollback-v2",
            "2.0.0",
            v1.ExactVersion,
            displayName: "Rollback Runtime",
            instructions: "replacement instructions");
        await CreateAndActivateAsync(store, v2, 1, v1.ExactVersion, Timestamp(2));

        var result = await store.Capabilities.RollbackAsync(
            new CapabilityRollbackCommand(
                v1.ExactVersion.Id,
                2,
                v2.ExactVersion,
                v1.ExactVersion,
                "runtime.rollback",
                Timestamp(4)),
            CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.Succeeded, result.Outcome);
        var candidate = Assert.Single(await store.CapabilityRuntimeCandidates.LoadActiveRuntimeCandidatesAsync(
            "Rollback", 8, CancellationToken.None));
        Assert.Equal(v1.ExactVersion, candidate.ExactVersion);
        Assert.Equal(3, candidate.CatalogRevision);
        Assert.Equal("historical instructions", candidate.Payload.Instructions);
    }

    [Fact]
    public async Task RuntimeSourceFailsClosedIfPersistedPayloadContainsSecretLiteral()
    {
        using var store = await CreateStoreAsync("runtime-secret.db");
        var unsafeDraft = Draft(
            "draft-secret",
            "1.0.0",
            displayName: "Secret Runtime",
            instructions: "token=ordinary-secret-value");
        await CreateAndActivateAsync(store, unsafeDraft, 0, null, Timestamp(0));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.CapabilityRuntimeCandidates.LoadActiveRuntimeCandidatesAsync(
                "Secret", 8, CancellationToken.None));

        Assert.DoesNotContain("ordinary-secret-value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeSourceFailsClosedIfPersistedPayloadContainsConnectionLiteral()
    {
        using var store = await CreateStoreAsync("runtime-connection.db");
        var unsafeDraft = Draft(
            "draft-connection",
            "1.0.0",
            displayName: "Connection Runtime",
            instructions: "endpoint=https://runtime.internal.invalid/v1");
        await CreateAndActivateAsync(store, unsafeDraft, 0, null, Timestamp(0));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.CapabilityRuntimeCandidates.LoadActiveRuntimeCandidatesAsync(
                "Connection", 8, CancellationToken.None));

        Assert.DoesNotContain("runtime.internal.invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeSourceEnforcesStrictBoundedFtsInputs()
    {
        using var store = await CreateStoreAsync("runtime-input.db");

        Assert.Empty(await store.CapabilityRuntimeCandidates.LoadActiveRuntimeCandidatesAsync(
            "두자", 8, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.CapabilityRuntimeCandidates.LoadActiveRuntimeCandidatesAsync(
                "unsafe\nquery", 8, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.CapabilityRuntimeCandidates.LoadActiveRuntimeCandidatesAsync(
                new string('a', 257), 8, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.CapabilityRuntimeCandidates.LoadActiveRuntimeCandidatesAsync(
                "bounded query", 9, CancellationToken.None));
    }

    [Fact]
    public async Task BuilderOmitsActiveDatabasePayloadWhoseInputSchemaRootIsUnbounded()
    {
        using var store = await CreateStoreAsync("runtime-schema.db");
        var unbounded = Draft(
            "draft-unbounded-schema",
            "1.0.0",
            displayName: "Unbounded Runtime",
            inputSchema: "{\"type\":\"object\",\"properties\":{}}");
        await CreateAndActivateAsync(store, unbounded, 0, null, Timestamp(0));
        var builder = new CapabilityRuntimeSnapshotBuilder(
            store.CapabilityRuntimeCandidates,
            new EmptyDependencies());

        var snapshot = await builder.BuildAsync("Unbounded", CancellationToken.None);

        Assert.Empty(snapshot.Candidates);
    }

    [Fact]
    public async Task RuntimeSourceReturnsStableIdOrderAndNeverMoreThanEight()
    {
        using var store = await CreateStoreAsync("runtime-limit.db");
        for (var index = 9; index >= 0; index--)
        {
            var draft = Draft(
                $"draft-shared-{index:D2}",
                "1.0.0",
                displayName: "Shared Runtime",
                capabilityId: $"sample.runtime{index:D2}");
            await CreateAndActivateAsync(store, draft, 0, null, Timestamp(index * 2));
        }

        var candidates = await store.CapabilityRuntimeCandidates.LoadActiveRuntimeCandidatesAsync(
            "Shared", 8, CancellationToken.None);

        Assert.Equal(8, candidates.Count);
        Assert.Equal(
            Enumerable.Range(0, 8).Select(index => $"sample.runtime{index:D2}"),
            candidates.Select(candidate => candidate.ExactVersion.Id.Value));
    }

    private async Task<ConversationStore> CreateStoreAsync(string name)
    {
        var store = new ConversationStore(Path.Combine(_directory, name));
        await store.InitializeAsync();
        return store;
    }

    private static ValidatedCapabilityDraft Draft(
        string draftId,
        string version,
        CapabilityVersionRef? parent = null,
        string displayName = "Runtime Helper",
        string instructions = "runtime instructions",
        string inputSchema = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
        string capabilityId = "sample.runtime")
    {
        var id = CapabilityId.Parse(capabilityId);
        var semanticVersion = CapabilitySemanticVersion.Parse(version);
        var manifest = new CapabilityManifest(
            1,
            id,
            semanticVersion,
            displayName,
            "Projects an active immutable runtime capability.",
            CapabilityKind.Prompt,
            ["use runtime helper"],
            [],
            [],
            "R0",
            "instructions.md");
        var manifestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = id.Value,
            version = semanticVersion.Value,
            displayName,
            description = manifest.Description,
            kind = "prompt",
            intentExamples = manifest.IntentExamples,
            requiredTools = manifest.RequiredTools,
            requiredConnections = Array.Empty<object>(),
            maximumRisk = "R0",
            entrypoint = "instructions.md",
        });
        var payload = new CapabilityVersionPayload(
            manifest,
            manifestJson,
            instructions,
            inputSchema,
            null,
            null,
            "[]");
        var contentHash = CapabilityContentHasher.Compute(payload);
        return new ValidatedCapabilityDraft(
            draftId,
            payload,
            contentHash,
            parent,
            "local_import",
            CapabilityContentHash.Parse("sha256:" + new string('b', 64)));
    }

    private static async Task CreateAndActivateAsync(
        ConversationStore store,
        ValidatedCapabilityDraft draft,
        long expectedCatalogRevision,
        CapabilityVersionRef? expectedActive,
        DateTimeOffset timestamp)
    {
        Assert.Equal(CapabilityMutationOutcome.Succeeded, (
            await store.Capabilities.CreateValidatedDraftAsync(
                draft, timestamp, CancellationToken.None)).Outcome);
        await ActivateExistingAsync(
            store,
            draft,
            expectedCatalogRevision,
            expectedActive,
            timestamp.AddMinutes(1));
    }

    private static async Task ActivateExistingAsync(
        ConversationStore store,
        ValidatedCapabilityDraft draft,
        long expectedCatalogRevision,
        CapabilityVersionRef? expectedActive,
        DateTimeOffset timestamp)
    {
        var result = await store.Capabilities.ApproveAndActivateAsync(
            new CapabilityApprovalCommand(
                draft.DraftId,
                draft.ExactVersion,
                0,
                expectedCatalogRevision,
                expectedActive,
                "runtime.activate",
                timestamp),
            CancellationToken.None);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, result.Outcome);
    }

    private static DateTimeOffset Timestamp(int minutes) =>
        new DateTimeOffset(2026, 8, 2, 0, minutes, 0, TimeSpan.Zero);

    private sealed class EmptyDependencies : ICapabilityDependencyCatalog
    {
        public bool TryGetTool(string name, out CapabilityToolDependency? dependency)
        {
            dependency = null;
            return false;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
