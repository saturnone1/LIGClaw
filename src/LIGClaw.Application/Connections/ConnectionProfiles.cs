using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LIGClaw.Domain;

namespace LIGClaw.Application.Connections;

public sealed record ConnectionProfileId
{
    private ConnectionProfileId(string value) => Value = value;

    public string Value { get; }

    public static ConnectionProfileId Parse(string value)
    {
        if (!TryParse(value, out var parsed))
            throw new FormatException("Connection Profile ID 형식이 올바르지 않습니다.");
        return parsed!;
    }

    public static bool TryParse(string? value, out ConnectionProfileId? parsed)
    {
        parsed = null;
        if (value is not { Length: >= 1 and <= 64 } || value[0] is < 'a' or > 'z' ||
            value.Any(character => character is not (>= 'a' and <= 'z') &&
                                   !char.IsAsciiDigit(character) && character != '-'))
            return false;
        parsed = new ConnectionProfileId(value);
        return true;
    }

    public override string ToString() => Value;
}

public enum ConnectionProfileLifecycleState
{
    Staged,
    Updating,
    Stable,
    Error,
}

public enum ConnectionAuthenticationKind
{
    None,
    Bearer,
}

public enum ConnectionCredentialKind
{
    BearerToken,
    MailGatewayBearerToken,
    SmtpAuth,
}

public sealed record HttpConnectionProfile(
    ConnectionProfileId Id,
    string DisplayName,
    string BaseUrl,
    ConnectionAuthenticationKind Authentication,
    bool Enabled,
    ConnectionProfileLifecycleState State,
    long Revision,
    long TargetRevision,
    long CredentialRevision,
    string TargetHash,
    string? FixedErrorCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CapabilityConnectionBinding(
    CapabilityVersionRef ExactVersion,
    string Slot,
    string Kind,
    ConnectionProfileId ProfileId,
    long Revision,
    long ProfileTargetRevision,
    long ProfileCredentialRevision,
    string ProfileTargetHash,
    string BindingHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ConnectionProbeReceipt(
    ConnectionProfileId ProfileId,
    long ProfileTargetRevision,
    long ProfileCredentialRevision,
    string ProfileTargetHash,
    bool Succeeded,
    string? ErrorCode,
    DateTimeOffset CheckedAtUtc);

public sealed record ResolvedConnectionBinding(
    CapabilityVersionRef ExactVersion,
    string Slot,
    string Kind,
    ConnectionProfileId? ProfileId,
    long BindingRevision,
    string? BindingHash,
    long ProfileTargetRevision,
    long ProfileCredentialRevision,
    string? ProfileTargetHash,
    bool IsReady,
    string? ErrorCode);

public sealed record ConnectionReadinessResolution(
    bool IsReady,
    string AggregateHash,
    IReadOnlyList<ResolvedConnectionBinding> Bindings);

public interface IConnectionProfileRepository
{
    Task<HttpConnectionProfile?> GetProfileAsync(
        ConnectionProfileId id,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<HttpConnectionProfile>> ListProfilesAsync(
        CancellationToken cancellationToken);

    Task<bool> TrySaveProfileAsync(
        HttpConnectionProfile profile,
        long expectedRevision,
        CancellationToken cancellationToken);

    Task<ConnectionProbeReceipt?> GetProbeReceiptAsync(
        ConnectionProfileId id,
        CancellationToken cancellationToken);

    Task SaveProbeReceiptAsync(
        ConnectionProbeReceipt receipt,
        CancellationToken cancellationToken);

    Task<CapabilityConnectionBinding?> GetBindingAsync(
        CapabilityVersionRef exactVersion,
        string slot,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CapabilityConnectionBinding>> GetBindingsAsync(
        CapabilityVersionRef exactVersion,
        CancellationToken cancellationToken);

    Task<bool> TrySaveBindingAsync(
        CapabilityConnectionBinding binding,
        long expectedRevision,
        CancellationToken cancellationToken);
}

public interface IConnectionCredentialStore
{
    Task<bool> ExistsAsync(
        ConnectionProfileId profileId,
        ConnectionCredentialKind kind,
        CancellationToken cancellationToken);

    Task ReplaceAsync(
        ConnectionProfileId profileId,
        ConnectionCredentialKind kind,
        string secret,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        ConnectionProfileId profileId,
        ConnectionCredentialKind kind,
        CancellationToken cancellationToken);
}

public interface ICapabilityConnectionReadinessResolver
{
    Task<ConnectionReadinessResolution> ResolveAsync(
        CapabilityVersionRef exactVersion,
        IReadOnlyList<CapabilityConnectionRequirement> requirements,
        CancellationToken cancellationToken);
}

public sealed class ConnectionReadinessResolver(
    IConnectionProfileRepository repository,
    IConnectionCredentialStore credentials) : ICapabilityConnectionReadinessResolver
{
    public async Task<ConnectionReadinessResolution> ResolveAsync(
        CapabilityVersionRef exactVersion,
        IReadOnlyList<CapabilityConnectionRequirement> requirements,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exactVersion);
        ArgumentNullException.ThrowIfNull(requirements);
        if (requirements.Count > 8 || requirements.Any(requirement =>
                requirement is null || !IsName(requirement.Slot, 64) ||
                !IsName(requirement.Kind, 32)) ||
            requirements.Select(requirement => requirement.Slot)
                .Distinct(StringComparer.Ordinal).Count() != requirements.Count)
            throw new InvalidDataException("Capability Connection 요구사항이 올바르지 않습니다.");

        var resolved = new List<ResolvedConnectionBinding>(requirements.Count);
        var profiles = new Dictionary<ConnectionProfileId, HttpConnectionProfile?>();
        var probes = new Dictionary<ConnectionProfileId, ConnectionProbeReceipt?>();
        var credentialStates = new Dictionary<ConnectionProfileId, (bool Available, bool Exists)>();
        foreach (var requirement in requirements.OrderBy(item => item.Slot, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = await repository.GetBindingAsync(
                exactVersion,
                requirement.Slot,
                cancellationToken).ConfigureAwait(false);
            if (binding is null)
            {
                resolved.Add(NotReady(exactVersion, requirement, "connection.binding.missing"));
                continue;
            }

            if (!BindingHasValidIdentity(binding, exactVersion, requirement))
            {
                resolved.Add(ToResolution(binding, requirement, false, "connection.binding.invalid"));
                continue;
            }

            if (!profiles.TryGetValue(binding.ProfileId, out var profile))
            {
                profile = await repository.GetProfileAsync(
                    binding.ProfileId,
                    cancellationToken).ConfigureAwait(false);
                profiles.Add(binding.ProfileId, profile);
            }
            if (profile is null)
            {
                resolved.Add(ToResolution(binding, requirement, false, "connection.profile.missing"));
                continue;
            }

            var profileError = ProfileReadinessError(profile, binding, requirement);
            if (profileError is not null)
            {
                resolved.Add(ToResolution(binding, requirement, false, profileError));
                continue;
            }

            if (!probes.TryGetValue(profile.Id, out var receipt))
            {
                receipt = await repository.GetProbeReceiptAsync(
                    profile.Id,
                    cancellationToken).ConfigureAwait(false);
                probes.Add(profile.Id, receipt);
            }
            if (!ProbeMatches(profile, receipt))
            {
                resolved.Add(ToResolution(binding, requirement, false, "connection.probe.required"));
                continue;
            }
            if (!receipt!.Succeeded)
            {
                resolved.Add(ToResolution(
                    binding,
                    requirement,
                    false,
                    IsErrorCode(receipt.ErrorCode) ? receipt.ErrorCode : "connection.probe.failed"));
                continue;
            }

            if (profile.Authentication == ConnectionAuthenticationKind.Bearer)
            {
                if (!credentialStates.TryGetValue(profile.Id, out var credentialState))
                {
                    try
                    {
                        credentialState = (
                            Available: true,
                            Exists: await credentials.ExistsAsync(
                                profile.Id,
                                ConnectionCredentialKind.BearerToken,
                                cancellationToken).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        credentialState = (Available: false, Exists: false);
                    }
                    credentialStates.Add(profile.Id, credentialState);
                }
                if (!credentialState.Available)
                {
                    resolved.Add(ToResolution(
                        binding,
                        requirement,
                        false,
                        "connection.secret.unavailable"));
                    continue;
                }
                if (!credentialState.Exists)
                {
                    resolved.Add(ToResolution(
                        binding,
                        requirement,
                        false,
                        "connection.secret.missing"));
                    continue;
                }
            }

            resolved.Add(ToResolution(binding, requirement, true, null));
        }

        var frozen = Array.AsReadOnly(resolved.ToArray());
        return new ConnectionReadinessResolution(
            frozen.All(item => item.IsReady),
            ConnectionReadinessHasher.Compute(exactVersion, frozen),
            frozen);
    }

    private static string? ProfileReadinessError(
        HttpConnectionProfile profile,
        CapabilityConnectionBinding binding,
        CapabilityConnectionRequirement requirement)
    {
        if (requirement.Kind != CapabilityConnectionKindPolicy.Http) return "connection.kind.unsupported";
        if (!profile.Enabled) return "connection.profile.disabled";
        if (profile.State != ConnectionProfileLifecycleState.Stable)
            return "connection.profile.unstable";
        if (!HttpConnectionProfilePolicy.IsValid(profile))
            return "connection.profile.invalid";
        if (binding.ProfileTargetRevision != profile.TargetRevision ||
            binding.ProfileCredentialRevision != profile.CredentialRevision ||
            !StringComparer.Ordinal.Equals(binding.ProfileTargetHash, profile.TargetHash))
            return "connection.binding.stale";
        return null;
    }

    private static bool ProbeMatches(
        HttpConnectionProfile profile,
        ConnectionProbeReceipt? receipt) =>
        receipt is not null && receipt.ProfileId.Equals(profile.Id) &&
        receipt.ProfileTargetRevision == profile.TargetRevision &&
        receipt.ProfileCredentialRevision == profile.CredentialRevision &&
        StringComparer.Ordinal.Equals(receipt.ProfileTargetHash, profile.TargetHash) &&
        receipt.CheckedAtUtc.Offset == TimeSpan.Zero &&
        (receipt.Succeeded && receipt.ErrorCode is null ||
         !receipt.Succeeded && IsErrorCode(receipt.ErrorCode));

    private static bool BindingHasValidIdentity(
        CapabilityConnectionBinding binding,
        CapabilityVersionRef exactVersion,
        CapabilityConnectionRequirement requirement) =>
        binding.ExactVersion.Equals(exactVersion) &&
        StringComparer.Ordinal.Equals(binding.Slot, requirement.Slot) &&
        StringComparer.Ordinal.Equals(binding.Kind, requirement.Kind) &&
        binding.Revision > 0 && binding.ProfileTargetRevision > 0 &&
        binding.ProfileCredentialRevision >= 0 &&
        ConnectionHashPolicy.IsSha256(binding.ProfileTargetHash) &&
        ConnectionHashPolicy.IsSha256(binding.BindingHash) &&
        binding.CreatedAtUtc.Offset == TimeSpan.Zero &&
        binding.UpdatedAtUtc.Offset == TimeSpan.Zero;

    private static ResolvedConnectionBinding NotReady(
        CapabilityVersionRef exactVersion,
        CapabilityConnectionRequirement requirement,
        string errorCode) => new(
        exactVersion,
        requirement.Slot,
        requirement.Kind,
        null,
        0,
        null,
        0,
        0,
        null,
        false,
        errorCode);

    private static ResolvedConnectionBinding ToResolution(
        CapabilityConnectionBinding binding,
        CapabilityConnectionRequirement requirement,
        bool isReady,
        string? errorCode) => new(
        binding.ExactVersion,
        requirement.Slot,
        requirement.Kind,
        binding.ProfileId,
        binding.Revision,
        binding.BindingHash,
        binding.ProfileTargetRevision,
        binding.ProfileCredentialRevision,
        binding.ProfileTargetHash,
        isReady,
        errorCode);

    private static bool IsName(string? value, int maximumLength) =>
        value is { Length: >= 1 } && value.Length <= maximumLength &&
        value[0] is >= 'a' and <= 'z' && value.All(character =>
            character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) ||
            character is '.' or '_' or '-');

    private static bool IsErrorCode(string? value) =>
        value is { Length: >= 3 and <= 128 } && value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' ||
                               char.IsAsciiDigit(character) || character is '.' or '_');
}

public static class HttpConnectionProfilePolicy
{
    public static bool TryNormalizeBaseUrl(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2_048 ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            return false;
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.IdnHost.ToLowerInvariant(),
            Port = uri.IsDefaultPort ? -1 : uri.Port,
            Path = uri.AbsolutePath.TrimEnd('/') + "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        normalized = builder.Uri.AbsoluteUri;
        return normalized.Length <= 2_048;
    }

    public static bool IsValid(HttpConnectionProfile profile) =>
        profile is not null && ConnectionProfileId.TryParse(profile.Id.Value, out _) &&
        profile.DisplayName is { Length: >= 1 and <= 80 } &&
        profile.DisplayName.Trim() == profile.DisplayName &&
        TryNormalizeBaseUrl(profile.BaseUrl, out var normalized) &&
        StringComparer.Ordinal.Equals(normalized, profile.BaseUrl) &&
        Enum.IsDefined(profile.Authentication) && Enum.IsDefined(profile.State) &&
        profile.Revision > 0 && profile.TargetRevision > 0 && profile.CredentialRevision >= 0 &&
        ConnectionHashPolicy.IsSha256(profile.TargetHash) &&
        StringComparer.Ordinal.Equals(
            profile.TargetHash,
            ComputeTargetHash(normalized, profile.Authentication)) &&
        (profile.FixedErrorCode is null || IsFixedErrorCode(profile.FixedErrorCode)) &&
        profile.CreatedAtUtc.Offset == TimeSpan.Zero && profile.UpdatedAtUtc.Offset == TimeSpan.Zero &&
        profile.UpdatedAtUtc >= profile.CreatedAtUtc;

    public static string ComputeTargetHash(
        string normalizedBaseUrl,
        ConnectionAuthenticationKind authentication)
    {
        if (!TryNormalizeBaseUrl(normalizedBaseUrl, out var canonical) ||
            !StringComparer.Ordinal.Equals(normalizedBaseUrl, canonical) ||
            !Enum.IsDefined(authentication))
            throw new ArgumentException("HTTP Connection target이 canonical 형식이 아닙니다.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "format", "connection-http-target-v1");
        Append(hash, "baseUrl", canonical);
        Append(hash, "authentication", authentication == ConnectionAuthenticationKind.Bearer ? "bearer" : "none");
        return $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private static bool IsFixedErrorCode(string value) =>
        value.Length is >= 3 and <= 128 && value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' ||
                               char.IsAsciiDigit(character) || character is '.' or '_');

    private static void Append(IncrementalHash hash, string name, string value)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        Span<byte> framing = stackalloc byte[12];
        BinaryPrimitives.WriteInt32BigEndian(framing, nameBytes.Length);
        BinaryPrimitives.WriteInt64BigEndian(framing[4..], valueBytes.LongLength);
        hash.AppendData(framing);
        hash.AppendData(nameBytes);
        hash.AppendData(valueBytes);
    }
}

public static class ConnectionBindingHashPolicy
{
    public static bool IsValid(string? value) => ConnectionHashPolicy.IsSha256(value);
}

public static class ConnectionReadinessHasher
{
    public static string Compute(
        CapabilityVersionRef exactVersion,
        IReadOnlyList<ResolvedConnectionBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(exactVersion);
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count > 8 || bindings.Any(binding =>
                binding is null || !binding.ExactVersion.Equals(exactVersion) ||
                !IsName(binding.Slot, 64) || !IsName(binding.Kind, 32)) ||
            bindings.Select(binding => binding.Slot)
                .Distinct(StringComparer.Ordinal).Count() != bindings.Count)
            throw new ArgumentException("Connection readiness hash 입력이 올바르지 않습니다.", nameof(bindings));
        var ordered = bindings.OrderBy(binding => binding.Slot, StringComparer.Ordinal).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "format", "connection-readiness-v1");
        Append(hash, "capabilityId", exactVersion.Id.Value);
        Append(hash, "version", exactVersion.Version.Value);
        Append(hash, "contentHash", exactVersion.ContentHash.Value);
        Append(hash, "count", ordered.Length.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < ordered.Length; index++)
        {
            var binding = ordered[index];
            var prefix = $"bindings[{index}]";
            Append(hash, $"{prefix}.slot", binding.Slot);
            Append(hash, $"{prefix}.kind", binding.Kind);
            Append(hash, $"{prefix}.profileId", binding.ProfileId?.Value ?? string.Empty);
            Append(hash, $"{prefix}.bindingRevision", binding.BindingRevision.ToString(CultureInfo.InvariantCulture));
            Append(hash, $"{prefix}.bindingHash", binding.BindingHash ?? string.Empty);
            Append(hash, $"{prefix}.profileTargetRevision", binding.ProfileTargetRevision.ToString(CultureInfo.InvariantCulture));
            Append(hash, $"{prefix}.profileCredentialRevision", binding.ProfileCredentialRevision.ToString(CultureInfo.InvariantCulture));
            Append(hash, $"{prefix}.profileTargetHash", binding.ProfileTargetHash ?? string.Empty);
            Append(hash, $"{prefix}.ready", binding.IsReady ? "true" : "false");
            Append(hash, $"{prefix}.errorCode", binding.ErrorCode ?? string.Empty);
        }
        return $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private static bool IsName(string? value, int maximumLength) =>
        value is { Length: >= 1 } && value.Length <= maximumLength &&
        value[0] is >= 'a' and <= 'z' && value.All(character =>
            character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) ||
            character is '.' or '_' or '-');

    private static void Append(IncrementalHash hash, string name, string value)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        Span<byte> framing = stackalloc byte[12];
        BinaryPrimitives.WriteInt32BigEndian(framing, nameBytes.Length);
        BinaryPrimitives.WriteInt64BigEndian(framing[4..], valueBytes.LongLength);
        hash.AppendData(framing);
        hash.AppendData(nameBytes);
        hash.AppendData(valueBytes);
    }
}

internal static class ConnectionHashPolicy
{
    public static bool IsSha256(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef");
}
