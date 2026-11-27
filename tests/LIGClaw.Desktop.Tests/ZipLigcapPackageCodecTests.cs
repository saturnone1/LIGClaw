using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LIGClaw.Application.Capabilities;
using LIGClaw.Desktop.Infrastructure.Capabilities;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class ZipLigcapPackageCodecTests
{
    private const string ManifestJson = """
        {
          "version": "1.0.0",
          "schemaVersion": 1,
          "id": "company.prompt.summarize",
          "displayName": "문서 요약",
          "description": "입력 문서를 기존 도구로 안전하게 요약합니다.",
          "kind": "prompt",
          "intentExamples": ["문서를 요약해 줘", "핵심만 정리해 줘"],
          "requiredTools": [],
          "requiredConnections": [],
          "maximumRisk": "R0",
          "entrypoint": "instructions.md"
        }
        """;

    private const string WorkflowManifestJson = """
        {
          "entrypoint": "workflow.json",
          "maximumRisk": "R0",
          "requiredConnections": [],
          "requiredTools": [],
          "intentExamples": ["상태를 정리해 줘"],
          "kind": "workflow",
          "description": "정해진 단계를 폐쇄망에서 실행합니다.",
          "displayName": "상태 정리",
          "id": "company.workflow.status",
          "schemaVersion": 1,
          "version": "1.0.0"
        }
        """;

    private const string WorkflowJson = """
        {
          "steps": [
            { "value": { "status": "ready" }, "type": "output", "id": "done" }
          ],
          "entryStepId": "done",
          "schemaVersion": 1
        }
        """;

    private const string InputSchemaJson = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "text": { "type": "string", "minLength": 1, "maxLength": 4096 }
          },
          "required": ["text"],
          "additionalProperties": false
        }
        """;

    private const string SettingsSchemaJson = """
        {
          "type": "object",
          "properties": {
            "tone": { "type": "string", "maxLength": 20, "enum": ["brief", "detailed"] }
          },
          "additionalProperties": false
        }
        """;

    private const string TestsJson = """
        [
          {
            "name": "reject empty text",
            "input": { "text": "" },
            "expected": "rejected"
          },
          {
            "expected": "accepted",
            "input": { "text": "brief sample" },
            "name": "accept bounded text"
          }
        ]
        """;

    private const string CanonicalTestsJson =
        "[{\"expected\":\"rejected\",\"input\":{\"text\":\"\"},\"name\":\"reject empty text\"}," +
        "{\"expected\":\"accepted\",\"input\":{\"text\":\"brief sample\"},\"name\":\"accept bounded text\"}]";

    [Fact]
    public async Task PromptPackageRoundTripsWithCanonicalHashesAndDeterministicZipBytes()
    {
        var input = CreateArchive(
            Entry(LigcapPackageSecurity.InputSchemaEntry, InputSchemaJson),
            Entry(LigcapPackageSecurity.InstructionsEntry, "첫 문단을 읽습니다.\r\n핵심만 답합니다.\r\n"),
            Entry(LigcapPackageSecurity.SettingsSchemaEntry, SettingsSchemaJson),
            Entry(LigcapPackageSecurity.ManifestEntry, ManifestJson));
        var codec = new ZipLigcapPackageCodec();

        var read = await codec.ReadAsync(new NonSeekableReadStream(input), CancellationToken.None);

        Assert.True(read.Succeeded, Issue(read));
        Assert.NotNull(read.ContentHash);
        Assert.Equal(CapabilityContentHasher.Compute(read.Document!.Payload), read.ContentHash);
        Assert.Equal(
            $"sha256:{Convert.ToHexStringLower(SHA256.HashData(input))}",
            read.SourcePackageHash!.Value);
        Assert.DoesNotContain('\r', read.Document.Payload.Instructions);
        Assert.StartsWith("{\"description\"", read.Document.Payload.CanonicalManifestJson, StringComparison.Ordinal);
        Assert.Equal("[]", read.Document.Payload.CanonicalTestsJson);

        using var first = new MemoryStream();
        using var second = new MemoryStream();
        await codec.WriteAsync(first, read.Document, CancellationToken.None);
        await codec.WriteAsync(second, read.Document, CancellationToken.None);

        Assert.Equal(first.ToArray(), second.ToArray());
        using (var archive = new ZipArchive(new MemoryStream(first.ToArray()), ZipArchiveMode.Read))
        {
            Assert.Equal(
                ["manifest.json", "instructions.md", "input.schema.json", "settings.schema.json"],
                archive.Entries.Select(entry => entry.FullName));
            Assert.All(archive.Entries, entry => Assert.Equal(1980, entry.LastWriteTime.Year));
        }
        var reread = await codec.ReadAsync(new MemoryStream(first.ToArray()), CancellationToken.None);
        Assert.True(reread.Succeeded, Issue(reread));
        Assert.Equal(read.ContentHash, reread.ContentHash);
        Assert.Equal(read.Document.Payload.CanonicalManifestJson, reread.Document!.Payload.CanonicalManifestJson);
        Assert.Equal(read.Document.Payload.Instructions, reread.Document.Payload.Instructions);
        Assert.Equal(read.Document.Payload.CanonicalInputSchemaJson, reread.Document.Payload.CanonicalInputSchemaJson);
        Assert.Equal(read.Document.Payload.CanonicalSettingsSchemaJson, reread.Document.Payload.CanonicalSettingsSchemaJson);
        Assert.Equal(read.Document.Manifest.IntentExamples, reread.Document.Manifest.IntentExamples);
    }

    [Fact]
    public async Task NonEmptyFixtureTestsRoundTripCanonicallyWithStableBytesAndContentHash()
    {
        var entries = ValidEntries(includeSettings: true);
        entries.Add(Entry(LigcapPackageSecurity.TestsEntry, TestsJson));
        var codec = new ZipLigcapPackageCodec();

        var read = await codec.ReadAsync(
            new NonSeekableReadStream(CreateArchive(entries.AsEnumerable().Reverse().ToArray())),
            CancellationToken.None);

        Assert.True(read.Succeeded, Issue(read));
        Assert.Equal(CanonicalTestsJson, read.Document!.Payload.CanonicalTestsJson);
        Assert.Equal(CapabilityContentHasher.Compute(read.Document.Payload), read.ContentHash);

        var withoutTests = await ReadAsync(CreateArchive(ValidEntries(includeSettings: true).ToArray()));
        Assert.True(withoutTests.Succeeded, Issue(withoutTests));
        Assert.NotEqual(withoutTests.ContentHash, read.ContentHash);

        using var first = new MemoryStream();
        using var second = new MemoryStream();
        await codec.WriteAsync(first, read.Document, CancellationToken.None);
        await codec.WriteAsync(second, read.Document, CancellationToken.None);

        Assert.Equal(first.ToArray(), second.ToArray());
        using (var archive = new ZipArchive(new MemoryStream(first.ToArray()), ZipArchiveMode.Read))
        {
            Assert.Equal(
                ["manifest.json", "instructions.md", "input.schema.json", "settings.schema.json", "tests.json"],
                archive.Entries.Select(entry => entry.FullName));
            using var reader = new StreamReader(
                archive.GetEntry(LigcapPackageSecurity.TestsEntry)!.Open(),
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false);
            Assert.Equal(CanonicalTestsJson, await reader.ReadToEndAsync(CancellationToken.None));
        }

        var reread = await codec.ReadAsync(new MemoryStream(first.ToArray()), CancellationToken.None);
        Assert.True(reread.Succeeded, Issue(reread));
        Assert.Equal(read.ContentHash, reread.ContentHash);
        Assert.Equal(CanonicalTestsJson, reread.Document!.Payload.CanonicalTestsJson);

        using var third = new MemoryStream();
        await codec.WriteAsync(third, reread.Document, CancellationToken.None);
        Assert.Equal(first.ToArray(), third.ToArray());
    }

    [Fact]
    public async Task WorkflowPackageRoundTripsCanonicalWorkflowAndOptionalSettingsDeterministically()
    {
        var input = CreateArchive(ValidWorkflowEntries(includeSettings: true).AsEnumerable().Reverse().ToArray());
        var codec = new ZipLigcapPackageCodec();

        var read = await codec.ReadAsync(new NonSeekableReadStream(input), CancellationToken.None);

        Assert.True(read.Succeeded, Issue(read));
        Assert.Equal(CapabilityKind.Workflow, read.Document!.Manifest.Kind);
        Assert.Equal(LigcapPackageSecurity.WorkflowEntry, read.Document.Manifest.Entrypoint);
        Assert.Equal(
            "{\"entryStepId\":\"done\",\"schemaVersion\":1,\"steps\":[{\"id\":\"done\",\"type\":\"output\",\"value\":{\"status\":\"ready\"}}]}",
            read.Document.Payload.CanonicalWorkflowJson);
        Assert.NotNull(read.Document.Payload.CanonicalSettingsSchemaJson);
        Assert.Equal(CapabilityContentHasher.Compute(read.Document.Payload), read.ContentHash);

        using var first = new MemoryStream();
        using var second = new MemoryStream();
        await codec.WriteAsync(first, read.Document, CancellationToken.None);
        await codec.WriteAsync(second, read.Document, CancellationToken.None);

        Assert.Equal(first.ToArray(), second.ToArray());
        using (var archive = new ZipArchive(new MemoryStream(first.ToArray()), ZipArchiveMode.Read))
        {
            Assert.Equal(
                ["manifest.json", "instructions.md", "input.schema.json", "settings.schema.json", "workflow.json"],
                archive.Entries.Select(entry => entry.FullName));
        }
        var reread = await codec.ReadAsync(new MemoryStream(first.ToArray()), CancellationToken.None);
        Assert.True(reread.Succeeded, Issue(reread));
        Assert.Equal(read.ContentHash, reread.ContentHash);
        Assert.Equal(read.Document.Payload.CanonicalWorkflowJson, reread.Document!.Payload.CanonicalWorkflowJson);
        Assert.Equal(read.Document.Payload.CanonicalSettingsSchemaJson, reread.Document.Payload.CanonicalSettingsSchemaJson);
    }

    [Fact]
    public async Task EquivalentEntryOrderAndZipMetadataProduceTheSameContentHash()
    {
        var first = CreateArchive(ValidEntries(includeSettings: false).ToArray());
        var reversed = ValidEntries(includeSettings: false).AsEnumerable().Reverse().ToArray();
        reversed[0] = reversed[0] with { Timestamp = new DateTimeOffset(2026, 8, 2, 1, 2, 0, TimeSpan.Zero) };
        var second = CreateArchive(reversed);
        var codec = new ZipLigcapPackageCodec();

        var firstResult = await codec.ReadAsync(new MemoryStream(first), CancellationToken.None);
        var secondResult = await codec.ReadAsync(new MemoryStream(second), CancellationToken.None);

        Assert.True(firstResult.Succeeded, Issue(firstResult));
        Assert.True(secondResult.Succeeded, Issue(secondResult));
        Assert.Equal(firstResult.ContentHash, secondResult.ContentHash);
        Assert.NotEqual(firstResult.SourcePackageHash, secondResult.SourcePackageHash);
    }

    [Fact]
    public async Task RejectsPhysicallyTruncatedArchiveWithAFixedError()
    {
        var complete = CreateArchive(ValidEntries(includeSettings: false).ToArray());
        var truncated = complete[..^Math.Min(16, complete.Length / 4)];

        var result = await ReadAsync(truncated);

        Assert.Equal("package.archive.invalid", Assert.Single(result.Issues).Code);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task RejectsUnsupportedManifestSchemaVersion()
    {
        var manifest = ManifestJson.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        var result = await ReadAsync(CreateArchive(ValidEntries(false, manifest).ToArray()));

        Assert.Equal("package.manifest.schema_version_unsupported", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task PackageIdentityUsesTheSameCapabilityIdAndSemVerLanguageAsDomain()
    {
        var manifest = ManifestJson
            .Replace("company.prompt.summarize", "a.0", StringComparison.Ordinal)
            .Replace("\"1.0.0\"", "\"1.0.0-rc.1+offline.7\"", StringComparison.Ordinal);

        var result = await ReadAsync(CreateArchive(ValidEntries(false, manifest).ToArray()));

        Assert.True(result.Succeeded, Issue(result));
        Assert.Equal("a.0", result.Document!.Manifest.Id.Value);
        Assert.Equal("1.0.0-rc.1+offline.7", result.Document.Manifest.Version.Value);
    }

    [Theory]
    [InlineData("../instructions.md", "package.entry.path_invalid")]
    [InlineData("folder\\instructions.md", "package.entry.path_invalid")]
    [InlineData("/instructions.md", "package.entry.path_invalid")]
    [InlineData("C:/instructions.md", "package.entry.path_invalid")]
    [InlineData("nested/tests.json", "package.entry.unsupported")]
    [InlineData("metadata.json", "package.entry.unsupported")]
    public async Task RejectsTraversalRootBackslashAndUnsupportedEntries(string name, string expectedCode)
    {
        var entries = ValidEntries(includeSettings: false);
        entries.Add(Entry(name, "{}"));

        var result = await ReadAsync(CreateArchive(entries.ToArray()));

        Assert.Equal(expectedCode, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task RejectsExactDuplicatesAndCaseCollisionsBeforeParsingContent()
    {
        var duplicate = ValidEntries(includeSettings: false);
        duplicate.Add(Entry(LigcapPackageSecurity.ManifestEntry, ManifestJson));
        var caseCollision = ValidEntries(includeSettings: false);
        caseCollision.Add(Entry("Manifest.json", ManifestJson));

        var duplicateResult = await ReadAsync(CreateArchive(duplicate.ToArray()));
        var collisionResult = await ReadAsync(CreateArchive(caseCollision.ToArray()));

        Assert.Equal("package.entry.duplicate", Assert.Single(duplicateResult.Issues).Code);
        Assert.Equal("package.entry.case_collision", Assert.Single(collisionResult.Issues).Code);
    }

    [Fact]
    public async Task RejectsDuplicateAndCaseCollidingTestsEntriesBeforeFixtureParsing()
    {
        var duplicate = ValidEntries(includeSettings: false);
        duplicate.Add(Entry(LigcapPackageSecurity.TestsEntry, "[]"));
        duplicate.Add(Entry(LigcapPackageSecurity.TestsEntry, "[]"));
        var caseCollision = ValidEntries(includeSettings: false);
        caseCollision.Add(Entry(LigcapPackageSecurity.TestsEntry, "[]"));
        caseCollision.Add(Entry("Tests.json", "[]"));

        var duplicateResult = await ReadAsync(CreateArchive(duplicate.ToArray()));
        var collisionResult = await ReadAsync(CreateArchive(caseCollision.ToArray()));

        Assert.Equal("package.entry.duplicate", Assert.Single(duplicateResult.Issues).Code);
        Assert.Equal("package.entry.case_collision", Assert.Single(collisionResult.Issues).Code);
    }

    [Theory]
    [InlineData(unchecked((int)(0xA1A4u << 16)), "package.entry.symlink")]
    [InlineData(unchecked((int)(0x11A4u << 16)), "package.entry.non_regular")]
    [InlineData((int)FileAttributes.ReparsePoint, "package.entry.reparse_point")]
    [InlineData((int)FileAttributes.Directory, "package.entry.non_regular")]
    public async Task RejectsSymlinkReparseDirectoryAndNonRegularEntries(int attributes, string expectedCode)
    {
        var entries = ValidEntries(includeSettings: true);
        entries[^1] = entries[^1] with { ExternalAttributes = attributes };

        var result = await ReadAsync(CreateArchive(entries.ToArray()));

        Assert.Equal(expectedCode, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task RejectsNestedArchiveMagicWithoutWritingAnythingToDisk()
    {
        var entries = ValidEntries(includeSettings: false);
        entries[1] = new EntrySpec(
            LigcapPackageSecurity.InstructionsEntry,
            [0x50, 0x4B, 0x03, 0x04, 0x00],
            RegularFileAttributes,
            CanonicalTimestamp);
        var executableWorkflow = ValidWorkflowEntries(includeSettings: false);
        executableWorkflow[3] = new EntrySpec(
            LigcapPackageSecurity.WorkflowEntry,
            [0x4D, 0x5A, 0x00, 0x00],
            RegularFileAttributes,
            CanonicalTimestamp);

        var result = await ReadAsync(CreateArchive(entries.ToArray()));
        var workflowResult = await ReadAsync(CreateArchive(executableWorkflow.ToArray()));

        Assert.Equal("package.entry.nested_archive_or_executable", Assert.Single(result.Issues).Code);
        Assert.Equal("package.entry.nested_archive_or_executable", Assert.Single(workflowResult.Issues).Code);
        Assert.Equal(LigcapPackageSecurity.WorkflowEntry, workflowResult.Issues[0].Path);
    }

    [Fact]
    public async Task EnforcesRawArchiveEntryCountAndPerFileLimits()
    {
        var oversizedArchive = new byte[LigcapPackageSecurity.MaximumArchiveBytes + 1];
        var maximumEntries = ValidWorkflowEntries(includeSettings: true);
        maximumEntries.Add(Entry(LigcapPackageSecurity.TestsEntry, "[]"));
        var tooManyEntries = maximumEntries.ToList();
        tooManyEntries.Add(Entry("extra.json", "{}"));
        var oversizedInstructions = ValidEntries(includeSettings: false);
        oversizedInstructions[1] = new EntrySpec(
            LigcapPackageSecurity.InstructionsEntry,
            Enumerable.Repeat((byte)'x', 128 * 1024 + 1).ToArray(),
            RegularFileAttributes,
            CanonicalTimestamp);
        var oversizedWorkflow = ValidWorkflowEntries(includeSettings: false);
        oversizedWorkflow[3] = new EntrySpec(
            LigcapPackageSecurity.WorkflowEntry,
            Enumerable.Repeat((byte)'x', 256 * 1024 + 1).ToArray(),
            RegularFileAttributes,
            CanonicalTimestamp);
        var oversizedTests = ValidEntries(includeSettings: false);
        oversizedTests.Add(new EntrySpec(
            LigcapPackageSecurity.TestsEntry,
            Enumerable.Repeat((byte)'x', 192 * 1024 + 1).ToArray(),
            RegularFileAttributes,
            CanonicalTimestamp));

        Assert.Equal("package.archive.too_large", Assert.Single((await ReadAsync(oversizedArchive)).Issues).Code);
        Assert.True((await ReadAsync(CreateArchive(maximumEntries.ToArray()))).Succeeded);
        Assert.Equal("package.entry_count.invalid", Assert.Single((await ReadAsync(CreateArchive(tooManyEntries.ToArray()))).Issues).Code);
        Assert.Equal("package.entry.too_large", Assert.Single((await ReadAsync(CreateArchive(oversizedInstructions.ToArray()))).Issues).Code);
        Assert.Equal("package.entry.too_large", Assert.Single((await ReadAsync(CreateArchive(oversizedWorkflow.ToArray()))).Issues).Code);
        Assert.Equal("package.entry.too_large", Assert.Single((await ReadAsync(CreateArchive(oversizedTests.ToArray()))).Issues).Code);
    }

    [Theory]
    [InlineData("{}", "capability.tests.root_invalid")]
    [InlineData("[{\"expected\":\"accepted\",\"input\":{},\"Name\":\"wrong case\"}]", "capability.tests.case_invalid")]
    [InlineData("[{\"expected\":\"accepted\",\"input\":{},\"name\":\"one\",\"name\":\"two\"}]", "package.json.duplicate_property")]
    [InlineData("[{\"expected\":\"accepted\",\"input\":{},\"name\":\"one\",\"Name\":\"two\"}]", "package.json.case_collision")]
    public async Task RejectsInvalidFixtureRootCaseAndDuplicateProperties(string tests, string expectedCode)
    {
        var entries = ValidEntries(includeSettings: false);
        entries.Add(Entry(LigcapPackageSecurity.TestsEntry, tests));

        var result = await ReadAsync(CreateArchive(entries.ToArray()));

        var issue = Assert.Single(result.Issues);
        Assert.Equal(expectedCode, issue.Code);
        Assert.Equal(LigcapPackageSecurity.TestsEntry, issue.Path);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task RejectsSeventeenFixtureCases()
    {
        var tests = "[" + string.Join(
            ',',
            Enumerable.Range(1, CapabilityFixtureTestPolicy.MaximumCases + 1)
                .Select(index =>
                    $"{{\"expected\":\"accepted\",\"input\":{{}},\"name\":\"case {index}\"}}")) + "]";
        var entries = ValidEntries(includeSettings: false);
        entries.Add(Entry(LigcapPackageSecurity.TestsEntry, tests));

        var result = await ReadAsync(CreateArchive(entries.ToArray()));

        Assert.Equal("capability.tests.root_invalid", Assert.Single(result.Issues).Code);
        Assert.Equal(LigcapPackageSecurity.TestsEntry, result.Issues[0].Path);
    }

    [Theory]
    [InlineData(
        "[{\"expected\":\"accepted\",\"input\":{\"password\":\"correct-horse-battery-staple\"},\"name\":\"secret\"}]",
        "package.secret_literal.detected")]
    [InlineData(
        "[{\"expected\":\"accepted\",\"input\":{\"endpoint\":\"https://mail.internal.invalid/v1\"},\"name\":\"connection\"}]",
        "package.connection_literal.detected")]
    public async Task RejectsSecretAndConnectionLiteralsFromFixtureInputs(string tests, string expectedCode)
    {
        var entries = ValidEntries(includeSettings: false);
        entries.Add(Entry(LigcapPackageSecurity.TestsEntry, tests));

        var result = await ReadAsync(CreateArchive(entries.ToArray()));

        var issue = Assert.Single(result.Issues);
        Assert.Equal(expectedCode, issue.Code);
        Assert.Null(result.Document);
        Assert.DoesNotContain("correct-horse", issue.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("mail.internal", issue.Path ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllowsAnOpaqueFixturePlaceholderWithoutMaterializingASecret()
    {
        const string tests =
            "[{\"expected\":\"accepted\",\"input\":{\"password\":\"${fixture:mail-secret}\"},\"name\":\"credential placeholder\"}]";
        var entries = ValidEntries(includeSettings: false);
        entries.Add(Entry(LigcapPackageSecurity.TestsEntry, tests));

        var result = await ReadAsync(CreateArchive(entries.ToArray()));

        Assert.True(result.Succeeded, Issue(result));
        Assert.Contains("${fixture:mail-secret}", result.Document!.Payload.CanonicalTestsJson, StringComparison.Ordinal);
        Assert.Equal(CapabilityIngressViolation.None, CapabilityIngressPolicy.GetViolation(result.Document.Payload));
    }

    [Theory]
    [InlineData("duplicate", "package.json.duplicate_property")]
    [InlineData("case", "package.json.case_collision")]
    [InlineData("trailing", "package.json.invalid")]
    [InlineData("tokens", "package.json.tokens_exceeded")]
    public async Task RejectsAmbiguousOrUnboundedJson(string mutation, string expectedCode)
    {
        var manifest = mutation switch
        {
            "duplicate" => InsertFirstProperty(ManifestJson, "\"schemaVersion\":1"),
            "case" => InsertFirstProperty(ManifestJson, "\"SchemaVersion\":1"),
            "trailing" => ManifestJson[..ManifestJson.LastIndexOf('}')] + ",}",
            _ => ManifestJson,
        };
        var inputSchema = mutation == "tokens"
            ? "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false,\"const\":[" +
              string.Join(',', Enumerable.Repeat("0", 4_100)) + "]}"
            : InputSchemaJson;
        var entries = ValidEntries(includeSettings: false, manifest, inputSchema);

        var result = await ReadAsync(CreateArchive(entries.ToArray()));

        Assert.Equal(expectedCode, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task RejectsInvalidUtf8BomAndControlCharacters()
    {
        var invalidUtf8 = ValidEntries(includeSettings: false);
        invalidUtf8[1] = new EntrySpec(LigcapPackageSecurity.InstructionsEntry, [0xC3, 0x28], RegularFileAttributes, CanonicalTimestamp);
        var bom = ValidEntries(includeSettings: false);
        bom[1] = new EntrySpec(
            LigcapPackageSecurity.InstructionsEntry,
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("지침")],
            RegularFileAttributes,
            CanonicalTimestamp);
        var control = ValidEntries(includeSettings: false);
        control[1] = new EntrySpec(LigcapPackageSecurity.InstructionsEntry, [0x61, 0x00, 0x62], RegularFileAttributes, CanonicalTimestamp);

        Assert.Equal("package.text.invalid_utf8", Assert.Single((await ReadAsync(CreateArchive(invalidUtf8.ToArray()))).Issues).Code);
        Assert.Equal("package.text.bom_not_allowed", Assert.Single((await ReadAsync(CreateArchive(bom.ToArray()))).Issues).Code);
        Assert.Equal("package.text.control_character", Assert.Single((await ReadAsync(CreateArchive(control.ToArray()))).Issues).Code);
    }

    [Fact]
    public async Task RejectsBidiOrZeroWidthFormatCharactersAndMultilineManifestLabels()
    {
        var bidi = ValidEntries(includeSettings: false);
        bidi[1] = Entry(LigcapPackageSecurity.InstructionsEntry, "정상\u202Etxt.exe");
        var multilineManifest = ManifestJson.Replace(
            "\"displayName\": \"문서 요약\"",
            "\"displayName\": \"문서\\n요약\"",
            StringComparison.Ordinal);

        var bidiResult = await ReadAsync(CreateArchive(bidi.ToArray()));
        var multilineResult = await ReadAsync(CreateArchive(
            ValidEntries(false, multilineManifest).ToArray()));

        Assert.Equal("package.text.format_character", Assert.Single(bidiResult.Issues).Code);
        Assert.Equal("package.manifest.field_value_invalid", Assert.Single(multilineResult.Issues).Code);
    }

    [Fact]
    public async Task RejectsSupplementaryFormatRuneAndUnicodeLineSeparator()
    {
        var supplementaryFormat = ValidEntries(includeSettings: false);
        supplementaryFormat[1] = Entry(
            LigcapPackageSecurity.InstructionsEntry,
            "정상\U000E0001숨김");
        var lineSeparatorManifest = ManifestJson.Replace(
            "\"displayName\": \"문서 요약\"",
            "\"displayName\": \"문서\\u2028요약\"",
            StringComparison.Ordinal);
        var tabManifest = ManifestJson.Replace(
            "\"displayName\": \"문서 요약\"",
            "\"displayName\": \"문서\\t요약\"",
            StringComparison.Ordinal);

        var formatResult = await ReadAsync(CreateArchive(supplementaryFormat.ToArray()));
        var lineResult = await ReadAsync(CreateArchive(
            ValidEntries(false, lineSeparatorManifest).ToArray()));
        var tabResult = await ReadAsync(CreateArchive(
            ValidEntries(false, tabManifest).ToArray()));

        Assert.Equal("package.text.format_character", Assert.Single(formatResult.Issues).Code);
        Assert.Equal("package.manifest.field_value_invalid", Assert.Single(lineResult.Issues).Code);
        Assert.Equal("package.manifest.field_value_invalid", Assert.Single(tabResult.Issues).Code);
    }

    [Theory]
    [InlineData("{\"type\":\"object\",\"properties\":{}}", "package.schema.object_unbounded")]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false,\"$ref\":\"https://example.invalid/schema\"}", "package.schema.keyword_unsupported")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"additionalProperties\":false}", "package.schema.string_unbounded")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"array\",\"items\":{\"type\":\"boolean\"}}},\"additionalProperties\":false}", "package.schema.array_unbounded")]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"required\":[\"missing\"],\"additionalProperties\":false}", "package.schema.required_invalid")]
    public async Task RejectsUnsupportedOrUnboundedJsonSchema(string schema, string expectedCode)
    {
        var result = await ReadAsync(CreateArchive(ValidEntries(includeSettings: false, inputSchema: schema).ToArray()));

        Assert.Equal(expectedCode, Assert.Single(result.Issues).Code);
    }

    [Theory]
    [InlineData("Authorization: Bearer abcdefghijklmnop")]
    [InlineData("password=correct-horse-battery-staple")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("nvapi-abcdefghijklmnop")]
    [InlineData("https://admin:hunter2@example.invalid/")]
    public async Task RejectsSecretsWithoutEchoingThemInIssues(string secret)
    {
        var entries = ValidEntries(includeSettings: false);
        entries[1] = Entry(LigcapPackageSecurity.InstructionsEntry, "지침\n" + secret);

        var result = await ReadAsync(CreateArchive(entries.ToArray()));

        var issue = Assert.Single(result.Issues);
        Assert.Equal("package.secret_literal.detected", issue.Code);
        Assert.DoesNotContain(secret, issue.Code, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, issue.Path ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("instructions")]
    [InlineData("input-schema")]
    [InlineData("split-endpoint")]
    [InlineData("component-schema")]
    public async Task RejectsConnectionLiteralFromProjectedPackageSurfaces(string surface)
    {
        var entries = ValidEntries(includeSettings: false);
        switch (surface)
        {
            case "manifest":
                entries[0] = Entry(
                    LigcapPackageSecurity.ManifestEntry,
                    ManifestJson.Replace(
                        "입력 문서를 기존 도구로 안전하게 요약합니다.",
                        "Uses https://manifest.internal.invalid/v1.",
                        StringComparison.Ordinal));
                break;
            case "instructions":
                entries[1] = Entry(
                    LigcapPackageSecurity.InstructionsEntry,
                    "profileId=desktop-mail-prod");
                break;
            case "input-schema":
                entries[2] = Entry(
                    LigcapPackageSecurity.InputSchemaEntry,
                    "{\"additionalProperties\":false,\"properties\":{\"auth\":{\"const\":\"bearer\",\"maxLength\":16,\"minLength\":1,\"type\":\"string\"}},\"type\":\"object\"}");
                break;
            case "split-endpoint":
                entries[1] = Entry(
                    LigcapPackageSecurity.InstructionsEntry,
                    "Use host mail.internal.invalid on port 8443 over HTTPS; credential target LIGClaw/Connections/boundary-mail-profile/BearerToken.");
                break;
            case "component-schema":
                entries[2] = Entry(
                    LigcapPackageSecurity.InputSchemaEntry,
                    "{\"additionalProperties\":false,\"properties\":{\"host\":{\"const\":\"mail.internal.invalid\",\"maxLength\":253,\"minLength\":1,\"type\":\"string\"},\"port\":{\"const\":8443,\"maximum\":65535,\"minimum\":1,\"type\":\"integer\"},\"scheme\":{\"const\":\"https\",\"maxLength\":5,\"minLength\":4,\"type\":\"string\"}},\"type\":\"object\"}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(surface));
        }

        var result = await ReadAsync(CreateArchive(entries.ToArray()));

        var issue = Assert.Single(result.Issues);
        Assert.Equal("package.connection_literal.detected", issue.Code);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task AllowsLogicalConnectionSlotKindAndRelativeTemplateMetadata()
    {
        const string manifest = """
            {
              "entrypoint": "workflow.json",
              "maximumRisk": "R3",
              "requiredConnections": [{ "slot": "service", "kind": "http" }],
              "requiredTools": ["connection.http.request.v1"],
              "intentExamples": ["Check internal service health"],
              "kind": "workflow",
              "description": "Runs one bounded registered HTTP workflow.",
              "displayName": "Internal HTTP status",
              "id": "company.workflow.http-status",
              "schemaVersion": 1,
              "version": "1.0.0"
            }
            """;
        const string workflow = """
            {
              "steps": [
                {
                  "arguments": {
                    "connectionSlot": "service",
                    "method": "GET",
                    "pathTemplate": "users/{userId}/status",
                    "pathParameters": [{ "name": "userId", "value": "sample-user" }],
                    "queryParameters": [{ "name": "view", "value": "summary" }],
                    "reason": "Check internal service health"
                  },
                  "next": "done",
                  "tool": "connection.http.request.v1",
                  "type": "tool.call",
                  "id": "request"
                },
                { "value": { "status": "ready" }, "type": "output", "id": "done" }
              ],
              "entryStepId": "request",
              "schemaVersion": 1
            }
            """;

        var result = await ReadAsync(CreateArchive(
            ValidWorkflowEntries(false, manifest, workflow).ToArray()));

        Assert.True(result.Succeeded, Issue(result));
        Assert.Equal(
            CapabilityIngressViolation.None,
            CapabilityIngressPolicy.GetViolation(result.Document!.Payload));
    }

    [Fact]
    public async Task AllowsFixturePlaceholderButRejectsSecretInsideSchemaConst()
    {
        const string placeholderSchema = """
            {"type":"object","properties":{"note":{"type":"string","maxLength":100,"const":"password=${fixture:mail-secret}"}},"additionalProperties":false}
            """;
        const string secretSchema = """
            {"type":"object","properties":{"note":{"type":"string","maxLength":100,"const":"sk-abcdefghijklmnop"}},"additionalProperties":false}
            """;

        var placeholder = await ReadAsync(CreateArchive(ValidEntries(false, inputSchema: placeholderSchema).ToArray()));
        var secret = await ReadAsync(CreateArchive(ValidEntries(false, inputSchema: secretSchema).ToArray()));

        Assert.True(placeholder.Succeeded, Issue(placeholder));
        Assert.Equal("package.secret_literal.detected", Assert.Single(secret.Issues).Code);
    }

    [Theory]
    [InlineData("apiKey", "const", "correct-horse-battery-staple")]
    [InlineData("client_secret", "const", "ordinary-looking-secret")]
    [InlineData("accessToken", "enum", "token-without-a-known-prefix")]
    public async Task RejectsOrdinaryLiteralsBoundToSensitiveSchemaFields(
        string fieldName,
        string keyword,
        string literal)
    {
        var value = keyword == "enum" ? $"[\"{literal}\"]" : $"\"{literal}\"";
        var schema =
            $"{{\"type\":\"object\",\"properties\":{{\"{fieldName}\":{{\"type\":\"string\",\"maxLength\":128,\"{keyword}\":{value}}}}},\"additionalProperties\":false}}";

        var result = await ReadAsync(CreateArchive(ValidEntries(false, inputSchema: schema).ToArray()));

        Assert.Equal("package.secret_literal.detected", Assert.Single(result.Issues).Code);
        Assert.DoesNotContain(literal, result.Issues[0].Path ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"type\":\"object\",\"properties\":{\"token\":{\"type\":\"array\",\"minItems\":0,\"maxItems\":2,\"items\":{\"type\":\"string\",\"maxLength\":64,\"enum\":[\"ordinary-looking-secret\"]}}},\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"apiKey\":{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\",\"maxLength\":64,\"const\":\"ordinary-looking-secret\"}},\"additionalProperties\":false}},\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"authorization\":{\"type\":\"string\",\"maxLength\":64,\"const\":\"ordinary-looking-secret\"}},\"additionalProperties\":false}")]
    public async Task RejectsNestedOrAuthorizationSensitiveSchemaLiterals(string schema)
    {
        var result = await ReadAsync(CreateArchive(ValidEntries(false, inputSchema: schema).ToArray()));

        Assert.Equal("package.secret_literal.detected", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task AllowsSensitiveSchemaFieldWithoutEmbeddedValueOrWithFixturePlaceholder()
    {
        const string shapeOnly =
            "{\"type\":\"object\",\"properties\":{\"apiKey\":{\"type\":\"string\",\"maxLength\":128}},\"additionalProperties\":false}";
        const string placeholder =
            "{\"type\":\"object\",\"properties\":{\"clientSecret\":{\"type\":\"string\",\"maxLength\":128,\"const\":\"${fixture:mail-secret}\"}},\"additionalProperties\":false}";

        var shapeResult = await ReadAsync(CreateArchive(ValidEntries(false, inputSchema: shapeOnly).ToArray()));
        var placeholderResult = await ReadAsync(CreateArchive(ValidEntries(false, inputSchema: placeholder).ToArray()));

        Assert.True(shapeResult.Succeeded, Issue(shapeResult));
        Assert.True(placeholderResult.Succeeded, Issue(placeholderResult));
    }

    [Fact]
    public async Task EnforcesKindSpecificEntrypointsAndRequiredEntrySets()
    {
        var workflowWithoutEntrypoint = ValidEntries(false, WorkflowManifestJson);
        var promptWithWorkflow = ValidEntries(false);
        promptWithWorkflow.Add(Entry(LigcapPackageSecurity.WorkflowEntry, WorkflowJson));
        var workflowWithPromptEntrypoint = WorkflowManifestJson.Replace(
            "\"entrypoint\": \"workflow.json\"",
            "\"entrypoint\": \"instructions.md\"",
            StringComparison.Ordinal);

        var missingResult = await ReadAsync(CreateArchive(workflowWithoutEntrypoint.ToArray()));
        var extraResult = await ReadAsync(CreateArchive(promptWithWorkflow.ToArray()));
        var mismatchResult = await ReadAsync(CreateArchive(
            ValidWorkflowEntries(false, workflowWithPromptEntrypoint).ToArray()));

        Assert.Equal("package.entry.required_missing", Assert.Single(missingResult.Issues).Code);
        Assert.Equal(LigcapPackageSecurity.WorkflowEntry, missingResult.Issues[0].Path);
        Assert.Equal("package.entry.unsupported", Assert.Single(extraResult.Issues).Code);
        Assert.Equal(LigcapPackageSecurity.WorkflowEntry, extraResult.Issues[0].Path);
        Assert.Equal("package.manifest.entrypoint_invalid", Assert.Single(mismatchResult.Issues).Code);
    }

    [Fact]
    public async Task RejectsInvalidWorkflowJsonRootCaseCollisionAndSecretWithoutSemanticValidation()
    {
        var invalidRoot = ValidWorkflowEntries(false, workflow: "[]");
        var collision = ValidWorkflowEntries(false);
        collision.Add(Entry("Workflow.json", WorkflowJson));
        var secret = ValidWorkflowEntries(
            false,
            workflow: "{\"note\":\"Authorization: Bearer abcdefghijklmnop\"}");

        var rootResult = await ReadAsync(CreateArchive(invalidRoot.ToArray()));
        var collisionResult = await ReadAsync(CreateArchive(collision.ToArray()));
        var secretResult = await ReadAsync(CreateArchive(secret.ToArray()));

        Assert.Equal("package.workflow.root_invalid", Assert.Single(rootResult.Issues).Code);
        Assert.Equal(LigcapPackageSecurity.WorkflowEntry, rootResult.Issues[0].Path);
        Assert.Equal("package.entry.case_collision", Assert.Single(collisionResult.Issues).Code);
        Assert.Equal("package.secret_literal.detected", Assert.Single(secretResult.Issues).Code);
        Assert.Equal(LigcapPackageSecurity.WorkflowEntry, secretResult.Issues[0].Path);
    }

    [Fact]
    public async Task InvalidKindPayloadDoesNotMutateDestinationOnExport()
    {

        var validRead = await ReadAsync(CreateArchive(ValidEntries(false).ToArray()));
        Assert.True(validRead.Succeeded, Issue(validRead));
        var invalidPayload = validRead.Document!.Payload with { CanonicalWorkflowJson = "{}" };
        var invalidDocument = validRead.Document with { Payload = invalidPayload };
        using var destination = new MemoryStream();
        destination.Write("preserve"u8);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ZipLigcapPackageCodec().WriteAsync(destination, invalidDocument, CancellationToken.None));
        Assert.Equal("preserve"u8.ToArray(), destination.ToArray());
    }

    [Theory]
    [InlineData("\"id\": \"company.prompt.summarize\"", "\"id\": \"a.b.c.d.e.f.g.h.i\"", "package.manifest.id_invalid")]
    [InlineData("\"version\": \"1.0.0\"", "\"version\": \"999999999999999999999999999999999999999999999999999999.0.0\"", "package.manifest.version_invalid")]
    public async Task InvalidDomainIdentityReturnsARedactedIssueInsteadOfThrowing(
        string original,
        string replacement,
        string expectedCode)
    {
        var manifest = ManifestJson.Replace(original, replacement, StringComparison.Ordinal);

        var result = await ReadAsync(CreateArchive(ValidEntries(false, manifest).ToArray()));

        Assert.Equal(expectedCode, Assert.Single(result.Issues).Code);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task PreCancelledReadAndWriteDoNotProduceAResultOrOutput()
    {
        var cancellation = new CancellationToken(canceled: true);
        var bytes = CreateArchive(ValidEntries(false).ToArray());
        var codec = new ZipLigcapPackageCodec();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            codec.ReadAsync(new MemoryStream(bytes), cancellation));
        var valid = await codec.ReadAsync(new MemoryStream(bytes), CancellationToken.None);
        using var destination = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            codec.WriteAsync(destination, valid.Document!, cancellation));
        Assert.Empty(destination.ToArray());
    }

    private static async Task<CapabilityPackageReadResult> ReadAsync(byte[] bytes) =>
        await new ZipLigcapPackageCodec().ReadAsync(new MemoryStream(bytes), CancellationToken.None);

    private static List<EntrySpec> ValidEntries(
        bool includeSettings,
        string? manifest = null,
        string? inputSchema = null)
    {
        var entries = new List<EntrySpec>
        {
            Entry(LigcapPackageSecurity.ManifestEntry, manifest ?? ManifestJson),
            Entry(LigcapPackageSecurity.InstructionsEntry, "문서의 핵심만 요약합니다.\n"),
            Entry(LigcapPackageSecurity.InputSchemaEntry, inputSchema ?? InputSchemaJson),
        };
        if (includeSettings) entries.Add(Entry(LigcapPackageSecurity.SettingsSchemaEntry, SettingsSchemaJson));
        return entries;
    }

    private static List<EntrySpec> ValidWorkflowEntries(
        bool includeSettings,
        string? manifest = null,
        string? workflow = null)
    {
        var entries = new List<EntrySpec>
        {
            Entry(LigcapPackageSecurity.ManifestEntry, manifest ?? WorkflowManifestJson),
            Entry(LigcapPackageSecurity.InstructionsEntry, "정해진 단계만 실행합니다.\n"),
            Entry(LigcapPackageSecurity.InputSchemaEntry, InputSchemaJson),
            Entry(LigcapPackageSecurity.WorkflowEntry, workflow ?? WorkflowJson),
        };
        if (includeSettings) entries.Add(Entry(LigcapPackageSecurity.SettingsSchemaEntry, SettingsSchemaJson));
        return entries;
    }

    private static EntrySpec Entry(string name, string content) =>
        new(name, Encoding.UTF8.GetBytes(content), RegularFileAttributes, CanonicalTimestamp);

    private static byte[] CreateArchive(params EntrySpec[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (var specification in entries)
            {
                var entry = archive.CreateEntry(specification.Name, CompressionLevel.NoCompression);
                entry.LastWriteTime = specification.Timestamp;
                entry.ExternalAttributes = specification.ExternalAttributes;
                using var stream = entry.Open();
                stream.Write(specification.Content);
            }
        }
        return output.ToArray();
    }

    private static string Issue(CapabilityPackageReadResult result) =>
        string.Join(", ", result.Issues.Select(issue => $"{issue.Code}:{issue.Path}"));

    private static string InsertFirstProperty(string json, string property)
    {
        var openingBrace = json.IndexOf('{');
        return string.Concat(json.AsSpan(0, openingBrace + 1), property, ",", json.AsSpan(openingBrace + 1));
    }

    private const int RegularFileAttributes = unchecked((int)(0x81A4u << 16));
    private static readonly DateTimeOffset CanonicalTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record EntrySpec(
        string Name,
        byte[] Content,
        int ExternalAttributes,
        DateTimeOffset Timestamp);

    private sealed class NonSeekableReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
