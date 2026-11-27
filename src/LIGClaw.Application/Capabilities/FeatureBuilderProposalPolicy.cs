using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LIGClaw.Application.Connections;
using LIGClaw.Application.Workflows;
using LIGClaw.Domain;

namespace LIGClaw.Application.Capabilities;

public sealed record FeatureBuilderProposalScanResult(
    bool IsValid,
    CapabilityVersionPayload? Candidate = null,
    CapabilityContentHash? CandidateContentHash = null,
    string? ErrorCode = null)
{
    public static FeatureBuilderProposalScanResult Invalid(string errorCode) =>
        new(false, ErrorCode: errorCode);
}

public interface IFeatureBuilderProposalScanner
{
    FeatureBuilderProposalScanResult Scan(
        IReadOnlyList<FeatureBuilderRawProposalArtifact> artifacts,
        FeatureBuilderRevisionTarget? revisionTarget);
}

public sealed partial class FeatureBuilderProposalScanner(
    ICapabilityDependencyCatalog dependencies,
    IWorkflowToolMetadataCatalog? workflowTools = null) : IFeatureBuilderProposalScanner
{
    private static readonly IReadOnlyDictionary<string, string> ArtifactMediaTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["manifest.json"] = "application/json",
            ["instructions.md"] = "text/markdown",
            ["input.schema.json"] = "application/json",
            ["settings.schema.json"] = "application/json",
            ["workflow.json"] = "application/json",
            ["tests.json"] = "application/json",
        };

    public FeatureBuilderProposalScanResult Scan(
        IReadOnlyList<FeatureBuilderRawProposalArtifact> artifacts,
        FeatureBuilderRevisionTarget? revisionTarget)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (artifacts.Count is < 4 or > 6)
            return Invalid("feature_builder.proposal.artifact_count_invalid");

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            if (!ArtifactMediaTypes.TryGetValue(artifact.Path, out var mediaType) ||
                !StringComparer.Ordinal.Equals(mediaType, artifact.MediaType) ||
                string.IsNullOrEmpty(artifact.Content) || artifact.Content.Length > 262_144 ||
                Encoding.UTF8.GetByteCount(artifact.Content) > 262_144 ||
                !entries.TryAdd(artifact.Path, artifact.Content))
                return Invalid("feature_builder.proposal.artifact_invalid");
        }

        if (!entries.TryGetValue("manifest.json", out var manifestJson) ||
            !entries.TryGetValue("instructions.md", out var instructions) ||
            !entries.TryGetValue("input.schema.json", out var inputSchemaJson) ||
            !entries.TryGetValue("tests.json", out var testsJson))
            return Invalid("feature_builder.proposal.artifact_required");

        try
        {
            manifestJson = CanonicalizeJson(manifestJson);
            inputSchemaJson = CanonicalizeJson(inputSchemaJson, rejectControlFields: true);
            testsJson = CanonicalizeJson(testsJson, rejectControlFields: true);
            var settingsSchemaJson = entries.TryGetValue("settings.schema.json", out var settings)
                ? CanonicalizeJson(settings, rejectControlFields: true)
                : null;
            var workflowJson = entries.TryGetValue("workflow.json", out var workflow)
                ? CanonicalizeJson(workflow, rejectControlFields: true)
                : null;
            var manifest = ParseManifest(manifestJson);
            var payload = new CapabilityVersionPayload(
                manifest,
                manifestJson,
                NormalizeNewlines(instructions),
                inputSchemaJson,
                settingsSchemaJson,
                workflowJson,
                testsJson);

            if (manifest.Kind is not (CapabilityKind.Prompt or CapabilityKind.Workflow))
                return Invalid("feature_builder.proposal.kind_unsupported");
            if (revisionTarget is not null &&
                (!manifest.Id.Equals(revisionTarget.BaseVersion.Id) ||
                 manifest.Version.CompareTo(revisionTarget.BaseVersion.Version) <= 0))
                return Invalid("feature_builder.proposal.revision_target_mismatch");
            var definition = CapabilityDefinitionPolicy.Validate(payload);
            if (!definition.IsValid)
                return Invalid(definition.ErrorCode ?? "feature_builder.proposal.definition_invalid");
            if (!IsObjectSchema(inputSchemaJson) ||
                settingsSchemaJson is not null && !IsObjectSchema(settingsSchemaJson))
                return Invalid("feature_builder.proposal.schema_invalid");
            var fixtureTests = CapabilityFixtureTestPolicy.Validate(testsJson);
            if (!fixtureTests.IsValid)
                return Invalid(fixtureTests.ErrorCode ?? "feature_builder.proposal.tests_invalid");
            if (fixtureTests.CaseCount == 0)
                return Invalid("feature_builder.proposal.tests_required");

            var ingress = CapabilityIngressPolicy.GetViolation(payload);
            if (ingress == CapabilityIngressViolation.SecretLiteral)
                return Invalid("feature_builder.proposal.secret_literal_detected");
            if (ingress == CapabilityIngressViolation.ConnectionLiteral)
                return Invalid("feature_builder.proposal.connection_literal_detected");
            if (ContainsShellOrScriptLiteral(payload))
                return Invalid("feature_builder.proposal.shell_or_script_detected");
            foreach (var toolName in manifest.RequiredTools)
            {
                if (!dependencies.TryGetTool(toolName, out var dependency) || dependency is null)
                    return Invalid("feature_builder.proposal.tool_unknown");
                if (RiskOrdinal(dependency.Risk) > RiskOrdinal(manifest.MaximumRisk))
                    return Invalid("feature_builder.proposal.risk_underdeclared");
            }

            if (manifest.RequiredConnections.Count != 0 &&
                (manifest.Kind != CapabilityKind.Workflow ||
                 manifest.RequiredConnections.Any(requirement =>
                     !CapabilityConnectionKindPolicy.IsSupported(requirement.Kind))))
                return Invalid("feature_builder.proposal.connection_kind_unsupported");
            if (manifest.Kind == CapabilityKind.Workflow)
            {
                if (workflowTools is null)
                    return Invalid("feature_builder.proposal.workflow_validator_unavailable");
                var parsed = new WorkflowDefinitionParser().Parse(workflowJson!);
                if (!parsed.Succeeded || parsed.Definition is null)
                    return Invalid(parsed.Issues.FirstOrDefault()?.Code ?? "workflow.definition.invalid");
                var workflowValidation = new WorkflowStaticValidator(workflowTools).Validate(
                    parsed.Definition,
                    manifest,
                    inputSchemaJson);
                if (!workflowValidation.Succeeded)
                    return Invalid(workflowValidation.Issues.FirstOrDefault()?.Code ?? "workflow.definition.invalid");
            }

            var contentHash = CapabilityContentHasher.Compute(payload);
            return new FeatureBuilderProposalScanResult(true, payload, contentHash);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            return Invalid("feature_builder.proposal.artifact_invalid");
        }
    }

    private static FeatureBuilderProposalScanResult Invalid(string errorCode) =>
        FeatureBuilderProposalScanResult.Invalid(errorCode);

    private static CapabilityManifest ParseManifest(string canonicalJson)
    {
        using var document = JsonDocument.Parse(canonicalJson, JsonOptions());
        var root = document.RootElement;
        var fields = ReadExactObject(root, [
            "schemaVersion", "id", "version", "displayName", "description", "kind",
            "intentExamples", "requiredTools", "requiredConnections", "maximumRisk", "entrypoint",
        ]);
        var schemaVersion = ReadInt32(fields["schemaVersion"]);
        var id = CapabilityId.Parse(ReadString(fields["id"]));
        var version = CapabilitySemanticVersion.Parse(ReadString(fields["version"]));
        var displayName = ReadString(fields["displayName"]);
        var description = ReadString(fields["description"]);
        var kind = ReadString(fields["kind"]) switch
        {
            "prompt" => CapabilityKind.Prompt,
            "workflow" => CapabilityKind.Workflow,
            "connector" => CapabilityKind.Connector,
            _ => throw new JsonException("Capability kind가 올바르지 않습니다."),
        };
        var examples = ReadStringArray(fields["intentExamples"]);
        var requiredTools = ReadStringArray(fields["requiredTools"]);
        var connections = ReadConnections(fields["requiredConnections"]);
        return new CapabilityManifest(
            schemaVersion,
            id,
            version,
            displayName,
            description,
            kind,
            examples,
            requiredTools,
            connections,
            ReadString(fields["maximumRisk"]),
            ReadString(fields["entrypoint"]));
    }

    private static IReadOnlyList<CapabilityConnectionRequirement> ReadConnections(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new JsonException();
        var result = new List<CapabilityConnectionRequirement>();
        foreach (var item in value.EnumerateArray())
        {
            var fields = ReadExactObject(item, ["slot", "kind"]);
            result.Add(new CapabilityConnectionRequirement(
                ReadString(fields["slot"]),
                ReadString(fields["kind"])));
        }
        return result.AsReadOnly();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new JsonException();
        return Array.AsReadOnly(value.EnumerateArray().Select(ReadString).ToArray());
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadExactObject(
        JsonElement value,
        IReadOnlyList<string> required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (!fields.TryAdd(property.Name, property.Value) || !insensitive.Add(property.Name))
                throw new JsonException();
        }
        if (fields.Count != required.Count || required.Any(name => !fields.ContainsKey(name)))
            throw new JsonException();
        return fields;
    }

    private static string ReadString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            ? text
            : throw new JsonException();

    private static int ReadInt32(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw new JsonException();

    private static bool IsObjectSchema(string canonicalJson)
    {
        using var document = JsonDocument.Parse(canonicalJson, JsonOptions());
        if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
        return !document.RootElement.TryGetProperty("type", out var type) ||
               type.ValueKind == JsonValueKind.String && type.GetString() == "object";
    }

    private static string CanonicalizeJson(string json, bool rejectControlFields = false)
    {
        using var document = JsonDocument.Parse(json, JsonOptions());
        if (rejectControlFields && ContainsForbiddenControlField(document.RootElement))
            throw new JsonException();
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false }))
            WriteCanonical(writer, document.RootElement);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static bool ContainsForbiddenControlField(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(ContainsForbiddenControlField);
        if (element.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is "proposalId" or "draftId" or "contentHash" or "parent" or
                "parentHash" or "parentVersion" or "catalogRevision" or "proposalStatus" or
                "activation" or "activationRecord" || ContainsForbiddenControlField(property.Value))
                return true;
        }
        return false;
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var properties = element.EnumerateObject().ToArray();
                foreach (var property in properties)
                {
                    if (!names.Add(property.Name)) throw new JsonException();
                }
                foreach (var property in properties.OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static JsonDocumentOptions JsonOptions() => new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool ContainsShellOrScriptLiteral(CapabilityVersionPayload payload)
    {
        if (payload.Manifest.RequiredTools.Any(IsShellTool)) return true;
        return ShellOrScript().IsMatch(string.Join('\n',
            payload.Instructions,
            payload.CanonicalWorkflowJson ?? string.Empty,
            payload.CanonicalTestsJson));
    }

    private static bool IsShellTool(string value) =>
        value.Contains("shell", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("powershell", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("script", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("command.execute", StringComparison.OrdinalIgnoreCase);

    private static int RiskOrdinal(string risk) => risk switch
    {
        "R0" => 0,
        "R1" => 1,
        "R2" => 2,
        "R3" => 3,
        "R4" => 4,
        _ => int.MaxValue,
    };

    [GeneratedRegex(@"(?:\bshell\.execute\b|\bpowershell(?:\.exe)?\b|\bcmd(?:\.exe)?\s+/c\b|\bbash\s+-c\b|\bProcess\.Start\b|<script\b|\bexecute\s+(?:a\s+)?script\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShellOrScript();
}

public static class FeatureBuilderHashPolicy
{
    public static CapabilityContentHash ComputeRequirementHash(string requirement) =>
        Compute(("requirement", requirement));

    public static FeatureBuilderValidationResult ValidateInventory(FeatureBuilderInventory? inventory)
    {
        if (inventory is null ||
            !ValidUnique(inventory.Tools, 128, 3, 128, IsToolName) ||
            !ValidUnique(inventory.ConnectionKinds, 32, 1, 64, IsInventoryName) ||
            !ValidUnique(inventory.CapabilityIds, 128, 3, 128, IsCapabilityId))
            return FeatureBuilderValidationResult.Invalid("feature_builder.inventory.invalid");
        return FeatureBuilderValidationResult.Valid;
    }

    public static CapabilityContentHash ComputeInventoryHash(FeatureBuilderInventory inventory)
    {
        var validation = ValidateInventory(inventory);
        if (!validation.IsValid) throw new ArgumentException(validation.ErrorCode, nameof(inventory));
        var fields = new List<(string Name, string Value)>();
        fields.AddRange(inventory.Tools.Order(StringComparer.Ordinal).Select(value => ("tool", value)));
        fields.AddRange(inventory.ConnectionKinds.Order(StringComparer.Ordinal).Select(value => ("connection", value)));
        fields.AddRange(inventory.CapabilityIds.Order(StringComparer.Ordinal).Select(value => ("capability", value)));
        return Compute(fields.ToArray());
    }

    public static CapabilityContentHash ComputeRequestHash(
        string generationId,
        FeatureBuilderSession session,
        DateTimeOffset deadlineUtc,
        FeatureBuilderInventory inventory)
    {
        var fields = new List<(string Name, string Value)>
        {
            ("schemaVersion", "1"),
            ("generationId", generationId),
            ("sessionId", session.SessionId),
            ("sessionRevision", session.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("inventoryHash", session.InventoryHash.Value),
            ("deadlineUtc", deadlineUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            ("requirement", session.Requirement),
        };
        fields.AddRange(session.Answers.Select(answer => ("answer", $"{answer.QuestionId}\0{answer.Value}")));
        fields.AddRange(inventory.Tools.Order(StringComparer.Ordinal).Select(value => ("tool", value)));
        fields.AddRange(inventory.ConnectionKinds.Order(StringComparer.Ordinal).Select(value => ("connection", value)));
        fields.AddRange(inventory.CapabilityIds.Order(StringComparer.Ordinal).Select(value => ("capability", value)));
        return Compute(fields.ToArray());
    }

    public static CapabilityContentHash ComputeResponseHash(FeatureBuilderRuntimeResult result)
    {
        var fields = new List<(string Name, string Value)>
        {
            ("schemaVersion", result.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("generationId", result.GenerationId),
            ("requestHash", result.RequestHash.Value),
            ("sessionId", result.SessionId),
            ("sessionRevision", result.SessionRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("inventoryHash", result.InventoryHash.Value),
            ("deadlineUtc", result.DeadlineUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            ("outcome", result.Outcome.ToString()),
        };
        if (result.Questions is not null)
        {
            foreach (var question in result.Questions)
            {
                fields.Add(("question", $"{question.QuestionId}\0{question.Prompt}\0{question.AnswerKind}"));
                fields.AddRange(question.Choices.Select(choice => ("choice", choice)));
            }
        }
        if (result.Artifacts is not null)
            fields.AddRange(result.Artifacts.OrderBy(artifact => artifact.Path, StringComparer.Ordinal)
                .Select(artifact => ("artifact", $"{artifact.Path}\0{artifact.MediaType}\0{artifact.Content}")));
        if (result.Limitations is not null)
            fields.AddRange(result.Limitations.Select(value => ("limitation", value)));
        if (result.RefusalCode is not null) fields.Add(("refusal", result.RefusalCode));
        return Compute(fields.ToArray());
    }

    public static CapabilityContentHash ComputeQuarantinedResponseHash(
        FeatureBuilderRuntimeResult result,
        string fixedIssueCode) =>
        Compute(
            ("schemaVersion", result.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("generationId", result.GenerationId),
            ("requestHash", result.RequestHash.Value),
            ("sessionId", result.SessionId),
            ("sessionRevision", result.SessionRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("inventoryHash", result.InventoryHash.Value),
            ("deadlineUtc", result.DeadlineUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            ("outcome", "quarantined"),
            ("fixedIssueCode", fixedIssueCode));

    public static CapabilityContentHash ComputeProposalHash(
        string generationId,
        CapabilityContentHash requestHash,
        CapabilityContentHash responseHash,
        CapabilityContentHash? candidateContentHash,
        CapabilityVersionRef? baseVersion,
        long expectedCatalogRevision) =>
        Compute(
            ("generationId", generationId),
            ("requestHash", requestHash.Value),
            ("responseHash", responseHash.Value),
            ("candidateContentHash", candidateContentHash?.Value ?? string.Empty),
            ("base", baseVersion is null
                ? string.Empty
                : $"{baseVersion.Id.Value}\0{baseVersion.Version.Value}\0{baseVersion.ContentHash.Value}"),
            ("expectedCatalogRevision", expectedCatalogRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public static string CreateProposalId(CapabilityContentHash proposalHash) =>
        $"proposal_{proposalHash.Value.AsSpan(7, 48)}";

    public static CapabilityContentHash ComputeTestEvidenceHash(IReadOnlyList<FeatureBuilderTestRun> runs)
    {
        var fields = new List<(string Name, string Value)>();
        foreach (var run in runs.OrderBy(run => run.Kind).ThenBy(run => run.Attempt))
        {
            fields.Add(("run", string.Join('\0',
                run.TestRunId,
                run.Kind,
                run.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                run.CandidateContentHash.Value,
                run.EngineVersion,
                run.State,
                run.CanonicalResultJson)));
        }
        return Compute(fields.ToArray());
    }

    private static CapabilityContentHash Compute(params (string Name, string Value)[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> frame = stackalloc byte[12];
        foreach (var (name, value) in fields)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var valueBytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(frame, nameBytes.Length);
            BinaryPrimitives.WriteInt64BigEndian(frame[4..], valueBytes.LongLength);
            hash.AppendData(frame);
            hash.AppendData(nameBytes);
            hash.AppendData(valueBytes);
        }
        return CapabilityContentHash.Parse($"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}");
    }

    private static bool ValidUnique(
        IReadOnlyList<string> values,
        int maximumCount,
        int minimumLength,
        int maximumLength,
        Func<string, bool> predicate) =>
        values is not null && values.Count <= maximumCount &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Count &&
        values.All(value => value is not null && value.Length >= minimumLength &&
                            value.Length <= maximumLength && predicate(value));

    private static bool IsToolName(string value)
    {
        var parts = value.Split('.');
        return parts.Length >= 3 && parts[^1].Length >= 2 && parts[^1][0] == 'v' &&
               parts[^1].AsSpan(1).IndexOfAnyExcept("0123456789") < 0 && parts[^1][1] is >= '1' and <= '9' &&
               parts[..^1].All(IsToolSegment);
    }

    private static bool IsToolSegment(string value) =>
        value.Length > 0 && value[0] is >= 'a' and <= 'z' &&
        value.AsSpan(1).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789_") < 0;

    private static bool IsInventoryName(string value)
    {
        if (value.Length == 0 || value[0] is not (>= 'a' and <= 'z')) return false;
        var separator = false;
        foreach (var character in value.AsSpan(1))
        {
            if (character is '.' or '_' or '-')
            {
                if (separator) return false;
                separator = true;
            }
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                separator = false;
            }
            else
            {
                return false;
            }
        }
        return !separator;
    }

    private static bool IsCapabilityId(string value) => CapabilityId.TryParse(value, out _);
}
