using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LIGClaw.Application.Connections;
using LIGClaw.Domain;

namespace LIGClaw.Application.Capabilities;

public sealed record CapabilityRuntimeSnapshotHash
{
    private CapabilityRuntimeSnapshotHash(string value) => Value = value;

    public string Value { get; }

    public static CapabilityRuntimeSnapshotHash Parse(string value)
    {
        if (!TryParse(value, out var parsed))
            throw new FormatException("Capability runtime snapshot hash 형식이 올바르지 않습니다.");
        return parsed!;
    }

    public static bool TryParse(string? value, out CapabilityRuntimeSnapshotHash? parsed)
    {
        parsed = null;
        if (value is null || value.Length != 71 ||
            !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            value.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
            return false;
        parsed = new CapabilityRuntimeSnapshotHash(value);
        return true;
    }

    public override string ToString() => Value;
}

public sealed record CapabilityRuntimeSourceCandidate(
    CapabilityVersionRef ExactVersion,
    long CatalogRevision,
    CapabilityVersionPayload Payload);

public interface ICapabilityRuntimeCandidateSource
{
    Task<IReadOnlyList<CapabilityRuntimeSourceCandidate>> LoadActiveRuntimeCandidatesAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);
}

public sealed record CapabilityRuntimeCandidate
{
    private IReadOnlyList<string> _intentExamples = [];
    private IReadOnlyList<string> _requiredTools = [];

    public CapabilityRuntimeCandidate(
        CapabilityVersionRef exactVersion,
        long catalogRevision,
        string modelToolName,
        string displayName,
        string description,
        CapabilityKind kind,
        IReadOnlyList<string> intentExamples,
        string instructions,
        string canonicalInputSchemaJson,
        IReadOnlyList<string> requiredTools,
        string connectionBindingHash,
        string maximumRisk)
    {
        ExactVersion = exactVersion;
        CatalogRevision = catalogRevision;
        ModelToolName = modelToolName;
        DisplayName = displayName;
        Description = description;
        Kind = kind;
        IntentExamples = intentExamples;
        Instructions = instructions;
        CanonicalInputSchemaJson = canonicalInputSchemaJson;
        RequiredTools = requiredTools;
        ConnectionBindingHash = RequireHash(connectionBindingHash);
        MaximumRisk = maximumRisk;
    }

    public CapabilityVersionRef ExactVersion { get; init; }
    public long CatalogRevision { get; init; }
    public string ModelToolName { get; init; }
    public string DisplayName { get; init; }
    public string Description { get; init; }
    public CapabilityKind Kind { get; init; }
    public IReadOnlyList<string> IntentExamples
    {
        get => _intentExamples;
        init => _intentExamples = Copy(value);
    }
    public string Instructions { get; init; }
    public string CanonicalInputSchemaJson { get; init; }
    public IReadOnlyList<string> RequiredTools
    {
        get => _requiredTools;
        init => _requiredTools = Copy(value);
    }
    public string ConnectionBindingHash { get; init; }
    public string MaximumRisk { get; init; }

    private static IReadOnlyList<string> Copy(IReadOnlyList<string> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Array.AsReadOnly(source.ToArray());
    }

    private static string RequireHash(string value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef")
            ? value
            : throw new ArgumentException("Connection binding hash 형식이 올바르지 않습니다.", nameof(value));
}

public sealed record CapabilityRuntimeSnapshot
{
    public const int CurrentSchemaVersion = 2;

    private IReadOnlyList<CapabilityRuntimeCandidate> _candidates = [];

    public CapabilityRuntimeSnapshot(
        int schemaVersion,
        CapabilityRuntimeSnapshotHash snapshotHash,
        IReadOnlyList<CapabilityRuntimeCandidate> candidates)
    {
        SchemaVersion = schemaVersion;
        SnapshotHash = snapshotHash;
        Candidates = candidates;
    }

    public int SchemaVersion { get; init; }
    public CapabilityRuntimeSnapshotHash SnapshotHash { get; init; }
    public IReadOnlyList<CapabilityRuntimeCandidate> Candidates
    {
        get => _candidates;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _candidates = Array.AsReadOnly(value.ToArray());
        }
    }

    public static CapabilityRuntimeSnapshot Empty { get; } = Create([]);

    public static CapabilityRuntimeSnapshot Create(IReadOnlyList<CapabilityRuntimeCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var copy = Array.AsReadOnly(candidates.ToArray());
        return new CapabilityRuntimeSnapshot(
            CurrentSchemaVersion,
            CapabilityRuntimeHasher.ComputeSnapshotHash(copy),
            copy);
    }
}

public static class CapabilityRuntimeTransportPolicy
{
    public const int MaximumSerializedSnapshotBytes = 2 * 1024 * 1024;

    public static bool IsWithinBudget(CapabilityRuntimeSnapshot snapshot) =>
        GetSerializedSizeBytes(snapshot) <= MaximumSerializedSnapshotBytes;

    public static int GetSerializedSizeBytes(CapabilityRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var transport = new
        {
            schemaVersion = snapshot.SchemaVersion,
            snapshotHash = snapshot.SnapshotHash.Value,
            candidates = snapshot.Candidates.Select(candidate => new
            {
                id = candidate.ExactVersion.Id.Value,
                version = candidate.ExactVersion.Version.Value,
                contentHash = candidate.ExactVersion.ContentHash.Value,
                catalogRevision = candidate.CatalogRevision,
                modelToolName = candidate.ModelToolName,
                displayName = candidate.DisplayName,
                description = candidate.Description,
                kind = FormatKind(candidate.Kind),
                intentExamples = candidate.IntentExamples,
                instructions = candidate.Instructions,
                canonicalInputSchemaJson = candidate.CanonicalInputSchemaJson,
                requiredTools = candidate.RequiredTools,
                connectionBindingHash = candidate.ConnectionBindingHash,
                maximumRisk = candidate.MaximumRisk,
            }).ToArray(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(transport).Length;
    }

    private static string FormatKind(CapabilityKind kind) => kind switch
    {
        CapabilityKind.Prompt => "prompt",
        CapabilityKind.Workflow => "workflow",
        CapabilityKind.Connector => "connector",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public static class CapabilityRuntimeHasher
{
    private const string ModelToolFormat = "capability-model-tool-v1";
    private const string SnapshotFormat = "capability-runtime-snapshot-v2";

    public static string ComputeModelToolName(CapabilityVersionRef exactVersion)
    {
        ArgumentNullException.ThrowIfNull(exactVersion);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "format", ModelToolFormat);
        Append(hash, "capabilityId", exactVersion.Id.Value);
        Append(hash, "version", exactVersion.Version.Value);
        Append(hash, "contentHash", exactVersion.ContentHash.Value);
        return $"cap_{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..24]}";
    }

    public static CapabilityRuntimeSnapshotHash ComputeSnapshotHash(
        IReadOnlyList<CapabilityRuntimeCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "format", SnapshotFormat);
        Append(hash, "schemaVersion", CapabilityRuntimeSnapshot.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
        Append(hash, "candidateCount", candidates.Count.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index] ??
                throw new ArgumentException("Capability runtime candidate가 비어 있습니다.", nameof(candidates));
            var prefix = $"candidates[{index}]";
            Append(hash, $"{prefix}.id", candidate.ExactVersion.Id.Value);
            Append(hash, $"{prefix}.version", candidate.ExactVersion.Version.Value);
            Append(hash, $"{prefix}.contentHash", candidate.ExactVersion.ContentHash.Value);
            Append(hash, $"{prefix}.catalogRevision", candidate.CatalogRevision.ToString(CultureInfo.InvariantCulture));
            Append(hash, $"{prefix}.modelToolName", candidate.ModelToolName);
            Append(hash, $"{prefix}.displayName", candidate.DisplayName);
            Append(hash, $"{prefix}.description", candidate.Description);
            Append(hash, $"{prefix}.kind", FormatKind(candidate.Kind));
            AppendList(hash, $"{prefix}.intentExamples", candidate.IntentExamples);
            Append(hash, $"{prefix}.instructions", candidate.Instructions);
            Append(hash, $"{prefix}.canonicalInputSchemaJson", candidate.CanonicalInputSchemaJson);
            AppendList(hash, $"{prefix}.requiredTools", candidate.RequiredTools);
            Append(hash, $"{prefix}.connectionBindingHash", candidate.ConnectionBindingHash);
            Append(hash, $"{prefix}.maximumRisk", candidate.MaximumRisk);
        }

        return CapabilityRuntimeSnapshotHash.Parse(
            $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}");
    }

    private static void AppendList(
        IncrementalHash hash,
        string name,
        IReadOnlyList<string> values)
    {
        Append(hash, $"{name}.count", values.Count.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < values.Count; index++)
            Append(hash, $"{name}[{index}]", values[index]);
    }

    private static void Append(IncrementalHash hash, string name, string value)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        Span<byte> framing = stackalloc byte[12];
        BinaryPrimitives.WriteInt32BigEndian(framing, nameBytes.Length);
        BinaryPrimitives.WriteInt64BigEndian(framing[4..], valueBytes.LongLength);
        hash.AppendData(framing);
        hash.AppendData(nameBytes);
        hash.AppendData(valueBytes);
    }

    private static string FormatKind(CapabilityKind kind) => kind switch
    {
        CapabilityKind.Prompt => "prompt",
        CapabilityKind.Workflow => "workflow",
        CapabilityKind.Connector => "connector",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public static class CapabilityRuntimeInputSchemaPolicy
{
    private const int MaximumSchemaDepth = 8;
    private const int MaximumSchemaProperties = 128;
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly HashSet<string> SchemaKeywords =
    [
        "$schema", "type", "title", "description", "properties", "required", "additionalProperties",
        "items", "minItems", "maxItems", "minLength", "maxLength", "minimum", "maximum",
        "exclusiveMinimum", "exclusiveMaximum", "enum", "const", "format", "uniqueItems",
    ];
    private static readonly HashSet<string> SchemaTypes =
        ["object", "array", "string", "integer", "number", "boolean"];
    private static readonly HashSet<string> SchemaFormats = ["date", "date-time", "email", "uuid"];

    public static bool HasBoundedObjectRoot(string canonicalJson)
    {
        if (string.IsNullOrWhiteSpace(canonicalJson) ||
            canonicalJson.Length > CapabilityDefinitionPolicy.MaximumSchemaCharacters)
            return false;
        try
        {
            using var document = JsonDocument.Parse(canonicalJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            if (!ValidateJsonValueBounds(document.RootElement, 0)) return false;
            var propertyCount = 0;
            return ValidateSchemaNode(document.RootElement, 0, isRoot: true, ref propertyCount);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ValidateJsonValueBounds(JsonElement value, int depth)
    {
        if (depth > 32) return false;
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                var propertyCount = 0;
                foreach (var property in value.EnumerateObject())
                {
                    if (++propertyCount > 128 || !names.Add(property.Name) ||
                        !ValidateJsonValueBounds(property.Value, depth + 1))
                        return false;
                }
                return true;
            case JsonValueKind.Array:
                if (value.GetArrayLength() > 128) return false;
                foreach (var item in value.EnumerateArray())
                {
                    if (!ValidateJsonValueBounds(item, depth + 1)) return false;
                }
                return true;
            case JsonValueKind.Number:
                return value.TryGetDouble(out var number) && double.IsFinite(number);
            default:
                return true;
        }
    }

    private static bool ValidateSchemaNode(
        JsonElement node,
        int depth,
        bool isRoot,
        ref int propertyCount)
    {
        if (depth > MaximumSchemaDepth || node.ValueKind != JsonValueKind.Object ||
            !TryFields(node, out var fields) || fields.Keys.Any(key => !SchemaKeywords.Contains(key)) ||
            !fields.TryGetValue("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            return false;
        var type = typeElement.GetString();
        if (type is null || !SchemaTypes.Contains(type) || isRoot && type != "object") return false;
        if (fields.TryGetValue("$schema", out var declaration) &&
            (!isRoot || declaration.ValueKind != JsonValueKind.String ||
             declaration.GetString() != "https://json-schema.org/draft/2020-12/schema"))
            return false;
        if (fields.TryGetValue("title", out var title) &&
            (title.ValueKind != JsonValueKind.String || (title.GetString()?.Length ?? 0) is < 1 or > 200))
            return false;
        if (fields.TryGetValue("description", out var description) &&
            (description.ValueKind != JsonValueKind.String || (description.GetString()?.Length ?? 0) > 1_000))
            return false;
        if (!ValidateEnumAndConst(fields, type)) return false;

        return type switch
        {
            "object" => ValidateObject(fields, depth, ref propertyCount) &&
                        Reject(fields, "items", "minItems", "maxItems", "minLength", "maxLength", "minimum",
                            "maximum", "exclusiveMinimum", "exclusiveMaximum", "format", "uniqueItems"),
            "array" => ValidateArray(fields, depth, ref propertyCount) &&
                       Reject(fields, "properties", "required", "additionalProperties", "minLength", "maxLength",
                           "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "format"),
            "string" => ValidateString(fields) &&
                        Reject(fields, "properties", "required", "additionalProperties", "items", "minItems",
                            "maxItems", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
                            "uniqueItems"),
            "integer" or "number" => ValidateNumber(fields) &&
                                     Reject(fields, "properties", "required", "additionalProperties", "items",
                                         "minItems", "maxItems", "minLength", "maxLength", "format",
                                         "uniqueItems"),
            _ => Reject(fields, "properties", "required", "additionalProperties", "items", "minItems",
                "maxItems", "minLength", "maxLength", "minimum", "maximum", "exclusiveMinimum",
                "exclusiveMaximum", "format", "uniqueItems"),
        };
    }

    private static bool ValidateObject(
        IReadOnlyDictionary<string, JsonElement> fields,
        int depth,
        ref int propertyCount)
    {
        if (!fields.TryGetValue("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
            !fields.TryGetValue("additionalProperties", out var additional) ||
            additional.ValueKind != JsonValueKind.False || !TryFields(properties, out var propertyMap))
            return false;
        propertyCount = checked(propertyCount + propertyMap.Count);
        if (propertyCount > MaximumSchemaProperties ||
            propertyMap.Keys.Any(name => name.Length is < 1 or > 80))
            return false;
        foreach (var child in propertyMap.Values)
        {
            if (!ValidateSchemaNode(child, depth + 1, isRoot: false, ref propertyCount)) return false;
        }
        if (!fields.TryGetValue("required", out var required)) return true;
        if (required.ValueKind != JsonValueKind.Array || required.GetArrayLength() > propertyMap.Count) return false;
        var requiredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in required.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } name ||
                !propertyMap.ContainsKey(name) || !requiredNames.Add(name))
                return false;
        }
        return true;
    }

    private static bool ValidateArray(
        IReadOnlyDictionary<string, JsonElement> fields,
        int depth,
        ref int propertyCount)
    {
        if (!fields.TryGetValue("items", out var items) ||
            !TryBoundedInteger(fields, "maxItems", 0, 64, out var maximum) ||
            fields.TryGetValue("minItems", out _) &&
            !TryBoundedInteger(fields, "minItems", 0, maximum, out _))
            return false;
        if (!ValidateSchemaNode(items, depth + 1, isRoot: false, ref propertyCount)) return false;
        if (!fields.TryGetValue("uniqueItems", out var uniqueItems)) return true;
        if (uniqueItems.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        if (uniqueItems.ValueKind == JsonValueKind.False) return true;
        return TryFields(items, out var itemFields) &&
               itemFields.TryGetValue("type", out var itemType) &&
               itemType.ValueKind == JsonValueKind.String &&
               StringComparer.Ordinal.Equals(itemType.GetString(), "string");
    }

    private static bool ValidateString(IReadOnlyDictionary<string, JsonElement> fields)
    {
        if (!TryBoundedInteger(fields, "maxLength", 0, 4_096, out var maximum) ||
            fields.TryGetValue("minLength", out _) &&
            !TryBoundedInteger(fields, "minLength", 0, maximum, out _))
            return false;
        return !fields.TryGetValue("format", out var format) ||
               format.ValueKind == JsonValueKind.String &&
               SchemaFormats.Contains(format.GetString() ?? string.Empty);
    }

    private static bool ValidateNumber(IReadOnlyDictionary<string, JsonElement> fields)
    {
        foreach (var name in new[] { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum" })
        {
            if (fields.TryGetValue(name, out var value) &&
                (!value.TryGetDouble(out var number) || !double.IsFinite(number)))
                return false;
        }
        return !fields.TryGetValue("minimum", out var minimum) ||
               !fields.TryGetValue("maximum", out var maximum) ||
               minimum.GetDouble() <= maximum.GetDouble();
    }

    private static bool ValidateEnumAndConst(
        IReadOnlyDictionary<string, JsonElement> fields,
        string type)
    {
        if (fields.TryGetValue("enum", out var values))
        {
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > 64) return false;
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values.EnumerateArray())
            {
                if (!MatchesSchemaType(value, type) || !unique.Add(CanonicalValue(value))) return false;
            }
        }
        return !fields.TryGetValue("const", out var constant) || MatchesSchemaType(constant, type);
    }

    private static bool MatchesSchemaType(JsonElement value, string type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer) &&
                     integer is >= -MaximumSafeInteger and <= MaximumSafeInteger,
        "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
                    double.IsFinite(number),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        _ => false,
    };

    private static string CanonicalValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number) return JsonSerializer.Serialize(value);
        var number = value.GetDouble();
        return number == 0
            ? "0"
            : number.ToString("R", CultureInfo.InvariantCulture);
    }

    private static bool TryBoundedInteger(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        int minimum,
        int maximum,
        out int value)
    {
        value = 0;
        return fields.TryGetValue(name, out var element) && element.TryGetInt32(out value) &&
               value >= minimum && value <= maximum;
    }

    private static bool TryFields(
        JsonElement element,
        out IReadOnlyDictionary<string, JsonElement> fields)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!result.TryAdd(property.Name, property.Value))
            {
                fields = result;
                return false;
            }
        }
        fields = result;
        return true;
    }

    private static bool Reject(
        IReadOnlyDictionary<string, JsonElement> fields,
        params string[] names) =>
        names.All(name => !fields.ContainsKey(name));
}

public sealed class CapabilityRuntimeSnapshotBuilder(
    ICapabilityRuntimeCandidateSource source,
    ICapabilityDependencyCatalog dependencies,
    ICapabilityConnectionReadinessResolver? connections = null)
{
    public const int MaximumCandidateCount = 8;
    public const int MaximumQueryCharacters = 256;

    public async Task<CapabilityRuntimeSnapshot> BuildAsync(
        string? userRequest,
        CancellationToken cancellationToken)
    {
        var query = NormalizeQuery(userRequest);
        if (query.Length < 3) return CapabilityRuntimeSnapshot.Empty;

        var sourceCandidates = await source.LoadActiveRuntimeCandidatesAsync(
            query,
            MaximumCandidateCount,
            cancellationToken).ConfigureAwait(false);
        if (sourceCandidates.Count > MaximumCandidateCount)
            throw new InvalidDataException("Capability runtime candidate 상한을 초과했습니다.");

        var projected = new List<CapabilityRuntimeCandidate>(sourceCandidates.Count);
        var identities = new HashSet<CapabilityId>();
        var modelToolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceCandidate in sourceCandidates)
        {
            ValidateIntegrity(sourceCandidate);
            if (!identities.Add(sourceCandidate.ExactVersion.Id))
                throw new InvalidDataException("Capability runtime candidate ID가 중복되었습니다.");
            if (!IsReady(sourceCandidate.Payload)) continue;

            var connectionResolution = await ResolveConnectionsAsync(
                sourceCandidate.ExactVersion,
                sourceCandidate.Payload.Manifest.RequiredConnections,
                cancellationToken).ConfigureAwait(false);
            if (connectionResolution is null || !connectionResolution.IsReady ||
                !IsHash(connectionResolution.AggregateHash))
                continue;

            var manifest = sourceCandidate.Payload.Manifest;
            var modelToolName = CapabilityRuntimeHasher.ComputeModelToolName(sourceCandidate.ExactVersion);
            if (!modelToolNames.Add(modelToolName))
                throw new InvalidDataException("Capability model Tool 이름이 충돌했습니다.");
            var projectedCandidate = new CapabilityRuntimeCandidate(
                sourceCandidate.ExactVersion,
                sourceCandidate.CatalogRevision,
                modelToolName,
                manifest.DisplayName,
                manifest.Description,
                manifest.Kind,
                manifest.IntentExamples,
                sourceCandidate.Payload.Instructions,
                sourceCandidate.Payload.CanonicalInputSchemaJson,
                manifest.RequiredTools,
                connectionResolution.AggregateHash,
                manifest.MaximumRisk);
            var proposedSnapshot = CapabilityRuntimeSnapshot.Create([.. projected, projectedCandidate]);
            if (!CapabilityRuntimeTransportPolicy.IsWithinBudget(proposedSnapshot)) continue;
            projected.Add(projectedCandidate);
        }

        return CapabilityRuntimeSnapshot.Create(projected);
    }

    internal static string NormalizeQuery(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(Math.Min(value.Length, MaximumQueryCharacters));
        var textElements = StringInfo.GetTextElementEnumerator(value);
        var pendingSpace = false;
        while (textElements.MoveNext())
        {
            var element = textElements.GetTextElement();
            if (element.All(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            var required = element.Length + (pendingSpace ? 1 : 0);
            if (builder.Length + required > MaximumQueryCharacters) break;
            if (pendingSpace) builder.Append(' ');
            builder.Append(element);
            pendingSpace = false;
        }

        return builder.ToString();
    }

    private void ValidateIntegrity(CapabilityRuntimeSourceCandidate candidate)
    {
        if (candidate is null || candidate.ExactVersion is null || candidate.Payload is null ||
            candidate.CatalogRevision < 1)
            throw new InvalidDataException("Capability runtime candidate가 손상되었습니다.");
        var manifest = candidate.Payload.Manifest;
        if (!manifest.Id.Equals(candidate.ExactVersion.Id) ||
            !manifest.Version.Equals(candidate.ExactVersion.Version) ||
            !CapabilityContentHasher.Compute(candidate.Payload).Equals(candidate.ExactVersion.ContentHash))
            throw new InvalidDataException("Capability runtime exact identity가 일치하지 않습니다.");
        if (CapabilityIngressPolicy.GetViolation(candidate.Payload) != CapabilityIngressViolation.None)
            throw new InvalidDataException("Capability runtime payload가 안전하지 않습니다.");
    }

    private bool IsReady(CapabilityVersionPayload payload)
    {
        var validation = CapabilityDefinitionPolicy.Validate(payload);
        if (!validation.IsValid ||
            payload.Manifest.Kind is not (CapabilityKind.Prompt or CapabilityKind.Workflow) ||
            !CapabilityRuntimeInputSchemaPolicy.HasBoundedObjectRoot(payload.CanonicalInputSchemaJson))
            return false;
        foreach (var toolName in payload.Manifest.RequiredTools)
        {
            if (!dependencies.TryGetTool(toolName, out var dependency) || dependency is null ||
                !StringComparer.Ordinal.Equals(dependency.Name, toolName) ||
                RiskOrdinal(dependency.Risk) > RiskOrdinal(payload.Manifest.MaximumRisk))
                return false;
        }
        return true;
    }

    private async Task<ConnectionReadinessResolution?> ResolveConnectionsAsync(
        CapabilityVersionRef exactVersion,
        IReadOnlyList<CapabilityConnectionRequirement> requirements,
        CancellationToken cancellationToken)
    {
        if (requirements.Count == 0)
            return new ConnectionReadinessResolution(
                true,
                ConnectionReadinessHasher.Compute(exactVersion, []),
                []);
        if (connections is null || requirements.Any(requirement =>
                !CapabilityConnectionKindPolicy.IsSupported(requirement.Kind)))
            return null;
        try
        {
            return await connections.ResolveAsync(
                exactVersion,
                requirements,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsHash(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef");

    private static int RiskOrdinal(string risk) => risk switch
    {
        "R0" => 0,
        "R1" => 1,
        "R2" => 2,
        "R3" => 3,
        "R4" => 4,
        _ => int.MaxValue,
    };
}
