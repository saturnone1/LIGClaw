using System.Globalization;
using System.Text.Json;
using LIGClaw.Application.Scheduling;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class ScheduleCreateTool(IScheduleRepository schedules) : IWindowsToolAdapter
{
    public string Name => "schedule.create.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.DurableScheduler;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var draft = Read(input, "preview");
        if (draft is null) return null;
        var next = GetNextRun(draft, DateTimeOffset.UtcNow);
        if (next is null) return null;
        return new WindowsToolApprovalPrompt(
            "이 알림을 예약할까요?",
            "LIGClaw가 이 PC에 로컬 알림 예약을 저장합니다.",
            $"제목: {draft.Title}\n내용: {draft.Message}\n첫 실행: {FormatLocal(next.Value, draft.TimeZoneId)}\n반복: {RecurrenceText(draft)}\n놓친 실행: {MisfireText(draft.MisfirePolicy)}\n\n예약 실행은 생성 승인과 별도로 저장된 알림 내용만 표시합니다.",
            GrantScope: StableGrantScope(input));
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var draft = Read(input, "conversation", allowDesktopSource: true);
        if (draft is null) return Failure("예약 정보나 시간대가 올바르지 않습니다.");
        try
        {
            var job = await schedules.CreateAsync(draft, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            return new WindowsToolExecutionResult(
                true,
                new Dictionary<string, object?>
                {
                    ["jobId"] = job.Id,
                    ["nextRunAtUtc"] = job.NextRunAtUtc!.Value.ToString("O", CultureInfo.InvariantCulture),
                    ["status"] = job.Status,
                },
                ActivitySummary: "로컬 알림 예약 하나를 저장했어요.");
        }
        catch (ArgumentException)
        {
            return Failure("단발 예약은 미래 시각이어야 하며 반복·시간대 설정이 유효해야 합니다.");
        }
    }

    internal static NotificationScheduleDraft? Read(
        IReadOnlyDictionary<string, object?> input,
        string source,
        bool allowDesktopSource = false)
    {
        var allowed = allowDesktopSource
            ? new[] { "title", "message", "startLocal", "timeZoneId", "delayMinutes", "recurrence", "interval", "misfirePolicy", "reason", "_source" }
            : ["title", "message", "startLocal", "timeZoneId", "delayMinutes", "recurrence", "interval", "misfirePolicy", "reason"];
        if (!ToolInputReader.HasOnlyKeys(
                input, allowed) ||
            !ToolInputReader.TryGetRequiredString(input, "title", 63, out var title) ||
            !ToolInputReader.TryGetRequiredStringExact(input, "message", 255, out var message) ||
            !ToolInputReader.TryGetRequiredString(input, "recurrence", 16, out var recurrence) ||
            !ToolInputReader.TryGetRequiredString(input, "misfirePolicy", 32, out var misfire) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out _)) return null;
        DateTime parsedLocal;
        string timeZoneId;
        var delayMinutes = ReadOptionalInteger(input, "delayMinutes");
        if (delayMinutes is >= 1 and <= 10_080 &&
            !input.ContainsKey("startLocal") && !input.ContainsKey("timeZoneId"))
        {
            timeZoneId = TimeZoneInfo.Local.Id;
            var delayed = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddMinutes(delayMinutes.Value), TimeZoneInfo.Local).DateTime;
            parsedLocal = delayed.AddTicks(-(delayed.Ticks % TimeSpan.TicksPerSecond));
        }
        else if (!input.ContainsKey("delayMinutes") &&
                 ToolInputReader.TryGetRequiredString(input, "startLocal", 19, out var startLocal) &&
                 NotificationSchedulePolicy.TryParseLocal(startLocal, out parsedLocal) &&
                 ToolInputReader.TryGetRequiredString(input, "timeZoneId", 128, out timeZoneId))
        {
        }
        else
        {
            return null;
        }
        var interval = ReadOptionalInteger(input, "interval") ?? 1;
        if (interval is < 1 or > 365 || recurrence == ScheduleValues.Once && interval != 1) return null;
        if (allowDesktopSource && input.ContainsKey("_source") &&
            !ToolInputReader.TryGetRequiredString(input, "_source", 160, out source)) return null;
        var draft = new NotificationScheduleDraft(
            title, message, parsedLocal, timeZoneId, recurrence, (int)interval, misfire, source);
        return NotificationSchedulePolicy.IsValid(draft) ? draft : null;
    }

    private static string StableGrantScope(IReadOnlyDictionary<string, object?> input) =>
        JsonSerializer.Serialize(new SortedDictionary<string, object?>(
            input.Where(item => item.Key is not "reason" and not "_source")
                .ToDictionary(item => item.Key, item => item.Value),
            StringComparer.Ordinal));

    private static long? ReadOptionalInteger(IReadOnlyDictionary<string, object?> input, string key)
    {
        if (!input.TryGetValue(key, out var raw)) return null;
        return raw switch
        {
            null => null,
            0 => null,
            int value => value,
            long value when value == 0 => null,
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var value) && value == 0 => null,
            long value => value,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var value) => value,
            _ => -1,
        };
    }

    private static DateTimeOffset? GetNextRun(NotificationScheduleDraft draft, DateTimeOffset nowUtc)
    {
        var first = NotificationSchedulePolicy.FirstOccurrenceUtc(draft);
        if (first > nowUtc) return first;
        var template = new ScheduledNotification(
            "preview", draft.Title, draft.Message, draft.StartLocal, draft.TimeZoneId, draft.Recurrence,
            draft.Interval, draft.MisfirePolicy, draft.Source, ScheduleValues.Pending, first, nowUtc, nowUtc);
        return NotificationSchedulePolicy.NextOccurrenceAfter(template, nowUtc);
    }

    internal static string FormatLocal(DateTimeOffset instant, string timeZoneId)
    {
        _ = NotificationSchedulePolicy.TryFindTimeZone(timeZoneId, out var timeZone);
        return TimeZoneInfo.ConvertTime(instant, timeZone!).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.CurrentCulture) +
               $" ({timeZoneId})";
    }

    private static string RecurrenceText(NotificationScheduleDraft draft) => draft.Recurrence switch
    {
        ScheduleValues.Once => "한 번",
        ScheduleValues.Daily => draft.Interval == 1 ? "매일" : $"{draft.Interval}일마다",
        _ => draft.Interval == 1 ? "매주" : $"{draft.Interval}주마다",
    };

    private static string MisfireText(string policy) => policy switch
    {
        ScheduleValues.Skip => "건너뛰고 다음 회차 유지",
        ScheduleValues.RunOnceOnResume => "앱 재시작 시 한 번 실행",
        _ => "예약 관리에서 사용자에게 확인",
    };

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}

internal sealed class ScheduleListTool(IScheduleRepository schedules) : IWindowsToolAdapter
{
    public string Name => "schedule.list.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.DurableScheduler;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input) =>
        TryRead(input, out var includeInactive, out var reason)
            ? new WindowsToolApprovalPrompt(
                "저장된 예약을 사용할까요?",
                "로컬 알림 예약 정보를 모델에 전달합니다.",
                $"범위: {(includeInactive ? "완료·취소 포함" : "활성 예약")}\n요청 이유: {reason}")
            : null;

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        if (!TryRead(input, out var includeInactive, out _)) return Failure("예약 조회 조건이 올바르지 않습니다.");
        var jobs = await schedules.ListAsync(includeInactive, 0, 101, cancellationToken).ConfigureAwait(false);
        var output = jobs.Take(100).Select(job => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["jobId"] = job.Id,
            ["title"] = job.Title,
            ["message"] = job.Message,
            ["startLocal"] = job.StartLocal.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            ["timeZoneId"] = job.TimeZoneId,
            ["recurrence"] = job.Recurrence,
            ["interval"] = job.Interval,
            ["misfirePolicy"] = job.MisfirePolicy,
            ["status"] = job.Status,
            ["nextRunAtUtc"] = job.NextRunAtUtc?.ToString("O", CultureInfo.InvariantCulture),
        }).ToArray();
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?> { ["jobs"] = output, ["truncated"] = jobs.Count > 100 },
            ActivitySummary: $"로컬 알림 예약 {output.Length}개를 조회했어요.");
    }

    private static bool TryRead(
        IReadOnlyDictionary<string, object?> input,
        out bool includeInactive,
        out string reason)
    {
        includeInactive = false;
        reason = string.Empty;
        if (!ToolInputReader.HasOnlyKeys(input, "includeInactive", "reason") ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out reason)) return false;
        if (!input.TryGetValue("includeInactive", out var raw)) return true;
        includeInactive = raw switch
        {
            bool value => value,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => false,
        };
        return raw is bool or JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False };
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}

internal sealed class ScheduleCancelTool(IScheduleRepository schedules) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly HashSet<string> _prepared = new(StringComparer.Ordinal);

    public string Name => "schedule.cancel.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.DurableScheduler;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        if (!TryRead(input, out var id, out var reason)) return null;
        var job = schedules.GetAsync(id, CancellationToken.None).GetAwaiter().GetResult();
        if (job is null || job.Status is not (ScheduleValues.Pending or ScheduleValues.AwaitingDecision)) return null;
        lock (_sync) _prepared.Add(id);
        return new WindowsToolApprovalPrompt(
            "이 예약을 취소할까요?",
            "저장된 로컬 알림 예약을 취소합니다.",
            $"제목: {job.Title}\n다음 실행: {FormatNextRun(job)}\n요청 이유: {reason}");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        if (!TryRead(input, out var id, out _)) return Failure("취소할 예약 정보가 올바르지 않습니다.");
        lock (_sync)
        {
            if (!_prepared.Remove(id)) return Failure("승인한 예약 취소 정보를 찾을 수 없습니다.");
        }
        var cancelled = await schedules.CancelAsync(id, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        return cancelled
            ? new WindowsToolExecutionResult(
                true, new Dictionary<string, object?> { ["cancelled"] = true },
                ActivitySummary: "로컬 알림 예약 하나를 취소했어요.")
            : Failure("취소할 활성 예약을 찾을 수 없습니다.");
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input)
    {
        if (!TryRead(input, out var id, out _)) return;
        lock (_sync) _prepared.Remove(id);
    }

    private static bool TryRead(IReadOnlyDictionary<string, object?> input, out string id, out string reason)
    {
        id = string.Empty;
        reason = string.Empty;
        return ToolInputReader.HasOnlyKeys(input, "jobId", "reason") &&
               ToolInputReader.TryGetRequiredString(input, "jobId", 32, out id) &&
               id.Length == 32 && id.All(char.IsAsciiHexDigitLower) &&
               ToolInputReader.TryGetRequiredString(input, "reason", 160, out reason);
    }

    private static string FormatNextRun(ScheduledNotification job) => job.NextRunAtUtc is null
        ? "없음"
        : ScheduleCreateTool.FormatLocal(job.NextRunAtUtc.Value, job.TimeZoneId);

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}
