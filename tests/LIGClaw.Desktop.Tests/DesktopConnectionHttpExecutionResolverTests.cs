using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class DesktopConnectionHttpExecutionResolverTests
{
    [Fact]
    public async Task ResolvesExactAggregateAndReadsBearerOnlyAfterMetadataValidation()
    {
        var fixture = Fixture();
        var profiles = new FakeConnections(fixture.Profile);
        var credentialReads = 0;
        var executionLeases = new ConnectionExecutionLeaseCoordinator();
        var resolver = new DesktopConnectionHttpExecutionResolver(
            new FakeCapabilities(fixture.Version),
            profiles,
            new FakeReadiness(fixture.Resolution),
            (_, _, _) =>
            {
                credentialReads++;
                return Task.FromResult<string?>("fixture-secret");
            },
            executionLeases);

        var prepared = await resolver.ResolveAsync(
            fixture.Context,
            "service",
            ConnectionHttpResolutionPurpose.Preparation,
            CancellationToken.None);
        Assert.True(prepared.Success);
        Assert.Null(prepared.Binding!.Credential);
        Assert.Equal(0, credentialReads);

        var result = await resolver.ResolveAsync(
            fixture.Context,
            "service",
            ConnectionHttpResolutionPurpose.Execution,
            CancellationToken.None);

        Assert.True(result.Success);
        var binding = Assert.IsType<ConnectionHttpRuntimeBinding>(result.Binding);
        Assert.Equal(new Uri("https://internal.example/api/"), binding.BaseUri);
        Assert.Equal(fixture.Resolution.AggregateHash, binding.FrozenBindingHash);
        Assert.Equal(ConnectionHttpAuthenticationKind.Bearer, binding.AuthenticationKind);
        Assert.Equal("fixture-secret", binding.Credential!.Reveal());
        Assert.Equal("Connection HTTP runtime binding [redacted]", binding.ToString());
        Assert.Equal("[redacted]", binding.Credential.ToString());
        Assert.DoesNotContain("fixture-secret", binding.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, credentialReads);
        Assert.Equal(3, profiles.ProfileReads);
        var competingLease = executionLeases.AcquireAsync(CancellationToken.None).AsTask();
        Assert.False(competingLease.IsCompleted);
        await result.ExecutionLease!.DisposeAsync();
        await using var acquired = await competingLease;
    }

    [Fact]
    public async Task RejectsStaleAggregateBeforeReadingProfileOrCredential()
    {
        var fixture = Fixture();
        var profiles = new FakeConnections(fixture.Profile);
        var credentialReads = 0;
        var resolver = new DesktopConnectionHttpExecutionResolver(
            new FakeCapabilities(fixture.Version),
            profiles,
            new FakeReadiness(fixture.Resolution),
            (_, _, _) =>
            {
                credentialReads++;
                return Task.FromResult<string?>("must-not-be-read");
            });
        var stale = fixture.Context with { ConnectionBindingHash = Hash('f') };

        var result = await resolver.ResolveAsync(
            stale,
            "service",
            ConnectionHttpResolutionPurpose.Execution,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("connection.http.binding_mismatch", result.ErrorCode);
        Assert.Equal(0, profiles.ProfileReads);
        Assert.Equal(0, credentialReads);
    }

    [Fact]
    public async Task RejectsTargetChangeDuringCredentialReadWithoutReturningNewTarget()
    {
        var fixture = Fixture();
        const string changedBaseUrl = "https://changed.example/root/";
        var changed = fixture.Profile with
        {
            BaseUrl = changedBaseUrl,
            Revision = fixture.Profile.Revision + 1,
            TargetRevision = fixture.Profile.TargetRevision + 1,
            TargetHash = HttpConnectionProfilePolicy.ComputeTargetHash(
                changedBaseUrl,
                fixture.Profile.Authentication),
            UpdatedAtUtc = fixture.Profile.UpdatedAtUtc.AddSeconds(1),
        };
        var profiles = new FakeConnections(fixture.Profile, changed);
        var resolver = new DesktopConnectionHttpExecutionResolver(
            new FakeCapabilities(fixture.Version),
            profiles,
            new FakeReadiness(fixture.Resolution),
            (_, _, _) => Task.FromResult<string?>("old-secret"));

        var result = await resolver.ResolveAsync(
            fixture.Context,
            "service",
            ConnectionHttpResolutionPurpose.Execution,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Binding);
        Assert.Equal("connection.http.binding_mismatch", result.ErrorCode);
        Assert.Equal(2, profiles.ProfileReads);
    }

    [Fact]
    public async Task RejectsUndeclaredSlotWithoutResolvingReadinessOrSecret()
    {
        var fixture = Fixture();
        var readiness = new FakeReadiness(fixture.Resolution);
        var resolver = new DesktopConnectionHttpExecutionResolver(
            new FakeCapabilities(fixture.Version),
            new FakeConnections(fixture.Profile),
            readiness,
            (_, _, _) => throw new InvalidOperationException("secret read must not occur"));

        var result = await resolver.ResolveAsync(
            fixture.Context,
            "other",
            ConnectionHttpResolutionPurpose.Preparation,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("connection.http.binding_unavailable", result.ErrorCode);
        Assert.Equal(0, readiness.Calls);
    }

    [Fact]
    public async Task MapsMissingBearerToFixedCredentialErrorWithoutProfileRead()
    {
        var fixture = Fixture();
        var notReadyBinding = fixture.Resolution.Bindings[0] with
        {
            IsReady = false,
            ErrorCode = "connection.secret.missing",
        };
        var notReady = new ConnectionReadinessResolution(
            false,
            ConnectionReadinessHasher.Compute(fixture.Version.ExactVersion, [notReadyBinding]),
            [notReadyBinding]);
        var profiles = new FakeConnections(fixture.Profile);
        var resolver = new DesktopConnectionHttpExecutionResolver(
            new FakeCapabilities(fixture.Version),
            profiles,
            new FakeReadiness(notReady),
            (_, _, _) => throw new InvalidOperationException("secret read must not occur"));

        var result = await resolver.ResolveAsync(
            fixture.Context with { ConnectionBindingHash = notReady.AggregateHash },
            "service",
            ConnectionHttpResolutionPurpose.Preparation,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("connection.http.credential_unavailable", result.ErrorCode);
        Assert.Equal(0, profiles.ProfileReads);
    }

    [Fact]
    public async Task RejectsBindingChangeDuringCredentialReadOnFinalReadinessCheck()
    {
        var fixture = Fixture();
        var changedBinding = fixture.Resolution.Bindings[0] with
        {
            BindingRevision = fixture.Resolution.Bindings[0].BindingRevision + 1,
            BindingHash = Hash('f'),
        };
        var changedResolution = new ConnectionReadinessResolution(
            true,
            ConnectionReadinessHasher.Compute(fixture.Version.ExactVersion, [changedBinding]),
            [changedBinding]);
        var credentialReads = 0;
        var readiness = new FakeReadiness(fixture.Resolution, changedResolution);
        var resolver = new DesktopConnectionHttpExecutionResolver(
            new FakeCapabilities(fixture.Version),
            new FakeConnections(fixture.Profile),
            readiness,
            (_, _, _) =>
            {
                credentialReads++;
                return Task.FromResult<string?>("fixture-secret");
            });

        var result = await resolver.ResolveAsync(
            fixture.Context,
            "service",
            ConnectionHttpResolutionPurpose.Execution,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Binding);
        Assert.Equal("connection.http.binding_mismatch", result.ErrorCode);
        Assert.Equal(1, credentialReads);
        Assert.Equal(2, readiness.Calls);
    }

    [Fact]
    public async Task MixedHttpAndMailRequirementsUseCompositeReadinessButOnlyExactHttpSlot()
    {
        var fixture = Fixture(mixed: true);
        var httpReadiness = new FakeReadiness(Resolution(
            fixture.Version.ExactVersion,
            fixture.Resolution.Bindings
                .Where(binding => binding.Kind == CapabilityConnectionKindPolicy.Http)
                .ToArray()));
        var mailReadiness = new FakeReadiness(Resolution(
            fixture.Version.ExactVersion,
            fixture.Resolution.Bindings
                .Where(binding => binding.Kind == CapabilityConnectionKindPolicy.MailApi)
                .ToArray()));
        var resolver = new DesktopConnectionHttpExecutionResolver(
            new FakeCapabilities(fixture.Version),
            new FakeConnections(fixture.Profile),
            new CompositeConnectionReadinessResolver(httpReadiness, mailReadiness),
            (_, _, _) => throw new InvalidOperationException("preparation must not read a credential"));

        var resolved = await resolver.ResolveAsync(
            fixture.Context,
            "service",
            ConnectionHttpResolutionPurpose.Preparation,
            CancellationToken.None);
        var wrongSelectedKind = await resolver.ResolveAsync(
            fixture.Context,
            "mail",
            ConnectionHttpResolutionPurpose.Preparation,
            CancellationToken.None);

        Assert.True(resolved.Success);
        Assert.Equal(fixture.Resolution.AggregateHash, resolved.Binding!.FrozenBindingHash);
        Assert.False(wrongSelectedKind.Success);
        Assert.Equal("connection.http.binding_unavailable", wrongSelectedKind.ErrorCode);
        Assert.Equal(
            ["service"],
            httpReadiness.RequirementReads.Single().Select(requirement => requirement.Slot));
        Assert.Equal(
            ["mail"],
            mailReadiness.RequirementReads.Single().Select(requirement => requirement.Slot));
    }

    [Fact]
    public async Task SharedLeaseBlocksOppositeKindMutationAndRejectsTheStaleMixedAggregate()
    {
        var fixture = Fixture(mixed: true);
        var httpBinding = fixture.Resolution.Bindings.Single(binding =>
            binding.Kind == CapabilityConnectionKindPolicy.Http);
        var now = DateTimeOffset.Parse("2026-08-02T00:00:00+00:00");
        const string originalBaseUrl = "https://mail.internal.example/";
        const string changedBaseUrl = "https://mail.changed.example/";
        const string sender = "sender@example.test";
        var mailProfile = new MailGatewayConnectionProfile(
            ConnectionProfileId.Parse("fixture-mail"),
            "Fixture Mail Gateway",
            originalBaseUrl,
            sender,
            true,
            ConnectionProfileLifecycleState.Stable,
            3,
            1,
            1,
            MailGatewayConnectionProfilePolicy.ComputeTargetHash(originalBaseUrl, sender),
            null,
            now,
            now);
        var mailBinding = new ResolvedConnectionBinding(
            fixture.Version.ExactVersion,
            "mail",
            CapabilityConnectionKindPolicy.MailApi,
            mailProfile.Id,
            2,
            Hash('c'),
            mailProfile.TargetRevision,
            mailProfile.CredentialRevision,
            mailProfile.TargetHash,
            true,
            null);
        var mixedResolution = Resolution(
            fixture.Version.ExactVersion,
            [httpBinding, mailBinding]);
        var frozenContext = fixture.Context with
        {
            ConnectionBindingHash = mixedResolution.AggregateHash,
        };
        var mailConnections = new MutableMailConnections(mailProfile);
        var mailReadiness = new MutableMailReadiness(
            fixture.Version.ExactVersion,
            mailBinding,
            mailConnections);
        var executionLeases = new ConnectionExecutionLeaseCoordinator();
        var credentialReads = 0;
        var resolver = new DesktopConnectionHttpExecutionResolver(
            new FakeCapabilities(fixture.Version),
            new FakeConnections(fixture.Profile),
            new CompositeConnectionReadinessResolver(
                new FakeReadiness(Resolution(fixture.Version.ExactVersion, [httpBinding])),
                mailReadiness),
            (_, _, _) =>
            {
                credentialReads++;
                return Task.FromResult<string?>("fixture-secret");
            },
            executionLeases);
        var mailManagement = new MailGatewayConnectionManagementService(
            mailConnections,
            new NoMutationCredentials(),
            executionLeaseCoordinator: executionLeases);

        var executing = await resolver.ResolveAsync(
            frozenContext,
            "service",
            ConnectionHttpResolutionPurpose.Execution,
            CancellationToken.None);

        Assert.True(executing.Success);
        Assert.NotNull(executing.ExecutionLease);
        Assert.Equal(1, credentialReads);

        var pendingMutation = mailManagement.SaveMailGatewayProfileAsync(
            new MailGatewayConnectionProfileDraft(
                mailProfile.Id,
                mailProfile.DisplayName,
                changedBaseUrl,
                mailProfile.Sender,
                mailProfile.Enabled),
            mailProfile.Revision,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None);

        Assert.False(pendingMutation.IsCompleted);
        Assert.Equal(0, mailConnections.ProfileReads);
        Assert.Equal(0, mailConnections.ProfileWrites);

        await executing.ExecutionLease!.DisposeAsync();
        var mutation = await pendingMutation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ConnectionManagementOutcome.Succeeded, mutation.Outcome);
        Assert.Equal(changedBaseUrl, mutation.Profile!.BaseUrl);
        Assert.Equal(2, mailConnections.ProfileWrites);

        var staleExecution = await resolver.ResolveAsync(
            frozenContext,
            "service",
            ConnectionHttpResolutionPurpose.Execution,
            CancellationToken.None);

        Assert.False(staleExecution.Success);
        Assert.Null(staleExecution.Binding);
        Assert.Null(staleExecution.ExecutionLease);
        Assert.Equal("connection.http.binding_unavailable", staleExecution.ErrorCode);
        Assert.Equal(1, credentialReads);
        Assert.True(mailReadiness.StaleReads > 0);
    }

    private static ResolverFixture Fixture(bool mixed = false)
    {
        CapabilityConnectionRequirement[] requirements = mixed
            ?
            [
                new("service", CapabilityConnectionKindPolicy.Http),
                new("mail", CapabilityConnectionKindPolicy.MailApi),
            ]
            : [new("service", CapabilityConnectionKindPolicy.Http)];
        var manifest = new CapabilityManifest(
            1,
            CapabilityId.Parse("company.fixture.http"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            "HTTP fixture",
            "Calls one registered HTTP connection.",
            CapabilityKind.Workflow,
            ["call fixture"],
            ["connection.http.request.v1"],
            requirements,
            "R3",
            "workflow.json");
        var payload = new CapabilityVersionPayload(
            manifest,
            "{}",
            "Run the immutable workflow.",
            "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
            CanonicalWorkflowJson: "{}",
            CanonicalTestsJson: "[]");
        var exact = new CapabilityVersionRef(
            manifest.Id,
            manifest.Version,
            CapabilityContentHasher.Compute(payload));
        var version = new CapabilityVersion(
            exact,
            null,
            payload,
            "local_import",
            CapabilityContentHash.Parse(Hash('e')),
            DateTimeOffset.Parse("2026-08-02T00:00:00+00:00"));
        const string baseUrl = "https://internal.example/api/";
        var targetHash = HttpConnectionProfilePolicy.ComputeTargetHash(
            baseUrl,
            ConnectionAuthenticationKind.Bearer);
        var profileId = ConnectionProfileId.Parse("fixture-http");
        var now = DateTimeOffset.Parse("2026-08-02T00:00:00+00:00");
        var profile = new HttpConnectionProfile(
            profileId,
            "Fixture HTTP",
            baseUrl,
            ConnectionAuthenticationKind.Bearer,
            true,
            ConnectionProfileLifecycleState.Stable,
            3,
            1,
            1,
            targetHash,
            null,
            now,
            now);
        var bindingHash = Hash('b');
        var binding = new ResolvedConnectionBinding(
            exact,
            "service",
            "http",
            profileId,
            1,
            bindingHash,
            profile.TargetRevision,
            profile.CredentialRevision,
            profile.TargetHash,
            true,
            null);
        ResolvedConnectionBinding[] bindings = mixed
            ?
            [
                binding,
                new ResolvedConnectionBinding(
                    exact,
                    "mail",
                    CapabilityConnectionKindPolicy.MailApi,
                    ConnectionProfileId.Parse("fixture-mail"),
                    2,
                    Hash('c'),
                    1,
                    1,
                    Hash('d'),
                    true,
                    null),
            ]
            : [binding];
        var resolution = Resolution(exact, bindings);
        var context = new CapabilityInvocationContext(
            Hash('a'),
            CapabilityRuntimeHasher.ComputeModelToolName(exact),
            exact.Id.Value,
            exact.Version.Value,
            exact.ContentHash.Value,
            1,
            resolution.AggregateHash);
        return new ResolverFixture(version, profile, resolution, context);
    }

    private static ConnectionReadinessResolution Resolution(
        CapabilityVersionRef exactVersion,
        IReadOnlyList<ResolvedConnectionBinding> bindings)
    {
        var frozen = Array.AsReadOnly(bindings
            .OrderBy(binding => binding.Slot, StringComparer.Ordinal)
            .ToArray());
        return new ConnectionReadinessResolution(
            frozen.All(binding => binding.IsReady),
            ConnectionReadinessHasher.Compute(exactVersion, frozen),
            frozen);
    }

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    private sealed record ResolverFixture(
        CapabilityVersion Version,
        HttpConnectionProfile Profile,
        ConnectionReadinessResolution Resolution,
        CapabilityInvocationContext Context);

    private sealed class FakeCapabilities(CapabilityVersion version) : ICapabilityCatalogRepository
    {
        public Task<CapabilityVersion?> GetVersionAsync(
            CapabilityVersionRef exactVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<CapabilityVersion?>(version.ExactVersion.Equals(exactVersion) ? version : null);

        public Task<CapabilityCatalogEntry?> GetCatalogAsync(CapabilityId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<CapabilityDraft?> GetDraftAsync(string draftId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<CapabilityCatalogSummary>> ListAsync(string? query, int offset, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<CapabilityDraftCreateResult> CreateValidatedDraftAsync(ValidatedCapabilityDraft draft, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<CapabilityActivationResult> ApproveAndActivateAsync(CapabilityApprovalCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<CapabilityActivationResult> RollbackAsync(CapabilityRollbackCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<CapabilityActivation>> ListActivationsAsync(CapabilityId id, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeConnections(params HttpConnectionProfile[] profiles) : IConnectionProfileRepository
    {
        private readonly IReadOnlyList<HttpConnectionProfile> values = profiles;
        public int ProfileReads { get; private set; }

        public Task<HttpConnectionProfile?> GetProfileAsync(ConnectionProfileId id, CancellationToken cancellationToken)
        {
            var index = Math.Min(ProfileReads, values.Count - 1);
            ProfileReads++;
            return Task.FromResult<HttpConnectionProfile?>(
                index >= 0 && values[index].Id.Equals(id) ? values[index] : null);
        }

        public Task<IReadOnlyList<HttpConnectionProfile>> ListProfilesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> TrySaveProfileAsync(HttpConnectionProfile profile, long expectedRevision, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<ConnectionProbeReceipt?> GetProbeReceiptAsync(ConnectionProfileId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task SaveProbeReceiptAsync(ConnectionProbeReceipt receipt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<CapabilityConnectionBinding?> GetBindingAsync(CapabilityVersionRef exactVersion, string slot, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<CapabilityConnectionBinding>> GetBindingsAsync(CapabilityVersionRef exactVersion, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> TrySaveBindingAsync(CapabilityConnectionBinding binding, long expectedRevision, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeReadiness(params ConnectionReadinessResolution[] resolutions)
        : ICapabilityConnectionReadinessResolver
    {
        public int Calls { get; private set; }
        public List<IReadOnlyList<CapabilityConnectionRequirement>> RequirementReads { get; } = [];

        public Task<ConnectionReadinessResolution> ResolveAsync(
            CapabilityVersionRef exactVersion,
            IReadOnlyList<CapabilityConnectionRequirement> requirements,
            CancellationToken cancellationToken)
        {
            var resolution = resolutions[Math.Min(Calls, resolutions.Length - 1)];
            Calls++;
            RequirementReads.Add(Array.AsReadOnly(requirements.ToArray()));
            return Task.FromResult(resolution);
        }
    }

    private sealed class MutableMailReadiness(
        CapabilityVersionRef exactVersion,
        ResolvedConnectionBinding frozenBinding,
        MutableMailConnections connections) : ICapabilityConnectionReadinessResolver
    {
        public int StaleReads { get; private set; }

        public Task<ConnectionReadinessResolution> ResolveAsync(
            CapabilityVersionRef requestedVersion,
            IReadOnlyList<CapabilityConnectionRequirement> requirements,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(exactVersion, requestedVersion);
            var requirement = Assert.Single(requirements);
            Assert.Equal(frozenBinding.Slot, requirement.Slot);
            Assert.Equal(frozenBinding.Kind, requirement.Kind);
            var current = connections.Current;
            var binding = current.TargetRevision == frozenBinding.ProfileTargetRevision &&
                          current.CredentialRevision == frozenBinding.ProfileCredentialRevision &&
                          StringComparer.Ordinal.Equals(
                              current.TargetHash,
                              frozenBinding.ProfileTargetHash)
                ? frozenBinding
                : frozenBinding with
                {
                    IsReady = false,
                    ErrorCode = "connection.binding.stale",
                };
            if (!binding.IsReady) StaleReads++;
            return Task.FromResult(Resolution(exactVersion, [binding]));
        }
    }

    private sealed class MutableMailConnections(MailGatewayConnectionProfile initial)
        : IMailGatewayConnectionRepository
    {
        public MailGatewayConnectionProfile Current { get; private set; } = initial;
        public int ProfileReads { get; private set; }
        public int ProfileWrites { get; private set; }

        public Task<MailGatewayConnectionProfile?> GetProfileAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProfileReads++;
            return Task.FromResult<MailGatewayConnectionProfile?>(
                Current.Id.Equals(id) ? Current : null);
        }

        public Task<IReadOnlyList<MailGatewayConnectionProfile>> ListProfilesAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TrySaveProfileAsync(
            MailGatewayConnectionProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Current.Id.Equals(profile.Id) || Current.Revision != expectedRevision)
                return Task.FromResult(false);
            Current = profile;
            ProfileWrites++;
            return Task.FromResult(true);
        }

        public Task<ConnectionProbeReceipt?> GetProbeReceiptAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveProbeReceiptAsync(
            ConnectionProbeReceipt receipt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CapabilityConnectionBinding?> GetBindingAsync(
            CapabilityVersionRef exactVersion,
            string slot,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CapabilityConnectionBinding>> GetBindingsAsync(
            CapabilityVersionRef exactVersion,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TrySaveBindingAsync(
            CapabilityConnectionBinding binding,
            long expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoMutationCredentials : IConnectionCredentialStore
    {
        public Task<bool> ExistsAsync(
            ConnectionProfileId profileId,
            ConnectionCredentialKind kind,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ReplaceAsync(
            ConnectionProfileId profileId,
            ConnectionCredentialKind kind,
            string secret,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
            "Keep mutation must not replace a credential.");

        public Task DeleteAsync(
            ConnectionProfileId profileId,
            ConnectionCredentialKind kind,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
            "Keep mutation must not delete a credential.");
    }
}
