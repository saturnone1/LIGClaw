using System.Security.Cryptography;
using LIGClaw.Application.Connections;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal enum ConnectionSecretMutation
{
    Keep,
    Replace,
    Delete,
}

internal enum ConnectionManagementOutcome
{
    Succeeded,
    Invalid,
    NotFound,
    NotReady,
    Stale,
    CredentialFailure,
    StoreFailure,
}

internal sealed record HttpConnectionProfileDraft(
    ConnectionProfileId Id,
    string DisplayName,
    string BaseUrl,
    ConnectionAuthenticationKind Authentication,
    bool Enabled);

internal sealed record ConnectionProfileMutationResult(
    ConnectionManagementOutcome Outcome,
    HttpConnectionProfile? Profile = null,
    string? ErrorCode = null);

internal sealed record ConnectionProbeRecordingResult(
    ConnectionManagementOutcome Outcome,
    ConnectionProbeReceipt? Receipt = null,
    string? ErrorCode = null);

internal sealed record ConnectionBindingMutationResult(
    ConnectionManagementOutcome Outcome,
    CapabilityConnectionBinding? Binding = null,
    string? ErrorCode = null);

internal sealed class ConnectionManagementService(
    IConnectionProfileRepository repository,
    IConnectionCredentialStore credentials,
    TimeProvider? timeProvider = null,
    IConnectionHttpProbe? httpProbe = null,
    ConnectionExecutionLeaseCoordinator? executionLeases = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConnectionExecutionLeaseCoordinator _executionLeases =
        executionLeases ?? new ConnectionExecutionLeaseCoordinator();

    public async Task<ConnectionProfileMutationResult> SaveHttpProfileAsync(
        HttpConnectionProfileDraft draft,
        long expectedRevision,
        ConnectionSecretMutation secretMutation,
        string? secret,
        CancellationToken cancellationToken)
    {
        if (!TryValidateDraft(draft, expectedRevision, secretMutation, secret, out var baseUrl))
            return InvalidProfile("connection.profile.invalid");

        await using var mutationLease =
            await _executionLeases.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await SaveHttpProfileCoreAsync(
            draft,
            expectedRevision,
            secretMutation,
            secret,
            baseUrl!,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConnectionProfileMutationResult> SaveHttpProfileCoreAsync(
        HttpConnectionProfileDraft draft,
        long expectedRevision,
        ConnectionSecretMutation secretMutation,
        string? secret,
        string baseUrl,
        CancellationToken cancellationToken)
    {

        HttpConnectionProfile? current;
        try
        {
            current = await repository.GetProfileAsync(draft.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return StoreFailedProfile();
        }

        if (current is null && expectedRevision != 0)
            return new(ConnectionManagementOutcome.NotFound, ErrorCode: "connection.profile.not_found");
        if (current is not null && current.Revision != expectedRevision)
            return new(ConnectionManagementOutcome.Stale, current, "connection.profile.stale");
        if (current is not null && !HttpConnectionProfilePolicy.IsValid(current))
            return new(ConnectionManagementOutcome.StoreFailure, ErrorCode: "connection.profile.corrupt");

        var effectiveSecretMutation = EffectiveSecretMutation(current, draft, secretMutation);
        var enteringBearer = draft.Authentication == ConnectionAuthenticationKind.Bearer &&
                             current?.Authentication != ConnectionAuthenticationKind.Bearer;
        if ((current is { State: not ConnectionProfileLifecycleState.Stable } || enteringBearer) &&
            effectiveSecretMutation == ConnectionSecretMutation.Keep)
            return new(
                ConnectionManagementOutcome.CredentialFailure,
                current,
                "connection.credential.retry_required");
        var targetChanged = current is null ||
                            !StringComparer.Ordinal.Equals(current.BaseUrl, baseUrl) ||
                            current.Authentication != draft.Authentication ||
                            current.Enabled != draft.Enabled;
        var credentialChanged = effectiveSecretMutation != ConnectionSecretMutation.Keep;
        var targetRevision = current is null ? 1 : current.TargetRevision + (targetChanged ? 1 : 0);
        var credentialRevision = current?.CredentialRevision ?? 0;
        if (credentialChanged) credentialRevision++;
        var targetHash = HttpConnectionProfilePolicy.ComputeTargetHash(baseUrl, draft.Authentication);
        var nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var createdAtUtc = current?.CreatedAtUtc ?? nowUtc;
        var stagedOrCurrent = current;

        if (current is null)
        {
            var staged = CreateProfile(
                draft,
                baseUrl,
                ConnectionProfileLifecycleState.Staged,
                revision: 1,
                targetRevision,
                credentialRevision,
                targetHash,
                fixedErrorCode: null,
                createdAtUtc,
                nowUtc);
            if (!await TrySaveProfileAsync(staged, 0, cancellationToken).ConfigureAwait(false))
                return new(ConnectionManagementOutcome.Stale, ErrorCode: "connection.profile.stale");
            stagedOrCurrent = staged;
        }

        var updating = CreateProfile(
            draft,
            baseUrl,
            ConnectionProfileLifecycleState.Updating,
            stagedOrCurrent!.Revision + 1,
            targetRevision,
            credentialRevision,
            targetHash,
            fixedErrorCode: null,
            createdAtUtc,
            nowUtc);
        if (!await TrySaveProfileAsync(
                updating,
                stagedOrCurrent.Revision,
                cancellationToken).ConfigureAwait(false))
            return new(ConnectionManagementOutcome.Stale, stagedOrCurrent, "connection.profile.stale");

        try
        {
            await ApplyCredentialMutationAsync(
                draft.Id,
                effectiveSecretMutation,
                secret,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            var errored = updating with
            {
                State = ConnectionProfileLifecycleState.Error,
                Revision = updating.Revision + 1,
                FixedErrorCode = "connection.credential.write_failed",
                UpdatedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime(),
            };
            var saved = await TrySaveProfileAsync(
                errored,
                updating.Revision,
                cancellationToken).ConfigureAwait(false);
            return new(
                ConnectionManagementOutcome.CredentialFailure,
                saved ? errored : updating,
                "connection.credential.write_failed");
        }

        var stable = updating with
        {
            State = ConnectionProfileLifecycleState.Stable,
            Revision = updating.Revision + 1,
            UpdatedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime(),
        };
        if (!await TrySaveProfileAsync(stable, updating.Revision, cancellationToken).ConfigureAwait(false))
            return new(ConnectionManagementOutcome.Stale, updating, "connection.profile.stale");
        return new(ConnectionManagementOutcome.Succeeded, stable);
    }

    public async Task<ConnectionProbeRecordingResult> RecordProbeAsync(
        ConnectionProfileId profileId,
        bool succeeded,
        string? fixedErrorCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        if (succeeded == (fixedErrorCode is not null) ||
            fixedErrorCode is not null && !IsFixedErrorCode(fixedErrorCode))
            return new(ConnectionManagementOutcome.Invalid, ErrorCode: "connection.probe.invalid");

        await using var mutationLease =
            await _executionLeases.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await RecordProbeCoreAsync(
            profileId,
            succeeded,
            fixedErrorCode,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConnectionProbeRecordingResult> RecordProbeCoreAsync(
        ConnectionProfileId profileId,
        bool succeeded,
        string? fixedErrorCode,
        CancellationToken cancellationToken)
    {

        HttpConnectionProfile? profile;
        try
        {
            profile = await repository.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(ConnectionManagementOutcome.StoreFailure, ErrorCode: "connection.store.unavailable");
        }

        if (profile is null)
            return new(ConnectionManagementOutcome.NotFound, ErrorCode: "connection.profile.not_found");
        if (!profile.Enabled || profile.State != ConnectionProfileLifecycleState.Stable ||
            !HttpConnectionProfilePolicy.IsValid(profile))
            return new(ConnectionManagementOutcome.NotReady, ErrorCode: "connection.profile.not_ready");

        var receipt = new ConnectionProbeReceipt(
            profile.Id,
            profile.TargetRevision,
            profile.CredentialRevision,
            profile.TargetHash,
            succeeded,
            fixedErrorCode,
            _timeProvider.GetUtcNow().ToUniversalTime());
        try
        {
            await repository.SaveProbeReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
            return new(ConnectionManagementOutcome.Succeeded, receipt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            return new(ConnectionManagementOutcome.Stale, ErrorCode: "connection.probe.stale");
        }
        catch
        {
            return new(ConnectionManagementOutcome.StoreFailure, ErrorCode: "connection.store.unavailable");
        }
    }

    public async Task<ConnectionProbeRecordingResult> ProbeHttpProfileAsync(
        ConnectionProfileId profileId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        if (httpProbe is null)
            return new(
                ConnectionManagementOutcome.StoreFailure,
                ErrorCode: "connection.probe.unavailable");

        await using var probeLease =
            await _executionLeases.AcquireAsync(cancellationToken).ConfigureAwait(false);
        HttpConnectionProfile? before;
        try
        {
            before = await repository.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(ConnectionManagementOutcome.StoreFailure, ErrorCode: "connection.store.unavailable");
        }
        if (before is null)
            return new(ConnectionManagementOutcome.NotFound, ErrorCode: "connection.profile.not_found");
        if (!before.Enabled || before.State != ConnectionProfileLifecycleState.Stable ||
            !HttpConnectionProfilePolicy.IsValid(before))
            return new(ConnectionManagementOutcome.NotReady, ErrorCode: "connection.profile.not_ready");

        ConnectionHttpProbeResult probe;
        try
        {
            probe = await httpProbe.ProbeAsync(before, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            probe = new ConnectionHttpProbeResult(false, "connection.probe.transport_failed");
        }
        if (probe.Succeeded == (probe.ErrorCode is not null) ||
            probe.ErrorCode is not null && !IsFixedErrorCode(probe.ErrorCode))
            return new(ConnectionManagementOutcome.StoreFailure, ErrorCode: "connection.probe.invalid_result");

        HttpConnectionProfile? after;
        try
        {
            after = await repository.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(ConnectionManagementOutcome.StoreFailure, ErrorCode: "connection.store.unavailable");
        }
        if (!SameProbeIdentity(before, after))
            return new(ConnectionManagementOutcome.Stale, ErrorCode: "connection.probe.stale");

        return await RecordProbeCoreAsync(
            profileId,
            probe.Succeeded,
            probe.ErrorCode,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConnectionBindingMutationResult> BindAsync(
        CapabilityVersionRef exactVersion,
        string slot,
        string kind,
        ConnectionProfileId profileId,
        long expectedBindingRevision,
        CancellationToken cancellationToken)
    {
        if (exactVersion is null || profileId is null || expectedBindingRevision < 0 ||
            kind != "http" || !IsName(slot, 64))
            return new(ConnectionManagementOutcome.Invalid, ErrorCode: "connection.binding.invalid");

        await using var mutationLease =
            await _executionLeases.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await BindCoreAsync(
            exactVersion,
            slot,
            kind,
            profileId,
            expectedBindingRevision,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConnectionBindingMutationResult> BindCoreAsync(
        CapabilityVersionRef exactVersion,
        string slot,
        string kind,
        ConnectionProfileId profileId,
        long expectedBindingRevision,
        CancellationToken cancellationToken)
    {

        HttpConnectionProfile? profile;
        CapabilityConnectionBinding? current;
        try
        {
            profile = await repository.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
            current = await repository.GetBindingAsync(exactVersion, slot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(ConnectionManagementOutcome.StoreFailure, ErrorCode: "connection.store.unavailable");
        }

        if (profile is null)
            return new(ConnectionManagementOutcome.NotFound, ErrorCode: "connection.profile.not_found");
        if (profile.State != ConnectionProfileLifecycleState.Stable ||
            !HttpConnectionProfilePolicy.IsValid(profile))
            return new(ConnectionManagementOutcome.NotReady, ErrorCode: "connection.profile.not_ready");
        if (current is null && expectedBindingRevision != 0 ||
            current is not null && current.Revision != expectedBindingRevision)
            return new(ConnectionManagementOutcome.Stale, current, "connection.binding.stale");

        if (current is not null && current.Revision > 0 && current.Kind == kind &&
            current.ProfileId.Equals(profile.Id) &&
            current.ProfileTargetRevision == profile.TargetRevision &&
            current.ProfileCredentialRevision == profile.CredentialRevision &&
            StringComparer.Ordinal.Equals(current.ProfileTargetHash, profile.TargetHash) &&
            ConnectionBindingHashPolicy.IsValid(current.BindingHash))
            return new(ConnectionManagementOutcome.Succeeded, current);

        var nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var revision = expectedBindingRevision + 1;
        var bindingHash = CreateOpaqueBindingHash();
        var binding = new CapabilityConnectionBinding(
            exactVersion,
            slot,
            kind,
            profile.Id,
            revision,
            profile.TargetRevision,
            profile.CredentialRevision,
            profile.TargetHash,
            bindingHash,
            current?.CreatedAtUtc ?? nowUtc,
            nowUtc);
        try
        {
            if (!await repository.TrySaveBindingAsync(
                    binding,
                    expectedBindingRevision,
                    cancellationToken).ConfigureAwait(false))
                return new(ConnectionManagementOutcome.Stale, current, "connection.binding.stale");
            return new(ConnectionManagementOutcome.Succeeded, binding);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(ConnectionManagementOutcome.StoreFailure, ErrorCode: "connection.store.unavailable");
        }
    }

    private async Task<bool> TrySaveProfileAsync(
        HttpConnectionProfile profile,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        try
        {
            return await repository.TrySaveProfileAsync(
                profile,
                expectedRevision,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task ApplyCredentialMutationAsync(
        ConnectionProfileId profileId,
        ConnectionSecretMutation mutation,
        string? secret,
        CancellationToken cancellationToken)
    {
        switch (mutation)
        {
            case ConnectionSecretMutation.Keep:
                return;
            case ConnectionSecretMutation.Replace:
                await credentials.ReplaceAsync(
                    profileId,
                    ConnectionCredentialKind.BearerToken,
                    secret!,
                    cancellationToken).ConfigureAwait(false);
                return;
            case ConnectionSecretMutation.Delete:
                await credentials.DeleteAsync(
                    profileId,
                    ConnectionCredentialKind.BearerToken,
                    cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    private static ConnectionSecretMutation EffectiveSecretMutation(
        HttpConnectionProfile? current,
        HttpConnectionProfileDraft draft,
        ConnectionSecretMutation requested)
    {
        if (draft.Authentication == ConnectionAuthenticationKind.None &&
            (current?.Authentication == ConnectionAuthenticationKind.Bearer ||
             current is { State: not ConnectionProfileLifecycleState.Stable }))
            return ConnectionSecretMutation.Delete;
        return requested;
    }

    private static HttpConnectionProfile CreateProfile(
        HttpConnectionProfileDraft draft,
        string baseUrl,
        ConnectionProfileLifecycleState state,
        long revision,
        long targetRevision,
        long credentialRevision,
        string targetHash,
        string? fixedErrorCode,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc) => new(
        draft.Id,
        draft.DisplayName,
        baseUrl,
        draft.Authentication,
        draft.Enabled,
        state,
        revision,
        targetRevision,
        credentialRevision,
        targetHash,
        fixedErrorCode,
        createdAtUtc,
        updatedAtUtc);

    private static bool TryValidateDraft(
        HttpConnectionProfileDraft? draft,
        long expectedRevision,
        ConnectionSecretMutation secretMutation,
        string? secret,
        out string? normalizedBaseUrl)
    {
        normalizedBaseUrl = null;
        if (draft is null || draft.Id is null || expectedRevision < 0 ||
            draft.DisplayName is not { Length: >= 1 and <= 80 } ||
            draft.DisplayName.Trim() != draft.DisplayName ||
            !Enum.IsDefined(draft.Authentication) || !Enum.IsDefined(secretMutation) ||
            !HttpConnectionProfilePolicy.TryNormalizeBaseUrl(draft.BaseUrl, out normalizedBaseUrl))
            return false;
        if (secretMutation == ConnectionSecretMutation.Replace)
            return draft.Authentication == ConnectionAuthenticationKind.Bearer &&
                   ConnectionHttpBearerTokenPolicy.IsValid(secret);
        return secret is null;
    }

    private static bool IsName(string? value, int maximumLength) =>
        value is { Length: >= 1 } && value.Length <= maximumLength &&
        value[0] is >= 'a' and <= 'z' && value.All(character =>
            character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) ||
            character is '.' or '_' or '-');

    private static bool IsFixedErrorCode(string value) =>
        value.Length is >= 3 and <= 128 && value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' ||
                               char.IsAsciiDigit(character) || character is '.' or '_');

    private static string CreateOpaqueBindingHash() =>
        $"sha256:{Convert.ToHexString(
            SHA256.HashData(RandomNumberGenerator.GetBytes(32))).ToLowerInvariant()}";

    private static bool SameProbeIdentity(
        HttpConnectionProfile before,
        HttpConnectionProfile? after) =>
        after is not null && before.Id.Equals(after.Id) &&
        StringComparer.Ordinal.Equals(before.BaseUrl, after.BaseUrl) &&
        before.Authentication == after.Authentication && before.Enabled == after.Enabled &&
        after.State == ConnectionProfileLifecycleState.Stable &&
        before.TargetRevision == after.TargetRevision &&
        before.CredentialRevision == after.CredentialRevision &&
        StringComparer.Ordinal.Equals(before.TargetHash, after.TargetHash);

    private static ConnectionProfileMutationResult InvalidProfile(string errorCode) =>
        new(ConnectionManagementOutcome.Invalid, ErrorCode: errorCode);

    private static ConnectionProfileMutationResult StoreFailedProfile() =>
        new(ConnectionManagementOutcome.StoreFailure, ErrorCode: "connection.store.unavailable");
}
