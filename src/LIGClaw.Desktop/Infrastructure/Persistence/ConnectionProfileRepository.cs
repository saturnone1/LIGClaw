using System.Globalization;
using System.IO;
using LIGClaw.Application.Connections;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed class ConnectionProfileRepository(ConversationDatabase database) : IConnectionProfileRepository
{
    private const string ProfileSelect =
        """
        SELECT p.id, p.display_name, h.base_url, h.auth_kind, p.enabled, p.state,
               p.revision, p.target_revision, p.credential_revision, p.target_hash,
               p.fixed_error_code, p.created_at_utc, p.updated_at_utc
        FROM connection_profiles AS p
        JOIN http_connection_profiles AS h ON h.profile_id = p.id
        """;

    private const string BindingSelect =
        """
        SELECT capability_id, capability_version, capability_content_hash,
               slot, kind, profile_id, revision,
               profile_target_revision, profile_credential_revision,
               profile_target_hash, binding_hash, created_at_utc, updated_at_utc
        FROM capability_connection_bindings
        """;

    public async Task<HttpConnectionProfile?> GetProfileAsync(
        ConnectionProfileId id,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        HttpConnectionProfile? result = null;
        await database.WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ProfileSelect + " WHERE p.id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", id.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result = ReadProfile(reader);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<HttpConnectionProfile>> ListProfilesAsync(
        CancellationToken cancellationToken)
    {
        var results = new List<HttpConnectionProfile>();
        await database.WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ProfileSelect + " ORDER BY p.display_name COLLATE NOCASE, p.id;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadProfile(reader));
        }, cancellationToken).ConfigureAwait(false);
        return results.AsReadOnly();
    }

    public async Task<bool> TrySaveProfileAsync(
        HttpConnectionProfile profile,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ValidateProfile(profile, expectedRevision);
        var saved = false;
        await database.WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (expectedRevision == 0)
            {
                var inserted = await ExecuteAsync(
                    connection,
                    """
                    INSERT OR IGNORE INTO connection_profiles(
                        id, kind, display_name, enabled, state, revision,
                        target_revision, credential_revision, target_hash, fixed_error_code,
                        created_at_utc, updated_at_utc)
                    VALUES (
                        $id, 'http', $displayName, $enabled, $state, $revision,
                        $targetRevision, $credentialRevision, $targetHash, $errorCode,
                        $createdAtUtc, $updatedAtUtc);
                    """,
                    cancellationToken,
                    transaction,
                    ProfileParameters(profile)).ConfigureAwait(false);
                if (inserted == 1)
                {
                    var child = await ExecuteAsync(
                        connection,
                        """
                        INSERT INTO http_connection_profiles(profile_id, base_url, auth_kind)
                        VALUES ($id, $baseUrl, $authKind);
                        """,
                        cancellationToken,
                        transaction,
                        ("$id", profile.Id.Value),
                        ("$baseUrl", profile.BaseUrl),
                        ("$authKind", FormatAuthentication(profile.Authentication))).ConfigureAwait(false);
                    saved = child == 1;
                }
            }
            else
            {
                var updated = await ExecuteAsync(
                    connection,
                    """
                    UPDATE connection_profiles
                    SET display_name = $displayName,
                        enabled = $enabled,
                        state = $state,
                        revision = $revision,
                        target_revision = $targetRevision,
                        credential_revision = $credentialRevision,
                        target_hash = $targetHash,
                        fixed_error_code = $errorCode,
                        updated_at_utc = $updatedAtUtc
                    WHERE id = $id AND kind = 'http' AND revision = $expectedRevision;
                    """,
                    cancellationToken,
                    transaction,
                    [.. ProfileParameters(profile), ("$expectedRevision", expectedRevision)]).ConfigureAwait(false);
                if (updated == 1)
                {
                    var child = await ExecuteAsync(
                        connection,
                        """
                        UPDATE http_connection_profiles
                        SET base_url = $baseUrl, auth_kind = $authKind
                        WHERE profile_id = $id;
                        """,
                        cancellationToken,
                        transaction,
                        ("$id", profile.Id.Value),
                        ("$baseUrl", profile.BaseUrl),
                        ("$authKind", FormatAuthentication(profile.Authentication))).ConfigureAwait(false);
                    saved = child == 1;
                }
            }

            if (!saved)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            _ = await ExecuteAsync(
                connection,
                """
                DELETE FROM connection_probe_results
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
                FROM connection_probe_results
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
                INSERT INTO connection_probe_results(
                    profile_id, profile_target_revision, profile_credential_revision,
                    profile_target_hash, succeeded, fixed_error_code, checked_at_utc)
                SELECT id, $targetRevision, $credentialRevision,
                       $targetHash, $succeeded, $errorCode, $checkedAtUtc
                FROM connection_profiles
                WHERE id = $id AND kind = 'http' AND state = 'stable'
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
        if (rows != 1) throw new InvalidOperationException("현재 Connection target과 일치하지 않는 probe 결과입니다.");
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
            if (expectedRevision == 0)
            {
                rows = await ExecuteAsync(
                    connection,
                    """
                    INSERT OR IGNORE INTO capability_connection_bindings(
                        capability_id, capability_version, capability_content_hash,
                        slot, kind, profile_id, revision,
                        profile_target_revision, profile_credential_revision,
                        profile_target_hash, binding_hash, created_at_utc, updated_at_utc)
                    SELECT $capabilityId, $version, $contentHash,
                           $slot, $kind, p.id, $revision,
                           $targetRevision, $credentialRevision,
                           $targetHash, $bindingHash, $createdAtUtc, $updatedAtUtc
                    FROM connection_profiles AS p
                    WHERE p.id = $profileId AND p.kind = 'http' AND p.state = 'stable'
                      AND p.target_revision = $targetRevision
                      AND p.credential_revision = $credentialRevision
                      AND p.target_hash = $targetHash
                      AND EXISTS (
                          SELECT 1 FROM capability_drafts AS d
                          WHERE d.capability_id = $capabilityId
                            AND d.proposed_version = $version
                            AND d.content_hash = $contentHash);
                    """,
                    cancellationToken,
                    parameters: BindingParameters(binding)).ConfigureAwait(false);
            }
            else
            {
                rows = await ExecuteAsync(
                    connection,
                    """
                    UPDATE capability_connection_bindings
                    SET kind = $kind,
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
                          SELECT 1 FROM connection_profiles AS p
                          WHERE p.id = $profileId AND p.kind = 'http' AND p.state = 'stable'
                            AND p.target_revision = $targetRevision
                            AND p.credential_revision = $credentialRevision
                            AND p.target_hash = $targetHash);
                    """,
                    cancellationToken,
                    parameters: [.. BindingParameters(binding), ("$expectedRevision", expectedRevision)]).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
        return rows == 1;
    }

    private static HttpConnectionProfile ReadProfile(System.Data.Common.DbDataReader reader) => new(
        ConnectionProfileId.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        ParseAuthentication(reader.GetString(3)),
        reader.GetInt64(4) != 0,
        ParseState(reader.GetString(5)),
        reader.GetInt64(6),
        reader.GetInt64(7),
        reader.GetInt64(8),
        reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        ParseTimestamp(reader.GetString(11)),
        ParseTimestamp(reader.GetString(12)));

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

    private static void ValidateProfile(HttpConnectionProfile profile, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (expectedRevision < 0 || profile.Revision != expectedRevision + 1 ||
            !HttpConnectionProfilePolicy.IsValid(profile) ||
            (profile.State == ConnectionProfileLifecycleState.Error) != (profile.FixedErrorCode is not null))
            throw new ArgumentException("Connection Profile 저장 요청이 올바르지 않습니다.", nameof(profile));
    }

    private static void ValidateProbe(ConnectionProbeReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.ProfileTargetRevision < 1 || receipt.ProfileCredentialRevision < 0 ||
            !IsHash(receipt.ProfileTargetHash) || receipt.CheckedAtUtc.Offset != TimeSpan.Zero ||
            receipt.Succeeded == (receipt.ErrorCode is not null) ||
            receipt.ErrorCode is not null && !IsErrorCode(receipt.ErrorCode))
            throw new ArgumentException("Connection probe 결과가 올바르지 않습니다.", nameof(receipt));
    }

    private static void ValidateBinding(CapabilityConnectionBinding binding, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateExactVersionAndSlot(binding.ExactVersion, binding.Slot);
        if (expectedRevision < 0 || binding.Revision != expectedRevision + 1 ||
            binding.Kind != "http" || binding.ProfileTargetRevision < 1 ||
            binding.ProfileCredentialRevision < 0 || !IsHash(binding.ProfileTargetHash) ||
            !IsHash(binding.BindingHash) || binding.CreatedAtUtc.Offset != TimeSpan.Zero ||
            binding.UpdatedAtUtc.Offset != TimeSpan.Zero || binding.UpdatedAtUtc < binding.CreatedAtUtc)
            throw new ArgumentException("Capability Connection binding 저장 요청이 올바르지 않습니다.", nameof(binding));
    }

    private static void ValidateExactVersionAndSlot(CapabilityVersionRef exactVersion, string slot)
    {
        ArgumentNullException.ThrowIfNull(exactVersion);
        if (slot is not { Length: >= 1 and <= 64 } || slot[0] is < 'a' or > 'z' ||
            slot.Any(character => character is not (>= 'a' and <= 'z') &&
                                  !char.IsAsciiDigit(character) && character is not ('.' or '_' or '-')))
            throw new ArgumentException("Connection slot 형식이 올바르지 않습니다.", nameof(slot));
    }

    private static (string Name, object? Value)[] ProfileParameters(HttpConnectionProfile profile) =>
    [
        ("$id", profile.Id.Value),
        ("$displayName", profile.DisplayName),
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
        ("$kind", binding.Kind),
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
        _ => throw new InvalidDataException("저장된 Connection Profile 상태가 올바르지 않습니다."),
    };

    private static string FormatAuthentication(ConnectionAuthenticationKind authentication) => authentication switch
    {
        ConnectionAuthenticationKind.None => "none",
        ConnectionAuthenticationKind.Bearer => "bearer",
        _ => throw new ArgumentOutOfRangeException(nameof(authentication)),
    };

    private static ConnectionAuthenticationKind ParseAuthentication(string value) => value switch
    {
        "none" => ConnectionAuthenticationKind.None,
        "bearer" => ConnectionAuthenticationKind.Bearer,
        _ => throw new InvalidDataException("저장된 HTTP 인증 방식이 올바르지 않습니다."),
    };

    private static bool IsHash(string value) =>
        value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef");

    private static bool IsErrorCode(string value) =>
        value.Length is >= 3 and <= 128 && value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' ||
                               char.IsAsciiDigit(character) || character is '.' or '_');

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
    {
        var parsed = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return parsed.Offset == TimeSpan.Zero
            ? parsed
            : throw new InvalidDataException("저장된 Connection timestamp는 UTC여야 합니다.");
    }
}
