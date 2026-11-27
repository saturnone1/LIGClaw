using System.Globalization;
using System.Text;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Application.Mail;
using LIGClaw.Application.Tools;
using LIGClaw.Application.Workflows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Desktop.Infrastructure.Workflows;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class SmtpMailSendVerticalTests : IDisposable
{
    private const string RawCanary = "RAW-SMTP-MAIL-CANARY";
    private const string WorkflowRecipientCanary = "workflow-smtp-recipient-canary@example.invalid";
    private const string WorkflowCcCanary = "workflow-smtp-cc-canary@example.invalid";
    private const string WorkflowBccCanary = "workflow-smtp-bcc-canary@example.invalid";
    private const string WorkflowSubjectCanary = "WORKFLOW-SMTP-SUBJECT-CANARY-7B31";
    private const string WorkflowBodyCanary = "WORKFLOW-SMTP-BODY-CANARY-9D42";
    private const string CredentialUsernameCanary = "workflow-smtp-user-canary";
    private const string CredentialPasswordCanary = "WORKFLOW-SMTP-PASSWORD-CANARY-5F63";
    private const string RemoteReplyCanary = "WORKFLOW-SMTP-REMOTE-REPLY-CANARY-8A54";
    private static readonly DateTimeOffset PreparedAt =
        DateTimeOffset.Parse("2026-08-02T00:00:00Z", CultureInfo.InvariantCulture);
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "LIGClaw.Tests",
        $"smtp-mail-workflow-{Guid.NewGuid():N}");

    public SmtpMailSendVerticalTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task DirectInvocationIsDeniedWithoutResolverJournalOrSocket()
    {
        var resolver = new FakeResolver();
        var outbox = new MemoryOutbox();
        var factory = new ScriptedFactory();
        var tool = Tool(resolver, outbox, factory);

        var result = await tool.ExecuteAsync(Input(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("mail.send.context_required", result.ErrorCode);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, outbox.TotalCalls);
        Assert.Equal(0, factory.ConnectCount);
    }

    [Fact]
    public async Task PreparationBuildsFullPromptAndExactFrozenSmtpBinding()
    {
        var resolver = new FakeResolver();
        var outbox = new MemoryOutbox();
        var factory = new ScriptedFactory();
        var invocation = Invocation();
        var tool = Tool(resolver, outbox, factory);

        var preparation = await tool.PrepareWorkflowEffectAsync(
            invocation,
            PreparedAt,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        var effect = Assert.IsAssignableFrom<IPreparedWorkflowEffect>(preparation.Effect);

        Assert.True(preparation.Success, preparation.Error);
        Assert.Equal("다음 메일을 SMTP로 보낼까요?", effect.ApprovalPrompt.Heading);
        Assert.Equal(
            "SMTP 연결 (smtp.example.invalid:465) 연결에서 " +
            "sender@example.invalid 발신자로 메일을 제출합니다.",
            effect.ApprovalPrompt.Action);
        Assert.Equal(
            $"To:\nto@example.invalid\n\n" +
            $"Cc:\ncc@example.invalid\n\n" +
            $"Bcc:\nbcc@example.invalid\n\n" +
            $"제목:\nSubject {RawCanary}\n\n본문:\nBody {RawCanary}",
            effect.ApprovalPrompt.Details);
        Assert.Equal("R3", effect.ApprovalPrompt.Risk);
        Assert.Equal(PreparedAt.AddMinutes(2), effect.ExpiresAtUtc);
        Assert.Equal(
            WorkflowPreparedEffectHash.Compute(
                "mail.send.v1",
                "R3",
                "primary-mail",
                Draft().CanonicalHash),
            effect.CanonicalPayloadHash);
        Assert.Equal(
            WorkflowPreparedEffectHash.Compute(
                "mail.send.v1",
                "smtp",
                invocation.CapabilityContext!.SnapshotHash,
                invocation.CapabilityContext.ModelToolName,
                invocation.CapabilityContext.CapabilityId,
                invocation.CapabilityContext.Version,
                invocation.CapabilityContext.ContentHash,
                invocation.CapabilityContext.CatalogRevision.ToString(CultureInfo.InvariantCulture),
                invocation.CapabilityContext.ConnectionBindingHash,
                "primary-mail",
                FrozenBindingHash,
                LocalBindingHash,
                ExecutionFingerprint,
                "smtp.example.invalid",
                "465",
                ((int)SmtpTlsMode.ImplicitTls).ToString(CultureInfo.InvariantCulture),
                ((int)SmtpAuthenticationMechanism.Plain).ToString(CultureInfo.InvariantCulture),
                ((int)SmtpCertificatePolicy.SystemTrust).ToString(CultureInfo.InvariantCulture),
                "sender@example.invalid",
                "SMTP 연결"),
            effect.CanonicalBindingHash);
        var call = Assert.Single(resolver.Calls);
        Assert.Equal(SmtpResolutionPurpose.Preparation, call.Purpose);
        Assert.Equal("primary-mail", call.ConnectionSlot);
        Assert.Same(invocation.CapabilityContext, call.Context);
        Assert.Equal(0, outbox.TotalCalls);
        Assert.Equal(0, factory.ConnectCount);

        await effect.DisposeAsync();
        Assert.DoesNotContain(RawCanary, effect.ApprovalPrompt.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Final250CommitsQueuedOnlyAndReturnsExactBoundedResult()
    {
        var timeline = new List<string>();
        var connection = new ScriptedConnection(SubmissionReplies(includeFinal250: true), timeline);
        var factory = new ScriptedFactory(connection);
        var resolver = new FakeResolver();
        var outbox = new MemoryOutbox(timeline);
        var effect = await PrepareAsync(Tool(resolver, outbox, factory));

        var execution = await effect.ExecuteOnceAsync(
            PreparedAt.AddSeconds(1),
            CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.Succeeded, execution.Disposition);
        Assert.True(execution.Result.Success);
        Assert.Null(execution.Result.Error);
        Assert.Null(execution.Result.ErrorCode);
        Assert.Equal("승인된 메일을 SMTP 서버 대기열에 제출했어요.", execution.Result.ActivitySummary);
        Assert.Equal(
            new[] { "draftHash", "operationId", "status" },
            execution.Result.Output.Keys.OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal("queued", execution.Result.Output["status"]);
        Assert.Equal(Draft().CanonicalHash, execution.Result.Output["draftHash"]);
        var operation = Assert.Single(outbox.Operations);
        Assert.Equal(operation.OperationId.Value, execution.Result.Output["operationId"]);
        Assert.Equal(MailOutboxSubmissionState.Accepted, operation.SubmissionState);
        Assert.Equal(MailOutboxDeliveryState.Queued, operation.DeliveryState);
        Assert.Equal(MailOutboxSmtpPhase.DataStarted, operation.SmtpPhase);
        Assert.Equal(1, operation.AttemptCount);
        Assert.Equal(3, operation.Revision);
        Assert.Equal(1, connection.MessageWriteCount);
        Assert.Equal(1, connection.TerminatorWriteCount);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(1, connection.UpgradeCount);
        Assert.True(connection.Disposed);
        Assert.True(
            timeline.IndexOf("wire:reply:final250") <
            timeline.IndexOf("journal:commit:Accepted:Queued:DataStarted"),
            string.Join(" | ", timeline));
        AssertExecutionResourcesDisposed(resolver);
    }

    [Fact]
    public async Task BindingDriftAfterApprovalStopsBeforeJournalAndSocket()
    {
        var resolver = new FakeResolver
        {
            ExecutionFingerprint = Hash('f'),
        };
        var outbox = new MemoryOutbox();
        var factory = new ScriptedFactory();
        var effect = await PrepareAsync(Tool(resolver, outbox, factory));

        var execution = await effect.ExecuteOnceAsync(
            PreparedAt.AddSeconds(1),
            CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.NotStarted, execution.Disposition);
        Assert.Equal("mail.smtp.binding_mismatch", execution.Result.ErrorCode);
        Assert.Equal(0, outbox.TotalCalls);
        Assert.Equal(0, factory.ConnectCount);
        AssertExecutionResourcesDisposed(resolver);
    }

    [Fact]
    public async Task DataStartedCasCommitThenThrowIsConfirmedByGetAndSubmissionContinues()
    {
        var timeline = new List<string>();
        var connection = new ScriptedConnection(SubmissionReplies(includeFinal250: true), timeline);
        var factory = new ScriptedFactory(connection);
        var resolver = new FakeResolver();
        var outbox = new MemoryOutbox(timeline)
        {
            CommitThenThrowAtTransitionCall = 2,
        };
        var effect = await PrepareAsync(Tool(resolver, outbox, factory));

        var execution = await effect.ExecuteOnceAsync(
            PreparedAt.AddSeconds(1),
            CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.Succeeded, execution.Disposition);
        Assert.Equal("queued", execution.Result.Output["status"]);
        Assert.Equal(1, outbox.CommitThenThrowCount);
        Assert.True(outbox.GetCalls >= 2);
        Assert.Equal(1, connection.MessageWriteCount);
        var committed = timeline.IndexOf(
            "journal:commit-then-throw:Submitting:Unknown:DataStarted");
        var queried = timeline.IndexOf("journal:get");
        var message = timeline.IndexOf("wire:message");
        Assert.True(committed >= 0 && committed < queried && queried < message, string.Join(" | ", timeline));
        var operation = Assert.Single(outbox.Operations);
        Assert.Equal(MailOutboxSubmissionState.Accepted, operation.SubmissionState);
        Assert.Equal(MailOutboxDeliveryState.Queued, operation.DeliveryState);
        AssertExecutionResourcesDisposed(resolver);
    }

    [Fact]
    public async Task UncommittedDataStartedCasWritesNoMessageAndEndsKnownFailed()
    {
        var timeline = new List<string>();
        var connection = new ScriptedConnection(SubmissionReplies(includeFinal250: false), timeline);
        var factory = new ScriptedFactory(connection);
        var resolver = new FakeResolver();
        var outbox = new MemoryOutbox(timeline)
        {
            FailWithoutCommitAtTransitionCall = 2,
        };
        var effect = await PrepareAsync(Tool(resolver, outbox, factory));

        var execution = await effect.ExecuteOnceAsync(
            PreparedAt.AddSeconds(1),
            CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.FailedKnown, execution.Disposition);
        Assert.False(execution.Result.Success);
        Assert.Equal("mail.send.submit_failed", execution.Result.ErrorCode);
        Assert.Empty(execution.Result.Output);
        Assert.Equal(0, connection.MessageWriteCount);
        Assert.Equal(0, connection.TerminatorWriteCount);
        Assert.Equal("DATA\r\n", connection.Writes[^1].Text);
        Assert.Equal(1, factory.ConnectCount);
        var operation = Assert.Single(outbox.Operations);
        Assert.Equal(MailOutboxSubmissionState.FailedKnown, operation.SubmissionState);
        Assert.Equal(MailOutboxDeliveryState.Failed, operation.DeliveryState);
        Assert.Equal(MailOutboxSmtpPhase.PreData, operation.SmtpPhase);
        AssertExecutionResourcesDisposed(resolver);
    }

    [Fact]
    public async Task MissingFinalReplyIsUnknownAndNewApprovalDoesNotReconnectRetryOrFallback()
    {
        var timeline = new List<string>();
        var connection = new ScriptedConnection(SubmissionReplies(includeFinal250: false), timeline);
        var factory = new ScriptedFactory(connection);
        var resolver = new FakeResolver();
        var outbox = new MemoryOutbox(timeline);
        var fallbackEffects = new List<TrackingPreparedEffect>();
        var fallback = new TrackingTransport(() =>
        {
            var effect = new TrackingPreparedEffect("fallback");
            fallbackEffects.Add(effect);
            return new WorkflowEffectPreparationResult(
                false,
                effect,
                "SMTP binding만 일치합니다.",
                "mail.gateway.binding_unavailable");
        });
        var composite = new CompositeMailSendTool(Tool(resolver, outbox, factory), fallback);
        var firstEffect = await PrepareAsync(composite);

        var first = await firstEffect.ExecuteOnceAsync(
            PreparedAt.AddSeconds(1),
            CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.Unknown, first.Disposition);
        Assert.Equal("mail.send.outcome_unknown", first.Result.ErrorCode);
        Assert.Empty(first.Result.Output);
        var unknown = Assert.Single(outbox.Operations);
        Assert.Equal(MailOutboxSubmissionState.OutcomeUnknown, unknown.SubmissionState);
        Assert.Equal(MailOutboxDeliveryState.Unknown, unknown.DeliveryState);
        Assert.Equal(MailOutboxSmtpPhase.DataStarted, unknown.SmtpPhase);
        Assert.Contains(unknown.SmtpMessageId!, first.Result.ActivitySummary!, StringComparison.Ordinal);
        Assert.Equal(1, connection.MessageWriteCount);
        Assert.Equal(1, connection.TerminatorWriteCount);
        var writesAfterFirstApproval = connection.Writes.Count;

        var secondEffect = await PrepareAsync(
            composite,
            Invocation(toolCallId: "smtp-step-new-approval"));
        var second = await secondEffect.ExecuteOnceAsync(
            PreparedAt.AddSeconds(2),
            CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.Unknown, second.Disposition);
        Assert.Equal("mail.send.outcome_unknown", second.Result.ErrorCode);
        Assert.Contains(unknown.SmtpMessageId!, second.Result.ActivitySummary!, StringComparison.Ordinal);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(writesAfterFirstApproval, connection.Writes.Count);
        Assert.Equal(1, connection.MessageWriteCount);
        Assert.Equal(1, outbox.CreateCalls);
        Assert.Single(outbox.Operations);
        Assert.Equal(2, fallback.PrepareCalls);
        Assert.Equal(0, fallback.DirectExecuteCalls);
        Assert.All(fallbackEffects, effect =>
        {
            Assert.Equal(1, effect.DisposeCount);
            Assert.Equal(0, effect.ExecuteCount);
        });
        AssertExecutionResourcesDisposed(resolver);
    }

    [Fact]
    public async Task CompositeSelectsExactlyOneAndDisposesAllAmbiguousEffects()
    {
        var selected = new TrackingPreparedEffect("selected");
        var unused = new TrackingPreparedEffect("unused");
        var exact = new CompositeMailSendTool(
            new TrackingTransport(() => new WorkflowEffectPreparationResult(true, selected)),
            new TrackingTransport(() => new WorkflowEffectPreparationResult(
                false,
                unused,
                "not selected",
                "mail.smtp.binding_unavailable")));

        var exactResult = await exact.PrepareWorkflowEffectAsync(
            Invocation(), PreparedAt, TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.True(exactResult.Success);
        Assert.Same(selected, exactResult.Effect);
        Assert.Equal(0, selected.DisposeCount);
        Assert.Equal(1, unused.DisposeCount);
        _ = await selected.ExecuteOnceAsync(PreparedAt.AddSeconds(1), CancellationToken.None);
        Assert.Equal(1, selected.ExecuteCount);
        Assert.Equal(0, unused.ExecuteCount);
        await selected.DisposeAsync();
        Assert.Equal(1, selected.DisposeCount);

        var ambiguousFirst = new TrackingPreparedEffect("ambiguous-first");
        var ambiguousSecond = new TrackingPreparedEffect("ambiguous-second");
        var ambiguous = new CompositeMailSendTool(
            new TrackingTransport(() => new WorkflowEffectPreparationResult(true, ambiguousFirst)),
            new TrackingTransport(() => new WorkflowEffectPreparationResult(true, ambiguousSecond)));

        var ambiguousResult = await ambiguous.PrepareWorkflowEffectAsync(
            Invocation(), PreparedAt, TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.False(ambiguousResult.Success);
        Assert.Null(ambiguousResult.Effect);
        Assert.Equal("mail.send.binding_ambiguous", ambiguousResult.ErrorCode);
        Assert.Equal(1, ambiguousFirst.DisposeCount);
        Assert.Equal(1, ambiguousSecond.DisposeCount);
        Assert.Equal(0, ambiguousFirst.ExecuteCount);
        Assert.Equal(0, ambiguousSecond.ExecuteCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealWorkflowPersistsSmtpOutcomeWithoutCopyingWorkflowMailPayloadIntoDurableSurfaces(
        bool includeFinal250)
    {
        var databasePath = Path.Combine(
            directory,
            includeFinal250 ? "smtp-workflow-queued.db" : "smtp-workflow-unknown.db");
        using var store = new ConversationStore(databasePath, new FixedTimeProvider(PreparedAt));
        await store.InitializeAsync();
        const string conversationId = "smtp-workflow-conversation";
        const string parentRunId = "smtp-workflow-parent";
        const string conversationInput = "conversation input is durable by product design";
        await store.StartRunAsync(
            conversationId,
            parentRunId,
            conversationInput,
            PreparedAt);
        Assert.Equal(
            conversationInput,
            Assert.Single(await store.GetConversationTurnsAsync(conversationId)).UserInput);

        var capability = CreateWorkflowCapabilityDraft();
        Assert.Equal(
            CapabilityMutationOutcome.Succeeded,
            (await store.Capabilities.CreateValidatedDraftAsync(
                capability,
                PreparedAt,
                CancellationToken.None)).Outcome);
        var activation = await store.Capabilities.ApproveAndActivateAsync(
            new CapabilityApprovalCommand(
                capability.DraftId,
                capability.ExactVersion,
                ExpectedDraftRevision: 0,
                ExpectedCatalogRevision: 0,
                ExpectedActiveVersion: null,
                "review.approved",
                PreparedAt.AddMilliseconds(1)),
            CancellationToken.None);
        Assert.Equal(CapabilityMutationOutcome.Succeeded, activation.Outcome);

        var replies = WorkflowSubmissionReplies(includeFinal250);
        var connection = new ScriptedConnection(replies);
        var factory = new ScriptedFactory(connection);
        var resolver = new FakeResolver();
        var host = new WindowsToolHost(
            WindowsPlatformProfile.Classify(10, 0, 22_631, isWorkstation: true),
            [
                new MailCreateDraftTool(),
                new SmtpMailSendTool(resolver, store.MailOutbox, new SmtpWireClient(factory)),
            ]);
        var workflowTools = new DesktopWorkflowToolMetadataCatalog(host);
        var audit = new ForwardingAuditSink(store);
        var approvalCount = 0;
        var approvalWasExact = false;
        var orchestrator = new DesktopWorkflowOrchestrator(
            store.Capabilities,
            store.WorkflowLedger,
            workflowTools,
            host,
            policy => new ToolInvocationCoordinator(
                policy,
                host,
                (prompt, allowAlways, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref approvalCount);
                    approvalWasExact = !allowAlways &&
                                       prompt.Risk == "R3" &&
                                       prompt.Action.Contains("sender@example.invalid", StringComparison.Ordinal) &&
                                       prompt.Details.Contains(WorkflowRecipientCanary, StringComparison.Ordinal) &&
                                       prompt.Details.Contains(WorkflowCcCanary, StringComparison.Ordinal) &&
                                       prompt.Details.Contains(WorkflowBccCanary, StringComparison.Ordinal) &&
                                       prompt.Details.Contains(WorkflowSubjectCanary, StringComparison.Ordinal) &&
                                       prompt.Details.Contains(WorkflowBodyCanary, StringComparison.Ordinal) &&
                                       !prompt.ToString().Contains(CredentialPasswordCanary, StringComparison.Ordinal);
                    return Task.FromResult(ToolApprovalChoice.AllowOnce);
                },
                audit,
                store),
            new FixedTimeProvider(PreparedAt));
        var context = new CapabilityInvocationContext(
            SnapshotHash,
            CapabilityRuntimeHasher.ComputeModelToolName(capability.ExactVersion),
            capability.ExactVersion.Id.Value,
            capability.ExactVersion.Version.Value,
            capability.ExactVersion.ContentHash.Value,
            activation.Catalog!.CatalogRevision,
            FrozenBindingHash);
        var invocation = WorkflowInvocation(
            context,
            conversationId,
            parentRunId,
            workflowCallSuffix: '1',
            idempotencySuffix: '2');

        var result = await orchestrator.InvokeAsync(invocation, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var output = Assert.IsType<WorkflowInvokeV1Result>(result.Output);
        Assert.Equal(1, Volatile.Read(ref approvalCount));
        Assert.True(approvalWasExact);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(1, connection.MessageWriteCount);
        Assert.Equal(1, connection.TerminatorWriteCount);
        var sendStep = Assert.IsType<WorkflowStepRunRecord>(
            await store.WorkflowLedger.GetStepAsync(
                ToolCallId(output.WorkflowRunId, sequence: 1, attempt: 1),
                CancellationToken.None));
        var workflowRun = Assert.IsType<WorkflowRunRecord>(
            await store.WorkflowLedger.GetRunAsync(output.WorkflowRunId, CancellationToken.None));

        MailOutboxOperation operation;
        if (includeFinal250)
        {
            Assert.Equal("succeeded", output.Status);
            Assert.Equal("none", output.NextAction);
            Assert.Null(output.ErrorCode);
            var publicResult = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(output.Result);
            operation = Assert.IsType<MailOutboxOperation>(
                await store.MailOutbox.GetAsync(
                    MailOutboxOperationId.Parse(Assert.IsType<string>(publicResult["operationId"])),
                    CancellationToken.None));
            Assert.Equal("queued", Assert.IsType<string>(publicResult["status"]));
            Assert.Equal("succeeded", sendStep.Status);
            Assert.Equal("succeeded", workflowRun.Status);
            Assert.Equal(MailOutboxSubmissionState.Accepted, operation.SubmissionState);
            Assert.Equal(MailOutboxDeliveryState.Queued, operation.DeliveryState);
        }
        else
        {
            Assert.Equal("attention_required", output.Status);
            Assert.Equal("review_outcome", output.NextAction);
            Assert.Equal("mail.send.outcome_unknown", output.ErrorCode);
            Assert.Null(output.Result);
            operation = Assert.Single(await store.MailOutbox.ListSmtpNetworkStartedUnresolvedAsync(
                MailOutboxHash.Parse(WorkflowDraft().CanonicalHash),
                2,
                CancellationToken.None));
            Assert.Equal("outcome_unknown", sendStep.Status);
            Assert.Equal("unknown", sendStep.Disposition);
            Assert.Contains(operation.SmtpMessageId!, sendStep.RedactedSummary!, StringComparison.Ordinal);
            Assert.Equal("attention_required", workflowRun.Status);
            Assert.Equal("review_outcome", workflowRun.FixedNextAction);
            Assert.Equal(MailOutboxSubmissionState.OutcomeUnknown, operation.SubmissionState);
            Assert.Equal(MailOutboxDeliveryState.Unknown, operation.DeliveryState);

            var second = await orchestrator.InvokeAsync(
                WorkflowInvocation(
                    context,
                    conversationId,
                    parentRunId,
                    workflowCallSuffix: '3',
                    idempotencySuffix: '4'),
                CancellationToken.None);
            var secondOutput = Assert.IsType<WorkflowInvokeV1Result>(second.Output);
            Assert.Equal("attention_required", secondOutput.Status);
            Assert.Equal("review_outcome", secondOutput.NextAction);
            Assert.Equal("mail.send.outcome_unknown", secondOutput.ErrorCode);
            Assert.Equal(2, Volatile.Read(ref approvalCount));
            Assert.True(approvalWasExact);
            Assert.Equal(1, factory.ConnectCount);
            Assert.Equal(1, connection.MessageWriteCount);
            Assert.Single(await store.MailOutbox.ListSmtpNetworkStartedUnresolvedAsync(
                MailOutboxHash.Parse(WorkflowDraft().CanonicalHash),
                2,
                CancellationToken.None));
            var secondStep = Assert.IsType<WorkflowStepRunRecord>(
                await store.WorkflowLedger.GetStepAsync(
                    ToolCallId(secondOutput.WorkflowRunId, sequence: 1, attempt: 1),
                    CancellationToken.None));
            Assert.Contains(operation.SmtpMessageId!, secondStep.RedactedSummary!, StringComparison.Ordinal);
            var activities = await store.GetRecentToolActivityAsync(
                cancellationToken: CancellationToken.None);
            var expectedActivityName =
                $"Workflow · {capability.ExactVersion.Id.Value}@{capability.ExactVersion.Version.Value}";
            Assert.Contains(activities, activity =>
                activity.ToolName == expectedActivityName &&
                activity.Status == "attention_required" &&
                activity.Summary.Contains(operation.SmtpMessageId!, StringComparison.Ordinal));
            var secondActivity = activities.First(activity =>
                activity.ToolName == expectedActivityName &&
                activity.Status == "attention_required" &&
                activity.Summary.Contains(operation.SmtpMessageId!, StringComparison.Ordinal));
            Assert.Contains("실행 결과 검토", secondActivity.Summary, StringComparison.Ordinal);
        }

        Assert.Equal(MailOutboxTransportKind.Smtp, operation.TransportKind);
        Assert.Equal(MailOutboxSmtpPhase.DataStarted, operation.SmtpPhase);
        Assert.Equal(1, operation.AttemptCount);
        Assert.All(resolver.ExecutionCredentials, credential =>
            Assert.Throws<ObjectDisposedException>(() => credential.CreateAuthPlainCommand()));
        Assert.All(resolver.ExecutionLeases, lease => Assert.Equal(1, lease.DisposeCount));

        var publicJson = JsonSerializer.Serialize(output);
        var persistedVisibleText = string.Join(
            "\n",
            workflowRun,
            sendStep,
            operation,
            string.Join("\n", await store.GetRecentToolActivityAsync(
                cancellationToken: CancellationToken.None)),
            string.Join("\n", audit.Approvals),
            string.Join("\n", audit.Executions));
        AssertNoWorkflowRawCanaries(publicJson, persistedVisibleText);
        AssertDatabaseFilesDoNotContainInvocationMailPayloadCanaries(databasePath);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static SmtpMailSendTool Tool(
        FakeResolver resolver,
        MemoryOutbox outbox,
        ScriptedFactory factory) =>
        new(resolver, outbox, new SmtpWireClient(factory));

    private static async Task<IPreparedWorkflowEffect> PrepareAsync(
        IWorkflowOneShotToolAdapter tool,
        ToolInvokeParams? invocation = null)
    {
        var preparation = await tool.PrepareWorkflowEffectAsync(
            invocation ?? Invocation(),
            PreparedAt,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        Assert.True(preparation.Success, preparation.Error);
        return Assert.IsAssignableFrom<IPreparedWorkflowEffect>(preparation.Effect);
    }

    private static ToolInvokeParams Invocation(
        string toolCallId = "smtp-step-1",
        IReadOnlyDictionary<string, object?>? input = null) =>
        new(
            toolCallId,
            "conversation-1",
            "workflow-run-1",
            "mail.send.v1",
            "R3",
            input ?? Input(),
            Context());

    private static Dictionary<string, object?> Input()
    {
        var draft = Draft();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["connectionSlot"] = "primary-mail",
            ["draftHash"] = draft.CanonicalHash,
            ["to"] = new[] { "to@example.invalid" },
            ["cc"] = new[] { "cc@example.invalid" },
            ["bcc"] = new[] { "bcc@example.invalid" },
            ["subject"] = $"Subject {RawCanary}",
            ["body"] = $"Body {RawCanary}",
        };
    }

    private static MailDraft Draft() =>
        Assert.IsType<MailDraft>(MailDraftPolicy.Validate(new MailDraftInput(
            ["to@example.invalid"],
            ["cc@example.invalid"],
            ["bcc@example.invalid"],
            $"Subject {RawCanary}",
            $"Body {RawCanary}")).Draft);

    private static CapabilityInvocationContext Context()
    {
        var exact = new CapabilityVersionRef(
            CapabilityId.Parse("company.mail.send"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            CapabilityContentHash.Parse(ContentHash));
        return new CapabilityInvocationContext(
            SnapshotHash,
            CapabilityRuntimeHasher.ComputeModelToolName(exact),
            exact.Id.Value,
            exact.Version.Value,
            exact.ContentHash.Value,
            7,
            FrozenBindingHash);
    }

    private static SmtpRuntimeBinding Binding(
        string executionFingerprint = ExecutionFingerprint,
        SmtpWireCredential? credential = null) =>
        new(
            "SMTP 연결",
            "smtp.example.invalid",
            465,
            SmtpTlsMode.ImplicitTls,
            SmtpAuthenticationMechanism.Plain,
            SmtpCertificatePolicy.SystemTrust,
            "sender@example.invalid",
            FrozenBindingHash,
            LocalBindingHash,
            executionFingerprint,
            credential);

    private static IReadOnlyList<ReplyChunk> SubmissionReplies(bool includeFinal250)
    {
        var replies = new List<ReplyChunk>
        {
            new("greeting", "220 smtp ready\r\n"),
            new("ehlo", "250-smtp\r\n250 AUTH PLAIN\r\n"),
            new("auth", "235 authenticated\r\n"),
            new("mail", "250 sender accepted\r\n"),
            new("rcpt-to", "250 recipient accepted\r\n"),
            new("rcpt-cc", "250 recipient accepted\r\n"),
            new("rcpt-bcc", "250 recipient accepted\r\n"),
            new("data", "354 send content\r\n"),
        };
        if (includeFinal250) replies.Add(new ReplyChunk("final250", "250 queued\r\n"));
        return replies.AsReadOnly();
    }

    private static IReadOnlyList<ReplyChunk> WorkflowSubmissionReplies(bool includeFinal250)
    {
        var replies = new List<ReplyChunk>
        {
            new("greeting", $"220 {RemoteReplyCanary}\r\n"),
            new("ehlo", "250-smtp\r\n250 AUTH PLAIN\r\n"),
            new("auth", "235 authenticated\r\n"),
            new("mail", "250 sender accepted\r\n"),
            new("rcpt-to", "250 recipient accepted\r\n"),
            new("rcpt-cc", "250 recipient accepted\r\n"),
            new("rcpt-bcc", "250 recipient accepted\r\n"),
            new("data", "354 send content\r\n"),
        };
        if (includeFinal250) replies.Add(new ReplyChunk("final250", $"250 {RemoteReplyCanary}\r\n"));
        return replies.AsReadOnly();
    }

    private static MailDraft WorkflowDraft() =>
        Assert.IsType<MailDraft>(MailDraftPolicy.Validate(new MailDraftInput(
            [WorkflowRecipientCanary],
            [WorkflowCcCanary],
            [WorkflowBccCanary],
            WorkflowSubjectCanary,
            $"{WorkflowBodyCanary}\nsecond line")).Draft);

    private static WorkflowInvokeParams WorkflowInvocation(
        CapabilityInvocationContext context,
        string conversationId,
        string parentRunId,
        char workflowCallSuffix,
        char idempotencySuffix) =>
        new(
            $"wfinv_{new string(workflowCallSuffix, 48)}",
            conversationId,
            parentRunId,
            "workflow.invoke.v1",
            context,
            $"wfid_{new string(idempotencySuffix, 64)}",
            PreparedAt.AddMinutes(8),
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["to"] = new[] { WorkflowRecipientCanary },
                ["cc"] = new[] { WorkflowCcCanary },
                ["bcc"] = new[] { WorkflowBccCanary },
                ["subject"] = WorkflowSubjectCanary,
                ["body"] = $"{WorkflowBodyCanary}\nsecond line",
            });

    private static ValidatedCapabilityDraft CreateWorkflowCapabilityDraft()
    {
        var id = CapabilityId.Parse("company.sample.smtp-mail-send-vertical");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        var manifest = new CapabilityManifest(
            1,
            id,
            version,
            "SMTP mail send vertical",
            "Validates and submits one bounded mail draft through an exact SMTP connection.",
            CapabilityKind.Workflow,
            ["Send a reviewed mail through SMTP"],
            ["mail.create_draft.v1", "mail.send.v1"],
            [new CapabilityConnectionRequirement("primary-mail", CapabilityConnectionKindPolicy.Smtp)],
            "R3",
            "workflow.json");
        var payload = new CapabilityVersionPayload(
            manifest,
            JsonSerializer.Serialize(new
            {
                description = manifest.Description,
                displayName = manifest.DisplayName,
                entrypoint = manifest.Entrypoint,
                id = id.Value,
                intentExamples = manifest.IntentExamples,
                kind = "workflow",
                maximumRisk = manifest.MaximumRisk,
                requiredConnections = new[]
                {
                    new { kind = CapabilityConnectionKindPolicy.Smtp, slot = "primary-mail" },
                },
                requiredTools = manifest.RequiredTools,
                schemaVersion = 1,
                version = version.Value,
            }),
            "Validate locally, obtain one-shot approval, then submit through exact SMTP.",
            WorkflowInputSchema,
            CanonicalWorkflowJson: WorkflowJson);
        var hash = CapabilityContentHasher.Compute(payload);
        return new ValidatedCapabilityDraft(
            "smtp_mail_send_workflow_vertical_draft",
            payload,
            hash,
            null,
            "local_import",
            hash);
    }

    private static string ToolCallId(string runId, int sequence, int attempt)
    {
        var hash = WorkflowPreparedEffectHash.Compute(
            "workflow-step-tool-call-v1",
            runId,
            sequence.ToString(CultureInfo.InvariantCulture),
            attempt.ToString(CultureInfo.InvariantCulture));
        return $"wfs-{hash[7..]}";
    }

    private static void AssertNoWorkflowRawCanaries(params string[] visibleValues)
    {
        foreach (var value in visibleValues)
            foreach (var canary in WorkflowRawCanaries)
                Assert.DoesNotContain(canary, value, StringComparison.Ordinal);
    }

    private static void AssertDatabaseFilesDoNotContainInvocationMailPayloadCanaries(string databasePath)
    {
        var files = new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }
            .Where(File.Exists)
            .ToArray();
        Assert.Contains(databasePath, files, StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            foreach (var canary in WorkflowRawCanaries)
                Assert.False(
                    Contains(bytes, Encoding.UTF8.GetBytes(canary)),
                    $"Raw SMTP canary was found in {Path.GetFileName(path)}: {canary}");
        }
    }

    private static bool Contains(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return true;
        for (var index = 0; index <= source.Length - value.Length; index++)
            if (source.Slice(index, value.Length).SequenceEqual(value)) return true;
        return false;
    }

    private static void AssertExecutionResourcesDisposed(FakeResolver resolver)
    {
        Assert.NotEmpty(resolver.ExecutionCredentials);
        Assert.All(resolver.ExecutionCredentials, credential =>
            Assert.Throws<ObjectDisposedException>(() => credential.CreateAuthPlainCommand()));
        Assert.All(resolver.ExecutionLeases, lease => Assert.Equal(1, lease.DisposeCount));
    }

    private const string SnapshotHash =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ContentHash =
        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string FrozenBindingHash =
        "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string LocalBindingHash =
        "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string ExecutionFingerprint =
        "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    private static readonly string[] WorkflowRawCanaries =
    [
        WorkflowRecipientCanary,
        WorkflowCcCanary,
        WorkflowBccCanary,
        WorkflowSubjectCanary,
        WorkflowBodyCanary,
        CredentialUsernameCanary,
        CredentialPasswordCanary,
        RemoteReplyCanary,
        "sender@example.invalid",
    ];

    private const string WorkflowInputSchema =
        "{\"additionalProperties\":false,\"properties\":{\"bcc\":{\"items\":{\"format\":\"email\",\"maxLength\":254,\"minLength\":3,\"type\":\"string\"},\"maxItems\":16,\"minItems\":1,\"type\":\"array\",\"uniqueItems\":true},\"body\":{\"maxLength\":4096,\"minLength\":1,\"type\":\"string\"},\"cc\":{\"items\":{\"format\":\"email\",\"maxLength\":254,\"minLength\":3,\"type\":\"string\"},\"maxItems\":16,\"minItems\":1,\"type\":\"array\",\"uniqueItems\":true},\"subject\":{\"maxLength\":200,\"minLength\":1,\"type\":\"string\"},\"to\":{\"items\":{\"format\":\"email\",\"maxLength\":254,\"minLength\":3,\"type\":\"string\"},\"maxItems\":16,\"minItems\":1,\"type\":\"array\",\"uniqueItems\":true}},\"required\":[\"to\",\"cc\",\"bcc\",\"subject\",\"body\"],\"type\":\"object\"}";

    private const string WorkflowJson =
        "{\"schemaVersion\":1,\"entryStepId\":\"draft\",\"steps\":[{\"id\":\"draft\",\"type\":\"tool.call\",\"tool\":\"mail.create_draft.v1\",\"arguments\":{\"to\":{\"$bind\":{\"source\":\"input\",\"path\":\"/to\"}},\"cc\":{\"$bind\":{\"source\":\"input\",\"path\":\"/cc\"}},\"bcc\":{\"$bind\":{\"source\":\"input\",\"path\":\"/bcc\"}},\"subject\":{\"$bind\":{\"source\":\"input\",\"path\":\"/subject\"}},\"body\":{\"$bind\":{\"source\":\"input\",\"path\":\"/body\"}}},\"next\":\"send\"},{\"id\":\"send\",\"type\":\"tool.call\",\"tool\":\"mail.send.v1\",\"arguments\":{\"connectionSlot\":\"primary-mail\",\"draftHash\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"draft\",\"path\":\"/draftHash\"}},\"to\":{\"$bind\":{\"source\":\"input\",\"path\":\"/to\"}},\"cc\":{\"$bind\":{\"source\":\"input\",\"path\":\"/cc\"}},\"bcc\":{\"$bind\":{\"source\":\"input\",\"path\":\"/bcc\"}},\"subject\":{\"$bind\":{\"source\":\"input\",\"path\":\"/subject\"}},\"body\":{\"$bind\":{\"source\":\"input\",\"path\":\"/body\"}}},\"next\":\"done\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{\"operationId\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"send\",\"path\":\"/operationId\"}},\"status\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"send\",\"path\":\"/status\"}},\"draftHash\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"send\",\"path\":\"/draftHash\"}}}}]}";

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    private sealed record ResolutionCall(
        CapabilityInvocationContext Context,
        string ConnectionSlot,
        SmtpResolutionPurpose Purpose);

    private sealed class FakeResolver : ISmtpExecutionResolver
    {
        public string ExecutionFingerprint { get; set; } = SmtpMailSendVerticalTests.ExecutionFingerprint;
        public List<ResolutionCall> Calls { get; } = [];
        public List<SmtpWireCredential> ExecutionCredentials { get; } = [];
        public List<SmtpRuntimeBinding> ExecutionBindings { get; } = [];
        public List<TrackingLease> ExecutionLeases { get; } = [];
        public int CallCount => Calls.Count;

        public Task<SmtpExecutionResolution> ResolveAsync(
            CapabilityInvocationContext capabilityContext,
            string connectionSlot,
            SmtpResolutionPurpose purpose,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new ResolutionCall(capabilityContext, connectionSlot, purpose));
            if (purpose == SmtpResolutionPurpose.Preparation)
                return Task.FromResult(new SmtpExecutionResolution(true, Binding()));

            var credential = new SmtpWireCredential(
                CredentialUsernameCanary,
                CredentialPasswordCanary);
            var binding = Binding(ExecutionFingerprint, credential);
            var lease = new TrackingLease();
            ExecutionCredentials.Add(credential);
            ExecutionBindings.Add(binding);
            ExecutionLeases.Add(lease);
            return Task.FromResult(new SmtpExecutionResolution(
                true,
                binding,
                ExecutionLease: lease));
        }
    }

    private sealed class TrackingLease : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }

    private sealed class ForwardingAuditSink(ConversationStore store) : IToolAuditSink
    {
        public List<string> Approvals { get; } = [];
        public List<string> Executions { get; } = [];

        public async Task RecordApprovalAsync(
            ToolApprovalAuditRecord record,
            CancellationToken cancellationToken)
        {
            Approvals.Add(record.ToString());
            await store.RecordApprovalAsync(record, cancellationToken);
        }

        public async Task RecordExecutionAsync(
            ToolExecutionAuditRecord record,
            CancellationToken cancellationToken)
        {
            Executions.Add(record.ToString());
            await store.RecordExecutionAsync(record, cancellationToken);
        }
    }

    private sealed record ReplyChunk(string Label, string Text)
    {
        public byte[] Bytes { get; } = Encoding.ASCII.GetBytes(Text);
    }

    private sealed record RecordedWrite(byte[] Bytes)
    {
        public string Text => Encoding.UTF8.GetString(Bytes);
    }

    private sealed class ScriptedConnection(
        IReadOnlyList<ReplyChunk> replies,
        List<string>? timeline = null) : ISmtpWireConnection
    {
        private int replyIndex;
        private int replyOffset;
        private bool eofRecorded;

        public bool IsTls { get; private set; }
        public int UpgradeCount { get; private set; }
        public bool Disposed { get; private set; }
        public List<RecordedWrite> Writes { get; } = [];
        public int MessageWriteCount => Writes.Count(write =>
            write.Text.Contains("Message-ID:", StringComparison.Ordinal));
        public int TerminatorWriteCount => Writes.Count(write => write.Text == ".\r\n");

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (replyIndex == replies.Count)
            {
                if (!eofRecorded)
                {
                    timeline?.Add("wire:eof");
                    eofRecorded = true;
                }
                return ValueTask.FromResult(0);
            }

            var reply = replies[replyIndex];
            if (replyOffset == 0) timeline?.Add($"wire:reply:{reply.Label}");
            var length = Math.Min(buffer.Length, reply.Bytes.Length - replyOffset);
            reply.Bytes.AsMemory(replyOffset, length).CopyTo(buffer);
            replyOffset += length;
            if (replyOffset == reply.Bytes.Length)
            {
                replyIndex++;
                replyOffset = 0;
            }
            return ValueTask.FromResult(length);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var write = new RecordedWrite(buffer.ToArray());
            Writes.Add(write);
            if (write.Text.Contains("Message-ID:", StringComparison.Ordinal))
                timeline?.Add("wire:message");
            else if (write.Text == ".\r\n")
                timeline?.Add("wire:terminator");
            else if (write.Text == "DATA\r\n")
                timeline?.Add("wire:data-command");
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public Task UpgradeToTlsAsync(string targetHost, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("smtp.example.invalid", targetHost);
            IsTls = true;
            UpgradeCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedFactory(ScriptedConnection? connection = null) : ISmtpWireConnectionFactory
    {
        public int ConnectCount { get; private set; }

        public Task<ISmtpWireConnection> ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            if (connection is null || ConnectCount > 1)
                throw new InvalidOperationException("Unexpected SMTP connection.");
            Assert.Equal("smtp.example.invalid", host);
            Assert.Equal(465, port);
            return Task.FromResult<ISmtpWireConnection>(connection);
        }
    }

    private sealed class MemoryOutbox(List<string>? timeline = null) : IMailOutboxRepository
    {
        private readonly Dictionary<string, MailOutboxOperation> byToolCall = new(StringComparer.Ordinal);
        private int identity;

        public int? CommitThenThrowAtTransitionCall { get; set; }
        public int? FailWithoutCommitAtTransitionCall { get; set; }
        public int CommitThenThrowCount { get; private set; }
        public int CreateCalls { get; private set; }
        public int GetCalls { get; private set; }
        public int TransitionCalls { get; private set; }
        public int ListNetworkCalls { get; private set; }
        public int ListSmtpCalls { get; private set; }
        public int RecoveryCalls { get; private set; }
        public int TotalCalls =>
            CreateCalls + GetCalls + TransitionCalls + ListNetworkCalls + ListSmtpCalls + RecoveryCalls;
        public IReadOnlyList<MailOutboxOperation> Operations =>
            byToolCall.Values.OrderBy(operation => operation.OperationId.Value, StringComparer.Ordinal).ToArray();

        public Task<MailOutboxCreateResult> CreateOrGetAsync(
            MailOutboxCreateRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            timeline?.Add("journal:create");
            if (byToolCall.TryGetValue(request.ToolCallId, out var existing))
            {
                return Task.FromResult(new MailOutboxCreateResult(
                    IdentityMatches(existing, request)
                        ? MailOutboxCreateOutcome.Existing
                        : MailOutboxCreateOutcome.Conflict,
                    existing,
                    IdentityMatches(existing, request) ? null : MailOutboxPolicy.IdentityConflict));
            }

            var next = ++identity;
            var operation = MailOutboxOperation.CreateStaged(
                MailOutboxOperationId.Parse($"mgo_{next:x64}"),
                MailOutboxIdempotencyKey.Parse($"mik_{next:x64}"),
                request);
            byToolCall.Add(request.ToolCallId, operation);
            return Task.FromResult(new MailOutboxCreateResult(
                MailOutboxCreateOutcome.Created,
                operation));
        }

        public Task<MailOutboxOperation?> GetAsync(
            MailOutboxOperationId operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            timeline?.Add("journal:get");
            return Task.FromResult<MailOutboxOperation?>(byToolCall.Values.SingleOrDefault(
                operation => operation.OperationId.Equals(operationId)));
        }

        public Task<MailOutboxTransitionResult> TryTransitionAsync(
            MailOutboxTransitionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransitionCalls++;
            var current = byToolCall.Values.SingleOrDefault(
                operation => operation.OperationId.Equals(request.OperationId));
            if (current is null)
            {
                return Task.FromResult(new MailOutboxTransitionResult(
                    MailOutboxTransitionOutcome.NotFound,
                    ErrorCode: MailOutboxPolicy.NotFound));
            }
            if (FailWithoutCommitAtTransitionCall == TransitionCalls)
            {
                timeline?.Add($"journal:stale:{Describe(request)}");
                return Task.FromResult(new MailOutboxTransitionResult(
                    MailOutboxTransitionOutcome.Stale,
                    current,
                    MailOutboxPolicy.TransitionStale));
            }
            if (!MailOutboxPolicy.TryApplyTransition(current, request, out var updated, out var error) ||
                updated is null)
            {
                return Task.FromResult(new MailOutboxTransitionResult(
                    StringComparer.Ordinal.Equals(error, MailOutboxPolicy.TransitionStale)
                        ? MailOutboxTransitionOutcome.Stale
                        : MailOutboxTransitionOutcome.Invalid,
                    current,
                    error));
            }

            byToolCall[current.ToolCallId] = updated;
            if (CommitThenThrowAtTransitionCall == TransitionCalls)
            {
                CommitThenThrowCount++;
                timeline?.Add($"journal:commit-then-throw:{Describe(request)}");
                throw new InvalidOperationException("Committed CAS response was lost.");
            }
            timeline?.Add($"journal:commit:{Describe(request)}");
            return Task.FromResult(new MailOutboxTransitionResult(
                MailOutboxTransitionOutcome.Succeeded,
                updated));
        }

        public Task<IReadOnlyList<MailOutboxOperation>> ListNetworkStartedUnresolvedAsync(
            MailOutboxHash draftHash,
            MailOutboxHash bindingHash,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListNetworkCalls++;
            return Task.FromResult<IReadOnlyList<MailOutboxOperation>>([]);
        }

        public Task<IReadOnlyList<MailOutboxOperation>> ListSmtpNetworkStartedUnresolvedAsync(
            MailOutboxHash draftHash,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListSmtpCalls++;
            timeline?.Add("journal:list-smtp-unresolved");
            IReadOnlyList<MailOutboxOperation> result = byToolCall.Values
                .Where(operation =>
                    operation.TransportKind == MailOutboxTransportKind.Smtp &&
                    operation.DraftHash.Equals(draftHash) &&
                    operation.SubmissionState is MailOutboxSubmissionState.Submitting or
                        MailOutboxSubmissionState.OutcomeUnknown &&
                    operation.DeliveryState == MailOutboxDeliveryState.Unknown)
                .OrderByDescending(operation => operation.UpdatedAtUtc)
                .ThenBy(operation => operation.OperationId.Value, StringComparer.Ordinal)
                .Take(limit)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task<MailOutboxStartupRecoveryResult> RecoverOnStartupAsync(
            DateTimeOffset recoveredAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecoveryCalls++;
            return Task.FromResult(new MailOutboxStartupRecoveryResult(0, 0));
        }

        private static string Describe(MailOutboxTransitionRequest request) =>
            $"{request.NextSubmissionState}:{request.NextDeliveryState}:{request.NextSmtpPhase}";

        private static bool IdentityMatches(
            MailOutboxOperation operation,
            MailOutboxCreateRequest request) =>
            StringComparer.Ordinal.Equals(operation.ConversationId, request.ConversationId) &&
            StringComparer.Ordinal.Equals(operation.WorkflowRunId, request.WorkflowRunId) &&
            StringComparer.Ordinal.Equals(operation.ToolCallId, request.ToolCallId) &&
            StringComparer.Ordinal.Equals(operation.CapabilityId, request.CapabilityId) &&
            StringComparer.Ordinal.Equals(operation.CapabilityVersion, request.CapabilityVersion) &&
            operation.CapabilityContentHash.Equals(request.CapabilityContentHash) &&
            operation.CatalogRevision == request.CatalogRevision &&
            operation.SnapshotHash.Equals(request.SnapshotHash) &&
            StringComparer.Ordinal.Equals(operation.ConnectionSlot, request.ConnectionSlot) &&
            operation.BindingHash.Equals(request.BindingHash) &&
            operation.DraftHash.Equals(request.DraftHash) &&
            operation.TransportKind == request.TransportKind;
    }

    private sealed class TrackingTransport(
        Func<WorkflowEffectPreparationResult> prepare) : IWorkflowOneShotToolAdapter
    {
        public string Name => "mail.send.v1";
        public string Risk => "R3";
        public WindowsCapability RequiredCapabilities =>
            WindowsCapability.WebAccess | WindowsCapability.CredentialManager;
        public int Priority => 0;
        public TimeSpan Timeout => TimeSpan.FromSeconds(50);
        public int PrepareCalls { get; private set; }
        public int DirectExecuteCalls { get; private set; }

        public WindowsToolApprovalPrompt? CreateApprovalPrompt(
            IReadOnlyDictionary<string, object?> input) => null;

        public Task<WindowsToolExecutionResult> ExecuteAsync(
            IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectExecuteCalls++;
            return Task.FromResult(new WindowsToolExecutionResult(
                false,
                new Dictionary<string, object?>(),
                ErrorCode: "tracking.direct"));
        }

        public Task<WorkflowEffectPreparationResult> PrepareWorkflowEffectAsync(
            ToolInvokeParams invocation,
            DateTimeOffset nowUtc,
            TimeSpan lifetime,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCalls++;
            return Task.FromResult(prepare());
        }
    }

    private sealed class TrackingPreparedEffect(string name) : IPreparedWorkflowEffect
    {
        public string ToolName => "mail.send.v1";
        public string Risk => "R3";
        public string CanonicalPayloadHash => Hash('1');
        public string CanonicalBindingHash => Hash('2');
        public DateTimeOffset ExpiresAtUtc => PreparedAt.AddMinutes(2);
        public WindowsToolApprovalPrompt ApprovalPrompt { get; } =
            new("tracking", name, "tracking", "R3");
        public int ExecuteCount { get; private set; }
        public int DisposeCount { get; private set; }

        public Task<WorkflowPreparedEffectExecution> ExecuteOnceAsync(
            DateTimeOffset trustedNowUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            return Task.FromResult(new WorkflowPreparedEffectExecution(
                WorkflowEffectDisposition.Unknown,
                new WindowsToolExecutionResult(
                    false,
                    new Dictionary<string, object?>(),
                    ErrorCode: "tracking.unknown")));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
