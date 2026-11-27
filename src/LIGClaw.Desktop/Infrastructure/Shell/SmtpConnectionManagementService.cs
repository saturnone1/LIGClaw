using System.Security.Cryptography;
using LIGClaw.Application.Connections;
using LIGClaw.Application.Mail;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record SmtpConnectionProfileDraft(
    ConnectionProfileId Id,
    string DisplayName,
    string Host,
    int Port,
    SmtpTlsMode TlsMode,
    string Sender,
    bool Enabled)
{
    public override string ToString() =>
        $"SmtpConnectionProfileDraft(Port={Port}, TlsMode={TlsMode}, Enabled={Enabled}) [redacted]";
}

internal sealed record SmtpProfileMutationResult(
    ConnectionManagementOutcome Outcome,
    SmtpConnectionProfile? Profile = null,
    string? ErrorCode = null)
{
    public override string ToString() =>
        $"SmtpProfileMutationResult(Outcome={Outcome}, ErrorCode={ErrorCode})";
}

internal sealed record SmtpProbeRecordingResult(
    ConnectionManagementOutcome Outcome,
    ConnectionProbeReceipt? Receipt = null,
    string? ErrorCode = null)
{
    public override string ToString() =>
        $"SmtpProbeRecordingResult(Outcome={Outcome}, ErrorCode={ErrorCode})";
}

internal sealed record SmtpBindingMutationResult(
    ConnectionManagementOutcome Outcome,
    CapabilityConnectionBinding? Binding = null,
    string? ErrorCode = null)
{
    public override string ToString() =>
        $"SmtpBindingMutationResult(Outcome={Outcome}, ErrorCode={ErrorCode})";
}

internal sealed class SmtpConnectionManagementService(
    ISmtpConnectionRepository repository,
    IConnectionCredentialStore credentials,
    TimeProvider? timeProvider = null,
    ISmtpConnectionProbe? probe = null,
    ConnectionExecutionLeaseCoordinator? executionLeaseCoordinator = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConnectionExecutionLeaseCoordinator executionLeases =
        executionLeaseCoordinator ?? new ConnectionExecutionLeaseCoordinator();

    public async Task<SmtpProfileMutationResult> SaveSmtpProfileAsync(
        SmtpConnectionProfileDraft draft,
        long expectedRevision,
        ConnectionSecretMutation credentialMutation,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        if (!TryValidateDraft(
                draft,
                expectedRevision,
                credentialMutation,
                username,
                password,
                out var normalizedHost,
                out var encodedCredential))
            return InvalidProfile("connection.profile.invalid");

        await using var mutationLease =
            await executionLeases.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await SaveProfileCoreAsync(
            draft,
            expectedRevision,
            credentialMutation,
            encodedCredential,
            normalizedHost!,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SmtpProfileMutationResult> SaveProfileCoreAsync(
        SmtpConnectionProfileDraft draft,
        long expectedRevision,
        ConnectionSecretMutation credentialMutation,
        string? encodedCredential,
        string host,
        CancellationToken cancellationToken)
    {
        SmtpConnectionProfile? current;
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
        if (current is not null && !SmtpConnectionProfilePolicy.IsValid(current))
            return new(ConnectionManagementOutcome.StoreFailure, ErrorCode: "connection.profile.corrupt");
        if (current is null or { State: not ConnectionProfileLifecycleState.Stable } &&
            credentialMutation != ConnectionSecretMutation.Replace)
            return new(
                ConnectionManagementOutcome.CredentialFailure,
                current,
                "connection.credential.retry_required");

        var targetChanged = current is null ||
                            !StringComparer.Ordinal.Equals(current.Host, host) ||
                            current.Port != draft.Port || current.TlsMode != draft.TlsMode ||
                            current.Authentication != SmtpAuthenticationMechanism.Plain ||
                            current.CertificatePolicy != SmtpCertificatePolicy.SystemTrust ||
                            !StringComparer.Ordinal.Equals(current.Sender, draft.Sender) ||
                            current.Enabled != draft.Enabled;
        var credentialChanged = credentialMutation != ConnectionSecretMutation.Keep;
        var targetRevision = current is null ? 1 : current.TargetRevision + (targetChanged ? 1 : 0);
        var credentialRevision = current?.CredentialRevision ?? 0;
        if (credentialChanged) credentialRevision++;
        var targetHash = SmtpConnectionProfilePolicy.ComputeTargetHash(
            host,
            draft.Port,
            draft.TlsMode,
            SmtpAuthenticationMechanism.Plain,
            SmtpCertificatePolicy.SystemTrust,
            draft.Sender);
        var nowUtc = timeProvider.GetUtcNow().ToUniversalTime();
        var createdAtUtc = current?.CreatedAtUtc ?? nowUtc;
        var stagedOrCurrent = current;

        if (current is null)
        {
            var staged = CreateProfile(
                draft,
                host,
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
            host,
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
                credentialMutation,
                encodedCredential,
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
                UpdatedAtUtc = timeProvider.GetUtcNow().ToUniversalTime(),
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
            UpdatedAtUtc = timeProvider.GetUtcNow().ToUniversalTime(),
        };
        if (!await TrySaveProfileAsync(stable, updating.Revision, cancellationToken).ConfigureAwait(false))
            return new(ConnectionManagementOutcome.Stale, updating, "connection.profile.stale");
        return new(ConnectionManagementOutcome.Succeeded, stable);
    }

    public async Task<SmtpProbeRecordingResult> RecordProbeAsync(
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
            await executionLeases.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await RecordProbeCoreAsync(
            profileId,
            succeeded,
            fixedErrorCode,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SmtpProbeRecordingResult> RecordProbeCoreAsync(
        ConnectionProfileId profileId,
        bool succeeded,
        string? fixedErrorCode,
        CancellationToken cancellationToken)
    {
        SmtpConnectionProfile? profile;
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
            !SmtpConnectionProfilePolicy.IsValid(profile))
            return new(ConnectionManagementOutcome.NotReady, ErrorCode: "connection.profile.not_ready");

        var receipt = new ConnectionProbeReceipt(
            profile.Id,
            profile.TargetRevision,
            profile.CredentialRevision,
            profile.TargetHash,
            succeeded,
            fixedErrorCode,
            timeProvider.GetUtcNow().ToUniversalTime());
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

    public async Task<SmtpProbeRecordingResult> ProbeSmtpProfileAsync(
        ConnectionProfileId profileId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        if (probe is null)
            return new(ConnectionManagementOutcome.StoreFailure, ErrorCode: "connection.probe.unavailable");

        await using var probeLease =
            await executionLeases.AcquireAsync(cancellationToken).ConfigureAwait(false);
        SmtpConnectionProfile? before;
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
            !SmtpConnectionProfilePolicy.IsValid(before))
            return new(ConnectionManagementOutcome.NotReady, ErrorCode: "connection.profile.not_ready");

        try
        {
            if (!await credentials.ExistsAsync(
                    before.Id,
                    ConnectionCredentialKind.SmtpAuth,
                    cancellationToken).ConfigureAwait(false))
                return new(ConnectionManagementOutcome.NotReady, ErrorCode: "connection.secret.missing");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(ConnectionManagementOutcome.NotReady, ErrorCode: "connection.secret.unavailable");
        }

        SmtpConnectionProbeResult probeResult;
        try
        {
            probeResult = await probe.ProbeAsync(before, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            probeResult = new SmtpConnectionProbeResult(false, "connection.probe.transport_failed");
        }
        if (probeResult is null || probeResult.Succeeded == (probeResult.ErrorCode is not null) ||
            probeResult.ErrorCode is not null && !IsFixedErrorCode(probeResult.ErrorCode))
            return new(ConnectionManagementOutcome.StoreFailure, ErrorCode: "connection.probe.invalid_result");

        SmtpConnectionProfile? after;
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
            probeResult.Succeeded,
            probeResult.ErrorCode,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SmtpBindingMutationResult> BindAsync(
        CapabilityVersionRef exactVersion,
        string slot,
        ConnectionProfileId profileId,
        long expectedBindingRevision,
        CancellationToken cancellationToken)
    {
        if (exactVersion is null || profileId is null || expectedBindingRevision < 0 ||
            !IsName(slot, 64))
            return new(ConnectionManagementOutcome.Invalid, ErrorCode: "connection.binding.invalid");

        await using var mutationLease =
            await executionLeases.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await BindCoreAsync(
            exactVersion,
            slot,
            profileId,
            expectedBindingRevision,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SmtpBindingMutationResult> BindCoreAsync(
        CapabilityVersionRef exactVersion,
        string slot,
        ConnectionProfileId profileId,
        long expectedBindingRevision,
        CancellationToken cancellationToken)
    {
        SmtpConnectionProfile? profile;
        CapabilityConnectionBinding? current;
        try
        {
            profile = await repository.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
            current = await repository.GetBindingAsync(exactVersion, slot, cancellationToken)
                .ConfigureAwait(false);
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
            !SmtpConnectionProfilePolicy.IsValid(profile))
            return new(ConnectionManagementOutcome.NotReady, ErrorCode: "connection.profile.not_ready");
        if (current is null && expectedBindingRevision != 0 ||
            current is not null && current.Revision != expectedBindingRevision)
            return new(ConnectionManagementOutcome.Stale, current, "connection.binding.stale");

        if (current is not null && current.Revision > 0 &&
            current.Kind == CapabilityConnectionKindPolicy.Smtp &&
            current.ProfileId.Equals(profile.Id) &&
            current.ProfileTargetRevision == profile.TargetRevision &&
            current.ProfileCredentialRevision == profile.CredentialRevision &&
            StringComparer.Ordinal.Equals(current.ProfileTargetHash, profile.TargetHash) &&
            ConnectionBindingHashPolicy.IsValid(current.BindingHash))
            return new(ConnectionManagementOutcome.Succeeded, current);

        var nowUtc = timeProvider.GetUtcNow().ToUniversalTime();
        var binding = new CapabilityConnectionBinding(
            exactVersion,
            slot,
            CapabilityConnectionKindPolicy.Smtp,
            profile.Id,
            expectedBindingRevision + 1,
            profile.TargetRevision,
            profile.CredentialRevision,
            profile.TargetHash,
            CreateOpaqueBindingHash(),
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
        SmtpConnectionProfile profile,
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
        string? encodedCredential,
        CancellationToken cancellationToken)
    {
        switch (mutation)
        {
            case ConnectionSecretMutation.Keep:
                return;
            case ConnectionSecretMutation.Replace:
                await credentials.ReplaceAsync(
                    profileId,
                    ConnectionCredentialKind.SmtpAuth,
                    encodedCredential!,
                    cancellationToken).ConfigureAwait(false);
                return;
            case ConnectionSecretMutation.Delete:
                await credentials.DeleteAsync(
                    profileId,
                    ConnectionCredentialKind.SmtpAuth,
                    cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    private static SmtpConnectionProfile CreateProfile(
        SmtpConnectionProfileDraft draft,
        string host,
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
        host,
        draft.Port,
        draft.TlsMode,
        SmtpAuthenticationMechanism.Plain,
        SmtpCertificatePolicy.SystemTrust,
        draft.Sender,
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
        SmtpConnectionProfileDraft? draft,
        long expectedRevision,
        ConnectionSecretMutation credentialMutation,
        string? username,
        string? password,
        out string? normalizedHost,
        out string? encodedCredential)
    {
        normalizedHost = null;
        encodedCredential = null;
        if (draft is null || draft.Id is null || expectedRevision < 0 ||
            draft.DisplayName is not { Length: >= 1 and <= 80 } ||
            draft.DisplayName.Trim() != draft.DisplayName || draft.DisplayName.Any(char.IsControl) ||
            draft.Port is < SmtpConnectionProfilePolicy.MinimumPort or
                > SmtpConnectionProfilePolicy.MaximumPort ||
            draft.TlsMode is not (SmtpTlsMode.StartTlsRequired or SmtpTlsMode.ImplicitTls) ||
            !MailAddressPolicy.IsValidAsciiMailbox(draft.Sender) ||
            !Enum.IsDefined(credentialMutation) ||
            !SmtpConnectionProfilePolicy.TryNormalizeHost(draft.Host, out normalizedHost))
            return false;

        if (credentialMutation != ConnectionSecretMutation.Replace)
            return username is null && password is null;
        try
        {
            encodedCredential = SmtpCredentialCodec.Encode(username!, password!);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool SameProbeIdentity(
        SmtpConnectionProfile before,
        SmtpConnectionProfile? after) =>
        after is not null && before.Id.Equals(after.Id) &&
        StringComparer.Ordinal.Equals(before.Host, after.Host) &&
        before.Port == after.Port && before.TlsMode == after.TlsMode &&
        before.Authentication == after.Authentication &&
        before.CertificatePolicy == after.CertificatePolicy &&
        StringComparer.Ordinal.Equals(before.Sender, after.Sender) &&
        before.Enabled == after.Enabled && after.State == ConnectionProfileLifecycleState.Stable &&
        before.TargetRevision == after.TargetRevision &&
        before.CredentialRevision == after.CredentialRevision &&
        StringComparer.Ordinal.Equals(before.TargetHash, after.TargetHash);

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

    private static SmtpProfileMutationResult InvalidProfile(string errorCode) =>
        new(ConnectionManagementOutcome.Invalid, ErrorCode: errorCode);

    private static SmtpProfileMutationResult StoreFailedProfile() =>
        new(ConnectionManagementOutcome.StoreFailure, ErrorCode: "connection.store.unavailable");
}
