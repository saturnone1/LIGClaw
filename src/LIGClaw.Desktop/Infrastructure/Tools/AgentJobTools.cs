using System.Globalization;
using System.Text.Json;
using LIGClaw.Application.Scheduling;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class AgentJobCreateTool(IAgentJobRepository jobs) : IWindowsToolAdapter
{
    public string Name => "agent_job.create.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.DurableScheduler;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var draft = Read(input, "preview");
        if (draft is null) return null;
        var first = AgentJobPolicy.FirstOccurrenceUtc(draft);
        if (first <= DateTimeOffset.UtcNow && draft.Recurrence == ScheduleValues.Once) return null;
        return new WindowsToolApprovalPrompt(
            "이 Agent 작업을 예약할까요?",
            "LIGClaw가 지정 시각에 모델 요청을 실행하고 결과를 이 PC에 저장합니다.",
            $"제목: {draft.Title}\n지시: {draft.Prompt}\n첫 실행: {ScheduleCreateTool.FormatLocal(first, draft.TimeZoneId)}\n반복: {draft.Recurrence}\n최대 실행: {draft.MaxRuntimeSeconds}초\n최대 시도: {draft.MaxAttempts}회\n\n파일 변경이나 Windows 효과는 실행 시점에 다시 정책과 승인을 통과해야 합니다.",
            GrantScope: StableGrantScope(input));
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var draft = Read(input, "conversation", allowDesktopSource: true);
        if (draft is null) return Failure("Agent 작업 정보나 시간대가 올바르지 않습니다.");
        try
        {
            var job = await jobs.CreateAgentJobAsync(draft, DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            return new WindowsToolExecutionResult(
                true,
                new Dictionary<string, object?>
                {
                    ["jobId"] = job.Id,
                    ["nextRunAtUtc"] = job.NextRunAtUtc!.Value.ToString("O", CultureInfo.InvariantCulture),
                    ["status"] = job.Status,
                },
                ActivitySummary: "백그라운드 Agent 작업 하나를 예약했어요.");
        }
        catch (ArgumentException)
        {
            return Failure("실행 시각과 반복·시간대·제한 설정을 확인해 주세요.");
        }
    }

    internal static AgentJobDraft? Read(
        IReadOnlyDictionary<string, object?> input,
        string source,
        bool allowDesktopSource = false)
    {
        var allowed = new HashSet<string>(
            ["title", "prompt", "startLocal", "timeZoneId", "delayMinutes", "recurrence", "interval",
             "misfirePolicy", "modelProfileId", "maxRuntimeSeconds", "maxAttempts", "resultMaxCharacters", "reason"],
            StringComparer.Ordinal);
        if (allowDesktopSource) allowed.Add("_source");
        if (!ToolInputReader.HasOnlyKeys(input, [.. allowed]) ||
            !ToolInputReader.TryGetRequiredString(input, "title", 80, out var title) ||
            !ToolInputReader.TryGetRequiredStringExact(input, "prompt", 8_000, out var prompt) ||
            !ToolInputReader.TryGetRequiredString(input, "recurrence", 16, out var recurrence) ||
            !ToolInputReader.TryGetRequiredString(input, "misfirePolicy", 32, out var misfire) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out _)) return null;

        DateTime startLocal;
        string timeZoneId;
        var delay = ReadInteger(input, "delayMinutes");
        if (delay is >= 1 and <= 10_080 && !input.ContainsKey("startLocal") && !input.ContainsKey("timeZoneId"))
        {
            timeZoneId = TimeZoneInfo.Local.Id;
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddMinutes(delay.Value), TimeZoneInfo.Local).DateTime;
            startLocal = local.AddTicks(-(local.Ticks % TimeSpan.TicksPerSecond));
        }
        else if (!input.ContainsKey("delayMinutes") &&
                 ToolInputReader.TryGetRequiredString(input, "startLocal", 19, out var rawLocal) &&
                 NotificationSchedulePolicy.TryParseLocal(rawLocal, out startLocal) &&
                 ToolInputReader.TryGetRequiredString(input, "timeZoneId", 128, out timeZoneId))
        {
        }
        else return null;

        var interval = (int)(ReadInteger(input, "interval") ?? 1);
        var maxRuntime = (int)(ReadInteger(input, "maxRuntimeSeconds") ?? 300);
        var maxAttempts = (int)(ReadInteger(input, "maxAttempts") ?? 3);
        var resultMax = (int)(ReadInteger(input, "resultMaxCharacters") ?? 20_000);
        string? modelProfile = null;
        if (input.ContainsKey("modelProfileId") &&
            !ToolInputReader.TryGetRequiredString(input, "modelProfileId", 64, out modelProfile)) return null;
        if (allowDesktopSource && input.ContainsKey("_source") &&
            !ToolInputReader.TryGetRequiredString(input, "_source", 160, out source)) return null;
        var draft = new AgentJobDraft(
            title, prompt, startLocal, timeZoneId, recurrence, interval, misfire, modelProfile,
            maxRuntime, maxAttempts, resultMax, source);
        return AgentJobPolicy.IsValid(draft) ? draft : null;
    }

    private static long? ReadInteger(IReadOnlyDictionary<string, object?> input, string key)
    {
        if (!input.TryGetValue(key, out var raw) || raw is null) return null;
        return raw switch
        {
            int value => value,
            long value => value,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var value) => value,
            _ => long.MinValue,
        };
    }

    private static string StableGrantScope(IReadOnlyDictionary<string, object?> input) =>
        JsonSerializer.Serialize(new SortedDictionary<string, object?>(
            input.Where(item => item.Key is not "reason" and not "_source")
                .ToDictionary(item => item.Key, item => item.Value), StringComparer.Ordinal));

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}

internal sealed class AgentJobListTool(IAgentJobRepository jobs) : IWindowsToolAdapter
{
    public string Name => "agent_job.list.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.DurableScheduler;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input) =>
        TryRead(input, out var includeInactive, out var reason)
            ? new WindowsToolApprovalPrompt(
                "저장된 Agent 작업을 사용할까요?",
                "로컬 작업 제목·지시·실행 상태를 모델에 전달합니다.",
                $"범위: {(includeInactive ? "완료·취소 포함" : "활성 작업")}\n요청 이유: {reason}")
            : null;

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        if (!TryRead(input, out var includeInactive, out _)) return Failure("조회 조건이 올바르지 않습니다.");
        var jobsList = await jobs.ListAgentJobsAsync(includeInactive, 0, 101, cancellationToken).ConfigureAwait(false);
        var output = jobsList.Take(100).Select(job => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["jobId"] = job.Id,
            ["title"] = job.Title,
            ["prompt"] = job.Prompt,
            ["recurrence"] = job.Recurrence,
            ["status"] = job.Status,
            ["nextRunAtUtc"] = job.NextRunAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            ["attemptCount"] = job.AttemptCount,
        }).ToArray();
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?> { ["jobs"] = output, ["truncated"] = jobsList.Count > 100 },
            ActivitySummary: $"백그라운드 Agent 작업 {output.Length}개를 조회했어요.");
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

internal sealed class AgentJobCancelTool(IAgentJobRepository jobs) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly HashSet<string> _prepared = new(StringComparer.Ordinal);

    public string Name => "agent_job.cancel.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.DurableScheduler;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        if (!TryRead(input, out var id, out var reason)) return null;
        var job = jobs.GetAgentJobAsync(id, CancellationToken.None).GetAwaiter().GetResult();
        if (job?.Status is not (ScheduleValues.Pending or ScheduleValues.AwaitingDecision or ScheduleValues.Paused)) return null;
        lock (_sync) _prepared.Add(id);
        return new WindowsToolApprovalPrompt(
            "이 Agent 작업을 취소할까요?",
            "저장된 백그라운드 작업을 취소합니다.",
            $"제목: {job.Title}\n요청 이유: {reason}");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        if (!TryRead(input, out var id, out _)) return Failure("취소할 작업 정보가 올바르지 않습니다.");
        lock (_sync)
        {
            if (!_prepared.Remove(id)) return Failure("승인한 작업 취소 정보를 찾을 수 없습니다.");
        }
        var cancelled = await jobs.CancelAgentJobAsync(id, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        return cancelled
            ? new WindowsToolExecutionResult(
                true, new Dictionary<string, object?> { ["cancelled"] = true },
                ActivitySummary: "백그라운드 Agent 작업 하나를 취소했어요.")
            : Failure("취소할 활성 작업을 찾을 수 없습니다.");
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

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}

internal sealed class AgentJobControlTool(IAgentJobRepository jobs) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _prepared = new(StringComparer.Ordinal);
    public string Name => "agent_job.control.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.DurableScheduler;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var value = Read(input); if (value is null) return null;
        var job = jobs.GetAgentJobAsync(value.Value.JobId, CancellationToken.None).GetAwaiter().GetResult();
        if (job is null || !CanApply(job.Status, value.Value.Action)) return null;
        lock (_sync) _prepared[value.Value.JobId] = value.Value.Action;
        return new WindowsToolApprovalPrompt(
            "Agent 작업 상태를 바꿀까요?", $"‘{job.Title}’ 작업을 {Label(value.Value.Action)}합니다.",
            $"현재 상태: {job.Status}\n요청 이유: {value.Value.Reason}",
            GrantScope: $"agent-job:{job.Id}:{value.Value.Action}");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken)
    {
        var value = Read(input); if (value is null) return Failure("Agent 작업 제어 정보가 올바르지 않습니다.");
        string? prepared; lock (_sync) _prepared.Remove(value.Value.JobId, out prepared);
        if (prepared != value.Value.Action) return Failure("승인한 Agent 작업 제어 정보를 찾을 수 없습니다.");
        var now = DateTimeOffset.UtcNow;
        var changed = value.Value.Action switch
        {
            "pause" => await jobs.SetAgentJobPausedAsync(value.Value.JobId, true, now, cancellationToken).ConfigureAwait(false),
            "resume" => await jobs.SetAgentJobPausedAsync(value.Value.JobId, false, now, cancellationToken).ConfigureAwait(false),
            _ => await jobs.RetryAgentJobNowAsync(value.Value.JobId, now, cancellationToken).ConfigureAwait(false),
        };
        return changed
            ? new WindowsToolExecutionResult(true, new Dictionary<string, object?> { ["changed"] = true, ["action"] = value.Value.Action }, ActivitySummary: $"Agent 작업을 {Label(value.Value.Action)}했어요.")
            : Failure("현재 상태에서는 요청한 Agent 작업 제어를 적용할 수 없습니다.");
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input) { var value = Read(input); if (value is not null) lock (_sync) _prepared.Remove(value.Value.JobId); }
    private static bool CanApply(string status, string action) => action switch { "pause" => status is ScheduleValues.Pending or ScheduleValues.AwaitingDecision, "resume" => status == ScheduleValues.Paused, _ => status is ScheduleValues.Failed or ScheduleValues.Completed or ScheduleValues.Cancelled };
    private static string Label(string action) => action switch { "pause" => "일시정지", "resume" => "재개", _ => "즉시 재시도" };
    private static (string JobId, string Action, string Reason)? Read(IReadOnlyDictionary<string, object?> input) => ToolInputReader.HasOnlyKeys(input, "jobId", "action", "reason") && ToolInputReader.TryGetRequiredString(input, "jobId", 32, out var id) && id.Length == 32 && id.All(char.IsAsciiHexDigitLower) && ToolInputReader.TryGetRequiredString(input, "action", 16, out var action) && action is "pause" or "resume" or "retry_now" && ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason) ? (id, action, reason) : null;
    private static WindowsToolExecutionResult Failure(string message) => new(false, new Dictionary<string, object?>(), message);
}
