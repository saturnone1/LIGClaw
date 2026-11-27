using System.Text.Json;
using LIGClaw.Contracts.Generated;
using LIGClaw.Contracts.Protocol;

namespace LIGClaw.Contracts.Tests;

public sealed class GeneratedCapabilityContractTests
{
    [Fact]
    public void LocalReferencesGenerateStrongNestedTypes()
    {
        Assert.Equal(
            typeof(IReadOnlyList<CapabilityRuntimeCandidate>),
            typeof(CapabilityRuntimeSnapshot).GetProperty(nameof(CapabilityRuntimeSnapshot.Candidates))!.PropertyType);
        Assert.Equal(
            typeof(CapabilityRuntimeSnapshot),
            typeof(ConversationStartParams).GetProperty(nameof(ConversationStartParams.CapabilitySnapshot))!.PropertyType);
        Assert.Equal(
            typeof(CapabilityInvocationContext),
            typeof(ToolInvokeParams).GetProperty(nameof(ToolInvokeParams.CapabilityContext))!.PropertyType);
        Assert.Equal(
            typeof(CapabilityInvocationContext),
            typeof(CapabilitySelectV1Result).GetProperty(nameof(CapabilitySelectV1Result.Context))!.PropertyType);
    }

    [Fact]
    public void OptionalTrailingReferencesKeepExistingConstructorsSourceCompatible()
    {
        var start = new ConversationStartParams("conversation", "run", "input", "replay", null, null);
        var invocation = new ToolInvokeParams(
            "call",
            "conversation",
            "run",
            "system.get_status.v1",
            "R0",
            new Dictionary<string, object?>());

        Assert.Null(start.CapabilitySnapshot);
        Assert.Null(invocation.CapabilityContext);
    }

    [Fact]
    public void CapabilitySnapshotRoundTripsWithExactTypedIdentity()
    {
        var candidate = new CapabilityRuntimeCandidate(
            "company.mail",
            "1.0.0",
            Hash('a'),
            7,
            $"cap_{new string('b', 24)}",
            "Mail",
            "Compose a bounded mail request.",
            "prompt",
            ["Send a report"],
            "Use only the declared tools.",
            "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
            ["file.read_text.v1"],
            Hash('d'),
            "R1");
        var expected = new CapabilityRuntimeSnapshot(2, Hash('c'), [candidate]);
        var request = new ConversationStartParams(
            "conversation",
            "run",
            "input",
            "cline",
            CapabilitySnapshot: expected);

        var json = JsonSerializer.Serialize(request, ContentLengthMessageStream.SerializerOptions);
        var actual = JsonSerializer.Deserialize<ConversationStartParams>(
            json,
            ContentLengthMessageStream.SerializerOptions);

        Assert.NotNull(actual?.CapabilitySnapshot);
        Assert.Equal(expected.SnapshotHash, actual.CapabilitySnapshot.SnapshotHash);
        var roundTrippedCandidate = Assert.Single(actual.CapabilitySnapshot.Candidates);
        Assert.Equal(candidate.Id, roundTrippedCandidate.Id);
        Assert.Equal(candidate.ContentHash, roundTrippedCandidate.ContentHash);
        Assert.Equal(candidate.ModelToolName, roundTrippedCandidate.ModelToolName);
    }

    [Fact]
    public void ProtocolMetadataMovesBuilderControlPlaneToOnePointTwentyFour()
    {
        Assert.Equal("1.24", ContractMetadata.ProtocolVersion);
        Assert.Equal("7a221a94e96ef061630a92382356761cea49acdf271d0503a915c31ccd013776", ContractMetadata.Hash);
        Assert.Equal(
            typeof(IReadOnlyList<IReadOnlyDictionary<string, object?>>),
            typeof(WorkflowV1Definition).GetProperty(nameof(WorkflowV1Definition.Steps))!.PropertyType);
    }

    [Fact]
    public void WorkflowResultOmitsAbsentOptionalPropertiesOnTheWire()
    {
        var workflowCallId = $"wfinv_{new string('a', 48)}";
        var success = new WorkflowResultParams(
            workflowCallId,
            true,
            new WorkflowInvokeV1Result("workflow-run", "succeeded", "none"));
        var failure = new WorkflowResultParams(
            workflowCallId,
            false,
            Error: "workflow.policy.denied");

        using var successJson = JsonDocument.Parse(
            JsonSerializer.Serialize(success, ContentLengthMessageStream.SerializerOptions));
        Assert.False(successJson.RootElement.TryGetProperty("error", out _));
        var output = successJson.RootElement.GetProperty("output");
        Assert.Equal(
            ["nextAction", "status", "workflowRunId"],
            output.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());

        using var failureJson = JsonDocument.Parse(
            JsonSerializer.Serialize(failure, ContentLengthMessageStream.SerializerOptions));
        Assert.False(failureJson.RootElement.TryGetProperty("output", out _));
        Assert.Equal("workflow.policy.denied", failureJson.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("capability.builder.open.v1")]
    [InlineData("system.get_status.v1")]
    [InlineData("file.get_metadata.v1")]
    [InlineData("system.show_notification.v1")]
    [InlineData("connection.http.request.v1")]
    [InlineData("mail.create_draft.v1")]
    [InlineData("mail.send.v1")]
    public void ToolSchemaRegistryPairsInputAndResultByCanonicalFileName(string canonicalToolName)
    {
        Assert.True(ToolSchemaRegistry.TryGet(canonicalToolName, out var schemas));
        Assert.NotNull(schemas);
        Assert.Equal(canonicalToolName, schemas.CanonicalName);

        using var input = JsonDocument.Parse(schemas.CanonicalInputSchemaJson);
        using var result = JsonDocument.Parse(schemas.CanonicalResultSchemaJson);
        Assert.EndsWith(
            $"/contracts/tools/{canonicalToolName}.schema.json",
            input.RootElement.GetProperty("$id").GetString(),
            StringComparison.Ordinal);
        Assert.EndsWith(
            $"/contracts/tools/{canonicalToolName}.result.schema.json",
            result.RootElement.GetProperty("$id").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuilderOpenContractCarriesOnlyIntentAndAcknowledgement()
    {
        Assert.True(ToolSchemaRegistry.TryGet("capability.builder.open.v1", out var schemas));
        using var input = JsonDocument.Parse(schemas!.CanonicalInputSchemaJson);
        using var result = JsonDocument.Parse(schemas.CanonicalResultSchemaJson);

        Assert.Equal(
            ["intent"],
            input.RootElement.GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        Assert.Equal(
            ["create", "revise"],
            input.RootElement.GetProperty("properties").GetProperty("intent").GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
        Assert.Equal(
            ["accepted", "intent"],
            result.RootElement.GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.True(result.RootElement.GetProperty("properties").GetProperty("accepted")
            .GetProperty("const").GetBoolean());
        foreach (var forbidden in new[] { "requirement", "prompt", "userInput", "capabilityId", "content" })
        {
            Assert.False(input.RootElement.GetProperty("properties").TryGetProperty(forbidden, out _));
            Assert.False(result.RootElement.GetProperty("properties").TryGetProperty(forbidden, out _));
        }
        Assert.Equal(typeof(string), typeof(CapabilityBuilderOpenV1).GetProperty("Intent")!.PropertyType);
        Assert.Equal(typeof(bool), typeof(CapabilityBuilderOpenV1Result).GetProperty("Accepted")!.PropertyType);
    }

    [Fact]
    public void ToolSchemaRegistryDoesNotUseSchemaTitleAsLookupKey()
    {
        Assert.True(ToolSchemaRegistry.TryGet("system.show_notification.v1", out var schemas));
        Assert.NotNull(schemas);
        using var input = JsonDocument.Parse(schemas.CanonicalInputSchemaJson);
        Assert.Equal("SystemShowNotificationV1", input.RootElement.GetProperty("title").GetString());
        Assert.False(ToolSchemaRegistry.TryGet("SystemShowNotificationV1", out _));
    }

    [Fact]
    public void ConnectionHttpContractExposesNoRawTransportOrSecretFields()
    {
        Assert.True(ToolSchemaRegistry.TryGet("connection.http.request.v1", out var schemas));
        using var input = JsonDocument.Parse(schemas!.CanonicalInputSchemaJson);
        using var result = JsonDocument.Parse(schemas.CanonicalResultSchemaJson);

        Assert.Equal(
            ["connectionSlot", "method", "pathParameters", "pathTemplate", "queryParameters", "reason"],
            input.RootElement.GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ["contentType", "responseBytes", "statusCode", "text", "truncated", "untrusted"],
            result.RootElement.GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            "GET",
            input.RootElement.GetProperty("properties").GetProperty("method")
                .GetProperty("const").GetString());
        Assert.True(
            result.RootElement.GetProperty("properties").GetProperty("untrusted")
                .GetProperty("const").GetBoolean());
    }

    [Fact]
    public void MailDraftContractHasExactContentOnlyFieldsAndBoundedMetadataResult()
    {
        Assert.True(ToolSchemaRegistry.TryGet("mail.create_draft.v1", out var schemas));
        using var input = JsonDocument.Parse(schemas!.CanonicalInputSchemaJson);
        using var result = JsonDocument.Parse(schemas.CanonicalResultSchemaJson);

        Assert.Equal(
            ["bcc", "body", "cc", "subject", "to"],
            input.RootElement.GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ["body", "subject", "to"],
            input.RootElement.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ["bodyLength", "draftHash", "ready", "recipientCount", "subjectLength"],
            result.RootElement.GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ["bodyLength", "draftHash", "ready", "recipientCount", "subjectLength"],
            result.RootElement.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .Order(StringComparer.Ordinal)
                .ToArray());

        string[] forbiddenFields =
        [
            "sender", "from", "endpoint", "url", "token", "auth", "header",
            "connectionSlot", "attachment", "html", "reason",
        ];
        foreach (var field in forbiddenFields)
        {
            Assert.False(input.RootElement.GetProperty("properties").TryGetProperty(field, out _));
            Assert.False(result.RootElement.GetProperty("properties").TryGetProperty(field, out _));
        }

        Assert.True(input.RootElement.GetProperty("additionalProperties").ValueKind is JsonValueKind.False);
        Assert.True(result.RootElement.GetProperty("additionalProperties").ValueKind is JsonValueKind.False);
        Assert.True(result.RootElement.GetProperty("properties").GetProperty("ready").GetProperty("const").GetBoolean());
        var draftHash = result.RootElement.GetProperty("properties").GetProperty("draftHash");
        Assert.Equal(71, draftHash.GetProperty("minLength").GetInt32());
        Assert.Equal(71, draftHash.GetProperty("maxLength").GetInt32());
        Assert.False(draftHash.TryGetProperty("pattern", out _));
    }

    [Fact]
    public void MailSendContractHasExactBoundContentAndOperationMetadataFields()
    {
        Assert.True(ToolSchemaRegistry.TryGet("mail.send.v1", out var schemas));
        using var input = JsonDocument.Parse(schemas!.CanonicalInputSchemaJson);
        using var result = JsonDocument.Parse(schemas.CanonicalResultSchemaJson);

        string[] expectedInputFields =
        [
            "bcc", "body", "cc", "connectionSlot", "draftHash", "subject", "to",
        ];
        Assert.Equal(
            expectedInputFields,
            input.RootElement.GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            expectedInputFields,
            input.RootElement.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ["draftHash", "operationId", "status"],
            result.RootElement.GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ["draftHash", "operationId", "status"],
            result.RootElement.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .Order(StringComparer.Ordinal)
                .ToArray());

        string[] forbiddenFields =
        [
            "endpoint", "url", "method", "path", "header", "auth", "token", "password", "secret",
            "sender", "from", "profileId", "operationId", "idempotencyKey", "retry", "reason", "html",
            "attachment",
        ];
        foreach (var field in forbiddenFields)
            Assert.False(input.RootElement.GetProperty("properties").TryGetProperty(field, out _));

        Assert.True(input.RootElement.GetProperty("additionalProperties").ValueKind is JsonValueKind.False);
        Assert.True(result.RootElement.GetProperty("additionalProperties").ValueKind is JsonValueKind.False);
        var draftHash = input.RootElement.GetProperty("properties").GetProperty("draftHash");
        Assert.Equal(71, draftHash.GetProperty("minLength").GetInt32());
        Assert.Equal(71, draftHash.GetProperty("maxLength").GetInt32());
        Assert.False(draftHash.TryGetProperty("pattern", out _));
        var operationId = result.RootElement.GetProperty("properties").GetProperty("operationId");
        Assert.Equal(68, operationId.GetProperty("minLength").GetInt32());
        Assert.Equal(68, operationId.GetProperty("maxLength").GetInt32());
        Assert.False(operationId.TryGetProperty("pattern", out _));
        Assert.Equal(
            ["queued", "delivered"],
            result.RootElement.GetProperty("properties").GetProperty("status").GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
    }

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";
}
