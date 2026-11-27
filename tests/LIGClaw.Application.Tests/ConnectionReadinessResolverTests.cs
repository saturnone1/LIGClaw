using LIGClaw.Application.Connections;
using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class ConnectionReadinessResolverTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddHours(1);

    [Fact]
    public async Task EmptyRequirementsHaveAnExactVersionBoundDeterministicHash()
    {
        var exact = Exact("company.empty", 'a');
        var resolver = new ConnectionReadinessResolver(new FakeRepository(), new FakeCredentials());

        var first = await resolver.ResolveAsync(exact, [], CancellationToken.None);
        var second = await resolver.ResolveAsync(exact, [], CancellationToken.None);
        var other = await resolver.ResolveAsync(Exact("company.other", 'a'), [], CancellationToken.None);

        Assert.True(first.IsReady);
        Assert.Empty(first.Bindings);
        Assert.StartsWith("sha256:", first.AggregateHash, StringComparison.Ordinal);
        Assert.Equal(first.AggregateHash, second.AggregateHash);
        Assert.Equal(ConnectionReadinessHasher.Compute(exact, []), first.AggregateHash);
        Assert.NotEqual(first.AggregateHash, other.AggregateHash);
    }

    [Fact]
    public async Task ReadyBindingsAreOrdinallyResolvedAndAggregatedWithoutReadingASecret()
    {
        var exact = Exact("company.ready", 'b');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        AddReady(repository, credentials, exact, "zeta", "none-profile", ConnectionAuthenticationKind.None);
        AddReady(repository, credentials, exact, "alpha", "bearer-profile", ConnectionAuthenticationKind.Bearer);
        var resolver = new ConnectionReadinessResolver(repository, credentials);

        var first = await resolver.ResolveAsync(
            exact,
            [new("zeta", "http"), new("alpha", "http")],
            CancellationToken.None);
        var reordered = await resolver.ResolveAsync(
            exact,
            [new("alpha", "http"), new("zeta", "http")],
            CancellationToken.None);

        Assert.True(first.IsReady);
        Assert.Equal(["alpha", "zeta"], first.Bindings.Select(item => item.Slot));
        Assert.Equal(first.AggregateHash, reordered.AggregateHash);
        Assert.Equal(
            [ConnectionProfileId.Parse("bearer-profile"), ConnectionProfileId.Parse("bearer-profile")],
            credentials.ExistenceChecks);
    }

    [Fact]
    public async Task DisplayRenameDoesNotInvalidateAnOtherwiseCurrentBindingOrProbe()
    {
        var exact = Exact("company.rename", 'c');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        AddReady(repository, credentials, exact, "mail", "mail-profile", ConnectionAuthenticationKind.Bearer);
        var id = ConnectionProfileId.Parse("mail-profile");
        repository.Profiles[id] = repository.Profiles[id] with
        {
            DisplayName = "표시 이름만 변경",
            Revision = repository.Profiles[id].Revision + 1,
            UpdatedAtUtc = Now.AddMinutes(1),
        };

        var resolution = await new ConnectionReadinessResolver(repository, credentials).ResolveAsync(
            exact,
            [new("mail", "http")],
            CancellationToken.None);

        Assert.True(resolution.IsReady);
        Assert.Null(Assert.Single(resolution.Bindings).ErrorCode);
    }

    [Fact]
    public async Task MailApiRequirementRemainsNotReadyWithoutATypedMailProfileResolver()
    {
        var exact = Exact("company.mail-api", '7');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        AddReady(repository, credentials, exact, "mail", "http-profile", ConnectionAuthenticationKind.Bearer);
        repository.Bindings[(exact, "mail")] = repository.Bindings[(exact, "mail")] with
        {
            Kind = CapabilityConnectionKindPolicy.MailApi,
        };

        var resolution = await new ConnectionReadinessResolver(repository, credentials).ResolveAsync(
            exact,
            [new("mail", CapabilityConnectionKindPolicy.MailApi)],
            CancellationToken.None);

        Assert.False(resolution.IsReady);
        Assert.Equal("connection.kind.unsupported", Assert.Single(resolution.Bindings).ErrorCode);
        Assert.Empty(credentials.ExistenceChecks);
    }

    [Fact]
    public async Task MultipleSlotsBoundToOneProfileReadEachReadinessSourceOncePerResolution()
    {
        var exact = Exact("company.shared", '9');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        AddReady(repository, credentials, exact, "alpha", "shared-profile", ConnectionAuthenticationKind.Bearer);
        var profile = repository.Profiles[ConnectionProfileId.Parse("shared-profile")];
        var secondHash = $"sha256:{new string('c', 64)}";
        repository.Bindings[(exact, "beta")] = repository.Bindings[(exact, "alpha")] with
        {
            Slot = "beta",
            BindingHash = secondHash,
        };

        var resolution = await new ConnectionReadinessResolver(repository, credentials).ResolveAsync(
            exact,
            [new("beta", "http"), new("alpha", "http")],
            CancellationToken.None);

        Assert.True(resolution.IsReady);
        Assert.Equal([profile.Id], repository.ProfileReads);
        Assert.Equal([profile.Id], repository.ProbeReads);
        Assert.Equal([profile.Id], credentials.ExistenceChecks);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("credential")]
    [InlineData("probe")]
    [InlineData("secret")]
    [InlineData("state")]
    public async Task ChangedOrUnavailableReadinessEvidenceFailsClosed(string mutation)
    {
        var exact = Exact("company.stale", 'd');
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        AddReady(repository, credentials, exact, "mail", "mail-profile", ConnectionAuthenticationKind.Bearer);
        var id = ConnectionProfileId.Parse("mail-profile");
        var profile = repository.Profiles[id];
        switch (mutation)
        {
            case "target":
                repository.Profiles[id] = profile with { TargetRevision = profile.TargetRevision + 1 };
                break;
            case "credential":
                repository.Profiles[id] = profile with { CredentialRevision = profile.CredentialRevision + 1 };
                break;
            case "probe":
                repository.Probes[id] = repository.Probes[id] with
                {
                    ProfileTargetRevision = profile.TargetRevision + 1,
                };
                break;
            case "secret":
                credentials.Existing.Clear();
                break;
            case "state":
                repository.Profiles[id] = profile with
                {
                    State = ConnectionProfileLifecycleState.Updating,
                };
                break;
        }

        var resolution = await new ConnectionReadinessResolver(repository, credentials).ResolveAsync(
            exact,
            [new("mail", "http")],
            CancellationToken.None);

        Assert.False(resolution.IsReady);
        Assert.NotNull(Assert.Single(resolution.Bindings).ErrorCode);
    }

    [Fact]
    public async Task MoreThanEightConnectionRequirementsAreRejected()
    {
        var requirements = Enumerable.Range(0, 9)
            .Select(index => new CapabilityConnectionRequirement($"slot-{index}", "http"))
            .ToArray();
        var resolver = new ConnectionReadinessResolver(new FakeRepository(), new FakeCredentials());

        await Assert.ThrowsAsync<InvalidDataException>(() => resolver.ResolveAsync(
            Exact("company.limit", 'e'),
            requirements,
            CancellationToken.None));
    }

    private static void AddReady(
        FakeRepository repository,
        FakeCredentials credentials,
        CapabilityVersionRef exact,
        string slot,
        string profileName,
        ConnectionAuthenticationKind authentication)
    {
        var id = ConnectionProfileId.Parse(profileName);
        var baseUrl = $"https://{profileName}.example.test/";
        var targetHash = HttpConnectionProfilePolicy.ComputeTargetHash(baseUrl, authentication);
        var profile = new HttpConnectionProfile(
            id,
            profileName,
            baseUrl,
            authentication,
            Enabled: true,
            ConnectionProfileLifecycleState.Stable,
            Revision: 3,
            TargetRevision: 1,
            CredentialRevision: authentication == ConnectionAuthenticationKind.Bearer ? 1 : 0,
            targetHash,
            FixedErrorCode: null,
            Now,
            Now);
        repository.Profiles[id] = profile;
        var bindingHash = $"sha256:{new string('b', 64)}";
        repository.Bindings[(exact, slot)] = new CapabilityConnectionBinding(
            exact,
            slot,
            "http",
            id,
            Revision: 1,
            profile.TargetRevision,
            profile.CredentialRevision,
            targetHash,
            bindingHash,
            Now,
            Now);
        repository.Probes[id] = new ConnectionProbeReceipt(
            id,
            profile.TargetRevision,
            profile.CredentialRevision,
            targetHash,
            Succeeded: true,
            ErrorCode: null,
            Now);
        if (authentication == ConnectionAuthenticationKind.Bearer) credentials.Existing.Add(id);
    }

    private static CapabilityVersionRef Exact(string id, char hash) => new(
        CapabilityId.Parse(id),
        CapabilitySemanticVersion.Parse("1.0.0"),
        CapabilityContentHash.Parse($"sha256:{new string(hash, 64)}"));

    private sealed class FakeRepository : IConnectionProfileRepository
    {
        public Dictionary<ConnectionProfileId, HttpConnectionProfile> Profiles { get; } = [];
        public Dictionary<ConnectionProfileId, ConnectionProbeReceipt> Probes { get; } = [];
        public Dictionary<(CapabilityVersionRef Exact, string Slot), CapabilityConnectionBinding> Bindings { get; } = [];
        public List<ConnectionProfileId> ProfileReads { get; } = [];
        public List<ConnectionProfileId> ProbeReads { get; } = [];

        public Task<HttpConnectionProfile?> GetProfileAsync(ConnectionProfileId id, CancellationToken cancellationToken)
        {
            ProfileReads.Add(id);
            return Task.FromResult(Profiles.GetValueOrDefault(id));
        }

        public Task<IReadOnlyList<HttpConnectionProfile>> ListProfilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HttpConnectionProfile>>(Profiles.Values.ToArray());

        public Task<bool> TrySaveProfileAsync(HttpConnectionProfile profile, long expectedRevision, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ConnectionProbeReceipt?> GetProbeReceiptAsync(ConnectionProfileId id, CancellationToken cancellationToken)
        {
            ProbeReads.Add(id);
            return Task.FromResult(Probes.GetValueOrDefault(id));
        }

        public Task SaveProbeReceiptAsync(ConnectionProbeReceipt receipt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CapabilityConnectionBinding?> GetBindingAsync(CapabilityVersionRef exactVersion, string slot, CancellationToken cancellationToken) =>
            Task.FromResult(Bindings.GetValueOrDefault((exactVersion, slot)));

        public Task<IReadOnlyList<CapabilityConnectionBinding>> GetBindingsAsync(CapabilityVersionRef exactVersion, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityConnectionBinding>>(Bindings.Values
                .Where(item => item.ExactVersion.Equals(exactVersion)).ToArray());

        public Task<bool> TrySaveBindingAsync(CapabilityConnectionBinding binding, long expectedRevision, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCredentials : IConnectionCredentialStore
    {
        public HashSet<ConnectionProfileId> Existing { get; } = [];
        public List<ConnectionProfileId> ExistenceChecks { get; } = [];

        public Task<bool> ExistsAsync(ConnectionProfileId profileId, ConnectionCredentialKind kind, CancellationToken cancellationToken)
        {
            ExistenceChecks.Add(profileId);
            return Task.FromResult(Existing.Contains(profileId));
        }

        public Task ReplaceAsync(ConnectionProfileId profileId, ConnectionCredentialKind kind, string secret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(ConnectionProfileId profileId, ConnectionCredentialKind kind, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
