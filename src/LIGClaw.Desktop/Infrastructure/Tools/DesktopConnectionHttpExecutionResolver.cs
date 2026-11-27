using System.Globalization;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Contracts.Generated;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class DesktopConnectionHttpExecutionResolver(
    ICapabilityCatalogRepository capabilities,
    IConnectionProfileRepository connections,
    ICapabilityConnectionReadinessResolver readiness,
    Func<ConnectionProfileId, ConnectionCredentialKind, CancellationToken, Task<string?>> readCredential,
    ConnectionExecutionLeaseCoordinator? executionLeaseCoordinator = null)
    : IConnectionHttpExecutionResolver
{
    private readonly ConnectionExecutionLeaseCoordinator _executionLeases =
        executionLeaseCoordinator ?? new ConnectionExecutionLeaseCoordinator();

    public async Task<ConnectionHttpExecutionResolution> ResolveAsync(
        CapabilityInvocationContext capabilityContext,
        string connectionSlot,
        ConnectionHttpResolutionPurpose purpose,
        CancellationToken cancellationToken)
    {
        if (!TryParseExactVersion(capabilityContext, out var exactVersion) ||
            exactVersion is null || !IsName(connectionSlot, 64) ||
            purpose is not (
                ConnectionHttpResolutionPurpose.Preparation or
                ConnectionHttpResolutionPurpose.Execution))
            return Unavailable();

        CapabilityVersion? version;
        try
        {
            version = await capabilities.GetVersionAsync(
                exactVersion,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unavailable();
        }

        if (version is null || !version.ExactVersion.Equals(exactVersion) ||
            !CapabilityContentHasher.Compute(version.Payload).Equals(exactVersion.ContentHash) ||
            version.Payload.Manifest.Kind != CapabilityKind.Workflow ||
            !version.Payload.Manifest.RequiredTools.Contains(
                "connection.http.request.v1",
                StringComparer.Ordinal))
            return Unavailable();

        IAsyncDisposable? executionLease = null;
        try
        {
            if (purpose == ConnectionHttpResolutionPurpose.Execution)
                executionLease = await _executionLeases.AcquireAsync(cancellationToken).ConfigureAwait(false);

            var requirements = version.Payload.Manifest.RequiredConnections;
            if (!RequirementsAreValid(requirements, connectionSlot))
                return Unavailable();

            ConnectionReadinessResolution resolution;
            try
            {
                resolution = await readiness.ResolveAsync(
                    exactVersion,
                    requirements,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Unavailable();
            }

            if (!resolution.IsReady || resolution.Bindings.Count != requirements.Count)
                return SelectedCredentialIsUnavailable(resolution, connectionSlot)
                    ? CredentialUnavailable()
                    : Unavailable();
            if (!StringComparer.Ordinal.Equals(
                    resolution.AggregateHash,
                    capabilityContext.ConnectionBindingHash))
                return Mismatch();

            var binding = resolution.Bindings.SingleOrDefault(candidate =>
                StringComparer.Ordinal.Equals(candidate.Slot, connectionSlot));
            if (!IsReadyHttpBinding(binding, exactVersion, connectionSlot)) return Unavailable();

            HttpConnectionProfile? profile;
            try
            {
                profile = await connections.GetProfileAsync(
                    binding!.ProfileId!,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Unavailable();
            }
            if (!MatchesFrozenBinding(profile, binding)) return Mismatch();

            ConnectionHttpCredential? credential = null;
            if (profile!.Authentication == ConnectionAuthenticationKind.Bearer &&
                purpose == ConnectionHttpResolutionPurpose.Execution)
            {
                string? secret;
                try
                {
                    secret = await readCredential(
                        profile.Id,
                        ConnectionCredentialKind.BearerToken,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    return CredentialUnavailable();
                }
                if (!ConnectionHttpBearerTokenPolicy.IsValid(secret)) return CredentialUnavailable();
                credential = new ConnectionHttpCredential(secret!);

                HttpConnectionProfile? afterCredentialRead;
                try
                {
                    afterCredentialRead = await connections.GetProfileAsync(
                        profile.Id,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    return Unavailable();
                }
                if (!MatchesFrozenBinding(afterCredentialRead, binding) ||
                    !SameExecutionIdentity(profile, afterCredentialRead!))
                    return Mismatch();
            }

            if (purpose == ConnectionHttpResolutionPurpose.Execution)
            {
                ConnectionReadinessResolution finalResolution;
                try
                {
                    finalResolution = await readiness.ResolveAsync(
                        exactVersion,
                        requirements,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    return Unavailable();
                }
                if (!finalResolution.IsReady ||
                    finalResolution.Bindings.Count != requirements.Count ||
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
                    !IsReadyHttpBinding(finalBinding, exactVersion, connectionSlot))
                    return Mismatch();
                resolution = finalResolution;
                binding = finalBinding;
            }

            if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var baseUri))
                return Unavailable();
            var fingerprint = WorkflowPreparedEffectHash.Compute(
                "connection-http-runtime-binding-v1",
                binding.BindingHash!,
                profile.TargetHash,
                profile.TargetRevision.ToString(CultureInfo.InvariantCulture),
                profile.CredentialRevision.ToString(CultureInfo.InvariantCulture));
            var transferredLease = executionLease;
            executionLease = null;
            return new ConnectionHttpExecutionResolution(
                true,
                new ConnectionHttpRuntimeBinding(
                    profile.DisplayName,
                    baseUri,
                    resolution.AggregateHash,
                    fingerprint,
                    profile.Authentication == ConnectionAuthenticationKind.Bearer
                        ? ConnectionHttpAuthenticationKind.Bearer
                        : ConnectionHttpAuthenticationKind.None,
                    credential),
                ExecutionLease: transferredLease);
        }
        finally
        {
            if (executionLease is not null)
                await executionLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool TryParseExactVersion(
        CapabilityInvocationContext? context,
        out CapabilityVersionRef? exactVersion)
    {
        exactVersion = null;
        if (context is null || !IsHash(context.SnapshotHash) ||
            !IsHash(context.ContentHash) || !IsHash(context.ConnectionBindingHash) ||
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
        requirements.Count is >= 1 and <= 8 &&
        requirements.All(requirement =>
            requirement is not null && IsName(requirement.Slot, 64) &&
            IsName(requirement.Kind, 32) &&
            CapabilityConnectionKindPolicy.IsSupported(requirement.Kind)) &&
        requirements.Select(requirement => requirement.Slot)
            .Distinct(StringComparer.Ordinal).Count() == requirements.Count &&
        requirements.Count(requirement =>
            StringComparer.Ordinal.Equals(requirement.Slot, connectionSlot) &&
            StringComparer.Ordinal.Equals(requirement.Kind, CapabilityConnectionKindPolicy.Http)) == 1;

    private static bool SelectedCredentialIsUnavailable(
        ConnectionReadinessResolution resolution,
        string connectionSlot) =>
        resolution.Bindings.SingleOrDefault(binding =>
            StringComparer.Ordinal.Equals(binding.Slot, connectionSlot))?.ErrorCode is
            "connection.secret.missing" or "connection.secret.unavailable";

    private static bool IsReadyHttpBinding(
        ResolvedConnectionBinding? binding,
        CapabilityVersionRef exactVersion,
        string connectionSlot) =>
        binding is not null && binding.IsReady && binding.ErrorCode is null &&
        binding.ExactVersion.Equals(exactVersion) &&
        StringComparer.Ordinal.Equals(binding.Slot, connectionSlot) &&
        StringComparer.Ordinal.Equals(binding.Kind, CapabilityConnectionKindPolicy.Http) &&
        binding.ProfileId is not null && binding.BindingRevision > 0 &&
        IsHash(binding.BindingHash) && binding.ProfileTargetRevision > 0 &&
        binding.ProfileCredentialRevision >= 0 && IsHash(binding.ProfileTargetHash);

    private static bool MatchesFrozenBinding(
        HttpConnectionProfile? profile,
        ResolvedConnectionBinding binding) =>
        profile is not null && profile.Enabled &&
        profile.State == ConnectionProfileLifecycleState.Stable &&
        HttpConnectionProfilePolicy.IsValid(profile) &&
        profile.Id.Equals(binding.ProfileId) &&
        profile.TargetRevision == binding.ProfileTargetRevision &&
        profile.CredentialRevision == binding.ProfileCredentialRevision &&
        StringComparer.Ordinal.Equals(profile.TargetHash, binding.ProfileTargetHash);

    private static bool SameExecutionIdentity(
        HttpConnectionProfile before,
        HttpConnectionProfile after) =>
        before.Id.Equals(after.Id) &&
        StringComparer.Ordinal.Equals(before.BaseUrl, after.BaseUrl) &&
        before.Authentication == after.Authentication &&
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

    private static ConnectionHttpExecutionResolution Unavailable() =>
        new(false, ErrorCode: "connection.http.binding_unavailable");

    private static ConnectionHttpExecutionResolution Mismatch() =>
        new(false, ErrorCode: "connection.http.binding_mismatch");

    private static ConnectionHttpExecutionResolution CredentialUnavailable() =>
        new(false, ErrorCode: "connection.http.credential_unavailable");
}
