using System.Reflection;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class DesktopSmtpExecutionResolverTests
{
    [Fact]
    public async Task PreparationAvoidsCredentialAndExecutionReReadsExactContextWithStableFingerprint()
    {
        var fixture = SmtpFixture();
        var connections = new FakeSmtpConnections(fixture.Profile);
        var readiness = new FakeReadiness(fixture.Resolution);
        var credentialReads = 0;
        SmtpWireCredential? executionCredential = null;
        var resolver = new DesktopSmtpExecutionResolver(
            new FakeCapabilities(fixture.Version),
            connections,
            readiness,
            (_, _) =>
            {
                credentialReads++;
                executionCredential = new SmtpWireCredential("smtp-user", "smtp-password");
                return Task.FromResult<SmtpWireCredential?>(executionCredential);
            });

        var preparation = await resolver.ResolveAsync(
            fixture.Context,
            "smtp",
            SmtpResolutionPurpose.Preparation,
            CancellationToken.None);
        var execution = await resolver.ResolveAsync(
            fixture.Context,
            "smtp",
            SmtpResolutionPurpose.Execution,
            CancellationToken.None);

        var expectedFingerprint = WorkflowPreparedEffectHash.Compute(
            "smtp-runtime-binding-v1",
            fixture.Binding.BindingHash!,
            fixture.Profile.TargetHash,
            fixture.Profile.TargetRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            fixture.Profile.CredentialRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(preparation.Success);
        Assert.Null(preparation.Binding?.Credential);
        Assert.Null(preparation.ExecutionLease);
        Assert.Equal(expectedFingerprint, preparation.Binding?.ExecutionFingerprint);
        Assert.True(execution.Success);
        Assert.Same(executionCredential, execution.Binding?.Credential);
        Assert.NotNull(execution.ExecutionLease);
        Assert.Equal(expectedFingerprint, execution.Binding?.ExecutionFingerprint);
        Assert.Equal(fixture.Resolution.AggregateHash, execution.Binding?.FrozenBindingHash);
        Assert.Equal(fixture.Binding.BindingHash, execution.Binding?.LocalBindingHash);
        Assert.Equal(1, credentialReads);
        Assert.Equal(3, connections.ProfileReads);
        Assert.Equal(3, readiness.Calls);

        execution.Binding!.Dispose();
        Assert.Throws<ObjectDisposedException>(() => executionCredential!.CreateAuthPlainCommand());
        await execution.ExecutionLease!.DisposeAsync();
    }

    [Fact]
    public async Task ExactContextHashAndExactSmtpKindAreRequiredBeforeCredentialRead()
    {
        var fixture = SmtpFixture();
        var readiness = new FakeReadiness(fixture.Resolution);
        var credentialReads = 0;
        var resolver = new DesktopSmtpExecutionResolver(
            new FakeCapabilities(fixture.Version),
            new FakeSmtpConnections(fixture.Profile),
            readiness,
            (_, _) =>
            {
                credentialReads++;
                return Task.FromResult<SmtpWireCredential?>(new("smtp-user", "smtp-password"));
            });
        var mismatchedContext = fixture.Context with { ConnectionBindingHash = Hash('f') };

        var mismatch = await resolver.ResolveAsync(
            mismatchedContext,
            "smtp",
            SmtpResolutionPurpose.Execution,
            CancellationToken.None);

        var wrongKindFixture = SmtpFixture(CapabilityConnectionKindPolicy.MailApi);
        var wrongKindReadiness = new FakeReadiness(wrongKindFixture.Resolution);
        var wrongKind = await new DesktopSmtpExecutionResolver(
                new FakeCapabilities(wrongKindFixture.Version),
                new FakeSmtpConnections(wrongKindFixture.Profile),
                wrongKindReadiness,
                (_, _) => throw new InvalidOperationException("must not read"))
            .ResolveAsync(
                wrongKindFixture.Context,
                "smtp",
                SmtpResolutionPurpose.Execution,
                CancellationToken.None);

        Assert.False(mismatch.Success);
        Assert.Equal("mail.smtp.binding_mismatch", mismatch.ErrorCode);
        Assert.Equal(0, credentialReads);
        Assert.False(wrongKind.Success);
        Assert.Equal("mail.smtp.binding_unavailable", wrongKind.ErrorCode);
        Assert.Equal(0, wrongKindReadiness.Calls);
    }

    [Fact]
    public async Task CredentialRotationDuringFreshReadFailsStaleAndDisposesCredentialAndLease()
    {
        var fixture = SmtpFixture();
        var changed = fixture.Profile with
        {
            Revision = fixture.Profile.Revision + 1,
            CredentialRevision = fixture.Profile.CredentialRevision + 1,
            UpdatedAtUtc = fixture.Profile.UpdatedAtUtc.AddSeconds(1),
        };
        var connections = new FakeSmtpConnections(fixture.Profile, changed);
        var readiness = new FakeReadiness(fixture.Resolution);
        var executionLeases = new ConnectionExecutionLeaseCoordinator();
        var credential = new SmtpWireCredential("rotating-user", "rotating-password");
        var resolver = new DesktopSmtpExecutionResolver(
            new FakeCapabilities(fixture.Version),
            connections,
            readiness,
            (_, _) => Task.FromResult<SmtpWireCredential?>(credential),
            executionLeases);

        var result = await resolver.ResolveAsync(
            fixture.Context,
            "smtp",
            SmtpResolutionPurpose.Execution,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("mail.smtp.binding_mismatch", result.ErrorCode);
        Assert.Throws<ObjectDisposedException>(() => credential.CreateAuthPlainCommand());
        await using var released = await executionLeases.AcquireAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CredentialFailuresAndRuntimeDiagnosticsNeverExposeRawValues()
    {
        const string providerCanary = "RAW-SMTP-CREDENTIAL-PROVIDER-CANARY";
        const string username = "RAW-SMTP-RUNTIME-USER-CANARY";
        const string password = "RAW-SMTP-RUNTIME-PASSWORD-CANARY";
        var fixture = SmtpFixture();
        var failed = await new DesktopSmtpExecutionResolver(
                new FakeCapabilities(fixture.Version),
                new FakeSmtpConnections(fixture.Profile),
                new FakeReadiness(fixture.Resolution),
                (_, _) => throw new InvalidOperationException(providerCanary))
            .ResolveAsync(
                fixture.Context,
                "smtp",
                SmtpResolutionPurpose.Execution,
                CancellationToken.None);
        var successful = await new DesktopSmtpExecutionResolver(
                new FakeCapabilities(fixture.Version),
                new FakeSmtpConnections(fixture.Profile),
                new FakeReadiness(fixture.Resolution),
                (_, _) => Task.FromResult<SmtpWireCredential?>(new(username, password)))
            .ResolveAsync(
                fixture.Context,
                "smtp",
                SmtpResolutionPurpose.Execution,
                CancellationToken.None);

        Assert.False(failed.Success);
        Assert.Equal("mail.smtp.credential_unavailable", failed.ErrorCode);
        Assert.DoesNotContain(providerCanary, failed.ToString(), StringComparison.Ordinal);
        Assert.True(successful.Success);
        var diagnostics = successful + successful.Binding!.ToString() +
                          successful.Binding.Credential;
        Assert.DoesNotContain(username, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(password, diagnostics, StringComparison.Ordinal);
        Assert.Empty(typeof(SmtpWireCredential).GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));
        successful.Binding.Dispose();
        await successful.ExecutionLease!.DisposeAsync();
    }

    [Fact]
    public async Task SharedCoordinatorSerializesSmtpExecutionAgainstMailGatewayExecution()
    {
        var smtp = SmtpFixture();
        var mail = MailFixture();
        var executionLeases = new ConnectionExecutionLeaseCoordinator();
        var smtpReadiness = new FakeReadiness(smtp.Resolution);
        var mailReadiness = new FakeReadiness(mail.Resolution);
        var smtpResolver = new DesktopSmtpExecutionResolver(
            new FakeCapabilities(smtp.Version),
            new FakeSmtpConnections(smtp.Profile),
            smtpReadiness,
            (_, _) => Task.FromResult<SmtpWireCredential?>(new("smtp-user", "smtp-password")),
            executionLeases);
        var mailResolver = new DesktopMailGatewayExecutionResolver(
            new FakeCapabilities(mail.Version),
            new FakeMailConnections(mail.Profile),
            mailReadiness,
            (_, kind, _) => Task.FromResult<string?>(
                kind == ConnectionCredentialKind.MailGatewayBearerToken
                    ? "mail-gateway-token"
                    : null),
            executionLeases);

        var smtpExecution = await smtpResolver.ResolveAsync(
            smtp.Context,
            "smtp",
            SmtpResolutionPurpose.Execution,
            CancellationToken.None);
        var pendingMail = mailResolver.ResolveAsync(
            mail.Context,
            "mail",
            MailGatewayResolutionPurpose.Execution,
            CancellationToken.None);

        Assert.True(smtpExecution.Success);
        Assert.False(pendingMail.IsCompleted);
        Assert.Equal(0, mailReadiness.Calls);
        smtpExecution.Binding!.Dispose();
        await smtpExecution.ExecutionLease!.DisposeAsync();

        var mailExecution = await pendingMail;
        Assert.True(mailExecution.Success);
        Assert.Equal(2, mailReadiness.Calls);
        await mailExecution.ExecutionLease!.DisposeAsync();
    }

    private static SmtpFixtureData SmtpFixture(string kind = CapabilityConnectionKindPolicy.Smtp)
    {
        var requirement = new CapabilityConnectionRequirement("smtp", kind);
        var manifest = Manifest("company.fixture.smtp", requirement);
        var payload = Payload(manifest);
        var exact = new CapabilityVersionRef(
            manifest.Id,
            manifest.Version,
            CapabilityContentHasher.Compute(payload));
        var version = Version(exact, payload);
        const string host = "smtp.internal.example";
        const string sender = "sender@example.test";
        var targetHash = SmtpConnectionProfilePolicy.ComputeTargetHash(
            host,
            587,
            SmtpTlsMode.StartTlsRequired,
            SmtpAuthenticationMechanism.Plain,
            SmtpCertificatePolicy.SystemTrust,
            sender);
        var now = DateTimeOffset.Parse("2026-08-02T00:00:00+00:00");
        var profile = new SmtpConnectionProfile(
            ConnectionProfileId.Parse("fixture-smtp"),
            "Fixture SMTP",
            host,
            587,
            SmtpTlsMode.StartTlsRequired,
            SmtpAuthenticationMechanism.Plain,
            SmtpCertificatePolicy.SystemTrust,
            sender,
            true,
            ConnectionProfileLifecycleState.Stable,
            3,
            2,
            4,
            targetHash,
            null,
            now,
            now);
        var binding = new ResolvedConnectionBinding(
            exact,
            requirement.Slot,
            requirement.Kind,
            profile.Id,
            5,
            Hash('b'),
            profile.TargetRevision,
            profile.CredentialRevision,
            profile.TargetHash,
            true,
            null);
        var resolution = Resolution(exact, [binding]);
        return new SmtpFixtureData(
            version,
            profile,
            resolution,
            Context(exact, resolution.AggregateHash),
            binding);
    }

    private static MailFixtureData MailFixture()
    {
        var requirement = new CapabilityConnectionRequirement(
            "mail",
            CapabilityConnectionKindPolicy.MailApi);
        var manifest = Manifest("company.fixture.mail-lease", requirement);
        var payload = Payload(manifest);
        var exact = new CapabilityVersionRef(
            manifest.Id,
            manifest.Version,
            CapabilityContentHasher.Compute(payload));
        var version = Version(exact, payload);
        const string baseUrl = "https://mail.internal.example/";
        const string sender = "sender@example.test";
        var targetHash = MailGatewayConnectionProfilePolicy.ComputeTargetHash(baseUrl, sender);
        var now = DateTimeOffset.Parse("2026-08-02T00:00:00+00:00");
        var profile = new MailGatewayConnectionProfile(
            ConnectionProfileId.Parse("fixture-mail-lease"),
            "Fixture Mail",
            baseUrl,
            sender,
            true,
            ConnectionProfileLifecycleState.Stable,
            3,
            2,
            4,
            targetHash,
            null,
            now,
            now);
        var binding = new ResolvedConnectionBinding(
            exact,
            requirement.Slot,
            requirement.Kind,
            profile.Id,
            5,
            Hash('c'),
            profile.TargetRevision,
            profile.CredentialRevision,
            profile.TargetHash,
            true,
            null);
        var resolution = Resolution(exact, [binding]);
        return new MailFixtureData(
            version,
            profile,
            resolution,
            Context(exact, resolution.AggregateHash));
    }

    private static CapabilityManifest Manifest(
        string id,
        CapabilityConnectionRequirement requirement) => new(
        1,
        CapabilityId.Parse(id),
        CapabilitySemanticVersion.Parse("1.0.0"),
        "Mail fixture",
        "Sends through one exact connection.",
        CapabilityKind.Workflow,
        ["send fixture"],
        ["mail.send.v1"],
        [requirement],
        "R3",
        "workflow.json");

    private static CapabilityVersionPayload Payload(CapabilityManifest manifest) => new(
        manifest,
        "{}",
        "Run the immutable workflow.",
        "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
        CanonicalWorkflowJson: "{}",
        CanonicalTestsJson: "[]");

    private static CapabilityVersion Version(
        CapabilityVersionRef exact,
        CapabilityVersionPayload payload) => new(
        exact,
        null,
        payload,
        "local_import",
        CapabilityContentHash.Parse(Hash('e')),
        DateTimeOffset.Parse("2026-08-02T00:00:00+00:00"));

    private static CapabilityInvocationContext Context(
        CapabilityVersionRef exact,
        string bindingHash) => new(
        Hash('a'),
        CapabilityRuntimeHasher.ComputeModelToolName(exact),
        exact.Id.Value,
        exact.Version.Value,
        exact.ContentHash.Value,
        1,
        bindingHash);

    private static ConnectionReadinessResolution Resolution(
        CapabilityVersionRef exact,
        IReadOnlyList<ResolvedConnectionBinding> bindings)
    {
        var frozen = Array.AsReadOnly(bindings.OrderBy(item => item.Slot, StringComparer.Ordinal).ToArray());
        return new ConnectionReadinessResolution(
            frozen.All(item => item.IsReady),
            ConnectionReadinessHasher.Compute(exact, frozen),
            frozen);
    }

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    private sealed record SmtpFixtureData(
        CapabilityVersion Version,
        SmtpConnectionProfile Profile,
        ConnectionReadinessResolution Resolution,
        CapabilityInvocationContext Context,
        ResolvedConnectionBinding Binding);

    private sealed record MailFixtureData(
        CapabilityVersion Version,
        MailGatewayConnectionProfile Profile,
        ConnectionReadinessResolution Resolution,
        CapabilityInvocationContext Context);

    private sealed class FakeCapabilities(params CapabilityVersion[] versions)
        : ICapabilityCatalogRepository
    {
        public Task<CapabilityVersion?> GetVersionAsync(
            CapabilityVersionRef exactVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<CapabilityVersion?>(versions.SingleOrDefault(item =>
                item.ExactVersion.Equals(exactVersion)));

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

    private sealed class FakeSmtpConnections(params SmtpConnectionProfile[] profiles)
        : ISmtpConnectionRepository
    {
        private readonly IReadOnlyList<SmtpConnectionProfile> values = profiles;
        public int ProfileReads { get; private set; }

        public Task<SmtpConnectionProfile?> GetProfileAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken)
        {
            var index = Math.Min(ProfileReads, values.Count - 1);
            ProfileReads++;
            return Task.FromResult<SmtpConnectionProfile?>(
                index >= 0 && values[index].Id.Equals(id) ? values[index] : null);
        }

        public Task<IReadOnlyList<SmtpConnectionProfile>> ListProfilesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> TrySaveProfileAsync(SmtpConnectionProfile profile, long expectedRevision, CancellationToken cancellationToken) =>
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

    private sealed class FakeMailConnections(MailGatewayConnectionProfile profile)
        : IMailGatewayConnectionRepository
    {
        public Task<MailGatewayConnectionProfile?> GetProfileAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) =>
            Task.FromResult<MailGatewayConnectionProfile?>(profile.Id.Equals(id) ? profile : null);

        public Task<IReadOnlyList<MailGatewayConnectionProfile>> ListProfilesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> TrySaveProfileAsync(MailGatewayConnectionProfile item, long expectedRevision, CancellationToken cancellationToken) =>
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

    private sealed class FakeReadiness(ConnectionReadinessResolution resolution)
        : ICapabilityConnectionReadinessResolver
    {
        public int Calls { get; private set; }

        public Task<ConnectionReadinessResolution> ResolveAsync(
            CapabilityVersionRef exactVersion,
            IReadOnlyList<CapabilityConnectionRequirement> requirements,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(resolution);
        }
    }
}
