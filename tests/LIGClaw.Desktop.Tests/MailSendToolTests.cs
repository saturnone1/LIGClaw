using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Mail;
using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class MailSendToolTests
{
    private const string Token = "mail-gateway-test-token";
    private const string RawCanary = "RAW-MAIL-CANARY-DO-NOT-RETAIN";
    private static readonly DateTimeOffset PreparedAt =
        DateTimeOffset.Parse("2026-08-02T00:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public async Task DirectInvocationIsDeniedWithoutResolverJournalOrNetwork()
    {
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException());
        var resolver = new FakeResolver();
        var outbox = new MemoryOutbox();
        var tool = Tool(resolver, outbox, handler);

        var result = await tool.ExecuteAsync(Input(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("mail.send.context_required", result.ErrorCode);
        Assert.Equal(0, resolver.CallCount);
        Assert.Empty(outbox.Operations);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("queued", MailOutboxDeliveryState.Queued)]
    [InlineData("delivered", MailOutboxDeliveryState.Delivered)]
    public async Task SubmitsExactlyOnceAndReturnsExactBoundedResult(
        string status,
        MailOutboxDeliveryState delivery)
    {
        var handler = new ScriptedHandler(request => OperationResponse(request, status));
        var outbox = new MemoryOutbox();
        var effect = await PrepareAsync(Tool(new FakeResolver(), outbox, handler));

        var execution = await effect.ExecuteOnceAsync(PreparedAt.AddSeconds(1), CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.Succeeded, execution.Disposition);
        Assert.True(execution.Result.Success);
        Assert.Equal(
            new[] { "operationId", "status", "draftHash" }.OrderBy(value => value, StringComparer.Ordinal),
            execution.Result.Output.Keys.OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal(status, execution.Result.Output["status"]);
        Assert.Equal(Draft().CanonicalHash, execution.Result.Output["draftHash"]);
        Assert.StartsWith("mgo_", Assert.IsType<string>(execution.Result.Output["operationId"]), StringComparison.Ordinal);
        Assert.Equal(1, handler.PostCount);
        Assert.Equal(0, handler.GetCount);
        var operation = Assert.Single(outbox.Operations);
        Assert.Equal(MailOutboxSubmissionState.Accepted, operation.SubmissionState);
        Assert.Equal(delivery, operation.DeliveryState);
        Assert.Equal(1, operation.AttemptCount);
        Assert.Equal(status == "delivered" ? 3 : 2, operation.Revision);
        Assert.DoesNotContain(Token, Flatten(execution.Result), StringComparison.Ordinal);
        Assert.DoesNotContain(RawCanary, Flatten(execution.Result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreparationRevalidatesHashAndBuildsFullRedactedApprovalWithExactHashes()
    {
        var resolver = new FakeResolver();
        var outbox = new MemoryOutbox();
        var handler = new ScriptedHandler(OperationResponseQueued);
        var tool = Tool(resolver, outbox, handler);
        var invocation = Invocation();

        var preparation = await tool.PrepareWorkflowEffectAsync(
            invocation,
            PreparedAt,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        var effect = Assert.IsAssignableFrom<IPreparedWorkflowEffect>(preparation.Effect);

        Assert.True(preparation.Success);
        Assert.Equal(MailGatewayResolutionPurpose.Preparation, Assert.Single(resolver.Purposes));
        Assert.Contains("메일 연결", effect.ApprovalPrompt.Action, StringComparison.Ordinal);
        Assert.Contains("sender@example.invalid", effect.ApprovalPrompt.Action, StringComparison.Ordinal);
        Assert.Contains("to@example.invalid", effect.ApprovalPrompt.Details, StringComparison.Ordinal);
        Assert.Contains("cc@example.invalid", effect.ApprovalPrompt.Details, StringComparison.Ordinal);
        Assert.Contains("bcc@example.invalid", effect.ApprovalPrompt.Details, StringComparison.Ordinal);
        Assert.Contains(RawCanary, effect.ApprovalPrompt.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(RawCanary, effect.ApprovalPrompt.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sender@example.invalid", effect.ApprovalPrompt.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(RawCanary, effect.ToString(), StringComparison.Ordinal);
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
                invocation.CapabilityContext!.SnapshotHash,
                invocation.CapabilityContext.ModelToolName,
                invocation.CapabilityContext.CapabilityId,
                invocation.CapabilityContext.Version,
                invocation.CapabilityContext.ContentHash,
                invocation.CapabilityContext.CatalogRevision.ToString(CultureInfo.InvariantCulture),
                invocation.CapabilityContext.ConnectionBindingHash,
                "primary-mail",
                AggregateHash,
                LocalBindingHash,
                ExecutionFingerprint,
                "https://mail.example.invalid/",
                "sender@example.invalid",
                "메일 연결"),
            effect.CanonicalBindingHash);

        var mismatched = Input();
        mismatched["draftHash"] = Hash('9');
        var rejected = await tool.PrepareWorkflowEffectAsync(
            Invocation(input: mismatched),
            PreparedAt,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        Assert.False(rejected.Success);
        Assert.Equal("mail.send.draft_hash_mismatch", rejected.ErrorCode);
        Assert.Equal(1, resolver.CallCount);
        Assert.Empty(outbox.Operations);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WorkflowObjectCollectionsAreAcceptedAndInvalidInvocationTokensFailBeforeResolver()
    {
        var resolver = new FakeResolver();
        var tool = Tool(resolver, new MemoryOutbox(), new ScriptedHandler(OperationResponseQueued));
        var converted = Input();
        converted["to"] = Array.AsReadOnly<object?>(["to@example.invalid"]);
        converted["cc"] = Array.AsReadOnly<object?>(["cc@example.invalid"]);
        converted["bcc"] = Array.AsReadOnly<object?>(["bcc@example.invalid"]);

        var accepted = await tool.PrepareWorkflowEffectAsync(
            Invocation(input: converted),
            PreparedAt,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        var invalidToken = await tool.PrepareWorkflowEffectAsync(
            Invocation(conversationId: "conversation\ninvalid"),
            PreparedAt,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.True(accepted.Success);
        Assert.False(invalidToken.Success);
        Assert.Equal("mail.send.context_required", invalidToken.ErrorCode);
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task BindingDriftAfterApprovalStopsBeforeJournalAndNetwork()
    {
        var resolver = new FakeResolver
        {
            ExecutionBinding = Binding(executionFingerprint: Hash('f'), credential: Token),
        };
        var outbox = new MemoryOutbox();
        var handler = new ScriptedHandler(OperationResponseQueued);
        var effect = await PrepareAsync(Tool(resolver, outbox, handler));

        var execution = await effect.ExecuteOnceAsync(PreparedAt.AddSeconds(1), CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.NotStarted, execution.Disposition);
        Assert.Equal("mail.gateway.binding_mismatch", execution.Result.ErrorCode);
        Assert.Empty(outbox.Operations);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StageAndCasFailuresBothGuaranteeZeroPost()
    {
        var stageHandler = new ScriptedHandler(OperationResponseQueued);
        var stageOutbox = new MemoryOutbox { ThrowOnCreate = true };
        var stageEffect = await PrepareAsync(Tool(new FakeResolver(), stageOutbox, stageHandler));

        var stage = await stageEffect.ExecuteOnceAsync(PreparedAt.AddSeconds(1), CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.NotStarted, stage.Disposition);
        Assert.Equal("mail.send.outbox_unavailable", stage.Result.ErrorCode);
        Assert.Equal(0, stageHandler.PostCount);

        var casHandler = new ScriptedHandler(OperationResponseQueued);
        var casOutbox = new MemoryOutbox { FailNextTransition = true };
        var casEffect = await PrepareAsync(Tool(new FakeResolver(), casOutbox, casHandler));

        var cas = await casEffect.ExecuteOnceAsync(PreparedAt.AddSeconds(1), CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.NotStarted, cas.Disposition);
        Assert.Equal("mail.send.outbox_unavailable", cas.Result.ErrorCode);
        Assert.Equal(0, casHandler.PostCount);
        Assert.Equal(MailOutboxSubmissionState.Staged, Assert.Single(casOutbox.Operations).SubmissionState);
    }

    [Fact]
    public async Task ReturnedStageWithDifferentImmutableMetadataCannotReachPost()
    {
        var handler = new ScriptedHandler(OperationResponseQueued);
        var outbox = new MemoryOutbox { ReturnMismatchedCreateMetadata = true };
        var effect = await PrepareAsync(Tool(new FakeResolver(), outbox, handler));

        var execution = await effect.ExecuteOnceAsync(PreparedAt.AddSeconds(1), CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.NotStarted, execution.Disposition);
        Assert.Equal("mail.send.outbox_unavailable", execution.Result.ErrorCode);
        Assert.Equal(0, handler.PostCount);
    }

    [Fact]
    public async Task DeliveredEvidenceFallsBackToQueuedWhenFinalJournalPromotionLosesCas()
    {
        var handler = new ScriptedHandler(request => OperationResponse(request, "delivered"));
        var outbox = new MemoryOutbox { FailTransitionAtCall = 3 };
        var effect = await PrepareAsync(Tool(new FakeResolver(), outbox, handler));

        var execution = await effect.ExecuteOnceAsync(PreparedAt.AddSeconds(1), CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.Succeeded, execution.Disposition);
        Assert.Equal("queued", execution.Result.Output["status"]);
        Assert.Equal(MailOutboxDeliveryState.Queued, Assert.Single(outbox.Operations).DeliveryState);
        Assert.Equal(1, handler.PostCount);
    }

    [Fact]
    public async Task ExactDuplicateCoalescesAndIdentityConflictNeverPostsAgain()
    {
        var handler = new ScriptedHandler(OperationResponseQueued);
        var outbox = new MemoryOutbox();
        var tool = Tool(new FakeResolver(), outbox, handler);
        var firstEffect = await PrepareAsync(tool);
        var first = await firstEffect.ExecuteOnceAsync(PreparedAt.AddSeconds(1), CancellationToken.None);

        var duplicateEffect = await PrepareAsync(tool);
        var duplicate = await duplicateEffect.ExecuteOnceAsync(PreparedAt.AddSeconds(2), CancellationToken.None);

        var changedInput = Input(subject: "Changed subject", body: "Changed body");
        var conflictEffect = await PrepareAsync(tool, Invocation(input: changedInput));
        var conflict = await conflictEffect.ExecuteOnceAsync(PreparedAt.AddSeconds(3), CancellationToken.None);

        Assert.True(first.Result.Success);
        Assert.True(duplicate.Result.Success);
        Assert.Equal(first.Result.Output["operationId"], duplicate.Result.Output["operationId"]);
        Assert.Equal(WorkflowEffectDisposition.NotStarted, conflict.Disposition);
        Assert.Equal("mail.send.outbox_conflict", conflict.Result.ErrorCode);
        Assert.Equal(1, handler.PostCount);
        Assert.Single(outbox.Operations);
    }

    [Fact]
    public async Task AmbiguousPostGetsOneLookupAndLaterApprovalReconcilesWithoutRetryingPost()
    {
        var getAttempt = 0;
        var handler = new ScriptedHandler(request =>
        {
            if (request.Method == HttpMethod.Post) throw new HttpRequestException("ambiguous");
            getAttempt++;
            if (getAttempt == 1) throw new HttpRequestException("still ambiguous");
            return OperationResponse(request, "queued");
        });
        var outbox = new MemoryOutbox();
        var tool = Tool(new FakeResolver(), outbox, handler);
        var firstEffect = await PrepareAsync(tool);

        var first = await firstEffect.ExecuteOnceAsync(PreparedAt.AddSeconds(1), CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.Unknown, first.Disposition);
        Assert.Equal("mail.send.outcome_unknown", first.Result.ErrorCode);
        Assert.Equal(1, handler.PostCount);
        Assert.Equal(1, handler.GetCount);
        Assert.Equal(MailOutboxSubmissionState.OutcomeUnknown, Assert.Single(outbox.Operations).SubmissionState);

        var secondInvocation = Invocation(toolCallId: "mail-step-new-approval");
        var secondEffect = await PrepareAsync(tool, secondInvocation);
        var second = await secondEffect.ExecuteOnceAsync(PreparedAt.AddSeconds(2), CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.Succeeded, second.Disposition);
        Assert.Empty(first.Result.Output);
        Assert.Equal("queued", second.Result.Output["status"]);
        Assert.Equal(1, handler.PostCount);
        Assert.Equal(2, handler.GetCount);
        Assert.Equal(MailOutboxSubmissionState.Accepted, Assert.Single(outbox.Operations).SubmissionState);
    }

    [Fact]
    public async Task AcceptedPostWithLostJournalTransitionIsReconciledFromLiveSubmittingWithoutAnotherPost()
    {
        var handler = new ScriptedHandler(OperationResponseQueued);
        var outbox = new MemoryOutbox { FailTransitionAtCall = 2 };
        var tool = Tool(new FakeResolver(), outbox, handler);
        var firstEffect = await PrepareAsync(tool);

        var first = await firstEffect.ExecuteOnceAsync(PreparedAt.AddSeconds(1), CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.Unknown, first.Disposition);
        Assert.Equal("mail.send.outcome_unknown", first.Result.ErrorCode);
        Assert.Equal(1, handler.PostCount);
        Assert.Equal(0, handler.GetCount);
        var liveSubmitting = Assert.Single(outbox.Operations);
        Assert.Equal(MailOutboxSubmissionState.Submitting, liveSubmitting.SubmissionState);
        Assert.Equal(MailOutboxDeliveryState.Unknown, liveSubmitting.DeliveryState);

        var secondEffect = await PrepareAsync(
            tool,
            Invocation(toolCallId: "mail-step-live-submitting-reconciliation"));
        var second = await secondEffect.ExecuteOnceAsync(
            PreparedAt.AddSeconds(2),
            CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.Succeeded, second.Disposition);
        Assert.Equal("queued", second.Result.Output["status"]);
        Assert.Equal(liveSubmitting.OperationId.Value, second.Result.Output["operationId"]);
        Assert.Equal(1, handler.PostCount);
        Assert.Equal(1, handler.GetCount);
        var accepted = Assert.Single(outbox.Operations);
        Assert.Equal(MailOutboxSubmissionState.Accepted, accepted.SubmissionState);
        Assert.Equal(MailOutboxDeliveryState.Queued, accepted.DeliveryState);
        Assert.Equal(1, accepted.AttemptCount);
    }

    [Theory]
    [InlineData(230, 241, 0)]
    [InlineData(310, 230, 0)]
    [InlineData(100, 110, 310)]
    public async Task NotFoundRequiresFreshOrderedUtcObservationInsideTheSafetyWindow(
        int executionStartedAfterSeconds,
        int lookupObservedAfterSeconds,
        int operationUpdatedAfterSeconds)
    {
        var handler = new ScriptedHandler(request => OperationResponse(request, "not_found"));
        var outbox = new MemoryOutbox();
        outbox.SeedOutcomeUnknown(
            CreateRequest(
                "old-step-outside-authoritative-window",
                PreparedAt),
            seed: 1,
            updatedAtUtc: PreparedAt.AddSeconds(operationUpdatedAfterSeconds));
        var before = Assert.Single(outbox.Operations);
        var effect = await PrepareAsync(
            Tool(
                new FakeResolver(),
                outbox,
                handler,
                new FixedTimeProvider(PreparedAt.AddSeconds(lookupObservedAfterSeconds))),
            lifetime: TimeSpan.FromMinutes(10));

        var execution = await effect.ExecuteOnceAsync(
            PreparedAt.AddSeconds(executionStartedAfterSeconds),
            CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.NotStarted, execution.Disposition);
        Assert.Equal("mail.send.reconciliation_required", execution.Result.ErrorCode);
        Assert.Equal(0, handler.PostCount);
        Assert.Equal(1, handler.GetCount);
        var unchanged = Assert.Single(outbox.Operations);
        Assert.Equal(before.OperationId, unchanged.OperationId);
        Assert.Equal(before.Revision, unchanged.Revision);
        Assert.Equal(MailOutboxSubmissionState.OutcomeUnknown, unchanged.SubmissionState);
        Assert.Equal(MailOutboxDeliveryState.Unknown, unchanged.DeliveryState);
        Assert.Equal(1, unchanged.AttemptCount);
    }

    [Fact]
    public async Task ManyUnknownRowsReconcileOneAndFailClosedBeforeStartingNewPost()
    {
        var handler = new ScriptedHandler(request => OperationResponse(request, "not_found"));
        var outbox = new MemoryOutbox();
        for (var index = 0; index < 100; index++)
            outbox.SeedOutcomeUnknown(CreateRequest($"old-step-{index}"), index + 1);
        var effect = await PrepareAsync(Tool(new FakeResolver(), outbox, handler));

        var execution = await effect.ExecuteOnceAsync(PreparedAt.AddSeconds(1), CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.NotStarted, execution.Disposition);
        Assert.Equal("mail.send.reconciliation_required", execution.Result.ErrorCode);
        Assert.Equal(0, handler.PostCount);
        Assert.Equal(1, handler.GetCount);
        Assert.Equal(100, outbox.Operations.Count);
        Assert.Single(outbox.Operations, operation =>
            operation.SubmissionState == MailOutboxSubmissionState.FailedKnown);
        Assert.Equal(
            99,
            outbox.Operations.Count(operation =>
                operation.SubmissionState == MailOutboxSubmissionState.OutcomeUnknown));
    }

    [Fact]
    public async Task TwoUnknownRowsConsumeOnlyOneLookupAndBlockTheNewPost()
    {
        var handler = new ScriptedHandler(request => OperationResponse(request, "not_found"));
        var outbox = new MemoryOutbox();
        outbox.SeedOutcomeUnknown(CreateRequest("old-step-1"), 1);
        outbox.SeedOutcomeUnknown(CreateRequest("old-step-2"), 2);
        var effect = await PrepareAsync(Tool(new FakeResolver(), outbox, handler));

        var execution = await effect.ExecuteOnceAsync(PreparedAt.AddSeconds(1), CancellationToken.None);

        Assert.Equal(WorkflowEffectDisposition.NotStarted, execution.Disposition);
        Assert.Equal("mail.send.reconciliation_required", execution.Result.ErrorCode);
        Assert.Equal(1, handler.GetCount);
        Assert.Equal(0, handler.PostCount);
        Assert.Single(outbox.Operations, operation =>
            operation.SubmissionState == MailOutboxSubmissionState.FailedKnown);
        Assert.Single(outbox.Operations, operation =>
            operation.SubmissionState == MailOutboxSubmissionState.OutcomeUnknown);
    }

    [Fact]
    public async Task CancellationAfterStagePersistsCancelledNotStartedAndSendsNothing()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new ScriptedHandler(OperationResponseQueued);
        var outbox = new MemoryOutbox { CancelAfterCreate = cancellation };
        var effect = await PrepareAsync(Tool(new FakeResolver(), outbox, handler));

        var execution = await effect.ExecuteOnceAsync(PreparedAt.AddSeconds(1), cancellation.Token);

        Assert.Equal(WorkflowEffectDisposition.NotStarted, execution.Disposition);
        Assert.Equal("mail.send.cancelled", execution.Result.ErrorCode);
        Assert.Empty(handler.Requests);
        var operation = Assert.Single(outbox.Operations);
        Assert.Equal(MailOutboxSubmissionState.CancelledNotStarted, operation.SubmissionState);
        Assert.Equal(0, operation.AttemptCount);
    }

    [Fact]
    public async Task CompletionAndDisposeReleaseInternalRawDraftAndPromptReferences()
    {
        var handler = new ScriptedHandler(OperationResponseQueued);
        var outbox = new MemoryOutbox();
        var completed = await PrepareAsync(Tool(new FakeResolver(), outbox, handler));

        var execution = await completed.ExecuteOnceAsync(PreparedAt.AddSeconds(1), CancellationToken.None);

        Assert.True(execution.Result.Success);
        Assert.DoesNotContain(RawCanary, completed.ApprovalPrompt.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(RawCanary, completed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(RawCanary, Flatten(execution.Result), StringComparison.Ordinal);
        Assert.DoesNotContain(RawCanary, string.Join("|", outbox.Operations), StringComparison.Ordinal);
        Assert.Null(completed.GetType().GetField(
            "draft",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(completed));

        var disposed = await PrepareAsync(Tool(new FakeResolver(), new MemoryOutbox(), handler));
        await disposed.DisposeAsync();

        Assert.DoesNotContain(RawCanary, disposed.ApprovalPrompt.Details, StringComparison.Ordinal);
        Assert.Null(disposed.GetType().GetField(
            "draft",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(disposed));
        var afterDispose = await disposed.ExecuteOnceAsync(PreparedAt.AddSeconds(1), CancellationToken.None);
        Assert.Equal(WorkflowEffectDisposition.NotStarted, afterDispose.Disposition);
        Assert.Equal("mail.send.already_consumed", afterDispose.Result.ErrorCode);
    }

    private static MailSendTool Tool(
        FakeResolver resolver,
        MemoryOutbox outbox,
        ScriptedHandler handler,
        TimeProvider? timeProvider = null) =>
        new(
            resolver,
            outbox,
            new MailGatewayWireClient(handler),
            timeProvider ?? new FixedTimeProvider(PreparedAt.AddSeconds(1)));

    private static async Task<IPreparedWorkflowEffect> PrepareAsync(
        MailSendTool tool,
        ToolInvokeParams? invocation = null,
        TimeSpan? lifetime = null)
    {
        var preparation = await tool.PrepareWorkflowEffectAsync(
            invocation ?? Invocation(),
            PreparedAt,
            lifetime ?? TimeSpan.FromMinutes(2),
            CancellationToken.None);
        Assert.True(preparation.Success, preparation.Error);
        return Assert.IsAssignableFrom<IPreparedWorkflowEffect>(preparation.Effect);
    }

    private static ToolInvokeParams Invocation(
        string toolCallId = "mail-step-1",
        IReadOnlyDictionary<string, object?>? input = null,
        string conversationId = "conversation-1") =>
        new(
            toolCallId,
            conversationId,
            "workflow-run-1",
            "mail.send.v1",
            "R3",
            input ?? Input(),
            Context());

    private static Dictionary<string, object?> Input(
        string subject = $"Subject {RawCanary}",
        string body = $"Body {RawCanary}")
    {
        var draft = ValidDraft(subject, body);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["connectionSlot"] = "primary-mail",
            ["draftHash"] = draft.CanonicalHash,
            ["to"] = new[] { "to@example.invalid" },
            ["cc"] = new[] { "cc@example.invalid" },
            ["bcc"] = new[] { "bcc@example.invalid" },
            ["subject"] = subject,
            ["body"] = body,
        };
    }

    private static MailDraft Draft() =>
        ValidDraft($"Subject {RawCanary}", $"Body {RawCanary}");

    private static MailDraft ValidDraft(string subject, string body) =>
        Assert.IsType<MailDraft>(MailDraftPolicy.Validate(new MailDraftInput(
            ["to@example.invalid"],
            ["cc@example.invalid"],
            ["bcc@example.invalid"],
            subject,
            body)).Draft);

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
            AggregateHash);
    }

    private static MailGatewayRuntimeBinding Binding(
        string? executionFingerprint = null,
        string? credential = null) =>
        new(
            "메일 연결",
            new Uri("https://mail.example.invalid/", UriKind.Absolute),
            "sender@example.invalid",
            AggregateHash,
            LocalBindingHash,
            executionFingerprint ?? ExecutionFingerprint,
            credential is null ? null : new MailGatewayCredential(credential));

    private static MailOutboxCreateRequest CreateRequest(
        string toolCallId,
        DateTimeOffset? createdAtUtc = null) =>
        new(
            "conversation-old",
            "workflow-run-old",
            toolCallId,
            Context().CapabilityId,
            Context().Version,
            MailOutboxHash.Parse(ContentHash),
            7,
            MailOutboxHash.Parse(SnapshotHash),
            "primary-mail",
            MailOutboxHash.Parse(LocalBindingHash),
            MailOutboxHash.Parse(Draft().CanonicalHash),
            createdAtUtc ?? PreparedAt);

    private static HttpResponseMessage OperationResponseQueued(RecordedRequest request) =>
        OperationResponse(request, "queued");

    private static HttpResponseMessage OperationResponse(RecordedRequest request, string status)
    {
        string operationId;
        string? draftHash = null;
        if (request.Method == HttpMethod.Post)
        {
            using var document = JsonDocument.Parse(request.Body!);
            operationId = document.RootElement.GetProperty("operationId").GetString()!;
            draftHash = document.RootElement.GetProperty("draftHash").GetString()!;
        }
        else
        {
            operationId = request.RequestUri.Segments[^1];
            draftHash = Draft().CanonicalHash;
        }
        var json = status == "not_found"
            ? $"{{\"contract\":\"ligclaw-mail-gateway-v1\",\"operationId\":\"{operationId}\",\"status\":\"not_found\"}}"
            : $"{{\"contract\":\"ligclaw-mail-gateway-v1\",\"operationId\":\"{operationId}\",\"status\":\"{status}\",\"draftHash\":\"{draftHash}\"}}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static string Flatten(WindowsToolExecutionResult result) =>
        string.Join("|", result.Output.Select(item => $"{item.Key}:{item.Value}")) +
        $"|{result.Error}|{result.ActivitySummary}|{result.ErrorCode}";

    private const string SnapshotHash =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ContentHash =
        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string AggregateHash =
        "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string LocalBindingHash =
        "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string ExecutionFingerprint =
        "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    private sealed class FakeResolver : IMailGatewayExecutionResolver
    {
        public MailGatewayRuntimeBinding PreparationBinding { get; set; } = Binding();
        public MailGatewayRuntimeBinding ExecutionBinding { get; set; } = Binding(credential: Token);
        public int CallCount { get; private set; }
        public List<MailGatewayResolutionPurpose> Purposes { get; } = [];

        public Task<MailGatewayExecutionResolution> ResolveAsync(
            CapabilityInvocationContext capabilityContext,
            string connectionSlot,
            MailGatewayResolutionPurpose purpose,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Purposes.Add(purpose);
            return Task.FromResult(new MailGatewayExecutionResolution(
                true,
                purpose == MailGatewayResolutionPurpose.Preparation
                    ? PreparationBinding
                    : ExecutionBinding,
                ExecutionLease: purpose == MailGatewayResolutionPurpose.Execution
                    ? new NoopLease()
                    : null));
        }
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri RequestUri, string? Body);

    private sealed class ScriptedHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];
        public int PostCount => Requests.Count(request => request.Method == HttpMethod.Post);
        public int GetCount => Requests.Count(request => request.Method == HttpMethod.Get);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest(request.Method, request.RequestUri!, body);
            Requests.Add(recorded);
            return respond(recorded);
        }
    }

    private sealed class MemoryOutbox : IMailOutboxRepository
    {
        private readonly Dictionary<string, MailOutboxOperation> byToolCall = new(StringComparer.Ordinal);
        private int identity;

        public bool ThrowOnCreate { get; set; }
        public bool FailNextTransition { get; set; }
        public int? FailTransitionAtCall { get; set; }
        public bool ReturnMismatchedCreateMetadata { get; set; }
        public CancellationTokenSource? CancelAfterCreate { get; set; }
        private int transitionCalls;
        public IReadOnlyList<MailOutboxOperation> Operations =>
            byToolCall.Values.OrderBy(operation => operation.OperationId.Value, StringComparer.Ordinal).ToArray();

        public Task<MailOutboxCreateResult> CreateOrGetAsync(
            MailOutboxCreateRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnCreate) throw new InvalidOperationException("stage failure");
            if (byToolCall.TryGetValue(request.ToolCallId, out var existing))
                return Task.FromResult(IdentityMatches(existing, request)
                    ? new MailOutboxCreateResult(MailOutboxCreateOutcome.Existing, existing)
                    : new MailOutboxCreateResult(
                        MailOutboxCreateOutcome.Conflict,
                        existing,
                        MailOutboxPolicy.IdentityConflict));

            var next = ++identity;
            var operation = MailOutboxOperation.CreateStaged(
                MailOutboxOperationId.Parse($"mgo_{next:x64}"),
                MailOutboxIdempotencyKey.Parse($"mik_{next:x64}"),
                request);
            byToolCall.Add(request.ToolCallId, operation);
            CancelAfterCreate?.Cancel();
            if (ReturnMismatchedCreateMetadata)
            {
                var mismatchedRequest = new MailOutboxCreateRequest(
                    "different-conversation",
                    request.WorkflowRunId,
                    request.ToolCallId,
                    request.CapabilityId,
                    request.CapabilityVersion,
                    request.CapabilityContentHash,
                    request.CatalogRevision,
                    request.SnapshotHash,
                    request.ConnectionSlot,
                    request.BindingHash,
                    request.DraftHash,
                    request.CreatedAtUtc);
                operation = MailOutboxOperation.CreateStaged(
                    MailOutboxOperationId.Parse($"mgo_{++identity:x64}"),
                    MailOutboxIdempotencyKey.Parse($"mik_{identity:x64}"),
                    mismatchedRequest);
            }
            return Task.FromResult(new MailOutboxCreateResult(
                MailOutboxCreateOutcome.Created,
                operation));
        }

        public Task<MailOutboxOperation?> GetAsync(
            MailOutboxOperationId operationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<MailOutboxOperation?>(byToolCall.Values.SingleOrDefault(
                operation => operation.OperationId.Equals(operationId)));
        }

        public Task<MailOutboxTransitionResult> TryTransitionAsync(
            MailOutboxTransitionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            transitionCalls++;
            var current = byToolCall.Values.SingleOrDefault(
                operation => operation.OperationId.Equals(request.OperationId));
            if (current is null)
                return Task.FromResult(new MailOutboxTransitionResult(
                    MailOutboxTransitionOutcome.NotFound,
                    ErrorCode: MailOutboxPolicy.NotFound));
            if (FailNextTransition || FailTransitionAtCall == transitionCalls)
            {
                FailNextTransition = false;
                return Task.FromResult(new MailOutboxTransitionResult(
                    MailOutboxTransitionOutcome.Stale,
                    current,
                    MailOutboxPolicy.TransitionStale));
            }
            if (!MailOutboxPolicy.TryApplyTransition(current, request, out var updated, out var error) ||
                updated is null)
                return Task.FromResult(new MailOutboxTransitionResult(
                    StringComparer.Ordinal.Equals(error, MailOutboxPolicy.TransitionStale)
                        ? MailOutboxTransitionOutcome.Stale
                        : MailOutboxTransitionOutcome.Invalid,
                    current,
                    error));
            byToolCall[current.ToolCallId] = updated;
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
            IReadOnlyList<MailOutboxOperation> result = byToolCall.Values
                .Where(operation =>
                       operation.TransportKind == MailOutboxTransportKind.MailApi &&
                       (operation.SubmissionState is
                           MailOutboxSubmissionState.Submitting or
                           MailOutboxSubmissionState.OutcomeUnknown) &&
                                    operation.DraftHash.Equals(draftHash) &&
                                    operation.BindingHash.Equals(bindingHash))
                .OrderByDescending(operation => operation.UpdatedAtUtc)
                .ThenBy(operation => operation.OperationId.Value, StringComparer.Ordinal)
                .Take(limit)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<MailOutboxOperation>> ListSmtpNetworkStartedUnresolvedAsync(
            MailOutboxHash draftHash,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<MailOutboxOperation> result = byToolCall.Values
                .Where(operation =>
                    operation.TransportKind == MailOutboxTransportKind.Smtp &&
                    (operation.SubmissionState is
                        MailOutboxSubmissionState.Submitting or
                        MailOutboxSubmissionState.OutcomeUnknown) &&
                    operation.DraftHash.Equals(draftHash))
                .OrderByDescending(operation => operation.UpdatedAtUtc)
                .ThenBy(operation => operation.OperationId.Value, StringComparer.Ordinal)
                .Take(limit)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task<MailOutboxStartupRecoveryResult> RecoverOnStartupAsync(
            DateTimeOffset recoveredAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(new MailOutboxStartupRecoveryResult(0, 0));

        public void SeedOutcomeUnknown(
            MailOutboxCreateRequest request,
            int seed,
            DateTimeOffset? updatedAtUtc = null)
        {
            identity = Math.Max(identity, seed);
            var transitionAtUtc = updatedAtUtc ?? PreparedAt;
            var staged = MailOutboxOperation.CreateStaged(
                MailOutboxOperationId.Parse($"mgo_{seed:x64}"),
                MailOutboxIdempotencyKey.Parse($"mik_{seed:x64}"),
                request);
            Assert.True(MailOutboxPolicy.TryApplyTransition(
                staged,
                new MailOutboxTransitionRequest(
                    staged.OperationId,
                    staged.Revision,
                    staged.SubmissionState,
                    staged.DeliveryState,
                    MailOutboxSubmissionState.Submitting,
                    MailOutboxDeliveryState.Unknown,
                    1,
                    null,
                    transitionAtUtc),
                out var submitting,
                out _));
            Assert.True(MailOutboxPolicy.TryApplyTransition(
                submitting!,
                new MailOutboxTransitionRequest(
                    submitting!.OperationId,
                    submitting.Revision,
                    submitting.SubmissionState,
                    submitting.DeliveryState,
                    MailOutboxSubmissionState.OutcomeUnknown,
                    MailOutboxDeliveryState.Unknown,
                    1,
                    "mail.gateway.submit.outcome_unknown",
                    transitionAtUtc),
                out var unknown,
                out _));
            byToolCall.Add(request.ToolCallId, unknown!);
        }

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
            operation.DraftHash.Equals(request.DraftHash);
    }
}
