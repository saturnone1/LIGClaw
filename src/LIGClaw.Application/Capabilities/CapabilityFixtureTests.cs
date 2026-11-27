using System.Text;
using System.Text.Json;

namespace LIGClaw.Application.Capabilities;

public sealed record CapabilityFixtureTestCase(
    string Name,
    string Expected,
    string CanonicalInputJson);

public sealed record CapabilityWorkflowSemanticToolResult(
    string StepId,
    string ToolName,
    string CanonicalArgumentsJson,
    string CanonicalOutputJson);

public sealed record CapabilityWorkflowSemanticExpectation(
    string Status,
    string? CanonicalOutputJson,
    IReadOnlyList<string> VisitedSteps);

public sealed record CapabilityWorkflowSemanticTestCase(
    string Name,
    string CanonicalInputJson,
    IReadOnlyList<CapabilityWorkflowSemanticToolResult> ToolResults,
    CapabilityWorkflowSemanticExpectation Expected);

public sealed record CapabilityFixtureTestValidation(
    bool IsValid,
    IReadOnlyList<CapabilityFixtureTestCase> Cases,
    IReadOnlyList<CapabilityWorkflowSemanticTestCase> WorkflowSemanticCases,
    string? ErrorCode = null)
{
    public int CaseCount => Cases.Count + WorkflowSemanticCases.Count;

    public static CapabilityFixtureTestValidation Invalid(string errorCode) =>
        new(false, [], [], errorCode);
}

public static class CapabilityFixtureTestPolicy
{
    public const int MaximumCases = 16;
    public const int MaximumNameCharacters = 80;
    public const int MaximumSemanticToolResults = 20;
    public const int MaximumSemanticVisitedSteps = 20;
    private const int MaximumInputBytes = 32 * 1_024;
    private const int MaximumToolValueBytes = 64 * 1_024;
    private const int MaximumOutputBytes = 100 * 1_024;

    public static CapabilityFixtureTestValidation Validate(string canonicalTestsJson)
    {
        if (string.IsNullOrWhiteSpace(canonicalTestsJson) ||
            canonicalTestsJson.Length > LIGClaw.Domain.CapabilityDefinitionPolicy.MaximumTestsCharacters)
            return CapabilityFixtureTestValidation.Invalid("capability.tests.out_of_bounds");

        try
        {
            using var document = JsonDocument.Parse(canonicalTestsJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (HasAmbiguousProperties(document.RootElement))
                return CapabilityFixtureTestValidation.Invalid("capability.tests.case_invalid");
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => ValidateSchemaCases(document.RootElement),
                JsonValueKind.Object => ValidateWorkflowSemanticCases(document.RootElement),
                _ => CapabilityFixtureTestValidation.Invalid("capability.tests.root_invalid"),
            };
        }
        catch (JsonException)
        {
            return CapabilityFixtureTestValidation.Invalid("capability.tests.json_invalid");
        }
    }

    private static CapabilityFixtureTestValidation ValidateSchemaCases(JsonElement root)
    {
        if (root.GetArrayLength() > MaximumCases)
            return CapabilityFixtureTestValidation.Invalid("capability.tests.root_invalid");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cases = new List<CapabilityFixtureTestCase>(root.GetArrayLength());
        foreach (var item in root.EnumerateArray())
        {
            string? expected = null;
            if (item.ValueKind != JsonValueKind.Object || !TryReadFields(item, out var fields) ||
                !fields.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["name", "input", "expected"]) ||
                !TryReadName(fields["name"], names, out var name) ||
                fields["input"].ValueKind != JsonValueKind.Object ||
                fields["expected"].ValueKind != JsonValueKind.String ||
                (expected = fields["expected"].GetString()) is not ("accepted" or "rejected"))
                return CapabilityFixtureTestValidation.Invalid("capability.tests.case_invalid");

            cases.Add(new CapabilityFixtureTestCase(name!, expected, fields["input"].GetRawText()));
        }

        return new CapabilityFixtureTestValidation(true, cases.AsReadOnly(), []);
    }

    private static CapabilityFixtureTestValidation ValidateWorkflowSemanticCases(JsonElement root)
    {
        if (!TryReadFields(root, out var rootFields) ||
            !rootFields.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["schemaVersion", "cases"]) ||
            rootFields["schemaVersion"].ValueKind != JsonValueKind.Number ||
            !rootFields["schemaVersion"].TryGetInt32(out var schemaVersion) || schemaVersion != 2 ||
            rootFields["cases"].ValueKind != JsonValueKind.Array ||
            rootFields["cases"].GetArrayLength() is < 1 or > MaximumCases)
            return CapabilityFixtureTestValidation.Invalid("capability.tests.root_invalid");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cases = new List<CapabilityWorkflowSemanticTestCase>(rootFields["cases"].GetArrayLength());
        foreach (var item in rootFields["cases"].EnumerateArray())
        {
            if (!TryReadSemanticCase(item, names, out var testCase) || testCase is null)
                return CapabilityFixtureTestValidation.Invalid("capability.tests.semantic_case_invalid");
            cases.Add(testCase);
        }
        return new CapabilityFixtureTestValidation(true, [], cases.AsReadOnly());
    }

    private static bool TryReadSemanticCase(
        JsonElement item,
        HashSet<string> names,
        out CapabilityWorkflowSemanticTestCase? testCase)
    {
        testCase = null;
        if (item.ValueKind != JsonValueKind.Object || !TryReadFields(item, out var fields) ||
            !fields.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["name", "input", "toolResults", "expected"]) ||
            !TryReadName(fields["name"], names, out var name) ||
            fields["input"].ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(fields["input"].GetRawText()) > MaximumInputBytes ||
            fields["toolResults"].ValueKind != JsonValueKind.Array ||
            fields["toolResults"].GetArrayLength() > MaximumSemanticToolResults ||
            fields["expected"].ValueKind != JsonValueKind.Object)
            return false;

        var toolResults = new List<CapabilityWorkflowSemanticToolResult>();
        var toolStepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var toolResult in fields["toolResults"].EnumerateArray())
        {
            if (toolResult.ValueKind != JsonValueKind.Object || !TryReadFields(toolResult, out var toolFields) ||
                !toolFields.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["stepId", "tool", "arguments", "output"]) ||
                !TryReadIdentifier(toolFields["stepId"], out var stepId) || !toolStepIds.Add(stepId!) ||
                !TryReadToolName(toolFields["tool"], out var toolName) ||
                toolFields["arguments"].ValueKind != JsonValueKind.Object ||
                toolFields["output"].ValueKind != JsonValueKind.Object ||
                Encoding.UTF8.GetByteCount(toolFields["arguments"].GetRawText()) > MaximumToolValueBytes ||
                Encoding.UTF8.GetByteCount(toolFields["output"].GetRawText()) > MaximumToolValueBytes)
                return false;
            toolResults.Add(new CapabilityWorkflowSemanticToolResult(
                stepId!,
                toolName!,
                toolFields["arguments"].GetRawText(),
                toolFields["output"].GetRawText()));
        }

        if (!TryReadFields(fields["expected"], out var expectedFields) ||
            !expectedFields.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["status", "output", "visitedSteps"]) ||
            expectedFields["status"].ValueKind != JsonValueKind.String ||
            expectedFields["status"].GetString() is not { } status ||
            status is not ("completed" or "needs_information" or "failed" or "cancelled" or "attention_required") ||
            expectedFields["visitedSteps"].ValueKind != JsonValueKind.Array ||
            expectedFields["visitedSteps"].GetArrayLength() is < 1 or > MaximumSemanticVisitedSteps)
            return false;

        string? expectedOutput = null;
        if (status == "completed")
        {
            if (expectedFields["output"].ValueKind != JsonValueKind.Object ||
                Encoding.UTF8.GetByteCount(expectedFields["output"].GetRawText()) > MaximumOutputBytes)
                return false;
            expectedOutput = expectedFields["output"].GetRawText();
        }
        else if (expectedFields["output"].ValueKind != JsonValueKind.Null)
        {
            return false;
        }

        var visitedSteps = new List<string>();
        var visitedSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in expectedFields["visitedSteps"].EnumerateArray())
        {
            if (!TryReadIdentifier(step, out var stepId) || !visitedSet.Add(stepId!)) return false;
            visitedSteps.Add(stepId!);
        }
        if (toolStepIds.Any(stepId => !visitedSet.Contains(stepId))) return false;

        testCase = new CapabilityWorkflowSemanticTestCase(
            name!,
            fields["input"].GetRawText(),
            toolResults.AsReadOnly(),
            new CapabilityWorkflowSemanticExpectation(status, expectedOutput, visitedSteps.AsReadOnly()));
        return true;
    }

    private static bool TryReadName(JsonElement value, HashSet<string> names, out string? name)
    {
        name = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return name is not null && name.Length is >= 1 and <= MaximumNameCharacters &&
               !string.IsNullOrWhiteSpace(name) && name.Trim() == name &&
               !name.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029') &&
               names.Add(name);
    }

    private static bool TryReadIdentifier(JsonElement value, out string? identifier)
    {
        identifier = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return identifier is not null && identifier.Length is >= 1 and <= 64 &&
               identifier[0] is >= 'a' and <= 'z' &&
               identifier.AsSpan(1).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789_-") < 0;
    }

    private static bool TryReadToolName(JsonElement value, out string? toolName)
    {
        toolName = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (toolName is null || toolName.Length is < 3 or > 128) return false;
        var parts = toolName.Split('.');
        return parts.Length >= 3 && parts[^1].Length >= 2 && parts[^1][0] == 'v' &&
               parts[^1][1] is >= '1' and <= '9' && parts[^1].AsSpan(1).IndexOfAnyExcept("0123456789") < 0 &&
               parts[..^1].All(part => part.Length > 0 && part[0] is >= 'a' and <= 'z' &&
                   part.AsSpan(1).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789_") < 0);
    }

    private static bool HasAmbiguousProperties(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var exact = new HashSet<string>(StringComparer.Ordinal);
                var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    if (!exact.Add(property.Name) || !insensitive.Add(property.Name) ||
                        HasAmbiguousProperties(property.Value))
                        return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (HasAmbiguousProperties(item)) return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool TryReadFields(
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
}
