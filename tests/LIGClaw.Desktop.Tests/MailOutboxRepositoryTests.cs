using System.Text;
using LIGClaw.Application.Mail;
using LIGClaw.Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Tests;

public sealed class MailOutboxRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.Parse("2026-08-02T01:00:00Z");
    private const string HistoricalUnresolvedSql =
        """
        SELECT operation_id, submission_state
        FROM mail_outbox_operations
        WHERE draft_hash = $draftHash
          AND binding_hash = $bindingHash
          AND submission_state IN ('submitting', 'outcome_unknown')
        ORDER BY updated_at_utc DESC, operation_id
        LIMIT $limit;
        """;
    private const string SchemaNineteenUnresolvedSql =
        """
        SELECT operation_id, submission_state
        FROM mail_outbox_operations INDEXED BY ix_mail_outbox_network_started_unresolved
        WHERE draft_hash = $draftHash
          AND binding_hash = $bindingHash
          AND submission_state IN ('submitting', 'outcome_unknown')
        ORDER BY updated_at_utc DESC, operation_id
        LIMIT $limit;
        """;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public MailOutboxRepositoryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task CurrentSchemaPreservesTheExactMetadataOnlyOutboxColumnAllowlist()
    {
        var path = DatabasePath("schema.db");
        using (var store = new ConversationStore(path))
            await store.InitializeAsync();

        await using var connection = Open(path, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync();
        Assert.Equal(21, await ScalarAsync(
            connection,
            "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(
            [
                "operation_id",
                "idempotency_key",
                "conversation_id",
                "workflow_run_id",
                "tool_call_id",
                "capability_id",
                "capability_version",
                "capability_content_hash",
                "catalog_revision",
                "snapshot_hash",
                "connection_slot",
                "binding_hash",
                "draft_hash",
                "transport_kind",
                "smtp_message_id",
                "smtp_phase",
                "submission_state",
                "delivery_state",
                "attempt_count",
                "fixed_error_code",
                "revision",
                "created_at_utc",
                "updated_at_utc",
            ],
            await ReadColumnNamesAsync(connection, "mail_outbox_operations"));

        var tableSql = await ScalarStringAsync(
            connection,
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'mail_outbox_operations';");
        foreach (var forbidden in new[]
                 {
                     "recipient", "subject", "body", "sender", "host", "endpoint", "username", "password",
                     "credential", "token", "payload", "ciphertext",
                 })
        {
            Assert.DoesNotContain(forbidden, tableSql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task MigratesSchemaNineteenToTwentyAndBackfillsMailApiTransport()
    {
        var path = DatabasePath("schema-19.db");
        MailOutboxOperation existing;
        using (var current = await CreateSeededStoreAsync(path, "schema-19", ["one"]))
        {
            existing = (await current.MailOutbox.CreateOrGetAsync(
                Request("schema-19", "one", 'd', 'b'), CancellationToken.None)).Operation;
        }

        await using (var schemaNineteen = Open(path, SqliteOpenMode.ReadWrite))
        {
            await schemaNineteen.OpenAsync();
            await ExecuteAsync(
                schemaNineteen,
                """
                DROP TABLE capability_smtp_bindings;
                DROP TABLE smtp_probe_results;
                DROP TABLE smtp_connection_profiles;
                DROP INDEX ix_mail_outbox_mail_api_unresolved;
                DROP INDEX ix_mail_outbox_smtp_unresolved;
                CREATE INDEX ix_mail_outbox_network_started_unresolved
                    ON mail_outbox_operations(
                        draft_hash, binding_hash, updated_at_utc DESC, operation_id)
                    WHERE submission_state IN ('submitting', 'outcome_unknown');
                DELETE FROM schema_migrations WHERE version IN (20, 21);
                """);
        }

        using (var migrated = new ConversationStore(path))
            await migrated.InitializeAsync();

        await using var connection = Open(path, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync();
        Assert.Equal(21, await ScalarAsync(
            connection,
            "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(
            ["ix_mail_outbox_mail_api_unresolved", "ix_mail_outbox_smtp_unresolved"],
            await ReadStringsAsync(
                connection,
                """
                SELECT name
                FROM sqlite_master
                WHERE type = 'index'
                  AND name IN (
                      'ix_mail_outbox_mail_api_unresolved',
                      'ix_mail_outbox_smtp_unresolved')
                ORDER BY name;
                """));
        Assert.Equal(
            ["capability_smtp_bindings", "smtp_connection_profiles", "smtp_probe_results"],
            await ReadStringsAsync(
                connection,
                """
                SELECT name FROM sqlite_master
                WHERE type = 'table' AND name IN (
                    'smtp_connection_profiles', 'smtp_probe_results', 'capability_smtp_bindings')
                ORDER BY name;
                """));
        await using var operationCommand = connection.CreateCommand();
        operationCommand.CommandText =
            """
            SELECT transport_kind, smtp_message_id, smtp_phase
            FROM mail_outbox_operations
            WHERE operation_id = $operationId;
            """;
        operationCommand.Parameters.AddWithValue("$operationId", existing.OperationId.Value);
        await using var operationReader = await operationCommand.ExecuteReaderAsync();
        Assert.True(await operationReader.ReadAsync());
        Assert.Equal("mail-api", operationReader.GetString(0));
        Assert.True(operationReader.IsDBNull(1));
        Assert.True(operationReader.IsDBNull(2));

        var indexSql = await ScalarStringAsync(
            connection,
            """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'index'
              AND name = 'ix_mail_outbox_mail_api_unresolved';
            """);
        Assert.Contains(
            "draft_hash, binding_hash, updated_at_utc DESC, operation_id",
            indexSql,
            StringComparison.Ordinal);
        Assert.Contains("transport_kind = 'mail-api'", indexSql, StringComparison.Ordinal);
        Assert.Contains(
            "submission_state IN ('submitting', 'outcome_unknown')",
            indexSql,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActualSchemaEighteenSnapshotPreservesUnresolvedParityThroughSchemaNineteenAndCurrentReopen()
    {
        var path = DatabasePath("schema-18-history.db");
        var schemaNineteenPath = DatabasePath("schema-19-history.db");
        var manySuffixes = Enumerable.Range(0, 101)
            .Select(index => $"many-{index:D3}")
            .ToArray();

        const string smallScope = "schema18-small";
        using (var seeded = await CreateSeededStoreAsync(
                       path,
                       smallScope,
                       ["zero-staged", "one", "two-old", "two-new"]))
        {
            _ = await CreateHistoricalMailApiOperationAsync(
                seeded, smallScope, "zero-staged", '0', 'a', 0, HistoricalTerminalState.Staged);
            _ = await CreateHistoricalMailApiOperationAsync(
                seeded, smallScope, "one", '1', 'b', 1, HistoricalTerminalState.Submitting);
            _ = await CreateHistoricalMailApiOperationAsync(
                seeded, smallScope, "two-old", '2', 'c', 2, HistoricalTerminalState.Submitting);
            _ = await CreateHistoricalMailApiOperationAsync(
                seeded, smallScope, "two-new", '2', 'c', 3, HistoricalTerminalState.OutcomeUnknown);
        }
        for (var offset = 0; offset < manySuffixes.Length; offset += 20)
        {
            var batch = manySuffixes.Skip(offset).Take(20).ToArray();
            var scope = $"schema18-many-{offset / 20}";
            using var seeded = await CreateSeededStoreAsync(path, scope, batch);
            for (var batchIndex = 0; batchIndex < batch.Length; batchIndex++)
            {
                var index = offset + batchIndex;
                _ = await CreateHistoricalMailApiOperationAsync(
                    seeded,
                    scope,
                    batch[batchIndex],
                    '3',
                    'd',
                    10 + index,
                    index % 2 == 0
                        ? HistoricalTerminalState.OutcomeUnknown
                        : HistoricalTerminalState.Submitting);
            }
        }

        await DowngradeCurrentDatabaseToSchemaEighteenAsync(path);
        var queries = new[]
        {
            new HistoricalUnresolvedQuery(Hash('0'), Hash('a'), 2),
            new HistoricalUnresolvedQuery(Hash('1'), Hash('b'), 2),
            new HistoricalUnresolvedQuery(Hash('2'), Hash('c'), 2),
            new HistoricalUnresolvedQuery(Hash('3'), Hash('d'), 2),
            new HistoricalUnresolvedQuery(Hash('3'), Hash('d'), 100),
        };
        Dictionary<HistoricalUnresolvedQuery, IReadOnlyList<HistoricalUnresolvedRow>> expected;
        await using (var schemaEighteen = Open(path, SqliteOpenMode.ReadOnly))
        {
            await schemaEighteen.OpenAsync();
            Assert.Equal(18, await ScalarAsync(
                schemaEighteen,
                "SELECT MAX(version) FROM schema_migrations;"));
            Assert.DoesNotContain(
                "transport_kind",
                await ReadColumnNamesAsync(schemaEighteen, "mail_outbox_operations"));
            Assert.Equal(
                ["ix_mail_outbox_outcome_unknown", "ix_mail_outbox_recovery"],
                await ReadStringsAsync(
                    schemaEighteen,
                    """
                    SELECT name FROM sqlite_master
                    WHERE type = 'index' AND name LIKE 'ix_mail_outbox_%'
                    ORDER BY name;
                    """));

            expected = new Dictionary<HistoricalUnresolvedQuery, IReadOnlyList<HistoricalUnresolvedRow>>();
            foreach (var query in queries)
                expected.Add(query, await ReadHistoricalUnresolvedAsync(schemaEighteen, query));
            Assert.Empty(expected[queries[0]]);
            Assert.Single(expected[queries[1]]);
            Assert.Equal(2, expected[queries[2]].Count);
            Assert.Equal(2, expected[queries[3]].Count);
            Assert.Equal(100, expected[queries[4]].Count);
            Assert.Equal(
                101,
                await CountHistoricalUnresolvedAsync(schemaEighteen, queries[3]));
            Assert.Equal(
                ["outcome_unknown", "submitting"],
                expected[queries[3]].Select(row => row.SubmissionState).ToArray());
        }

        await CopyDatabaseAsync(path, schemaNineteenPath);
        await ApplySchemaNineteenMigrationAsync(schemaNineteenPath);
        await using (var schemaNineteen = Open(schemaNineteenPath, SqliteOpenMode.ReadOnly))
        {
            await schemaNineteen.OpenAsync();
            Assert.Equal(19, await ScalarAsync(
                schemaNineteen,
                "SELECT MAX(version) FROM schema_migrations;"));
            var indexSql = await ScalarStringAsync(
                schemaNineteen,
                """
                SELECT sql FROM sqlite_master
                WHERE type = 'index'
                  AND name = 'ix_mail_outbox_network_started_unresolved';
                """);
            Assert.Contains(
                "draft_hash, binding_hash, updated_at_utc DESC, operation_id",
                indexSql,
                StringComparison.Ordinal);
            Assert.Contains(
                "submission_state IN ('submitting', 'outcome_unknown')",
                indexSql,
                StringComparison.Ordinal);
            var plan = await ReadQueryPlanAsync(
                schemaNineteen,
                SchemaNineteenUnresolvedSql,
                ("$draftHash", queries[3].DraftHash),
                ("$bindingHash", queries[3].BindingHash),
                ("$limit", queries[3].Limit));
            Assert.Contains(
                plan,
                detail => detail.Contains(
                    "USING INDEX ix_mail_outbox_network_started_unresolved",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(
                plan,
                detail => detail.Contains("SCAN mail_outbox_operations", StringComparison.Ordinal));
            Assert.DoesNotContain(
                plan,
                detail => detail.Contains("USE TEMP B-TREE", StringComparison.Ordinal));
            foreach (var query in queries)
            {
                Assert.Equal(
                    expected[query],
                    await ReadHistoricalUnresolvedAsync(
                        schemaNineteen,
                        query,
                        SchemaNineteenUnresolvedSql));
            }
        }

        using (var migrated = new ConversationStore(path))
        {
            await migrated.InitializeAsync();
            await AssertCurrentUnresolvedParityAsync(migrated, queries, expected);
        }
        using (var reopened = new ConversationStore(path))
        {
            await reopened.InitializeAsync();
            await AssertCurrentUnresolvedParityAsync(reopened, queries, expected);
        }

        await using var current = Open(path, SqliteOpenMode.ReadOnly);
        await current.OpenAsync();
        Assert.Equal(21, await ScalarAsync(
            current,
            "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(1, await ScalarAsync(
            current,
            "SELECT COUNT(*) FROM schema_migrations WHERE version = 19;"));
        var currentPlan = await ReadQueryPlanAsync(
            current,
            MailOutboxRepository.NetworkStartedUnresolvedSql,
            ("$draftHash", queries[3].DraftHash),
            ("$bindingHash", queries[3].BindingHash),
            ("$limit", queries[3].Limit));
        Assert.Contains(
            currentPlan,
            detail => detail.Contains(
                "USING INDEX ix_mail_outbox_mail_api_unresolved",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            currentPlan,
            detail => detail.Contains("SCAN mail_outbox_operations", StringComparison.Ordinal));
        Assert.DoesNotContain(
            currentPlan,
            detail => detail.Contains("USE TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProductionUnresolvedQueriesUseExactPartialIndexesWithoutScanOrTemporarySort()
    {
        var path = DatabasePath("query-plan.db");
        using (var store = new ConversationStore(path))
            await store.InitializeAsync();

        await using var connection = Open(path, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync();
        var details = await ReadQueryPlanAsync(
            connection,
            MailOutboxRepository.NetworkStartedUnresolvedSql,
            ("$draftHash", Hash('d')),
            ("$bindingHash", Hash('b')),
            ("$limit", 2));

        Assert.Contains(
            details,
            detail => detail.Contains(
                "USING INDEX ix_mail_outbox_mail_api_unresolved",
                StringComparison.Ordinal));
        Assert.Contains(details, detail => detail.Contains("draft_hash=?", StringComparison.Ordinal));
        Assert.Contains(details, detail => detail.Contains("binding_hash=?", StringComparison.Ordinal));
        Assert.DoesNotContain(
            details,
            detail => detail.Contains("SCAN mail_outbox_operations", StringComparison.Ordinal));
        Assert.DoesNotContain(
            details,
            detail => detail.Contains("USE TEMP B-TREE", StringComparison.Ordinal));

        var smtpDetails = await ReadQueryPlanAsync(
            connection,
            MailOutboxRepository.SmtpNetworkStartedUnresolvedSql,
            ("$draftHash", Hash('d')),
            ("$limit", 2));
        Assert.Contains(
            smtpDetails,
            detail => detail.Contains(
                "USING INDEX ix_mail_outbox_smtp_unresolved",
                StringComparison.Ordinal));
        Assert.Contains(smtpDetails, detail => detail.Contains("draft_hash=?", StringComparison.Ordinal));
        Assert.DoesNotContain(
            smtpDetails,
            detail => detail.Contains("binding_hash=?", StringComparison.Ordinal));
        Assert.DoesNotContain(
            smtpDetails,
            detail => detail.Contains("SCAN mail_outbox_operations", StringComparison.Ordinal));
        Assert.DoesNotContain(
            smtpDetails,
            detail => detail.Contains("USE TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateOrGetReusesOnlyTheSameExactImmutableIdentity()
    {
        var path = DatabasePath("create-or-get.db");
        using var store = await CreateSeededStoreAsync(path, "create", ["one"]);
        var request = Request("create", "one", 'd', 'b');

        var created = await store.MailOutbox.CreateOrGetAsync(request, CancellationToken.None);
        var existing = await store.MailOutbox.CreateOrGetAsync(
            Request("create", "one", 'd', 'b', Baseline.AddMinutes(1)),
            CancellationToken.None);
        var conflict = await store.MailOutbox.CreateOrGetAsync(
            Request("create", "one", 'e', 'b', Baseline.AddMinutes(2)),
            CancellationToken.None);
        var transportConflict = await store.MailOutbox.CreateOrGetAsync(
            Request(
                "create",
                "one",
                'd',
                'b',
                Baseline.AddMinutes(3),
                transportKind: MailOutboxTransportKind.Smtp),
            CancellationToken.None);

        Assert.Equal(MailOutboxCreateOutcome.Created, created.Outcome);
        Assert.Matches("^mgo_[0-9a-f]{64}$", created.Operation.OperationId.Value);
        Assert.Matches("^mik_[0-9a-f]{64}$", created.Operation.IdempotencyKey.Value);
        Assert.Equal(MailOutboxCreateOutcome.Existing, existing.Outcome);
        Assert.Equal(created.Operation.OperationId, existing.Operation.OperationId);
        Assert.Equal(created.Operation.IdempotencyKey, existing.Operation.IdempotencyKey);
        Assert.Equal(MailOutboxCreateOutcome.Conflict, conflict.Outcome);
        Assert.Equal(MailOutboxPolicy.IdentityConflict, conflict.ErrorCode);
        Assert.Equal(created.Operation.OperationId, conflict.Operation.OperationId);
        Assert.Equal(MailOutboxCreateOutcome.Conflict, transportConflict.Outcome);
        Assert.Equal(MailOutboxPolicy.IdentityConflict, transportConflict.ErrorCode);
        Assert.Equal(MailOutboxTransportKind.MailApi, transportConflict.Operation.TransportKind);

        await using var connection = Open(path, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync();
        Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM mail_outbox_operations;"));
    }

    [Fact]
    public async Task CompareAndSwapAllowsOneRacerAndTerminalRowsCannotChange()
    {
        var path = DatabasePath("cas.db");
        using var store = await CreateSeededStoreAsync(path, "cas", ["one"]);
        var created = await store.MailOutbox.CreateOrGetAsync(
            Request("cas", "one", 'd', 'b'),
            CancellationToken.None);
        var stage = created.Operation;
        var transition = Transition(
            stage,
            MailOutboxSubmissionState.Submitting,
            MailOutboxDeliveryState.Unknown,
            1,
            null,
            Baseline.AddSeconds(1));

        var racers = await Task.WhenAll(
            store.MailOutbox.TryTransitionAsync(transition, CancellationToken.None),
            store.MailOutbox.TryTransitionAsync(transition, CancellationToken.None));

        Assert.Single(racers, result => result.Outcome == MailOutboxTransitionOutcome.Succeeded);
        Assert.Single(racers, result => result.Outcome == MailOutboxTransitionOutcome.Stale);
        var submitting = Assert.IsType<MailOutboxOperation>(
            await store.MailOutbox.GetAsync(stage.OperationId, CancellationToken.None));
        var acceptedResult = await store.MailOutbox.TryTransitionAsync(
            Transition(
                submitting,
                MailOutboxSubmissionState.Accepted,
                MailOutboxDeliveryState.Queued,
                1,
                null,
                Baseline.AddSeconds(2)),
            CancellationToken.None);
        var accepted = Assert.IsType<MailOutboxOperation>(acceptedResult.Operation);
        var deliveredResult = await store.MailOutbox.TryTransitionAsync(
            Transition(
                accepted,
                MailOutboxSubmissionState.Accepted,
                MailOutboxDeliveryState.Delivered,
                1,
                null,
                Baseline.AddSeconds(3)),
            CancellationToken.None);
        var delivered = Assert.IsType<MailOutboxOperation>(deliveredResult.Operation);

        var resume = await store.MailOutbox.TryTransitionAsync(
            Transition(
                delivered,
                MailOutboxSubmissionState.Submitting,
                MailOutboxDeliveryState.Unknown,
                2,
                null,
                Baseline.AddSeconds(4)),
            CancellationToken.None);
        Assert.Equal(MailOutboxTransitionOutcome.Invalid, resume.Outcome);

        await using var connection = Open(path, SqliteOpenMode.ReadWrite);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE mail_outbox_operations SET revision = revision + 1 WHERE operation_id = $operationId;";
        command.Parameters.AddWithValue("$operationId", delivered.OperationId.Value);
        _ = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        var unchanged = Assert.IsType<MailOutboxOperation>(
            await store.MailOutbox.GetAsync(delivered.OperationId, CancellationToken.None));
        Assert.Equal(delivered.Revision, unchanged.Revision);
        Assert.Equal(MailOutboxDeliveryState.Delivered, unchanged.DeliveryState);
    }

    [Fact]
    public async Task DesktopStartupClosesOnlyUnstartedAndIndeterminateSubmissions()
    {
        var path = DatabasePath("recovery.db");
        MailOutboxOperation staged;
        MailOutboxOperation submitting;
        MailOutboxOperation accepted;
        using (var store = await CreateSeededStoreAsync(path, "recovery", ["staged", "submitting", "accepted"]))
        {
            staged = (await store.MailOutbox.CreateOrGetAsync(
                Request("recovery", "staged", '1', 'a'),
                CancellationToken.None)).Operation;
            var submittingStaged = (await store.MailOutbox.CreateOrGetAsync(
                Request("recovery", "submitting", '2', 'b'),
                CancellationToken.None)).Operation;
            submitting = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
                Transition(
                    submittingStaged,
                    MailOutboxSubmissionState.Submitting,
                    MailOutboxDeliveryState.Unknown,
                    1,
                    null,
                    Baseline.AddSeconds(1)),
                CancellationToken.None)).Operation);

            var acceptedStaged = (await store.MailOutbox.CreateOrGetAsync(
                Request("recovery", "accepted", '3', 'c'),
                CancellationToken.None)).Operation;
            var acceptedSubmitting = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
                Transition(
                    acceptedStaged,
                    MailOutboxSubmissionState.Submitting,
                    MailOutboxDeliveryState.Unknown,
                    1,
                    null,
                    Baseline.AddSeconds(1)),
                CancellationToken.None)).Operation);
            accepted = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
                Transition(
                    acceptedSubmitting,
                    MailOutboxSubmissionState.Accepted,
                    MailOutboxDeliveryState.Queued,
                    1,
                    null,
                    Baseline.AddSeconds(2)),
                CancellationToken.None)).Operation);
        }

        using var restarted = new ConversationStore(path);
        _ = await restarted.InitializeForDesktopStartupAsync();
        var recoveredStaged = Assert.IsType<MailOutboxOperation>(
            await restarted.MailOutbox.GetAsync(staged.OperationId, CancellationToken.None));
        var recoveredSubmitting = Assert.IsType<MailOutboxOperation>(
            await restarted.MailOutbox.GetAsync(submitting.OperationId, CancellationToken.None));
        var recoveredAccepted = Assert.IsType<MailOutboxOperation>(
            await restarted.MailOutbox.GetAsync(accepted.OperationId, CancellationToken.None));

        Assert.Equal(MailOutboxSubmissionState.CancelledNotStarted, recoveredStaged.SubmissionState);
        Assert.Equal(MailOutboxSubmissionState.OutcomeUnknown, recoveredSubmitting.SubmissionState);
        Assert.Equal(MailOutboxSubmissionState.Accepted, recoveredAccepted.SubmissionState);
        Assert.Equal(MailOutboxDeliveryState.Queued, recoveredAccepted.DeliveryState);
        Assert.Equal(staged.Revision + 1, recoveredStaged.Revision);
        Assert.Equal(submitting.Revision + 1, recoveredSubmitting.Revision);
        Assert.Equal(accepted.Revision, recoveredAccepted.Revision);

        var unresolved = await restarted.MailOutbox.ListNetworkStartedUnresolvedAsync(
            recoveredSubmitting.DraftHash,
            recoveredSubmitting.BindingHash,
            10,
            CancellationToken.None);
        Assert.Equal(recoveredSubmitting.OperationId, Assert.Single(unresolved).OperationId);
        var secondRecovery = await restarted.MailOutbox.RecoverOnStartupAsync(
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.Equal(0, secondRecovery.CancelledNotStarted);
        Assert.Equal(0, secondRecovery.MarkedOutcomeUnknown);

        var retry = await restarted.MailOutbox.TryTransitionAsync(
            Transition(
                recoveredSubmitting,
                MailOutboxSubmissionState.Submitting,
                MailOutboxDeliveryState.Unknown,
                2,
                null,
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        Assert.Equal(MailOutboxTransitionOutcome.Succeeded, retry.Outcome);
        Assert.Equal(2, Assert.IsType<MailOutboxOperation>(retry.Operation).AttemptCount);
    }

    [Fact]
    public async Task NetworkStartedUnresolvedQueryIncludesLiveSubmittingBeforeStartupRecovery()
    {
        using var store = await CreateSeededStoreAsync(
            DatabasePath("live-submitting-query.db"),
            "live-submitting-query",
            ["one"]);
        var staged = (await store.MailOutbox.CreateOrGetAsync(
            Request("live-submitting-query", "one", '1', 'a'),
            CancellationToken.None)).Operation;
        var submitting = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(
                staged,
                MailOutboxSubmissionState.Submitting,
                MailOutboxDeliveryState.Unknown,
                1,
                null,
                Baseline.AddSeconds(1)),
            CancellationToken.None)).Operation);

        var unresolved = await store.MailOutbox.ListNetworkStartedUnresolvedAsync(
            submitting.DraftHash,
            submitting.BindingHash,
            10,
            CancellationToken.None);

        var live = Assert.Single(unresolved);
        Assert.Equal(submitting.OperationId, live.OperationId);
        Assert.Equal(MailOutboxSubmissionState.Submitting, live.SubmissionState);
        Assert.Equal(MailOutboxDeliveryState.Unknown, live.DeliveryState);
        Assert.Equal(1, live.AttemptCount);
        Assert.Equal(submitting.Revision, live.Revision);
    }

    [Fact]
    public async Task SmtpUnresolvedQueryBlocksTheSameDraftAcrossBindingRotation()
    {
        using var store = await CreateSeededStoreAsync(
            DatabasePath("smtp-cross-binding.db"),
            "smtp-cross-binding",
            ["first", "rotated"]);
        var first = await CreateSmtpOutcomeUnknownAsync(
            store,
            Request(
                "smtp-cross-binding",
                "first",
                'd',
                'a',
                transportKind: MailOutboxTransportKind.Smtp));
        var rotated = await CreateSmtpOutcomeUnknownAsync(
            store,
            Request(
                "smtp-cross-binding",
                "rotated",
                'd',
                'b',
                transportKind: MailOutboxTransportKind.Smtp));

        Assert.NotEqual(first.BindingHash, rotated.BindingHash);
        Assert.Matches("^<ligclaw\\.[0-9a-f]{64}@ligclaw\\.invalid>$", first.SmtpMessageId);
        Assert.Equal(
            MailOutboxPolicy.ComputeSmtpMessageId(first.OperationId),
            first.SmtpMessageId);
        var unresolved = await store.MailOutbox.ListSmtpNetworkStartedUnresolvedAsync(
            first.DraftHash,
            10,
            CancellationToken.None);
        Assert.Equal(2, unresolved.Count);
        Assert.Contains(unresolved, operation => operation.OperationId.Equals(first.OperationId));
        Assert.Contains(unresolved, operation => operation.OperationId.Equals(rotated.OperationId));
        Assert.All(unresolved, operation =>
        {
            Assert.Equal(MailOutboxTransportKind.Smtp, operation.TransportKind);
            Assert.Equal(MailOutboxSmtpPhase.DataStarted, operation.SmtpPhase);
            Assert.Equal(1, operation.AttemptCount);
        });

        var retry = await store.MailOutbox.TryTransitionAsync(
            Transition(
                first,
                MailOutboxSubmissionState.Submitting,
                MailOutboxDeliveryState.Unknown,
                1,
                null,
                Baseline.AddSeconds(10)),
            CancellationToken.None);
        Assert.Equal(MailOutboxTransitionOutcome.Invalid, retry.Outcome);
    }

    [Fact]
    public async Task SmtpDataStartedMarkerIsADurableSingleWinnerCas()
    {
        var path = DatabasePath("smtp-phase-cas.db");
        MailOutboxOperation marked;
        using (var store = await CreateSeededStoreAsync(
                   path,
                   "smtp-phase-cas",
                   ["one"]))
        {
            var staged = (await store.MailOutbox.CreateOrGetAsync(
                Request(
                    "smtp-phase-cas",
                    "one",
                    'd',
                    'b',
                    transportKind: MailOutboxTransportKind.Smtp),
                CancellationToken.None)).Operation;
            var submitting = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
                Transition(
                    staged,
                    MailOutboxSubmissionState.Submitting,
                    MailOutboxDeliveryState.Unknown,
                    1,
                    null,
                    Baseline.AddSeconds(1)),
                CancellationToken.None)).Operation);
            var markDataStarted = Transition(
                submitting,
                MailOutboxSubmissionState.Submitting,
                MailOutboxDeliveryState.Unknown,
                1,
                null,
                Baseline.AddSeconds(2),
                MailOutboxSmtpPhase.DataStarted);

            var racers = await Task.WhenAll(
                store.MailOutbox.TryTransitionAsync(markDataStarted, CancellationToken.None),
                store.MailOutbox.TryTransitionAsync(markDataStarted, CancellationToken.None));
            Assert.Single(racers, result => result.Outcome == MailOutboxTransitionOutcome.Succeeded);
            Assert.Single(racers, result => result.Outcome == MailOutboxTransitionOutcome.Stale);
            marked = Assert.IsType<MailOutboxOperation>(
                await store.MailOutbox.GetAsync(staged.OperationId, CancellationToken.None));
            Assert.Equal(MailOutboxSmtpPhase.DataStarted, marked.SmtpPhase);
            Assert.Equal(submitting.Revision + 1, marked.Revision);
            Assert.Equal(
                MailOutboxPolicy.ComputeSmtpMessageId(marked.OperationId),
                marked.SmtpMessageId);

            var phaseRollback = await store.MailOutbox.TryTransitionAsync(
                Transition(
                    marked,
                    MailOutboxSubmissionState.Submitting,
                    MailOutboxDeliveryState.Unknown,
                    1,
                    null,
                    Baseline.AddSeconds(3),
                    MailOutboxSmtpPhase.PreData),
                CancellationToken.None);
            Assert.Equal(MailOutboxTransitionOutcome.Invalid, phaseRollback.Outcome);
        }

        using var reopened = new ConversationStore(path);
        await reopened.InitializeAsync();
        var persisted = Assert.IsType<MailOutboxOperation>(
            await reopened.MailOutbox.GetAsync(marked.OperationId, CancellationToken.None));
        Assert.Equal(marked.SmtpMessageId, persisted.SmtpMessageId);
        Assert.Equal(MailOutboxSmtpPhase.DataStarted, persisted.SmtpPhase);
        Assert.Equal(marked.Revision, persisted.Revision);
    }

    [Fact]
    public async Task StartupRecoveryDistinguishesSmtpPreDataFromDataStarted()
    {
        var path = DatabasePath("smtp-recovery.db");
        MailOutboxOperation staged;
        MailOutboxOperation preData;
        MailOutboxOperation dataStarted;
        using (var store = await CreateSeededStoreAsync(
                   path,
                   "smtp-recovery",
                   ["staged", "pre-data", "data-started"]))
        {
            staged = (await store.MailOutbox.CreateOrGetAsync(
                Request(
                    "smtp-recovery",
                    "staged",
                    '1',
                    'a',
                    transportKind: MailOutboxTransportKind.Smtp),
                CancellationToken.None)).Operation;
            preData = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
                Transition(
                    (await store.MailOutbox.CreateOrGetAsync(
                        Request(
                            "smtp-recovery",
                            "pre-data",
                            '2',
                            'b',
                            transportKind: MailOutboxTransportKind.Smtp),
                        CancellationToken.None)).Operation,
                    MailOutboxSubmissionState.Submitting,
                    MailOutboxDeliveryState.Unknown,
                    1,
                    null,
                    Baseline.AddSeconds(1)),
                CancellationToken.None)).Operation);
            var dataSubmitting = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
                Transition(
                    (await store.MailOutbox.CreateOrGetAsync(
                        Request(
                            "smtp-recovery",
                            "data-started",
                            '3',
                            'c',
                            transportKind: MailOutboxTransportKind.Smtp),
                        CancellationToken.None)).Operation,
                    MailOutboxSubmissionState.Submitting,
                    MailOutboxDeliveryState.Unknown,
                    1,
                    null,
                    Baseline.AddSeconds(1)),
                CancellationToken.None)).Operation);
            dataStarted = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
                Transition(
                    dataSubmitting,
                    MailOutboxSubmissionState.Submitting,
                    MailOutboxDeliveryState.Unknown,
                    1,
                    null,
                    Baseline.AddSeconds(2),
                    MailOutboxSmtpPhase.DataStarted),
                CancellationToken.None)).Operation);
        }

        using var restarted = new ConversationStore(path);
        await restarted.InitializeAsync();
        var recovery = await restarted.MailOutbox.RecoverOnStartupAsync(
            Baseline.AddMinutes(1),
            CancellationToken.None);
        Assert.Equal(1, recovery.CancelledNotStarted);
        Assert.Equal(1, recovery.MarkedFailedKnown);
        Assert.Equal(1, recovery.MarkedOutcomeUnknown);

        var recoveredStaged = Assert.IsType<MailOutboxOperation>(
            await restarted.MailOutbox.GetAsync(staged.OperationId, CancellationToken.None));
        var recoveredPreData = Assert.IsType<MailOutboxOperation>(
            await restarted.MailOutbox.GetAsync(preData.OperationId, CancellationToken.None));
        var recoveredDataStarted = Assert.IsType<MailOutboxOperation>(
            await restarted.MailOutbox.GetAsync(dataStarted.OperationId, CancellationToken.None));
        Assert.Equal(MailOutboxSubmissionState.CancelledNotStarted, recoveredStaged.SubmissionState);
        Assert.Equal(MailOutboxSubmissionState.FailedKnown, recoveredPreData.SubmissionState);
        Assert.Equal(MailOutboxDeliveryState.Failed, recoveredPreData.DeliveryState);
        Assert.Equal(MailOutboxSubmissionState.OutcomeUnknown, recoveredDataStarted.SubmissionState);
        Assert.Equal(MailOutboxSmtpPhase.DataStarted, recoveredDataStarted.SmtpPhase);

        var second = await restarted.MailOutbox.RecoverOnStartupAsync(
            Baseline.AddMinutes(2),
            CancellationToken.None);
        Assert.Equal(new MailOutboxStartupRecoveryResult(0, 0, 0), second);
    }

    [Fact]
    public async Task ReconcilesOutcomeUnknownAndAllowsOnlyOneBoundedSameKeyRetry()
    {
        using var store = await CreateSeededStoreAsync(
            DatabasePath("reconcile.db"), "reconcile", ["queued", "retry"]);
        var queuedStaged = (await store.MailOutbox.CreateOrGetAsync(
            Request("reconcile", "queued", '1', 'a'), CancellationToken.None)).Operation;
        var queuedSubmitting = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(queuedStaged, MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown,
                1, null, Baseline.AddSeconds(1)), CancellationToken.None)).Operation);
        var queuedUnknown = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(queuedSubmitting, MailOutboxSubmissionState.OutcomeUnknown, MailOutboxDeliveryState.Unknown,
                1, "mail.outbox.timeout", Baseline.AddSeconds(2)), CancellationToken.None)).Operation);

        var directDelivered = await store.MailOutbox.TryTransitionAsync(
            Transition(queuedUnknown, MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Delivered,
                1, null, Baseline.AddSeconds(3)), CancellationToken.None);
        Assert.Equal(MailOutboxTransitionOutcome.Invalid, directDelivered.Outcome);
        var queued = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(queuedUnknown, MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Queued,
                1, null, Baseline.AddSeconds(3)), CancellationToken.None)).Operation);
        var delivered = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(queued, MailOutboxSubmissionState.Accepted, MailOutboxDeliveryState.Delivered,
                1, null, Baseline.AddSeconds(4)), CancellationToken.None)).Operation);
        Assert.Equal(MailOutboxDeliveryState.Delivered, delivered.DeliveryState);

        var retryStaged = (await store.MailOutbox.CreateOrGetAsync(
            Request("reconcile", "retry", '2', 'b'), CancellationToken.None)).Operation;
        var retrySubmitting = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(retryStaged, MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown,
                1, null, Baseline.AddSeconds(1)), CancellationToken.None)).Operation);
        var retryUnknown = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(retrySubmitting, MailOutboxSubmissionState.OutcomeUnknown, MailOutboxDeliveryState.Unknown,
                1, "mail.outbox.timeout", Baseline.AddSeconds(2)), CancellationToken.None)).Operation);
        var secondSubmitting = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(retryUnknown, MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown,
                2, null, Baseline.AddSeconds(3)), CancellationToken.None)).Operation);
        var secondUnknown = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(secondSubmitting, MailOutboxSubmissionState.OutcomeUnknown, MailOutboxDeliveryState.Unknown,
                2, "mail.outbox.timeout", Baseline.AddSeconds(4)), CancellationToken.None)).Operation);
        var thirdAttempt = await store.MailOutbox.TryTransitionAsync(
            Transition(secondUnknown, MailOutboxSubmissionState.Submitting, MailOutboxDeliveryState.Unknown,
                3, null, Baseline.AddSeconds(5)), CancellationToken.None);
        Assert.Equal(MailOutboxTransitionOutcome.Invalid, thirdAttempt.Outcome);
    }

    [Fact]
    public async Task RejectsMismatchedWorkflowRunIdentity()
    {
        using var store = await CreateSeededStoreAsync(
            DatabasePath("identity-mismatch.db"), "identity", ["capability", "version", "content", "catalog", "snapshot"]);
        var requests = new[]
        {
            Request("identity", "capability", '1', 'a', capabilityId: "company.other-mail"),
            Request("identity", "version", '2', 'b', capabilityVersion: "1.0.1"),
            Request("identity", "content", '3', 'c', capabilityContentHashDigit: '6'),
            Request("identity", "catalog", '4', 'd', catalogRevision: 8),
            Request("identity", "snapshot", '5', 'e', snapshotHashDigit: '7'),
        };

        foreach (var request in requests)
            await Assert.ThrowsAsync<SqliteException>(() => store.MailOutbox.CreateOrGetAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task ProtectsUnresolvedOperationsFromDirectAndParentCascadeDelete()
    {
        var path = DatabasePath("delete-protection.db");
        using var store = await CreateSeededStoreAsync(path, "delete", ["one"]);
        var staged = (await store.MailOutbox.CreateOrGetAsync(
            Request("delete", "one", '1', 'a'), CancellationToken.None)).Operation;

        await using var connection = Open(path, SqliteOpenMode.ReadWrite);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;");
        await using (var directDelete = connection.CreateCommand())
        {
            directDelete.CommandText = "DELETE FROM mail_outbox_operations WHERE operation_id = $operationId;";
            directDelete.Parameters.AddWithValue("$operationId", staged.OperationId.Value);
            _ = await Assert.ThrowsAsync<SqliteException>(() => directDelete.ExecuteNonQueryAsync());
        }
        await using (var parentDelete = connection.CreateCommand())
        {
            parentDelete.CommandText = "DELETE FROM conversations WHERE id = $conversationId;";
            parentDelete.Parameters.AddWithValue("$conversationId", ConversationId("delete"));
            _ = await Assert.ThrowsAsync<SqliteException>(() => parentDelete.ExecuteNonQueryAsync());
        }

        var cancelled = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(staged, MailOutboxSubmissionState.CancelledNotStarted, MailOutboxDeliveryState.Unknown,
                0, "mail.outbox.cancelled", Baseline.AddSeconds(1)), CancellationToken.None)).Operation);
        Assert.Equal(MailOutboxSubmissionState.CancelledNotStarted, cancelled.SubmissionState);
        await using var terminalDelete = connection.CreateCommand();
        terminalDelete.CommandText = "DELETE FROM conversations WHERE id = $conversationId;";
        terminalDelete.Parameters.AddWithValue("$conversationId", ConversationId("delete"));
        Assert.Equal(1, await terminalDelete.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task PersistsOnlyTheDraftHashAndNoRawMailCanary()
    {
        var path = DatabasePath("raw-canary.db");
        const string recipient = "raw-recipient-canary-7f91@example.invalid";
        const string subject = "RAW-SUBJECT-CANARY-7F91";
        const string body = "RAW-BODY-CANARY-7F91 must never enter SQLite";
        var draft = Assert.IsType<MailDraft>(MailDraftPolicy.Validate(
            new MailDraftInput([recipient], [], [], subject, body)).Draft);

        using (var store = await CreateSeededStoreAsync(path, "canary", ["one"]))
        {
            var request = Request("canary", "one", 'd', 'b', draftHash: draft.CanonicalHash);
            Assert.Equal(
                MailOutboxCreateOutcome.Created,
                (await store.MailOutbox.CreateOrGetAsync(request, CancellationToken.None)).Outcome);
        }

        foreach (var file in new[] { path, $"{path}-wal", $"{path}-shm" }.Where(File.Exists))
        {
            var persistedBytes = File.ReadAllBytes(file);
            Assert.DoesNotContain(Encoding.UTF8.GetBytes(recipient), persistedBytes);
            Assert.DoesNotContain(Encoding.UTF8.GetBytes(subject), persistedBytes);
            Assert.DoesNotContain(Encoding.UTF8.GetBytes(body), persistedBytes);
        }

        await using var connection = Open(path, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT draft_hash FROM mail_outbox_operations;";
        Assert.Equal(draft.CanonicalHash, Assert.IsType<string>(await command.ExecuteScalarAsync()));
    }

    private async Task<ConversationStore> CreateSeededStoreAsync(
        string path,
        string scope,
        IReadOnlyList<string> stepSuffixes)
    {
        var conversationId = ConversationId(scope);
        var parentRunId = ParentRunId(scope);
        var workflowRunId = WorkflowRunId(scope);
        var store = new ConversationStore(path);
        await store.InitializeAsync();
        await store.StartRunAsync(conversationId, parentRunId, "mail outbox parent", Baseline);
        var run = RunStart(scope);
        Assert.Equal(
            WorkflowRunStartOutcome.Created,
            (await store.WorkflowLedger.StartOrCoalesceRunAsync(run, CancellationToken.None)).Outcome);
        for (var index = 0; index < stepSuffixes.Count; index++)
        {
            var suffix = stepSuffixes[index];
            Assert.True(await store.WorkflowLedger.AddPlannedStepAsync(
                new WorkflowStepPlan(
                    workflowRunId,
                    index,
                    1,
                    $"mail-step-{scope}-{suffix}",
                    ToolCallId(scope, suffix),
                    "mail.send.v1",
                    "R3",
                    "never",
                    WorkflowLedgerEffectClass.Effect,
                    Hash('9'),
                    Hash('8'),
                    Baseline.AddMilliseconds(index + 1)),
                CancellationToken.None));
        }
        return store;
    }

    private static WorkflowRunStart RunStart(string scope) =>
        new(
            WorkflowRunId(scope),
            ConversationId(scope),
            ParentRunId(scope),
            $"parent-tool-{scope}",
            $"workflow-idempotency-{scope}",
            Hash('1'),
            "company.workflow_mail.v1",
            "company.workflow-mail",
            "1.0.0",
            7,
            Hash('2'),
            Hash('3'),
            Hash('4'),
            Hash('5'),
            "R3",
            Baseline);

    private static MailOutboxCreateRequest Request(
        string scope,
        string suffix,
        char draftHashDigit,
        char bindingHashDigit,
        DateTimeOffset? createdAtUtc = null,
        string? draftHash = null,
        string capabilityId = "company.workflow-mail",
        string capabilityVersion = "1.0.0",
        char capabilityContentHashDigit = '2',
        long catalogRevision = 7,
        char snapshotHashDigit = '1',
        MailOutboxTransportKind transportKind = MailOutboxTransportKind.MailApi) =>
        new(
            ConversationId(scope),
            WorkflowRunId(scope),
            ToolCallId(scope, suffix),
            capabilityId,
            capabilityVersion,
            MailOutboxHash.Parse(Hash(capabilityContentHashDigit)),
            catalogRevision,
            MailOutboxHash.Parse(Hash(snapshotHashDigit)),
            "primary-mail",
            MailOutboxHash.Parse(Hash(bindingHashDigit)),
            MailOutboxHash.Parse(draftHash ?? Hash(draftHashDigit)),
            createdAtUtc ?? Baseline,
            transportKind);

    private static MailOutboxTransitionRequest Transition(
        MailOutboxOperation current,
        MailOutboxSubmissionState submission,
        MailOutboxDeliveryState delivery,
        int attemptCount,
        string? errorCode,
        DateTimeOffset updatedAtUtc,
        MailOutboxSmtpPhase? nextSmtpPhase = null) =>
        new(
            current.OperationId,
            current.Revision,
            current.SubmissionState,
            current.DeliveryState,
            submission,
            delivery,
            attemptCount,
            errorCode,
            updatedAtUtc,
            nextSmtpPhase);

    private static async Task<MailOutboxOperation> CreateHistoricalMailApiOperationAsync(
        ConversationStore store,
        string scope,
        string suffix,
        char draftHashDigit,
        char bindingHashDigit,
        int order,
        HistoricalTerminalState terminalState)
    {
        var createdAtUtc = Baseline.AddMinutes(10).AddMilliseconds(order * 10L);
        var staged = (await store.MailOutbox.CreateOrGetAsync(
            Request(
                scope,
                suffix,
                draftHashDigit,
                bindingHashDigit,
                createdAtUtc),
            CancellationToken.None)).Operation;
        if (terminalState == HistoricalTerminalState.Staged) return staged;

        var submitting = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(
                staged,
                MailOutboxSubmissionState.Submitting,
                MailOutboxDeliveryState.Unknown,
                1,
                null,
                createdAtUtc.AddMilliseconds(1)),
            CancellationToken.None)).Operation);
        if (terminalState == HistoricalTerminalState.Submitting) return submitting;
        return Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(
                submitting,
                MailOutboxSubmissionState.OutcomeUnknown,
                MailOutboxDeliveryState.Unknown,
                1,
                "mail.outbox.timeout",
                createdAtUtc.AddMilliseconds(2)),
            CancellationToken.None)).Operation);
    }

    private static async Task DowngradeCurrentDatabaseToSchemaEighteenAsync(string path)
    {
        await using var connection = Open(path, SqliteOpenMode.ReadWrite);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            $"""
            PRAGMA foreign_keys = OFF;
            BEGIN IMMEDIATE;
            DROP TRIGGER mail_outbox_require_workflow_identity;
            DROP TRIGGER mail_outbox_reject_identity_update;
            DROP TRIGGER mail_outbox_reject_terminal_update;
            DROP TRIGGER mail_outbox_require_legal_transition;
            DROP TRIGGER mail_outbox_reject_unresolved_delete;
            DROP INDEX ix_mail_outbox_recovery;
            DROP INDEX ix_mail_outbox_mail_api_unresolved;
            DROP INDEX ix_mail_outbox_smtp_unresolved;
            ALTER TABLE mail_outbox_operations RENAME TO mail_outbox_operations_schema20_fixture;

            {SchemaEighteenOutboxDdl}

            INSERT INTO mail_outbox_operations(
                operation_id, idempotency_key, conversation_id, workflow_run_id, tool_call_id,
                capability_id, capability_version, capability_content_hash, catalog_revision,
                snapshot_hash, connection_slot, binding_hash, draft_hash, submission_state,
                delivery_state, attempt_count, fixed_error_code, revision, created_at_utc,
                updated_at_utc)
            SELECT operation_id, idempotency_key, conversation_id, workflow_run_id, tool_call_id,
                   capability_id, capability_version, capability_content_hash, catalog_revision,
                   snapshot_hash, connection_slot, binding_hash, draft_hash, submission_state,
                   delivery_state, attempt_count, fixed_error_code, revision, created_at_utc,
                   updated_at_utc
            FROM mail_outbox_operations_schema20_fixture
            WHERE transport_kind = 'mail-api';
            DROP TABLE mail_outbox_operations_schema20_fixture;

            DROP TABLE capability_smtp_bindings;
            DROP TABLE smtp_probe_results;
            DROP TABLE smtp_connection_profiles;
            DELETE FROM schema_migrations WHERE version IN (19, 20, 21);
            COMMIT;
            PRAGMA foreign_keys = ON;
            """);
        await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    private static async Task CopyDatabaseAsync(string sourcePath, string destinationPath)
    {
        await using var source = Open(sourcePath, SqliteOpenMode.ReadOnly);
        await using var destination = Open(destinationPath, SqliteOpenMode.ReadWriteCreate);
        await source.OpenAsync();
        await destination.OpenAsync();
        source.BackupDatabase(destination);
    }

    private static async Task ApplySchemaNineteenMigrationAsync(string path)
    {
        await using var connection = Open(path, SqliteOpenMode.ReadWrite);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            BEGIN IMMEDIATE;
            CREATE INDEX ix_mail_outbox_network_started_unresolved
                ON mail_outbox_operations(
                    draft_hash, binding_hash, updated_at_utc DESC, operation_id)
                WHERE submission_state IN ('submitting', 'outcome_unknown');
            DROP INDEX ix_mail_outbox_outcome_unknown;
            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES (19, '2026-08-02T01:00:00.0000000+00:00');
            COMMIT;
            """);
        await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    private static async Task<IReadOnlyList<HistoricalUnresolvedRow>> ReadHistoricalUnresolvedAsync(
        SqliteConnection connection,
        HistoricalUnresolvedQuery query,
        string sql = HistoricalUnresolvedSql)
    {
        var result = new List<HistoricalUnresolvedRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$draftHash", query.DraftHash);
        command.Parameters.AddWithValue("$bindingHash", query.BindingHash);
        command.Parameters.AddWithValue("$limit", query.Limit);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new HistoricalUnresolvedRow(reader.GetString(0), reader.GetString(1)));
        return result.AsReadOnly();
    }

    private static async Task<long> CountHistoricalUnresolvedAsync(
        SqliteConnection connection,
        HistoricalUnresolvedQuery query)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM mail_outbox_operations
            WHERE draft_hash = $draftHash
              AND binding_hash = $bindingHash
              AND submission_state IN ('submitting', 'outcome_unknown');
            """;
        command.Parameters.AddWithValue("$draftHash", query.DraftHash);
        command.Parameters.AddWithValue("$bindingHash", query.BindingHash);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task AssertCurrentUnresolvedParityAsync(
        ConversationStore store,
        IReadOnlyList<HistoricalUnresolvedQuery> queries,
        IReadOnlyDictionary<HistoricalUnresolvedQuery, IReadOnlyList<HistoricalUnresolvedRow>> expected)
    {
        foreach (var query in queries)
        {
            var current = await store.MailOutbox.ListNetworkStartedUnresolvedAsync(
                MailOutboxHash.Parse(query.DraftHash),
                MailOutboxHash.Parse(query.BindingHash),
                query.Limit,
                CancellationToken.None);
            Assert.Equal(
                expected[query],
                current.Select(operation => new HistoricalUnresolvedRow(
                    operation.OperationId.Value,
                    operation.SubmissionState switch
                    {
                        MailOutboxSubmissionState.Submitting => "submitting",
                        MailOutboxSubmissionState.OutcomeUnknown => "outcome_unknown",
                        _ => throw new InvalidOperationException(
                            $"Unexpected unresolved state: {operation.SubmissionState}"),
                    })).ToArray());
        }
    }

    private static async Task<MailOutboxOperation> CreateSmtpOutcomeUnknownAsync(
        ConversationStore store,
        MailOutboxCreateRequest request)
    {
        var staged = (await store.MailOutbox.CreateOrGetAsync(
            request,
            CancellationToken.None)).Operation;
        var submitting = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(
                staged,
                MailOutboxSubmissionState.Submitting,
                MailOutboxDeliveryState.Unknown,
                1,
                null,
                Baseline.AddSeconds(1)),
            CancellationToken.None)).Operation);
        var dataStarted = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(
                submitting,
                MailOutboxSubmissionState.Submitting,
                MailOutboxDeliveryState.Unknown,
                1,
                null,
                Baseline.AddSeconds(2),
                MailOutboxSmtpPhase.DataStarted),
            CancellationToken.None)).Operation);
        return Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(
                dataStarted,
                MailOutboxSubmissionState.OutcomeUnknown,
                MailOutboxDeliveryState.Unknown,
                1,
                "mail.smtp.outcome_unknown",
                Baseline.AddSeconds(3)),
            CancellationToken.None)).Operation);
    }

    private static string ConversationId(string scope) => $"conversation-mail-{scope}";
    private static string ParentRunId(string scope) => $"parent-run-mail-{scope}";
    private static string WorkflowRunId(string scope) => $"workflow-run-mail-{scope}";
    private static string ToolCallId(string scope, string suffix) => $"tool-call-mail-{scope}-{suffix}";
    private static string Hash(char digit) => $"sha256:{new string(digit, 64)}";
    private string DatabasePath(string fileName) => Path.Combine(_directory, fileName);

    private static SqliteConnection Open(string path, SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string table)
    {
        var result = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(1));
        return result;
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(
        SqliteConnection connection,
        string sql)
    {
        var result = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<IReadOnlyList<string>> ReadQueryPlanAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var result = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {sql}";
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(3));
        return result;
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
    }

    private const string SchemaEighteenOutboxDdl =
        """
        CREATE TABLE mail_outbox_operations (
            operation_id TEXT NOT NULL PRIMARY KEY CHECK (
                length(operation_id) = 68
                AND substr(operation_id, 1, 4) = 'mgo_'
                AND substr(operation_id, 5) NOT GLOB '*[^0-9a-f]*'),
            idempotency_key TEXT NOT NULL UNIQUE CHECK (
                length(idempotency_key) = 68
                AND substr(idempotency_key, 1, 4) = 'mik_'
                AND substr(idempotency_key, 5) NOT GLOB '*[^0-9a-f]*'),
            conversation_id TEXT NOT NULL CHECK (length(conversation_id) BETWEEN 1 AND 256),
            workflow_run_id TEXT NOT NULL CHECK (length(workflow_run_id) BETWEEN 1 AND 256),
            tool_call_id TEXT NOT NULL UNIQUE CHECK (length(tool_call_id) BETWEEN 1 AND 256),
            capability_id TEXT NOT NULL CHECK (length(capability_id) BETWEEN 3 AND 128),
            capability_version TEXT NOT NULL CHECK (length(capability_version) BETWEEN 5 AND 64),
            capability_content_hash TEXT NOT NULL CHECK (
                length(capability_content_hash) = 71
                AND substr(capability_content_hash, 1, 7) = 'sha256:'
                AND substr(capability_content_hash, 8) NOT GLOB '*[^0-9a-f]*'),
            catalog_revision INTEGER NOT NULL CHECK (
                catalog_revision BETWEEN 1 AND 9007199254740991),
            snapshot_hash TEXT NOT NULL CHECK (
                length(snapshot_hash) = 71
                AND substr(snapshot_hash, 1, 7) = 'sha256:'
                AND substr(snapshot_hash, 8) NOT GLOB '*[^0-9a-f]*'),
            connection_slot TEXT NOT NULL CHECK (
                length(connection_slot) BETWEEN 1 AND 64
                AND substr(connection_slot, 1, 1) GLOB '[a-z]'
                AND connection_slot NOT GLOB '*[^a-z0-9._-]*'),
            binding_hash TEXT NOT NULL CHECK (
                length(binding_hash) = 71
                AND substr(binding_hash, 1, 7) = 'sha256:'
                AND substr(binding_hash, 8) NOT GLOB '*[^0-9a-f]*'),
            draft_hash TEXT NOT NULL CHECK (
                length(draft_hash) = 71
                AND substr(draft_hash, 1, 7) = 'sha256:'
                AND substr(draft_hash, 8) NOT GLOB '*[^0-9a-f]*'),
            submission_state TEXT NOT NULL CHECK (submission_state IN (
                'staged', 'submitting', 'accepted', 'failed_known',
                'outcome_unknown', 'cancelled_not_started')),
            delivery_state TEXT NOT NULL CHECK (delivery_state IN (
                'unknown', 'queued', 'delivered', 'failed')),
            attempt_count INTEGER NOT NULL CHECK (attempt_count BETWEEN 0 AND 2),
            fixed_error_code TEXT NULL CHECK (fixed_error_code IS NULL OR (
                length(fixed_error_code) BETWEEN 3 AND 128
                AND substr(fixed_error_code, 1, 1) GLOB '[a-z]'
                AND fixed_error_code NOT GLOB '*[^a-z0-9._-]*')),
            revision INTEGER NOT NULL CHECK (revision >= 0),
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            CHECK (
                (submission_state = 'staged'
                    AND delivery_state = 'unknown'
                    AND attempt_count = 0
                    AND fixed_error_code IS NULL)
                OR (submission_state = 'submitting'
                    AND delivery_state = 'unknown'
                    AND attempt_count BETWEEN 1 AND 2
                    AND fixed_error_code IS NULL)
                OR (submission_state = 'accepted'
                    AND delivery_state IN ('queued', 'delivered')
                    AND attempt_count BETWEEN 1 AND 2
                    AND fixed_error_code IS NULL)
                OR (submission_state = 'accepted'
                    AND delivery_state = 'failed'
                    AND attempt_count BETWEEN 1 AND 2
                    AND fixed_error_code IS NOT NULL)
                OR (submission_state = 'failed_known'
                    AND delivery_state = 'failed'
                    AND attempt_count BETWEEN 1 AND 2
                    AND fixed_error_code IS NOT NULL)
                OR (submission_state = 'outcome_unknown'
                    AND delivery_state = 'unknown'
                    AND attempt_count BETWEEN 1 AND 2
                    AND fixed_error_code IS NOT NULL)
                OR (submission_state = 'cancelled_not_started'
                    AND delivery_state = 'unknown'
                    AND attempt_count = 0
                    AND fixed_error_code IS NOT NULL)),
            FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
            FOREIGN KEY (workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE,
            FOREIGN KEY (tool_call_id) REFERENCES workflow_step_runs(tool_call_id) ON DELETE CASCADE
        );
        CREATE INDEX ix_mail_outbox_recovery
            ON mail_outbox_operations(submission_state, updated_at_utc, operation_id);
        CREATE INDEX ix_mail_outbox_outcome_unknown
            ON mail_outbox_operations(draft_hash, binding_hash, updated_at_utc DESC, operation_id)
            WHERE submission_state = 'outcome_unknown';
        CREATE TRIGGER mail_outbox_require_workflow_identity
        BEFORE INSERT ON mail_outbox_operations
        WHEN NOT EXISTS (
            SELECT 1
            FROM workflow_step_runs AS step
            JOIN workflow_runs AS run ON run.id = step.workflow_run_id
            WHERE step.tool_call_id = NEW.tool_call_id
              AND step.workflow_run_id = NEW.workflow_run_id
              AND step.tool_name = 'mail.send.v1'
              AND step.risk = 'R3'
              AND step.retry_class = 'never'
              AND step.maximum_retries = 0
              AND step.effect_class = 'effect'
              AND run.conversation_id = NEW.conversation_id
              AND run.capability_id = NEW.capability_id
              AND run.capability_version = NEW.capability_version
              AND run.capability_content_hash = NEW.capability_content_hash
              AND run.catalog_revision = NEW.catalog_revision
              AND run.snapshot_hash = NEW.snapshot_hash
        )
        BEGIN
            SELECT RAISE(ABORT, 'mail outbox workflow identity mismatch');
        END;
        CREATE TRIGGER mail_outbox_reject_identity_update
        BEFORE UPDATE ON mail_outbox_operations
        WHEN OLD.operation_id IS NOT NEW.operation_id
          OR OLD.idempotency_key IS NOT NEW.idempotency_key
          OR OLD.conversation_id IS NOT NEW.conversation_id
          OR OLD.workflow_run_id IS NOT NEW.workflow_run_id
          OR OLD.tool_call_id IS NOT NEW.tool_call_id
          OR OLD.capability_id IS NOT NEW.capability_id
          OR OLD.capability_version IS NOT NEW.capability_version
          OR OLD.capability_content_hash IS NOT NEW.capability_content_hash
          OR OLD.catalog_revision IS NOT NEW.catalog_revision
          OR OLD.snapshot_hash IS NOT NEW.snapshot_hash
          OR OLD.connection_slot IS NOT NEW.connection_slot
          OR OLD.binding_hash IS NOT NEW.binding_hash
          OR OLD.draft_hash IS NOT NEW.draft_hash
          OR OLD.created_at_utc IS NOT NEW.created_at_utc
        BEGIN
            SELECT RAISE(ABORT, 'mail outbox identity is immutable');
        END;
        CREATE TRIGGER mail_outbox_reject_terminal_update
        BEFORE UPDATE ON mail_outbox_operations
        WHEN OLD.submission_state IN (
                'failed_known', 'cancelled_not_started')
          OR (OLD.submission_state = 'accepted'
              AND OLD.delivery_state IN ('delivered', 'failed'))
        BEGIN
            SELECT RAISE(ABORT, 'terminal mail outbox operation is immutable');
        END;
        CREATE TRIGGER mail_outbox_require_legal_transition
        BEFORE UPDATE ON mail_outbox_operations
        WHEN NEW.revision <> OLD.revision + 1
          OR NEW.updated_at_utc < OLD.updated_at_utc
          OR NOT (
              (OLD.submission_state = 'staged'
                  AND OLD.delivery_state = 'unknown'
                  AND OLD.attempt_count = 0
                  AND NEW.submission_state = 'submitting'
                  AND NEW.delivery_state = 'unknown'
                  AND NEW.attempt_count = 1
                  AND NEW.fixed_error_code IS NULL)
              OR (OLD.submission_state = 'staged'
                  AND OLD.delivery_state = 'unknown'
                  AND OLD.attempt_count = 0
                  AND NEW.submission_state = 'cancelled_not_started'
                  AND NEW.delivery_state = 'unknown'
                  AND NEW.attempt_count = 0
                  AND NEW.fixed_error_code IS NOT NULL)
              OR (OLD.submission_state = 'submitting'
                  AND OLD.delivery_state = 'unknown'
                  AND NEW.submission_state = 'accepted'
                  AND NEW.delivery_state = 'queued'
                  AND NEW.attempt_count = OLD.attempt_count
                  AND NEW.fixed_error_code IS NULL)
              OR (OLD.submission_state = 'submitting'
                  AND OLD.delivery_state = 'unknown'
                  AND NEW.submission_state = 'failed_known'
                  AND NEW.delivery_state = 'failed'
                  AND NEW.attempt_count = OLD.attempt_count
                  AND NEW.fixed_error_code IS NOT NULL)
              OR (OLD.submission_state = 'submitting'
                  AND OLD.delivery_state = 'unknown'
                  AND NEW.submission_state = 'outcome_unknown'
                  AND NEW.delivery_state = 'unknown'
                  AND NEW.attempt_count = OLD.attempt_count
                  AND NEW.fixed_error_code IS NOT NULL)
              OR (OLD.submission_state = 'outcome_unknown'
                  AND OLD.delivery_state = 'unknown'
                  AND NEW.submission_state = 'accepted'
                  AND NEW.delivery_state = 'queued'
                  AND NEW.attempt_count = OLD.attempt_count
                  AND NEW.fixed_error_code IS NULL)
              OR (OLD.submission_state = 'outcome_unknown'
                  AND OLD.delivery_state = 'unknown'
                  AND NEW.submission_state = 'failed_known'
                  AND NEW.delivery_state = 'failed'
                  AND NEW.attempt_count = OLD.attempt_count
                  AND NEW.fixed_error_code IS NOT NULL)
              OR (OLD.submission_state = 'outcome_unknown'
                  AND OLD.delivery_state = 'unknown'
                  AND OLD.attempt_count = 1
                  AND NEW.submission_state = 'submitting'
                  AND NEW.delivery_state = 'unknown'
                  AND NEW.attempt_count = 2
                  AND NEW.fixed_error_code IS NULL)
              OR (OLD.submission_state = 'accepted'
                  AND OLD.delivery_state = 'queued'
                  AND NEW.submission_state = 'accepted'
                  AND NEW.delivery_state = 'delivered'
                  AND NEW.attempt_count = OLD.attempt_count
                  AND NEW.fixed_error_code IS NULL)
              OR (OLD.submission_state = 'accepted'
                  AND OLD.delivery_state = 'queued'
                  AND NEW.submission_state = 'accepted'
                  AND NEW.delivery_state = 'failed'
                  AND NEW.attempt_count = OLD.attempt_count
                  AND NEW.fixed_error_code IS NOT NULL))
        BEGIN
            SELECT RAISE(ABORT, 'mail outbox transition is invalid');
        END;
        CREATE TRIGGER mail_outbox_reject_unresolved_delete
        BEFORE DELETE ON mail_outbox_operations
        WHEN OLD.submission_state IN ('staged', 'submitting', 'outcome_unknown')
          OR (OLD.submission_state = 'accepted'
              AND OLD.delivery_state = 'queued')
        BEGIN
            SELECT RAISE(ABORT, 'unresolved mail outbox operation cannot be deleted');
        END;
        """;

    private enum HistoricalTerminalState
    {
        Staged,
        Submitting,
        OutcomeUnknown,
    }

    private sealed record HistoricalUnresolvedQuery(
        string DraftHash,
        string BindingHash,
        int Limit);

    private sealed record HistoricalUnresolvedRow(
        string OperationId,
        string SubmissionState);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
