using System.Text.Json;
using LIGClaw.Application.Memory;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record MemoryArgumentResolution(
    bool Success,
    IReadOnlyDictionary<string, object?> Input,
    string? Error = null,
    bool ContainsReference = false);

internal sealed class MemoryArgumentResolver(IMemoryRepository memories)
{
    private static readonly HashSet<string> ResolvableFields =
        ["appName", "path", "rootPath", "destinationDirectory", "sources", "paths"];

    public async Task<MemoryArgumentResolution> ResolveAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, object?>(input.Count, StringComparer.Ordinal);
        var containsReference = false;
        foreach (var (key, value) in input)
        {
            if (!ResolvableFields.Contains(key))
            {
                resolved[key] = value;
                continue;
            }
            containsReference |= HasMemoryReference(value);
            var item = await ResolveValueAsync(value, cancellationToken).ConfigureAwait(false);
            if (!item.Success)
                return new MemoryArgumentResolution(false, input, "요청에 사용한 별칭 또는 선호를 찾을 수 없습니다.");
            resolved[key] = item.Value;
        }
        return new MemoryArgumentResolution(true, resolved, ContainsReference: containsReference);
    }

    private static bool HasMemoryReference(object? value)
    {
        static bool IsReference(string text) =>
            text.StartsWith("$alias:", StringComparison.Ordinal) ||
            text.StartsWith("$preference:", StringComparison.Ordinal);
        if (value is string text) return IsReference(text);
        if (value is IEnumerable<string> strings) return strings.Any(IsReference);
        if (value is JsonElement { ValueKind: JsonValueKind.String } jsonString)
            return IsReference(jsonString.GetString() ?? string.Empty);
        if (value is JsonElement { ValueKind: JsonValueKind.Array } jsonArray)
            return jsonArray.EnumerateArray().Any(element =>
                element.ValueKind == JsonValueKind.String && IsReference(element.GetString() ?? string.Empty));
        return false;
    }

    private async Task<(bool Success, object? Value)> ResolveValueAsync(
        object? value,
        CancellationToken cancellationToken)
    {
        if (value is string text) return await ResolveTextAsync(text, cancellationToken).ConfigureAwait(false);
        if (value is IEnumerable<string> strings)
        {
            var output = new List<string>();
            foreach (var item in strings)
            {
                var resolved = await ResolveTextAsync(item, cancellationToken).ConfigureAwait(false);
                if (!resolved.Success || resolved.Value is not string result) return (false, value);
                output.Add(result);
            }
            return (true, output.ToArray());
        }
        if (value is JsonElement { ValueKind: JsonValueKind.String } jsonString)
            return await ResolveTextAsync(jsonString.GetString() ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (value is JsonElement { ValueKind: JsonValueKind.Array } jsonArray)
        {
            var output = new List<string>();
            foreach (var element in jsonArray.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String) return (true, value);
                var resolved = await ResolveTextAsync(element.GetString() ?? string.Empty, cancellationToken).ConfigureAwait(false);
                if (!resolved.Success || resolved.Value is not string result) return (false, value);
                output.Add(result);
            }
            return (true, output.ToArray());
        }
        return (true, value);
    }

    private async Task<(bool Success, object? Value)> ResolveTextAsync(
        string value,
        CancellationToken cancellationToken)
    {
        const string aliasPrefix = "$alias:";
        const string preferencePrefix = "$preference:";
        var kind = value.StartsWith(aliasPrefix, StringComparison.Ordinal)
            ? "alias"
            : value.StartsWith(preferencePrefix, StringComparison.Ordinal)
                ? "preference"
                : null;
        if (kind is null) return (true, value);
        var prefix = kind == "alias" ? aliasPrefix : preferencePrefix;
        var key = value[prefix.Length..].Trim();
        if (key.Length is < 1 or > 80) return (false, value);
        var resolved = await memories.ResolveValueAsync(kind, key, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        return resolved is null ? (false, value) : (true, resolved);
    }
}
