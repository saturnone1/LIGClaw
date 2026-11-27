using System.Text.Json;
using LIGClaw.Application.Connections;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class ConnectionManagementServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1);

    [Fact]
    public async Task NewBearerProfileStagesCredentialMutationAndStabilizesWithoutPersistingTheSecret()
    {
        const string canary = "D4-secret-canary-value";
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var service = Service(repository, credentials);
        var id = ConnectionProfileId.Parse("mail-prod");

        var result = await service.SaveHttpProfileAsync(
            new(id, "Mail production", "HTTPS://MAIL.EXAMPLE.TEST/api", ConnectionAuthenticationKind.Bearer, true),
            expectedRevision: 0,
            ConnectionSecretMutation.Replace,
            canary,
            CancellationToken.None);

        Assert.Equal(ConnectionManagementOutcome.Succeeded, result.Outcome);
        Assert.Equal(ConnectionProfileLifecycleState.Stable, result.Profile?.State);
        Assert.Equal(3, result.Profile?.Revision);
        Assert.Equal(1, result.Profile?.TargetRevision);
        Assert.Equal(1, result.Profile?.CredentialRevision);
        Assert.Equal("https://mail.example.test/api/", result.Profile?.BaseUrl);
        Assert.Equal(
            [ConnectionProfileLifecycleState.Staged, ConnectionProfileLifecycleState.Updating, ConnectionProfileLifecycleState.Stable],
            repository.ProfileWrites.Select(item => item.State));
        Assert.Equal(canary, credentials.Values[(id, ConnectionCredentialKind.BearerToken)]);
        Assert.DoesNotContain(canary, JsonSerializer.Serialize(repository.ProfileWrites), StringComparison.Ordinal);
        Assert.DoesNotContain(canary, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("token with whitespace")]
    [InlineData("token=followed-by-data")]
    [InlineData("token:colon")]
    [InlineData("short")]
    public async Task InvalidBearerGrammarIsRejectedBeforeProfileOrCredentialWrites(string secret)
    {
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var service = Service(repository, credentials);

        var result = await service.SaveHttpProfileAsync(
            new(
                ConnectionProfileId.Parse("invalid-token"),
                "Invalid token",
                "https://invalid.example.test/",
                ConnectionAuthenticationKind.Bearer,
                true),
            0,
            ConnectionSecretMutation.Replace,
            secret,
            CancellationToken.None);

        Assert.Equal(ConnectionManagementOutcome.Invalid, result.Outcome);
        Assert.Empty(repository.ProfileWrites);
        Assert.Empty(credentials.Values);
    }

    [Fact]
    public async Task DisplayRenamePreservesTargetCredentialAndProbeButEndpointAndTokenChangesInvalidateIt()
    {
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var service = Service(repository, credentials);
        var id = ConnectionProfileId.Parse("internal-api");
        var created = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Internal", "https://internal.example.test/", ConnectionAuthenticationKind.Bearer, true),
            0,
            ConnectionSecretMutation.Replace,
            "token-one",
            CancellationToken.None)).Profile);
        Assert.Equal(ConnectionManagementOutcome.Succeeded, (await service.RecordProbeAsync(
            id,
            succeeded: true,
            fixedErrorCode: null,
            CancellationToken.None)).Outcome);

        var renamed = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Renamed", created.BaseUrl, created.Authentication, true),
            created.Revision,
            ConnectionSecretMutation.Keep,
            secret: null,
            CancellationToken.None)).Profile);

        Assert.Equal(created.TargetRevision, renamed.TargetRevision);
        Assert.Equal(created.CredentialRevision, renamed.CredentialRevision);
        Assert.Equal(created.TargetHash, renamed.TargetHash);
        Assert.NotNull(repository.Probes.GetValueOrDefault(id));

        var endpointChanged = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Renamed", "https://new.example.test/", renamed.Authentication, true),
            renamed.Revision,
            ConnectionSecretMutation.Keep,
            secret: null,
            CancellationToken.None)).Profile);

        Assert.Equal(renamed.TargetRevision + 1, endpointChanged.TargetRevision);
        Assert.Equal(renamed.CredentialRevision, endpointChanged.CredentialRevision);
        Assert.Null(repository.Probes.GetValueOrDefault(id));
        Assert.Equal(ConnectionManagementOutcome.Succeeded, (await service.RecordProbeAsync(
            id,
            succeeded: true,
            fixedErrorCode: null,
            CancellationToken.None)).Outcome);

        var tokenChanged = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Renamed", endpointChanged.BaseUrl, endpointChanged.Authentication, true),
            endpointChanged.Revision,
            ConnectionSecretMutation.Replace,
            "token-two",
            CancellationToken.None)).Profile);

        Assert.Equal(endpointChanged.TargetRevision, tokenChanged.TargetRevision);
        Assert.Equal(endpointChanged.CredentialRevision + 1, tokenChanged.CredentialRevision);
        Assert.Null(repository.Probes.GetValueOrDefault(id));
    }

    [Fact]
    public async Task CredentialFailureLeavesTheProfileInAFixedErrorState()
    {
        var repository = new FakeRepository();
        var credentials = new FakeCredentials { FailWrites = true };
        var service = Service(repository, credentials);

        var result = await service.SaveHttpProfileAsync(
            new(
                ConnectionProfileId.Parse("broken-api"),
                "Broken",
                "https://broken.example.test/",
                ConnectionAuthenticationKind.Bearer,
                true),
            0,
            ConnectionSecretMutation.Replace,
            "do-not-persist-this",
            CancellationToken.None);

        Assert.Equal(ConnectionManagementOutcome.CredentialFailure, result.Outcome);
        Assert.Equal(ConnectionProfileLifecycleState.Error, result.Profile?.State);
        Assert.Equal("connection.credential.write_failed", result.Profile?.FixedErrorCode);
        Assert.Equal(ConnectionProfileLifecycleState.Error, repository.ProfileWrites[^1].State);
    }

    [Fact]
    public async Task FailedReplaceCannotBeForgottenByKeepingTheOldCredential()
    {
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var service = Service(repository, credentials);
        var id = ConnectionProfileId.Parse("replace-recovery");
        var stable = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Replace recovery", "https://replace.example.test/", ConnectionAuthenticationKind.Bearer, true),
            0,
            ConnectionSecretMutation.Replace,
            "old-token",
            CancellationToken.None)).Profile);
        credentials.FailWrites = true;
        var failed = await service.SaveHttpProfileAsync(
            new(id, "Replace recovery", stable.BaseUrl, stable.Authentication, true),
            stable.Revision,
            ConnectionSecretMutation.Replace,
            "new-token",
            CancellationToken.None);
        var errored = Assert.IsType<HttpConnectionProfile>(failed.Profile);
        credentials.FailWrites = false;

        var keep = await service.SaveHttpProfileAsync(
            new(id, "Replace recovery", errored.BaseUrl, errored.Authentication, true),
            errored.Revision,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None);

        Assert.Equal(ConnectionManagementOutcome.CredentialFailure, keep.Outcome);
        Assert.Equal("connection.credential.retry_required", keep.ErrorCode);
        Assert.Equal(ConnectionProfileLifecycleState.Error, repository.Profiles[id].State);
        Assert.Equal("old-token", credentials.Values[(id, ConnectionCredentialKind.BearerToken)]);

        var recovered = await service.SaveHttpProfileAsync(
            new(id, "Replace recovery", errored.BaseUrl, errored.Authentication, true),
            errored.Revision,
            ConnectionSecretMutation.Replace,
            "new-token",
            CancellationToken.None);
        Assert.Equal(ConnectionManagementOutcome.Succeeded, recovered.Outcome);
        Assert.Equal("new-token", credentials.Values[(id, ConnectionCredentialKind.BearerToken)]);
    }

    [Fact]
    public async Task FailedDeleteIsRetriedUntilTheCredentialIsActuallyAbsent()
    {
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var service = Service(repository, credentials);
        var id = ConnectionProfileId.Parse("delete-recovery");
        var stable = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Delete recovery", "https://delete.example.test/", ConnectionAuthenticationKind.Bearer, true),
            0,
            ConnectionSecretMutation.Replace,
            "old-token",
            CancellationToken.None)).Profile);
        credentials.FailDeletes = true;

        var failed = await service.SaveHttpProfileAsync(
            new(id, "Delete recovery", stable.BaseUrl, ConnectionAuthenticationKind.None, true),
            stable.Revision,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None);
        var errored = Assert.IsType<HttpConnectionProfile>(failed.Profile);
        var failedAgain = await service.SaveHttpProfileAsync(
            new(id, "Delete recovery", errored.BaseUrl, ConnectionAuthenticationKind.None, true),
            errored.Revision,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None);
        var erroredAgain = Assert.IsType<HttpConnectionProfile>(failedAgain.Profile);

        Assert.Equal(ConnectionManagementOutcome.CredentialFailure, failedAgain.Outcome);
        Assert.Equal(ConnectionProfileLifecycleState.Error, erroredAgain.State);
        Assert.Equal("old-token", credentials.Values[(id, ConnectionCredentialKind.BearerToken)]);
        Assert.Equal(2, credentials.DeleteCalls);

        credentials.FailDeletes = false;
        var recovered = await service.SaveHttpProfileAsync(
            new(id, "Delete recovery", erroredAgain.BaseUrl, ConnectionAuthenticationKind.None, true),
            erroredAgain.Revision,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None);

        Assert.Equal(ConnectionManagementOutcome.Succeeded, recovered.Outcome);
        Assert.Equal(ConnectionProfileLifecycleState.Stable, recovered.Profile?.State);
        Assert.False(credentials.Values.ContainsKey((id, ConnectionCredentialKind.BearerToken)));
        Assert.Equal(3, credentials.DeleteCalls);
    }

    [Fact]
    public async Task EnteringBearerCannotKeepAnOrphanedCredential()
    {
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var service = Service(repository, credentials);
        var id = ConnectionProfileId.Parse("orphan-recovery");
        var anonymous = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Orphan recovery", "https://orphan.example.test/", ConnectionAuthenticationKind.None, true),
            0,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None)).Profile);
        credentials.Values[(id, ConnectionCredentialKind.BearerToken)] = "orphan-token";

        var keep = await service.SaveHttpProfileAsync(
            new(id, "Orphan recovery", anonymous.BaseUrl, ConnectionAuthenticationKind.Bearer, true),
            anonymous.Revision,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None);

        Assert.Equal(ConnectionManagementOutcome.CredentialFailure, keep.Outcome);
        Assert.Equal("connection.credential.retry_required", keep.ErrorCode);
        Assert.Equal(anonymous, repository.Profiles[id]);
        Assert.Equal("orphan-token", credentials.Values[(id, ConnectionCredentialKind.BearerToken)]);

        var replaced = await service.SaveHttpProfileAsync(
            new(id, "Orphan recovery", anonymous.BaseUrl, ConnectionAuthenticationKind.Bearer, true),
            anonymous.Revision,
            ConnectionSecretMutation.Replace,
            "fresh-token",
            CancellationToken.None);
        Assert.Equal(ConnectionManagementOutcome.Succeeded, replaced.Outcome);
        Assert.Equal("fresh-token", credentials.Values[(id, ConnectionCredentialKind.BearerToken)]);
    }

    [Fact]
    public async Task ConcurrentCredentialMutationsAreSerializedAcrossTheDatabaseTransition()
    {
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var service = Service(repository, credentials);
        var id = ConnectionProfileId.Parse("serialized-mutation");
        var stable = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Serialized", "https://serialized.example.test/", ConnectionAuthenticationKind.Bearer, true),
            0,
            ConnectionSecretMutation.Replace,
            "initial-token",
            CancellationToken.None)).Profile);
        var replaceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        credentials.NextReplaceStarted = replaceStarted;
        credentials.ReleaseNextReplace = releaseReplace;

        var firstTask = service.SaveHttpProfileAsync(
            new(id, "Serialized", stable.BaseUrl, stable.Authentication, true),
            stable.Revision,
            ConnectionSecretMutation.Replace,
            "first-token",
            CancellationToken.None);
        await replaceStarted.Task;
        var inFlightRevision = repository.Profiles[id].Revision;
        var secondTask = service.SaveHttpProfileAsync(
            new(id, "Serialized", stable.BaseUrl, stable.Authentication, true),
            inFlightRevision,
            ConnectionSecretMutation.Replace,
            "second-token",
            CancellationToken.None);

        Assert.False(secondTask.IsCompleted);
        releaseReplace.SetResult();
        var first = await firstTask;
        var second = await secondTask;

        Assert.Equal(ConnectionManagementOutcome.Succeeded, first.Outcome);
        Assert.Equal(ConnectionManagementOutcome.Stale, second.Outcome);
        Assert.Equal(ConnectionProfileLifecycleState.Stable, repository.Profiles[id].State);
        Assert.Equal("first-token", credentials.Values[(id, ConnectionCredentialKind.BearerToken)]);
    }

    [Fact]
    public async Task ProfileMutationWaitsForTheSharedExecutionLease()
    {
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var leases = new ConnectionExecutionLeaseCoordinator();
        var service = Service(repository, credentials, executionLeases: leases);
        var id = ConnectionProfileId.Parse("leased-mutation");
        var stable = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Leased", "https://leased.example.test/", ConnectionAuthenticationKind.None, true),
            0,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None)).Profile);
        var executionLease = await leases.AcquireAsync(CancellationToken.None);

        var mutationTask = service.SaveHttpProfileAsync(
            new(id, "Leased", "https://changed.example.test/", stable.Authentication, true),
            stable.Revision,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None);

        Assert.False(mutationTask.IsCompleted);
        Assert.Equal(stable, repository.Profiles[id]);
        await executionLease.DisposeAsync();
        var mutation = await mutationTask;

        Assert.Equal(ConnectionManagementOutcome.Succeeded, mutation.Outcome);
        Assert.Equal("https://changed.example.test/", repository.Profiles[id].BaseUrl);
    }

    [Fact]
    public async Task BindingCapturesExactDraftAndProfileIdentityAndRenameIsANoOp()
    {
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var service = Service(repository, credentials);
        var id = ConnectionProfileId.Parse("bound-api");
        var exact = Exact("company.bound", 'f');
        var profile = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Bound", "https://bound.example.test/", ConnectionAuthenticationKind.None, true),
            0,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None)).Profile);

        var first = await service.BindAsync(exact, "primary", "http", id, 0, CancellationToken.None);
        Assert.Equal(ConnectionManagementOutcome.Succeeded, first.Outcome);
        Assert.Equal(exact, first.Binding?.ExactVersion);
        Assert.Equal(profile.TargetHash, first.Binding?.ProfileTargetHash);
        Assert.Equal(1, first.Binding?.Revision);

        var renamed = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Only renamed", profile.BaseUrl, profile.Authentication, true),
            profile.Revision,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None)).Profile);
        var rebound = await service.BindAsync(exact, "primary", "http", id, 1, CancellationToken.None);

        Assert.Equal(ConnectionManagementOutcome.Succeeded, rebound.Outcome);
        Assert.Equal(first.Binding, rebound.Binding);
        Assert.Equal(profile.TargetRevision, renamed.TargetRevision);
        Assert.Single(repository.BindingWrites);
    }

    [Fact]
    public async Task BindingGenerationIsOpaqueAcrossIndependentDesktopStores()
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 4; index++)
        {
            var repository = new FakeRepository();
            var service = Service(repository, new FakeCredentials());
            var id = ConnectionProfileId.Parse("opaque-api");
            var exact = Exact("company.opaque", 'd');
            _ = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
                new(id, "Opaque", "https://opaque.example.test/", ConnectionAuthenticationKind.None, true),
                0,
                ConnectionSecretMutation.Keep,
                null,
                CancellationToken.None)).Profile);

            var result = await service.BindAsync(
                exact,
                "primary",
                "http",
                id,
                0,
                CancellationToken.None);

            var binding = Assert.IsType<CapabilityConnectionBinding>(result.Binding);
            Assert.True(ConnectionBindingHashPolicy.IsValid(binding.BindingHash));
            hashes.Add(binding.BindingHash);
        }

        Assert.Equal(4, hashes.Count);
    }

    [Fact]
    public async Task DisableAndReenableInvalidateProbeAndRequireAReboundExactVersion()
    {
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var service = Service(repository, credentials);
        var id = ConnectionProfileId.Parse("toggle-api");
        var exact = Exact("company.toggle", 'e');
        var created = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Toggle", "https://toggle.example.test/", ConnectionAuthenticationKind.None, true),
            0,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None)).Profile);
        Assert.Equal(ConnectionManagementOutcome.Succeeded, (await service.RecordProbeAsync(
            id,
            true,
            null,
            CancellationToken.None)).Outcome);
        var firstBinding = Assert.IsType<CapabilityConnectionBinding>((await service.BindAsync(
            exact,
            "primary",
            "http",
            id,
            0,
            CancellationToken.None)).Binding);

        var disabled = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Toggle", created.BaseUrl, created.Authentication, false),
            created.Revision,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None)).Profile);
        Assert.Equal(created.TargetRevision + 1, disabled.TargetRevision);
        Assert.Empty(repository.Probes);

        var enabled = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Toggle", disabled.BaseUrl, disabled.Authentication, true),
            disabled.Revision,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None)).Profile);
        Assert.Equal(disabled.TargetRevision + 1, enabled.TargetRevision);
        Assert.Equal(ConnectionManagementOutcome.Succeeded, (await service.RecordProbeAsync(
            id,
            true,
            null,
            CancellationToken.None)).Outcome);

        var stale = await new ConnectionReadinessResolver(repository, credentials).ResolveAsync(
            exact,
            [new("primary", "http")],
            CancellationToken.None);
        Assert.False(stale.IsReady);
        Assert.Equal("connection.binding.stale", Assert.Single(stale.Bindings).ErrorCode);

        var rebound = Assert.IsType<CapabilityConnectionBinding>((await service.BindAsync(
            exact,
            "primary",
            "http",
            id,
            firstBinding.Revision,
            CancellationToken.None)).Binding);
        Assert.NotEqual(firstBinding.BindingHash, rebound.BindingHash);
        var ready = await new ConnectionReadinessResolver(repository, credentials).ResolveAsync(
            exact,
            [new("primary", "http")],
            CancellationToken.None);
        Assert.True(ready.IsReady);
    }

    [Fact]
    public async Task AuthenticationOnlyChangeInvalidatesProbeReadinessAndBindingIdentity()
    {
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var service = Service(repository, credentials);
        var id = ConnectionProfileId.Parse("auth-only-api");
        var exact = Exact("company.auth-only", 'a');
        var anonymous = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(
                id,
                "Auth only",
                "https://auth-only.example.test/",
                ConnectionAuthenticationKind.None,
                true),
            0,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None)).Profile);
        Assert.Equal(ConnectionManagementOutcome.Succeeded, (await service.RecordProbeAsync(
            id,
            true,
            null,
            CancellationToken.None)).Outcome);
        var anonymousBinding = Assert.IsType<CapabilityConnectionBinding>((await service.BindAsync(
            exact,
            "primary",
            "http",
            id,
            0,
            CancellationToken.None)).Binding);
        Assert.True((await new ConnectionReadinessResolver(repository, credentials).ResolveAsync(
            exact,
            [new("primary", "http")],
            CancellationToken.None)).IsReady);

        var bearer = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(
                id,
                anonymous.DisplayName,
                anonymous.BaseUrl,
                ConnectionAuthenticationKind.Bearer,
                true),
            anonymous.Revision,
            ConnectionSecretMutation.Replace,
            "auth-only-token",
            CancellationToken.None)).Profile);

        Assert.Equal(anonymous.BaseUrl, bearer.BaseUrl);
        Assert.Equal(anonymous.TargetRevision + 1, bearer.TargetRevision);
        Assert.Equal(anonymous.CredentialRevision + 1, bearer.CredentialRevision);
        Assert.NotEqual(anonymous.TargetHash, bearer.TargetHash);
        Assert.Empty(repository.Probes);
        var stale = await new ConnectionReadinessResolver(repository, credentials).ResolveAsync(
            exact,
            [new("primary", "http")],
            CancellationToken.None);
        Assert.False(stale.IsReady);
        Assert.Equal("connection.binding.stale", Assert.Single(stale.Bindings).ErrorCode);

        Assert.Equal(ConnectionManagementOutcome.Succeeded, (await service.RecordProbeAsync(
            id,
            true,
            null,
            CancellationToken.None)).Outcome);
        var rebound = Assert.IsType<CapabilityConnectionBinding>((await service.BindAsync(
            exact,
            "primary",
            "http",
            id,
            anonymousBinding.Revision,
            CancellationToken.None)).Binding);
        Assert.Equal(bearer.TargetRevision, rebound.ProfileTargetRevision);
        Assert.Equal(bearer.CredentialRevision, rebound.ProfileCredentialRevision);
        Assert.NotEqual(anonymousBinding.BindingHash, rebound.BindingHash);
        Assert.True((await new ConnectionReadinessResolver(repository, credentials).ResolveAsync(
            exact,
            [new("primary", "http")],
            CancellationToken.None)).IsReady);
    }

    [Fact]
    public async Task ProbeReceiptContainsOnlyCurrentRevisionIdentityAndFixedResult()
    {
        var repository = new FakeRepository();
        var service = Service(repository, new FakeCredentials());
        var id = ConnectionProfileId.Parse("probe-api");
        var profile = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Probe", "https://probe.example.test/", ConnectionAuthenticationKind.None, true),
            0,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None)).Profile);

        var result = await service.RecordProbeAsync(
            id,
            succeeded: false,
            "connection.probe.unreachable",
            CancellationToken.None);

        Assert.Equal(ConnectionManagementOutcome.Succeeded, result.Outcome);
        Assert.Equal(profile.TargetRevision, result.Receipt?.ProfileTargetRevision);
        Assert.Equal(profile.CredentialRevision, result.Receipt?.ProfileCredentialRevision);
        Assert.Equal(profile.TargetHash, result.Receipt?.ProfileTargetHash);
        Assert.Equal("connection.probe.unreachable", result.Receipt?.ErrorCode);
    }

    [Fact]
    public async Task ActualProbeRecordsOnlyItsFixedResultForTheCurrentProfileIdentity()
    {
        var repository = new FakeRepository();
        var probe = new FakeProbe(new ConnectionHttpProbeResult(
            false,
            "connection.probe.status_rejected"));
        var service = Service(repository, new FakeCredentials(), probe);
        var id = ConnectionProfileId.Parse("actual-probe");
        _ = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Actual probe", "https://probe.example.test/", ConnectionAuthenticationKind.None, true),
            0,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None)).Profile);

        var result = await service.ProbeHttpProfileAsync(id, CancellationToken.None);

        Assert.Equal(ConnectionManagementOutcome.Succeeded, result.Outcome);
        Assert.False(result.Receipt?.Succeeded);
        Assert.Equal("connection.probe.status_rejected", result.Receipt?.ErrorCode);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(result.Receipt, repository.Probes[id]);
    }

    [Fact]
    public async Task ActualProbeKeepsTheOldEndpointAndCredentialTogetherUntilMutationCanProceed()
    {
        var repository = new FakeRepository();
        var credentials = new FakeCredentials();
        var executionLeases = new ConnectionExecutionLeaseCoordinator();
        var id = ConnectionProfileId.Parse("leased-probe");
        var probe = new BlockingCredentialObservingProbe(credentials, id);
        var service = Service(repository, credentials, probe, executionLeases);
        var stable = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(
                id,
                "Leased probe",
                "https://old-probe.example.test/",
                ConnectionAuthenticationKind.Bearer,
                true),
            0,
            ConnectionSecretMutation.Replace,
            "old-probe-token",
            CancellationToken.None)).Profile);

        var probeTask = service.ProbeHttpProfileAsync(id, CancellationToken.None);
        await probe.Entered.Task;
        var mutationTask = service.SaveHttpProfileAsync(
            new(
                id,
                "Leased probe",
                "https://new-probe.example.test/",
                ConnectionAuthenticationKind.Bearer,
                true),
            stable.Revision,
            ConnectionSecretMutation.Replace,
            "new-probe-token",
            CancellationToken.None);

        Assert.False(mutationTask.IsCompleted);
        Assert.Equal(stable, repository.Profiles[id]);
        Assert.Equal("old-probe-token", probe.CredentialAtEntry);
        Assert.Equal("old-probe-token", credentials.Values[(id, ConnectionCredentialKind.BearerToken)]);

        probe.Release.SetResult();
        var probeResult = await probeTask;
        var mutation = await mutationTask;

        Assert.Equal(ConnectionManagementOutcome.Succeeded, probeResult.Outcome);
        Assert.Equal(stable.BaseUrl, probe.Profile?.BaseUrl);
        Assert.Equal("old-probe-token", probe.CredentialBeforeReturn);
        Assert.Equal(ConnectionManagementOutcome.Succeeded, mutation.Outcome);
        Assert.Equal("https://new-probe.example.test/", repository.Profiles[id].BaseUrl);
        Assert.Equal("new-probe-token", credentials.Values[(id, ConnectionCredentialKind.BearerToken)]);
        Assert.Empty(repository.Probes);
    }

    [Fact]
    public async Task ActualProbeDoesNotRecordAReceiptWhenTargetChangesInFlight()
    {
        var repository = new FakeRepository();
        var id = ConnectionProfileId.Parse("stale-probe");
        var probe = new FakeProbe(new ConnectionHttpProbeResult(true), profile =>
        {
            const string changedBaseUrl = "https://changed.example.test/";
            repository.Profiles[id] = profile with
            {
                BaseUrl = changedBaseUrl,
                Revision = profile.Revision + 1,
                TargetRevision = profile.TargetRevision + 1,
                TargetHash = HttpConnectionProfilePolicy.ComputeTargetHash(
                    changedBaseUrl,
                    profile.Authentication),
                UpdatedAtUtc = profile.UpdatedAtUtc.AddSeconds(1),
            };
        });
        var service = Service(repository, new FakeCredentials(), probe);
        _ = Assert.IsType<HttpConnectionProfile>((await service.SaveHttpProfileAsync(
            new(id, "Stale probe", "https://probe.example.test/", ConnectionAuthenticationKind.None, true),
            0,
            ConnectionSecretMutation.Keep,
            null,
            CancellationToken.None)).Profile);

        var result = await service.ProbeHttpProfileAsync(id, CancellationToken.None);

        Assert.Equal(ConnectionManagementOutcome.Stale, result.Outcome);
        Assert.Equal("connection.probe.stale", result.ErrorCode);
        Assert.Empty(repository.Probes);
    }

    private static ConnectionManagementService Service(
        FakeRepository repository,
        FakeCredentials credentials,
        IConnectionHttpProbe? probe = null,
        ConnectionExecutionLeaseCoordinator? executionLeases = null) =>
        new(repository, credentials, new FixedTimeProvider(Now), probe, executionLeases);

    private static CapabilityVersionRef Exact(string id, char hash) => new(
        CapabilityId.Parse(id),
        CapabilitySemanticVersion.Parse("1.0.0"),
        CapabilityContentHash.Parse($"sha256:{new string(hash, 64)}"));

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }

    private sealed class FakeRepository : IConnectionProfileRepository
    {
        public Dictionary<ConnectionProfileId, HttpConnectionProfile> Profiles { get; } = [];
        public Dictionary<ConnectionProfileId, ConnectionProbeReceipt> Probes { get; } = [];
        public Dictionary<(CapabilityVersionRef Exact, string Slot), CapabilityConnectionBinding> Bindings { get; } = [];
        public List<HttpConnectionProfile> ProfileWrites { get; } = [];
        public List<CapabilityConnectionBinding> BindingWrites { get; } = [];

        public Task<HttpConnectionProfile?> GetProfileAsync(ConnectionProfileId id, CancellationToken cancellationToken) =>
            Task.FromResult(Profiles.GetValueOrDefault(id));

        public Task<IReadOnlyList<HttpConnectionProfile>> ListProfilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HttpConnectionProfile>>(Profiles.Values.ToArray());

        public Task<bool> TrySaveProfileAsync(HttpConnectionProfile profile, long expectedRevision, CancellationToken cancellationToken)
        {
            var current = Profiles.GetValueOrDefault(profile.Id);
            if (current is null && expectedRevision != 0 || current is not null && current.Revision != expectedRevision)
                return Task.FromResult(false);
            Profiles[profile.Id] = profile;
            ProfileWrites.Add(profile);
            if (Probes.TryGetValue(profile.Id, out var probe) &&
                (probe.ProfileTargetRevision != profile.TargetRevision ||
                 probe.ProfileCredentialRevision != profile.CredentialRevision ||
                 !StringComparer.Ordinal.Equals(probe.ProfileTargetHash, profile.TargetHash)))
                Probes.Remove(profile.Id);
            return Task.FromResult(true);
        }

        public Task<ConnectionProbeReceipt?> GetProbeReceiptAsync(ConnectionProfileId id, CancellationToken cancellationToken) =>
            Task.FromResult(Probes.GetValueOrDefault(id));

        public Task SaveProbeReceiptAsync(ConnectionProbeReceipt receipt, CancellationToken cancellationToken)
        {
            var profile = Profiles.GetValueOrDefault(receipt.ProfileId);
            if (profile is null || profile.State != ConnectionProfileLifecycleState.Stable ||
                profile.TargetRevision != receipt.ProfileTargetRevision ||
                profile.CredentialRevision != receipt.ProfileCredentialRevision ||
                !StringComparer.Ordinal.Equals(profile.TargetHash, receipt.ProfileTargetHash))
                throw new InvalidOperationException();
            Probes[receipt.ProfileId] = receipt;
            return Task.CompletedTask;
        }

        public Task<CapabilityConnectionBinding?> GetBindingAsync(CapabilityVersionRef exactVersion, string slot, CancellationToken cancellationToken) =>
            Task.FromResult(Bindings.GetValueOrDefault((exactVersion, slot)));

        public Task<IReadOnlyList<CapabilityConnectionBinding>> GetBindingsAsync(CapabilityVersionRef exactVersion, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityConnectionBinding>>(Bindings.Values
                .Where(item => item.ExactVersion.Equals(exactVersion)).ToArray());

        public Task<bool> TrySaveBindingAsync(CapabilityConnectionBinding binding, long expectedRevision, CancellationToken cancellationToken)
        {
            var key = (binding.ExactVersion, binding.Slot);
            var current = Bindings.GetValueOrDefault(key);
            if (current is null && expectedRevision != 0 || current is not null && current.Revision != expectedRevision)
                return Task.FromResult(false);
            Bindings[key] = binding;
            BindingWrites.Add(binding);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeCredentials : IConnectionCredentialStore
    {
        public Dictionary<(ConnectionProfileId Id, ConnectionCredentialKind Kind), string> Values { get; } = [];
        public bool FailWrites { get; set; }
        public bool FailDeletes { get; set; }
        public int DeleteCalls { get; private set; }
        public TaskCompletionSource? NextReplaceStarted { get; set; }
        public TaskCompletionSource? ReleaseNextReplace { get; set; }

        public Task<bool> ExistsAsync(ConnectionProfileId profileId, ConnectionCredentialKind kind, CancellationToken cancellationToken) =>
            Task.FromResult(Values.ContainsKey((profileId, kind)));

        public async Task ReplaceAsync(ConnectionProfileId profileId, ConnectionCredentialKind kind, string secret, CancellationToken cancellationToken)
        {
            if (FailWrites) throw new InvalidOperationException("test failure with no production leak");
            if (NextReplaceStarted is { } started && ReleaseNextReplace is { } release)
            {
                NextReplaceStarted = null;
                ReleaseNextReplace = null;
                started.SetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            Values[(profileId, kind)] = secret;
        }

        public Task DeleteAsync(ConnectionProfileId profileId, ConnectionCredentialKind kind, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            if (FailDeletes) throw new InvalidOperationException("test failure with no production leak");
            Values.Remove((profileId, kind));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProbe(
        ConnectionHttpProbeResult result,
        Action<HttpConnectionProfile>? onProbe = null) : IConnectionHttpProbe
    {
        public int Calls { get; private set; }

        public Task<ConnectionHttpProbeResult> ProbeAsync(
            HttpConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            Calls++;
            onProbe?.Invoke(profile);
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingCredentialObservingProbe(
        FakeCredentials credentials,
        ConnectionProfileId profileId) : IConnectionHttpProbe
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpConnectionProfile? Profile { get; private set; }
        public string? CredentialAtEntry { get; private set; }
        public string? CredentialBeforeReturn { get; private set; }

        public async Task<ConnectionHttpProbeResult> ProbeAsync(
            HttpConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            Profile = profile;
            CredentialAtEntry = credentials.Values.GetValueOrDefault(
                (profileId, ConnectionCredentialKind.BearerToken));
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            CredentialBeforeReturn = credentials.Values.GetValueOrDefault(
                (profileId, ConnectionCredentialKind.BearerToken));
            return new ConnectionHttpProbeResult(true);
        }
    }
}
