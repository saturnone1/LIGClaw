using System.Security.Cryptography;
using System.Text;
using LIGClaw.Desktop.Infrastructure.Persistence;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed record RoutineSuggestionCandidate(
    string Fingerprint,
    string Prompt,
    string Recurrence,
    DateTime SuggestedStartLocal,
    int OccurrenceCount);

internal static class RoutineSuggestionPolicy
{
    internal const int MaximumHistoryItems = 200;
    internal static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(45);
    internal static readonly TimeSpan OfferCooldown = TimeSpan.FromDays(14);

    private static readonly string[] SensitiveTerms =
    [
        "password", "passcode", "api key", "apikey", "token", "secret", "otp", "nvapi-", "sk-",
        "비밀번호", "암호", "인증번호", "주민등록", "계좌번호", "카드번호",
    ];

    private static readonly string[] SchedulingTerms =
    [
        "예약", "알림", "매일", "매주", "매월", "반복", "schedule", "remind", "every day", "every week",
    ];

    internal static RoutineSuggestionCandidate? Evaluate(
        RoutineSuggestionPreferences preferences,
        string currentRunId,
        IReadOnlyList<CompletedUserInput> history,
        DateTimeOffset nowUtc,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRunId);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        if (!preferences.Enabled || history.Count is < 3 or > MaximumHistoryItems) return null;

        var current = history.FirstOrDefault(item => StringComparer.Ordinal.Equals(item.RunId, currentRunId));
        if (current is null || current.UserInput.Length is < 8 or > 1_000) return null;
        var normalized = Normalize(current.UserInput);
        if (normalized.Length < 8 || ShouldExclude(normalized)) return null;

        var fingerprint = Fingerprint(normalized);
        if (preferences.IgnoredFingerprints.Contains(fingerprint)) return null;
        if (preferences.LastOfferedUtc.TryGetValue(fingerprint, out var lastOffered) &&
            nowUtc - lastOffered < OfferCooldown) return null;

        var occurrences = history
            .Where(item => item.UserInput.Length <= 1_000 && StringComparer.Ordinal.Equals(Normalize(item.UserInput), normalized))
            .OrderBy(item => item.CreatedAtUtc)
            .ToArray();
        var dates = occurrences
            .Select(item => TimeZoneInfo.ConvertTime(item.CreatedAtUtc, localTimeZone).Date)
            .Distinct()
            .Order()
            .ToArray();
        if (occurrences.Length < 3 || dates.Length < 3) return null;

        var recentDates = dates[^3..];
        var gaps = new[]
        {
            (recentDates[1] - recentDates[0]).Days,
            (recentDates[2] - recentDates[1]).Days,
        };
        string recurrence;
        int intervalDays;
        if (gaps.All(gap => gap is >= 1 and <= 3))
        {
            recurrence = "daily";
            intervalDays = 1;
        }
        else if (gaps.All(gap => gap is >= 5 and <= 9))
        {
            recurrence = "weekly";
            intervalDays = 7;
        }
        else
        {
            return null;
        }

        var localNow = TimeZoneInfo.ConvertTime(nowUtc, localTimeZone).DateTime;
        var suggestedStart = localNow.AddDays(intervalDays);
        return new RoutineSuggestionCandidate(
            fingerprint,
            current.UserInput.Trim(),
            recurrence,
            suggestedStart.AddTicks(-(suggestedStart.Ticks % TimeSpan.TicksPerSecond)),
            occurrences.Length);
    }

    internal static string Fingerprint(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(input)))).ToLowerInvariant();

    private static string Normalize(string input)
    {
        var builder = new StringBuilder(input.Length);
        var pendingSpace = false;
        foreach (var character in input.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            builder.Append(char.ToLowerInvariant(character));
            pendingSpace = false;
        }
        return builder.ToString();
    }

    private static bool ShouldExclude(string normalized) =>
        normalized.Contains("[선택한 화면에서 확인한 글자]", StringComparison.Ordinal) ||
        normalized.Contains("[explorer에서 선택한 항목]", StringComparison.Ordinal) ||
        SensitiveTerms.Any(term => normalized.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
        SchedulingTerms.Any(term => normalized.Contains(term, StringComparison.OrdinalIgnoreCase));
}
