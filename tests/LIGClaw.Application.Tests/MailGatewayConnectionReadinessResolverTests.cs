using LIGClaw.Application.Connections;
using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class MailGatewayConnectionReadinessResolverTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(2);

    [Fact]
    public async Task ReadyMailBindingsUseTheSeparateCredentialKindAndCacheSharedEvidence()
    {
        var exact = Exact("company.mail-ready", 'a');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        AddReady(repository, credentials, exact, "zeta", "mail-profile");
        repository.Bindings[(exact, "alpha")] = repository.Bindings[(exact, "zeta")] with
        {
            Slot = "alpha",
            BindingHash = Hash('c'),
        };

        var resolution = await new MailGatewayConnectionReadinessResolver(repository, credentials)
            .ResolveAsync(
                exact,
                [
                    new("zeta", CapabilityConnectionKindPolicy.MailApi),
                    new("alpha", CapabilityConnectionKindPolicy.MailApi),
                ],
                CancellationToken.None);

        var profileId = ConnectionProfileId.Parse("mail-profile");
        Assert.True(resolution.IsReady);
        Assert.Equal(["alpha", "zeta"], resolution.Bindings.Select(binding => binding.Slot));
        Assert.Equal(ConnectionReadinessHasher.Compute(exact, resolution.Bindings), resolution.AggregateHash);
        Assert.Equal([profileId], repository.ProfileReads);
        Assert.Equal([profileId], repository.ProbeReads);
        Assert.Equal([(profileId, ConnectionCredentialKind.MailGatewayBearerToken)], credentials.ExistenceChecks);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ResolvedConnectionBinding>)resolution.Bindings).Add(resolution.Bindings[0]));
    }

    [Fact]
    public async Task HttpRequirementIsRejectedWithoutReadingMailState()
    {
        var exact = Exact("company.mail-kind", 'b');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();

        var resolution = await new MailGatewayConnectionReadinessResolver(repository, credentials)
            .ResolveAsync(
                exact,
                [new("service", CapabilityConnectionKindPolicy.Http)],
                CancellationToken.None);

        Assert.False(resolution.IsReady);
        Assert.Equal("connection.kind.unsupported", Assert.Single(resolution.Bindings).ErrorCode);
        Assert.Equal(0, repository.BindingReads);
        Assert.Empty(repository.ProfileReads);
        Assert.Empty(repository.ProbeReads);
        Assert.Empty(credentials.ExistenceChecks);
    }

    [Fact]
    public async Task HttpBearerCredentialCannotSatisfyMailGatewayCredential()
    {
        var exact = Exact("company.mail-secret", 'c');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        AddReady(repository, credentials, exact, "mail", "shared-id");
        var profileId = ConnectionProfileId.Parse("shared-id");
        credentials.Existing.Remove((profileId, ConnectionCredentialKind.MailGatewayBearerToken));
        credentials.Existing.Add((profileId, ConnectionCredentialKind.BearerToken));
        var resolver = new MailGatewayConnectionReadinessResolver(repository, credentials);

        var missing = await resolver.ResolveAsync(
            exact,
            [new("mail", CapabilityConnectionKindPolicy.MailApi)],
            CancellationToken.None);
        credentials.Existing.Add((profileId, ConnectionCredentialKind.MailGatewayBearerToken));
        var ready = await resolver.ResolveAsync(
            exact,
            [new("mail", CapabilityConnectionKindPolicy.MailApi)],
            CancellationToken.None);

        Assert.False(missing.IsReady);
        Assert.Equal("connection.secret.missing", Assert.Single(missing.Bindings).ErrorCode);
        Assert.True(ready.IsReady);
        Assert.All(
            credentials.ExistenceChecks,
            check => Assert.Equal(ConnectionCredentialKind.MailGatewayBearerToken, check.Kind));
    }

    [Theory]
    [InlineData("target", "connection.binding.stale")]
    [InlineData("sender", "connection.binding.stale")]
    [InlineData("credential", "connection.binding.stale")]
    [InlineData("probe", "connection.probe.required")]
    [InlineData("failed-probe", "mail.gateway.probe_failed")]
    [InlineData("disabled", "connection.profile.disabled")]
    [InlineData("updating", "connection.profile.unstable")]
    [InlineData("invalid", "connection.profile.invalid")]
    [InlineData("secret", "connection.secret.missing")]
    public async Task ChangedOrUnavailableEvidenceFailsClosed(string mutation, string expectedError)
    {
        var exact = Exact("company.mail-stale", 'd');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        AddReady(repository, credentials, exact, "mail", "mail-profile");
        var id = ConnectionProfileId.Parse("mail-profile");
        var profile = repository.Profiles[id];
        switch (mutation)
        {
            case "target":
                {
                    const string changedUrl = "https://changed.example.test/";
                    repository.Profiles[id] = profile with
                    {
                        BaseUrl = changedUrl,
                        TargetRevision = profile.TargetRevision + 1,
                        TargetHash = MailGatewayConnectionProfilePolicy.ComputeTargetHash(changedUrl, profile.Sender),
                        UpdatedAtUtc = Now.AddMinutes(1),
                    };
                    break;
                }
            case "sender":
                {
                    const string changedSender = "changed@example.test";
                    repository.Profiles[id] = profile with
                    {
                        Sender = changedSender,
                        TargetRevision = profile.TargetRevision + 1,
                        TargetHash = MailGatewayConnectionProfilePolicy.ComputeTargetHash(profile.BaseUrl, changedSender),
                        UpdatedAtUtc = Now.AddMinutes(1),
                    };
                    break;
                }
            case "credential":
                repository.Profiles[id] = profile with
                {
                    CredentialRevision = profile.CredentialRevision + 1,
                    UpdatedAtUtc = Now.AddMinutes(1),
                };
                break;
            case "probe":
                repository.Probes[id] = repository.Probes[id] with
                {
                    ProfileTargetRevision = profile.TargetRevision + 1,
                };
                break;
            case "failed-probe":
                repository.Probes[id] = repository.Probes[id] with
                {
                    Succeeded = false,
                    ErrorCode = "mail.gateway.probe_failed",
                };
                break;
            case "disabled":
                repository.Profiles[id] = profile with { Enabled = false };
                break;
            case "updating":
                repository.Profiles[id] = profile with { State = ConnectionProfileLifecycleState.Updating };
                break;
            case "invalid":
                repository.Profiles[id] = profile with { TargetHash = Hash('f') };
                break;
            case "secret":
                credentials.Existing.Clear();
                break;
        }

        var resolution = await new MailGatewayConnectionReadinessResolver(repository, credentials)
            .ResolveAsync(
                exact,
                [new("mail", CapabilityConnectionKindPolicy.MailApi)],
                CancellationToken.None);

        Assert.False(resolution.IsReady);
        Assert.Equal(expectedError, Assert.Single(resolution.Bindings).ErrorCode);
    }

    [Fact]
    public async Task CredentialStoreFailureReturnsOnlyAFixedError()
    {
        const string canary = "credential-provider-raw-canary";
        var exact = Exact("company.mail-unavailable", 'e');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials { Exception = new InvalidOperationException(canary) };
        AddReady(repository, credentials, exact, "mail", "mail-profile");

        var resolution = await new MailGatewayConnectionReadinessResolver(repository, credentials)
            .ResolveAsync(
                exact,
                [new("mail", CapabilityConnectionKindPolicy.MailApi)],
                CancellationToken.None);

        Assert.False(resolution.IsReady);
        Assert.Equal("connection.secret.unavailable", Assert.Single(resolution.Bindings).ErrorCode);
        Assert.DoesNotContain(canary, resolution.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructurallyInvalidRequirementsAreRejectedBeforeAnyIo()
    {
        var exact = Exact("company.mail-invalid-input", 'f');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var resolver = new MailGatewayConnectionReadinessResolver(repository, credentials);

        await Assert.ThrowsAsync<InvalidDataException>(() => resolver.ResolveAsync(
            exact,
            [
                new("duplicate", CapabilityConnectionKindPolicy.MailApi),
                new("duplicate", CapabilityConnectionKindPolicy.MailApi),
            ],
            CancellationToken.None));

        Assert.Equal(0, repository.BindingReads);
        Assert.Empty(credentials.ExistenceChecks);
    }

    [Fact]
    public void ConstructorRejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MailGatewayConnectionReadinessResolver(null!, new FakeCredentials()));
        Assert.Throws<ArgumentNullException>(() =>
            new MailGatewayConnectionReadinessResolver(new FakeRepository(), null!));
    }

    private static void AddReady(
        FakeRepository repository,
        FakeCredentials credentials,
        CapabilityVersionRef exact,
        string slot,
        string profileName)
    {
        var id = ConnectionProfileId.Parse(profileName);
        var baseUrl = $"https://{profileName}.example.test/";
        var sender = $"sender@{profileName}.example.test";
        var targetHash = MailGatewayConnectionProfilePolicy.ComputeTargetHash(baseUrl, sender);
        var profile = new MailGatewayConnectionProfile(
            id,
            profileName,
            baseUrl,
            sender,
            Enabled: true,
            ConnectionProfileLifecycleState.Stable,
            Revision: 3,
            TargetRevision: 2,
            CredentialRevision: 4,
            targetHash,
            FixedErrorCode: null,
            Now,
            Now);
        repository.Profiles[id] = profile;
        repository.Bindings[(exact, slot)] = new CapabilityConnectionBinding(
            exact,
            slot,
            CapabilityConnectionKindPolicy.MailApi,
            id,
            Revision: 5,
            profile.TargetRevision,
            profile.CredentialRevision,
            profile.TargetHash,
            Hash('b'),
            Now,
            Now);
        repository.Probes[id] = new ConnectionProbeReceipt(
            id,
            profile.TargetRevision,
            profile.CredentialRevision,
            profile.TargetHash,
            Succeeded: true,
            ErrorCode: null,
            Now);
        credentials.Existing.Add((id, ConnectionCredentialKind.MailGatewayBearerToken));
    }

    private static CapabilityVersionRef Exact(string id, char hash) => new(
        CapabilityId.Parse(id),
        CapabilitySemanticVersion.Parse("1.0.0"),
        CapabilityContentHash.Parse(Hash(hash)));

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    private sealed class FakeRepository : IMailGatewayConnectionRepository
    {
        public Dictionary<ConnectionProfileId, MailGatewayConnectionProfile> Profiles { get; } = [];
        public Dictionary<ConnectionProfileId, ConnectionProbeReceipt> Probes { get; } = [];
        public Dictionary<(CapabilityVersionRef Exact, string Slot), CapabilityConnectionBinding> Bindings { get; } = [];
        public List<ConnectionProfileId> ProfileReads { get; } = [];
        public List<ConnectionProfileId> ProbeReads { get; } = [];
        public int BindingReads { get; private set; }

        public Task<MailGatewayConnectionProfile?> GetProfileAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken)
        {
            ProfileReads.Add(id);
            return Task.FromResult(Profiles.GetValueOrDefault(id));
        }

        public Task<IReadOnlyList<MailGatewayConnectionProfile>> ListProfilesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MailGatewayConnectionProfile>>(Profiles.Values.ToArray());

        public Task<bool> TrySaveProfileAsync(
            MailGatewayConnectionProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ConnectionProbeReceipt?> GetProbeReceiptAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken)
        {
            ProbeReads.Add(id);
            return Task.FromResult(Probes.GetValueOrDefault(id));
        }

        public Task SaveProbeReceiptAsync(
            ConnectionProbeReceipt receipt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CapabilityConnectionBinding?> GetBindingAsync(
            CapabilityVersionRef exactVersion,
            string slot,
            CancellationToken cancellationToken)
        {
            BindingReads++;
            return Task.FromResult(Bindings.GetValueOrDefault((exactVersion, slot)));
        }

        public Task<IReadOnlyList<CapabilityConnectionBinding>> GetBindingsAsync(
            CapabilityVersionRef exactVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityConnectionBinding>>(Bindings.Values
                .Where(binding => binding.ExactVersion.Equals(exactVersion))
                .ToArray());

        public Task<bool> TrySaveBindingAsync(
            CapabilityConnectionBinding binding,
            long expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeCredentials : IConnectionCredentialStore
    {
        public HashSet<(ConnectionProfileId Id, ConnectionCredentialKind Kind)> Existing { get; } = [];
        public List<(ConnectionProfileId Id, ConnectionCredentialKind Kind)> ExistenceChecks { get; } = [];
        public Exception? Exception { get; init; }

        public Task<bool> ExistsAsync(
            ConnectionProfileId profileId,
            ConnectionCredentialKind kind,
            CancellationToken cancellationToken)
        {
            ExistenceChecks.Add((profileId, kind));
            if (Exception is not null) throw Exception;
            return Task.FromResult(Existing.Contains((profileId, kind)));
        }

        public Task ReplaceAsync(
            ConnectionProfileId profileId,
            ConnectionCredentialKind kind,
            string secret,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(
            ConnectionProfileId profileId,
            ConnectionCredentialKind kind,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
