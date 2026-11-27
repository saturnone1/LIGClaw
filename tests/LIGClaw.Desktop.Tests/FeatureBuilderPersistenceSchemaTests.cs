using LIGClaw.Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Tests;

public sealed class FeatureBuilderPersistenceSchemaTests : IDisposable
{
    private const string SessionId = "fbs_0123456789abcdef0123456789abcdef";
    private const string ProposalId = "proposal_0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string GenerationId = "fbgen_0123456789abcdef0123456789abcdef";
    private const string Timestamp = "2026-08-02T03:00:00.0000000+00:00";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public FeatureBuilderPersistenceSchemaTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task InitializesSchemaTwentyOneWithSecretFreeFeatureBuilderTables()
    {
        var path = DatabasePath("schema.db");
        await InitializeAsync(path);

        await using var connection = await OpenAsync(path);
        Assert.Equal(21L, await ScalarAsync(
            connection,
            "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(
            ["capability_test_runs", "feature_builder_sessions", "improvement_proposals"],
            await StringsAsync(
                connection,
                """
                SELECT name
                FROM sqlite_master
                WHERE type = 'table'
                  AND name IN (
                    'feature_builder_sessions',
                    'improvement_proposals',
                    'capability_test_runs')
                ORDER BY name;
                """));

        var columns = new List<string>();
        foreach (var table in new[]
                 {
                     "feature_builder_sessions",
                     "improvement_proposals",
                     "capability_test_runs",
                 })
        {
            columns.AddRange(await StringsAsync(
                connection,
                $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid;"));
        }

        Assert.DoesNotContain(
            columns,
            column =>
                column.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                column.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                column.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                column.Contains("credential", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task MigratesSchemaTwentyToTwentyOneAndPreservesRowsIndicesTriggersAndReopen()
    {
        var path = DatabasePath("schema-20-to-21.db");
        await InitializeAsync(path);
        await using (var connection = await OpenAsync(path))
        {
            await InsertSessionAsync(connection, activeGenerationId: GenerationId);
            await InsertProposalAsync(
                connection,
                ProposalId,
                SessionId,
                GenerationId,
                "pending",
                includePayload: true,
                terminalAtUtc: null);
            await InsertPassedTestRunAsync(connection, ProposalId);
            await ExecuteAsync(connection, "DELETE FROM schema_migrations WHERE version = 21;");
        }

        await InitializeAsync(path);

        await using (var migrated = await OpenAsync(path))
        {
            Assert.Equal(21L, await ScalarAsync(
                migrated,
                "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(1L, await ScalarAsync(
                migrated,
                "SELECT COUNT(*) FROM improvement_proposals WHERE id = '" + ProposalId +
                "' AND tests_json = '[]';"));
            Assert.Equal(1L, await ScalarAsync(
                migrated,
                "SELECT COUNT(*) FROM capability_test_runs WHERE proposal_id = '" + ProposalId + "';"));
            Assert.Equal(
                "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc|" +
                "sha256:3333333333333333333333333333333333333333333333333333333333333333|" +
                "pending|0|[]",
                await StringScalarAsync(
                    migrated,
                    """
                    SELECT candidate_content_hash || '|' || proposal_hash || '|' ||
                           status || '|' || revision || '|' || tests_json
                    FROM improvement_proposals;
                    """));
            Assert.Equal(
                "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc|" +
                "passed|0|{}",
                await StringScalarAsync(
                    migrated,
                    """
                    SELECT candidate_content_hash || '|' || status || '|' || revision || '|' || result_json
                    FROM capability_test_runs;
                    """));
            Assert.Equal(
                ["ix_capability_test_runs_proposal", "ix_improvement_proposals_session_status"],
                await StringsAsync(
                    migrated,
                    """
                    SELECT name FROM sqlite_master
                    WHERE type = 'index' AND name IN (
                        'ix_capability_test_runs_proposal',
                        'ix_improvement_proposals_session_status')
                    ORDER BY name;
                    """));
            Assert.Equal(
                [
                    "capability_test_runs_reject_delete",
                    "capability_test_runs_reject_identity_update",
                    "capability_test_runs_reject_terminal_update",
                    "improvement_proposals_reject_delete",
                    "improvement_proposals_reject_evidence_update",
                    "improvement_proposals_reject_payload_update",
                    "improvement_proposals_reject_terminal_update",
                ],
                await StringsAsync(
                    migrated,
                    """
                    SELECT name FROM sqlite_master
                    WHERE type = 'trigger' AND (
                        name LIKE 'improvement_proposals_reject_%' OR
                        name LIKE 'capability_test_runs_reject_%')
                    ORDER BY name;
                    """));
            Assert.Equal(0L, await ScalarAsync(
                migrated,
                "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
            var payload = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
                migrated,
                "UPDATE improvement_proposals SET manifest_json = '{\"changed\":true}' WHERE id = '" +
                ProposalId + "';"));
            Assert.Contains("payload is immutable", payload.Message, StringComparison.Ordinal);
            var evidence = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
                migrated,
                """
                UPDATE improvement_proposals
                SET test_evidence_hash = 'sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'
                WHERE id = 'proposal_0123456789abcdef0123456789abcdef0123456789abcdef';
                """));
            Assert.Contains("evidence is immutable", evidence.Message, StringComparison.Ordinal);
            var testTerminal = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
                migrated,
                """
                UPDATE capability_test_runs SET result_json = '{"changed":true}'
                WHERE id = 'fbtest_0123456789abcdef0123456789abcdef';
                """));
            Assert.Contains("terminal capability test runs are immutable", testTerminal.Message, StringComparison.Ordinal);
            var testDelete = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
                migrated,
                "DELETE FROM capability_test_runs WHERE proposal_id = '" + ProposalId + "';"));
            Assert.Contains("cannot be deleted", testDelete.Message, StringComparison.Ordinal);
            await ExecuteAsync(
                migrated,
                """
                UPDATE improvement_proposals
                SET status = 'rejected', terminal_at_utc = '2026-08-02T03:00:00.0000000+00:00'
                WHERE id = 'proposal_0123456789abcdef0123456789abcdef0123456789abcdef';
                """);
            var proposalTerminal = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
                migrated,
                "UPDATE improvement_proposals SET revision = 1 WHERE id = '" + ProposalId + "';"));
            Assert.Contains("terminal capability proposals are immutable", proposalTerminal.Message, StringComparison.Ordinal);
            var proposalDelete = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
                migrated,
                "DELETE FROM improvement_proposals WHERE id = '" + ProposalId + "';"));
            Assert.Contains("cannot be deleted", proposalDelete.Message, StringComparison.Ordinal);
        }

        await InitializeAsync(path);
        await using var reopened = await OpenAsync(path);
        Assert.Equal(21L, await ScalarAsync(
            reopened,
            "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(1L, await ScalarAsync(
            reopened,
            "SELECT COUNT(*) FROM improvement_proposals WHERE id = '" + ProposalId + "';"));
    }

    [Fact]
    public async Task SchemaTwentyOneMigrationFailureRollsBackAndCanBeRetried()
    {
        var path = DatabasePath("schema-21-rollback.db");
        await InitializeAsync(path);
        await using (var connection = await OpenAsync(path))
        {
            await InsertSessionAsync(connection, activeGenerationId: GenerationId);
            await InsertProposalAsync(
                connection,
                ProposalId,
                SessionId,
                GenerationId,
                "pending",
                includePayload: true,
                terminalAtUtc: null);
            await InsertPassedTestRunAsync(connection, ProposalId);
            await ExecuteAsync(
                connection,
                """
                DELETE FROM schema_migrations WHERE version = 21;
                CREATE TABLE improvement_proposals_schema20(sentinel INTEGER);
                """);
        }

        using (var failing = new ConversationDatabase(path))
        {
            await Assert.ThrowsAsync<SqliteException>(() => failing.InitializeAsync());
        }

        await using (var rolledBack = await OpenAsync(path))
        {
            Assert.Equal(20L, await ScalarAsync(
                rolledBack,
                "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(1L, await ScalarAsync(
                rolledBack,
                "SELECT COUNT(*) FROM improvement_proposals WHERE id = '" + ProposalId + "';"));
            Assert.Equal(1L, await ScalarAsync(
                rolledBack,
                "SELECT COUNT(*) FROM capability_test_runs WHERE proposal_id = '" + ProposalId + "';"));
            Assert.Equal(0L, await ScalarAsync(
                rolledBack,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'capability_test_runs_schema20';"));
            Assert.Equal(7L, await ScalarAsync(
                rolledBack,
                """
                SELECT COUNT(*) FROM sqlite_master
                WHERE type = 'trigger' AND (
                    name LIKE 'improvement_proposals_reject_%' OR
                    name LIKE 'capability_test_runs_reject_%');
                """));
            await ExecuteAsync(rolledBack, "DROP TABLE improvement_proposals_schema20;");
        }

        await InitializeAsync(path);
        await using var recovered = await OpenAsync(path);
        Assert.Equal(21L, await ScalarAsync(
            recovered,
            "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(0L, await ScalarAsync(
            recovered,
            "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task ProposalStorageAcceptsV1ArrayOrStrictSemanticV2RootOnly()
    {
        var path = DatabasePath("tests-json-v2-root.db");
        await InitializeAsync(path);
        await using var connection = await OpenAsync(path);
        await InsertSessionAsync(connection, activeGenerationId: GenerationId);
        foreach (var invalid in new[]
                 {
                     "{}",
                     "{\"schemaVersion\":1,\"cases\":[{}]}",
                     "{\"schemaVersion\":2.0,\"cases\":[{}]}",
                     "{\"schemaVersion\":2,\"cases\":[]}",
                     "{\"schemaVersion\":2,\"cases\":[{}],\"extra\":true}",
                 })
        {
            await Assert.ThrowsAsync<SqliteException>(() => InsertProposalAsync(
                connection,
                ProposalId,
                SessionId,
                GenerationId,
                "pending",
                includePayload: true,
                terminalAtUtc: null,
                testsJson: invalid));
        }

        await InsertProposalAsync(
            connection,
            ProposalId,
            SessionId,
            GenerationId,
            "pending",
            includePayload: true,
            terminalAtUtc: null,
            testsJson: "{\"cases\":[{}],\"schemaVersion\":2}");

        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM improvement_proposals WHERE json_type(tests_json) = 'object';"));
    }

    [Fact]
    public async Task AllowsOnlyQuarantinedProposalsToRemainMetadataOnly()
    {
        var path = DatabasePath("quarantine.db");
        await InitializeAsync(path);
        await using var connection = await OpenAsync(path);
        await InsertSessionAsync(connection, activeGenerationId: GenerationId);

        await InsertProposalAsync(
            connection,
            proposalId: ProposalId,
            sessionId: SessionId,
            generationId: GenerationId,
            status: "quarantined",
            includePayload: false,
            terminalAtUtc: Timestamp);

        Assert.Equal(1L, await ScalarAsync(
            connection,
            """
            SELECT COUNT(*)
            FROM improvement_proposals
            WHERE id = 'proposal_0123456789abcdef0123456789abcdef0123456789abcdef'
              AND status = 'quarantined'
              AND candidate_content_hash IS NULL
              AND manifest_json IS NULL
              AND instructions_markdown IS NULL
              AND input_schema_json IS NULL
              AND tests_json IS NULL;
            """));
    }

    [Theory]
    [InlineData("pending", null)]
    [InlineData("rejected", Timestamp)]
    [InlineData("stale", Timestamp)]
    public async Task RejectsMetadataOnlyNonQuarantinedProposals(string status, string? terminalAtUtc)
    {
        var path = DatabasePath($"payload-{status}.db");
        await InitializeAsync(path);
        await using var connection = await OpenAsync(path);
        await InsertSessionAsync(connection, activeGenerationId: GenerationId);

        await Assert.ThrowsAsync<SqliteException>(() => InsertProposalAsync(
            connection,
            proposalId: ProposalId,
            sessionId: SessionId,
            generationId: GenerationId,
            status,
            includePayload: false,
            terminalAtUtc));
    }

    [Fact]
    public async Task EnforcesForeignKeysAndAppendOnlyProposalAndTestEvidence()
    {
        var path = DatabasePath("integrity.db");
        await InitializeAsync(path);
        await using var connection = await OpenAsync(path);
        await InsertSessionAsync(connection, activeGenerationId: GenerationId);
        await InsertProposalAsync(
            connection,
            proposalId: ProposalId,
            sessionId: SessionId,
            generationId: GenerationId,
            status: "pending",
            includePayload: true,
            terminalAtUtc: null);
        await InsertPassedTestRunAsync(connection, ProposalId);

        await Assert.ThrowsAsync<SqliteException>(() => InsertProposalAsync(
            connection,
            proposalId: "proposal_abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdef",
            sessionId: "fbs_abcdefabcdefabcdefabcdefabcdefab",
            generationId: "fbgen_abcdefabcdefabcdefabcdefabcdefab",
            status: "quarantined",
            includePayload: false,
            terminalAtUtc: Timestamp));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            """
            INSERT INTO capability_test_runs(
                id, proposal_id, kind, attempt, candidate_content_hash, engine_version,
                status, revision, result_json, fixed_error_code, started_at_utc, completed_at_utc)
            VALUES (
                'fbtest_abcdefabcdefabcdefabcdefabcdefab',
                'proposal_abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdef',
                'static_scan', 1,
                'sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
                'd5-static-v1', 'passed', 0, '{}', NULL,
                '2026-08-02T03:00:00.0000000+00:00',
                '2026-08-02T03:00:01.0000000+00:00');
            """));

        var proposalUpdate = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "UPDATE improvement_proposals SET manifest_json = '{\"changed\":true}' WHERE id = '" + ProposalId + "';"));
        Assert.Contains("payload is immutable", proposalUpdate.Message, StringComparison.Ordinal);
        var proposalDelete = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "DELETE FROM improvement_proposals WHERE id = '" + ProposalId + "';"));
        Assert.Contains("cannot be deleted", proposalDelete.Message, StringComparison.Ordinal);

        var testUpdate = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "UPDATE capability_test_runs SET revision = 1 WHERE id = 'fbtest_0123456789abcdef0123456789abcdef';"));
        Assert.Contains("terminal capability test runs are immutable", testUpdate.Message, StringComparison.Ordinal);
        var testDelete = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "DELETE FROM capability_test_runs WHERE id = 'fbtest_0123456789abcdef0123456789abcdef';"));
        Assert.Contains("cannot be deleted", testDelete.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "DELETE FROM feature_builder_sessions WHERE id = '" + SessionId + "';"));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task AcceptsCanonicalBuilderGenerationId()
    {
        var path = DatabasePath("canonical-generation.db");
        await InitializeAsync(path);
        await using var connection = await OpenAsync(path);

        await InsertSessionAsync(connection, activeGenerationId: GenerationId);
        await InsertProposalAsync(
            connection,
            proposalId: ProposalId,
            sessionId: SessionId,
            generationId: GenerationId,
            status: "quarantined",
            includePayload: false,
            terminalAtUtc: Timestamp);

        Assert.Equal(GenerationId, await StringScalarAsync(
            connection,
            "SELECT generation_id FROM improvement_proposals WHERE id = '" + ProposalId + "';"));
    }

    [Theory]
    [InlineData("fbgen_0123456789abcdef0123456789abcde")]
    [InlineData("fbgen_0123456789abcdef0123456789abcdef0")]
    [InlineData("fbgen_0123456789abcdef0123456789abcdeg")]
    [InlineData("fbgen_0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("cpgen_0123456789abcdef0123456789abcdef")]
    public async Task RejectsNonCanonicalBuilderGenerationIds(string generationId)
    {
        var path = DatabasePath($"invalid-generation-{Guid.NewGuid():N}.db");
        await InitializeAsync(path);
        await using var connection = await OpenAsync(path);
        await Assert.ThrowsAsync<SqliteException>(() => InsertSessionAsync(
            connection,
            activeGenerationId: generationId));
        await InsertSessionAsync(connection, activeGenerationId: null);

        await Assert.ThrowsAsync<SqliteException>(() => InsertProposalAsync(
            connection,
            proposalId: ProposalId,
            sessionId: SessionId,
            generationId,
            status: "quarantined",
            includePayload: false,
            terminalAtUtc: Timestamp));
    }

    [Fact]
    public async Task RejectsInvalidJsonShapesAndInconsistentCatalogSnapshots()
    {
        var path = DatabasePath("strict-boundaries.db");
        await InitializeAsync(path);
        await using var connection = await OpenAsync(path);

        await Assert.ThrowsAsync<SqliteException>(() => InsertSessionAsync(
            connection,
            activeGenerationId: GenerationId,
            expectedCatalogRevision: 1));
        await Assert.ThrowsAsync<SqliteException>(() => InsertSessionAsync(
            connection,
            activeGenerationId: GenerationId,
            answersJson: "{}"));
        await InsertSessionAsync(connection, activeGenerationId: GenerationId);

        await Assert.ThrowsAsync<SqliteException>(() => InsertProposalAsync(
            connection,
            proposalId: ProposalId,
            sessionId: SessionId,
            generationId: GenerationId,
            status: "pending",
            includePayload: true,
            terminalAtUtc: null,
            expectedCatalogRevision: 1));
        await Assert.ThrowsAsync<SqliteException>(() => InsertProposalAsync(
            connection,
            proposalId: ProposalId,
            sessionId: SessionId,
            generationId: GenerationId,
            status: "pending",
            includePayload: true,
            terminalAtUtc: null,
            knownLimitationsJson: null));
        await InsertProposalAsync(
            connection,
            proposalId: ProposalId,
            sessionId: SessionId,
            generationId: GenerationId,
            status: "pending",
            includePayload: true,
            terminalAtUtc: null);

        await Assert.ThrowsAsync<SqliteException>(() => InsertPassedTestRunAsync(
            connection,
            ProposalId,
            resultJson: "null"));
        await Assert.ThrowsAsync<SqliteException>(() => InsertPassedTestRunAsync(
            connection,
            ProposalId,
            engineVersion: "bad engine"));
    }

    private static async Task InitializeAsync(string path)
    {
        using var database = new ConversationDatabase(path);
        await database.InitializeAsync();
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;");
        return connection;
    }

    private static async Task InsertSessionAsync(
        SqliteConnection connection,
        string? activeGenerationId,
        long expectedCatalogRevision = 0,
        string answersJson = "[]")
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO feature_builder_sessions(
                id, mode, state, revision, requirement, requirement_hash,
                answers_json, questions_json,
                base_capability_id, base_version, base_content_hash,
                expected_catalog_revision, inventory_hash, active_generation_id,
                fixed_error_code, created_at_utc, updated_at_utc)
            VALUES (
                $id, 'create', $state, 0, '파일 메타데이터를 요약하고 알림으로 보여줘',
                'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                $answersJson, '[]', NULL, NULL, NULL, $expectedCatalogRevision,
                'sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                $activeGenerationId, NULL, $timestamp, $timestamp);
            """;
        command.Parameters.AddWithValue("$id", SessionId);
        command.Parameters.AddWithValue("$state", activeGenerationId is null ? "proposal_ready" : "generating");
        command.Parameters.AddWithValue("$activeGenerationId", (object?)activeGenerationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$expectedCatalogRevision", expectedCatalogRevision);
        command.Parameters.AddWithValue("$answersJson", answersJson);
        command.Parameters.AddWithValue("$timestamp", Timestamp);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertProposalAsync(
        SqliteConnection connection,
        string proposalId,
        string sessionId,
        string generationId,
        string status,
        bool includePayload,
        string? terminalAtUtc,
        long expectedCatalogRevision = 0,
        string? knownLimitationsJson = "[]",
        string testsJson = "[]")
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO improvement_proposals(
                id, session_id, session_revision, generation_id,
                request_hash, response_hash, inventory_hash,
                base_capability_id, base_version, base_content_hash,
                expected_catalog_revision, candidate_content_hash, proposal_hash,
                manifest_json, instructions_markdown, input_schema_json,
                settings_schema_json, workflow_json, tests_json, known_limitations_json,
                test_evidence_hash, status, revision, fixed_issue_code,
                applied_draft_id, created_at_utc, updated_at_utc, terminal_at_utc)
            VALUES (
                $id, $sessionId, 0, $generationId,
                'sha256:1111111111111111111111111111111111111111111111111111111111111111',
                'sha256:2222222222222222222222222222222222222222222222222222222222222222',
                'sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                NULL, NULL, NULL, $expectedCatalogRevision, $candidateContentHash,
                'sha256:3333333333333333333333333333333333333333333333333333333333333333',
                $manifestJson, $instructionsMarkdown, $inputSchemaJson,
                $settingsSchemaJson, $workflowJson, $testsJson, $knownLimitationsJson,
                NULL, $status, 0, $fixedIssueCode, NULL,
                $timestamp, $timestamp, $terminalAtUtc);
            """;
        command.Parameters.AddWithValue("$id", proposalId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$generationId", generationId);
        command.Parameters.AddWithValue("$expectedCatalogRevision", expectedCatalogRevision);
        command.Parameters.AddWithValue(
            "$candidateContentHash",
            includePayload
                ? "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$manifestJson",
            includePayload ? "{\"id\":\"local.feature.sample\",\"version\":\"1.0.0\"}" : DBNull.Value);
        command.Parameters.AddWithValue(
            "$instructionsMarkdown",
            includePayload ? "# Sample feature\n\nUse only the declared workflow." : DBNull.Value);
        command.Parameters.AddWithValue(
            "$inputSchemaJson",
            includePayload ? "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}" : DBNull.Value);
        command.Parameters.AddWithValue(
            "$settingsSchemaJson",
            includePayload ? "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}" : DBNull.Value);
        command.Parameters.AddWithValue(
            "$workflowJson",
            includePayload ? "{\"version\":1,\"steps\":[]}" : DBNull.Value);
        command.Parameters.AddWithValue("$testsJson", includePayload ? testsJson : DBNull.Value);
        command.Parameters.AddWithValue(
            "$knownLimitationsJson",
            (object?)knownLimitationsJson ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$fixedIssueCode",
            status == "quarantined" ? "unsafe_payload" : DBNull.Value);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$timestamp", Timestamp);
        command.Parameters.AddWithValue("$terminalAtUtc", (object?)terminalAtUtc ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertPassedTestRunAsync(
        SqliteConnection connection,
        string proposalId,
        string resultJson = "{}",
        string engineVersion = "d5-static-v1")
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO capability_test_runs(
                id, proposal_id, kind, attempt, candidate_content_hash, engine_version,
                status, revision, result_json, fixed_error_code, started_at_utc, completed_at_utc)
            VALUES (
                'fbtest_0123456789abcdef0123456789abcdef', $proposalId, 'static_scan', 1,
                'sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
                $engineVersion, 'passed', 0, $resultJson, NULL,
                '2026-08-02T03:00:00.0000000+00:00',
                '2026-08-02T03:00:01.0000000+00:00');
            """;
        command.Parameters.AddWithValue("$proposalId", proposalId);
        command.Parameters.AddWithValue("$engineVersion", engineVersion);
        command.Parameters.AddWithValue("$resultJson", resultJson);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> StringScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static async Task<IReadOnlyList<string>> StringsAsync(SqliteConnection connection, string sql)
    {
        var values = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }

    private string DatabasePath(string fileName) => Path.Combine(_directory, fileName);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
