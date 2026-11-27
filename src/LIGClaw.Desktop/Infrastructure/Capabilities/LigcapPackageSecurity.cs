using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LIGClaw.Application.Capabilities;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Infrastructure.Capabilities;

internal sealed class LigcapValidationException(string code, string? entryName = null, string? jsonPointer = null)
    : Exception(code)
{
    public string Code { get; } = code;
    public string? EntryName { get; } = entryName;
    public string? JsonPointer { get; } = jsonPointer;
}

internal sealed record LigcapArchiveContents(
    IReadOnlyDictionary<string, byte[]> Entries,
    string SourcePackageSha256);

internal sealed record CanonicalJsonPayload(JsonElement Root, byte[] Utf8);

internal sealed record ParsedCapabilityManifest(
    int SchemaVersion,
    string Id,
    string Version,
    string DisplayName,
    string Description,
    string Kind,
    IReadOnlyList<string> IntentExamples,
    IReadOnlyList<string> RequiredTools,
    IReadOnlyList<ParsedRequiredConnection> RequiredConnections,
    string MaximumRisk,
    string Entrypoint);

internal sealed record ParsedRequiredConnection(string Slot, string Kind);

internal static partial class LigcapPackageSecurity
{
    internal const int MaximumArchiveBytes = 1024 * 1024;
    internal const int MaximumEntries = 6;
    internal const int MaximumEntryNameUtf8Bytes = 64;
    internal const int MaximumExpandedBytes = 896 * 1024;
    internal const int MaximumJsonDepth = 32;
    internal const int MaximumJsonTokens = 4_096;

    internal const string ManifestEntry = "manifest.json";
    internal const string InstructionsEntry = "instructions.md";
    internal const string InputSchemaEntry = "input.schema.json";
    internal const string SettingsSchemaEntry = "settings.schema.json";
    internal const string WorkflowEntry = "workflow.json";
    internal const string TestsEntry = "tests.json";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly DateTimeOffset CanonicalZipTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<string, int> EntryLimits =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [ManifestEntry] = 32 * 1024,
            [InstructionsEntry] = 128 * 1024,
            [InputSchemaEntry] = 128 * 1024,
            [SettingsSchemaEntry] = 128 * 1024,
            [WorkflowEntry] = 256 * 1024,
            [TestsEntry] = 192 * 1024,
        });
    private static readonly HashSet<string> ManifestFields =
    [
        "schemaVersion", "id", "version", "displayName", "description", "kind", "intentExamples",
        "requiredTools", "requiredConnections", "maximumRisk", "entrypoint",
    ];
    private static readonly HashSet<string> ConnectionFields = ["slot", "kind"];
    private static readonly HashSet<string> SchemaKeywords =
    [
        "$schema", "type", "title", "description", "properties", "required", "additionalProperties",
        "items", "minItems", "maxItems", "uniqueItems", "minLength", "maxLength", "minimum", "maximum",
        "exclusiveMinimum", "exclusiveMaximum", "enum", "const", "format",
    ];
    private static readonly HashSet<string> SchemaTypes = ["object", "array", "string", "integer", "number", "boolean"];
    private static readonly HashSet<string> SchemaFormats = ["date", "date-time", "email", "uuid"];

    internal static async ValueTask<LigcapArchiveContents> ReadArchiveAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var archiveBytes = await ReadBoundedAsync(
            source,
            MaximumArchiveBytes,
            "package.archive.too_large",
            null,
            cancellationToken).ConfigureAwait(false);
        var sourceHash = Convert.ToHexStringLower(SHA256.HashData(archiveBytes));

        try
        {
            using var memory = new MemoryStream(archiveBytes, writable: false);
            using var archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
            if (archive.Entries.Count is < 1 or > MaximumEntries)
                throw new LigcapValidationException("package.entry_count.invalid");

            ValidateEntryNames(archive.Entries);
            long declaredTotal = 0;
            foreach (var entry in archive.Entries)
            {
                ValidateEntryMetadata(entry);
                var maximum = EntryLimits[entry.FullName];
                if (entry.Length < 0 || entry.Length > maximum)
                    throw new LigcapValidationException("package.entry.too_large", entry.FullName);
                declaredTotal = checked(declaredTotal + entry.Length);
                if (declaredTotal > MaximumExpandedBytes)
                    throw new LigcapValidationException("package.expanded.too_large");
            }

            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long actualTotal = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var entryStream = entry.Open();
                var bytes = await ReadBoundedAsync(
                    entryStream,
                    EntryLimits[entry.FullName],
                    "package.entry.too_large",
                    entry.FullName,
                    cancellationToken).ConfigureAwait(false);
                actualTotal = checked(actualTotal + bytes.Length);
                if (actualTotal > MaximumExpandedBytes)
                    throw new LigcapValidationException("package.expanded.too_large");
                if (LooksLikeNestedArchiveOrExecutable(bytes))
                    throw new LigcapValidationException("package.entry.nested_archive_or_executable", entry.FullName);
                entries.Add(entry.FullName, bytes);
            }

            return new LigcapArchiveContents(
                new ReadOnlyDictionary<string, byte[]>(entries),
                sourceHash);
        }
        catch (LigcapValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException)
        {
            throw new LigcapValidationException("package.archive.invalid");
        }
    }

    internal static async ValueTask<byte[]> WriteArchiveAsync(
        IReadOnlyDictionary<string, byte[]> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count is < 1 or > MaximumEntries)
            throw new LigcapValidationException("package.entry_count.invalid");
        var orderedNames = CanonicalEntryOrder(entries.Keys);
        ValidateEntryNames(orderedNames);

        long total = 0;
        foreach (var name in orderedNames)
        {
            var bytes = entries[name];
            if (bytes.Length > EntryLimits[name])
                throw new LigcapValidationException("package.entry.too_large", name);
            total = checked(total + bytes.Length);
            if (total > MaximumExpandedBytes)
                throw new LigcapValidationException("package.expanded.too_large");
        }

        await using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (var name in orderedNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = CanonicalZipTimestamp;
                entry.ExternalAttributes = unchecked((int)(0x81A4u << 16)); // Unix regular file, 0644.
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(entries[name], cancellationToken).ConfigureAwait(false);
            }
        }

        if (output.Length > MaximumArchiveBytes)
            throw new LigcapValidationException("package.archive.too_large");
        return output.ToArray();
    }

    internal static void ValidateExpandedEntries(IReadOnlyDictionary<string, byte[]> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count is < 1 or > MaximumEntries)
            throw new LigcapValidationException("package.entry_count.invalid");

        ValidateEntryNames(entries.Keys);
        long total = 0;
        foreach (var (name, bytes) in entries)
        {
            if (bytes is null)
                throw new LigcapValidationException("package.entry.invalid", name);
            if (bytes.Length > EntryLimits[name])
                throw new LigcapValidationException("package.entry.too_large", name);
            total = checked(total + bytes.Length);
            if (total > MaximumExpandedBytes)
                throw new LigcapValidationException("package.expanded.too_large");
            if (LooksLikeNestedArchiveOrExecutable(bytes))
                throw new LigcapValidationException("package.entry.nested_archive_or_executable", name);
        }
    }

    internal static string DecodeInstructions(byte[] bytes)
    {
        var text = DecodeStrictUtf8(bytes, InstructionsEntry);
        EnsurePermittedTextCharacters(text, InstructionsEntry);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    internal static CanonicalJsonPayload ParseJson(byte[] bytes, string entryName)
    {
        _ = DecodeStrictUtf8(bytes, entryName);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumJsonDepth,
        });
        var tokenCount = 0;
        try
        {
            while (reader.Read())
            {
                if (++tokenCount > MaximumJsonTokens)
                    throw new LigcapValidationException("package.json.tokens_exceeded", entryName);
                if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String)
                {
                    var value = reader.GetString() ?? string.Empty;
                    if (reader.TokenType == JsonTokenType.PropertyName && value.Length > 128)
                        throw new LigcapValidationException("package.json.property_name_too_long", entryName);
                    if (reader.TokenType == JsonTokenType.String && value.Length > 8_192)
                        throw new LigcapValidationException("package.json.string_too_long", entryName);
                    EnsurePermittedTextCharacters(value, entryName);
                }
                else if (reader.TokenType == JsonTokenType.Number &&
                         (reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length) > 128)
                {
                    throw new LigcapValidationException("package.json.number_too_long", entryName);
                }
            }
        }
        catch (LigcapValidationException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new LigcapValidationException("package.json.invalid", entryName);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth,
            });
            ValidateJsonTree(document.RootElement, entryName, string.Empty);
            var root = document.RootElement.Clone();
            return new CanonicalJsonPayload(root, CanonicalizeJson(root));
        }
        catch (LigcapValidationException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new LigcapValidationException("package.json.invalid", entryName);
        }
    }

    internal static ParsedCapabilityManifest ParseManifest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new LigcapValidationException("package.manifest.root_invalid", ManifestEntry, string.Empty);
        var fields = root.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        if (!fields.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(ManifestFields))
            throw new LigcapValidationException("package.manifest.fields_invalid", ManifestEntry, string.Empty);

        var schemaVersion = RequiredInt32(fields, "schemaVersion");
        var id = RequiredString(fields, "id", 3, 128);
        var version = RequiredString(fields, "version", 5, 64);
        var displayName = RequiredString(fields, "displayName", 1, 80);
        var description = RequiredString(fields, "description", 1, 500);
        var kind = RequiredString(fields, "kind", 1, 16);
        var maximumRisk = RequiredString(fields, "maximumRisk", 2, 2);
        var entrypoint = RequiredString(fields, "entrypoint", 1, 64);
        var intentExamples = RequiredStringArray(fields, "intentExamples", 1, 16, 160);
        var requiredTools = RequiredStringArray(fields, "requiredTools", 0, 32, 128);
        var requiredConnections = RequiredConnections(fields);

        if (schemaVersion != 1)
            throw new LigcapValidationException("package.manifest.schema_version_unsupported", ManifestEntry, "/schemaVersion");
        if (!CapabilityId.TryParse(id, out _))
            throw new LigcapValidationException("package.manifest.id_invalid", ManifestEntry, "/id");
        if (!CapabilitySemanticVersion.TryParse(version, out _))
            throw new LigcapValidationException("package.manifest.version_invalid", ManifestEntry, "/version");
        if (kind is not ("prompt" or "workflow"))
            throw new LigcapValidationException("package.manifest.kind_unsupported", ManifestEntry, "/kind");
        var expectedEntrypoint = kind == "prompt" ? InstructionsEntry : WorkflowEntry;
        if (entrypoint != expectedEntrypoint)
            throw new LigcapValidationException("package.manifest.entrypoint_invalid", ManifestEntry, "/entrypoint");
        if (maximumRisk is not ("R0" or "R1" or "R2" or "R3" or "R4"))
            throw new LigcapValidationException("package.manifest.risk_invalid", ManifestEntry, "/maximumRisk");
        if (requiredTools.Any(tool => !ToolOrSlotPattern().IsMatch(tool)))
            throw new LigcapValidationException("package.manifest.tool_invalid", ManifestEntry, "/requiredTools");

        return new ParsedCapabilityManifest(
            schemaVersion,
            id,
            version,
            displayName,
            description,
            kind,
            intentExamples,
            requiredTools,
            requiredConnections,
            maximumRisk,
            entrypoint);
    }

    internal static void ValidateSchema(JsonElement root, string entryName)
    {
        var propertyCount = 0;
        ValidateSchemaNode(root, entryName, string.Empty, 0, true, ref propertyCount);
    }

    internal static void RejectSecretLiterals(string text, string entryName)
    {
        var scrubbed = AllowedPlaceholderPattern().Replace(text, string.Empty);
        if (PrivateKeyPattern().IsMatch(scrubbed) ||
            KnownTokenPattern().IsMatch(scrubbed) ||
            JwtPattern().IsMatch(scrubbed) ||
            AuthorizationPattern().IsMatch(scrubbed) ||
            CredentialAssignmentPattern().IsMatch(scrubbed) ||
            UriUserInfoPattern().IsMatch(scrubbed))
        {
            throw new LigcapValidationException("package.secret_literal.detected", entryName);
        }
    }

    internal static void RejectSecretLiterals(JsonElement root, string entryName)
    {
        VisitJsonStrings(root, entryName, string.Empty);
        if (CapabilityIngressPolicy.ContainsSensitiveJsonLiteral(root))
            throw new LigcapValidationException("package.secret_literal.detected", entryName);
    }

    private static void ValidateEntryNames(IReadOnlyList<ZipArchiveEntry> entries) =>
        ValidateEntryNames(entries.Select(entry => entry.FullName));

    private static void ValidateEntryNames(IEnumerable<string> names)
    {
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var materialized = names.ToArray();
        foreach (var name in materialized)
        {
            if (!IsSafeCanonicalEntryName(name))
                throw new LigcapValidationException("package.entry.path_invalid", name);
            if (!exact.Add(name))
                throw new LigcapValidationException("package.entry.duplicate", name);
            if (!insensitive.Add(name))
                throw new LigcapValidationException("package.entry.case_collision", name);
        }
        foreach (var name in materialized)
        {
            if (!EntryLimits.ContainsKey(name))
                throw new LigcapValidationException("package.entry.unsupported", name);
        }
    }

    private static bool IsSafeCanonicalEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            Encoding.UTF8.GetByteCount(name) > MaximumEntryNameUtf8Bytes ||
            !name.IsNormalized(NormalizationForm.FormC) ||
            name.Contains('\\') ||
            name.Contains(':') ||
            name[0] == '/' ||
            name[^1] == '/' ||
            Path.IsPathRooted(name) ||
            name.Any(char.IsControl))
        {
            return false;
        }
        var segments = name.Split('/');
        return segments.All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private static void ValidateEntryMetadata(ZipArchiveEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Name))
            throw new LigcapValidationException("package.entry.directory", entry.FullName);
        var attributes = unchecked((uint)entry.ExternalAttributes);
        var unixType = (attributes >> 16) & 0xF000u;
        if (unixType == 0xA000u)
            throw new LigcapValidationException("package.entry.symlink", entry.FullName);
        if (unixType is not (0u or 0x8000u))
            throw new LigcapValidationException("package.entry.non_regular", entry.FullName);
        var dosAttributes = (FileAttributes)(attributes & 0xFFFFu);
        if ((dosAttributes & FileAttributes.ReparsePoint) != 0)
            throw new LigcapValidationException("package.entry.reparse_point", entry.FullName);
        if ((dosAttributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
            throw new LigcapValidationException("package.entry.non_regular", entry.FullName);
    }

    private static async ValueTask<byte[]> ReadBoundedAsync(
        Stream source,
        int maximumBytes,
        string errorCode,
        string? entryName,
        CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var total = 0;
            while (true)
            {
                var remaining = maximumBytes - total;
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > maximumBytes)
                    throw new LigcapValidationException(errorCode, entryName);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static string DecodeStrictUtf8(byte[] bytes, string entryName)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            throw new LigcapValidationException("package.text.bom_not_allowed", entryName);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new LigcapValidationException("package.text.invalid_utf8", entryName);
        }
    }

    private static void EnsurePermittedTextCharacters(string value, string entryName)
    {
        if (value.Any(character => char.IsControl(character) && character is not ('\t' or '\r' or '\n')))
            throw new LigcapValidationException("package.text.control_character", entryName);
        if (value.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format))
            throw new LigcapValidationException("package.text.format_character", entryName);
    }

    private static void ValidateJsonTree(JsonElement element, string entryName, string pointer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = element.EnumerateObject().ToArray();
            if (properties.Length > 128)
                throw new LigcapValidationException("package.json.object_too_large", entryName, pointer);
            var exact = new HashSet<string>(StringComparer.Ordinal);
            var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties)
            {
                var childPointer = pointer + "/" + EscapeJsonPointer(property.Name);
                if (!exact.Add(property.Name))
                    throw new LigcapValidationException("package.json.duplicate_property", entryName, childPointer);
                if (!insensitive.Add(property.Name))
                    throw new LigcapValidationException("package.json.case_collision", entryName, childPointer);
                ValidateJsonTree(property.Value, entryName, childPointer);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() > 128)
                throw new LigcapValidationException("package.json.array_too_large", entryName, pointer);
            var index = 0;
            foreach (var item in element.EnumerateArray())
                ValidateJsonTree(item, entryName, pointer + "/" + index++);
        }
    }

    private static byte[] CanonicalizeJson(JsonElement root)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false }))
            WriteCanonicalJson(writer, root);
        return output.ToArray();
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonicalJson(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static int RequiredInt32(IReadOnlyDictionary<string, JsonElement> fields, string name)
    {
        if (!fields[name].TryGetInt32(out var value))
            throw new LigcapValidationException("package.manifest.field_type_invalid", ManifestEntry, "/" + name);
        return value;
    }

    private static string RequiredString(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        int minimumLength,
        int maximumLength)
    {
        if (fields[name].ValueKind != JsonValueKind.String)
            throw new LigcapValidationException("package.manifest.field_type_invalid", ManifestEntry, "/" + name);
        var value = fields[name].GetString() ?? string.Empty;
        if (value.Length < minimumLength || value.Length > maximumLength || value.Trim() != value ||
            ContainsLineBreakingCharacter(value) ||
            !value.IsNormalized(NormalizationForm.FormC))
            throw new LigcapValidationException("package.manifest.field_value_invalid", ManifestEntry, "/" + name);
        return value;
    }

    private static IReadOnlyList<string> RequiredStringArray(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        int minimumCount,
        int maximumCount,
        int maximumItemLength)
    {
        var element = fields[name];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < minimumCount ||
            element.GetArrayLength() > maximumCount)
            throw new LigcapValidationException("package.manifest.field_type_invalid", ManifestEntry, "/" + name);
        var values = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new LigcapValidationException("package.manifest.field_type_invalid", ManifestEntry, $"/{name}/{index}");
            var value = item.GetString() ?? string.Empty;
            if (value.Length is < 1 || value.Length > maximumItemLength || value.Trim() != value ||
                ContainsLineBreakingCharacter(value) ||
                !value.IsNormalized(NormalizationForm.FormC))
                throw new LigcapValidationException("package.manifest.field_value_invalid", ManifestEntry, $"/{name}/{index}");
            if (!unique.Add(value))
                throw new LigcapValidationException("package.manifest.duplicate_value", ManifestEntry, $"/{name}/{index}");
            values.Add(value);
            index++;
        }
        return values.AsReadOnly();
    }

    private static bool ContainsLineBreakingCharacter(string value) =>
        value.AsSpan().ContainsAny('\t', '\r', '\n') ||
        value.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);

    private static IReadOnlyList<ParsedRequiredConnection> RequiredConnections(
        IReadOnlyDictionary<string, JsonElement> fields)
    {
        var element = fields["requiredConnections"];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 8)
            throw new LigcapValidationException("package.manifest.field_type_invalid", ManifestEntry, "/requiredConnections");
        var values = new List<ParsedRequiredConnection>();
        var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new LigcapValidationException("package.manifest.field_type_invalid", ManifestEntry, $"/requiredConnections/{index}");
            var properties = item.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            if (!properties.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(ConnectionFields))
                throw new LigcapValidationException("package.manifest.connection_fields_invalid", ManifestEntry, $"/requiredConnections/{index}");
            var slot = RequiredNestedString(properties, "slot", index);
            var kind = RequiredNestedString(properties, "kind", index);
            if (!ToolOrSlotPattern().IsMatch(slot) || !ToolOrSlotPattern().IsMatch(kind))
                throw new LigcapValidationException("package.manifest.connection_invalid", ManifestEntry, $"/requiredConnections/{index}");
            if (!slots.Add(slot))
                throw new LigcapValidationException("package.manifest.duplicate_connection", ManifestEntry, $"/requiredConnections/{index}/slot");
            values.Add(new ParsedRequiredConnection(slot, kind));
            index++;
        }
        return values.AsReadOnly();
    }

    private static string RequiredNestedString(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        int index)
    {
        if (properties[name].ValueKind != JsonValueKind.String)
            throw new LigcapValidationException("package.manifest.field_type_invalid", ManifestEntry, $"/requiredConnections/{index}/{name}");
        var value = properties[name].GetString() ?? string.Empty;
        if (value.Length is < 1 or > 64 || value.Trim() != value)
            throw new LigcapValidationException("package.manifest.field_value_invalid", ManifestEntry, $"/requiredConnections/{index}/{name}");
        return value;
    }

    private static void ValidateSchemaNode(
        JsonElement node,
        string entryName,
        string pointer,
        int depth,
        bool isRoot,
        ref int propertyCount)
    {
        if (depth > 8 || node.ValueKind != JsonValueKind.Object)
            throw new LigcapValidationException("package.schema.node_invalid", entryName, pointer);
        var fields = node.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        if (fields.Keys.Any(field => !SchemaKeywords.Contains(field)))
            throw new LigcapValidationException("package.schema.keyword_unsupported", entryName, pointer);
        if (!fields.TryGetValue("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            throw new LigcapValidationException("package.schema.type_required", entryName, pointer + "/type");
        var type = typeElement.GetString() ?? string.Empty;
        if (!SchemaTypes.Contains(type) || isRoot && type != "object")
            throw new LigcapValidationException("package.schema.type_invalid", entryName, pointer + "/type");

        if (fields.TryGetValue("$schema", out var schemaDeclaration) &&
            (!isRoot || schemaDeclaration.ValueKind != JsonValueKind.String ||
             schemaDeclaration.GetString() != "https://json-schema.org/draft/2020-12/schema"))
            throw new LigcapValidationException("package.schema.declaration_invalid", entryName, pointer + "/$schema");
        ValidateSchemaAnnotations(fields, entryName, pointer);
        ValidateEnumAndConst(fields, type, entryName, pointer);

        switch (type)
        {
            case "object":
                ValidateObjectSchema(fields, entryName, pointer, depth, ref propertyCount);
                RejectFields(fields, entryName, pointer, "items", "minItems", "maxItems", "minLength", "maxLength",
                    "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "format", "uniqueItems");
                break;
            case "array":
                ValidateArraySchema(fields, entryName, pointer, depth, ref propertyCount);
                RejectFields(fields, entryName, pointer, "properties", "required", "additionalProperties", "minLength",
                    "maxLength", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "format");
                break;
            case "string":
                ValidateStringSchema(fields, entryName, pointer);
                RejectFields(fields, entryName, pointer, "properties", "required", "additionalProperties", "items",
                    "minItems", "maxItems", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
                    "uniqueItems");
                break;
            case "integer":
            case "number":
                ValidateNumberSchema(fields, entryName, pointer);
                RejectFields(fields, entryName, pointer, "properties", "required", "additionalProperties", "items",
                    "minItems", "maxItems", "minLength", "maxLength", "format", "uniqueItems");
                break;
            default:
                RejectFields(fields, entryName, pointer, "properties", "required", "additionalProperties", "items",
                    "minItems", "maxItems", "minLength", "maxLength", "minimum", "maximum",
                    "exclusiveMinimum", "exclusiveMaximum", "format", "uniqueItems");
                break;
        }
    }

    private static void ValidateObjectSchema(
        IReadOnlyDictionary<string, JsonElement> fields,
        string entryName,
        string pointer,
        int depth,
        ref int propertyCount)
    {
        if (!fields.TryGetValue("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
            !fields.TryGetValue("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False)
            throw new LigcapValidationException("package.schema.object_unbounded", entryName, pointer);
        var propertyMap = properties.EnumerateObject().ToArray();
        propertyCount = checked(propertyCount + propertyMap.Length);
        if (propertyCount > 128)
            throw new LigcapValidationException("package.schema.properties_exceeded", entryName, pointer + "/properties");
        var names = propertyMap.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var property in propertyMap)
        {
            if (property.Name.Length is < 1 or > 80)
                throw new LigcapValidationException("package.schema.property_invalid", entryName, pointer + "/properties");
            ValidateSchemaNode(property.Value, entryName, pointer + "/properties/" + EscapeJsonPointer(property.Name),
                depth + 1, false, ref propertyCount);
        }
        if (!fields.TryGetValue("required", out var required)) return;
        if (required.ValueKind != JsonValueKind.Array || required.GetArrayLength() > names.Count)
            throw new LigcapValidationException("package.schema.required_invalid", entryName, pointer + "/required");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in required.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } name ||
                !names.Contains(name) || !seen.Add(name))
                throw new LigcapValidationException("package.schema.required_invalid", entryName, pointer + "/required");
        }
    }

    private static void ValidateArraySchema(
        IReadOnlyDictionary<string, JsonElement> fields,
        string entryName,
        string pointer,
        int depth,
        ref int propertyCount)
    {
        if (!fields.TryGetValue("items", out var items) ||
            !TryBoundedInteger(fields, "maxItems", 0, 64, out var maximum))
            throw new LigcapValidationException("package.schema.array_unbounded", entryName, pointer);
        if (fields.TryGetValue("minItems", out _) &&
            (!TryBoundedInteger(fields, "minItems", 0, maximum, out _)))
            throw new LigcapValidationException("package.schema.array_bounds_invalid", entryName, pointer);
        if (fields.TryGetValue("uniqueItems", out var uniqueItems))
        {
            if (uniqueItems.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new LigcapValidationException(
                    "package.schema.unique_items_invalid",
                    entryName,
                    pointer + "/uniqueItems");
            if (uniqueItems.GetBoolean() &&
                (items.ValueKind != JsonValueKind.Object ||
                 !items.TryGetProperty("type", out var itemType) ||
                 itemType.ValueKind != JsonValueKind.String ||
                 !StringComparer.Ordinal.Equals(itemType.GetString(), "string")))
                throw new LigcapValidationException(
                    "package.schema.unique_items_unsupported",
                    entryName,
                    pointer + "/uniqueItems");
        }
        ValidateSchemaNode(items, entryName, pointer + "/items", depth + 1, false, ref propertyCount);
    }

    private static void ValidateStringSchema(
        IReadOnlyDictionary<string, JsonElement> fields,
        string entryName,
        string pointer)
    {
        if (!TryBoundedInteger(fields, "maxLength", 0, 4_096, out var maximum))
            throw new LigcapValidationException("package.schema.string_unbounded", entryName, pointer);
        if (fields.TryGetValue("minLength", out _) &&
            !TryBoundedInteger(fields, "minLength", 0, maximum, out _))
            throw new LigcapValidationException("package.schema.string_bounds_invalid", entryName, pointer);
        if (fields.TryGetValue("format", out var format) &&
            (format.ValueKind != JsonValueKind.String || !SchemaFormats.Contains(format.GetString() ?? string.Empty)))
            throw new LigcapValidationException("package.schema.format_unsupported", entryName, pointer + "/format");
    }

    private static void ValidateNumberSchema(
        IReadOnlyDictionary<string, JsonElement> fields,
        string entryName,
        string pointer)
    {
        foreach (var name in new[] { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum" })
        {
            if (fields.TryGetValue(name, out var value) &&
                (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out _)))
                throw new LigcapValidationException("package.schema.number_bound_invalid", entryName, pointer + "/" + name);
        }
        if (fields.TryGetValue("minimum", out var minimum) && fields.TryGetValue("maximum", out var maximum) &&
            minimum.GetDecimal() > maximum.GetDecimal())
            throw new LigcapValidationException("package.schema.number_bounds_invalid", entryName, pointer);
    }

    private static void ValidateSchemaAnnotations(
        IReadOnlyDictionary<string, JsonElement> fields,
        string entryName,
        string pointer)
    {
        if (fields.TryGetValue("title", out var title) &&
            (title.ValueKind != JsonValueKind.String || (title.GetString()?.Length ?? 0) is < 1 or > 200))
            throw new LigcapValidationException("package.schema.annotation_invalid", entryName, pointer + "/title");
        if (fields.TryGetValue("description", out var description) &&
            (description.ValueKind != JsonValueKind.String || (description.GetString()?.Length ?? 0) > 1_000))
            throw new LigcapValidationException("package.schema.annotation_invalid", entryName, pointer + "/description");
    }

    private static void ValidateEnumAndConst(
        IReadOnlyDictionary<string, JsonElement> fields,
        string type,
        string entryName,
        string pointer)
    {
        if (fields.TryGetValue("enum", out var values))
        {
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > 64)
                throw new LigcapValidationException("package.schema.enum_invalid", entryName, pointer + "/enum");
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values.EnumerateArray())
            {
                if (!MatchesSchemaType(value, type) || !unique.Add(value.GetRawText()))
                    throw new LigcapValidationException("package.schema.enum_invalid", entryName, pointer + "/enum");
            }
        }
        if (fields.TryGetValue("const", out var constant) && !MatchesSchemaType(constant, type))
            throw new LigcapValidationException("package.schema.const_invalid", entryName, pointer + "/const");
    }

    private static bool MatchesSchemaType(JsonElement value, string type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        _ => false,
    };

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

    private static void RejectFields(
        IReadOnlyDictionary<string, JsonElement> fields,
        string entryName,
        string pointer,
        params string[] forbidden)
    {
        var found = forbidden.FirstOrDefault(fields.ContainsKey);
        if (found is not null)
            throw new LigcapValidationException("package.schema.keyword_inapplicable", entryName, pointer + "/" + found);
    }

    private static void VisitJsonStrings(JsonElement element, string entryName, string pointer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                try
                {
                    RejectSecretLiterals(element.GetString() ?? string.Empty, entryName);
                }
                catch (LigcapValidationException exception)
                {
                    throw new LigcapValidationException(exception.Code, entryName, pointer);
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    VisitJsonStrings(property.Value, entryName, pointer + "/" + EscapeJsonPointer(property.Name));
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    VisitJsonStrings(item, entryName, pointer + "/" + index++);
                break;
        }
    }

    private static IReadOnlyList<string> CanonicalEntryOrder(IEnumerable<string> names)
    {
        var present = names.ToHashSet(StringComparer.Ordinal);
        var result = new List<string>(MaximumEntries);
        foreach (var name in new[]
                 {
                     ManifestEntry,
                     InstructionsEntry,
                     InputSchemaEntry,
                      SettingsSchemaEntry,
                      WorkflowEntry,
                      TestsEntry,
                 })
        {
            if (present.Contains(name)) result.Add(name);
        }
        if (result.Count != present.Count)
            throw new LigcapValidationException("package.entry.unsupported");
        return result;
    }

    private static bool LooksLikeNestedArchiveOrExecutable(ReadOnlySpan<byte> bytes)
    {
        return bytes.StartsWith("PK\u0003\u0004"u8) ||
               bytes.StartsWith("PK\u0005\u0006"u8) ||
               bytes.StartsWith("PK\u0007\u0008"u8) ||
               bytes.StartsWith(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 }) ||
               bytes.StartsWith(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }) ||
               bytes.StartsWith(new byte[] { 0x1F, 0x8B }) ||
               bytes.StartsWith("MSCF"u8) ||
               bytes.StartsWith("BZh"u8) ||
               bytes.StartsWith(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }) ||
               bytes.StartsWith("MZ"u8) ||
               bytes.StartsWith(new byte[] { 0x7F, 0x45, 0x4C, 0x46 }) ||
               bytes.Length >= 262 && bytes.Slice(257, 5).SequenceEqual("ustar"u8);
    }

    private static string EscapeJsonPointer(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolOrSlotPattern();

    [GeneratedRegex("\\$\\{fixture:[a-z0-9._-]{1,64}\\}|<redacted>|\\[REDACTED\\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllowedPlaceholderPattern();

    [GeneratedRegex("-----BEGIN(?: [A-Z0-9]+)? PRIVATE KEY-----", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex("(?:\\b(?:sk|nvapi)-[A-Za-z0-9_-]{12,}|\\bghp_[A-Za-z0-9]{12,}|\\bgithub_pat_[A-Za-z0-9_]{12,}|\\bxox[a-z]-[A-Za-z0-9-]{12,}|\\bAKIA[0-9A-Z]{16}\\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KnownTokenPattern();

    [GeneratedRegex("\\b[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    [GeneratedRegex("\\bauthorization\\s*[:=]\\s*(?:bearer|basic)\\s+[A-Za-z0-9+/_.=-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex("\\b(?:password|passwd|pwd|api[_ -]?key|client[_ -]?secret|access[_ -]?token|refresh[_ -]?token)\\s*[:=]\\s*[\"']?[^\\s\"',;]{4,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignmentPattern();

    [GeneratedRegex("\\b[a-z][a-z0-9+.-]*://[^/\\s:@]+:[^/@\\s]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriUserInfoPattern();

}
