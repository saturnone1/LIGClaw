using LIGClaw.Application.Connections;
using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class CompositeConnectionReadinessResolverTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(3);

    [Theory]
    [InlineData("ready")]
    [InlineData("missing")]
    [InlineData("empty")]
    public async Task SingleHttpResolutionIsByteForByteCompatibleWithLegacyResolver(string scenario)
    {
        var exact = Exact("company.http-compat", 'a');
        var repository = new HttpRepository();
        if (scenario == "ready") AddReadyHttp(repository, exact, "service");
        var legacy = new ConnectionReadinessResolver(repository, new EmptyCredentials());
        var mail = new RecordingResolver((version, requirements, _) =>
            Task.FromResult(ReadyResolution(version, requirements)));
        var composite = new CompositeConnectionReadinessResolver(legacy, mail);
        CapabilityConnectionRequirement[] requirements = scenario == "empty"
            ? []
            : [new("service", CapabilityConnectionKindPolicy.Http)];

        var direct = await legacy.ResolveAsync(exact, requirements, CancellationToken.None);
        var combined = await composite.ResolveAsync(exact, requirements, CancellationToken.None);

        Assert.Equal(direct.IsReady, combined.IsReady);
        Assert.Equal(direct.AggregateHash, combined.AggregateHash);
        Assert.Equal(direct.Bindings.ToArray(), combined.Bindings.ToArray());
        Assert.Empty(mail.Calls);
    }

    [Fact]
    public async Task MixedThreeKindsArePartitionedMergedByExactSlotAndRehashedOnce()
    {
        var exact = Exact("company.mixed", 'b');
        var http = new RecordingResolver((version, requirements, _) =>
            Task.FromResult(ReadyResolution(version, requirements)));
        var mail = new RecordingResolver((version, requirements, _) =>
            Task.FromResult(ReadyResolution(version, requirements)));
        var smtp = new RecordingResolver((version, requirements, _) =>
            Task.FromResult(ReadyResolution(version, requirements)));
        var composite = new CompositeConnectionReadinessResolver(http, mail, smtp);
        CapabilityConnectionRequirement[] input =
        [
            new("zeta-mail", CapabilityConnectionKindPolicy.MailApi),
            new("gamma-smtp", CapabilityConnectionKindPolicy.Smtp),
            new("beta-http", CapabilityConnectionKindPolicy.Http),
            new("alpha-mail", CapabilityConnectionKindPolicy.MailApi),
        ];

        var first = await composite.ResolveAsync(exact, input, CancellationToken.None);
        var reordered = await composite.ResolveAsync(exact, input.Reverse().ToArray(), CancellationToken.None);

        Assert.True(first.IsReady);
        Assert.Equal(
            ["alpha-mail", "beta-http", "gamma-smtp", "zeta-mail"],
            first.Bindings.Select(binding => binding.Slot));
        Assert.Equal(ConnectionReadinessHasher.Compute(exact, first.Bindings), first.AggregateHash);
        Assert.Equal(first.AggregateHash, reordered.AggregateHash);
        Assert.All(http.Calls.SelectMany(call => call), requirement =>
            Assert.Equal(CapabilityConnectionKindPolicy.Http, requirement.Kind));
        Assert.All(mail.Calls.SelectMany(call => call), requirement =>
            Assert.Equal(CapabilityConnectionKindPolicy.MailApi, requirement.Kind));
        Assert.All(smtp.Calls.SelectMany(call => call), requirement =>
            Assert.Equal(CapabilityConnectionKindPolicy.Smtp, requirement.Kind));
        Assert.All(http.Calls, call => Assert.Single(call));
        Assert.All(mail.Calls, call => Assert.Equal(2, call.Count));
        Assert.All(smtp.Calls, call => Assert.Single(call));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ResolvedConnectionBinding>)first.Bindings).Add(first.Bindings[0]));
    }

    [Fact]
    public async Task DuplicateOrOversizedRequirementsFailClosedWithoutCallingChildren()
    {
        var exact = Exact("company.invalid-mixed", 'c');
        var http = SuccessResolver();
        var mail = SuccessResolver();
        var composite = new CompositeConnectionReadinessResolver(http, mail);

        var duplicate = await composite.ResolveAsync(
            exact,
            [
                new("same", CapabilityConnectionKindPolicy.Http),
                new("same", CapabilityConnectionKindPolicy.MailApi),
            ],
            CancellationToken.None);
        var oversized = await composite.ResolveAsync(
            exact,
            Enumerable.Range(0, 9)
                .Select(index => new CapabilityConnectionRequirement(
                    $"slot-{index}",
                    CapabilityConnectionKindPolicy.Http))
                .ToArray(),
            CancellationToken.None);

        Assert.False(duplicate.IsReady);
        Assert.Empty(duplicate.Bindings);
        Assert.False(oversized.IsReady);
        Assert.Empty(oversized.Bindings);
        Assert.Empty(http.Calls);
        Assert.Empty(mail.Calls);
    }

    [Fact]
    public async Task UnsupportedKindBlocksTheWholeResolutionWithoutCallingChildren()
    {
        var exact = Exact("company.unsupported-mixed", 'd');
        var http = SuccessResolver();
        var mail = SuccessResolver();
        var smtp = SuccessResolver();
        var composite = new CompositeConnectionReadinessResolver(http, mail, smtp);

        var resolution = await composite.ResolveAsync(
            exact,
            [
                new("supported", CapabilityConnectionKindPolicy.Http),
                new("unsupported", "sql-read"),
            ],
            CancellationToken.None);

        Assert.False(resolution.IsReady);
        Assert.Equal(["supported", "unsupported"], resolution.Bindings.Select(binding => binding.Slot));
        Assert.Equal("connection.resolution.blocked", resolution.Bindings[0].ErrorCode);
        Assert.Equal("connection.kind.unsupported", resolution.Bindings[1].ErrorCode);
        Assert.Equal(ConnectionReadinessHasher.Compute(exact, resolution.Bindings), resolution.AggregateHash);
        Assert.Empty(http.Calls);
        Assert.Empty(mail.Calls);
        Assert.Empty(smtp.Calls);
    }

    [Fact]
    public async Task LegacyTwoResolverCompositionFailsClosedForSmtpWithoutDroppingOtherEvidence()
    {
        var exact = Exact("company.smtp-resolver-missing", '7');
        var http = SuccessResolver();
        var mail = SuccessResolver();
        var composite = new CompositeConnectionReadinessResolver(http, mail);

        var resolution = await composite.ResolveAsync(
            exact,
            [
                new("http", CapabilityConnectionKindPolicy.Http),
                new("smtp", CapabilityConnectionKindPolicy.Smtp),
            ],
            CancellationToken.None);

        Assert.False(resolution.IsReady);
        Assert.True(resolution.Bindings.Single(binding => binding.Slot == "http").IsReady);
        Assert.Equal(
            "connection.resolution.blocked",
            resolution.Bindings.Single(binding => binding.Slot == "smtp").ErrorCode);
        Assert.Single(http.Calls);
        Assert.Empty(mail.Calls);
    }

    [Fact]
    public async Task PartialChildResultInvalidatesItsWholeGroupButPreservesOtherKind()
    {
        var exact = Exact("company.partial-child", 'e');
        var http = SuccessResolver();
        var mail = new RecordingResolver((version, requirements, _) =>
        {
            var partial = ReadyResolution(version, requirements.Take(1).ToArray());
            return Task.FromResult(partial);
        });
        var composite = new CompositeConnectionReadinessResolver(http, mail);

        var resolution = await composite.ResolveAsync(
            exact,
            [
                new("http", CapabilityConnectionKindPolicy.Http),
                new("mail-one", CapabilityConnectionKindPolicy.MailApi),
                new("mail-two", CapabilityConnectionKindPolicy.MailApi),
            ],
            CancellationToken.None);

        Assert.False(resolution.IsReady);
        Assert.True(resolution.Bindings.Single(binding => binding.Slot == "http").IsReady);
        Assert.All(
            resolution.Bindings.Where(binding => binding.Kind == CapabilityConnectionKindPolicy.MailApi),
            binding =>
            {
                Assert.False(binding.IsReady);
                Assert.Equal("connection.resolution.invalid", binding.ErrorCode);
                Assert.Null(binding.ProfileId);
            });
        Assert.Single(http.Calls);
        Assert.Single(mail.Calls);
    }

    [Theory]
    [InlineData("wrong-hash")]
    [InlineData("wrong-kind")]
    [InlineData("incoherent-ready")]
    [InlineData("duplicate-output")]
    public async Task MalformedChildEvidenceIsReplacedWithFixedRawFreeBindings(string mutation)
    {
        const string canary = "malformed-child-raw-canary";
        var exact = Exact("company.malformed-child", 'f');
        var http = new RecordingResolver((version, requirements, _) =>
        {
            var valid = ReadyResolution(version, requirements);
            return mutation switch
            {
                "wrong-hash" => Task.FromResult(valid with { AggregateHash = Hash('0') }),
                "wrong-kind" => Task.FromResult(valid with
                {
                    Bindings = Array.AsReadOnly([
                        valid.Bindings[0] with { Kind = CapabilityConnectionKindPolicy.MailApi },
                    ]),
                }),
                "incoherent-ready" => Task.FromResult(valid with
                {
                    Bindings = Array.AsReadOnly([
                        valid.Bindings[0] with { ErrorCode = canary },
                    ]),
                }),
                "duplicate-output" => Task.FromResult(new ConnectionReadinessResolution(
                    true,
                    Hash('6'),
                    Array.AsReadOnly([valid.Bindings[0], valid.Bindings[0]]))),
                _ => throw new InvalidOperationException(),
            };
        });
        var composite = new CompositeConnectionReadinessResolver(http, SuccessResolver());

        var resolution = await composite.ResolveAsync(
            exact,
            [new("http", CapabilityConnectionKindPolicy.Http)],
            CancellationToken.None);

        Assert.False(resolution.IsReady);
        Assert.Equal("connection.resolution.invalid", Assert.Single(resolution.Bindings).ErrorCode);
        Assert.DoesNotContain(canary, resolution.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChildExceptionFailsOnlyThatGroupAndDoesNotExposeItsMessage()
    {
        const string canary = "resolver-exception-raw-canary";
        var exact = Exact("company.exception-child", '1');
        var http = new RecordingResolver((_, _, _) =>
            throw new InvalidOperationException(canary));
        var mail = SuccessResolver();
        var composite = new CompositeConnectionReadinessResolver(http, mail);

        var resolution = await composite.ResolveAsync(
            exact,
            [
                new("http", CapabilityConnectionKindPolicy.Http),
                new("mail", CapabilityConnectionKindPolicy.MailApi),
            ],
            CancellationToken.None);

        Assert.False(resolution.IsReady);
        Assert.Equal("connection.resolution.failed", resolution.Bindings.Single(binding => binding.Slot == "http").ErrorCode);
        Assert.True(resolution.Bindings.Single(binding => binding.Slot == "mail").IsReady);
        Assert.Single(mail.Calls);
        Assert.DoesNotContain(canary, resolution.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedIntoReadinessEvidence()
    {
        var exact = Exact("company.cancel-child", '2');
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var http = new RecordingResolver((_, _, token) =>
            Task.FromCanceled<ConnectionReadinessResolution>(token));
        var composite = new CompositeConnectionReadinessResolver(http, SuccessResolver());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => composite.ResolveAsync(
            exact,
            [new("http", CapabilityConnectionKindPolicy.Http)],
            cancellation.Token));
    }

    [Fact]
    public void ConstructorRejectsNullChildren()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CompositeConnectionReadinessResolver(null!, SuccessResolver()));
        Assert.Throws<ArgumentNullException>(() =>
            new CompositeConnectionReadinessResolver(SuccessResolver(), null!));
        Assert.Throws<ArgumentNullException>(() =>
            new CompositeConnectionReadinessResolver(SuccessResolver(), SuccessResolver(), null!));
    }

    private static RecordingResolver SuccessResolver() => new((version, requirements, _) =>
        Task.FromResult(ReadyResolution(version, requirements)));

    private static ConnectionReadinessResolution ReadyResolution(
        CapabilityVersionRef exactVersion,
        IReadOnlyList<CapabilityConnectionRequirement> requirements)
    {
        var bindings = requirements
            .Select(requirement => new ResolvedConnectionBinding(
                exactVersion,
                requirement.Slot,
                requirement.Kind,
                ConnectionProfileId.Parse($"profile-{requirement.Slot}"),
                BindingRevision: 1,
                BindingHash: Hash('3'),
                ProfileTargetRevision: 2,
                ProfileCredentialRevision: 4,
                ProfileTargetHash: Hash('4'),
                IsReady: true,
                ErrorCode: null))
            .OrderBy(binding => binding.Slot, StringComparer.Ordinal)
            .ToArray();
        var frozen = Array.AsReadOnly(bindings);
        return new ConnectionReadinessResolution(
            true,
            ConnectionReadinessHasher.Compute(exactVersion, frozen),
            frozen);
    }

    private static void AddReadyHttp(
        HttpRepository repository,
        CapabilityVersionRef exact,
        string slot)
    {
        var id = ConnectionProfileId.Parse("http-profile");
        const string baseUrl = "https://http.example.test/api/";
        var targetHash = HttpConnectionProfilePolicy.ComputeTargetHash(
            baseUrl,
            ConnectionAuthenticationKind.None);
        var profile = new HttpConnectionProfile(
            id,
            "HTTP profile",
            baseUrl,
            ConnectionAuthenticationKind.None,
            Enabled: true,
            ConnectionProfileLifecycleState.Stable,
            Revision: 1,
            TargetRevision: 2,
            CredentialRevision: 0,
            targetHash,
            FixedErrorCode: null,
            Now,
            Now);
        repository.Profiles[id] = profile;
        repository.Bindings[(exact, slot)] = new CapabilityConnectionBinding(
            exact,
            slot,
            CapabilityConnectionKindPolicy.Http,
            id,
            Revision: 3,
            profile.TargetRevision,
            profile.CredentialRevision,
            profile.TargetHash,
            Hash('5'),
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
    }

    private static CapabilityVersionRef Exact(string id, char hash) => new(
        CapabilityId.Parse(id),
        CapabilitySemanticVersion.Parse("1.0.0"),
        CapabilityContentHash.Parse(Hash(hash)));

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    private sealed class RecordingResolver(
        Func<CapabilityVersionRef, IReadOnlyList<CapabilityConnectionRequirement>, CancellationToken,
            Task<ConnectionReadinessResolution>> handler) : ICapabilityConnectionReadinessResolver
    {
        public List<IReadOnlyList<CapabilityConnectionRequirement>> Calls { get; } = [];

        public Task<ConnectionReadinessResolution> ResolveAsync(
            CapabilityVersionRef exactVersion,
            IReadOnlyList<CapabilityConnectionRequirement> requirements,
            CancellationToken cancellationToken)
        {
            var frozen = Array.AsReadOnly(requirements.ToArray());
            Calls.Add(frozen);
            return handler(exactVersion, frozen, cancellationToken);
        }
    }

    private sealed class HttpRepository : IConnectionProfileRepository
    {
        public Dictionary<ConnectionProfileId, HttpConnectionProfile> Profiles { get; } = [];
        public Dictionary<ConnectionProfileId, ConnectionProbeReceipt> Probes { get; } = [];
        public Dictionary<(CapabilityVersionRef Exact, string Slot), CapabilityConnectionBinding> Bindings { get; } = [];

        public Task<HttpConnectionProfile?> GetProfileAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) => Task.FromResult(Profiles.GetValueOrDefault(id));

        public Task<IReadOnlyList<HttpConnectionProfile>> ListProfilesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HttpConnectionProfile>>(Profiles.Values.ToArray());

        public Task<bool> TrySaveProfileAsync(
            HttpConnectionProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ConnectionProbeReceipt?> GetProbeReceiptAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) => Task.FromResult(Probes.GetValueOrDefault(id));

        public Task SaveProbeReceiptAsync(
            ConnectionProbeReceipt receipt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CapabilityConnectionBinding?> GetBindingAsync(
            CapabilityVersionRef exactVersion,
            string slot,
            CancellationToken cancellationToken) =>
            Task.FromResult(Bindings.GetValueOrDefault((exactVersion, slot)));

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

    private sealed class EmptyCredentials : IConnectionCredentialStore
    {
        public Task<bool> ExistsAsync(
            ConnectionProfileId profileId,
            ConnectionCredentialKind kind,
            CancellationToken cancellationToken) => Task.FromResult(false);

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
