using System.Text.Json;
using LIGClaw.Application.Memory;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed class MemoryRememberTool(IMemoryRepository memories) : IWindowsToolAdapter
{
    public string Name => "memory.remember.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.PersonalMemory;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var draft = Read(input, "preview");
        if (draft is null || !PersonalMemoryPolicy.IsValid(draft, DateTimeOffset.UtcNow)) return null;
        return new WindowsToolApprovalPrompt(
            "이 내용을 기억할까요?",
            "개인 기억 하나를 이 PC에 저장하거나 갱신합니다.",
            $"종류: {draft.Kind}\n키: {draft.Key}\n내용: {draft.Value}\n민감도: {draft.Sensitivity}\n만료: {draft.ExpiresAtUtc?.ToLocalTime().ToString("yyyy-MM-dd") ?? "없음"}\n\nAPI 키와 비밀번호 같은 인증정보는 저장하지 않습니다.",
            GrantScope: StableGrantScope(input));
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var draft = Read(input, "conversation", allowDesktopSource: true);
        if (draft is null || !PersonalMemoryPolicy.IsValid(draft, now))
            return Failure("기억할 내용이 정책에 맞지 않거나 인증정보로 보입니다.");
        var result = await memories.UpsertAsync(draft, now, cancellationToken).ConfigureAwait(false);
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?>
            {
                ["memoryId"] = result.Memory.Id,
                ["created"] = result.Created,
            },
            ActivitySummary: result.Created ? "개인 기억 하나를 저장했어요." : "개인 기억 하나를 갱신했어요.");
    }

    private static PersonalMemoryDraft? Read(
        IReadOnlyDictionary<string, object?> input,
        string sourcePrefix,
        bool allowDesktopSource = false)
    {
        var allowed = allowDesktopSource
            ? new[] { "kind", "key", "value", "sensitivity", "ttlDays", "reason", "_source" }
            : ["kind", "key", "value", "sensitivity", "ttlDays", "reason"];
        if (!HasOnly(input, allowed) ||
            !ToolInputReader.TryGetRequiredString(input, "kind", 16, out var kind) ||
            !ToolInputReader.TryGetRequiredString(input, "key", 80, out var key) ||
            !ToolInputReader.TryGetRequiredStringExact(input, "value", 2000, out var value) ||
            !ToolInputReader.TryGetRequiredString(input, "sensitivity", 16, out var sensitivity) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out _))
            return null;
        var ttlDays = ReadOptionalInteger(input, "ttlDays");
        if (input.ContainsKey("ttlDays") && ttlDays is < 1 or > 3650) return null;
        DateTimeOffset? expires = ttlDays > 0 ? DateTimeOffset.UtcNow.AddDays(ttlDays.Value) : null;
        var source = sourcePrefix;
        if (allowDesktopSource && input.ContainsKey("_source") &&
            !ToolInputReader.TryGetRequiredString(input, "_source", 160, out source)) return null;
        return new PersonalMemoryDraft(kind, key, value, sensitivity, source, expires);
    }

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

    internal static bool HasOnly(IReadOnlyDictionary<string, object?> input, params string[] allowed) =>
        input.Keys.All(key => allowed.Contains(key, StringComparer.Ordinal));

    private static string StableGrantScope(IReadOnlyDictionary<string, object?> input) =>
        JsonSerializer.Serialize(new SortedDictionary<string, object?>(
            input.Where(item => item.Key is not "reason" and not "_source")
                .ToDictionary(item => item.Key, item => item.Value),
            StringComparer.Ordinal));

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}

internal sealed class MemoryListTool(IMemoryRepository memories) : IWindowsToolAdapter
{
    public string Name => "memory.list.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.PersonalMemory;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        if (!TryRead(input, out var query, out var reason)) return null;
        return new WindowsToolApprovalPrompt(
            "저장된 기억을 사용할까요?",
            "저장된 개인 기억을 모델에 전달합니다.",
            $"검색어: {query ?? "전체"}\n요청 이유: {reason}\n\n만료되지 않은 기억을 최대 50개 전달합니다.");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        if (!TryRead(input, out var query, out _)) return Failure("기억 검색 조건이 올바르지 않습니다.");
        var results = await memories.ListAsync(query, 51, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        var truncated = results.Count > 50;
        var output = results.Take(50).Select(memory =>
        {
            var item = new Dictionary<string, object?>
            {
                ["memoryId"] = memory.Id,
                ["kind"] = memory.Kind,
                ["key"] = memory.Key,
                ["value"] = memory.Value,
                ["sensitivity"] = memory.Sensitivity,
                ["source"] = memory.Source,
                ["updatedAtUtc"] = memory.UpdatedAtUtc.ToString("O"),
            };
            if (memory.ExpiresAtUtc is not null) item["expiresAtUtc"] = memory.ExpiresAtUtc.Value.ToString("O");
            return (IReadOnlyDictionary<string, object?>)item;
        }).ToArray();
        return new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?> { ["memories"] = output, ["truncated"] = truncated },
            ActivitySummary: $"개인 기억 {output.Length}개를 조회했어요.");
    }

    private static bool TryRead(
        IReadOnlyDictionary<string, object?> input,
        out string? query,
        out string reason)
    {
        query = null;
        reason = string.Empty;
        if (!MemoryRememberTool.HasOnly(input, "query", "reason") ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out reason)) return false;
        if (!input.ContainsKey("query")) return true;
        if (!ToolInputReader.TryGetRequiredString(input, "query", 80, out var parsed)) return false;
        query = parsed;
        return true;
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}

internal sealed class MemoryForgetTool(IMemoryRepository memories) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly HashSet<string> _prepared = new(StringComparer.Ordinal);

    public string Name => "memory.forget.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.PersonalMemory;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        if (!TryRead(input, out var id, out var reason)) return null;
        var memory = memories.GetAsync(id, DateTimeOffset.UtcNow, CancellationToken.None).GetAwaiter().GetResult();
        if (memory is null) return null;
        lock (_sync) _prepared.Add(id);
        return new WindowsToolApprovalPrompt(
            "이 기억을 삭제할까요?",
            "개인 기억 하나를 이 PC에서 삭제합니다.",
            $"키: {memory.Key}\n내용: {memory.Value}\n요청 이유: {reason}\n\n삭제하면 자동으로 복구되지 않습니다.");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        if (!TryRead(input, out var id, out _)) return Failure("삭제할 기억 정보가 올바르지 않습니다.");
        lock (_sync)
        {
            if (!_prepared.Remove(id)) return Failure("승인한 기억 삭제 정보를 찾을 수 없어요.");
        }
        var deleted = await memories.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        return deleted
            ? new WindowsToolExecutionResult(
                true,
                new Dictionary<string, object?> { ["forgotten"] = true },
                ActivitySummary: "개인 기억 하나를 삭제했어요.")
            : Failure("삭제할 기억을 찾을 수 없어요.");
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
        return ToolInputReader.HasOnlyKeys(input, "memoryId", "reason") &&
               ToolInputReader.TryGetRequiredString(input, "memoryId", 32, out id) &&
               id.Length == 32 && id.All(char.IsAsciiHexDigitLower) &&
               ToolInputReader.TryGetRequiredString(input, "reason", 160, out reason);
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}
