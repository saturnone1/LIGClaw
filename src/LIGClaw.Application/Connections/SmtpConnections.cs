using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LIGClaw.Application.Mail;
using LIGClaw.Domain;

namespace LIGClaw.Application.Connections;

public enum SmtpTlsMode
{
    StartTlsRequired,
    ImplicitTls,
}

public enum SmtpAuthenticationMechanism
{
    Plain,
}

public enum SmtpCertificatePolicy
{
    SystemTrust,
}

public sealed record SmtpConnectionProfile(
    ConnectionProfileId Id,
    string DisplayName,
    string Host,
    int Port,
    SmtpTlsMode TlsMode,
    SmtpAuthenticationMechanism Authentication,
    SmtpCertificatePolicy CertificatePolicy,
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
        $"SmtpConnectionProfile(Enabled={Enabled}, State={State}, Revision={Revision}, " +
        $"TargetRevision={TargetRevision}, CredentialRevision={CredentialRevision}, " +
        $"TlsMode={TlsMode}, Authentication={Authentication}, " +
        $"CertificatePolicy={CertificatePolicy}, HasFixedError={FixedErrorCode is not null})";
}

public interface ISmtpConnectionRepository
{
    Task<SmtpConnectionProfile?> GetProfileAsync(
        ConnectionProfileId id,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SmtpConnectionProfile>> ListProfilesAsync(
        CancellationToken cancellationToken);

    Task<bool> TrySaveProfileAsync(
        SmtpConnectionProfile profile,
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

public static class SmtpConnectionProfilePolicy
{
    public const string SmtpContract = "ligclaw-smtp-v1";
    public const int MaximumHostCharacters = 253;
    public const int MinimumPort = 1;
    public const int MaximumPort = 65_535;

    private const string TargetHashFormat = "smtp-target-v1";
    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true };

    public static bool TryNormalizeHost(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumHostCharacters ||
            value.Any(char.IsControl))
            return false;

        var candidate = value.Trim();
        if (candidate.Length == 0 || candidate.Contains('%') ||
            candidate.Contains('[') || candidate.Contains(']'))
            return false;

        if (candidate.Contains(':') ||
            candidate.All(character => char.IsAsciiDigit(character) || character == '.'))
            return false;

        candidate = candidate.EndsWith(".", StringComparison.Ordinal)
            ? candidate[..^1]
            : candidate;
        if (candidate.Length == 0) return false;

        try
        {
            var ascii = Idn.GetAscii(candidate).ToLowerInvariant();
            if (ascii.Length is < 1 or > MaximumHostCharacters) return false;
            var labels = ascii.Split('.', StringSplitOptions.None);
            if (labels.Any(label => label.Length is < 1 or > 63 ||
                                    label[0] == '-' || label[^1] == '-' ||
                                    label.Any(character => character is not (>= 'a' and <= 'z') &&
                                                           !char.IsAsciiDigit(character) && character != '-')))
                return false;
            normalized = ascii;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool IsValid(SmtpConnectionProfile? profile) =>
        profile is not null && profile.Id is not null &&
        ConnectionProfileId.TryParse(profile.Id.Value, out _) &&
        profile.DisplayName is { Length: >= 1 and <= 80 } &&
        profile.DisplayName.Trim() == profile.DisplayName &&
        !profile.DisplayName.Any(char.IsControl) &&
        TryNormalizeHost(profile.Host, out var normalizedHost) &&
        StringComparer.Ordinal.Equals(normalizedHost, profile.Host) &&
        profile.Port is >= MinimumPort and <= MaximumPort &&
        Enum.IsDefined(profile.TlsMode) && Enum.IsDefined(profile.Authentication) &&
        Enum.IsDefined(profile.CertificatePolicy) &&
        MailAddressPolicy.IsValidAsciiMailbox(profile.Sender) &&
        Enum.IsDefined(profile.State) &&
        profile.Revision > 0 && profile.TargetRevision > 0 && profile.CredentialRevision >= 0 &&
        ConnectionHashPolicy.IsSha256(profile.TargetHash) &&
        StringComparer.Ordinal.Equals(
            profile.TargetHash,
            ComputeTargetHash(
                normalizedHost,
                profile.Port,
                profile.TlsMode,
                profile.Authentication,
                profile.CertificatePolicy,
                profile.Sender)) &&
        (profile.FixedErrorCode is null || IsFixedErrorCode(profile.FixedErrorCode)) &&
        profile.CreatedAtUtc.Offset == TimeSpan.Zero && profile.UpdatedAtUtc.Offset == TimeSpan.Zero &&
        profile.UpdatedAtUtc >= profile.CreatedAtUtc;

    public static string ComputeTargetHash(
        string canonicalHost,
        int port,
        SmtpTlsMode tlsMode,
        SmtpAuthenticationMechanism authentication,
        SmtpCertificatePolicy certificatePolicy,
        string sender)
    {
        if (!TryNormalizeHost(canonicalHost, out var normalizedHost) ||
            !StringComparer.Ordinal.Equals(canonicalHost, normalizedHost) ||
            port is < MinimumPort or > MaximumPort ||
            !Enum.IsDefined(tlsMode) || !Enum.IsDefined(authentication) ||
            !Enum.IsDefined(certificatePolicy) ||
            !MailAddressPolicy.IsValidAsciiMailbox(sender))
            throw new ArgumentException("SMTP Connection target이 canonical 형식이 아닙니다.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "format", TargetHashFormat);
        Append(hash, "host", normalizedHost);
        Append(hash, "port", port.ToString(CultureInfo.InvariantCulture));
        Append(hash, "tlsMode", TlsModeValue(tlsMode));
        Append(hash, "authentication", AuthenticationValue(authentication));
        Append(hash, "certificatePolicy", CertificatePolicyValue(certificatePolicy));
        Append(hash, "sender", sender);
        Append(hash, "contract", SmtpContract);
        return $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private static string TlsModeValue(SmtpTlsMode value) => value switch
    {
        SmtpTlsMode.StartTlsRequired => "starttls-required",
        SmtpTlsMode.ImplicitTls => "implicit-tls",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string AuthenticationValue(SmtpAuthenticationMechanism value) => value switch
    {
        SmtpAuthenticationMechanism.Plain => "plain",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string CertificatePolicyValue(SmtpCertificatePolicy value) => value switch
    {
        SmtpCertificatePolicy.SystemTrust => "system-trust",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

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

public sealed class SmtpConnectionReadinessResolver : ICapabilityConnectionReadinessResolver
{
    private readonly ISmtpConnectionRepository repository;
    private readonly IConnectionCredentialStore credentials;

    public SmtpConnectionReadinessResolver(
        ISmtpConnectionRepository repository,
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
            throw new InvalidDataException("Capability SMTP 요구사항이 올바르지 않습니다.");

        var resolved = new List<ResolvedConnectionBinding>(requirements.Count);
        var profiles = new Dictionary<ConnectionProfileId, SmtpConnectionProfile?>();
        var probes = new Dictionary<ConnectionProfileId, ConnectionProbeReceipt?>();
        var credentialStates = new Dictionary<ConnectionProfileId, (bool Available, bool Exists)>();
        foreach (var requirement in requirements.OrderBy(item => item.Slot, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StringComparer.Ordinal.Equals(requirement.Kind, CapabilityConnectionKindPolicy.Smtp))
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
                profile = await repository.GetProfileAsync(binding.ProfileId, cancellationToken)
                    .ConfigureAwait(false);
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
                receipt = await repository.GetProbeReceiptAsync(profile.Id, cancellationToken)
                    .ConfigureAwait(false);
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
                            ConnectionCredentialKind.SmtpAuth,
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
        SmtpConnectionProfile profile,
        CapabilityConnectionBinding binding)
    {
        if (!profile.Enabled) return "connection.profile.disabled";
        if (profile.State != ConnectionProfileLifecycleState.Stable)
            return "connection.profile.unstable";
        if (!SmtpConnectionProfilePolicy.IsValid(profile)) return "connection.profile.invalid";
        if (binding.ProfileTargetRevision != profile.TargetRevision ||
            binding.ProfileCredentialRevision != profile.CredentialRevision ||
            !StringComparer.Ordinal.Equals(binding.ProfileTargetHash, profile.TargetHash))
            return "connection.binding.stale";
        return null;
    }

    private static bool ProbeMatches(
        SmtpConnectionProfile profile,
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
        binding.CreatedAtUtc.Offset == TimeSpan.Zero && binding.UpdatedAtUtc.Offset == TimeSpan.Zero &&
        binding.UpdatedAtUtc >= binding.CreatedAtUtc;

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
