using LIGClaw.Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Tests;

public sealed class WorkflowLedgerRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.Parse("2026-08-02T01:00:00Z");
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        Guid.NewGuid().ToString("N"));

    public WorkflowLedgerRepositoryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task MigratesToSchemaTwentyOneWithMetadataOnlyWorkflowTables()
    {
        var path = DatabasePath("schema.db");
        using (var store = new ConversationStore(path))
            await store.InitializeAsync();

        await using var connection = OpenReadOnly(path);
        await connection.OpenAsync();

        Assert.Equal(21, await ScalarInt64Async(
            connection,
            "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(
            [
                "workflow_authorization_ticket_items",
                "workflow_authorization_tickets",
                "workflow_runs",
                "workflow_step_runs",
            ],
            await ReadStringsAsync(
                connection,
                """
                SELECT name
                FROM sqlite_master
                WHERE type = 'table' AND name LIKE 'workflow_%'
                ORDER BY name;
                """));

        foreach (var table in new[]
                 {
                     "workflow_runs",
                     "workflow_authorization_tickets",
                     "workflow_authorization_ticket_items",
                     "workflow_step_runs",
                 })
        {
            var columns = await ReadTableColumnsAsync(connection, table);
            Assert.DoesNotContain(
                columns,
                column => column is "input" or "payload" or "output" or "result" or "prompt" or "arguments");
            Assert.All(
                columns.Where(column =>
                    column.Contains("input", StringComparison.Ordinal) ||
                    column.Contains("payload", StringComparison.Ordinal) ||
                    column.Contains("output", StringComparison.Ordinal) ||
                    column.Contains("result", StringComparison.Ordinal)),
                column => Assert.EndsWith("_hash", column, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task MigratesAnExistingSchemaThirteenDatabaseWithoutLosingParentRunsOrForeignKeys()
    {
        var path = DatabasePath("schema-thirteen.db");
        const string conversationId = "conversation-schema-thirteen";
        const string parentRunId = "parent-run-schema-thirteen";
        using (var current = new ConversationStore(path))
        {
            await current.InitializeAsync();
            await current.StartRunAsync(conversationId, parentRunId, "preserved parent", Baseline);
        }

        await using (var schemaThirteen = OpenWritable(path))
        {
            await schemaThirteen.OpenAsync();
            await ExecuteNonQueryAsync(
                schemaThirteen,
                """
                PRAGMA foreign_keys=OFF;
                DROP TABLE capability_smtp_bindings;
                DROP TABLE smtp_probe_results;
                DROP TABLE smtp_connection_profiles;
                DROP TABLE capability_mail_gateway_bindings;
                DROP TABLE mail_gateway_probe_results;
                DROP TABLE mail_gateway_connection_profiles;
                DROP TABLE mail_outbox_operations;
                DROP TABLE capability_test_runs;
                DROP TABLE improvement_proposals;
                DROP TABLE feature_builder_sessions;
                DROP TABLE capability_connection_bindings;
                DROP TABLE connection_probe_results;
                DROP TABLE http_connection_profiles;
                DROP TABLE connection_profiles;
                DROP TRIGGER workflow_ticket_item_require_matching_step;
                DROP TABLE workflow_step_runs;
                DROP TABLE workflow_authorization_ticket_items;
                DROP TABLE workflow_authorization_tickets;
                DROP TABLE workflow_runs;
                DROP INDEX ux_conversation_runs_run_conversation;
                DELETE FROM schema_migrations WHERE version IN (14, 15, 16, 17, 18, 19, 20, 21);
                """);
        }

        using (var migrated = new ConversationStore(path))
        {
            await migrated.InitializeAsync();
            Assert.Equal(
                parentRunId,
                Assert.Single(await migrated.GetConversationTurnsAsync(conversationId)).RunId);
            var run = RunStart(
                "workflow-run-schema-thirteen",
                conversationId,
                parentRunId,
                "schema-thirteen-key",
                "R0");
            Assert.Equal(
                WorkflowRunStartOutcome.Created,
                (await migrated.WorkflowLedger.StartOrCoalesceRunAsync(run, CancellationToken.None)).Outcome);
        }

        await using var verified = OpenWritable(path);
        await verified.OpenAsync();
        await ExecuteNonQueryAsync(verified, "PRAGMA foreign_keys=ON;");
        await using (var foreignKeys = verified.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await foreignKeys.ExecuteReaderAsync();
            Assert.False(await reader.ReadAsync());
        }
        await using (var delete = verified.CreateCommand())
        {
            delete.CommandText = "DELETE FROM conversations WHERE id = $conversationId;";
            delete.Parameters.AddWithValue("$conversationId", conversationId);
            Assert.Equal(1, await delete.ExecuteNonQueryAsync());
        }
        Assert.Equal(0, await ScalarInt64Async(verified, "SELECT COUNT(*) FROM workflow_runs;"));
        Assert.Equal(21, await ScalarInt64Async(verified, "SELECT MAX(version) FROM schema_migrations;"));
    }

    [Fact]
    public async Task CoalescesOnlyTheSameExactIdentityAndInputForAnIdempotencyKey()
    {
        using var store = await CreateStoreWithParentAsync(
            "idempotency.db",
            "conversation-idempotency",
            "parent-run-idempotency");
        var original = RunStart(
            "workflow-run-original",
            "conversation-idempotency",
            "parent-run-idempotency",
            "idempotency-key");

        var created = await store.WorkflowLedger.StartOrCoalesceRunAsync(original, CancellationToken.None);
        var coalesced = await store.WorkflowLedger.StartOrCoalesceRunAsync(
            original with
            {
                RunId = "workflow-run-duplicate",
                CreatedAtUtc = Baseline.AddMinutes(1),
            },
            CancellationToken.None);
        var conflict = await store.WorkflowLedger.StartOrCoalesceRunAsync(
            original with
            {
                RunId = "workflow-run-conflict",
                InputHash = Hash('f'),
                CreatedAtUtc = Baseline.AddMinutes(2),
            },
            CancellationToken.None);
        var parentToolReplay = await store.WorkflowLedger.StartOrCoalesceRunAsync(
            original with
            {
                RunId = "workflow-run-parent-tool-replay",
                IdempotencyKey = "different-idempotency-key",
                CreatedAtUtc = Baseline.AddMinutes(3),
            },
            CancellationToken.None);

        Assert.Equal(WorkflowRunStartOutcome.Created, created.Outcome);
        Assert.Equal(WorkflowRunStartOutcome.Coalesced, coalesced.Outcome);
        Assert.Equal(original.RunId, coalesced.Run.RunId);
        Assert.Null(coalesced.ErrorCode);
        Assert.Equal(WorkflowRunStartOutcome.Conflict, conflict.Outcome);
        Assert.Equal(original.RunId, conflict.Run.RunId);
        Assert.Equal("workflow.idempotency_conflict", conflict.ErrorCode);
        Assert.Equal(WorkflowRunStartOutcome.Conflict, parentToolReplay.Outcome);
        Assert.Equal(original.RunId, parentToolReplay.Run.RunId);
        Assert.Equal("workflow.idempotency_conflict", parentToolReplay.ErrorCode);
    }

    [Fact]
    public async Task ReservesAnEffectAndConsumesItsTicketItemExactlyOnceBeforeRecordingUnknownOutcome()
    {
        using var store = await CreateStoreWithParentAsync(
            "reservation.db",
            "conversation-reservation",
            "parent-run-reservation");
        var ledger = store.WorkflowLedger;
        var run = RunStart(
            "workflow-run-reservation",
            "conversation-reservation",
            "parent-run-reservation",
            "reservation-key");
        var payloadHash = Hash('1');
        var bindingHash = Hash('2');
        var nonceHash = Hash('3');
        var step = new WorkflowStepPlan(
            run.RunId,
            0,
            1,
            "step-effect",
            "tool-call-effect",
            "app.launch.v1",
            "R1",
            "never",
            WorkflowLedgerEffectClass.Effect,
            payloadHash,
            bindingHash,
            Baseline.AddSeconds(1));

        Assert.Equal(
            WorkflowRunStartOutcome.Created,
            (await ledger.StartOrCoalesceRunAsync(run, CancellationToken.None)).Outcome);
        Assert.True(await ledger.AddPlannedStepAsync(step, CancellationToken.None));
        var ticketItem = new WorkflowAuthorizationTicketItemDraft(
            "ticket-item-effect",
            0,
            nonceHash,
            step.ToolName,
            step.Risk,
            payloadHash,
            bindingHash);
        Assert.False(await ledger.SaveAuthorizationTicketAsync(
            new WorkflowAuthorizationTicketDraft(
                "ticket-risk-downgrade",
                run.RunId,
                "R0",
                Baseline.AddSeconds(2),
                Baseline.AddMinutes(7),
                [ticketItem]),
            CancellationToken.None));
        Assert.False(await ledger.SaveAuthorizationTicketAsync(
            new WorkflowAuthorizationTicketDraft(
                "ticket-stale-run",
                run.RunId,
                "R1",
                Baseline.AddSeconds(2),
                Baseline.AddMinutes(7),
                [ticketItem],
                ExpectedRunRevision: 1,
                ExpectedCurrentSequence: -1),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ledger.SaveAuthorizationTicketAsync(
                new WorkflowAuthorizationTicketDraft(
                    "ticket-too-long",
                    run.RunId,
                    "R1",
                    Baseline.AddSeconds(2),
                    Baseline.AddSeconds(2).AddMinutes(9),
                    [ticketItem]),
                CancellationToken.None));
        Assert.Null(await ledger.GetTicketAsync("ticket-risk-downgrade", CancellationToken.None));
        Assert.Null(await ledger.GetTicketAsync("ticket-stale-run", CancellationToken.None));
        Assert.True(await ledger.SaveAuthorizationTicketAsync(
            new WorkflowAuthorizationTicketDraft(
                "ticket-effect",
                run.RunId,
                "R1",
                Baseline.AddSeconds(2),
                Baseline.AddMinutes(7),
                [ticketItem]),
            CancellationToken.None));

        var storedTicket = Assert.IsType<WorkflowAuthorizationTicketRecord>(
            await ledger.GetTicketAsync("ticket-effect", CancellationToken.None));
        var storedItem = Assert.IsType<WorkflowAuthorizationTicketItemRecord>(
            await ledger.GetTicketItemAsync("ticket-item-effect", CancellationToken.None));
        Assert.Equal("active", storedTicket.Status);
        Assert.Equal("allow_once", await ReadApprovalScopeAsync(store.DatabasePath, storedTicket.TicketId));
        Assert.Equal("ready", storedItem.Status);
        Assert.Equal(payloadHash, storedItem.PayloadHash);
        Assert.Equal(bindingHash, storedItem.BindingHash);

        var exactReservation = new WorkflowEffectReservation(
            run.RunId,
            step.ToolCallId,
            storedTicket.TicketId,
            storedItem.ItemId,
            step.Sequence,
            nonceHash,
            step.ToolName,
            step.Risk,
            payloadHash,
            bindingHash,
            1,
            -1,
            Baseline.AddSeconds(3));
        Assert.False(await ledger.TryReserveEffectStepAsync(
            exactReservation with { BindingHash = Hash('4') },
            CancellationToken.None));
        Assert.Equal(
            "ready",
            (await ledger.GetTicketItemAsync(storedItem.ItemId, CancellationToken.None))!.Status);
        Assert.Equal(
            "planned",
            (await ledger.GetStepAsync(step.ToolCallId, CancellationToken.None))!.Status);

        var concurrent = await Task.WhenAll(
            ledger.TryReserveEffectStepAsync(exactReservation, CancellationToken.None),
            ledger.TryReserveEffectStepAsync(exactReservation, CancellationToken.None));

        Assert.Single(concurrent, result => result);
        var consumed = Assert.IsType<WorkflowAuthorizationTicketItemRecord>(
            await ledger.GetTicketItemAsync(storedItem.ItemId, CancellationToken.None));
        var executing = Assert.IsType<WorkflowStepRunRecord>(
            await ledger.GetStepAsync(step.ToolCallId, CancellationToken.None));
        Assert.Equal("consumed", consumed.Status);
        Assert.Equal(step.ToolCallId, consumed.ConsumedByToolCallId);
        Assert.Equal("executing", executing.Status);
        Assert.Equal(storedItem.ItemId, executing.TicketItemId);
        Assert.Equal(
            "consumed",
            (await ledger.GetTicketAsync(storedTicket.TicketId, CancellationToken.None))!.Status);

        Assert.True(await ledger.CompleteStepAsync(
            new WorkflowStepCompletion(
                run.RunId,
                step.ToolCallId,
                WorkflowStepCompletionDisposition.Unknown,
                null,
                "실행 결과를 확인할 수 없습니다.",
                "workflow.adapter.outcome_unknown",
                2,
                Baseline.AddSeconds(4)),
            CancellationToken.None));

        var completed = Assert.IsType<WorkflowStepRunRecord>(
            await ledger.GetStepAsync(step.ToolCallId, CancellationToken.None));
        var completedRun = Assert.IsType<WorkflowRunRecord>(
            await ledger.GetRunAsync(run.RunId, CancellationToken.None));
        Assert.Equal("outcome_unknown", completed.Status);
        Assert.Equal("unknown", completed.Disposition);
        Assert.Null(completed.OutputHash);
        Assert.Equal("workflow.adapter.outcome_unknown", completed.FixedErrorCode);
        Assert.Equal("attention_required", completedRun.Status);
        Assert.Equal("review_outcome", completedRun.FixedNextAction);
        Assert.Null(completedRun.ResultHash);

        Assert.True(await ledger.CompleteRunAsync(
            new WorkflowRunCompletion(
                run.RunId,
                WorkflowRunTerminalStatus.AttentionRequired,
                WorkflowRunNextAction.ReviewOutcome,
                "workflow.adapter.outcome_unknown",
                "실행 결과를 검토해야 합니다.",
                null,
                3,
                0,
                Baseline.AddSeconds(5)),
            CancellationToken.None));
        var finalizedRun = Assert.IsType<WorkflowRunRecord>(
            await ledger.GetRunAsync(run.RunId, CancellationToken.None));
        Assert.Equal("review_outcome", finalizedRun.FixedNextAction);
        Assert.Equal("실행 결과를 검토해야 합니다.", finalizedRun.RedactedSummary);
        Assert.NotNull(finalizedRun.CompletedAtUtc);
    }

    [Fact]
    public async Task ReopenRecoveryRevokesActiveTicketsAndSeparatesObservationFromEffectOutcomes()
    {
        var path = DatabasePath("recovery.db");
        var clock = new MutableTimeProvider(Baseline.AddSeconds(20));
        const string conversationId = "conversation-recovery";
        const string observationParentRunId = "parent-run-observation";
        const string effectParentRunId = "parent-run-effect";
        var observationRun = RunStart(
            "workflow-run-observation",
            conversationId,
            observationParentRunId,
            "observation-key",
            "R0");
        var effectRun = RunStart(
            "workflow-run-effect",
            conversationId,
            effectParentRunId,
            "effect-key");
        var observationStep = new WorkflowStepPlan(
            observationRun.RunId,
            0,
            1,
            "step-observation",
            "tool-call-observation",
            "system.get_status.v1",
            "R0",
            "read_only",
            WorkflowLedgerEffectClass.Observation,
            Hash('5'),
            Hash('6'),
            Baseline.AddSeconds(1));
        var firstEffectStep = new WorkflowStepPlan(
            effectRun.RunId,
            0,
            1,
            "step-effect-first",
            "tool-call-effect-first",
            "app.launch.v1",
            "R1",
            "never",
            WorkflowLedgerEffectClass.Effect,
            Hash('7'),
            Hash('8'),
            Baseline.AddSeconds(1));
        var secondEffectStep = new WorkflowStepPlan(
            effectRun.RunId,
            1,
            1,
            "step-effect-second",
            "tool-call-effect-second",
            "system.show_notification.v1",
            "R1",
            "never",
            WorkflowLedgerEffectClass.Effect,
            Hash('9'),
            Hash('a'),
            Baseline.AddSeconds(2));

        using (var first = new ConversationStore(path, clock))
        {
            await first.InitializeAsync();
            await first.StartRunAsync(conversationId, observationParentRunId, "observation parent", Baseline);
            await first.StartRunAsync(conversationId, effectParentRunId, "effect parent", Baseline.AddSeconds(1));
            var ledger = first.WorkflowLedger;

            Assert.Equal(
                WorkflowRunStartOutcome.Created,
                (await ledger.StartOrCoalesceRunAsync(observationRun, CancellationToken.None)).Outcome);
            Assert.True(await ledger.AddPlannedStepAsync(observationStep, CancellationToken.None));
            Assert.True(await ledger.TryStartObservationStepAsync(
                new WorkflowObservationReservation(
                    observationRun.RunId,
                    observationStep.ToolCallId,
                    observationStep.Sequence,
                    observationStep.ToolName,
                    observationStep.Risk,
                    observationStep.RetryClass,
                    observationStep.PayloadHash,
                    observationStep.BindingHash,
                    0,
                    -1,
                    Baseline.AddSeconds(10)),
                CancellationToken.None));

            Assert.Equal(
                WorkflowRunStartOutcome.Created,
                (await ledger.StartOrCoalesceRunAsync(effectRun, CancellationToken.None)).Outcome);
            Assert.True(await ledger.AddPlannedStepAsync(firstEffectStep, CancellationToken.None));
            Assert.True(await ledger.AddPlannedStepAsync(secondEffectStep, CancellationToken.None));
            Assert.True(await ledger.SaveAuthorizationTicketAsync(
                new WorkflowAuthorizationTicketDraft(
                    "ticket-recovery",
                    effectRun.RunId,
                    "R1",
                    Baseline.AddSeconds(3),
                    Baseline.AddMinutes(8),
                    [
                        TicketItem("ticket-item-consumed", firstEffectStep, Hash('b')),
                        TicketItem("ticket-item-ready", secondEffectStep, Hash('c')),
                    ]),
                CancellationToken.None));
            Assert.False(await ledger.TryReserveEffectStepAsync(
                new WorkflowEffectReservation(
                    effectRun.RunId,
                    secondEffectStep.ToolCallId,
                    "ticket-recovery",
                    "ticket-item-ready",
                    secondEffectStep.Sequence,
                    Hash('c'),
                    secondEffectStep.ToolName,
                    secondEffectStep.Risk,
                    secondEffectStep.PayloadHash,
                    secondEffectStep.BindingHash,
                    1,
                    -1,
                    Baseline.AddSeconds(19)),
                CancellationToken.None));
            Assert.True(await ledger.TryReserveEffectStepAsync(
                new WorkflowEffectReservation(
                    effectRun.RunId,
                    firstEffectStep.ToolCallId,
                    "ticket-recovery",
                    "ticket-item-consumed",
                    firstEffectStep.Sequence,
                    Hash('b'),
                    firstEffectStep.ToolName,
                    firstEffectStep.Risk,
                    firstEffectStep.PayloadHash,
                    firstEffectStep.BindingHash,
                    1,
                    -1,
                    Baseline.AddSeconds(20)),
                CancellationToken.None));
            var effectRunAfterFirstReservation = Assert.IsType<WorkflowRunRecord>(
                await ledger.GetRunAsync(effectRun.RunId, CancellationToken.None));
            Assert.False(await ledger.TryReserveEffectStepAsync(
                new WorkflowEffectReservation(
                    effectRun.RunId,
                    secondEffectStep.ToolCallId,
                    "ticket-recovery",
                    "ticket-item-ready",
                    secondEffectStep.Sequence,
                    Hash('c'),
                    secondEffectStep.ToolName,
                    secondEffectStep.Risk,
                    secondEffectStep.PayloadHash,
                    secondEffectStep.BindingHash,
                    effectRunAfterFirstReservation.Revision,
                    effectRunAfterFirstReservation.CurrentSequence,
                    Baseline.AddSeconds(21)),
                CancellationToken.None));
        }

        clock.SetUtcNow(Baseline.AddMinutes(2));
        using var reopened = new ConversationStore(path, clock);
        var recovery = await reopened.InitializeForDesktopStartupAsync(CancellationToken.None);

        Assert.Equal(1, recovery.RevokedTickets);
        Assert.Equal(1, recovery.RevokedTicketItems);
        Assert.Equal(1, recovery.InterruptedObservations);
        Assert.Equal(1, recovery.UnknownEffects);
        Assert.Equal(2, recovery.RecoveredRuns);

        var ticket = Assert.IsType<WorkflowAuthorizationTicketRecord>(
            await reopened.WorkflowLedger.GetTicketAsync("ticket-recovery", CancellationToken.None));
        var consumedItem = Assert.IsType<WorkflowAuthorizationTicketItemRecord>(
            await reopened.WorkflowLedger.GetTicketItemAsync("ticket-item-consumed", CancellationToken.None));
        var revokedItem = Assert.IsType<WorkflowAuthorizationTicketItemRecord>(
            await reopened.WorkflowLedger.GetTicketItemAsync("ticket-item-ready", CancellationToken.None));
        var recoveredObservation = Assert.IsType<WorkflowStepRunRecord>(
            await reopened.WorkflowLedger.GetStepAsync(observationStep.ToolCallId, CancellationToken.None));
        var recoveredEffect = Assert.IsType<WorkflowStepRunRecord>(
            await reopened.WorkflowLedger.GetStepAsync(firstEffectStep.ToolCallId, CancellationToken.None));
        var recoveredPlannedEffect = Assert.IsType<WorkflowStepRunRecord>(
            await reopened.WorkflowLedger.GetStepAsync(secondEffectStep.ToolCallId, CancellationToken.None));
        var recoveredObservationRun = Assert.IsType<WorkflowRunRecord>(
            await reopened.WorkflowLedger.GetRunAsync(observationRun.RunId, CancellationToken.None));
        var recoveredEffectRun = Assert.IsType<WorkflowRunRecord>(
            await reopened.WorkflowLedger.GetRunAsync(effectRun.RunId, CancellationToken.None));

        Assert.Equal("revoked", ticket.Status);
        Assert.Equal("desktop_restart", ticket.RevocationCode);
        Assert.Equal("consumed", consumedItem.Status);
        Assert.Equal("revoked", revokedItem.Status);
        Assert.Equal("desktop_restart", revokedItem.RevocationCode);
        Assert.Equal("interrupted", recoveredObservation.Status);
        Assert.Equal("unknown", recoveredObservation.Disposition);
        Assert.Equal("outcome_unknown", recoveredEffect.Status);
        Assert.Equal("unknown", recoveredEffect.Disposition);
        Assert.Equal("not_started", recoveredPlannedEffect.Status);
        Assert.Equal("not_started", recoveredPlannedEffect.Disposition);
        Assert.Equal("interrupted", recoveredObservationRun.Status);
        Assert.Equal("attention_required", recoveredEffectRun.Status);
        Assert.Equal("review_outcome", recoveredEffectRun.FixedNextAction);
        Assert.NotNull(recoveredObservationRun.CompletedAtUtc);
        Assert.NotNull(recoveredEffectRun.CompletedAtUtc);

        var secondRecovery = await reopened.WorkflowLedger.RecoverOnStartupAsync(
            Baseline.AddMinutes(3),
            CancellationToken.None);
        Assert.Equal(
            new WorkflowStartupRecoveryResult(0, 0, 0, 0, 0),
            secondRecovery);
    }

    [Fact]
    public async Task StartsObservationStepsOnlyInStrictSerialOrder()
    {
        using var store = await CreateStoreWithParentAsync(
            "strict-order.db",
            "conversation-strict-order",
            "parent-run-strict-order");
        var ledger = store.WorkflowLedger;
        var run = RunStart(
            "workflow-run-strict-order",
            "conversation-strict-order",
            "parent-run-strict-order",
            "strict-order-key",
            "R0");
        var first = ObservationStep(run.RunId, 0, "first", '1', '2');
        var second = ObservationStep(run.RunId, 1, "second", '3', '4');
        var third = ObservationStep(run.RunId, 2, "third", '5', '6');

        Assert.Equal(
            WorkflowRunStartOutcome.Created,
            (await ledger.StartOrCoalesceRunAsync(run, CancellationToken.None)).Outcome);
        Assert.True(await ledger.AddPlannedStepAsync(first, CancellationToken.None));
        Assert.True(await ledger.AddPlannedStepAsync(second, CancellationToken.None));
        Assert.True(await ledger.AddPlannedStepAsync(third, CancellationToken.None));

        Assert.False(await ledger.TryStartObservationStepAsync(
            ObservationReservation(second, 0, -1, Baseline.AddSeconds(1)),
            CancellationToken.None));
        Assert.Equal("planned", (await ledger.GetStepAsync(second.ToolCallId, CancellationToken.None))!.Status);

        Assert.True(await ledger.TryStartObservationStepAsync(
            ObservationReservation(first, 0, -1, Baseline.AddSeconds(2)),
            CancellationToken.None));
        Assert.True(await ledger.CompleteStepAsync(
            new WorkflowStepCompletion(
                run.RunId,
                first.ToolCallId,
                WorkflowStepCompletionDisposition.Succeeded,
                Hash('7'),
                null,
                null,
                1,
                Baseline.AddSeconds(3)),
            CancellationToken.None));

        Assert.True(await ledger.TryStartObservationStepAsync(
            ObservationReservation(second, 2, 0, Baseline.AddSeconds(4)),
            CancellationToken.None));
        Assert.False(await ledger.TryStartObservationStepAsync(
            ObservationReservation(third, 3, 1, Baseline.AddSeconds(5)),
            CancellationToken.None));
        Assert.Equal("planned", (await ledger.GetStepAsync(third.ToolCallId, CancellationToken.None))!.Status);

        Assert.True(await ledger.CompleteStepAsync(
            new WorkflowStepCompletion(
                run.RunId,
                second.ToolCallId,
                WorkflowStepCompletionDisposition.Succeeded,
                Hash('8'),
                null,
                null,
                3,
                Baseline.AddSeconds(6)),
            CancellationToken.None));
        Assert.True(await ledger.TryStartObservationStepAsync(
            ObservationReservation(third, 4, 1, Baseline.AddSeconds(7)),
            CancellationToken.None));
    }

    [Fact]
    public async Task RecordsRetryableObservationOnlyWithinItsStoredRetryBound()
    {
        using var store = await CreateStoreWithParentAsync(
            "observation-retry.db",
            "conversation-observation-retry",
            "parent-run-observation-retry");
        var ledger = store.WorkflowLedger;
        var run = RunStart(
            "workflow-run-observation-retry",
            "conversation-observation-retry",
            "parent-run-observation-retry",
            "observation-retry-key",
            "R0");
        var firstAttempt = new WorkflowStepPlan(
            run.RunId,
            0,
            1,
            "step-observation-retry",
            "tool-call-observation-retry-1",
            "system.get_status.v1",
            "R0",
            "read_only",
            WorkflowLedgerEffectClass.Observation,
            Hash('1'),
            Hash('2'),
            Baseline.AddSeconds(1),
            MaximumRetries: 1);

        Assert.Equal(
            WorkflowRunStartOutcome.Created,
            (await ledger.StartOrCoalesceRunAsync(run, CancellationToken.None)).Outcome);
        Assert.True(await ledger.AddPlannedStepAsync(firstAttempt, CancellationToken.None));
        Assert.True(await ledger.TryStartObservationStepAsync(
            ObservationReservation(firstAttempt, 0, -1, Baseline.AddSeconds(2)),
            CancellationToken.None));
        Assert.True(await ledger.CompleteStepAsync(
            new WorkflowStepCompletion(
                run.RunId,
                firstAttempt.ToolCallId,
                WorkflowStepCompletionDisposition.RetryableFailed,
                null,
                "읽기 전용 관찰을 다시 시도합니다.",
                "workflow.observation.retryable",
                1,
                Baseline.AddSeconds(3)),
            CancellationToken.None));

        var retriedStep = Assert.IsType<WorkflowStepRunRecord>(
            await ledger.GetStepAsync(firstAttempt.ToolCallId, CancellationToken.None));
        var liveRun = Assert.IsType<WorkflowRunRecord>(
            await ledger.GetRunAsync(run.RunId, CancellationToken.None));
        Assert.Equal("retryable_failed", retriedStep.Status);
        Assert.Equal("retryable_failed", retriedStep.Disposition);
        Assert.Equal("running", liveRun.Status);
        Assert.Equal(2, liveRun.Revision);
        Assert.False(await ledger.CompleteRunAsync(
            new WorkflowRunCompletion(
                run.RunId,
                WorkflowRunTerminalStatus.Completed,
                WorkflowRunNextAction.None,
                null,
                "Workflow를 완료했습니다.",
                Hash('9'),
                liveRun.Revision,
                liveRun.CurrentSequence,
                Baseline.AddSeconds(4)),
            CancellationToken.None));

        var secondAttempt = firstAttempt with
        {
            Attempt = 2,
            ToolCallId = "tool-call-observation-retry-2",
            CreatedAtUtc = Baseline.AddSeconds(4),
        };
        Assert.True(await ledger.AddPlannedStepAsync(secondAttempt, CancellationToken.None));
        Assert.True(await ledger.TryStartObservationStepAsync(
            ObservationReservation(secondAttempt, 2, 0, Baseline.AddSeconds(5)),
            CancellationToken.None));
        Assert.False(await ledger.CompleteStepAsync(
            new WorkflowStepCompletion(
                run.RunId,
                secondAttempt.ToolCallId,
                WorkflowStepCompletionDisposition.RetryableFailed,
                null,
                null,
                "workflow.observation.retry_exhausted",
                3,
                Baseline.AddSeconds(6)),
            CancellationToken.None));
        Assert.Equal(
            "executing",
            (await ledger.GetStepAsync(secondAttempt.ToolCallId, CancellationToken.None))!.Status);
        Assert.True(await ledger.CompleteStepAsync(
            new WorkflowStepCompletion(
                run.RunId,
                secondAttempt.ToolCallId,
                WorkflowStepCompletionDisposition.FailedKnown,
                null,
                null,
                "workflow.observation.retry_exhausted",
                3,
                Baseline.AddSeconds(7)),
            CancellationToken.None));
        Assert.Equal(
            "failed",
            (await ledger.GetRunAsync(run.RunId, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task AbandonsAPlannedEffectAndBlocksAnyRetryAfterAnEffectStarted()
    {
        using var store = await CreateStoreWithParentAsync(
            "effect-at-most-once.db",
            "conversation-effect-at-most-once",
            "parent-run-effect-at-most-once");
        var ledger = store.WorkflowLedger;
        var run = RunStart(
            "workflow-run-effect-at-most-once",
            "conversation-effect-at-most-once",
            "parent-run-effect-at-most-once",
            "effect-at-most-once-key");
        var firstAttempt = new WorkflowStepPlan(
            run.RunId,
            0,
            1,
            "step-effect-at-most-once",
            "tool-call-effect-at-most-once-1",
            "app.launch.v1",
            "R1",
            "never",
            WorkflowLedgerEffectClass.Effect,
            Hash('3'),
            Hash('4'),
            Baseline.AddSeconds(1));

        Assert.Equal(
            WorkflowRunStartOutcome.Created,
            (await ledger.StartOrCoalesceRunAsync(run, CancellationToken.None)).Outcome);
        Assert.True(await ledger.AddPlannedStepAsync(firstAttempt, CancellationToken.None));
        var abandonedTicketItem = TicketItem(
            "ticket-item-abandoned-effect",
            firstAttempt,
            Hash('5'));
        Assert.True(await ledger.SaveAuthorizationTicketAsync(
            new WorkflowAuthorizationTicketDraft(
                "ticket-abandoned-effect",
                run.RunId,
                "R1",
                Baseline.AddSeconds(1),
                Baseline.AddMinutes(7),
                [abandonedTicketItem]),
            CancellationToken.None));
        var abandonment = new WorkflowPlannedStepAbandonment(
            run.RunId,
            firstAttempt.ToolCallId,
            "workflow.approval.declined",
            1,
            -1,
            Baseline.AddSeconds(2));
        Assert.False(await ledger.AbandonPlannedStepAsync(
            abandonment with { ExpectedRunRevision = 2 },
            CancellationToken.None));
        Assert.Equal(
            "planned",
            (await ledger.GetStepAsync(firstAttempt.ToolCallId, CancellationToken.None))!.Status);
        Assert.True(await ledger.AbandonPlannedStepAsync(abandonment, CancellationToken.None));
        Assert.Equal(
            "not_started",
            (await ledger.GetStepAsync(firstAttempt.ToolCallId, CancellationToken.None))!.Status);
        Assert.Equal(
            "revoked",
            (await ledger.GetTicketAsync("ticket-abandoned-effect", CancellationToken.None))!.Status);
        Assert.Equal(
            "revoked",
            (await ledger.GetTicketItemAsync(abandonedTicketItem.ItemId, CancellationToken.None))!.Status);
        var abandonedRun = Assert.IsType<WorkflowRunRecord>(
            await ledger.GetRunAsync(run.RunId, CancellationToken.None));
        Assert.False(await ledger.CompleteRunAsync(
            new WorkflowRunCompletion(
                run.RunId,
                WorkflowRunTerminalStatus.Completed,
                WorkflowRunNextAction.None,
                null,
                "Workflow를 완료했습니다.",
                Hash('8'),
                abandonedRun.Revision,
                abandonedRun.CurrentSequence,
                Baseline.AddSeconds(3)),
            CancellationToken.None));

        var secondAttempt = firstAttempt with
        {
            Attempt = 2,
            ToolCallId = "tool-call-effect-at-most-once-2",
            CreatedAtUtc = Baseline.AddSeconds(3),
        };
        Assert.True(await ledger.AddPlannedStepAsync(secondAttempt, CancellationToken.None));
        Assert.False(await ledger.TryReserveEffectStepAsync(
            new WorkflowEffectReservation(
                run.RunId,
                secondAttempt.ToolCallId,
                "ticket-abandoned-effect",
                abandonedTicketItem.ItemId,
                secondAttempt.Sequence,
                abandonedTicketItem.NonceHash,
                secondAttempt.ToolName,
                secondAttempt.Risk,
                secondAttempt.PayloadHash,
                secondAttempt.BindingHash,
                abandonedRun.Revision,
                abandonedRun.CurrentSequence,
                Baseline.AddSeconds(4)),
            CancellationToken.None));
        Assert.True(await ledger.SaveAuthorizationTicketAsync(
            new WorkflowAuthorizationTicketDraft(
                "ticket-effect-at-most-once",
                run.RunId,
                "R1",
                Baseline.AddSeconds(4),
                Baseline.AddMinutes(8),
                [TicketItem("ticket-item-effect-at-most-once", secondAttempt, Hash('9'))],
                ExpectedRunRevision: 2,
                ExpectedCurrentSequence: -1),
            CancellationToken.None));
        Assert.True(await ledger.TryReserveEffectStepAsync(
            new WorkflowEffectReservation(
                run.RunId,
                secondAttempt.ToolCallId,
                "ticket-effect-at-most-once",
                "ticket-item-effect-at-most-once",
                0,
                Hash('9'),
                secondAttempt.ToolName,
                secondAttempt.Risk,
                secondAttempt.PayloadHash,
                secondAttempt.BindingHash,
                3,
                -1,
                Baseline.AddSeconds(5)),
            CancellationToken.None));
        Assert.True(await ledger.CompleteStepAsync(
            new WorkflowStepCompletion(
                run.RunId,
                secondAttempt.ToolCallId,
                WorkflowStepCompletionDisposition.Succeeded,
                Hash('6'),
                null,
                null,
                4,
                Baseline.AddSeconds(6)),
            CancellationToken.None));

        var forbiddenThirdAttempt = secondAttempt with
        {
            Attempt = 3,
            ToolCallId = "tool-call-effect-at-most-once-3",
            CreatedAtUtc = Baseline.AddSeconds(7),
        };
        Assert.False(await ledger.AddPlannedStepAsync(forbiddenThirdAttempt, CancellationToken.None));
        Assert.Null(await ledger.GetStepAsync(forbiddenThirdAttempt.ToolCallId, CancellationToken.None));

        var runCompletion = new WorkflowRunCompletion(
            run.RunId,
            WorkflowRunTerminalStatus.Completed,
            WorkflowRunNextAction.None,
            null,
            "Workflow를 완료했습니다.",
            Hash('7'),
            5,
            0,
            Baseline.AddSeconds(8));
        Assert.False(await ledger.CompleteRunAsync(
            runCompletion with { ExpectedRunRevision = 4 },
            CancellationToken.None));
        Assert.True(await ledger.CompleteRunAsync(runCompletion, CancellationToken.None));
        var completedRun = Assert.IsType<WorkflowRunRecord>(
            await ledger.GetRunAsync(run.RunId, CancellationToken.None));
        Assert.Equal("succeeded", completedRun.Status);
        Assert.Equal(Hash('7'), completedRun.ResultHash);
        Assert.Equal("Workflow를 완료했습니다.", completedRun.RedactedSummary);
        Assert.NotNull(completedRun.CompletedAtUtc);
    }

    [Fact]
    public async Task UsesTheRepositoryClockToRejectAnExpiredTicket()
    {
        var clock = new MutableTimeProvider(Baseline.AddMinutes(10));
        using var store = new ConversationStore(DatabasePath("expired-ticket.db"), clock);
        await store.InitializeAsync();
        const string conversationId = "conversation-expired-ticket";
        const string parentRunId = "parent-run-expired-ticket";
        await store.StartRunAsync(conversationId, parentRunId, "workflow parent", Baseline);
        var ledger = store.WorkflowLedger;
        var run = RunStart(
            "workflow-run-expired-ticket",
            conversationId,
            parentRunId,
            "expired-ticket-key");
        var step = new WorkflowStepPlan(
            run.RunId,
            0,
            1,
            "step-expired-ticket",
            "tool-call-expired-ticket",
            "system.show_notification.v1",
            "R1",
            "never",
            WorkflowLedgerEffectClass.Effect,
            Hash('1'),
            Hash('2'),
            Baseline);
        var item = TicketItem("ticket-item-expired", step, Hash('3'));

        Assert.Equal(
            WorkflowRunStartOutcome.Created,
            (await ledger.StartOrCoalesceRunAsync(run, CancellationToken.None)).Outcome);
        Assert.True(await ledger.AddPlannedStepAsync(step, CancellationToken.None));
        Assert.True(await ledger.SaveAuthorizationTicketAsync(
            new WorkflowAuthorizationTicketDraft(
                "ticket-expired",
                run.RunId,
                "R1",
                Baseline.AddMinutes(1),
                Baseline.AddMinutes(7),
                [item]),
            CancellationToken.None));

        Assert.False(await ledger.TryReserveEffectStepAsync(
            new WorkflowEffectReservation(
                run.RunId,
                step.ToolCallId,
                "ticket-expired",
                item.ItemId,
                step.Sequence,
                item.NonceHash,
                step.ToolName,
                step.Risk,
                step.PayloadHash,
                step.BindingHash,
                1,
                -1,
                Baseline.AddMinutes(2)),
            CancellationToken.None));
        Assert.Equal("ready", (await ledger.GetTicketItemAsync(item.ItemId, CancellationToken.None))!.Status);
        Assert.Equal("planned", (await ledger.GetStepAsync(step.ToolCallId, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task StartupFinalizesAHalfTerminalFailedRunWithCanonicalNextAction()
    {
        var path = DatabasePath("half-terminal-recovery.db");
        var clock = new MutableTimeProvider(Baseline.AddSeconds(1));
        const string conversationId = "conversation-half-terminal";
        const string parentRunId = "parent-run-half-terminal";
        var run = RunStart(
            "workflow-run-half-terminal",
            conversationId,
            parentRunId,
            "half-terminal-key",
            "R0");
        var step = ObservationStep(run.RunId, 0, "half-terminal", '4', '5');

        using (var first = new ConversationStore(path, clock))
        {
            await first.InitializeAsync();
            await first.StartRunAsync(conversationId, parentRunId, "workflow parent", Baseline);
            Assert.Equal(
                WorkflowRunStartOutcome.Created,
                (await first.WorkflowLedger.StartOrCoalesceRunAsync(run, CancellationToken.None)).Outcome);
            Assert.True(await first.WorkflowLedger.AddPlannedStepAsync(step, CancellationToken.None));
            Assert.True(await first.WorkflowLedger.TryStartObservationStepAsync(
                ObservationReservation(step, 0, -1, Baseline.AddSeconds(1)),
                CancellationToken.None));
            Assert.True(await first.WorkflowLedger.CompleteStepAsync(
                new WorkflowStepCompletion(
                    run.RunId,
                    step.ToolCallId,
                    WorkflowStepCompletionDisposition.FailedKnown,
                    null,
                    null,
                    "workflow.observation.failed",
                    1,
                    Baseline.AddSeconds(2)),
                CancellationToken.None));
            var halfTerminal = (await first.WorkflowLedger.GetRunAsync(run.RunId, CancellationToken.None))!;
            Assert.Equal("failed", halfTerminal.Status);
            Assert.Equal("retry_from_start", halfTerminal.FixedNextAction);
            Assert.Null(halfTerminal.CompletedAtUtc);
        }

        clock.SetUtcNow(Baseline.AddMinutes(1));
        using var reopened = new ConversationStore(path, clock);
        var recovery = await reopened.InitializeForDesktopStartupAsync(CancellationToken.None);
        var recovered = (await reopened.WorkflowLedger.GetRunAsync(run.RunId, CancellationToken.None))!;
        Assert.Equal(1, recovery.RecoveredRuns);
        Assert.Equal("failed", recovered.Status);
        Assert.Equal("retry_from_start", recovered.FixedNextAction);
        Assert.NotNull(recovered.CompletedAtUtc);
    }

    [Fact]
    public async Task CancelledRunRequiresRetryFromStartNextAction()
    {
        using var store = await CreateStoreWithParentAsync(
            "cancelled-run.db",
            "conversation-cancelled-run",
            "parent-run-cancelled-run");
        var run = RunStart(
            "workflow-run-cancelled",
            "conversation-cancelled-run",
            "parent-run-cancelled-run",
            "cancelled-key",
            "R0");
        Assert.Equal(
            WorkflowRunStartOutcome.Created,
            (await store.WorkflowLedger.StartOrCoalesceRunAsync(run, CancellationToken.None)).Outcome);

        await Assert.ThrowsAsync<ArgumentException>(() => store.WorkflowLedger.CompleteRunAsync(
            new WorkflowRunCompletion(
                run.RunId,
                WorkflowRunTerminalStatus.Cancelled,
                WorkflowRunNextAction.None,
                null,
                "Workflow가 취소되었습니다.",
                null,
                0,
                -1,
                Baseline.AddSeconds(1)),
            CancellationToken.None));
        Assert.True(await store.WorkflowLedger.CompleteRunAsync(
            new WorkflowRunCompletion(
                run.RunId,
                WorkflowRunTerminalStatus.Cancelled,
                WorkflowRunNextAction.RetryFromStart,
                null,
                "Workflow가 취소되었습니다.",
                null,
                0,
                -1,
                Baseline.AddSeconds(1)),
            CancellationToken.None));
        Assert.Equal(
            "retry_from_start",
            (await store.WorkflowLedger.GetRunAsync(run.RunId, CancellationToken.None))!.FixedNextAction);
    }

    [Fact]
    public async Task NeedsInformationPersistsOnlyBoundedRequiredPointersForExactReplay()
    {
        using var store = await CreateStoreWithParentAsync(
            "needs-information.db",
            "conversation-needs-information",
            "parent-run-needs-information");
        var run = RunStart(
            "workflow-run-needs-information",
            "conversation-needs-information",
            "parent-run-needs-information",
            "needs-information-key",
            "R0");

        Assert.Equal(
            WorkflowRunStartOutcome.Created,
            (await store.WorkflowLedger.StartOrCoalesceRunAsync(run, CancellationToken.None)).Outcome);
        Assert.True(await store.WorkflowLedger.CompleteRunAsync(
            new WorkflowRunCompletion(
                run.RunId,
                WorkflowRunTerminalStatus.NeedsInformation,
                WorkflowRunNextAction.ProvideInformation,
                null,
                "Workflow needs additional information.",
                null,
                0,
                -1,
                Baseline.AddSeconds(1),
                ["/recipient", "/subject"]),
            CancellationToken.None));

        var persisted = Assert.IsType<WorkflowRunRecord>(
            await store.WorkflowLedger.GetRunAsync(run.RunId, CancellationToken.None));
        Assert.Equal("needs_information", persisted.Status);
        Assert.Equal(["/recipient", "/subject"], persisted.RequiredInputs);
        var replay = await store.WorkflowLedger.StartOrCoalesceRunAsync(
            run with { CreatedAtUtc = Baseline.AddMinutes(1) },
            CancellationToken.None);
        Assert.Equal(WorkflowRunStartOutcome.Coalesced, replay.Outcome);
        Assert.Equal(persisted.RequiredInputs, replay.Run.RequiredInputs);
    }

    [Fact]
    public async Task DesktopStartupRecoveryRunsOnlyOncePerStoreInstance()
    {
        var clock = new MutableTimeProvider(Baseline);
        using var store = new ConversationStore(DatabasePath("one-shot-startup.db"), clock);
        Assert.Equal(
            new WorkflowStartupRecoveryResult(0, 0, 0, 0, 0),
            await store.InitializeForDesktopStartupAsync(CancellationToken.None));
        const string conversationId = "conversation-one-shot-startup";
        const string parentRunId = "parent-run-one-shot-startup";
        await store.StartRunAsync(conversationId, parentRunId, "workflow parent", Baseline);
        var run = RunStart(
            "workflow-run-one-shot-startup",
            conversationId,
            parentRunId,
            "one-shot-startup-key",
            "R0");
        Assert.Equal(
            WorkflowRunStartOutcome.Created,
            (await store.WorkflowLedger.StartOrCoalesceRunAsync(run, CancellationToken.None)).Outcome);

        clock.SetUtcNow(Baseline.AddMinutes(1));
        Assert.Equal(
            new WorkflowStartupRecoveryResult(0, 0, 0, 0, 0),
            await store.InitializeForDesktopStartupAsync(CancellationToken.None));
        Assert.Equal(
            "planned",
            (await store.WorkflowLedger.GetRunAsync(run.RunId, CancellationToken.None))!.Status);
    }

    private async Task<ConversationStore> CreateStoreWithParentAsync(
        string fileName,
        string conversationId,
        string parentRunId)
    {
        var store = new ConversationStore(
            DatabasePath(fileName),
            new MutableTimeProvider(Baseline.AddSeconds(5)));
        await store.InitializeAsync();
        await store.StartRunAsync(conversationId, parentRunId, "workflow parent", Baseline);
        return store;
    }

    private static WorkflowStepPlan ObservationStep(
        string workflowRunId,
        int sequence,
        string suffix,
        char payloadHash,
        char bindingHash) =>
        new(
            workflowRunId,
            sequence,
            1,
            $"step-observation-{suffix}",
            $"tool-call-observation-{suffix}",
            "system.get_status.v1",
            "R0",
            "read_only",
            WorkflowLedgerEffectClass.Observation,
            Hash(payloadHash),
            Hash(bindingHash),
            Baseline.AddSeconds(sequence));

    private static WorkflowRunStart RunStart(
        string runId,
        string conversationId,
        string parentRunId,
        string idempotencyKey,
        string actualMaximumRisk = "R1") =>
        new(
            runId,
            conversationId,
            parentRunId,
            $"parent-tool-{runId}",
            idempotencyKey,
            Hash('0'),
            "company.workflow_example.v1",
            "company.workflow-example",
            "1.0.0",
            7,
            Hash('a'),
            Hash('b'),
            Hash('c'),
            Hash('d'),
            actualMaximumRisk,
            Baseline);

    private static WorkflowAuthorizationTicketItemDraft TicketItem(
        string itemId,
        WorkflowStepPlan step,
        string nonceHash) =>
        new(
            itemId,
            step.Sequence,
            nonceHash,
            step.ToolName,
            step.Risk,
            step.PayloadHash,
            step.BindingHash);

    private static WorkflowObservationReservation ObservationReservation(
        WorkflowStepPlan step,
        long expectedRunRevision,
        int expectedCurrentSequence,
        DateTimeOffset startedAtUtc) =>
        new(
            step.WorkflowRunId,
            step.ToolCallId,
            step.Sequence,
            step.ToolName,
            step.Risk,
            step.RetryClass,
            step.PayloadHash,
            step.BindingHash,
            expectedRunRevision,
            expectedCurrentSequence,
            startedAtUtc);

    private static string Hash(char digit) => $"sha256:{new string(digit, 64)}";

    private string DatabasePath(string fileName) => Path.Combine(_directory, fileName);

    private static SqliteConnection OpenReadOnly(string path) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());

    private static SqliteConnection OpenWritable(string path) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());

    private static async Task<long> ScalarInt64Async(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(SqliteConnection connection, string sql)
    {
        var values = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }

    private static async Task<IReadOnlyList<string>> ReadTableColumnsAsync(
        SqliteConnection connection,
        string tableName)
    {
        var values = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($tableName) ORDER BY cid;";
        command.Parameters.AddWithValue("$tableName", tableName);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }

    private static async Task<string> ReadApprovalScopeAsync(string path, string ticketId)
    {
        await using var connection = OpenReadOnly(path);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT approval_scope FROM workflow_authorization_tickets WHERE id = $id;";
        command.Parameters.AddWithValue("$id", ticketId);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;

        public override DateTimeOffset GetUtcNow() => current;

        public void SetUtcNow(DateTimeOffset value) => current = value;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
