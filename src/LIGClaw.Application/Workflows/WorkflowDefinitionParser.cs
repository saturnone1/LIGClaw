using System.Globalization;
using System.Text;
using System.Text.Json;
using LIGClaw.Domain;

namespace LIGClaw.Application.Workflows;

public sealed record WorkflowParseIssue(string Code, string Path);

public sealed record WorkflowParseResult(
    WorkflowDefinition? Definition,
    string? CanonicalJson,
    IReadOnlyList<WorkflowParseIssue> Issues)
{
    public bool Succeeded => Definition is not null && Issues.Count == 0;
}

public sealed class WorkflowDefinitionParser
{
    public const int MaximumDefinitionBytes = 262_144;
    public const int MaximumSteps = 20;
    private const int MaximumTemplateDepth = 8;
    private const int MaximumTemplateNodes = 2_048;
    internal const int MaximumObjectProperties = 64;
    internal const int MaximumArrayItems = 64;
    internal const int MaximumStringCharacters = 4_096;
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ConditionOperators =
    [
        "equals",
        "notEquals",
        "exists",
        "isTrue",
        "isFalse",
    ];
    private static readonly HashSet<string> TransformOperations =
    [
        "concat",
        "trim",
        "lowerInvariant",
        "upperInvariant",
        "toNumber",
        "toDate",
    ];

    public WorkflowParseResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Failure("workflow.json.invalid", "/");

        try
        {
            if (json.Length > MaximumDefinitionBytes)
                return Failure("workflow.document.too_large", "/");
            var byteCount = StrictUtf8.GetByteCount(json);
            if (byteCount > MaximumDefinitionBytes)
                return Failure("workflow.document.too_large", "/");
            var bytes = new byte[byteCount];
            _ = StrictUtf8.GetBytes(json, bytes);

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            var root = ReadFields(
                document.RootElement,
                "/",
                ["schemaVersion", "entryStepId", "steps"],
                ["schemaVersion", "entryStepId", "steps"]);
            var schemaVersion = ReadInt32(root, "schemaVersion", "/schemaVersion");
            if (schemaVersion != 1)
                throw new WorkflowParseFailure("workflow.schema_version.unsupported", "/schemaVersion");
            var entryStepId = ReadIdentifier(root, "entryStepId", "/entryStepId");
            var stepArray = root["steps"];
            if (stepArray.ValueKind != JsonValueKind.Array ||
                stepArray.GetArrayLength() is < 1 or > MaximumSteps)
                throw new WorkflowParseFailure("workflow.step.count_out_of_bounds", "/steps");

            var budget = new TemplateBudget();
            var steps = new List<WorkflowStep>(stepArray.GetArrayLength());
            var stepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var element in stepArray.EnumerateArray())
            {
                var path = $"/steps/{index}";
                var step = ParseStep(element, path, budget);
                if (!stepIds.Add(step.Id))
                    throw new WorkflowParseFailure("workflow.step.id_collision", $"{path}/id");
                steps.Add(step);
                index++;
            }

            return new WorkflowParseResult(
                new WorkflowDefinition(schemaVersion, entryStepId, steps),
                Canonicalize(document.RootElement),
                []);
        }
        catch (WorkflowParseFailure failure)
        {
            return Failure(failure.Code, failure.Path);
        }
        catch (EncoderFallbackException)
        {
            return Failure("workflow.json.invalid_unicode", "/");
        }
        catch (JsonException)
        {
            return Failure("workflow.json.invalid", "/");
        }
        catch (FormatException)
        {
            return Failure("workflow.json.invalid", "/");
        }
        catch (OverflowException)
        {
            return Failure("workflow.json.invalid", "/");
        }
    }

    private static WorkflowStep ParseStep(
        JsonElement element,
        string path,
        TemplateBudget budget)
    {
        var fields = ReadFields(element, path);
        if (!fields.ContainsKey("id"))
            throw new WorkflowParseFailure("workflow.object.missing_property", $"{path}/id");
        if (!fields.ContainsKey("type"))
            throw new WorkflowParseFailure("workflow.object.missing_property", $"{path}/type");
        var id = ReadIdentifier(fields, "id", $"{path}/id");
        var type = ReadString(fields, "type", $"{path}/type", 1, 32);
        return type switch
        {
            "input.map" => ParseInputMap(id, fields, path, budget),
            "tool.call" => ParseToolCall(id, fields, path, budget),
            "condition" => ParseCondition(id, fields, path, budget),
            "transform" => ParseTransform(id, fields, path, budget),
            "needs_information" => ParseNeedsInformation(id, fields, path),
            "output" => ParseOutput(id, fields, path, budget),
            _ => throw new WorkflowParseFailure("workflow.step.type_unsupported", $"{path}/type"),
        };
    }

    private static WorkflowStep ParseInputMap(
        string id,
        IReadOnlyDictionary<string, JsonElement> fields,
        string path,
        TemplateBudget budget)
    {
        ValidateFieldSet(
            fields,
            path,
            ["id", "type", "value", "saveAs", "next"],
            ["id", "type", "value", "saveAs", "next"]);
        return new WorkflowInputMapStep(
            id,
            ParseValue(fields["value"], $"{path}/value", 0, budget),
            ReadIdentifier(fields, "saveAs", $"{path}/saveAs"),
            ReadIdentifier(fields, "next", $"{path}/next"));
    }

    private static WorkflowStep ParseToolCall(
        string id,
        IReadOnlyDictionary<string, JsonElement> fields,
        string path,
        TemplateBudget budget)
    {
        ValidateFieldSet(
            fields,
            path,
            ["id", "type", "tool", "arguments", "maxRetries", "next"],
            ["id", "type", "tool", "arguments", "next"]);
        var tool = ReadString(fields, "tool", $"{path}/tool", 1, 128);
        if (!IsCanonicalToolName(tool))
            throw new WorkflowParseFailure("workflow.tool.name_invalid", $"{path}/tool");
        var arguments = ParseValue(fields["arguments"], $"{path}/arguments", 0, budget);
        if (arguments is not WorkflowObjectValue objectArguments)
            throw new WorkflowParseFailure("workflow.tool.arguments_not_object", $"{path}/arguments");
        var maximumRetries = fields.ContainsKey("maxRetries")
            ? ReadInt32(fields, "maxRetries", $"{path}/maxRetries")
            : 0;
        if (maximumRetries is < 0 or > 3)
            throw new WorkflowParseFailure("workflow.tool.retries_out_of_bounds", $"{path}/maxRetries");
        return new WorkflowToolCallStep(
            id,
            tool,
            objectArguments,
            maximumRetries,
            ReadIdentifier(fields, "next", $"{path}/next"));
    }

    private static WorkflowStep ParseCondition(
        string id,
        IReadOnlyDictionary<string, JsonElement> fields,
        string path,
        TemplateBudget budget)
    {
        ValidateFieldSet(
            fields,
            path,
            ["id", "type", "left", "operator", "right", "then", "else"],
            ["id", "type", "left", "operator", "then", "else"]);
        var @operator = ReadString(fields, "operator", $"{path}/operator", 1, 32);
        if (!ConditionOperators.Contains(@operator))
            throw new WorkflowParseFailure("workflow.condition.operator_unsupported", $"{path}/operator");
        var requiresRight = @operator is "equals" or "notEquals";
        if (requiresRight != fields.ContainsKey("right"))
            throw new WorkflowParseFailure("workflow.condition.right_invalid", $"{path}/right");
        return new WorkflowConditionStep(
            id,
            ParseValue(fields["left"], $"{path}/left", 0, budget),
            @operator,
            requiresRight ? ParseValue(fields["right"], $"{path}/right", 0, budget) : null,
            ReadIdentifier(fields, "then", $"{path}/then"),
            ReadIdentifier(fields, "else", $"{path}/else"));
    }

    private static WorkflowStep ParseTransform(
        string id,
        IReadOnlyDictionary<string, JsonElement> fields,
        string path,
        TemplateBudget budget)
    {
        ValidateFieldSet(
            fields,
            path,
            ["id", "type", "operation", "operands", "saveAs", "next"],
            ["id", "type", "operation", "operands", "saveAs", "next"]);
        var operation = ReadString(fields, "operation", $"{path}/operation", 1, 32);
        if (!TransformOperations.Contains(operation))
            throw new WorkflowParseFailure("workflow.transform.operation_unsupported", $"{path}/operation");
        var operandArray = fields["operands"];
        if (operandArray.ValueKind != JsonValueKind.Array || operandArray.GetArrayLength() is < 1 or > 16)
            throw new WorkflowParseFailure("workflow.transform.operands_out_of_bounds", $"{path}/operands");
        var operands = new List<WorkflowValue>(operandArray.GetArrayLength());
        var index = 0;
        foreach (var operand in operandArray.EnumerateArray())
        {
            operands.Add(ParseValue(operand, $"{path}/operands/{index}", 0, budget));
            index++;
        }
        return new WorkflowTransformStep(
            id,
            operation,
            operands,
            ReadIdentifier(fields, "saveAs", $"{path}/saveAs"),
            ReadIdentifier(fields, "next", $"{path}/next"));
    }

    private static WorkflowStep ParseNeedsInformation(
        string id,
        IReadOnlyDictionary<string, JsonElement> fields,
        string path)
    {
        ValidateFieldSet(
            fields,
            path,
            ["id", "type", "requiredInputs"],
            ["id", "type", "requiredInputs"]);
        var inputArray = fields["requiredInputs"];
        if (inputArray.ValueKind != JsonValueKind.Array || inputArray.GetArrayLength() is < 1 or > 16)
            throw new WorkflowParseFailure("workflow.needs_information.inputs_out_of_bounds", $"{path}/requiredInputs");
        var requiredInputs = new List<string>(inputArray.GetArrayLength());
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in inputArray.EnumerateArray())
        {
            var pointer = ReadPointer(element, $"{path}/requiredInputs/{index}", allowRoot: false);
            if (!unique.Add(pointer))
                throw new WorkflowParseFailure(
                    "workflow.needs_information.input_duplicate",
                    $"{path}/requiredInputs/{index}");
            requiredInputs.Add(pointer);
            index++;
        }
        return new WorkflowNeedsInformationStep(id, requiredInputs);
    }

    private static WorkflowStep ParseOutput(
        string id,
        IReadOnlyDictionary<string, JsonElement> fields,
        string path,
        TemplateBudget budget)
    {
        ValidateFieldSet(fields, path, ["id", "type", "value"], ["id", "type", "value"]);
        return new WorkflowOutputStep(
            id,
            ParseValue(fields["value"], $"{path}/value", 0, budget));
    }

    private static WorkflowValue ParseValue(
        JsonElement element,
        string path,
        int depth,
        TemplateBudget budget)
    {
        budget.Nodes++;
        if (depth > MaximumTemplateDepth || budget.Nodes > MaximumTemplateNodes)
            throw new WorkflowParseFailure("workflow.template.bounds_exceeded", path);
        return element.ValueKind switch
        {
            JsonValueKind.Null => WorkflowNullValue.Instance,
            JsonValueKind.True => new WorkflowBooleanValue(true),
            JsonValueKind.False => new WorkflowBooleanValue(false),
            JsonValueKind.String => ParseStringValue(element, path),
            JsonValueKind.Number => ParseNumberValue(element, path),
            JsonValueKind.Array => ParseArrayValue(element, path, depth, budget),
            JsonValueKind.Object => ParseObjectValue(element, path, depth, budget),
            _ => throw new WorkflowParseFailure("workflow.template.value_invalid", path),
        };
    }

    private static WorkflowValue ParseStringValue(JsonElement element, string path)
    {
        var value = element.GetString() ?? string.Empty;
        if (value.Length > MaximumStringCharacters)
            throw new WorkflowParseFailure("workflow.template.string_too_long", path);
        if (ContainsForbiddenTextCharacter(value))
            throw new WorkflowParseFailure("workflow.template.string_invalid", path);
        return new WorkflowStringValue(value);
    }

    private static WorkflowValue ParseNumberValue(JsonElement element, string path)
    {
        var raw = element.GetRawText();
        if (raw.Length > 64)
            throw new WorkflowParseFailure("workflow.template.number_invalid", path);

        var integerClassification = ClassifyExactInteger(raw, out var canonicalInteger);
        if (integerClassification == ExactIntegerClassification.UnsafeOrInvalid)
            throw new WorkflowParseFailure("workflow.template.number_invalid", path);
        if (integerClassification == ExactIntegerClassification.Safe)
            return new WorkflowNumberValue(canonicalInteger, true);

        if (!element.TryGetDouble(out var number) || !double.IsFinite(number) ||
            number == 0 || Math.Truncate(number) == number)
            throw new WorkflowParseFailure("workflow.template.number_invalid", path);
        return new WorkflowNumberValue(number.ToString("R", CultureInfo.InvariantCulture), false);
    }

    public static bool TryCanonicalizeRuntimeNumber(
        string raw,
        out WorkflowNumberValue? canonical)
    {
        canonical = null;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 64 || raw.AsSpan().Trim().Length != raw.Length)
            return false;
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 2,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Number) return false;
            canonical = ParseNumberValue(document.RootElement, "/") as WorkflowNumberValue;
            return canonical is not null;
        }
        catch (Exception exception) when (exception is JsonException or WorkflowParseFailure or
                                          FormatException or OverflowException)
        {
            return false;
        }
    }

    private static ExactIntegerClassification ClassifyExactInteger(
        string raw,
        out string canonicalValue)
    {
        canonicalValue = string.Empty;
        var exponentIndex = raw.IndexOfAny(['e', 'E']);
        var significand = exponentIndex < 0 ? raw : raw[..exponentIndex];
        var isNegative = significand[0] == '-';
        if (isNegative) significand = significand[1..];

        var decimalIndex = significand.IndexOf('.');
        var fractionalDigits = decimalIndex < 0 ? 0 : significand.Length - decimalIndex - 1;
        var digits = decimalIndex < 0
            ? significand
            : string.Concat(significand.AsSpan(0, decimalIndex), significand.AsSpan(decimalIndex + 1));
        var firstSignificantDigit = 0;
        while (firstSignificantDigit < digits.Length && digits[firstSignificantDigit] == '0')
            firstSignificantDigit++;
        if (firstSignificantDigit == digits.Length)
        {
            canonicalValue = "0";
            return ExactIntegerClassification.Safe;
        }

        long exponent = 0;
        if (exponentIndex >= 0 &&
            !long.TryParse(
                raw.AsSpan(exponentIndex + 1),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out exponent))
            return ExactIntegerClassification.UnsafeOrInvalid;

        long scale;
        try
        {
            scale = checked(exponent - fractionalDigits);
        }
        catch (OverflowException)
        {
            return ExactIntegerClassification.UnsafeOrInvalid;
        }

        var coefficient = digits[firstSignificantDigit..];
        string integerDigits;
        if (scale < 0)
        {
            if (scale <= -coefficient.Length)
                return ExactIntegerClassification.NonInteger;
            var integerLength = coefficient.Length + (int)scale;
            if (coefficient.AsSpan(integerLength).ContainsAnyExcept('0'))
                return ExactIntegerClassification.NonInteger;
            integerDigits = coefficient[..integerLength];
        }
        else
        {
            const int maximumSafeIntegerDigits = 16;
            if (scale > maximumSafeIntegerDigits || coefficient.Length + scale > maximumSafeIntegerDigits)
                return ExactIntegerClassification.UnsafeOrInvalid;
            integerDigits = coefficient + new string('0', (int)scale);
        }

        const string maximumSafeIntegerText = "9007199254740991";
        if (integerDigits.Length > maximumSafeIntegerText.Length ||
            integerDigits.Length == maximumSafeIntegerText.Length &&
            string.CompareOrdinal(integerDigits, maximumSafeIntegerText) > 0)
            return ExactIntegerClassification.UnsafeOrInvalid;

        canonicalValue = isNegative ? $"-{integerDigits}" : integerDigits;
        return ExactIntegerClassification.Safe;
    }

    private static WorkflowValue ParseArrayValue(
        JsonElement element,
        string path,
        int depth,
        TemplateBudget budget)
    {
        if (element.GetArrayLength() > MaximumArrayItems)
            throw new WorkflowParseFailure("workflow.template.array_too_large", path);
        var values = new List<WorkflowValue>(element.GetArrayLength());
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            values.Add(ParseValue(item, $"{path}/{index}", depth + 1, budget));
            index++;
        }
        return new WorkflowArrayValue(values);
    }

    private static WorkflowValue ParseObjectValue(
        JsonElement element,
        string path,
        int depth,
        TemplateBudget budget)
    {
        var fields = ReadFields(element, path);
        if (fields.Count > MaximumObjectProperties)
            throw new WorkflowParseFailure("workflow.template.object_too_large", path);
        if (fields.TryGetValue("$bind", out var binding))
        {
            if (fields.Count != 1)
                throw new WorkflowParseFailure("workflow.binding.shape_invalid", path);
            return ParseBinding(binding, $"{path}/$bind");
        }

        var properties = new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);
        foreach (var (name, value) in fields)
        {
            if (name.Length is < 1 or > 80 || name[0] == '$' || ContainsForbiddenTextCharacter(name))
                throw new WorkflowParseFailure("workflow.template.property_invalid", path);
            properties.Add(name, ParseValue(value, $"{path.TrimEnd('/')}/*", depth + 1, budget));
        }
        return new WorkflowObjectValue(properties);
    }

    private static WorkflowBindingValue ParseBinding(JsonElement element, string path)
    {
        var fields = ReadFields(element, path);
        if (!fields.ContainsKey("source"))
            throw new WorkflowParseFailure("workflow.object.missing_property", $"{path}/source");
        var source = ReadString(fields, "source", $"{path}/source", 1, 16);
        return source switch
        {
            "input" => ParseInputBinding(fields, path),
            "step" => ParseStepBinding(fields, path),
            "variable" => ParseVariableBinding(fields, path),
            _ => throw new WorkflowParseFailure("workflow.binding.source_unsupported", $"{path}/source"),
        };
    }

    private static WorkflowBindingValue ParseInputBinding(
        IReadOnlyDictionary<string, JsonElement> fields,
        string path)
    {
        ValidateFieldSet(fields, path, ["source", "path"], ["source", "path"]);
        return new WorkflowBindingValue(
            WorkflowBindingSource.Input,
            null,
            null,
            ReadPointer(fields["path"], $"{path}/path", allowRoot: true));
    }

    private static WorkflowBindingValue ParseStepBinding(
        IReadOnlyDictionary<string, JsonElement> fields,
        string path)
    {
        ValidateFieldSet(fields, path, ["source", "stepId", "path"], ["source", "stepId", "path"]);
        return new WorkflowBindingValue(
            WorkflowBindingSource.Step,
            ReadIdentifier(fields, "stepId", $"{path}/stepId"),
            null,
            ReadPointer(fields["path"], $"{path}/path", allowRoot: true));
    }

    private static WorkflowBindingValue ParseVariableBinding(
        IReadOnlyDictionary<string, JsonElement> fields,
        string path)
    {
        ValidateFieldSet(fields, path, ["source", "name", "path"], ["source", "name", "path"]);
        return new WorkflowBindingValue(
            WorkflowBindingSource.Variable,
            null,
            ReadIdentifier(fields, "name", $"{path}/name"),
            ReadPointer(fields["path"], $"{path}/path", allowRoot: true));
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadFields(
        JsonElement element,
        string path,
        IReadOnlyCollection<string>? allowed = null,
        IReadOnlyCollection<string>? required = null)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new WorkflowParseFailure("workflow.object.expected", path);
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (!fields.TryAdd(property.Name, property.Value))
                throw new WorkflowParseFailure(
                    "workflow.object.duplicate_property",
                    $"{path.TrimEnd('/')}/*");
            if (!insensitive.Add(property.Name))
                throw new WorkflowParseFailure(
                    "workflow.object.case_collision",
                    $"{path.TrimEnd('/')}/*");
        }
        if (allowed is not null && required is not null)
            ValidateFieldSet(fields, path, allowed, required);
        return fields;
    }

    private static void ValidateFieldSet(
        IReadOnlyDictionary<string, JsonElement> fields,
        string path,
        IReadOnlyCollection<string> allowed,
        IReadOnlyCollection<string> required)
    {
        var allowedSet = allowed as HashSet<string> ?? new HashSet<string>(allowed, StringComparer.Ordinal);
        var unknown = fields.Keys.FirstOrDefault(name => !allowedSet.Contains(name));
        if (unknown is not null)
            throw new WorkflowParseFailure(
                "workflow.object.unknown_property",
                $"{path.TrimEnd('/')}/*");
        var missing = required.FirstOrDefault(name => !fields.ContainsKey(name));
        if (missing is not null)
            throw new WorkflowParseFailure(
                "workflow.object.missing_property",
                $"{path.TrimEnd('/')}/{EscapePointerToken(missing)}");
    }

    private static string ReadIdentifier(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        string path)
    {
        var value = ReadString(fields, name, path, 1, 64);
        if (!IsIdentifier(value))
            throw new WorkflowParseFailure("workflow.identifier.invalid", path);
        return value;
    }

    private static string ReadString(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        string path,
        int minimumLength,
        int maximumLength)
    {
        if (!fields.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String)
            throw new WorkflowParseFailure("workflow.string.invalid", path);
        var value = element.GetString();
        if (value is null || value.Length < minimumLength || value.Length > maximumLength ||
            ContainsForbiddenTextCharacter(value))
            throw new WorkflowParseFailure("workflow.string.invalid", path);
        return value;
    }

    private static int ReadInt32(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        string path)
    {
        if (!fields.TryGetValue(name, out var element) || !element.TryGetInt32(out var value))
            throw new WorkflowParseFailure("workflow.integer.invalid", path);
        return value;
    }

    private static string ReadPointer(JsonElement element, string path, bool allowRoot)
    {
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } pointer ||
            pointer.Length > 512 || !IsJsonPointer(pointer) || !allowRoot && pointer.Length == 0)
            throw new WorkflowParseFailure("workflow.pointer.invalid", path);
        return pointer;
    }

    private static bool IsIdentifier(string value) =>
        value.Length is >= 1 and <= 64 && value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' ||
                               char.IsAsciiDigit(character) || character is '_' or '-');

    private static bool IsCanonicalToolName(string value) =>
        value.Length is >= 1 and <= 128 && value[0] is >= 'a' and <= 'z' && value.Contains('.') &&
        value.All(character => character is >= 'a' and <= 'z' ||
                               char.IsAsciiDigit(character) || character is '_' or '.');

    private static bool IsJsonPointer(string value)
    {
        if (value.Length == 0) return true;
        if (value[0] != '/') return false;
        var segments = 1;
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '/')
            {
                segments++;
                if (segments > 32) return false;
            }
            if (character != '~') continue;
            if (++index >= value.Length || value[index] is not ('0' or '1')) return false;
        }
        return true;
    }

    private static string EscapePointerToken(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static bool ContainsForbiddenTextCharacter(string value) => value.EnumerateRunes().Any(rune =>
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control && rune.Value is not ('\t' or '\r' or '\n') ||
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format);

    private static string Canonicalize(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        }))
        {
            WriteCanonicalValue(writer, root);
        }
        return StrictUtf8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalValue(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalValue(writer, property.Value);
                }
                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonicalValue(writer, item);
                writer.WriteEndArray();
                return;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                return;
            case JsonValueKind.Number:
                var number = (WorkflowNumberValue)ParseNumberValue(element, "/");
                writer.WriteRawValue(number.CanonicalValue);
                return;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                return;
            default:
                throw new WorkflowParseFailure("workflow.json.invalid", "/");
        }
    }

    private static WorkflowParseResult Failure(string code, string path) =>
        new(null, null, [new WorkflowParseIssue(code, path)]);

    private sealed class TemplateBudget
    {
        public int Nodes { get; set; }
    }

    private enum ExactIntegerClassification
    {
        NonInteger,
        Safe,
        UnsafeOrInvalid,
    }

    private sealed class WorkflowParseFailure(string code, string path) : Exception
    {
        public string Code { get; } = code;
        public string Path { get; } = path;
    }
}
