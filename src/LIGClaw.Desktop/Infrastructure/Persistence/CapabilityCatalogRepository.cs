using System.Globalization;
using System.IO;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Infrastructure.Persistence;

internal sealed partial class CapabilityCatalogRepository(ConversationDatabase database) :
    ICapabilityCatalogRepository,
    ICapabilityCandidateRepository,
    ICapabilityRuntimeCandidateSource,
    IFeatureBuilderRepository
{
    private const int MaximumCandidateQueryLength = 256;
    private const int MaximumCandidateCount = 8;
    private const string CatalogSelect =
        """
        SELECT id, active_version, active_content_hash, catalog_revision, created_at_utc, updated_at_utc
        FROM capabilities
        """;

    private const string DraftSelect =
        """
        SELECT id, capability_id, proposed_version, content_hash,
               parent_version, parent_content_hash, manifest_json, instructions_markdown,
               input_schema_json, settings_schema_json, workflow_json, tests_json,
               status, draft_revision, source_kind, source_package_hash,
               expected_catalog_revision, created_at_utc, updated_at_utc
        FROM capability_drafts
        """;

    private const string VersionSelect =
        """
        SELECT capability_id, version, content_hash, parent_version, parent_content_hash,
               manifest_json, instructions_markdown, input_schema_json, settings_schema_json,
               workflow_json, tests_json, source_kind, source_package_hash, created_at_utc
        FROM capability_versions
        """;

    private const string ActivationSelect =
        """
        SELECT id, capability_id, catalog_revision, action,
               from_version, from_content_hash, target_version, target_content_hash,
               reason_code, created_at_utc
        FROM capability_activations
        """;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<CapabilityCatalogEntry?> GetCatalogAsync(
        CapabilityId id,
        CancellationToken cancellationToken)
    {
        CapabilityCatalogEntry? result = null;
        await WithConnectionAsync(async connection =>
        {
            result = await ReadCatalogAsync(connection, null, id, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<CapabilityVersion?> GetVersionAsync(
        CapabilityVersionRef exactVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exactVersion);
        CapabilityVersion? result = null;
        await WithConnectionAsync(async connection =>
        {
            result = await ReadExactVersionAsync(connection, null, exactVersion, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<CapabilityDraft?> GetDraftAsync(
        string draftId,
        CancellationToken cancellationToken)
    {
        ValidateDraftId(draftId);
        StoredDraft? result = null;
        await WithConnectionAsync(async connection =>
        {
            result = await ReadDraftAsync(connection, null, draftId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result?.Draft;
    }

    public async Task<IReadOnlyList<CapabilityCatalogSummary>> ListAsync(
        string? query,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (query?.Length > 256) throw new ArgumentOutOfRangeException(nameof(query));

        var results = new List<CapabilityCatalogSummary>();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT c.id, c.active_version, c.active_content_hash, c.catalog_revision,
                       d.proposed_version, d.content_hash, d.manifest_json, d.status,
                       d.source_kind, d.updated_at_utc
                FROM capabilities c
                JOIN capability_drafts d ON d.id = (
                    SELECT latest.id
                    FROM capability_drafts latest
                    WHERE latest.capability_id = c.id
                    ORDER BY latest.updated_at_utc DESC, latest.id DESC
                    LIMIT 1
                )
                WHERE $query IS NULL
                   OR c.id LIKE $query ESCAPE '\'
                   OR c.display_name LIKE $query ESCAPE '\'
                   OR c.description LIKE $query ESCAPE '\'
                ORDER BY d.updated_at_utc DESC, c.id
                LIMIT $limit OFFSET $offset;
                """;
            command.Parameters.AddWithValue("$query", query is null ? DBNull.Value : $"%{EscapeLike(query.Trim())}%");
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = CapabilityId.Parse(reader.GetString(0));
                var manifest = ReadManifest(reader.GetString(6));
                var latest = new CapabilityVersionRef(
                    id,
                    CapabilitySemanticVersion.Parse(reader.GetString(4)),
                    CapabilityContentHash.Parse(reader.GetString(5)));
                results.Add(new CapabilityCatalogSummary(
                    id,
                    manifest.DisplayName,
                    manifest.Description,
                    manifest.Kind,
                    manifest.MaximumRisk,
                    ReadOptionalVersionRef(reader, id, 1, 2),
                    latest,
                    ParseDraftState(reader.GetString(7)),
                    reader.GetInt64(3),
                    reader.GetString(8),
                    ParseTimestamp(reader.GetString(9))));
            }
        }, cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<IReadOnlyList<CapabilityVersionRef>> SearchActiveAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > MaximumCandidateQueryLength || query.Any(char.IsControl))
            throw new ArgumentOutOfRangeException(nameof(query));
        var normalizedQuery = query.Trim();
        if (limit is < 1 or > MaximumCandidateCount)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (normalizedQuery.Length < 3) return [];
        var ftsQuery = BuildFtsQuery(normalizedQuery);
        if (ftsQuery.Length == 0) return [];

        var results = new List<CapabilityVersionRef>(limit);
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT search.capability_id, search.version, search.content_hash
                FROM capability_search AS search
                JOIN capabilities AS catalog
                  ON catalog.id = search.capability_id
                 AND catalog.active_version = search.version
                 AND catalog.active_content_hash = search.content_hash
                WHERE capability_search MATCH $query
                ORDER BY bm25(capability_search), search.capability_id COLLATE BINARY
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$query", ftsQuery);
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new CapabilityVersionRef(
                    CapabilityId.Parse(reader.GetString(0)),
                    CapabilitySemanticVersion.Parse(reader.GetString(1)),
                    CapabilityContentHash.Parse(reader.GetString(2))));
            }
        }, cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<IReadOnlyList<CapabilityRuntimeSourceCandidate>> LoadActiveRuntimeCandidatesAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > MaximumCandidateQueryLength || query.Any(char.IsControl))
            throw new ArgumentOutOfRangeException(nameof(query));
        var normalizedQuery = query.Trim();
        if (limit is < 1 or > MaximumCandidateCount)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (normalizedQuery.Length < 3) return [];
        var ftsQuery = BuildFtsQuery(normalizedQuery);
        if (ftsQuery.Length == 0) return [];

        var results = new List<CapabilityRuntimeSourceCandidate>(limit);
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT catalog.id, catalog.active_version, catalog.active_content_hash,
                       catalog.catalog_revision, version.manifest_json,
                       version.instructions_markdown, version.input_schema_json,
                       version.settings_schema_json, version.workflow_json, version.tests_json
                FROM capability_search AS search
                JOIN capabilities AS catalog
                  ON catalog.id = search.capability_id
                 AND catalog.active_version = search.version
                 AND catalog.active_content_hash = search.content_hash
                JOIN capability_versions AS version
                  ON version.capability_id = catalog.id
                 AND version.version = catalog.active_version
                 AND version.content_hash = catalog.active_content_hash
                WHERE capability_search MATCH $query
                ORDER BY bm25(capability_search), search.capability_id COLLATE BINARY
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$query", ftsQuery);
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var exactVersion = new CapabilityVersionRef(
                    CapabilityId.Parse(reader.GetString(0)),
                    CapabilitySemanticVersion.Parse(reader.GetString(1)),
                    CapabilityContentHash.Parse(reader.GetString(2)));
                var payload = ReadPayload(reader, 4, 5, 6, 7, 8, 9);
                EnsureManifestIdentity(payload.Manifest, exactVersion);
                if (!CapabilityContentHasher.Compute(payload).Equals(exactVersion.ContentHash))
                    throw new InvalidDataException("저장된 Capability runtime content hash가 일치하지 않습니다.");
                if (CapabilityIngressPolicy.GetViolation(payload) != CapabilityIngressViolation.None)
                    throw new InvalidDataException("저장된 Capability runtime payload가 안전하지 않습니다.");
                results.Add(new CapabilityRuntimeSourceCandidate(
                    exactVersion,
                    reader.GetInt64(3),
                    payload));
            }
        }, cancellationToken).ConfigureAwait(false);
        return Array.AsReadOnly(results.ToArray());
    }

    public async Task<CapabilityDraftCreateResult> CreateValidatedDraftAsync(
        ValidatedCapabilityDraft draft,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraftId(draft.DraftId);
        if (!ManifestMatchesPayload(draft.Payload))
            return new(CapabilityMutationOutcome.Invalid, ErrorCode: "capability.manifest.canonical_mismatch");

        CapabilityDraftCreateResult result = new(
            CapabilityMutationOutcome.Invalid,
            ErrorCode: "capability.draft.not_created");
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var existingDraft = await ReadDraftAsync(
                connection, transaction, draft.DraftId, cancellationToken).ConfigureAwait(false);
            if (existingDraft is not null)
            {
                result = Equivalent(existingDraft.Draft, draft)
                    ? new(CapabilityMutationOutcome.Duplicate, existingDraft.Draft, "capability.draft.duplicate")
                    : new(CapabilityMutationOutcome.Invalid, ErrorCode: "capability.draft.id_conflict");
                return;
            }

            var exactVersion = draft.ExactVersion;
            var existingExactDraft = await ReadDraftByExactVersionAsync(
                connection, transaction, exactVersion, cancellationToken).ConfigureAwait(false);
            if (existingExactDraft is not null)
            {
                result = new(
                    CapabilityMutationOutcome.Duplicate,
                    existingExactDraft.Draft,
                    "capability.draft.exact_duplicate");
                return;
            }
            var catalog = await ReadCatalogAsync(
                connection, transaction, exactVersion.Id, cancellationToken).ConfigureAwait(false);
            if (catalog is null)
            {
                if (draft.Parent is not null)
                {
                    result = new(CapabilityMutationOutcome.Stale, ErrorCode: "capability.parent.not_active");
                    return;
                }

                await InsertCatalogAsync(connection, transaction, draft.Payload.Manifest, nowUtc, cancellationToken)
                    .ConfigureAwait(false);
                catalog = new CapabilityCatalogEntry(exactVersion.Id, null, 0, nowUtc, nowUtc);
            }
            else if (!Nullable.Equals(catalog.ActiveVersion, draft.Parent))
            {
                result = new(CapabilityMutationOutcome.Stale, ErrorCode: "capability.parent.not_active");
                return;
            }

            var identityVersion = await ReadVersionByIdentityAsync(
                connection, transaction, exactVersion.Id, exactVersion.Version, cancellationToken).ConfigureAwait(false);
            if (identityVersion is not null)
            {
                result = identityVersion.ExactVersion.Equals(exactVersion)
                    ? new(CapabilityMutationOutcome.Duplicate, ErrorCode: "capability.version.duplicate")
                    : new(CapabilityMutationOutcome.Stale, ErrorCode: "capability.version.hash_conflict");
                return;
            }

            var created = new CapabilityDraft(
                draft.DraftId,
                exactVersion,
                draft.Parent,
                draft.Payload,
                CapabilityDraftState.ReadyForReview,
                0,
                draft.SourceKind,
                draft.SourcePackageHash,
                nowUtc,
                nowUtc);
            await InsertDraftAsync(
                connection, transaction, created, catalog.CatalogRevision, cancellationToken).ConfigureAwait(false);
            await UpdateCatalogProposalMetadataAsync(
                connection, transaction, draft.Payload.Manifest, nowUtc, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            result = new(CapabilityMutationOutcome.Succeeded, created);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<CapabilityActivationResult> ApproveAndActivateAsync(
        CapabilityApprovalCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        CapabilityActivationResult result = new(
            CapabilityMutationOutcome.Invalid,
            ErrorCode: "capability.activation.not_applied");
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var storedDraft = await ReadDraftAsync(
                connection, transaction, command.DraftId, cancellationToken).ConfigureAwait(false);
            if (storedDraft is null)
            {
                result = new(CapabilityMutationOutcome.NotFound, ErrorCode: "capability.draft.not_found");
                return;
            }

            var draft = storedDraft.Draft;
            var catalog = await ReadCatalogAsync(
                connection, transaction, draft.ExactVersion.Id, cancellationToken).ConfigureAwait(false);
            if (catalog is null)
            {
                result = new(CapabilityMutationOutcome.NotFound, ErrorCode: "capability.catalog.not_found");
                return;
            }

            if (!draft.ExactVersion.Equals(command.ExpectedVersion))
            {
                result = new(CapabilityMutationOutcome.Stale, catalog, ErrorCode: "capability.approval.compare_failed");
                return;
            }
            if (draft.State == CapabilityDraftState.Applied)
            {
                var previous = await ReadActivationForDraftAsync(
                    connection, transaction, draft.DraftId, cancellationToken).ConfigureAwait(false);
                result = new(CapabilityMutationOutcome.Duplicate, catalog, previous, "capability.draft.already_applied");
                return;
            }
            if (draft.State != CapabilityDraftState.ReadyForReview)
            {
                result = draft.State == CapabilityDraftState.Stale
                    ? new(CapabilityMutationOutcome.Stale, catalog, ErrorCode: "capability.draft.stale")
                    : new(CapabilityMutationOutcome.NotReady, catalog, ErrorCode: "capability.draft.not_ready");
                return;
            }
            if (!CapabilityContentHasher.Compute(draft.Payload).Equals(draft.ExactVersion.ContentHash))
            {
                result = new(
                    CapabilityMutationOutcome.Invalid,
                    catalog,
                    ErrorCode: "capability.draft.content_hash_mismatch");
                return;
            }
            if (draft.Revision != command.ExpectedDraftRevision ||
                catalog.CatalogRevision != command.ExpectedCatalogRevision ||
                !Nullable.Equals(catalog.ActiveVersion, command.ExpectedActiveVersion))
            {
                result = new(CapabilityMutationOutcome.Stale, catalog, ErrorCode: "capability.approval.compare_failed");
                return;
            }
            if (storedDraft.ExpectedCatalogRevision != catalog.CatalogRevision ||
                !Nullable.Equals(draft.Parent, catalog.ActiveVersion))
            {
                await MarkDraftStaleAsync(connection, transaction, draft, command.NowUtc, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                result = new(CapabilityMutationOutcome.Stale, catalog, ErrorCode: "capability.draft.base_changed");
                return;
            }

            var identityVersion = await ReadVersionByIdentityAsync(
                connection, transaction, draft.ExactVersion.Id, draft.ExactVersion.Version, cancellationToken)
                .ConfigureAwait(false);
            if (identityVersion is not null)
            {
                result = identityVersion.ExactVersion.Equals(draft.ExactVersion)
                    ? new(CapabilityMutationOutcome.Duplicate, catalog, ErrorCode: "capability.version.duplicate")
                    : new(CapabilityMutationOutcome.Stale, catalog, ErrorCode: "capability.version.hash_conflict");
                return;
            }

            await InsertVersionAsync(connection, transaction, draft, command.NowUtc, cancellationToken)
                .ConfigureAwait(false);
            var nextRevision = checked(catalog.CatalogRevision + 1);
            var updated = await UpdateActiveCatalogAsync(
                connection,
                transaction,
                draft.Payload.Manifest,
                draft.ExactVersion,
                catalog.CatalogRevision,
                nextRevision,
                command.NowUtc,
                cancellationToken).ConfigureAwait(false);
            if (updated != 1)
            {
                result = new(CapabilityMutationOutcome.Stale, catalog, ErrorCode: "capability.catalog.compare_failed");
                return;
            }

            var activation = new CapabilityActivation(
                Guid.NewGuid().ToString("N"),
                draft.ExactVersion.Id,
                nextRevision,
                "activate",
                catalog.ActiveVersion,
                draft.ExactVersion,
                command.ReasonCode,
                command.NowUtc);
            await InsertActivationAsync(
                connection, transaction, activation, draft.DraftId, cancellationToken).ConfigureAwait(false);
            var draftUpdated = await ExecuteAsync(
                connection,
                """
                UPDATE capability_drafts
                SET status = 'applied', draft_revision = draft_revision + 1,
                    updated_at_utc = $updatedAtUtc, applied_at_utc = $updatedAtUtc
                WHERE id = $id AND status = 'ready_for_review' AND draft_revision = $expectedRevision;
                """,
                cancellationToken,
                transaction,
                ("$updatedAtUtc", Format(command.NowUtc)),
                ("$id", draft.DraftId),
                ("$expectedRevision", draft.Revision)).ConfigureAwait(false);
            if (draftUpdated != 1)
                throw new InvalidOperationException("Capability draft CAS가 activation transaction 안에서 변경되었습니다.");

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            result = new(
                CapabilityMutationOutcome.Succeeded,
                new CapabilityCatalogEntry(
                    catalog.Id,
                    draft.ExactVersion,
                    nextRevision,
                    catalog.CreatedAtUtc,
                    command.NowUtc),
                activation);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<CapabilityActivationResult> RollbackAsync(
        CapabilityRollbackCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        CapabilityActivationResult result = new(
            CapabilityMutationOutcome.Invalid,
            ErrorCode: "capability.rollback.not_applied");
        await WithConnectionAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await ReadCatalogAsync(
                connection, transaction, command.Id, cancellationToken).ConfigureAwait(false);
            if (catalog is null)
            {
                result = new(CapabilityMutationOutcome.NotFound, ErrorCode: "capability.catalog.not_found");
                return;
            }
            if (catalog.CatalogRevision != command.ExpectedCatalogRevision ||
                !Nullable.Equals(catalog.ActiveVersion, command.ExpectedActiveVersion))
            {
                result = new(CapabilityMutationOutcome.Stale, catalog, ErrorCode: "capability.rollback.compare_failed");
                return;
            }
            if (Nullable.Equals(catalog.ActiveVersion, command.TargetVersion))
            {
                result = new(CapabilityMutationOutcome.Duplicate, catalog, ErrorCode: "capability.rollback.already_active");
                return;
            }

            var target = await ReadExactVersionAsync(
                connection, transaction, command.TargetVersion, cancellationToken).ConfigureAwait(false);
            if (target is null || !await WasActivatedBeforeAsync(
                    connection,
                    transaction,
                    command.TargetVersion,
                    catalog.CatalogRevision,
                    cancellationToken).ConfigureAwait(false))
            {
                result = new(
                    CapabilityMutationOutcome.NotFound,
                    catalog,
                    ErrorCode: "capability.rollback.target_unavailable");
                return;
            }

            var nextRevision = checked(catalog.CatalogRevision + 1);
            var updated = await UpdateActiveCatalogAsync(
                connection,
                transaction,
                target.Payload.Manifest,
                command.TargetVersion,
                catalog.CatalogRevision,
                nextRevision,
                command.NowUtc,
                cancellationToken).ConfigureAwait(false);
            if (updated != 1)
            {
                result = new(CapabilityMutationOutcome.Stale, catalog, ErrorCode: "capability.catalog.compare_failed");
                return;
            }

            var activation = new CapabilityActivation(
                Guid.NewGuid().ToString("N"),
                command.Id,
                nextRevision,
                "rollback",
                catalog.ActiveVersion,
                command.TargetVersion,
                command.ReasonCode,
                command.NowUtc);
            await InsertActivationAsync(connection, transaction, activation, null, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            result = new(
                CapabilityMutationOutcome.Succeeded,
                new CapabilityCatalogEntry(
                    catalog.Id,
                    command.TargetVersion,
                    nextRevision,
                    catalog.CreatedAtUtc,
                    command.NowUtc),
                activation);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<CapabilityActivation>> ListActivationsAsync(
        CapabilityId id,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var results = new List<CapabilityActivation>();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"{ActivationSelect} WHERE capability_id = $id ORDER BY catalog_revision DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$id", id.Value);
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadActivation(reader));
        }, cancellationToken).ConfigureAwait(false);
        return results;
    }

    private static async Task<CapabilityCatalogEntry?> ReadCatalogAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CapabilityId id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{CatalogSelect} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new CapabilityCatalogEntry(
            CapabilityId.Parse(reader.GetString(0)),
            ReadOptionalVersionRef(reader, id, 1, 2),
            reader.GetInt64(3),
            ParseTimestamp(reader.GetString(4)),
            ParseTimestamp(reader.GetString(5)));
    }

    private static async Task<StoredDraft?> ReadDraftAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string draftId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{DraftSelect} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", draftId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDraft(reader) : null;
    }

    private static async Task<StoredDraft?> ReadDraftByExactVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CapabilityVersionRef exactVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"{DraftSelect} WHERE capability_id = $id AND proposed_version = $version AND content_hash = $contentHash LIMIT 1;";
        command.Parameters.AddWithValue("$id", exactVersion.Id.Value);
        command.Parameters.AddWithValue("$version", exactVersion.Version.Value);
        command.Parameters.AddWithValue("$contentHash", exactVersion.ContentHash.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDraft(reader) : null;
    }

    private static async Task<CapabilityVersion?> ReadExactVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CapabilityVersionRef exactVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"{VersionSelect} WHERE capability_id = $id AND version = $version AND content_hash = $contentHash;";
        command.Parameters.AddWithValue("$id", exactVersion.Id.Value);
        command.Parameters.AddWithValue("$version", exactVersion.Version.Value);
        command.Parameters.AddWithValue("$contentHash", exactVersion.ContentHash.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadVersion(reader) : null;
    }

    private static async Task<CapabilityVersion?> ReadVersionByIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CapabilityId id,
        CapabilitySemanticVersion version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{VersionSelect} WHERE capability_id = $id AND version = $version;";
        command.Parameters.AddWithValue("$id", id.Value);
        command.Parameters.AddWithValue("$version", version.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadVersion(reader) : null;
    }

    private static async Task<CapabilityActivation?> ReadActivationForDraftAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string draftId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"{ActivationSelect} WHERE source_draft_id = $draftId ORDER BY catalog_revision DESC LIMIT 1;";
        command.Parameters.AddWithValue("$draftId", draftId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadActivation(reader) : null;
    }

    private static async Task<bool> WasActivatedBeforeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CapabilityVersionRef target,
        long currentCatalogRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM capability_activations
                WHERE capability_id = $id
                  AND target_version = $version
                  AND target_content_hash = $contentHash
                  AND catalog_revision < $currentRevision
            );
            """;
        command.Parameters.AddWithValue("$id", target.Id.Value);
        command.Parameters.AddWithValue("$version", target.Version.Value);
        command.Parameters.AddWithValue("$contentHash", target.ContentHash.Value);
        command.Parameters.AddWithValue("$currentRevision", currentCatalogRevision);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
    }

    private static async Task InsertCatalogAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CapabilityManifest manifest,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            """
            INSERT INTO capabilities(
                id, display_name, description, kind, maximum_risk,
                active_version, active_content_hash, catalog_revision, created_at_utc, updated_at_utc)
            VALUES (
                $id, $displayName, $description, $kind, $maximumRisk,
                NULL, NULL, 0, $nowUtc, $nowUtc);
            """,
            cancellationToken,
            transaction,
            ("$id", manifest.Id.Value),
            ("$displayName", manifest.DisplayName),
            ("$description", manifest.Description),
            ("$kind", FormatKind(manifest.Kind)),
            ("$maximumRisk", manifest.MaximumRisk),
            ("$nowUtc", Format(nowUtc))).ConfigureAwait(false);
    }

    private static Task UpdateCatalogProposalMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CapabilityManifest manifest,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            """
            UPDATE capabilities
            SET display_name = $displayName, description = $description,
                kind = $kind, maximum_risk = $maximumRisk, updated_at_utc = $updatedAtUtc
            WHERE id = $id;
            """,
            cancellationToken,
            transaction,
            ("$displayName", manifest.DisplayName),
            ("$description", manifest.Description),
            ("$kind", FormatKind(manifest.Kind)),
            ("$maximumRisk", manifest.MaximumRisk),
            ("$updatedAtUtc", Format(nowUtc)),
            ("$id", manifest.Id.Value));

    private static Task InsertDraftAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CapabilityDraft draft,
        long expectedCatalogRevision,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            """
            INSERT INTO capability_drafts(
                id, capability_id, proposed_version, content_hash,
                parent_version, parent_content_hash, display_name, kind,
                manifest_json, instructions_markdown, input_schema_json, settings_schema_json,
                workflow_json, tests_json, status, draft_revision, source_kind,
                source_package_hash, expected_catalog_revision, created_at_utc, updated_at_utc, applied_at_utc)
            VALUES (
                $id, $capabilityId, $version, $contentHash,
                $parentVersion, $parentContentHash, $displayName, $kind,
                $manifestJson, $instructions, $inputSchemaJson, $settingsSchemaJson,
                $workflowJson, $testsJson, $status, $draftRevision, $sourceKind,
                $sourcePackageHash, $expectedCatalogRevision, $createdAtUtc, $updatedAtUtc, NULL);
            """,
            cancellationToken,
            transaction,
            ("$id", draft.DraftId),
            ("$capabilityId", draft.ExactVersion.Id.Value),
            ("$version", draft.ExactVersion.Version.Value),
            ("$contentHash", draft.ExactVersion.ContentHash.Value),
            ("$parentVersion", draft.Parent?.Version.Value),
            ("$parentContentHash", draft.Parent?.ContentHash.Value),
            ("$displayName", draft.Payload.Manifest.DisplayName),
            ("$kind", FormatKind(draft.Payload.Manifest.Kind)),
            ("$manifestJson", draft.Payload.CanonicalManifestJson),
            ("$instructions", draft.Payload.Instructions),
            ("$inputSchemaJson", draft.Payload.CanonicalInputSchemaJson),
            ("$settingsSchemaJson", draft.Payload.CanonicalSettingsSchemaJson),
            ("$workflowJson", draft.Payload.CanonicalWorkflowJson),
            ("$testsJson", draft.Payload.CanonicalTestsJson),
            ("$status", FormatDraftState(draft.State)),
            ("$draftRevision", draft.Revision),
            ("$sourceKind", draft.SourceKind),
            ("$sourcePackageHash", draft.SourcePackageHash.Value),
            ("$expectedCatalogRevision", expectedCatalogRevision),
            ("$createdAtUtc", Format(draft.CreatedAtUtc)),
            ("$updatedAtUtc", Format(draft.UpdatedAtUtc)));

    private static Task InsertVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CapabilityDraft draft,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            """
            INSERT INTO capability_versions(
                capability_id, version, content_hash, parent_version, parent_content_hash,
                manifest_json, instructions_markdown, input_schema_json, settings_schema_json,
                workflow_json, tests_json, source_kind, source_package_hash, created_at_utc)
            VALUES (
                $capabilityId, $version, $contentHash, $parentVersion, $parentContentHash,
                $manifestJson, $instructions, $inputSchemaJson, $settingsSchemaJson,
                $workflowJson, $testsJson, $sourceKind, $sourcePackageHash, $createdAtUtc);
            """,
            cancellationToken,
            transaction,
            ("$capabilityId", draft.ExactVersion.Id.Value),
            ("$version", draft.ExactVersion.Version.Value),
            ("$contentHash", draft.ExactVersion.ContentHash.Value),
            ("$parentVersion", draft.Parent?.Version.Value),
            ("$parentContentHash", draft.Parent?.ContentHash.Value),
            ("$manifestJson", draft.Payload.CanonicalManifestJson),
            ("$instructions", draft.Payload.Instructions),
            ("$inputSchemaJson", draft.Payload.CanonicalInputSchemaJson),
            ("$settingsSchemaJson", draft.Payload.CanonicalSettingsSchemaJson),
            ("$workflowJson", draft.Payload.CanonicalWorkflowJson),
            ("$testsJson", draft.Payload.CanonicalTestsJson),
            ("$sourceKind", draft.SourceKind),
            ("$sourcePackageHash", draft.SourcePackageHash.Value),
            ("$createdAtUtc", Format(createdAtUtc)));

    private static Task<int> UpdateActiveCatalogAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CapabilityManifest manifest,
        CapabilityVersionRef target,
        long expectedRevision,
        long nextRevision,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            """
            UPDATE capabilities
            SET active_version = $version, active_content_hash = $contentHash,
                display_name = $displayName, description = $description,
                kind = $kind, maximum_risk = $maximumRisk,
                catalog_revision = $nextRevision, updated_at_utc = $updatedAtUtc
            WHERE id = $id AND catalog_revision = $expectedRevision;
            """,
            cancellationToken,
            transaction,
            ("$version", target.Version.Value),
            ("$contentHash", target.ContentHash.Value),
            ("$displayName", manifest.DisplayName),
            ("$description", manifest.Description),
            ("$kind", FormatKind(manifest.Kind)),
            ("$maximumRisk", manifest.MaximumRisk),
            ("$nextRevision", nextRevision),
            ("$updatedAtUtc", Format(updatedAtUtc)),
            ("$id", target.Id.Value),
            ("$expectedRevision", expectedRevision));

    private static Task InsertActivationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CapabilityActivation activation,
        string? sourceDraftId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            """
            INSERT INTO capability_activations(
                id, capability_id, catalog_revision, action, source_draft_id,
                from_version, from_content_hash, target_version, target_content_hash,
                reason_code, created_at_utc)
            VALUES (
                $id, $capabilityId, $catalogRevision, $action, $sourceDraftId,
                $fromVersion, $fromContentHash, $targetVersion, $targetContentHash,
                $reasonCode, $createdAtUtc);
            """,
            cancellationToken,
            transaction,
            ("$id", activation.ActivationId),
            ("$capabilityId", activation.Id.Value),
            ("$catalogRevision", activation.CatalogRevision),
            ("$action", activation.Action),
            ("$sourceDraftId", sourceDraftId),
            ("$fromVersion", activation.From?.Version.Value),
            ("$fromContentHash", activation.From?.ContentHash.Value),
            ("$targetVersion", activation.To.Version.Value),
            ("$targetContentHash", activation.To.ContentHash.Value),
            ("$reasonCode", activation.ReasonCode),
            ("$createdAtUtc", Format(activation.CreatedAtUtc)));

    private static Task MarkDraftStaleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CapabilityDraft draft,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            """
            UPDATE capability_drafts
            SET status = 'stale', draft_revision = draft_revision + 1, updated_at_utc = $updatedAtUtc
            WHERE id = $id AND status = 'ready_for_review' AND draft_revision = $expectedRevision;
            """,
            cancellationToken,
            transaction,
            ("$updatedAtUtc", Format(updatedAtUtc)),
            ("$id", draft.DraftId),
            ("$expectedRevision", draft.Revision));

    private static StoredDraft ReadDraft(System.Data.Common.DbDataReader reader)
    {
        var id = CapabilityId.Parse(reader.GetString(1));
        var exactVersion = new CapabilityVersionRef(
            id,
            CapabilitySemanticVersion.Parse(reader.GetString(2)),
            CapabilityContentHash.Parse(reader.GetString(3)));
        var parent = ReadOptionalVersionRef(reader, id, 4, 5);
        var payload = ReadPayload(reader, 6, 7, 8, 9, 10, 11);
        EnsureManifestIdentity(payload.Manifest, exactVersion);
        return new StoredDraft(
            new CapabilityDraft(
                reader.GetString(0),
                exactVersion,
                parent,
                payload,
                ParseDraftState(reader.GetString(12)),
                reader.GetInt64(13),
                reader.GetString(14),
                CapabilityContentHash.Parse(reader.GetString(15)),
                ParseTimestamp(reader.GetString(17)),
                ParseTimestamp(reader.GetString(18))),
            reader.GetInt64(16));
    }

    private static CapabilityVersion ReadVersion(System.Data.Common.DbDataReader reader)
    {
        var id = CapabilityId.Parse(reader.GetString(0));
        var exactVersion = new CapabilityVersionRef(
            id,
            CapabilitySemanticVersion.Parse(reader.GetString(1)),
            CapabilityContentHash.Parse(reader.GetString(2)));
        var payload = ReadPayload(reader, 5, 6, 7, 8, 9, 10);
        EnsureManifestIdentity(payload.Manifest, exactVersion);
        return new CapabilityVersion(
            exactVersion,
            ReadOptionalVersionRef(reader, id, 3, 4),
            payload,
            reader.GetString(11),
            CapabilityContentHash.Parse(reader.GetString(12)),
            ParseTimestamp(reader.GetString(13)));
    }

    private static CapabilityActivation ReadActivation(System.Data.Common.DbDataReader reader)
    {
        var id = CapabilityId.Parse(reader.GetString(1));
        return new CapabilityActivation(
            reader.GetString(0),
            id,
            reader.GetInt64(2),
            reader.GetString(3),
            ReadOptionalVersionRef(reader, id, 4, 5),
            new CapabilityVersionRef(
                id,
                CapabilitySemanticVersion.Parse(reader.GetString(6)),
                CapabilityContentHash.Parse(reader.GetString(7))),
            reader.GetString(8),
            ParseTimestamp(reader.GetString(9)));
    }

    private static CapabilityVersionPayload ReadPayload(
        System.Data.Common.DbDataReader reader,
        int manifestOrdinal,
        int instructionsOrdinal,
        int inputSchemaOrdinal,
        int settingsSchemaOrdinal,
        int workflowOrdinal,
        int testsOrdinal)
    {
        var canonicalManifest = reader.GetString(manifestOrdinal);
        return new CapabilityVersionPayload(
            ReadManifest(canonicalManifest),
            canonicalManifest,
            reader.GetString(instructionsOrdinal),
            reader.GetString(inputSchemaOrdinal),
            reader.IsDBNull(settingsSchemaOrdinal) ? null : reader.GetString(settingsSchemaOrdinal),
            reader.IsDBNull(workflowOrdinal) ? null : reader.GetString(workflowOrdinal),
            reader.GetString(testsOrdinal));
    }

    private static CapabilityVersionRef? ReadOptionalVersionRef(
        System.Data.Common.DbDataReader reader,
        CapabilityId id,
        int versionOrdinal,
        int hashOrdinal)
    {
        if (reader.IsDBNull(versionOrdinal) && reader.IsDBNull(hashOrdinal)) return null;
        if (reader.IsDBNull(versionOrdinal) || reader.IsDBNull(hashOrdinal))
            throw new InvalidDataException("Capability exact version reference가 손상되었습니다.");
        return new CapabilityVersionRef(
            id,
            CapabilitySemanticVersion.Parse(reader.GetString(versionOrdinal)),
            CapabilityContentHash.Parse(reader.GetString(hashOrdinal)));
    }

    private static CapabilityManifest ReadManifest(string canonicalJson)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredManifest>(canonicalJson, ManifestJsonOptions)
                         ?? throw new JsonException("Capability manifest가 비어 있습니다.");
            if (stored.Id is null ||
                stored.Version is null ||
                stored.DisplayName is null ||
                stored.Description is null ||
                stored.Kind is null ||
                stored.IntentExamples is null ||
                stored.IntentExamples.Any(value => value is null) ||
                stored.RequiredTools is null ||
                stored.RequiredTools.Any(value => value is null) ||
                stored.RequiredConnections is null ||
                stored.RequiredConnections.Any(requirement =>
                    requirement is null || requirement.Slot is null || requirement.Kind is null) ||
                stored.MaximumRisk is null ||
                stored.Entrypoint is null)
                throw new JsonException("Capability manifest의 필수 값이 누락되었습니다.");
            return new CapabilityManifest(
                stored.SchemaVersion,
                CapabilityId.Parse(stored.Id),
                CapabilitySemanticVersion.Parse(stored.Version),
                stored.DisplayName,
                stored.Description,
                ParseKind(stored.Kind),
                stored.IntentExamples.Select(value => value!).ToArray(),
                stored.RequiredTools.Select(value => value!).ToArray(),
                stored.RequiredConnections
                    .Select(requirement => new CapabilityConnectionRequirement(
                        requirement!.Slot!,
                        requirement.Kind!))
                    .ToArray(),
                stored.MaximumRisk,
                stored.Entrypoint);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentNullException)
        {
            throw new InvalidDataException("저장된 Capability manifest가 손상되었습니다.", exception);
        }
    }

    private static bool ManifestMatchesPayload(CapabilityVersionPayload payload)
    {
        try
        {
            var parsed = ReadManifest(payload.CanonicalManifestJson);
            var manifest = payload.Manifest;
            return parsed.SchemaVersion == manifest.SchemaVersion &&
                   parsed.Id.Equals(manifest.Id) &&
                   parsed.Version.Equals(manifest.Version) &&
                   StringComparer.Ordinal.Equals(parsed.DisplayName, manifest.DisplayName) &&
                   StringComparer.Ordinal.Equals(parsed.Description, manifest.Description) &&
                   parsed.Kind == manifest.Kind &&
                   parsed.IntentExamples.SequenceEqual(manifest.IntentExamples, StringComparer.Ordinal) &&
                   parsed.RequiredTools.SequenceEqual(manifest.RequiredTools, StringComparer.Ordinal) &&
                   parsed.RequiredConnections.SequenceEqual(manifest.RequiredConnections) &&
                   StringComparer.Ordinal.Equals(parsed.MaximumRisk, manifest.MaximumRisk) &&
                   StringComparer.Ordinal.Equals(parsed.Entrypoint, manifest.Entrypoint);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool Equivalent(CapabilityDraft existing, ValidatedCapabilityDraft proposed) =>
        existing.DraftId == proposed.DraftId &&
        existing.ExactVersion.Equals(proposed.ExactVersion) &&
        Equals(existing.Parent, proposed.Parent) &&
        existing.SourceKind == proposed.SourceKind &&
        existing.SourcePackageHash.Equals(proposed.SourcePackageHash) &&
        existing.Payload.CanonicalManifestJson == proposed.Payload.CanonicalManifestJson &&
        existing.Payload.Instructions == proposed.Payload.Instructions &&
        existing.Payload.CanonicalInputSchemaJson == proposed.Payload.CanonicalInputSchemaJson &&
        existing.Payload.CanonicalSettingsSchemaJson == proposed.Payload.CanonicalSettingsSchemaJson &&
        existing.Payload.CanonicalWorkflowJson == proposed.Payload.CanonicalWorkflowJson &&
        existing.Payload.CanonicalTestsJson == proposed.Payload.CanonicalTestsJson;

    private static void EnsureManifestIdentity(CapabilityManifest manifest, CapabilityVersionRef exactVersion)
    {
        if (!manifest.Id.Equals(exactVersion.Id) || !manifest.Version.Equals(exactVersion.Version))
            throw new InvalidDataException("Capability manifest identity와 exact version이 일치하지 않습니다.");
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string BuildFtsQuery(string value) =>
        string.Join(
            " OR ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(term => term.Length >= 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(32)
                .Select(term => $"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));

    private static string FormatKind(CapabilityKind kind) => kind switch
    {
        CapabilityKind.Prompt => "prompt",
        CapabilityKind.Workflow => "workflow",
        CapabilityKind.Connector => "connector",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static CapabilityKind ParseKind(string value) => value switch
    {
        "prompt" => CapabilityKind.Prompt,
        "workflow" => CapabilityKind.Workflow,
        "connector" => CapabilityKind.Connector,
        _ => throw new InvalidDataException("지원하지 않는 Capability kind입니다."),
    };

    private static string FormatDraftState(CapabilityDraftState state) => state switch
    {
        CapabilityDraftState.Draft => "draft",
        CapabilityDraftState.Validating => "validating",
        CapabilityDraftState.ReadyForReview => "ready_for_review",
        CapabilityDraftState.Applied => "applied",
        CapabilityDraftState.Rejected => "rejected",
        CapabilityDraftState.Stale => "stale",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static CapabilityDraftState ParseDraftState(string value) => value switch
    {
        "draft" => CapabilityDraftState.Draft,
        "validating" => CapabilityDraftState.Validating,
        "ready_for_review" => CapabilityDraftState.ReadyForReview,
        "applied" => CapabilityDraftState.Applied,
        "rejected" => CapabilityDraftState.Rejected,
        "stale" => CapabilityDraftState.Stale,
        _ => throw new InvalidDataException("지원하지 않는 Capability draft 상태입니다."),
    };

    private static void ValidateDraftId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            throw new ArgumentException("Capability draft ID가 올바르지 않습니다.", nameof(value));
    }

    private Task WithConnectionAsync(
        Func<SqliteConnection, Task> operation,
        CancellationToken cancellationToken) =>
        database.WithConnectionAsync(operation, cancellationToken);

    private static Task<int> ExecuteAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        System.Data.Common.DbTransaction? transaction = null,
        params (string Name, object? Value)[] parameters) =>
        ConversationDatabase.ExecuteAsync(connection, commandText, cancellationToken, transaction, parameters);

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record StoredDraft(CapabilityDraft Draft, long ExpectedCatalogRevision);

    private sealed record StoredManifest(
        int SchemaVersion,
        string? Id,
        string? Version,
        string? DisplayName,
        string? Description,
        string? Kind,
        IReadOnlyList<string?>? IntentExamples,
        IReadOnlyList<string?>? RequiredTools,
        IReadOnlyList<StoredConnectionRequirement?>? RequiredConnections,
        string? MaximumRisk,
        string? Entrypoint);

    private sealed record StoredConnectionRequirement(string? Slot, string? Kind);
}
