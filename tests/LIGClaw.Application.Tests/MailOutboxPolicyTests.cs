using LIGClaw.Application.Mail;

namespace LIGClaw.Application.Tests;

public sealed class MailOutboxPolicyTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.Parse("2026-08-02T01:00:00Z");

    [Fact]
    public void ParsesOnlyExactOpaqueIdentitiesAndCanonicalSha256Hashes()
    {
        var operationValue = $"mgo_{new string('a', 64)}";
        var idempotencyValue = $"mik_{new string('b', 64)}";

        var operationId = MailOutboxOperationId.Parse(operationValue);
        var idempotencyKey = MailOutboxIdempotencyKey.Parse(idempotencyValue);

        Assert.Equal(68, operationId.Value.Length);
        Assert.Equal(operationValue, operationId.ToString());
        Assert.Equal(68, idempotencyKey.Value.Length);
        Assert.Equal("[mail-outbox-idempotency-key]", idempotencyKey.ToString());
        Assert.False(MailOutboxOperationId.TryParse($"mgo_{new string('A', 64)}", out _));
        Assert.False(MailOutboxOperationId.TryParse($"mgo_{new string('a', 63)}", out _));
        Assert.False(MailOutboxIdempotencyKey.TryParse($"mgo_{new string('b', 64)}", out _));
        Assert.True(MailOutboxHash.TryParse(Hash('c'), out _));
        Assert.False(MailOutboxHash.TryParse($"sha256:{new string('C', 64)}", out _));
        Assert.False(MailOutboxHash.TryParse(new string('c', 64), out _));
    }

    [Fact]
    public void AppliesOnlyTheNarrowSubmissionAndDeliveryTransitions()
    {
        var staged = Operation();
        var submitting = Apply(
            staged,
            MailOutboxSubmissionState.Submitting,
            MailOutboxDeliveryState.Unknown,
            1,
            null,
            1);
        var accepted = Apply(
            submitting,
            MailOutboxSubmissionState.Accepted,
            MailOutboxDeliveryState.Queued,
            1,
            null,
            2);
        var delivered = Apply(
            accepted,
            MailOutboxSubmissionState.Accepted,
            MailOutboxDeliveryState.Delivered,
            1,
            null,
            3);

        Assert.Equal(MailOutboxSubmissionState.Accepted, delivered.SubmissionState);
        Assert.Equal(MailOutboxDeliveryState.Delivered, delivered.DeliveryState);
        Assert.Equal(3, delivered.Revision);

        var cancelled = Apply(
            Operation(),
            MailOutboxSubmissionState.CancelledNotStarted,
            MailOutboxDeliveryState.Unknown,
            0,
            "mail.outbox.cancelled",
            1);
        var failedKnown = Apply(
            Apply(
                Operation(),
                MailOutboxSubmissionState.Submitting,
                MailOutboxDeliveryState.Unknown,
                1,
                null,
                1),
            MailOutboxSubmissionState.FailedKnown,
            MailOutboxDeliveryState.Failed,
            1,
            "mail.outbox.rejected",
            2);

        Assert.Equal(MailOutboxSubmissionState.CancelledNotStarted, cancelled.SubmissionState);
        Assert.Equal(MailOutboxSubmissionState.FailedKnown, failedKnown.SubmissionState);
    }

    [Fact]
    public void ReconcilesOutcomeUnknownBeforeDeliveryAndAllowsOnlyOneSameKeyRetry()
    {
        var submitting = Apply(
            Operation(),
            MailOutboxSubmissionState.Submitting,
            MailOutboxDeliveryState.Unknown,
            1,
            null,
            1);
        var outcomeUnknown = Apply(
            submitting,
            MailOutboxSubmissionState.OutcomeUnknown,
            MailOutboxDeliveryState.Unknown,
            1,
            "mail.outbox.timeout",
            2);

        var queued = Apply(
            outcomeUnknown,
            MailOutboxSubmissionState.Accepted,
            MailOutboxDeliveryState.Queued,
            1,
            null,
            3);
        var delivered = Apply(
            queued,
            MailOutboxSubmissionState.Accepted,
            MailOutboxDeliveryState.Delivered,
            1,
            null,
            4);
        Assert.Equal(MailOutboxDeliveryState.Delivered, delivered.DeliveryState);

        var directDelivered = Transition(
            outcomeUnknown,
            MailOutboxSubmissionState.Accepted,
            MailOutboxDeliveryState.Delivered,
            1,
            null,
            3);
        Assert.False(MailOutboxPolicy.TryApplyTransition(
            outcomeUnknown, directDelivered, out _, out var directDeliveryError));
        Assert.Equal(MailOutboxPolicy.TransitionInvalid, directDeliveryError);

        var retrying = Apply(
            outcomeUnknown,
            MailOutboxSubmissionState.Submitting,
            MailOutboxDeliveryState.Unknown,
            2,
            null,
            5);
        var secondUnknown = Apply(
            retrying,
            MailOutboxSubmissionState.OutcomeUnknown,
            MailOutboxDeliveryState.Unknown,
            2,
            "mail.outbox.timeout",
            6);
        var thirdAttempt = Transition(
            secondUnknown,
            MailOutboxSubmissionState.Submitting,
            MailOutboxDeliveryState.Unknown,
            3,
            null,
            7);
        Assert.False(MailOutboxPolicy.TryApplyTransition(
            secondUnknown, thirdAttempt, out _, out var retryError));
        Assert.Equal(MailOutboxPolicy.TransitionInvalid, retryError);

        var failed = Apply(
            outcomeUnknown,
            MailOutboxSubmissionState.FailedKnown,
            MailOutboxDeliveryState.Failed,
            1,
            "mail.outbox.not_found",
            8);
        Assert.Equal(MailOutboxSubmissionState.FailedKnown, failed.SubmissionState);
    }

    [Fact]
    public void SmtpUsesStableMessageIdentityAndNeverRetriesAfterDataStarted()
    {
        var staged = Operation(MailOutboxTransportKind.Smtp);
        Assert.Equal(MailOutboxSmtpPhase.PreData, staged.SmtpPhase);
        Assert.Equal(
            $"<ligclaw.{new string('1', 64)}@ligclaw.invalid>",
            staged.SmtpMessageId);

        var submitting = Apply(
            staged,
            MailOutboxSubmissionState.Submitting,
            MailOutboxDeliveryState.Unknown,
            1,
            null,
            1);
        var dataStarted = Apply(
            submitting,
            MailOutboxSubmissionState.Submitting,
            MailOutboxDeliveryState.Unknown,
            1,
            null,
            2,
            MailOutboxSmtpPhase.DataStarted);
        var unknown = Apply(
            dataStarted,
            MailOutboxSubmissionState.OutcomeUnknown,
            MailOutboxDeliveryState.Unknown,
            1,
            "mail.smtp.outcome_unknown",
            3);

        Assert.Equal(MailOutboxSmtpPhase.DataStarted, unknown.SmtpPhase);
        Assert.False(MailOutboxPolicy.TryApplyTransition(
            unknown,
            Transition(
                unknown,
                MailOutboxSubmissionState.Submitting,
                MailOutboxDeliveryState.Unknown,
                1,
                null,
                4),
            out _,
            out var retryError));
        Assert.Equal(MailOutboxPolicy.TransitionInvalid, retryError);

        var beforeData = Apply(
            Operation(MailOutboxTransportKind.Smtp),
            MailOutboxSubmissionState.Submitting,
            MailOutboxDeliveryState.Unknown,
            1,
            null,
            1);
        Assert.False(MailOutboxPolicy.TryApplyTransition(
            beforeData,
            Transition(
                beforeData,
                MailOutboxSubmissionState.OutcomeUnknown,
                MailOutboxDeliveryState.Unknown,
                1,
                "mail.smtp.outcome_unknown",
                2),
            out _,
            out var preDataError));
        Assert.Equal(MailOutboxPolicy.TransitionInvalid, preDataError);

        var failedKnown = Apply(
            beforeData,
            MailOutboxSubmissionState.FailedKnown,
            MailOutboxDeliveryState.Failed,
            1,
            "mail.smtp.not_submitted",
            3);
        Assert.Equal(MailOutboxSmtpPhase.PreData, failedKnown.SmtpPhase);
    }

    [Fact]
    public void MetadataObjectsExposeNoMessageFieldsOrRawValuesInDiagnostics()
    {
        var operation = Operation();
        var prohibitedNames = new[]
        {
            "recipient", "subject", "body", "sender", "endpoint", "token", "payload", "ciphertext",
        };

        foreach (var type in new[] { typeof(MailOutboxCreateRequest), typeof(MailOutboxOperation) })
        {
            var names = type.GetProperties().Select(property => property.Name).ToArray();
            Assert.DoesNotContain(
                names,
                name => prohibitedNames.Any(prohibited =>
                    name.Contains(prohibited, StringComparison.OrdinalIgnoreCase)));
        }

        var diagnostic = operation.ToString();
        Assert.DoesNotContain(operation.ConversationId, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(operation.WorkflowRunId, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(operation.ToolCallId, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(operation.IdempotencyKey.Value, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(operation.DraftHash.Value, diagnostic, StringComparison.Ordinal);
    }

    private static MailOutboxOperation Operation(
        MailOutboxTransportKind transportKind = MailOutboxTransportKind.MailApi) =>
        MailOutboxOperation.CreateStaged(
            MailOutboxOperationId.Parse($"mgo_{new string('1', 64)}"),
            MailOutboxIdempotencyKey.Parse($"mik_{new string('2', 64)}"),
            new MailOutboxCreateRequest(
                "conversation-canary",
                "workflow-run-canary",
                "tool-call-canary",
                "company.mail-send",
                "1.0.0",
                MailOutboxHash.Parse(Hash('3')),
                7,
                MailOutboxHash.Parse(Hash('4')),
                "mail-primary",
                MailOutboxHash.Parse(Hash('5')),
                MailOutboxHash.Parse(Hash('6')),
                Baseline,
                transportKind));

    private static MailOutboxOperation Apply(
        MailOutboxOperation current,
        MailOutboxSubmissionState submission,
        MailOutboxDeliveryState delivery,
        int attemptCount,
        string? errorCode,
        int seconds,
        MailOutboxSmtpPhase? nextSmtpPhase = null)
    {
        Assert.True(MailOutboxPolicy.TryApplyTransition(
            current,
            Transition(current, submission, delivery, attemptCount, errorCode, seconds, nextSmtpPhase),
            out var updated,
            out var transitionError),
            transitionError);
        return Assert.IsType<MailOutboxOperation>(updated);
    }

    private static MailOutboxTransitionRequest Transition(
        MailOutboxOperation current,
        MailOutboxSubmissionState submission,
        MailOutboxDeliveryState delivery,
        int attemptCount,
        string? errorCode,
        int seconds,
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
            Baseline.AddSeconds(seconds),
            nextSmtpPhase);

    private static string Hash(char digit) => $"sha256:{new string(digit, 64)}";
}
