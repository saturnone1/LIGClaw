using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class RoutineSuggestionPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-02T09:00:00Z");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void SuggestsDailyOnlyAfterThreeDistinctDaysAndNormalizesWhitespace()
    {
        var history = new[]
        {
            Item("current", "오늘 해야 할 일을 정리해 줘", 0),
            Item("second", "  오늘  해야 할 일을\n정리해 줘 ", -1),
            Item("first", "오늘 해야 할 일을 정리해 줘", -2),
        };

        var result = RoutineSuggestionPolicy.Evaluate(Enabled(), "current", history, Now, Utc);

        Assert.NotNull(result);
        Assert.Equal("daily", result.Recurrence);
        Assert.Equal(3, result.OccurrenceCount);
        Assert.Equal(new DateTime(2026, 8, 3, 9, 0, 0), result.SuggestedStartLocal);
    }

    [Fact]
    public void SuggestsWeeklyForAStableWeeklyCadence()
    {
        var history = new[] { Item("current", Prompt, 0), Item("second", Prompt, -7), Item("first", Prompt, -14) };

        var result = RoutineSuggestionPolicy.Evaluate(Enabled(), "current", history, Now, Utc);

        Assert.NotNull(result);
        Assert.Equal("weekly", result.Recurrence);
        Assert.Equal(new DateTime(2026, 8, 9, 9, 0, 0), result.SuggestedStartLocal);
    }

    [Fact]
    public void DoesNotSuggestForSameDayOrIrregularUse()
    {
        var sameDay = new[]
        {
            Item("current", Prompt, 0),
            new CompletedUserInput("second", Prompt, Now.AddHours(-1)),
            new CompletedUserInput("first", Prompt, Now.AddHours(-2)),
        };
        var irregular = new[] { Item("current", Prompt, 0), Item("second", Prompt, -4), Item("first", Prompt, -14) };

        Assert.Null(RoutineSuggestionPolicy.Evaluate(Enabled(), "current", sameDay, Now, Utc));
        Assert.Null(RoutineSuggestionPolicy.Evaluate(Enabled(), "current", irregular, Now, Utc));
    }

    [Theory]
    [InlineData("API key token 값을 확인해 줘")]
    [InlineData("매일 시스템 상태를 확인해 줘")]
    [InlineData("[선택한 화면에서 확인한 글자]\n업무 내용을 정리해 줘")]
    [InlineData("[Explorer에서 선택한 항목]\n- C:\\work")]
    public void ExcludesSensitiveExplicitSchedulingAndAttachedContext(string prompt)
    {
        var history = new[] { Item("current", prompt, 0), Item("second", prompt, -1), Item("first", prompt, -2) };

        Assert.Null(RoutineSuggestionPolicy.Evaluate(Enabled(), "current", history, Now, Utc));
    }

    [Fact]
    public void HonorsOptInIgnoreAndCooldownWithoutStoringPromptInPreferences()
    {
        var history = new[] { Item("current", Prompt, 0), Item("second", Prompt, -1), Item("first", Prompt, -2) };
        var fingerprint = RoutineSuggestionPolicy.Fingerprint(Prompt);

        Assert.Null(RoutineSuggestionPolicy.Evaluate(Enabled(enabled: false), "current", history, Now, Utc));
        Assert.Null(RoutineSuggestionPolicy.Evaluate(
            Enabled(ignored: new HashSet<string> { fingerprint }), "current", history, Now, Utc));
        Assert.Null(RoutineSuggestionPolicy.Evaluate(
            Enabled(offers: new Dictionary<string, DateTimeOffset> { [fingerprint] = Now.AddDays(-13) }),
            "current", history, Now, Utc));
        Assert.NotNull(RoutineSuggestionPolicy.Evaluate(
            Enabled(offers: new Dictionary<string, DateTimeOffset> { [fingerprint] = Now.AddDays(-14) }),
            "current", history, Now, Utc));
    }

    [Fact]
    public void RequiresTheCompletingRunAndBoundsHistory()
    {
        var history = new[] { Item("other", Prompt, 0), Item("second", Prompt, -1), Item("first", Prompt, -2) };
        Assert.Null(RoutineSuggestionPolicy.Evaluate(Enabled(), "current", history, Now, Utc));

        var oversized = Enumerable.Range(0, RoutineSuggestionPolicy.MaximumHistoryItems + 1)
            .Select(index => Item(index.ToString(), Prompt, -index))
            .ToArray();
        Assert.Null(RoutineSuggestionPolicy.Evaluate(Enabled(), "0", oversized, Now, Utc));
    }

    private const string Prompt = "오늘 업무 현황을 요약해 줘";

    private static CompletedUserInput Item(string runId, string prompt, int dayOffset) =>
        new(runId, prompt, Now.AddDays(dayOffset));

    private static RoutineSuggestionPreferences Enabled(
        bool enabled = true,
        IReadOnlySet<string>? ignored = null,
        IReadOnlyDictionary<string, DateTimeOffset>? offers = null) =>
        new(enabled, ignored ?? new HashSet<string>(), offers ?? new Dictionary<string, DateTimeOffset>());
}
