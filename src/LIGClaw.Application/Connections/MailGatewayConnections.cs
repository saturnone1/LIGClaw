using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LIGClaw.Application.Mail;
using LIGClaw.Domain;

namespace LIGClaw.Application.Connections;

public sealed record MailGatewayConnectionProfile(
    ConnectionProfileId Id,
    string DisplayName,
    string BaseUrl,
    string Sender,
    bool Enabled,
    ConnectionProfileLifecycleState State,
    long Revision,
    long TargetRevision,
    long CredentialRevision,
    string TargetHash,
    string? FixedErrorCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public override string ToString() =>
        $"MailGatewayConnectionProfile(Enabled={Enabled}, State={State}, Revision={Revision}, " +
        $"TargetRevision={TargetRevision}, CredentialRevision={CredentialRevision}, " +
        $"HasFixedError={FixedErrorCode is not null})";
}

public interface IMailGatewayConnectionRepository
{
    Task<MailGatewayConnectionProfile?> GetProfileAsync(
        ConnectionProfileId id,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MailGatewayConnectionProfile>> ListProfilesAsync(
        CancellationToken cancellationToken);

    Task<bool> TrySaveProfileAsync(
        MailGatewayConnectionProfile profile,
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

public static class MailGatewayConnectionProfilePolicy
{
    public const string GatewayContract = "ligclaw-mail-gateway-v1";
    private const string TargetHashFormat = "mail-gateway-target-v1";
    private const string Authentication = "bearer";

    public static bool TryNormalizeBaseUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2_048) return false;
        try
        {
            var candidate = value.Trim();
            if (candidate.Contains('@') || candidate.Contains('?') || candidate.Contains('#') ||
                !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                !StringComparer.OrdinalIgnoreCase.Equals(uri.Scheme, Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                !StringComparer.Ordinal.Equals(uri.AbsolutePath, "/"))
                return false;
            var builder = new UriBuilder(uri)
            {
                Scheme = Uri.UriSchemeHttps,
                Host = uri.IdnHost.ToLowerInvariant(),
                Port = uri.IsDefaultPort ? -1 : uri.Port,
                Path = "/",
                Query = string.Empty,
                Fragment = string.Empty,
            };
            normalized = builder.Uri.AbsoluteUri;
            return normalized.Length <= 2_048;
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException)
        {
            return false;
        }
    }

    public static bool IsValid(MailGatewayConnectionProfile? profile) =>
        profile is not null && profile.Id is not null &&
        ConnectionProfileId.TryParse(profile.Id.Value, out _) &&
        profile.DisplayName is { Length: >= 1 and <= 80 } &&
        profile.DisplayName.Trim() == profile.DisplayName &&
        !profile.DisplayName.Any(char.IsControl) &&
        TryNormalizeBaseUrl(profile.BaseUrl, out var normalized) &&
        StringComparer.Ordinal.Equals(normalized, profile.BaseUrl) &&
        MailAddressPolicy.IsValidAsciiMailbox(profile.Sender) &&
        Enum.IsDefined(profile.State) &&
        profile.Revision > 0 && profile.TargetRevision > 0 && profile.CredentialRevision >= 0 &&
        ConnectionHashPolicy.IsSha256(profile.TargetHash) &&
        StringComparer.Ordinal.Equals(profile.TargetHash, ComputeTargetHash(normalized, profile.Sender)) &&
        (profile.FixedErrorCode is null || IsFixedErrorCode(profile.FixedErrorCode)) &&
        profile.CreatedAtUtc.Offset == TimeSpan.Zero && profile.UpdatedAtUtc.Offset == TimeSpan.Zero &&
        profile.UpdatedAtUtc >= profile.CreatedAtUtc;

    public static string ComputeTargetHash(string canonicalBaseUrl, string sender)
    {
        if (!TryNormalizeBaseUrl(canonicalBaseUrl, out var normalized) ||
            !StringComparer.Ordinal.Equals(canonicalBaseUrl, normalized) ||
            !MailAddressPolicy.IsValidAsciiMailbox(sender))
            throw new ArgumentException("Mail Gateway target이 canonical 형식이 아닙니다.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "format", TargetHashFormat);
        Append(hash, "baseUrl", normalized);
        Append(hash, "sender", sender);
        Append(hash, "contract", GatewayContract);
        Append(hash, "authentication", Authentication);
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

public sealed class MailGatewayConnectionReadinessResolver : ICapabilityConnectionReadinessResolver
{
    private readonly IMailGatewayConnectionRepository repository;
    private readonly IConnectionCredentialStore credentials;

    public MailGatewayConnectionReadinessResolver(
        IMailGatewayConnectionRepository repository,
        IConnectionCredentialStore credentials)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

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
            throw new InvalidDataException("Capability Mail Gateway 요구사항이 올바르지 않습니다.");

        var resolved = new List<ResolvedConnectionBinding>(requirements.Count);
        var profiles = new Dictionary<ConnectionProfileId, MailGatewayConnectionProfile?>();
        var probes = new Dictionary<ConnectionProfileId, ConnectionProbeReceipt?>();
        var credentialStates = new Dictionary<ConnectionProfileId, (bool Available, bool Exists)>();
        foreach (var requirement in requirements.OrderBy(item => item.Slot, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StringComparer.Ordinal.Equals(requirement.Kind, CapabilityConnectionKindPolicy.MailApi))
            {
                resolved.Add(NotReady(exactVersion, requirement, "connection.kind.unsupported"));
                continue;
            }

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
                profile = await repository.GetProfileAsync(binding.ProfileId, cancellationToken).ConfigureAwait(false);
                profiles.Add(binding.ProfileId, profile);
            }
            if (profile is null)
            {
                resolved.Add(ToResolution(binding, requirement, false, "connection.profile.missing"));
                continue;
            }

            var profileError = ProfileReadinessError(profile, binding);
            if (profileError is not null)
            {
                resolved.Add(ToResolution(binding, requirement, false, profileError));
                continue;
            }

            if (!probes.TryGetValue(profile.Id, out var receipt))
            {
                receipt = await repository.GetProbeReceiptAsync(profile.Id, cancellationToken).ConfigureAwait(false);
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

            if (!credentialStates.TryGetValue(profile.Id, out var credentialState))
            {
                try
                {
                    credentialState = (
                        Available: true,
                        Exists: await credentials.ExistsAsync(
                            profile.Id,
                            ConnectionCredentialKind.MailGatewayBearerToken,
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
                resolved.Add(ToResolution(binding, requirement, false, "connection.secret.unavailable"));
                continue;
            }
            if (!credentialState.Exists)
            {
                resolved.Add(ToResolution(binding, requirement, false, "connection.secret.missing"));
                continue;
            }

            resolved.Add(ToResolution(binding, requirement, true, null));
        }

        var frozen = Array.AsReadOnly(resolved.ToArray());
        return new ConnectionReadinessResolution(
            frozen.All(binding => binding.IsReady),
            ConnectionReadinessHasher.Compute(exactVersion, frozen),
            frozen);
    }

    private static string? ProfileReadinessError(
        MailGatewayConnectionProfile profile,
        CapabilityConnectionBinding binding)
    {
        if (!profile.Enabled) return "connection.profile.disabled";
        if (profile.State != ConnectionProfileLifecycleState.Stable) return "connection.profile.unstable";
        if (!MailGatewayConnectionProfilePolicy.IsValid(profile)) return "connection.profile.invalid";
        if (binding.ProfileTargetRevision != profile.TargetRevision ||
            binding.ProfileCredentialRevision != profile.CredentialRevision ||
            !StringComparer.Ordinal.Equals(binding.ProfileTargetHash, profile.TargetHash))
            return "connection.binding.stale";
        return null;
    }

    private static bool ProbeMatches(
        MailGatewayConnectionProfile profile,
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
        binding.CreatedAtUtc.Offset == TimeSpan.Zero && binding.UpdatedAtUtc.Offset == TimeSpan.Zero;

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

public sealed class CompositeConnectionReadinessResolver : ICapabilityConnectionReadinessResolver
{
    private readonly ICapabilityConnectionReadinessResolver http;
    private readonly ICapabilityConnectionReadinessResolver mailGateway;
    private readonly ICapabilityConnectionReadinessResolver? smtp;

    public CompositeConnectionReadinessResolver(
        ICapabilityConnectionReadinessResolver http,
        ICapabilityConnectionReadinessResolver mailGateway)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.mailGateway = mailGateway ?? throw new ArgumentNullException(nameof(mailGateway));
    }

    public CompositeConnectionReadinessResolver(
        ICapabilityConnectionReadinessResolver http,
        ICapabilityConnectionReadinessResolver mailGateway,
        ICapabilityConnectionReadinessResolver smtp)
        : this(http, mailGateway)
    {
        this.smtp = smtp ?? throw new ArgumentNullException(nameof(smtp));
    }

    public async Task<ConnectionReadinessResolution> ResolveAsync(
        CapabilityVersionRef exactVersion,
        IReadOnlyList<CapabilityConnectionRequirement> requirements,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exactVersion);
        ArgumentNullException.ThrowIfNull(requirements);
        if (!RequirementsAreStructurallyValid(requirements)) return InvalidRequirements(exactVersion);
        if (requirements.Count == 0)
            return new ConnectionReadinessResolution(
                true,
                ConnectionReadinessHasher.Compute(exactVersion, []),
                Array.AsReadOnly(Array.Empty<ResolvedConnectionBinding>()));

        if (requirements.Any(requirement => !CapabilityConnectionKindPolicy.IsSupported(requirement.Kind)))
        {
            var unsupported = requirements
                .OrderBy(requirement => requirement.Slot, StringComparer.Ordinal)
                .Select(requirement => NotReady(
                    exactVersion,
                    requirement,
                    CapabilityConnectionKindPolicy.IsSupported(requirement.Kind)
                        ? "connection.resolution.blocked"
                        : "connection.kind.unsupported"))
                .ToArray();
            return Freeze(exactVersion, unsupported);
        }

        var httpRequirements = requirements
            .Where(requirement => requirement.Kind == CapabilityConnectionKindPolicy.Http)
            .ToArray();
        var mailRequirements = requirements
            .Where(requirement => requirement.Kind == CapabilityConnectionKindPolicy.MailApi)
            .ToArray();
        var smtpRequirements = requirements
            .Where(requirement => requirement.Kind == CapabilityConnectionKindPolicy.Smtp)
            .ToArray();
        var bindings = new List<ResolvedConnectionBinding>(requirements.Count);
        if (httpRequirements.Length != 0)
        {
            bindings.AddRange(await ResolveGroupAsync(
                http,
                exactVersion,
                httpRequirements,
                cancellationToken).ConfigureAwait(false));
        }
        if (mailRequirements.Length != 0)
        {
            bindings.AddRange(await ResolveGroupAsync(
                mailGateway,
                exactVersion,
                mailRequirements,
                cancellationToken).ConfigureAwait(false));
        }
        if (smtpRequirements.Length != 0)
        {
            bindings.AddRange(smtp is null
                ? FailedBindings(exactVersion, smtpRequirements, "connection.resolution.blocked")
                : await ResolveGroupAsync(
                    smtp,
                    exactVersion,
                    smtpRequirements,
                    cancellationToken).ConfigureAwait(false));
        }
        return Freeze(exactVersion, bindings);
    }

    private static async Task<IReadOnlyList<ResolvedConnectionBinding>> ResolveGroupAsync(
        ICapabilityConnectionReadinessResolver resolver,
        CapabilityVersionRef exactVersion,
        IReadOnlyList<CapabilityConnectionRequirement> requirements,
        CancellationToken cancellationToken)
    {
        try
        {
            var frozenRequirements = Array.AsReadOnly(requirements.ToArray());
            var resolution = await resolver.ResolveAsync(
                exactVersion,
                frozenRequirements,
                cancellationToken).ConfigureAwait(false);
            return ChildResolutionIsValid(exactVersion, frozenRequirements, resolution)
                ? Array.AsReadOnly(resolution.Bindings.ToArray())
                : FailedBindings(exactVersion, frozenRequirements, "connection.resolution.invalid");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return FailedBindings(exactVersion, requirements, "connection.resolution.failed");
        }
    }

    private static bool ChildResolutionIsValid(
        CapabilityVersionRef exactVersion,
        IReadOnlyList<CapabilityConnectionRequirement> requirements,
        ConnectionReadinessResolution? resolution)
    {
        if (resolution?.Bindings is null || resolution.Bindings.Count != requirements.Count) return false;
        var expected = requirements.ToDictionary(requirement => requirement.Slot, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in resolution.Bindings)
        {
            if (binding is null || !binding.ExactVersion.Equals(exactVersion) ||
                !seen.Add(binding.Slot) || !expected.TryGetValue(binding.Slot, out var requirement) ||
                !StringComparer.Ordinal.Equals(binding.Kind, requirement.Kind) ||
                !BindingStateIsCoherent(binding))
                return false;
        }
        if (resolution.IsReady != resolution.Bindings.All(binding => binding.IsReady)) return false;
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

    private static bool BindingStateIsCoherent(ResolvedConnectionBinding binding)
    {
        if (binding.IsReady)
        {
            return binding.ErrorCode is null && binding.ProfileId is not null &&
                   binding.BindingRevision > 0 && ConnectionHashPolicy.IsSha256(binding.BindingHash) &&
                   binding.ProfileTargetRevision > 0 && binding.ProfileCredentialRevision >= 0 &&
                   ConnectionHashPolicy.IsSha256(binding.ProfileTargetHash);
        }
        return IsErrorCode(binding.ErrorCode);
    }

    private static IReadOnlyList<ResolvedConnectionBinding> FailedBindings(
        CapabilityVersionRef exactVersion,
        IReadOnlyList<CapabilityConnectionRequirement> requirements,
        string errorCode) =>
        Array.AsReadOnly(requirements
            .Select(requirement => NotReady(exactVersion, requirement, errorCode))
            .ToArray());

    private static ConnectionReadinessResolution Freeze(
        CapabilityVersionRef exactVersion,
        IEnumerable<ResolvedConnectionBinding> bindings)
    {
        var frozen = Array.AsReadOnly(bindings.OrderBy(binding => binding.Slot, StringComparer.Ordinal).ToArray());
        return new ConnectionReadinessResolution(
            frozen.All(binding => binding.IsReady),
            ConnectionReadinessHasher.Compute(exactVersion, frozen),
            frozen);
    }

    private static ConnectionReadinessResolution InvalidRequirements(CapabilityVersionRef exactVersion) =>
        new(
            false,
            ConnectionReadinessHasher.Compute(exactVersion, []),
            Array.AsReadOnly(Array.Empty<ResolvedConnectionBinding>()));

    private static bool RequirementsAreStructurallyValid(
        IReadOnlyList<CapabilityConnectionRequirement> requirements) =>
        requirements.Count <= 8 && requirements.All(requirement =>
            requirement is not null && IsName(requirement.Slot, 64) && IsName(requirement.Kind, 32)) &&
        requirements.Select(requirement => requirement.Slot)
            .Distinct(StringComparer.Ordinal).Count() == requirements.Count;

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
