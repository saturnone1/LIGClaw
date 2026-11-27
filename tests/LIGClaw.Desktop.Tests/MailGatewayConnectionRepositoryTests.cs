using System.Text;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Tests;

public sealed class MailGatewayConnectionRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-02T05:00:00Z");
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public MailGatewayConnectionRepositoryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task CurrentSchemaUsesExactStandaloneMetadataColumnsIndexesAndTriggers()
    {
        var path = DatabasePath("schema.db");
        using (var store = new ConversationStore(path))
            await store.InitializeAsync();

        await using var connection = Open(path, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync();
        Assert.Equal(21, await ScalarAsync(
            connection,
            "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(
            [
                "capability_mail_gateway_bindings",
                "mail_gateway_connection_profiles",
                "mail_gateway_probe_results",
            ],
            await StringsAsync(
                connection,
                """
                SELECT name FROM sqlite_master
                WHERE type = 'table' AND (
                    name LIKE 'mail_gateway_%' OR name = 'capability_mail_gateway_bindings')
                ORDER BY name;
                """));

        var expectedColumns = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["mail_gateway_connection_profiles"] =
            [
                "id", "display_name", "base_url", "sender", "enabled", "state", "revision",
                "target_revision", "credential_revision", "target_hash", "fixed_error_code",
                "created_at_utc", "updated_at_utc",
            ],
            ["mail_gateway_probe_results"] =
            [
                "profile_id", "profile_target_revision", "profile_credential_revision",
                "profile_target_hash", "succeeded", "fixed_error_code", "checked_at_utc",
            ],
            ["capability_mail_gateway_bindings"] =
            [
                "capability_id", "capability_version", "capability_content_hash", "slot", "kind",
                "profile_id", "revision", "profile_target_revision", "profile_credential_revision",
                "profile_target_hash", "binding_hash", "created_at_utc", "updated_at_utc",
            ],
        };
        var forbidden = new[]
        {
            "token", "recipient", "subject", "body", "payload", "ciphertext", "authorization",
        };
        foreach (var (table, expected) in expectedColumns)
        {
            var actual = await ColumnNamesAsync(connection, table);
            Assert.Equal(expected, actual);
            Assert.DoesNotContain(
                actual,
                column => forbidden.Any(value =>
                    column.Contains(value, StringComparison.OrdinalIgnoreCase)));
            if (table == "mail_gateway_connection_profiles")
                Assert.Contains("sender", actual);
            else
                Assert.DoesNotContain("sender", actual);
        }

        Assert.Equal(
            [
                "ix_capability_mail_gateway_bindings_profile",
                "ix_mail_gateway_profiles_management",
            ],
            await StringsAsync(
                connection,
                """
                SELECT name FROM sqlite_master
                WHERE type = 'index' AND name IN (
                    'ix_capability_mail_gateway_bindings_profile',
                    'ix_mail_gateway_profiles_management')
                ORDER BY name;
                """));
        Assert.Equal(
            [
                "mail_gateway_binding_require_current_profile_insert",
                "mail_gateway_binding_require_legal_update",
                "mail_gateway_probe_require_current_profile_insert",
                "mail_gateway_probe_require_current_profile_update",
                "mail_gateway_profiles_invalidate_probe",
                "mail_gateway_profiles_require_legal_update",
                "mail_gateway_profiles_require_staged_insert",
            ],
            await StringsAsync(
                connection,
                """
                SELECT name FROM sqlite_master
                WHERE type = 'trigger' AND name LIKE 'mail_gateway_%'
                ORDER BY name;
                """));
    }

    [Fact]
    public async Task HttpAndMailProfilesUseSeparateNamespacesAndTokensNeverEnterSqlite()
    {
        var path = DatabasePath("namespace.db");
        const string httpToken = "HttpTokenCanary9f8a";
        const string mailToken = "MailBearerTokenCanary9f8a";
        var id = ConnectionProfileId.Parse("shared-production");
        var credentials = new FakeCredentialStore();
        using (var store = new ConversationStore(path))
        {
            await store.InitializeAsync();
            var http = new ConnectionManagementService(
                store.Connections,
                credentials,
                new FixedTimeProvider(Now));
            var mail = new MailGatewayConnectionManagementService(
                store.MailGatewayConnections,
                credentials,
                new FixedTimeProvider(Now));

            Assert.Equal(
                ConnectionManagementOutcome.Succeeded,
                (await http.SaveHttpProfileAsync(
                    new(id, "Shared HTTP", "https://http.example.test/api", ConnectionAuthenticationKind.Bearer, true),
                    0,
                    ConnectionSecretMutation.Replace,
                    httpToken,
                    CancellationToken.None)).Outcome);
            var mailResult = await mail.SaveMailGatewayProfileAsync(
                new(id, "Shared Mail", "https://mail.example.test/", "sender@example.test", true),
                0,
                ConnectionSecretMutation.Replace,
                mailToken,
                CancellationToken.None);

            Assert.Equal(ConnectionManagementOutcome.Succeeded, mailResult.Outcome);
            Assert.Equal("https://http.example.test/api/", (
                await store.Connections.GetProfileAsync(id, CancellationToken.None))?.BaseUrl);
            Assert.Equal("sender@example.test", (
                await store.MailGatewayConnections.GetProfileAsync(id, CancellationToken.None))?.Sender);
            Assert.Equal(httpToken, credentials.Values[(id, ConnectionCredentialKind.BearerToken)]);
            Assert.Equal(mailToken, credentials.Values[(id, ConnectionCredentialKind.MailGatewayBearerToken)]);
            Assert.NotEqual(
                WindowsConnectionCredentialStore.Target(id, ConnectionCredentialKind.BearerToken),
                WindowsConnectionCredentialStore.Target(id, ConnectionCredentialKind.MailGatewayBearerToken));
            Assert.Equal(
                "LIGClaw/MailGatewayConnections/shared-production/BearerToken",
                WindowsConnectionCredentialStore.Target(
                    id,
                    ConnectionCredentialKind.MailGatewayBearerToken));
            Assert.True(File.Exists($"{path}-wal"));
            Assert.True(File.Exists($"{path}-shm"));
            await AssertRawCanariesAbsentAsync(path, httpToken, mailToken);
        }

        await AssertRawCanariesAbsentAsync(path, httpToken, mailToken);
    }

    [Fact]
    public async Task EndpointSenderAndCredentialRotationInvalidateProbeAndExactBinding()
    {
        var path = DatabasePath("invalidation.db");
        var credentials = new FakeCredentialStore();
        var probe = new FakeProbe(new MailGatewayProbeResult(true));
        var id = ConnectionProfileId.Parse("mail-production");
        using var store = new ConversationStore(path);
        await store.InitializeAsync();
        var draft = MailCapabilityDraft();
        Assert.Equal(
            CapabilityMutationOutcome.Succeeded,
            (await store.Capabilities.CreateValidatedDraftAsync(
                draft,
                Now,
                CancellationToken.None)).Outcome);
        var service = new MailGatewayConnectionManagementService(
            store.MailGatewayConnections,
            credentials,
            new FixedTimeProvider(Now),
            probe);
        var created = Assert.IsType<MailGatewayConnectionProfile>((await service.SaveMailGatewayProfileAsync(
            new(id, "Production Mail", "https://gateway.example.test/", "sender@example.test", true),
            0,
            ConnectionSecretMutation.Replace,
            "MailTokenOriginal9f8a",
            CancellationToken.None)).Profile);
        Assert.Equal(ConnectionProfileLifecycleState.Stable, created.State);
        Assert.Equal(3, created.Revision);
        Assert.Equal(1, created.TargetRevision);
        Assert.Equal(1, created.CredentialRevision);

        var binding = await ProbeBindAndResolveAsync(service, store, credentials, draft.ExactVersion, id, 0);
        var endpointChanged = Assert.IsType<MailGatewayConnectionProfile>((await service.SaveMailGatewayProfileAsync(
            new(id, "Production Mail", "https://moved.example.test/", created.Sender, true),
            created.Revision,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None)).Profile);
        Assert.Equal(created.TargetRevision + 1, endpointChanged.TargetRevision);
        Assert.Equal(created.CredentialRevision, endpointChanged.CredentialRevision);
        Assert.NotEqual(created.TargetHash, endpointChanged.TargetHash);
        await AssertInvalidatedAsync(store, credentials, draft.ExactVersion, endpointChanged, binding);

        var rebound = await ProbeBindAndResolveAsync(
            service,
            store,
            credentials,
            draft.ExactVersion,
            id,
            binding.Revision);
        Assert.NotEqual(binding.BindingHash, rebound.BindingHash);
        binding = rebound;
        var senderChanged = Assert.IsType<MailGatewayConnectionProfile>((await service.SaveMailGatewayProfileAsync(
            new(id, "Production Mail", endpointChanged.BaseUrl, "other-sender@example.test", true),
            endpointChanged.Revision,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None)).Profile);
        Assert.Equal(endpointChanged.TargetRevision + 1, senderChanged.TargetRevision);
        Assert.Equal(endpointChanged.CredentialRevision, senderChanged.CredentialRevision);
        Assert.NotEqual(endpointChanged.TargetHash, senderChanged.TargetHash);
        await AssertInvalidatedAsync(store, credentials, draft.ExactVersion, senderChanged, binding);

        rebound = await ProbeBindAndResolveAsync(
            service,
            store,
            credentials,
            draft.ExactVersion,
            id,
            binding.Revision);
        Assert.NotEqual(binding.BindingHash, rebound.BindingHash);
        binding = rebound;
        var rotated = Assert.IsType<MailGatewayConnectionProfile>((await service.SaveMailGatewayProfileAsync(
            new(id, "Production Mail", senderChanged.BaseUrl, senderChanged.Sender, true),
            senderChanged.Revision,
            ConnectionSecretMutation.Replace,
            "MailTokenRotated9f8a",
            CancellationToken.None)).Profile);
        Assert.Equal(senderChanged.TargetRevision, rotated.TargetRevision);
        Assert.Equal(senderChanged.TargetHash, rotated.TargetHash);
        Assert.Equal(senderChanged.CredentialRevision + 1, rotated.CredentialRevision);
        await AssertInvalidatedAsync(store, credentials, draft.ExactVersion, rotated, binding);
    }

    [Fact]
    public async Task ManagementRejectsInvalidBearerAndKeepsDiagnosticsRedacted()
    {
        var path = DatabasePath("invalid-token.db");
        using var store = new ConversationStore(path);
        await store.InitializeAsync();
        var credentials = new FakeCredentialStore();
        var service = new MailGatewayConnectionManagementService(
            store.MailGatewayConnections,
            credentials,
            new FixedTimeProvider(Now));
        var draft = new MailGatewayConnectionProfileDraft(
            ConnectionProfileId.Parse("redacted-mail"),
            "Redacted Mail",
            "https://raw-endpoint-marker.example.test/",
            "raw-sender-marker@example.test",
            true);

        var result = await service.SaveMailGatewayProfileAsync(
            draft,
            0,
            ConnectionSecretMutation.Replace,
            "contains spaces",
            CancellationToken.None);

        Assert.Equal(ConnectionManagementOutcome.Invalid, result.Outcome);
        Assert.Empty(credentials.Values);
        Assert.DoesNotContain("raw-endpoint-marker", draft.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("raw-sender-marker", draft.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("raw", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CredentialWriteFailureEndsInFixedErrorAndExplicitReplaceCanRecover()
    {
        var path = DatabasePath("credential-failure.db");
        using var store = new ConversationStore(path);
        await store.InitializeAsync();
        var id = ConnectionProfileId.Parse("recoverable-mail");
        var credentials = new FakeCredentialStore { ThrowOnReplace = true };
        var service = new MailGatewayConnectionManagementService(
            store.MailGatewayConnections,
            credentials,
            new FixedTimeProvider(Now));
        var draft = new MailGatewayConnectionProfileDraft(
            id,
            "Recoverable Mail",
            "https://recoverable.example.test/",
            "sender@example.test",
            true);

        var failed = await service.SaveMailGatewayProfileAsync(
            draft,
            0,
            ConnectionSecretMutation.Replace,
            "MailTokenFailure9f8a",
            CancellationToken.None);
        var errored = Assert.IsType<MailGatewayConnectionProfile>(failed.Profile);

        Assert.Equal(ConnectionManagementOutcome.CredentialFailure, failed.Outcome);
        Assert.Equal(ConnectionProfileLifecycleState.Error, errored.State);
        Assert.Equal("connection.credential.write_failed", errored.FixedErrorCode);
        Assert.Equal(3, errored.Revision);
        Assert.Empty(credentials.Values);

        credentials.ThrowOnReplace = false;
        var recovered = Assert.IsType<MailGatewayConnectionProfile>((await service.SaveMailGatewayProfileAsync(
            draft,
            errored.Revision,
            ConnectionSecretMutation.Replace,
            "MailTokenRecovered9f8a",
            CancellationToken.None)).Profile);
        Assert.Equal(ConnectionProfileLifecycleState.Stable, recovered.State);
        Assert.Null(recovered.FixedErrorCode);
        Assert.Equal(errored.Revision + 2, recovered.Revision);
        Assert.Equal(errored.CredentialRevision + 1, recovered.CredentialRevision);
    }

    [Fact]
    public async Task ManagementMutationWaitsForTheSharedExecutionLease()
    {
        var path = DatabasePath("shared-lease.db");
        using var store = new ConversationStore(path);
        await store.InitializeAsync();
        var credentials = new FakeCredentialStore();
        var coordinator = new ConnectionExecutionLeaseCoordinator();
        var service = new MailGatewayConnectionManagementService(
            store.MailGatewayConnections,
            credentials,
            new FixedTimeProvider(Now),
            executionLeaseCoordinator: coordinator);
        var draft = new MailGatewayConnectionProfileDraft(
            ConnectionProfileId.Parse("leased-mail"),
            "Leased Mail",
            "https://leased.example.test/",
            "sender@example.test",
            true);
        var created = Assert.IsType<MailGatewayConnectionProfile>((await service.SaveMailGatewayProfileAsync(
            draft,
            0,
            ConnectionSecretMutation.Replace,
            "MailTokenLeased9f8a",
            CancellationToken.None)).Profile);

        var executionLease = await coordinator.AcquireAsync(CancellationToken.None);
        var mutation = service.SaveMailGatewayProfileAsync(
            draft with { DisplayName = "Renamed Leased Mail" },
            created.Revision,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None);
        await Task.Yield();
        Assert.False(mutation.IsCompleted);

        await executionLease.DisposeAsync();
        Assert.Equal(ConnectionManagementOutcome.Succeeded, (await mutation).Outcome);
    }

    private static async Task<CapabilityConnectionBinding> ProbeBindAndResolveAsync(
        MailGatewayConnectionManagementService service,
        ConversationStore store,
        FakeCredentialStore credentials,
        CapabilityVersionRef exactVersion,
        ConnectionProfileId profileId,
        long expectedBindingRevision)
    {
        Assert.Equal(
            ConnectionManagementOutcome.Succeeded,
            (await service.ProbeMailGatewayProfileAsync(profileId, CancellationToken.None)).Outcome);
        var bound = await service.BindAsync(
            exactVersion,
            "mail",
            profileId,
            expectedBindingRevision,
            CancellationToken.None);
        var binding = Assert.IsType<CapabilityConnectionBinding>(bound.Binding);
        var readiness = await new MailGatewayConnectionReadinessResolver(
            store.MailGatewayConnections,
            credentials).ResolveAsync(
            exactVersion,
            [new("mail", CapabilityConnectionKindPolicy.MailApi)],
            CancellationToken.None);
        Assert.True(readiness.IsReady);
        return binding;
    }

    private static async Task AssertInvalidatedAsync(
        ConversationStore store,
        FakeCredentialStore credentials,
        CapabilityVersionRef exactVersion,
        MailGatewayConnectionProfile profile,
        CapabilityConnectionBinding oldBinding)
    {
        Assert.Null(await store.MailGatewayConnections.GetProbeReceiptAsync(
            profile.Id,
            CancellationToken.None));
        var persistedBinding = Assert.IsType<CapabilityConnectionBinding>(
            await store.MailGatewayConnections.GetBindingAsync(
                exactVersion,
                "mail",
                CancellationToken.None));
        Assert.Equal(oldBinding.BindingHash, persistedBinding.BindingHash);
        Assert.True(
            profile.TargetRevision != persistedBinding.ProfileTargetRevision ||
            profile.CredentialRevision != persistedBinding.ProfileCredentialRevision ||
            !StringComparer.Ordinal.Equals(profile.TargetHash, persistedBinding.ProfileTargetHash));

        var readiness = await new MailGatewayConnectionReadinessResolver(
            store.MailGatewayConnections,
            credentials).ResolveAsync(
            exactVersion,
            [new("mail", CapabilityConnectionKindPolicy.MailApi)],
            CancellationToken.None);
        Assert.False(readiness.IsReady);
        Assert.Equal("connection.binding.stale", Assert.Single(readiness.Bindings).ErrorCode);
    }

    private static ValidatedCapabilityDraft MailCapabilityDraft()
    {
        var id = CapabilityId.Parse("company.mail-capability");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        var manifest = new CapabilityManifest(
            1,
            id,
            version,
            "Mail Capability",
            "Uses the fixed Mail Gateway",
            CapabilityKind.Prompt,
            ["send mail"],
            [],
            [new CapabilityConnectionRequirement("mail", CapabilityConnectionKindPolicy.MailApi)],
            "R3",
            "instructions.md");
        var canonicalManifest = JsonSerializer.Serialize(new
        {
            description = "Uses the fixed Mail Gateway",
            displayName = "Mail Capability",
            entrypoint = "instructions.md",
            id = id.Value,
            intentExamples = new[] { "send mail" },
            kind = "prompt",
            maximumRisk = "R3",
            requiredConnections = new[] { new { kind = "mail-api", slot = "mail" } },
            requiredTools = Array.Empty<string>(),
            schemaVersion = 1,
            version = version.Value,
        });
        var payload = new CapabilityVersionPayload(
            manifest,
            canonicalManifest,
            "Use the fixed Mail Gateway.",
            "{\"additionalProperties\":false,\"type\":\"object\"}");
        var contentHash = CapabilityContentHasher.Compute(payload);
        return new(
            "draft-mail-gateway-repository",
            payload,
            contentHash,
            null,
            "local_import",
            contentHash);
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());

    private static async Task<IReadOnlyList<string>> ColumnNamesAsync(
        SqliteConnection connection,
        string table)
    {
        var values = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(1));
        return values;
    }

    private static async Task<IReadOnlyList<string>> StringsAsync(
        SqliteConnection connection,
        string sql)
    {
        var values = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task AssertRawCanariesAbsentAsync(string path, params string[] canaries)
    {
        foreach (var file in new[] { path, $"{path}-wal", $"{path}-shm" }.Where(File.Exists))
        {
            await using var input = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var copy = new MemoryStream();
            await input.CopyToAsync(copy);
            var persisted = Encoding.UTF8.GetString(copy.ToArray());
            foreach (var canary in canaries)
                Assert.DoesNotContain(canary, persisted, StringComparison.Ordinal);
        }
    }

    private string DatabasePath(string fileName) => Path.Combine(_directory, fileName);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeCredentialStore : IConnectionCredentialStore
    {
        public Dictionary<(ConnectionProfileId Id, ConnectionCredentialKind Kind), string> Values { get; } = [];
        public bool ThrowOnReplace { get; set; }

        public Task<bool> ExistsAsync(
            ConnectionProfileId profileId,
            ConnectionCredentialKind kind,
            CancellationToken cancellationToken) =>
            Task.FromResult(Values.ContainsKey((profileId, kind)));

        public Task ReplaceAsync(
            ConnectionProfileId profileId,
            ConnectionCredentialKind kind,
            string secret,
            CancellationToken cancellationToken)
        {
            if (ThrowOnReplace) throw new InvalidOperationException("credential write failed");
            Values[(profileId, kind)] = secret;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            ConnectionProfileId profileId,
            ConnectionCredentialKind kind,
            CancellationToken cancellationToken)
        {
            Values.Remove((profileId, kind));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProbe(MailGatewayProbeResult result) : IMailGatewayProbe
    {
        public List<MailGatewayConnectionProfile> Profiles { get; } = [];

        public Task<MailGatewayProbeResult> ProbeAsync(
            MailGatewayConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            Profiles.Add(profile);
            return Task.FromResult(result);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
