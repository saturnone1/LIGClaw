using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Application.Workflows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class FeatureBuilderProposalScannerTests
{
    [Fact]
    public void MaterializationCanonicalizesArtifactsAndProducesADeterministicDesktopHash()
    {
        var scanner = Scanner(("system.get_status.v1", "R0"));
        var first = scanner.Scan(Artifacts(), null);
        var second = scanner.Scan(Artifacts(
            manifest: """
                {"entrypoint":"instructions.md","maximumRisk":"R0","requiredConnections":[],"requiredTools":["system.get_status.v1"],"intentExamples":["상태 기능을 만들어 줘"],"kind":"prompt","description":"등록 도구로 상태를 설명합니다.","displayName":"상태 설명","version":"1.0.0","id":"company.builder.status","schemaVersion":1}
                """,
            inputSchema: "{ \"properties\" : {}, \"type\" : \"object\" }"), null);

        Assert.True(first.IsValid, first.ErrorCode);
        Assert.True(second.IsValid, second.ErrorCode);
        Assert.Equal(first.CandidateContentHash, second.CandidateContentHash);
        Assert.Equal(first.Candidate!.CanonicalManifestJson, second.Candidate!.CanonicalManifestJson);
        Assert.StartsWith("{\"description\"", first.Candidate.CanonicalManifestJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviseModeRequiresTheExactBaseIdAndAHigherVersion()
    {
        var scanner = Scanner(("system.get_status.v1", "R0"));
        var target = new FeatureBuilderRevisionTarget(
            VersionRef("company.builder.status", "1.0.0", 'a'),
            4);

        Assert.True(scanner.Scan(Artifacts(version: "1.1.0"), target).IsValid);
        Assert.Equal(
            "feature_builder.proposal.revision_target_mismatch",
            scanner.Scan(Artifacts(id: "company.builder.other", version: "1.1.0"), target).ErrorCode);
        Assert.Equal(
            "feature_builder.proposal.revision_target_mismatch",
            scanner.Scan(Artifacts(version: "1.0.0"), target).ErrorCode);
    }

    [Fact]
    public void ScannerRejectsUnknownToolsAndUnderdeclaredRisk()
    {
        Assert.Equal(
            "feature_builder.proposal.tool_unknown",
            Scanner().Scan(Artifacts(), null).ErrorCode);
        Assert.Equal(
            "feature_builder.proposal.risk_underdeclared",
            Scanner(("system.get_status.v1", "R3")).Scan(Artifacts(), null).ErrorCode);
    }

    [Fact]
    public void MailApiProducesAValidConnectedWorkflowProposalWhileUnknownKindsRemainUnsupported()
    {
        var scanner = MailWorkflowScanner();

        var admitted = scanner.Scan(ConnectedMailWorkflowArtifacts(CapabilityConnectionKindPolicy.MailApi), null);
        var rejected = scanner.Scan(ConnectedMailWorkflowArtifacts("sql"), null);

        Assert.True(admitted.IsValid, admitted.ErrorCode);
        Assert.Equal(CapabilityConnectionKindPolicy.MailApi,
            Assert.Single(admitted.Candidate!.Manifest.RequiredConnections).Kind);
        Assert.Equal("feature_builder.proposal.connection_kind_unsupported", rejected.ErrorCode);
    }

    [Theory]
    [InlineData("token=ordinary-secret-value", "feature_builder.proposal.secret_literal_detected")]
    [InlineData("endpoint=https://internal.invalid/api", "feature_builder.proposal.connection_literal_detected")]
    [InlineData("Run powershell.exe with these arguments.", "feature_builder.proposal.shell_or_script_detected")]
    public void ScannerQuarantinesUnsafeGeneratedInstructions(string instructions, string expectedCode)
    {
        var result = Scanner(("system.get_status.v1", "R0")).Scan(
            Artifacts(instructions: instructions),
            null);

        Assert.False(result.IsValid);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Null(result.Candidate);
        Assert.Null(result.CandidateContentHash);
    }

    [Fact]
    public void ScannerAllowsSecretPlaceholdersButNotDesktopControlMetadata()
    {
        var scanner = Scanner(("system.get_status.v1", "R0"));

        Assert.True(scanner.Scan(Artifacts(instructions: "token=${fixture:mail-token}"), null).IsValid);
        Assert.Equal(
            "feature_builder.proposal.artifact_invalid",
            scanner.Scan(Artifacts(inputSchema:
                "{\"type\":\"object\",\"properties\":{\"proposalId\":{\"type\":\"string\"}}}"), null).ErrorCode);
    }

    [Fact]
    public void ScannerValidatesFixtureTestsBeforeCreatingACandidate()
    {
        var result = Scanner(("system.get_status.v1", "R0")).Scan(
            Artifacts(tests: "[{\"name\":\"bad\",\"input\":{},\"expected\":\"maybe\"}]"),
            null);

        Assert.Equal("capability.tests.case_invalid", result.ErrorCode);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void BuilderProposalRequiresAtLeastOneFixtureCase()
    {
        var result = Scanner(("system.get_status.v1", "R0")).Scan(
            Artifacts(tests: "[]"),
            null);

        Assert.Equal("feature_builder.proposal.tests_required", result.ErrorCode);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void ScannerRejectsDuplicateAndWrongMediaArtifacts()
    {
        var scanner = Scanner(("system.get_status.v1", "R0"));
        var duplicate = Artifacts().Concat([
            new FeatureBuilderRawProposalArtifact("tests.json", "application/json", "[]"),
        ]).ToArray();
        var wrongMedia = Artifacts().ToArray();
        wrongMedia[0] = wrongMedia[0] with { MediaType = "text/markdown" };

        Assert.Equal("feature_builder.proposal.artifact_invalid", scanner.Scan(duplicate, null).ErrorCode);
        Assert.Equal("feature_builder.proposal.artifact_invalid", scanner.Scan(wrongMedia, null).ErrorCode);
    }

    private static FeatureBuilderProposalScanner Scanner(params (string Name, string Risk)[] tools) =>
        new(new Dependencies(tools));

    internal static IReadOnlyList<FeatureBuilderRawProposalArtifact> Artifacts(
        string id = "company.builder.status",
        string version = "1.0.0",
        string? manifest = null,
        string instructions = "등록된 도구를 사용해 현재 상태를 설명합니다.",
        string inputSchema = "{\"type\":\"object\",\"properties\":{}}",
        string tests = "[{\"name\":\"accept\",\"input\":{},\"expected\":\"accepted\"}]") =>
    [
        new("manifest.json", "application/json", manifest ?? $$"""
            {
              "schemaVersion": 1,
              "id": "{{id}}",
              "version": "{{version}}",
              "displayName": "상태 설명",
              "description": "등록 도구로 상태를 설명합니다.",
              "kind": "prompt",
              "intentExamples": ["상태 기능을 만들어 줘"],
              "requiredTools": ["system.get_status.v1"],
              "requiredConnections": [],
              "maximumRisk": "R0",
              "entrypoint": "instructions.md"
            }
            """),
        new("instructions.md", "text/markdown", instructions),
        new("input.schema.json", "application/json", inputSchema),
        new("tests.json", "application/json", tests),
    ];

    private static IReadOnlyList<FeatureBuilderRawProposalArtifact> ConnectedMailWorkflowArtifacts(string kind) =>
    [
        new("manifest.json", "application/json", $$"""
            {
              "schemaVersion": 1,
              "id": "company.builder.connected",
              "version": "1.0.0",
              "displayName": "Connected workflow",
              "description": "Runs one connection-bound workflow.",
              "kind": "workflow",
              "intentExamples": ["run the connected workflow"],
              "requiredTools": ["mail.create_draft.v1", "mail.send.v1"],
              "requiredConnections": [{"slot":"mail","kind":"{{kind}}"}],
              "maximumRisk": "R3",
              "entrypoint": "workflow.json"
            }
            """),
        new("instructions.md", "text/markdown", "Run only the declared connected workflow."),
        new("input.schema.json", "application/json",
            "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}"),
        new("workflow.json", "application/json",
            "{\"schemaVersion\":1,\"entryStepId\":\"draft\",\"steps\":[{\"id\":\"draft\",\"type\":\"tool.call\",\"tool\":\"mail.create_draft.v1\",\"arguments\":{\"to\":[\"owner@example.com\"],\"cc\":[],\"bcc\":[],\"subject\":\"Status\",\"body\":\"Body\"},\"next\":\"send\"},{\"id\":\"send\",\"type\":\"tool.call\",\"tool\":\"mail.send.v1\",\"arguments\":{\"connectionSlot\":\"mail\",\"draftHash\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"draft\",\"path\":\"/draftHash\"}},\"to\":[\"owner@example.com\"],\"cc\":[],\"bcc\":[],\"subject\":\"Status\",\"body\":\"Body\"},\"next\":\"done\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{}}]}"),
        new("tests.json", "application/json", "[{\"name\":\"accept\",\"input\":{},\"expected\":\"accepted\"}]"),
    ];

    private static FeatureBuilderProposalScanner MailWorkflowScanner()
    {
        Assert.True(ToolSchemaRegistry.TryGet("mail.create_draft.v1", out var createDraftSchemas));
        Assert.True(ToolSchemaRegistry.TryGet("mail.send.v1", out var sendSchemas));
        return new FeatureBuilderProposalScanner(
            new Dependencies(("mail.create_draft.v1", "R0"), ("mail.send.v1", "R3")),
            new FixedWorkflowCatalog(
                new WorkflowToolMetadata(
                    "mail.create_draft.v1",
                    "R0",
                    createDraftSchemas!.CanonicalInputSchemaJson,
                    createDraftSchemas.CanonicalResultSchemaJson,
                    true,
                    WorkflowToolPreparationMode.None,
                    true,
                    WorkflowRetryClass.ReadOnly,
                    WorkflowToolEffectClass.Observation),
                new WorkflowToolMetadata(
                    "mail.send.v1",
                    "R3",
                    sendSchemas!.CanonicalInputSchemaJson,
                    sendSchemas.CanonicalResultSchemaJson,
                    true,
                    WorkflowToolPreparationMode.OneShot,
                    true,
                    WorkflowRetryClass.Never,
                    WorkflowToolEffectClass.Effect)));
    }

    private static CapabilityVersionRef VersionRef(string id, string version, char hash) =>
        new(
            CapabilityId.Parse(id),
            CapabilitySemanticVersion.Parse(version),
            CapabilityContentHash.Parse("sha256:" + new string(hash, 64)));

    private sealed class Dependencies(params (string Name, string Risk)[] tools) : ICapabilityDependencyCatalog
    {
        private readonly IReadOnlyDictionary<string, string> _tools =
            tools.ToDictionary(tool => tool.Name, tool => tool.Risk, StringComparer.Ordinal);

        public bool TryGetTool(string name, out CapabilityToolDependency? dependency)
        {
            if (_tools.TryGetValue(name, out var risk))
            {
                dependency = new CapabilityToolDependency(name, risk);
                return true;
            }
            dependency = null;
            return false;
        }
    }

    private sealed class FixedWorkflowCatalog(params WorkflowToolMetadata[] tools) : IWorkflowToolMetadataCatalog
    {
        private readonly IReadOnlyDictionary<string, WorkflowToolMetadata> _tools =
            tools.ToDictionary(tool => tool.CanonicalName, StringComparer.Ordinal);

        public bool TryGet(string canonicalName, out WorkflowToolMetadata? metadata)
        {
            var found = _tools.TryGetValue(canonicalName, out var value);
            metadata = value;
            return found;
        }
    }
}
