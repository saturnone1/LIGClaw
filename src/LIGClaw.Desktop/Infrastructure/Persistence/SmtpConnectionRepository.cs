using System.Globalization;
using System.IO;
using LIGClaw.Application.Connections;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed class SmtpConnectionRepository(ConversationDatabase database)
    : ISmtpConnectionRepository
{
    private const string ProfileSelect =
        """
        SELECT id, display_name, host, port, tls_mode, authentication, certificate_policy,
               sender, enabled, state, revision, target_revision, credential_revision,
               target_hash, fixed_error_code, created_at_utc, updated_at_utc
        FROM smtp_connection_profiles
        """;

    private const string BindingSelect =
        """
        SELECT capability_id, capability_version, capability_content_hash,
               slot, kind, profile_id, revision,
               profile_target_revision, profile_credential_revision,
               profile_target_hash, binding_hash, created_at_utc, updated_at_utc
        FROM capability_smtp_bindings
        """;

    public async Task<SmtpConnectionProfile?> GetProfileAsync(
        ConnectionProfileId id,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        SmtpConnectionProfile? result = null;
        await database.WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ProfileSelect + " WHERE id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", id.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result = ReadProfile(reader);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<SmtpConnectionProfile>> ListProfilesAsync(
        CancellationToken cancellationToken)
    {
        var results = new List<SmtpConnectionProfile>();
        await database.WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ProfileSelect + " ORDER BY display_name COLLATE NOCASE, id;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadProfile(reader));
        }, cancellationToken).ConfigureAwait(false);
        return results.AsReadOnly();
    }

    public async Task<bool> TrySaveProfileAsync(
        SmtpConnectionProfile profile,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ValidateProfile(profile, expectedRevision);
        var saved = false;
        await database.WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var rows = expectedRevision == 0
                ? await ExecuteAsync(
                    connection,
                    """
                    INSERT OR IGNORE INTO smtp_connection_profiles(
                        id, display_name, host, port, tls_mode, authentication, certificate_policy,
                        sender, enabled, state, revision, target_revision, credential_revision,
                        target_hash, fixed_error_code, created_at_utc, updated_at_utc)
                    VALUES (
                        $id, $displayName, $host, $port, $tlsMode, $authentication,
                        $certificatePolicy, $sender, $enabled, $state, $revision,
                        $targetRevision, $credentialRevision, $targetHash, $errorCode,
                        $createdAtUtc, $updatedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ProfileParameters(profile)).ConfigureAwait(false)
                : await ExecuteAsync(
                    connection,
                    """
                    UPDATE smtp_connection_profiles
                    SET display_name = $displayName,
                        host = $host,
                        port = $port,
                        tls_mode = $tlsMode,
                        authentication = $authentication,
                        certificate_policy = $certificatePolicy,
                        sender = $sender,
                        enabled = $enabled,
                        state = $state,
                        revision = $revision,
                        target_revision = $targetRevision,
                        credential_revision = $credentialRevision,
                        target_hash = $targetHash,
                        fixed_error_code = $errorCode,
                        updated_at_utc = $updatedAtUtc
                    WHERE id = $id AND revision = $expectedRevision;
                    """,
                    cancellationToken,
                    transaction,
                    [.. ProfileParameters(profile), ("$expectedRevision", expectedRevision)]).ConfigureAwait(false);
            saved = rows == 1;
            if (!saved)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            _ = await ExecuteAsync(
                connection,
                """
                DELETE FROM smtp_probe_results
                WHERE profile_id = $id
                  AND (profile_target_revision <> $targetRevision
                    OR profile_credential_revision <> $credentialRevision
                    OR profile_target_hash <> $targetHash);
                """,
                cancellationToken,
                transaction,
                ("$id", profile.Id.Value),
                ("$targetRevision", profile.TargetRevision),
                ("$credentialRevision", profile.CredentialRevision),
                ("$targetHash", profile.TargetHash)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public async Task<ConnectionProbeReceipt?> GetProbeReceiptAsync(
        ConnectionProfileId id,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        ConnectionProbeReceipt? result = null;
        await database.WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT profile_id, profile_target_revision, profile_credential_revision,
                       profile_target_hash, succeeded, fixed_error_code, checked_at_utc
                FROM smtp_probe_results
                WHERE profile_id = $id LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", id.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result = ReadProbe(reader);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task SaveProbeReceiptAsync(
        ConnectionProbeReceipt receipt,
        CancellationToken cancellationToken)
    {
        ValidateProbe(receipt);
        var rows = 0;
        await database.WithConnectionAsync(async connection =>
        {
            rows = await ExecuteAsync(
                connection,
                """
                INSERT INTO smtp_probe_results(
                    profile_id, profile_target_revision, profile_credential_revision,
                    profile_target_hash, succeeded, fixed_error_code, checked_at_utc)
                SELECT id, $targetRevision, $credentialRevision,
                       $targetHash, $succeeded, $errorCode, $checkedAtUtc
                FROM smtp_connection_profiles
                WHERE id = $id AND enabled = 1 AND state = 'stable'
                  AND target_revision = $targetRevision
                  AND credential_revision = $credentialRevision
                  AND target_hash = $targetHash
                ON CONFLICT(profile_id) DO UPDATE SET
                    profile_target_revision = excluded.profile_target_revision,
                    profile_credential_revision = excluded.profile_credential_revision,
                    profile_target_hash = excluded.profile_target_hash,
                    succeeded = excluded.succeeded,
                    fixed_error_code = excluded.fixed_error_code,
                    checked_at_utc = excluded.checked_at_utc;
                """,
                cancellationToken,
                parameters:
                [
                    ("$id", receipt.ProfileId.Value),
                    ("$targetRevision", receipt.ProfileTargetRevision),
                    ("$credentialRevision", receipt.ProfileCredentialRevision),
                    ("$targetHash", receipt.ProfileTargetHash),
                    ("$succeeded", receipt.Succeeded ? 1 : 0),
                    ("$errorCode", receipt.ErrorCode),
                    ("$checkedAtUtc", Format(receipt.CheckedAtUtc)),
                ]).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (rows != 1)
            throw new InvalidOperationException("현재 SMTP target과 일치하지 않는 probe 결과입니다.");
    }

    public async Task<CapabilityConnectionBinding?> GetBindingAsync(
        CapabilityVersionRef exactVersion,
        string slot,
        CancellationToken cancellationToken)
    {
        ValidateExactVersionAndSlot(exactVersion, slot);
        CapabilityConnectionBinding? result = null;
        await database.WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = BindingSelect +
                " WHERE capability_id = $capabilityId AND capability_version = $version" +
                " AND capability_content_hash = $contentHash AND slot = $slot LIMIT 1;";
            AddExactVersion(command, exactVersion);
            command.Parameters.AddWithValue("$slot", slot);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result = ReadBinding(reader);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<CapabilityConnectionBinding>> GetBindingsAsync(
        CapabilityVersionRef exactVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exactVersion);
        var results = new List<CapabilityConnectionBinding>();
        await database.WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = BindingSelect +
                " WHERE capability_id = $capabilityId AND capability_version = $version" +
                " AND capability_content_hash = $contentHash ORDER BY slot;";
            AddExactVersion(command, exactVersion);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadBinding(reader));
        }, cancellationToken).ConfigureAwait(false);
        return results.AsReadOnly();
    }

    public async Task<bool> TrySaveBindingAsync(
        CapabilityConnectionBinding binding,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ValidateBinding(binding, expectedRevision);
        var rows = 0;
        await database.WithConnectionAsync(async connection =>
        {
            rows = expectedRevision == 0
                ? await ExecuteAsync(
                    connection,
                    """
                    INSERT OR IGNORE INTO capability_smtp_bindings(
                        capability_id, capability_version, capability_content_hash,
                        slot, kind, profile_id, revision,
                        profile_target_revision, profile_credential_revision,
                        profile_target_hash, binding_hash, created_at_utc, updated_at_utc)
                    SELECT $capabilityId, $version, $contentHash,
                           $slot, 'smtp', profile.id, $revision,
                           $targetRevision, $credentialRevision,
                           $targetHash, $bindingHash, $createdAtUtc, $updatedAtUtc
                    FROM smtp_connection_profiles AS profile
                    WHERE profile.id = $profileId
                      AND profile.enabled = 1
                      AND profile.state = 'stable'
                      AND profile.target_revision = $targetRevision
                      AND profile.credential_revision = $credentialRevision
                      AND profile.target_hash = $targetHash
                      AND EXISTS (
                          SELECT 1 FROM capability_drafts AS draft
                          WHERE draft.capability_id = $capabilityId
                            AND draft.proposed_version = $version
                            AND draft.content_hash = $contentHash);
                    """,
                    cancellationToken,
                    parameters: BindingParameters(binding)).ConfigureAwait(false)
                : await ExecuteAsync(
                    connection,
                    """
                    UPDATE capability_smtp_bindings
                    SET kind = 'smtp',
                        profile_id = $profileId,
                        revision = $revision,
                        profile_target_revision = $targetRevision,
                        profile_credential_revision = $credentialRevision,
                        profile_target_hash = $targetHash,
                        binding_hash = $bindingHash,
                        updated_at_utc = $updatedAtUtc
                    WHERE capability_id = $capabilityId
                      AND capability_version = $version
                      AND capability_content_hash = $contentHash
                      AND slot = $slot
                      AND revision = $expectedRevision
                      AND EXISTS (
                          SELECT 1 FROM smtp_connection_profiles AS profile
                          WHERE profile.id = $profileId
                            AND profile.enabled = 1
                            AND profile.state = 'stable'
                            AND profile.target_revision = $targetRevision
                            AND profile.credential_revision = $credentialRevision
                            AND profile.target_hash = $targetHash);
                    """,
                    cancellationToken,
                    parameters: [.. BindingParameters(binding), ("$expectedRevision", expectedRevision)])
                    .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return rows == 1;
    }

    private static SmtpConnectionProfile ReadProfile(System.Data.Common.DbDataReader reader) => new(
        ConnectionProfileId.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt32(3),
        ParseTlsMode(reader.GetString(4)),
        ParseAuthentication(reader.GetString(5)),
        ParseCertificatePolicy(reader.GetString(6)),
        reader.GetString(7),
        reader.GetInt64(8) != 0,
        ParseState(reader.GetString(9)),
        reader.GetInt64(10),
        reader.GetInt64(11),
        reader.GetInt64(12),
        reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        ParseTimestamp(reader.GetString(15)),
        ParseTimestamp(reader.GetString(16)));

    private static ConnectionProbeReceipt ReadProbe(System.Data.Common.DbDataReader reader) => new(
        ConnectionProfileId.Parse(reader.GetString(0)),
        reader.GetInt64(1),
        reader.GetInt64(2),
        reader.GetString(3),
        reader.GetInt64(4) != 0,
        reader.IsDBNull(5) ? null : reader.GetString(5),
        ParseTimestamp(reader.GetString(6)));

    private static CapabilityConnectionBinding ReadBinding(System.Data.Common.DbDataReader reader) => new(
        new CapabilityVersionRef(
            CapabilityId.Parse(reader.GetString(0)),
            CapabilitySemanticVersion.Parse(reader.GetString(1)),
            CapabilityContentHash.Parse(reader.GetString(2))),
        reader.GetString(3),
        reader.GetString(4),
        ConnectionProfileId.Parse(reader.GetString(5)),
        reader.GetInt64(6),
        reader.GetInt64(7),
        reader.GetInt64(8),
        reader.GetString(9),
        reader.GetString(10),
        ParseTimestamp(reader.GetString(11)),
        ParseTimestamp(reader.GetString(12)));

    private static void ValidateProfile(SmtpConnectionProfile profile, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (expectedRevision < 0 || profile.Revision != expectedRevision + 1 ||
            !SmtpConnectionProfilePolicy.IsValid(profile) ||
            (profile.State == ConnectionProfileLifecycleState.Error) != (profile.FixedErrorCode is not null) ||
            expectedRevision == 0 &&
            (profile.State != ConnectionProfileLifecycleState.Staged || profile.TargetRevision != 1 ||
             profile.CredentialRevision != 1))
            throw new ArgumentException("SMTP Connection Profile 저장 요청이 올바르지 않습니다.", nameof(profile));
    }

    private static void ValidateProbe(ConnectionProbeReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.ProfileTargetRevision < 1 || receipt.ProfileCredentialRevision < 0 ||
            !IsHash(receipt.ProfileTargetHash) || receipt.CheckedAtUtc.Offset != TimeSpan.Zero ||
            receipt.Succeeded == (receipt.ErrorCode is not null) ||
            receipt.ErrorCode is not null && !IsErrorCode(receipt.ErrorCode))
            throw new ArgumentException("SMTP probe 결과가 올바르지 않습니다.", nameof(receipt));
    }

    private static void ValidateBinding(CapabilityConnectionBinding binding, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateExactVersionAndSlot(binding.ExactVersion, binding.Slot);
        if (expectedRevision < 0 || binding.Revision != expectedRevision + 1 ||
            binding.Kind != CapabilityConnectionKindPolicy.Smtp ||
            binding.ProfileTargetRevision < 1 || binding.ProfileCredentialRevision < 0 ||
            !IsHash(binding.ProfileTargetHash) || !IsHash(binding.BindingHash) ||
            binding.CreatedAtUtc.Offset != TimeSpan.Zero || binding.UpdatedAtUtc.Offset != TimeSpan.Zero ||
            binding.UpdatedAtUtc < binding.CreatedAtUtc)
            throw new ArgumentException("Capability SMTP binding 저장 요청이 올바르지 않습니다.", nameof(binding));
    }

    private static void ValidateExactVersionAndSlot(CapabilityVersionRef exactVersion, string slot)
    {
        ArgumentNullException.ThrowIfNull(exactVersion);
        if (!IsName(slot, 64))
            throw new ArgumentException("SMTP slot 형식이 올바르지 않습니다.", nameof(slot));
    }

    private static (string Name, object? Value)[] ProfileParameters(SmtpConnectionProfile profile) =>
    [
        ("$id", profile.Id.Value),
        ("$displayName", profile.DisplayName),
        ("$host", profile.Host),
        ("$port", profile.Port),
        ("$tlsMode", FormatTlsMode(profile.TlsMode)),
        ("$authentication", FormatAuthentication(profile.Authentication)),
        ("$certificatePolicy", FormatCertificatePolicy(profile.CertificatePolicy)),
        ("$sender", profile.Sender),
        ("$enabled", profile.Enabled ? 1 : 0),
        ("$state", FormatState(profile.State)),
        ("$revision", profile.Revision),
        ("$targetRevision", profile.TargetRevision),
        ("$credentialRevision", profile.CredentialRevision),
        ("$targetHash", profile.TargetHash),
        ("$errorCode", profile.FixedErrorCode),
        ("$createdAtUtc", Format(profile.CreatedAtUtc)),
        ("$updatedAtUtc", Format(profile.UpdatedAtUtc)),
    ];

    private static (string Name, object? Value)[] BindingParameters(CapabilityConnectionBinding binding) =>
    [
        ("$capabilityId", binding.ExactVersion.Id.Value),
        ("$version", binding.ExactVersion.Version.Value),
        ("$contentHash", binding.ExactVersion.ContentHash.Value),
        ("$slot", binding.Slot),
        ("$profileId", binding.ProfileId.Value),
        ("$revision", binding.Revision),
        ("$targetRevision", binding.ProfileTargetRevision),
        ("$credentialRevision", binding.ProfileCredentialRevision),
        ("$targetHash", binding.ProfileTargetHash),
        ("$bindingHash", binding.BindingHash),
        ("$createdAtUtc", Format(binding.CreatedAtUtc)),
        ("$updatedAtUtc", Format(binding.UpdatedAtUtc)),
    ];

    private static void AddExactVersion(SqliteCommand command, CapabilityVersionRef exactVersion)
    {
        command.Parameters.AddWithValue("$capabilityId", exactVersion.Id.Value);
        command.Parameters.AddWithValue("$version", exactVersion.Version.Value);
        command.Parameters.AddWithValue("$contentHash", exactVersion.ContentHash.Value);
    }

    private static Task<int> ExecuteAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        System.Data.Common.DbTransaction? transaction = null,
        params (string Name, object? Value)[] parameters) =>
        ConversationDatabase.ExecuteAsync(connection, commandText, cancellationToken, transaction, parameters);

    private static string FormatTlsMode(SmtpTlsMode value) => value switch
    {
        SmtpTlsMode.StartTlsRequired => "starttls-required",
        SmtpTlsMode.ImplicitTls => "implicit-tls",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static SmtpTlsMode ParseTlsMode(string value) => value switch
    {
        "starttls-required" => SmtpTlsMode.StartTlsRequired,
        "implicit-tls" => SmtpTlsMode.ImplicitTls,
        _ => throw new InvalidDataException("저장된 SMTP TLS mode가 올바르지 않습니다."),
    };

    private static string FormatAuthentication(SmtpAuthenticationMechanism value) => value switch
    {
        SmtpAuthenticationMechanism.Plain => "plain",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static SmtpAuthenticationMechanism ParseAuthentication(string value) => value switch
    {
        "plain" => SmtpAuthenticationMechanism.Plain,
        _ => throw new InvalidDataException("저장된 SMTP authentication이 올바르지 않습니다."),
    };

    private static string FormatCertificatePolicy(SmtpCertificatePolicy value) => value switch
    {
        SmtpCertificatePolicy.SystemTrust => "system-trust",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static SmtpCertificatePolicy ParseCertificatePolicy(string value) => value switch
    {
        "system-trust" => SmtpCertificatePolicy.SystemTrust,
        _ => throw new InvalidDataException("저장된 SMTP certificate policy가 올바르지 않습니다."),
    };

    private static string FormatState(ConnectionProfileLifecycleState state) => state switch
    {
        ConnectionProfileLifecycleState.Staged => "staged",
        ConnectionProfileLifecycleState.Updating => "updating",
        ConnectionProfileLifecycleState.Stable => "stable",
        ConnectionProfileLifecycleState.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static ConnectionProfileLifecycleState ParseState(string value) => value switch
    {
        "staged" => ConnectionProfileLifecycleState.Staged,
        "updating" => ConnectionProfileLifecycleState.Updating,
        "stable" => ConnectionProfileLifecycleState.Stable,
        "error" => ConnectionProfileLifecycleState.Error,
        _ => throw new InvalidDataException("저장된 SMTP Profile 상태가 올바르지 않습니다."),
    };

    private static bool IsHash(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef");

    private static bool IsName(string? value, int maximumLength) =>
        value is { Length: >= 1 } && value.Length <= maximumLength &&
        value[0] is >= 'a' and <= 'z' && value.All(character =>
            character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) ||
            character is '.' or '_' or '-');

    private static bool IsErrorCode(string value) =>
        value.Length is >= 3 and <= 128 && value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' ||
                               char.IsAsciiDigit(character) || character is '.' or '_');

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
    {
        var parsed = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return parsed.Offset == TimeSpan.Zero
            ? parsed
            : throw new InvalidDataException("저장된 SMTP timestamp는 UTC여야 합니다.");
    }
}
