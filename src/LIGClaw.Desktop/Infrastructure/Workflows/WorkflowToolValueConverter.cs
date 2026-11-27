using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using LIGClaw.Application.Workflows;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Workflows;

internal static class WorkflowToolValueConverter
{
    private const int MaximumDepth = 16;
    private const int MaximumNodes = 4_096;

    public static bool TryToToolInput(
        WorkflowObjectValue value,
        out IReadOnlyDictionary<string, object?> input)
    {
        var counter = 0;
        if (TryToObject(value, 0, ref counter, out var converted) && converted is not null)
        {
            input = converted;
            return true;
        }
        input = new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(StringComparer.Ordinal));
        return false;
    }

    public static bool TryFromToolOutput(
        IReadOnlyDictionary<string, object?> output,
        out WorkflowObjectValue? value)
    {
        ArgumentNullException.ThrowIfNull(output);
        var counter = 0;
        var active = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var converted = FromObject(output, 0, ref counter, active);
        value = converted as WorkflowObjectValue;
        return value is not null;
    }

    private static bool TryToObject(
        WorkflowObjectValue value,
        int depth,
        ref int counter,
        out IReadOnlyDictionary<string, object?>? converted)
    {
        converted = null;
        if (depth > MaximumDepth || ++counter > MaximumNodes) return false;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in value.Properties)
        {
            if (!TryToValue(pair.Value, depth + 1, ref counter, out var item)) return false;
            result.Add(pair.Key, item);
        }
        converted = new ReadOnlyDictionary<string, object?>(result);
        return true;
    }

    private static bool TryToValue(
        WorkflowValue value,
        int depth,
        ref int counter,
        out object? converted)
    {
        converted = null;
        if (depth > MaximumDepth || ++counter > MaximumNodes) return false;
        switch (value)
        {
            case WorkflowNullValue:
                return true;
            case WorkflowBooleanValue boolean:
                converted = boolean.Value;
                return true;
            case WorkflowStringValue text:
                converted = text.Value;
                return true;
            case WorkflowNumberValue { IsInteger: true } integer when long.TryParse(
                integer.CanonicalValue,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var integerValue):
                converted = integerValue;
                return true;
            case WorkflowNumberValue number when double.TryParse(
                number.CanonicalValue,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var numberValue) && double.IsFinite(numberValue):
                converted = numberValue;
                return true;
            case WorkflowArrayValue array:
                {
                    var items = new object?[array.Items.Count];
                    for (var index = 0; index < array.Items.Count; index++)
                    {
                        if (!TryToValue(array.Items[index], depth + 1, ref counter, out items[index])) return false;
                    }
                    converted = Array.AsReadOnly(items);
                    return true;
                }
            case WorkflowObjectValue valueObject:
                if (!TryToObject(valueObject, depth + 1, ref counter, out var dictionary)) return false;
                converted = dictionary;
                return true;
            default:
                return false;
        }
    }

    private static WorkflowValue? FromObject(
        IReadOnlyDictionary<string, object?> value,
        int depth,
        ref int counter,
        HashSet<object> active)
    {
        if (depth > MaximumDepth || ++counter > MaximumNodes || !active.Add(value)) return null;
        try
        {
            var result = new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);
            foreach (var pair in value)
            {
                if (pair.Key.Length is < 1 or > 80 || result.ContainsKey(pair.Key)) return null;
                var converted = FromValue(pair.Value, depth + 1, ref counter, active);
                if (converted is null) return null;
                result.Add(pair.Key, converted);
            }
            return new WorkflowObjectValue(result);
        }
        finally
        {
            active.Remove(value);
        }
    }

    private static WorkflowValue? FromValue(
        object? value,
        int depth,
        ref int counter,
        HashSet<object> active)
    {
        if (depth > MaximumDepth || ++counter > MaximumNodes) return null;
        switch (value)
        {
            case null:
                return WorkflowNullValue.Instance;
            case bool boolean:
                return new WorkflowBooleanValue(boolean);
            case string text when text.Length <= WorkflowInterpreter.MaximumFinalOutputBytes:
                return new WorkflowStringValue(text);
            case DateTimeOffset timestamp:
                return new WorkflowStringValue(
                    timestamp.ToString("O", CultureInfo.InvariantCulture));
            case DateTime timestamp:
                return new WorkflowStringValue(
                    timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            case JsonElement element:
                return FromJsonElement(element, depth + 1, ref counter);
            case sbyte or byte or short or ushort or int or uint or long:
                {
                    var integer = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    return integer is >= -9_007_199_254_740_991 and <= 9_007_199_254_740_991
                        ? new WorkflowNumberValue(integer.ToString(CultureInfo.InvariantCulture), true)
                        : null;
                }
            case ulong unsigned when unsigned <= 9_007_199_254_740_991:
                return new WorkflowNumberValue(unsigned.ToString(CultureInfo.InvariantCulture), true);
            case float or double or decimal:
                {
                    var raw = Convert.ToString(value, CultureInfo.InvariantCulture);
                    return raw is not null && WorkflowDefinitionParser.TryCanonicalizeRuntimeNumber(raw, out var number)
                        ? number
                        : null;
                }
            case IReadOnlyDictionary<string, object?> dictionary:
                return FromObject(dictionary, depth + 1, ref counter, active);
            case IDictionary mutable:
                {
                    if (!active.Add(mutable)) return null;
                    try
                    {
                        var dictionaryValue = new Dictionary<string, object?>(StringComparer.Ordinal);
                        foreach (DictionaryEntry pair in mutable)
                        {
                            if (pair.Key is not string name || !dictionaryValue.TryAdd(name, pair.Value)) return null;
                        }
                        return FromObject(dictionaryValue, depth + 1, ref counter, active);
                    }
                    finally
                    {
                        active.Remove(mutable);
                    }
                }
            case IEnumerable sequence when value is not string:
                {
                    if (!active.Add(sequence)) return null;
                    try
                    {
                        var items = new List<WorkflowValue>();
                        foreach (var item in sequence)
                        {
                            if (items.Count >= 128) return null;
                            var converted = FromValue(item, depth + 1, ref counter, active);
                            if (converted is null) return null;
                            items.Add(converted);
                        }
                        return new WorkflowArrayValue(items);
                    }
                    finally
                    {
                        active.Remove(sequence);
                    }
                }
            default:
                return null;
        }
    }

    private static WorkflowValue? FromJsonElement(JsonElement element, int depth, ref int counter)
    {
        if (depth > MaximumDepth || ++counter > MaximumNodes) return null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return WorkflowNullValue.Instance;
            case JsonValueKind.True:
                return new WorkflowBooleanValue(true);
            case JsonValueKind.False:
                return new WorkflowBooleanValue(false);
            case JsonValueKind.String:
                {
                    var text = element.GetString();
                    return text is not null && text.Length <= WorkflowInterpreter.MaximumFinalOutputBytes
                        ? new WorkflowStringValue(text)
                        : null;
                }
            case JsonValueKind.Number:
                return WorkflowDefinitionParser.TryCanonicalizeRuntimeNumber(
                    element.GetRawText(),
                    out var number)
                    ? number
                    : null;
            case JsonValueKind.Array:
                {
                    if (element.GetArrayLength() > 128) return null;
                    var items = new List<WorkflowValue>(element.GetArrayLength());
                    foreach (var item in element.EnumerateArray())
                    {
                        var converted = FromJsonElement(item, depth + 1, ref counter);
                        if (converted is null) return null;
                        items.Add(converted);
                    }
                    return new WorkflowArrayValue(items);
                }
            case JsonValueKind.Object:
                {
                    var properties = new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name.Length is < 1 or > 80 || properties.ContainsKey(property.Name)) return null;
                        var converted = FromJsonElement(property.Value, depth + 1, ref counter);
                        if (converted is null) return null;
                        properties.Add(property.Name, converted);
                    }
                    return new WorkflowObjectValue(properties);
                }
            default:
                return null;
        }
    }
}
