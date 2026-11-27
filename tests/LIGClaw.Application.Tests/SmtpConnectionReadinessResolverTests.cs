using LIGClaw.Application.Connections;
using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class SmtpConnectionReadinessResolverTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(5);

    [Fact]
    public async Task ReadySmtpBindingsUseOnlySmtpAuthAndCacheSharedEvidence()
    {
        var exact = Exact("company.smtp-ready", 'a');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        AddReady(repository, credentials, exact, "zeta", "smtp-profile");
        repository.Bindings[(exact, "alpha")] = repository.Bindings[(exact, "zeta")] with
        {
            Slot = "alpha",
            BindingHash = Hash('c'),
        };

        var resolution = await new SmtpConnectionReadinessResolver(repository, credentials)
            .ResolveAsync(
                exact,
                [
                    new("zeta", CapabilityConnectionKindPolicy.Smtp),
                    new("alpha", CapabilityConnectionKindPolicy.Smtp),
                ],
                CancellationToken.None);

        var profileId = ConnectionProfileId.Parse("smtp-profile");
        Assert.True(resolution.IsReady);
        Assert.Equal(["alpha", "zeta"], resolution.Bindings.Select(binding => binding.Slot));
        Assert.Equal(ConnectionReadinessHasher.Compute(exact, resolution.Bindings), resolution.AggregateHash);
        Assert.Equal([profileId], repository.ProfileReads);
        Assert.Equal([profileId], repository.ProbeReads);
        Assert.Equal([(profileId, ConnectionCredentialKind.SmtpAuth)], credentials.ExistenceChecks);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ResolvedConnectionBinding>)resolution.Bindings).Add(resolution.Bindings[0]));
    }

    [Fact]
    public async Task NonSmtpRequirementIsRejectedWithoutReadingSmtpState()
    {
        var exact = Exact("company.smtp-kind", 'b');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();

        var resolution = await new SmtpConnectionReadinessResolver(repository, credentials)
            .ResolveAsync(
                exact,
                [new("service", CapabilityConnectionKindPolicy.MailApi)],
                CancellationToken.None);

        Assert.False(resolution.IsReady);
        Assert.Equal("connection.kind.unsupported", Assert.Single(resolution.Bindings).ErrorCode);
        Assert.Equal(0, repository.BindingReads);
        Assert.Empty(repository.ProfileReads);
        Assert.Empty(repository.ProbeReads);
        Assert.Empty(credentials.ExistenceChecks);
    }

    [Fact]
    public async Task HttpAndMailGatewayCredentialsCannotSatisfySmtpAuth()
    {
        var exact = Exact("company.smtp-secret", 'c');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        AddReady(repository, credentials, exact, "smtp", "shared-id");
        var profileId = ConnectionProfileId.Parse("shared-id");
        credentials.Existing.Clear();
        credentials.Existing.Add((profileId, ConnectionCredentialKind.BearerToken));
        credentials.Existing.Add((profileId, ConnectionCredentialKind.MailGatewayBearerToken));
        var resolver = new SmtpConnectionReadinessResolver(repository, credentials);

        var missing = await resolver.ResolveAsync(
            exact,
            [new("smtp", CapabilityConnectionKindPolicy.Smtp)],
            CancellationToken.None);
        credentials.Existing.Add((profileId, ConnectionCredentialKind.SmtpAuth));
        var ready = await resolver.ResolveAsync(
            exact,
            [new("smtp", CapabilityConnectionKindPolicy.Smtp)],
            CancellationToken.None);

        Assert.False(missing.IsReady);
        Assert.Equal("connection.secret.missing", Assert.Single(missing.Bindings).ErrorCode);
        Assert.True(ready.IsReady);
        Assert.All(credentials.ExistenceChecks, check => Assert.Equal(ConnectionCredentialKind.SmtpAuth, check.Kind));
    }

    [Theory]
    [InlineData("target", "connection.binding.stale")]
    [InlineData("credential", "connection.binding.stale")]
    [InlineData("probe", "connection.probe.required")]
    [InlineData("failed-probe", "smtp.probe_failed")]
    [InlineData("disabled", "connection.profile.disabled")]
    [InlineData("updating", "connection.profile.unstable")]
    [InlineData("invalid-profile", "connection.profile.invalid")]
    [InlineData("invalid-binding", "connection.binding.invalid")]
    [InlineData("secret", "connection.secret.missing")]
    public async Task StaleOrUnavailableProfileProbeCredentialAndBindingFailClosed(
        string mutation,
        string expectedError)
    {
        var exact = Exact("company.smtp-stale", 'd');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        AddReady(repository, credentials, exact, "smtp", "smtp-profile");
        var id = ConnectionProfileId.Parse("smtp-profile");
        var profile = repository.Profiles[id];
        switch (mutation)
        {
            case "target":
                repository.Profiles[id] = Rehash(profile with
                {
                    Host = "changed.example.test",
                    TargetRevision = profile.TargetRevision + 1,
                    UpdatedAtUtc = Now.AddMinutes(1),
                });
                break;
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
                    ProfileCredentialRevision = profile.CredentialRevision + 1,
                };
                break;
            case "failed-probe":
                repository.Probes[id] = repository.Probes[id] with
                {
                    Succeeded = false,
                    ErrorCode = "smtp.probe_failed",
                };
                break;
            case "disabled":
                repository.Profiles[id] = profile with { Enabled = false };
                break;
            case "updating":
                repository.Profiles[id] = profile with { State = ConnectionProfileLifecycleState.Updating };
                break;
            case "invalid-profile":
                repository.Profiles[id] = profile with { TargetHash = Hash('f') };
                break;
            case "invalid-binding":
                repository.Bindings[(exact, "smtp")] = repository.Bindings[(exact, "smtp")] with
                {
                    BindingHash = "not-a-hash",
                };
                break;
            case "secret":
                credentials.Existing.Clear();
                break;
        }

        var resolution = await new SmtpConnectionReadinessResolver(repository, credentials)
            .ResolveAsync(
                exact,
                [new("smtp", CapabilityConnectionKindPolicy.Smtp)],
                CancellationToken.None);

        Assert.False(resolution.IsReady);
        Assert.Equal(expectedError, Assert.Single(resolution.Bindings).ErrorCode);
    }

    [Fact]
    public async Task CredentialStoreFailureReturnsOnlyAFixedError()
    {
        const string canary = "smtp-credential-provider-raw-canary";
        var exact = Exact("company.smtp-unavailable", 'e');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials { Exception = new InvalidOperationException(canary) };
        AddReady(repository, credentials, exact, "smtp", "smtp-profile");

        var resolution = await new SmtpConnectionReadinessResolver(repository, credentials)
            .ResolveAsync(
                exact,
                [new("smtp", CapabilityConnectionKindPolicy.Smtp)],
                CancellationToken.None);

        Assert.False(resolution.IsReady);
        Assert.Equal("connection.secret.unavailable", Assert.Single(resolution.Bindings).ErrorCode);
        Assert.DoesNotContain(canary, resolution.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructurallyInvalidRequirementsAreRejectedBeforeAnyIo()
    {
        var exact = Exact("company.smtp-invalid-input", 'f');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var resolver = new SmtpConnectionReadinessResolver(repository, credentials);

        await Assert.ThrowsAsync<InvalidDataException>(() => resolver.ResolveAsync(
            exact,
            [
                new("duplicate", CapabilityConnectionKindPolicy.Smtp),
                new("duplicate", CapabilityConnectionKindPolicy.Smtp),
            ],
            CancellationToken.None));

        Assert.Equal(0, repository.BindingReads);
        Assert.Empty(credentials.ExistenceChecks);
    }

    [Fact]
    public void ConstructorRejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SmtpConnectionReadinessResolver(null!, new FakeCredentials()));
        Assert.Throws<ArgumentNullException>(() =>
            new SmtpConnectionReadinessResolver(new FakeRepository(), null!));
    }

    private static void AddReady(
        FakeRepository repository,
        FakeCredentials credentials,
        CapabilityVersionRef exact,
        string slot,
        string profileName)
    {
        var id = ConnectionProfileId.Parse(profileName);
        var profile = Rehash(new SmtpConnectionProfile(
            id,
            profileName,
            $"{profileName}.example.test",
            587,
            SmtpTlsMode.StartTlsRequired,
            SmtpAuthenticationMechanism.Plain,
            SmtpCertificatePolicy.SystemTrust,
            $"sender@{profileName}.example.test",
            Enabled: true,
            ConnectionProfileLifecycleState.Stable,
            Revision: 3,
            TargetRevision: 2,
            CredentialRevision: 4,
            Hash('0'),
            FixedErrorCode: null,
            Now,
            Now));
        repository.Profiles[id] = profile;
        repository.Bindings[(exact, slot)] = new CapabilityConnectionBinding(
            exact,
            slot,
            CapabilityConnectionKindPolicy.Smtp,
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
        credentials.Existing.Add((id, ConnectionCredentialKind.SmtpAuth));
    }

    private static SmtpConnectionProfile Rehash(SmtpConnectionProfile profile) => profile with
    {
        TargetHash = SmtpConnectionProfilePolicy.ComputeTargetHash(
            profile.Host,
            profile.Port,
            profile.TlsMode,
            profile.Authentication,
            profile.CertificatePolicy,
            profile.Sender),
    };

    private static CapabilityVersionRef Exact(string id, char hash) => new(
        CapabilityId.Parse(id),
        CapabilitySemanticVersion.Parse("1.0.0"),
        CapabilityContentHash.Parse(Hash(hash)));

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    private sealed class FakeRepository : ISmtpConnectionRepository
    {
        public Dictionary<ConnectionProfileId, SmtpConnectionProfile> Profiles { get; } = [];
        public Dictionary<ConnectionProfileId, ConnectionProbeReceipt> Probes { get; } = [];
        public Dictionary<(CapabilityVersionRef Exact, string Slot), CapabilityConnectionBinding> Bindings { get; } = [];
        public List<ConnectionProfileId> ProfileReads { get; } = [];
        public List<ConnectionProfileId> ProbeReads { get; } = [];
        public int BindingReads { get; private set; }

        public Task<SmtpConnectionProfile?> GetProfileAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken)
        {
            ProfileReads.Add(id);
            return Task.FromResult(Profiles.GetValueOrDefault(id));
        }

        public Task<IReadOnlyList<SmtpConnectionProfile>> ListProfilesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SmtpConnectionProfile>>(Profiles.Values.ToArray());

        public Task<bool> TrySaveProfileAsync(
            SmtpConnectionProfile profile,
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
