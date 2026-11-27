using System.Globalization;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Contracts.Generated;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class DesktopSmtpExecutionResolver : ISmtpExecutionResolver
{
    private readonly ICapabilityCatalogRepository capabilities;
    private readonly ISmtpConnectionRepository connections;
    private readonly ICapabilityConnectionReadinessResolver readiness;
    private readonly Func<ConnectionProfileId, CancellationToken, Task<SmtpWireCredential?>> readCredential;
    private readonly ConnectionExecutionLeaseCoordinator executionLeases;

    public DesktopSmtpExecutionResolver(
        ICapabilityCatalogRepository capabilities,
        ISmtpConnectionRepository connections,
        ICapabilityConnectionReadinessResolver readiness,
        Func<ConnectionProfileId, CancellationToken, Task<SmtpWireCredential?>> readCredential,
        ConnectionExecutionLeaseCoordinator? executionLeaseCoordinator = null)
    {
        this.capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        this.connections = connections ?? throw new ArgumentNullException(nameof(connections));
        this.readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        this.readCredential = readCredential ?? throw new ArgumentNullException(nameof(readCredential));
        executionLeases = executionLeaseCoordinator ?? new ConnectionExecutionLeaseCoordinator();
    }

    public async Task<SmtpExecutionResolution> ResolveAsync(
        CapabilityInvocationContext capabilityContext,
        string connectionSlot,
        SmtpResolutionPurpose purpose,
        CancellationToken cancellationToken)
    {
        if (!TryParseExactVersion(capabilityContext, out var exactVersion) || exactVersion is null ||
            !IsName(connectionSlot, 64) || purpose is not (
                SmtpResolutionPurpose.Preparation or SmtpResolutionPurpose.Execution))
            return Unavailable();

        CapabilityVersion? version;
        try
        {
            version = await capabilities.GetVersionAsync(exactVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unavailable();
        }

        if (!VersionMatchesContext(version, exactVersion)) return Unavailable();
        var requirements = version!.Payload.Manifest.RequiredConnections;
        if (!RequirementsAreValid(requirements, connectionSlot)) return Unavailable();

        IAsyncDisposable? executionLease = null;
        SmtpWireCredential? credential = null;
        try
        {
            if (purpose == SmtpResolutionPurpose.Execution)
                executionLease = await executionLeases.AcquireAsync(cancellationToken).ConfigureAwait(false);

            var resolution = await ResolveReadinessAsync(
                exactVersion,
                requirements,
                cancellationToken).ConfigureAwait(false);
            if (resolution is null ||
                !ResolutionMatchesRequirements(resolution, exactVersion, requirements))
                return Unavailable();
            if (!resolution.IsReady)
                return SelectedCredentialIsUnavailable(resolution, connectionSlot)
                    ? CredentialUnavailable()
                    : Unavailable();
            if (!StringComparer.Ordinal.Equals(
                    resolution.AggregateHash,
                    capabilityContext.ConnectionBindingHash))
                return Mismatch();

            var binding = resolution.Bindings.SingleOrDefault(candidate =>
                StringComparer.Ordinal.Equals(candidate.Slot, connectionSlot));
            if (!IsReadySmtpBinding(binding, exactVersion, connectionSlot)) return Unavailable();

            var profileRead = await ReadProfileAsync(binding!.ProfileId!, cancellationToken)
                .ConfigureAwait(false);
            if (!profileRead.Available) return Unavailable();
            var profile = profileRead.Profile;
            if (!MatchesFrozenBinding(profile, binding)) return Mismatch();

            if (purpose == SmtpResolutionPurpose.Execution)
            {
                try
                {
                    credential = await readCredential(profile!.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    return CredentialUnavailable();
                }
                if (credential is null) return CredentialUnavailable();

                var afterCredentialReadResult = await ReadProfileAsync(profile.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (!afterCredentialReadResult.Available) return Unavailable();
                var afterCredentialRead = afterCredentialReadResult.Profile;
                if (!MatchesFrozenBinding(afterCredentialRead, binding) ||
                    !SameExecutionIdentity(profile, afterCredentialRead!))
                    return Mismatch();

                var finalResolution = await ResolveReadinessAsync(
                    exactVersion,
                    requirements,
                    cancellationToken).ConfigureAwait(false);
                if (finalResolution is null ||
                    !ResolutionMatchesRequirements(finalResolution, exactVersion, requirements) ||
                    !finalResolution.IsReady ||
                    !StringComparer.Ordinal.Equals(
                        finalResolution.AggregateHash,
                        capabilityContext.ConnectionBindingHash) ||
                    !StringComparer.Ordinal.Equals(
                        finalResolution.AggregateHash,
                        resolution.AggregateHash))
                    return Mismatch();
                var finalBinding = finalResolution.Bindings.SingleOrDefault(candidate =>
                    StringComparer.Ordinal.Equals(candidate.Slot, connectionSlot));
                if (!Equals(binding, finalBinding) ||
                    !IsReadySmtpBinding(finalBinding, exactVersion, connectionSlot))
                    return Mismatch();
                resolution = finalResolution;
                binding = finalBinding;
            }

            var fingerprint = WorkflowPreparedEffectHash.Compute(
                "smtp-runtime-binding-v1",
                binding!.BindingHash!,
                profile!.TargetHash,
                profile.TargetRevision.ToString(CultureInfo.InvariantCulture),
                profile.CredentialRevision.ToString(CultureInfo.InvariantCulture));
            var runtimeBinding = new SmtpRuntimeBinding(
                profile.DisplayName,
                profile.Host,
                profile.Port,
                profile.TlsMode,
                profile.Authentication,
                profile.CertificatePolicy,
                profile.Sender,
                resolution.AggregateHash,
                binding.BindingHash!,
                fingerprint,
                credential);
            credential = null;
            var transferredLease = executionLease;
            executionLease = null;
            return new SmtpExecutionResolution(
                true,
                runtimeBinding,
                ExecutionLease: transferredLease);
        }
        finally
        {
            credential?.Dispose();
            if (executionLease is not null)
                await executionLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<ConnectionReadinessResolution?> ResolveReadinessAsync(
        CapabilityVersionRef exactVersion,
        IReadOnlyList<CapabilityConnectionRequirement> requirements,
        CancellationToken cancellationToken)
    {
        try
        {
            return await readiness.ResolveAsync(exactVersion, requirements, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<ProfileReadResult> ReadProfileAsync(
        ConnectionProfileId profileId,
        CancellationToken cancellationToken)
    {
        try
        {
            var profile = await connections.GetProfileAsync(profileId, cancellationToken)
                .ConfigureAwait(false);
            return new ProfileReadResult(true, profile);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ProfileReadResult(false, null);
        }
    }

    private static bool VersionMatchesContext(
        CapabilityVersion? version,
        CapabilityVersionRef exactVersion)
    {
        try
        {
            return version is not null && version.Payload is not null &&
                   version.Payload.Manifest is not null &&
                   version.ExactVersion.Equals(exactVersion) &&
                   CapabilityContentHasher.Compute(version.Payload).Equals(exactVersion.ContentHash) &&
                   version.Payload.Manifest.Kind == CapabilityKind.Workflow &&
                   version.Payload.Manifest.RequiredTools is not null &&
                   version.Payload.Manifest.RequiredTools.Contains("mail.send.v1", StringComparer.Ordinal) &&
                   version.Payload.Manifest.RequiredConnections is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseExactVersion(
        CapabilityInvocationContext? context,
        out CapabilityVersionRef? exactVersion)
    {
        exactVersion = null;
        if (context is null || !IsHash(context.SnapshotHash) || !IsHash(context.ContentHash) ||
            !IsHash(context.ConnectionBindingHash) ||
            context.CatalogRevision is < 1 or > 9_007_199_254_740_991L)
            return false;
        try
        {
            exactVersion = new CapabilityVersionRef(
                CapabilityId.Parse(context.CapabilityId),
                CapabilitySemanticVersion.Parse(context.Version),
                CapabilityContentHash.Parse(context.ContentHash));
            return StringComparer.Ordinal.Equals(
                context.ModelToolName,
                CapabilityRuntimeHasher.ComputeModelToolName(exactVersion));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static bool RequirementsAreValid(
        IReadOnlyList<CapabilityConnectionRequirement> requirements,
        string connectionSlot) =>
        requirements is not null && requirements.Count is >= 1 and <= 8 &&
        requirements.All(requirement =>
            requirement is not null && IsName(requirement.Slot, 64) &&
            IsName(requirement.Kind, 32) &&
            CapabilityConnectionKindPolicy.IsSupported(requirement.Kind)) &&
        requirements.Select(requirement => requirement.Slot)
            .Distinct(StringComparer.Ordinal).Count() == requirements.Count &&
        requirements.Count(requirement =>
            StringComparer.Ordinal.Equals(requirement.Slot, connectionSlot) &&
            StringComparer.Ordinal.Equals(requirement.Kind, CapabilityConnectionKindPolicy.Smtp)) == 1;

    private static bool ResolutionMatchesRequirements(
        ConnectionReadinessResolution resolution,
        CapabilityVersionRef exactVersion,
        IReadOnlyList<CapabilityConnectionRequirement> requirements)
    {
        if (resolution.Bindings is null || resolution.Bindings.Count != requirements.Count ||
            resolution.IsReady != resolution.Bindings.All(binding => binding is not null && binding.IsReady))
            return false;
        var expected = requirements.ToDictionary(requirement => requirement.Slot, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in resolution.Bindings)
        {
            if (binding is null || binding.ExactVersion is null ||
                !binding.ExactVersion.Equals(exactVersion) ||
                !IsName(binding.Slot, 64) || !IsName(binding.Kind, 32) ||
                !seen.Add(binding.Slot) || !expected.TryGetValue(binding.Slot, out var requirement) ||
                !StringComparer.Ordinal.Equals(binding.Kind, requirement.Kind))
                return false;
        }
        try
        {
            return StringComparer.Ordinal.Equals(
                resolution.AggregateHash,
                ConnectionReadinessHasher.Compute(exactVersion, resolution.Bindings));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool SelectedCredentialIsUnavailable(
        ConnectionReadinessResolution resolution,
        string connectionSlot) =>
        resolution.Bindings.SingleOrDefault(binding =>
            StringComparer.Ordinal.Equals(binding.Slot, connectionSlot))?.ErrorCode is
            "connection.secret.missing" or "connection.secret.unavailable";

    private static bool IsReadySmtpBinding(
        ResolvedConnectionBinding? binding,
        CapabilityVersionRef exactVersion,
        string connectionSlot) =>
        binding is not null && binding.IsReady && binding.ErrorCode is null &&
        binding.ExactVersion.Equals(exactVersion) &&
        StringComparer.Ordinal.Equals(binding.Slot, connectionSlot) &&
        StringComparer.Ordinal.Equals(binding.Kind, CapabilityConnectionKindPolicy.Smtp) &&
        binding.ProfileId is not null && binding.BindingRevision > 0 &&
        IsHash(binding.BindingHash) && binding.ProfileTargetRevision > 0 &&
        binding.ProfileCredentialRevision >= 0 && IsHash(binding.ProfileTargetHash);

    private static bool MatchesFrozenBinding(
        SmtpConnectionProfile? profile,
        ResolvedConnectionBinding binding) =>
        profile is not null && profile.Enabled &&
        profile.State == ConnectionProfileLifecycleState.Stable &&
        SmtpConnectionProfilePolicy.IsValid(profile) &&
        profile.Authentication == SmtpAuthenticationMechanism.Plain &&
        profile.CertificatePolicy == SmtpCertificatePolicy.SystemTrust &&
        profile.Id.Equals(binding.ProfileId) &&
        profile.TargetRevision == binding.ProfileTargetRevision &&
        profile.CredentialRevision == binding.ProfileCredentialRevision &&
        StringComparer.Ordinal.Equals(profile.TargetHash, binding.ProfileTargetHash);

    private static bool SameExecutionIdentity(
        SmtpConnectionProfile before,
        SmtpConnectionProfile after) =>
        before.Id.Equals(after.Id) &&
        StringComparer.Ordinal.Equals(before.Host, after.Host) &&
        before.Port == after.Port && before.TlsMode == after.TlsMode &&
        before.Authentication == after.Authentication &&
        before.CertificatePolicy == after.CertificatePolicy &&
        StringComparer.Ordinal.Equals(before.Sender, after.Sender) &&
        before.Enabled == after.Enabled && before.State == after.State &&
        before.TargetRevision == after.TargetRevision &&
        before.CredentialRevision == after.CredentialRevision &&
        StringComparer.Ordinal.Equals(before.TargetHash, after.TargetHash);

    private static bool IsName(string? value, int maximumLength) =>
        value is { Length: >= 1 } && value.Length <= maximumLength &&
        value[0] is >= 'a' and <= 'z' && value.All(character =>
            character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) ||
            character is '.' or '_' or '-');

    private static bool IsHash(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef");

    private static SmtpExecutionResolution Unavailable() =>
        new(false, ErrorCode: "mail.smtp.binding_unavailable");

    private static SmtpExecutionResolution Mismatch() =>
        new(false, ErrorCode: "mail.smtp.binding_mismatch");

    private static SmtpExecutionResolution CredentialUnavailable() =>
        new(false, ErrorCode: "mail.smtp.credential_unavailable");

    private readonly record struct ProfileReadResult(
        bool Available,
        SmtpConnectionProfile? Profile);
}
