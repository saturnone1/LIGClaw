using System.IO;
using System.Text;
using System.Text.Json;
using LIGClaw.Application.Capabilities;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Capabilities;

internal sealed record PreparedCapabilityPayload(
    CapabilityManifest Manifest,
    CapabilityVersionPayload Payload);

internal static class CapabilityPayloadMaterializer
{
    internal static PreparedCapabilityPayload Prepare(IReadOnlyDictionary<string, byte[]> entries)
    {
        LigcapPackageSecurity.ValidateExpandedEntries(entries);
        Require(entries, LigcapPackageSecurity.ManifestEntry);

        var manifestJson = LigcapPackageSecurity.ParseJson(
            entries[LigcapPackageSecurity.ManifestEntry],
            LigcapPackageSecurity.ManifestEntry);
        LigcapPackageSecurity.RejectSecretLiterals(manifestJson.Root, LigcapPackageSecurity.ManifestEntry);
        var parsedManifest = LigcapPackageSecurity.ParseManifest(manifestJson.Root);
        ValidateEntrySet(entries, parsedManifest.Kind);

        var instructions = LigcapPackageSecurity.DecodeInstructions(entries[LigcapPackageSecurity.InstructionsEntry]);
        if (string.IsNullOrWhiteSpace(instructions))
            throw new LigcapValidationException("package.instructions.empty", LigcapPackageSecurity.InstructionsEntry);
        LigcapPackageSecurity.RejectSecretLiterals(instructions, LigcapPackageSecurity.InstructionsEntry);

        var inputSchema = LigcapPackageSecurity.ParseJson(
            entries[LigcapPackageSecurity.InputSchemaEntry],
            LigcapPackageSecurity.InputSchemaEntry);
        LigcapPackageSecurity.ValidateSchema(inputSchema.Root, LigcapPackageSecurity.InputSchemaEntry);
        LigcapPackageSecurity.RejectSecretLiterals(inputSchema.Root, LigcapPackageSecurity.InputSchemaEntry);

        CanonicalJsonPayload? settingsSchema = null;
        if (entries.TryGetValue(LigcapPackageSecurity.SettingsSchemaEntry, out var settingsBytes))
        {
            settingsSchema = LigcapPackageSecurity.ParseJson(settingsBytes, LigcapPackageSecurity.SettingsSchemaEntry);
            LigcapPackageSecurity.ValidateSchema(settingsSchema.Root, LigcapPackageSecurity.SettingsSchemaEntry);
            LigcapPackageSecurity.RejectSecretLiterals(settingsSchema.Root, LigcapPackageSecurity.SettingsSchemaEntry);
        }

        CanonicalJsonPayload? workflow = null;
        if (parsedManifest.Kind == "workflow")
        {
            workflow = LigcapPackageSecurity.ParseJson(
                entries[LigcapPackageSecurity.WorkflowEntry],
                LigcapPackageSecurity.WorkflowEntry);
            if (workflow.Root.ValueKind != JsonValueKind.Object)
                throw new LigcapValidationException(
                    "package.workflow.root_invalid",
                    LigcapPackageSecurity.WorkflowEntry,
                    string.Empty);
            LigcapPackageSecurity.RejectSecretLiterals(workflow.Root, LigcapPackageSecurity.WorkflowEntry);
        }

        var canonicalTests = "[]";
        if (entries.TryGetValue(LigcapPackageSecurity.TestsEntry, out var testsBytes))
        {
            var tests = LigcapPackageSecurity.ParseJson(testsBytes, LigcapPackageSecurity.TestsEntry);
            LigcapPackageSecurity.RejectSecretLiterals(tests.Root, LigcapPackageSecurity.TestsEntry);
            canonicalTests = Encoding.UTF8.GetString(tests.Utf8);
            var testValidation = CapabilityFixtureTestPolicy.Validate(canonicalTests);
            if (!testValidation.IsValid)
                throw new LigcapValidationException(
                    testValidation.ErrorCode ?? "package.tests.invalid",
                    LigcapPackageSecurity.TestsEntry);
        }

        var manifest = ToDomainManifest(parsedManifest);
        var payload = new CapabilityVersionPayload(
            manifest,
            Encoding.UTF8.GetString(manifestJson.Utf8),
            instructions,
            Encoding.UTF8.GetString(inputSchema.Utf8),
            settingsSchema is null ? null : Encoding.UTF8.GetString(settingsSchema.Utf8),
            workflow is null ? null : Encoding.UTF8.GetString(workflow.Utf8),
            canonicalTests);
        var validation = CapabilityDefinitionPolicy.Validate(payload);
        if (!validation.IsValid)
            throw new LigcapValidationException(validation.ErrorCode ?? "package.definition.invalid");
        var ingressViolation = CapabilityIngressPolicy.GetViolation(payload);
        if (ingressViolation == CapabilityIngressViolation.SecretLiteral)
            throw new LigcapValidationException("package.secret_literal.detected");
        if (ingressViolation == CapabilityIngressViolation.ConnectionLiteral)
            throw new LigcapValidationException("package.connection_literal.detected");
        return new PreparedCapabilityPayload(manifest, payload);
    }

    internal static IReadOnlyDictionary<string, byte[]> CreateSourceEntries(CapabilityVersionPayload payload)
    {
        switch (payload.Manifest.Kind)
        {
            case CapabilityKind.Prompt when payload.CanonicalWorkflowJson is not null:
                throw new InvalidDataException("package.prompt.executable_not_allowed");
            case CapabilityKind.Workflow when string.IsNullOrWhiteSpace(payload.CanonicalWorkflowJson):
                throw new InvalidDataException("package.workflow.entrypoint_invalid");
            case CapabilityKind.Prompt or CapabilityKind.Workflow:
                break;
            default:
                throw new InvalidDataException("package.kind.unsupported");
        }
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [LigcapPackageSecurity.ManifestEntry] = Encoding.UTF8.GetBytes(payload.CanonicalManifestJson),
            [LigcapPackageSecurity.InstructionsEntry] = Encoding.UTF8.GetBytes(payload.Instructions),
            [LigcapPackageSecurity.InputSchemaEntry] = Encoding.UTF8.GetBytes(payload.CanonicalInputSchemaJson),
        };
        if (payload.CanonicalSettingsSchemaJson is not null)
            entries[LigcapPackageSecurity.SettingsSchemaEntry] = Encoding.UTF8.GetBytes(payload.CanonicalSettingsSchemaJson);
        if (payload.CanonicalWorkflowJson is not null)
            entries[LigcapPackageSecurity.WorkflowEntry] = Encoding.UTF8.GetBytes(payload.CanonicalWorkflowJson);
        if (!StringComparer.Ordinal.Equals(payload.CanonicalTestsJson, "[]"))
            entries[LigcapPackageSecurity.TestsEntry] = Encoding.UTF8.GetBytes(payload.CanonicalTestsJson);
        return entries;
    }

    internal static IReadOnlyDictionary<string, byte[]> CreateCanonicalEntries(CapabilityVersionPayload payload) =>
        CreateSourceEntries(payload);

    internal static bool Equivalent(CapabilityManifest left, CapabilityManifest right) =>
        left.SchemaVersion == right.SchemaVersion &&
        left.Id.Equals(right.Id) &&
        left.Version.Equals(right.Version) &&
        left.DisplayName == right.DisplayName &&
        left.Description == right.Description &&
        left.Kind == right.Kind &&
        left.IntentExamples.SequenceEqual(right.IntentExamples, StringComparer.Ordinal) &&
        left.RequiredTools.SequenceEqual(right.RequiredTools, StringComparer.Ordinal) &&
        left.RequiredConnections.Select(connection => (connection.Slot, connection.Kind)).SequenceEqual(
            right.RequiredConnections.Select(connection => (connection.Slot, connection.Kind))) &&
        left.MaximumRisk == right.MaximumRisk &&
        left.Entrypoint == right.Entrypoint;

    private static CapabilityManifest ToDomainManifest(ParsedCapabilityManifest manifest)
    {
        try
        {
            return new CapabilityManifest(
                manifest.SchemaVersion,
                CapabilityId.Parse(manifest.Id),
                CapabilitySemanticVersion.Parse(manifest.Version),
                manifest.DisplayName,
                manifest.Description,
                manifest.Kind switch
                {
                    "prompt" => CapabilityKind.Prompt,
                    "workflow" => CapabilityKind.Workflow,
                    _ => throw new LigcapValidationException(
                        "package.manifest.kind_unsupported",
                        LigcapPackageSecurity.ManifestEntry,
                        "/kind"),
                },
                manifest.IntentExamples,
                manifest.RequiredTools,
                manifest.RequiredConnections
                    .Select(connection => new CapabilityConnectionRequirement(connection.Slot, connection.Kind))
                    .ToArray(),
                manifest.MaximumRisk,
                manifest.Entrypoint);
        }
        catch (FormatException)
        {
            throw new LigcapValidationException(
                "package.manifest.identity_invalid",
                LigcapPackageSecurity.ManifestEntry);
        }
    }

    private static void ValidateEntrySet(IReadOnlyDictionary<string, byte[]> entries, string kind)
    {
        Require(entries, LigcapPackageSecurity.InstructionsEntry);
        Require(entries, LigcapPackageSecurity.InputSchemaEntry);

        if (kind == "workflow")
            Require(entries, LigcapPackageSecurity.WorkflowEntry);
        else if (entries.ContainsKey(LigcapPackageSecurity.WorkflowEntry))
            throw new LigcapValidationException("package.entry.unsupported", LigcapPackageSecurity.WorkflowEntry);

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            LigcapPackageSecurity.ManifestEntry,
            LigcapPackageSecurity.InstructionsEntry,
            LigcapPackageSecurity.InputSchemaEntry,
            LigcapPackageSecurity.SettingsSchemaEntry,
            LigcapPackageSecurity.TestsEntry,
        };
        if (kind == "workflow") allowed.Add(LigcapPackageSecurity.WorkflowEntry);
        var unexpected = entries.Keys.FirstOrDefault(name => !allowed.Contains(name));
        if (unexpected is not null)
            throw new LigcapValidationException("package.entry.unsupported", unexpected);
    }

    private static void Require(IReadOnlyDictionary<string, byte[]> entries, string name)
    {
        if (!entries.ContainsKey(name))
            throw new LigcapValidationException("package.entry.required_missing", name);
    }
}
