using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Domain;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Tests;

public sealed class CapabilityCatalogRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public CapabilityCatalogRepositoryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task MigratesSchemaElevenToTwentyWithMetadataOnlyCandidateIndex()
    {
        var path = DatabasePath("migration.db");
        await using (var legacy = Open(path))
        {
            await legacy.OpenAsync();
            await ExecuteAsync(
                legacy,
                """
                CREATE TABLE schema_migrations(
                    version INTEGER NOT NULL PRIMARY KEY,
                    applied_at_utc TEXT NOT NULL);
                CREATE TABLE conversations(id TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE conversation_runs(
                    run_id TEXT NOT NULL PRIMARY KEY,
                    conversation_id TEXT NOT NULL);
                INSERT INTO schema_migrations VALUES (11, '2026-08-02T00:00:00.0000000+00:00');
                """);
        }

        using (var store = new ConversationStore(path)) await store.InitializeAsync();

        await using var connection = Open(path);
        await connection.OpenAsync();
        Assert.Equal(21L, await ScalarInt64Async(connection, "SELECT MAX(version) FROM schema_migrations;"));
        var tables = await ReadStringsAsync(
            connection,
            """
            SELECT name FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('capability_activations', 'capability_drafts', 'capability_versions')
            ORDER BY name;
            """);
        Assert.Equal(
            ["capability_activations", "capability_drafts", "capability_versions"],
            tables);
        Assert.Equal(1L, await ScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'capabilities';"));

        var columns = new List<string>();
        foreach (var table in tables.Append("capabilities"))
            columns.AddRange(await ReadStringsAsync(connection, $"SELECT name FROM pragma_table_info('{table}');"));
        Assert.DoesNotContain(columns, column => column.Contains("credential", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("raw_package", columns);
        Assert.DoesNotContain("source_path", columns);
        Assert.Equal(
            ["capability_id", "version", "content_hash", "display_name", "description", "intent_examples"],
            await ReadStringsAsync(connection, "SELECT name FROM pragma_table_info('capability_search');"));
        var searchSql = Assert.Single(await ReadStringsAsync(
            connection,
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'capability_search';"));
        Assert.Contains("tokenize='trigram'", searchSql, StringComparison.Ordinal);
        Assert.DoesNotContain("instructions", searchSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("schema", searchSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", searchSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source", searchSql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task RestoresExactActiveVersionAndHashAfterRestart()
    {
        var path = DatabasePath("restart.db");
        var draft = Draft("draft-v1", "1.0.0");
        CapabilityVersionRef exactVersion;
        using (var store = new ConversationStore(path))
        {
            await store.InitializeAsync();
            var created = await store.Capabilities.CreateValidatedDraftAsync(
                draft, Timestamp(0), CancellationToken.None);
            Assert.Equal(CapabilityMutationOutcome.Succeeded, created.Outcome);
            var activated = await store.Capabilities.ApproveAndActivateAsync(
                Approval(draft, 0, null, Timestamp(1)), CancellationToken.None);
            Assert.Equal(CapabilityMutationOutcome.Succeeded, activated.Outcome);
            exactVersion = activated.Catalog!.ActiveVersion!;
        }

        using var reopened = new ConversationStore(path);
        await reopened.InitializeAsync();
        var catalog = Assert.IsType<CapabilityCatalogEntry>(
            await reopened.Capabilities.GetCatalogAsync(exactVersion.Id, CancellationToken.None));
        var version = Assert.IsType<CapabilityVersion>(
            await reopened.Capabilities.GetVersionAsync(exactVersion, CancellationToken.None));
        var storedDraft = Assert.IsType<CapabilityDraft>(
            await reopened.Capabilities.GetDraftAsync("draft-v1", CancellationToken.None));

        Assert.Equal(exactVersion, catalog.ActiveVersion);
        Assert.Equal(1, catalog.CatalogRevision);
        Assert.Equal(exactVersion, version.ExactVersion);
        Assert.Equal("Version 1.0.0 instructions", version.Payload.Instructions);
        Assert.Equal(CapabilityDraftState.Applied, storedDraft.State);
        Assert.Equal(1, storedDraft.Revision);
        Assert.Single(await reopened.Capabilities.ListActivationsAsync(
            exactVersion.Id, 10, CancellationToken.None));
    }

    [Fact]
    public async Task ActivatesV1ThenV2AndRollsBackOnlyToThePastExactTarget()
    {
        using var store = CreateStore("rollback.db");
        await store.InitializeAsync();
        var v1 = Draft("draft-v1", "1.0.0");
        await CreateAndApproveAsync(store, v1, 0, null, Timestamp(0));

        var v2 = Draft("draft-v2", "2.0.0", v1.ExactVersion);
        await CreateAndApproveAsync(store, v2, 1, v1.ExactVersion, Timestamp(2));

        var beforeRollback = Assert.IsType<CapabilityCatalogEntry>(
            await store.Capabilities.GetCatalogAsync(v1.ExactVersion.Id, CancellationToken.None));
        var rolledBack = await store.Capabilities.RollbackAsync(
            new CapabilityRollbackCommand(
                v1.ExactVersion.Id,
                2,
                v2.ExactVersion,
                v1.ExactVersion,
                "review.rollback",
                Timestamp(4)),
            CancellationToken.None);

        Assert.Equal(v2.ExactVersion, beforeRollback.ActiveVersion);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, rolledBack.Outcome);
        Assert.Equal(v1.ExactVersion, rolledBack.Catalog!.ActiveVersion);
        Assert.Equal(3, rolledBack.Catalog.CatalogRevision);
        var history = await store.Capabilities.ListActivationsAsync(
            v1.ExactVersion.Id, 10, CancellationToken.None);
        Assert.Equal([3L, 2L, 1L], history.Select(item => item.CatalogRevision));
        Assert.Equal("rollback", history[0].Action);

        var unavailable = v1.ExactVersion with
        {
            ContentHash = CapabilityContentHash.Parse(
                "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
        };
        var rejected = await store.Capabilities.RollbackAsync(
            new CapabilityRollbackCommand(
                v1.ExactVersion.Id,
                3,
                v1.ExactVersion,
                unavailable,
                "review.rollback",
                Timestamp(5)),
            CancellationToken.None);
        Assert.Equal(CapabilityMutationOutcome.NotFound, rejected.Outcome);
        Assert.Equal(3, (await store.Capabilities.GetCatalogAsync(
            v1.ExactVersion.Id, CancellationToken.None))!.CatalogRevision);
    }

    [Fact]
    public async Task DuplicateAndStaleCommandsHaveFixedOutcomes()
    {
        using var store = CreateStore("outcomes.db");
        await store.InitializeAsync();
        var v1 = Draft("draft-v1", "1.0.0");
        var firstCreate = await store.Capabilities.CreateValidatedDraftAsync(
            v1, Timestamp(0), CancellationToken.None);
        var duplicateCreate = await store.Capabilities.CreateValidatedDraftAsync(
            v1, Timestamp(0), CancellationToken.None);
        var firstActivation = await store.Capabilities.ApproveAndActivateAsync(
            Approval(v1, 0, null, Timestamp(1)), CancellationToken.None);
        var duplicateActivation = await store.Capabilities.ApproveAndActivateAsync(
            Approval(v1, 0, null, Timestamp(1)), CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.Succeeded, firstCreate.Outcome);
        Assert.Equal(CapabilityMutationOutcome.Duplicate, duplicateCreate.Outcome);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, firstActivation.Outcome);
        Assert.Equal(CapabilityMutationOutcome.Duplicate, duplicateActivation.Outcome);

        var v2 = Draft("draft-v2", "2.0.0", v1.ExactVersion);
        var v3 = Draft("draft-v3", "3.0.0", v1.ExactVersion);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, (
            await store.Capabilities.CreateValidatedDraftAsync(v2, Timestamp(2), CancellationToken.None)).Outcome);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, (
            await store.Capabilities.CreateValidatedDraftAsync(v3, Timestamp(2), CancellationToken.None)).Outcome);
        await CreateAndApproveExistingAsync(store, v2, 1, v1.ExactVersion, Timestamp(3));

        var stale = await store.Capabilities.ApproveAndActivateAsync(
            Approval(v3, 2, v2.ExactVersion, Timestamp(4)), CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.Stale, stale.Outcome);
        Assert.Equal(CapabilityDraftState.Stale, (await store.Capabilities.GetDraftAsync(
            v3.DraftId, CancellationToken.None))!.State);
        Assert.Equal(v2.ExactVersion, (await store.Capabilities.GetCatalogAsync(
            v1.ExactVersion.Id, CancellationToken.None))!.ActiveVersion);
    }

    [Fact]
    public async Task DuplicateExactPackageWithAnotherDraftIdHasFixedDuplicateOutcome()
    {
        using var store = CreateStore("exact-draft-duplicate.db");
        await store.InitializeAsync();
        var first = Draft("draft-original", "1.0.0");
        var duplicate = first with { DraftId = "draft-copy" };

        var firstResult = await store.Capabilities.CreateValidatedDraftAsync(
            first, Timestamp(0), CancellationToken.None);
        var duplicateResult = await store.Capabilities.CreateValidatedDraftAsync(
            duplicate, Timestamp(1), CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.Succeeded, firstResult.Outcome);
        Assert.Equal(CapabilityMutationOutcome.Duplicate, duplicateResult.Outcome);
        Assert.Equal("capability.draft.exact_duplicate", duplicateResult.ErrorCode);
        Assert.Equal(first.DraftId, duplicateResult.Draft?.DraftId);
        Assert.Null(await store.Capabilities.GetDraftAsync(duplicate.DraftId, CancellationToken.None));
    }

    [Fact]
    public async Task ApprovalMustBindTheReviewedExactDraftVersion()
    {
        using var store = CreateStore("approval-exact-binding.db");
        await store.InitializeAsync();
        var draft = Draft("draft-v1", "1.0.0");
        Assert.Equal(CapabilityMutationOutcome.Succeeded, (
            await store.Capabilities.CreateValidatedDraftAsync(
                draft, Timestamp(0), CancellationToken.None)).Outcome);
        var wrongExact = draft.ExactVersion with
        {
            ContentHash = CapabilityContentHash.Parse($"sha256:{new string('f', 64)}"),
        };

        var result = await store.Capabilities.ApproveAndActivateAsync(
            new CapabilityApprovalCommand(
                draft.DraftId,
                wrongExact,
                0,
                0,
                null,
                "review.approved",
                Timestamp(1)),
            CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.Stale, result.Outcome);
        Assert.Equal("capability.approval.compare_failed", result.ErrorCode);
        Assert.Null((await store.Capabilities.GetCatalogAsync(
            draft.ExactVersion.Id, CancellationToken.None))!.ActiveVersion);
        Assert.Null(await store.Capabilities.GetVersionAsync(draft.ExactVersion, CancellationToken.None));
        Assert.Equal(CapabilityDraftState.ReadyForReview, (await store.Capabilities.GetDraftAsync(
            draft.DraftId, CancellationToken.None))!.State);
        Assert.Empty(await store.Capabilities.ListActivationsAsync(
            draft.ExactVersion.Id, 10, CancellationToken.None));
    }

    [Fact]
    public async Task DraftPayloadAndIdentityRejectUpdateAndDraftRejectsDelete()
    {
        var path = DatabasePath("draft-immutable.db");
        var draft = Draft("draft-v1", "1.0.0");
        using (var store = new ConversationStore(path))
        {
            await store.InitializeAsync();
            Assert.Equal(CapabilityMutationOutcome.Succeeded, (
                await store.Capabilities.CreateValidatedDraftAsync(
                    draft, Timestamp(0), CancellationToken.None)).Outcome);
        }

        await using var connection = Open(path);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "UPDATE capability_drafts SET instructions_markdown = 'tampered' WHERE id = 'draft-v1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "UPDATE capability_drafts SET expected_catalog_revision = 9 WHERE id = 'draft-v1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "DELETE FROM capability_drafts WHERE id = 'draft-v1';"));

        Assert.Equal(
            "Version 1.0.0 instructions",
            Assert.Single(await ReadStringsAsync(
                connection,
                "SELECT instructions_markdown FROM capability_drafts WHERE id = 'draft-v1';")));
        Assert.Equal(1L, await ScalarInt64Async(
            connection, "SELECT COUNT(*) FROM capability_drafts WHERE id = 'draft-v1';"));
    }

    [Theory]
    [InlineData("$.intentExamples")]
    [InlineData("$.displayName")]
    public async Task MalformedStoredManifestNullsFailAsInvalidData(string jsonPath)
    {
        var path = DatabasePath($"malformed-{jsonPath[2..]}.db");
        using var store = new ConversationStore(path);
        await store.InitializeAsync();
        var draft = Draft("draft-v1", "1.0.0");
        Assert.Equal(CapabilityMutationOutcome.Succeeded, (
            await store.Capabilities.CreateValidatedDraftAsync(
                draft, Timestamp(0), CancellationToken.None)).Outcome);
        await using (var sabotage = Open(path))
        {
            await sabotage.OpenAsync();
            await ExecuteAsync(sabotage, "DROP TRIGGER capability_drafts_reject_payload_update;");
            await ExecuteAsync(
                sabotage,
                $"UPDATE capability_drafts SET manifest_json = json_set(manifest_json, '{jsonPath}', json('null'));");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.Capabilities.GetDraftAsync(draft.DraftId, CancellationToken.None));
    }

    [Fact]
    public async Task ImmutableVersionsAndAppendOnlyActivationsRejectUpdateAndDelete()
    {
        var path = DatabasePath("immutable.db");
        var draft = Draft("draft-v1", "1.0.0");
        using (var store = new ConversationStore(path))
        {
            await store.InitializeAsync();
            await CreateAndApproveAsync(store, draft, 0, null, Timestamp(0));
        }

        await using var connection = Open(path);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;");
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "UPDATE capability_versions SET instructions_markdown = 'tampered';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "DELETE FROM capability_versions;"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "UPDATE capability_activations SET reason_code = 'tampered';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "DELETE FROM capability_activations;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM capability_versions;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM capability_activations;"));
    }

    [Fact]
    public async Task ActivationFailureRollsBackVersionPointerLedgerAndDraftState()
    {
        var path = DatabasePath("transaction.db");
        using var store = new ConversationStore(path);
        await store.InitializeAsync();
        var draft = Draft("draft-v1", "1.0.0");
        Assert.Equal(CapabilityMutationOutcome.Succeeded, (
            await store.Capabilities.CreateValidatedDraftAsync(draft, Timestamp(0), CancellationToken.None)).Outcome);
        await using (var sabotage = Open(path))
        {
            await sabotage.OpenAsync();
            await ExecuteAsync(
                sabotage,
                """
                CREATE TRIGGER fixture_fail_activation
                BEFORE INSERT ON capability_activations BEGIN
                    SELECT RAISE(ABORT, 'fixture activation failure');
                END;
                """);
        }

        await Assert.ThrowsAsync<SqliteException>(() => store.Capabilities.ApproveAndActivateAsync(
            Approval(draft, 0, null, Timestamp(1)), CancellationToken.None));

        var catalog = Assert.IsType<CapabilityCatalogEntry>(
            await store.Capabilities.GetCatalogAsync(draft.ExactVersion.Id, CancellationToken.None));
        var storedDraft = Assert.IsType<CapabilityDraft>(
            await store.Capabilities.GetDraftAsync(draft.DraftId, CancellationToken.None));
        Assert.Null(catalog.ActiveVersion);
        Assert.Equal(0, catalog.CatalogRevision);
        Assert.Null(await store.Capabilities.GetVersionAsync(draft.ExactVersion, CancellationToken.None));
        Assert.Equal(CapabilityDraftState.ReadyForReview, storedDraft.State);
        Assert.Equal(0, storedDraft.Revision);
        Assert.Empty(await store.Capabilities.ListActivationsAsync(
            draft.ExactVersion.Id, 10, CancellationToken.None));
    }

    [Fact]
    public async Task ActivationRehashRejectsTamperedDraftWithoutAnyActivationChanges()
    {
        var path = DatabasePath("draft-rehash.db");
        using var store = new ConversationStore(path);
        await store.InitializeAsync();
        var draft = Draft("draft-v1", "1.0.0");
        Assert.Equal(CapabilityMutationOutcome.Succeeded, (
            await store.Capabilities.CreateValidatedDraftAsync(
                draft, Timestamp(0), CancellationToken.None)).Outcome);
        await using (var sabotage = Open(path))
        {
            await sabotage.OpenAsync();
            await ExecuteAsync(
                sabotage,
                "DROP TRIGGER capability_drafts_reject_payload_update;");
            await ExecuteAsync(
                sabotage,
                "UPDATE capability_drafts SET instructions_markdown = 'tampered' WHERE id = 'draft-v1';");
        }

        var result = await store.Capabilities.ApproveAndActivateAsync(
            Approval(draft, 0, null, Timestamp(1)), CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.Invalid, result.Outcome);
        Assert.Equal("capability.draft.content_hash_mismatch", result.ErrorCode);
        var catalog = Assert.IsType<CapabilityCatalogEntry>(
            await store.Capabilities.GetCatalogAsync(draft.ExactVersion.Id, CancellationToken.None));
        var storedDraft = Assert.IsType<CapabilityDraft>(
            await store.Capabilities.GetDraftAsync(draft.DraftId, CancellationToken.None));
        Assert.Null(catalog.ActiveVersion);
        Assert.Equal(0, catalog.CatalogRevision);
        Assert.Null(await store.Capabilities.GetVersionAsync(draft.ExactVersion, CancellationToken.None));
        Assert.Equal(CapabilityDraftState.ReadyForReview, storedDraft.State);
        Assert.Equal(0, storedDraft.Revision);
        Assert.Equal("tampered", storedDraft.Payload.Instructions);
        Assert.Empty(await store.Capabilities.ListActivationsAsync(
            draft.ExactVersion.Id, 10, CancellationToken.None));
    }

    [Fact]
    public async Task CatalogServiceRejectsSecretLiteralBeforeAnyCapabilityRowIsStored()
    {
        var path = DatabasePath("secret.db");
        using (var store = new ConversationStore(path))
        {
            await store.InitializeAsync();
            var service = new CapabilityCatalogService(store.Capabilities, new EmptyDependencyCatalog());
            var draft = Draft(
                "draft-secret",
                "1.0.0",
                instructions: "Authorization: Bearer abcdefghijklmnop");

            var result = await service.SubmitValidatedDraftAsync(
                draft, Timestamp(0), CancellationToken.None);

            Assert.Equal(CapabilityMutationOutcome.Invalid, result.Outcome);
            Assert.Equal("capability.secret_literal.detected", result.ErrorCode);
        }

        await using var connection = Open(path);
        await connection.OpenAsync();
        foreach (var table in new[]
                 {
                     "capabilities",
                     "capability_drafts",
                     "capability_versions",
                     "capability_activations",
                 })
            Assert.Equal(0L, await ScalarInt64Async(connection, $"SELECT COUNT(*) FROM {table};"));
    }

    [Fact]
    public async Task CandidateSearchExcludesInactiveDraftThenFindsActiveDisplayAndIntentOnly()
    {
        using var store = CreateStore("candidate-active.db");
        await store.InitializeAsync();
        Assert.Same(store.Capabilities, store.CapabilityCandidates);
        var draft = Draft(
            "draft-candidate",
            "1.0.0",
            instructions: "instruction-sentinel-never-index",
            displayName: "Calendar Assistant",
            description: "Plans team events",
            intentExamples: ["schedule quarterly briefing"]);

        Assert.Equal(CapabilityMutationOutcome.Succeeded, (
            await store.Capabilities.CreateValidatedDraftAsync(
                draft, Timestamp(0), CancellationToken.None)).Outcome);
        Assert.Empty(await store.CapabilityCandidates.SearchActiveAsync(
            "Calendar", 8, CancellationToken.None));
        Assert.Empty(await store.CapabilityCandidates.SearchActiveAsync(
            "briefing", 8, CancellationToken.None));

        await CreateAndApproveExistingAsync(store, draft, 0, null, Timestamp(1));

        Assert.Equal(draft.ExactVersion, Assert.Single(
            await store.CapabilityCandidates.SearchActiveAsync(
                "Calendar", 8, CancellationToken.None)));
        Assert.Equal(draft.ExactVersion, Assert.Single(
            await store.CapabilityCandidates.SearchActiveAsync(
                "briefing", 8, CancellationToken.None)));
        Assert.Equal(draft.ExactVersion, Assert.Single(
            await store.CapabilityCandidates.SearchActiveAsync(
                "Could you schedule the quarterly briefing now", 8, CancellationToken.None)));
        Assert.Empty(await store.CapabilityCandidates.SearchActiveAsync(
            "instruction-sentinel", 8, CancellationToken.None));
    }

    [Fact]
    public async Task CandidateIndexTracksActivationAndRollbackWithoutLeakingNextDraftMetadata()
    {
        using var store = CreateStore("candidate-rollback.db");
        await store.InitializeAsync();
        var v1 = Draft(
            "draft-legacy",
            "1.0.0",
            displayName: "Legacy Planner",
            description: "Handles legacy calendar requests",
            intentExamples: ["legacy planning"]);
        await CreateAndApproveAsync(store, v1, 0, null, Timestamp(0));

        var v2 = Draft(
            "draft-modern",
            "2.0.0",
            v1.ExactVersion,
            displayName: "Modern Planner",
            description: "Handles modern calendar requests",
            intentExamples: ["modern planning"]);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, (
            await store.Capabilities.CreateValidatedDraftAsync(
                v2, Timestamp(2), CancellationToken.None)).Outcome);

        Assert.Equal(v1.ExactVersion, Assert.Single(
            await store.CapabilityCandidates.SearchActiveAsync(
                "legacy", 8, CancellationToken.None)));
        Assert.Empty(await store.CapabilityCandidates.SearchActiveAsync(
            "modern", 8, CancellationToken.None));

        await CreateAndApproveExistingAsync(store, v2, 1, v1.ExactVersion, Timestamp(3));
        Assert.Empty(await store.CapabilityCandidates.SearchActiveAsync(
            "legacy", 8, CancellationToken.None));
        Assert.Equal(v2.ExactVersion, Assert.Single(
            await store.CapabilityCandidates.SearchActiveAsync(
                "modern", 8, CancellationToken.None)));

        var rollback = await store.Capabilities.RollbackAsync(
            new CapabilityRollbackCommand(
                v1.ExactVersion.Id,
                2,
                v2.ExactVersion,
                v1.ExactVersion,
                "review.rollback",
                Timestamp(4)),
            CancellationToken.None);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, rollback.Outcome);
        Assert.Equal(v1.ExactVersion, Assert.Single(
            await store.CapabilityCandidates.SearchActiveAsync(
                "legacy", 8, CancellationToken.None)));
        Assert.Empty(await store.CapabilityCandidates.SearchActiveAsync(
            "modern", 8, CancellationToken.None));
    }

    [Fact]
    public async Task CandidateMigrationBackfillsExistingActiveVersion()
    {
        var path = DatabasePath("candidate-backfill.db");
        var draft = Draft(
            "draft-backfill",
            "1.0.0",
            displayName: "Backfill Assistant",
            intentExamples: ["recover indexed metadata"]);
        using (var store = new ConversationStore(path))
        {
            await store.InitializeAsync();
            await CreateAndApproveAsync(store, draft, 0, null, Timestamp(0));
        }

        await using (var schemaTwelve = Open(path))
        {
            await schemaTwelve.OpenAsync();
            await ExecuteAsync(
                schemaTwelve,
                """
                PRAGMA foreign_keys=OFF;
                DROP TABLE capability_smtp_bindings;
                DROP TABLE smtp_probe_results;
                DROP TABLE smtp_connection_profiles;
                DROP TABLE capability_test_runs;
                DROP TABLE improvement_proposals;
                DROP TABLE feature_builder_sessions;
                DROP TABLE capability_connection_bindings;
                DROP TABLE connection_probe_results;
                DROP TABLE http_connection_profiles;
                DROP TABLE capability_mail_gateway_bindings;
                DROP TABLE mail_gateway_probe_results;
                DROP TABLE mail_gateway_connection_profiles;
                DROP TABLE mail_outbox_operations;
                DROP TABLE connection_profiles;
                DROP TRIGGER workflow_ticket_item_require_matching_step;
                DROP TABLE workflow_step_runs;
                DROP TABLE workflow_authorization_ticket_items;
                DROP TABLE workflow_authorization_tickets;
                DROP TABLE workflow_runs;
                DROP TRIGGER capabilities_search_active_update;
                DROP TABLE capability_search;
                DELETE FROM schema_migrations WHERE version IN (13, 14, 15, 16, 17, 18, 19, 20, 21);
                PRAGMA foreign_keys=ON;
                """);
        }

        using var reopened = new ConversationStore(path);
        await reopened.InitializeAsync();
        Assert.Equal(draft.ExactVersion, Assert.Single(
            await reopened.CapabilityCandidates.SearchActiveAsync(
                "Backfill", 8, CancellationToken.None)));
    }

    [Fact]
    public async Task CandidateSearchReturnsDeterministicMaximumEightAfterRestart()
    {
        var path = DatabasePath("candidate-limit.db");
        var expected = new List<CapabilityVersionRef>();
        using (var store = new ConversationStore(path))
        {
            await store.InitializeAsync();
            for (var index = 0; index < 10; index++)
            {
                var id = $"sample.candidate{index:D2}";
                var draft = Draft(
                    $"draft-candidate-{index:D2}",
                    "1.0.0",
                    capabilityId: id,
                    displayName: "Shared Candidate",
                    description: "Common deterministic retrieval",
                    intentExamples: ["find shared candidate"]);
                await CreateAndApproveAsync(store, draft, 0, null, Timestamp(index));
                expected.Add(draft.ExactVersion);
            }
        }

        using var reopened = new ConversationStore(path);
        await reopened.InitializeAsync();
        var matches = await reopened.CapabilityCandidates.SearchActiveAsync(
            "Shared", 8, CancellationToken.None);

        Assert.Equal(8, matches.Count);
        Assert.Equal(expected.Take(8), matches);
    }

    [Fact]
    public async Task CandidateSearchRejectsBroadOrUnsafeInputsWithoutRunningFtsSyntax()
    {
        using var store = CreateStore("candidate-input.db");
        await store.InitializeAsync();
        var draft = Draft("draft-input", "1.0.0");
        await CreateAndApproveAsync(store, draft, 0, null, Timestamp(0));

        Assert.Empty(await store.CapabilityCandidates.SearchActiveAsync(
            "Absent\" OR *", 8, CancellationToken.None));
        Assert.Empty(await store.CapabilityCandidates.SearchActiveAsync(
            "ec", 8, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.CapabilityCandidates.SearchActiveAsync(
                "Echo\nbriefing", 8, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.CapabilityCandidates.SearchActiveAsync(
                new string('a', 257), 8, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.CapabilityCandidates.SearchActiveAsync(
                "Echo", 9, CancellationToken.None));
    }

    private ConversationStore CreateStore(string name) => new(DatabasePath(name));

    private string DatabasePath(string name) => Path.Combine(_directory, name);

    private static ValidatedCapabilityDraft Draft(
        string draftId,
        string version,
        CapabilityVersionRef? parent = null,
        string? instructions = null,
        string capabilityId = "sample.echo",
        string displayName = "Echo",
        string description = "Echo prompt",
        IReadOnlyList<string>? intentExamples = null)
    {
        var id = CapabilityId.Parse(capabilityId);
        var semanticVersion = CapabilitySemanticVersion.Parse(version);
        var intents = intentExamples ?? ["echo"];
        var manifest = new CapabilityManifest(
            1,
            id,
            semanticVersion,
            displayName,
            description,
            CapabilityKind.Prompt,
            intents,
            [],
            [],
            "R0",
            "instructions.md");
        var canonicalManifest = JsonSerializer.Serialize(new
        {
            description,
            displayName,
            entrypoint = "instructions.md",
            id = capabilityId,
            intentExamples = intents,
            kind = "prompt",
            maximumRisk = "R0",
            requiredConnections = Array.Empty<object>(),
            requiredTools = Array.Empty<string>(),
            schemaVersion = 1,
            version,
        });
        var payload = new CapabilityVersionPayload(
            manifest,
            canonicalManifest,
            instructions ?? $"Version {version} instructions",
            "{\"additionalProperties\":false,\"type\":\"object\"}");
        var hash = CapabilityContentHasher.Compute(payload);
        return new ValidatedCapabilityDraft(draftId, payload, hash, parent, "local_import", hash);
    }

    private static CapabilityApprovalCommand Approval(
        ValidatedCapabilityDraft draft,
        long expectedCatalogRevision,
        CapabilityVersionRef? expectedActive,
        DateTimeOffset nowUtc) =>
        new(
            draft.DraftId,
            draft.ExactVersion,
            0,
            expectedCatalogRevision,
            expectedActive,
            "review.approved",
            nowUtc);

    private static async Task CreateAndApproveAsync(
        ConversationStore store,
        ValidatedCapabilityDraft draft,
        long expectedCatalogRevision,
        CapabilityVersionRef? expectedActive,
        DateTimeOffset nowUtc)
    {
        Assert.Equal(CapabilityMutationOutcome.Succeeded, (
            await store.Capabilities.CreateValidatedDraftAsync(draft, nowUtc, CancellationToken.None)).Outcome);
        await CreateAndApproveExistingAsync(
            store, draft, expectedCatalogRevision, expectedActive, nowUtc.AddMinutes(1));
    }

    private static async Task CreateAndApproveExistingAsync(
        ConversationStore store,
        ValidatedCapabilityDraft draft,
        long expectedCatalogRevision,
        CapabilityVersionRef? expectedActive,
        DateTimeOffset nowUtc)
    {
        var result = await store.Capabilities.ApproveAndActivateAsync(
            Approval(draft, expectedCatalogRevision, expectedActive, nowUtc),
            CancellationToken.None);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, result.Outcome);
    }

    private static DateTimeOffset Timestamp(int minute) =>
        DateTimeOffset.Parse("2026-08-02T00:00:00Z").AddMinutes(minute);

    private static SqliteConnection Open(string path) => new($"Data Source={path};Pooling=False");

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarInt64Async(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(SqliteConnection connection, string sql)
    {
        var results = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) results.Add(reader.GetString(0));
        return results;
    }

    private sealed class EmptyDependencyCatalog : ICapabilityDependencyCatalog
    {
        public bool TryGetTool(string name, out CapabilityToolDependency? dependency)
        {
            dependency = null;
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (Exception)
        {
        }
    }
}
