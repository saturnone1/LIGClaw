using System.Security.Cryptography;
using System.Text;
using LIGClaw.Application.Mail;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Tools;
using Microsoft.Data.Sqlite;

namespace LIGClaw.Desktop.Tests;

public sealed class WorkflowActivityTests : IDisposable
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.Parse("2026-08-02T00:00:00Z");
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"ligclaw-workflow-activity-{Guid.NewGuid():N}");

    public WorkflowActivityTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task RecentActivityShowsFailedWorkflowStepAndFixedNextAction()
    {
        using var store = new ConversationStore(Path.Combine(directory, "activity.db"));
        await store.InitializeAsync();
        await store.StartRunAsync("conversation-activity", "parent-run-activity", "기록하지 않을 원문", Baseline);

        var run = new WorkflowRunStart(
            "workflow-run-activity",
            "conversation-activity",
            "parent-run-activity",
            "parent-tool-activity",
            "workflow-idempotency-activity",
            Hash('0'),
            "company.workflow_example.v1",
            "company.workflow-example",
            "1.0.0",
            7,
            Hash('a'),
            Hash('b'),
            Hash('c'),
            Hash('d'),
            "R0",
            Baseline);
        var step = new WorkflowStepPlan(
            run.RunId,
            0,
            1,
            "check-system-status",
            "workflow-tool-call-activity",
            "system.get_status.v1",
            "R0",
            "read_only",
            WorkflowLedgerEffectClass.Observation,
            Hash('1'),
            Hash('2'),
            Baseline.AddSeconds(1));

        Assert.Equal(
            WorkflowRunStartOutcome.Created,
            (await store.WorkflowLedger.StartOrCoalesceRunAsync(run, CancellationToken.None)).Outcome);
        Assert.True(await store.WorkflowLedger.AddPlannedStepAsync(step, CancellationToken.None));
        Assert.True(await store.WorkflowLedger.TryStartObservationStepAsync(
            new WorkflowObservationReservation(
                run.RunId,
                step.ToolCallId,
                step.Sequence,
                step.ToolName,
                step.Risk,
                step.RetryClass,
                step.PayloadHash,
                step.BindingHash,
                0,
                -1,
                Baseline.AddSeconds(2)),
            CancellationToken.None));
        Assert.True(await store.WorkflowLedger.CompleteStepAsync(
            new WorkflowStepCompletion(
                run.RunId,
                step.ToolCallId,
                WorkflowStepCompletionDisposition.FailedKnown,
                null,
                "상태 확인을 완료하지 못했습니다.",
                "workflow.status_check.failed",
                1,
                Baseline.AddSeconds(3)),
            CancellationToken.None));
        Assert.True(await store.WorkflowLedger.CompleteRunAsync(
            new WorkflowRunCompletion(
                run.RunId,
                WorkflowRunTerminalStatus.Failed,
                WorkflowRunNextAction.RetryFromStart,
                "workflow.status_check.failed",
                "Workflow를 완료하지 못했습니다.",
                null,
                2,
                0,
                Baseline.AddSeconds(4)),
            CancellationToken.None));

        var activity = Assert.Single(await store.GetRecentToolActivityAsync());
        Assert.Equal("Workflow · company.workflow-example@1.0.0", activity.ToolName);
        Assert.Equal("failed", activity.Status);
        Assert.Equal("실패", activity.StatusDisplay);
        Assert.Contains("실패 단계: check-system-status (system.get_status.v1)", activity.Summary, StringComparison.Ordinal);
        Assert.Contains("다음 행동: 처음부터 다시 실행", activity.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("기록하지 않을 원문", activity.Summary, StringComparison.Ordinal);
        Assert.Null(activity.Approved);
    }

    [Fact]
    public async Task RecentActivityShowsStableSmtpMessageIdOnlyForStrictOutcomeUnknownSummaries()
    {
        const string smtpActivityPrefix = "메일 서버의 접수 여부를 확인해야 해요. 메시지 식별자: ";
        const string rawMailCanary = "RAW-MAIL-CONTENT-CANARY";
        const string credentialCanary = "SMTP-CREDENTIAL-CANARY";
        using var store = new ConversationStore(
            Path.Combine(directory, "smtp-activity.db"),
            new FixedTimeProvider(Baseline.AddMinutes(14)));
        await store.InitializeAsync();

        var firstMessageId = Assert.IsType<string>(await SeedMailWorkflowAsync(
            store,
            "smtp-first",
            "company.workflow-smtp",
            Baseline.AddMinutes(10),
            WorkflowStepCompletionDisposition.Unknown,
            MailOutboxTransportKind.Smtp,
            smtpMessageId => smtpActivityPrefix + Assert.IsType<string>(smtpMessageId)));
        Assert.Null(await SeedMailWorkflowAsync(
            store,
            "smtp-repeat",
            "company.workflow-smtp",
            Baseline.AddMinutes(11),
            WorkflowStepCompletionDisposition.Unknown,
            transportKind: null,
            _ => smtpActivityPrefix + firstMessageId));
        Assert.Null(await SeedMailWorkflowAsync(
            store,
            "mail-api-unknown",
            "company.workflow-mail-api",
            Baseline.AddMinutes(12),
            WorkflowStepCompletionDisposition.Unknown,
            MailOutboxTransportKind.MailApi,
            _ => $"{rawMailCanary} · {credentialCanary}"));
        var knownFailureMessageId = Assert.IsType<string>(await SeedMailWorkflowAsync(
            store,
            "smtp-known-failure",
            "company.workflow-smtp-known",
            Baseline.AddMinutes(13),
            WorkflowStepCompletionDisposition.FailedKnown,
            MailOutboxTransportKind.Smtp,
            smtpMessageId => smtpActivityPrefix + Assert.IsType<string>(smtpMessageId)));

        var activities = await store.GetRecentToolActivityAsync(cancellationToken: CancellationToken.None);
        var repeatedSmtpActivities = activities
            .Where(activity => activity.ToolName == "Workflow · company.workflow-smtp@1.0.0")
            .ToArray();
        Assert.Equal(2, repeatedSmtpActivities.Length);
        Assert.All(repeatedSmtpActivities, activity =>
        {
            Assert.Equal("attention_required", activity.Status);
            Assert.Equal("검토 필요", activity.StatusDisplay);
            Assert.Contains(smtpActivityPrefix + firstMessageId, activity.Summary, StringComparison.Ordinal);
            Assert.Contains("다음 행동: 실행 결과 검토", activity.Summary, StringComparison.Ordinal);
        });

        var mailApiActivity = Assert.Single(
            activities,
            activity => activity.ToolName == "Workflow · company.workflow-mail-api@1.0.0");
        Assert.Equal("attention_required", mailApiActivity.Status);
        Assert.DoesNotContain("Message-ID:", mailApiActivity.Summary, StringComparison.Ordinal);

        var knownFailureActivity = Assert.Single(
            activities,
            activity => activity.ToolName == "Workflow · company.workflow-smtp-known@1.0.0");
        Assert.Equal("failed", knownFailureActivity.Status);
        Assert.DoesNotContain(knownFailureMessageId, knownFailureActivity.Summary, StringComparison.Ordinal);

        Assert.All(activities, activity =>
        {
            Assert.DoesNotContain(rawMailCanary, activity.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(credentialCanary, activity.Summary, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task StartupRecoveryActivityShowsOnlyBoundCanonicalDataStartedSmtpMessageId()
    {
        const string smtpActivityPrefix = "메일 서버의 접수 여부를 확인해야 해요. 메시지 식별자: ";
        const string rawMailCanary = "RESTART-RAW-MAIL-CONTENT-CANARY";
        const string credentialCanary = "RESTART-SMTP-CREDENTIAL-CANARY";
        var path = Path.Combine(directory, "smtp-restart-activity.db");
        var seedTime = Baseline.AddHours(1);
        ExecutingMailOutbox canonicalSmtp;
        ExecutingMailOutbox mailApi;
        ExecutingMailOutbox preDataSmtp;
        ExecutingMailOutbox noncanonicalSmtp;
        using (var store = new ConversationStore(
                   path,
                   new FixedTimeProvider(seedTime.AddMinutes(1))))
        {
            await store.InitializeAsync();
            var sensitiveInput = $"{rawMailCanary} · {credentialCanary}";
            canonicalSmtp = await SeedExecutingMailOutboxAsync(
                store,
                "restart-smtp-data",
                "company.workflow-restart-smtp-data",
                seedTime,
                sensitiveInput,
                MailOutboxTransportKind.Smtp,
                smtpDataStarted: true);
            mailApi = await SeedExecutingMailOutboxAsync(
                store,
                "restart-mail-api",
                "company.workflow-restart-mail-api",
                seedTime.AddSeconds(10),
                sensitiveInput,
                MailOutboxTransportKind.MailApi,
                smtpDataStarted: false);
            preDataSmtp = await SeedExecutingMailOutboxAsync(
                store,
                "restart-smtp-pre-data",
                "company.workflow-restart-smtp-pre-data",
                seedTime.AddSeconds(20),
                sensitiveInput,
                MailOutboxTransportKind.Smtp,
                smtpDataStarted: false);
            noncanonicalSmtp = await SeedExecutingMailOutboxAsync(
                store,
                "restart-smtp-noncanonical",
                "company.restart-smtp-badid",
                seedTime.AddSeconds(30),
                sensitiveInput,
                MailOutboxTransportKind.Smtp,
                smtpDataStarted: true);
        }
        await SetStepRedactedSummaryAsync(
            path,
            canonicalSmtp.Workflow.Step.ToolCallId,
            $"{rawMailCanary} · {credentialCanary}");

        using var restarted = new ConversationStore(
            path,
            new FixedTimeProvider(seedTime.AddMinutes(2)));
        var recovery = await restarted.InitializeForDesktopStartupAsync(CancellationToken.None);
        Assert.Equal(4, recovery.UnknownEffects);
        Assert.Equal(4, recovery.RecoveredRuns);

        var recoveredCanonical = Assert.IsType<MailOutboxOperation>(
            await restarted.MailOutbox.GetAsync(
                canonicalSmtp.Outbox.OperationId,
                CancellationToken.None));
        var recoveredMailApi = Assert.IsType<MailOutboxOperation>(
            await restarted.MailOutbox.GetAsync(mailApi.Outbox.OperationId, CancellationToken.None));
        var recoveredPreData = Assert.IsType<MailOutboxOperation>(
            await restarted.MailOutbox.GetAsync(preDataSmtp.Outbox.OperationId, CancellationToken.None));
        var recoveredNoncanonical = Assert.IsType<MailOutboxOperation>(
            await restarted.MailOutbox.GetAsync(
                noncanonicalSmtp.Outbox.OperationId,
                CancellationToken.None));
        Assert.Equal(MailOutboxSubmissionState.OutcomeUnknown, recoveredCanonical.SubmissionState);
        Assert.Equal(MailOutboxSmtpPhase.DataStarted, recoveredCanonical.SmtpPhase);
        Assert.Equal(MailOutboxSubmissionState.OutcomeUnknown, recoveredMailApi.SubmissionState);
        Assert.Equal(MailOutboxSubmissionState.FailedKnown, recoveredPreData.SubmissionState);
        Assert.Equal(MailOutboxSmtpPhase.PreData, recoveredPreData.SmtpPhase);
        Assert.Equal(MailOutboxSubmissionState.OutcomeUnknown, recoveredNoncanonical.SubmissionState);

        var recoveredStep = Assert.IsType<WorkflowStepRunRecord>(
            await restarted.WorkflowLedger.GetStepAsync(
                canonicalSmtp.Workflow.Step.ToolCallId,
                CancellationToken.None));
        Assert.Equal("outcome_unknown", recoveredStep.Status);
        Assert.Equal("unknown", recoveredStep.Disposition);
        Assert.Equal("workflow.desktop_restart.effect_outcome_unknown", recoveredStep.FixedErrorCode);
        Assert.Contains(rawMailCanary, recoveredStep.RedactedSummary!, StringComparison.Ordinal);

        const string invalidMessageId =
            "<ligclaw.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA@ligclaw.invalid>";
        await CorruptSmtpMessageIdAsync(
            path,
            recoveredNoncanonical.OperationId,
            invalidMessageId);

        var activities = await restarted.GetRecentToolActivityAsync(
            cancellationToken: CancellationToken.None);
        var canonicalActivity = ActivityFor(activities, "company.workflow-restart-smtp-data");
        Assert.Equal("attention_required", canonicalActivity.Status);
        Assert.Contains(
            smtpActivityPrefix + recoveredCanonical.SmtpMessageId,
            canonicalActivity.Summary,
            StringComparison.Ordinal);
        Assert.Contains("다음 행동: 실행 결과 검토", canonicalActivity.Summary, StringComparison.Ordinal);

        var mailApiActivity = ActivityFor(activities, "company.workflow-restart-mail-api");
        Assert.DoesNotContain("Message-ID:", mailApiActivity.Summary, StringComparison.Ordinal);
        var preDataActivity = ActivityFor(activities, "company.workflow-restart-smtp-pre-data");
        Assert.DoesNotContain("Message-ID:", preDataActivity.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(
            recoveredPreData.SmtpMessageId!,
            preDataActivity.Summary,
            StringComparison.Ordinal);
        var noncanonicalActivity = ActivityFor(
            activities,
            "company.restart-smtp-badid");
        Assert.DoesNotContain("Message-ID:", noncanonicalActivity.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(invalidMessageId, noncanonicalActivity.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(
            recoveredNoncanonical.SmtpMessageId!,
            noncanonicalActivity.Summary,
            StringComparison.Ordinal);

        Assert.All(activities, activity =>
        {
            Assert.DoesNotContain(rawMailCanary, activity.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(credentialCanary, activity.Summary, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("needs_information", "정보 필요")]
    [InlineData("running", "실행 중")]
    [InlineData("attention_required", "검토 필요")]
    [InlineData("interrupted", "재시작 필요")]
    public void WorkflowActivityStatusesHaveUserFacingLabels(string status, string expected)
    {
        var activity = new ToolActivitySummary(
            -1,
            "Workflow · company.workflow-example@1.0.0",
            "R1",
            status,
            "작업 흐름 실행",
            null,
            Baseline);

        Assert.Equal(expected, activity.StatusDisplay);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static async Task<string?> SeedMailWorkflowAsync(
        ConversationStore store,
        string scope,
        string capabilityId,
        DateTimeOffset startedAtUtc,
        WorkflowStepCompletionDisposition disposition,
        MailOutboxTransportKind? transportKind,
        Func<string?, string> stepSummaryFactory)
    {
        var executing = await SeedExecutingMailWorkflowAsync(
            store,
            scope,
            capabilityId,
            startedAtUtc,
            $"원문 메일은 활동에 남기지 않음: {scope}");
        var run = executing.Run;
        var step = executing.Step;

        MailOutboxOperation? operation = null;
        if (transportKind is { } selectedTransport)
        {
            operation = (await store.MailOutbox.CreateOrGetAsync(
                new MailOutboxCreateRequest(
                    run.ConversationId,
                    run.RunId,
                    step.ToolCallId,
                    run.CapabilityId,
                    run.CapabilityVersion,
                    MailOutboxHash.Parse(run.CapabilityContentHash),
                    run.CatalogRevision,
                    MailOutboxHash.Parse(run.SnapshotHash),
                    "primary-mail",
                    MailOutboxHash.Parse(step.BindingHash),
                    MailOutboxHash.Parse(Hash('4')),
                    startedAtUtc.AddSeconds(5),
                    selectedTransport),
                CancellationToken.None)).Operation;
            operation = await MoveOutboxToTerminalAsync(
                store,
                operation,
                disposition,
                startedAtUtc.AddSeconds(6));
        }

        var fixedErrorCode = disposition == WorkflowStepCompletionDisposition.Unknown
            ? "mail.send.outcome_unknown"
            : "mail.send.submit_failed";
        var currentRun = Assert.IsType<WorkflowRunRecord>(
            await store.WorkflowLedger.GetRunAsync(run.RunId, CancellationToken.None));
        Assert.True(await store.WorkflowLedger.CompleteStepAsync(
            new WorkflowStepCompletion(
                run.RunId,
                step.ToolCallId,
                disposition,
                null,
                stepSummaryFactory(operation?.SmtpMessageId),
                fixedErrorCode,
                currentRun.Revision,
                startedAtUtc.AddSeconds(9)),
            CancellationToken.None));
        currentRun = Assert.IsType<WorkflowRunRecord>(
            await store.WorkflowLedger.GetRunAsync(run.RunId, CancellationToken.None));
        var isUnknown = disposition == WorkflowStepCompletionDisposition.Unknown;
        Assert.True(await store.WorkflowLedger.CompleteRunAsync(
            new WorkflowRunCompletion(
                run.RunId,
                isUnknown ? WorkflowRunTerminalStatus.AttentionRequired : WorkflowRunTerminalStatus.Failed,
                isUnknown ? WorkflowRunNextAction.ReviewOutcome : WorkflowRunNextAction.RetryFromStart,
                fixedErrorCode,
                isUnknown ? "작업 흐름의 메일 전송 결과를 검토해야 합니다." : "작업 흐름의 메일 전송에 실패했습니다.",
                null,
                currentRun.Revision,
                currentRun.CurrentSequence,
                startedAtUtc.AddSeconds(10)),
            CancellationToken.None));
        return operation?.SmtpMessageId;
    }

    private static async Task<ExecutingMailWorkflow> SeedExecutingMailWorkflowAsync(
        ConversationStore store,
        string scope,
        string capabilityId,
        DateTimeOffset startedAtUtc,
        string userInput)
    {
        var conversationId = $"conversation-{scope}";
        var parentRunId = $"parent-run-{scope}";
        var workflowRunId = $"workflow-run-{scope}";
        var toolCallId = $"tool-call-{scope}";
        await store.StartRunAsync(conversationId, parentRunId, userInput, startedAtUtc);

        var run = new WorkflowRunStart(
            workflowRunId,
            conversationId,
            parentRunId,
            $"parent-tool-{scope}",
            $"workflow-idempotency-{scope}",
            Hash('0'),
            $"company.workflow_{scope.Replace('-', '_')}.v1",
            capabilityId,
            "1.0.0",
            7,
            Hash('a'),
            Hash('b'),
            Hash('c'),
            Hash('d'),
            "R3",
            startedAtUtc.AddSeconds(1));
        var step = new WorkflowStepPlan(
            run.RunId,
            0,
            1,
            "send-mail",
            toolCallId,
            "mail.send.v1",
            "R3",
            "never",
            WorkflowLedgerEffectClass.Effect,
            Hash('1'),
            Hash('2'),
            startedAtUtc.AddSeconds(2));

        Assert.Equal(
            WorkflowRunStartOutcome.Created,
            (await store.WorkflowLedger.StartOrCoalesceRunAsync(run, CancellationToken.None)).Outcome);
        Assert.True(await store.WorkflowLedger.AddPlannedStepAsync(step, CancellationToken.None));
        var currentRun = Assert.IsType<WorkflowRunRecord>(
            await store.WorkflowLedger.GetRunAsync(run.RunId, CancellationToken.None));
        var nonceHash = HashText(scope);
        var ticketId = $"ticket-{scope}";
        var ticketItemId = $"ticket-item-{scope}";
        Assert.True(await store.WorkflowLedger.SaveAuthorizationTicketAsync(
            new WorkflowAuthorizationTicketDraft(
                ticketId,
                run.RunId,
                "R3",
                startedAtUtc.AddSeconds(3),
                startedAtUtc.AddMinutes(7),
                [
                    new WorkflowAuthorizationTicketItemDraft(
                        ticketItemId,
                        step.Sequence,
                        nonceHash,
                        step.ToolName,
                        step.Risk,
                        step.PayloadHash,
                        step.BindingHash),
                ],
                currentRun.Revision,
                currentRun.CurrentSequence),
            CancellationToken.None));
        currentRun = Assert.IsType<WorkflowRunRecord>(
            await store.WorkflowLedger.GetRunAsync(run.RunId, CancellationToken.None));
        Assert.True(await store.WorkflowLedger.TryReserveEffectStepAsync(
            new WorkflowEffectReservation(
                run.RunId,
                step.ToolCallId,
                ticketId,
                ticketItemId,
                step.Sequence,
                nonceHash,
                step.ToolName,
                step.Risk,
                step.PayloadHash,
                step.BindingHash,
                currentRun.Revision,
                currentRun.CurrentSequence,
                startedAtUtc.AddSeconds(4)),
            CancellationToken.None));
        return new ExecutingMailWorkflow(run, step);
    }

    private static async Task<ExecutingMailOutbox> SeedExecutingMailOutboxAsync(
        ConversationStore store,
        string scope,
        string capabilityId,
        DateTimeOffset startedAtUtc,
        string userInput,
        MailOutboxTransportKind transportKind,
        bool smtpDataStarted)
    {
        if (smtpDataStarted && transportKind != MailOutboxTransportKind.Smtp)
            throw new ArgumentException("DATA 시작 상태는 SMTP에만 사용할 수 있습니다.", nameof(smtpDataStarted));
        var workflow = await SeedExecutingMailWorkflowAsync(
            store,
            scope,
            capabilityId,
            startedAtUtc,
            userInput);
        var run = workflow.Run;
        var step = workflow.Step;
        var staged = (await store.MailOutbox.CreateOrGetAsync(
            new MailOutboxCreateRequest(
                run.ConversationId,
                run.RunId,
                step.ToolCallId,
                run.CapabilityId,
                run.CapabilityVersion,
                MailOutboxHash.Parse(run.CapabilityContentHash),
                run.CatalogRevision,
                MailOutboxHash.Parse(run.SnapshotHash),
                "primary-mail",
                MailOutboxHash.Parse(step.BindingHash),
                MailOutboxHash.Parse(Hash('4')),
                startedAtUtc.AddSeconds(5),
                transportKind),
            CancellationToken.None)).Operation;
        var current = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(
                staged,
                MailOutboxSubmissionState.Submitting,
                MailOutboxDeliveryState.Unknown,
                1,
                null,
                startedAtUtc.AddSeconds(6)),
            CancellationToken.None)).Operation);
        if (smtpDataStarted)
        {
            current = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
                Transition(
                    current,
                    MailOutboxSubmissionState.Submitting,
                    MailOutboxDeliveryState.Unknown,
                    1,
                    null,
                    startedAtUtc.AddSeconds(7),
                    MailOutboxSmtpPhase.DataStarted),
                CancellationToken.None)).Operation);
        }
        return new ExecutingMailOutbox(workflow, current);
    }

    private static async Task<MailOutboxOperation> MoveOutboxToTerminalAsync(
        ConversationStore store,
        MailOutboxOperation staged,
        WorkflowStepCompletionDisposition disposition,
        DateTimeOffset updatedAtUtc)
    {
        var current = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(
                staged,
                MailOutboxSubmissionState.Submitting,
                MailOutboxDeliveryState.Unknown,
                1,
                null,
                updatedAtUtc),
            CancellationToken.None)).Operation);
        if (current.TransportKind == MailOutboxTransportKind.Smtp &&
            disposition == WorkflowStepCompletionDisposition.Unknown)
        {
            current = Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
                Transition(
                    current,
                    MailOutboxSubmissionState.Submitting,
                    MailOutboxDeliveryState.Unknown,
                    1,
                    null,
                    updatedAtUtc.AddSeconds(1),
                    MailOutboxSmtpPhase.DataStarted),
                CancellationToken.None)).Operation);
        }

        var isUnknown = disposition == WorkflowStepCompletionDisposition.Unknown;
        return Assert.IsType<MailOutboxOperation>((await store.MailOutbox.TryTransitionAsync(
            Transition(
                current,
                isUnknown ? MailOutboxSubmissionState.OutcomeUnknown : MailOutboxSubmissionState.FailedKnown,
                isUnknown ? MailOutboxDeliveryState.Unknown : MailOutboxDeliveryState.Failed,
                1,
                isUnknown ? "mail.outbox.outcome_unknown" : "mail.outbox.submit_failed",
                updatedAtUtc.AddSeconds(2)),
            CancellationToken.None)).Operation);
    }

    private static MailOutboxTransitionRequest Transition(
        MailOutboxOperation current,
        MailOutboxSubmissionState submissionState,
        MailOutboxDeliveryState deliveryState,
        int attemptCount,
        string? fixedErrorCode,
        DateTimeOffset updatedAtUtc,
        MailOutboxSmtpPhase? smtpPhase = null) =>
        new(
            current.OperationId,
            current.Revision,
            current.SubmissionState,
            current.DeliveryState,
            submissionState,
            deliveryState,
            attemptCount,
            fixedErrorCode,
            updatedAtUtc,
            smtpPhase);

    private static ToolActivitySummary ActivityFor(
        IReadOnlyList<ToolActivitySummary> activities,
        string capabilityId) =>
        Assert.Single(
            activities,
            activity => activity.ToolName == $"Workflow · {capabilityId}@1.0.0");

    private static async Task SetStepRedactedSummaryAsync(
        string databasePath,
        string toolCallId,
        string redactedSummary)
    {
        await using var connection = OpenDatabase(databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE workflow_step_runs SET redacted_summary = $summary WHERE tool_call_id = $toolCallId;";
        command.Parameters.AddWithValue("$summary", redactedSummary);
        command.Parameters.AddWithValue("$toolCallId", toolCallId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task CorruptSmtpMessageIdAsync(
        string databasePath,
        MailOutboxOperationId operationId,
        string smtpMessageId)
    {
        await using var connection = OpenDatabase(databasePath);
        await connection.OpenAsync();
        foreach (var statement in new[]
                 {
                     "PRAGMA ignore_check_constraints = ON;",
                     "DROP TRIGGER mail_outbox_reject_identity_update;",
                     "DROP TRIGGER mail_outbox_reject_terminal_update;",
                     "DROP TRIGGER mail_outbox_require_legal_transition;",
                 })
        {
            await using var schemaCommand = connection.CreateCommand();
            schemaCommand.CommandText = statement;
            _ = await schemaCommand.ExecuteNonQueryAsync();
        }

        await using var update = connection.CreateCommand();
        update.CommandText =
            "UPDATE mail_outbox_operations SET smtp_message_id = $messageId " +
            "WHERE operation_id = $operationId;";
        update.Parameters.AddWithValue("$messageId", smtpMessageId);
        update.Parameters.AddWithValue("$operationId", operationId.Value);
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
    }

    private static SqliteConnection OpenDatabase(string databasePath) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());

    private sealed record ExecutingMailWorkflow(WorkflowRunStart Run, WorkflowStepPlan Step);

    private sealed record ExecutingMailOutbox(
        ExecutingMailWorkflow Workflow,
        MailOutboxOperation Outbox);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static string Hash(char digit) => $"sha256:{new string(digit, 64)}";

    private static string HashText(string value) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()}";
}
