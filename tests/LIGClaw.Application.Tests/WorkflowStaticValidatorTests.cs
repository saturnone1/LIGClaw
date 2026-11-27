using System.Text.Json;
using LIGClaw.Application.Workflows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class WorkflowStaticValidatorTests
{
    private const string EmptyInputSchema =
        "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}";
    private const string PathInputSchema =
        "{\"additionalProperties\":false,\"properties\":{\"path\":{\"maxLength\":32767,\"minLength\":3,\"type\":\"string\"}},\"required\":[\"path\"],\"type\":\"object\"}";
    private const string WorkflowPathInputSchema =
        "{\"additionalProperties\":false,\"properties\":{\"path\":{\"maxLength\":4096,\"minLength\":3,\"type\":\"string\"}},\"required\":[\"path\"],\"type\":\"object\"}";
    private const string ParityInputSchema =
        "{\"additionalProperties\":false,\"properties\":{\"path\":{\"maxLength\":4096,\"type\":\"string\"}},\"required\":[\"path\"],\"type\":\"object\"}";
    private const string StatusResultSchema =
        "{\"additionalProperties\":false,\"properties\":{\"architecture\":{\"type\":\"string\"},\"buildNumber\":{\"type\":\"integer\"},\"localTime\":{\"type\":\"string\"},\"powerSource\":{\"type\":\"string\"},\"timeZone\":{\"type\":\"string\"},\"windowsRelease\":{\"type\":\"string\"}},\"required\":[\"windowsRelease\",\"buildNumber\",\"architecture\",\"localTime\",\"timeZone\",\"powerSource\"],\"type\":\"object\"}";
    private const string MetadataResultSchema =
        "{\"additionalProperties\":false,\"properties\":{\"isDirectory\":{\"type\":\"boolean\"},\"lastWriteTimeUtc\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"},\"readOnly\":{\"type\":\"boolean\"},\"sizeBytes\":{\"type\":\"integer\"}},\"required\":[\"path\",\"name\",\"isDirectory\",\"sizeBytes\",\"lastWriteTimeUtc\",\"readOnly\"],\"type\":\"object\"}";
    private const string NotificationInputSchema =
        "{\"additionalProperties\":false,\"properties\":{\"message\":{\"maxLength\":255,\"minLength\":1,\"type\":\"string\"},\"reason\":{\"maxLength\":160,\"minLength\":1,\"type\":\"string\"},\"title\":{\"maxLength\":63,\"minLength\":1,\"type\":\"string\"}},\"required\":[\"title\",\"message\",\"reason\"],\"type\":\"object\"}";
    private const string NotificationResultSchema =
        "{\"additionalProperties\":false,\"properties\":{\"shown\":{\"type\":\"boolean\"}},\"required\":[\"shown\"],\"type\":\"object\"}";
    private const string HttpWorkflowInputSchema =
        "{\"additionalProperties\":false,\"properties\":{\"query\":{\"maxLength\":100,\"minLength\":0,\"type\":\"string\"},\"userId\":{\"maxLength\":64,\"minLength\":1,\"type\":\"string\"}},\"required\":[\"userId\",\"query\"],\"type\":\"object\"}";

    [Fact]
    public void ValidatesCanonicalThreeToolWorkflowAndBuildsDeterministicPlan()
    {
        var parser = new WorkflowDefinitionParser();
        var first = parser.Parse(ThreeToolWorkflow());
        var second = parser.Parse(first.CanonicalJson!);
        var validator = new WorkflowStaticValidator(Catalog());

        var firstResult = validator.Validate(first.Definition!, Manifest(), WorkflowPathInputSchema);
        var secondResult = validator.Validate(second.Definition!, Manifest(), WorkflowPathInputSchema);

        Assert.True(firstResult.Succeeded, firstResult.Issues.FirstOrDefault()?.Code);
        var plan = Assert.IsType<ValidatedWorkflowPlan>(firstResult.Plan);
        Assert.Equal(["file.get_metadata.v1", "system.get_status.v1", "system.show_notification.v1"], plan.RequiredTools);
        Assert.Equal("R1", plan.MaximumRisk);
        Assert.Equal(71, plan.DefinitionHash.Value.Length);
        Assert.Equal(71, plan.PlanHash.Value.Length);
        Assert.Equal(
            "sha256:7e439fa7a23a8003147068f6e27522dd353c92d1874be6eaa30c59699798eaa6",
            plan.DefinitionHash.Value);
        Assert.Equal(
            "sha256:6ab607c8a58b99e0d96f748b4632d6807eb8baf193af9fc275885705c5027990",
            plan.PlanHash.Value);
        Assert.Equal(plan.DefinitionHash, secondResult.Plan!.DefinitionHash);
        Assert.Equal(plan.PlanHash, secondResult.Plan.PlanHash);
        Assert.Equal([1], plan.Steps[0].SuccessorOrdinals);
        Assert.Equal(WorkflowToolPreparationMode.OneShot, plan.Steps[2].PreparationMode);
    }

    [Fact]
    public void DefinitionAndPlanHashesChangeWhenBoundPayloadChanges()
    {
        var parser = new WorkflowDefinitionParser();
        var validator = new WorkflowStaticValidator(Catalog());
        var baseline = validator.Validate(parser.Parse(ThreeToolWorkflow()).Definition!, Manifest(), WorkflowPathInputSchema).Plan!;
        var changedJson = ThreeToolWorkflow().Replace("운영체제 상태를 알립니다.", "다른 알림입니다.", StringComparison.Ordinal);
        var changed = validator.Validate(parser.Parse(changedJson).Definition!, Manifest(), WorkflowPathInputSchema).Plan!;

        Assert.NotEqual(baseline.DefinitionHash, changed.DefinitionHash);
        Assert.NotEqual(baseline.PlanHash, changed.PlanHash);
    }

    [Theory]
    [InlineData("missing", "workflow.graph.dangling_edge")]
    [InlineData("status", "workflow.graph.backward_edge")]
    public void RejectsDanglingAndBackwardEdges(string metadataNext, string expectedCode)
    {
        var json = ThreeToolWorkflow().Replace(
            "\"next\": \"notify\"",
            $"\"next\": \"{metadataNext}\"",
            StringComparison.Ordinal);
        var definition = new WorkflowDefinitionParser().Parse(json).Definition!;

        var issue = Assert.Single(new WorkflowStaticValidator(Catalog()).Validate(
            definition,
            Manifest(),
            WorkflowPathInputSchema).Issues);

        Assert.Equal(expectedCode, issue.Code);
    }

    [Fact]
    public void RejectsUnreachableStepsAndMoreThanFiveConditionsOnAPath()
    {
        const string unreachable = """
            {
              "schemaVersion": 1,
              "entryStepId": "done",
              "steps": [
                { "id": "done", "type": "output", "value": {} },
                { "id": "orphan", "type": "output", "value": {} }
              ]
            }
            """;
        var parser = new WorkflowDefinitionParser();
        var unreachableDefinition = parser.Parse(unreachable).Definition!;
        var emptyManifest = Manifest([], "R0");
        var validator = new WorkflowStaticValidator(new FixedCatalog());

        var unreachableIssue = Assert.Single(validator.Validate(
            unreachableDefinition,
            emptyManifest,
            EmptyInputSchema).Issues);
        var deepIssue = Assert.Single(validator.Validate(
            parser.Parse(DeepBranchWorkflow()).Definition!,
            emptyManifest,
            EmptyInputSchema).Issues);

        Assert.Equal("workflow.output.count_invalid", unreachableIssue.Code);
        Assert.Equal("workflow.graph.branch_depth_exceeded", deepIssue.Code);
    }

    [Fact]
    public void RejectsReferenceFromOnlyOneBranchBecauseItDoesNotDominateConsumer()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "entryStepId": "choose",
              "steps": [
                { "id": "choose", "type": "condition", "left": true, "operator": "isTrue", "then": "status", "else": "fallback" },
                { "id": "status", "type": "tool.call", "tool": "system.get_status.v1", "arguments": {}, "next": "notify" },
                { "id": "fallback", "type": "input.map", "value": "unknown", "saveAs": "fallback_value", "next": "notify" },
                {
                  "id": "notify",
                  "type": "tool.call",
                  "tool": "system.show_notification.v1",
                  "arguments": {
                    "title": "상태",
                    "message": { "$bind": { "source": "step", "stepId": "status", "path": "/windowsRelease" } },
                    "reason": "상태 알림"
                  },
                  "next": "done"
                },
                { "id": "done", "type": "output", "value": {} }
              ]
            }
            """;
        var manifest = Manifest(["system.get_status.v1", "system.show_notification.v1"], "R1");
        var definition = new WorkflowDefinitionParser().Parse(json).Definition!;

        var issue = Assert.Single(new WorkflowStaticValidator(Catalog()).Validate(
            definition,
            manifest,
            EmptyInputSchema).Issues);

        Assert.Equal("workflow.binding.step_not_dominating", issue.Code);
    }

    [Fact]
    public void RejectsOptionalInputBindingOutsideAnExistsCondition()
    {
        const string optionalPathSchema =
            "{\"additionalProperties\":false,\"properties\":{\"path\":{\"maxLength\":4096,\"minLength\":3,\"type\":\"string\"}},\"type\":\"object\"}";
        const string json = """
            {"schemaVersion":1,"entryStepId":"metadata","steps":[
              {"id":"metadata","type":"tool.call","tool":"file.get_metadata.v1","arguments":{"path":{"$bind":{"source":"input","path":"/path"}}},"next":"done"},
              {"id":"done","type":"output","value":{}}
            ]}
            """;
        var definition = new WorkflowDefinitionParser().Parse(json).Definition!;

        var issue = Assert.Single(new WorkflowStaticValidator(Catalog()).Validate(
            definition,
            Manifest(["file.get_metadata.v1"], "R0"),
            optionalPathSchema).Issues);

        Assert.Equal("workflow.binding.pointer_optional", issue.Code);
    }

    [Fact]
    public void RejectsNeedsInformationThatCanBeReachedAfterAnEffect()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "entryStepId": "choose",
              "steps": [
                { "id": "choose", "type": "condition", "left": true, "operator": "isTrue", "then": "notify", "else": "done" },
                {
                  "id": "notify",
                  "type": "tool.call",
                  "tool": "system.show_notification.v1",
                  "arguments": { "title": "상태", "message": "완료", "reason": "사용자 요청" },
                  "next": "ask"
                },
                { "id": "ask", "type": "needs_information", "requiredInputs": ["/path"] },
                { "id": "done", "type": "output", "value": {} }
              ]
            }
            """;
        var definition = new WorkflowDefinitionParser().Parse(json).Definition!;

        var issue = Assert.Single(new WorkflowStaticValidator(Catalog()).Validate(
            definition,
            Manifest(["system.show_notification.v1"], "R1"),
            WorkflowPathInputSchema).Issues);

        Assert.Equal("workflow.needs_information.after_effect", issue.Code);
    }

    [Theory]
    [InlineData("capability.select.v1", "workflow.tool.forbidden")]
    [InlineData("capability.select.v2", "workflow.tool.forbidden")]
    [InlineData("mcp.read.v1", "workflow.tool.forbidden")]
    [InlineData("mcp.read.v2", "workflow.tool.forbidden")]
    [InlineData("workflow.invoke.v2", "workflow.tool.forbidden")]
    [InlineData("shell.execute.v1", "workflow.tool.forbidden")]
    public void RejectsMetaMcpAndShellToolsEvenWhenCatalogClaimsTheyAreAllowed(
        string toolName,
        string expectedCode)
    {
        var json = $"{{\"schemaVersion\":1,\"entryStepId\":\"call\",\"steps\":[{{\"id\":\"call\",\"type\":\"tool.call\",\"tool\":\"{toolName}\",\"arguments\":{{}},\"next\":\"done\"}},{{\"id\":\"done\",\"type\":\"output\",\"value\":{{}}}}]}}";
        var metadata = new WorkflowToolMetadata(
            toolName,
            "R0",
            EmptyInputSchema,
            EmptyInputSchema,
            true,
            WorkflowToolPreparationMode.None,
            true,
            WorkflowRetryClass.ReadOnly,
            WorkflowToolEffectClass.Observation);
        var definition = new WorkflowDefinitionParser().Parse(json).Definition!;

        var issue = Assert.Single(new WorkflowStaticValidator(new FixedCatalog(metadata)).Validate(
            definition,
            Manifest([toolName], "R0"),
            EmptyInputSchema).Issues);

        Assert.Equal(expectedCode, issue.Code);
    }

    [Fact]
    public void RequiresExactManifestToolSetAndTreatsMaximumRiskAsAnUpperBound()
    {
        var definition = new WorkflowDefinitionParser().Parse(ThreeToolWorkflow()).Definition!;
        var validator = new WorkflowStaticValidator(Catalog());

        var toolsIssue = Assert.Single(validator.Validate(
            definition,
            Manifest(["system.get_status.v1", "file.get_metadata.v1"], "R1"),
            WorkflowPathInputSchema).Issues);
        var broaderRisk = validator.Validate(
            definition,
            Manifest(maximumRisk: "R2"),
            WorkflowPathInputSchema);
        var riskIssue = Assert.Single(validator.Validate(
            definition,
            Manifest(maximumRisk: "R0"),
            WorkflowPathInputSchema).Issues);

        Assert.Equal("workflow.manifest.tools_mismatch", toolsIssue.Code);
        Assert.True(broaderRisk.Succeeded);
        Assert.Equal("workflow.manifest.risk_underdeclared", riskIssue.Code);
    }

    [Fact]
    public void AllowsRetriesOnlyForAvailableR0ReadOnlyTools()
    {
        var retryStatus = ThreeToolWorkflow().Replace(
            "\"arguments\": {},",
            "\"arguments\": {},\n              \"maxRetries\": 3,",
            StringComparison.Ordinal);
        var retryNotification = ThreeToolWorkflow().Replace(
            "\"next\": \"done\"",
            "\"maxRetries\": 1,\n              \"next\": \"done\"",
            StringComparison.Ordinal);
        var parser = new WorkflowDefinitionParser();
        var validator = new WorkflowStaticValidator(Catalog());

        Assert.True(validator.Validate(parser.Parse(retryStatus).Definition!, Manifest(), WorkflowPathInputSchema).Succeeded);
        var issue = Assert.Single(validator.Validate(
            parser.Parse(retryNotification).Definition!,
            Manifest(),
            WorkflowPathInputSchema).Issues);

        Assert.Equal("workflow.tool.retry_forbidden", issue.Code);
    }

    [Fact]
    public void RejectsMissingToolArgumentsUnknownInputAndResultPointers()
    {
        var parser = new WorkflowDefinitionParser();
        var validator = new WorkflowStaticValidator(Catalog());
        var missingArgument = ThreeToolWorkflow().Replace(
            "\"path\": { \"$bind\": { \"source\": \"input\", \"path\": \"/path\" } }",
            "\"unknown\": true",
            StringComparison.Ordinal);
        var unknownInput = ThreeToolWorkflow().Replace("\"/path\"", "\"/missing\"", StringComparison.Ordinal);
        var unknownResult = ThreeToolWorkflow().Replace("\"/windowsRelease\"", "\"/secret\"", StringComparison.Ordinal);

        Assert.Equal(
            "workflow.tool.arguments_schema_mismatch",
            Assert.Single(validator.Validate(parser.Parse(missingArgument).Definition!, Manifest(), WorkflowPathInputSchema).Issues).Code);
        Assert.Equal(
            "workflow.input_pointer.unknown",
            Assert.Single(validator.Validate(parser.Parse(unknownInput).Definition!, Manifest(), WorkflowPathInputSchema).Issues).Code);
        Assert.Equal(
            "workflow.binding.result_pointer_unknown",
            Assert.Single(validator.Validate(parser.Parse(unknownResult).Definition!, Manifest(), WorkflowPathInputSchema).Issues).Code);
    }

    [Fact]
    public void ContractParityFixturesMatchParserAndStaticGrammar()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(FindParityFixture()));
        var parser = new WorkflowDefinitionParser();
        var validator = new WorkflowStaticValidator(Catalog());

        foreach (var testCase in fixture.RootElement.EnumerateArray())
        {
            var name = testCase.GetProperty("name").GetString() ?? "unnamed";
            var expected = testCase.GetProperty("valid").GetBoolean();
            var requiredTools = testCase.GetProperty("requiredTools")
                .EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .ToArray();
            var maximumRisk = testCase.GetProperty("maximumRisk").GetString() ?? "invalid";
            var parsed = parser.Parse(testCase.GetProperty("document").GetRawText());
            var actual = parsed.Succeeded && validator.Validate(
                parsed.Definition!,
                Manifest(requiredTools, maximumRisk),
                ParityInputSchema).Succeeded;

            Assert.True(
                actual == expected,
                $"Workflow contract parity case '{name}' expected {expected} but parser/static grammar returned {actual}. " +
                $"Parser issue: {parsed.Issues.FirstOrDefault()?.Code ?? "none"}.");
        }
    }

    [Fact]
    public void RejectsLiteralToolArgumentsThatDoNotMatchTheRegisteredInputSchema()
    {
        var invalidTitle = ThreeToolWorkflow().Replace(
            "\"title\": \"LIGClaw 상태\"",
            "\"title\": 42",
            StringComparison.Ordinal);
        var definition = new WorkflowDefinitionParser().Parse(invalidTitle).Definition!;

        var issue = Assert.Single(new WorkflowStaticValidator(Catalog()).Validate(
            definition,
            Manifest(),
            WorkflowPathInputSchema).Issues);

        Assert.Equal("workflow.tool.arguments_schema_mismatch", issue.Code);
    }

    [Fact]
    public void PlanHashChangesWhenAnAdmittedToolContractChanges()
    {
        var definition = new WorkflowDefinitionParser().Parse(ThreeToolWorkflow()).Definition!;
        var baseline = new WorkflowStaticValidator(Catalog()).Validate(
            definition,
            Manifest(),
            WorkflowPathInputSchema).Plan!;
        var changedCatalog = new FixedCatalog(
            new WorkflowToolMetadata(
                "system.get_status.v1",
                "R0",
                EmptyInputSchema,
                StatusResultSchema,
                true,
                WorkflowToolPreparationMode.None,
                true,
                WorkflowRetryClass.ReadOnly,
                WorkflowToolEffectClass.Observation),
            new WorkflowToolMetadata(
                "file.get_metadata.v1",
                "R0",
                PathInputSchema.Replace("32767", "4096", StringComparison.Ordinal),
                MetadataResultSchema,
                true,
                WorkflowToolPreparationMode.None,
                true,
                WorkflowRetryClass.ReadOnly,
                WorkflowToolEffectClass.Observation),
            new WorkflowToolMetadata(
                "system.show_notification.v1",
                "R1",
                NotificationInputSchema,
                NotificationResultSchema,
                true,
                WorkflowToolPreparationMode.OneShot,
                true,
                WorkflowRetryClass.Never,
                WorkflowToolEffectClass.Effect));
        var changed = new WorkflowStaticValidator(changedCatalog).Validate(
            definition,
            Manifest(),
            WorkflowPathInputSchema).Plan!;

        Assert.NotEqual(baseline.PlanHash, changed.PlanHash);
    }

    [Fact]
    public void PlanHashChangesWhenTheWorkflowInputContractChanges()
    {
        var definition = new WorkflowDefinitionParser().Parse(ThreeToolWorkflow()).Definition!;
        var validator = new WorkflowStaticValidator(Catalog());
        var baseline = validator.Validate(definition, Manifest(), WorkflowPathInputSchema).Plan!;
        var changed = validator.Validate(
            definition,
            Manifest(),
            WorkflowPathInputSchema.Replace("4096", "2048", StringComparison.Ordinal)).Plan!;

        Assert.NotEqual(baseline.InputContractHash, changed.InputContractHash);
        Assert.NotEqual(baseline.PlanHash, changed.PlanHash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    public void RejectsNotificationTitleOutsideItsDeclaredLengthBounds(int length)
    {
        var title = new string('x', length);
        var json = ThreeToolWorkflow().Replace(
            "\"title\": \"LIGClaw 상태\"",
            $"\"title\": \"{title}\"",
            StringComparison.Ordinal);
        var definition = new WorkflowDefinitionParser().Parse(json).Definition!;

        var issue = Assert.Single(new WorkflowStaticValidator(Catalog()).Validate(
            definition,
            Manifest(),
            WorkflowPathInputSchema).Issues);

        Assert.Equal("workflow.tool.arguments_schema_mismatch", issue.Code);
    }

    [Fact]
    public void RejectsBindingWhoseSourceTypeDoesNotMatchTheToolArgument()
    {
        var json = ThreeToolWorkflow().Replace(
            "\"title\": \"LIGClaw 상태\"",
            "\"title\": { \"$bind\": { \"source\": \"step\", \"stepId\": \"status\", \"path\": \"/buildNumber\" } }",
            StringComparison.Ordinal);
        var definition = new WorkflowDefinitionParser().Parse(json).Definition!;

        var issue = Assert.Single(new WorkflowStaticValidator(Catalog()).Validate(
            definition,
            Manifest(),
            WorkflowPathInputSchema).Issues);

        Assert.Equal("workflow.tool.arguments_schema_mismatch", issue.Code);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void UniqueArrayBindingsRequireASourceSchemaUniquenessGuarantee(
        string? sourceUniqueItems,
        bool expectedSuccess)
    {
        const string toolName = "fixture.unique_strings.v1";
        const string toolInputSchema =
            "{\"additionalProperties\":false,\"properties\":{\"values\":{\"items\":{\"type\":\"string\"},\"maxItems\":4,\"type\":\"array\",\"uniqueItems\":true}},\"required\":[\"values\"],\"type\":\"object\"}";
        const string workflow =
            "{\"schemaVersion\":1,\"entryStepId\":\"call\",\"steps\":[{\"id\":\"call\",\"type\":\"tool.call\",\"tool\":\"fixture.unique_strings.v1\",\"arguments\":{\"values\":{\"$bind\":{\"source\":\"input\",\"path\":\"/values\"}}},\"next\":\"done\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{}}]}";
        var uniqueItemsClause = sourceUniqueItems is null
            ? string.Empty
            : $",\"uniqueItems\":{sourceUniqueItems}";
        var workflowInputSchema =
            $"{{\"additionalProperties\":false,\"properties\":{{\"values\":{{\"items\":{{\"maxLength\":16,\"type\":\"string\"}},\"maxItems\":4,\"type\":\"array\"{uniqueItemsClause}}}}},\"required\":[\"values\"],\"type\":\"object\"}}";
        var metadata = new WorkflowToolMetadata(
            toolName,
            "R0",
            toolInputSchema,
            EmptyInputSchema,
            true,
            WorkflowToolPreparationMode.None,
            true,
            WorkflowRetryClass.ReadOnly,
            WorkflowToolEffectClass.Observation);
        var definition = new WorkflowDefinitionParser().Parse(workflow).Definition!;

        var result = new WorkflowStaticValidator(new FixedCatalog(metadata)).Validate(
            definition,
            Manifest([toolName], "R0"),
            workflowInputSchema);

        if (expectedSuccess)
        {
            Assert.True(result.Succeeded, result.Issues.FirstOrDefault()?.Code);
            return;
        }
        Assert.Equal("workflow.tool.arguments_schema_mismatch", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void RejectsUnsupportedToolSchemaKeywordAndInvalidMetadataEnum()
    {
        var definition = new WorkflowDefinitionParser().Parse(ThreeToolWorkflow()).Definition!;
        var invalidSchemaCatalog = CatalogWithStatus(
            EmptyInputSchema.Replace("\"type\":\"object\"", "\"pattern\":\".*\",\"type\":\"object\"", StringComparison.Ordinal),
            WorkflowToolPreparationMode.None);
        var invalidMetadataCatalog = CatalogWithStatus(
            EmptyInputSchema,
            (WorkflowToolPreparationMode)99);

        var schemaIssue = Assert.Single(new WorkflowStaticValidator(invalidSchemaCatalog).Validate(
            definition,
            Manifest(),
            WorkflowPathInputSchema).Issues);
        var metadataIssue = Assert.Single(new WorkflowStaticValidator(invalidMetadataCatalog).Validate(
            definition,
            Manifest(),
            WorkflowPathInputSchema).Issues);

        Assert.Equal("workflow.tool.input_schema_unsupported", schemaIssue.Code);
        Assert.Equal("workflow.tool.metadata_invalid", metadataIssue.Code);
    }

    [Fact]
    public void RejectsPointerIntoScalarVariableAndNonCanonicalArrayIndex()
    {
        const string scalarPointer = """
            {
              "schemaVersion": 1,
              "entryStepId": "map",
              "steps": [
                { "id": "map", "type": "input.map", "value": "text", "saveAs": "mapped", "next": "done" },
                { "id": "done", "type": "output", "value": { "bad": { "$bind": { "source": "variable", "name": "mapped", "path": "/missing" } } } }
              ]
            }
            """;
        const string arrayPointer = """
            {
              "schemaVersion": 1,
              "entryStepId": "map",
              "steps": [
                { "id": "map", "type": "input.map", "value": { "$bind": { "source": "input", "path": "/items/01" } }, "saveAs": "mapped", "next": "done" },
                { "id": "done", "type": "output", "value": {} }
              ]
            }
            """;
        const string arrayInputSchema =
            "{\"additionalProperties\":false,\"properties\":{\"items\":{\"items\":{\"maxLength\":10,\"type\":\"string\"},\"maxItems\":4,\"type\":\"array\"}},\"required\":[\"items\"],\"type\":\"object\"}";
        var parser = new WorkflowDefinitionParser();
        var validator = new WorkflowStaticValidator(new FixedCatalog());

        var scalarIssue = Assert.Single(validator.Validate(
            parser.Parse(scalarPointer).Definition!,
            Manifest([], "R0"),
            EmptyInputSchema).Issues);
        var arrayIssue = Assert.Single(validator.Validate(
            parser.Parse(arrayPointer).Definition!,
            Manifest([], "R0"),
            arrayInputSchema).Issues);

        Assert.Equal("workflow.binding.variable_pointer_unknown", scalarIssue.Code);
        Assert.Equal("workflow.input_pointer.unknown", arrayIssue.Code);
    }

    [Fact]
    public void RejectsTransformOperandTypeMismatch()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "entryStepId": "map",
              "steps": [
                { "id": "map", "type": "input.map", "value": 42, "saveAs": "mapped", "next": "transform" },
                { "id": "transform", "type": "transform", "operation": "trim", "operands": [{ "$bind": { "source": "variable", "name": "mapped", "path": "" } }], "saveAs": "trimmed", "next": "done" },
                { "id": "done", "type": "output", "value": {} }
              ]
            }
            """;
        var definition = new WorkflowDefinitionParser().Parse(json).Definition!;

        var issue = Assert.Single(new WorkflowStaticValidator(new FixedCatalog()).Validate(
            definition,
            Manifest([], "R0"),
            EmptyInputSchema).Issues);

        Assert.Equal("workflow.transform.operand_type_mismatch", issue.Code);
    }

    [Fact]
    public void RejectsBooleanConditionAppliedToAString()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "entryStepId": "choose",
              "steps": [
                { "id": "choose", "type": "condition", "left": "yes", "operator": "isTrue", "then": "selected", "else": "fallback" },
                { "id": "selected", "type": "input.map", "value": true, "saveAs": "selected_value", "next": "done" },
                { "id": "fallback", "type": "input.map", "value": false, "saveAs": "fallback_value", "next": "done" },
                { "id": "done", "type": "output", "value": {} }
              ]
            }
            """;
        var definition = new WorkflowDefinitionParser().Parse(json).Definition!;

        var issue = Assert.Single(new WorkflowStaticValidator(new FixedCatalog()).Validate(
            definition,
            Manifest([], "R0"),
            EmptyInputSchema).Issues);

        Assert.Equal("workflow.condition.operand_type_mismatch", issue.Code);
    }

    [Fact]
    public void RejectsActuallyUnreachableNonOutputStep()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "entryStepId": "done",
              "steps": [
                { "id": "done", "type": "output", "value": {} },
                { "id": "orphan", "type": "needs_information", "requiredInputs": ["/path"] }
              ]
            }
            """;
        var definition = new WorkflowDefinitionParser().Parse(json).Definition!;

        var issue = Assert.Single(new WorkflowStaticValidator(new FixedCatalog()).Validate(
            definition,
            Manifest([], "R0"),
            WorkflowPathInputSchema).Issues);

        Assert.Equal("workflow.graph.unreachable_step", issue.Code);
    }

    [Fact]
    public void RejectsForgedStepKindAndNonCanonicalNumberAst()
    {
        var output = new WorkflowOutputStep("done", new WorkflowObjectValue(
            new Dictionary<string, WorkflowValue>(StringComparer.Ordinal)));
        var forgedKind = new WorkflowDefinition(1, "done", [output with { Kind = WorkflowStepKind.ToolCall }]);
        var forgedNumber = new WorkflowDefinition(
            1,
            "done",
            [new WorkflowOutputStep(
                "done",
                new WorkflowObjectValue(new Dictionary<string, WorkflowValue>(StringComparer.Ordinal)
                {
                    ["value"] = new WorkflowNumberValue("01", true),
                }))]);
        var validator = new WorkflowStaticValidator(new FixedCatalog());

        var kindIssue = Assert.Single(validator.Validate(forgedKind, Manifest([], "R0"), EmptyInputSchema).Issues);
        var numberIssue = Assert.Single(validator.Validate(forgedNumber, Manifest([], "R0"), EmptyInputSchema).Issues);

        Assert.Equal("workflow.ast.invalid", kindIssue.Code);
        Assert.Equal("workflow.ast.invalid", numberIssue.Code);
    }

    [Fact]
    public void ValidatesBoundHttpGetWithExactDeclaredConnectionAndR3OneShotMetadata()
    {
        var definition = new WorkflowDefinitionParser().Parse(HttpWorkflow()).Definition!;

        var result = new WorkflowStaticValidator(HttpCatalog()).Validate(
            definition,
            HttpManifest(),
            HttpWorkflowInputSchema);

        Assert.True(result.Succeeded, result.Issues.FirstOrDefault()?.Code);
        var step = Assert.Single(result.Plan!.Steps, item => item.CanonicalToolName is not null);
        Assert.Equal("connection.http.request.v1", step.CanonicalToolName);
        Assert.Equal(WorkflowToolPreparationMode.OneShot, step.PreparationMode);
        Assert.Equal("R3", result.Plan.MaximumRisk);
    }

    [Theory]
    [InlineData("slot", "workflow.connection.http_binding_invalid")]
    [InlineData("template", "workflow.connection.http_binding_invalid")]
    [InlineData("empty_path", "workflow.tool.arguments_schema_mismatch")]
    [InlineData("dot_path", "workflow.connection.http_binding_invalid")]
    [InlineData("retry", "workflow.tool.retry_forbidden")]
    public void RejectsModelControlledHttpShapeAndRetry(string mutation, string expectedCode)
    {
        var json = mutation switch
        {
            "slot" => HttpWorkflow().Replace(
                "\"connectionSlot\": \"service\"",
                "\"connectionSlot\": { \"$bind\": { \"source\": \"input\", \"path\": \"/userId\" } }",
                StringComparison.Ordinal),
            "template" => HttpWorkflow().Replace(
                "\"pathTemplate\": \"users/{userId}\"",
                "\"pathTemplate\": { \"$bind\": { \"source\": \"input\", \"path\": \"/userId\" } }",
                StringComparison.Ordinal),
            "empty_path" => HttpWorkflow().Replace(
                "{ \"$bind\": { \"source\": \"input\", \"path\": \"/userId\" } }",
                "\"\"",
                StringComparison.Ordinal),
            "dot_path" => HttpWorkflow().Replace(
                "{ \"$bind\": { \"source\": \"input\", \"path\": \"/userId\" } }",
                "\"..\"",
                StringComparison.Ordinal),
            "retry" => HttpWorkflow().Replace(
                "\"next\": \"done\"",
                "\"maxRetries\": 1, \"next\": \"done\"",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var definition = new WorkflowDefinitionParser().Parse(json).Definition!;

        var issue = Assert.Single(new WorkflowStaticValidator(HttpCatalog()).Validate(
            definition,
            HttpManifest(),
            HttpWorkflowInputSchema).Issues);

        Assert.Equal(expectedCode, issue.Code);
    }

    [Fact]
    public void RejectsUndeclaredAndUnusedHttpConnectionSlots()
    {
        var definition = new WorkflowDefinitionParser().Parse(HttpWorkflow()).Definition!;
        var validator = new WorkflowStaticValidator(HttpCatalog());
        var undeclared = HttpManifest([new("other", "http")]);
        var unused = HttpManifest([new("service", "http"), new("unused", "http")]);

        var undeclaredIssue = Assert.Single(validator.Validate(
            definition,
            undeclared,
            HttpWorkflowInputSchema).Issues);
        var unusedIssue = Assert.Single(validator.Validate(
            definition,
            unused,
            HttpWorkflowInputSchema).Issues);

        Assert.Equal("workflow.connection.http_binding_invalid", undeclaredIssue.Code);
        Assert.Equal("workflow.manifest.connections_mismatch", unusedIssue.Code);
    }

    private static string ThreeToolWorkflow() => """
        {
          "schemaVersion": 1,
          "entryStepId": "status",
          "steps": [
            {
              "id": "status",
              "type": "tool.call",
              "tool": "system.get_status.v1",
              "arguments": {},
              "next": "metadata"
            },
            {
              "id": "metadata",
              "type": "tool.call",
              "tool": "file.get_metadata.v1",
              "arguments": {
                "path": { "$bind": { "source": "input", "path": "/path" } }
              },
              "next": "notify"
            },
            {
              "id": "notify",
              "type": "tool.call",
              "tool": "system.show_notification.v1",
              "arguments": {
                "title": "LIGClaw 상태",
                "message": "운영체제 상태를 알립니다.",
                "reason": "사용자가 요청한 상태 알림"
              },
              "next": "done"
            },
            {
              "id": "done",
              "type": "output",
              "value": {
                "windowsRelease": { "$bind": { "source": "step", "stepId": "status", "path": "/windowsRelease" } },
                "metadata": { "$bind": { "source": "step", "stepId": "metadata", "path": "" } }
              }
            }
          ]
        }
        """;

    private static string HttpWorkflow() => """
        {
          "schemaVersion": 1,
          "entryStepId": "request",
          "steps": [
            {
              "id": "request",
              "type": "tool.call",
              "tool": "connection.http.request.v1",
              "arguments": {
                "connectionSlot": "service",
                "method": "GET",
                "pathTemplate": "users/{userId}",
                "reason": "승인된 내부 사용자 조회",
                "pathParameters": [
                  { "name": "userId", "value": { "$bind": { "source": "input", "path": "/userId" } } }
                ],
                "queryParameters": [
                  { "name": "q", "value": { "$bind": { "source": "input", "path": "/query" } } }
                ]
              },
              "next": "done"
            },
            { "id": "done", "type": "output", "value": {} }
          ]
        }
        """;

    private static string DeepBranchWorkflow()
    {
        var steps = new List<string>();
        for (var condition = 0; condition < 6; condition++)
        {
            var conditionIndex = condition * 3;
            var next = condition == 5 ? "done" : $"c{condition + 1}";
            steps.Add($"{{\"id\":\"c{condition}\",\"type\":\"condition\",\"left\":true,\"operator\":\"isTrue\",\"then\":\"t{condition}\",\"else\":\"f{condition}\"}}");
            steps.Add($"{{\"id\":\"t{condition}\",\"type\":\"input.map\",\"value\":true,\"saveAs\":\"tv{conditionIndex}\",\"next\":\"{next}\"}}");
            steps.Add($"{{\"id\":\"f{condition}\",\"type\":\"input.map\",\"value\":false,\"saveAs\":\"fv{conditionIndex}\",\"next\":\"{next}\"}}");
        }
        steps.Add("{\"id\":\"done\",\"type\":\"output\",\"value\":{}}");
        return $"{{\"schemaVersion\":1,\"entryStepId\":\"c0\",\"steps\":[{string.Join(',', steps)}]}}";
    }

    private static CapabilityManifest Manifest(
        IReadOnlyList<string>? requiredTools = null,
        string maximumRisk = "R1") =>
        new(
            1,
            CapabilityId.Parse("company.sample.workflow"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            "Sample Workflow",
            "Runs a bounded test workflow.",
            CapabilityKind.Workflow,
            ["Run the sample workflow"],
            requiredTools ?? ["system.get_status.v1", "file.get_metadata.v1", "system.show_notification.v1"],
            [],
            maximumRisk,
            "workflow.json");

    private static CapabilityManifest HttpManifest(
        IReadOnlyList<CapabilityConnectionRequirement>? connections = null) =>
        new(
            1,
            CapabilityId.Parse("company.sample.http-workflow"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            "HTTP Workflow",
            "Runs one bounded registered HTTP GET.",
            CapabilityKind.Workflow,
            ["Look up an internal user"],
            ["connection.http.request.v1"],
            connections ?? [new("service", "http")],
            "R3",
            "workflow.json");

    private static string FindParityFixture()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "fixtures", "workflow-v1-schema-parity.json");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Workflow v1 schema parity fixture를 찾을 수 없습니다.");
    }

    private static FixedCatalog Catalog() =>
        new(
            new WorkflowToolMetadata(
                "system.get_status.v1",
                "R0",
                EmptyInputSchema,
                StatusResultSchema,
                true,
                WorkflowToolPreparationMode.None,
                true,
                WorkflowRetryClass.ReadOnly,
                WorkflowToolEffectClass.Observation),
            new WorkflowToolMetadata(
                "file.get_metadata.v1",
                "R0",
                PathInputSchema,
                MetadataResultSchema,
                true,
                WorkflowToolPreparationMode.None,
                true,
                WorkflowRetryClass.ReadOnly,
                WorkflowToolEffectClass.Observation),
            new WorkflowToolMetadata(
                "system.show_notification.v1",
                "R1",
                NotificationInputSchema,
                NotificationResultSchema,
                true,
                WorkflowToolPreparationMode.OneShot,
                true,
                WorkflowRetryClass.Never,
                WorkflowToolEffectClass.Effect));

    private static FixedCatalog HttpCatalog()
    {
        Assert.True(ToolSchemaRegistry.TryGet("connection.http.request.v1", out var schemas));
        return new FixedCatalog(new WorkflowToolMetadata(
            "connection.http.request.v1",
            "R3",
            schemas!.CanonicalInputSchemaJson,
            schemas.CanonicalResultSchemaJson,
            true,
            WorkflowToolPreparationMode.OneShot,
            true,
            WorkflowRetryClass.Never,
            WorkflowToolEffectClass.Effect));
    }

    private static FixedCatalog CatalogWithStatus(
        string statusInputSchema,
        WorkflowToolPreparationMode preparationMode) =>
        new(
            new WorkflowToolMetadata(
                "system.get_status.v1",
                "R0",
                statusInputSchema,
                StatusResultSchema,
                true,
                preparationMode,
                true,
                WorkflowRetryClass.ReadOnly,
                WorkflowToolEffectClass.Observation),
            new WorkflowToolMetadata(
                "file.get_metadata.v1",
                "R0",
                PathInputSchema,
                MetadataResultSchema,
                true,
                WorkflowToolPreparationMode.None,
                true,
                WorkflowRetryClass.ReadOnly,
                WorkflowToolEffectClass.Observation),
            new WorkflowToolMetadata(
                "system.show_notification.v1",
                "R1",
                NotificationInputSchema,
                NotificationResultSchema,
                true,
                WorkflowToolPreparationMode.OneShot,
                true,
                WorkflowRetryClass.Never,
                WorkflowToolEffectClass.Effect));

    private sealed class FixedCatalog(params WorkflowToolMetadata[] metadata) : IWorkflowToolMetadataCatalog
    {
        private readonly IReadOnlyDictionary<string, WorkflowToolMetadata> _metadata =
            metadata.ToDictionary(item => item.CanonicalName, StringComparer.Ordinal);

        public bool TryGet(string canonicalName, out WorkflowToolMetadata? metadata)
        {
            var found = _metadata.TryGetValue(canonicalName, out var value);
            metadata = value;
            return found;
        }
    }
}
