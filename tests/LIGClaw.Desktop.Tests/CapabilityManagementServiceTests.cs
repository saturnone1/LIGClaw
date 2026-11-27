using LIGClaw.Application.Capabilities;
using LIGClaw.Desktop.Infrastructure.Capabilities;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Tests;

public sealed class CapabilityManagementServiceTests
{
    [Fact]
    public void DynamicDependencyAvailabilityChangesOnlyFutureReadinessChecks()
    {
        var available = false;
        var dependencies = new CapabilityToolDependencyCatalog(
            [new CapabilityToolDependency("mcp.read.v1", "R3")],
            name => name != "mcp.read.v1" || available);

        Assert.False(dependencies.TryGetTool("mcp.read.v1", out _));
        available = true;
        Assert.True(dependencies.TryGetTool("mcp.read.v1", out var dependency));
        Assert.Equal("R3", dependency!.Risk);
    }

    [Fact]
    public async Task RealCatalogCompletesImportReviewActivateRollbackAndExport()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var codec = new ZipLigcapPackageCodec();
            var firstPayload = CanonicalPayload("company.summary", "1.0.0", "첫 지침");
            var secondPayload = CanonicalPayload("company.summary", "2.0.0", "둘째 지침");
            var firstPackage = Path.Combine(directory, "company.summary-1.0.0.ligcap");
            var secondPackage = Path.Combine(directory, "company.summary-2.0.0.ligcap");
            await WritePackageAsync(codec, firstPackage, firstPayload);
            await WritePackageAsync(codec, secondPackage, secondPayload);

            using var store = new ConversationStore(Path.Combine(directory, "catalog.db"));
            await store.InitializeAsync();
            var management = new CapabilityManagementService(
                new CapabilityCatalogService(
                    store.Capabilities,
                    new CapabilityToolDependencyCatalog([])),
                codec);

            var firstImport = await management.ImportPackageAsync(
                firstPackage,
                DateTimeOffset.UnixEpoch.AddMinutes(1),
                CancellationToken.None);
            Assert.Equal(CapabilityManagementOutcome.Succeeded, firstImport.Outcome);
            var duplicateImport = await management.ImportPackageAsync(
                firstPackage,
                DateTimeOffset.UnixEpoch.AddMinutes(1),
                CancellationToken.None);
            Assert.Equal(CapabilityManagementOutcome.Duplicate, duplicateImport.Outcome);
            Assert.Equal(firstImport.Draft?.ExactVersion, duplicateImport.Draft?.ExactVersion);
            var firstItem = Assert.Single(await management.ListAsync(
                CapabilityManagementFilter.ReadyForReview,
                CapabilityManagementSort.RecentlyUpdated,
                CancellationToken.None));
            var firstReview = await management.GetActivationReviewAsync(
                firstItem.DraftId,
                firstItem.Summary.LatestVersion,
                firstItem.Summary.CatalogRevision,
                CancellationToken.None);
            Assert.Equal(CapabilityManagementOutcome.Succeeded, (await management.ActivateAsync(
                Assert.IsType<CapabilityActivationReview>(firstReview.Review),
                DateTimeOffset.UnixEpoch.AddMinutes(2),
                CancellationToken.None)).Outcome);

            var secondImport = await management.ImportPackageAsync(
                secondPackage,
                DateTimeOffset.UnixEpoch.AddMinutes(3),
                CancellationToken.None);
            Assert.Equal(CapabilityManagementOutcome.Succeeded, secondImport.Outcome);
            Assert.Equal(firstItem.Summary.LatestVersion, secondImport.Draft?.Parent);
            var secondItem = Assert.Single(await management.ListAsync(
                CapabilityManagementFilter.ReadyForReview,
                CapabilityManagementSort.RecentlyUpdated,
                CancellationToken.None));
            var secondReview = await management.GetActivationReviewAsync(
                secondItem.DraftId,
                secondItem.Summary.LatestVersion,
                secondItem.Summary.CatalogRevision,
                CancellationToken.None);
            Assert.Equal(CapabilityManagementOutcome.Succeeded, (await management.ActivateAsync(
                Assert.IsType<CapabilityActivationReview>(secondReview.Review),
                DateTimeOffset.UnixEpoch.AddMinutes(4),
                CancellationToken.None)).Outcome);

            var activeItem = Assert.Single(await management.ListAsync(
                CapabilityManagementFilter.Active,
                CapabilityManagementSort.RecentlyUpdated,
                CancellationToken.None));
            var rollbackReview = await management.GetRollbackReviewAsync(
                activeItem.Summary.Id,
                Assert.IsType<CapabilityVersionRef>(activeItem.Summary.ActiveVersion),
                activeItem.Summary.CatalogRevision,
                CancellationToken.None);
            var rollback = await management.RollbackAsync(
                Assert.IsType<CapabilityRollbackReview>(rollbackReview.Review),
                DateTimeOffset.UnixEpoch.AddMinutes(5),
                CancellationToken.None);
            Assert.Equal(CapabilityManagementOutcome.Succeeded, rollback.Outcome);
            Assert.Equal("1.0.0", rollback.Catalog?.ActiveVersion?.Version.Value);

            var exportPath = Path.Combine(directory, "exported.ligcap");
            Assert.Equal(CapabilityManagementOutcome.Succeeded, (await management.ExportVersionAsync(
                Assert.IsType<CapabilityVersionRef>(rollback.Catalog?.ActiveVersion),
                exportPath,
                CancellationToken.None)).Outcome);
            await using var exported = File.OpenRead(exportPath);
            var reread = await codec.ReadAsync(exported, CancellationToken.None);
            Assert.True(reread.Succeeded);
            Assert.Equal("1.0.0", reread.Document?.Manifest.Version.Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportBindsTheCurrentActiveExactVersionAsParent()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var packagePath = Path.Combine(directory, "sample.ligcap");
            await File.WriteAllBytesAsync(packagePath, [1, 2, 3]);
            var payload = Payload("company.summary", "2.0.0", "새 지침");
            var contentHash = CapabilityContentHasher.Compute(payload);
            var sourceHash = Hash('b');
            var active = VersionRef("company.summary", "1.0.0", 'a');
            var catalog = new FakeCatalogService
            {
                CurrentCatalog = new CapabilityCatalogEntry(
                    payload.Manifest.Id,
                    active,
                    1,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch),
            };
            var codec = new FakeCodec
            {
                ReadResult = new CapabilityPackageReadResult(
                    new CapabilityPackageDocument(payload.Manifest, payload, null),
                    [],
                    contentHash,
                    sourceHash),
            };
            var service = new CapabilityManagementService(catalog, codec);

            var result = await service.ImportPackageAsync(
                packagePath,
                DateTimeOffset.UnixEpoch.AddMinutes(1),
                CancellationToken.None);

            Assert.Equal(CapabilityManagementOutcome.Succeeded, result.Outcome);
            Assert.NotNull(result.Draft);
            Assert.Equal(active, Assert.IsType<ValidatedCapabilityDraft>(catalog.SubmittedDraft).Parent);
            Assert.Equal(sourceHash, catalog.SubmittedDraft.SourcePackageHash);
            Assert.Equal("local_import", catalog.SubmittedDraft.SourceKind);
            Assert.Equal(contentHash, result.Draft.ExactVersion.ContentHash);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SensitiveSchemaLiteralIsRejectedBeforeAnyCapabilityRowIsStored()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var packagePath = Path.Combine(directory, "sensitive.ligcap");
            await File.WriteAllBytesAsync(packagePath, [1]);
            var databasePath = Path.Combine(directory, "catalog.db");
            var payload = CanonicalPayload("company.sensitive", "1.0.0", "안전한 지침") with
            {
                CanonicalInputSchemaJson =
                    "{\"additionalProperties\":false,\"properties\":{\"apiKey\":{\"const\":\"correct-horse-battery-staple\",\"maxLength\":64,\"type\":\"string\"}},\"type\":\"object\"}",
            };
            var contentHash = CapabilityContentHasher.Compute(payload);
            var codec = new FakeCodec
            {
                ReadResult = new CapabilityPackageReadResult(
                    new CapabilityPackageDocument(payload.Manifest, payload, null),
                    [],
                    contentHash,
                    Hash('a')),
            };
            using (var store = new ConversationStore(databasePath))
            {
                await store.InitializeAsync();
                var management = new CapabilityManagementService(
                    new CapabilityCatalogService(store.Capabilities, new CapabilityToolDependencyCatalog([])),
                    codec);

                var result = await management.ImportPackageAsync(
                    packagePath,
                    DateTimeOffset.UnixEpoch,
                    CancellationToken.None);

                Assert.Equal(CapabilityManagementOutcome.Invalid, result.Outcome);
                Assert.Equal("capability.secret_literal.detected", result.ErrorCode);
            }

            await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await connection.OpenAsync();
            foreach (var table in new[]
                     {
                         "capabilities",
                         "capability_drafts",
                         "capability_versions",
                         "capability_activations",
                     })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM {table};";
                Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ActivationReviewFailsClosedWhenCatalogRevisionChanges()
    {
        var payload = Payload("company.summary", "2.0.0", "새 지침");
        var exact = new CapabilityVersionRef(
            payload.Manifest.Id,
            payload.Manifest.Version,
            CapabilityContentHasher.Compute(payload));
        var parent = VersionRef("company.summary", "1.0.0", 'a');
        var draft = Draft("draft_1", payload, exact, parent);
        var reviewedCatalog = new CapabilityCatalogEntry(
            payload.Manifest.Id,
            parent,
            1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = new FakeCatalogService
        {
            CurrentCatalog = reviewedCatalog with { CatalogRevision = 2 },
        };
        catalog.Drafts[draft.DraftId] = draft;
        var service = new CapabilityManagementService(catalog, new FakeCodec());

        var result = await service.ActivateAsync(
            new CapabilityActivationReview(draft, reviewedCatalog),
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(CapabilityManagementOutcome.Stale, result.Outcome);
        Assert.Null(catalog.ApprovalCommand);
    }

    [Fact]
    public async Task ActivationPassesTheReviewedExactVersionToTheCatalogCas()
    {
        var payload = Payload("company.summary", "2.0.0", "새 지침");
        var exact = new CapabilityVersionRef(
            payload.Manifest.Id,
            payload.Manifest.Version,
            CapabilityContentHasher.Compute(payload));
        var parent = VersionRef("company.summary", "1.0.0", 'a');
        var draft = Draft("draft_1", payload, exact, parent);
        var reviewedCatalog = new CapabilityCatalogEntry(
            payload.Manifest.Id,
            parent,
            1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = new FakeCatalogService { CurrentCatalog = reviewedCatalog };
        catalog.Drafts[draft.DraftId] = draft;
        var service = new CapabilityManagementService(catalog, new FakeCodec());

        var result = await service.ActivateAsync(
            new CapabilityActivationReview(draft, reviewedCatalog),
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(CapabilityManagementOutcome.Succeeded, result.Outcome);
        Assert.Equal(exact, Assert.IsType<CapabilityApprovalCommand>(catalog.ApprovalCommand).ExpectedVersion);
    }

    [Fact]
    public async Task RollbackReviewUsesThePreviousActivatedExactVersion()
    {
        var id = CapabilityId.Parse("company.summary");
        var targetPayload = Payload(id.Value, "1.0.0", "이전 지침");
        var currentPayload = Payload(id.Value, "2.0.0", "현재 지침");
        var targetRef = new CapabilityVersionRef(id, targetPayload.Manifest.Version, CapabilityContentHasher.Compute(targetPayload));
        var currentRef = new CapabilityVersionRef(id, currentPayload.Manifest.Version, CapabilityContentHasher.Compute(currentPayload));
        var catalogEntry = new CapabilityCatalogEntry(
            id,
            currentRef,
            2,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = new FakeCatalogService
        {
            CurrentCatalog = catalogEntry,
            Activations =
            [
                new CapabilityActivation(
                    "activation_2",
                    id,
                    2,
                    "activate",
                    targetRef,
                    currentRef,
                    "test.activate",
                    DateTimeOffset.UnixEpoch),
            ],
        };
        catalog.Versions.Add(new CapabilityVersion(currentRef, targetRef, currentPayload, "local_import", Hash('c'), DateTimeOffset.UnixEpoch));
        catalog.Versions.Add(new CapabilityVersion(targetRef, null, targetPayload, "local_import", Hash('d'), DateTimeOffset.UnixEpoch));
        var service = new CapabilityManagementService(catalog, new FakeCodec());

        var review = await service.GetRollbackReviewAsync(id, currentRef, 2, CancellationToken.None);
        var mutation = await service.RollbackAsync(
            Assert.IsType<CapabilityRollbackReview>(review.Review),
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(CapabilityManagementOutcome.Succeeded, mutation.Outcome);
        Assert.Equal(targetRef, Assert.IsType<CapabilityRollbackCommand>(catalog.RollbackCommand).TargetVersion);
        Assert.Equal(currentRef, catalog.RollbackCommand.ExpectedActiveVersion);
    }

    [Fact]
    public async Task ExportWritesOnlyAnImmutableVersionAndLeavesNoTemporaryFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var payload = Payload("company.summary", "1.0.0", "지침");
            var exact = new CapabilityVersionRef(
                payload.Manifest.Id,
                payload.Manifest.Version,
                CapabilityContentHasher.Compute(payload));
            var catalog = new FakeCatalogService();
            catalog.Versions.Add(new CapabilityVersion(
                exact,
                null,
                payload,
                "local_import",
                Hash('e'),
                DateTimeOffset.UnixEpoch));
            var codec = new FakeCodec { ExportBytes = [4, 3, 2, 1] };
            var service = new CapabilityManagementService(catalog, codec);
            var destination = Path.Combine(directory, "export.ligcap");

            var result = await service.ExportVersionAsync(exact, destination, CancellationToken.None);

            Assert.Equal(CapabilityManagementOutcome.Succeeded, result.Outcome);
            Assert.Equal(codec.ExportBytes, await File.ReadAllBytesAsync(destination));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly));
            Assert.Equal(exact, codec.WrittenDocument?.Payload is { } written
                ? new CapabilityVersionRef(written.Manifest.Id, written.Manifest.Version, CapabilityContentHasher.Compute(written))
                : null);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportRejectsWrongExtensionBeforeOpeningTheCodec()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "sample.zip");
            await File.WriteAllBytesAsync(path, [1]);
            var codec = new FakeCodec();
            var service = new CapabilityManagementService(new FakeCatalogService(), codec);

            var result = await service.ImportPackageAsync(path, DateTimeOffset.UnixEpoch, CancellationToken.None);

            Assert.Equal(CapabilityManagementOutcome.Invalid, result.Outcome);
            Assert.Equal("capability.import.path_invalid", result.ErrorCode);
            Assert.Equal(0, codec.ReadCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static CapabilityDraft Draft(
        string draftId,
        CapabilityVersionPayload payload,
        CapabilityVersionRef exact,
        CapabilityVersionRef? parent) => new(
        draftId,
        exact,
        parent,
        payload,
        CapabilityDraftState.ReadyForReview,
        0,
        "local_import",
        Hash('f'),
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);

    private static CapabilityVersionPayload Payload(string id, string version, string instructions)
    {
        var manifest = new CapabilityManifest(
            1,
            CapabilityId.Parse(id),
            CapabilitySemanticVersion.Parse(version),
            "문서 요약",
            "문서의 핵심을 요약합니다.",
            CapabilityKind.Prompt,
            ["요약해 줘"],
            [],
            [],
            "R0",
            "instructions.md");
        return new CapabilityVersionPayload(
            manifest,
            $"{{\"id\":\"{id}\",\"version\":\"{version}\"}}",
            instructions,
            "{\"type\":\"object\"}");
    }

    private static CapabilityVersionPayload CanonicalPayload(string id, string version, string instructions)
    {
        var manifest = new CapabilityManifest(
            1,
            CapabilityId.Parse(id),
            CapabilitySemanticVersion.Parse(version),
            "문서 요약",
            "문서의 핵심을 요약합니다.",
            CapabilityKind.Prompt,
            ["요약해 줘"],
            [],
            [],
            "R0",
            "instructions.md");
        var canonicalManifest =
            $"{{\"description\":\"문서의 핵심을 요약합니다.\",\"displayName\":\"문서 요약\",\"entrypoint\":\"instructions.md\",\"id\":\"{id}\",\"intentExamples\":[\"요약해 줘\"],\"kind\":\"prompt\",\"maximumRisk\":\"R0\",\"requiredConnections\":[],\"requiredTools\":[],\"schemaVersion\":1,\"version\":\"{version}\"}}";
        return new CapabilityVersionPayload(
            manifest,
            canonicalManifest,
            instructions,
            "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}");
    }

    private static async Task WritePackageAsync(
        ILigcapPackageCodec codec,
        string path,
        CapabilityVersionPayload payload)
    {
        await using var destination = File.Create(path);
        await codec.WriteAsync(
            destination,
            new CapabilityPackageDocument(payload.Manifest, payload, null),
            CancellationToken.None);
    }

    private static CapabilityVersionRef VersionRef(string id, string version, char hash) =>
        new(CapabilityId.Parse(id), CapabilitySemanticVersion.Parse(version), Hash(hash));

    private static CapabilityContentHash Hash(char value) =>
        CapabilityContentHash.Parse($"sha256:{new string(value, 64)}");

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ligclaw-capability-management-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeCodec : ILigcapPackageCodec
    {
        public CapabilityPackageReadResult ReadResult { get; set; } = new(null, [new("package.invalid")]);
        public byte[] ExportBytes { get; set; } = [1];
        public int ReadCount { get; private set; }
        public CapabilityPackageDocument? WrittenDocument { get; private set; }

        public Task<CapabilityPackageReadResult> ReadAsync(Stream source, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(ReadResult);
        }

        public async Task WriteAsync(
            Stream destination,
            CapabilityPackageDocument document,
            CancellationToken cancellationToken)
        {
            WrittenDocument = document;
            await destination.WriteAsync(ExportBytes, cancellationToken);
        }
    }

    private sealed class FakeCatalogService : ICapabilityCatalogService
    {
        public CapabilityCatalogEntry? CurrentCatalog { get; set; }
        public Dictionary<string, CapabilityDraft> Drafts { get; } = new(StringComparer.Ordinal);
        public List<CapabilityVersion> Versions { get; } = [];
        public IReadOnlyList<CapabilityActivation> Activations { get; set; } = [];
        public ValidatedCapabilityDraft? SubmittedDraft { get; private set; }
        public CapabilityApprovalCommand? ApprovalCommand { get; private set; }
        public CapabilityRollbackCommand? RollbackCommand { get; private set; }

        public Task<CapabilityCatalogEntry?> GetCatalogAsync(CapabilityId id, CancellationToken cancellationToken) =>
            Task.FromResult(CurrentCatalog is not null && CurrentCatalog.Id.Equals(id) ? CurrentCatalog : null);

        public Task<IReadOnlyList<CapabilityCatalogSummary>> ListAsync(
            string? query,
            int offset,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityCatalogSummary>>([]);

        public Task<CapabilityDraft?> GetDraftAsync(string draftId, CancellationToken cancellationToken) =>
            Task.FromResult(Drafts.GetValueOrDefault(draftId));

        public Task<CapabilityVersion?> GetVersionAsync(
            CapabilityVersionRef exactVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult(Versions.FirstOrDefault(version => version.ExactVersion.Equals(exactVersion)));

        public Task<IReadOnlyList<CapabilityActivation>> ListActivationsAsync(
            CapabilityId id,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(Activations);

        public Task<CapabilityDraftCreateResult> SubmitValidatedDraftAsync(
            ValidatedCapabilityDraft draft,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken)
        {
            SubmittedDraft = draft;
            var created = new CapabilityDraft(
                draft.DraftId,
                draft.ExactVersion,
                draft.Parent,
                draft.Payload,
                CapabilityDraftState.ReadyForReview,
                0,
                draft.SourceKind,
                draft.SourcePackageHash,
                nowUtc,
                nowUtc);
            Drafts[created.DraftId] = created;
            return Task.FromResult(new CapabilityDraftCreateResult(CapabilityMutationOutcome.Succeeded, created));
        }

        public Task<CapabilityActivationResult> ApproveAndActivateAsync(
            CapabilityApprovalCommand command,
            CancellationToken cancellationToken)
        {
            ApprovalCommand = command;
            return Task.FromResult(new CapabilityActivationResult(
                CapabilityMutationOutcome.Succeeded,
                CurrentCatalog));
        }

        public Task<CapabilityActivationResult> RollbackAsync(
            CapabilityRollbackCommand command,
            CancellationToken cancellationToken)
        {
            RollbackCommand = command;
            return Task.FromResult(new CapabilityActivationResult(
                CapabilityMutationOutcome.Succeeded,
                CurrentCatalog));
        }
    }
}
