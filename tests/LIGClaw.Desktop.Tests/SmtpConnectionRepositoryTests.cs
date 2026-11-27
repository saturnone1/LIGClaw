using System.Text;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Tests;

public sealed class SmtpConnectionRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-02T06:00:00Z");
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public SmtpConnectionRepositoryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task CurrentSchemaUsesExactTypedMetadataColumnsIndexesAndGuards()
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
            ["capability_smtp_bindings", "smtp_connection_profiles", "smtp_probe_results"],
            await StringsAsync(
                connection,
                """
                SELECT name FROM sqlite_master
                WHERE type = 'table' AND name IN (
                    'smtp_connection_profiles', 'smtp_probe_results', 'capability_smtp_bindings')
                ORDER BY name;
                """));

        var expectedColumns = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["smtp_connection_profiles"] =
            [
                "id", "display_name", "host", "port", "tls_mode", "authentication",
                "certificate_policy", "sender", "enabled", "state", "revision",
                "target_revision", "credential_revision", "target_hash", "fixed_error_code",
                "created_at_utc", "updated_at_utc",
            ],
            ["smtp_probe_results"] =
            [
                "profile_id", "profile_target_revision", "profile_credential_revision",
                "profile_target_hash", "succeeded", "fixed_error_code", "checked_at_utc",
            ],
            ["capability_smtp_bindings"] =
            [
                "capability_id", "capability_version", "capability_content_hash", "slot", "kind",
                "profile_id", "revision", "profile_target_revision", "profile_credential_revision",
                "profile_target_hash", "binding_hash", "created_at_utc", "updated_at_utc",
            ],
        };
        var forbidden = new[]
        {
            "username", "password", "secret", "token", "recipient", "subject", "body", "payload",
            "ciphertext",
        };
        foreach (var (table, expected) in expectedColumns)
        {
            var actual = await ColumnNamesAsync(connection, table);
            Assert.Equal(expected, actual);
            Assert.DoesNotContain(
                actual,
                column => forbidden.Any(value =>
                    column.Contains(value, StringComparison.OrdinalIgnoreCase)));
        }

        Assert.Equal(
            ["ix_capability_smtp_bindings_profile", "ix_smtp_profiles_management"],
            await StringsAsync(
                connection,
                """
                SELECT name FROM sqlite_master
                WHERE type = 'index' AND name IN (
                    'ix_capability_smtp_bindings_profile', 'ix_smtp_profiles_management')
                ORDER BY name;
                """));
        Assert.Equal(
            [
                "smtp_binding_require_current_profile_insert",
                "smtp_binding_require_legal_update",
                "smtp_bindings_reject_delete",
                "smtp_probe_require_current_profile_insert",
                "smtp_probe_require_current_profile_update",
                "smtp_profiles_invalidate_probe",
                "smtp_profiles_reject_delete",
                "smtp_profiles_require_legal_update",
                "smtp_profiles_require_staged_insert",
            ],
            await StringsAsync(
                connection,
                """
                SELECT name FROM sqlite_master
                WHERE type = 'trigger' AND name LIKE 'smtp_%'
                ORDER BY name;
                """));
    }

    [Fact]
    public async Task ProfileProbeAndBindingUseExactCasAndRejectStaleEvidenceAcrossReopen()
    {
        var path = DatabasePath("cas.db");
        var id = ConnectionProfileId.Parse("smtp-cas");
        var draft = SmtpCapabilityDraft();
        SmtpConnectionProfile original;
        ConnectionProbeReceipt probe;
        CapabilityConnectionBinding binding;
        using (var store = new ConversationStore(path))
        {
            await store.InitializeAsync();
            Assert.Equal(
                CapabilityMutationOutcome.Succeeded,
                (await store.Capabilities.CreateValidatedDraftAsync(
                    draft,
                    Now,
                    CancellationToken.None)).Outcome);
            original = await SaveStableProfileAsync(store.SmtpConnections, id);
            probe = new(
                id,
                original.TargetRevision,
                original.CredentialRevision,
                original.TargetHash,
                true,
                null,
                Now.AddMinutes(3));
            await store.SmtpConnections.SaveProbeReceiptAsync(probe, CancellationToken.None);
            binding = new(
                draft.ExactVersion,
                "smtp",
                CapabilityConnectionKindPolicy.Smtp,
                id,
                1,
                original.TargetRevision,
                original.CredentialRevision,
                original.TargetHash,
                Hash('b'),
                Now.AddMinutes(4),
                Now.AddMinutes(4));
            Assert.True(await store.SmtpConnections.TrySaveBindingAsync(
                binding,
                0,
                CancellationToken.None));

            var staleProfile = original with
            {
                DisplayName = "Stale SMTP",
            };
            Assert.False(await store.SmtpConnections.TrySaveProfileAsync(
                staleProfile,
                2,
                CancellationToken.None));
        }

        CapabilityConnectionBinding rebound;
        using (var reopened = new ConversationStore(path))
        {
            await reopened.InitializeAsync();
            Assert.Equal(original, await reopened.SmtpConnections.GetProfileAsync(id, CancellationToken.None));
            Assert.Equal(probe, await reopened.SmtpConnections.GetProbeReceiptAsync(id, CancellationToken.None));
            Assert.Equal(binding, await reopened.SmtpConnections.GetBindingAsync(
                draft.ExactVersion,
                "smtp",
                CancellationToken.None));
            Assert.Equal(
                [binding],
                await reopened.SmtpConnections.GetBindingsAsync(
                    draft.ExactVersion,
                    CancellationToken.None));

            var updating = original with
            {
                State = ConnectionProfileLifecycleState.Updating,
                Revision = 4,
                CredentialRevision = 2,
                UpdatedAtUtc = Now.AddMinutes(5),
            };
            Assert.True(await reopened.SmtpConnections.TrySaveProfileAsync(
                updating,
                3,
                CancellationToken.None));
            Assert.Null(await reopened.SmtpConnections.GetProbeReceiptAsync(id, CancellationToken.None));
            var rotated = updating with
            {
                State = ConnectionProfileLifecycleState.Stable,
                Revision = 5,
                UpdatedAtUtc = Now.AddMinutes(6),
            };
            Assert.True(await reopened.SmtpConnections.TrySaveProfileAsync(
                rotated,
                4,
                CancellationToken.None));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reopened.SmtpConnections.SaveProbeReceiptAsync(probe, CancellationToken.None));
            var staleIdentity = binding with
            {
                Revision = 2,
                BindingHash = Hash('c'),
                UpdatedAtUtc = Now.AddMinutes(7),
            };
            Assert.False(await reopened.SmtpConnections.TrySaveBindingAsync(
                staleIdentity,
                1,
                CancellationToken.None));
            rebound = staleIdentity with
            {
                ProfileCredentialRevision = rotated.CredentialRevision,
                BindingHash = Hash('d'),
            };
            Assert.True(await reopened.SmtpConnections.TrySaveBindingAsync(
                rebound,
                1,
                CancellationToken.None));
            Assert.Equal(rebound, await reopened.SmtpConnections.GetBindingAsync(
                draft.ExactVersion,
                "smtp",
                CancellationToken.None));

            var staleCas = rebound with
            {
                BindingHash = Hash('e'),
                UpdatedAtUtc = Now.AddMinutes(8),
            };
            Assert.False(await reopened.SmtpConnections.TrySaveBindingAsync(
                staleCas,
                1,
                CancellationToken.None));
        }

        await using var connection = Open(path, SqliteOpenMode.ReadWrite);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "DELETE FROM capability_smtp_bindings;"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "DELETE FROM smtp_connection_profiles;"));
    }

    [Fact]
    public async Task RawSmtpCredentialsNeverEnterSqliteOrItsWal()
    {
        const string username = "RAW-SMTP-DB-USER-CANARY-42";
        const string password = "RAW-SMTP-DB-PASSWORD-CANARY-42";
        var path = DatabasePath("secret-free.db");
        var backupPath = DatabasePath("secret-free.backup.db");
        var credentials = new FakeCredentialStore();
        using (var store = new ConversationStore(path))
        {
            await store.InitializeAsync();
            var service = new SmtpConnectionManagementService(
                store.SmtpConnections,
                credentials,
                new FixedTimeProvider(Now));
            var result = await service.SaveSmtpProfileAsync(
                new(
                    ConnectionProfileId.Parse("smtp-secret-free"),
                    "SMTP Secret Free",
                    "smtp.example.test",
                    587,
                    SmtpTlsMode.StartTlsRequired,
                    "sender@example.test",
                    true),
                0,
                ConnectionSecretMutation.Replace,
                username,
                password,
                CancellationToken.None);

            Assert.Equal(ConnectionManagementOutcome.Succeeded, result.Outcome);
            Assert.Single(credentials.Values);
            await AssertRawCanariesAbsentAsync(path, username, password);
            await store.CreateBackupAsync(backupPath);
            await AssertRawCanariesAbsentAsync(backupPath, username, password);
        }

        await AssertRawCanariesAbsentAsync(path, username, password);
        await AssertRawCanariesAbsentAsync(backupPath, username, password);
    }

    private static async Task<SmtpConnectionProfile> SaveStableProfileAsync(
        ISmtpConnectionRepository repository,
        ConnectionProfileId id)
    {
        var hash = SmtpConnectionProfilePolicy.ComputeTargetHash(
            "smtp.example.test",
            587,
            SmtpTlsMode.StartTlsRequired,
            SmtpAuthenticationMechanism.Plain,
            SmtpCertificatePolicy.SystemTrust,
            "sender@example.test");
        var staged = new SmtpConnectionProfile(
            id,
            "SMTP CAS",
            "smtp.example.test",
            587,
            SmtpTlsMode.StartTlsRequired,
            SmtpAuthenticationMechanism.Plain,
            SmtpCertificatePolicy.SystemTrust,
            "sender@example.test",
            true,
            ConnectionProfileLifecycleState.Staged,
            1,
            1,
            1,
            hash,
            null,
            Now,
            Now);
        Assert.True(await repository.TrySaveProfileAsync(staged, 0, CancellationToken.None));
        var updating = staged with
        {
            State = ConnectionProfileLifecycleState.Updating,
            Revision = 2,
            UpdatedAtUtc = Now.AddMinutes(1),
        };
        Assert.True(await repository.TrySaveProfileAsync(updating, 1, CancellationToken.None));
        var stable = updating with
        {
            State = ConnectionProfileLifecycleState.Stable,
            Revision = 3,
            UpdatedAtUtc = Now.AddMinutes(2),
        };
        Assert.True(await repository.TrySaveProfileAsync(stable, 2, CancellationToken.None));
        return stable;
    }

    private static ValidatedCapabilityDraft SmtpCapabilityDraft()
    {
        var id = CapabilityId.Parse("company.smtp-capability");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        var manifest = new CapabilityManifest(
            1,
            id,
            version,
            "SMTP Capability",
            "Uses an explicitly selected SMTP connection",
            CapabilityKind.Prompt,
            ["send through smtp"],
            [],
            [new CapabilityConnectionRequirement("smtp", CapabilityConnectionKindPolicy.Smtp)],
            "R3",
            "instructions.md");
        var canonicalManifest = JsonSerializer.Serialize(new
        {
            description = "Uses an explicitly selected SMTP connection",
            displayName = "SMTP Capability",
            entrypoint = "instructions.md",
            id = id.Value,
            intentExamples = new[] { "send through smtp" },
            kind = "prompt",
            maximumRisk = "R3",
            requiredConnections = new[] { new { kind = "smtp", slot = "smtp" } },
            requiredTools = Array.Empty<string>(),
            schemaVersion = 1,
            version = version.Value,
        });
        var payload = new CapabilityVersionPayload(
            manifest,
            canonicalManifest,
            "Use the selected SMTP connection.",
            "{\"additionalProperties\":false,\"type\":\"object\"}");
        var contentHash = CapabilityContentHasher.Compute(payload);
        return new(
            "draft-smtp-repository",
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

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
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

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    private string DatabasePath(string fileName) => Path.Combine(_directory, fileName);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeCredentialStore : IConnectionCredentialStore
    {
        public Dictionary<(ConnectionProfileId Id, ConnectionCredentialKind Kind), string> Values { get; } = [];

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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
