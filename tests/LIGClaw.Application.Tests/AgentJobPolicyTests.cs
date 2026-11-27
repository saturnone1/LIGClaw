using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class AgentJobPolicyTests
{
    [Fact]
    public void ValidatesBoundedReadOnlyAgentJobConfiguration()
    {
        var draft = Draft();

        Assert.True(AgentJobPolicy.IsValid(draft));
        Assert.False(AgentJobPolicy.IsValid(draft with { Prompt = new string('x', 8_001) }));
        Assert.False(AgentJobPolicy.IsValid(draft with { MaxRuntimeSeconds = 3_601 }));
        Assert.False(AgentJobPolicy.IsValid(draft with { MaxAttempts = 6 }));
        Assert.False(AgentJobPolicy.IsValid(draft with { ResultMaxCharacters = 999 }));
    }

    [Fact]
    public void ComputesRecurringOccurrenceUsingWindowsTimeZoneRules()
    {
        var draft = Draft() with
        {
            StartLocal = new DateTime(2026, 7, 27, 9, 0, 0, DateTimeKind.Unspecified),
            Recurrence = ScheduleValues.Weekly,
        };
        var first = AgentJobPolicy.FirstOccurrenceUtc(draft);
        var job = new ScheduledAgentJob(
            "id", draft.Title, draft.Prompt, draft.StartLocal, draft.TimeZoneId, draft.Recurrence,
            draft.Interval, draft.MisfirePolicy, null, draft.MaxRuntimeSeconds, draft.MaxAttempts,
            draft.ResultMaxCharacters, draft.Source, ScheduleValues.Pending, 0, first,
            first.AddDays(-1), first.AddDays(-1));

        Assert.Equal(DateTimeOffset.Parse("2026-07-27T00:00:00Z"), first);
        Assert.Equal(DateTimeOffset.Parse("2026-08-03T00:00:00Z"),
            AgentJobPolicy.NextOccurrenceAfter(job, first));
    }

    private static AgentJobDraft Draft() => new(
        "주간 요약", "승인된 문서를 읽고 요약해 줘",
        new DateTime(2026, 7, 27, 9, 0, 0, DateTimeKind.Unspecified),
        "Korea Standard Time", ScheduleValues.Once, 1, ScheduleValues.RunOnceOnResume,
        null, 300, 3, 10_000, "test");
}
