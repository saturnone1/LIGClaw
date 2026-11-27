using System.Text;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Tests;

public sealed class ConnectionProfileRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-02T02:00:00Z");
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public ConnectionProfileRepositoryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task MigratesSchemaFourteenToTwentyWithSecretFreeConnectionTables()
    {
        var path = Path.Combine(_directory, "schema-14.db");
        using (var current = new ConversationStore(path)) await current.InitializeAsync();
        await using (var schemaFourteen = Open(path))
        {
            await schemaFourteen.OpenAsync();
            await ExecuteAsync(
                schemaFourteen,
                """
                PRAGMA foreign_keys=OFF;
                DROP TABLE capability_smtp_bindings;
                DROP TABLE smtp_probe_results;
                DROP TABLE smtp_connection_profiles;
                DROP TABLE capability_mail_gateway_bindings;
                DROP TABLE mail_gateway_probe_results;
                DROP TABLE mail_gateway_connection_profiles;
                DROP TABLE mail_outbox_operations;
                DROP TABLE capability_test_runs;
                DROP TABLE improvement_proposals;
                DROP TABLE feature_builder_sessions;
                DROP TABLE capability_connection_bindings;
                DROP TABLE connection_probe_results;
                DROP TABLE http_connection_profiles;
                DROP TABLE connection_profiles;
                DELETE FROM schema_migrations WHERE version IN (15, 16, 17, 18, 19, 20, 21);
                PRAGMA foreign_keys=ON;
                """);
        }

        using (var migrated = new ConversationStore(path)) await migrated.InitializeAsync();

        await using var connection = Open(path);
        await connection.OpenAsync();
        Assert.Equal(21L, await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(
            [
                "capability_connection_bindings",
                "connection_probe_results",
                "connection_profiles",
                "http_connection_profiles",
            ],
            await StringsAsync(
                connection,
                """
                SELECT name FROM sqlite_master
                WHERE type = 'table' AND name IN (
                    'connection_profiles',
                    'http_connection_profiles',
                    'connection_probe_results',
                    'capability_connection_bindings')
                ORDER BY name;
                """));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));

        foreach (var table in new[]
                 {
                     "connection_profiles",
                     "http_connection_profiles",
                     "connection_probe_results",
                     "capability_connection_bindings",
                 })
        {
            var columns = await StringsAsync(connection, $"SELECT name FROM pragma_table_info('{table}');");
            Assert.DoesNotContain(columns, IsSensitiveStorageColumn);
        }
    }

    [Fact]
    public async Task ExactDraftBindingRoundTripsAndProfileChangesInvalidateOnlyTheRequiredEvidence()
    {
        const string canary = "connection-repository-secret-canary";
        var path = Path.Combine(_directory, "connections.db");
        var credentials = new FakeCredentialStore();
        CapabilityVersionRef exact;
        ConnectionProfileId profileId;
        string initialBindingHash;
        using (var store = new ConversationStore(path))
        {
            await store.InitializeAsync();
            var draft = Draft();
            exact = draft.ExactVersion;
            Assert.Equal(
                CapabilityMutationOutcome.Succeeded,
                (await store.Capabilities.CreateValidatedDraftAsync(draft, Now, CancellationToken.None)).Outcome);
            var management = new ConnectionManagementService(
                store.Connections,
                credentials,
                new FixedTimeProvider(Now));
            profileId = ConnectionProfileId.Parse("repository-api");
            var created = Assert.IsType<HttpConnectionProfile>((await management.SaveHttpProfileAsync(
                new(
                    profileId,
                    "Repository API",
                    "https://repository.example.test/api",
                    ConnectionAuthenticationKind.Bearer,
                    true),
                0,
                ConnectionSecretMutation.Replace,
                canary,
                CancellationToken.None)).Profile);
            Assert.Equal(ConnectionManagementOutcome.Succeeded, (await management.RecordProbeAsync(
                profileId,
                succeeded: true,
                fixedErrorCode: null,
                CancellationToken.None)).Outcome);
            var bound = await management.BindAsync(
                exact,
                "primary",
                "http",
                profileId,
                expectedBindingRevision: 0,
                CancellationToken.None);
            initialBindingHash = Assert.IsType<CapabilityConnectionBinding>(bound.Binding).BindingHash;

            var ready = await new ConnectionReadinessResolver(store.Connections, credentials).ResolveAsync(
                exact,
                [new("primary", "http")],
                CancellationToken.None);
            Assert.True(ready.IsReady);

            var renamed = Assert.IsType<HttpConnectionProfile>((await management.SaveHttpProfileAsync(
                new(profileId, "Renamed only", created.BaseUrl, created.Authentication, true),
                created.Revision,
                ConnectionSecretMutation.Keep,
                null,
                CancellationToken.None)).Profile);
            Assert.NotNull(await store.Connections.GetProbeReceiptAsync(profileId, CancellationToken.None));
            Assert.True((await new ConnectionReadinessResolver(store.Connections, credentials).ResolveAsync(
                exact,
                [new("primary", "http")],
                CancellationToken.None)).IsReady);

            var moved = Assert.IsType<HttpConnectionProfile>((await management.SaveHttpProfileAsync(
                new(
                    profileId,
                    renamed.DisplayName,
                    "https://moved.example.test/",
                    renamed.Authentication,
                    true),
                renamed.Revision,
                ConnectionSecretMutation.Keep,
                null,
                CancellationToken.None)).Profile);
            Assert.Null(await store.Connections.GetProbeReceiptAsync(profileId, CancellationToken.None));
            var stale = await new ConnectionReadinessResolver(store.Connections, credentials).ResolveAsync(
                exact,
                [new("primary", "http")],
                CancellationToken.None);
            Assert.False(stale.IsReady);
            Assert.Equal("connection.binding.stale", Assert.Single(stale.Bindings).ErrorCode);

            var rebound = await management.BindAsync(
                exact,
                "primary",
                "http",
                profileId,
                expectedBindingRevision: 1,
                CancellationToken.None);
            Assert.Equal(2, rebound.Binding?.Revision);
            Assert.NotEqual(initialBindingHash, rebound.Binding?.BindingHash);
            Assert.Equal(moved.TargetRevision, rebound.Binding?.ProfileTargetRevision);
        }

        using (var reopened = new ConversationStore(path))
        {
            await reopened.InitializeAsync();
            var profile = await reopened.Connections.GetProfileAsync(profileId, CancellationToken.None);
            var binding = await reopened.Connections.GetBindingAsync(exact, "primary", CancellationToken.None);
            Assert.Equal("https://moved.example.test/", profile?.BaseUrl);
            Assert.Equal(2, binding?.Revision);
            Assert.Equal(21, await CurrentSchemaVersionAsync(path));
        }

        foreach (var file in new[] { path, path + "-wal", path + "-shm" }.Where(File.Exists))
            Assert.DoesNotContain(canary, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentProfileAndBindingSnapshotsHaveExactlyOneSqliteCasWinner()
    {
        var path = Path.Combine(_directory, "connection-cas.db");
        using var firstStore = new ConversationStore(path);
        await firstStore.InitializeAsync();
        var draft = Draft();
        Assert.Equal(
            CapabilityMutationOutcome.Succeeded,
            (await firstStore.Capabilities.CreateValidatedDraftAsync(
                draft,
                Now,
                CancellationToken.None)).Outcome);
        var management = new ConnectionManagementService(
            firstStore.Connections,
            new FakeCredentialStore(),
            new FixedTimeProvider(Now));
        var profileId = ConnectionProfileId.Parse("sqlite-cas-api");
        var initialProfile = Assert.IsType<HttpConnectionProfile>((await management.SaveHttpProfileAsync(
            new(
                profileId,
                "SQLite CAS",
                "https://sqlite-cas.example.test/",
                ConnectionAuthenticationKind.None,
                true),
            expectedRevision: 0,
            ConnectionSecretMutation.Keep,
            secret: null,
            CancellationToken.None)).Profile);
        var initialBinding = Assert.IsType<CapabilityConnectionBinding>((await management.BindAsync(
            draft.ExactVersion,
            "primary",
            "http",
            profileId,
            expectedBindingRevision: 0,
            CancellationToken.None)).Binding);

        using var competingStore = new ConversationStore(path);
        await competingStore.InitializeAsync();
        const string firstBaseUrl = "https://sqlite-cas-a.example.test/";
        const string secondBaseUrl = "https://sqlite-cas-b.example.test/";
        var firstCandidate = initialProfile with
        {
            DisplayName = "SQLite CAS A",
            BaseUrl = firstBaseUrl,
            Revision = initialProfile.Revision + 1,
            TargetRevision = initialProfile.TargetRevision + 1,
            TargetHash = HttpConnectionProfilePolicy.ComputeTargetHash(
                firstBaseUrl,
                initialProfile.Authentication),
            UpdatedAtUtc = Now.AddMinutes(1),
        };
        var secondCandidate = initialProfile with
        {
            DisplayName = "SQLite CAS B",
            BaseUrl = secondBaseUrl,
            Revision = initialProfile.Revision + 1,
            TargetRevision = initialProfile.TargetRevision + 1,
            TargetHash = HttpConnectionProfilePolicy.ComputeTargetHash(
                secondBaseUrl,
                initialProfile.Authentication),
            UpdatedAtUtc = Now.AddMinutes(1),
        };

        var profileResults = await Task.WhenAll(
            firstStore.Connections.TrySaveProfileAsync(
                firstCandidate,
                initialProfile.Revision,
                CancellationToken.None),
            competingStore.Connections.TrySaveProfileAsync(
                secondCandidate,
                initialProfile.Revision,
                CancellationToken.None));

        Assert.Equal(1, profileResults.Count(saved => saved));
        var persistedProfile = Assert.IsType<HttpConnectionProfile>(await firstStore.Connections.GetProfileAsync(
            profileId,
            CancellationToken.None));
        Assert.Equal(profileResults[0] ? firstCandidate : secondCandidate, persistedProfile);

        var firstBindingCandidate = initialBinding with
        {
            Revision = initialBinding.Revision + 1,
            ProfileTargetRevision = persistedProfile.TargetRevision,
            ProfileCredentialRevision = persistedProfile.CredentialRevision,
            ProfileTargetHash = persistedProfile.TargetHash,
            BindingHash = $"sha256:{new string('c', 64)}",
            UpdatedAtUtc = Now.AddMinutes(2),
        };
        var secondBindingCandidate = firstBindingCandidate with
        {
            BindingHash = $"sha256:{new string('d', 64)}",
        };

        var bindingResults = await Task.WhenAll(
            firstStore.Connections.TrySaveBindingAsync(
                firstBindingCandidate,
                initialBinding.Revision,
                CancellationToken.None),
            competingStore.Connections.TrySaveBindingAsync(
                secondBindingCandidate,
                initialBinding.Revision,
                CancellationToken.None));

        Assert.Equal(1, bindingResults.Count(saved => saved));
        var persistedBinding = Assert.IsType<CapabilityConnectionBinding>(await firstStore.Connections.GetBindingAsync(
            draft.ExactVersion,
            "primary",
            CancellationToken.None));
        Assert.Equal(bindingResults[0] ? firstBindingCandidate : secondBindingCandidate, persistedBinding);
    }

    [Fact]
    public async Task BindingToAnIdentityThatIsNotAnImmutableDraftFailsWithoutWritingARow()
    {
        var path = Path.Combine(_directory, "missing-draft.db");
        using var store = new ConversationStore(path);
        await store.InitializeAsync();
        var profileId = ConnectionProfileId.Parse("orphan-api");
        var baseUrl = "https://orphan.example.test/";
        var targetHash = HttpConnectionProfilePolicy.ComputeTargetHash(
            baseUrl,
            ConnectionAuthenticationKind.None);
        Assert.True(await store.Connections.TrySaveProfileAsync(
            new(
                profileId,
                "Orphan",
                baseUrl,
                ConnectionAuthenticationKind.None,
                true,
                ConnectionProfileLifecycleState.Stable,
                Revision: 1,
                TargetRevision: 1,
                CredentialRevision: 0,
                targetHash,
                FixedErrorCode: null,
                Now,
                Now),
            expectedRevision: 0,
            CancellationToken.None));
        var exact = new CapabilityVersionRef(
            CapabilityId.Parse("company.missing"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            CapabilityContentHash.Parse($"sha256:{new string('a', 64)}"));
        var bindingHash = $"sha256:{new string('b', 64)}";

        Assert.False(await store.Connections.TrySaveBindingAsync(
            new(
                exact,
                "primary",
                "http",
                profileId,
                Revision: 1,
                ProfileTargetRevision: 1,
                ProfileCredentialRevision: 0,
                targetHash,
                bindingHash,
                Now,
                Now),
            expectedRevision: 0,
            CancellationToken.None));
        Assert.Null(await store.Connections.GetBindingAsync(exact, "primary", CancellationToken.None));
    }

    private static ValidatedCapabilityDraft Draft()
    {
        var id = CapabilityId.Parse("company.repository");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        var manifest = new CapabilityManifest(
            1,
            id,
            version,
            "Repository",
            "Repository connection",
            CapabilityKind.Prompt,
            ["use repository"],
            [],
            [new CapabilityConnectionRequirement("primary", "http")],
            "R0",
            "instructions.md");
        var canonicalManifest = JsonSerializer.Serialize(new
        {
            description = "Repository connection",
            displayName = "Repository",
            entrypoint = "instructions.md",
            id = id.Value,
            intentExamples = new[] { "use repository" },
            kind = "prompt",
            maximumRisk = "R0",
            requiredConnections = new[] { new { kind = "http", slot = "primary" } },
            requiredTools = Array.Empty<string>(),
            schemaVersion = 1,
            version = version.Value,
        });
        var payload = new CapabilityVersionPayload(
            manifest,
            canonicalManifest,
            "Use the repository connection.",
            "{\"additionalProperties\":false,\"type\":\"object\"}");
        var hash = CapabilityContentHasher.Compute(payload);
        return new("draft-connection-repository", payload, hash, null, "local_import", hash);
    }

    private static bool IsSensitiveStorageColumn(string column) =>
        column.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        column.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        column.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        column.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        column.Contains("credential_value", StringComparison.OrdinalIgnoreCase) ||
        column.Contains("credential_blob", StringComparison.OrdinalIgnoreCase);

    private static SqliteConnection Open(string path) => new($"Data Source={path};Pooling=False");

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<IReadOnlyList<string>> StringsAsync(SqliteConnection connection, string sql)
    {
        var values = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }

    private static async Task<long> CurrentSchemaVersionAsync(string path)
    {
        await using var connection = Open(path);
        await connection.OpenAsync();
        return await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;");
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }

    private sealed class FakeCredentialStore : IConnectionCredentialStore
    {
        private readonly Dictionary<(ConnectionProfileId Id, ConnectionCredentialKind Kind), string> _values = [];

        public Task<bool> ExistsAsync(ConnectionProfileId profileId, ConnectionCredentialKind kind, CancellationToken cancellationToken) =>
            Task.FromResult(_values.ContainsKey((profileId, kind)));

        public Task ReplaceAsync(ConnectionProfileId profileId, ConnectionCredentialKind kind, string secret, CancellationToken cancellationToken)
        {
            _values[(profileId, kind)] = secret;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(ConnectionProfileId profileId, ConnectionCredentialKind kind, CancellationToken cancellationToken)
        {
            _values.Remove((profileId, kind));
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (Exception)
        {
        }
    }
}
