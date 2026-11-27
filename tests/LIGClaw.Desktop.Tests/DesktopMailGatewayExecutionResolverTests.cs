using System.Reflection;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class DesktopMailGatewayExecutionResolverTests
{
    [Fact]
    public async Task PreparationAndExecutionExposeExactRedactedRuntimeBindings()
    {
        const string token = "fixture-mail-token";
        var fixture = Fixture();
        var profiles = new FakeConnections(fixture.Profile);
        var readiness = new FakeReadiness(fixture.Resolution);
        var credentialReads = new List<(ConnectionProfileId Id, ConnectionCredentialKind Kind)>();
        var executionLeases = new ConnectionExecutionLeaseCoordinator();
        var resolver = new DesktopMailGatewayExecutionResolver(
            new FakeCapabilities(fixture.Version),
            profiles,
            readiness,
            (id, kind, _) =>
            {
                credentialReads.Add((id, kind));
                return Task.FromResult<string?>(token);
            },
            executionLeases);

        var prepared = await resolver.ResolveAsync(
            fixture.Context,
            "mail",
            MailGatewayResolutionPurpose.Preparation,
            CancellationToken.None);

        Assert.True(prepared.Success);
        Assert.Null(prepared.ExecutionLease);
        var preparedBinding = Assert.IsType<MailGatewayRuntimeBinding>(prepared.Binding);
        Assert.Equal(fixture.Profile.DisplayName, preparedBinding.DisplayName);
        Assert.Equal(new Uri(fixture.Profile.BaseUrl), preparedBinding.BaseUri);
        Assert.Equal(fixture.Profile.Sender, preparedBinding.Sender);
        Assert.Equal(fixture.Resolution.AggregateHash, preparedBinding.FrozenBindingHash);
        Assert.Equal(fixture.MailBinding.BindingHash, preparedBinding.LocalBindingHash);
        Assert.Null(preparedBinding.Credential);
        Assert.Empty(credentialReads);

        var executed = await resolver.ResolveAsync(
            fixture.Context,
            "mail",
            MailGatewayResolutionPurpose.Execution,
            CancellationToken.None);

        Assert.True(executed.Success);
        Assert.NotNull(executed.ExecutionLease);
        var executionBinding = Assert.IsType<MailGatewayRuntimeBinding>(executed.Binding);
        var credential = Assert.IsType<MailGatewayCredential>(executionBinding.Credential);
        Assert.Equal(token, credential.Reveal());
        Assert.Equal(
            WorkflowPreparedEffectHash.Compute(
                "mail-gateway-runtime-binding-v1",
                fixture.MailBinding.BindingHash!,
                fixture.Profile.TargetHash,
                fixture.Profile.TargetRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                fixture.Profile.CredentialRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            executionBinding.ExecutionFingerprint);
        Assert.Equal(
            [(fixture.Profile.Id, ConnectionCredentialKind.MailGatewayBearerToken)],
            credentialReads);
        Assert.Equal(3, profiles.ProfileReads);
        Assert.Equal(3, readiness.Calls);

        Assert.Equal("Mail Gateway runtime binding [redacted]", executionBinding.ToString());
        Assert.Equal("Mail Gateway execution resolution [redacted]", executed.ToString());
        Assert.Equal("[redacted]", credential.ToString());
        Assert.DoesNotContain(token, executionBinding.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Profile.BaseUrl, executionBinding.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Profile.Sender, executed.ToString(), StringComparison.Ordinal);

        var competingMutation = executionLeases.AcquireAsync(CancellationToken.None).AsTask();
        Assert.False(competingMutation.IsCompleted);
        await executed.ExecutionLease!.DisposeAsync();
        await using var acquired = await competingMutation;
    }

    [Fact]
    public async Task MixedHttpAndMailRequirementsRetainTheFullFrozenAggregate()
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
        var resolver = new DesktopMailGatewayExecutionResolver(
            new FakeCapabilities(fixture.Version),
            new FakeConnections(fixture.Profile),
            new CompositeConnectionReadinessResolver(httpReadiness, mailReadiness),
            (_, _, _) => Task.FromResult<string?>("must-not-be-read"));

        var result = await resolver.ResolveAsync(
            fixture.Context,
            "mail",
            MailGatewayResolutionPurpose.Preparation,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(fixture.Resolution.AggregateHash, result.Binding!.FrozenBindingHash);
        Assert.Equal(fixture.MailBinding.BindingHash, result.Binding.LocalBindingHash);
        Assert.Equal(
            [CapabilityConnectionKindPolicy.Http],
            httpReadiness.RequirementReads.Single().Select(requirement => requirement.Kind));
        Assert.Equal(
            [CapabilityConnectionKindPolicy.MailApi],
            mailReadiness.RequirementReads.Single().Select(requirement => requirement.Kind));
    }

    [Theory]
    [InlineData("wrong-tool")]
    [InlineData("wrong-capability-kind")]
    [InlineData("wrong-slot")]
    [InlineData("wrong-connection-kind")]
    [InlineData("duplicate-slot")]
    public async Task RejectsCapabilitiesWithoutOneExactWorkflowMailSlot(string mutation)
    {
        var requirements = mutation switch
        {
            "wrong-connection-kind" => new[]
            {
                new CapabilityConnectionRequirement("mail", CapabilityConnectionKindPolicy.Http),
            },
            "duplicate-slot" => new[]
            {
                new CapabilityConnectionRequirement("mail", CapabilityConnectionKindPolicy.MailApi),
                new CapabilityConnectionRequirement("mail", CapabilityConnectionKindPolicy.Http),
            },
            _ => null,
        };
        var fixture = Fixture(
            kind: mutation == "wrong-capability-kind" ? CapabilityKind.Prompt : CapabilityKind.Workflow,
            tools: mutation == "wrong-tool" ? ["mail.create_draft.v1"] : null,
            requirements: requirements);
        var readiness = new FakeReadiness(fixture.Resolution);
        var resolver = Resolver(fixture, new FakeConnections(fixture.Profile), readiness);

        var result = await resolver.ResolveAsync(
            fixture.Context,
            mutation == "wrong-slot" ? "other" : "mail",
            MailGatewayResolutionPurpose.Preparation,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("mail.gateway.binding_unavailable", result.ErrorCode);
        Assert.Equal(0, readiness.Calls);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("model-tool")]
    [InlineData("capability-id")]
    [InlineData("version")]
    [InlineData("content")]
    [InlineData("catalog-revision")]
    [InlineData("binding-hash-shape")]
    [InlineData("purpose")]
    public async Task RejectsMalformedOrNonExactFrozenContextBeforeReadiness(string mutation)
    {
        var fixture = Fixture();
        var context = mutation switch
        {
            "snapshot" => fixture.Context with { SnapshotHash = Hash('A') },
            "model-tool" => fixture.Context with { ModelToolName = "cap_wrong" },
            "capability-id" => fixture.Context with { CapabilityId = "INVALID" },
            "version" => fixture.Context with { Version = "not-a-version" },
            "content" => fixture.Context with { ContentHash = Hash('f') },
            "catalog-revision" => fixture.Context with { CatalogRevision = 0 },
            "binding-hash-shape" => fixture.Context with { ConnectionBindingHash = "not-a-hash" },
            _ => fixture.Context,
        };
        var readiness = new FakeReadiness(fixture.Resolution);
        var resolver = Resolver(fixture, new FakeConnections(fixture.Profile), readiness);

        var result = await resolver.ResolveAsync(
            context,
            "mail",
            mutation == "purpose"
                ? (MailGatewayResolutionPurpose)999
                : MailGatewayResolutionPurpose.Preparation,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("mail.gateway.binding_unavailable", result.ErrorCode);
        Assert.Equal(0, readiness.Calls);
    }

    [Fact]
    public async Task RejectsChangedAggregateBeforeProfileOrCredentialRead()
    {
        var fixture = Fixture();
        var profiles = new FakeConnections(fixture.Profile);
        var credentialReads = 0;
        var resolver = new DesktopMailGatewayExecutionResolver(
            new FakeCapabilities(fixture.Version),
            profiles,
            new FakeReadiness(fixture.Resolution),
            (_, _, _) =>
            {
                credentialReads++;
                return Task.FromResult<string?>("must-not-be-read");
            });

        var result = await resolver.ResolveAsync(
            fixture.Context with { ConnectionBindingHash = Hash('f') },
            "mail",
            MailGatewayResolutionPurpose.Execution,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("mail.gateway.binding_mismatch", result.ErrorCode);
        Assert.Equal(0, profiles.ProfileReads);
        Assert.Equal(0, credentialReads);
    }

    [Theory]
    [InlineData("aggregate")]
    [InlineData("local-binding")]
    public async Task RejectsAggregateOrLocalBindingChangeOnFinalReadiness(string mutation)
    {
        var fixture = Fixture(mixed: mutation == "aggregate");
        var changedBindings = fixture.Resolution.Bindings.Select(binding =>
        {
            var selected = mutation == "local-binding"
                ? binding.Kind == CapabilityConnectionKindPolicy.MailApi
                : binding.Kind == CapabilityConnectionKindPolicy.Http;
            return selected
                ? binding with
                {
                    BindingRevision = binding.BindingRevision + 1,
                    BindingHash = Hash('f'),
                }
                : binding;
        }).ToArray();
        var changed = Resolution(fixture.Version.ExactVersion, changedBindings);
        var readiness = new FakeReadiness(fixture.Resolution, changed);
        var resolver = Resolver(fixture, new FakeConnections(fixture.Profile), readiness);

        var result = await resolver.ResolveAsync(
            fixture.Context,
            "mail",
            MailGatewayResolutionPurpose.Execution,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Binding);
        Assert.Equal("mail.gateway.binding_mismatch", result.ErrorCode);
        Assert.Equal(2, readiness.Calls);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("sender")]
    [InlineData("credential-revision")]
    [InlineData("state")]
    public async Task RejectsProfileIdentityChangeDuringCredentialRead(string mutation)
    {
        var fixture = Fixture();
        var changed = mutation switch
        {
            "target" => ChangedTarget(fixture.Profile, "https://changed.example.test/", fixture.Profile.Sender),
            "sender" => ChangedTarget(fixture.Profile, fixture.Profile.BaseUrl, "changed@example.test"),
            "credential-revision" => fixture.Profile with
            {
                CredentialRevision = fixture.Profile.CredentialRevision + 1,
                UpdatedAtUtc = fixture.Profile.UpdatedAtUtc.AddSeconds(1),
            },
            "state" => fixture.Profile with
            {
                State = ConnectionProfileLifecycleState.Updating,
                UpdatedAtUtc = fixture.Profile.UpdatedAtUtc.AddSeconds(1),
            },
            _ => throw new InvalidOperationException(),
        };
        var profiles = new FakeConnections(fixture.Profile, changed);
        var readiness = new FakeReadiness(fixture.Resolution);
        var resolver = Resolver(fixture, profiles, readiness);

        var result = await resolver.ResolveAsync(
            fixture.Context,
            "mail",
            MailGatewayResolutionPurpose.Execution,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("mail.gateway.binding_mismatch", result.ErrorCode);
        Assert.Equal(2, profiles.ProfileReads);
        Assert.Equal(1, readiness.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("cross-kind")]
    [InlineData("invalid")]
    [InlineData("exception")]
    public async Task MissingCrossKindOrInvalidCredentialFailsWithFixedDiagnostics(string mutation)
    {
        const string canary = "credential-provider-raw-canary";
        var fixture = Fixture();
        var credentialValues = new Dictionary<ConnectionCredentialKind, string>();
        if (mutation == "cross-kind")
            credentialValues[ConnectionCredentialKind.BearerToken] = "http-only-token";
        if (mutation == "invalid")
            credentialValues[ConnectionCredentialKind.MailGatewayBearerToken] = "bad token";
        var checks = new List<ConnectionCredentialKind>();
        var executionLeases = new ConnectionExecutionLeaseCoordinator();
        var resolver = new DesktopMailGatewayExecutionResolver(
            new FakeCapabilities(fixture.Version),
            new FakeConnections(fixture.Profile),
            new FakeReadiness(fixture.Resolution),
            (_, kind, _) =>
            {
                checks.Add(kind);
                if (mutation == "exception") throw new InvalidOperationException(canary);
                return Task.FromResult<string?>(credentialValues.GetValueOrDefault(kind));
            },
            executionLeases);

        var result = await resolver.ResolveAsync(
            fixture.Context,
            "mail",
            MailGatewayResolutionPurpose.Execution,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("mail.gateway.credential_unavailable", result.ErrorCode);
        Assert.Equal([ConnectionCredentialKind.MailGatewayBearerToken], checks);
        Assert.DoesNotContain(canary, result.ToString(), StringComparison.Ordinal);
        await using var released = await executionLeases.AcquireAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReadinessMissingMailSecretMapsToCredentialUnavailableWithoutProfileRead()
    {
        var fixture = Fixture();
        var missingBinding = fixture.MailBinding with
        {
            IsReady = false,
            ErrorCode = "connection.secret.missing",
        };
        var missing = Resolution(fixture.Version.ExactVersion, [missingBinding]);
        var profiles = new FakeConnections(fixture.Profile);
        var resolver = Resolver(fixture, profiles, new FakeReadiness(missing));

        var result = await resolver.ResolveAsync(
            fixture.Context,
            "mail",
            MailGatewayResolutionPurpose.Preparation,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("mail.gateway.credential_unavailable", result.ErrorCode);
        Assert.Equal(0, profiles.ProfileReads);
    }

    [Fact]
    public async Task CredentialHasNoPublicSecretSurfaceAndAllTextIsRedacted()
    {
        const string token = "reflection-token-canary";
        var fixture = Fixture();
        var resolver = Resolver(
            fixture,
            new FakeConnections(fixture.Profile),
            new FakeReadiness(fixture.Resolution),
            (_, kind, _) => Task.FromResult<string?>(
                kind == ConnectionCredentialKind.MailGatewayBearerToken ? token : null));

        var result = await resolver.ResolveAsync(
            fixture.Context,
            "mail",
            MailGatewayResolutionPurpose.Execution,
            CancellationToken.None);
        var credential = Assert.IsType<MailGatewayCredential>(result.Binding!.Credential);

        Assert.Empty(typeof(MailGatewayCredential).GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));
        Assert.Null(typeof(MailGatewayCredential).GetMethod(
            "Reveal",
            BindingFlags.Instance | BindingFlags.Public));
        Assert.All(
            typeof(MailGatewayCredential).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            field => Assert.True(field.IsPrivate));
        Assert.DoesNotContain(token, credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(token, result.Binding.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(token, result.ToString(), StringComparison.Ordinal);
        await result.ExecutionLease!.DisposeAsync();
    }

    [Fact]
    public async Task CallerCancellationWhileWaitingForMutationLeaseIsPropagated()
    {
        var fixture = Fixture();
        var readiness = new FakeReadiness(fixture.Resolution);
        var executionLeases = new ConnectionExecutionLeaseCoordinator();
        await using var mutationLease = await executionLeases.AcquireAsync(CancellationToken.None);
        var resolver = Resolver(
            fixture,
            new FakeConnections(fixture.Profile),
            readiness,
            executionLeaseCoordinator: executionLeases);
        using var cancellation = new CancellationTokenSource();

        var pending = resolver.ResolveAsync(
            fixture.Context,
            "mail",
            MailGatewayResolutionPurpose.Execution,
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, readiness.Calls);
    }

    [Fact]
    public async Task SharedLeaseSerializesMutationBeforeValidationAndAfterSuccessfulTransfer()
    {
        var fixture = Fixture();
        var readiness = new FakeReadiness(fixture.Resolution);
        var executionLeases = new ConnectionExecutionLeaseCoordinator();
        var mutationLease = await executionLeases.AcquireAsync(CancellationToken.None);
        var resolver = Resolver(
            fixture,
            new FakeConnections(fixture.Profile),
            readiness,
            executionLeaseCoordinator: executionLeases);

        var pendingExecution = resolver.ResolveAsync(
            fixture.Context,
            "mail",
            MailGatewayResolutionPurpose.Execution,
            CancellationToken.None);

        Assert.False(pendingExecution.IsCompleted);
        Assert.Equal(0, readiness.Calls);
        await mutationLease.DisposeAsync();
        var result = await pendingExecution;
        Assert.True(result.Success);

        var pendingMutation = executionLeases.AcquireAsync(CancellationToken.None).AsTask();
        Assert.False(pendingMutation.IsCompleted);
        await result.ExecutionLease!.DisposeAsync();
        await using var acquiredMutation = await pendingMutation;
    }

    [Fact]
    public void ConstructorsRejectInvalidOrRawCredentialStateWithoutEchoingIt()
    {
        const string canary = "raw invalid credential";
        var credentialError = Assert.Throws<ArgumentException>(() => new MailGatewayCredential(canary));

        Assert.DoesNotContain(canary, credentialError.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => new DesktopMailGatewayExecutionResolver(
            null!,
            null!,
            null!,
            null!));
    }

    private static DesktopMailGatewayExecutionResolver Resolver(
        ResolverFixture fixture,
        FakeConnections connections,
        FakeReadiness readiness,
        Func<ConnectionProfileId, ConnectionCredentialKind, CancellationToken, Task<string?>>?
            readCredential = null,
        ConnectionExecutionLeaseCoordinator? executionLeaseCoordinator = null) =>
        new(
            new FakeCapabilities(fixture.Version),
            connections,
            readiness,
            readCredential ?? ((_, _, _) => Task.FromResult<string?>("fixture-mail-token")),
            executionLeaseCoordinator);

    private static ResolverFixture Fixture(
        bool mixed = false,
        CapabilityKind kind = CapabilityKind.Workflow,
        IReadOnlyList<string>? tools = null,
        IReadOnlyList<CapabilityConnectionRequirement>? requirements = null)
    {
        requirements ??= mixed
            ?
            [
                new CapabilityConnectionRequirement("service", CapabilityConnectionKindPolicy.Http),
                new CapabilityConnectionRequirement("mail", CapabilityConnectionKindPolicy.MailApi),
            ]
            : [new CapabilityConnectionRequirement("mail", CapabilityConnectionKindPolicy.MailApi)];
        var manifest = new CapabilityManifest(
            1,
            CapabilityId.Parse("company.fixture.mail"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            "Mail fixture",
            "Submits through one fixed Mail Gateway.",
            kind,
            ["send fixture"],
            tools ?? ["mail.send.v1"],
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
        const string baseUrl = "https://mail.internal.example/";
        const string sender = "sender@example.test";
        var targetHash = MailGatewayConnectionProfilePolicy.ComputeTargetHash(baseUrl, sender);
        var profileId = ConnectionProfileId.Parse("fixture-mail");
        var now = DateTimeOffset.Parse("2026-08-02T00:00:00+00:00");
        var profile = new MailGatewayConnectionProfile(
            profileId,
            "Fixture Mail Gateway",
            baseUrl,
            sender,
            true,
            ConnectionProfileLifecycleState.Stable,
            3,
            1,
            1,
            targetHash,
            null,
            now,
            now);
        var bindings = requirements.Select(requirement =>
            requirement.Kind == CapabilityConnectionKindPolicy.MailApi
                ? new ResolvedConnectionBinding(
                    exact,
                    requirement.Slot,
                    requirement.Kind,
                    profile.Id,
                    1,
                    Hash('b'),
                    profile.TargetRevision,
                    profile.CredentialRevision,
                    profile.TargetHash,
                    true,
                    null)
                : new ResolvedConnectionBinding(
                    exact,
                    requirement.Slot,
                    requirement.Kind,
                    ConnectionProfileId.Parse("fixture-http"),
                    2,
                    Hash('c'),
                    2,
                    0,
                    Hash('d'),
                    true,
                    null))
            .OrderBy(binding => binding.Slot, StringComparer.Ordinal)
            .ToArray();
        var resolutionBindings = bindings
            .GroupBy(binding => binding.Slot, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var resolution = Resolution(exact, resolutionBindings);
        var context = new CapabilityInvocationContext(
            Hash('a'),
            CapabilityRuntimeHasher.ComputeModelToolName(exact),
            exact.Id.Value,
            exact.Version.Value,
            exact.ContentHash.Value,
            1,
            resolution.AggregateHash);
        var mailBinding = bindings.SingleOrDefault(binding =>
                              binding.Slot == "mail" &&
                              binding.Kind == CapabilityConnectionKindPolicy.MailApi) ??
                          bindings.First();
        return new ResolverFixture(version, profile, resolution, context, mailBinding);
    }

    private static MailGatewayConnectionProfile ChangedTarget(
        MailGatewayConnectionProfile profile,
        string baseUrl,
        string sender) => profile with
        {
            BaseUrl = baseUrl,
            Sender = sender,
            Revision = profile.Revision + 1,
            TargetRevision = profile.TargetRevision + 1,
            TargetHash = MailGatewayConnectionProfilePolicy.ComputeTargetHash(baseUrl, sender),
            UpdatedAtUtc = profile.UpdatedAtUtc.AddSeconds(1),
        };

    private static ConnectionReadinessResolution Resolution(
        CapabilityVersionRef exactVersion,
        IReadOnlyList<ResolvedConnectionBinding> bindings)
    {
        var frozen = Array.AsReadOnly(bindings.OrderBy(binding => binding.Slot, StringComparer.Ordinal).ToArray());
        return new ConnectionReadinessResolution(
            frozen.All(binding => binding.IsReady),
            ConnectionReadinessHasher.Compute(exactVersion, frozen),
            frozen);
    }

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    private sealed record ResolverFixture(
        CapabilityVersion Version,
        MailGatewayConnectionProfile Profile,
        ConnectionReadinessResolution Resolution,
        CapabilityInvocationContext Context,
        ResolvedConnectionBinding MailBinding);

    private sealed class FakeCapabilities(CapabilityVersion version) : ICapabilityCatalogRepository
    {
        public Task<CapabilityVersion?> GetVersionAsync(
            CapabilityVersionRef exactVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<CapabilityVersion?>(version.ExactVersion.Equals(exactVersion) ? version : null);

        public Task<CapabilityCatalogEntry?> GetCatalogAsync(
            CapabilityId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CapabilityDraft?> GetDraftAsync(
            string draftId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CapabilityCatalogSummary>> ListAsync(
            string? query,
            int offset,
            int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CapabilityDraftCreateResult> CreateValidatedDraftAsync(
            ValidatedCapabilityDraft draft,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CapabilityActivationResult> ApproveAndActivateAsync(
            CapabilityApprovalCommand command,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CapabilityActivationResult> RollbackAsync(
            CapabilityRollbackCommand command,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CapabilityActivation>> ListActivationsAsync(
            CapabilityId id,
            int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeConnections(params MailGatewayConnectionProfile[] profiles)
        : IMailGatewayConnectionRepository
    {
        private readonly IReadOnlyList<MailGatewayConnectionProfile> values = profiles;

        public int ProfileReads { get; private set; }

        public Task<MailGatewayConnectionProfile?> GetProfileAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken)
        {
            var index = Math.Min(ProfileReads, values.Count - 1);
            ProfileReads++;
            return Task.FromResult<MailGatewayConnectionProfile?>(
                index >= 0 && values[index].Id.Equals(id) ? values[index] : null);
        }

        public Task<IReadOnlyList<MailGatewayConnectionProfile>> ListProfilesAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TrySaveProfileAsync(
            MailGatewayConnectionProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

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
}
