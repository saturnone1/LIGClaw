using System.Text.Json;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal static class ToolInputReader
{
    public static bool HasOnlyKeys(IReadOnlyDictionary<string, object?> input, params string[] keys) =>
        input.Keys.All(key => keys.Contains(key, StringComparer.Ordinal));

    public static bool TryGetRequiredString(
        IReadOnlyDictionary<string, object?> input,
        string key,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (!input.TryGetValue(key, out var raw)) return false;
        var text = raw switch
        {
            string direct => direct,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
        if (text is null) return false;
        text = text.Trim();
        if (text.Length is 0 || text.Length > maximumLength) return false;
        value = text;
        return true;
    }

    public static bool TryGetRequiredStrings(
        IReadOnlyDictionary<string, object?> input,
        string key,
        int maximumItems,
        int maximumItemLength,
        out IReadOnlyList<string> values)
    {
        values = [];
        if (!input.TryGetValue(key, out var raw)) return false;
        IEnumerable<string?>? candidates = raw switch
        {
            IEnumerable<string> direct => direct,
            JsonElement { ValueKind: JsonValueKind.Array } element =>
                element.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null),
            _ => null,
        };
        if (candidates is null) return false;
        var result = new List<string>();
        foreach (var candidate in candidates)
        {
            var value = candidate?.Trim();
            if (string.IsNullOrEmpty(value) || value.Length > maximumItemLength || result.Count == maximumItems)
                return false;
            result.Add(value);
        }
        if (result.Count == 0 || result.Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count) return false;
        values = result;
        return true;
    }

    public static bool TryGetRequiredStringExact(
        IReadOnlyDictionary<string, object?> input,
        string key,
        int maximumLength,
        out string value)
    {
        return TryGetStringExact(input, key, maximumLength, out value) && value.Length > 0;
    }

    public static bool TryGetStringExact(
        IReadOnlyDictionary<string, object?> input,
        string key,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (!input.TryGetValue(key, out var raw)) return false;
        var text = raw switch
        {
            string direct => direct,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
        if (text is null || text.Length > maximumLength) return false;
        value = text;
        return true;
    }
}
