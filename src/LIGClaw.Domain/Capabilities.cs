using System.Globalization;
using System.Text.RegularExpressions;

namespace LIGClaw.Domain;

public sealed record CapabilityId
{
    private CapabilityId(string value) => Value = value;

    public string Value { get; }

    public static CapabilityId Parse(string value)
    {
        if (!TryParse(value, out var parsed))
            throw new FormatException("Capability ID 형식이 올바르지 않습니다.");
        return parsed!;
    }

    public static bool TryParse(string? value, out CapabilityId? parsed)
    {
        parsed = null;
        if (value is null || value.Length is < 3 or > 128 || !value.All(char.IsAscii)) return false;
        var segments = value.Split('.');
        if (segments.Length is < 2 or > 8) return false;
        foreach (var segment in segments)
        {
            if (segment.Length is < 1 or > 32 ||
                !char.IsAsciiLetterOrDigit(segment[0]) ||
                !char.IsAsciiLetterOrDigit(segment[^1]) ||
                segment.Any(character => !(character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character == '-')))
                return false;
        }

        parsed = new CapabilityId(value);
        return true;
    }

    public override string ToString() => Value;
}

public sealed record CapabilitySemanticVersion : IComparable<CapabilitySemanticVersion>
{
    private static readonly Regex Pattern = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private CapabilitySemanticVersion(
        string value,
        int major,
        int minor,
        int patch,
        IReadOnlyList<string> prerelease,
        IReadOnlyList<string> build)
    {
        Value = value;
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Build = build;
    }

    public string Value { get; }
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }
    public IReadOnlyList<string> Build { get; }

    public static CapabilitySemanticVersion Parse(string value)
    {
        if (!TryParse(value, out var parsed))
            throw new FormatException("Capability version은 canonical SemVer여야 합니다.");
        return parsed!;
    }

    public static bool TryParse(string? value, out CapabilitySemanticVersion? parsed)
    {
        parsed = null;
        if (value is null || value.Length is < 5 or > 64 || !value.All(char.IsAscii)) return false;
        var match = Pattern.Match(value);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            return false;
        var prerelease = SplitIdentifiers(match.Groups[4].Value);
        var build = SplitIdentifiers(match.Groups[5].Value);
        if (!IdentifiersAreValid(prerelease, prereleaseIdentifiers: true) ||
            !IdentifiersAreValid(build, prereleaseIdentifiers: false))
            return false;

        parsed = new CapabilitySemanticVersion(value, major, minor, patch, prerelease, build);
        return true;
    }

    public int CompareTo(CapabilitySemanticVersion? other)
    {
        if (other is null) return 1;
        var comparison = Major.CompareTo(other.Major);
        if (comparison != 0) return comparison;
        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0) return comparison;
        comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0) return comparison;
        if (Prerelease.Count == 0 || other.Prerelease.Count == 0)
            return Prerelease.Count == other.Prerelease.Count ? 0 : Prerelease.Count == 0 ? 1 : -1;
        for (var index = 0; index < Math.Min(Prerelease.Count, other.Prerelease.Count); index++)
        {
            comparison = ComparePrereleaseIdentifier(Prerelease[index], other.Prerelease[index]);
            if (comparison != 0) return comparison;
        }
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public bool Equals(CapabilitySemanticVersion? other) =>
        other is not null && StringComparer.Ordinal.Equals(Value, other.Value);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    private static IReadOnlyList<string> SplitIdentifiers(string value) =>
        value.Length == 0 ? [] : Array.AsReadOnly(value.Split('.'));

    private static bool IdentifiersAreValid(IReadOnlyList<string> identifiers, bool prereleaseIdentifiers)
    {
        if (identifiers.Count > 8) return false;
        foreach (var identifier in identifiers)
        {
            if (identifier.Length is < 1 or > 32 ||
                identifier.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')) ||
                prereleaseIdentifiers && identifier.All(char.IsAsciiDigit) && identifier.Length > 1 && identifier[0] == '0')
                return false;
        }
        return true;
    }

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
        if (!leftNumeric) return StringComparer.Ordinal.Compare(left, right);
        var lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0 ? lengthComparison : StringComparer.Ordinal.Compare(left, right);
    }
}

public sealed record CapabilityContentHash
{
    private CapabilityContentHash(string value) => Value = value;

    public string Value { get; }

    public static CapabilityContentHash Parse(string value)
    {
        if (!TryParse(value, out var parsed))
            throw new FormatException("Capability content hash 형식이 올바르지 않습니다.");
        return parsed!;
    }

    public static bool TryParse(string? value, out CapabilityContentHash? parsed)
    {
        parsed = null;
        if (value is null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal)) return false;
        if (value.AsSpan(7).ContainsAnyExcept("0123456789abcdef")) return false;
        parsed = new CapabilityContentHash(value);
        return true;
    }

    public override string ToString() => Value;
}

public enum CapabilityKind
{
    Prompt,
    Workflow,
    Connector,
}

public enum CapabilityDraftState
{
    Draft,
    Validating,
    ReadyForReview,
    Applied,
    Rejected,
    Stale,
}

public sealed record CapabilityConnectionRequirement(string Slot, string Kind);

public sealed record CapabilityManifest
{
    private IReadOnlyList<string> _intentExamples = [];
    private IReadOnlyList<string> _requiredTools = [];
    private IReadOnlyList<CapabilityConnectionRequirement> _requiredConnections = [];

    public CapabilityManifest(
        int schemaVersion,
        CapabilityId id,
        CapabilitySemanticVersion version,
        string displayName,
        string description,
        CapabilityKind kind,
        IReadOnlyList<string> intentExamples,
        IReadOnlyList<string> requiredTools,
        IReadOnlyList<CapabilityConnectionRequirement> requiredConnections,
        string maximumRisk,
        string entrypoint)
    {
        SchemaVersion = schemaVersion;
        Id = id;
        Version = version;
        DisplayName = displayName;
        Description = description;
        Kind = kind;
        IntentExamples = intentExamples;
        RequiredTools = requiredTools;
        RequiredConnections = requiredConnections;
        MaximumRisk = maximumRisk;
        Entrypoint = entrypoint;
    }

    public int SchemaVersion { get; init; }
    public CapabilityId Id { get; init; }
    public CapabilitySemanticVersion Version { get; init; }
    public string DisplayName { get; init; }
    public string Description { get; init; }
    public CapabilityKind Kind { get; init; }
    public IReadOnlyList<string> IntentExamples
    {
        get => _intentExamples;
        init => _intentExamples = Copy(value);
    }
    public IReadOnlyList<string> RequiredTools
    {
        get => _requiredTools;
        init => _requiredTools = Copy(value);
    }
    public IReadOnlyList<CapabilityConnectionRequirement> RequiredConnections
    {
        get => _requiredConnections;
        init => _requiredConnections = Copy(value);
    }
    public string MaximumRisk { get; init; }
    public string Entrypoint { get; init; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Array.AsReadOnly(source.ToArray());
    }
}

public sealed record CapabilityVersionPayload(
    CapabilityManifest Manifest,
    string CanonicalManifestJson,
    string Instructions,
    string CanonicalInputSchemaJson,
    string? CanonicalSettingsSchemaJson = null,
    string? CanonicalWorkflowJson = null,
    string CanonicalTestsJson = "[]");

public sealed record CapabilityVersionRef(
    CapabilityId Id,
    CapabilitySemanticVersion Version,
    CapabilityContentHash ContentHash);

public sealed record CapabilityDraft(
    string DraftId,
    CapabilityVersionRef ExactVersion,
    CapabilityVersionRef? Parent,
    CapabilityVersionPayload Payload,
    CapabilityDraftState State,
    long Revision,
    string SourceKind,
    CapabilityContentHash SourcePackageHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CapabilityVersion(
    CapabilityVersionRef ExactVersion,
    CapabilityVersionRef? Parent,
    CapabilityVersionPayload Payload,
    string SourceKind,
    CapabilityContentHash SourcePackageHash,
    DateTimeOffset CreatedAtUtc);

public sealed record CapabilityCatalogEntry(
    CapabilityId Id,
    CapabilityVersionRef? ActiveVersion,
    long CatalogRevision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CapabilityActivation(
    string ActivationId,
    CapabilityId Id,
    long CatalogRevision,
    string Action,
    CapabilityVersionRef? From,
    CapabilityVersionRef To,
    string ReasonCode,
    DateTimeOffset CreatedAtUtc);

public sealed record CapabilityValidationResult(bool IsValid, string? ErrorCode = null)
{
    public static CapabilityValidationResult Valid { get; } = new(true);
    public static CapabilityValidationResult Invalid(string errorCode) => new(false, errorCode);
}

public static class CapabilityDefinitionPolicy
{
    public const int MaximumManifestCharacters = 65_536;
    public const int MaximumInstructionsCharacters = 131_072;
    public const int MaximumSchemaCharacters = 131_072;
    public const int MaximumWorkflowCharacters = 262_144;
    public const int MaximumTestsCharacters = 262_144;
    public const int MaximumTotalCharacters = 1_048_576;

    public static CapabilityValidationResult Validate(CapabilityVersionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var manifest = payload.Manifest;
        if (manifest.SchemaVersion != 1) return CapabilityValidationResult.Invalid("capability.schema_version.unsupported");
        if (manifest.DisplayName.Length is < 1 or > 80 || manifest.Description.Length is < 1 or > 500)
            return CapabilityValidationResult.Invalid("capability.manifest.text_out_of_bounds");
        if (manifest.IntentExamples.Count is < 1 or > 16 ||
            manifest.IntentExamples.Any(value => value.Length is < 1 or > 160) ||
            manifest.IntentExamples.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.IntentExamples.Count)
            return CapabilityValidationResult.Invalid("capability.manifest.intent_invalid");
        if (manifest.RequiredTools.Count > 32 ||
            manifest.RequiredTools.Any(value => value.Length is < 1 or > 128) ||
            manifest.RequiredTools.Distinct(StringComparer.Ordinal).Count() != manifest.RequiredTools.Count)
            return CapabilityValidationResult.Invalid("capability.manifest.tool_invalid");
        if (manifest.RequiredConnections.Count > 8 || manifest.RequiredConnections.Any(requirement =>
                !IsConnectionIdentifier(requirement.Slot, 64, allowUnderscore: true) ||
                !IsConnectionIdentifier(requirement.Kind, 32, allowUnderscore: false)) ||
            manifest.RequiredConnections.Select(requirement => requirement.Slot).Distinct(StringComparer.Ordinal).Count() != manifest.RequiredConnections.Count)
            return CapabilityValidationResult.Invalid("capability.manifest.connection_invalid");
        if (manifest.MaximumRisk is not ("R0" or "R1" or "R2" or "R3" or "R4"))
            return CapabilityValidationResult.Invalid("capability.manifest.risk_invalid");
        if (manifest.Entrypoint.Length is < 1 or > 128)
            return CapabilityValidationResult.Invalid("capability.manifest.entrypoint_invalid");
        if (payload.CanonicalManifestJson.Length is < 2 or > MaximumManifestCharacters ||
            payload.Instructions.Length is < 1 or > MaximumInstructionsCharacters ||
            payload.CanonicalInputSchemaJson.Length is < 2 or > MaximumSchemaCharacters ||
            payload.CanonicalSettingsSchemaJson?.Length > MaximumSchemaCharacters ||
            payload.CanonicalWorkflowJson?.Length > MaximumWorkflowCharacters ||
            payload.CanonicalTestsJson.Length > MaximumTestsCharacters)
            return CapabilityValidationResult.Invalid("capability.payload.out_of_bounds");
        var total = (long)payload.CanonicalManifestJson.Length + payload.Instructions.Length +
                    payload.CanonicalInputSchemaJson.Length + (payload.CanonicalSettingsSchemaJson?.Length ?? 0) +
                    (payload.CanonicalWorkflowJson?.Length ?? 0) + payload.CanonicalTestsJson.Length;
        if (total > MaximumTotalCharacters) return CapabilityValidationResult.Invalid("capability.payload.total_out_of_bounds");
        if (manifest.Kind == CapabilityKind.Prompt &&
            (!StringComparer.Ordinal.Equals(manifest.Entrypoint, "instructions.md") || payload.CanonicalWorkflowJson is not null))
            return CapabilityValidationResult.Invalid("capability.prompt.executable_not_allowed");
        if (manifest.Kind == CapabilityKind.Workflow &&
            (!StringComparer.Ordinal.Equals(manifest.Entrypoint, "workflow.json") ||
             string.IsNullOrWhiteSpace(payload.CanonicalWorkflowJson)))
            return CapabilityValidationResult.Invalid("capability.workflow.entrypoint_invalid");
        return CapabilityValidationResult.Valid;
    }

    private static bool IsConnectionIdentifier(string value, int maximumLength, bool allowUnderscore) =>
        value.Length is >= 1 && value.Length <= maximumLength &&
        value[0] is >= 'a' and <= 'z' &&
        value.AsSpan(1).IndexOfAnyExcept(
            allowUnderscore ? "abcdefghijklmnopqrstuvwxyz0123456789_-" : "abcdefghijklmnopqrstuvwxyz0123456789-") < 0;
}

public static class CapabilityLifecyclePolicy
{
    public static bool CanTransition(CapabilityDraftState from, CapabilityDraftState to) => (from, to) switch
    {
        (CapabilityDraftState.Draft, CapabilityDraftState.Validating) => true,
        (CapabilityDraftState.Validating, CapabilityDraftState.ReadyForReview) => true,
        (CapabilityDraftState.Validating, CapabilityDraftState.Rejected) => true,
        (CapabilityDraftState.ReadyForReview, CapabilityDraftState.Applied) => true,
        (CapabilityDraftState.ReadyForReview, CapabilityDraftState.Rejected) => true,
        (CapabilityDraftState.ReadyForReview, CapabilityDraftState.Stale) => true,
        _ => false,
    };
}
