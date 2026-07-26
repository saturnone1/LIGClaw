using System.Text.Json;
using LIGClaw.Application.Agents;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class SubagentRunTool(Func<ILocalSubagentOrchestrator?> getOrchestrator) : IWindowsToolAdapter
{
    public string Name => "subagent.run.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.DurableScheduler;
    public int Priority => 0;
    public TimeSpan Timeout => TimeSpan.FromMinutes(20);

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parsed = Read(input, "preview", "preview", "preview");
        if (parsed is null) return null;
        var tasks = string.Join("\n", parsed.Tasks.Select((task, index) => $"{index + 1}. {task.Title}: {task.Prompt}"));
        return new WindowsToolApprovalPrompt(
            "로컬 하위 Agent를 실행할까요?",
            $"최대 {parsed.Tasks.Count}개 로컬 모델 작업을 병렬 실행합니다.",
            $"{tasks}\n\n권한 상한: {parsed.MaxRisk}\n작업별 최대 실행: {parsed.MaxRuntimeSeconds}초\n결과 상한: {parsed.ResultMaxCharacters:N0}자\n\n각 Windows 동작은 실행 시점에 별도 정책과 승인을 통과합니다.");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var context = ReadDesktopContext(input);
        if (context is null) return Failure("하위 Agent 작업 정보가 올바르지 않습니다.");
        var orchestrator = getOrchestrator();
        if (orchestrator is null) return Failure("하위 Agent 실행 서비스가 준비되지 않았습니다.");
        try
        {
            var result = await orchestrator.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            var tasks = result.Tasks.Select(task => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["title"] = task.Title,
                ["succeeded"] = task.Succeeded,
                ["result"] = task.ResultText,
                ["errorCode"] = task.ErrorCode,
            }).ToArray();
            var succeeded = result.Tasks.Count(task => task.Succeeded);
            return new WindowsToolExecutionResult(
                true,
                new Dictionary<string, object?>
                {
                    ["batchId"] = result.BatchId,
                    ["tasks"] = tasks,
                    ["succeeded"] = succeeded,
                    ["failed"] = result.Tasks.Count - succeeded,
                },
                ActivitySummary: $"로컬 하위 Agent {result.Tasks.Count}개를 실행했어요.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("부모 요청이 중단되어 하위 Agent도 중단했어요.");
        }
        catch
        {
            return Failure("로컬 하위 Agent 작업을 완료하지 못했어요.");
        }
    }

    private static SubagentBatchDraft? ReadDesktopContext(IReadOnlyDictionary<string, object?> input)
    {
        if (!TryReadContext(input, "_conversationId", out var conversationId) ||
            !TryReadContext(input, "_runId", out var runId) ||
            !TryReadContext(input, "_toolCallId", out var toolCallId)) return null;
        return Read(input, conversationId, runId, toolCallId, allowDesktopContext: true);
    }

    private static SubagentBatchDraft? Read(
        IReadOnlyDictionary<string, object?> input,
        string conversationId,
        string runId,
        string toolCallId,
        bool allowDesktopContext = false)
    {
        var allowed = new HashSet<string>(
            ["tasks", "modelProfileId", "maxRisk", "maxRuntimeSeconds", "resultMaxCharacters", "reason"],
            StringComparer.Ordinal);
        if (allowDesktopContext)
        {
            allowed.Add("_conversationId"); allowed.Add("_runId"); allowed.Add("_toolCallId");
        }
        if (!ToolInputReader.HasOnlyKeys(input, [.. allowed]) ||
            !TryReadTasks(input, out var tasks) ||
            !ToolInputReader.TryGetRequiredString(input, "maxRisk", 2, out var maxRisk) ||
            !TryReadInteger(input, "maxRuntimeSeconds", out var runtime) ||
            !TryReadInteger(input, "resultMaxCharacters", out var resultLimit) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason)) return null;
        string? profile = null;
        if (input.ContainsKey("modelProfileId") &&
            !ToolInputReader.TryGetRequiredString(input, "modelProfileId", 64, out profile)) return null;
        var draft = new SubagentBatchDraft(
            conversationId, runId, toolCallId, tasks, profile, maxRisk, runtime, resultLimit, reason);
        return SubagentPolicy.IsValid(draft) ? draft : null;
    }

    private static bool TryReadTasks(
        IReadOnlyDictionary<string, object?> input,
        out IReadOnlyList<SubagentTaskDraft> tasks)
    {
        tasks = [];
        if (!input.TryGetValue("tasks", out var raw) || raw is not JsonElement { ValueKind: JsonValueKind.Array } array ||
            array.GetArrayLength() is < 1 or > 4) return false;
        var parsed = new List<SubagentTaskDraft>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(property => property.Name is not ("title" or "prompt")) ||
                !item.TryGetProperty("title", out var titleElement) || titleElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("prompt", out var promptElement) || promptElement.ValueKind != JsonValueKind.String) return false;
            var title = titleElement.GetString()?.Trim();
            var prompt = promptElement.GetString();
            if (string.IsNullOrEmpty(title) || title.Length > 80 || string.IsNullOrEmpty(prompt) || prompt.Length > 4_000) return false;
            parsed.Add(new SubagentTaskDraft(title, prompt));
        }
        tasks = parsed;
        return true;
    }

    private static bool TryReadInteger(IReadOnlyDictionary<string, object?> input, string key, out int value)
    {
        value = 0;
        if (!input.TryGetValue(key, out var raw)) return false;
        long? parsed = raw switch
        {
            int direct => direct,
            long direct => direct,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var number) => number,
            _ => null,
        };
        if (parsed is null or < int.MinValue or > int.MaxValue) return false;
        value = (int)parsed.Value;
        return true;
    }

    private static bool TryReadContext(IReadOnlyDictionary<string, object?> input, string key, out string value) =>
        ToolInputReader.TryGetRequiredString(input, key, 160, out value);

    private static WindowsToolExecutionResult Failure(string error) =>
        new(false, new Dictionary<string, object?>(), error);
}
