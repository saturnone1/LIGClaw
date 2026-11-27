using LIGClaw.Application.Workflows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class WorkflowMailSendStaticValidatorTests
{
    private const string EmptyInputSchema =
        "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}";
    private const string HashInputSchema =
        "{\"additionalProperties\":false,\"properties\":{\"draftHash\":{\"maxLength\":71,\"minLength\":71,\"type\":\"string\"}},\"required\":[\"draftHash\"],\"type\":\"object\"}";
    private const string SlotInputSchema =
        "{\"additionalProperties\":false,\"properties\":{\"slot\":{\"maxLength\":64,\"minLength\":1,\"type\":\"string\"}},\"required\":[\"slot\"],\"type\":\"object\"}";
    private const string DraftResultWithAlternateHashSchema =
        "{\"additionalProperties\":false,\"properties\":{\"draftHash\":{\"maxLength\":71,\"minLength\":71,\"type\":\"string\"},\"otherHash\":{\"maxLength\":71,\"minLength\":71,\"type\":\"string\"}},\"required\":[\"draftHash\",\"otherHash\"],\"type\":\"object\"}";
    private const string FixedHash =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void GeneratedMailContractsAcceptLiteralMailApiSlotAndExactDominatingDraftHashBinding()
    {
        var result = Validate(MailWorkflow());

        Assert.True(result.Succeeded, result.Issues.FirstOrDefault()?.Code);
        Assert.Equal("R3", result.Plan!.MaximumRisk);
        Assert.Equal(["mail.create_draft.v1", "mail.send.v1"], result.Plan.RequiredTools);
    }

    [Fact]
    public void ExistingMailSendContractAcceptsAnExactLiteralSmtpSlot()
    {
        var result = Validate(
            MailWorkflow(),
            Manifest([new("mail", "smtp")]));

        Assert.True(result.Succeeded, result.Issues.FirstOrDefault()?.Code);
        Assert.Equal("R3", result.Plan!.MaximumRisk);
        Assert.Equal(["mail.create_draft.v1", "mail.send.v1"], result.Plan.RequiredTools);
    }

    [Fact]
    public void RejectsConnectionSlotBindingEvenWhenItsInputTypeMatches()
    {
        var workflow = MailWorkflow().Replace(
            "\"connectionSlot\":\"mail\"",
            "\"connectionSlot\":{\"$bind\":{\"source\":\"input\",\"path\":\"/slot\"}}",
            StringComparison.Ordinal);

        var issue = Assert.Single(Validate(workflow, inputSchema: SlotInputSchema).Issues);

        Assert.Equal("workflow.connection.mail_binding_invalid", issue.Code);
    }

    [Fact]
    public void RejectsMailSlotThatIsUndeclaredOrDeclaredAsHttp()
    {
        var undeclared = Manifest([new("other", "mail-api")]);
        var wrongKind = Manifest([new("mail", "http")]);

        var undeclaredIssue = Assert.Single(Validate(MailWorkflow(), undeclared).Issues);
        var wrongKindIssue = Assert.Single(Validate(MailWorkflow(), wrongKind).Issues);

        Assert.Equal("workflow.connection.mail_binding_invalid", undeclaredIssue.Code);
        Assert.Equal("workflow.connection.mail_binding_invalid", wrongKindIssue.Code);
    }

    [Fact]
    public void RejectsLiteralAndInputDraftHashes()
    {
        var literal = MailWorkflow().Replace(DraftHashBinding("draft"), $"\"{FixedHash}\"", StringComparison.Ordinal);
        var input = MailWorkflow().Replace(
            DraftHashBinding("draft"),
            "{\"$bind\":{\"source\":\"input\",\"path\":\"/draftHash\"}}",
            StringComparison.Ordinal);

        var literalIssue = Assert.Single(Validate(literal).Issues);
        var inputIssue = Assert.Single(Validate(input, inputSchema: HashInputSchema).Issues);

        Assert.Equal("workflow.connection.mail_binding_invalid", literalIssue.Code);
        Assert.Equal("workflow.connection.mail_binding_invalid", inputIssue.Code);
    }

    [Fact]
    public void RejectsVariableDraftHashEvenWhenItIsExactAndDominating()
    {
        var workflow = MailWorkflow()
            .Replace("\"entryStepId\":\"draft\"", "\"entryStepId\":\"map\"", StringComparison.Ordinal)
            .Replace(
                "\"steps\":[",
                $"\"steps\":[{{\"id\":\"map\",\"type\":\"input.map\",\"value\":\"{FixedHash}\",\"saveAs\":\"draft_hash\",\"next\":\"draft\"}},",
                StringComparison.Ordinal)
            .Replace(
                DraftHashBinding("draft"),
                "{\"$bind\":{\"source\":\"variable\",\"name\":\"draft_hash\",\"path\":\"\"}}",
                StringComparison.Ordinal);

        var issue = Assert.Single(Validate(workflow).Issues);

        Assert.Equal("workflow.connection.mail_binding_invalid", issue.Code);
    }

    [Fact]
    public void RejectsAnotherStepAndAnyPointerOtherThanExactDraftHash()
    {
        var otherStepWorkflow = MailWorkflow()
            .Replace("\"next\":\"send\"", "\"next\":\"other\"", StringComparison.Ordinal)
            .Replace(
                "{\"id\":\"send\"",
                "{\"id\":\"other\",\"type\":\"tool.call\",\"tool\":\"fixture.hash.v1\",\"arguments\":{},\"next\":\"send\"},{\"id\":\"send\"",
                StringComparison.Ordinal)
            .Replace(DraftHashBinding("draft"), DraftHashBinding("other"), StringComparison.Ordinal);
        var otherPointerWorkflow = MailWorkflow().Replace(
            DraftHashBinding("draft"),
            "{\"$bind\":{\"source\":\"step\",\"stepId\":\"draft\",\"path\":\"/otherHash\"}}",
            StringComparison.Ordinal);
        var otherStepManifest = Manifest(
            [new("mail", "mail-api")],
            ["mail.create_draft.v1", "fixture.hash.v1", "mail.send.v1"]);

        var otherStepIssue = Assert.Single(Validate(
            otherStepWorkflow,
            otherStepManifest,
            catalog: Catalog(includeOtherHashTool: true)).Issues);
        var otherPointerIssue = Assert.Single(Validate(
            otherPointerWorkflow,
            catalog: Catalog(useAlternateDraftResult: true)).Issues);

        Assert.Equal("workflow.connection.mail_binding_invalid", otherStepIssue.Code);
        Assert.Equal("workflow.connection.mail_binding_invalid", otherPointerIssue.Code);
    }

    [Fact]
    public void RejectsDraftStepThatDoesNotDominateTheSend()
    {
        const string workflow =
            "{\"schemaVersion\":1,\"entryStepId\":\"choose\",\"steps\":[{\"id\":\"choose\",\"type\":\"condition\",\"left\":true,\"operator\":\"isTrue\",\"then\":\"draft\",\"else\":\"fallback\"},{\"id\":\"draft\",\"type\":\"tool.call\",\"tool\":\"mail.create_draft.v1\",\"arguments\":{\"to\":[\"owner@example.com\"],\"cc\":[],\"bcc\":[],\"subject\":\"Status\",\"body\":\"Body\"},\"next\":\"send\"},{\"id\":\"fallback\",\"type\":\"input.map\",\"value\":true,\"saveAs\":\"fallback_value\",\"next\":\"send\"},{\"id\":\"send\",\"type\":\"tool.call\",\"tool\":\"mail.send.v1\",\"arguments\":{\"connectionSlot\":\"mail\",\"draftHash\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"draft\",\"path\":\"/draftHash\"}},\"to\":[\"owner@example.com\"],\"cc\":[],\"bcc\":[],\"subject\":\"Status\",\"body\":\"Body\"},\"next\":\"done\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{}}]}";

        var issue = Assert.Single(Validate(workflow).Issues);

        Assert.Equal("workflow.binding.step_not_dominating", issue.Code);
    }

    [Fact]
    public void CountsMailSlotUsageAndRejectsAnUnusedDeclaredSlot()
    {
        var manifest = Manifest([new("mail", "mail-api"), new("unused", "http")]);

        var issue = Assert.Single(Validate(MailWorkflow(), manifest).Issues);

        Assert.Equal("workflow.manifest.connections_mismatch", issue.Code);
    }

    private static WorkflowStaticValidationResult Validate(
        string workflow,
        CapabilityManifest? manifest = null,
        string inputSchema = EmptyInputSchema,
        IWorkflowToolMetadataCatalog? catalog = null)
    {
        var parsed = new WorkflowDefinitionParser().Parse(workflow);
        Assert.True(parsed.Succeeded, parsed.Issues.FirstOrDefault()?.Code);
        return new WorkflowStaticValidator(catalog ?? Catalog()).Validate(
            parsed.Definition!,
            manifest ?? Manifest(),
            inputSchema);
    }

    private static string MailWorkflow() =>
        "{\"schemaVersion\":1,\"entryStepId\":\"draft\",\"steps\":[{\"id\":\"draft\",\"type\":\"tool.call\",\"tool\":\"mail.create_draft.v1\",\"arguments\":{\"to\":[\"owner@example.com\"],\"cc\":[],\"bcc\":[],\"subject\":\"Status\",\"body\":\"Body\"},\"next\":\"send\"},{\"id\":\"send\",\"type\":\"tool.call\",\"tool\":\"mail.send.v1\",\"arguments\":{\"connectionSlot\":\"mail\",\"draftHash\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"draft\",\"path\":\"/draftHash\"}},\"to\":[\"owner@example.com\"],\"cc\":[],\"bcc\":[],\"subject\":\"Status\",\"body\":\"Body\"},\"next\":\"done\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{}}]}";

    private static string DraftHashBinding(string stepId) =>
        $"{{\"$bind\":{{\"source\":\"step\",\"stepId\":\"{stepId}\",\"path\":\"/draftHash\"}}}}";

    private static CapabilityManifest Manifest(
        IReadOnlyList<CapabilityConnectionRequirement>? connections = null,
        IReadOnlyList<string>? tools = null) =>
        new(
            1,
            CapabilityId.Parse("company.sample.mail-send"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            "Mail Send",
            "Sends one previously validated mail draft.",
            CapabilityKind.Workflow,
            ["Send a validated mail draft"],
            tools ?? ["mail.create_draft.v1", "mail.send.v1"],
            connections ?? [new("mail", "mail-api")],
            "R3",
            "workflow.json");

    private static FixedCatalog Catalog(
        bool includeOtherHashTool = false,
        bool useAlternateDraftResult = false)
    {
        Assert.True(ToolSchemaRegistry.TryGet("mail.create_draft.v1", out var createDraftSchemas));
        Assert.True(ToolSchemaRegistry.TryGet("mail.send.v1", out var sendSchemas));
        var tools = new List<WorkflowToolMetadata>
        {
            new(
                "mail.create_draft.v1",
                "R0",
                createDraftSchemas!.CanonicalInputSchemaJson,
                useAlternateDraftResult
                    ? DraftResultWithAlternateHashSchema
                    : createDraftSchemas.CanonicalResultSchemaJson,
                true,
                WorkflowToolPreparationMode.None,
                true,
                WorkflowRetryClass.ReadOnly,
                WorkflowToolEffectClass.Observation),
            new(
                "mail.send.v1",
                "R3",
                sendSchemas!.CanonicalInputSchemaJson,
                sendSchemas.CanonicalResultSchemaJson,
                true,
                WorkflowToolPreparationMode.OneShot,
                true,
                WorkflowRetryClass.Never,
                WorkflowToolEffectClass.Effect),
        };
        if (includeOtherHashTool)
        {
            tools.Add(new WorkflowToolMetadata(
                "fixture.hash.v1",
                "R0",
                EmptyInputSchema,
                createDraftSchemas.CanonicalResultSchemaJson,
                true,
                WorkflowToolPreparationMode.None,
                true,
                WorkflowRetryClass.ReadOnly,
                WorkflowToolEffectClass.Observation));
        }
        return new FixedCatalog(tools.ToArray());
    }

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
