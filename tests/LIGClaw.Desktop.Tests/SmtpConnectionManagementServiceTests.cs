using System.Text.Json;
using LIGClaw.Application.Connections;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class SmtpConnectionManagementServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(8);

    [Fact]
    public async Task SaveNormalizesTypedProfileAndAtomicallyStoresOnlyEncodedSmtpAuth()
    {
        const string username = "RAW-SMTP-USER-CANARY";
        const string password = "RAW-SMTP-PASSWORD-CANARY";
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var service = Service(repository, credentials);
        var id = ConnectionProfileId.Parse("smtp-production");

        var result = await service.SaveSmtpProfileAsync(
            new(
                id,
                "SMTP Production",
                " SMTP.BÜCHER.Example. ",
                587,
                SmtpTlsMode.StartTlsRequired,
                "sender@example.test",
                true),
            expectedRevision: 0,
            ConnectionSecretMutation.Replace,
            username,
            password,
            CancellationToken.None);

        var profile = Assert.IsType<SmtpConnectionProfile>(result.Profile);
        Assert.Equal(ConnectionManagementOutcome.Succeeded, result.Outcome);
        Assert.Equal("smtp.xn--bcher-kva.example", profile.Host);
        Assert.Equal(SmtpAuthenticationMechanism.Plain, profile.Authentication);
        Assert.Equal(SmtpCertificatePolicy.SystemTrust, profile.CertificatePolicy);
        Assert.Equal(ConnectionProfileLifecycleState.Stable, profile.State);
        Assert.Equal(3, profile.Revision);
        Assert.Equal(1, profile.TargetRevision);
        Assert.Equal(1, profile.CredentialRevision);
        Assert.Equal(
            [
                ConnectionProfileLifecycleState.Staged,
                ConnectionProfileLifecycleState.Updating,
                ConnectionProfileLifecycleState.Stable,
            ],
            repository.ProfileWrites.Select(item => item.State));
        var encoded = credentials.Values[(id, ConnectionCredentialKind.SmtpAuth)];
        Assert.True(SmtpCredentialCodec.IsValid(encoded));
        Assert.DoesNotContain(username, encoded, StringComparison.Ordinal);
        Assert.DoesNotContain(password, encoded, StringComparison.Ordinal);
        Assert.DoesNotContain(username, JsonSerializer.Serialize(repository.ProfileWrites), StringComparison.Ordinal);
        Assert.DoesNotContain(password, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain(username, result.ToString(), StringComparison.Ordinal);
        Assert.Equal([ConnectionCredentialKind.SmtpAuth], credentials.ReplaceKinds);
    }

    [Fact]
    public async Task ProbeBindCredentialRotationAndRebindInvalidateOldEvidenceDeterministically()
    {
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var probe = new FakeProbe(new SmtpConnectionProbeResult(true));
        var service = Service(repository, credentials, probe);
        var id = ConnectionProfileId.Parse("smtp-rotation");
        var created = Assert.IsType<SmtpConnectionProfile>((await service.SaveSmtpProfileAsync(
            Draft(id),
            0,
            ConnectionSecretMutation.Replace,
            "smtp-user-one",
            "smtp-password-one",
            CancellationToken.None)).Profile);

        var probeResult = await service.ProbeSmtpProfileAsync(id, CancellationToken.None);
        var exact = Exact("company.smtp-rotation", 'a');
        var bound = Assert.IsType<CapabilityConnectionBinding>((await service.BindAsync(
            exact,
            "smtp",
            id,
            0,
            CancellationToken.None)).Binding);

        Assert.Equal(ConnectionManagementOutcome.Succeeded, probeResult.Outcome);
        Assert.True(probeResult.Receipt?.Succeeded);
        Assert.Equal(CapabilityConnectionKindPolicy.Smtp, bound.Kind);
        Assert.Equal(created.TargetRevision, bound.ProfileTargetRevision);
        Assert.Equal(created.CredentialRevision, bound.ProfileCredentialRevision);

        var rotatedResult = await service.SaveSmtpProfileAsync(
            Draft(id),
            created.Revision,
            ConnectionSecretMutation.Replace,
            "smtp-user-two",
            "smtp-password-two",
            CancellationToken.None);
        var rotated = Assert.IsType<SmtpConnectionProfile>(rotatedResult.Profile);

        Assert.Equal(created.TargetRevision, rotated.TargetRevision);
        Assert.Equal(created.CredentialRevision + 1, rotated.CredentialRevision);
        Assert.Null(repository.Probes.GetValueOrDefault(id));
        Assert.Equal(bound, repository.Bindings[(exact, "smtp")]);

        var rebound = Assert.IsType<CapabilityConnectionBinding>((await service.BindAsync(
            exact,
            "smtp",
            id,
            bound.Revision,
            CancellationToken.None)).Binding);
        Assert.Equal(bound.Revision + 1, rebound.Revision);
        Assert.Equal(rotated.CredentialRevision, rebound.ProfileCredentialRevision);
        Assert.NotEqual(bound.BindingHash, rebound.BindingHash);

        var stale = await service.SaveSmtpProfileAsync(
            Draft(id),
            created.Revision,
            ConnectionSecretMutation.Replace,
            "stale-user",
            "stale-password",
            CancellationToken.None);
        Assert.Equal(ConnectionManagementOutcome.Stale, stale.Outcome);
        Assert.Equal(2, credentials.ReplaceKinds.Count);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task ProbeRefusesToRecordReceiptWhenCredentialIdentityChangesDuringProbe()
    {
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var probe = new FakeProbe(new SmtpConnectionProbeResult(true));
        var service = Service(repository, credentials, probe);
        var id = ConnectionProfileId.Parse("smtp-probe-stale");
        var profile = Assert.IsType<SmtpConnectionProfile>((await service.SaveSmtpProfileAsync(
            Draft(id),
            0,
            ConnectionSecretMutation.Replace,
            "smtp-user",
            "smtp-password",
            CancellationToken.None)).Profile);
        probe.OnProbe = current => repository.Profiles[id] = current with
        {
            Revision = current.Revision + 1,
            CredentialRevision = current.CredentialRevision + 1,
            UpdatedAtUtc = current.UpdatedAtUtc.AddSeconds(1),
        };

        var result = await service.ProbeSmtpProfileAsync(id, CancellationToken.None);

        Assert.Equal(ConnectionManagementOutcome.Stale, result.Outcome);
        Assert.Equal("connection.probe.stale", result.ErrorCode);
        Assert.Null(repository.Probes.GetValueOrDefault(id));
        Assert.Equal(profile.CredentialRevision + 1, repository.Profiles[id].CredentialRevision);
    }

    [Fact]
    public async Task InvalidOrFailedCredentialsReturnFixedRawFreeDiagnostics()
    {
        const string usernameCanary = "RAW INVALID USERNAME CANARY";
        const string passwordCanary = "RAW-PASSWORD-WRITE-CANARY";
        var invalidRepository = new FakeRepository();
        var invalidCredentials = new FakeCredentials();
        var invalid = await Service(invalidRepository, invalidCredentials).SaveSmtpProfileAsync(
            Draft(ConnectionProfileId.Parse("smtp-invalid")),
            0,
            ConnectionSecretMutation.Replace,
            usernameCanary,
            passwordCanary,
            CancellationToken.None);

        var failedRepository = new FakeRepository();
        var failedCredentials = new FakeCredentials { FailWrites = true };
        var failed = await Service(failedRepository, failedCredentials).SaveSmtpProfileAsync(
            Draft(ConnectionProfileId.Parse("smtp-write-failure")),
            0,
            ConnectionSecretMutation.Replace,
            "valid-user",
            passwordCanary,
            CancellationToken.None);

        Assert.Equal(ConnectionManagementOutcome.Invalid, invalid.Outcome);
        Assert.Empty(invalidRepository.ProfileWrites);
        Assert.Equal(ConnectionManagementOutcome.CredentialFailure, failed.Outcome);
        Assert.Equal(ConnectionProfileLifecycleState.Error, failed.Profile?.State);
        Assert.Equal("connection.credential.write_failed", failed.ErrorCode);
        var diagnostics = invalid + failed.ToString() +
                          JsonSerializer.Serialize(failedRepository.ProfileWrites);
        Assert.DoesNotContain(usernameCanary, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(passwordCanary, diagnostics, StringComparison.Ordinal);
    }

    private static SmtpConnectionManagementService Service(
        FakeRepository repository,
        FakeCredentials credentials,
        FakeProbe? probe = null,
        ConnectionExecutionLeaseCoordinator? executionLeases = null) =>
        new(repository, credentials, new FixedTimeProvider(Now), probe, executionLeases);

    private static SmtpConnectionProfileDraft Draft(ConnectionProfileId id) => new(
        id,
        "SMTP Profile",
        "smtp.example.test",
        587,
        SmtpTlsMode.StartTlsRequired,
        "sender@example.test",
        true);

    private static CapabilityVersionRef Exact(string id, char hash) => new(
        CapabilityId.Parse(id),
        CapabilitySemanticVersion.Parse("1.0.0"),
        CapabilityContentHash.Parse(Hash(hash)));

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeProbe(SmtpConnectionProbeResult result) : ISmtpConnectionProbe
    {
        public int Calls { get; private set; }
        public Action<SmtpConnectionProfile>? OnProbe { get; set; }

        public Task<SmtpConnectionProbeResult> ProbeAsync(
            SmtpConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            Calls++;
            OnProbe?.Invoke(profile);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeCredentials : IConnectionCredentialStore
    {
        public Dictionary<(ConnectionProfileId Id, ConnectionCredentialKind Kind), string> Values { get; } = [];
        public List<ConnectionCredentialKind> ReplaceKinds { get; } = [];
        public bool FailWrites { get; set; }

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
            ReplaceKinds.Add(kind);
            if (FailWrites) throw new InvalidOperationException("raw fake credential write failure");
            if (kind != ConnectionCredentialKind.SmtpAuth || !SmtpCredentialCodec.IsValid(secret))
                throw new InvalidOperationException("invalid SMTP credential");
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

    private sealed class FakeRepository : ISmtpConnectionRepository
    {
        public Dictionary<ConnectionProfileId, SmtpConnectionProfile> Profiles { get; } = [];
        public Dictionary<ConnectionProfileId, ConnectionProbeReceipt> Probes { get; } = [];
        public Dictionary<(CapabilityVersionRef Version, string Slot), CapabilityConnectionBinding> Bindings { get; } = [];
        public List<SmtpConnectionProfile> ProfileWrites { get; } = [];

        public Task<SmtpConnectionProfile?> GetProfileAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) =>
            Task.FromResult<SmtpConnectionProfile?>(Profiles.GetValueOrDefault(id));

        public Task<IReadOnlyList<SmtpConnectionProfile>> ListProfilesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SmtpConnectionProfile>>(Profiles.Values.ToArray());

        public Task<bool> TrySaveProfileAsync(
            SmtpConnectionProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            var current = Profiles.GetValueOrDefault(profile.Id);
            if (current is null && expectedRevision != 0 ||
                current is not null && current.Revision != expectedRevision)
                return Task.FromResult(false);
            Profiles[profile.Id] = profile;
            ProfileWrites.Add(profile);
            if (current is not null && (
                    current.TargetRevision != profile.TargetRevision ||
                    current.CredentialRevision != profile.CredentialRevision ||
                    !StringComparer.Ordinal.Equals(current.TargetHash, profile.TargetHash)))
                Probes.Remove(profile.Id);
            return Task.FromResult(true);
        }

        public Task<ConnectionProbeReceipt?> GetProbeReceiptAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) =>
            Task.FromResult<ConnectionProbeReceipt?>(Probes.GetValueOrDefault(id));

        public Task SaveProbeReceiptAsync(
            ConnectionProbeReceipt receipt,
            CancellationToken cancellationToken)
        {
            var current = Profiles.GetValueOrDefault(receipt.ProfileId);
            if (current is null || current.TargetRevision != receipt.ProfileTargetRevision ||
                current.CredentialRevision != receipt.ProfileCredentialRevision ||
                !StringComparer.Ordinal.Equals(current.TargetHash, receipt.ProfileTargetHash))
                throw new InvalidOperationException("stale");
            Probes[receipt.ProfileId] = receipt;
            return Task.CompletedTask;
        }

        public Task<CapabilityConnectionBinding?> GetBindingAsync(
            CapabilityVersionRef exactVersion,
            string slot,
            CancellationToken cancellationToken) =>
            Task.FromResult<CapabilityConnectionBinding?>(Bindings.GetValueOrDefault((exactVersion, slot)));

        public Task<IReadOnlyList<CapabilityConnectionBinding>> GetBindingsAsync(
            CapabilityVersionRef exactVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityConnectionBinding>>(Bindings
                .Where(item => item.Key.Version.Equals(exactVersion))
                .Select(item => item.Value)
                .ToArray());

        public Task<bool> TrySaveBindingAsync(
            CapabilityConnectionBinding binding,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            var key = (binding.ExactVersion, binding.Slot);
            var current = Bindings.GetValueOrDefault(key);
            if (current is null && expectedRevision != 0 ||
                current is not null && current.Revision != expectedRevision)
                return Task.FromResult(false);
            Bindings[key] = binding;
            return Task.FromResult(true);
        }
    }
}
