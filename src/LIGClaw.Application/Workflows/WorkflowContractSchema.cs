using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using LIGClaw.Domain;

namespace LIGClaw.Application.Workflows;

internal sealed class WorkflowContractSchema
{
    private const int MaximumDepth = 8;
    private const int MaximumProperties = 128;
    private static readonly HashSet<string> AnnotationKeywords =
        ["$schema", "$id", "title", "description", "type", "enum", "const"];
    private static readonly HashSet<string> SupportedFormats = ["date", "date-time", "email", "uuid"];
    private readonly IReadOnlyDictionary<string, WorkflowContractSchema> properties;
    private readonly IReadOnlySet<string> required;
    private readonly IReadOnlySet<string>? enumValues;
    private readonly string? constantValue;
    private readonly IReadOnlyList<WorkflowContractSchema>? tupleItems;

    private WorkflowContractSchema(
        string type,
        IReadOnlyDictionary<string, WorkflowContractSchema>? properties = null,
        IReadOnlySet<string>? required = null,
        WorkflowContractSchema? items = null,
        IReadOnlyList<WorkflowContractSchema>? tupleItems = null,
        int? minimumLength = null,
        int? maximumLength = null,
        double? minimum = null,
        double? maximum = null,
        double? exclusiveMinimum = null,
        double? exclusiveMaximum = null,
        int? minimumItems = null,
        int? maximumItems = null,
        bool uniqueItems = false,
        string? format = null,
        IReadOnlySet<string>? enumValues = null,
        string? constantValue = null)
    {
        Type = type;
        this.properties = properties ?? new ReadOnlyDictionary<string, WorkflowContractSchema>(
            new Dictionary<string, WorkflowContractSchema>(StringComparer.Ordinal));
        this.required = required ?? new HashSet<string>(StringComparer.Ordinal);
        Items = items;
        this.tupleItems = tupleItems;
        MinimumLength = minimumLength;
        MaximumLength = maximumLength;
        Minimum = minimum;
        Maximum = maximum;
        ExclusiveMinimum = exclusiveMinimum;
        ExclusiveMaximum = exclusiveMaximum;
        MinimumItems = minimumItems;
        MaximumItems = maximumItems;
        UniqueItems = uniqueItems;
        Format = format;
        this.enumValues = enumValues;
        this.constantValue = constantValue;
    }

    public string Type { get; }
    public WorkflowContractSchema? Items { get; }
    public int? MinimumLength { get; }
    public int? MaximumLength { get; }
    public double? Minimum { get; }
    public double? Maximum { get; }
    public double? ExclusiveMinimum { get; }
    public double? ExclusiveMaximum { get; }
    public int? MinimumItems { get; }
    public int? MaximumItems { get; }
    public bool UniqueItems { get; }
    public string? Format { get; }

    public bool AcceptsValue(
        WorkflowValue value,
        Func<WorkflowBindingValue, WorkflowContractSchema?>? resolveBinding = null)
    {
        if (value is WorkflowBindingValue binding)
            return resolveBinding is not null && resolveBinding(binding) is { } source && AcceptsSource(source);
        if (!AcceptsLiteralCore(value)) return false;
        var canonical = CanonicalScalar(value);
        if (constantValue is not null && !StringComparer.Ordinal.Equals(constantValue, canonical)) return false;
        if (enumValues is not null && (canonical is null || !enumValues.Contains(canonical))) return false;
        return (Type, value) switch
        {
            ("array", WorkflowArrayValue array) when Items is not null =>
                array.Items.All(item => Items.AcceptsValue(item, resolveBinding)),
            ("object", WorkflowObjectValue valueObject) when AcceptsObjectKeys(valueObject.Properties.Keys) =>
                valueObject.Properties.All(pair => properties[pair.Key].AcceptsValue(pair.Value, resolveBinding)),
            _ => true,
        };
    }

    public bool AcceptsSource(WorkflowContractSchema source)
    {
        if (!StringComparer.Ordinal.Equals(Type, source.Type) && !(Type == "number" && source.Type == "integer"))
            return false;
        if (constantValue is not null && !StringComparer.Ordinal.Equals(constantValue, source.constantValue))
            return false;
        if (enumValues is not null)
        {
            var sourceValues = source.constantValue is null
                ? source.enumValues
                : new HashSet<string>([source.constantValue], StringComparer.Ordinal);
            if (sourceValues is null || sourceValues.Any(value => !enumValues.Contains(value))) return false;
        }
        if (Type == "string")
        {
            if (MinimumLength is { } minimum && (source.MinimumLength is null || source.MinimumLength < minimum))
                return false;
            if (MaximumLength is { } maximum && (source.MaximumLength is null || source.MaximumLength > maximum))
                return false;
            if (Format is not null && !StringComparer.Ordinal.Equals(Format, source.Format)) return false;
        }
        if (Type is "integer" or "number")
        {
            if (Minimum is { } minimum && (source.Minimum is null || source.Minimum < minimum)) return false;
            if (Maximum is { } maximum && (source.Maximum is null || source.Maximum > maximum)) return false;
            if (ExclusiveMinimum is { } exclusiveMinimum &&
                (source.ExclusiveMinimum is null || source.ExclusiveMinimum < exclusiveMinimum))
                return false;
            if (ExclusiveMaximum is { } exclusiveMaximum &&
                (source.ExclusiveMaximum is null || source.ExclusiveMaximum > exclusiveMaximum))
                return false;
        }
        if (Type == "array")
        {
            if (MinimumItems is { } minimum && (source.MinimumItems is null || source.MinimumItems < minimum))
                return false;
            if (MaximumItems is { } maximum && (source.MaximumItems is null || source.MaximumItems > maximum))
                return false;
            if (UniqueItems && !source.UniqueItems) return false;
            if (Items is null) return false;
            if (source.tupleItems is not null) return source.tupleItems.All(AcceptsArrayItem);
            if (source.Items is null || !Items.AcceptsSource(source.Items)) return false;
        }
        if (Type == "object")
        {
            if (source.properties.Keys.Any(key => !properties.ContainsKey(key)) ||
                required.Any(name => !source.required.Contains(name)))
                return false;
            foreach (var pair in source.properties)
            {
                if (!properties[pair.Key].AcceptsSource(pair.Value)) return false;
            }
        }
        return true;

        bool AcceptsArrayItem(WorkflowContractSchema item) => Items!.AcceptsSource(item);
    }

    public bool IsComparableWith(WorkflowContractSchema other) =>
        StringComparer.Ordinal.Equals(Type, other.Type) ||
        Type is "integer" or "number" && other.Type is "integer" or "number";

    public bool TryResolvePointer(string pointer, out WorkflowContractSchema? resolved)
    {
        return TryResolvePointer(pointer, out resolved, out _);
    }

    public bool TryResolvePointer(
        string pointer,
        out WorkflowContractSchema? resolved,
        out bool guaranteed)
    {
        resolved = this;
        guaranteed = true;
        if (pointer.Length == 0) return true;
        if (!TryDecodePointer(pointer, out var tokens)) return false;
        foreach (var token in tokens)
        {
            if (resolved.Type == "object")
            {
                guaranteed &= resolved.required.Contains(token);
                if (!resolved.properties.TryGetValue(token, out resolved)) return false;
                continue;
            }
            if (resolved.Type == "array" && IsCanonicalArrayIndex(token))
            {
                if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return false;
                if (resolved.tupleItems is not null)
                {
                    if (index >= resolved.tupleItems.Count)
                        return false;
                    resolved = resolved.tupleItems[index];
                    continue;
                }
                if (resolved.Items is null) return false;
                guaranteed &= resolved.MinimumItems is { } minimumItems && index < minimumItems;
                resolved = resolved.Items;
                continue;
            }
            return false;
        }
        return true;
    }

    public static WorkflowContractSchema? Infer(
        WorkflowValue value,
        Func<WorkflowBindingValue, WorkflowContractSchema?> resolveBinding)
    {
        return value switch
        {
            WorkflowBindingValue binding => resolveBinding(binding),
            WorkflowNullValue => null,
            WorkflowBooleanValue boolean => Scalar("boolean", CanonicalScalar(boolean)),
            WorkflowNumberValue number => NumberLiteral(number),
            WorkflowStringValue text => StringLiteral(text.Value),
            WorkflowArrayValue array => ArrayLiteral(array, resolveBinding),
            WorkflowObjectValue valueObject => ObjectLiteral(valueObject, resolveBinding),
            _ => null,
        };
    }

    public static WorkflowContractSchema Scalar(string type) => new(type);

    public static WorkflowContractSchema String(string? format = null) => new("string", format: format);

    public static bool TryParse(string json, out WorkflowContractSchema? schema)
    {
        schema = null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var propertyCount = 0;
            schema = ParseNode(document.RootElement, 0, ref propertyCount);
            return schema is not null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                          FormatException or OverflowException)
        {
            return false;
        }
    }

    private bool AcceptsLiteralCore(WorkflowValue value)
    {
        switch (Type, value)
        {
            case ("string", WorkflowStringValue text):
                var length = text.Value.EnumerateRunes().Count();
                return (MinimumLength is null || length >= MinimumLength) &&
                       (MaximumLength is null || length <= MaximumLength) &&
                       MatchesFormat(text.Value, Format);
            case ("integer", WorkflowNumberValue { IsInteger: true } integerValue):
                return AcceptsNumber(integerValue);
            case ("number", WorkflowNumberValue numberValue):
                return AcceptsNumber(numberValue);
            case ("boolean", WorkflowBooleanValue):
                return true;
            case ("array", WorkflowArrayValue array):
                return Items is not null &&
                       (MinimumItems is null || array.Items.Count >= MinimumItems) &&
                       (MaximumItems is null || array.Items.Count <= MaximumItems) &&
                       (!UniqueItems || HasUniqueStringItems(array.Items));
            case ("object", WorkflowObjectValue valueObject):
                return AcceptsObjectKeys(valueObject.Properties.Keys);
            default:
                return false;
        }

        bool AcceptsNumber(WorkflowNumberValue number) =>
            double.TryParse(number.CanonicalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric) &&
                       double.IsFinite(numeric) &&
                       (Minimum is null || numeric >= Minimum) &&
                       (Maximum is null || numeric <= Maximum) &&
                       (ExclusiveMinimum is null || numeric > ExclusiveMinimum) &&
                       (ExclusiveMaximum is null || numeric < ExclusiveMaximum);
    }

    private bool AcceptsObjectKeys(IEnumerable<string> keys)
    {
        if (Type != "object") return false;
        var keyArray = keys.ToArray();
        var provided = keyArray.ToHashSet(StringComparer.Ordinal);
        return provided.Count == keyArray.Length &&
               provided.All(properties.ContainsKey) &&
               required.All(provided.Contains);
    }

    private static WorkflowContractSchema? ParseNode(JsonElement element, int depth, ref int propertyCount)
    {
        if (depth > MaximumDepth || element.ValueKind != JsonValueKind.Object ||
            !TryFields(element, out var fields) ||
            !fields.TryGetValue("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String ||
            typeElement.GetString() is not { } type ||
            type is not ("object" or "array" or "string" or "integer" or "number" or "boolean") ||
            fields.Keys.Any(name => !IsAllowedKeyword(type, name)) ||
            !ValidateAnnotations(fields))
            return null;

        int? minimumLength = null;
        int? maximumLength = null;
        double? minimum = null;
        double? maximum = null;
        double? exclusiveMinimum = null;
        double? exclusiveMaximum = null;
        int? minimumItems = null;
        int? maximumItems = null;
        var uniqueItems = false;
        string? format = null;
        WorkflowContractSchema? items = null;
        IReadOnlyDictionary<string, WorkflowContractSchema>? properties = null;
        IReadOnlySet<string>? required = null;

        if (type == "string")
        {
            if (!TryOptionalBoundedInteger(fields, "minLength", 0, 65_536, out minimumLength) ||
                !TryOptionalBoundedInteger(fields, "maxLength", 0, 65_536, out maximumLength) ||
                minimumLength is { } minLength && maximumLength is { } maxLength && minLength > maxLength)
                return null;
            if (fields.TryGetValue("format", out var formatElement))
            {
                if (formatElement.ValueKind != JsonValueKind.String ||
                    !SupportedFormats.Contains(formatElement.GetString() ?? string.Empty))
                    return null;
                format = formatElement.GetString();
            }
        }
        else if (type is "integer" or "number")
        {
            if (!TryOptionalFiniteNumber(fields, "minimum", out minimum) ||
                !TryOptionalFiniteNumber(fields, "maximum", out maximum) ||
                !TryOptionalFiniteNumber(fields, "exclusiveMinimum", out exclusiveMinimum) ||
                !TryOptionalFiniteNumber(fields, "exclusiveMaximum", out exclusiveMaximum) ||
                minimum is { } min && maximum is { } max && min > max)
                return null;
        }
        else if (type == "array")
        {
            if (!fields.TryGetValue("items", out var itemElement) ||
                (items = ParseNode(itemElement, depth + 1, ref propertyCount)) is null ||
                !TryOptionalBoundedInteger(fields, "minItems", 0, 64, out minimumItems) ||
                !TryOptionalBoundedInteger(fields, "maxItems", 0, 64, out maximumItems) ||
                minimumItems is { } min && maximumItems is { } max && min > max)
                return null;
            if (fields.TryGetValue("uniqueItems", out var uniqueItemsElement))
            {
                if (uniqueItemsElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return null;
                uniqueItems = uniqueItemsElement.GetBoolean();
                if (uniqueItems && items.Type != "string") return null;
            }
        }
        else if (type == "object")
        {
            if (!fields.TryGetValue("additionalProperties", out var additional) ||
                additional.ValueKind != JsonValueKind.False ||
                !fields.TryGetValue("properties", out var propertiesElement) ||
                propertiesElement.ValueKind != JsonValueKind.Object ||
                !TryFields(propertiesElement, out var propertyFields))
                return null;
            var parsedProperties = new Dictionary<string, WorkflowContractSchema>(StringComparer.Ordinal);
            var insensitiveProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in propertyFields)
            {
                propertyCount++;
                var child = ParseNode(property.Value, depth + 1, ref propertyCount);
                if (propertyCount > MaximumProperties || property.Key.Length is < 1 or > 80 || child is null ||
                    !insensitiveProperties.Add(property.Key) || !parsedProperties.TryAdd(property.Key, child))
                    return null;
            }
            var parsedRequired = new HashSet<string>(StringComparer.Ordinal);
            if (fields.TryGetValue("required", out var requiredElement))
            {
                if (requiredElement.ValueKind != JsonValueKind.Array) return null;
                foreach (var item in requiredElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } name ||
                        !parsedProperties.ContainsKey(name) || !parsedRequired.Add(name))
                        return null;
                }
            }
            properties = new ReadOnlyDictionary<string, WorkflowContractSchema>(parsedProperties);
            required = parsedRequired;
        }

        if (!TryEnumValues(fields, type, out var enumValues) ||
            !TryConstantValue(fields, type, out var constantValue))
            return null;
        var result = new WorkflowContractSchema(
            type,
            properties,
            required,
            items,
            minimumLength: minimumLength,
            maximumLength: maximumLength,
            minimum: minimum,
            maximum: maximum,
            exclusiveMinimum: exclusiveMinimum,
            exclusiveMaximum: exclusiveMaximum,
            minimumItems: minimumItems,
            maximumItems: maximumItems,
            uniqueItems: uniqueItems,
            format: format,
            enumValues: enumValues,
            constantValue: constantValue);
        return result.EnumAndConstantSatisfyConstraints() ? result : null;
    }

    private bool EnumAndConstantSatisfyConstraints()
    {
        if (constantValue is not null && !AcceptsCanonicalPrimitive(constantValue)) return false;
        return enumValues is null || enumValues.All(AcceptsCanonicalPrimitive);
    }

    private bool AcceptsCanonicalPrimitive(string canonical)
    {
        WorkflowValue? value = Type switch
        {
            "string" => JsonSerializer.Deserialize<string>(canonical) is { } text ? new WorkflowStringValue(text) : null,
            "boolean" => canonical == "true" ? new WorkflowBooleanValue(true) :
                canonical == "false" ? new WorkflowBooleanValue(false) : null,
            "integer" => new WorkflowNumberValue(canonical, true),
            "number" => new WorkflowNumberValue(canonical, false),
            _ => null,
        };
        return value is not null && AcceptsLiteralCore(value);
    }

    private static bool IsAllowedKeyword(string type, string name)
    {
        if (AnnotationKeywords.Contains(name)) return true;
        return type switch
        {
            "object" => name is "properties" or "required" or "additionalProperties",
            "array" => name is "items" or "minItems" or "maxItems" or "uniqueItems",
            "string" => name is "minLength" or "maxLength" or "format",
            "integer" or "number" => name is "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum",
            _ => false,
        };
    }

    private static bool ValidateAnnotations(IReadOnlyDictionary<string, JsonElement> fields)
    {
        if (fields.TryGetValue("$schema", out var declaration) &&
            (declaration.ValueKind != JsonValueKind.String ||
             declaration.GetString() != "https://json-schema.org/draft/2020-12/schema"))
            return false;
        if (fields.TryGetValue("$id", out var id) &&
            (id.ValueKind != JsonValueKind.String || !Uri.TryCreate(id.GetString(), UriKind.Absolute, out _)))
            return false;
        if (fields.TryGetValue("title", out var title) &&
            (title.ValueKind != JsonValueKind.String || (title.GetString()?.Length ?? 0) is < 1 or > 200))
            return false;
        return !fields.TryGetValue("description", out var description) ||
               description.ValueKind == JsonValueKind.String && (description.GetString()?.Length ?? 0) <= 1_000;
    }

    private static bool TryEnumValues(
        IReadOnlyDictionary<string, JsonElement> fields,
        string type,
        out IReadOnlySet<string>? values)
    {
        values = null;
        if (!fields.TryGetValue("enum", out var element)) return true;
        if (type is "object" or "array" || element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() is < 1 or > 64)
            return false;
        var parsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (!TryCanonicalPrimitive(item, type, out var canonical) || !parsed.Add(canonical)) return false;
        }
        values = parsed;
        return true;
    }

    private static bool TryConstantValue(
        IReadOnlyDictionary<string, JsonElement> fields,
        string type,
        out string? value)
    {
        value = null;
        return !fields.TryGetValue("const", out var element) ||
               type is not ("object" or "array") && TryCanonicalPrimitive(element, type, out value);
    }

    private static bool TryCanonicalPrimitive(JsonElement element, string type, out string canonical)
    {
        canonical = string.Empty;
        switch (type)
        {
            case "string" when element.ValueKind == JsonValueKind.String:
                canonical = JsonSerializer.Serialize(element.GetString());
                return true;
            case "boolean" when element.ValueKind is JsonValueKind.True or JsonValueKind.False:
                canonical = element.GetBoolean() ? "true" : "false";
                return true;
            case "integer" when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var integer) &&
                                integer is >= -9_007_199_254_740_991 and <= 9_007_199_254_740_991:
                canonical = integer.ToString(CultureInfo.InvariantCulture);
                return true;
            case "number" when element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number) &&
                               double.IsFinite(number):
                canonical = number == 0 ? "0" : number.ToString("R", CultureInfo.InvariantCulture);
                return true;
            default:
                return false;
        }
    }

    private static bool TryOptionalBoundedInteger(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        int minimum,
        int maximum,
        out int? value)
    {
        value = null;
        if (!fields.TryGetValue(name, out var element)) return true;
        if (!element.TryGetInt32(out var parsed) || parsed < minimum || parsed > maximum) return false;
        value = parsed;
        return true;
    }

    private static bool TryOptionalFiniteNumber(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        out double? value)
    {
        value = null;
        if (!fields.TryGetValue(name, out var element)) return true;
        if (!element.TryGetDouble(out var parsed) || !double.IsFinite(parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryFields(
        JsonElement element,
        out IReadOnlyDictionary<string, JsonElement> fields)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (!result.TryAdd(property.Name, property.Value) || !insensitive.Add(property.Name))
            {
                fields = result;
                return false;
            }
        }
        fields = result;
        return true;
    }

    private static WorkflowContractSchema Scalar(string type, string? constant) =>
        new(type, constantValue: constant);

    private static WorkflowContractSchema? NumberLiteral(WorkflowNumberValue number)
    {
        if (!double.TryParse(number.CanonicalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value))
            return null;
        return new WorkflowContractSchema(
            number.IsInteger ? "integer" : "number",
            minimum: value,
            maximum: value,
            constantValue: value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static WorkflowContractSchema StringLiteral(string value)
    {
        var length = value.EnumerateRunes().Count();
        return new WorkflowContractSchema(
            "string",
            minimumLength: length,
            maximumLength: length,
            constantValue: JsonSerializer.Serialize(value));
    }

    private static WorkflowContractSchema? ArrayLiteral(
        WorkflowArrayValue array,
        Func<WorkflowBindingValue, WorkflowContractSchema?> resolveBinding)
    {
        var shapes = new List<WorkflowContractSchema>(array.Items.Count);
        foreach (var item in array.Items)
        {
            var shape = Infer(item, resolveBinding);
            if (shape is null) return null;
            shapes.Add(shape);
        }
        return new WorkflowContractSchema(
            "array",
            items: shapes.FirstOrDefault() ?? Scalar("string"),
            tupleItems: shapes.AsReadOnly(),
            minimumItems: shapes.Count,
            maximumItems: shapes.Count,
            uniqueItems: HasUniqueStringItems(array.Items));
    }

    private static WorkflowContractSchema? ObjectLiteral(
        WorkflowObjectValue value,
        Func<WorkflowBindingValue, WorkflowContractSchema?> resolveBinding)
    {
        var shapes = new Dictionary<string, WorkflowContractSchema>(StringComparer.Ordinal);
        foreach (var pair in value.Properties)
        {
            var shape = Infer(pair.Value, resolveBinding);
            if (shape is null) return null;
            shapes.Add(pair.Key, shape);
        }
        return new WorkflowContractSchema(
            "object",
            new ReadOnlyDictionary<string, WorkflowContractSchema>(shapes),
            shapes.Keys.ToHashSet(StringComparer.Ordinal));
    }

    private static string? CanonicalScalar(WorkflowValue value) => value switch
    {
        WorkflowStringValue text => JsonSerializer.Serialize(text.Value),
        WorkflowBooleanValue boolean => boolean.Value ? "true" : "false",
        WorkflowNumberValue number when double.TryParse(
            number.CanonicalValue,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed) && double.IsFinite(parsed) =>
            parsed == 0 ? "0" : parsed.ToString("R", CultureInfo.InvariantCulture),
        _ => null,
    };

    private static bool HasUniqueStringItems(IReadOnlyList<WorkflowValue> items)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is not WorkflowStringValue text || !values.Add(text.Value)) return false;
        }
        return true;
    }

    private static bool MatchesFormat(string value, string? format) => format switch
    {
        null => true,
        "date" => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "date-time" => MatchesRfc3339DateTime(value),
        "email" => MatchesAsciiEmail(value),
        "uuid" => Guid.TryParseExact(value, "D", out _),
        _ => false,
    };

    private static bool MatchesRfc3339DateTime(string value)
    {
        if (value.Length is < 20 or > 35 || value[4] != '-' || value[7] != '-' || value[10] != 'T' ||
            value[13] != ':' || value[16] != ':')
            return false;
        var zoneStart = value.EndsWith('Z')
            ? value.Length - 1
            : value.Length >= 25 && value[^6] is ('+' or '-') && value[^3] == ':'
                ? value.Length - 6
                : -1;
        if (zoneStart < 19) return false;
        if (zoneStart > 19 &&
            (value[19] != '.' || zoneStart - 20 is < 1 or > 7 ||
             value.AsSpan(20, zoneStart - 20).ContainsAnyExceptInRange('0', '9')))
            return false;
        if (value.AsSpan(0, 4).ContainsAnyExceptInRange('0', '9') ||
            value.AsSpan(5, 2).ContainsAnyExceptInRange('0', '9') ||
            value.AsSpan(8, 2).ContainsAnyExceptInRange('0', '9') ||
            value.AsSpan(11, 2).ContainsAnyExceptInRange('0', '9') ||
            value.AsSpan(14, 2).ContainsAnyExceptInRange('0', '9') ||
            value.AsSpan(17, 2).ContainsAnyExceptInRange('0', '9'))
            return false;
        if (zoneStart < value.Length - 1 &&
            (value.AsSpan(zoneStart + 1, 2).ContainsAnyExceptInRange('0', '9') ||
             value.AsSpan(zoneStart + 4, 2).ContainsAnyExceptInRange('0', '9')))
            return false;
        var utc = value.EndsWith('Z');
        var fractionalDigits = zoneStart == 19 ? 0 : zoneStart - 20;
        var format = "yyyy-MM-dd'T'HH:mm:ss" +
                     (fractionalDigits == 0 ? string.Empty : "." + new string('f', fractionalDigits)) +
                     (utc ? "'Z'" : "zzz");
        return DateTimeOffset.TryParseExact(
            value,
            format,
            CultureInfo.InvariantCulture,
            utc ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal : DateTimeStyles.None,
            out _);
    }

    private static bool MatchesAsciiEmail(string value)
    {
        if (value.Length is < 3 or > 254 || value.Any(character => character > 0x7f || char.IsControl(character)))
            return false;
        var at = value.IndexOf('@');
        if (at is < 1 or > 64 || at != value.LastIndexOf('@') || at == value.Length - 1) return false;
        var local = value.AsSpan(0, at);
        if (local[0] == '.' || local[^1] == '.' || value.Contains("..", StringComparison.Ordinal)) return false;
        foreach (var character in local)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '!' or '#' or '$' or '%' or '&' or
                '\'' or '*' or '+' or '-' or '/' or '=' or '?' or '^' or '_' or '`' or '{' or '|' or '}' or '~'))
                return false;
        }
        var domain = value.AsSpan(at + 1);
        if (domain.Length > 253) return false;
        foreach (var label in domain.ToString().Split('.'))
        {
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                return false;
        }
        return true;
    }

    private static bool IsCanonicalArrayIndex(string token) =>
        token == "0" || token.Length > 0 && token[0] is >= '1' and <= '9' && token.All(char.IsAsciiDigit);

    private static bool TryDecodePointer(string pointer, out IReadOnlyList<string> tokens)
    {
        tokens = [];
        if (pointer.Length == 0) return true;
        if (pointer[0] != '/') return false;
        var result = new List<string>();
        foreach (var encoded in pointer[1..].Split('/'))
        {
            var builder = new System.Text.StringBuilder(encoded.Length);
            for (var index = 0; index < encoded.Length; index++)
            {
                if (encoded[index] != '~')
                {
                    builder.Append(encoded[index]);
                    continue;
                }
                if (++index >= encoded.Length || encoded[index] is not ('0' or '1')) return false;
                builder.Append(encoded[index] == '0' ? '~' : '/');
            }
            result.Add(builder.ToString());
        }
        tokens = result;
        return true;
    }
}
